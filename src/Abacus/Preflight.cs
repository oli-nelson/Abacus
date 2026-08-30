namespace Abacus;

public sealed record ExternalTools(string Bd, string Git, string OpenCode, string Tmux);

public sealed record ValidatedAgent(
    string Name,
    string WorkspacePath,
    DoltIdentity DoltIdentity,
    bool HasRemote);

public sealed record PreflightResult(
    Options Options,
    IReadOnlyList<ValidatedAgent> Agents,
    ExternalTools Tools,
    string? OpenCodeServerUrl);

public sealed class Preflight(CommandRunner runner, string? executablePath = null)
{
    private readonly string path = executablePath
        ?? Environment.GetEnvironmentVariable("PATH")
        ?? string.Empty;

    public async Task<PreflightResult> RunAsync(Options options, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PreflightException("Abacus supports macOS and Linux only");
        }

        var tools = new ExternalTools(
            FindExecutable("bd"),
            FindExecutable("git"),
            FindExecutable("opencode"),
            FindExecutable("tmux"));

        await VerifyTmuxSessionAsync(tools.Tmux, options.TmuxSession, cancellationToken);

        var git = new Git(runner, tools.Git);
        var beads = new Beads(runner, tools.Bd);
        var existingAgents = options.Agents
            .Select(static agent => new AgentOptions(agent.Name, ResolveWorkspace(agent.WorkspacePath)))
            .ToArray();
        var resolvedAgents = new List<AgentOptions>(existingAgents.Length);
        foreach (var agent in existingAgents)
        {
            resolvedAgents.Add(new AgentOptions(
                agent.Name,
                await git.ResolveWorkspaceRootAsync(agent.WorkspacePath, agent.Name, cancellationToken)));
        }

        RejectDuplicateResolvedWorkspaces(resolvedAgents);

        var validated = new List<ValidatedAgent>(options.Agents.Count);
        foreach (var agent in resolvedAgents)
        {
            await git.VerifyWorkspaceAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            var identity = await beads.ReadDoltIdentityAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            var hasRemote = await beads.HasRemoteAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            validated.Add(new ValidatedAgent(agent.Name, agent.WorkspacePath, identity, hasRemote));
        }

        ValidateDoltSafety(validated);

        return new PreflightResult(
            options,
            validated.AsReadOnly(),
            tools,
            NormalizeServer(options.OpenCodeServer));
    }

    public static string? NormalizeServer(string? server)
    {
        if (server is null)
        {
            return null;
        }

        if (server.Contains("//", StringComparison.Ordinal)
            || server.Contains("/", StringComparison.Ordinal)
            || server.Any(char.IsWhiteSpace)
            || !Uri.TryCreate($"http://{server}", UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Port is <= 0 or > 65535
            || !HasExplicitPort(server))
        {
            throw new PreflightException("--opencode-server must be a host:port value");
        }

        return $"http://{server}";
    }

    private static bool HasExplicitPort(string server)
    {
        if (server.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = server.IndexOf(']');
            return closingBracket > 0
                && closingBracket + 2 < server.Length
                && server[closingBracket + 1] == ':'
                && int.TryParse(server[(closingBracket + 2)..], out _);
        }

        var separator = server.LastIndexOf(':');
        return separator > 0
            && separator < server.Length - 1
            && int.TryParse(server[(separator + 1)..], out _);
    }

    private async Task VerifyTmuxSessionAsync(
        string tmux,
        string session,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new CommandSpec(
            tmux,
            ["has-session", "-t", session],
            Environment.CurrentDirectory), cancellationToken);
        if (!result.Succeeded)
        {
            throw new PreflightException($"tmux session '{session}' does not exist");
        }
    }

    private string FindExecutable(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PreflightException("Abacus supports macOS and Linux only");
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

        throw new PreflightException($"required executable '{name}' was not found on PATH");
    }

    private static string ResolveWorkspace(string workspace)
    {
        if (!Directory.Exists(workspace))
        {
            throw new PreflightException($"workspace does not exist: '{workspace}'");
        }

        var directory = new DirectoryInfo(workspace);
        var resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved?.FullName ?? directory.FullName));
    }

    private static void RejectDuplicateResolvedWorkspaces(IReadOnlyList<AgentOptions> agents)
    {
        var comparer = OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var duplicate = agents
            .GroupBy(static agent => agent.WorkspacePath, comparer)
            .FirstOrDefault(static group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new PreflightException($"multiple agents resolve to the same workspace: '{duplicate}'");
        }
    }

    private static void ValidateDoltSafety(IReadOnlyList<ValidatedAgent> agents)
    {
        if (agents.Count == 1)
        {
            return;
        }

        var first = agents[0].DoltIdentity;
        if (!first.IsShared)
        {
            throw new PreflightException("multiple agents require one shared, server-backed Dolt database");
        }

        foreach (var agent in agents.Skip(1))
        {
            if (!agent.DoltIdentity.IsShared
                || !string.Equals(first.SharedKey, agent.DoltIdentity.SharedKey, StringComparison.Ordinal))
            {
                throw new PreflightException("all agents must use the same shared Dolt host, port, and database");
            }
        }
    }
}

public sealed class PreflightException(string message) : Exception(message);
