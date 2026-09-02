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
        Assert.Contains("USER ATTENTION (1)", text, StringComparison.Ordinal);
        Assert.Contains("abc-9 — Choose a save format", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[?25h", text, StringComparison.Ordinal);
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

        await output.SummaryAsync(new RunSummarySnapshot(
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            [
                new AgentRunSummary("alice", 2, 1, 0, 0),
                new AgentRunSummary("bob", 0, 0, 1, 1),
            ]));

        var text = writer.ToString();
        Assert.Contains("ABACUS RUN SUMMARY", text, StringComparison.Ordinal);
        Assert.Contains("2m 3s", text, StringComparison.Ordinal);
        Assert.Contains("5 outcomes", text, StringComparison.Ordinal);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("closed 2", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
        Assert.Contains("blocked 1", text, StringComparison.Ordinal);
        Assert.Contains("interrupted 1", text, StringComparison.Ordinal);
    }
}
