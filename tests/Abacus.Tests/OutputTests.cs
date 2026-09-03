using Abacus;

namespace Abacus.Tests;

public sealed class OutputTests
{
    [Fact]
    public async Task DefaultRedirectedOutputShowsStatesButSuppressesCommands()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: false,
            color: false);

        await output.SetAgentAsync("alice", AgentActivity.Working, "abc-1 • agent CLI in pane %1");
        await output.DebugCommandAsync("alice", "bd show abc-1 --json");

        var text = writer.ToString();
        Assert.Contains("[alice] WORKING", text, StringComparison.Ordinal);
        Assert.Contains("abc-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bd show", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseOutputShowsStatesWarningsAndCommands()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: true,
            interactive: false,
            color: false);

        await output.SetAgentAsync("alice", AgentActivity.Preparing, "abc-1 • preparing workspace");
        await output.WarningAsync("alice", "something needs attention");
        await output.DebugCommandAsync("alice", "bd show abc-1 --json");

        var text = writer.ToString();
        Assert.Contains("[alice] PREPARING", text, StringComparison.Ordinal);
        Assert.Contains("[alice] WARNING", text, StringComparison.Ordinal);
        Assert.Contains("[alice] DEBUG", text, StringComparison.Ordinal);
        Assert.Contains("bd show abc-1 --json", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveDashboardRendersAllAgentRowsAndRecentWarnings()
    {
        var writer = new StringWriter();
        using (var output = new ConsoleOutput(
            writer,
            ["alice", "bob"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false))
        {
            await output.SetTicketAsync("alice", "abc-1", "Make the dashboard useful");
            await output.SetRunLocationAsync("alice", "pane %1");
            await output.SetAgentAsync("alice", AgentActivity.Working, "abc-1 • agent CLI in pane %1");
            await output.SetAgentAsync("bob", AgentActivity.Idle, "No ready tickets");
            await output.SetAgentAsync("alice", AgentActivity.Retrying, "Agent CLI failed; retrying soon");
            await output.SetLastExitCodeAsync("alice", 17);
            await output.WarningAsync("alice", "example warning");
            await output.SetUserAttentionIssuesAsync(
                [new BeadsIssue("abc-9", IssueStatus.Blocked, "Choose a save format")]);
            await output.SetPersistentAlertAsync("alice", "Recovery could not be verified");
            await output.SetLatestCommentsAsync([
                Comment("comment-1", "abc-9", "Choose a save format", "alice", "Agent update", attention: true),
                Comment("comment-2", "abc-2", "Implement parser", "alice", "Managed agent update"),
                Comment("comment-3", "abc-3", "Review output", "reviewer", "Unknown author update"),
            ]);
        }

        var text = writer.ToString();
        Assert.Contains("ABACUS", text, StringComparison.Ordinal);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("WORKING", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
        Assert.Contains("IDLE", text, StringComparison.Ordinal);
        Assert.Contains("abc-1 — Make the dashboard useful", text, StringComparison.Ordinal);
        Assert.Contains("pane %1", text, StringComparison.Ordinal);
        Assert.Contains("retries 1", text, StringComparison.Ordinal);
        Assert.Contains("last exit 17", text, StringComparison.Ordinal);
        Assert.Contains("example warning", text, StringComparison.Ordinal);
        Assert.Contains("USER ATTENTION (2)", text, StringComparison.Ordinal);
        Assert.Contains("abc-9 — Choose a save format", text, StringComparison.Ordinal);
        Assert.Contains("alice — Recovery could not be verified", text, StringComparison.Ordinal);
        Assert.Contains("LATEST COMMENTS (3)", text, StringComparison.Ordinal);
        Assert.Contains("abc-9", text, StringComparison.Ordinal);
        Assert.Contains("Choose a save format", text, StringComparison.Ordinal);
        Assert.Contains("Agent update", text, StringComparison.Ordinal);
        Assert.Contains("Managed agent update", text, StringComparison.Ordinal);
        Assert.Contains("Unknown author update", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[?25h", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LatestCommentsUseAttentionAgentAndUnknownAuthorColors()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: true);

        await output.SetLatestCommentsAsync([
            Comment("comment-1", "abc-1", "Attention", "alice", "red", attention: true),
            Comment("comment-2", "abc-2", "Agent", "alice", "green"),
            Comment("comment-3", "abc-3", "Unknown", "reviewer", "cyan"),
        ]);

        var text = writer.ToString();
        Assert.Contains("\u001b[31m • abc-1", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[31m   ↳ red", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[32m • abc-2", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[32m   ↳ green", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[36m • abc-3", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[36m   ↳ cyan", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestCommentLinesFlattenAndTruncateHeaderAndCommentToTerminalWidth()
    {
        var lines = ConsoleOutput.FormatLatestCommentLines(
            Comment(
                "comment-1",
                "abc-123",
                "A very long issue title\nthat continues on another line",
                "external-reviewer",
                "A very long comment\nthat also continues and must be truncated for the dashboard"),
            52);

        Assert.Equal(52, lines.Header.Length);
        Assert.Equal(52, lines.Comment.Length);
        Assert.DoesNotContain('\n', lines.Header);
        Assert.DoesNotContain('\n', lines.Comment);
        Assert.Contains('…', lines.Header);
        Assert.Contains('…', lines.Comment);
        Assert.StartsWith(" • abc-123", lines.Header, StringComparison.Ordinal);
        Assert.StartsWith("   ↳ ", lines.Comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectedOutputReportsAttentionChangesWithoutRepeatingThem()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: false,
            color: false);
        var issue = new BeadsIssue("abc-9", IssueStatus.Open, "Choose a save format");

        await output.SetUserAttentionIssuesAsync([issue]);
        await output.SetUserAttentionIssuesAsync([issue with { Status = IssueStatus.Blocked }]);
        await output.SetUserAttentionIssuesAsync([]);

        var attentionLines = writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.Contains("ATTENTION", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, attentionLines.Length);
        Assert.Contains("abc-9 — Choose a save format", attentionLines[0], StringComparison.Ordinal);
        Assert.Contains("No issues currently need user attention", attentionLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryReplacesInteractiveDashboardWithOutcomeTotals()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice", "bob"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false);

        await output.SetPersistentAlertAsync("alice", "Could not verify recovery");
        await output.SummaryAsync(new RunSummarySnapshot(
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            "pjmrvjigiph28prpf6ir4uv0tuv88vnn",
            [
                new AgentRunSummary("alice", 2, 1, 0, 0),
                new AgentRunSummary("bob", 0, 0, 1, 1),
            ]));

        var text = writer.ToString();
        Assert.Contains("ABACUS RUN SUMMARY", text, StringComparison.Ordinal);
        Assert.Contains("2m 3s", text, StringComparison.Ordinal);
        Assert.Contains("5 outcomes", text, StringComparison.Ordinal);
        Assert.Contains("Initial Beads Dolt commit  pjmrvjigiph28prpf6ir4uv0tuv88vnn", text, StringComparison.Ordinal);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("closed 2", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
        Assert.Contains("blocked 1", text, StringComparison.Ordinal);
        Assert.Contains("interrupted 1", text, StringComparison.Ordinal);
        Assert.Contains("USER ATTENTION", text, StringComparison.Ordinal);
        Assert.Contains("alice — Could not verify recovery", text, StringComparison.Ordinal);
    }

    private static BeadsComment Comment(
        string id,
        string issueId,
        string title,
        string author,
        string text,
        bool attention = false) =>
        new(id, issueId, title, author, text, DateTimeOffset.Parse("2026-09-02T12:00:00Z"), attention);
}
