namespace Abacus.Tests;

public sealed partial class WorktreePoolTests
{
    [Fact]
    public async Task MutationGuardRejectsUnlistedWorkspaceWithoutJournal()
    {
        var pool = await CreateAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var manifest = pool.ReadManifest()!; var slot = Assert.Single(manifest.Slots);
        await GitAsync(slot.Path, "checkout", "-b", "abacus/abc-1");
        Assert.Null(pool.ReadAssignment(slot.Name));
        await File.WriteAllTextAsync(Path.Combine(Repository, ".git", "abacus", "pool.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest with { Slots = [] }));
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        Assert.Equal("abacus/abc-1", await GitAsync(slot.Path, "branch", "--show-current"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationGuardRejectsUnlistedReservationsEvenWithoutLiveWorkers(bool released)
    {
        var pool = await CreateAsync(); var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var worker = (await pool.AcquireAssignmentAsync(Worker("worker"), beads, WorkerId(), Token))!;
        Assert.True(worker.TryClaiming("abc-1"));
        if (released) worker.Dispose();
        var manifest = pool.ReadManifest()!;
        var manifestPath = Path.Combine(Repository, ".git", "abacus", "pool.json");
        var altered = System.Text.Json.JsonSerializer.Serialize(manifest with { Slots = [] });
        await File.WriteAllTextAsync(manifestPath, altered);
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        Assert.Equal(altered, await File.ReadAllTextAsync(manifestPath));
        Assert.Equal("abc-1", pool.ReadAssignment("slot-1")!.IssueId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationGuardRejectsDirectoryOrDuplicateFieldJournal(bool directory)
    {
        var pool = await CreateAsync(); var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var worker = (await pool.AcquireAssignmentAsync(Worker("worker"), beads, WorkerId(), Token))!;
        Assert.True(worker.TryClaiming("abc-1")); worker.Dispose();
        var path = Path.Combine(Repository, ".git", "abacus", "assignment-slot-1.json");
        var original = await File.ReadAllTextAsync(path);
        if (directory) { File.Delete(path); Directory.CreateDirectory(path); }
        else await File.WriteAllTextAsync(path, original.TrimEnd()[..^1] + ",\"IssueId\":null}");
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        if (directory) { Assert.True(Directory.Exists(path)); Directory.Delete(path); }
        await File.WriteAllTextAsync(path, original);
        Assert.Equal("abc-1", pool.ReadAssignment("slot-1")!.IssueId);
        using var other = await pool.GuardUnreservedIssueAsync("abc-2", Token).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MutationGuardFencesExistingClaimersAndRestoresAdmissionOnDispose()
    {
        var pool = await CreateAsync(); var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var worker = (await pool.AcquireAssignmentAsync(Worker("worker"), beads, WorkerId(), Token))!;
        var before = pool.ReadAssignment(worker.Slot.Name);
        var guard = await pool.GuardUnreservedIssueAsync("abc-1", Token);
        Assert.False(worker.TryClaiming("abc-1"));
        Assert.Equal(before, pool.ReadAssignment(worker.Slot.Name));
        Assert.True(worker.TryClaiming("abc-2")); // Existing unrelated work is not globally paused.
        worker.ClaimContended();
        guard.Dispose(); guard.Dispose();
        Assert.True(worker.TryClaiming("abc-1"));
    }

    [Fact]
    public async Task MutationGuardBlocksAllocationAndCancelledWaiterDoesNotReleaseAnotherGuard()
    {
        var pool = await CreateAsync(); var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var guard = await pool.GuardUnreservedIssueAsync("abc-1", Token);
        using var cancellation = new CancellationTokenSource();
        var waiting = pool.GuardUnreservedIssueAsync("abc-2", cancellation.Token);
        Assert.False(waiting.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var allocation = pool.AcquireAssignmentAsync(Worker("worker"), beads, WorkerId(), Token);
        Assert.False(allocation.IsCompleted);
        guard.Dispose();
        using var assigned = await allocation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(assigned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationGuardRejectsDurableReservationsEvenAfterWorkerRelease(bool stopped)
    {
        var pool = await CreateAsync(); var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var worker = (await pool.AcquireAssignmentAsync(Worker("worker"), beads, WorkerId(), Token))!;
        Assert.True(worker.TryClaiming("abc-1")); worker.Prepared();
        var execution = worker.Launching();
        if (stopped) worker.Stopped(execution);
        worker.Dispose();
        var before = pool.ReadAssignment("slot-1");
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        Assert.Equal(before, pool.ReadAssignment("slot-1"));
        // Failure must release its temporary fence/allocation gate, never the reservation.
        using var other = await pool.GuardUnreservedIssueAsync("abc-2", Token).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MutationGuardRejectsCleanIssueBranchWithoutJournal()
    {
        var pool = await CreateAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var slot = Assert.Single(pool.ReadManifest()!.Slots);
        await GitAsync(slot.Path, "checkout", "-b", "abacus/abc-1");
        Assert.Null(pool.ReadAssignment(slot.Name));
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        Assert.Equal("abacus/abc-1", await GitAsync(slot.Path, "branch", "--show-current"));
        using var other = await pool.GuardUnreservedIssueAsync("abc-2", Token).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MutationGuardRejectsMalformedJournalAndMissingPoolCoverage()
    {
        var pool = await CreateAsync();
        using var controller = pool.AcquireLease();
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        var path = Path.Combine(Repository, ".git", "abacus", "assignment-slot-1.json");
        await File.WriteAllTextAsync(path, "broken journal");
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        Assert.Equal("broken journal", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MutationGuardRequiresControllerLease()
    {
        var pool = await CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
    }

    [Fact]
    public async Task MutationGuardRejectsLiveRunJournalMismatchWithoutChangingIt()
    {
        var pool = await CreateAsync(); var beads = await PoolBeadsAsync();
        using var controller = pool.AcquireLease();
        await pool.EnsureSlotsAsync(1, await GitAsync(Repository, "rev-parse", "HEAD"), Token);
        using var worker = (await pool.AcquireAssignmentAsync(Worker("worker"), beads, WorkerId(), Token))!;
        var path = Path.Combine(Repository, ".git", "abacus", "assignment-slot-1.json");
        var original = await File.ReadAllTextAsync(path);
        var record = pool.ReadAssignment("slot-1")!;
        var foreign = System.Text.Json.JsonSerializer.Serialize(record with { RunId = Guid.NewGuid().ToString("N") });
        await File.WriteAllTextAsync(path, foreign);
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.GuardUnreservedIssueAsync("abc-1", Token));
        Assert.Equal(foreign, await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, original);
        using var guard = await pool.GuardUnreservedIssueAsync("abc-1", Token).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
