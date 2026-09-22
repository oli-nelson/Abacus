using Abacus;

namespace Abacus.Tests;

public sealed class AgentControlTests
{
    [Fact]
    public async Task ReceiptIsInstalledBeforeSynchronousCancellationObserversRun()
    {
        using var control = new AgentControl();
        using var operation = control.CreateOperationCancellation(default);
        using var observer = operation.Token.Register(() => control.TakeRequestedAction());
        var receipt = control.TryRequestTracked(AgentControlAction.Stop)!;
        Assert.False(receipt.Completion.IsCompleted);
        Assert.False(control.TryRequest(AgentControlAction.Restart));
        control.CompleteTrackedAction(AgentControlAction.Stop);
        Assert.Equal("completed", (await receipt.Completion).Outcome);
    }

    [Theory]
    [InlineData(AgentControlAction.Stop, true, "completed")]
    [InlineData(AgentControlAction.Restart, true, "completed")]
    [InlineData(AgentControlAction.CleanWorkspace, true, "completed")]
    [InlineData(AgentControlAction.CleanWorkspace, false, "failed")]
    public async Task TrackedActionsCompleteOnlyAtExplicitWorkerAcknowledgement(AgentControlAction action, bool succeeded, string outcome)
    {
        using var control = new AgentControl();
        using var operation = control.CreateOperationCancellation(default);
        var receipt = Assert.IsType<AgentControlReceipt>(control.TryRequestTracked(action));
        Assert.True(operation.IsCancellationRequested);
        Assert.False(receipt.Completion.IsCompleted);
        Assert.Null(control.TryRequestTracked(action));
        Assert.False(control.TryRequest(action));
        Assert.Equal(action, control.TakeRequestedAction());
        Assert.False(receipt.Completion.IsCompleted); // dequeued is not completed
        Assert.False(control.TryRequest(action));
        Assert.Throws<InvalidOperationException>(() => control.Request(action));
        var wrong = action == AgentControlAction.Stop ? AgentControlAction.Restart : AgentControlAction.Stop;
        control.CompleteTrackedAction(wrong);
        Assert.False(receipt.Completion.IsCompleted);
        control.CompleteTrackedAction(action, succeeded);
        Assert.Equal(outcome, (await receipt.Completion).Outcome);
        Assert.True(control.TryRequest(AgentControlAction.Restart));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownNeverClaimsUnacknowledgedTrackedActionSucceeded(bool consumed)
    {
        var control = new AgentControl();
        var receipt = control.TryRequestTracked(AgentControlAction.Stop)!;
        if (consumed) control.TakeRequestedAction();
        control.Dispose();
        Assert.Equal("outcome-unknown", (await receipt.Completion).Outcome);
    }

    [Theory]
    [InlineData(AgentControlAction.Stop, true)]
    [InlineData(AgentControlAction.Restart, true)]
    [InlineData(AgentControlAction.CleanWorkspace, false)]
    public void LifecycleRequestsInterruptCurrentOperationAndSetReservationPolicy(
        AgentControlAction action,
        bool shouldPreserveClaim)
    {
        using var control = new AgentControl();
        using var operation = control.CreateOperationCancellation(CancellationToken.None);

        control.Request(action);

        Assert.True(operation.IsCancellationRequested);
        Assert.Equal(shouldPreserveClaim, control.ShouldPreserveClaimOnInterruption);
        Assert.Equal(action, control.TakeRequestedAction());
        Assert.False(control.ShouldPreserveClaimOnInterruption);
    }

    [Fact]
    public async Task StoppedAgentCanWaitForRestartRequest()
    {
        using var control = new AgentControl();
        var waiting = control.WaitForRequestedActionAsync(CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        control.Request(AgentControlAction.Restart);

        Assert.Equal(AgentControlAction.Restart, await waiting);
    }

    [Fact]
    public void FirstInterruptionKeepsItsTicketPolicyWhileLatestActionWins()
    {
        using var control = new AgentControl();
        using var operation = control.CreateOperationCancellation(CancellationToken.None);

        control.Request(AgentControlAction.Stop);
        control.Request(AgentControlAction.CleanWorkspace);

        Assert.True(control.ShouldPreserveClaimOnInterruption);
        Assert.Equal(AgentControlAction.CleanWorkspace, control.TakeRequestedAction());
    }
}
