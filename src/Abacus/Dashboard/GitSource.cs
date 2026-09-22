using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Abacus.Dashboard;

internal sealed record BranchFact(string Ref, string Tip);
internal sealed record WorktreeFact(string Path, string? Head, string? Branch, bool Bare, bool Detached, bool Locked, bool Prunable)
{
    public string Id => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path))).ToLowerInvariant();
    public bool? Dirty { get; init; }
    public string? StatusError { get; init; }
}
internal sealed record GitSnapshot(string Revision, ImmutableArray<BranchFact> Branches, ImmutableArray<WorktreeFact> Worktrees)
{
    public string HistoryBoundary { get; init; } = "";
}
internal sealed record GitFileChange(string Path, long? Additions, long? Deletions, bool Binary);
internal sealed record GitComparison(string TargetTip, string IssueTip, string? MergeBase, bool ContainedInTarget,
    long Ahead, long Behind, ImmutableArray<GitFileChange> Files, string Basis, string? Warning)
{
    public string HistoryBoundary { get; init; } = "";
}
internal sealed record GitPatch(string TargetTip, string IssueTip, string Text, bool Available, string? Warning)
{
    public string HistoryBoundary { get; init; } = "";
}
internal sealed record GitCommitFact(string Id, ImmutableArray<string> Parents, DateTimeOffset CommittedAt, string Author, string Message);
internal sealed record GitHistory(string Tip, ImmutableArray<GitCommitFact> Commits, bool LimitReached, string Coverage)
{
    public bool ClockSkew { get; init; }
    public string HistoryBoundary { get; init; } = "";
}
internal sealed record GitHistoryKey(string Tip, int Limit);
internal sealed record GitComparisonKey(string TargetTip, string IssueTip);

/// <summary>Read-only Git facts. Never accepts filesystem paths or revision expressions from HTTP.</summary>
internal sealed partial class DashboardGit(CommandRunner runner, string repository, CancellationToken lifetime)
{
    private readonly DetailCache<(GitComparisonKey Key, string Boundary), GitComparison> comparisons = new(128, lifetime);
    private readonly DetailCache<(GitComparisonKey Key, string Boundary), GitPatch> patches = new(16, lifetime);
    private readonly DetailCache<(GitHistoryKey Key, string Boundary), GitHistory> histories = new(64, lifetime);
    private readonly SemaphoreSlim commands = new(4);
    private long gitCommands;
    internal long GitCommands => Interlocked.Read(ref gitCommands);
    private long gitDiffCommands;
    internal long GitDiffCommands => Interlocked.Read(ref gitDiffCommands);
    private const int OutputLimit = 4 * 1024 * 1024;

    private async Task<CommandResult> RunAsync(string[] args, CancellationToken token, int limit = OutputLimit)
    {
        await commands.WaitAsync(token);
        try
        {
            Interlocked.Increment(ref gitCommands);
            if (args.Contains("diff", StringComparer.Ordinal)) Interlocked.Increment(ref gitDiffCommands);
            return await runner.RunAsync(new CommandSpec("git", ["--no-optional-locks", "--no-replace-objects", .. args], repository,
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_PAGER"] = "cat", ["GIT_LFS_SKIP_SMUDGE"] = "1" },
                MaxOutputCharacters: limit), token);
        }
        finally { commands.Release(); }
    }

    private async Task<string> RequiredAsync(string[] args, CancellationToken token)
    {
        var result = await RunAsync(args, token);
        if (!result.Succeeded) throw new InvalidDataException("Git facts unavailable; repository may be changing or incomplete.");
        return result.StandardOutput;
    }

