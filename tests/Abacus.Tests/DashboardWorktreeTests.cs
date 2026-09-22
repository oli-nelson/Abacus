using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardWorktreeTests
{
    [Fact]
    public async Task RegisteredWorktreesPublishCleanDirtyUnknownWithoutChangingIndexOrRefs()
    {
        var root = Directory.CreateTempSubdirectory("abacus-worktree-status-");
        var runner = new CommandRunner(TextWriter.Null);
        var repo = Path.Combine(root.FullName, "repo"); Directory.CreateDirectory(repo);
        var linked = Path.Combine(root.FullName, "linked");
        async Task Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, repo)); Assert.True(result.Succeeded, result.StandardError);
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "base");
            await File.WriteAllTextAsync(Path.Combine(repo, ".gitignore"), "cache/\n");
            await Git("add", "."); await Git("commit", "-m", "base");
            await Git("worktree", "add", "--detach", linked, "HEAD");
            var source = new DashboardGit(runner, repo, default);
            var indexPath = Path.Combine(repo, ".git", "index");
            var index = await File.ReadAllBytesAsync(indexPath);
            var clean = await source.ProbeAsync(default);
            var linkedPath = clean.Worktrees.Single(w => w.Detached).Path;
            var repoPath = clean.Worktrees.Single(w => !w.Detached).Path;
            Assert.All(clean.Worktrees, w => { Assert.False(w.Dirty); Assert.Null(w.StatusError); });
            Assert.Equal(clean.Revision, (await source.ProbeAsync(default)).Revision);
            Directory.CreateDirectory(Path.Combine(linked, "cache"));
            await File.WriteAllTextAsync(Path.Combine(linked, "cache", "ignored"), "cache");
            Assert.Equal(clean.Revision, (await source.ProbeAsync(default)).Revision);
            await File.WriteAllTextAsync(Path.Combine(linked, "untracked.txt"), "new");
            var dirty = await source.ProbeAsync(default);
            Assert.NotEqual(clean.Revision, dirty.Revision);
            Assert.True(dirty.Worktrees.Single(w => w.Path == linkedPath).Dirty);
            Assert.True(dirty.Worktrees.Single(w => w.Path == linkedPath).Detached);
            Assert.False(dirty.Worktrees.Single(w => w.Path == repoPath).Dirty);
            File.Delete(Path.Combine(linked, "untracked.txt"));
            await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "changed");
            var tracked = await source.ProbeAsync(default);
            Assert.True(tracked.Worktrees.Single(w => w.Path == repoPath).Dirty);
            Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
            Assert.Equal(clean.Branches.ToArray(), tracked.Branches.ToArray());
            Directory.Delete(linked, true);
            var missing = await source.ProbeAsync(default);
            var registration = missing.Worktrees.Single(w => w.Path == linkedPath);
            Assert.Null(registration.Dirty); Assert.NotNull(registration.StatusError);
            Assert.True(missing.Worktrees.Single(w => w.Path == repoPath).Dirty);
        }
        finally { root.Delete(true); }
    }
}
