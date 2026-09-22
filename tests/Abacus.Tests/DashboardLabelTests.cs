using Abacus.Dashboard;
using System.Text.Json;

namespace Abacus.Tests;

public sealed class DashboardLabelTests
{
    private static IssueAction Parse(object fields)
    {
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(fields));
        return IssueActions.Parse(body.RootElement);
    }

    [Theory]
    [InlineData("abacus:needs-user-attention")]
    [InlineData("ABACUS:supervisor-cannot-resolve")]
    [InlineData("gt:slot")]
    [InlineData("feature,abacus:needs-user-attention")]
    [InlineData("\"quoted\"")]
    [InlineData("newline\nvalue")]
    [InlineData(" padded")]
    public void ReservedAndCsvAmbiguousLabelsAreRejected(string label)
    {
        Assert.Throws<ArgumentException>(() => Parse(new { requestId = "unused", expectedRevision = new string('a', 64), action = "edit", addLabels = new[] { label } }));
        Assert.Throws<ArgumentException>(() => Parse(new { requestId = "unused", expectedRevision = new string('a', 64), action = "edit", removeLabels = new[] { label } }));
    }

    [Fact]
    public void DeltasRejectOverlapDuplicatesEmptyEditsAndNonEditActions()
    {
        Assert.Throws<ArgumentException>(() => Beads.LabelDeltaArguments(["feature"], ["feature"]));
        Assert.Throws<ArgumentException>(() => Beads.LabelDeltaArguments(["feature", "feature"], null));
        Assert.Throws<ArgumentException>(() => Beads.LabelDeltaArguments([new string('a', 101)], null));
        Assert.Throws<ArgumentException>(() => Beads.LabelDeltaArguments(Enumerable.Range(0, 33).Select(i => "label" + i).ToArray(), null));
        Assert.Throws<ArgumentException>(() => Parse(new { requestId = "unused", expectedRevision = new string('a', 64), action = "edit", addLabels = Array.Empty<string>() }));
        Assert.Throws<ArgumentException>(() => Parse(new { requestId = "unused", expectedRevision = new string('a', 64), action = "comment", text = "hello", addLabels = new[] { "feature" } }));
        Assert.Throws<ArgumentException>(() => Parse(new { requestId = "unused", expectedRevision = new string('a', 64), action = "edit", addLabels = "feature" }));
    }

    [Theory]
    [InlineData(false, false, "completed")]
    [InlineData(true, false, "partially-applied")]
    [InlineData(false, true, "partially-applied")]
    public async Task DeltaUsesLiteralFlagsAndVerifiesPreservationWithoutOverwritingRacingLabels(bool lostReserved, bool removalFailed, string outcome)
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"in_progress\",\"assignee\":\"worker\",\"metadata\":{\"abacus_target\":\"main\"},\"labels\":[\"abacus:needs-user-attention\",\"unrelated\",\"old\"]}");
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            calls++;
            Assert.Equal(new[] { "update", "web-a", "--json", "--add-label=--leading-option", "--add-label=team:ui", "--remove-label=old" }, args);
            var labels = new List<string> { "unrelated", "--leading-option", "team:ui", "racing-addition" };
            if (!lostReserved) labels.Add("abacus:needs-user-attention");
            if (removalFailed) labels.Add("old");
            state = IssueExport.Parse(JsonSerializer.Serialize(new { id = "web-a", status = "in_progress", assignee = "worker", metadata = new { abacus_target = "main" }, labels }));
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default);
        var request = Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}", expectedRevision = state.Issues["web-a"].Revision,
            action = "edit", addLabels = new[] { "--leading-option", "team:ui" }, removeLabels = new[] { "old" } });
        var result = await actions.SubmitAsync("web-a", request, default);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("worker", result.Issue!.Assignee);
        Assert.Equal("in_progress", result.Issue.Status);
        Assert.Contains("racing-addition", result.Issue.Labels);
        Assert.Same(result, await actions.SubmitAsync("web-a", request, default));
        Assert.Equal(1, calls);
        await actions.DrainAsync();
    }
}
