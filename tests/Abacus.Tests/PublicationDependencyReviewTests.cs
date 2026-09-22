using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class PublicationDependencyReviewTests
{
    private static IReadOnlyDictionary<string, IssueSummary> Source(params (string Id, string[]? Targets)[] entries)
    {
        var lines = entries.Select(entry => JsonSerializer.Serialize(new
        {
            id = entry.Id, status = "closed", dependencies = entry.Targets?.Select(target => new
            { issue_id = entry.Id, depends_on_id = target, type = "blocks" })
        }));
        var projection = new IssueProjection(); projection.Apply(IssueExport.Parse(string.Join('\n', lines)));
        return projection.Current.Issues;
    }

    [Fact]
    public void TransitiveMissingAndUnknownEvidenceCannotLookLikeCompleteDirectEdges()
    {
        var result = PublicationDependencyReview.Inspect("a", Source(("a", ["b"]),
            ("b", ["missing", "unknown"]), ("unknown", null), ("unrelated", null)), default);
        Assert.False(result.Complete); Assert.False(result.Truncated);
        Assert.Equal(new[] { "missing" }, result.MissingIssues);
        Assert.Equal(new[] { "unknown" }, result.UnknownCollections);
        Assert.Equal(4, result.ObservedIssues); Assert.Equal(3, result.ObservedEdges);
    }

    [Fact]
    public void CompleteCoverageDoesNotMeanAcyclicOrReadyAndIgnoresUnrelatedUnknowns()
    {
        var result = PublicationDependencyReview.Inspect("a", Source(("a", ["b"]), ("b", ["a"]), ("other", null)), default);
        Assert.True(result.Complete); Assert.False(result.Truncated);
        Assert.Equal(2, result.ObservedIssues); Assert.Equal(2, result.ObservedEdges);
        Assert.True(PublicationDependencyReview.Inspect("empty", Source(("empty", [])), default).Complete);
    }

    [Fact]
    public void BoundsCannotBeMisrepresentedAsCompleteAndCancellationIsObserved()
    {
        var source = Source(("a", ["b", "c"]), ("b", []), ("c", []));
        var limited = PublicationDependencyReview.Inspect("a", source, default, maximumIssues: 2);
        Assert.True(limited.Truncated); Assert.False(limited.Complete); Assert.Equal(2, limited.ObservedIssues);
        limited = PublicationDependencyReview.Inspect("a", source, default, maximumEdges: 1);
        Assert.True(limited.Truncated); Assert.False(limited.Complete); Assert.Equal(1, limited.ObservedEdges);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => PublicationDependencyReview.Inspect("a", source, stop.Token));
    }
}
