using System.Text.Json;
using Abacus;

namespace Abacus.Tests;

public sealed class RepositoryInitializerTests
{
    [Fact]
    public void InitIsStandalone()
    {
        var parsed = Options.Parse(["init"]);
        Assert.True(parsed.InitializeRepository);
        Assert.Null(parsed.Value);
        Assert.False(parsed.InstallSkills);
        foreach (var option in new[] { "init", "health", "skills", "install", "--once", "--config", "targets", "check" })
            Assert.Throws<OptionsException>(() => Options.Parse(["init", option]));
    }

    [Fact]
    public async Task OutsideGitFailsBeforeBeadsOrFileWrites()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var exception = await Assert.ThrowsAsync<PreflightException>(() => f.InitAsync(f.Root));
        Assert.Contains("--repo <main-checkout>", exception.Message);
        Assert.False(File.Exists(f.Log));
        Assert.False(Directory.Exists(Path.Combine(f.Root, ".agents")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, ".abacus")));
    }

    [Fact]
    public async Task CreatesDefaultsAndSkillsFromSubdirectoryWithoutChangingGitOrBeads()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var before = await f.GitAsync(f.Repo, "rev-parse", "HEAD");
        var nested = Directory.CreateDirectory(Path.Combine(f.Repo, "src", "nested")).FullName;
        var result = await f.InitAsync(nested);
        Assert.True(result.CreatedTargets);
        Assert.False(result.Skills.Cancelled);
        Assert.Equal(TargetRegistry.DefaultConfiguration, await File.ReadAllTextAsync(f.Config));
        Assert.Equal(4, result.Skills.InstalledSkills.Count);
        var registry = await TargetRegistry.LoadAsync(f.Config, CancellationToken.None);
        Assert.False(registry.EnforceTargetBranch);
        Assert.Equal("main", registry.DefaultTarget);
        Assert.All(result.Skills.InstalledSkills, skill =>
            Assert.True(File.Exists(Path.Combine(f.Repo, ".agents", "skills", skill, "SKILL.md"))));
        Assert.Equal(before, await f.GitAsync(f.Repo, "rev-parse", "HEAD"));
        Assert.Equal("main", await f.GitAsync(f.Repo, "branch", "--show-current"));
        Assert.Equal("", await f.GitAsync(f.Repo, "diff", "--cached", "--name-only"));
        Assert.Equal(["where --json", "list --limit 1 --json"], await File.ReadAllLinesAsync(f.Log));
    }

    [Fact]
    public async Task PreservesCustomConfigAndPolicyExactlyOnRepeatedInit()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        await f.GitAsync(f.Repo, "branch", "release/1.2");
        Directory.CreateDirectory(Path.GetDirectoryName(f.Config)!);
        var custom = """{ "version": 1, "repositoryId": "legacy", "defaultTarget": "release/1.2", "targets": { "release/1.2": {"mergeInstructions":"release.md"} } }""";
        await File.WriteAllTextAsync(f.Config, custom);
        var instructions = Path.Combine(Path.GetDirectoryName(f.Config)!, "release.md");
        await File.WriteAllTextAsync(instructions, "keep this policy\n");
        Assert.False((await f.InitAsync()).CreatedTargets);
        var confirmations = 0;
        var second = await f.InitAsync(confirm: skills => { confirmations++; Assert.Equal(4, skills.Count); return true; });
        Assert.False(second.CreatedTargets);
        Assert.Equal(1, confirmations);
        Assert.Equal(custom, await File.ReadAllTextAsync(f.Config));
        Assert.Equal("keep this policy\n", await File.ReadAllTextAsync(instructions));
    }

    [Fact]
    public async Task DecliningSkillReplacementDoesNotCreateConfigOrReplaceSkills()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var skill = Path.Combine(f.Repo, ".agents", "skills", "abacus-beads-planner", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        await File.WriteAllTextAsync(skill, "custom");
        var result = await f.InitAsync(confirm: _ => false);
        Assert.True(result.Skills.Cancelled);
        Assert.False(File.Exists(f.Config));
        Assert.Equal("custom", await File.ReadAllTextAsync(skill));
    }

    [Theory]
    [InlineData("where-fails")]
    [InlineData("where-malformed")]
    [InlineData("where-missing-path")]
    [InlineData("list-fails")]
    [InlineData("list-malformed")]
    [InlineData("list-object")]
    public async Task InvalidBeadsFailsBeforeAnyInstallation(string failure)
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        switch (failure)
        {
            case "where-fails": await File.WriteAllTextAsync(f.WhereExit, "1"); break;
            case "where-malformed": await File.WriteAllTextAsync(f.WhereJson, "bad json"); break;
            case "where-missing-path": await File.WriteAllTextAsync(f.WhereJson, "{}"); break;
            case "list-fails": await File.WriteAllTextAsync(f.ListExit, "1"); break;
            case "list-malformed": await File.WriteAllTextAsync(f.ListJson, "bad json"); break;
            case "list-object": await File.WriteAllTextAsync(f.ListJson, "{}"); break;
        }
        var exception = await Assert.ThrowsAsync<RepositoryInitializationException>(() => f.InitAsync());
        Assert.Contains("Beads", exception.Message);
        f.AssertNoInstallation();
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("missing-instructions")]
    [InlineData("missing-branch")]
    public async Task InvalidExistingConfigurationIsPreservedBeforeSkillsAreInstalled(string failure)
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Config)!);
        var config = failure switch
        {
            "missing-instructions" => """{"version":1,"targets":{"main":{"mergeInstructions":"missing.md"}}}""",
            "missing-branch" => """{"version":1,"targets":{"release/nope":{}}}""",
            _ => "bad json",
        };
        await File.WriteAllTextAsync(f.Config, config);
        await Assert.ThrowsAnyAsync<Exception>(() => f.InitAsync());
        Assert.Equal(config, await File.ReadAllTextAsync(f.Config));
        Assert.False(Directory.Exists(Path.Combine(f.Repo, ".agents")));
    }

    [Fact]
    public async Task NoMainRequiresExplicitConfigAndNeverGuessesCurrentBranch()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        await f.GitAsync(f.Repo, "branch", "-m", "trunk");
        var exception = await Assert.ThrowsAsync<RepositoryInitializationException>(() => f.InitAsync());
        Assert.Contains("local target branch 'main'", exception.Message);
        f.AssertNoInstallation();
        Directory.CreateDirectory(Path.GetDirectoryName(f.Config)!);
        await File.WriteAllTextAsync(f.Config, """{"version":1,"defaultTarget":"trunk","targets":{"trunk":{}}}""");
        Assert.False((await f.InitAsync()).CreatedTargets);
    }

    [Fact]
    public async Task RejectsAncestorBeadsFromAnotherRepository()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var nested = Directory.CreateDirectory(Path.Combine(f.Repo, "other-repo")).FullName;
        await f.GitAsync(nested, "init", "-b", "main");
        var exception = await Assert.ThrowsAsync<RepositoryInitializationException>(() => f.InitAsync(nested));
        Assert.Contains("different Git repository", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(nested, ".agents")));
        Assert.False(Directory.Exists(Path.Combine(nested, ".abacus")));
    }

    [Fact]
    public async Task RejectsLinkedWorktreeBeforeBeadsOrInstallation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var worktree = Path.Combine(f.Root, "worktree");
        await f.GitAsync(f.Repo, "worktree", "add", "--detach", worktree, "main");
        var exception = await Assert.ThrowsAsync<PreflightException>(() => f.InitAsync(worktree));
        Assert.Contains("linked Git worktree", exception.Message);
        Assert.False(File.Exists(f.Log));
        Assert.False(Directory.Exists(Path.Combine(worktree, ".agents")));
        Assert.False(File.Exists(f.Config));
    }

    [Fact]
    public async Task SupportsExplicitExternalBeadsRedirectFromMainRepository()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        var external = Directory.CreateDirectory(Path.Combine(f.Root, "external-beads")).FullName;
        await File.WriteAllTextAsync(f.WhereJson, JsonSerializer.Serialize(new
            { path = external, redirected_from = Path.Combine(f.Repo, ".beads") }));
        Assert.True((await f.InitAsync()).CreatedTargets);
    }

    [Fact]
    public async Task ConcurrentConfigCreationIsNeverOverwritten()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = await Fixture.CreateAsync();
        await f.InitAsync();
        File.Delete(f.Config);
        await Assert.ThrowsAsync<IOException>(() => f.InitAsync(confirm: _ =>
        {
            File.WriteAllText(f.Config, "concurrent configuration");
            return true;
        }));
        Assert.Equal("concurrent configuration", await File.ReadAllTextAsync(f.Config));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f.Config)!, "*.tmp"));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("abacus-init-").FullName;
        public string Repo => Path.Combine(Root, "repo");
        public string Config => Path.Combine(Repo, ".abacus", "targets.json");
        public string WhereJson => Path.Combine(Root, "where.json");
        public string WhereExit => Path.Combine(Root, "where.exit");
        public string ListJson => Path.Combine(Root, "list.json");
        public string ListExit => Path.Combine(Root, "list.exit");
        public string Log => Path.Combine(Root, "bd.log");
        private string Bd => Path.Combine(Root, "bd");
        private readonly CommandRunner runner = new(TextWriter.Null);

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Repo);
            await f.GitAsync(f.Repo, "init", "-b", "main");
            await f.GitAsync(f.Repo, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "initial");
            var beads = Directory.CreateDirectory(Path.Combine(f.Repo, ".beads")).FullName;
            await File.WriteAllTextAsync(f.WhereJson, JsonSerializer.Serialize(new { path = beads }));
            await File.WriteAllTextAsync(f.WhereExit, "0");
            await File.WriteAllTextAsync(f.ListJson, "[]");
            await File.WriteAllTextAsync(f.ListExit, "0");
            await File.WriteAllTextAsync(f.Bd, $"""
                #!/bin/sh
                printf '%s\n' "$*" >> '{f.Log}'
                case "$*" in
                'where --json') cat '{f.WhereJson}'; exit "$(cat '{f.WhereExit}')" ;;
                'list --limit 1 --json') cat '{f.ListJson}'; exit "$(cat '{f.ListExit}')" ;;
                *) echo 'unexpected Beads mutation' >&2; exit 99 ;;
                esac
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f.Bd, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return f;
        }

        public Task<RepositoryInitializationResult> InitAsync(string? directory = null,
            Func<IReadOnlyList<string>, bool>? confirm = null) =>
            new RepositoryInitializer(runner, beadsExecutable: Bd)
                .InitializeAsync(directory ?? Repo, confirm ?? (_ => true), CancellationToken.None);

        public async Task<string> GitAsync(string directory, params string[] args)
        {
            var result = await runner.RunAsync(new CommandSpec("git", args, directory));
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }

        public void AssertNoInstallation()
        {
            Assert.False(Directory.Exists(Path.Combine(Repo, ".agents")));
            Assert.False(Directory.Exists(Path.Combine(Repo, ".abacus")));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
