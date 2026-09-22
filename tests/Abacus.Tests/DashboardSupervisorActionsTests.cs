using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardSupervisorActionsTests
{
    [Fact]
    public async Task HttpSupervisorActionsKeepOriginSchemaAndTargetBoundaries()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        using var control = new AgentControl();
        var runtime = new DashboardRuntime(output);
        var forced = new SupervisorForceReceipt();
        string? prompt = null;
        var forceCalls = 0;
        runtime.SupervisorActions.Bind(new(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor> { ["maintenance"] = new(_ => { }, _ => { }, new TaskCompletionSource().Task,
                action => control.TryRequestTracked(action) ?? throw new InvalidOperationException(),
                text => { forceCalls++; prompt = text; return forced; }) }, default));
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        var binding = new DashboardBinding([System.Net.IPAddress.Loopback], new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" }, port);
        await using var host = await DashboardHost.StartAsync(binding, new DashboardStream(), "fixture", "operator", default, integrated: true, runtime: runtime);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(binding.Urls.Single()) };
        var request = new WorkerActionRequest(runtime.Instance, Guid.NewGuid().ToString(), "stop", "maintenance", false);
        async Task<int> Post(WorkerActionRequest action, string path = "/api/v1/runtime/supervisors/actions", string? origin = null)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
                { Content = System.Net.Http.Json.JsonContent.Create(action, options: DashboardStream.Json) };
            message.Headers.Add("X-Abacus-Request", "1");
            if (origin is not null) message.Headers.Add("Origin", origin);
            using var response = await http.SendAsync(message);
            return (int)response.StatusCode;
        }
        Assert.Equal(403, await Post(request, origin: "https://attacker.invalid"));
        Assert.Equal(400, await Post(request with { Command = "force-run" }));
        Assert.Equal(400, await Post(request with { Command = "clean-workspace", Confirm = true }));
        Assert.Equal(400, await Post(request with { Worker = "alice" }));
        Assert.Null(control.TakeRequestedAction());
        Assert.Equal(202, await Post(request));
        Assert.Equal(AgentControlAction.Stop, control.TakeRequestedAction());
        Assert.Equal(202, await Post(request));
        control.CompleteTrackedAction(AgentControlAction.Stop);
        Assert.Equal(200, await Post(request));
        Assert.Null(control.TakeRequestedAction());
        var force = request with { RequestId = Guid.NewGuid().ToString(), Command = "force-run", Confirm = true, Prompt = "--literal secret\n$(not shell)" };
        Assert.Equal(400, await Post(force with { Confirm = false }));
        Assert.Equal(202, await Post(force));
        Assert.Equal(force.Prompt, prompt);
        Assert.Equal(202, await Post(force));
        Assert.Equal(1, forceCalls);
        Assert.Equal(400, await Post(force with { Prompt = "changed prompt" }));
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(runtime.Refresh().Body));
        Assert.Equal(409, await Post(force with { RequestId = Guid.NewGuid().ToString() }));
        var interrupt = request with { RequestId = Guid.NewGuid().ToString() };
        Assert.Equal(202, await Post(interrupt)); // pending force must not prevent Stop
        control.TakeRequestedAction(); control.CompleteTrackedAction(AgentControlAction.Stop);
        forced.Finish("outcome-unknown");
        Assert.Equal(503, await Post(force));
        Assert.Equal(1, forceCalls);
        runtime.StopControls();
        Assert.Equal(503, await Post(request with { RequestId = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public void SupervisorLedgerRejectsCleaningWorkersAndReplaysWithoutReapplying()
    {
        using var control = new AgentControl();
        var actions = new DashboardWorkerActions(() => { }, supervisors: true);
        var execution = new TaskCompletionSource();
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor>
            {
                ["maintenance"] = new(_ => { }, _ => { }, execution.Task,
                    action => control.TryRequestTracked(action) ?? throw new InvalidOperationException()),
            }, default);
        actions.Bind(router);
        var session = Guid.NewGuid().ToString("N");
        var request = new WorkerActionRequest(session, Guid.NewGuid().ToString(), "stop", "maintenance", false);
        Assert.Equal(400, actions.Submit(session, request with { Worker = "alice" }).StatusCode);
        Assert.Equal(400, actions.Submit(session, request with { Command = "clean-workspace", Confirm = true }).StatusCode);
        Assert.Null(control.TakeRequestedAction());
        Assert.Equal("accepted", actions.Submit(session, request).Outcome);
        Assert.Equal(409, actions.Submit(session, request with { RequestId = Guid.NewGuid().ToString(), Command = "restart" }).StatusCode);
        Assert.Equal(AgentControlAction.Stop, control.TakeRequestedAction());
        Assert.Equal("accepted", actions.Submit(session, request).Outcome);
        control.CompleteTrackedAction(AgentControlAction.Stop);
        var completed = actions.Submit(session, request);
        Assert.Equal("completed", completed.Outcome);
        Assert.Contains("Supervisor", completed.Message);
        Assert.Null(control.TakeRequestedAction());
        Assert.Equal(400, actions.Submit(session, request with { Command = "restart" }).StatusCode);
        execution.SetResult();
        Assert.Equal(409, actions.Submit(session, request with { RequestId = Guid.NewGuid().ToString() }).StatusCode);
    }

    [Fact]
    public void RuntimePublishesSupervisorReceiptsSeparatelyAndOnlyWhenChanged()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", false, interactive: false);
        using var control = new AgentControl();
        var runtime = new DashboardRuntime(output);
        runtime.SupervisorActions.Bind(new(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor> { ["continuation"] = new(_ => { }, _ => { }, new TaskCompletionSource().Task,
                action => control.TryRequestTracked(action)!) }, default));
        var request = new WorkerActionRequest(runtime.Instance, Guid.NewGuid().ToString(), "restart", "continuation", false);
        runtime.SupervisorActions.Submit(runtime.Instance, request);
        var accepted = runtime.Refresh();
        Assert.Equal("accepted", Assert.Single(accepted.View.SupervisorActions).Outcome);
        Assert.Empty(accepted.View.WorkerActions);
        Assert.Same(accepted, runtime.Refresh());
        control.TakeRequestedAction();
        control.CompleteTrackedAction(AgentControlAction.Restart);
        Assert.Equal("completed", Assert.Single(runtime.Refresh().View.SupervisorActions).Outcome);
        Assert.Contains("no new harness", runtime.SupervisorActions.Submit(runtime.Instance, request).Message);
        runtime.StopControls();
        Assert.False(runtime.Refresh().View.SupervisorControlsAvailable);
    }
}
