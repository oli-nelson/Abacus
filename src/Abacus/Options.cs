namespace Abacus;

public sealed record AgentOptions(string Name, string WorkspacePath);

public sealed record Options(
    string TmuxSession,
    string Model,
    string? OpenCodeServer,
    IReadOnlyList<AgentOptions> Agents)
{
    public const string ShortUsage =
        "Usage: abacus --tmux-session <name> --model <provider/model> " +
        "[--opencode-server <host:port>] -a <agent_name> <git_workspace_path> [-a ...]";

    public const string Usage = """
        Abacus coordinates Beads tasks and OpenCode agents in an existing tmux session.

        Usage:
          abacus --tmux-session <name> --model <provider/model> \
            [--opencode-server <host:port>] \
            -a <agent_name> <git_workspace_path> [-a ...]

        Required prerequisites:
          - macOS or Linux with bd, git, opencode, and tmux on PATH
          - an existing tmux session
          - each workspace is a clean Git worktree with a Beads project
          - multiple workspaces share one server-backed Dolt database

        Run agents locally:
          abacus --tmux-session work --model provider/model \
            -a alice /work/repo-a -a bob /work/repo-b

        Connect each new client session to an existing OpenCode server:
          abacus --tmux-session work --model provider/model \
            --opencode-server 127.0.0.1:1234 \
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
        string? model = null;
        string? server = null;
        var agents = new List<AgentOptions>();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--tmux-session":
                    tmuxSession = ReadValue(arguments, ref index, argument);
                    break;
                case "--model":
                    model = ReadValue(arguments, ref index, argument);
                    break;
                case "--opencode-server":
                    server = ReadValue(arguments, ref index, argument);
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

        if (string.IsNullOrWhiteSpace(tmuxSession))
        {
            throw new OptionsException("--tmux-session is required");
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

        return new OptionsParseResult(
            new Options(tmuxSession, model, server, agents.AsReadOnly()),
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
