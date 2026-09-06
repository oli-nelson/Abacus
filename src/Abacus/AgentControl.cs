namespace Abacus;

public enum AgentControlAction
{
    Stop,
    Restart,
    CleanWorkspace,
}

public sealed class AgentControl : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource interruption = new();
    private TaskCompletionSource requestAvailable = NewSignal();
    private AgentControlAction? requestedAction;
    private AgentControlAction? interruptionAction;
    private bool disposed;

    public bool ShouldPreserveClaimOnInterruption
    {
        get
        {
            lock (gate)
            {
                return interruptionAction is AgentControlAction.Stop or AgentControlAction.Restart;
            }
        }
    }

    public void Request(AgentControlAction action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            requestedAction = action;
            interruptionAction ??= action;
            requestAvailable.TrySetResult();
            interruption.Cancel();
        }
    }

    public CancellationTokenSource CreateOperationCancellation(
        CancellationToken applicationCancellation)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(
                applicationCancellation,
                interruption.Token);
        }
    }

    public AgentControlAction? TakeRequestedAction()
    {
        lock (gate)
        {
            if (requestedAction is not { } action)
            {
                return null;
            }

            requestedAction = null;
            interruptionAction = null;
            interruption.Dispose();
            interruption = new CancellationTokenSource();
            requestAvailable = NewSignal();
            return action;
        }
    }

    public async Task<AgentControlAction> WaitForRequestedActionAsync(
        CancellationToken cancellationToken)
    {
        Task signal;
        lock (gate)
        {
            if (requestedAction is not null)
            {
                return TakeRequestedAction()!.Value;
            }

            signal = requestAvailable.Task;
        }

        await signal.WaitAsync(cancellationToken);
        return TakeRequestedAction()
            ?? throw new InvalidOperationException("agent action signal completed without an action");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            interruption.Cancel();
            interruption.Dispose();
            requestAvailable.TrySetCanceled();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
