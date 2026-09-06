using Abacus;

namespace Abacus.Tests;

public sealed class RepositorySelectionTests
{
    [Theory]
    [InlineData("init")]
    [InlineData("skills", "install")]
    [InlineData("health")]
    [InlineData("targets", "check")]
    [InlineData("attention", "list")]
    [InlineData("branches", "prune")]
    public void RepositoryOverrideWorksWithStandaloneCommands(params string[] command)
    {
        var expected = Path.GetFullPath("repo with spaces");
        foreach (var args in new[] { command.Concat(["--repo", "repo with spaces"]).ToArray(), new[] { "--repo", "repo with spaces" }.Concat(command).ToArray() })
        {
            var parsed = Options.Parse(args);
            Assert.Equal(expected, parsed.RepositoryPath);
            if (parsed.TargetCommand is { } target) Assert.Equal(expected, target.RepositoryPath);
        }
    }

    [Fact]
    public void RepositoryOverrideWorksWithRunAndRepairCommandsAndRejectsLegacyConfig()
    {
        var run = Options.Parse(["run", "--repo", "/tmp/repo", "--model", "p/m", "--tmux-session", "a", "-a", "a", "/tmp/wt"]);
        Assert.Equal("/tmp/repo", run.Value!.RepositoryPath);
        Assert.Equal("/tmp/repo", Options.Parse(["attention", "resolve", "abc-1", "--repo", "/tmp/repo"]).RepositoryPath);
        Assert.Equal("/tmp/repo", Options.Parse(["targets", "set", "main", "abc-1", "--repo", "/tmp/repo"]).TargetCommand!.RepositoryPath);
        Assert.Throws<OptionsException>(() => Options.Parse(["init", "--repo"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["init", "--repo", ""]));
        Assert.Throws<OptionsException>(() => Options.Parse(["init", "--repo", "/tmp/a", "--repo", "/tmp/b"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["models", "--repo", "/tmp/a"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["new", "sample", "--agents", "2", "--repo", "/tmp/a"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["health", "--config", "/tmp/targets.json"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["targets", "check", "--config", "/tmp/targets.json"]));
    }

    [Fact]
    public async Task MainCheckoutSelectionRejectsLinkedBareMissingAndOutsideWithoutOverride()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var main = await f.Git.ResolveMainRepositoryAsync(f.Repo, null, CancellationToken.None);
        var nested = Directory.CreateDirectory(Path.Combine(f.Repo, "src", "nested")).FullName;
        Assert.Equal(main, await f.Git.ResolveMainRepositoryAsync(nested, null, CancellationToken.None));
        Assert.Equal(main, await f.Git.ResolveMainRepositoryAsync(f.Root, f.Repo, CancellationToken.None));
        Assert.Equal(main, await f.Git.ResolveMainRepositoryAsync(f.Worktree, f.Repo, CancellationToken.None));
        var outside = await Assert.ThrowsAsync<PreflightException>(() => f.Git.ResolveMainRepositoryAsync(f.Root, null, CancellationToken.None));
        Assert.Contains("--repo", outside.Message);
        foreach (var selected in new[] { f.Worktree, Path.Combine(f.Root, "missing"), f.Bare })
            await Assert.ThrowsAsync<PreflightException>(() => f.Git.ResolveMainRepositoryAsync(f.Root, selected, CancellationToken.None));
        var linked = await Assert.ThrowsAsync<PreflightException>(() => f.Git.ResolveMainRepositoryAsync(f.Worktree, null, CancellationToken.None));
        Assert.Contains("linked Git worktree", linked.Message);
        // Main checkout means primary checkout, not a checkout of the branch named main.
        await f.RunGitAsync(f.Repo, "switch", "-c", "feature");
        Assert.Equal(main, await f.Git.ResolveMainRepositoryAsync(f.Repo, null, CancellationToken.None));
    }

    [Fact]
    public async Task AuditUsesSelectedRepositoryForBothConfigAndBeadsNotCallingWorktree()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(f.Repo, ".abacus"));
        await File.WriteAllTextAsync(Path.Combine(f.Repo, ".abacus", "targets.json"), TargetRegistry.DefaultConfiguration);
        Directory.CreateDirectory(Path.Combine(f.Worktree, ".abacus"));
        await File.WriteAllTextAsync(Path.Combine(f.Worktree, ".abacus", "targets.json"), "invalid worktree config");
        var bd = Path.Combine(f.Root, "bd");
        var log = Path.Combine(f.Root, "bd-cwd");
        await File.WriteAllTextAsync(bd, $"#!/bin/sh\npwd -P > '{log}'\nprintf '[{{\"id\":\"abc-1\",\"status\":\"open\"}}]\\n'\n");
        File.SetUnixFileMode(bd, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var commands = new TicketTargets(new Beads(new CommandRunner(TextWriter.Null), bd), f.Git);
        var output = new StringWriter();
        Assert.Equal(0, await commands.RunAsync(f.Worktree, new(true, null, [], f.Repo), output, CancellationToken.None));
        Assert.Contains("main (default)", output.ToString());
        Assert.Equal(await f.RunGitAsync(f.Repo, "rev-parse", "--show-toplevel"), (await File.ReadAllTextAsync(log)).Trim());
        await Assert.ThrowsAsync<PreflightException>(() => commands.RunAsync(f.Root, new(true, null, [], null), output, CancellationToken.None));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("abacus-repository-selection-").FullName;
        public string Repo => Path.Combine(Root, "repo");
        public string Worktree => Path.Combine(Root, "worktree");
        public string Bare => Path.Combine(Root, "bare.git");
        public Git Git { get; } = new(new CommandRunner(TextWriter.Null));

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Repo);
            await f.RunGitAsync(f.Repo, "init", "-q", "-b", "main");
            await f.RunGitAsync(f.Repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-qm", "initial");
            await f.RunGitAsync(f.Repo, "worktree", "add", "--detach", f.Worktree, "main");
            await f.RunGitAsync(f.Root, "init", "--bare", f.Bare);
            return f;
        }

        public async Task<string> RunGitAsync(string directory, params string[] args)
        {
            var result = await new CommandRunner(TextWriter.Null).RunAsync(new("git", args, directory));
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
