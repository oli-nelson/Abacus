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
    public void ClaimFixtureExtractsIdStatusAndTitle()
    {
        var issue = Assert.Single(Beads.ParseIssues(Fixture("ready-claimed.json"), "claim fixture"));
        Assert.Equal("abc-123", issue.Id);
        Assert.Equal(IssueStatus.InProgress, issue.Status);
        Assert.Equal("Contract fixture", issue.Title);
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

    [Fact]
    public void CommentExportIsFlattenedSortedLimitedAndCarriesAttentionState()
    {
        var comments = Beads.ParseLatestComments(Fixture("latest-comments.jsonl"), 2);

        Assert.Collection(
            comments,
            comment =>
            {
                Assert.Equal("comment-3", comment.Id);
                Assert.Equal("abc-1", comment.IssueId);
                Assert.Equal("Needs a user decision", comment.IssueTitle);
                Assert.Equal("reviewer", comment.Author);
                Assert.Equal("Newest attention comment", comment.Text);
                Assert.True(comment.NeedsUserAttention);
            },
            comment =>
            {
                Assert.Equal("comment-2", comment.Id);
                Assert.Equal("abc-2", comment.IssueId);
                Assert.Equal("alice", comment.Author);
                Assert.False(comment.NeedsUserAttention);
            });
    }

    [Fact]
    public async Task LatestCommentsUseReadOnlyExportWithAgentAttribution()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-comments-");
        try
        {
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, """
                #!/bin/sh
                printf '%s\n' "$*" > "$PWD/calls"
                printf '%s\n' "$BEADS_ACTOR" > "$PWD/actor"
                printf '%s\n' '{"id":"abc-1","title":"Issue title","labels":[],"comments":[{"id":"comment-1","issue_id":"abc-1","author":"alice","text":"Done","created_at":"2026-09-02T12:00:00Z"}]}'
                """);
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var comments = await new Beads(new CommandRunner(TextWriter.Null), script)
                .GetLatestCommentsAsync(root.FullName, "alice", 8, CancellationToken.None);

            Assert.Single(comments);
            Assert.Equal("--readonly export", (await File.ReadAllTextAsync(Path.Combine(root.FullName, "calls"))).Trim());
            Assert.Equal("alice", (await File.ReadAllTextAsync(Path.Combine(root.FullName, "actor"))).Trim());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
