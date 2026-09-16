namespace Abacus;

/// <summary>Crash journal around the existing CLI hosts. Uncertain launches fail closed on restart.</summary>
public sealed class PoolAgentHost(IAgentHost inner) : IAgentHost
{
    private sealed record Run(IAgentRun Inner, PoolAssignment Assignment, string ExecutionId) : IAgentRun
    {
        public string Location => Inner.Location;
        public bool HasExited => Inner.HasExited;
        public int? TryReadExitCode() => Inner.TryReadExitCode();
    }

    public async Task<IAgentRun> StartAgentAsync(ValidatedAgent agent, BeadsIssue issue, string model,
        string effort, string? serverUrl, CancellationToken cancellationToken,
        IReadOnlyList<string>? extraArguments = null)
    {
        var assignment = agent.PoolAssignment;
        if (assignment is null)
            return await inner.StartAgentAsync(agent, issue, model, effort, serverUrl, cancellationToken, extraArguments);
        var executionId = assignment.Launching();
        // Even a launch exception can leave a child alive. Never clear uncertainty in the catch path.
        var run = await inner.StartAgentAsync(agent, issue, model, effort, serverUrl, cancellationToken, extraArguments);
        assignment.Running(executionId, run.Location);
        return new Run(run, assignment, executionId);
    }

    public Task<bool> IsRunningAsync(IAgentRun run, CancellationToken cancellationToken) =>
        inner.IsRunningAsync(run is Run leased ? leased.Inner : run, cancellationToken);

    public async Task StopAndCleanupAsync(IAgentRun run, CancellationToken cancellationToken)
    {
        if (run is not Run leased) { await inner.StopAndCleanupAsync(run, cancellationToken); return; }
        leased.Assignment.RequireExecution(leased.ExecutionId);
        await inner.StopAndCleanupAsync(leased.Inner, cancellationToken);
        // Tmux's replacement `true` can take a moment to exit. An error/live pane is never success.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!await inner.IsRunningAsync(leased.Inner, cancellationToken))
            {
                leased.Assignment.Stopped(leased.ExecutionId);
                return;
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new SupervisorCleanupException("pool execution did not stop; assignment retained for review");
    }
}
