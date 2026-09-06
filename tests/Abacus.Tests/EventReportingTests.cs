using System.Text.Json;
using System.Text.RegularExpressions;
using Abacus;

namespace Abacus.Tests;

public sealed class EventReportingTests
{
    private static Options RunOptions(params string[] extra) => Options.Parse(
        ["run", "--model", "p/m", "--tmux-session", "test", "-a", "alice", "/tmp/alice", .. extra]).Value!;

    [Fact]
    public void ParsesRunOnlyOptionsAndEqualsPaths()
    {
        var options = RunOptions("--stdio", "--start-paused", "--no-intro", "--event-log=/tmp/events.jsonl");
        Assert.True(options.Stdio && options.StartPaused && options.NoIntro);
        Assert.Equal("/tmp/events.jsonl", options.EventLogPath);
    }

    [Fact]
    public void StartPausedDoesNotRequireStdio()
    {
        var options = RunOptions("--start-paused", "--no-intro");
        Assert.True(options.StartPaused);
        Assert.False(options.Stdio);
        Assert.False(RunOptions().StartPaused);
    }

    [Theory]
    [InlineData("--stdio", "--verbose")]
    [InlineData("--stdio", "--notify=all")]
    [InlineData("--stdio", "--stdio")]
    [InlineData("--start-paused", "--verbose")]
    [InlineData("--event-log=", "--no-intro")]
    public void InvalidCombinationsAreRejected(string first, string second) =>
        Assert.Throws<OptionsException>(() => RunOptions(first, second));

