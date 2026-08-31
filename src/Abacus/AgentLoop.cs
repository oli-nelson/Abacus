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
            await log.SetAgentAsync(agent.Name, AgentActivity.Waiting, "Looking for a ready ticket");
            if (!Directory.Exists(agent.WorkspacePath))
            {
                throw new StartupInvariantException(
                    $"[{agent.Name}] workspace disappeared: '{agent.WorkspacePath}'");
            }

            try
            {
                if (!await git.IsWorkspaceCleanAsync(agent.WorkspacePath, agent.Name, cancellationToken))
                {
                    await log.SetAgentAsync(agent.Name, AgentActivity.Cleaning, "Discarding workspace changes");
                    await WarnAsync(agent.Name, "workspace is dirty; discarding local changes before claiming work");
                    await git.CleanWorkspaceAsync(agent.WorkspacePath, agent.Name, cancellationToken);
                    await log.SetAgentAsync(agent.Name, AgentActivity.Waiting, "workspace cleaned; continuing claims");
                }
            }
            catch (WorkspacePreparationException exception)
            {
                throw new StartupInvariantException(
                    $"[{agent.Name}] could not clean workspace '{agent.WorkspacePath}': {exception.Message}");
            }

            if (singleAgentMode && agent.HasRemote)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Syncing, "Pulling the latest Beads data");
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
                await log.SetAgentAsync(agent.Name, AgentActivity.Waiting, "No ready tickets; checking again soon");
                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            try
            {
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Preparing,
                    $"{issue.Id} • preparing workspace and branch");
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
                await log.SetAgentAsync(agent.Name, AgentActivity.Recovering, $"{issue.Id} • reopening ticket");
                await WarnAsync(agent.Name, note);
                await recovery.ReopenKnownClaimAsync(agent, issue.Id, note, cancellationToken);

                await Task.Delay(PollingInterval, cancellationToken);
            }
        }
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WarningAsync(agentName, message);
}

public sealed class AgentLoop(
    ValidatedAgent agent,
    bool singleAgentMode,
    string model,
    string? serverUrl,
    ClaimCoordinator claims,
    IOpenCodeHost openCodeHost,
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
                IOpenCodeRun run;
                try
                {
                    run = await openCodeHost.StartOpenCodeAsync(
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
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{claim.Issue.Id} • OpenCode could not start; reopening ticket");
                    await recovery.ReopenKnownClaimAsync(
                        agent,
                        claim.Issue.Id,
                        $"Abacus could not start OpenCode for {agent.Name}: {exception.Message}",
                        CancellationToken.None);
                    throw;
                }

                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Working,
                    $"{claim.Issue.Id} • OpenCode in {run.Location}");
                await supervisor.SuperviseAsync(agent, claim.Issue, run, cancellationToken);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Finalizing,
                    $"{claim.Issue.Id} • session finished");
            }
            catch (StartupInvariantException)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Workspace invariant failed");
                throw;
            }
            catch (OperationCanceledException)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Shutting down");
                throw;
            }
            catch (Exception exception)
            {
                await log.WarningAsync(agent.Name, $"agent loop failed: {exception.Message}");
                await Task.Delay(claims.PollingInterval, cancellationToken);
            }
        }
    }
}

public sealed class StartupInvariantException(string message) : Exception(message);
