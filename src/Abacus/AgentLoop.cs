namespace Abacus;

public sealed record PreparedClaim(BeadsIssue Issue, string Branch);

public sealed class ClaimCoordinator(
    Beads beads,
    Git git,
    TicketRecovery recovery,
    TextWriter log,
    TimeSpan? pollingInterval = null,
    RunSummary? summary = null,
    DispatchFilters? dispatchFilters = null)
{
    private readonly DispatchFilters filters = dispatchFilters ?? DispatchFilters.Empty;
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
                issue = await beads.TryClaimReadyAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    filters,
                    cancellationToken);
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
                await RecoverClaimAsync(
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
                await RecoverClaimAsync(agent, issue.Id, note, cancellationToken);
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

    private async Task RecoverClaimAsync(
        ValidatedAgent agent,
        string issueId,
        string note,
        CancellationToken cancellationToken)
    {
        var result = await recovery.ReopenKnownClaimAsync(agent, issueId, note, cancellationToken);
        if (result.Outcome is RecoveryOutcome.Failed)
        {
            await HaltAsync(agent.Name, $"Could not reopen and verify {issueId}; no more work will be claimed");
        }

        if (result.Outcome is RecoveryOutcome.Reopened)
        {
            summary?.Record(agent.Name, TicketOutcome.Reopened);
        }
        else if (result.VerifiedStatus is IssueStatus.Closed)
        {
            summary?.Record(agent.Name, TicketOutcome.Closed);
        }
        else if (result.VerifiedStatus is IssueStatus.Open)
        {
            summary?.Record(agent.Name, TicketOutcome.Reopened);
        }
        else if (result.VerifiedStatus is IssueStatus.Blocked)
        {
            summary?.Record(agent.Name, TicketOutcome.Blocked);
        }

        if (await recovery.PushWithRetryAsync(agent, CancellationToken.None) is PushOutcome.Failed)
        {
            await HaltAsync(agent.Name, "All Beads push attempts failed; no more work will be claimed");
        }
    }

    private async Task HaltAsync(string agentName, string message)
    {
        await WarnAsync(agentName, message);
        await log.SetPersistentAlertAsync(agentName, message);
        throw new AgentHaltedException($"[{agentName}] {message}");
    }
}

public sealed class AgentLoop(
    ValidatedAgent agent,
    bool singleAgentMode,
    string model,
    string effort,
    string? serverUrl,
    ClaimCoordinator claims,
    IAgentHost agentHost,
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

                IAgentRun run;
                try
                {
                    run = await agentHost.StartAgentAsync(
                        agent,
                        claim.Issue,
                        model,
                        effort,
                        serverUrl,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await RecoverClaimAsync(
                        claim.Issue.Id,
                        $"Abacus shut down before the agent CLI started for {agent.Name}");
                    summary.Record(agent.Name, TicketOutcome.Interrupted);
                    throw;
                }
                catch (Exception exception)
                {
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{claim.Issue.Id} • agent CLI could not start; reopening ticket");
                    await RecoverClaimAsync(
                        claim.Issue.Id,
                        $"Abacus could not start the agent CLI for {agent.Name}: {exception.Message}");
                    throw;
                }

                await log.SetRunLocationAsync(agent.Name, run.Location);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Working,
                    $"{claim.Issue.Id} • agent CLI in {run.Location}");
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
            catch (AgentHaltedException exception)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Persistent recovery failure needs user attention");
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                await log.WarningAsync(agent.Name, exception.Message);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
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

    private async Task RecoverClaimAsync(string issueId, string note)
    {
        var result = await recovery.ReopenKnownClaimAsync(
            agent, issueId, note, CancellationToken.None);
        if (result.Outcome is RecoveryOutcome.Failed)
        {
            await HaltAsync($"Could not reopen and verify {issueId}; no more work will be claimed");
        }

        if (result.Outcome is RecoveryOutcome.Reopened)
        {
            summary.Record(agent.Name, TicketOutcome.Reopened);
        }
        else if (result.VerifiedStatus is IssueStatus.Closed)
        {
            summary.Record(agent.Name, TicketOutcome.Closed);
        }
        else if (result.VerifiedStatus is IssueStatus.Open)
        {
            summary.Record(agent.Name, TicketOutcome.Reopened);
        }
        else if (result.VerifiedStatus is IssueStatus.Blocked)
        {
            summary.Record(agent.Name, TicketOutcome.Blocked);
        }

        if (await recovery.PushWithRetryAsync(agent, CancellationToken.None) is PushOutcome.Failed)
        {
            await HaltAsync("All Beads push attempts failed; no more work will be claimed");
        }
    }

    private async Task HaltAsync(string message)
    {
        await log.WarningAsync(agent.Name, message);
        await log.SetPersistentAlertAsync(agent.Name, message);
        throw new AgentHaltedException($"[{agent.Name}] {message}");
    }
}

public sealed class StartupInvariantException(string message) : Exception(message);
