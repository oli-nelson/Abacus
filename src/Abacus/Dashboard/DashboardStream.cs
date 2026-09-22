using System.Text.Json;
using System.Threading.Channels;

namespace Abacus.Dashboard;

internal sealed record DashboardEvent(string Id, string Kind, byte[] Data);
internal sealed class DashboardSubscription(Channel<DashboardEvent> channel, Action dispose) : IDisposable
{
    public ChannelReader<DashboardEvent> Reader => channel.Reader;
    public void Dispose() => dispose();
}

/// <summary>Atomically pairs cached snapshots with a bounded replay cursor.</summary>
internal sealed class DashboardStream
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly Queue<DashboardEvent> replay = new();
    private readonly HashSet<Channel<DashboardEvent>> clients = [];
    private readonly string instance = Guid.NewGuid().ToString("N");
    private long sequence;
    private long replayBytes;
    private IssueSourceState? current;
    private byte[] snapshot = [];
    private bool snapshotDirty;
    private readonly SortedDictionary<string, byte[]> issueBodies = new(StringComparer.Ordinal);
    private byte[] healthBody = "null"u8.ToArray();
    private byte[] historyBody = "null"u8.ToArray();
    internal int SerializedIssues { get; private set; }
    internal int SnapshotBuilds { get; private set; }
    private GitPublication? git;
    private HistorySourceState? history;
    private RuntimePublication? runtime;
    private string Cursor => $"{instance}:{sequence}";

    public void Publish(IssueSourceState state)
    {
        lock (sync)
        {
            if (current is not null && ReferenceEquals(current.View, state.View) && current.Stale == state.Stale && current.Error == state.Error) return;
            var changedView = current is null || !ReferenceEquals(current.View, state.View);
            if (changedView)
            {
                var ids = current is null ? state.View.Issues.Keys : state.View.ChangedIds;
                foreach (var id in ids)
                {
                    issueBodies[id] = JsonSerializer.SerializeToUtf8Bytes(state.View.Issues[id], Json);
                    SerializedIssues++;
                }
                foreach (var id in state.View.RemovedIds) issueBodies.Remove(id);
            }
            current = state;
            sequence++;
            var health = new { state.Stale, state.Error, state.LastSuccess };
            healthBody = JsonSerializer.SerializeToUtf8Bytes(health, Json);
            snapshotDirty = true;
            Emit(new DashboardEvent(Cursor, "change", JsonSerializer.SerializeToUtf8Bytes(new
            {
                revision = state.View.Revision, beads = health,
                upserts = changedView ? state.View.ChangedIds.Select(id => state.View.Issues[id]).ToArray() : [],
                removals = changedView ? state.View.RemovedIds.ToArray() : [],
            }, Json)));
        }
    }

    public void PublishGit(GitPublication publication)
    {
        lock (sync)
        {
            if (ReferenceEquals(git, publication)) return;
            git = publication;
            sequence++;
            snapshotDirty = true;
            Emit(new DashboardEvent(Cursor, "git", publication.Body));
        }
    }

    public void PublishHistory(HistorySourceState state)
    {
        lock (sync)
        {
            if (history == state) return;
            history = state;
            sequence++;
            snapshotDirty = true;
            historyBody = JsonSerializer.SerializeToUtf8Bytes(state, Json);
            Emit(new DashboardEvent(Cursor, "history", historyBody));
        }
    }

    public void PublishRuntime(RuntimePublication publication)
    {
        lock (sync)
        {
            if (ReferenceEquals(runtime, publication)) return;
            runtime = publication;
            sequence++;
            snapshotDirty = true;
            Emit(new DashboardEvent(Cursor, "runtime", publication.Body));
        }
    }

    private void RebuildSnapshot()
    {
        if (current is null) return;
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("instance", instance);
            writer.WriteString("cursor", Cursor);
            writer.WriteNumber("revision", current.View.Revision);
            writer.WritePropertyName("beads"); writer.WriteRawValue(healthBody, skipInputValidation: true);
            writer.WriteStartArray("issues");
            foreach (var body in issueBodies.Values) writer.WriteRawValue(body, skipInputValidation: true);
            writer.WriteEndArray();
            writer.WritePropertyName("git"); writer.WriteRawValue(git?.Body ?? "null"u8.ToArray(), skipInputValidation: true);
            writer.WritePropertyName("runtime"); writer.WriteRawValue(runtime?.Body ?? "null"u8.ToArray(), skipInputValidation: true);
            writer.WritePropertyName("history"); writer.WriteRawValue(historyBody, skipInputValidation: true);
            writer.WriteString("coverage", "Current issue snapshot; historical fields are unknown until loaded from recorded sources.");
            writer.WriteEndObject();
        }
        snapshot = bytes.ToArray();
        snapshotDirty = false;
        SnapshotBuilds++;
    }

    public (string ETag, byte[] Body) Snapshot()
    {
        lock (sync)
        {
            // SSE-only clients need deltas, not a full snapshot allocation per
            // update. A reconnect/GET materializes the latest coherent view once.
            if (snapshotDirty) RebuildSnapshot();
            return ($"\"{Cursor}\"", snapshot);
        }
    }

    public bool IssuesStale { get { lock (sync) return current is null || current.Stale; } }

    public IssueSummary? Issue(string id)
    {
        lock (sync) return current?.View.Issues.GetValueOrDefault(id);
    }

    public DashboardSubscription Subscribe(string? after)
    {
        lock (sync)
        {
            if (clients.Count >= 64) throw new InvalidOperationException("Dashboard client limit reached.");
            var channel = Channel.CreateBounded<DashboardEvent>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
            // A client with no cursor or an evicted/restarted cursor must read one
            // fresh snapshot, never silently miss the gap between GET and SSE.
            var history = replay.ToArray();
            var index = Array.FindIndex(history, e => e.Id == after);
            if (after != Cursor && (index < 0 || history.Length - index - 1 > 31))
                channel.Writer.TryWrite(new DashboardEvent(Cursor, "resync", "{}"u8.ToArray()));
            else if (index >= 0)
                foreach (var item in history.Skip(index + 1)) channel.Writer.TryWrite(item);
            clients.Add(channel);
            return new DashboardSubscription(channel, () => { lock (sync) { clients.Remove(channel); channel.Writer.TryComplete(); } });
        }
    }

    public void Complete()
    {
        lock (sync)
        {
            var final = new DashboardEvent(Cursor, "disconnected", "{}"u8.ToArray());
            foreach (var client in clients) { client.Writer.TryWrite(final); client.Writer.TryComplete(); }
            clients.Clear();
        }
    }

    private void Emit(DashboardEvent item)
    {
        replay.Enqueue(item);
        replayBytes += item.Data.Length;
        while (replay.Count > 128 || replayBytes > 8 * 1024 * 1024)
            replayBytes -= replay.Dequeue().Data.Length;
        foreach (var client in clients.ToArray())
        {
            if (client.Writer.TryWrite(item)) continue;
            // Closing forces reconnection/replay; no unbounded browser backlog.
            client.Writer.TryComplete();
            clients.Remove(client);
        }
    }
}
