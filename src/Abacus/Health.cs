using System.Text;
using System.Text.Json;
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

public enum MergeSlotHealthStatus
{
    NotChecked,
    Missing,
    Available,
    Held,
    Error,
}

public sealed record MergeSlotHealth(
    MergeSlotHealthStatus Status,
    string? Id,
    string? Holder,
    string Detail);

public enum NoGitOpsHealthStatus
{
    NotChecked,
    Disabled,
    Enabled,
    Error,
}

public sealed record NoGitOpsHealth(NoGitOpsHealthStatus Status, string Detail);

public sealed record TargetConfigurationHealth(string Path, IReadOnlyList<string> Branches, string? Error,
    bool EnforceTargetBranch = false, string DefaultTarget = "main")
{
    public bool IsReady => Error is null;
}

public sealed record ReasoningConfigurationHealth(
    string Path,
    bool Exists,
    bool EnforceLabels,
    string? Error)
{
    public bool IsReady => Error is null;
}

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
    NoGitOpsHealth NoGitOps,
    MergeSlotHealth MergeSlot,
    IReadOnlyList<GitWorktreeHealth> Worktrees,
    string? WorktreeError,
    IReadOnlyList<SkillHealth> Skills,
    IReadOnlyList<string> AvailableModes,
    bool SingleAgentReady,
    bool MultiAgentReady,
    TargetConfigurationHealth? TargetConfiguration = null,
    ReasoningConfigurationHealth? ReasoningConfiguration = null)
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Red = "\u001b[31m";

    public bool AreSkillsInstalled => Skills.All(static skill => skill.IsInstalled);

    public bool IsHealthy => SingleAgentReady && AreSkillsInstalled
        && ReasoningConfiguration is not { IsReady: false };

    public string Render(bool color = false)
    {
        var text = new StringBuilder();
        text.AppendLine(Color(color, Bold + Cyan, "Abacus health"));
        text.AppendLine("=============");
        text.AppendLine();
        AppendHeading(text, "Project", color);
        AppendTool(text, Git, color);
        AppendStatus(text, RepositoryRoot is null ? "FAIL" : "PASS",
            RepositoryRoot is null
                ? "Git repository root could not be resolved."
                : $"Git root: {RepositoryRoot}", color);

        text.AppendLine();
        AppendHeading(text, "Target configuration", color);
        if (TargetConfiguration is { IsReady: true } targetConfig)
        {
            AppendStatus(text, "PASS", targetConfig.Path, color);
            text.AppendLine($"  Allowed local targets: {string.Join(", ", targetConfig.Branches)}");
            text.AppendLine(targetConfig.EnforceTargetBranch
                ? "  Target metadata enforcement: enabled (explicit ticket targets required)"
                : $"  Target metadata enforcement: disabled (missing targets use {targetConfig.DefaultTarget})");
            text.AppendLine("  Ticket metadata is checked separately with: abacus targets check");
        }
        else
        {
            AppendStatus(text, "FAIL", TargetConfiguration?.Error ?? "Target configuration was not checked", color);
            text.AppendLine("  Create .abacus/targets.json with abacus init, or repair the existing version-1 targets allowlist explicitly.");
        }
        text.AppendLine();
        AppendHeading(text, "Reasoning model routing", color);
        if (ReasoningConfiguration is { IsReady: true } reasoningConfig)
        {
            AppendStatus(text, reasoningConfig.Exists ? "PASS" : "INFO",
                reasoningConfig.Exists
                    ? reasoningConfig.Path
                    : $"{reasoningConfig.Path} is absent; reasoning-label enforcement defaults to disabled.", color);
            text.AppendLine(reasoningConfig.EnforceLabels
                ? "  Reasoning-label enforcement: enabled (all three runtime mappings are required)"
                : "  Reasoning-label enforcement: disabled (unmapped or unlabelled tickets use --model)");
        }
        else
        {
            AppendStatus(text, "FAIL",
                ReasoningConfiguration?.Error ?? "Reasoning configuration was not checked", color);
            text.AppendLine("  Repair .abacus/reasoning.json or recreate its version-1 default with abacus init.");
        }
        text.AppendLine();
        AppendHeading(text, "Beads", color);
        AppendTool(text, Beads, color);
        switch (NoGitOps.Status)
        {
            case NoGitOpsHealthStatus.Disabled:
                AppendStatus(text, "PASS", "Git operations: no-git-ops is disabled.", color);
                break;
            case NoGitOpsHealthStatus.Enabled:
                AppendStatus(text, "FAIL", "Git operations: Beads no-git-ops is enabled, so Abacus cannot continue.", color);
                text.AppendLine($"  Disable it with: {Abacus.Beads.DisableNoGitOpsCommand}");
                break;
            case NoGitOpsHealthStatus.Error:
                AppendStatus(text, "FAIL", $"Git operations: could not check no-git-ops: {NoGitOps.Detail}", color);
                break;
            default:
                AppendStatus(text, "INFO", $"Git operations: no-git-ops was not checked: {NoGitOps.Detail}", color);
                break;
        }

        if (DoltIdentity is null)
        {
            AppendStatus(text, "FAIL", $"Not initialized or unavailable: {BeadsError ?? "unknown error"}", color);
            text.AppendLine("  Agent concurrency: unavailable until Beads is initialized and healthy.");
        }
        else if (DoltIdentity.Embedded)
        {
            AppendStatus(text, "PASS", $"Initialized with embedded Dolt database '{DoltIdentity.Database}'.", color);
            text.AppendLine("  Agent concurrency: single-agent only; embedded Beads is not safe for Abacus multi-agent execution.");
        }
        else if (!DoltIdentity.ConnectionOk)
        {
            AppendStatus(text, "FAIL", $"Initialized for server-backed Dolt database '{DoltIdentity.Database}', but the server connection is unavailable.", color);
            text.AppendLine("  Agent concurrency: unavailable until the configured Dolt server is reachable.");
        }
        else
        {
            AppendStatus(text, "PASS", $"Initialized with shared Dolt database {DoltIdentity.SharedKey}.", color);
            text.AppendLine("  Agent concurrency: Beads permits single- and multi-agent execution.");
        }

        switch (MergeSlot.Status)
        {
            case MergeSlotHealthStatus.Available:
                AppendStatus(text, "PASS", $"Merge slot {MergeSlot.Id}: available.", color);
                break;
            case MergeSlotHealthStatus.Held:
                AppendStatus(text, "PASS", $"Merge slot {MergeSlot.Id}: held by {MergeSlot.Holder}.", color);
                break;
            case MergeSlotHealthStatus.Missing:
                AppendStatus(text, "WARN", "Merge slot: not configured.", color);
                text.AppendLine("  Without a merge slot, agents on multiple workspaces or machines may attempt merges concurrently unless another serialized merge process is configured. Create one with: bd merge-slot create");
                break;
            case MergeSlotHealthStatus.Error:
                AppendStatus(text, "WARN", $"Merge slot could not be checked: {MergeSlot.Detail}", color);
                break;
            default:
                AppendStatus(text, "INFO", $"Merge slot not checked: {MergeSlot.Detail}", color);
                break;
        }

        text.AppendLine();
        AppendHeading(text, "Agent harnesses", color);
        AppendTool(text, OpenCode, color, optional: true);
        AppendTool(text, Claude, color, optional: true);
        AppendTool(text, Codex, color, optional: true);
        var readyHarnesses = new[] { OpenCode, Claude, Codex }.Count(static tool => tool.IsReady);
        AppendStatus(text, readyHarnesses > 0 ? "PASS" : "FAIL",
            readyHarnesses > 0
                ? $"{readyHarnesses} supported agent harness{(readyHarnesses == 1 ? string.Empty : "es")} available; at least one is required."
                : "No supported agent harness meets its minimum version; at least one is required.", color);

        text.AppendLine();
        AppendHeading(text, "tmux", color);
        AppendTool(text, Tmux, color, optional: true);
        AppendStatus(text, Tmux.IsReady ? "PASS" : "WARN",
            Tmux.IsReady
                ? "Pane-hosted OpenCode, Claude, and Codex modes can use tmux."
                : "Pane-hosted modes are unavailable; direct OpenCode Server mode does not require tmux.", color);

        text.AppendLine();
        AppendHeading(text, "Referenced Git worktrees", color);
        if (WorktreeError is not null)
        {
            AppendStatus(text, "FAIL", $"Could not list worktrees: {WorktreeError}", color);
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
                AppendStatus(text, "FAIL", "Git reported no referenced worktrees.", color);
                text.AppendLine("  Multi-agent execution cannot use linked worktrees until worktrees are created. Separate clones may also be supplied, but health does not search for them.");
            }
            else if (Worktrees.Count == 1)
            {
                AppendStatus(text, "WARN", "No additional linked worktrees are referenced by the root repository.", color);
                text.AppendLine("  Multi-agent execution cannot use linked worktrees until more are created. Separate clones may also be supplied, but health does not search for them.");
            }
            else
            {
                AppendStatus(text, "PASS", $"{Worktrees.Count} worktrees provide distinct candidate workspaces.", color);
            }
        }

        text.AppendLine();
        AppendHeading(text, "Bundled agent skills", color);
        foreach (var skill in Skills)
        {
            if (skill.IsInstalled)
            {
                AppendStatus(text, "PASS", $"{skill.Name}: {skill.Path}", color);
            }
            else
            {
                AppendStatus(text, "FAIL", $"{skill.Name}: missing {string.Join(", ", skill.MissingFiles)}", color);
            }
        }

        if (!AreSkillsInstalled)
        {
            text.AppendLine("  Install or replace the bundled skills with: abacus skills install");
        }

        text.AppendLine();
        AppendHeading(text, "Available agent modes", color);
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
        AppendReadiness(text, "Bundled skills readiness", AreSkillsInstalled, color);
        AppendReadiness(text, "Single-agent readiness", SingleAgentReady, color);
        AppendReadiness(text, "Multi-agent readiness from linked worktrees", MultiAgentReady, color);
        if (!MultiAgentReady)
        {
            text.AppendLine("Separate clones can satisfy the workspace requirement, but they were not searched.");
        }

        return text.ToString();
    }

    internal static bool ShouldUseColor(bool outputRedirected, string? term, string? noColor) =>
        TerminalUi.ShouldUseColor(outputRedirected, term, noColor);

    private static void AppendHeading(StringBuilder text, string heading, bool color) =>
        text.AppendLine(Color(color, Bold + Cyan, heading));

    private static void AppendReadiness(StringBuilder text, string label, bool ready, bool color)
    {
        var status = ready ? "READY" : "NOT READY";
        text.Append(label).Append(": ")
            .AppendLine(Color(color, ready ? Green : Red, status));
    }

    private static void AppendStatus(StringBuilder text, string marker, string detail, bool color)
    {
        var ansi = marker switch
        {
            "PASS" => Green,
            "WARN" => Yellow,
            "INFO" => Cyan,
            _ => Red,
        };
        text.Append("  ").Append(Color(color, ansi, $"[{marker}]")).Append(' ').AppendLine(detail);
    }

    private static string Color(bool enabled, string ansi, string value) =>
        enabled ? ansi + value + Reset : value;

    private static void AppendTool(StringBuilder text, ToolHealth tool, bool color, bool optional = false)
    {
        var marker = tool.Status switch
        {
            ToolHealthStatus.Ready => "PASS",
            ToolHealthStatus.Missing when optional => "INFO",
            ToolHealthStatus.Missing => "FAIL",
            ToolHealthStatus.Outdated => "WARN",
            _ => "FAIL",
        };
        AppendStatus(text, marker, $"{tool.Name}: {tool.Detail}", color);
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

    public async Task<HealthReport> RunAsync(string workingDirectory, CancellationToken cancellationToken, string? repositoryPath = null)
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
            try
            {
                repositoryRoot = await new Git(runner, FindExecutable("git")!)
                    .ResolveMainRepositoryAsync(workingDirectory, repositoryPath, cancellationToken);
                var worktreeResult = await RunAsync("git", ["-C", repositoryRoot, "worktree", "list", "--porcelain"],
                    repositoryRoot, cancellationToken);
                if (worktreeResult.Succeeded) worktrees = ParseWorktrees(worktreeResult.StandardOutput);
                else worktreeError = FailureDetail(worktreeResult);
            }
            catch (PreflightException exception)
            {
                repositoryError = exception.Message;
                worktreeError = repositoryError;
            }
        }

        TargetConfigurationHealth targetConfiguration;
        var targetConfigPath = Path.Combine(repositoryRoot ?? repositoryPath ?? workingDirectory, ".abacus", "targets.json");
        try
        {
            if (repositoryRoot is null) throw new TargetException("Git repository is unavailable; target configuration cannot be checked");
            var registry = await TargetRegistry.LoadAsync(targetConfigPath, cancellationToken);
            var targetGit = new Git(runner, FindExecutable("git")!);
            foreach (var branch in registry.Targets.Keys)
                await targetGit.ResolveTargetCommitAsync(repositoryRoot, "abacus", branch, cancellationToken);
            targetConfiguration = new(targetConfigPath, registry.Targets.Keys.Order(StringComparer.Ordinal).ToArray(), null,
                registry.EnforceTargetBranch, registry.DefaultTarget);
        }
        catch (TargetException exception)
        {
            targetConfiguration = new(targetConfigPath, [], exception.Message);
        }

        ReasoningConfigurationHealth reasoningConfiguration;
        var reasoningConfigPath = Path.Combine(
            repositoryRoot ?? repositoryPath ?? workingDirectory, ".abacus", "reasoning.json");
        try
        {
            if (repositoryRoot is null)
                throw new ReasoningPolicyException(
                    "Git repository is unavailable; reasoning configuration cannot be checked");
            var exists = File.Exists(reasoningConfigPath);
            var policy = await ReasoningPolicy.LoadAsync(reasoningConfigPath, cancellationToken);
            reasoningConfiguration = new(
                reasoningConfigPath, exists, policy.EnforceLabels, Error: null);
        }
        catch (ReasoningPolicyException exception)
        {
            reasoningConfiguration = new(reasoningConfigPath, false, false, exception.Message);
        }

        var skills = InspectSkills(repositoryRoot);

        DoltIdentity? identity = null;
        string? beadsError = null;
        var noGitOps = new NoGitOpsHealth(
            NoGitOpsHealthStatus.NotChecked,
            "Git or Beads is unavailable");
        var mergeSlot = new MergeSlotHealth(
            MergeSlotHealthStatus.NotChecked,
            Id: null,
            Holder: null,
            Detail: "Beads is not initialized or available");
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
            try
            {
                var enabled = await new Beads(runner, FindExecutable("bd")!)
                    .IsNoGitOpsEnabledAsync(repositoryRoot, cancellationToken);
                noGitOps = new NoGitOpsHealth(
                    enabled ? NoGitOpsHealthStatus.Enabled : NoGitOpsHealthStatus.Disabled,
                    enabled ? "enabled" : "disabled");
            }
            catch (PreflightException exception)
            {
                noGitOps = new NoGitOpsHealth(NoGitOpsHealthStatus.Error, exception.Message);
            }

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

                if (identity is not null)
                {
                    mergeSlot = await CheckMergeSlotAsync(repositoryRoot, cancellationToken);
                }
            }
        }

        var beadsOperational = beads.IsReady
            && identity is not null
            && (identity.Embedded || identity.ConnectionOk)
            && noGitOps.Status is NoGitOpsHealthStatus.Disabled;
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

        var singleAgentReady = repositoryRoot is not null && modes.Count > 0
            && targetConfiguration.IsReady && reasoningConfiguration.IsReady;
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
            noGitOps,
            mergeSlot,
            worktrees,
            worktreeError,
            skills,
            modes,
            singleAgentReady,
            multiAgentReady,
            targetConfiguration,
            reasoningConfiguration);
    }

    private async Task<MergeSlotHealth> CheckMergeSlotAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "bd",
            ["merge-slot", "check", "--json"],
            repositoryRoot,
            cancellationToken);
        if (!result.Succeeded)
        {
            return new MergeSlotHealth(
                MergeSlotHealthStatus.Error,
                Id: null,
                Holder: null,
                Detail: FailureDetail(result));
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement)
                && idElement.ValueKind is JsonValueKind.String
                ? idElement.GetString()
                : null;
            if (root.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind is JsonValueKind.String)
            {
                var error = errorElement.GetString() ?? "unknown error";
                return error.Equals("not found", StringComparison.OrdinalIgnoreCase)
                    ? new MergeSlotHealth(MergeSlotHealthStatus.Missing, id, Holder: null, error)
                    : new MergeSlotHealth(MergeSlotHealthStatus.Error, id, Holder: null, error);
            }

            if (!root.TryGetProperty("available", out var availableElement)
                || availableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new MergeSlotHealth(
                    MergeSlotHealthStatus.Error,
                    id,
                    Holder: null,
                    "response did not contain an availability value");
            }

            if (availableElement.GetBoolean())
            {
                return new MergeSlotHealth(MergeSlotHealthStatus.Available, id, Holder: null, "available");
            }

            var holder = root.TryGetProperty("holder", out var holderElement)
                && holderElement.ValueKind is JsonValueKind.String
                ? holderElement.GetString()
                : null;
            return string.IsNullOrWhiteSpace(holder)
                ? new MergeSlotHealth(
                    MergeSlotHealthStatus.Error,
                    id,
                    Holder: null,
                    "slot was unavailable but did not identify a holder")
                : new MergeSlotHealth(MergeSlotHealthStatus.Held, id, holder, "held");
        }
        catch (JsonException exception)
        {
            return new MergeSlotHealth(
                MergeSlotHealthStatus.Error,
                Id: null,
                Holder: null,
                Detail: $"invalid JSON: {exception.Message}");
        }
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
