using Abacus;

namespace Abacus.Tests;

public sealed class SupervisorRunDetailTests
{
    [Theory]
    [InlineData("maintenance", 52, 12)]
    [InlineData("continuation", 80, 24)]
    public async Task LastRunIsWrappedScrollableAndSurvivesNewActivity(string name, int width, int height)
    {
        var writer = new FrameWriter();
        using var output = new ConsoleOutput(writer, [name], "default", false, interactive: true,
            color: false, terminalSize: () => (width, height));
        output.EnableSupervisor(name);
        await output.SetModelAsync(name, "previous-model", "medium");
        var summary = "FIRST-LINE\n" + string.Join('\n', Enumerable.Range(1, 60).Select(i => $"summary line {i:D2}"))
            + "\n" + new string('x', 200) + "\nFINAL-LINE";
        await output.SetSupervisorLastRunAsync(name, summary);
        await output.SetModelAsync(name, "next-model", "high");
        await output.SetAgentAsync(name, AgentActivity.Working, "New run active");
        var gate = new ClaimGate();
        output.HandleDashboardKey(Key(ConsoleKey.DownArrow), gate);
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.L), gate));
        Assert.Contains($"LAST RUN — {name}", writer.Frame());
        Assert.Contains("previous-model", writer.Frame());
        Assert.DoesNotContain("next-model", writer.Frame());
        Assert.Contains("FIRST-LINE", writer.Frame());
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.PageDown), gate));
        Assert.DoesNotContain("FIRST-LINE", writer.Frame());
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.End), gate));
        Assert.Contains("FINAL-LINE", writer.Frame());
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.Home), gate));
        Assert.Contains("FIRST-LINE", writer.Frame());
        Assert.False(output.HandleDashboardKey(Key(ConsoleKey.R), gate, (_, _) => Assert.Fail("Read-only view requested restart")));
        Assert.False(output.HandleDashboardKey(Key(ConsoleKey.S), gate));
        Assert.False(output.HandleDashboardKey(Key(ConsoleKey.C), gate));
        // Resizing rewraps content and clamps scrolling without losing the tail.
        width = 60; height = 16;
        output.HandleDashboardKey(Key(ConsoleKey.End), gate);
        Assert.Contains("FINAL-LINE", writer.Frame());
        output.HandleDashboardKey(Key(ConsoleKey.Escape), gate);
        Assert.Contains("New run active", writer.Frame());
    }

    [Theory]
    [InlineData("maintenance")]
    [InlineData("continuation")]
    public async Task MenuOffersReadOnlyDetailsAndHandlesNoPreviousRun(string name)
    {
        var writer = new FrameWriter();
        using var output = new ConsoleOutput(writer, [name], "model", false, interactive: true,
            color: false, terminalSize: () => (100, 24));
        output.EnableSupervisor(name);
        await output.SetAgentAsync(name, AgentActivity.Idle, "Waiting");
        var gate = new ClaimGate();
        output.HandleDashboardKey(Key(ConsoleKey.Enter), gate);
        Assert.Contains("[L] Last run details", writer.Frame());
        output.HandleDashboardKey(Key(ConsoleKey.L), gate);
        Assert.Contains("No last-run information yet", writer.Frame());
        await output.SetSupervisorLastRunAsync(name, "Reported result\nunsafe\u001b[31m text\nEND");
        Assert.Contains("Reported result", writer.Frame());
        Assert.DoesNotContain("\u001b[31m", writer.Frame(), StringComparison.Ordinal);
        output.HandleDashboardKey(Key(ConsoleKey.Escape), gate);
        Assert.Contains("AGENT ACTIONS", writer.Frame());
        Assert.DoesNotContain("Clean workspace", writer.Frame());
    }

    [Fact]
    public async Task WorkersDoNotOfferSupervisorHistory()
    {
        var writer = new FrameWriter();
        using var output = new ConsoleOutput(writer, ["worker"], "model", false, interactive: true,
            color: false, terminalSize: () => (100, 24));
        await output.SetAgentAsync("worker", AgentActivity.Idle, "Waiting");
        var gate = new ClaimGate();
        output.HandleDashboardKey(Key(ConsoleKey.Enter), gate);
        Assert.DoesNotContain("Last run details", writer.Frame());
        Assert.False(output.HandleDashboardKey(Key(ConsoleKey.L), gate));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);
    private sealed class FrameWriter : StringWriter
    {
        private readonly object gate = new();
        public override void Write(string? value) { lock (gate) base.Write(value); }
        public string Frame() { lock (gate) return base.ToString().Split("\u001b[H")[^1]; }
    }
}
