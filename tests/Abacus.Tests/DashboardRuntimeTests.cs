using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardRuntimeTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 2, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task ScheduleBoundaryPublishesWithoutAnyWorkerOrGateEvent()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        var clock = new Clock();
        var schedule = ClaimSchedule.FromDocument(System.Text.Json.Nodes.JsonNode.Parse(
            """{"timezone":"UTC","block":["mon-fri 01:00-04:00"]}"""))!;
        var runtime = new DashboardRuntime(output, new ClaimGate(), schedule, clock);
        var events = System.Threading.Channels.Channel.CreateUnbounded<RuntimePublication>();
        using var stop = new CancellationTokenSource();
        var running = runtime.RunAsync(value => events.Writer.TryWrite(value), stop.Token);
        Assert.False((await events.Reader.ReadAsync()).View.Claims!.GateAllows);
        clock.Now = new(2026, 9, 14, 4, 0, 0, TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True((await events.Reader.ReadAsync(timeout.Token)).View.Claims!.GateAllows);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public void ClaimsSeparateManualPauseFromScheduleWithoutChangingAuthority()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        var gate = new ClaimGate();
        var clock = new Clock();
        var schedule = ClaimSchedule.FromDocument(System.Text.Json.Nodes.JsonNode.Parse(
            """{"timezone":"UTC","block":["mon-fri 01:00-04:00"],"minWindowRemaining":"10m"}"""))!;
        var runtime = new DashboardRuntime(output, gate, schedule, clock);
        var blocked = runtime.Refresh();
        Assert.True(blocked.View.Claims!.ManualEnabled);
        Assert.False(blocked.View.Claims.ScheduleAllows);
        Assert.False(blocked.View.Claims.GateAllows);
        Assert.True(gate.IsEnabled); // visibility never mutates the gate
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Same(blocked, runtime.Refresh(false)); // no ticking countdown publication
        gate.SetEnabled(false);
        Assert.Equal("Manually paused", runtime.Refresh().View.Claims!.Reason);
        clock.Now = new(2026, 9, 14, 4, 0, 0, TimeSpan.Zero);
        var openButPaused = runtime.Refresh(false);
        Assert.True(openButPaused.View.Claims!.ScheduleAllows);
        Assert.False(openButPaused.View.Claims.GateAllows);
        gate.SetEnabled(true);
        Assert.True(runtime.Refresh().View.Claims!.GateAllows);
        clock.Now = new(2026, 9, 15, 0, 55, 0, TimeSpan.Zero);
        Assert.Contains("Insufficient", runtime.Refresh(false).View.Claims!.Reason);
    }

    [Fact]
    public async Task ManualGateChangesTriggerRuntimeEventsWithoutWorkerChanges()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        var gate = new ClaimGate();
        var runtime = new DashboardRuntime(output, gate);
        var events = System.Threading.Channels.Channel.CreateUnbounded<RuntimePublication>();
        using var stop = new CancellationTokenSource();
        var running = runtime.RunAsync(value => events.Writer.TryWrite(value), stop.Token);
        var initial = await events.Reader.ReadAsync();
        gate.SetEnabled(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var paused = await events.Reader.ReadAsync(timeout.Token);
        Assert.False(paused.View.Claims!.ManualEnabled);
        Assert.True(initial.View.Workers.SequenceEqual(paused.View.Workers));
        gate.SetEnabled(false);
        Assert.False(events.Reader.TryRead(out _));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        gate.SetEnabled(true);
        Assert.False(runtime.State.View.Claims!.ManualEnabled);
    }

    [Fact]
    public async Task WorkerProjectionUsesAuthoritativeRowsAndExcludesRawDetails()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", verbose: false, interactive: false);
        var runtime = new DashboardRuntime(output);
        var first = runtime.Refresh();
        Assert.Equal("Starting", Assert.Single(first.View.Workers).Activity);
        Assert.Same(first, runtime.Refresh());
        await output.SetAgentAsync("alice", AgentActivity.Working, "secret raw command details");
        await output.SetTicketAsync("alice", "web-a", "secret raw ticket title");
        await output.SetRunLocationAsync("alice", "credential-bearing location");
        var changed = runtime.Refresh();
        Assert.Equal("Working", changed.View.Workers[0].Activity);
        Assert.Equal("web-a", changed.View.Workers[0].IssueId);
        Assert.True(changed.View.Workers[0].RunActive);
        var json = System.Text.Encoding.UTF8.GetString(changed.Body);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("credential", json);
        await output.SetAgentAsync("alice", AgentActivity.Working, "different raw details");
        Assert.Same(changed, runtime.Refresh());
    }

    [Fact]
    public async Task RuntimeChangesPublishWithoutIssueSerializationAndUnsubscribeOnStop()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", verbose: false, interactive: false);
        var runtime = new DashboardRuntime(output);
        var stream = new DashboardStream();
        var collector = new IssueCollector(_ => Task.FromResult("{\"id\":\"web-a\"}"), default);
        stream.Publish(await collector.RefreshAsync());
        using var stop = new CancellationTokenSource();
        var running = runtime.RunAsync(stream.PublishRuntime, stop.Token);
        var initial = stream.Snapshot();
        using var events = stream.Subscribe(initial.ETag.Trim('"'));
        await output.SetAgentAsync("alice", AgentActivity.Working, "not exported");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var change = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal("runtime", change.Kind);
        Assert.Equal(1, stream.SerializedIssues);
        Assert.Equal(1, stream.SnapshotBuilds);
        using var body = JsonDocument.Parse(change.Data);
        Assert.Equal("Working", body.RootElement.GetProperty("workers")[0].GetProperty("activity").GetString());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        var last = runtime.State;
        await output.SetAgentAsync("alice", AgentActivity.Stopped, "finished");
        Assert.Same(last, runtime.State);
    }
}
