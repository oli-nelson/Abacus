using System.Text.Json;

namespace Abacus;

public sealed record AssignmentRecord(int Version, string Id, string RunId, string WorkerId,
    string SlotId, string AgentName, string? IssueId, string Phase, DateTimeOffset AcquiredAt,
    string? Location = null, string? ExpectedAssignee = null, string? ExecutionId = null);

/// <summary>Temporary ownership, never keyed by a display name. Disposal does not erase unfinished work.</summary>
public sealed class PoolAssignment : IDisposable
{
    private readonly WorktreePool pool;
    private readonly WorkspaceOwnership ownership;
    internal AssignmentRecord Record { get; private set; }
    public WorktreePool.Slot Slot { get; }
    public string? RecoveryIssueId { get; }
    public string? PreviousAgentName { get; }
    private bool disposed;

    internal PoolAssignment(WorktreePool pool, WorkspaceOwnership ownership, WorktreePool.Slot slot,
        AssignmentRecord record, string? recoveryIssueId, string? previousAgentName)
    {
        this.pool = pool; this.ownership = ownership; Slot = slot; Record = record;
        RecoveryIssueId = recoveryIssueId; PreviousAgentName = previousAgentName;
    }

    public bool TryClaiming(string issueId)
    {
        if (Record.Phase == "execution-uncertain") throw new StartupInvariantException("previous execution has not been confirmed stopped");
        var next = Record with { IssueId = issueId, Phase = "claiming", ExpectedAssignee = null };
        if (!pool.TryReserveIssue(next)) return false;
        Record = next;
        return true;
    }
    public void ClaimContended() => Save(Record with { IssueId = null, Phase = "leased", ExpectedAssignee = null });
    public void Prepared() => Save(Record with { Phase = "prepared", ExpectedAssignee = null });
    // Written and flushed BEFORE invoking any host. A crash at any point thereafter is not proof of exit.
    public string Launching()
    {
        if (Record.Phase is not ("prepared" or "stopped"))
            throw new StartupInvariantException("assignment is not prepared for a new execution");
        var id = Guid.NewGuid().ToString("N");
        Save(Record with { Phase = "execution-uncertain", ExecutionId = id, Location = null });
        return id;
    }
    public void RequireExecution(string id)
    {
        if (disposed || Record.ExecutionId != id) throw new StartupInvariantException("stale execution update/cleanup rejected");
    }
    public void Running(string id, string location) { RequireExecution(id); Save(Record with { Location = location }); }
    public void Stopped(string id) { RequireExecution(id); Save(Record with { Phase = "stopped" }); }
    private void Save(AssignmentRecord record)
    {
        if (disposed) throw new InvalidOperationException("assignment has already been released");
        if (Record.Phase == "execution-uncertain" && record.Phase is not ("execution-uncertain" or "stopped"))
            throw new StartupInvariantException("previous execution has not been confirmed stopped; pool assignment preserved");
        pool.UpdateAssignment(record);
        Record = record;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
        pool.ReleaseAssignment(Slot.Name, Record.Id);
    }
}

public sealed partial class WorktreePool
{
    public sealed record DiagnosticSlot(string SlotId, string WorkspacePath, bool? LeasedInThisRun,
        AssignmentRecord? Assignment, string? Error = null);
    public sealed record DiagnosticSnapshot(DateTimeOffset ObservedAt, IReadOnlyList<DiagnosticSlot> Slots,
        string? Error = null);

    // Read-only, bounded to bookkeeping: no Git mutations, lock-file probing, or cache-size scans.
    // A snapshot is diagnostic evidence, not a lease or permission to repair a checkout.
    public DiagnosticSnapshot GetDiagnosticSnapshot()
    {
        lock (activeAssignments)
        {
            var observed = DateTimeOffset.UtcNow;
            try
            {
                var slots = new List<DiagnosticSlot>();
                foreach (var slot in ReadManifest()?.Slots ?? [])
                {
                    try
                    {
                        var record = ReadAssignment(slot.Name);
                        var leased = activeAssignments.TryGetValue(slot.Name, out var id);
                        if (leased && record?.Id != id)
                            slots.Add(new(slot.Name, slot.Path, null, record, "live lease and assignment journal disagree; ownership unknown"));
                        else slots.Add(new(slot.Name, slot.Path, leased, record));
                    }
                    catch (Exception ex) when (ex is WorkspacePreparationException or IOException or UnauthorizedAccessException)
                    { slots.Add(new(slot.Name, slot.Path, null, null, ex.Message)); }
                }
                return new(observed, slots);
            }
            catch (Exception ex) when (ex is WorkspacePreparationException or IOException or UnauthorizedAccessException)
            { return new(observed, [], ex.Message); }
        }
    }

