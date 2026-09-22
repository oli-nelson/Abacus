using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardHistoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Beads", "Dashboard-1.2.2", name));
    private static HistoryKey Key(int limit = 2) => new("web-cuh", "issue-revision", "dolt-revision", limit);

    [Fact]
    public void HistoricalMetadataPreservesPresentFieldsAndDoesNotInventMissingRelations()
    {
        var real = IssueHistoryReader.Parse(Key(), Fixture("history.json"));
        Assert.All(real.Versions, v => { Assert.Equal(1, v.Priority); Assert.Equal("task", v.IssueType); Assert.Null(v.Labels); Assert.Null(v.Assignee); Assert.Null(v.Target); });
        var entries = System.Text.Json.Nodes.JsonNode.Parse(Fixture("history.json"))!.AsArray();
        var issue = entries[0]!["Issue"]!;
        issue["assignee"] = "historical-worker";
        issue["labels"] = new System.Text.Json.Nodes.JsonArray("team:ui", "abacus:needs-user-attention");
        issue["metadata"] = System.Text.Json.Nodes.JsonNode.Parse("{\"abacus_target\":\"release\"}");
        var version = IssueHistoryReader.Parse(Key(), entries.ToJsonString()).Versions[0];
        Assert.Equal("historical-worker", version.Assignee);
        Assert.Equal("release", version.Target);
        Assert.Contains("abacus:needs-user-attention", version.Labels!.Value);
        issue["labels"] = new System.Text.Json.Nodes.JsonArray();
        Assert.Empty(IssueHistoryReader.Parse(Key(), entries.ToJsonString()).Versions[0].Labels!.Value);
        issue["priority"] = "bad";
        Assert.Throws<InvalidDataException>(() => IssueHistoryReader.Parse(Key(), entries.ToJsonString()));
    }

    [Fact]
    public void CurrentTypeAndDeclaredTargetAreSourceFieldsNotExecutionBindings()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("{\"id\":\"x\",\"issue_type\":\"task\",\"metadata\":{\"abacus_target\":\"release\"}}"));
        Assert.Equal("task", projection.Current.Issues["x"].IssueType);
        Assert.Equal("release", projection.Current.Issues["x"].Target);
        projection.Apply(IssueExport.Parse("{\"id\":\"x\"}"));
        Assert.Null(projection.Current.Issues["x"].IssueType);
        Assert.Null(projection.Current.Issues["x"].Target);
    }

    [Fact]
    public void RealCommentAndDependencyChangesNeedNeitherHeadNorUpdatedAtMovement()
    {
        var baseline = IssueExport.Parse(Fixture("export-two.jsonl")).Issues["web-cuh"];
        var comment = IssueExport.Parse(Fixture("export-comment.jsonl")).Issues["web-cuh"];
        var dependency = IssueExport.Parse(Fixture("export-dependency.jsonl")).Issues["web-cuh"];
        Assert.Equal(Fixture("head-two.json"), Fixture("head-comment.json"));
        Assert.Equal(Fixture("head-two.json"), Fixture("head-dependency.json"));
        Assert.Equal(baseline.Source.GetProperty("updated_at").GetString(), comment.Source.GetProperty("updated_at").GetString());
        Assert.Equal(baseline.Source.GetProperty("updated_at").GetString(), dependency.Source.GetProperty("updated_at").GetString());
        Assert.NotEqual(baseline.Revision, comment.Revision);
        Assert.NotEqual(comment.Revision, dependency.Revision);
    }

    [Fact]
    public void RealWorkingSetFixturesChangeWhileHeadRemainsIdentical()
    {
        Assert.Equal(Fixture("head-one.json"), Fixture("head-two.json"));
        var one = IssueExport.Parse(Fixture("export-one.jsonl"));
        var two = IssueExport.Parse(Fixture("export-two.jsonl"));
        Assert.NotEqual(one.Issues["web-cuh"].Revision, two.Issues["web-cuh"].Revision);
        var history = IssueHistoryReader.Parse(Key(), Fixture("history.json"));
        Assert.All(history.Versions, v => Assert.Equal("Camera controls added", v.Notes));
        Assert.True(history.Coverage.LimitReached);
        Assert.False(history.Coverage.Complete);
        Assert.Contains("working-set", history.Coverage.Explanation);
    }

    [Fact]
    public void EmptyHistoryIsNotProofOfCompleteCoverage()
    {
        var history = IssueHistoryReader.Parse(Key(), "[]");
        Assert.Empty(history.Versions);
        Assert.Null(history.Coverage.OldestReturned);
        Assert.False(history.Coverage.Complete);
    }

    [Fact]
    public void HistoryPreservesSourceOrderAndStableIdentity()
    {
        var history = IssueHistoryReader.Parse(Key(), Fixture("history.json"));
        var again = IssueHistoryReader.Parse(Key(), Fixture("history.json"));
        Assert.Equal(history.Versions[0], again.Versions[0]);
        Assert.Equal(TimeSpan.Zero, history.Versions[0].RecordedAt.Offset);
        Assert.StartsWith("beads:web-cuh:", history.Versions[0].Id);
    }

    [Fact]
    public void MalformedOrMismatchedHistoryCannotBecomeEvents()
    {
        Assert.Throws<InvalidDataException>(() => IssueHistoryReader.Parse(Key(1), Fixture("history.json")));
        Assert.Throws<InvalidDataException>(() => IssueHistoryReader.Parse(Key(), Fixture("history.json").Replace("web-cuh", "other")));
        Assert.Throws<InvalidDataException>(() => IssueHistoryReader.Parse(Key(), "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => IssueHistoryReader.Parse(Key(0), "[]"));
        Assert.Throws<ArgumentException>(() => IssueHistoryReader.Parse(Key() with { IssueId = "--help" }, "[]"));
    }

    [Fact]
    public async Task CacheCoalescesAndHonorsRevisionAndLru()
    {
        var cache = new DetailCache<string, object>(2, default);
        var pending = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        Task<object> Read(CancellationToken _) { Interlocked.Increment(ref reads); return pending.Task; }
        var clients = Enumerable.Range(0, 10).Select(_ => cache.GetAsync("revision-1", Read)).ToArray();
        pending.SetResult(new object());
        await Task.WhenAll(clients);
        Assert.Equal(1, reads);
        Assert.Same(await clients[0], await cache.GetAsync("revision-1", Read));
        await cache.GetAsync("revision-2", _ => Task.FromResult(new object()));
        await cache.GetAsync("revision-3", _ => Task.FromResult(new object()));
        await cache.GetAsync("revision-1", Read);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task FailedLoadsRetryAndPendingCapacityDoesNotDuplicateWork()
    {
        var cache = new DetailCache<string, int>(1, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetAsync("a", _ => Task.FromException<int>(new InvalidDataException())));
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetAsync("a", _ => pending.Task);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("b", _ => Task.FromResult(2)));
        using var cancellation = new CancellationTokenSource();
        var abandoned = cache.GetAsync("a", _ => throw new Exception("must not run"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        pending.SetResult(1);
        Assert.Equal(1, await first);
        Assert.Equal(2, await cache.GetAsync("b", _ => Task.FromResult(2)));
    }

    [Fact]
    public async Task DirtyDuringCollectionTriggersFollowupWithoutPublishingIdenticalData()
    {
        var firstRead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var published = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var collector = new IssueCollector(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) return firstRead.Task;
            secondRead.TrySetResult();
            return Task.FromResult("{\"id\":\"a\"}");
        }, lifetime.Token);
        var loop = new CollectionLoop(collector, TimeSpan.FromSeconds(60), _ => Interlocked.Increment(ref published), TimeSpan.Zero);
        var running = loop.RunAsync(lifetime.Token);
        loop.MarkDirty();
        firstRead.SetResult("{\"id\":\"a\"}");
        await secondRead.Task.WaitAsync(lifetime.Token);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(2, calls);
        Assert.Equal(1, published);
    }
}
