using Abacus.Dashboard;
using System.Text.Json;

namespace Abacus.Tests;

public sealed class DashboardForceActionsTests
{
    [Fact]
    public async Task ConcurrentRetriesDispatchOnceAndBoundedLedgerNeverEvictsAcceptedIds()
    {
        var calls = 0;
        var receipt = new SupervisorForceReceipt();
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor> { ["maintenance"] = new(_ => { }, _ => { }, new TaskCompletionSource().Task,
                TrackedForce: _ => { Interlocked.Increment(ref calls); return receipt; }) }, default);
        var actions = new DashboardWorkerActions(() => { }, supervisors: true);
        actions.Bind(router);
        var session = Guid.NewGuid().ToString("N");
        var request = new WorkerActionRequest(session, Guid.NewGuid().ToString(), "force-run", "maintenance", true, "literal prompt");
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => actions.Submit(session, request))));
        Assert.All(results, r => Assert.Equal("accepted", r.Outcome));
        Assert.Equal(1, calls);
        receipt.Finish("completed");
        for (var i = 1; i < 1024; i++)
            Assert.Equal("completed", actions.Submit(session, request with { RequestId = Guid.NewGuid().ToString() }).Outcome);
        Assert.Equal(1024, calls);
        Assert.Equal(503, actions.Submit(session, request with { RequestId = Guid.NewGuid().ToString() }).StatusCode);
        Assert.Equal("completed", actions.Submit(session, request).Outcome);
        Assert.Equal(1024, calls);
        Assert.Equal(32, actions.Snapshot().Length);
        Assert.DoesNotContain("literal prompt", JsonSerializer.Serialize(actions.Snapshot()));
    }

    [Theory]
    [InlineData("completed", 200)]
    [InlineData("cancelled", 200)]
    [InlineData("deferred", 200)]
    [InlineData("failed", 503)]
    [InlineData("outcome-unknown", 503)]
    public void ForceTerminalOutcomesAreNotReplayed(string outcome, int code)
    {
        var calls = 0;
        var receipt = new SupervisorForceReceipt();
        var actions = new DashboardWorkerActions(() => { }, supervisors: true);
        actions.Bind(new(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor> { ["continuation"] = new(_ => { }, _ => { }, new TaskCompletionSource().Task,
                TrackedForce: _ => { calls++; return receipt; }) }, default));
        var session = Guid.NewGuid().ToString("N");
        var request = new WorkerActionRequest(session, Guid.NewGuid().ToString(), "force-run", "continuation", true, "instructions");
        Assert.Equal(202, actions.Submit(session, request).StatusCode);
        receipt.Finish(outcome);
        actions.Stop();
        var result = actions.Submit(session, request);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(code, result.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StrictPromptSchemaRejectsWrongTypesUnexpectedFieldsAndOversize()
    {
        var request = new WorkerActionRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString(), "force-run", "maintenance", true, "--literal\nquotes '");
        WorkerActionRequest Parse(object value)
        {
            using var body = JsonDocument.Parse(JsonSerializer.Serialize(value, DashboardStream.Json));
            return DashboardWorkerActions.Parse(body.RootElement);
        }
        Assert.Equal(request, Parse(request));
        Assert.Throws<ArgumentException>(() => Parse(request with { Prompt = new string('x', 16001) }));
        Assert.Throws<ArgumentException>(() => Parse(request with { Prompt = " " }));
        Assert.Throws<ArgumentException>(() => Parse(request with { Prompt = "x\0y" }));
        Assert.Throws<ArgumentException>(() => Parse(request with { Command = "stop" }));
        Assert.Throws<ArgumentException>(() => Parse(new { request.Session, request.RequestId, request.Command, request.Worker, request.Confirm, prompt = 1 }));
        Assert.Throws<ArgumentException>(() => Parse(new { request.Session, request.RequestId, request.Command, request.Confirm, request.Prompt }));
    }
}
