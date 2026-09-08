namespace Abacus;

public sealed record AgentOptions(string Name, string WorkspacePath);

public sealed record AttentionResolutionOptions(string IssueId, string? Message, bool Reopen);

public sealed record NewMultiAgentRepositoryOptions(string ProjectName, int AgentCount);

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
    bool NotificationSound = false,
    int LatestCommentCount = 8,
    string? AppendAgentPrompt = null,
    string? RepositoryPath = null,
    IReadOnlyList<string>? TargetBranches = null,
    bool Stdio = false,
    string? EventLogPath = null,
    bool NoIntro = false,
    bool StartPaused = false,
    bool DisownTmuxSession = false,
    IReadOnlyDictionary<string, string>? ReasoningModels = null)
{
    public const string DefaultTmuxLayout = "tiled";

    public bool UsesTmux => AgentMode is not AgentMode.OpenCodeServer
        || TmuxSession is not null
        || TmuxWindow is not null
        || TmuxLayout is not null
        || DisownTmuxSession;

    public string? EffectiveTmuxLayout => UsesTmux
        ? TmuxLayout ?? DefaultTmuxLayout
        : null;

    public IReadOnlyDictionary<string, string> EffectiveReasoningModels =>
        ReasoningModels ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly HashSet<string> TmuxLayouts = new(StringComparer.Ordinal)
    {
        "even-horizontal",
        "even-vertical",
        "main-horizontal",
        "main-vertical",
        "tiled",
    };

    public const string ShortUsage = "Usage: abacus <command> [options]. Run 'abacus help' for commands.";
    public const string Usage = CliHelp.Overview;

    public static OptionsParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) return OptionsParseResult.Help;
        var index = 0;
        string? repositoryPath = null;
        while (index < arguments.Count && (arguments[index] == "--repo" || arguments[index].StartsWith("--repo=", StringComparison.Ordinal)))
        {
            var argument = arguments[index];
            var value = argument.StartsWith("--repo=", StringComparison.Ordinal)
                ? argument[7..] : ReadValue(arguments, ref index, "--repo");
            SetRepository(value);
            index++;
        }
        if (index == arguments.Count) throw new OptionsException("a command is required after --repo");
        var command = arguments[index++];
        if (command is "--help" or "-h" or "help")
        {
            var topic = string.Join(" ", arguments.Skip(index));
            return OptionsParseResult.Help with { HelpText = CliHelp.For(topic) };
        }
        if (command is "skills" or "branches" or "attention" or "targets")
        {
            if (index == arguments.Count || arguments[index] is "--help" or "-h")
            {
                if (index < arguments.Count - 1) throw new OptionsException("unexpected arguments after help");
                return OptionsParseResult.Help with { HelpText = CliHelp.For(command) };
            }
            command += " " + arguments[index++];
        }
        // Validate the command before interpreting any options or their values.
        _ = CliHelp.For(command);
        if (repositoryPath is not null && command is "new" or "models")
            throw new OptionsException($"--repo is not supported by {command}");
        var run = command is "run" or "preflight";
        var optionValues = new List<string>();
        var positionals = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var endOfOptions = false;
        var help = false;
        for (; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!endOfOptions && argument == "--") { endOfOptions = true; continue; }
            if (endOfOptions || !argument.StartsWith("-", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }
            if (argument is "--help" or "-h") { help = true; continue; }
            var equals = argument.IndexOf('=');
            var option = equals < 0 ? argument : argument[..equals];
            var canonical = option switch { "-a" => "--agent", "-v" => "--verbose", _ => option };
            var arity = OptionArity(command, canonical);
            if (arity < 0) throw new OptionsException($"unknown option '{option}' for {command}");
            if (!seen.Add(canonical) && canonical is not ("--agent" or "--label" or "--exclude-label" or "--target-filter" or "--reasoning-model"))
                throw new OptionsException($"{option} can only be specified once");
            if (equals >= 0 && arity != 1) throw new OptionsException($"{option} does not accept an equals value");
            var values = new List<string>();
            for (var n = 0; n < arity; n++)
            {
                if (equals >= 0) values.Add(argument[(equals + 1)..]);
                else if (canonical is "--message" or "--append-prompt")
                {
                    if (++index >= arguments.Count) throw new OptionsException($"{option} requires a value");
                    values.Add(arguments[index]);
                }
                else values.Add(ReadValue(arguments, ref index, option));
            }
            if (canonical == "--repo") SetRepository(values[0]);
            else { optionValues.Add(canonical); optionValues.AddRange(values); }
        }
        if (help) return OptionsParseResult.Help with { HelpText = CliHelp.For(command) };
        if (run && positionals.Count != 0) throw new OptionsException($"{command} does not accept positional arguments");
        OptionsParseResult parsed;
        if (run) parsed = ParseRun(optionValues, command == "preflight");
        else
        {
            string? Value(string name)
            {
                var at = optionValues.IndexOf(name);
                return at < 0 ? null : optionValues[at + 1];
            }
            switch (command)
            {
                case "new":
                    if (positionals.Count != 1 || !IsValidProjectName(positionals[0]))
                        throw new OptionsException("new requires a single nonempty project directory name, not a path");
                    if (!int.TryParse(Value("--agents"), out var count) || count <= 0)
                        throw new OptionsException("--agents is required and must be a positive integer");
                    parsed = OptionsParseResult.InitializeNewMultiAgentRepository(positionals[0], count);
                    break;
                case "attention resolve":
                    if (positionals.Count != 1 || !Git.IsValidIssueId(positionals[0]))
                        throw new OptionsException("attention resolve requires exactly one issue ID");
                    var message = Value("--message");
                    if (message is not null && string.IsNullOrWhiteSpace(message))
                        throw new OptionsException("--message cannot be empty");
                    parsed = OptionsParseResult.ResolveAttentionOnly(positionals[0], message, seen.Contains("--reopen"));
                    break;
                case "targets check":
                case "targets set":
                    var check = command == "targets check";
                    string? target = null;
                    if (!check)
                    {
                        if (positionals.Count < 2 || !Git.IsValidTargetBranch(positionals[0]))
                            throw new OptionsException("use targets set <branch> <issue-id> [<issue-id> ...]");
                        target = positionals[0];
                        positionals.RemoveAt(0);
                    }
                    if (positionals.Any(id => !Git.IsValidIssueId(id))) throw new OptionsException("invalid issue ID");
                    if (positionals.Distinct(StringComparer.Ordinal).Count() != positionals.Count)
                        throw new OptionsException("duplicate issue IDs");
                    var adopt = seen.Contains("--adopt-existing-branch");
                    var commit = Value("--start-commit");
                    if ((adopt || commit is not null) && (!adopt || positionals.Count != 1 || !Git.IsCommitId(commit)))
                        throw new OptionsException("adoption requires targets set <branch> <id> --adopt-existing-branch --start-commit <full-commit-id>");
                    parsed = new(null, false, TargetCommand: new(check, target, positionals, null, adopt, commit));
                    break;
                default:
                    if (positionals.Count != 0) throw new OptionsException($"{command} does not accept positional arguments");
                    parsed = command switch
                    {
                        "init" => new(null, false, InitializeRepository: true),
                        "skills install" => OptionsParseResult.InstallSkillsOnly,
                        "health" => OptionsParseResult.Health,
                        "models" => OptionsParseResult.Models,
                        "branches prune" => OptionsParseResult.PruneClosedBranchesOnly,
                        "attention list" => OptionsParseResult.ListUserAttentionOnly,
                        _ => throw new OptionsException($"unknown command '{command}'"),
                    };
                    break;
            }
        }
        return parsed with
        {
            RepositoryPath = repositoryPath,
            Value = parsed.Value is { } options ? options with { RepositoryPath = repositoryPath } : null,
            TargetCommand = parsed.TargetCommand is { } targetCommand ? targetCommand with { RepositoryPath = repositoryPath } : null,
        };

        void SetRepository(string value)
        {
            if (repositoryPath is not null) throw new OptionsException("--repo can only be specified once");
            repositoryPath = CanonicalizePath(value);
        }
    }

    private static int OptionArity(string command, string option)
    {
        if (option == "--repo") return command is "new" or "models" ? -1 : 1;
        if (command is "run" or "preflight")
            return option switch
            {
                "--agent" or "--reasoning-model" => 2,
                "--mode" or "--model" or "--effort" or "--tmux-session" or "--tmux-window" or "--tmux-layout"
                    or "--opencode-server" or "--target-filter" or "--append-prompt" or "--label" or "--exclude-label"
                    or "--type" or "--priority" or "--ticket-timeout" or "--latest-comments" or "--notify" => 1,
                "--remote-control" or "--notify-sound" or "--verbose" => 0,
                "--once" or "--drain" or "--stdio" or "--no-intro" or "--start-paused"
                    or "--disown-tmux-session" when command == "run" => 0,
                "--event-log" when command == "run" => 1,
                _ => -1,
            };
        return (command, option) switch
        {
            ("new", "--agents") or ("attention resolve", "--message") or ("targets set", "--start-commit") => 1,
            ("attention resolve", "--reopen") or ("targets set", "--adopt-existing-branch") => 0,
            _ => -1,
        };
    }

    private static OptionsParseResult ParseRun(IReadOnlyList<string> arguments, bool checkOnly)
    {
        var targetBranches = new List<string>();
        string? tmuxSession = null;
        string? tmuxWindow = null;
        string? tmuxLayout = null;
        string? model = null;
        var effort = "high";
        string? server = null;
        AgentMode? requestedAgentMode = null;
        var verbose = false;
        var stdio = false;
        var noIntro = false;
        var startPaused = false;
        var disownTmuxSession = false;
        string? eventLogPath = null;
        var once = false;
        var drain = false;
        var remote = false;
        var labels = new List<string>();
        var excludedLabels = new List<string>();
        string? issueType = null;
        int? priority = null;
        TimeSpan? ticketTimeout = null;
        var notificationMode = NotificationMode.Off;
        var notificationModeSpecified = false;
        var notificationSound = false;
        var latestCommentCount = 8;
        var latestCommentCountSpecified = false;
        string? appendAgentPrompt = null;
        var appendAgentPromptSpecified = false;
        var agents = new List<AgentOptions>();
        var reasoningModels = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--stdio": stdio = true; break;
                case "--no-intro": noIntro = true; break;
                case "--start-paused": startPaused = true; break;
                case "--disown-tmux-session": disownTmuxSession = true; break;
                case "--event-log":
                    eventLogPath = CanonicalizePath(ReadValue(arguments, ref index, argument));
                    break;
                case "--target-filter":
                    var target = ReadValue(arguments, ref index, argument);
                    if (!Git.IsValidTargetBranch(target)) throw new OptionsException("--target-filter requires a literal local branch name");
                    if (targetBranches.Contains(target)) throw new OptionsException($"duplicate target filter '{target}'");
                    targetBranches.Add(target);
                    break;
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
                case "--reasoning-model":
                    var tier = ReadValue(arguments, ref index, argument);
                    string label;
                    try { label = ReasoningPolicy.LabelForTier(tier); }
                    catch (ArgumentException)
                    {
                        throw new OptionsException("--reasoning-model tier must be high, medium, or low");
                    }
                    var reasoningModel = ReadValue(arguments, ref index, argument);
                    if (!reasoningModels.TryAdd(label, reasoningModel))
                        throw new OptionsException($"--reasoning-model {tier} can only be specified once");
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
                case "--remote-control":
                    remote = true;
                    break;
                case "--append-prompt":
                    if (appendAgentPromptSpecified)
                    {
                        throw new OptionsException("--append-prompt can only be specified once");
                    }

                    appendAgentPrompt = arguments[++index];
                    appendAgentPromptSpecified = true;
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
                case "--latest-comments":
                    if (latestCommentCountSpecified)
                    {
                        throw new OptionsException("--latest-comments can only be specified once");
                    }

                    latestCommentCount = ParseLatestCommentCount(ReadValue(arguments, ref index, argument));
                    latestCommentCountSpecified = true;
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
                case "-v":
                    verbose = true;
                    break;
                case "--agent":
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

        var agentMode = requestedAgentMode ?? AgentMode.OpenCode;

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
            throw new OptionsException("--remote-control can only be used with --mode claude");
        }

        if (tmuxWindow is not null && string.IsNullOrWhiteSpace(tmuxWindow))
        {
            throw new OptionsException("--tmux-window cannot be empty");
        }

        if (disownTmuxSession && tmuxSession is not null)
        {
            throw new OptionsException("--disown-tmux-session cannot be combined with --tmux-session");
        }

        if (tmuxLayout is not null && !TmuxLayouts.Contains(tmuxLayout))
        {
            throw new OptionsException(
                "--tmux-layout must be one of even-horizontal, even-vertical, main-horizontal, main-vertical, or tiled");
        }

        if (startPaused && verbose) throw new OptionsException("--start-paused cannot be combined with --verbose; use the interactive dashboard or --stdio");
        if (stdio && (verbose || notificationMode != NotificationMode.Off || notificationSound))
            throw new OptionsException("--stdio cannot be combined with --verbose or desktop notifications");

        if (once && drain)
        {
            throw new OptionsException("--once and --drain cannot be used together");
        }

        if (checkOnly && (once || drain))
        {
            throw new OptionsException("preflight cannot be combined with --once or --drain");
        }

        if (notificationSound && notificationMode is NotificationMode.Off)
        {
            throw new OptionsException("--notify-sound requires --notify attention or --notify all");
        }

        if (appendAgentPrompt is not null && string.IsNullOrWhiteSpace(appendAgentPrompt))
        {
            throw new OptionsException("--append-prompt cannot be empty");
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

        foreach (var (label, mappedModel) in reasoningModels)
        {
            if (!IsValidModel(mappedModel, agentMode))
            {
                var tier = label["abacus:".Length..^"_reasoning".Length];
                throw new OptionsException(agentMode is AgentMode.OpenCode or AgentMode.OpenCodeServer
                    ? $"--reasoning-model {tier} must use OpenCode's provider/model format"
                    : $"--reasoning-model {tier} cannot contain whitespace");
            }
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
                notificationSound,
                latestCommentCount,
                appendAgentPrompt,
                null,
                targetBranches.AsReadOnly(),
                stdio, eventLogPath, noIntro, startPaused,
                DisownTmuxSession: disownTmuxSession,
                ReasoningModels: reasoningModels),
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

    private static int ParseLatestCommentCount(string value)
    {
        if (!int.TryParse(value, out var count) || count is < 1 or > 100)
        {
            throw new OptionsException("--latest-comments must be an integer from 1 through 100");
        }

        return count;
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

    private static bool IsValidProjectName(string projectName) =>
        !string.IsNullOrWhiteSpace(projectName)
        && projectName is not "." and not ".."
        && !Path.IsPathRooted(projectName)
        && projectName.IndexOf(Path.DirectorySeparatorChar) < 0
        && projectName.IndexOf(Path.AltDirectorySeparatorChar) < 0
        && projectName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}

public sealed record OptionsParseResult(
    Options? Value,
    bool ShowHelp,
    bool InstallSkills = false,
    bool ShowHealth = false,
    bool ShowModels = false,
    bool PruneClosedBranches = false,
    bool ListUserAttention = false,
    AttentionResolutionOptions? AttentionResolution = null,
    NewMultiAgentRepositoryOptions? NewMultiAgentRepository = null,
    TicketTargetCommand? TargetCommand = null,
    string? RepositoryPath = null,
    bool InitializeRepository = false,
    string? HelpText = null)
{
    public static OptionsParseResult Help { get; } = new(null, ShowHelp: true);
    public static OptionsParseResult InstallSkillsOnly { get; } = new(null, ShowHelp: false, InstallSkills: true);
    public static OptionsParseResult Health { get; } = new(null, ShowHelp: false, ShowHealth: true);
    public static OptionsParseResult Models { get; } = new(null, ShowHelp: false, ShowModels: true);
    public static OptionsParseResult PruneClosedBranchesOnly { get; } =
        new(null, ShowHelp: false, PruneClosedBranches: true);
    public static OptionsParseResult ListUserAttentionOnly { get; } =
        new(null, ShowHelp: false, ListUserAttention: true);
    public static OptionsParseResult ResolveAttentionOnly(string issueId, string? message, bool reopen) =>
        new(
            null,
            ShowHelp: false,
            AttentionResolution: new AttentionResolutionOptions(issueId, message, reopen));
    public static OptionsParseResult InitializeNewMultiAgentRepository(string projectName, int agentCount) =>
        new(
            null,
            ShowHelp: false,
            NewMultiAgentRepository: new NewMultiAgentRepositoryOptions(projectName, agentCount));
}

public sealed class OptionsException(string message) : Exception(message);

public sealed record TicketTargetCommand(bool Check, string? Target, IReadOnlyList<string> IssueIds, string? RepositoryPath,
    bool AdoptExistingBranch = false, string? StartCommit = null);
