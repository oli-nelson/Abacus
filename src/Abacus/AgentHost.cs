namespace Abacus;

public interface IAgentRun
{
    string Location { get; }
    bool HasExited { get; }
    int? TryReadExitCode();
}

public interface IAgentHost
{
    Task<IAgentRun> StartAgentAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string effort,
        string? serverUrl,
        CancellationToken cancellationToken);

    Task<bool> IsRunningAsync(IAgentRun run, CancellationToken cancellationToken);
    Task StopAndCleanupAsync(IAgentRun run, CancellationToken cancellationToken);
}
