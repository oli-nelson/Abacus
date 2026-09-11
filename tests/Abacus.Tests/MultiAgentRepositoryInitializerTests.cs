using Abacus;

namespace Abacus.Tests;

public sealed class MultiAgentRepositoryInitializerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesSharedBeadsRepositoryWorktreesSkillsAndConfigsWithoutLaunchers(bool beadsCreatesGitignore)
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
                $"#!/bin/sh\nprintf '%s | %s\\n' \"$PWD\" \"$*\" >> '{beadsLog}'\n" +
                (beadsCreatesGitignore ? """
                if [ "$1" = init ]; then
                    printf '.dolt/\n*.db\n' > .gitignore
                    printf '.beads/\n' >> .git/info/exclude
                    mkdir -p .beads .dolt
                    printf '*\n' > .beads/.gitignore
                    touch .beads/metadata.json .dolt/runtime test.db
                fi
                """ : ""));
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
            Assert.Equal(beadsCreatesGitignore, File.Exists(Path.Combine(result.RepositoryPath, ".gitignore")));
            if (beadsCreatesGitignore)
            {
                Assert.Equal(".dolt/\n*.db\n", await File.ReadAllTextAsync(Path.Combine(result.RepositoryPath, ".gitignore")));
                Assert.Equal(".gitignore", await RunGitAsync(result.RepositoryPath, "ls-files", ".gitignore"));
                Assert.Equal(string.Empty, await RunGitAsync(result.RepositoryPath, "ls-files", ".beads", ".dolt", "test.db"));
            }
            var targetConfig = await TargetRegistry.LoadAsync(
                Path.Combine(result.RepositoryPath, ".abacus", "targets.json"), CancellationToken.None);
            var reasoningConfig = await ReasoningPolicy.LoadAsync(
                Path.Combine(result.RepositoryPath, ".abacus", "reasoning.json"), CancellationToken.None);
            Assert.DoesNotContain("repositoryId", await File.ReadAllTextAsync(Path.Combine(result.RepositoryPath, ".abacus", "targets.json")));
            Assert.Single(targetConfig.Targets);
            Assert.False(targetConfig.EnforceTargetBranch);
            Assert.Equal("main", targetConfig.DefaultTarget);
            Assert.Contains("main", targetConfig.Targets.Keys);
            Assert.False(reasoningConfig.EnforceLabels);
            Assert.Equal(".abacus/targets.json", await RunGitAsync(result.RepositoryPath, "ls-files", ".abacus/targets.json"));
            Assert.Equal(".abacus/reasoning.json", await RunGitAsync(result.RepositoryPath, "ls-files", ".abacus/reasoning.json"));

            for (var index = 0; index < 3; index++)
            {
                var worktree = Path.Combine(result.WorktreesPath, index.ToString());
                Assert.True(Directory.Exists(worktree));
                Assert.True(File.Exists(Path.Combine(worktree, ".git")));
                Assert.Equal(beadsCreatesGitignore, File.Exists(Path.Combine(worktree, ".gitignore")));
                Assert.Equal(string.Empty, await RunGitAsync(worktree, "status", "--porcelain"));
                Assert.True(File.Exists(Path.Combine(worktree, ".abacus", "targets.json")));
                Assert.True(File.Exists(Path.Combine(worktree, ".abacus", "reasoning.json")));
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

            var basePath = Path.Combine(result.ProjectRoot, "abacus_base.json");
            var baseConfig = RunConfiguration.Load(basePath).Document;
            Assert.Equal(4, Directory.GetFiles(result.ProjectRoot, "abacus_*.json").Length);
            Assert.Equal(new[] { "agents", "effort", "notify", "notifySound", "repo", "startPaused", "version" }, baseConfig.Select(p => p.Key).Order());
            Assert.Equal("repo", baseConfig["repo"]!.GetValue<string>());
            Assert.Equal("high", baseConfig["effort"]!.GetValue<string>());
            Assert.True(baseConfig["startPaused"]!.GetValue<bool>());
            Assert.Equal("all", baseConfig["notify"]!.GetValue<string>());
            Assert.True(baseConfig["notifySound"]!.GetValue<bool>());
            Assert.Equal(3, baseConfig["agents"]!.AsArray().Count);
            foreach (var mode in new[] { "opencode", "codex", "claude" })
            {
                var harness = RunConfiguration.Load(Path.Combine(result.ProjectRoot, $"abacus_{mode}.json")).Document;
                Assert.Equal(new[] { "baseConfig", "mode", "model", "version" }, harness.Select(p => p.Key).Order());
                Assert.Equal(mode, harness["mode"]!.GetValue<string>());
                Assert.Equal("abacus_base.json", harness["baseConfig"]!.GetValue<string>());
            }

            Assert.Equal(4, result.ConfigurationPaths.Count);
            Assert.Equal(basePath, result.ConfigurationPaths[0]);
            Assert.All(result.ConfigurationPaths, path => Assert.Equal(result.ProjectRoot, Path.GetDirectoryName(path)));
            Assert.Empty(Directory.GetFiles(result.ProjectRoot, "*.sh"));
            Assert.Equal(result.ConfigurationPaths.Order(), RunConfigurationSelection.Discover(result.ProjectRoot));

            // Interactive `abacus run` can select each generated harness config.
            foreach (var (mode, model, expectedMode) in new[]
            {
                ("opencode", "openai/gpt-5.6-sol", AgentMode.OpenCode),
                ("codex", "gpt-5.6-sol", AgentMode.Codex),
                ("claude", "opus", AgentMode.Claude),
            })
            {
                var configPath = Path.Combine(result.ProjectRoot, $"abacus_{mode}.json");
                var choices = RunConfigurationSelection.Discover(result.ProjectRoot).ToList();
                var output = new StringWriter();
                var selected = Options.Parse(["run"], missing => RunConfigurationSelection.Select(
                    result.ProjectRoot, missing, new StringReader($"{choices.IndexOf(configPath) + 1}\n"), output)).Value!;
                Assert.Equal(expectedMode, selected.AgentMode);
                Assert.Equal(model, selected.Model);
                Assert.True(selected.StartPaused);
                Assert.Equal(NotificationMode.All, selected.NotificationMode);
                Assert.True(selected.NotificationSound);
                Assert.Equal(result.RepositoryPath, selected.RepositoryPath);
                Assert.Equal(3, selected.Agents.Count);
                for (var index = 0; index < 3; index++)
                    Assert.Equal(Path.Combine(result.WorktreesPath, index.ToString()), selected.Agents[index].WorkspacePath);
                Assert.Contains($"abacus_{mode}.json", output.ToString());
            }

            // A shared edit reaches all harnesses; explicit config paths work outside the project.
            var editedBase = RunConfiguration.Load(basePath);
            editedBase.Document["latestComments"] = 12;
            editedBase.Save(basePath, true);
            foreach (var configPath in result.ConfigurationPaths.Skip(1))
            {
                var parsed = Options.Parse(["run", "--config", configPath,
                    "--model", "provider/override", "--effort", "xhigh", "--notify", "off",
                    "--notify-sound=false", "--start-paused=false"]).Value!;
                Assert.Equal(result.RepositoryPath, parsed.RepositoryPath);
                Assert.Equal("provider/override", parsed.Model);
                Assert.Equal("xhigh", parsed.Effort);
                Assert.Equal(12, parsed.LatestCommentCount);
                Assert.Equal(NotificationMode.Off, parsed.NotificationMode);
                Assert.False(parsed.NotificationSound);
                Assert.False(parsed.StartPaused);
                Assert.Equal(3, parsed.Agents.Count);
                Assert.Null(parsed.TmuxSession);
                Assert.True(parsed.UsesTmux);
                Assert.Equal("tiled", parsed.EffectiveTmuxLayout);
            }
            Assert.Equal(string.Empty, await RunGitAsync(result.RepositoryPath, "status", "--porcelain"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NewCommandReportsConfigsAndDirectRunInstructions()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-new-output-");
        try
        {
            var fakeBeads = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(fakeBeads, "#!/bin/sh\nexit 0\n");
            MakeExecutable(fakeBeads);
            var result = await new CommandRunner(TextWriter.Null).RunAsync(new CommandSpec(
                "dotnet", [typeof(Program).Assembly.Location, "new", "project", "--agents", "1"], root.FullName,
                new Dictionary<string, string?> { ["PATH"] = root.FullName + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }));
            Assert.True(result.Succeeded, result.StandardError);
            Assert.Contains("Configs:", result.StandardOutput);
            Assert.Contains("abacus_base.json", result.StandardOutput);
            Assert.Contains("abacus_codex.json", result.StandardOutput);
            Assert.Contains("execute abacus run", result.StandardOutput);
            Assert.Contains("For non-interactive use, pass --config", result.StandardOutput);
            Assert.DoesNotContain("Launchers:", result.StandardOutput);
            Assert.DoesNotContain(".sh", result.StandardOutput);
            Assert.DoesNotContain("\u001b", result.StandardOutput, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.Combine(root.FullName, "project"), "*.sh"));
        }
        finally { root.Delete(true); }
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
