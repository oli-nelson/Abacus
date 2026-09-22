using System.Collections.Immutable;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardHistoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "Beads", "Dashboard-1.2.2", name));
    private static HistoryKey Key(int limit = 2) => new("web-cuh", "issue-revision", "dolt-revision", limit);

    private static string Row(string id, string commit, string date, string status, string title = "T", string notes = "n") =>
        $"{{\"id\":\"{id}\",\"commit_hash\":\"{commit}\",\"committer\":\"beads\",\"commit_date\":\"{date}\"," +
        $"\"title\":\"{title}\",\"status\":\"{status}\",\"priority\":1,\"issue_type\":\"task\",\"assignee\":null," +
        $"\"notes\":\"{notes}\",\"declared_target\":null}}";

    [Fact]
    public void RepeatedStatesCollapseToTheEarliestCommitThatRecordsThem()
    {
        // Dolt keeps a row per commit, so an untouched issue repeats itself. Newest first.
        var versions = new[] { ("c", "open"), ("b", "open"), ("a", "open") }
            .Select((x, i) => new IssueVersion($"v{x.Item1}", x.Item1, DateTimeOffset.UnixEpoch.AddHours(3 - i),
                "committer", "T", x.Item2, "n")).ToImmutableArray();
        var collapsed = IssueVersionSequence.Collapse(versions);
        Assert.Single(collapsed);
        // The oldest commit of the run: the earliest time the state is known to have existed.
        Assert.Equal("a", collapsed[0].SourceRevision);
        // A state that is left and returned to is two recorded changes, not one.
        var reopened = new[] { ("d", "open"), ("c", "closed"), ("b", "open"), ("a", "open") }
            .Select((x, i) => new IssueVersion($"v{x.Item1}", x.Item1, DateTimeOffset.UnixEpoch.AddHours(4 - i),
                "committer", "T", x.Item2, "n")).ToImmutableArray();
        Assert.Equal(["d", "c", "a"], IssueVersionSequence.Collapse(reopened).Select(x => x.SourceRevision));
        // Unknown labels never read as equal to a known empty list.
        var unknown = versions[0];
        Assert.Equal(2, IssueVersionSequence.Collapse([unknown, unknown with { Labels = [] }]).Length);
    }

    [Fact]
    public void ProjectHistoryReadsEveryIssueAtOnceAndRefusesMalformedOrUnlabelledRows()
    {
        const string a = "ku9rtvg51ra4n33igep0pd9oo3rdad1i", b = "i1s9bnb1ra4n33igep0pd9oo3rdad1ii", c = "vpib790ka4v1imkkea359qh8nr2da877";
        var json = "[" + string.Join(",",
            Row("web-a", a, "2026-09-21T21:03:02Z", "closed"),
            Row("web-a", b, "2026-09-21T21:03:01Z", "open"),
            Row("web-b", c, "2026-09-21T21:03:00Z", "open")) + "]";
        var history = ProjectHistoryReader.Parse("dolt-revision", json);
        Assert.Equal("dolt-revision", history.HistoryRevision);
        Assert.Equal(["web-a", "web-b"], history.Issues.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, history.Issues["web-a"].Length);
        Assert.Equal($"beads:web-a:{a}", history.Issues["web-a"][0].Id);
        Assert.Equal("closed", history.Issues["web-a"][0].Status);
        // Labels are not in this table: unknown, never a claim that the issue has none.
        Assert.All(history.Issues["web-a"], v => Assert.Null(v.Labels));
        Assert.False(history.Coverage.LimitReached);
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T21:03:00Z"), history.Coverage.OldestReturned);
        Assert.Contains("Recorded label history is not available", history.Coverage.Explanation);
        // A failed query answers with an error object; that is not an empty history.
        Assert.Throws<InvalidDataException>(() => ProjectHistoryReader.Parse("dolt-revision", "{\"error\":\"query error\"}"));
        Assert.Throws<InvalidDataException>(() => ProjectHistoryReader.Parse("dolt-revision", "[" + Row("web-a", "short", "2026-09-21T21:03:02Z", "open") + "]"));
        Assert.Throws<InvalidDataException>(() => ProjectHistoryReader.Parse("dolt-revision", "[" + Row("web-a", a, "not-a-date", "open") + "]"));
        Assert.Throws<InvalidDataException>(() => ProjectHistoryReader.Parse("dolt-revision",
            "[" + Row("web-a", a, "2026-09-21T21:03:02Z", "open") + "," + Row("web-a", a, "2026-09-21T21:03:01Z", "closed") + "]"));
        Assert.Throws<ArgumentException>(() => ProjectHistoryReader.Parse(" ", json));
    }

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
