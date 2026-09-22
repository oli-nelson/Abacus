using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardActivityTests
{
    private static IssueSummary Issue()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("{\"id\":\"web-a\",\"title\":\"A\"}"));
        return projection.Current.Issues["web-a"];
    }
    private static IssueHistory History(HistoryKey key) => new(key.IssueId, key.IssueRevision, key.HistoryRevision,
        Enumerable.Range(0, 5).Select(i => new IssueVersion($"event-{i}", $"commit-{i}",
            DateTimeOffset.UnixEpoch, "committer", $"snapshot-{i}", "open", null)).ToImmutableArray(),
        new(false, false, "Committed snapshots only.", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

    [Fact]
    public async Task TenClientsAndPaginationShareHistoryUntilHistoryOrIssueRevisionChanges()
    {
        var head = "first"; var reads = 0;
        var activity = new IssueActivity(_ => Task.FromResult(head), (key, _) =>
        { Interlocked.Increment(ref reads); return Task.FromResult(History(key)); }, default);
        await activity.RefreshAsync(default);
        var issue = Issue();
        var pages = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => activity.ReadAsync(issue, 2, null, default)));
        Assert.Equal(1, reads); Assert.All(pages, p => Assert.Equal(2, p.Versions.Length));
        var page2 = await activity.ReadAsync(issue, 2, pages[0].Continuation, default);
        Assert.Equal("event-2", page2.Versions[0].Id);
        var last = await activity.ReadAsync(issue, 2, page2.Continuation, default);
        Assert.Single(last.Versions); Assert.Null(last.Continuation); Assert.Equal(1, reads);
        var state = activity.State;
        await activity.RefreshAsync(default);
        Assert.Same(state, activity.State);
        head = "second"; await activity.RefreshAsync(default);
        await Assert.ThrowsAsync<ActivityConflictException>(() => activity.ReadAsync(issue, 2, pages[0].Continuation, default));
        await activity.ReadAsync(issue, 2, null, default); Assert.Equal(2, reads);
        await activity.ReadAsync(issue with { Revision = "updated" }, 2, null, default); Assert.Equal(3, reads);
    }

    [Fact]
    public async Task FailedHistoryRevisionRetainsIdentityButRefusesToPresentFreshHistory()
    {
        var failed = false;
        var activity = new IssueActivity(_ => failed ? throw new InvalidDataException("secret") : Task.FromResult("head"),
            (key, _) => Task.FromResult(History(key)), default);
        await activity.RefreshAsync(default); failed = true; await activity.RefreshAsync(default);
        Assert.True(activity.State.Stale); Assert.Equal("head", activity.State.Revision);
        Assert.DoesNotContain("secret", activity.State.Error);
        await Assert.ThrowsAsync<InvalidDataException>(() => activity.ReadAsync(Issue(), 2, null, default));
        failed = false; await activity.RefreshAsync(default);
        Assert.False(activity.State.Stale);
    }

    [Fact]
    public async Task InFlightRevisionChangeCannotPublishMismatchedHistory()
    {
        var head = "a";
        var pending = new TaskCompletionSource<IssueHistory>();
        HistoryKey? requested = null;
        var activity = new IssueActivity(_ => Task.FromResult(head), (key, _) => { requested = key; return pending.Task; }, default);
        await activity.RefreshAsync(default);
        var read = activity.ReadAsync(Issue(), 2, null, default);
        while (requested is null) await Task.Delay(1);
        head = "b"; await activity.RefreshAsync(default); pending.SetResult(History(requested));
        await Assert.ThrowsAsync<ActivityConflictException>(() => read);
    }

    [Fact]
    public void BranchAndCommitBothIdentifyHistoryAndMalformedStatusIsRejected()
    {
        const string status = "{\"branch\":\"main\",\"commit\":\"ku9rtvg51ra4n33igep0pd9oo3rdad1i\"}";
        Assert.NotEqual(IssueActivity.ParseRevision(status), IssueActivity.ParseRevision(status.Replace("main", "other")));
        Assert.Throws<InvalidDataException>(() => IssueActivity.ParseRevision("{}"));
        Assert.Throws<InvalidDataException>(() => IssueActivity.ParseRevision(status.Replace("ku9r", "!!!!")));
    }

    [Fact]
    public async Task HttpHistoryUsesStrictBoundedPagesAndRejectsStaleContinuations()
    {
        var head = "a"; var reads = 0;
        var activity = new IssueActivity(_ => Task.FromResult(head), (key, _) =>
        { reads++; return Task.FromResult(History(key)); }, default);
        await activity.RefreshAsync(default);
        var collector = new IssueCollector(_ => Task.FromResult("{\"id\":\"web-a\",\"title\":\"A\"}"), default);
        var stream = new DashboardStream();stream.Publish(await collector.RefreshAsync());stream.PublishHistory(activity.State);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));var port = ((IPEndPoint)socket.LocalEndPoint!).Port;socket.Close();
        await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port),
            stream, "fixture", "actor", default, activity: activity);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var page = await http.GetFromJsonAsync<ActivityPage>("/api/v1/issues/web-a/activity?limit=2", DashboardStream.Json);
        Assert.NotNull(page); Assert.Equal(2, page.Versions.Length); Assert.NotNull(page.Continuation);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/v1/issues/web-a/activity?limit=2&after=" + Uri.EscapeDataString(page.Continuation))).StatusCode);
        Assert.Equal(1, reads);
        foreach (var query in new[] { "limit=0", "limit=101", "limit=2&limit=3", "extra=1", "after=bad" })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/issues/web-a/activity?" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/issues/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/issues/missing/activity")).StatusCode);
        head = "b";await activity.RefreshAsync(default);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync("/api/v1/issues/web-a/activity?after=" + Uri.EscapeDataString(page.Continuation))).StatusCode);
    }

    [Fact]
    public async Task IdenticalHistoryProbesDoNotPublishEventsOrRebuildSnapshot()
    {
        var stream = new DashboardStream();
        var collector = new IssueCollector(_ => Task.FromResult("{\"id\":\"a\"}"), default);
        stream.Publish(await collector.RefreshAsync());
        var source = new HistorySourceState("head", false, null);stream.PublishHistory(source);
        var snapshot = stream.Snapshot();
        using var subscription = stream.Subscribe(snapshot.ETag.Trim('"'));
        for (var i = 0; i < 10; i++) stream.PublishHistory(source with { });
        Assert.Same(snapshot.Body, stream.Snapshot().Body);
        Assert.False(subscription.Reader.TryRead(out _));
        stream.PublishHistory(source with { Revision = "next" });
        Assert.True(subscription.Reader.TryRead(out var changed));Assert.Equal("history", changed.Kind);
    }
}
