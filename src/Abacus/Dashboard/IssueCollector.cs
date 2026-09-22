using System.Diagnostics;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record IssueSourceState(IssueView View, DateTimeOffset? LastSuccess,
    bool Stale, string? Error, long ExportBytes, TimeSpan DetectionTime);

/// <summary>
/// One repository-wide read boundary. Concurrent callers share a probe, while
/// caller cancellation never cancels a probe needed by another browser. The host
/// owns lifetime cancellation and scheduling; requests consume State, not Refresh.
/// </summary>
internal sealed class IssueCollector(Func<CancellationToken, Task<string>> export, CancellationToken lifetime)
{
    private readonly object sync = new();
    private readonly IssueProjection projection = new();
    private Task<IssueSourceState>? pending;
    private IssueSourceState state = new(new IssueProjection().Current, null, true, "Not collected", 0, TimeSpan.Zero);
    public IssueSourceState State => Volatile.Read(ref state);
    public long RebuiltIssues => Interlocked.Read(ref rebuiltIssues);
    private long rebuiltIssues, successfulExports, totalExportBytes, hashTicks;
    public object Metrics => new
    {
        SuccessfulExports = Interlocked.Read(ref successfulExports),
        ExportBytes = Interlocked.Read(ref totalExportBytes),
        HashSeconds = TimeSpan.FromTicks(Interlocked.Read(ref hashTicks)).TotalSeconds,
        RebuiltIssues
    };

    public static IssueCollector ForRepository(CommandRunner runner, string repository, CancellationToken lifetime) =>
        new(async token =>
        {
            var result = await runner.RunAsync(new CommandSpec("bd", ["--readonly", "export"], repository,
                MaxOutputCharacters: IssueExport.MaximumCharacters), token);
            if (!result.Succeeded) throw new InvalidDataException("Beads export failed.");
            return result.StandardOutput;
        }, lifetime);

    public Task<IssueSourceState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (pending is null || pending.IsCompleted) pending = CollectAsync();
            return pending.WaitAsync(cancellationToken);
        }
    }

    private async Task<IssueSourceState> CollectAsync()
    {
        var watch = Stopwatch.StartNew();
        IssueSourceState next;
        try
        {
            var snapshot = IssueExport.Parse(await export(lifetime), out var hashTime);
            Interlocked.Increment(ref successfulExports);
            Interlocked.Add(ref totalExportBytes, snapshot.ExportBytes);
            Interlocked.Add(ref hashTicks, hashTime.Ticks);
            lifetime.ThrowIfCancellationRequested();
            projection.Apply(snapshot);
            Interlocked.Exchange(ref rebuiltIssues, projection.RebuiltIssues);
            next = new(projection.Current, DateTimeOffset.UtcNow, false, null, snapshot.ExportBytes, watch.Elapsed);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or
            CommandStartException or CommandTimeoutException or CommandOutputLimitException)
        {
            // Never forward raw CLI diagnostics (which may include credentials).
            next = State with { Stale = true, Error = "Issue source unavailable or invalid; showing last successful snapshot.", DetectionTime = watch.Elapsed };
        }
        Volatile.Write(ref state, next);
        return next;
    }
}
