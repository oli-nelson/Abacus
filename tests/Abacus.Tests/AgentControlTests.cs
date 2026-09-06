using Abacus;

namespace Abacus.Tests;

public sealed class AgentControlTests
{
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
