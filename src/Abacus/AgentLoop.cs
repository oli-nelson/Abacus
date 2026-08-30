namespace Abacus;

public sealed record PreparedClaim(BeadsIssue Issue, string Branch);

public sealed class ClaimCoordinator(
    Beads beads,
    Git git,
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var note = $"Abacus could not prepare the workspace for {agent.Name}: {exception.Message}";
                await WarnAsync(agent.Name, note);
                var reopen = await beads.ReopenAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    issue.Id,
                    note,
                    cancellationToken);
                if (!reopen.Succeeded)
                {
                    await WarnAsync(agent.Name, $"failed to reopen {issue.Id}: {Beads.FailureDetail(reopen)}");
                }

                if (agent.HasRemote)
                {
                    var push = await beads.PushAsync(agent.WorkspacePath, agent.Name, cancellationToken);
                    if (!push.Succeeded)
                    {
                        await WarnAsync(agent.Name, $"failed to push reopened ticket: {Beads.FailureDetail(push)}");
                    }
                }

                await Task.Delay(PollingInterval, cancellationToken);
            }
        }
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WriteLineAsync($"[{agentName}] warning: {message}");
}
