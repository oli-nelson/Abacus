namespace Abacus;

public sealed partial class WorktreePool
{
    // Protected by activeAssignments. allocation serializes acquisition/recovery
    // while this issue-specific fence prevents already leased workers claiming it.
    private string? mutationIssue;

    /// <summary>
    /// Fence this controller's allocation/claims and reject durable reservations,
    /// including stopped assignments and clean issue branches. This is NOT proof
    /// of external-writer quiescence, Beads ownership, or permission to publish.
    /// The caller must separately establish those conditions and keep the guard
    /// through its fresh read, mutation and verification.
    /// </summary>
    internal async Task<IDisposable> GuardUnreservedIssueAsync(string issueId, CancellationToken token)
    {
        if (!Git.IsValidIssueId(issueId)) throw new ArgumentException("Invalid issue ID.");
        RequireLease();
        await allocation.WaitAsync(token);
        var handedOff = false;
        try
        {
            RequireLease();
            lock (activeAssignments) mutationIssue = issueId;
            var manifest = ReadManifest() ?? throw new WorkspacePreparationException("pool reservation coverage unavailable");
            var slots = manifest.Slots.Select(slot => slot.Name).ToHashSet(StringComparer.Ordinal);
            lock (activeAssignments)
                if (activeAssignments.Keys.Any(slot => !slots.Contains(slot)))
                    throw new WorkspacePreparationException("live assignment is missing from the pool manifest; mutation refused");
            var journals = slots.Select(slot => Path.GetFileName(AssignmentPath(slot))).ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFileSystemEntries(MetadataDirectory, "assignment-*.json"))
                if (!journals.Contains(Path.GetFileName(path)))
                    throw new WorkspacePreparationException("assignment journal is missing from the pool manifest; mutation refused");
            foreach (var path in Directory.EnumerateFileSystemEntries(manifest.Root, "slot-*"))
                if (IsSlotName(Path.GetFileName(path)) && !slots.Contains(Path.GetFileName(path)))
                    throw new WorkspacePreparationException("pool workspace is missing from the manifest; mutation refused");
            foreach (var slot in manifest.Slots)
            {
                token.ThrowIfCancellationRequested();
                lock (activeAssignments)
                {
                    var record = ReadAssignment(slot.Name);
                    if (activeAssignments.TryGetValue(slot.Name, out var active) && (record is null || record.Id != active || record.RunId != runId))
                        throw new WorkspacePreparationException("pool assignment and live lease disagree; mutation refused");
                    if (record?.IssueId == issueId || record is { Phase: "execution-uncertain", IssueId: null })
                        throw new WorkspacePreparationException("issue is reserved or execution ownership is unknown; mutation refused");
                }
                await VerifySlotAsync(slot, token);
                var status = await git.GetWorkspaceStatusAsync(slot.Path, "dashboard", token);
                // The existing pool recovery path treats clean committed issue
                // branches as reservations even when their journal is absent.
                if (status.Branch == $"abacus/{issueId}")
                    throw new WorkspacePreparationException("issue branch remains in a pool workspace; mutation refused");
            }
            token.ThrowIfCancellationRequested();
            RequireLease();
            handedOff = true;
            return new IssueMutationGuard(this);
        }
        finally
        {
            if (!handedOff) ReleaseIssueMutation();
        }
    }

    private void ReleaseIssueMutation()
    {
        lock (activeAssignments) mutationIssue = null;
        allocation.Release();
    }

    private sealed class IssueMutationGuard(WorktreePool pool) : IDisposable
    {
        private WorktreePool? owner = pool;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseIssueMutation();
    }
}