    public IReadOnlyDictionary<string, ValidatedAgent>? ValidatedSlots { get; set; }
    public ValidatedAgent AssignWorker(ValidatedAgent worker, PoolAssignment assignment) =>
        (ValidatedSlots?.GetValueOrDefault(assignment.Slot.Path) ?? worker) with
        { Name = worker.Name, WorkspacePath = assignment.Slot.Path, PoolAssignment = assignment };

    public string? ActiveWorkspace(string name)
    {
        lock (activeAssignments)
        {
            var manifest = ReadManifest();
            return manifest?.Slots.FirstOrDefault(s => activeAssignments.ContainsKey(s.Name)
                && ReadAssignment(s.Name)?.AgentName == name)?.Path;
        }
    }

    private readonly SemaphoreSlim allocation = new(1, 1);
    private readonly Dictionary<string, string> activeAssignments = new(StringComparer.Ordinal);
    private readonly string runId = Guid.NewGuid().ToString("N");
    private string AssignmentPath(string slot) => Path.Combine(MetadataDirectory, $"assignment-{slot}.json");

    public AssignmentRecord? ReadAssignment(string slot)
    {
        if (!IsSlotName(slot)) throw new ArgumentException("invalid slot ID");
        var path = AssignmentPath(slot);
        if (Directory.Exists(path)) throw new WorkspacePreparationException($"{slot}: assignment path is a directory; preserved for review");
        if (!File.Exists(path)) return null;
        try
        {
            if (new FileInfo(path).Length > 16384) throw new JsonException("oversized assignment");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count())
                throw new JsonException("ambiguous assignment record");
            var value = document.RootElement.Deserialize<AssignmentRecord>();
            if (value is null || value.Version != 1 || value.SlotId != slot
                || !Guid.TryParseExact(value.Id, "N", out _) || !Guid.TryParseExact(value.RunId, "N", out _)
                || !Guid.TryParseExact(value.WorkerId, "N", out _) || string.IsNullOrWhiteSpace(value.AgentName)
                || value.Phase is not ("leased" or "claiming" or "prepared" or "execution-uncertain" or "stopped")
                || (value.IssueId is not null && !Git.IsValidIssueId(value.IssueId))
                || (value.ExecutionId is not null && !Guid.TryParseExact(value.ExecutionId, "N", out _)))
                throw new JsonException("invalid assignment record");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        { throw new WorkspacePreparationException($"{slot}: cannot read assignment; preserved for review: {ex.Message}"); }
    }

