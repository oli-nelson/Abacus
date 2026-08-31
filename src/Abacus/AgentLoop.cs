namespace Abacus;

public sealed record PreparedClaim(BeadsIssue Issue, string Branch);

public sealed class ClaimCoordinator(
    Beads beads,
    Git git,
    TicketRecovery recovery,
    TextWriter log,
    TimeSpan? pollingInterval = null,
    RunSummary? summary = null)
{
    public TimeSpan PollingInterval { get; } = pollingInterval ?? TimeSpan.FromSeconds(5);

    public async Task<PreparedClaim> WaitForPreparedClaimAsync(
        ValidatedAgent agent,
        bool singleAgentMode,
        CancellationToken cancellationToken) =>
        await WaitForPreparedClaimAsync(
            agent,
            singleAgentMode,
            ExecutionMode.Continuous,
            cancellationToken)
        ?? throw new InvalidOperationException("continuous claim polling returned without a ticket");

    public async Task<PreparedClaim?> WaitForPreparedClaimAsync(
        ValidatedAgent agent,
        bool singleAgentMode,
        ExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        await log.ClearTicketAsync(agent.Name);
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
                    var detail = Beads.FailureDetail(pull);
                    await WarnAsync(agent.Name, $"Beads pull failed; claim delayed: {detail}");
                    if (executionMode is not ExecutionMode.Continuous)
                    {
                        throw new BeadsException($"Beads pull failed during finite execution: {detail}");
                    }

                    await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Beads pull failed; retrying soon");
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
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Could not claim work; retrying soon");
                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            if (issue is null)
            {
                var idleDetail = executionMode is ExecutionMode.Continuous
                    ? "No ready tickets; checking again soon"
                    : "No ready tickets; finite run is complete";
                await log.SetAgentAsync(agent.Name, AgentActivity.Idle, idleDetail);
                if (executionMode is not ExecutionMode.Continuous)
                {
                    return null;
                }

                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            try
            {
                await log.SetTicketAsync(agent.Name, issue.Id, issue.Title);
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
                summary?.Record(agent.Name, TicketOutcome.Interrupted);
                throw;
            }
            catch (Exception exception)
            {
                var note = $"Abacus could not prepare the workspace for {agent.Name}: {exception.Message}";
                await log.SetAgentAsync(agent.Name, AgentActivity.Recovering, $"{issue.Id} • reopening ticket");
                await WarnAsync(agent.Name, note);
                await recovery.ReopenKnownClaimAsync(agent, issue.Id, note, cancellationToken);
                summary?.Record(agent.Name, TicketOutcome.Reopened);
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Workspace preparation failed; retrying soon");
                await Task.Delay(PollingInterval, cancellationToken);
                await log.ClearTicketAsync(agent.Name);
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
    ExecutionMode executionMode,
    RunSummary summary,
    TextWriter log)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var claim = await claims.WaitForPreparedClaimAsync(
                    agent,
                    singleAgentMode,
                    executionMode,
                    cancellationToken);
                if (claim is null)
                {
                    var detail = executionMode is ExecutionMode.Once
                        ? "No ready ticket; once complete"
                        : "No ready tickets; drain complete";
                    await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, detail);
                    return;
                }

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
                    summary.Record(agent.Name, TicketOutcome.Interrupted);
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
                    summary.Record(agent.Name, TicketOutcome.Reopened);
                    throw;
                }

                await log.SetRunLocationAsync(agent.Name, run.Location);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Working,
                    $"{claim.Issue.Id} • OpenCode in {run.Location}");
                await supervisor.SuperviseAsync(agent, claim.Issue, run, cancellationToken);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Finalizing,
                    $"{claim.Issue.Id} • session finished");
                if (executionMode is ExecutionMode.Once)
                {
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Stopped,
                        "One ticket processed; once complete");
                    return;
                }
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
                if (executionMode is not ExecutionMode.Continuous)
                {
                    await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Finite execution failed");
                    throw;
                }

                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Agent loop failed; retrying soon");
                await Task.Delay(claims.PollingInterval, cancellationToken);
            }
        }
    }
}

public sealed class StartupInvariantException(string message) : Exception(message);
