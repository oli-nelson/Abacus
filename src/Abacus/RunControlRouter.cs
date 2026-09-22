namespace Abacus;

internal sealed record RunControlWorker(AgentControl Control, Task Execution);
internal sealed record RunControlSupervisor(Action<AgentControlAction> Request, Action<string> Force, Task Execution,
    Func<AgentControlAction, AgentControlReceipt>? TrackedRequest = null,
    Func<string, SupervisorForceReceipt>? TrackedForce = null);

/// <summary>
/// Direct in-process control boundary shared by operator interfaces. Returning
/// means accepted by the existing controller, never that cleanup/restart finished.
/// No workspace operations, subprocesses, serialization or network I/O occur here.
/// </summary>
internal sealed class RunControlRouter(
    IReadOnlyDictionary<string, RunControlWorker> workers,
    IReadOnlyDictionary<string, RunControlSupervisor> supervisors,
    CancellationToken lifetime)
{
    public void Request(string name, AgentControlAction action)
    {
        EnsureRunning();
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown worker action.");
        if (supervisors.TryGetValue(name, out var supervisor))
        {
            if (supervisor.Execution.IsCompleted)
                throw new InvalidOperationException($"supervisor '{name}' has finished; start a new run");
            if (action == AgentControlAction.CleanWorkspace)
                throw new InvalidOperationException("Supervisors cannot clean the main checkout; use Stop or Restart.");
            supervisor.Request(action);
            return;
        }
        if (!workers.TryGetValue(name, out var worker)) throw new ArgumentException($"unknown agent '{name}'");
        if (worker.Execution.IsCompleted)
            throw new InvalidOperationException($"agent '{name}' has finished; start a new run");
        if (!worker.Control.TryRequest(action))
            throw new InvalidOperationException($"agent '{name}' already has a pending control request");
    }

    internal AgentControlReceipt RequestTrackedWorker(string name, AgentControlAction action)
    {
        EnsureRunning();
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown worker action.");
        if (!workers.TryGetValue(name, out var worker))
            throw new ArgumentException("Tracked worker controls require a known worker, not a supervisor.");
        if (worker.Execution.IsCompleted) throw new InvalidOperationException("Worker has finished; start a new run.");
        return worker.Control.TryRequestTracked(action)
            ?? throw new InvalidOperationException("Worker already has a pending control request.");
    }

    internal AgentControlReceipt RequestTrackedSupervisor(string name, AgentControlAction action)
    {
        EnsureRunning();
        if (action is not (AgentControlAction.Stop or AgentControlAction.Restart))
            throw new ArgumentException("Supervisors cannot clean the main checkout.");
        if (!supervisors.TryGetValue(name, out var supervisor))
            throw new ArgumentException("Unknown or disabled supervisor.");
        if (supervisor.Execution.IsCompleted || supervisor.TrackedRequest is null)
            throw new InvalidOperationException("Supervisor is finished or tracked controls are unavailable.");
        return supervisor.TrackedRequest(action);
    }

    internal SupervisorForceReceipt ForceSupervisorTracked(string name, string prompt)
    {
        EnsureRunning();
        if (!supervisors.TryGetValue(name, out var supervisor)) throw new ArgumentException("Unknown or disabled supervisor.");
        if (supervisor.Execution.IsCompleted || supervisor.TrackedForce is null)
            throw new InvalidOperationException("Supervisor is finished or tracked force controls are unavailable.");
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Force-run prompt must not be empty.");
        return supervisor.TrackedForce(prompt);
    }

    public void ForceSupervisor(string name, string prompt)
    {
        EnsureRunning();
        if (!supervisors.TryGetValue(name, out var supervisor))
            throw new ArgumentException($"unknown or disabled supervisor '{name}'");
        if (supervisor.Execution.IsCompleted)
            throw new InvalidOperationException($"supervisor '{name}' has finished; start a new run");
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("force-run prompt must not be empty");
        supervisor.Force(prompt);
    }

    private void EnsureRunning()
    {
        if (lifetime.IsCancellationRequested)
            throw new InvalidOperationException("The run is stopping; no new control request was accepted.");
    }
}
