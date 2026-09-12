namespace Abacus;

/// <summary>
/// Tracks which configured agents currently have a hosted agent CLI process.
/// The merge-slot reclaimer uses it to tell a live merge claim from one left
/// behind by a harness that exited, was stopped, or crashed.
/// </summary>
public sealed class AgentRunRegistry
{
    private readonly object gate = new();
    private readonly HashSet<string> running = new(StringComparer.Ordinal);

    public void MarkRunning(string agentName)
    {
        lock (gate)
        {
            running.Add(agentName);
        }
    }

    public void MarkStopped(string agentName)
    {
        lock (gate)
        {
            running.Remove(agentName);
        }
    }

    public bool IsRunning(string agentName)
    {
        lock (gate)
        {
            return running.Contains(agentName);
        }
    }
}
