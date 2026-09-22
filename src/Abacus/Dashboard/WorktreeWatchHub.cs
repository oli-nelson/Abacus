using System.Text.Json;
using System.Threading.Channels;

namespace Abacus.Dashboard;

internal sealed record WorktreeUpdate(WorktreeDiff? Diff, bool Stale, string? Error);
internal sealed class WorktreeWatch(Channel<byte[]> channel, Action close) : IDisposable
{
    public ChannelReader<byte[]> Reader => channel.Reader;
    public void Dispose() => close();
}

// One latest-state slot per subscriber, one read per visible worktree, no client timers.
internal sealed class WorktreeWatchHub
{
    private sealed class Topic(string id, Func<CancellationToken, Task<WorktreeDiff>> read)
    {
        public string Id { get; } = id;
        public Func<CancellationToken, Task<WorktreeDiff>> Read { get; } = read;
        public HashSet<Channel<byte[]>> Clients { get; } = [];
        public Task? Pending;
        public byte[]? Body;
        public string? Key;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Topic> topics = [];
    private int clients;
    private bool completed;
    private long reads;
    public long SourceReads => Interlocked.Read(ref reads);
    public int TopicCount { get { lock (sync) return topics.Count; } }

    public WorktreeWatch Subscribe(string id, Func<CancellationToken, Task<WorktreeDiff>> read)
    {
        lock (sync)
        {
            if (completed) throw new InvalidOperationException("Worktree observation is stopping.");
            if (clients >= 64 || (!topics.ContainsKey(id) && topics.Count >= 8))
                throw new InvalidOperationException("Worktree watch capacity reached.");
            if (!topics.TryGetValue(id, out var topic)) topics.Add(id, topic = new(id, read));
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
            topic.Clients.Add(channel); clients++;
            if (topic.Body is not null) channel.Writer.TryWrite(topic.Body);
            else Start(topic, default);
            return new(channel, () =>
            {
                lock (sync)
                {
                    if (!topic.Clients.Remove(channel)) return;
                    clients--; channel.Writer.TryComplete();
                    if (topic.Clients.Count == 0 && topics.GetValueOrDefault(id) == topic) topics.Remove(id);
                }
            });
        }
    }

    public void Complete()
    {
        lock (sync)
        {
            if (completed) return;
            completed = true;
            foreach (var topic in topics.Values)
            {
                foreach (var channel in topic.Clients) channel.Writer.TryComplete();
                topic.Clients.Clear();
            }
            topics.Clear(); clients = 0;
        }
    }

    public Task ReconcileAsync(CancellationToken token)
    {
        lock (sync) return Task.WhenAll(topics.Values.Select(topic => Start(topic, token)).ToArray());
    }

    private Task Start(Topic topic, CancellationToken token)
    {
        if (topic.Pending is null || topic.Pending.IsCompleted) topic.Pending = ReadAsync(topic, token);
        return topic.Pending;
    }

    private async Task ReadAsync(Topic topic, CancellationToken token)
    {
        await Task.Yield();
        lock (sync) if (completed || topics.GetValueOrDefault(topic.Id) != topic) return;
        WorktreeUpdate update;
        try { Interlocked.Increment(ref reads); update = new(await topic.Read(token), false, null); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or
            UnauthorizedAccessException or CommandStartException or CommandTimeoutException or CommandOutputLimitException or OperationCanceledException)
        { update = new(null, true, "Worktree content unavailable, changed during reading, or exceeded its limits. Waiting for reconciliation."); }
        var key = update.Stale ? "stale" : update.Diff!.Revision;
        lock (sync)
        {
            if (topics.GetValueOrDefault(topic.Id) != topic || topic.Key == key) return;
            topic.Key = key;
            topic.Body = JsonSerializer.SerializeToUtf8Bytes(update, DashboardStream.Json);
            foreach (var channel in topic.Clients) channel.Writer.TryWrite(topic.Body);
        }
    }
}
