using Abacus;

namespace Abacus.Tests;

public sealed class MultiAgentRepositoryInitializerTests
{
    [Fact]
    public async Task CreatesSharedBeadsRepositoryWorktreesSkillsAndLaunchers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-init-repo-");
        try
        {
            var beadsLog = Path.Combine(root.FullName, "beads.log");
            var fakeBeads = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(
                fakeBeads,
                $"#!/bin/sh\nprintf '%s | %s\\n' \"$PWD\" \"$*\" >> '{beadsLog}'\n");
            MakeExecutable(fakeBeads);

            var initializer = new MultiAgentRepositoryInitializer(
                new CommandRunner(TextWriter.Null),
                beadsExecutable: fakeBeads);
            var result = await initializer.InitializeAsync(
                root.FullName,
                new NewMultiAgentRepositoryOptions("sample-project", 3),
                CancellationToken.None);

            Assert.Equal(Path.Combine(root.FullName, "sample-project"), result.ProjectRoot);
            Assert.Equal(3, result.AgentCount);
            Assert.StartsWith("abacus_sample_project_", result.BeadsDatabase, StringComparison.Ordinal);
            Assert.True(result.BeadsDatabase.Length <= 64);
            Assert.Equal("main", await RunGitAsync(result.RepositoryPath, "branch", "--show-current"));
            Assert.Equal("# sample-project\n", await File.ReadAllTextAsync(Path.Combine(result.RepositoryPath, "README.md")));
            Assert.False(File.Exists(Path.Combine(result.RepositoryPath, ".gitignore")));

            for (var index = 0; index < 3; index++)
            {
                var worktree = Path.Combine(result.WorktreesPath, index.ToString());
                Assert.True(Directory.Exists(worktree));
                Assert.True(File.Exists(Path.Combine(worktree, ".git")));
                Assert.True(File.Exists(Path.Combine(
                    worktree,
                    ".agents",
                    "skills",
                    "abacus-beads-planner",
                    "SKILL.md")));
            }

            var beadsCalls = await File.ReadAllTextAsync(beadsLog);
            Assert.Contains("init --shared-server --setup-exclude --prefix sample-project --database", beadsCalls, StringComparison.Ordinal);
            Assert.Contains("--skip-agents --non-interactive --role maintainer --quiet", beadsCalls, StringComparison.Ordinal);
            Assert.Contains("config set no-git-ops false", beadsCalls, StringComparison.Ordinal);
            Assert.Contains("config set dolt.local-only true", beadsCalls, StringComparison.Ordinal);
            Assert.Contains("merge-slot create", beadsCalls, StringComparison.Ordinal);
            Assert.All(
                await File.ReadAllLinesAsync(beadsLog),
                call =>
                {
                    Assert.Contains(
                        $"{Path.DirectorySeparatorChar}sample-project{Path.DirectorySeparatorChar}repo | ",
                        call,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain(" | -C ", call, StringComparison.Ordinal);
                });

            Assert.Equal(3, result.LauncherPaths.Count);
            foreach (var launcher in result.LauncherPaths)
            {
                Assert.Equal(result.ProjectRoot, Path.GetDirectoryName(launcher));
                Assert.True(File.Exists(launcher));
                Assert.True((File.GetUnixFileMode(launcher) & UnixFileMode.UserExecute) != 0);
            }

            var launcherText = await File.ReadAllTextAsync(
                Path.Combine(result.ProjectRoot, "run_abacus_codex.sh"));
            Assert.Contains("--mode codex", launcherText, StringComparison.Ordinal);
            Assert.Contains("worktrees=\"$root/worktrees\"", launcherText, StringComparison.Ordinal);
            Assert.Contains("for workspace in \"$worktrees\"/*", launcherText, StringComparison.Ordinal);

            var abacusLog = Path.Combine(root.FullName, "abacus.log");
            var fakeAbacus = Path.Combine(root.FullName, "abacus");
            await File.WriteAllTextAsync(
                fakeAbacus,
                $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{abacusLog}'\n");
            MakeExecutable(fakeAbacus);
            var launch = await new CommandRunner(TextWriter.Null).RunAsync(
                new CommandSpec(
                    Path.Combine(result.ProjectRoot, "run_abacus_codex.sh"),
                    [],
                    result.ProjectRoot,
                    new Dictionary<string, string?>
                    {
                        ["ABACUS_BIN"] = fakeAbacus,
                        ["ABACUS_TMUX_SESSION"] = "test-session",
                    }));

            Assert.True(launch.Succeeded, launch.StandardError);
            var launchedArguments = await File.ReadAllLinesAsync(abacusLog);
            Assert.Equal(3, launchedArguments.Count(static argument => argument == "-a"));
            Assert.Contains("codex", launchedArguments);
            Assert.Contains("test-session", launchedArguments);
            for (var index = 0; index < 3; index++)
            {
                Assert.Contains(Path.Combine(result.WorktreesPath, index.ToString()), launchedArguments);
            }

            Assert.Equal(string.Empty, await RunGitAsync(result.RepositoryPath, "status", "--porcelain"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RefusesToReplaceAnExistingDestination()
    {
        var root = Directory.CreateTempSubdirectory("abacus-init-existing-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "existing"));
            var initializer = new MultiAgentRepositoryInitializer(new CommandRunner(TextWriter.Null));

            var exception = await Assert.ThrowsAsync<RepositoryInitializationException>(() =>
                initializer.InitializeAsync(
                    root.FullName,
                    new NewMultiAgentRepositoryOptions("existing", 2),
                    CancellationToken.None));

            Assert.Contains("destination already exists", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("My Project", "my-project")]
    [InlineData("___", "abacus")]
    [InlineData("ONE--Two", "one-two")]
    public void CreatesSafeBeadsIdentifiers(string projectName, string expected)
    {
        Assert.Equal(expected, MultiAgentRepositoryInitializer.CreateIdentifier(projectName));
    }

    private static async Task<string> RunGitAsync(string repository, params string[] arguments)
    {
        var result = await new CommandRunner(TextWriter.Null).RunAsync(
            new CommandSpec("git", ["-C", repository, .. arguments], repository));
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
