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

        await output.SetAgentAsync("alice", AgentActivity.Working, "abc-1 • OpenCode in pane %1");
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
            await output.SetAgentAsync("alice", AgentActivity.Working, "abc-1 • OpenCode in pane %1");
            await output.SetAgentAsync("bob", AgentActivity.Waiting, "No ready tickets");
            await output.WarningAsync("alice", "example warning");
        }

        var text = writer.ToString();
        Assert.Contains("ABACUS", text, StringComparison.Ordinal);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("WORKING", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
        Assert.Contains("WAITING", text, StringComparison.Ordinal);
        Assert.Contains("example warning", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[?25h", text, StringComparison.Ordinal);
    }
}
