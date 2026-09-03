using Abacus;

namespace Abacus.Tests;

public sealed class RunSummaryTests
{
    [Fact]
    public void RecordsPerAgentAndAggregateOutcomes()
    {
        var summary = new RunSummary(["bob", "alice"], "baseline-commit");

        summary.Record("alice", TicketOutcome.Closed);
        summary.Record("alice", TicketOutcome.Reopened);
        summary.Record("bob", TicketOutcome.Blocked);
        summary.Record("bob", TicketOutcome.Interrupted);

        var snapshot = summary.Snapshot();

        Assert.Equal(4, snapshot.Total);
        Assert.Equal("baseline-commit", snapshot.InitialDoltCommit);
        Assert.True(snapshot.Elapsed >= TimeSpan.Zero);
        Assert.Collection(
            snapshot.Agents,
            bob => Assert.Equal(new AgentRunSummary("bob", 0, 0, 1, 1), bob),
            alice => Assert.Equal(new AgentRunSummary("alice", 1, 1, 0, 0), alice));
    }
}
