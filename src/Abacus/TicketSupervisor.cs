namespace Abacus;

using System.Diagnostics;

public enum RecoveryOutcome
{
    Reopened,
    AlreadyTerminal,
    Failed,
}

public sealed record RecoveryResult(RecoveryOutcome Outcome, IssueStatus? VerifiedStatus = null);

public enum PushOutcome
{
    NotRequired,
    Pushed,
    Failed,
}

public sealed class TicketRecovery(
    Beads beads,
    TextWriter log,
    int maximumAttempts = 3,
    TimeSpan? retryDelay = null,
    TimeSpan? totalTimeout = null)
{
    private readonly TimeSpan delay = retryDelay ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan deadline = totalTimeout ?? TimeSpan.FromSeconds(15);

    public Task<RecoveryResult> ReopenKnownClaimAsync(
        ValidatedAgent agent,
        string issueId,
        string note,
        CancellationToken cancellationToken) =>
        ReopenAndVerifyAsync(agent, issueId, note, cancellationToken);

    public Task<RecoveryResult> ReopenIfStillInProgressAsync(
        ValidatedAgent agent,
        string issueId,
        string note,
        CancellationToken cancellationToken) =>
        ReopenAndVerifyAsync(agent, issueId, note, cancellationToken);

    private async Task<RecoveryResult> ReopenAndVerifyAsync(
        ValidatedAgent agent,
        string issueId,
        string note,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(deadline);
        try
        {
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                try
                {
                    var current = await beads.GetIssueAsync(
                        agent.WorkspacePath, agent.Name, issueId, budget.Token);
                    if (current is { Status: IssueStatus.Open or IssueStatus.Closed or IssueStatus.Blocked })
                    {
                        return new RecoveryResult(RecoveryOutcome.AlreadyTerminal, current.Status);
                    }

                    if (current is null || current.Status is IssueStatus.Unknown)
                    {
                        throw new BeadsException("issue is missing or has an unknown status");
                    }

                    var reopen = await beads.ReopenAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        issueId,
                        note,
                        budget.Token);
                    if (!reopen.Succeeded)
                    {
                        await WarnAsync(agent.Name,
                            $"failed to reopen {issueId} (attempt {attempt}/{maximumAttempts}): {Beads.FailureDetail(reopen)}");
                    }
                    else
                    {
                        var verified = await beads.GetIssueAsync(
                            agent.WorkspacePath, agent.Name, issueId, budget.Token);
                        if (verified is { Status: IssueStatus.Open })
                        {
                            return new RecoveryResult(RecoveryOutcome.Reopened, IssueStatus.Open);
                        }

                        if (verified is { Status: IssueStatus.Closed or IssueStatus.Blocked })
                        {
                            return new RecoveryResult(RecoveryOutcome.AlreadyTerminal, verified.Status);
                        }

                        await WarnAsync(agent.Name,
                            $"could not verify that {issueId} reopened (attempt {attempt}/{maximumAttempts})");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await WarnAsync(agent.Name,
                        $"could not reopen and verify {issueId} (attempt {attempt}/{maximumAttempts}): {exception.Message}");
                }

                if (attempt < maximumAttempts)
                {
                    await Task.Delay(delay, budget.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WarnAsync(agent.Name, $"recovery deadline expired while reopening {issueId}");
        }

        return new RecoveryResult(RecoveryOutcome.Failed);
    }

    public async Task<PushOutcome> PushWithRetryAsync(
        ValidatedAgent agent,
        CancellationToken cancellationToken)
    {
        if (!agent.HasRemote)
        {
            return PushOutcome.NotRequired;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(deadline);
        try
        {
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                try
                {
                    var push = await beads.PushAsync(agent.WorkspacePath, agent.Name, budget.Token);
                    if (push.Succeeded)
                    {
                        return PushOutcome.Pushed;
                    }

                    await WarnAsync(agent.Name,
                        $"Beads push failed (attempt {attempt}/{maximumAttempts}): {Beads.FailureDetail(push)}");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await WarnAsync(agent.Name,
                        $"Beads push failed (attempt {attempt}/{maximumAttempts}): {exception.Message}");
                }

                if (attempt < maximumAttempts)
                {
                    await Task.Delay(delay, budget.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WarnAsync(agent.Name, "recovery deadline expired while pushing Beads data");
        }

        return PushOutcome.Failed;
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WarningAsync(agentName, message);
}

public sealed class TicketSupervisor(
    Beads beads,
    IAgentHost agentHost,
    TicketRecovery recovery,
    TextWriter log,
    TimeSpan? pollingInterval = null,
    int maximumInvalidPolls = 3,
    RunSummary? summary = null,
    TimeSpan? ticketTimeout = null,
    DesktopNotifier? notifier = null)
{
    private readonly TimeSpan interval = pollingInterval ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan? runtimeLimit = ticketTimeout;
    private static readonly TimeSpan FinalizationDeadline = TimeSpan.FromSeconds(30);

    public async Task SuperviseAsync(
        ValidatedAgent agent,
        BeadsIssue claimedIssue,
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        var shutdownHandled = false;
        var cleanupHandled = false;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var consecutiveInvalidPolls = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(agent.WorkspacePath))
                {
                    throw new StartupInvariantException(
                        $"[{agent.Name}] workspace disappeared: '{agent.WorkspacePath}'");
                }

                if (runtimeLimit is { } limit
                    && Stopwatch.GetElapsedTime(startedAt) >= limit)
                {
                    await WarnAsync(
                        agent.Name,
                        $"Ticket {claimedIssue.Id} exceeded its {FormatDuration(limit)} runtime limit; stopping the agent CLI and reopening the ticket");
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{claimedIssue.Id} • runtime limit reached; reopening ticket");

                    using var finalization = new CancellationTokenSource(FinalizationDeadline);
                    await CleanupRunAsync(agent.Name, run, finalization.Token);
                    cleanupHandled = true;
                    RecoveryResult recoveryResult;
                    try
                    {
                        recoveryResult = await recovery.ReopenIfStillInProgressAsync(
                            agent,
                            claimedIssue.Id,
                            $"Abacus stopped agent {agent.Name} after the {FormatDuration(limit)} ticket runtime limit elapsed",
                            finalization.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        await HaltAsync(
                            agent.Name,
                            $"Cleanup deadline expired while recovering timed-out ticket {claimedIssue.Id}");
                        throw;
                    }

                    await RequireRecoveryAsync(agent, claimedIssue, recoveryResult);
                    return;
                }

                BeadsIssue? current = null;
                var statusReadable = true;
                try
                {
                    var wasDegraded = consecutiveInvalidPolls > 0;
                    current = await beads.GetIssueAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        claimedIssue.Id,
                        cancellationToken);
                    if (current is null || current.Status is IssueStatus.Unknown)
                    {
                        throw new BeadsException("issue is missing or has an unknown status");
                    }

                    consecutiveInvalidPolls = 0;
                    if (wasDegraded && current.Status is IssueStatus.InProgress)
                    {
                        await log.SetAgentAsync(
                            agent.Name,
                            AgentActivity.Working,
                            $"{claimedIssue.Id} • ticket status readable again; agent remains supervised");
                    }
                }
                catch (Exception exception) when (
                    exception is BeadsException or CommandStartException or CommandTimeoutException)
                {
                    consecutiveInvalidPolls++;
                    statusReadable = false;
                    if (consecutiveInvalidPolls <= maximumInvalidPolls)
                    {
                        var suffix = consecutiveInvalidPolls == maximumInvalidPolls
                            ? "; continuing supervision and suppressing repeated warnings"
                            : string.Empty;
                        await WarnAsync(agent.Name,
                            $"could not poll {claimedIssue.Id} ({consecutiveInvalidPolls}/{maximumInvalidPolls}): {exception.Message}{suffix}");
                    }

                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{claimedIssue.Id} • ticket status unavailable; agent remains supervised");
                }

                if (statusReadable
                    && current!.Status is IssueStatus.Closed or IssueStatus.Open or IssueStatus.Blocked)
                {
                    RecordTerminalOutcome(agent.Name, current);
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Finalizing,
                        $"{claimedIssue.Id} • ticket is {StatusName(current.Status)}");
                    return;
                }

                var exitCode = run.TryReadExitCode();
                if (exitCode is not null
                    || run.HasExited
                    || !await agentHost.IsRunningAsync(run, cancellationToken))
                {
                    await log.SetLastExitCodeAsync(agent.Name, exitCode);
                    // Read once more after observing process exit. The agent's final ticket
                    // update may have raced with the observed process exit.
                    var final = await ReadAfterExitAsync(agent, claimedIssue.Id, cancellationToken);
                    if (final is { Status: IssueStatus.Closed or IssueStatus.Open or IssueStatus.Blocked })
                    {
                        RecordTerminalOutcome(agent.Name, final);
                        await log.SetAgentAsync(
                            agent.Name,
                            AgentActivity.Finalizing,
                            $"{claimedIssue.Id} • ticket is {StatusName(final.Status)}");
                        return;
                    }

                    if (final is { Status: IssueStatus.InProgress })
                    {
                        var exitDescription = exitCode?.ToString() ?? "unknown";
                        await WarnAsync(agent.Name,
                            $"Agent CLI exited with code {exitDescription} while {claimedIssue.Id} remained in_progress");
                        await log.SetAgentAsync(
                            agent.Name,
                            AgentActivity.Recovering,
                            $"{claimedIssue.Id} • agent CLI exited; reopening ticket");
                        var recoveryResult = await recovery.ReopenIfStillInProgressAsync(
                            agent,
                            claimedIssue.Id,
                            $"Abacus agent {agent.Name} exited with process code {exitDescription} before updating the ticket",
                            CancellationToken.None);
                        await RequireRecoveryAsync(agent, claimedIssue, recoveryResult);
                    }
                    else
                    {
                        await HaltAsync(
                            agent.Name,
                            $"Agent CLI exited while {claimedIssue.Id} status remained unreadable; no more work will be claimed");
                    }

                    return;
                }

                var nextPollDelay = interval;
                if (runtimeLimit is { } remainingLimit)
                {
                    var remaining = remainingLimit - Stopwatch.GetElapsedTime(startedAt);
                    if (remaining < nextPollDelay)
                    {
                        nextPollDelay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                    }
                }

                await Task.Delay(nextPollDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            shutdownHandled = true;
            summary?.Record(
                agent.Name,
                TicketOutcome.Interrupted,
                claimedIssue.Id,
                claimedIssue.Title);
            using var finalization = new CancellationTokenSource(FinalizationDeadline);
            await CleanupRunAsync(agent.Name, run, finalization.Token);
            RecoveryResult recoveryResult;
            try
            {
                recoveryResult = await recovery.ReopenIfStillInProgressAsync(
                    agent,
                    claimedIssue.Id,
                    $"Abacus shut down while {agent.Name} was working on this ticket",
                    finalization.Token);
            }
            catch (OperationCanceledException)
            {
                await HaltAsync(agent.Name, $"Cleanup deadline expired while recovering {claimedIssue.Id}");
                throw;
            }

            await RequireRecoveryAsync(agent, claimedIssue, recoveryResult, recordOutcome: false);
            await RequirePushAsync(agent, finalization.Token);
            throw;
        }
        finally
        {
            if (!shutdownHandled)
            {
                using var finalization = new CancellationTokenSource(FinalizationDeadline);
                if (!cleanupHandled)
                {
                    await CleanupRunAsync(agent.Name, run, finalization.Token);
                }

                await RequirePushAsync(agent, finalization.Token);
            }
        }
    }

    private async Task<BeadsIssue?> ReadAfterExitAsync(
        ValidatedAgent agent,
        string issueId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumInvalidPolls; attempt++)
        {
            try
            {
                var issue = await beads.GetIssueAsync(agent.WorkspacePath, agent.Name, issueId, cancellationToken);
                if (issue is not null && issue.Status is not IssueStatus.Unknown)
                {
                    return issue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WarnAsync(agent.Name,
                    $"could not resolve exit/status race for {issueId}: {exception.Message}");
            }

            if (attempt < maximumInvalidPolls)
            {
                await Task.Delay(interval, cancellationToken);
            }
        }

        return null;
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WarningAsync(agentName, message);

    private static string StatusName(IssueStatus status) => status switch
    {
        IssueStatus.InProgress => "in progress",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.###}h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:0.###}m";
        }

        return $"{duration.TotalSeconds:0.###}s";
    }

    private void RecordTerminalOutcome(string agentName, BeadsIssue issue)
    {
        var outcome = issue.Status switch
        {
            IssueStatus.Closed => TicketOutcome.Closed,
            IssueStatus.Open => TicketOutcome.Reopened,
            IssueStatus.Blocked => TicketOutcome.Blocked,
            _ => (TicketOutcome?)null,
        };
        if (outcome is not null)
        {
            summary?.Record(agentName, outcome.Value, issue.Id, issue.Title);
        }
    }

    private async Task CleanupRunAsync(
        string agentName,
        IAgentRun run,
        CancellationToken cancellationToken)
    {
        try
        {
            await agentHost.StopAndCleanupAsync(run, cancellationToken);
        }
        catch (Exception exception)
        {
            await HaltAsync(
                agentName,
                $"Could not verify cleanup of {run.Location}: {exception.Message}; no more work will be claimed");
        }
    }

    private async Task RequireRecoveryAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        RecoveryResult result,
        bool recordOutcome = true)
    {
        if (result.Outcome is RecoveryOutcome.Failed)
        {
            await HaltAsync(
                agent.Name,
                $"Could not reopen and verify {issue.Id}; it may remain claimed and no more work will be claimed");
        }

        if (!recordOutcome)
        {
            return;
        }

        if (result.Outcome is RecoveryOutcome.Reopened)
        {
            summary?.Record(agent.Name, TicketOutcome.Reopened, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is { } status)
        {
            RecordTerminalOutcome(agent.Name, issue with { Status = status });
        }
    }

    private async Task RequirePushAsync(
        ValidatedAgent agent,
        CancellationToken cancellationToken)
    {
        PushOutcome outcome;
        try
        {
            outcome = await recovery.PushWithRetryAsync(agent, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await HaltAsync(agent.Name, "Cleanup deadline expired while pushing Beads data");
            throw;
        }

        if (outcome is PushOutcome.Failed)
        {
            await HaltAsync(
                agent.Name,
                "All Beads push attempts failed; no more work will be claimed");
        }
    }

    private async Task HaltAsync(string agentName, string message)
    {
        await WarnAsync(agentName, message);
        await log.SetPersistentAlertAsync(agentName, message);
        notifier?.PersistentAlert(agentName, message);
        throw new AgentHaltedException($"[{agentName}] {message}");
    }
}

public sealed class AgentHaltedException(string message) : Exception(message);
