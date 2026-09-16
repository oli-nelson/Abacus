using Abacus;

namespace Abacus.Tests;

public sealed partial class WorktreePoolTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("abacus-pool-tests-").FullName;
    private readonly CommandRunner runner = new(TextWriter.Null);
    private string Repository => Path.Combine(root, "repo");
    private static CancellationToken Token => CancellationToken.None;

    private async Task<string> GitAsync(string path, params string[] args)
    {
        var result = await runner.RunAsync(new CommandSpec("git", ["-C", path, .. args], path), Token);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private async Task<WorktreePool> CreateAsync()
    {
        Directory.CreateDirectory(Repository);
        await GitAsync(Repository, "init", "--initial-branch=main");
        await GitAsync(Repository, "config", "user.name", "Test");
        await GitAsync(Repository, "config", "user.email", "test@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(Repository, ".gitignore"), "target/\n");
        await File.WriteAllTextAsync(Path.Combine(Repository, "source"), "initial");
        await GitAsync(Repository, "add", ".");
        await GitAsync(Repository, "commit", "-m", "initial");
        // Git returns physical paths; use the same form for the test's external data root.
        var physical = await GitAsync(Repository, "rev-parse", "--show-toplevel");
        return await WorktreePool.OpenAsync(runner, physical, Token,
            dataDirectory: Path.Combine(Path.GetDirectoryName(physical)!, "data"));
    }

    private TargetRegistry Targets => new(new Dictionary<string, TargetPolicy>
        { ["main"] = new("main", null, "test") });

    [Fact]
    public void ManagedWorkersDefaultToOneAndCountNeedsNoPaths()
    {
        var one = Options.Parse(["run", "--model", "p/m"]).Value!;
        Assert.Equal(1, one.ManagedAgentCount);
        Assert.Equal("agent-1", one.Agents.Single().Name);
        var many = Options.Parse(["run", "--model", "p/m", "--agents", "3"]).Value!;
        Assert.Equal(3, many.Agents.Count);
        Assert.All(many.Agents, a => Assert.Empty(a.WorkspacePath));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agents", "0"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--agents", "2", "-a", "a", "/tmp/a"]));
    }

    [Fact]
    public void MaintenanceCommandsAreScopedAndRemovalRequiresConfirmation()
    {
        Assert.Equal("list", Options.Parse(["worktrees", "list"]).WorktreeCommand);
        Assert.Equal("slot-2", Options.Parse(["worktrees", "reclaim", "slot-2"]).WorktreeSlot);
        Assert.Equal("remove", Options.Parse(["worktrees", "remove", "slot-2", "--confirm"]).WorktreeCommand);
        Assert.Throws<OptionsException>(() => Options.Parse(["worktrees", "remove", "slot-2"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["worktrees", "reclaim", "../user"]));
    }

    [Fact]
    public async Task InspectionIsReadOnlyAndAllocationReusesCachesAcrossLeases()
    {
        var pool = await CreateAsync();
        Assert.Null(pool.ReadManifest());
        Assert.Empty(await pool.InspectAsync(Token));
        Assert.False(Directory.Exists(Path.Combine(Repository, ".git", "abacus")));
        var commit = await GitAsync(Repository, "rev-parse", "HEAD");
        string workspace;
        using (pool.AcquireLease())
        {
            var agents = await pool.EnsureSlotsAsync(2, commit, Token);
            Assert.Equal(2, agents.Count);
            Assert.All(await pool.InspectAsync(Token), slot => Assert.Equal("in-use", slot.State));
            workspace = agents[0].WorkspacePath;
            Assert.DoesNotContain("/repo/", workspace);
            Directory.CreateDirectory(Path.Combine(workspace, "target"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "target", "cache"), "warm");
        }
        Assert.All(await pool.InspectAsync(Token), slot => Assert.Equal("available", slot.State));
        using (pool.AcquireLease())
        {
            var agents = await pool.EnsureSlotsAsync(1, commit, Token);
            Assert.Equal(workspace, agents.Single().WorkspacePath);
            Assert.Equal("warm", await File.ReadAllTextAsync(Path.Combine(workspace, "target", "cache")));
            Assert.Equal(2, pool.ReadManifest()!.Slots.Count); // reducing capacity never deletes work
        }
    }

    [Fact]
    public async Task LeaseExcludesAnotherControllerAndMaintenance()
    {
        var pool = await CreateAsync();
        var other = await WorktreePool.OpenAsync(runner, Repository, Token);
        using (pool.AcquireLease()) Assert.Throws<StartupInvariantException>(() => other.AcquireLease());
        using var next = other.AcquireLease();
    }

    [Fact]
    public async Task ReclaimPreservesCachesAndRejectsDirtyOrUnmergedWork()
    {
        var pool = await CreateAsync();
        using var lease = pool.AcquireLease();
        var commit = await GitAsync(Repository, "rev-parse", "HEAD");
        var workspace = (await pool.EnsureSlotsAsync(1, commit, Token)).Single().WorkspacePath;
        await File.WriteAllTextAsync(Path.Combine(workspace, "scratch"), "keep");
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.ReclaimAsync("slot-1", Targets, new Beads(runner), Token));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(workspace, "scratch")));
        File.Delete(Path.Combine(workspace, "scratch"));
        await GitAsync(workspace, "switch", "-c", "private-work");
        await File.WriteAllTextAsync(Path.Combine(workspace, "source"), "unmerged");
        await GitAsync(workspace, "commit", "-am", "work");
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.ReclaimAsync("slot-1", Targets, new Beads(runner), Token));
        await GitAsync(Repository, "merge", "--ff-only", "private-work");
        Directory.CreateDirectory(Path.Combine(workspace, "target"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "target", "cache"), "warm");
        await pool.ReclaimAsync("slot-1", Targets, new Beads(runner), Token);
        Assert.Equal("", await GitAsync(workspace, "branch", "--show-current"));
        Assert.Equal("warm", await File.ReadAllTextAsync(Path.Combine(workspace, "target", "cache")));
        await pool.RemoveAsync("slot-1", Targets, new Beads(runner), Token);
        Assert.False(Directory.Exists(workspace));
        Assert.Empty(pool.ReadManifest()!.Slots);
    }

    [Fact]
    public async Task UserWorktreesAreNeverAdoptedAndMissingSlotsAreNotSilentlyRecreated()
    {
        var pool = await CreateAsync();
        var user = Path.Combine(root, "user-worktree");
        await GitAsync(Repository, "worktree", "add", "--detach", user);
        using var lease = pool.AcquireLease();
        var commit = await GitAsync(Repository, "rev-parse", "HEAD");
        var workspace = (await pool.EnsureSlotsAsync(1, commit, Token)).Single().WorkspacePath;
        Assert.NotEqual(user, workspace);
        Directory.Delete(workspace, recursive: true);
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.EnsureSlotsAsync(1, commit, Token));
        await Assert.ThrowsAsync<WorkspacePreparationException>(() => pool.PruneAsync(Token));
        await GitAsync(Repository, "worktree", "prune");
        Assert.Equal(["slot-1"], await pool.PruneAsync(Token));
        Assert.True(Directory.Exists(user));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
