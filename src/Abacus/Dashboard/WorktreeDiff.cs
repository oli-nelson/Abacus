using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record WorktreeDiff(string WorktreeId, string Head, string Revision,
    string Staged, string Unstaged, string Untracked, string Coverage);

internal sealed partial class DashboardGit
{
    private readonly DetailCache<(string Id, string Fingerprint), WorktreeDiff> worktreePatches = new(8, lifetime);
    private readonly object worktreeReadsLock = new();
    private readonly Dictionary<string, Task<WorktreeDiff>> worktreeReads = [];

    public Task<WorktreeDiff> ReadWorktreeDiffAsync(GitSnapshot snapshot, string id, CancellationToken token)
    {
        var key = id + ":" + snapshot.Revision;
        Task<WorktreeDiff> task;
        lock (worktreeReadsLock)
        {
            foreach (var completed in worktreeReads.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray()) worktreeReads.Remove(completed);
            if (!worktreeReads.TryGetValue(key, out task!))
            {
                if (worktreeReads.Count >= 8) throw new InvalidOperationException("Worktree detail reads busy; retry.");
                task = WorktreeDiffAsync(snapshot, id, lifetime);
                worktreeReads.Add(key, task);
            }
        }
        return task.WaitAsync(token);
    }

    // IDs are derived from registered Git paths. HTTP never supplies a filesystem path.
    public async Task<WorktreeDiff> WorktreeDiffAsync(GitSnapshot snapshot, string id, CancellationToken token)
    {
        await Task.Yield();
        var tree = snapshot.Worktrees.SingleOrDefault(w => w.Id == id)
            ?? throw new ArgumentException("Unknown registered worktree.");
        if (tree.Bare || tree.Prunable || tree.Head is null)
            throw new InvalidOperationException("Worktree has no readable committed HEAD.");
        var common = (await RequiredAsync(["rev-parse", "--path-format=absolute", "--git-common-dir"], token)).TrimEnd('\r', '\n');
        async Task<string> Verify(CancellationToken readToken)
        {
            var registrations = ParseWorktrees(await RequiredAsync(["worktree", "list", "--porcelain", "-z"], readToken));
            var current = registrations.SingleOrDefault(w => w.Id == id);
            if (current is null || current.Head != tree.Head || current.Branch != tree.Branch || current.Bare || current.Prunable)
                throw new InvalidDataException("Worktree registration or HEAD changed.");
            var actual = (await RequiredAsync(["-C", tree.Path, "rev-parse", "--path-format=absolute", "--git-common-dir"], readToken)).TrimEnd('\r', '\n');
            if (!Path.IsPathRooted(common) || !Path.IsPathRooted(actual) || Path.GetFullPath(actual) != Path.GetFullPath(common))
                throw new InvalidDataException("Worktree belongs to another repository.");
            var head = (await RequiredAsync(["-C", tree.Path, "rev-parse", "--verify", "HEAD"], readToken)).Trim();
            if (head != tree.Head) throw new InvalidDataException("Worktree HEAD changed.");
            return await RequiredAsync(["-C", tree.Path, "status", "--porcelain=v2", "--untracked-files=all", "-z"], readToken);
        }
        async Task<(string Staged, string Unstaged, string Untracked)> Read(CancellationToken readToken)
        {
            // Separate index and working-tree patches: a staged edit reversed in the
            // working tree must not collapse into an apparently empty HEAD diff.
            string[] flags = ["--no-ext-diff", "--no-textconv", "--no-renames", "--binary", "--no-color", "--no-relative", "--src-prefix=a/", "--dst-prefix=b/"];
            var staged = await RequiredAsync(["-C", tree.Path, "diff", .. flags, "--cached", tree.Head, "--"], readToken);
            var unstaged = await RequiredAsync(["-C", tree.Path, "diff", .. flags, "--"], readToken);
            if (staged.Length + unstaged.Length > OutputLimit) throw new InvalidDataException("Worktree patches exceed the combined limit.");
            var paths = (await RequiredAsync(["-C", tree.Path, "ls-files", "--others", "--exclude-standard", "-z"], readToken))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
            if (paths.Length > 256) throw new InvalidDataException("Too many untracked files for bounded detail.");
            var untracked = new StringBuilder();
            foreach (var path in paths)
            {
                // Git supplies repository-relative names. Refuse directory traversal
                // and intermediate symlinks; final symlinks are rendered by Git as
                // mode 120000 link text, not the linked target's contents.
                var parts = path.Split('/');
                if (Path.IsPathRooted(path) || parts.Any(p => p is "" or "." or ".."))
                    throw new InvalidDataException("Unsupported untracked path.");
                var parent = tree.Path;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    parent = Path.Combine(parent, parts[i]);
                    if (File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                        throw new InvalidDataException("Untracked parent is a symbolic link.");
                }
                var fullPath = Path.Combine(tree.Path, path);
                var attributes = File.GetAttributes(fullPath);
                if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Nested untracked repositories/directories require separate review.");
                var remaining = OutputLimit - staged.Length - unstaged.Length - untracked.Length;
                if (remaining < 1) throw new InvalidDataException("Worktree patches exceed the combined limit.");
                var diff = await RunAsync(["-C", tree.Path, "diff", .. flags, "--no-index", "--", "/dev/null", "./" + path], readToken, remaining);
                if (diff.ExitCode is not (0 or 1)) throw new InvalidDataException("Untracked content unavailable.");
                untracked.Append(diff.StandardOutput);
            }
            return (staged, unstaged, untracked.ToString());
        }
        var status = await Verify(token);
        var fingerprint = await WorktreeFingerprintAsync(tree, status, token);
        var result = await worktreePatches.GetAsync((id, fingerprint), async cancellation =>
        {
            var first = await Read(cancellation);
            var second = await Read(cancellation);
            var afterStatus = await Verify(cancellation);
            if (first != second || afterStatus != status || await WorktreeFingerprintAsync(tree, afterStatus, cancellation) != fingerprint)
                throw new InvalidDataException("Worktree content changed during reading; retry.");
            var canonical = JsonSerializer.Serialize(new { tree.Head, first.Staged, first.Unstaged, first.Untracked, status });
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return new WorktreeDiff(id, tree.Head, revision, first.Staged, first.Unstaged, first.Untracked,
                "Collected worktree snapshot: staged, unstaged and untracked patches are separate. Binary payloads and symlink text participate in the revision; symlink targets are not read. At most 256 untracked files, 1,024 changed paths, 64 MiB fingerprint input and 4 Mi characters of patches combined. Ignored files are excluded. Nested repositories/submodules are unsupported. Not an atomic filesystem snapshot.");
        }, token);
        var finalStatus = await Verify(token);
        if (finalStatus != status || await WorktreeFingerprintAsync(tree, finalStatus, token) != fingerprint)
            throw new InvalidDataException("Worktree changed while checking cached content; retry.");
        return result;
    }
}
