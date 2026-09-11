using System.Text;
using System.Text.RegularExpressions;

namespace Abacus;

public sealed record MultiAgentRepositoryInitializationResult(
    string ProjectRoot,
    string RepositoryPath,
    string WorktreesPath,
    int AgentCount,
    string BeadsDatabase,
    IReadOnlyList<string> ConfigurationPaths);

public sealed partial class MultiAgentRepositoryInitializer(
    CommandRunner runner,
    string gitExecutable = "git",
    string beadsExecutable = "bd")
{
    public async Task<MultiAgentRepositoryInitializationResult> InitializeAsync(
        string workingDirectory,
        NewMultiAgentRepositoryOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(options);

        var projectRoot = Path.GetFullPath(Path.Combine(workingDirectory, options.ProjectName));
        if (PathExists(projectRoot))
        {
            throw new RepositoryInitializationException(
                $"destination already exists: {projectRoot}");
        }

        var repositoryPath = Path.Combine(projectRoot, "repo");
        var worktreesPath = Path.Combine(projectRoot, "worktrees");
        var identifier = CreateIdentifier(options.ProjectName);
        var databaseSuffix = Guid.NewGuid().ToString("N")[..8];
        var database = $"abacus_{identifier.Replace('-', '_')}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Environment.ProcessId}_{databaseSuffix}";

        Directory.CreateDirectory(repositoryPath);
        Directory.CreateDirectory(worktreesPath);

        await RunRequiredAsync(
            gitExecutable,
            ["-C", repositoryPath, "init", "--initial-branch=main"],
            projectRoot,
            "initialize the Git repository",
            cancellationToken);

        await RunRequiredAsync(
            beadsExecutable,
            [
                "init",
                "--shared-server",
                "--setup-exclude",
                "--prefix", identifier,
                "--database", database,
                "--skip-agents",
                "--non-interactive",
                "--role", "maintainer",
                "--quiet",
            ],
            repositoryPath,
            "initialize the shared Beads database",
            cancellationToken);
        await RunRequiredAsync(
            beadsExecutable,
            ["config", "set", "no-git-ops", "false"],
            repositoryPath,
            "disable Beads no-git-ops",
            cancellationToken);
        await RunRequiredAsync(
            beadsExecutable,
            ["config", "set", "dolt.local-only", "true"],
            repositoryPath,
            "mark the Beads database as local-only",
            cancellationToken);
        await RunRequiredAsync(
            beadsExecutable,
            ["merge-slot", "create"],
            repositoryPath,
            "create the Beads merge slot",
            cancellationToken);

        var installer = new SkillInstaller(runner, gitExecutable);
        var skillResult = await installer.InstallAsync(
            repositoryPath,
            static _ => false,
            cancellationToken);
        if (skillResult.Cancelled)
        {
            throw new RepositoryInitializationException(
                "bundled skill installation was unexpectedly cancelled");
        }

        await File.WriteAllTextAsync(
            Path.Combine(repositoryPath, "README.md"),
            $"# {options.ProjectName}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        Directory.CreateDirectory(Path.Combine(repositoryPath, ".abacus"));
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, ".abacus", "targets.json"),
            TargetRegistry.DefaultConfiguration, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repositoryPath, ".abacus", "reasoning.json"),
            ReasoningPolicy.DefaultConfiguration, cancellationToken);

        var stageArguments = new List<string> { "-C", repositoryPath, "add", "README.md", ".agents", ".abacus" };
        // Beads may create root ignore rules for Dolt files. Keep them in the
        // initial commit (and worktrees), without staging local Beads state.
        if (File.Exists(Path.Combine(repositoryPath, ".gitignore")))
        {
            stageArguments.Add(".gitignore");
        }

        await RunRequiredAsync(
            gitExecutable,
            stageArguments,
            projectRoot,
            "stage the initialized repository",
            cancellationToken);
        await RunRequiredAsync(
            gitExecutable,
            [
                "-C", repositoryPath,
                "-c", "user.name=Abacus",
                "-c", "user.email=abacus@example.invalid",
                "commit", "--no-gpg-sign", "-m", "Initialize repository for Abacus",
            ],
            projectRoot,
            "create the initial Git commit",
            cancellationToken);

        for (var index = 0; index < options.AgentCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worktree = Path.Combine(worktreesPath, index.ToString());
            await RunRequiredAsync(
                gitExecutable,
                ["-C", repositoryPath, "worktree", "add", "--detach", worktree, "main"],
                projectRoot,
                $"create worktree {index}",
                cancellationToken);
        }

        var baseConfiguration = RunConfiguration.Create(projectRoot);
        baseConfiguration.Document["repo"] = "repo";
        baseConfiguration.Document["effort"] = "high";
        baseConfiguration.Document["startPaused"] = true;
        baseConfiguration.Document["notify"] = "all";
        baseConfiguration.Document["notifySound"] = true;
        baseConfiguration.Document["agents"] = new System.Text.Json.Nodes.JsonArray(
            Enumerable.Range(0, options.AgentCount).Select(index => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = $"agent-{index}", ["workspace"] = $"worktrees/{index}",
            }).ToArray());
        var basePath = Path.Combine(projectRoot, "abacus_base.json");
        baseConfiguration.Save(basePath, overwrite: false);

        var configurationPaths = new List<string> { basePath };
        foreach (var (mode, defaultModel) in new[]
        {
            ("opencode", "openai/gpt-5.6-sol"),
            ("codex", "gpt-5.6-sol"),
            ("claude", "opus"),
        })
        {
            var configuration = RunConfiguration.Create(projectRoot);
            configuration.Document["baseConfig"] = "abacus_base.json";
            configuration.Document["mode"] = mode;
            configuration.Document["model"] = defaultModel;
            var configurationPath = Path.Combine(projectRoot, $"abacus_{mode}.json");
            configuration.Save(configurationPath, overwrite: false);
            configurationPaths.Add(configurationPath);
        }

        return new MultiAgentRepositoryInitializationResult(
            projectRoot,
            repositoryPath,
            worktreesPath,
            options.AgentCount,
            database,
            configurationPaths.AsReadOnly());
    }

    internal static string CreateIdentifier(string projectName)
    {
        var identifier = NonIdentifierCharacters().Replace(projectName.ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrEmpty(identifier))
        {
            return "abacus";
        }

        return identifier.Length <= 20 ? identifier : identifier[..20].TrimEnd('-');
    }

    private async Task RunRequiredAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string action,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            new CommandSpec(executable, arguments, workingDirectory),
            cancellationToken);
        if (result.Succeeded)
        {
            return;
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {result.ExitCode}"
            : result.StandardError.Trim();
        throw new RepositoryInitializationException($"failed to {action}: {detail}");
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifierCharacters();
}

public sealed class RepositoryInitializationException(string message) : Exception(message);
