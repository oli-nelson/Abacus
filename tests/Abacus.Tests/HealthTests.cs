using Abacus;

namespace Abacus.Tests;

public sealed class HealthTests
{
    [Fact]
    public async Task EmbeddedBeadsReportsSingleAgentModesMissingSkillsAndNoAdditionalWorktrees()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = await HealthEnvironment.CreateAsync(
            embedded: true,
            worktreeCount: 1,
            tools: new Dictionary<string, string>
            {
                ["opencode"] = "1.18.20",
                ["claude"] = "2.1.100 (Claude Code)",
                ["tmux"] = "tmux 3.5",
            });

        var report = await environment.CheckAsync();
        var rendered = report.Render();

        Assert.True(report.SingleAgentReady);
        Assert.False(report.MultiAgentReady);
        Assert.False(report.AreSkillsInstalled);
        Assert.False(report.IsHealthy);
        Assert.True(report.DoltIdentity!.Embedded);
        Assert.Equal(ToolHealthStatus.Ready, report.OpenCode.Status);
        Assert.Equal(ToolHealthStatus.Outdated, report.Claude.Status);
        Assert.Equal(ToolHealthStatus.Missing, report.Codex.Status);
        Assert.Equal(ToolHealthStatus.Outdated, report.Tmux.Status);
        Assert.Equal(
            ["opencode-server (direct; an existing server is still required and is not checked)"],
            report.AvailableModes);
        Assert.Contains("single-agent only", rendered, StringComparison.Ordinal);
        Assert.Contains("No additional linked worktrees", rendered, StringComparison.Ordinal);
        Assert.Contains("Separate clones", rendered, StringComparison.Ordinal);
        Assert.Contains("abacus --install-skills", rendered, StringComparison.Ordinal);
        Assert.Equal(MergeSlotHealthStatus.Missing, report.MergeSlot.Status);
        Assert.Contains("may attempt merges concurrently", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedBeadsWithWorktreesAndInstalledSkillsReportsMultiAgentReady()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = await HealthEnvironment.CreateAsync(
            embedded: false,
            worktreeCount: 2,
            tools: new Dictionary<string, string>
            {
                ["codex"] = "codex-cli 0.152.1",
                ["tmux"] = "tmux 3.6a",
            },
            mergeSlotExists: true,
            mergeSlotHolder: "merge-agent");
        await environment.InstallSkillFilesAsync();

        var report = await environment.CheckAsync();
        var rendered = report.Render();

        Assert.True(report.SingleAgentReady);
        Assert.True(report.MultiAgentReady);
        Assert.True(report.AreSkillsInstalled);
        Assert.True(report.IsHealthy);
        Assert.True(report.DoltIdentity!.IsShared);
        Assert.Equal(2, report.Worktrees.Count);
        Assert.Contains("codex (tmux-hosted)", report.AvailableModes);
        Assert.Equal(MergeSlotHealthStatus.Held, report.MergeSlot.Status);
        Assert.Equal("merge-agent", report.MergeSlot.Holder);
        Assert.Contains("held by merge-agent", rendered, StringComparison.Ordinal);
        Assert.Contains("Bundled skills readiness: READY", rendered, StringComparison.Ordinal);
        Assert.Contains("Multi-agent readiness from linked worktrees: READY", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninitializedBeadsAndNoHarnessReportNotReady()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = await HealthEnvironment.CreateAsync(
            embedded: null,
            worktreeCount: 0,
            tools: new Dictionary<string, string>
            {
                ["tmux"] = "tmux 3.6a",
            });

        var report = await environment.CheckAsync();

        Assert.Null(report.DoltIdentity);
        Assert.Equal(MergeSlotHealthStatus.NotChecked, report.MergeSlot.Status);
        Assert.False(report.SingleAgentReady);
        Assert.False(report.IsHealthy);
        Assert.Empty(report.AvailableModes);
        Assert.Contains("Not initialized or unavailable", report.Render(), StringComparison.Ordinal);
        Assert.Contains("No supported agent harness", report.Render(), StringComparison.Ordinal);
        Assert.Contains("Git reported no referenced worktrees", report.Render(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("3.6a", "3.6", 1)]
    [InlineData("3.6a", "3.6a", 0)]
    [InlineData("3.6b", "3.6a", 1)]
    [InlineData("0.152.1", "0.151.0", 1)]
    [InlineData("1.18.19", "1.18.20", -1)]
    public void ComparesSupportedVersionFormats(string detected, string minimum, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(HealthChecker.CompareVersions(detected, minimum)));
    }

    [Fact]
    public void ParsesPorcelainWorktreesIncludingDetachedEntries()
    {
        var worktrees = HealthChecker.ParseWorktrees("""
            worktree /repo
            HEAD abc
            branch refs/heads/main

            worktree /repo-wt
            HEAD def
            detached

            """);

        Assert.Equal(2, worktrees.Count);
        Assert.Equal("main", worktrees[0].Branch);
        Assert.Null(worktrees[1].Branch);
    }

    private sealed class HealthEnvironment : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string bin;

        private HealthEnvironment(DirectoryInfo root, string bin, string nested)
        {
            this.root = root;
            this.bin = bin;
            Nested = nested;
        }

        private string Nested { get; }

        public static async Task<HealthEnvironment> CreateAsync(
            bool? embedded,
            int worktreeCount,
            IReadOnlyDictionary<string, string> tools,
            bool mergeSlotExists = false,
            string? mergeSlotHolder = null)
        {
            var root = Directory.CreateTempSubdirectory("abacus-health-");
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "repo", "src")).FullName;
            var repository = Directory.GetParent(nested)!.FullName;
            var worktrees = new List<string>();
            if (worktreeCount > 0)
            {
                worktrees.Add($"worktree {repository}\nHEAD abc\nbranch refs/heads/main\n");
            }
            if (worktreeCount > 1)
            {
                worktrees.Add($"worktree {root.FullName}/repo-wt\nHEAD def\nbranch refs/heads/worker\n");
            }

            await WriteExecutableAsync(bin, "git", $$"""
                if [ "$1" = "--version" ]; then
                  printf 'git version 2.55.0\n'
                elif [ "$3" = "rev-parse" ]; then
                  printf '%s\n' '{{repository}}'
                elif [ "$3" = "worktree" ]; then
                  printf '%s\n' '{{string.Join("\n", worktrees)}}'
                else
                  exit 2
                fi
                """);

            await WriteExecutableAsync(bin, "bd", embedded switch
            {
                null => """
                    if [ "$1" = "version" ]; then
                      printf 'bd version 1.2.2\n'
                    elif [ "$1" = "where" ]; then
                      printf 'no active beads workspace found\n' >&2
                      exit 1
                    else
                      exit 2
                    fi
                    """,
                true => $$"""
                    if [ "$1" = "version" ]; then
                      printf 'bd version 1.2.2\n'
                    elif [ "$1" = "where" ]; then
                      printf '{"database":"abacus"}\n'
                    elif [ "$1 $2" = "dolt show" ]; then
                      printf '{"embedded":true,"database":"abacus"}\n'
                    elif [ "$1 $2" = "merge-slot check" ]; then
                      printf '%s\n' '{{MergeSlotJson(mergeSlotExists, mergeSlotHolder)}}'
                    else
                      exit 2
                    fi
                    """,
                false => $$"""
                    if [ "$1" = "version" ]; then
                      printf 'bd version 1.2.2\n'
                    elif [ "$1" = "where" ]; then
                      printf '{"database":"abacus"}\n'
                    elif [ "$1 $2" = "dolt show" ]; then
                      printf '{"embedded":false,"database":"abacus","host":"127.0.0.1","port":3307,"connection_ok":true}\n'
                    elif [ "$1 $2" = "merge-slot check" ]; then
                      printf '%s\n' '{{MergeSlotJson(mergeSlotExists, mergeSlotHolder)}}'
                    else
                      exit 2
                    fi
                    """,
            });

            foreach (var (name, version) in tools)
            {
                await WriteExecutableAsync(bin, name, $"printf '%s\\n' '{version}'\n");
            }

            return new HealthEnvironment(root, bin, nested);
        }

