namespace Abacus;

public enum AgentControlAction
{
    Stop,
    Restart,
    CleanWorkspace,
}

internal sealed record AgentControlOutcome(string Outcome);
internal sealed class AgentControlReceipt(AgentControlAction action)
{
    private readonly TaskCompletionSource<AgentControlOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AgentControlAction Action { get; } = action;
    public Task<AgentControlOutcome> Completion => completion.Task;
    internal void Finish(string outcome) => completion.TrySetResult(new(outcome));
}

public sealed class AgentControl : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource interruption = new();
    private TaskCompletionSource requestAvailable = NewSignal();
    private AgentControlAction? requestedAction;
    private AgentControlAction? interruptionAction;
    private bool disposed;
    private AgentControlReceipt? queuedReceipt;
    private AgentControlReceipt? activeReceipt;

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

    public bool TryRequest(AgentControlAction action)
    {
        lock (gate)
        {
            if (requestedAction is not null || activeReceipt is not null) return false;
            Request(action);
            return true;
        }
    }

    internal AgentControlReceipt? TryRequestTracked(AgentControlAction action)
    {
        lock (gate)
        {
            if (requestedAction is not null || activeReceipt is not null) return null;
            ObjectDisposedException.ThrowIf(disposed, this);
            var receipt = queuedReceipt = new(action);
            RequestCore(action);
            return receipt;
        }
    }

    internal void CompleteTrackedAction(AgentControlAction action, bool succeeded = true)
    {
        lock (gate)
        {
            if (activeReceipt is null || activeReceipt.Action != action) return;
            activeReceipt.Finish(succeeded ? "completed" : "failed");
            activeReceipt = null;
        }
    }

    internal void AbandonTrackedActions()
    {
        lock (gate)
        {
            queuedReceipt?.Finish("outcome-unknown");
            activeReceipt?.Finish("outcome-unknown");
            queuedReceipt = activeReceipt = null;
        }
    }

    public void Request(AgentControlAction action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (queuedReceipt is not null || activeReceipt is not null)
                throw new InvalidOperationException("A tracked worker action is still pending.");
            RequestCore(action);
        }
    }

    // The caller owns gate. Install a tracked receipt before signalling because
    // cancellation callbacks may synchronously observe/consume the request.
    private void RequestCore(AgentControlAction action)
    {
        requestedAction = action;
        interruptionAction ??= action;
        requestAvailable.TrySetResult();
        interruption.Cancel();
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

            activeReceipt = queuedReceipt;
            queuedReceipt = null;
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
            AbandonTrackedActions();
            interruption.Cancel();
            interruption.Dispose();
            requestAvailable.TrySetCanceled();
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
