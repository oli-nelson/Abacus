using Abacus;

namespace Abacus.Tests;

public sealed class BeadsFixtureTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Beads", name));

    [Fact]
    public void EmptyReadyFixtureMeansIdle()
    {
        Assert.Empty(Beads.ParseIssues(Fixture("ready-none.json"), "ready fixture"));
    }

    [Fact]
    public void ClaimFixtureExtractsOnlyIdAndStatus()
    {
        var issue = Assert.Single(Beads.ParseIssues(Fixture("ready-claimed.json"), "claim fixture"));
        Assert.Equal("abc-123", issue.Id);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
    }

    [Theory]
    [InlineData("show-in-progress.json", IssueStatus.InProgress)]
    [InlineData("show-open.json", IssueStatus.Open)]
    [InlineData("show-blocked.json", IssueStatus.Blocked)]
    [InlineData("show-closed.json", IssueStatus.Closed)]
    public void ShowFixturesExtractSupportedStatuses(string fixture, IssueStatus expected)
    {
        var issue = Assert.Single(Beads.ParseIssues(Fixture(fixture), "show fixture"));
        Assert.Equal("abc-123", issue.Id);
        Assert.Equal(expected, issue.Status);
    }

    [Fact]
    public void HarmlessSchemaAdditionsAreIgnored()
    {
        var issue = Assert.Single(Beads.ParseIssues(
            "[{\"id\":\"abc-1\",\"status\":\"open\",\"future\":{\"anything\":true}}]",
            "future fixture"));
        Assert.Equal(new BeadsIssue("abc-1", IssueStatus.Open), issue);
    }
}
