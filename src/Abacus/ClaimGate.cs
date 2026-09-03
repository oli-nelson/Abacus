namespace Abacus;

public sealed class ClaimGate
{
    private readonly object gate = new();
    private bool enabled = true;
    private TaskCompletionSource enabledSignal = CompletedSignal();

    public bool IsEnabled
    {
        get
        {
            lock (gate)
            {
                return enabled;
            }
        }
    }

    public bool Toggle()
    {
        lock (gate)
        {
            SetEnabledCore(!enabled);
            return enabled;
        }
    }

    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            SetEnabledCore(value);
        }
    }

    public Task WaitUntilEnabledAsync(CancellationToken cancellationToken)
    {
        Task signal;
        lock (gate)
        {
            signal = enabledSignal.Task;
        }

        return signal.WaitAsync(cancellationToken);
    }

    private void SetEnabledCore(bool value)
    {
        if (enabled == value)
        {
            return;
        }

        enabled = value;
        if (enabled)
        {
            enabledSignal.TrySetResult();
        }
        else
        {
            enabledSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
}