    [Theory]
    [InlineData("--stdio")]
    [InlineData("--event-log=/tmp/events")]
    [InlineData("--no-intro")]
    [InlineData("--start-paused")]
    public void StandaloneCommandsRejectRunFlags(string flag)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["health", flag]));
        Assert.Throws<OptionsException>(() => Options.Parse(["preflight", flag]));
    }

    [Fact]
    public async Task ConcurrentEventsAreOrderedEscapedAndMirroredWithoutTruncation()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "previous run\n");
            var stdout = new StringWriter();
            using (var events = new EventReporter(stdout, path))
                await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
                    events.Emit("test", new { i, text = "hello\n\u001b[31m" }))));
            var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(50, lines.Length);
            for (var i = 0; i < lines.Length; i++)
            {
                using var json = JsonDocument.Parse(lines[i]);
                Assert.Equal(i + 1, json.RootElement.GetProperty("sequence").GetInt64());
                Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
                Assert.True(json.RootElement.GetProperty("timestamp").TryGetDateTimeOffset(out _));
                Assert.Equal("hello\n\u001b[31m", json.RootElement.GetProperty("data").GetProperty("text").GetString());
            }
            Assert.Equal("previous run\n" + stdout, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReportsDashboardStateAlertsOutcomesAndSummaryWithoutVerbose()
    {
        var stdout = new StringWriter();
        using var events = new EventReporter(stdout);
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "p/m", false,
            interactive: false, events: events);
        await output.SetTicketAsync("alice", "abc-1", "A ticket");
        await output.SetAgentAsync("alice", AgentActivity.Working, "working");
        await output.SetRunLocationAsync("alice", "pid 123");
        await output.SetLastExitCodeAsync("alice", 0);
        await output.SetPersistentAlertAsync("alice", "help");
        await output.ClearPersistentAlertAsync("alice");
        await output.DebugCommandAsync("alice", "git status");
        await output.WarningAsync("alice", "warning");
        var summary = new RunSummary(["alice"], "baseline", events: events);
        summary.Record("alice", TicketOutcome.Closed, "abc-1", "A ticket");
        await output.SummaryAsync(summary.Snapshot());
        var text = stdout.ToString();
        Assert.Contains("\"type\":\"agent.state\"", text);
        Assert.Contains("\"activity\":\"working\"", text);
        Assert.Contains("\"type\":\"alert.cleared\"", text);
        Assert.Contains("\"type\":\"command\"", text);
        Assert.Contains("\"type\":\"ticket.outcome\"", text);
        Assert.Contains("\"type\":\"run.summary\"", text);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABACUS RUN SUMMARY", text);
    }

    [Fact]
    public void FailedSinkDoesNotThrowOrPreventOtherSink()
    {
        var failures = 0;
        using var events = new EventReporter(new BrokenWriter(), reportFailure: _ => failures++);
        events.Emit("test");
        events.Emit("test");
        Assert.True(events.HasFailed);
        Assert.Equal(1, failures);
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("broken pipe");
    }

    [Fact]
    public async Task ControlValidatesCommandsAndUsesExistingActions()
    {
        var stdout = new StringWriter();
        using var events = new EventReporter(stdout);
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "p/m", false,
            interactive: false, events: events);
        var claims = new ClaimGate();
        var actions = new List<AgentControlAction>();
        var shutdown = false;
        var control = new StdioControl(new StringReader(""), output, claims, (agent, action) =>
        {
            if (agent != "alice") throw new ArgumentException("unknown agent");
            actions.Add(action);
        }, () => shutdown = true);
        control.Handle("""{"id":"p","command":"pause"}""");
        Assert.False(claims.IsEnabled);
        control.Handle("""{"id":"r","command":"resume"}""");
        Assert.True(claims.IsEnabled);
        await output.SetTicketAsync("alice", "abc-1", "test");
        control.Handle("""{"id":"s","command":"status"}""");
        control.Handle("""{"id":"a","command":"stop","agent":"alice"}""");
        control.Handle("""{"id":"b","command":"restart","agent":"alice"}""");
        control.Handle("""{"id":"c","command":"clean-workspace","agent":"alice"}""");
        control.Handle("""{"id":"d","command":"clean-workspace","agent":"alice","confirm":true}""");
        control.Handle("""{"id":"e","command":"stop","agent":"missing"}""");
        control.Handle("""{"id":"f","command":"wat"}""");
        control.Handle("""{"id":"g","command":"resume","typo":1}""");
        control.Handle("""{"id":"h","command":"resume","command":"pause"}""");
        control.Handle("[]");
        control.Handle("{broken");
        Assert.Equal(new[] { AgentControlAction.Stop, AgentControlAction.Restart, AgentControlAction.CleanWorkspace }, actions);
        Assert.False(control.Handle("""{"id":"quit","command":"shutdown"}"""));
        Assert.True(shutdown);
        var text = stdout.ToString();
        Assert.Contains("\"issueId\":\"abc-1\"", text);
        Assert.Contains("\"id\":\"c\",\"command\":\"clean-workspace\",\"ok\":false", text);
        Assert.Equal(7, Regex.Matches(text, "\"ok\":false").Count);
    }

    [Fact]
    public async Task EofRequestsShutdown()
    {
        using var events = new EventReporter(new StringWriter());
        using var output = new ConsoleOutput(TextWriter.Null, [], "p/m", false, interactive: false, events: events);
        var stopped = false;
        await new StdioControl(new StringReader(""), output, new ClaimGate(), (_, _) => { }, () => stopped = true)
            .RunAsync(CancellationToken.None);
        Assert.True(stopped);
    }

    [Fact]
    public void IntroOnlyPlaysForInteractiveDashboard()
    {
        var normal = RunOptions();
        Assert.True(AsciiIntro.ShouldPlay(normal, false, false, false, "xterm"));
        foreach (var options in new[] { normal with { Stdio = true }, normal with { NoIntro = true },
            normal with { Verbose = true }, normal with { CheckOnly = true } })
            Assert.False(AsciiIntro.ShouldPlay(options, false, false, false, "xterm"));
        Assert.False(AsciiIntro.ShouldPlay(normal, true, false, false, "xterm"));
        Assert.False(AsciiIntro.ShouldPlay(normal, false, true, false, "xterm"));
        Assert.False(AsciiIntro.ShouldPlay(normal, false, false, true, "xterm"));
        Assert.False(AsciiIntro.ShouldPlay(normal, false, false, false, "dumb"));
    }

    [Fact]
    public void IntroFramesAnimateAndFitSmallTerminalsWithoutColor()
    {
        Assert.NotEqual(AsciiIntro.Frame(0, 80, 24, false), AsciiIntro.Frame(29, 80, 24, false));
        var frame = AsciiIntro.Frame(12, 20, 6, false);
        var plain = Regex.Replace(frame, "\u001b\\[[0-9;?]*[A-Za-z]", "");
        Assert.All(plain.Split('\n'), line => Assert.True(line.Length < 20));
        Assert.Contains("ABACUS", plain);
        Assert.DoesNotContain("m", frame);
    }
}
