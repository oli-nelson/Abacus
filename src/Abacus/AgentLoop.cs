namespace Abacus;

public sealed record PreparedClaim(BeadsIssue Issue, string Branch);

public sealed class ClaimCoordinator(
    Beads beads,
    Git git,
    TicketRecovery recovery,
    TextWriter log,
    TimeSpan? pollingInterval = null)
{
    public TimeSpan PollingInterval { get; } = pollingInterval ?? TimeSpan.FromSeconds(5);

    public async Task<PreparedClaim> WaitForPreparedClaimAsync(
        ValidatedAgent agent,
        bool singleAgentMode,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(agent.WorkspacePath))
            {
                throw new StartupInvariantException(
                    $"[{agent.Name}] workspace disappeared: '{agent.WorkspacePath}'");
            }

            try
            {
                if (!await git.IsWorkspaceCleanAsync(agent.WorkspacePath, agent.Name, cancellationToken))
                {
                    await WarnAsync(agent.Name, "workspace is dirty; discarding local changes before claiming work");
                    await git.CleanWorkspaceAsync(agent.WorkspacePath, agent.Name, cancellationToken);
                    await log.WriteLineAsync($"[{agent.Name}] workspace cleaned; continuing claims");
                }
            }
            catch (WorkspacePreparationException exception)
            {
                throw new StartupInvariantException(
                    $"[{agent.Name}] could not clean workspace '{agent.WorkspacePath}': {exception.Message}");
            }

            if (singleAgentMode && agent.HasRemote)
            {
                var pull = await beads.PullAsync(agent.WorkspacePath, agent.Name, cancellationToken);
                if (!pull.Succeeded)
                {
                    await WarnAsync(agent.Name, $"Beads pull failed; claim delayed: {Beads.FailureDetail(pull)}");
                    await Task.Delay(PollingInterval, cancellationToken);
                    continue;
                }
            }

            BeadsIssue? issue;
            try
            {
                issue = await beads.TryClaimReadyAsync(agent.WorkspacePath, agent.Name, cancellationToken);
            }
            catch (BeadsException exception)
            {
                await WarnAsync(agent.Name, exception.Message);
                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            if (issue is null)
            {
                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            try
            {
                var branch = await git.PrepareIssueBranchAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    issue.Id,
                    cancellationToken);
                return new PreparedClaim(issue, branch);
            }
            catch (OperationCanceledException)
            {
                await recovery.ReopenKnownClaimAsync(
                    agent,
                    issue.Id,
                    $"Abacus shut down while preparing the workspace for {agent.Name}",
                    CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                var note = $"Abacus could not prepare the workspace for {agent.Name}: {exception.Message}";
                await WarnAsync(agent.Name, note);
                await recovery.ReopenKnownClaimAsync(agent, issue.Id, note, cancellationToken);

                await Task.Delay(PollingInterval, cancellationToken);
            }
        }
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WriteLineAsync($"[{agentName}] warning: {message}");
}

public sealed class AgentLoop(
    ValidatedAgent agent,
    bool singleAgentMode,
    string model,
    string? serverUrl,
    ClaimCoordinator claims,
    Tmux tmux,
    TicketSupervisor supervisor,
    TicketRecovery recovery,
    TextWriter log)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var claim = await claims.WaitForPreparedClaimAsync(agent, singleAgentMode, cancellationToken);
                OpenCodeRun run;
                try
                {
                    run = await tmux.StartOpenCodeAsync(
                        agent,
                        claim.Issue,
                        model,
                        serverUrl,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await recovery.ReopenKnownClaimAsync(
                        agent,
                        claim.Issue.Id,
                        $"Abacus shut down before OpenCode started for {agent.Name}",
                        CancellationToken.None);
                    throw;
                }
                catch (Exception exception)
                {
                    await recovery.ReopenKnownClaimAsync(
                        agent,
                        claim.Issue.Id,
                        $"Abacus could not start OpenCode for {agent.Name}: {exception.Message}",
                        CancellationToken.None);
                    throw;
                }

                await supervisor.SuperviseAsync(agent, claim.Issue, run, cancellationToken);
            }
            catch (StartupInvariantException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await log.WriteLineAsync($"[{agent.Name}] warning: agent loop failed: {exception.Message}");
                await Task.Delay(claims.PollingInterval, cancellationToken);
            }
        }
    }
}

public sealed class StartupInvariantException(string message) : Exception(message);