    public async Task<GitSnapshot> ProbeAsync(CancellationToken token)
    {
        var refs = await RequiredAsync(["for-each-ref", "--sort=refname", "--format=%(refname)%00%(objectname)", "refs/heads/"], token);
        var worktrees = await RequiredAsync(["worktree", "list", "--porcelain", "-z"], token);
        var branches = ParseBranches(refs);
        var registered = ParseWorktrees(worktrees);
        var common = (await RequiredAsync(["rev-parse", "--path-format=absolute", "--git-common-dir"], token)).TrimEnd('\r', '\n');
        if (!Path.IsPathRooted(common)) throw new InvalidDataException("Invalid common Git directory.");
        var treeBuilder = ImmutableArray.CreateBuilder<WorktreeFact>();
        foreach (var tree in registered) treeBuilder.Add(await WorktreeStatusAsync(tree, common, token));
        var trees = treeBuilder.ToImmutable();
        // Source order is not a revision. Timestamps never enter this fingerprint.
        var boundary = await HistoryBoundaryAsync(token);
        var canonical = System.Text.Json.JsonSerializer.Serialize(new { branches, trees, boundary });
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(), branches, trees) { HistoryBoundary = boundary };
    }

    private async Task<WorktreeFact> WorktreeStatusAsync(WorktreeFact tree, string common, CancellationToken token)
    {
        if (tree.Bare || tree.Prunable) return tree with { StatusError = "Bare or prunable registration; status unavailable." };
        try
        {
            async Task VerifyRepository()
            {
                var actual = (await RequiredAsync(["-C", tree.Path, "rev-parse", "--path-format=absolute", "--git-common-dir"], token)).TrimEnd('\r', '\n');
                if (!Path.IsPathRooted(actual) || Path.GetFullPath(actual) != Path.GetFullPath(common))
                    throw new InvalidDataException("Worktree repository changed.");
            }
            await VerifyRepository();
            var status = await RequiredAsync(["-C", tree.Path, "status", "--porcelain=v2", "--branch", "--no-ahead-behind", "--untracked-files=normal", "-z"], token);
            await VerifyRepository();
            var fields = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            var oid = fields.FirstOrDefault(f => f.StartsWith("# branch.oid ", StringComparison.Ordinal))?[13..];
            var branch = fields.FirstOrDefault(f => f.StartsWith("# branch.head ", StringComparison.Ordinal))?[14..];
            var expectedBranch = tree.Detached ? "(detached)" : tree.Branch?[11..];
            if (branch != expectedBranch || oid != (tree.Head ?? "(initial)"))
                throw new InvalidDataException("Worktree changed during status collection.");
            return tree with { Dirty = fields.Any(f => !f.StartsWith("# ", StringComparison.Ordinal)) };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CommandStartException or CommandTimeoutException or CommandOutputLimitException)
        {
            return tree with { Dirty = null, StatusError = "Worktree status unavailable or registration/HEAD changed; not known clean." };
        }
    }

    private async Task<string> HistoryBoundaryAsync(CancellationToken token)
    {
        // The path comes only from Git, never from an HTTP parameter. Linked
        // worktrees share this file; content, not mtime or line order, is identity.
        var path = (await RequiredAsync(["rev-parse", "--path-format=absolute", "--git-path", "shallow"], token)).TrimEnd('\r', '\n');
        if (!Path.IsPathRooted(path)) throw new InvalidDataException("Invalid Git shallow path.");
        string contents;
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            var buffer = new char[4096];
            var builder = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (builder.Length + count > OutputLimit) throw new InvalidDataException("Git shallow boundary exceeds its limit.");
                builder.Append(buffer, 0, count);
            }
            contents = builder.ToString();
        }
        catch (FileNotFoundException) { contents = ""; }
        var ids = contents.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Any(id => !Git.IsCommitId(id))) throw new InvalidDataException("Malformed Git shallow boundary.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", ids.Distinct().Order(StringComparer.Ordinal))))).ToLowerInvariant();
    }

    internal static ImmutableArray<BranchFact> ParseBranches(string text)
    {
        var result = ImmutableArray.CreateBuilder<BranchFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length != 2 || !parts[0].StartsWith("refs/heads/", StringComparison.Ordinal) ||
                parts[0].Any(char.IsControl) || !Git.IsCommitId(parts[1]) || !seen.Add(parts[0]))
                throw new InvalidDataException("Malformed Git ref facts.");
            result.Add(new(parts[0], parts[1]));
        }
        return result.OrderBy(x => x.Ref, StringComparer.Ordinal).ToImmutableArray();
    }

    internal static ImmutableArray<WorktreeFact> ParseWorktrees(string text)
    {
        if (text.Length == 0 || !text.EndsWith('\0')) throw new InvalidDataException("Missing or truncated Git worktree facts.");
        var result = ImmutableArray.CreateBuilder<WorktreeFact>();
        string? path = null, head = null, branch = null;
        bool bare = false, detached = false, locked = false, prunable = false;
        void Finish()
        {
            if (path is null) return;
            if (!Path.IsPathRooted(path) || (head is not null && !Git.IsCommitId(head))) throw new InvalidDataException("Malformed Git worktree facts.");
            if (head is not null && head.All(c => c == '0')) head = null; // unborn checkout
            result.Add(new(path, head, branch, bare, detached, locked, prunable));
            path = head = branch = null; bare = detached = locked = prunable = false;
        }
        foreach (var field in text.Split('\0'))
        {
            if (field.Length == 0) { Finish(); continue; }
            if (field.StartsWith("worktree ", StringComparison.Ordinal)) { Finish(); path = field[9..]; }
            else if (path is null) throw new InvalidDataException("Malformed Git worktree record.");
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = field[5..];
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field[7..];
            else if (field == "bare") bare = true;
            else if (field == "detached") detached = true;
            else if (field == "locked" || field.StartsWith("locked ", StringComparison.Ordinal)) locked = true;
            else if (field == "prunable" || field.StartsWith("prunable ", StringComparison.Ordinal)) prunable = true;
            else throw new InvalidDataException("Unsupported Git worktree record.");
        }
        Finish();
        if (result.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != result.Count) throw new InvalidDataException("Duplicate Git worktree.");
        return result.OrderBy(x => x.Path, StringComparer.Ordinal).ToImmutableArray();
    }

    // Only server-collected literal local refs are eligible. Resolve again before
    // and after expensive work; returned object IDs always describe the actual basis.
    public async Task<GitComparison> CompareRefsAsync(GitSnapshot snapshot, string targetRef, string issueRef, CancellationToken token)
    {
        if (!snapshot.Branches.Any(x => x.Ref == targetRef) || !snapshot.Branches.Any(x => x.Ref == issueRef))
            throw new ArgumentException("Comparison requires known local refs.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var key = new GitComparisonKey(await ResolveAsync(targetRef, token), await ResolveAsync(issueRef, token));
            var result = await CompareAsync(key, token);
            if (await ResolveAsync(targetRef, token) == key.TargetTip && await ResolveAsync(issueRef, token) == key.IssueTip) return result;
        }
        throw new InvalidDataException("Git refs are updating; retry the comparison.");
    }

    public async Task<bool> IsStartAncestorAsync(string start, string tip, CancellationToken token)
    {
        if (!Git.IsCommitId(start) || !Git.IsCommitId(tip)) throw new ArgumentException("Invalid commit.");
        // Missing objects are unknown, not evidence of an unrelated branch.
        await ResolveAsync(start, token);
        var boundary = await HistoryBoundaryAsync(token);
        var result = await RunAsync(["merge-base", "--is-ancestor", start, tip], token);
        if (result.ExitCode is not (0 or 1) || await HistoryBoundaryAsync(token) != boundary)
            throw new InvalidDataException("Start ancestry unavailable or history changed.");
        return result.ExitCode == 0;
    }

    private async Task<string> ResolveAsync(string reference, CancellationToken token)
    {
        var value = (await RequiredAsync(["rev-parse", "--verify", "--end-of-options", reference + "^{commit}"], token)).Trim();
        if (!Git.IsCommitId(value)) throw new InvalidDataException("Git ref no longer resolves to a commit.");
        return value;
    }

    private async Task<T> BoundaryCachedAsync<T>(DetailCache<(GitComparisonKey Key, string Boundary), T> cache,
        GitComparisonKey key, Func<CancellationToken, Task<T>> load, Func<T, string, T> stamp, CancellationToken token)
    {
        var boundary = await HistoryBoundaryAsync(token);
        var result = await cache.GetAsync((key, boundary), async cancellation =>
        {
            var value = await load(cancellation);
            if (await HistoryBoundaryAsync(cancellation) != boundary)
                throw new InvalidDataException("Git shallow boundary changed during comparison; retry.");
            return stamp(value, boundary);
        }, token);
        if (await HistoryBoundaryAsync(token) != boundary)
            throw new InvalidDataException("Git shallow boundary changed during comparison; retry.");
        return result;
    }

    public Task<GitComparison> CompareAsync(GitComparisonKey key, CancellationToken token)
    {
        Validate(key);
        return BoundaryCachedAsync(comparisons, key, async cancellation =>
        {
            var ancestry = await RunAsync(["merge-base", "--is-ancestor", key.IssueTip, key.TargetTip], cancellation);
            if (ancestry.ExitCode is not (0 or 1)) throw new InvalidDataException("Git ancestry unavailable.");
            var counts = (await RequiredAsync(["rev-list", "--left-right", "--count", key.TargetTip + "..." + key.IssueTip, "--"], cancellation)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (counts.Length != 2 || !long.TryParse(counts[0], out var behind) || !long.TryParse(counts[1], out var ahead) || behind < 0 || ahead < 0)
                throw new InvalidDataException("Malformed Git ahead/behind counts.");
            var basis = await RunAsync(["merge-base", "--all", key.TargetTip, key.IssueTip], cancellation);
            if (basis.ExitCode is not (0 or 1)) throw new InvalidDataException("Git merge base unavailable.");
            var bases = basis.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (bases.Any(x => !Git.IsCommitId(x))) throw new InvalidDataException("Malformed Git merge base.");
            if (bases.Length != 1)
                return new GitComparison(key.TargetTip, key.IssueTip, null, ancestry.Succeeded, ahead, behind, [], "target...issue",
                    bases.Length == 0 ? "Unrelated or missing history; no comparison basis." : "Multiple merge bases; comparison is ambiguous.");
            var stats = await RequiredAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--numstat", "-z", key.TargetTip + "..." + key.IssueTip, "--"], cancellation);
            return new GitComparison(key.TargetTip, key.IssueTip, bases[0], ancestry.Succeeded, ahead, behind,
                ParseNumstat(stats), "merge-base-to-issue-tip (target...issue)", null);
        }, (value, boundary) => value with { HistoryBoundary = boundary }, token);
    }

    public Task<GitPatch> PatchAsync(GitComparisonKey key, CancellationToken token)
    {
        Validate(key);
        return BoundaryCachedAsync(patches, key, async cancellation =>
        {
            var comparison = await CompareAsync(key, cancellation);
            if (comparison.MergeBase is null) return new GitPatch(key.TargetTip, key.IssueTip, "", false, comparison.Warning);
            try
            {
                var result = await RequiredAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--no-color", "--unified=3", key.TargetTip + "..." + key.IssueTip, "--"], cancellation);
                return new GitPatch(key.TargetTip, key.IssueTip, result, true, null);
            }
            catch (CommandOutputLimitException)
            { return new GitPatch(key.TargetTip, key.IssueTip, "", false, "Patch exceeds the 4 Mi-character limit; summary remains available."); }
        }, (value, boundary) => value with { HistoryBoundary = boundary }, token);
    }

    public async Task<GitHistory> HistoryRefAsync(GitSnapshot snapshot, string reference, int limit, CancellationToken token)
    {
        if (!snapshot.Branches.Any(b => b.Ref == reference)) throw new ArgumentException("History requires a known local branch.");
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var tip = await ResolveAsync(reference, token);
        var result = await HistoryAsync(new(tip, limit), token);
        if (await ResolveAsync(reference, token) != tip) throw new InvalidDataException("Branch changed while loading history.");
        return result;
    }

    public async Task<GitHistory> HistoryAsync(GitHistoryKey key, CancellationToken token)
    {
        if (!Git.IsCommitId(key.Tip) || key.Limit is < 1 or > 1000) throw new ArgumentException("History requires a full tip ID and a limit from 1 through 1000.");
        var boundary = await HistoryBoundaryAsync(token);
        var result = await histories.GetAsync((key, boundary), async cancellation =>
        {
            var output = await RequiredAsync(["log", "--topo-order", "--no-show-signature", "--format=%H%x00%P%x00%cI%x00%an%x00%B", "-z",
                "-n", key.Limit.ToString(CultureInfo.InvariantCulture), key.Tip, "--"], cancellation);
            if (await HistoryBoundaryAsync(cancellation) != boundary)
                throw new InvalidDataException("Git shallow boundary changed while loading history; retry.");
            var fields = output.Split('\0');
            if (fields[^1] != "" || (fields.Length - 1) % 5 != 0) throw new InvalidDataException("Malformed Git history records.");
            var commits = ImmutableArray.CreateBuilder<GitCommitFact>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Length - 1; i += 5)
            {
                var parents = fields[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
                if (!Git.IsCommitId(fields[i]) || !seen.Add(fields[i]) || parents.Any(p => !Git.IsCommitId(p)) ||
                    !DateTimeOffset.TryParse(fields[i + 2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
                    throw new InvalidDataException("Invalid Git commit facts.");
                commits.Add(new(fields[i], parents, date.ToUniversalTime(), fields[i + 3], fields[i + 4]));
            }
            if (commits.Count > key.Limit) throw new InvalidDataException("Git history exceeded its requested limit.");
            var immutable = commits.ToImmutable();
            var byId = immutable.ToDictionary(c => c.Id, StringComparer.Ordinal);
            return new GitHistory(key.Tip, immutable, commits.Count == key.Limit,
                "Bounded reachable commits in topology order; shallow/missing history and skewed clocks may leave gaps. Commit time is not a verified issue integration time.")
            { HistoryBoundary = boundary, ClockSkew = immutable.Any(c => c.Parents.Any(p => byId.TryGetValue(p, out var parent) && parent.CommittedAt > c.CommittedAt)) };
        }, token);
        if (await HistoryBoundaryAsync(token) != boundary)
            throw new InvalidDataException("Git shallow boundary changed while loading history; retry.");
        return result;
    }

    private static void Validate(GitComparisonKey key)
    {
        if (!Git.IsCommitId(key.TargetTip) || !Git.IsCommitId(key.IssueTip)) throw new ArgumentException("Full Git object IDs are required.");
    }

    internal static ImmutableArray<GitFileChange> ParseNumstat(string text)
    {
        var result = ImmutableArray.CreateBuilder<GitFileChange>();
        if (text.Length > 0 && !text.EndsWith('\0')) throw new InvalidDataException("Truncated Git file summary.");
        foreach (var record in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = record.IndexOf('\t'); var second = first < 0 ? -1 : record.IndexOf('\t', first + 1);
            if (first < 1 || second <= first + 1 || second == record.Length - 1) throw new InvalidDataException("Malformed Git file summary.");
            var added = record[..first]; var removed = record[(first + 1)..second];
            var binary = added == "-" && removed == "-";
            long? Count(string value) => binary ? null : long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number : throw new InvalidDataException("Invalid Git line count.");
            result.Add(new(record[(second + 1)..], Count(added), Count(removed), binary));
        }
        return result.ToImmutable();
    }
}