        private static string MergeSlotJson(bool exists, string? holder)
        {
            if (!exists)
            {
                return "{\"available\":false,\"error\":\"not found\",\"id\":\"ab-merge-slot\"}";
            }

            return holder is null
                ? "{\"available\":true,\"holder\":null,\"id\":\"ab-merge-slot\",\"waiters\":null}"
                : $"{{\"available\":false,\"holder\":\"{holder}\",\"id\":\"ab-merge-slot\",\"waiters\":null}}";
        }

        public Task<HealthReport> CheckAsync() =>
            new HealthChecker(new CommandRunner(TextWriter.Null), bin)
                .RunAsync(Nested, CancellationToken.None);

        public async Task InstallSkillFilesAsync()
        {
            var repository = Directory.GetParent(Nested)!.FullName;
            foreach (var name in SkillInstaller.InstallableSkillNames)
            {
                var skill = Directory.CreateDirectory(Path.Combine(repository, ".agents", "skills", name));
                Directory.CreateDirectory(Path.Combine(skill.FullName, "agents"));
                await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), $"name: {name}\n");
                await File.WriteAllTextAsync(Path.Combine(skill.FullName, "agents", "openai.yaml"), "interface: {}\n");
            }
        }

        public void Dispose() => root.Delete(recursive: true);

        private static async Task WriteExecutableAsync(string directory, string name, string body)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
