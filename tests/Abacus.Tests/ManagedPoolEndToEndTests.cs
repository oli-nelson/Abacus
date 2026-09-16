using Abacus;

namespace Abacus.Tests;

public sealed partial class EndToEndTests
{
    [Fact]
    public async Task ManagedRunAllocatesRealWorktreeReusesCacheAndPreflightIsReadOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-e2e-managed-pool-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            File.Delete(Path.Combine(bin, "git")); // Real Git; only the Beads/harness boundary is scripted.
            var runner = new CommandRunner(TextWriter.Null);
            async Task<string> Git(params string[] args)
            {
                var result = await runner.RunAsync(new CommandSpec("git", ["-C", root.FullName, .. args], root.FullName));
                Assert.True(result.Succeeded, result.StandardError);
                return result.StandardOutput.Trim();
            }
            await Git("init", "--initial-branch=main");
            await Git("config", "user.name", "Test");
            await Git("config", "user.email", "test@example.invalid");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, ".gitignore"), "target/\n");
            await Git("add", ".gitignore", ".abacus");
            await Git("commit", "-m", "initial");
            var physical = await Git("rev-parse", "--show-toplevel");
            var bd = Path.Combine(bin, "bd");
            await File.WriteAllTextAsync(bd, (await File.ReadAllTextAsync(bd))
                .Replace("assignee=alice", "assignee=$(cat \"$root/claim-name\")")
                .Replace("touch \"$root/claimed\"; printf", "printf '%s' \"$BEADS_ACTOR\" > \"$root/claim-name\"; touch \"$root/claimed\"; printf")
                .Replace("elif test \"$3\" = --metadata; then", """
                    elif test "$3" = --status && test "$4" = in_progress; then
                      printf '%s' "$6" > "$root/claim-name"
                      printf in_progress > "$root/status"
                      printf '[]\n'
                    elif test "$3" = --metadata; then
                    """));
            var harness = Path.Combine(bin, "opencode");
            await File.WriteAllTextAsync(harness, (await File.ReadAllTextAsync(harness)).Replace("#!/bin/sh",
                "#!/bin/sh\nmkdir -p target\nprintf warm-cache > target/cache\n"));
            var environment = new Dictionary<string, string?>
                { ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
            async Task<CommandResult> Abacus(params string[] args) => await runner.RunAsync(new CommandSpec(
                FindOnPath("dotnet"), [typeof(Program).Assembly.Location, .. args], root.FullName, environment));
            string[] runOptions = ["--repo", physical, "--model", "provider/model", "--mode", "opencode-server",
                "--opencode-server", "localhost:4096", "--agents", "1"];
            var preflight = await Abacus(["preflight", .. runOptions]);
            Assert.True(preflight.Succeeded, preflight.StandardError);
            Assert.False(Directory.Exists(Path.Combine(physical, ".git", "abacus")));
            Assert.Single((await Git("worktree", "list", "--porcelain")).Split('\n'), line => line.StartsWith("worktree "));

            // Pin only the test pool's external root, avoiding writes to the user's actual data directory.
            // Leave no slots: the Abacus process must allocate the real workspace itself.
            var pool = await WorktreePool.OpenAsync(runner, physical, CancellationToken.None,
                dataDirectory: Path.Combine(Path.GetDirectoryName(physical)!, root.Name + "-data"));
            var registry = await TargetRegistry.LoadAsync(Path.Combine(physical, ".abacus", "targets.json"), CancellationToken.None);
            using (pool.AcquireLease())
            {
                await pool.EnsureSlotsAsync(1, await Git("rev-parse", "HEAD"), CancellationToken.None);
                await pool.RemoveAsync("slot-1", registry, new Beads(runner, bd), CancellationToken.None);
            }
            try
            {
                var first = await Abacus(["run", "--once", .. runOptions, "--agent-name", "Alice"]);
                Assert.True(first.Succeeded, first.StandardError);
                Assert.Contains("closed 1", first.StandardError);
                var slot = Assert.Single(pool.ReadManifest()!.Slots);
                Assert.Equal("warm-cache", await File.ReadAllTextAsync(Path.Combine(slot.Path, "target", "cache")));
                Assert.Equal("Alice", await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-actor")));
                // Simulate an interrupted issue in the same checkout, then rename the worker.
                await File.WriteAllTextAsync(Path.Combine(root.FullName, "status"), "in_progress");
                await File.AppendAllTextAsync(Path.Combine(slot.Path, ".gitignore"), "# interrupted work\n");
                // A dead controller is not proof that its previous execution stopped.
                var journalPath = Path.Combine(physical, ".git", "abacus", $"assignment-{slot.Name}.json");
                await File.WriteAllTextAsync(journalPath, System.Text.Json.JsonSerializer.Serialize(
                    pool.ReadAssignment(slot.Name)! with { Phase = "execution-uncertain" }));
                var unsafeRestart = await Abacus(["run", "--once", .. runOptions, "--agent-name", "Bob"]);
                Assert.False(unsafeRestart.Succeeded);
                Assert.Contains("previous execution may still be alive", unsafeRestart.StandardError);
                Assert.Equal("Alice", await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-actor")));
                var confirmStopped = await Abacus("worktrees", "recover", slot.Name, "--confirm", "--repo", physical);
                Assert.True(confirmStopped.Succeeded, confirmStopped.StandardError);
                var second = await Abacus(["run", "--once", .. runOptions, "--agent-name", "Bob"]);
                Assert.True(second.Succeeded, second.StandardError);
                Assert.Equal("Bob", await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-actor")));
                Assert.Contains("# interrupted work", await File.ReadAllTextAsync(Path.Combine(slot.Path, ".gitignore")));
                // Operator finishes the fake harness's deliberately uncommitted work without losing it.
                var commit = await runner.RunAsync(new CommandSpec("git", ["-C", slot.Path, "commit", "-am", "recovered work"], slot.Path));
                Assert.True(commit.Succeeded, commit.StandardError);
                await Git("merge", "--ff-only", "abacus/abc-1");
                Assert.Equal(slot.Path, Assert.Single(pool.ReadManifest()!.Slots).Path);
                Assert.Equal("warm-cache", await File.ReadAllTextAsync(Path.Combine(slot.Path, "target", "cache")));
                var list = await Abacus("worktrees", "list", "--repo", physical);
                Assert.True(list.Succeeded, list.StandardError);
                Assert.Contains(slot.Path, list.StandardOutput);
                var reclaim = await Abacus("worktrees", "reclaim", "slot-1", "--repo", physical);
                Assert.True(reclaim.Succeeded, reclaim.StandardError);
                Assert.True(File.Exists(Path.Combine(slot.Path, "target", "cache")));
                var remove = await Abacus("worktrees", "remove", "slot-1", "--confirm", "--repo", physical);
                Assert.True(remove.Succeeded, remove.StandardError);
                Assert.False(Directory.Exists(slot.Path));

                // Multiple managed workers require the shared database and allocate distinct slots.
                await File.WriteAllTextAsync(bd, (await File.ReadAllTextAsync(bd)).Replace(
                    "\"embedded\":true", "\"embedded\":false,\"connection_ok\":true,\"host\":\"127.0.0.1\",\"port\":3307"));
                var multiOptions = runOptions.ToArray();
                multiOptions[^1] = "2";
                var multiple = await Abacus(["run", "--drain", .. multiOptions]);
                Assert.True(multiple.Succeeded, multiple.StandardError);
                Assert.Equal(2, pool.ReadManifest()!.Slots.Count);
                Assert.Equal(2, pool.ReadManifest()!.Slots.Select(s => s.Path).Distinct().Count());
                var reduced = await Abacus(["run", "--drain", .. runOptions]);
                Assert.True(reduced.Succeeded, reduced.StandardError);
                Assert.Equal(2, pool.ReadManifest()!.Slots.Count);

            }
            finally
            {
                var data = Path.Combine(Path.GetDirectoryName(physical)!, root.Name + "-data");
                if (Directory.Exists(data)) Directory.Delete(data, true);
            }
        }
        finally { root.Delete(true); }
    }
}
