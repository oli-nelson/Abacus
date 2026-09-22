using Abacus.Dashboard;
using System.Text.Json;

namespace Abacus.Tests;

public sealed class DashboardRunActionsTests
{
    [Fact]
    public async Task ConfirmedStopIsAcceptedOnceAndNeverClaimsCleanupCompleted()
    {
        var actions = new DashboardRunActions(() => { });
        var calls = 0;
        actions.Bind(() => Interlocked.Increment(ref calls));
        var session = Guid.NewGuid().ToString("N");
        var request = new RunActionRequest(session, Guid.NewGuid().ToString(), "stop", true);
        Assert.Equal(400, actions.Submit(session, request with { Confirm = false }).StatusCode);
        Assert.Equal(409, actions.Submit(session, request with { Session = Guid.NewGuid().ToString("N") }).StatusCode);
        Assert.Equal("accepted", actions.Submit(session, request).Outcome);
        Assert.Equal(0, calls); // response can flush before cancellation
        Assert.False(actions.Available);
        Assert.Equal(400, actions.Submit(session, request with { Confirm = false }).StatusCode);
        Assert.Equal(503, actions.Submit(session, request with { RequestId = Guid.NewGuid().ToString() }).StatusCode);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(actions.DispatchAccepted)));
        actions.Stop();
        Assert.Equal("accepted", actions.Submit(session, request).Outcome);
        actions.DispatchAccepted();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CancellationCallbackFailureNeverReplaysShutdown()
    {
        var actions = new DashboardRunActions(() => { });
        var calls = 0;
        actions.Bind(() => { calls++; throw new AggregateException(new InvalidOperationException()); });
        var session = Guid.NewGuid().ToString("N");
        var request = new RunActionRequest(session, Guid.NewGuid().ToString(), "stop", true);
        actions.Submit(session, request);
        actions.DispatchAccepted(); actions.DispatchAccepted();
        Assert.Equal(1, calls);
        Assert.Equal("accepted", actions.Submit(session, request).Outcome);
    }

    [Fact]
    public void UnboundAndStoppedRunsRejectWithoutDispatch()
    {
        var actions = new DashboardRunActions(() => { });
        var session = Guid.NewGuid().ToString("N");
        var request = new RunActionRequest(session, Guid.NewGuid().ToString(), "stop", true);
        Assert.Equal(503, actions.Submit(session, request).StatusCode);
        var calls = 0;
        actions.Bind(() => calls++);
        actions.Stop();
        Assert.Equal(503, actions.Submit(session, request).StatusCode);
        actions.DispatchAccepted();
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => actions.Bind(() => { }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"session\":\"bad\",\"requestId\":\"bad\",\"command\":\"stop\",\"confirm\":true}")]
    public void InvalidBodiesAreRejected(string json)
    {
        using var body = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => DashboardRunActions.Parse(body.RootElement));
    }
}
