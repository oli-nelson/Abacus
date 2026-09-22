using Abacus.Dashboard;
using System.Text.Json;

namespace Abacus.Tests;

public sealed class DashboardWorkerActionsTests
{
    [Fact]
    public void CancellationCallbackFailureRetainsUnknownOutcomeInsteadOfReplaying()
    {
        using var control = new AgentControl();
        using var operation = control.CreateOperationCancellation(default);
        using var callback = operation.Token.Register(() => throw new InvalidOperationException("fixture"));
        var actions = new DashboardWorkerActions(() => { });
        actions.Bind(new(new Dictionary<string, RunControlWorker> { ["alice"] = new(control, new TaskCompletionSource().Task) },
            new Dictionary<string, RunControlSupervisor>(), default));
        var session = Guid.NewGuid().ToString("N");
        var request = new WorkerActionRequest(session, Guid.NewGuid().ToString(), "stop", "alice", false);
        Assert.Equal("outcome-unknown", actions.Submit(session, request).Outcome);
        Assert.Equal(AgentControlAction.Stop, control.TakeRequestedAction());
        control.CompleteTrackedAction(AgentControlAction.Stop);
        Assert.Equal("outcome-unknown", actions.Submit(session, request).Outcome);
        Assert.Null(control.TakeRequestedAction());
    }

    [Fact]
    public async Task AcceptedReceiptCompletesAndRetriesNeverReapplyIt()
    {
        using var control = new AgentControl();
        var hints = 0;
        var actions = new DashboardWorkerActions(() => Interlocked.Increment(ref hints));
        actions.Bind(new(new Dictionary<string, RunControlWorker> { ["alice"] = new(control, new TaskCompletionSource().Task) },
            new Dictionary<string, RunControlSupervisor>(), default));
        var session = Guid.NewGuid().ToString("N");
        var request = new WorkerActionRequest(session, Guid.NewGuid().ToString(), "stop", "alice", false);
        Assert.Equal(202, actions.Submit(session, request).StatusCode);
        Assert.Equal("accepted", Assert.Single(actions.Snapshot()).Outcome);
        Assert.Equal(AgentControlAction.Stop, control.TakeRequestedAction());
        Assert.Equal(202, actions.Submit(session, request).StatusCode);
        control.CompleteTrackedAction(AgentControlAction.Stop);
        Assert.Equal("completed", actions.Submit(session, request).Outcome);
        Assert.Null(control.TakeRequestedAction());
        Assert.Equal(400, actions.Submit(session, request with { Command = "restart" }).StatusCode);
        actions.Stop();
        Assert.Equal("completed", actions.Submit(session, request).Outcome);
        Assert.Equal(503, actions.Submit(session, request with { RequestId = Guid.NewGuid().ToString() }).StatusCode);
        await Task.Yield();
        Assert.True(hints >= 2);
    }

    [Fact]
    public void CleaningRequiresConfirmationAndUnknownOutcomesRemainExplicit()
    {
        using var control = new AgentControl();
        var actions = new DashboardWorkerActions(() => { });
        actions.Bind(new(new Dictionary<string, RunControlWorker> { ["alice"] = new(control, new TaskCompletionSource().Task) },
            new Dictionary<string, RunControlSupervisor>(), default));
        var session = Guid.NewGuid().ToString("N");
        var request = new WorkerActionRequest(session, Guid.NewGuid().ToString(), "clean-workspace", "alice", false);
        Assert.Equal(400, actions.Submit(session, request).StatusCode);
        Assert.Null(control.TakeRequestedAction());
        request = request with { Confirm = true };
        Assert.Equal(202, actions.Submit(session, request).StatusCode);
        control.Dispose();
        Assert.Equal("outcome-unknown", actions.Submit(session, request).Outcome);
        Assert.Equal(409, actions.Submit("different", request).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"command\":\"stop\",\"worker\":\"alice\"}")]
    public void InvalidSchemaIsRejected(string json)
    {
        using var body = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => DashboardWorkerActions.Parse(body.RootElement));
    }
}
