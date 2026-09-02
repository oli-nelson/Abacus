using System.Text;
using System.Text.RegularExpressions;

namespace Abacus;

public enum ToolHealthStatus
{
    Ready,
    Missing,
    Outdated,
    Error,
}

public sealed record ToolHealth(
    string Name,
    string MinimumVersion,
    ToolHealthStatus Status,
    string? DetectedVersion,
    string Detail)
{
    public bool IsReady => Status is ToolHealthStatus.Ready;
}

public sealed record GitWorktreeHealth(string Path, string? Branch);

public sealed record SkillHealth(
    string Name,
    string Path,
    bool IsInstalled,
    IReadOnlyList<string> MissingFiles);

public sealed record HealthReport(
    string? RepositoryRoot,
    ToolHealth Git,
    ToolHealth Beads,
    ToolHealth OpenCode,
    ToolHealth Claude,
    ToolHealth Codex,
    ToolHealth Tmux,
    DoltIdentity? DoltIdentity,
    string? BeadsError,
    IReadOnlyList<GitWorktreeHealth> Worktrees,
    string? WorktreeError,
    IReadOnlyList<SkillHealth> Skills,
    IReadOnlyList<string> AvailableModes,
    bool SingleAgentReady,
    bool MultiAgentReady)
{
    public bool AreSkillsInstalled => Skills.All(static skill => skill.IsInstalled);

    public bool IsHealthy => SingleAgentReady && AreSkillsInstalled;

    public string Render()
    {
        var text = new StringBuilder();
        text.AppendLine("Abacus health");
        text.AppendLine("=============");
        text.AppendLine();
        text.AppendLine("Project");
        AppendTool(text, Git);
        text.AppendLine(RepositoryRoot is null
            ? "  [FAIL] Git repository root could not be resolved."
            : $"  [PASS] Git root: {RepositoryRoot}");

        text.AppendLine();
        text.AppendLine("Beads");
        AppendTool(text, Beads);
        if (DoltIdentity is null)
        {
            text.AppendLine($"  [FAIL] Not initialized or unavailable: {BeadsError ?? "unknown error"}");
            text.AppendLine("  Agent concurrency: unavailable until Beads is initialized and healthy.");
        }
        else if (DoltIdentity.Embedded)
        {
            text.AppendLine($"  [PASS] Initialized with embedded Dolt database '{DoltIdentity.Database}'.");
            text.AppendLine("  Agent concurrency: single-agent only; embedded Beads is not safe for Abacus multi-agent execution.");
        }
        else if (!DoltIdentity.ConnectionOk)
        {
            text.AppendLine($"  [FAIL] Initialized for server-backed Dolt database '{DoltIdentity.Database}', but the server connection is unavailable.");
            text.AppendLine("  Agent concurrency: unavailable until the configured Dolt server is reachable.");
        }
        else
        {
            text.AppendLine($"  [PASS] Initialized with shared Dolt database {DoltIdentity.SharedKey}.");
            text.AppendLine("  Agent concurrency: Beads permits single- and multi-agent execution.");
        }

        text.AppendLine();
        text.AppendLine("Agent harnesses");
        AppendTool(text, OpenCode, optional: true);
        AppendTool(text, Claude, optional: true);
        AppendTool(text, Codex, optional: true);
        var readyHarnesses = new[] { OpenCode, Claude, Codex }.Count(static tool => tool.IsReady);
        text.AppendLine(readyHarnesses > 0
            ? $"  [PASS] {readyHarnesses} supported agent harness{(readyHarnesses == 1 ? string.Empty : "es")} available; at least one is required."
            : "  [FAIL] No supported agent harness meets its minimum version; at least one is required.");

        text.AppendLine();
        text.AppendLine("tmux");
        AppendTool(text, Tmux, optional: true);
        text.AppendLine(Tmux.IsReady
            ? "  [PASS] Pane-hosted OpenCode, Claude, and Codex modes can use tmux."
            : "  [WARN] Pane-hosted modes are unavailable; direct OpenCode Server mode does not require tmux.");

        text.AppendLine();
        text.AppendLine("Referenced Git worktrees");
        if (WorktreeError is not null)
        {
            text.AppendLine($"  [FAIL] Could not list worktrees: {WorktreeError}");
        }
        else
        {
            foreach (var worktree in Worktrees)
            {
                text.Append("  - ").Append(worktree.Path);
                if (worktree.Branch is not null)
                {
                    text.Append(" [").Append(worktree.Branch).Append(']');
                }

                text.AppendLine();
            }

            if (Worktrees.Count == 0)
            {
                text.AppendLine("  [FAIL] Git reported no referenced worktrees.");
                text.AppendLine("  Multi-agent execution cannot use linked worktrees until worktrees are created. Separate clones may also be supplied, but --health does not search for them.");
            }
            else if (Worktrees.Count == 1)
            {
                text.AppendLine("  [WARN] No additional linked worktrees are referenced by the root repository.");
                text.AppendLine("  Multi-agent execution cannot use linked worktrees until more are created. Separate clones may also be supplied, but --health does not search for them.");
            }
            else
            {
                text.AppendLine($"  [PASS] {Worktrees.Count} worktrees provide distinct candidate workspaces.");
            }
        }

        text.AppendLine();
        text.AppendLine("Bundled agent skills");
        foreach (var skill in Skills)
        {
            if (skill.IsInstalled)
            {
                text.AppendLine($"  [PASS] {skill.Name}: {skill.Path}");
            }
            else
            {
                text.AppendLine($"  [FAIL] {skill.Name}: missing {string.Join(", ", skill.MissingFiles)}");
            }
        }

        if (!AreSkillsInstalled)
        {
            text.AppendLine("  Install or replace the bundled skills with: abacus --install-skills");
        }

        text.AppendLine();
        text.AppendLine("Available agent modes");
        if (AvailableModes.Count == 0)
        {
            text.AppendLine("  - none");
        }
        else
        {
            foreach (var mode in AvailableModes)
            {
                text.Append("  - ").AppendLine(mode);
            }
        }

        text.AppendLine();
        text.AppendLine($"Bundled skills readiness: {(AreSkillsInstalled ? "READY" : "NOT READY")}");
        text.AppendLine($"Single-agent readiness: {(SingleAgentReady ? "READY" : "NOT READY")}");
        text.AppendLine($"Multi-agent readiness from linked worktrees: {(MultiAgentReady ? "READY" : "NOT READY")}");
        if (!MultiAgentReady)
        {
            text.AppendLine("Separate clones can satisfy the workspace requirement, but they were not searched.");
        }

        return text.ToString();
    }

    private static void AppendTool(StringBuilder text, ToolHealth tool, bool optional = false)
    {
        var marker = tool.Status switch
        {
            ToolHealthStatus.Ready => "PASS",
            ToolHealthStatus.Missing when optional => "INFO",
            ToolHealthStatus.Missing => "FAIL",
            ToolHealthStatus.Outdated => "WARN",
            _ => "FAIL",
        };
        text.Append("  [").Append(marker).Append("] ").Append(tool.Name).Append(": ").AppendLine(tool.Detail);
    }
}

