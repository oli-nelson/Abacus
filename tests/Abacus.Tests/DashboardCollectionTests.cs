using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardCollectionTests
{
    [Fact]
    public void TimelineDatesUseRecordedFieldsAndLeaveMissingOrInvalidTimesUnknown()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("""
            {"id":"dated","created_at":"2026-09-21T09:00:00+02:00","closed_at":"2026-09-21T12:00:00Z"}
            {"id":"unknown","created_at":"not a date"}
            """));
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T07:00:00Z"), projection.Current.Issues["dated"].CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-21T12:00:00Z"), projection.Current.Issues["dated"].ClosedAt);
        Assert.Null(projection.Current.Issues["unknown"].CreatedAt);
        Assert.Null(projection.Current.Issues["unknown"].ClosedAt);
    }

    [Fact]
    public void ExistingBeadsExportFixtureIncludesCommentsInRevision()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Beads", "latest-comments.jsonl"));
        var initial = IssueExport.Parse(source);
        var changed = IssueExport.Parse(source.Replace("Agent progress update", "Second working-set edit"));
        Assert.Equal(3, initial.Issues.Count);
        Assert.NotEqual(initial.Issues["abc-2"].Revision, changed.Issues["abc-2"].Revision);
        Assert.Equal(initial.Issues["abc-1"].Revision, changed.Issues["abc-1"].Revision);
    }

    [Fact]
    public void IdenticalExportsKeepProjectionAndObjectIdentity()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("""{"id":"a","title":"A","labels":["b","a"],"comments":[{"id":2,"text":"two"},{"id":1,"text":"one"}]}"""));
        var initial = projection.Current;
        for (var poll = 0; poll < 120; poll++)
        {
            Assert.False(projection.Apply(IssueExport.Parse("""{ "comments":[{"text":"one","id":1},{"text":"two","id":2}], "labels":["a","b"], "title":"A", "id":"a" }""")));
            Assert.Same(initial, projection.Current);
        }
        Assert.Equal(1, projection.RebuiltIssues);
    }

    [Theory]
    [InlineData("notes", "\"new note\"")]
    [InlineData("comments", "[{\"id\":1,\"text\":\"new comment\"}]")]
    [InlineData("dependencies", "[{\"depends_on_id\":\"b\",\"type\":\"blocks\"}]")]
    public void ConsecutiveWorkingSetEditsInvalidateOnlyChangedIssue(string field, string value)
    {
        var projection = new IssueProjection();
        const string unchanged = "\n{\"id\":\"b\",\"title\":\"B\"}";
        projection.Apply(IssueExport.Parse("{\"id\":\"a\"}" + unchanged));
        var retained = projection.Current.Issues["b"];
        projection.Apply(IssueExport.Parse("{\"id\":\"a\",\"" + field + "\":" + value + "}" + unchanged));
        Assert.Equal("a", Assert.Single(projection.Current.ChangedIds));
        Assert.Same(retained, projection.Current.Issues["b"]);
        projection.Apply(IssueExport.Parse("{\"id\":\"a\",\"" + field + "\":" + value.Replace("new", "second").Replace("blocks", "related") + "}" + unchanged));
        Assert.Equal(3, projection.Current.Revision);
        Assert.Equal(4, projection.RebuiltIssues);
    }

    [Fact]
    public void DeletionsAndEmptySnapshotAreAuthoritative()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("""{"id":"a"}"""));
        Assert.True(projection.Apply(IssueExport.Parse("")));
        Assert.Equal("a", Assert.Single(projection.Current.RemovedIds));
        Assert.Empty(projection.Current.Issues);
        Assert.False(projection.Apply(IssueExport.Parse("\n")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"--unsafe\"}")]
    [InlineData("{\"id\":\"a\",\"id\":\"b\"}")]
    [InlineData("{\"id\":\"a\"}\n{\"id\":\"a\"}")]
    public void RejectsAmbiguousOrUnsafeRecords(string export) =>
        Assert.Throws<InvalidDataException>(() => IssueExport.Parse(export));

    [Fact]
    public async Task ConcurrentClientsShareReadAndCancellationIsIsolated()
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var collector = new IssueCollector(_ => { Interlocked.Increment(ref reads); return response.Task; }, default);
        using var cancelled = new CancellationTokenSource();
        var abandoned = collector.RefreshAsync(cancelled.Token);
        var requests = Enumerable.Range(0, 10).Select(_ => collector.RefreshAsync()).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        response.SetResult("{\"id\":\"a\"}");
        var results = await Task.WhenAll(requests);
        Assert.Equal(1, reads);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, collector.RebuiltIssues);
    }

    [Fact]
    public async Task MalformedSourceRetainsLastGoodViewAndRecoveryDoesNotRebuild()
    {
        var data = "{\"id\":\"a\"}";
        var collector = new IssueCollector(_ => Task.FromResult(data), default);
        var initial = await collector.RefreshAsync();
        data = "credential-bearing invalid output";
        var failed = await collector.RefreshAsync();
        Assert.True(failed.Stale);
        Assert.Same(initial.View, failed.View);
        Assert.Equal(initial.LastSuccess, failed.LastSuccess);
        Assert.DoesNotContain("credential", failed.Error);
        data = "{\"id\":\"a\"}";
        var recovered = await collector.RefreshAsync();
        Assert.False(recovered.Stale);
        Assert.Same(initial.View, recovered.View);
        Assert.Equal(1, collector.RebuiltIssues);
    }
}
