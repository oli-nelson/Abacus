namespace Abacus.Tests;

public sealed class RunControlRouterTests
{
    [Fact]
    public void TrackedForceUsesConfiguredSupervisorAndPreservesLiteralPrompt()
    {
        using var stop = new CancellationTokenSource();
        var execution = new TaskCompletionSource();
        var receipt = new SupervisorForceReceipt();
        string? delivered = null;
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor>
            {
                ["maintenance"] = new(_ => { }, _ => { }, execution.Task, TrackedForce: prompt => { delivered = prompt; return receipt; }),
            }, stop.Token);
        const string prompt = "--literal 'quotes'\n$(not a shell command)";
        Assert.Same(receipt, router.ForceSupervisorTracked("maintenance", prompt));
        Assert.Equal(prompt, delivered);
        Assert.False(receipt.Completion.IsCompleted);
        Assert.Throws<ArgumentException>(() => router.ForceSupervisorTracked("alice", prompt));
        Assert.Throws<ArgumentException>(() => router.ForceSupervisorTracked("maintenance", "  "));
        execution.SetResult();
        Assert.Throws<InvalidOperationException>(() => router.ForceSupervisorTracked("maintenance", prompt));
        stop.Cancel();
        Assert.Throws<InvalidOperationException>(() => router.ForceSupervisorTracked("maintenance", prompt));
        Assert.False(receipt.Completion.IsCompleted); // routing cannot manufacture completion
    }

    [Fact]
    public async Task TrackedWorkerRoutingRetainsPendingActionUntilWorkerAcknowledges()
    {
        using var control = new AgentControl();
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker>
            { ["alice"] = new(control, new TaskCompletionSource().Task) },
            new Dictionary<string, RunControlSupervisor>(), default);
        var receipt = router.RequestTrackedWorker("alice", AgentControlAction.Stop);
        Assert.False(receipt.Completion.IsCompleted);
        control.TakeRequestedAction();
        Assert.Throws<InvalidOperationException>(() => router.Request("alice", AgentControlAction.Restart));
        Assert.Throws<ArgumentException>(() => router.RequestTrackedWorker("unknown", AgentControlAction.Stop));
        control.CompleteTrackedAction(AgentControlAction.Stop);
        Assert.Equal("completed", (await receipt.Completion).Outcome);
        router.Request("alice", AgentControlAction.Restart);
    }

    [Fact]
    public void RequestsAreAcceptedNotCompletedAndKeepExistingPendingRules()
    {
        using var control = new AgentControl();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker> { ["alice"] = new(control, execution.Task) },
            new Dictionary<string, RunControlSupervisor>(), default);
        router.Request("alice", AgentControlAction.Stop);
        Assert.False(execution.Task.IsCompleted);
        Assert.True(control.ShouldPreserveClaimOnInterruption);
        Assert.Throws<InvalidOperationException>(() => router.Request("alice", AgentControlAction.Restart));
        Assert.Equal(AgentControlAction.Stop, control.TakeRequestedAction());
        router.Request("alice", AgentControlAction.Restart);
        Assert.Equal(AgentControlAction.Restart, control.TakeRequestedAction());
        execution.SetResult();
        Assert.Throws<InvalidOperationException>(() => router.Request("alice", AgentControlAction.CleanWorkspace));
        Assert.Throws<ArgumentException>(() => router.Request("unknown", AgentControlAction.Stop));
        Assert.Throws<ArgumentException>(() => router.Request("alice", (AgentControlAction)999));
        Assert.Throws<ArgumentException>(() => router.ForceSupervisor("alice", "prompt"));
    }

    [Fact]
    public void SupervisorRoutesPreserveLiteralPromptAndNeverCleanMainCheckout()
    {
        var actions = new List<AgentControlAction>();
        var prompts = new List<string>();
        var execution = new TaskCompletionSource();
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker>(),
            new Dictionary<string, RunControlSupervisor> { ["supervisor"] = new(actions.Add, prompts.Add, execution.Task) }, default);
        router.Request("supervisor", AgentControlAction.Stop);
        router.Request("supervisor", AgentControlAction.Restart);
        Assert.Throws<InvalidOperationException>(() => router.Request("supervisor", AgentControlAction.CleanWorkspace));
        Assert.Equal(new[] { AgentControlAction.Stop, AgentControlAction.Restart }, actions);
        const string prompt = "--literal <script> $HOME; not a command";
        router.ForceSupervisor("supervisor", prompt);
        Assert.Equal(prompt, Assert.Single(prompts));
        Assert.Throws<ArgumentException>(() => router.ForceSupervisor("supervisor", " "));
        Assert.Throws<ArgumentException>(() => router.ForceSupervisor("disabled", prompt));
        execution.SetResult();
        Assert.Throws<InvalidOperationException>(() => router.Request("supervisor", AgentControlAction.Restart));
        Assert.Throws<InvalidOperationException>(() => router.ForceSupervisor("supervisor", prompt));
        Assert.Single(prompts);
    }

    [Fact]
    public void ShutdownRejectsBothWorkerAndSupervisorRequests()
    {
        using var control = new AgentControl();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var router = new RunControlRouter(new Dictionary<string, RunControlWorker>
            { ["alice"] = new(control, new TaskCompletionSource().Task) },
            new Dictionary<string, RunControlSupervisor> { ["supervisor"] = new(_ => calls++, _ => calls++, new TaskCompletionSource().Task) }, stop.Token);
        stop.Cancel();
        Assert.Throws<InvalidOperationException>(() => router.Request("alice", AgentControlAction.Stop));
        Assert.Throws<InvalidOperationException>(() => router.Request("supervisor", AgentControlAction.Restart));
        Assert.Throws<InvalidOperationException>(() => router.ForceSupervisor("supervisor", "prompt"));
        Assert.Null(control.TakeRequestedAction());
        Assert.Equal(0, calls);
    }
}