public sealed partial class HealthChecker(CommandRunner runner, string? executablePath = null)
{
    public const string MinimumGitVersion = "2.55.0";
    public const string MinimumBeadsVersion = "1.2.2";
    public const string MinimumOpenCodeVersion = "1.18.20";
    public const string MinimumClaudeVersion = "2.1.212";
    public const string MinimumCodexVersion = "0.151.0";
    public const string MinimumTmuxVersion = "3.6a";

    private readonly string path = executablePath
        ?? Environment.GetEnvironmentVariable("PATH")
        ?? string.Empty;

    public async Task<HealthReport> RunAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        var git = await ProbeAsync("Git", "git", MinimumGitVersion, ["--version"], workingDirectory, cancellationToken);
        var beads = await ProbeAsync("Beads", "bd", MinimumBeadsVersion, ["version"], workingDirectory, cancellationToken);
        var openCode = await ProbeAsync("OpenCode", "opencode", MinimumOpenCodeVersion, ["--version"], workingDirectory, cancellationToken);
        var claude = await ProbeAsync("Claude Code", "claude", MinimumClaudeVersion, ["--version"], workingDirectory, cancellationToken);
        var codex = await ProbeAsync("Codex CLI", "codex", MinimumCodexVersion, ["--version"], workingDirectory, cancellationToken);
        var tmux = await ProbeAsync("tmux", "tmux", MinimumTmuxVersion, ["-V"], workingDirectory, cancellationToken);

