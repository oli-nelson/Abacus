using System.Text.Json;
using Abacus;

namespace Abacus.Tests;

public sealed partial class WorktreePoolTests
{
    private ValidatedAgent Worker(string name) => new(name, Repository, new(false, "test", null, null, false),
        false, Targets: Targets);
    private static string WorkerId() => Guid.NewGuid().ToString("N");
    private async Task<Beads> PoolBeadsAsync()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var script = Path.Combine(root, "bd");
        await File.WriteAllTextAsync(script, $$"""
            #!/bin/sh
            root='{{root}}'
            case "$1" in
              show) printf '['; cat "$root/$2.json"; printf ']\n' ;;
              update) printf '{"id":"%s","status":"in_progress","assignee":"%s"}\n' "$2" "$BEADS_ACTOR" > "$root/$2.json"; cat "$root/$2.json" ;;
              ready) printf '[]\n' ;;
              *) printf 'unexpected bd: %s\n' "$*" >&2; exit 1 ;;
            esac
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new Beads(runner, script);
    }
    private Task IssueAsync(string id, string status, string? assignee = null) =>
        File.WriteAllTextAsync(Path.Combine(root, id + ".json"), JsonSerializer.Serialize(new { id, status, assignee }));

    [Fact]
    public void NamesAreOptionalLabelsAndHaveIndependentCliAndConfigSettings()
    {
        var options = Options.Parse(["run", "--model", "p/m", "--agents", "3", "--agent-name", "Alice", "--agent-name", "Bob"]).Value!;
        Assert.Equal(["Alice", "Bob", "agent-3"], options.Agents.Select(a => a.Name));
        Assert.All(options.Agents, a => Assert.Empty(a.WorkspacePath));
        Assert.Equal(2, Options.Parse(["run", "--model", "p/m", "--agent-name", "x", "--agent-name", "y"]).Value!.ManagedAgentCount);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agents", "1", "--agent-name", "x", "--agent-name", "y"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agent-name", "x", "--agent-name", "x"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agent-name", "maintenance"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agent-name", "x", "-a", "y", root]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agent-name", "bad\nname"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", .. Enumerable.Range(0, 257).SelectMany(i => new[] { "--agent-name", $"worker-{i}" })]));
        var path = Path.Combine(root, "config.json");
        File.WriteAllText(path, """{"version":1,"model":"p/m","agentCount":2,"agentNames":["a","b"]}""");
        Assert.Equal(["a", "b"], Options.Parse(["run", "--config", path]).Value!.Agents.Select(a => a.Name));
        Assert.Equal(["new", "agent-2"], Options.Parse(["run", "--config", path, "--agent-name", "new"]).Value!.Agents.Select(a => a.Name));
        Assert.Equal("legacy", Options.Parse(["run", "--config", path, "-a", "legacy", root]).Value!.Agents.Single().Name);
    }

    [Fact]
    public async Task AnyNameCanLeaseSameSlotAndAssignmentIdsFenceOldUpdates()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var first = (await pool.AcquireAssignmentAsync(Worker("Alice"), beads, WorkerId(), Token))!;
        Assert.NotNull(first);
        Assert.Null(await pool.AcquireAssignmentAsync(Worker("Bob"), beads, WorkerId(), Token));
        var id = first.Record.Id;
        first.Dispose();
        using var second = (await pool.AcquireAssignmentAsync(Worker("CompletelyDifferent"), beads, WorkerId(), Token))!;
        Assert.Equal(first.Slot.Path, second.Slot.Path);
        Assert.NotEqual(id, second.Record.Id);
        Assert.Throws<InvalidOperationException>(() => first.Prepared());
        Assert.Throws<StartupInvariantException>(() => pool.UpdateAssignment(first.Record));
    }

    [Fact]
    public async Task ReducedCapacityRecoversHighSlotAcrossRenameAndPreservesDirtyWork()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(3, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var first = (await pool.AcquireAssignmentAsync(Worker("one"), beads, WorkerId(), Token))!;
        var second = (await pool.AcquireAssignmentAsync(Worker("two"), beads, WorkerId(), Token))!;
        var third = (await pool.AcquireAssignmentAsync(Worker("old-name"), beads, WorkerId(), Token))!;
        Assert.Equal("slot-3", third.Slot.Name);
        Assert.True(third.TryClaiming("abc-1"));
        await GitAsync(third.Slot.Path, "switch", "-c", "abacus/abc-1");
        third.Prepared();
        await File.WriteAllTextAsync(Path.Combine(third.Slot.Path, "source"), "unfinished");
        await IssueAsync("abc-1", "in_progress", "old-name");
        third.Dispose(); first.Dispose(); second.Dispose();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var recovered = (await pool.AcquireAssignmentAsync(Worker("new-name"), beads, WorkerId(), Token))!;
        Assert.Equal("slot-3", recovered.Slot.Name);
        Assert.Equal("abc-1", recovered.RecoveryIssueId);
        var coordinator = new ClaimCoordinator(beads, new Git(runner), new TicketRecovery(beads, TextWriter.Null), TextWriter.Null);
        var agent = pool.AssignWorker(Worker("new-name"), recovered) with { Targets = null };
        var claim = await coordinator.WaitForPreparedClaimAsync(agent, true, ExecutionMode.Once, Token);
        Assert.Equal("new-name", claim!.Issue.Assignee);
        Assert.Equal("unfinished", await File.ReadAllTextAsync(Path.Combine(third.Slot.Path, "source")));
        Assert.Equal("abacus/abc-1", await GitAsync(third.Slot.Path, "branch", "--show-current"));
    }

    [Fact]
    public async Task JournalBeforeBranchCreationRecoversClaimWithoutDependingOnName()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var old = (await pool.AcquireAssignmentAsync(Worker("old"), beads, WorkerId(), Token))!;
        old.TryClaiming("abc-1");
        await IssueAsync("abc-1", "in_progress", "old");
        old.Dispose();
        using var recovered = (await pool.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token))!;
        var coordinator = new ClaimCoordinator(beads, new Git(runner), new TicketRecovery(beads, TextWriter.Null), TextWriter.Null);
        var agent = pool.AssignWorker(Worker("new"), recovered) with { Targets = null };
        Assert.NotNull(await coordinator.WaitForPreparedClaimAsync(agent, true, ExecutionMode.Once, Token));
        Assert.Equal("abacus/abc-1", await GitAsync(recovered.Slot.Path, "branch", "--show-current"));
    }

    [Fact]
    public async Task UncertainLaunchSurvivesControllerRestartUntilExplicitStopConfirmation()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using (pool.AcquireLease())
        {
            await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
            using var old = (await pool.AcquireAssignmentAsync(Worker("old"), beads, WorkerId(), Token))!;
            old.TryClaiming("abc-1");
            await GitAsync(old.Slot.Path, "switch", "-c", "abacus/abc-1");
            await IssueAsync("abc-1", "in_progress", "old");
            old.Prepared(); old.Launching();
            Assert.Throws<StartupInvariantException>(() => old.Prepared());
        }
        var restarted = await WorktreePool.OpenAsync(runner, await GitAsync(Repository, "rev-parse", "--show-toplevel"), Token);
        using var lease = restarted.AcquireLease();
        Assert.Null(await restarted.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token));
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => restarted.RemoveAsync("slot-1", Targets, beads, Token));
        Assert.Throws<OptionsException>(() => Options.Parse(["worktrees", "recover", "slot-1"]));
        Assert.Equal("recover", Options.Parse(["worktrees", "recover", "slot-1", "--confirm"]).WorktreeCommand);
        await restarted.ConfirmStoppedAsync("slot-1", Token);
        using var recovered = await restarted.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token);
        Assert.Equal("abc-1", recovered!.RecoveryIssueId);
    }

    [Fact]
    public async Task TwoSlotsCannotReserveSameIssueAndExternalReassignmentIsNotStolen()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(2, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var first = (await pool.AcquireAssignmentAsync(Worker("first"), beads, WorkerId(), Token))!;
        using var second = (await pool.AcquireAssignmentAsync(Worker("second"), beads, WorkerId(), Token))!;
        Assert.True(first.TryClaiming("abc-1"));
        Assert.False(second.TryClaiming("abc-1"));
        await GitAsync(first.Slot.Path, "switch", "-c", "abacus/abc-1");
        first.Prepared();
        await IssueAsync("abc-1", "in_progress", "some-human");
        first.Dispose();
        Assert.Null(await pool.AcquireAssignmentAsync(Worker("renamed"), beads, WorkerId(), Token));
        Assert.Contains("some-human", await File.ReadAllTextAsync(Path.Combine(root, "abc-1.json")));
    }

    [Fact]
    public async Task ClosedButUnmergedAndDetachedDirtySlotsAreNotGeneralCapacity()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        var slots = await pool.EnsureSlotsAsync(2, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        await GitAsync(slots[0].WorkspacePath, "switch", "-c", "abacus/abc-1");
        await File.WriteAllTextAsync(Path.Combine(slots[0].WorkspacePath, "source"), "unmerged");
        await GitAsync(slots[0].WorkspacePath, "commit", "-am", "unfinished merge");
        await IssueAsync("abc-1", "closed", "old");
        await File.WriteAllTextAsync(Path.Combine(slots[1].WorkspacePath, "scratch"), "preserve");
        Assert.Null(await pool.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(slots[1].WorkspacePath, "scratch")));
    }
}

public sealed partial class WorktreePoolTests
{
    [Fact]
    public async Task CrashDuringRenameCanBeRecoveredAgainWithAnotherName()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var first = (await pool.AcquireAssignmentAsync(Worker("Alice"), beads, WorkerId(), Token))!;
        first.TryClaiming("abc-1");
        await GitAsync(first.Slot.Path, "switch", "-c", "abacus/abc-1");
        first.Prepared();
        await IssueAsync("abc-1", "in_progress", "Alice");
        first.Dispose();
        var second = (await pool.AcquireAssignmentAsync(Worker("Bob"), beads, WorkerId(), Token))!;
        await beads.ResumePoolIssueAsync(second.Slot.Path, "Bob", "abc-1", second.PreviousAgentName, Token);
        // Crash before the controller can mark preparation complete.
        second.Dispose();
        using var third = (await pool.AcquireAssignmentAsync(Worker("Charlie"), beads, WorkerId(), Token))!;
        Assert.NotNull(third);
        Assert.Equal("Bob", third.PreviousAgentName);
        Assert.Equal("Charlie", (await beads.ResumePoolIssueAsync(third.Slot.Path, "Charlie", "abc-1", third.PreviousAgentName, Token)).Assignee);
    }

    [Fact]
    public async Task DuplicateIssueCheckoutsFailClosedAndKeepBothTrees()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        var slots = await pool.EnsureSlotsAsync(2, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        await GitAsync(slots[0].WorkspacePath, "switch", "-c", "abacus/abc-1");
        await GitAsync(slots[1].WorkspacePath, "switch", "--ignore-other-worktrees", "abacus/abc-1");
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token));
        Assert.All(slots, s => Assert.True(Directory.Exists(s.WorkspacePath)));
    }

    [Fact]
    public async Task DetachedUnmergedHeadCannotBeDiscardedDuringInterruptedClaimRecovery()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var old = (await pool.AcquireAssignmentAsync(Worker("old"), beads, WorkerId(), Token))!;
        old.TryClaiming("abc-1");
        await IssueAsync("abc-1", "in_progress", "old");
        await File.WriteAllTextAsync(Path.Combine(old.Slot.Path, "source"), "detached work");
        await GitAsync(old.Slot.Path, "commit", "-am", "must preserve");
        var head = await GitAsync(old.Slot.Path, "rev-parse", "HEAD");
        old.Dispose();
        Assert.Null(await pool.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token));
        Assert.Equal(head, await GitAsync(old.Slot.Path, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task HostJournalsBeforeLaunchAndOnlyVerifiedCleanupClearsUncertainty()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var assignment = (await pool.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token))!;
        assignment.TryClaiming("abc-1"); assignment.Prepared();
        var inner = new JournalTestHost(() => Assert.Equal("execution-uncertain", pool.ReadAssignment(assignment.Slot.Name)!.Phase));
        var host = new PoolAgentHost(inner);
        var run = await host.StartAgentAsync(pool.AssignWorker(Worker("new"), assignment),
            new BeadsIssue("abc-1", IssueStatus.InProgress), "model", "high", null, Token);
        inner.FailCleanup = true;
        await Assert.ThrowsAsync<IOException>(() => host.StopAndCleanupAsync(run, Token));
        Assert.Equal("execution-uncertain", pool.ReadAssignment(assignment.Slot.Name)!.Phase);
        inner.FailCleanup = false;
        await host.StopAndCleanupAsync(run, Token);
        Assert.Equal("stopped", pool.ReadAssignment(assignment.Slot.Name)!.Phase);
        var restarted = await host.StartAgentAsync(pool.AssignWorker(Worker("new"), assignment),
            new BeadsIssue("abc-1", IssueStatus.InProgress), "model", "high", null, Token);
        await Assert.ThrowsAsync<StartupInvariantException>(() => host.StopAndCleanupAsync(run, Token));
        Assert.True(await host.IsRunningAsync(restarted, Token));
        await host.StopAndCleanupAsync(restarted, Token);
    }

    [Fact]
    public async Task FailedLaunchRetainsUncertaintyAndDoesNotPermitReclaim()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var assignment = (await pool.AcquireAssignmentAsync(Worker("new"), beads, WorkerId(), Token))!;
        assignment.TryClaiming("abc-1"); assignment.Prepared();
        var host = new PoolAgentHost(new JournalTestHost(() => throw new IOException("ambiguous launch")));
        await Assert.ThrowsAsync<IOException>(() => host.StartAgentAsync(pool.AssignWorker(Worker("new"), assignment),
            new BeadsIssue("abc-1", IssueStatus.InProgress), "model", "high", null, Token));
        assignment.Dispose();
        Assert.Equal("execution-uncertain", pool.ReadAssignment(assignment.Slot.Name)!.Phase);
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.ReclaimAsync(assignment.Slot.Name, Targets, beads, Token));
    }

    [Fact]
    public async Task ExistingAgentNumberedSlotIsNotRenamedOrTiedToWorkerName()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        var slot = (await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token)).Single();
        var oldPath = Path.Combine(Path.GetDirectoryName(slot.WorkspacePath)!, "agent-6");
        await GitAsync(Repository, "worktree", "move", slot.WorkspacePath, oldPath);
        var manifestPath = Path.Combine(Repository, ".git", "abacus", "pool.json");
        await File.WriteAllTextAsync(manifestPath, (await File.ReadAllTextAsync(manifestPath)).Replace("slot-1", "agent-6"));
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var assignment = await pool.AcquireAssignmentAsync(Worker("NotAgentSix"), beads, WorkerId(), Token);
        Assert.Equal("agent-6", assignment!.Slot.Name);
        Assert.Equal(oldPath, assignment.Slot.Path);
        Assert.Single(pool.ReadManifest()!.Slots);
    }

    private sealed class JournalTestHost(Action beforeLaunch) : IAgentHost
    {
        public bool FailCleanup { get; set; }
        private bool running;
        private sealed class TestRun : IAgentRun
        {
            public string Location => "test execution";
            public bool HasExited => false;
            public int? TryReadExitCode() => null;
        }
        public Task<IAgentRun> StartAgentAsync(ValidatedAgent agent, BeadsIssue issue, string model, string effort,
            string? serverUrl, CancellationToken cancellationToken, IReadOnlyList<string>? extraArguments = null)
        { beforeLaunch(); running = true; return Task.FromResult<IAgentRun>(new TestRun()); }
        public Task<bool> IsRunningAsync(IAgentRun run, CancellationToken cancellationToken) => Task.FromResult(running);
        public Task StopAndCleanupAsync(IAgentRun run, CancellationToken cancellationToken)
        {
            if (FailCleanup) throw new IOException("cleanup failed");
            running = false; return Task.CompletedTask;
        }
    }
}

public sealed partial class WorktreePoolTests
{
    [Fact]
    public async Task ClosedMergedAssignmentCanLeaveItsCheckoutOnTargetBranch()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var assignment = (await pool.AcquireAssignmentAsync(Worker("Alice"), beads, WorkerId(), Token))!;
        assignment.TryClaiming("abc-1");
        await GitAsync(assignment.Slot.Path, "switch", "-c", "abacus/abc-1");
        assignment.Prepared();
        await IssueAsync("abc-1", "closed", "Alice");
        await GitAsync(Repository, "switch", "--detach");
        await GitAsync(assignment.Slot.Path, "switch", "main");
        assignment.Dispose();
        using var next = await pool.AcquireAssignmentAsync(Worker("Bob"), beads, WorkerId(), Token);
        Assert.NotNull(next);
        Assert.Null(next.RecoveryIssueId);
        Assert.Equal(assignment.Slot.Path, next.Slot.Path);
        Assert.Equal("", await GitAsync(next.Slot.Path, "branch", "--show-current"));
    }
}

public sealed partial class WorktreePoolTests
{
    [Fact]
    public async Task OperatorStopDuringUncertainLaunchCannotRestartWorkerOnAnotherSlot()
    {
        var pool = await CreateAsync();
        var beads = await PoolBeadsAsync();
        using var lease = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var old = (await pool.AcquireAssignmentAsync(Worker("old"), beads, WorkerId(), Token))!;
        old.TryClaiming("abc-1");
        await GitAsync(old.Slot.Path, "switch", "-c", "abacus/abc-1");
        old.Prepared();
        await IssueAsync("abc-1", "in_progress", "old");
        old.Dispose();
        using var control = new AgentControl();
        var host = new PoolAgentHost(new JournalTestHost(() =>
        {
            control.Request(AgentControlAction.Stop);
            throw new OperationCanceledException();
        }));
        var git = new Git(runner);
        var recovery = new TicketRecovery(beads, TextWriter.Null);
        var claims = new ClaimCoordinator(beads, git, recovery, TextWriter.Null);
        var registry = new AgentRunRegistry();
        var loop = new AgentLoop(Worker("new") with { Targets = null }, true, "model", "high", null,
            claims, host, new TicketSupervisor(beads, host, recovery, TextWriter.Null), recovery,
            ExecutionMode.Once, new RunSummary(["new"], "baseline"), TextWriter.Null, git, control,
            registry, pool: pool, poolBeads: beads);
        await Assert.ThrowsAsync<SupervisorCleanupException>(() => loop.RunAsync(Token).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("execution-uncertain", pool.ReadAssignment("slot-1")!.Phase);
        Assert.True(registry.IsRunning("new")); // Do not reclaim a potentially live merge holder.
        Assert.Contains("in_progress", await File.ReadAllTextAsync(Path.Combine(root, "abc-1.json")));
    }
}

public sealed partial class WorktreePoolTests
{
    [Fact]
    public async Task SupervisorSnapshotIsReadOnlyAndUsesCurrentLeasesNotNamesOrPreflightPaths()
    {
        var pool = await CreateAsync();
        Assert.Empty(pool.GetDiagnosticSnapshot().Slots);
        Assert.False(Directory.Exists(Path.Combine(Repository, ".git", "abacus")));
        var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(2, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var old = (await pool.AcquireAssignmentAsync(Worker("old-name"), beads, WorkerId(), Token))!;
        old.Dispose();
        var idle = pool.GetDiagnosticSnapshot();
        Assert.All(idle.Slots, slot => Assert.False(slot.LeasedInThisRun));
        Assert.Equal("old-name", idle.Slots[0].Assignment!.AgentName);
        // Matching a historical label must not assign that slot to a current worker.
        var idleJson = JsonSerializer.SerializeToElement(MaintenanceSupervisor.BuildDiagnostics([], new Dictionary<string, string>(),
            [Worker("old-name")], idle, managedWorkers: true));
        Assert.Equal(JsonValueKind.Null, idleJson.GetProperty("workspaces")[0].GetProperty("WorkspacePath").ValueKind);

        using var current = (await pool.AcquireAssignmentAsync(Worker("renamed"), beads, WorkerId(), Token))!;
        Assert.True(current.TryClaiming("abc-7"));
        var before = File.ReadAllText(Path.Combine(Repository, ".git", "abacus", "assignment-slot-1.json"));
        var snapshot = pool.GetDiagnosticSnapshot();
        var diagnostic = JsonSerializer.SerializeToElement(MaintenanceSupervisor.BuildDiagnostics([], new Dictionary<string, string>(),
            [Worker("renamed"), Worker("idle")], snapshot, managedWorkers: true));
        var active = diagnostic.GetProperty("workspaces")[0];
        Assert.Equal(current.Slot.Path, active.GetProperty("WorkspacePath").GetString());
        Assert.NotEqual(Repository, active.GetProperty("WorkspacePath").GetString());
        Assert.Equal(current.Record.Id, active.GetProperty("AssignmentId").GetString());
        Assert.Equal(current.Slot.Name, active.GetProperty("SlotId").GetString());
        Assert.Equal(JsonValueKind.Null, diagnostic.GetProperty("workspaces")[1].GetProperty("WorkspacePath").ValueKind);
        Assert.Equal(2, diagnostic.GetProperty("pool").GetProperty("Slots").GetArrayLength());
        Assert.Equal("abc-7", snapshot.Slots[0].Assignment!.IssueId);
        Assert.Equal(before, File.ReadAllText(Path.Combine(Repository, ".git", "abacus", "assignment-slot-1.json")));
        var prompt = MaintenanceSupervisor.RenderPrompt("run", "/tmp/done", [], new Dictionary<string, string>(),
            [Worker("renamed")], null, snapshot, managedWorkers: true);
        var jsonStart = prompt.IndexOf("{\"issues\":", StringComparison.Ordinal);
        var jsonEnd = prompt.IndexOf('\n', jsonStart);
        using var embedded = JsonDocument.Parse(prompt[jsonStart..jsonEnd]);
        Assert.Equal(current.Slot.Path, embedded.RootElement.GetProperty("workspaces")[0].GetProperty("WorkspacePath").GetString());
    }

    [Fact]
    public async Task UnreadablePoolDiagnosticsStayUnknownWithoutDroppingOtherSlots()
    {
        var pool = await CreateAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(2, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var path = Path.Combine(Repository, ".git", "abacus", "assignment-slot-2.json");
        await File.WriteAllTextAsync(path, "broken journal");
        var snapshot = pool.GetDiagnosticSnapshot();
        Assert.Equal(2, snapshot.Slots.Count);
        Assert.Null(snapshot.Slots[1].LeasedInThisRun);
        Assert.NotNull(snapshot.Slots[1].Error);
        Assert.Equal("broken journal", await File.ReadAllTextAsync(path));
        var manifest = Path.Combine(Repository, ".git", "abacus", "pool.json");
        await File.WriteAllTextAsync(manifest, "broken manifest");
        var failed = pool.GetDiagnosticSnapshot();
        Assert.NotNull(failed.Error);
        Assert.Empty(failed.Slots);
        Assert.Equal("broken manifest", await File.ReadAllTextAsync(manifest));
    }
}
