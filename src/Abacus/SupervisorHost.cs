namespace Abacus;

/// <summary>Both supervisor roles share the controller checkout; never overlap their harnesses.</summary>
public sealed class SupervisorHost(IAgentHost inner, AgentRunRegistry runs) : IAgentHost
{
    public Func<string, Task>? WaitingForCheckoutAsync { get; init; }
    public Func<string, CancellationToken, Task>? BeforeStartAsync { get; init; }
    private readonly SemaphoreSlim checkout = new(1, 1);
    private sealed record Run(IAgentRun Inner, string Name) : IAgentRun
    {
        public string Location => Inner.Location;
        public bool HasExited => Inner.HasExited;
        public int? TryReadExitCode() => Inner.TryReadExitCode();
    }

    public async Task<IAgentRun> StartAgentAsync(ValidatedAgent agent, BeadsIssue issue,
        string model, string effort, string? serverUrl, CancellationToken cancellationToken,
        IReadOnlyList<string>? extraArguments = null)
    {
        if (!await checkout.WaitAsync(0, cancellationToken))
        {
            if (WaitingForCheckoutAsync is not null) await WaitingForCheckoutAsync(agent.Name);
            await checkout.WaitAsync(cancellationToken);
        }
        try
        {
            if (BeforeStartAsync is not null) await BeforeStartAsync(agent.Name, cancellationToken);
            runs.MarkRunning(agent.Name);
            return new Run(await inner.StartAgentAsync(agent, issue, model, effort, serverUrl,
                cancellationToken, extraArguments), agent.Name);
        }
        catch
        {
            runs.MarkStopped(agent.Name);
            checkout.Release();
            throw;
        }
    }

    public Task<bool> IsRunningAsync(IAgentRun run, CancellationToken token) =>
        inner.IsRunningAsync(((Run)run).Inner, token);

    public async Task StopAndCleanupAsync(IAgentRun run, CancellationToken token)
    {
        var hosted = (Run)run;
        // A failed cleanup deliberately retains ownership; the caller must abort the controller.
        await inner.StopAndCleanupAsync(hosted.Inner, token);
        runs.MarkStopped(hosted.Name);
        checkout.Release();
    }
}
