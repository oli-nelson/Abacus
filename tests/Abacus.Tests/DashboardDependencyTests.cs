using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardDependencyTests
{
    [Fact]
    public void RealExportProjectsOnlyExplicitEdgesAndKnownEmptyCounts()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Beads", "Dashboard-1.2.2", "export-dependency.jsonl"))));
        Assert.Equal(new IssueDependency("web-cuh", "web-0kh", "blocks"),
            Assert.Single(projection.Current.Issues["web-cuh"].Dependencies!.Value));
        Assert.Empty(projection.Current.Issues["web-0kh"].Dependencies!.Value);
    }

    [Fact]
    public void BlockingCountDoesNotRejectParentChildOrRelatedLinks()
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("""
            {"id":"child","dependency_count":1,"dependencies":[{"issue_id":"child","depends_on_id":"prereq","type":"blocks"},{"issue_id":"child","depends_on_id":"epic","type":"parent-child"},{"issue_id":"child","depends_on_id":"sibling","type":"relates-to"}]}
            {"id":"only-child","dependency_count":0,"dependencies":[{"issue_id":"only-child","depends_on_id":"epic","type":"parent-child"}]}
            """));
        Assert.Equal(3, projection.Current.Issues["child"].Dependencies!.Value.Length);
        Assert.Equal("parent-child", Assert.Single(projection.Current.Issues["only-child"].Dependencies!.Value).Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"dependency_count\":1")]
    [InlineData(",\"dependencies\":null")]
    [InlineData(",\"dependencies\":[{\"issue_id\":\"other\",\"depends_on_id\":\"b\",\"type\":\"blocks\"}]")]
    [InlineData(",\"dependency_count\":2,\"dependencies\":[]")]
    [InlineData(",\"dependency_count\":1,\"dependencies\":[{\"issue_id\":\"a\",\"depends_on_id\":\"b\",\"type\":\"parent-child\"}]")]
    [InlineData(",\"dependencies\":[{\"issue_id\":\"a\",\"depends_on_id\":\"b\"}]")]
    public void IncompleteOrInvalidRelationshipsAreUnknownNotEmpty(string fields)
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse("{\"id\":\"a\"" + fields + "}"));
        Assert.Null(projection.Current.Issues["a"].Dependencies);
    }

    [Fact]
    public void DependencyOnlyChangeRebuildsSourceButNotItsTarget()
    {
        var projection = new IssueProjection();
        const string before = """
            {"id":"a","dependencies":[]}
            {"id":"b","dependency_count":0}
            """;
        projection.Apply(IssueExport.Parse(before));
        var target = projection.Current.Issues["b"];
        projection.Apply(IssueExport.Parse(before.Replace("[]",
            """[{"issue_id":"a","depends_on_id":"b","type":"blocks","metadata":{"private":"excluded"}}]""")));
        Assert.Same(target, projection.Current.Issues["b"]);
        Assert.Equal("a", Assert.Single(projection.Current.ChangedIds));
        Assert.Single(projection.Current.Issues["a"].Dependencies!.Value);
    }
}
