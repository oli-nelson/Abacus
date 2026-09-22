using System.Collections.Immutable;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record GitView(long Revision, GitSnapshot? Facts, ImmutableArray<string> Targets, string? DefaultTarget,
    bool EnforceTarget, string? PolicyError, bool Stale, string? Error, DateTimeOffset? LastSuccess);
internal sealed record IssueGitEvidence(string IssueId, string IssueRevision, string State, string Explanation,
    string? EffectiveTarget = null, ExecutionBinding? Binding = null, GitComparison? Comparison = null,
    GitPatch? Patch = null, GitHistory? History = null)
{
    public ImmutableArray<WorktreeFact> Worktrees { get; init; } = [];
}
internal sealed record GitPublication(GitView View, byte[] Body);

internal sealed class GitCollector(DashboardGit source, string repository)
{
    private readonly WorktreeWatchHub worktreeWatches = new();
    internal WorktreeWatchHub WorktreeWatches => worktreeWatches;
    public WorktreeWatch WatchWorktree(string id)
    {
        if (State.View.Stale || State.View.Facts?.Worktrees.Any(w => w.Id == id) != true)
            throw new ArgumentException("Worktree is unavailable or unregistered.");
        return worktreeWatches.Subscribe(id, token => WorktreeDiffAsync(id, token));
    }

    private readonly SemaphoreSlim refresh = new(1);
    private GitPublication state = Serialize(new(0, null, [], null, false, "Target policy not loaded", true, "Not collected", null));
    private string? policyFingerprint;
    private DateTimeOffset? lastSuccess;
    public object Metrics => new
    {
        source.GitCommands, source.GitDiffCommands, source.WorktreeHashBytes,
        WorktreeFingerprintSeconds = source.WorktreeHashTime.TotalSeconds,
        WorktreeReads = worktreeWatches.SourceReads,
        WorktreeTopics = worktreeWatches.TopicCount
    };
    public GitPublication State => Volatile.Read(ref state);
    private static GitPublication Serialize(GitView view) => new(view, JsonSerializer.SerializeToUtf8Bytes(view, DashboardStream.Json));

    public async Task<GitPublication> RefreshAsync(CancellationToken token)
    {
        var next = await CollectAsync(token);
        await worktreeWatches.ReconcileAsync(token);
        return next;
    }

