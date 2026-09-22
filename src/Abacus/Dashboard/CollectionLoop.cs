using System.Threading.Channels;

namespace Abacus.Dashboard;

/// <summary>One host-owned loop per repository, independent of browser count.</summary>
internal sealed class CollectionLoop(
    IssueCollector collector,
    TimeSpan pollInterval,
    Action<IssueSourceState> publish,
    TimeSpan? debounce = null,
    Func<CancellationToken, Task>? refreshDetails = null)
{
    private readonly Channel<bool> hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false,
    });
    private int started;

    // Watchers and successful local mutations only hint; the timed export remains
    // authoritative, including remote server writes with no filesystem event.
    public void MarkDirty() => hints.Writer.TryWrite(true);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (pollInterval < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("Collector loop already started.");
        IssueSourceState? previous = null;
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = await collector.RefreshAsync(cancellationToken);
            if (previous is null || !ReferenceEquals(previous.View, next.View) || previous.Stale != next.Stale || previous.Error != next.Error)
                publish(next);
            previous = next;
            if (refreshDetails is not null) await refreshDetails(cancellationToken);
            failures = next.Stale ? Math.Min(failures + 1, 4) : 0;
            var delay = pollInterval >= TimeSpan.FromMinutes(1) ? pollInterval
                : TimeSpan.FromMilliseconds(Math.Min(60_000, pollInterval.TotalMilliseconds * Math.Pow(2, failures)));
            // A hint arriving during Refresh remains in the channel: no lost
            // dirty-again signal and no overlapping source refreshes.
            var remaining = delay;
            while (remaining > TimeSpan.Zero)
            {
                // Timer APIs have narrower duration limits than TimeSpan. Slice
                // long operator intervals without performing extra source probes.
                var slice = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(slice);
                try
                {
                    await hints.Reader.ReadAsync(wait.Token);
                    await Task.Delay(debounce ?? TimeSpan.FromMilliseconds(250), cancellationToken);
                    while (hints.Reader.TryRead(out _)) { }
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { remaining -= slice; }
            }
        }
    }
}
