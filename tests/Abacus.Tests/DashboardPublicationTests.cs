using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardPublicationTests
{
    [Fact]
    public async Task UnrelatedSourceUpdatesReuseIssueBytesAndBuildAggregateOnlyOnDemand()
    {
        var data = "{\"id\":\"b\",\"title\":\"B\"}\n{\"id\":\"a\",\"title\":\"A\"}";
        var collector = new IssueCollector(_ => Task.FromResult(data), default);
        var stream = new DashboardStream();
        stream.Publish(await collector.RefreshAsync());
        Assert.Equal(2, stream.SerializedIssues);
        Assert.Equal(0, stream.SnapshotBuilds);
        var initial = stream.Snapshot();
        using var events = stream.Subscribe(initial.ETag.Trim('"'));
        for (var revision = 1; revision <= 10; revision++)
        {
            var view = new GitView(revision, null, [], null, false, null, false, null, null);
            stream.PublishGit(new(view, JsonSerializer.SerializeToUtf8Bytes(view, DashboardStream.Json)));
            stream.PublishHistory(new("history-" + revision, false, null));
        }
        Assert.Equal(2, stream.SerializedIssues);
        Assert.Equal(1, stream.SnapshotBuilds);
        var observed = new List<DashboardEvent>();
        while (events.Reader.TryRead(out var item)) observed.Add(item);
        Assert.Equal(20, observed.Count);
        Assert.All(observed, item => Assert.True(item.Kind is "git" or "history"));
        var latest = stream.Snapshot();
        Assert.Equal(2, stream.SnapshotBuilds);
        Assert.Equal(observed[^1].Id, latest.ETag.Trim('"'));
        using var json = JsonDocument.Parse(latest.Body);
        Assert.Equal(10, json.RootElement.GetProperty("git").GetProperty("revision").GetInt64());
        Assert.Equal("history-10", json.RootElement.GetProperty("history").GetProperty("revision").GetString());
        Assert.Equal("a", json.RootElement.GetProperty("issues")[0].GetProperty("id").GetString());
        var clients = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(stream.Snapshot)));
        Assert.All(clients, client => Assert.Same(latest.Body, client.Body));
        Assert.Equal(2, stream.SnapshotBuilds);

        data = "{\"id\":\"b\",\"title\":\"changed <script> literal\"}\n{\"id\":\"a\",\"title\":\"A\"}";
        stream.Publish(await collector.RefreshAsync());
        Assert.Equal(3, stream.SerializedIssues); // only b was serialized again
        Assert.Equal(2, stream.SnapshotBuilds);
        using var edited = JsonDocument.Parse(stream.Snapshot().Body);
        Assert.Equal("changed <script> literal", edited.RootElement.GetProperty("issues")[1].GetProperty("title").GetString());
        data = "";
        stream.Publish(await collector.RefreshAsync());
        using var empty = JsonDocument.Parse(stream.Snapshot().Body);
        Assert.Empty(empty.RootElement.GetProperty("issues").EnumerateArray());
        Assert.Equal(3, stream.SerializedIssues);
    }

    [Fact]
    public async Task HealthUpdatesAndNoOpPublicationsDoNotReserializeIssues()
    {
        var collector = new IssueCollector(_ => Task.FromResult("{\"id\":\"a\"}"), default);
        var state = await collector.RefreshAsync();
        var stream = new DashboardStream();
        stream.Publish(state);
        var initial = stream.Snapshot();
        using var events = stream.Subscribe(initial.ETag.Trim('"'));
        for (var i = 0; i < 10; i++) stream.Publish(state with { LastSuccess = DateTimeOffset.UtcNow });
        Assert.False(events.Reader.TryRead(out _));
        Assert.Same(initial.Body, stream.Snapshot().Body);
        stream.Publish(state with { Stale = true, Error = "Unavailable" });
        Assert.Equal(1, stream.SerializedIssues);
        Assert.Equal(1, stream.SnapshotBuilds);
        using var stale = JsonDocument.Parse(stream.Snapshot().Body);
        Assert.True(stale.RootElement.GetProperty("beads").GetProperty("stale").GetBoolean());
        Assert.Equal(1, stale.RootElement.GetProperty("issues").GetArrayLength());
    }
}
