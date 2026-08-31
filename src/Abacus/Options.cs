namespace Abacus;

public sealed record AgentOptions(string Name, string WorkspacePath);

public enum ExecutionMode
{
    Continuous,
    Once,
    Drain,
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
    bool CheckOnly = false)
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
        "Usage: abacus [--tmux-session <name> [--tmux-window <name-or-index>] [--tmux-layout <layout>]] " +
        "--model <provider/model> " +
        "[--opencode-server <host:port>] [--once | --drain | --check] [--verbose] " +
        "-a <agent_name> <git_workspace_path> [-a ...]";

    public const string Usage = """
        Abacus coordinates Beads tasks and local or attached OpenCode agents.

        Usage:
          abacus [--tmux-session <name> [--tmux-window <name-or-index>] [--tmux-layout <layout>]] \
            --model <provider/model> \
            [--opencode-server <host:port>] \
            [--once | --drain | --check] \
            [--verbose] \
            -a <agent_name> <git_workspace_path> [-a ...]

        Output:
          The default interactive display is a live dashboard of agent activity.
          Use --verbose (or -v) for timestamped state changes and subprocess commands.

        Finite execution:
          --once   Process at most one ready ticket per agent, then exit.
          --drain  Process ready work until the queue is empty, then exit.
          --check  Run preflight validation without claiming tickets.

        Tmux layouts:
          --tmux-layout reapplies one of even-horizontal, even-vertical,
          main-horizontal, main-vertical, or tiled after each pane is spawned.

        Required prerequisites:
          - macOS or Linux with bd, git, and opencode on PATH
          - tmux on PATH for pane-hosted modes
          - local Mini mode requires an existing tmux session
          - attached-server mode can run directly without tmux
          - each workspace is a clean Git worktree with a Beads project
          - multiple workspaces share one server-backed Dolt database

        Run agents locally:
          abacus --tmux-session work --tmux-window agents --tmux-layout tiled --model provider/model \
            -a alice /work/repo-a -a bob /work/repo-b

        Connect each new client session to an existing OpenCode server:
          abacus --model provider/model --opencode-server 127.0.0.1:1234 \
            -a alice /work/repo-a -a bob /work/repo-b
        """;

    public static OptionsParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Any(static argument => argument is "--help" or "-h"))
        {
            return OptionsParseResult.Help;
        }

        string? tmuxSession = null;
        string? tmuxWindow = null;
        string? tmuxLayout = null;
        string? model = null;
        string? server = null;
        var verbose = false;
        var once = false;
        var drain = false;
        var checkOnly = false;
        var agents = new List<AgentOptions>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
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

        if (tmuxSession is null && server is null)
        {
            throw new OptionsException("--tmux-session is required unless --opencode-server is supplied");
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

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new OptionsException("--model is required");
        }

        if (!IsValidModel(model))
        {
            throw new OptionsException("--model must use OpenCode's provider/model format");
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
                checkOnly),
            ShowHelp: false);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || arguments[index].StartsWith("-", StringComparison.Ordinal))
        {
            throw new OptionsException($"{option} requires a value");
        }

        return arguments[index];
    }

    private static bool IsValidModel(string model)
    {
        var separator = model.IndexOf('/');
        return separator > 0
            && separator == model.LastIndexOf('/')
            && separator < model.Length - 1
            && !model.Any(char.IsWhiteSpace);
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

public sealed record OptionsParseResult(Options? Value, bool ShowHelp)
{
    public static OptionsParseResult Help { get; } = new(null, ShowHelp: true);
}

public sealed class OptionsException(string message) : Exception(message);