        string? repositoryRoot = null;
        string? repositoryError = null;
        IReadOnlyList<GitWorktreeHealth> worktrees = [];
        string? worktreeError = null;
        if (git.IsReady)
        {
            var rootResult = await RunAsync("git", ["-C", workingDirectory, "rev-parse", "--show-toplevel"], workingDirectory, cancellationToken);
            if (rootResult.Succeeded && !string.IsNullOrWhiteSpace(rootResult.StandardOutput))
            {
                repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootResult.StandardOutput.Trim()));
                var worktreeResult = await RunAsync(
                    "git",
                    ["-C", repositoryRoot, "worktree", "list", "--porcelain"],
                    repositoryRoot,
                    cancellationToken);
                if (worktreeResult.Succeeded)
                {
                    worktrees = ParseWorktrees(worktreeResult.StandardOutput);
                }
                else
                {
                    worktreeError = FailureDetail(worktreeResult);
                }
            }
            else
            {
                repositoryError = FailureDetail(rootResult);
                worktreeError = repositoryError;
            }
        }

        var skills = InspectSkills(repositoryRoot);

        DoltIdentity? identity = null;
        string? beadsError = null;
        if (repositoryRoot is null)
        {
            beadsError = repositoryError ?? "Git is unavailable";
        }
        else if (!beads.IsReady)
        {
            beadsError = beads.Status is ToolHealthStatus.Missing
                ? "bd is not installed"
                : "the installed bd version is unsupported";
        }
        else
        {
            var where = await RunAsync("bd", ["where", "--json"], repositoryRoot, cancellationToken);
            if (!where.Succeeded)
            {
                beadsError = $"{FailureDetail(where)}. Run 'bd init' if this repository has not been initialized";
            }
            else
            {
                try
                {
                    identity = await new Beads(runner, FindExecutable("bd")!)
                        .ReadDoltIdentityAsync(repositoryRoot, agentName: null, cancellationToken);
                }
                catch (Exception exception) when (exception is BeadsException or PreflightException)
                {
                    beadsError = exception.Message;
                }
            }
        }

        var beadsOperational = beads.IsReady
            && identity is not null
            && (identity.Embedded || identity.ConnectionOk);
        var modes = new List<string>();
        if (beadsOperational && openCode.IsReady)
        {
            modes.Add("opencode-server (direct; an existing server is still required and is not checked)");
            if (tmux.IsReady)
            {
                modes.Add("opencode (tmux-hosted)");
            }
        }

        if (beadsOperational && claude.IsReady && tmux.IsReady)
        {
            modes.Add("claude (tmux-hosted, with optional Remote Control)");
        }

        if (beadsOperational && codex.IsReady && tmux.IsReady)
        {
            modes.Add("codex (tmux-hosted)");
        }

        var singleAgentReady = repositoryRoot is not null && modes.Count > 0;
        var multiAgentReady = singleAgentReady
            && identity?.IsShared is true
            && worktreeError is null
            && worktrees.Count > 1;

        return new HealthReport(
            repositoryRoot,
            git,
            beads,
            openCode,
            claude,
            codex,
            tmux,
            identity,
            beadsError,
            worktrees,
            worktreeError,
            skills,
            modes,
            singleAgentReady,
            multiAgentReady);
    }

    internal static IReadOnlyList<GitWorktreeHealth> ParseWorktrees(string output)
    {
        var worktrees = new List<GitWorktreeHealth>();
        string? currentPath = null;
        string? currentBranch = null;
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (currentPath is not null)
                {
                    worktrees.Add(new GitWorktreeHealth(currentPath, currentBranch));
                }

                currentPath = line["worktree ".Length..];
                currentBranch = null;
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                currentBranch = line["branch refs/heads/".Length..];
            }
            else if (line.Length == 0 && currentPath is not null)
            {
                worktrees.Add(new GitWorktreeHealth(currentPath, currentBranch));
                currentPath = null;
                currentBranch = null;
            }
        }

        if (currentPath is not null)
        {
            worktrees.Add(new GitWorktreeHealth(currentPath, currentBranch));
        }

        return worktrees;
    }

    internal static IReadOnlyList<SkillHealth> InspectSkills(string? repositoryRoot)
    {
        return SkillInstaller.InstallableSkillNames.Select(name =>
        {
            var skillPath = repositoryRoot is null
                ? Path.Combine(".agents", "skills", name)
                : Path.Combine(repositoryRoot, ".agents", "skills", name);
            var missingFiles = new List<string>();
            if (!File.Exists(Path.Combine(skillPath, "SKILL.md")))
            {
                missingFiles.Add("SKILL.md");
            }

            if (!File.Exists(Path.Combine(skillPath, "agents", "openai.yaml")))
            {
                missingFiles.Add("agents/openai.yaml");
            }

            return new SkillHealth(name, skillPath, missingFiles.Count == 0, missingFiles);
        }).ToArray();
    }

    internal static int CompareVersions(string left, string right)
    {
        if (!TryParseVersion(left, out var leftVersion) || !TryParseVersion(right, out var rightVersion))
        {
            throw new ArgumentException("version must contain at least two numeric components");
        }

        for (var index = 0; index < Math.Max(leftVersion.Numbers.Length, rightVersion.Numbers.Length); index++)
        {
            var leftPart = index < leftVersion.Numbers.Length ? leftVersion.Numbers[index] : 0;
            var rightPart = index < rightVersion.Numbers.Length ? rightVersion.Numbers[index] : 0;
            var comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return SuffixValue(leftVersion.Suffix).CompareTo(SuffixValue(rightVersion.Suffix));
    }

    private async Task<ToolHealth> ProbeAsync(
        string displayName,
        string executableName,
        string minimumVersion,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var executable = FindExecutable(executableName);
        if (executable is null)
        {
            return new ToolHealth(
                displayName,
                minimumVersion,
                ToolHealthStatus.Missing,
                null,
                $"not installed (minimum {minimumVersion})");
        }

        var result = await runner.RunAsync(
            new CommandSpec(executable, arguments, workingDirectory),
            cancellationToken);
        if (!result.Succeeded)
        {
            return new ToolHealth(
                displayName,
                minimumVersion,
                ToolHealthStatus.Error,
                null,
                $"version check failed: {FailureDetail(result)}");
        }

        var versionOutput = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        if (!TryParseVersion(versionOutput, out var detected))
        {
            return new ToolHealth(
                displayName,
                minimumVersion,
                ToolHealthStatus.Error,
                null,
                $"could not parse version from '{versionOutput.Trim()}'");
        }

        var detectedVersion = detected.Original;
        var ready = CompareVersions(detectedVersion, minimumVersion) >= 0;
        return new ToolHealth(
            displayName,
            minimumVersion,
            ready ? ToolHealthStatus.Ready : ToolHealthStatus.Outdated,
            detectedVersion,
            ready
                ? $"{detectedVersion} (minimum {minimumVersion})"
                : $"{detectedVersion} is below minimum {minimumVersion}");
    }

    private Task<CommandResult> RunAsync(
        string executableName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            new CommandSpec(FindExecutable(executableName)!, arguments, workingDirectory),
            cancellationToken);

    private string? FindExecutable(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var mode = File.GetUnixFileMode(candidate);
            if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string FailureDetail(CommandResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {result.ExitCode}"
            : result.StandardError.Trim();

    private static bool TryParseVersion(string value, out ParsedVersion version)
    {
        var match = VersionPattern().Match(value);
        if (!match.Success)
        {
            version = default;
            return false;
        }

        var components = match.Groups[1].Value.Split('.');
        var numbers = new int[components.Length];
        for (var index = 0; index < components.Length; index++)
        {
            if (!int.TryParse(components[index], out numbers[index]))
            {
                version = default;
                return false;
            }
        }

        version = new ParsedVersion(match.Value, numbers, match.Groups[2].Value);
        return true;
    }

    private static int SuffixValue(string suffix)
    {
        var value = 0;
        foreach (var character in suffix.ToLowerInvariant())
        {
            value = checked((value * 26) + character - 'a' + 1);
        }

        return value;
    }

    private readonly record struct ParsedVersion(string Original, int[] Numbers, string Suffix);

    [GeneratedRegex(@"(?<!\d)(\d+(?:\.\d+)+)([a-zA-Z]*)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
