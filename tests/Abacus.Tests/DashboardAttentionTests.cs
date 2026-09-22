using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardAttentionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExactResponsePrecedesLabelAndReviewedRecoveryNeverDuplicatesComment(bool resolving, bool failLabel)
    {
        var flag = resolving; var fail = failLabel; var calls = new List<string[]>(); var comments = new List<object>();
        IssueSnapshot Snapshot() => IssueExport.Parse(JsonSerializer.Serialize(new {
            id = "web-a", status = "blocked", assignee = "worker",
            labels = flag ? new[] { "keep", Beads.NeedsUserAttentionLabel } : new[] { "keep" }, comments }));
        var actions = new IssueActions(_ => Task.FromResult(Snapshot()), (args, _) =>
        {
            calls.Add(args.ToArray());
            Assert.DoesNotContain("--status", args); Assert.DoesNotContain("--assignee", args);
            if (args[0] == "comment")
            {
                Assert.Equal(new[] { "comment", "--json", "web-a", "--", "--literal $HOME <b>text</b>" }, args);
                comments.Add(new { id = "stored", text = args[^1] });
            }
            else
            {
                if (fail) return Task.FromResult(new CommandResult(1, "", "private diagnostic"));
                flag = args.Contains("--add-label");
            }
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default, attentionEnabled: true);
        IssueAction Action(string? text, string? existing = null) => new(
            $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}", Snapshot().Issues["web-a"].Revision,
            resolving ? "attention-resolve" : "attention-request", text, null, null, null, null, existing);
        var action = Action("--literal $HOME <b>text</b>");
        var result = await actions.SubmitAsync("web-a", action, default);
        Assert.Equal(fail ? "partially-applied" : "completed", result.Outcome);
        Assert.Equal("stored", result.RecordedCommentId); Assert.Single(comments);
        Assert.Equal("blocked", result.Issue!.Status); Assert.Equal("worker", result.Issue.Assignee);
        Assert.Contains("keep", result.Issue.Labels); Assert.DoesNotContain("private", result.Message);
        Assert.Same(result, await actions.SubmitAsync("web-a", action, default)); Assert.Equal(2, calls.Count);
        if (fail)
        {
            fail = false;
            var finish = await actions.SubmitAsync("web-a", Action(null, resolving ? null : "stored"), default);
            Assert.Equal("completed", finish.Outcome); Assert.Single(comments);
            Assert.Equal(3, calls.Count); Assert.Equal("update", calls[^1][0]);
        }
        Assert.Equal(!resolving, flag);
    }

    [Fact]
    public async Task FailedCommentPreventsLabelChange()
    {
        var snapshot = IssueExport.Parse("{\"id\":\"web-a\",\"labels\":[\"abacus:needs-user-attention\"]}");
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(snapshot), (args, _) => {
            calls++; Assert.Equal("comment", args[0]); return Task.FromResult(new CommandResult(1, "", ""));
        }, () => true, () => { }, default, attentionEnabled: true);
        var action = new IssueAction($"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            snapshot.Issues["web-a"].Revision, "attention-resolve", "response", null, null, null, null);
        Assert.NotEqual("completed", (await actions.SubmitAsync("web-a", action, default)).Outcome);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("attention-request", "")]
    [InlineData("attention-request", ",\"text\":\"x\",\"existingCommentId\":\"y\"")]
    [InlineData("attention-resolve", ",\"reopen\":true")]
    [InlineData("attention-resolve", ",\"title\":\"x\"")]
    [InlineData("attention-resolve", ",\"existingCommentId\":\"x\"")]
    public void SchemaRejectsMissingExplanationAndMixedOrOwnershipFields(string action, string extra)
    {
        using var body = JsonDocument.Parse("{\"requestId\":\"x\",\"expectedRevision\":\"" + new string('a',64) + "\",\"action\":\"" + action + "\"" + extra + "}");
        Assert.Throws<ArgumentException>(() => IssueActions.Parse(body.RootElement));
    }
}
