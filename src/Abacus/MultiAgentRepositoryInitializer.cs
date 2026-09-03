using System.Text;
using System.Text.RegularExpressions;

namespace Abacus;

public sealed record MultiAgentRepositoryInitializationResult(
    string ProjectRoot,
    string RepositoryPath,
    string WorktreesPath,
    int AgentCount,
    string BeadsDatabase,
    IReadOnlyList<string> LauncherPaths);

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

        await RunRequiredAsync(
            gitExecutable,
            ["-C", repositoryPath, "add", "README.md", ".agents"],
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

        var launcherPaths = new List<string>();
        foreach (var (mode, defaultModel) in new[]
        {
            ("opencode", "openai/gpt-5.6-sol"),
            ("codex", "gpt-5.6-sol"),
            ("claude", "opus"),
        })
        {
            var launcherPath = Path.Combine(projectRoot, $"run_abacus_{mode}.sh");
            await File.WriteAllTextAsync(
                launcherPath,
                RenderLauncher(mode, defaultModel, identifier),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            MakeExecutable(launcherPath);
            launcherPaths.Add(launcherPath);
        }

        return new MultiAgentRepositoryInitializationResult(
            projectRoot,
            repositoryPath,
            worktreesPath,
            options.AgentCount,
            database,
            launcherPaths);
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

    internal static string RenderLauncher(string mode, string defaultModel, string tmuxSession)
    {
        var script = $$$"""
            #!/usr/bin/env bash
            set -Eeuo pipefail

            die() {
              printf 'error: %s\n' "$*" >&2
              exit 1
            }

            root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
            worktrees="$root/worktrees"
            abacus_bin="${ABACUS_BIN:-abacus}"
            tmux_session="${ABACUS_TMUX_SESSION:-{{{tmuxSession}}}}"
            model="${1:-${ABACUS_MODEL:-{{{defaultModel}}}}}"
            effort="${2:-${ABACUS_EFFORT:-high}}"

            [[ -d "$root/repo/.git" ]] || die "missing repository: $root/repo"
            [[ -d "$worktrees" ]] || die "missing worktrees directory: $worktrees"
            [[ -n "$model" && "$model" != *[[:space:]]* ]] || die "model must be nonempty and contain no whitespace"
            [[ -n "$effort" && "$effort" != *[[:space:]#]* ]] || die "effort must be nonempty and contain no whitespace or '#'"

            agent_args=()
            for workspace in "$worktrees"/*; do
              [[ -d "$workspace" ]] || continue
              [[ -e "$workspace/.git" ]] || continue
              index="${workspace##*/}"
              agent_args+=(-a "agent-$index" "$workspace")
            done
            (( ${#agent_args[@]} > 0 )) || die "no worktrees found under $worktrees"

            # Abacus owns panes but not the tmux session. Create it first, for example:
            #   tmux new-session -d -s "$tmux_session"
            exec "$abacus_bin" \
              --mode {{{mode}}} \
              --tmux-session "$tmux_session" \
              --tmux-layout tiled \
              --model "$model" \
              --effort "$effort" \
              "${agent_args[@]}"
            """;
        return script + Environment.NewLine;
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

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifierCharacters();
}

public sealed class RepositoryInitializationException(string message) : Exception(message);
