namespace Abacus;

public sealed class TicketRecovery(
    Beads beads,
    TextWriter log,
    int maximumAttempts = 3,
    TimeSpan? retryDelay = null)
{
    private readonly TimeSpan delay = retryDelay ?? TimeSpan.FromSeconds(1);

    public async Task ReopenKnownClaimAsync(
        ValidatedAgent agent,
        string issueId,
        string note,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                var reopen = await beads.ReopenAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    issueId,
                    note,
                    cancellationToken);
                if (reopen.Succeeded)
                {
                    await PushWithRetryAsync(agent, cancellationToken);
                    return;
                }

                await WarnAsync(agent.Name,
                    $"failed to reopen {issueId} (attempt {attempt}/{maximumAttempts}): {Beads.FailureDetail(reopen)}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WarnAsync(agent.Name,
                    $"failed to reopen {issueId} (attempt {attempt}/{maximumAttempts}): {exception.Message}");
            }

            if (attempt < maximumAttempts)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }

        await PushWithRetryAsync(agent, cancellationToken);
    }

    public async Task ReopenIfStillInProgressAsync(
        ValidatedAgent agent,
        string issueId,
        string note,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                var current = await beads.GetIssueAsync(
                    agent.WorkspacePath, agent.Name, issueId, cancellationToken);
                if (current is not { Status: IssueStatus.InProgress })
                {
                    return;
                }

                var reopen = await beads.ReopenAsync(
                    agent.WorkspacePath, agent.Name, issueId, note, cancellationToken);
                if (reopen.Succeeded)
                {
                    return;
                }

                await WarnAsync(agent.Name,
                    $"failed to reopen {issueId} (attempt {attempt}/{maximumAttempts}): {Beads.FailureDetail(reopen)}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await WarnAsync(agent.Name,
                    $"could not verify {issueId} before reopening (attempt {attempt}/{maximumAttempts}): {exception.Message}");
            }

            if (attempt < maximumAttempts)
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public async Task PushWithRetryAsync(ValidatedAgent agent, CancellationToken cancellationToken)
    {
        if (!agent.HasRemote)
        {
            return;
        }

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                var push = await beads.PushAsync(agent.WorkspacePath, agent.Name, cancellationToken);
                if (push.Succeeded)
                {
                    return;
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
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WarningAsync(agentName, message);
}

public sealed class TicketSupervisor(
    Beads beads,
    Tmux tmux,
    TicketRecovery recovery,
    TextWriter log,
    TimeSpan? pollingInterval = null,
    int maximumInvalidPolls = 3)
{
    private readonly TimeSpan interval = pollingInterval ?? TimeSpan.FromSeconds(5);

    public async Task SuperviseAsync(
        ValidatedAgent agent,
        BeadsIssue claimedIssue,
        OpenCodeRun run,
        CancellationToken cancellationToken)
    {
        var shutdownHandled = false;
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

                BeadsIssue? current;
                try
                {
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
                }
                catch (Exception exception) when (exception is BeadsException or CommandStartException)
                {
                    consecutiveInvalidPolls++;
                    await WarnAsync(agent.Name,
                        $"could not poll {claimedIssue.Id} ({consecutiveInvalidPolls}/{maximumInvalidPolls}): {exception.Message}");
                    if (consecutiveInvalidPolls >= maximumInvalidPolls)
                    {
                        throw new SupervisionException(
                            $"stopped supervising {claimedIssue.Id} after {maximumInvalidPolls} invalid polls");
                    }

                    await Task.Delay(interval, cancellationToken);
                    continue;
                }

                if (current.Status is IssueStatus.Closed or IssueStatus.Open or IssueStatus.Blocked)
                {
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Finalizing,
                        $"{claimedIssue.Id} • ticket is {StatusName(current.Status)}");
                    return;
                }

                var exitCode = run.TryReadExitCode();
                if (exitCode is not null
                    || File.Exists(run.MarkerPath)
                    || !await tmux.PaneExistsAsync(run, cancellationToken))
                {
                    // Read once more after observing process exit. The agent's final ticket
                    // update may have raced with the exit marker.
                    var final = await ReadAfterExitAsync(agent, claimedIssue.Id, cancellationToken);
                    if (final is { Status: IssueStatus.Closed or IssueStatus.Open or IssueStatus.Blocked })
                    {
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
                            $"OpenCode exited with code {exitDescription} while {claimedIssue.Id} remained in_progress");
                        await log.SetAgentAsync(
                            agent.Name,
                            AgentActivity.Recovering,
                            $"{claimedIssue.Id} • OpenCode exited; reopening ticket");
                        await recovery.ReopenIfStillInProgressAsync(
                            agent,
                            claimedIssue.Id,
                            $"Abacus agent {agent.Name} exited with process code {exitDescription} before updating the ticket",
                            CancellationToken.None);
                    }

                    return;
                }

                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            await CleanupPaneAsync(agent.Name, run);
            await recovery.ReopenIfStillInProgressAsync(
                agent,
                claimedIssue.Id,
                $"Abacus shut down while {agent.Name} was working on this ticket",
                CancellationToken.None);
            await recovery.PushWithRetryAsync(agent, CancellationToken.None);
            shutdownHandled = true;
            throw;
        }
        finally
        {
            if (!shutdownHandled)
            {
                await CleanupPaneAsync(agent.Name, run);
                await recovery.PushWithRetryAsync(agent, CancellationToken.None);
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
            catch (BeadsException exception)
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

    private async Task CleanupPaneAsync(string agentName, OpenCodeRun run)
    {
        try
        {
            await tmux.StopAndCleanupAsync(run, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await WarnAsync(agentName, $"could not completely clean pane {run.PaneId}: {exception.Message}");
        }
    }
}

public sealed class SupervisionException(string message) : Exception(message);
