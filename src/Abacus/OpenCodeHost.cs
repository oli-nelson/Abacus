namespace Abacus;

public interface IOpenCodeRun
{
    string Location { get; }
    bool HasExited { get; }
    int? TryReadExitCode();
}

public interface IOpenCodeHost
{
    Task<IOpenCodeRun> StartOpenCodeAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string? serverUrl,
        CancellationToken cancellationToken);

    Task<bool> IsRunningAsync(IOpenCodeRun run, CancellationToken cancellationToken);
    Task StopAndCleanupAsync(IOpenCodeRun run, CancellationToken cancellationToken);
}
