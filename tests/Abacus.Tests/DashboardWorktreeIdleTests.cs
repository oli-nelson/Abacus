using System.Diagnostics;
using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardWorktreeIdleTests
{
    [Fact]
    public async Task SixtySecondsWithTenVisibleSubscribersDoesNotRegeneratePatchesOrPublishData()
    {
        var root = Directory.CreateTempSubdirectory("abacus-worktree-idle-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, root.FullName)); Assert.True(result.Succeeded, result.StandardError);
        }
        WorktreeWatch[] readers = [];
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            var file = Path.Combine(root.FullName, "tracked.txt");
            await File.WriteAllTextAsync(file, "base\n"); await Git("add", "."); await Git("commit", "-m", "base");
            await File.WriteAllTextAsync(file, "already dirty\n");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "untracked.txt"), "untracked bytes\n");
            var source = new DashboardGit(runner, root.FullName, default);
            var collector = new GitCollector(source, root.FullName); var initial = await collector.RefreshAsync(default);
            var id = Assert.Single(initial.View.Facts!.Worktrees).Id;
            readers = Enumerable.Range(0, 10).Select(_ => collector.WatchWorktree(id)).ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            foreach (var reader in readers) await reader.Reader.ReadAsync(timeout.Token);
            await collector.WorktreeWatches.ReconcileAsync(timeout.Token);
            var queries = source.GitDiffCommands; var commands = source.GitCommands;
            var bytes = source.WorktreeHashBytes; var hashTime = source.WorktreeHashTime;
            var probes = 0; var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), timeout.Token);
                Assert.Same(initial, await collector.RefreshAsync(timeout.Token)); probes++;
                Assert.All(readers, reader => Assert.False(reader.Reader.TryRead(out _)));
            }
            Assert.Equal(queries, source.GitDiffCommands);
            var record = new { seconds = watch.Elapsed.TotalSeconds, clients = readers.Length, probes,
                repeatDiffQueries = source.GitDiffCommands - queries, dataMessages = 0,
                sourceCommands = source.GitCommands - commands, fingerprintBytes = source.WorktreeHashBytes - bytes,
                fingerprintSeconds = (source.WorktreeHashTime - hashTime).TotalSeconds,
                scope = "Real Git, one dirty worktree, ten server subscriptions. Not full HTTP/Beads/scale acceptance." };
            await File.WriteAllTextAsync("/tmp/abacus-worktree-idle.json", JsonSerializer.Serialize(record));
        }
        finally { foreach (var reader in readers) reader.Dispose(); root.Delete(true); }
    }
}
