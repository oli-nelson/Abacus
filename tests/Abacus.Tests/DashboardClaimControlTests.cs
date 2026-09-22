using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardClaimControlTests
{
    [Fact]
    public void RetryLedgerIsBoundedWithoutEvictingOldOperations()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        var gate = new ClaimGate();
        var runtime = new DashboardRuntime(output, gate);
        var first = new RuntimeControl(runtime.Instance, Guid.NewGuid().ToString(), "pause", true);
        Assert.Equal(200, runtime.Submit(first).StatusCode);
        for (var i = 1; i < 1024; i++)
            Assert.Equal(200, runtime.Submit(new(runtime.Instance, Guid.NewGuid().ToString(), "pause", false)).StatusCode);
        Assert.Equal(503, runtime.Submit(new(runtime.Instance, Guid.NewGuid().ToString(), "resume", false)).StatusCode);
        gate.SetEnabled(true);
        Assert.Equal(200, runtime.Submit(first).StatusCode);
        Assert.True(gate.IsEnabled);
    }

    [Fact]
    public void ControlsAreSessionScopedConditionalAndRetrySafe()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        var gate = new ClaimGate();
        var runtime = new DashboardRuntime(output, gate);
        var request = new RuntimeControl(runtime.Instance, Guid.NewGuid().ToString(), "pause", true);
        Assert.Equal(200, runtime.Submit(request).StatusCode);
        Assert.False(gate.IsEnabled);
        gate.SetEnabled(true); // another interface resumes
        Assert.Equal(200, runtime.Submit(request).StatusCode);
        Assert.True(gate.IsEnabled); // retry never replays the old pause
        Assert.Equal(400, runtime.Submit(request with { Command = "resume" }).StatusCode);
        Assert.Equal(409, runtime.Submit(request with { Session = Guid.NewGuid().ToString("N") }).StatusCode);
        Assert.Equal(409, runtime.Submit(request with { RequestId = Guid.NewGuid().ToString(), ExpectedManualEnabled = false }).StatusCode);
        runtime.StopControls();
        Assert.Equal(503, runtime.Submit(request with { RequestId = Guid.NewGuid().ToString() }).StatusCode);
        Assert.True(gate.IsEnabled);
    }

    [Fact]
    public void ResumeDoesNotBypassBlockedSchedule()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        var gate = new ClaimGate(); gate.SetEnabled(false);
        var schedule = ClaimSchedule.FromDocument(System.Text.Json.Nodes.JsonNode.Parse(
            """{"timezone":"UTC","block":["mon-sun 00:00-23:59"]}"""))!;
        var clock = new FixedClock();
        var runtime = new DashboardRuntime(output, gate, schedule, clock);
        Assert.Equal(200, runtime.Submit(new(runtime.Instance, Guid.NewGuid().ToString(), "resume", false)).StatusCode);
        var claims = runtime.Refresh().View.Claims!;
        Assert.True(claims.ManualEnabled);
        Assert.False(claims.GateAllows);
    }
    private sealed class FixedClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero); }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"command\":\"shutdown\"}")]
    [InlineData("{\"session\":\"bad\",\"requestId\":\"bad\",\"command\":\"pause\",\"expectedManualEnabled\":true}")]
    public void MalformedControlsAreRejected(string json)
    {
        using var body = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => DashboardRuntime.ParseControl(body.RootElement));
    }
}
