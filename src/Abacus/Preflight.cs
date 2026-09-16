namespace Abacus;

public sealed record ExternalTools(string Bd, string Git, string AgentExecutable, string? Tmux);

public sealed record ValidatedAgent(
    string Name,
    string WorkspacePath,
    DoltIdentity DoltIdentity,
    bool HasRemote,
    string? AppendedPrompt = null,
    string? MergeInstructionsOverride = null,
    TargetRegistry? Targets = null,
    IReadOnlyList<string>? TargetBranches = null,
    ReasoningPolicy? Reasoning = null,
    string? HarnessPromptOverride = null,
    PoolAssignment? PoolAssignment = null);

public sealed record PreflightResult(
    Options Options,
    IReadOnlyList<ValidatedAgent> Agents,
    ExternalTools Tools,
    string? OpenCodeServerUrl,
    string RepositoryRoot);

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
            FindExecutable(AgentCommandFactory.ExecutableName(options.AgentMode)),
            options.UsesTmux ? FindExecutable("tmux") : null);

        var git = new Git(runner, tools.Git);
        var controllerRoot = await git.ResolveMainRepositoryAsync(
            Environment.CurrentDirectory, options.RepositoryPath, cancellationToken);

        if (options.SupervisorModel is not null)
            await MaintenanceSupervisor.ReadAdditivePromptAsync(controllerRoot, options.SupervisorPromptFile, cancellationToken);

        if (options.ContinuationModel is not null)
            await ContinuationSupervisor.ReadAdditivePromptAsync(controllerRoot, options.ContinuationPromptFile, cancellationToken);

        var beads = new Beads(runner, tools.Bd);
        var resolvedAgents = new List<AgentOptions>();
        if (options.ManagedAgentCount is not null)
        {
            // Missing slots use the controller only for read-only tool/database/target validation.
            // Runtime allocates under the pool lease, then revalidates the real worktrees.
            var pool = await WorktreePool.OpenAsync(runner, controllerRoot, cancellationToken, tools.Git);
            // Validate every retained slot read-only, independently of this run's worker names/count.
            foreach (var slot in pool.ReadManifest()?.Slots ?? [])
            {
                await pool.VerifySlotAsync(slot, cancellationToken);
                pool.ReadAssignment(slot.Name);
            }
            foreach (var agent in options.Agents)
                resolvedAgents.Add(new(agent.Name, controllerRoot));
        }
        else
        {
            foreach (var agent in options.Agents)
                resolvedAgents.Add(new(agent.Name, await git.ResolveWorkspaceRootAsync(
                    ResolveWorkspace(agent.WorkspacePath), agent.Name, cancellationToken)));
            RejectDuplicateResolvedWorkspaces(resolvedAgents);
        }

        var validated = new List<ValidatedAgent>(options.Agents.Count);
        foreach (var agent in resolvedAgents)
        {
            await git.VerifyWorkspaceAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            if (await beads.IsNoGitOpsEnabledAsync(agent.WorkspacePath, cancellationToken))
            {
                throw new PreflightException(
                    $"Abacus cannot continue because Beads no-git-ops is enabled. Disable it with: {Beads.DisableNoGitOpsCommand}");
            }

            var identity = await beads.ReadDoltIdentityAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            var hasRemote = await beads.HasRemoteAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            string? repositoryPrompt;
            string? mergeInstructionsOverride;
            try
            {
                repositoryPrompt = await Prompt.ReadRepositoryAppendAsync(
                    agent.WorkspacePath,
                    cancellationToken);
                mergeInstructionsOverride = null; // Target policies are loaded once from the controller below.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new PreflightException(
                    $"could not read repository prompt instructions in '{agent.WorkspacePath}': {exception.Message}");
            }

            validated.Add(new ValidatedAgent(
                agent.Name,
                agent.WorkspacePath,
                identity,
                hasRemote,
                Prompt.CombineAppends(options.AppendAgentPrompt, repositoryPrompt),
                mergeInstructionsOverride));
        }

        ValidateDoltSafety(validated);
        if (options.HasSupervisors)
        {
            if (await beads.IsNoGitOpsEnabledAsync(controllerRoot, cancellationToken))
                throw new PreflightException($"Supervisor main checkout has no-git-ops enabled. {Beads.DisableNoGitOpsCommand}");
            var mainIdentity = await beads.ReadDoltIdentityAsync(controllerRoot, MaintenanceSupervisor.Name, cancellationToken);
            var workerIdentity = validated[0].DoltIdentity;
            if (workerIdentity.IsShared)
            {
                if (!mainIdentity.IsShared || mainIdentity.SharedKey != workerIdentity.SharedKey)
                    throw new PreflightException("Supervisor main checkout must use the same shared Dolt database as the agents");
            }
            else if (mainIdentity.IsShared
                || await beads.ReadProjectDirectoryAsync(controllerRoot, cancellationToken)
                    != await beads.ReadProjectDirectoryAsync(validated[0].WorkspacePath, cancellationToken))
            {
                throw new PreflightException("Supervisor main checkout and agent must use the same Beads project (check bd where --json)");
            }
        }

        if (options.HasSupervisors && options.ManagedAgentCount is null
            && validated.Any(agent => agent.WorkspacePath == controllerRoot))
            throw new PreflightException("Supervisors need the main checkout exclusively; use managed workers or separate worker worktrees");

        var targets = await TargetRegistry.LoadAsync(
            Path.Combine(controllerRoot, ".abacus", "targets.json"), cancellationToken);
        ReasoningPolicy reasoning;
        try
        {
            reasoning = await ReasoningPolicy.LoadAsync(
                Path.Combine(controllerRoot, ".abacus", "reasoning.json"), cancellationToken);
            reasoning.ValidateMappings(options.EffectiveReasoningModels);
        }
        catch (ReasoningPolicyException exception)
        {
            throw new PreflightException(exception.Message);
        }
        foreach (var branch in options.TargetBranches ?? []) targets.Resolve(branch);
        for (var i = 0; i < validated.Count; i++)
        {
            foreach (var target in targets.Targets.Keys)
                await git.ResolveTargetCommitAsync(validated[i].WorkspacePath, validated[i].Name, target, cancellationToken);
            validated[i] = validated[i] with
            {
                Targets = targets,
                TargetBranches = options.TargetBranches,
                Reasoning = reasoning,
            };
        }


        return new PreflightResult(
            options,
            validated.AsReadOnly(),
            tools,
            NormalizeServer(options.OpenCodeServer),
            controllerRoot);
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