    private void SaveAssignment(AssignmentRecord value)
    {
        RequireLease();
        var path = AssignmentPath(value.SlotId);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    internal bool TryReserveIssue(AssignmentRecord value)
    {
        lock (activeAssignments)
        {
            // Fence only new reservations. Existing execution-stop/cleanup state
            // updates must remain possible while the guard inspects reservations.
            if (value.IssueId is not null && value.IssueId == mutationIssue) return false;
            foreach (var slot in ReadManifest()!.Slots)
                if (slot.Name != value.SlotId && ReadAssignment(slot.Name)?.IssueId == value.IssueId) return false;
            UpdateAssignment(value);
            return true;
        }
    }

    internal void UpdateAssignment(AssignmentRecord value)
    {
        lock (activeAssignments)
        {
            if (!activeAssignments.TryGetValue(value.SlotId, out var id) || id != value.Id)
                throw new StartupInvariantException("stale assignment update rejected");
            SaveAssignment(value);
        }
    }

    internal void ReleaseAssignment(string slot, string id)
    {
        lock (activeAssignments)
            if (activeAssignments.GetValueOrDefault(slot) == id) activeAssignments.Remove(slot);
    }

    private void DeleteAssignment(string slot) => File.Delete(AssignmentPath(slot));

    /// <summary>Operator attests surviving processes were stopped. Never resets Git or changes Beads.</summary>
    public async Task ConfirmStoppedAsync(string slotId, CancellationToken token)
    {
        RequireLease();
        var slot = ReadManifest()?.Slots.SingleOrDefault(s => s.Name == slotId)
            ?? throw new WorkspacePreparationException($"unknown slot '{slotId}'");
        await VerifySlotAsync(slot, token);
        using var ownership = await WorkspaceOwnership.AcquireAsync(git,
            [new(slot.Name, slot.Path, new(false, "", null, null, false), false)], token);
        var record = ReadAssignment(slotId);
        if (record?.Phase == "execution-uncertain") SaveAssignment(record with { Phase = "stopped" });
    }

    public async Task<PoolAssignment?> AcquireAssignmentAsync(ValidatedAgent worker, Beads beads,
        string workerId, CancellationToken token)
    {
        RequireLease();
        await allocation.WaitAsync(token);
        try
        {
            var manifest = ReadManifest() ?? throw new WorkspacePreparationException("pool not initialized");
            // Inspect the WHOLE pool, including slots above the current worker count. Branch and journal
            // disagreement is a conflict, not permission to overwrite either source of evidence.
            var candidates = new List<(Slot Slot, AssignmentRecord? Record, string? Issue)>();
            var issueSlots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slot in manifest.Slots)
            {
                var record = ReadAssignment(slot.Name);
                await VerifySlotAsync(slot, token);
                var branch = (await git.GetWorkspaceStatusAsync(slot.Path, worker.Name, token)).Branch;
                string? branchIssue = Git.TryGetIssueId(branch, out var parsedIssue) ? parsedIssue : null;
                foreach (var id in new[] { record?.IssueId, branchIssue }.Where(id => id is not null).Distinct())
                {
                    if (issueSlots.TryGetValue(id!, out var other) && other != slot.Name)
                        throw new WorkspacePreparationException($"issue '{id}' appears in both {other} and {slot.Name}; review the pool before dispatch");
                    issueSlots[id!] = slot.Name;
                }
                lock (activeAssignments) if (activeAssignments.ContainsKey(slot.Name)) continue;
                if (record?.Phase == "execution-uncertain") continue;
                if (record?.IssueId is not null && branchIssue is not null && branchIssue != record.IssueId) continue;
                candidates.Add((slot, record, record?.IssueId ?? branchIssue));
            }
            // Interrupted work first. Clean committed branches are reservations too.
            foreach (var candidate in candidates.OrderBy(c => c.Issue is null))
            {
                var (slot, previous, issueId) = candidate;
                var agent = worker with { WorkspacePath = slot.Path };
                WorkspaceOwnership ownership;
                try { ownership = await WorkspaceOwnership.AcquireAsync(git, [agent], token); }
                catch (StartupInvariantException) { continue; }
                try
                {
                    var status = await git.GetWorkspaceStatusAsync(slot.Path, worker.Name, token);
                    BeadsIssue? issue = issueId is null ? null : await beads.GetIssueAsync(slot.Path, worker.Name, issueId, token);
                    string? recovery = null;
                    if (issueId is not null && issue?.Status is IssueStatus.Open or IssueStatus.InProgress)
                    {
                        // Never steal a manual reassignment. Old display names are accepted only with
                        // a durable assignment proving this pool previously owned that exact issue.
                        if (!string.IsNullOrEmpty(issue.Assignee) && issue.Assignee != (previous?.ExpectedAssignee ?? previous?.AgentName) && issue.Assignee != previous?.AgentName) continue;
                        if (issue.Status == IssueStatus.InProgress && previous is null) continue;
                        if (status.Branch != $"abacus/{issueId}"
                            && (status.IsDirty || previous?.Phase != "claiming"
                                || !status.Branch.StartsWith("detached@", StringComparison.Ordinal)
                                || !await IsMergedAsync(slot.Path, worker.Targets!, token))) continue;
                        recovery = issueId;
                    }
                    else
                    {
                        if (issueId is not null && issue?.Status != IssueStatus.Closed) continue;
                        if (status.IsDirty || !await IsMergedAsync(slot.Path, worker.Targets!, token)) continue;
                        // Closed is not enough: only merged, clean work becomes general capacity.
                        var head = (await RequiredAsync(slot.Path, ["rev-parse", "HEAD"], token)).StandardOutput.Trim();
                        await RequiredAsync(slot.Path, ["switch", "--detach", "--no-overwrite-ignore", head], token);
                    }
                    var record = new AssignmentRecord(1, Guid.NewGuid().ToString("N"), runId, workerId,
                        slot.Name, worker.Name, recovery, recovery is null ? "leased" : previous?.Phase == "claiming" ? "claiming" : "prepared",
                        DateTimeOffset.UtcNow, ExpectedAssignee: recovery is null ? null : issue?.Assignee);
                    SaveAssignment(record);
                    lock (activeAssignments) activeAssignments.Add(slot.Name, record.Id);
                    var result = new PoolAssignment(this, ownership, slot, record, recovery, issue?.Assignee);
                    ownership = null!; // handed to the assignment
                    return result;
                }
                finally { ownership?.Dispose(); }
            }
            return null;
        }
        finally { allocation.Release(); }
    }

    private async Task<bool> IsMergedAsync(string workspace, TargetRegistry targets, CancellationToken token)
    {
        var head = (await RequiredAsync(workspace, ["rev-parse", "HEAD"], token)).StandardOutput.Trim();
        foreach (var branch in targets.Targets.Keys)
        {
            var result = await runner.RunAsync(new CommandSpec(gitExecutable,
                ["-C", repository, "merge-base", "--is-ancestor", head, $"refs/heads/{branch}"], repository), token);
            if (result.Succeeded) return true;
            if (result.ExitCode != 1) throw new WorkspacePreparationException("could not verify pool history");
        }
        return false;
    }
}
