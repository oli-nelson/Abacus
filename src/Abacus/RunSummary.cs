namespace Abacus;

public enum TicketOutcome
{
    Closed,
    Reopened,
    Blocked,
    Interrupted,
}

public sealed record AgentRunSummary(
    string AgentName,
    int Closed,
    int Reopened,
    int Blocked,
    int Interrupted)
{
    public int Total => Closed + Reopened + Blocked + Interrupted;
}

public sealed record RunSummarySnapshot(
    TimeSpan Elapsed,
    IReadOnlyList<AgentRunSummary> Agents)
{
    public int Total => Agents.Sum(static agent => agent.Total);
}

public sealed class RunSummary
{
    private readonly object gate = new();
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, MutableAgentSummary> agents;

    public RunSummary(IEnumerable<string> agentNames)
    {
        agents = agentNames.ToDictionary(
            static name => name,
            static _ => new MutableAgentSummary(),
            StringComparer.Ordinal);
    }

    public void Record(string agentName, TicketOutcome outcome)
    {
        lock (gate)
        {
            if (!agents.TryGetValue(agentName, out var summary))
            {
                summary = new MutableAgentSummary();
                agents.Add(agentName, summary);
            }

            switch (outcome)
            {
                case TicketOutcome.Closed:
                    summary.Closed++;
                    break;
                case TicketOutcome.Reopened:
                    summary.Reopened++;
                    break;
                case TicketOutcome.Blocked:
                    summary.Blocked++;
                    break;
                case TicketOutcome.Interrupted:
                    summary.Interrupted++;
                    break;
            }
        }
    }

    public RunSummarySnapshot Snapshot()
    {
        lock (gate)
        {
            return new RunSummarySnapshot(
                DateTimeOffset.UtcNow - startedAt,
                agents.Select(static pair => new AgentRunSummary(
                    pair.Key,
                    pair.Value.Closed,
                    pair.Value.Reopened,
                    pair.Value.Blocked,
                    pair.Value.Interrupted)).ToArray());
        }
    }

    private sealed class MutableAgentSummary
    {
        public int Closed { get; set; }
        public int Reopened { get; set; }
        public int Blocked { get; set; }
        public int Interrupted { get; set; }
    }
}
