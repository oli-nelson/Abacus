namespace Abacus;

public sealed record AgentOptions(string Name, string WorkspacePath);

public sealed record AttentionResolutionOptions(string IssueId, string? Message);

public enum AgentMode
{
    OpenCode,
    Codex,
    Claude,
    OpenCodeServer,
}

public enum ExecutionMode
{
    Continuous,
    Once,
    Drain,
}

public enum NotificationMode
{
    Off,
    Attention,
    All,
}

public sealed record DispatchFilters(
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> ExcludedLabels,
    string? IssueType,
    int? Priority)
{
    public static DispatchFilters Empty { get; } = new([], [], null, null);
}

public sealed record Options(
    string? TmuxSession,
    string Model,
    string? OpenCodeServer,
    IReadOnlyList<AgentOptions> Agents,
    bool Verbose = false,
    string? TmuxWindow = null,
    string? TmuxLayout = null,
    ExecutionMode ExecutionMode = ExecutionMode.Continuous,
    bool CheckOnly = false,
    AgentMode AgentMode = AgentMode.OpenCode,
    string Effort = "high",
    bool Remote = false,
    DispatchFilters? DispatchFilters = null,
    TimeSpan? TicketTimeout = null,
    NotificationMode NotificationMode = NotificationMode.Off,
    bool NotificationSound = false)
{
    private static readonly HashSet<string> TmuxLayouts = new(StringComparer.Ordinal)
    {
        "even-horizontal",
        "even-vertical",
        "main-horizontal",
        "main-vertical",
        "tiled",
    };

    public const string ShortUsage =
        "Usage: abacus --install-skills | abacus --health | " +
        "abacus --resolve-attention <issue-id> [<message>] | " +
        "abacus [--mode <opencode|codex|claude|opencode-server>] " +
        "[--tmux-session <name> [--tmux-window <name-or-index>] [--tmux-layout <layout>]] " +
        "--model <model> [--effort <effort>] [--remote] " +
        "[--label <label>] [--exclude-label <label>] [--type <types>] [--priority <priority>] " +
        "[--ticket-timeout <duration>] [--notify <off|attention|all>] [--notify-sound] " +
        "[--opencode-server <host:port>] [--once | --drain | --check] [--verbose] " +
        "-a <agent_name> <git_workspace_path> [-a ...]";

    public const string Usage = """
        Abacus coordinates Beads tasks and interactive coding agents.

        Usage:
          abacus --install-skills
          abacus --health
          abacus --resolve-attention <issue-id> [<message>]

          abacus [--mode <opencode|codex|claude|opencode-server>] \
            [--tmux-session <name> [--tmux-window <name-or-index>] [--tmux-layout <layout>]] \
            --model <model> \
            [--effort <effort>] \
            [--remote] \
            [--label <label>] [--exclude-label <label>] \
            [--type <types>] [--priority <priority>] \
            [--ticket-timeout <duration>] \
            [--notify <off|attention|all>] [--notify-sound] \
            [--opencode-server <host:port>] \
            [--once | --drain | --check] \
            [--verbose] \
            -a <agent_name> <git_workspace_path> [-a ...]

        Setup:
          --install-skills installs the bundled abacus-beads-planner,
          abacus-beads-doctor, and abacus-beads-attention skills under
          .agents/skills at the Git root. Existing skills require confirmation
          before their directories are replaced.

        Health:
          --health reports Beads configuration and merge-slot availability,
          supported agent harness and tmux versions, referenced Git worktrees,
          bundled skill presence, and single-/multi-agent readiness.

        Resolve user attention:
          --resolve-attention removes the abacus:needs-user-attention label from
          one Beads issue. If a quoted message is supplied, Abacus first adds a
          Beads comment: "User Responded to a previous attention callout: <message>".

        Output:
          The default interactive display is a live dashboard of agent activity.
          Use --verbose (or -v) for timestamped state changes and subprocess commands.

        Agent modes:
          opencode        Run the interactive OpenCode TUI in tmux (default).
          codex           Run the interactive Codex TUI in tmux.
          claude          Run interactive Claude Code in tmux.
          opencode-server Attach OpenCode clients to --opencode-server; tmux is optional.

        Effort:
          --effort defaults to high. Codex, Claude Code, and OpenCode Server
          receive it through their native CLI options. The interactive OpenCode TUI
          has no variant option and uses its configured or session-selected variant.

        Remote control:
          --remote enables Claude Code Remote Control while keeping the session
          interactive and naming it after the Beads issue.

        Dispatch filters:
          --label and --exclude-label are repeatable. --type accepts the literal
          Beads type filter, including comma-separated values. --priority accepts
          0 (highest) through 4 (lowest). Filters apply to every ready claim.

        Ticket runtime guard:
          --ticket-timeout interrupts an agent after a positive duration such as
          30s, 15m, or 2h, then safely reopens and synchronizes the ticket.

        Desktop notifications:
          --notify defaults to off. attention reports tickets labelled for user
          attention, blocked tickets, and persistent recovery failures. all also
          reports every ticket outcome and the final run summary.
          --notify-sound requests an OS notification sound and permits a terminal
          bell fallback when desktop notification delivery is unavailable.

        Finite execution:
          --once   Process at most one ready ticket per agent, then exit.
          --drain  Process ready work until the queue is empty, then exit.
          --check  Run preflight validation without claiming tickets.

        Tmux layouts:
          --tmux-layout reapplies one of even-horizontal, even-vertical,
          main-horizontal, main-vertical, or tiled after each pane is spawned.

        Required prerequisites:
          - macOS or Linux with bd, git, and the selected agent CLI on PATH
          - tmux on PATH for pane-hosted modes
          - OpenCode, Codex, and Claude modes require an existing tmux session
          - OpenCode Server mode can run directly without tmux
          - each workspace is a clean Git worktree with a Beads project
          - multiple workspaces share one server-backed Dolt database

        Run Codex agents locally:
          abacus --mode codex --tmux-session work --tmux-window agents --tmux-layout tiled --model gpt-5.6-terra --effort high \
            -a alice /work/repo-a -a bob /work/repo-b

        Connect each new client session to an existing OpenCode server:
          abacus --mode opencode-server --model provider/model --effort high --opencode-server 127.0.0.1:1234 \
            -a alice /work/repo-a -a bob /work/repo-b
        """;

    public static OptionsParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Any(static argument => argument is "--help" or "-h"))
        {
            return OptionsParseResult.Help;
        }

        if (arguments.Contains("--install-skills", StringComparer.Ordinal))
        {
            if (arguments.Count != 1)
            {
                throw new OptionsException("--install-skills cannot be combined with other options");
            }

            return OptionsParseResult.InstallSkillsOnly;
        }

        if (arguments.Contains("--health", StringComparer.Ordinal))
        {
            if (arguments.Count != 1)
            {
                throw new OptionsException("--health cannot be combined with other options");
            }

            return OptionsParseResult.Health;
        }

        if (arguments.Contains("--resolve-attention", StringComparer.Ordinal))
        {
            if (arguments.Count is < 2 or > 3
                || !string.Equals(arguments[0], "--resolve-attention", StringComparison.Ordinal))
            {
                throw new OptionsException(
                    "--resolve-attention must be used alone as --resolve-attention <issue-id> [<message>]");
            }

            var issueId = arguments[1];
            if (string.IsNullOrWhiteSpace(issueId) || issueId.Any(char.IsWhiteSpace))
            {
                throw new OptionsException("--resolve-attention requires a nonempty issue ID without whitespace");
            }

            var message = arguments.Count == 3 ? arguments[2] : null;
            if (message is not null && string.IsNullOrWhiteSpace(message))
            {
                throw new OptionsException("--resolve-attention message cannot be empty");
            }

            return OptionsParseResult.ResolveAttentionOnly(issueId, message);
        }

        string? tmuxSession = null;
        string? tmuxWindow = null;
        string? tmuxLayout = null;
        string? model = null;
        var effort = "high";
        string? server = null;
        AgentMode? requestedAgentMode = null;
        var verbose = false;
        var once = false;
        var drain = false;
        var checkOnly = false;
        var remote = false;
        var labels = new List<string>();
        var excludedLabels = new List<string>();
        string? issueType = null;
        int? priority = null;
        TimeSpan? ticketTimeout = null;
        var notificationMode = NotificationMode.Off;
        var notificationModeSpecified = false;
        var notificationSound = false;
        var agents = new List<AgentOptions>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--mode":
                    requestedAgentMode = ParseAgentMode(ReadValue(arguments, ref index, argument));
                    break;
                case "--tmux-session":
                    tmuxSession = ReadValue(arguments, ref index, argument);
                    break;
                case "--tmux-window":
                    tmuxWindow = ReadValue(arguments, ref index, argument);
                    break;
                case "--tmux-layout":
                    tmuxLayout = ReadValue(arguments, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(arguments, ref index, argument);
                    break;
                case "--effort":
                    effort = ReadValue(arguments, ref index, argument);
                    break;
                case "--opencode-server":
                    server = ReadValue(arguments, ref index, argument);
                    break;
                case "--once":
                    once = true;
                    break;
                case "--drain":
                    drain = true;
                    break;
                case "--check":
                    checkOnly = true;
                    break;
                case "--remote":
                    remote = true;
                    break;
                case "--label":
                    labels.Add(ReadFilterValue(arguments, ref index, argument));
                    break;
                case "--exclude-label":
                    excludedLabels.Add(ReadFilterValue(arguments, ref index, argument));
                    break;
                case "--type":
                    if (issueType is not null)
                    {
                        throw new OptionsException("--type can only be specified once");
                    }

                    issueType = ReadFilterValue(arguments, ref index, argument);
                    break;
                case "--priority":
                    if (priority is not null)
                    {
                        throw new OptionsException("--priority can only be specified once");
                    }

                    priority = ParsePriority(ReadValue(arguments, ref index, argument));
                    break;
                case "--ticket-timeout":
                    if (ticketTimeout is not null)
                    {
                        throw new OptionsException("--ticket-timeout can only be specified once");
                    }

                    ticketTimeout = ParseDuration(ReadValue(arguments, ref index, argument));
                    break;
                case "--notify":
                    if (notificationModeSpecified)
                    {
                        throw new OptionsException("--notify can only be specified once");
                    }

                    notificationMode = ParseNotificationMode(ReadValue(arguments, ref index, argument));
                    notificationModeSpecified = true;
                    break;
                case "--notify-sound":
                    notificationSound = true;
                    break;
                case "--verbose":
                case "--debug":
                case "-v":
                    verbose = true;
                    break;
                case "-a":
                    var name = ReadValue(arguments, ref index, argument);
                    var workspace = ReadValue(arguments, ref index, argument);
                    agents.Add(new AgentOptions(name, CanonicalizePath(workspace)));
                    break;
                default:
                    throw new OptionsException($"unknown option '{argument}'");
            }
        }

        if (tmuxSession is not null && string.IsNullOrWhiteSpace(tmuxSession))
        {
            throw new OptionsException("--tmux-session cannot be empty");
        }

        var agentMode = requestedAgentMode ?? (server is null ? AgentMode.OpenCode : AgentMode.OpenCodeServer);

        if (agentMode is not AgentMode.OpenCodeServer && server is not null)
        {
            throw new OptionsException("--opencode-server can only be used with --mode opencode-server");
        }

        if (agentMode is AgentMode.OpenCodeServer && server is null)
        {
            throw new OptionsException("--mode opencode-server requires --opencode-server");
        }

        if (remote && agentMode is not AgentMode.Claude)
        {
            throw new OptionsException("--remote can only be used with --mode claude");
        }

        if (tmuxSession is null && agentMode is not AgentMode.OpenCodeServer)
        {
            throw new OptionsException("--tmux-session is required for opencode, codex, and claude modes");
        }

        if (tmuxWindow is not null && string.IsNullOrWhiteSpace(tmuxWindow))
        {
            throw new OptionsException("--tmux-window cannot be empty");
        }

        if (tmuxWindow is not null && tmuxSession is null)
        {
            throw new OptionsException("--tmux-window requires --tmux-session");
        }

        if (tmuxLayout is not null && tmuxSession is null)
        {
            throw new OptionsException("--tmux-layout requires --tmux-session");
        }

        if (tmuxLayout is not null && !TmuxLayouts.Contains(tmuxLayout))
        {
            throw new OptionsException(
                "--tmux-layout must be one of even-horizontal, even-vertical, main-horizontal, main-vertical, or tiled");
        }

        if (once && drain)
        {
            throw new OptionsException("--once and --drain cannot be used together");
        }

        if (checkOnly && (once || drain))
        {
            throw new OptionsException("--check cannot be combined with --once or --drain");
        }

        if (notificationSound && notificationMode is NotificationMode.Off)
        {
            throw new OptionsException("--notify-sound requires --notify attention or --notify all");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new OptionsException("--model is required");
        }

        if (!IsValidModel(model, agentMode))
        {
            throw new OptionsException(agentMode is AgentMode.OpenCode or AgentMode.OpenCodeServer
                ? "--model must use OpenCode's provider/model format"
                : "--model cannot contain whitespace");
        }

        if (string.IsNullOrEmpty(effort)
            || effort.Any(char.IsWhiteSpace)
            || effort.Contains('#', StringComparison.Ordinal))
        {
            throw new OptionsException("--effort must be a nonempty variant name without whitespace or '#'");
        }

        if (server is not null && string.IsNullOrWhiteSpace(server))
        {
            throw new OptionsException("--opencode-server cannot be empty");
        }

        if (agents.Count == 0)
        {
            throw new OptionsException("at least one -a <agent_name> <git_workspace_path> pair is required");
        }

        var duplicateName = agents
            .GroupBy(static agent => agent.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            throw new OptionsException($"duplicate agent name '{duplicateName}'");
        }

        if (agents.Any(static agent => string.IsNullOrWhiteSpace(agent.Name)))
        {
            throw new OptionsException("agent names cannot be empty");
        }

        var pathComparer = OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var duplicatePath = agents
            .GroupBy(static agent => agent.WorkspacePath, pathComparer)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicatePath is not null)
        {
            throw new OptionsException($"duplicate workspace path '{duplicatePath}'");
        }

        var executionMode = once
            ? ExecutionMode.Once
            : drain ? ExecutionMode.Drain : ExecutionMode.Continuous;
        return new OptionsParseResult(
            new Options(
                tmuxSession,
                model,
                server,
                agents.AsReadOnly(),
                verbose,
                tmuxWindow,
                tmuxLayout,
                executionMode,
                checkOnly,
                agentMode,
                effort,
                remote,
                new DispatchFilters(labels.AsReadOnly(), excludedLabels.AsReadOnly(), issueType, priority),
                ticketTimeout,
                notificationMode,
                notificationSound),
            ShowHelp: false);
    }

    private static string ReadFilterValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        var value = ReadValue(arguments, ref index, option);
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new OptionsException($"{option} must be a nonempty value without whitespace");
        }

        return value;
    }

    private static int ParsePriority(string value)
    {
        if (!int.TryParse(value, out var priority) || priority is < 0 or > 4)
        {
            throw new OptionsException("--priority must be an integer from 0 through 4");
        }

        return priority;
    }

    private static TimeSpan ParseDuration(string value)
    {
        if (value.Length < 2
            || !long.TryParse(value[..^1], out var amount)
            || amount <= 0)
        {
            throw new OptionsException("--ticket-timeout must be a positive duration such as 30s, 15m, or 2h");
        }

        try
        {
            return value[^1] switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => throw new OptionsException(
                    "--ticket-timeout must be a positive duration such as 30s, 15m, or 2h"),
            };
        }
        catch (OverflowException)
        {
            throw new OptionsException("--ticket-timeout is too large");
        }
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || arguments[index].StartsWith("-", StringComparison.Ordinal))
        {
            throw new OptionsException($"{option} requires a value");
        }

        return arguments[index];
    }

    private static AgentMode ParseAgentMode(string value) => value switch
    {
        "opencode" => AgentMode.OpenCode,
        "codex" => AgentMode.Codex,
        "claude" => AgentMode.Claude,
        "opencode-server" => AgentMode.OpenCodeServer,
        _ => throw new OptionsException(
            "--mode must be one of opencode, codex, claude, or opencode-server"),
    };

    private static NotificationMode ParseNotificationMode(string value) => value switch
    {
        "off" => NotificationMode.Off,
        "attention" => NotificationMode.Attention,
        "all" => NotificationMode.All,
        _ => throw new OptionsException("--notify must be one of off, attention, or all"),
    };

    private static bool IsValidModel(string model, AgentMode agentMode)
    {
        if (model.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (agentMode is AgentMode.Codex or AgentMode.Claude)
        {
            return model.Length > 0;
        }

        var separator = model.IndexOf('/');
        return separator > 0
            && separator == model.LastIndexOf('/')
            && separator < model.Length - 1
            && !model.Contains('#', StringComparison.Ordinal);
    }

    private static string CanonicalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new OptionsException("workspace paths cannot be empty");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}

public sealed record OptionsParseResult(
    Options? Value,
    bool ShowHelp,
    bool InstallSkills = false,
    bool ShowHealth = false,
    AttentionResolutionOptions? AttentionResolution = null)
{
    public static OptionsParseResult Help { get; } = new(null, ShowHelp: true);
    public static OptionsParseResult InstallSkillsOnly { get; } = new(null, ShowHelp: false, InstallSkills: true);
    public static OptionsParseResult Health { get; } = new(null, ShowHelp: false, ShowHealth: true);
    public static OptionsParseResult ResolveAttentionOnly(string issueId, string? message) =>
        new(
            null,
            ShowHelp: false,
            AttentionResolution: new AttentionResolutionOptions(issueId, message));
}

public sealed class OptionsException(string message) : Exception(message);