    private async Task<GitPublication> CollectAsync(CancellationToken token)
    {
        await refresh.WaitAsync(token);
        try
        {
            GitSnapshot facts;
            try { facts = await source.ProbeAsync(token); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CommandStartException or CommandTimeoutException or CommandOutputLimitException)
            {
                if (!State.View.Stale || State.View.Error != "Git source unavailable; showing last successful facts.")
                    Volatile.Write(ref state, Serialize(State.View with { Revision = State.View.Revision + 1, Stale = true, Error = "Git source unavailable; showing last successful facts.", LastSuccess = lastSuccess }));
                return State;
            }
            lastSuccess = DateTimeOffset.UtcNow;
            ImmutableArray<string> targets = [];
            string? defaultTarget = null, policyError = null, fingerprint = null;
            var enforce = false;
            try
            {
                var registry = await TargetRegistry.LoadAsync(Path.Combine(repository, ".abacus", "targets.json"), token);
                targets = registry.Targets.Keys.Order(StringComparer.Ordinal).ToImmutableArray();
                if (targets.Any(t => !facts.Branches.Any(b => b.Ref == "refs/heads/" + t)))
                    throw new TargetException("A configured target branch is missing.");
                enforce = registry.EnforceTargetBranch;
                defaultTarget = registry.DefaultTarget;
                fingerprint = string.Join('|', registry.Targets.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + p.Value.Identity));
            }
            catch (TargetException)
            {
                targets = [];
                policyError = "Target policy missing, invalid, or naming missing local branches; target comparisons are disabled.";
            }
            var old = State.View;
            if (old.Facts?.Revision == facts.Revision && !old.Stale && old.PolicyError == policyError &&
                old.DefaultTarget == defaultTarget && old.EnforceTarget == enforce && policyFingerprint == fingerprint) return State;
            policyFingerprint = fingerprint;
            var next = Serialize(new(old.Revision + 1, facts, targets, defaultTarget, enforce, policyError, false, null, DateTimeOffset.UtcNow));
            Volatile.Write(ref state, next);
            return next;
        }
        finally { refresh.Release(); }
    }

    public async Task RunAsync(TimeSpan interval, Action<GitPublication> publish, CancellationToken token)
    {
        GitPublication? previous = null;
        while (true)
        {
            var next = await RefreshAsync(token);
            if (!ReferenceEquals(previous, next)) publish(next);
            previous = next;
            var remaining = interval;
            while (remaining > TimeSpan.Zero)
            {
                var slice = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
                await Task.Delay(slice, token);
                remaining -= slice;
            }
        }
    }

    public async Task<IssueGitEvidence> IssueAsync(IssueSummary issue, CancellationToken token, bool patch = false, bool history = false)
    {
        var view = State.View;
        if (view.Stale || view.PolicyError is not null || view.Facts is null)
            throw new InvalidOperationException("Git or policy unavailable.");
        var routing = issue.Routing;
        if (routing is null) return new(issue.Id, issue.Revision, "unknown", "Routing metadata unavailable.");
        TargetRegistry registry;
        TargetPolicy policy;
        try
        {
            registry = await TargetRegistry.LoadAsync(Path.Combine(repository, ".abacus", "targets.json"), token);
            policy = registry.Validate(routing);
        }
        catch (TargetException) { return new(issue.Id, issue.Revision, "invalid", "Routing or execution binding conflicts with current target policy. Operator review required."); }
        var binding = routing.Binding;
        if (binding is null) return new(issue.Id, issue.Revision, "unbound", "No recorded execution binding; no issue branch is inferred.", policy.Branch);
        if (!view.Facts.Branches.Any(b => b.Ref == "refs/heads/" + binding.IssueBranch))
            return new(issue.Id, issue.Revision, "branch-missing", "Recorded issue branch is absent from the current Git snapshot; deletion does not prove integration.", policy.Branch, binding);
        var comparison = await CompareAsync(binding.TargetRef, "refs/heads/" + binding.IssueBranch, false, token);
        var containsStart = await source.IsStartAncestorAsync(binding.StartCommit, comparison.Comparison.IssueTip, token);
        GitPatch? patchBody = null;
        GitHistory? historyBody = null;
        if (containsStart)
        {
            if (patch) patchBody = await source.PatchAsync(new(comparison.Comparison.TargetTip, comparison.Comparison.IssueTip), token);
            if (history) historyBody = await source.HistoryAsync(new(comparison.Comparison.IssueTip, 100), token);
            if ((patchBody is not null && patchBody.HistoryBoundary != comparison.Comparison.HistoryBoundary) ||
                (historyBody is not null && historyBody.HistoryBoundary != comparison.Comparison.HistoryBoundary))
                throw new InvalidDataException("History changed while loading issue Git detail.");
        }
        var recheck = await CompareAsync(binding.TargetRef, "refs/heads/" + binding.IssueBranch, false, token);
        if (recheck.Comparison != comparison.Comparison) throw new InvalidDataException("Git changed while inspecting binding.");
        try
        {
            var latest = await TargetRegistry.LoadAsync(Path.Combine(repository, ".abacus", "targets.json"), token);
            if (latest.Validate(routing) != policy) throw new TargetException("Policy changed.");
        }
        catch (TargetException) { throw new InvalidDataException("Policy changed while inspecting binding."); }
        if (!containsStart) return new(issue.Id, issue.Revision, "diverged-or-incomplete",
            "Recorded start is not reachable from the issue tip in available history; divergence or shallow history requires review. Integration is not asserted.", policy.Branch, binding);
        return new(issue.Id, issue.Revision, "validated", "Binding matches current policy and the recorded start is an ancestor of the issue tip. This does not prove live worker ownership or an integration timestamp.", policy.Branch, binding, comparison.Comparison, patchBody, historyBody)
        {
            Worktrees = view.Facts.Worktrees.Where(w => w.Branch == "refs/heads/" + binding.IssueBranch)
                .Select(w => w.Head == comparison.Comparison.IssueTip ? w : w with
                { Dirty = null, StatusError = "Collected worktree HEAD differs from the current branch tip; refresh required." }).ToImmutableArray()
        };
    }

    public async Task<WorktreeDiff> WorktreeDiffAsync(string id, CancellationToken token)
    {
        var view = State.View;
        if (view.Stale || view.Facts is null) throw new InvalidOperationException("Git unavailable.");
        var result = await source.ReadWorktreeDiffAsync(view.Facts, id, token);
        if (State.View.Stale || State.View.Revision != view.Revision)
            throw new InvalidDataException("Git collection changed during worktree detail read.");
        return result;
    }

    public async Task<GitHistory> HistoryAsync(string reference, int limit, CancellationToken token)
    {
        var view = State.View;
        if (view.Stale || view.Facts is null) throw new InvalidOperationException("Git unavailable.");
        var result = await source.HistoryRefAsync(view.Facts, reference, limit, token);
        if (State.View.Stale || State.View.Facts?.Branches.Any(b => b.Ref == reference) != true)
            throw new InvalidOperationException("Git changed during history loading.");
        return result;
    }

    public async Task<(GitComparison Comparison, GitPatch? Patch)> CompareAsync(string targetRef, string branchRef, bool patch, CancellationToken token)
    {
        var view = State.View;
        if (view.Stale || view.PolicyError is not null || view.Facts is null) throw new InvalidOperationException("Git or target policy unavailable.");
        if (!view.Targets.Any(t => "refs/heads/" + t == targetRef)) throw new ArgumentException("Target is not configured.");
        var comparison = await source.CompareRefsAsync(view.Facts, targetRef, branchRef, token);
        var body = patch ? await source.PatchAsync(new(comparison.TargetTip, comparison.IssueTip), token) : null;
        // Recheck refs after a slow patch as well as after its comparison.
        if (patch)
        {
            var verified = await source.CompareRefsAsync(view.Facts, targetRef, branchRef, token);
            if (verified.TargetTip != comparison.TargetTip || verified.IssueTip != comparison.IssueTip ||
                verified.HistoryBoundary != comparison.HistoryBoundary || body?.HistoryBoundary != comparison.HistoryBoundary)
                throw new InvalidDataException("Git refs are updating; retry the comparison.");
        }
        if (State.View.PolicyError is not null || State.View.Stale || !State.View.Targets.Contains(targetRef[11..]))
            throw new InvalidOperationException("Git or target policy changed during comparison.");
        return (comparison, body);
    }
}
