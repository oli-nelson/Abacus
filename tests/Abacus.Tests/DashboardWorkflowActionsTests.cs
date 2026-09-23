using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardWorkflowActionsTests
{
    private static IssueAction Parse(object body)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return IssueActions.Parse(document.RootElement);
    }

    [Fact]
    public async Task StatusChangeVerifiesResultAndWarnsForAssignedWork()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"open\"}");
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            calls++;
            Assert.Equal(new[] { "update", "web-a", "--json", calls == 1 ? "--status=blocked" : "--status=closed" }, args);
            state = IssueExport.Parse(calls == 1 ? "{\"id\":\"web-a\",\"status\":\"blocked\"}" :
                "{\"id\":\"web-a\",\"status\":\"closed\",\"assignee\":\"worker\"}");
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default);
        IssueAction Change() => Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = state.Issues["web-a"].Revision, action = "status", status = "blocked" });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", Change(), default)).Outcome);
        Assert.Equal("rejected", (await actions.SubmitAsync("web-a", Change(), default)).Outcome);
        Assert.Equal(1, calls);

        state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"in_progress\",\"assignee\":\"worker\"}");
        var blocked = Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = state.Issues["web-a"].Revision, action = "status", status = "closed" });
        var changed = await actions.SubmitAsync("web-a", blocked, default);
        Assert.Equal("completed", changed.Outcome);
        Assert.Contains("possible worker ownership", changed.Message);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConnectedWorkerReservationWarnsButAllowsCombinedReopen()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"closed\",\"labels\":[\"abacus:needs-user-attention\"]}");
        var commands = new List<string[]>();
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            commands.Add(args.ToArray());
            state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"open\",\"assignee\":\"\",\"labels\":[]}");
            return Task.FromResult(new CommandResult(0, "", ""));
        },
            () => true, () => { }, default, attentionEnabled: true, reserved: id => id == "web-a");
        string Id() => $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}";
        var revision = state.Issues["web-a"].Revision;
        var result = await actions.SubmitAsync("web-a", Parse(new { requestId = Id(), expectedRevision = revision,
            action = "attention-resolve-reopen" }), default);
        Assert.Equal("completed", result.Outcome);
        Assert.Contains("may conflict with a worker or reserved assignment", result.Message);
        Assert.Equal(new[] { "update", "web-a", "--remove-label", Beads.NeedsUserAttentionLabel,
            "--status", "open", "--assignee", "", "--json" }, Assert.Single(commands));
    }

    [Fact]
    public async Task ReservedAttentionRequestCanBlockAfterVerifiedExplanation()
    {
        var comments = new List<object>(); var status = "in_progress"; var attention = false;
        IssueSnapshot Snapshot() => IssueExport.Parse(JsonSerializer.Serialize(new
        {
            id = "web-a", status, assignee = "worker", comments,
            labels = attention ? new[] { Beads.NeedsUserAttentionLabel } : Array.Empty<string>()
        }));
        var actions = new IssueActions(_ => Task.FromResult(Snapshot()), (args, _) =>
        {
            if (args[0] == "comment") comments.Add(new { id = "stored", text = args[^1] });
            else { Assert.Contains("blocked", args); status = "blocked"; attention = true; }
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default, attentionEnabled: true, reserved: id => id == "web-a");
        var request = Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = Snapshot().Issues["web-a"].Revision, action = "attention-request-block", text = "Please review" });
        var result = await actions.SubmitAsync("web-a", request, default);
        Assert.Equal("completed", result.Outcome);
        Assert.Contains("may conflict with a worker or reserved assignment", result.Message);
        Assert.Single(comments);
        Assert.Equal("blocked", status);
    }

    [Fact]
    public async Task UnassignedInProgressIssueCanBeChangedAfterReview()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"in_progress\"}");
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            Assert.Contains("--status=open", args);
            state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"open\"}");
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default);
        var request = Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = state.Issues["web-a"].Revision, action = "status", status = "open" });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", request, default)).Outcome);
    }

    [Fact]
    public async Task ConfirmedStatusChangeAllowsAssignedReservedIssueWithWarning()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"in_progress\",\"assignee\":\"worker\"}");
        var writes = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            writes++; Assert.Contains("--status=blocked", args);
            state = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"blocked\",\"assignee\":\"worker\"}");
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default, reserved: id => id == "web-a");
        var request = Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = state.Issues["web-a"].Revision, action = "status", status = "blocked", confirmOwnershipRisk = true });
        var result = await actions.SubmitAsync("web-a", request, default);
        Assert.Equal("completed", result.Outcome); Assert.Contains("despite possible worker ownership", result.Message);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task SeparateLabelsAndReasoningPreserveUnrelatedLabels()
    {
        var labels = new List<string> { "team:old", "keep", ReasoningPolicy.HighLabel };
        IssueSnapshot Snapshot() => IssueExport.Parse(JsonSerializer.Serialize(new { id = "web-a", status = "open", labels }));
        var commands = new List<string[]>();
        var actions = new IssueActions(_ => Task.FromResult(Snapshot()), (args, _) =>
        {
            commands.Add(args.ToArray());
            foreach (var flag in args.Where(a => a.StartsWith("--add-label=", StringComparison.Ordinal))) labels.Add(flag[12..]);
            foreach (var flag in args.Where(a => a.StartsWith("--remove-label=", StringComparison.Ordinal))) labels.Remove(flag[15..]);
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default);
        IssueAction Action(object fields) => Parse(fields);
        var label = Action(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = Snapshot().Issues["web-a"].Revision, action = "labels", addLabels = new[] { "team:new" }, removeLabels = new[] { "team:old" } });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", label, default)).Outcome);
        var reasoning = Action(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = Snapshot().Issues["web-a"].Revision, action = "reasoning-set", reasoningLevel = "low" });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", reasoning, default)).Outcome);
        Assert.Equal(new[] { "update", "web-a", "--json", "--add-label=team:new", "--remove-label=team:old" }, commands[0]);
        Assert.Equal(new[] { "update", "web-a", "--json", "--add-label=abacus:low_reasoning", "--remove-label=abacus:high_reasoning" }, commands[1]);
        var clear = Action(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = Snapshot().Issues["web-a"].Revision, action = "reasoning-set", reasoningLevel = "none" });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", clear, default)).Outcome);
        Assert.Equal(new[] { "update", "web-a", "--json", "--remove-label=abacus:low_reasoning" }, commands[2]);
        Assert.Contains("keep", labels);
        Assert.Contains("team:new", labels);
    }

    [Fact]
    public async Task RequiredReasoningPolicyRejectsClearingLevel()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"labels\":[\"abacus:high_reasoning\"]}");
        var policy = new DraftPolicy(new TargetRegistry(new Dictionary<string, TargetPolicy>()), new ReasoningPolicy(true));
        var actions = new IssueActions(_ => Task.FromResult(state), (_, _) => throw new Exception("Must not write"),
            () => true, () => { }, default, draftPolicy: _ => Task.FromResult(policy));
        var request = Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = state.Issues["web-a"].Revision, action = "reasoning-set", reasoningLevel = "none" });
        Assert.Equal(409, (await actions.SubmitAsync("web-a", request, default)).StatusCode);
    }

    [Fact]
    public async Task CombinedAttentionActionsSequenceAndVerifyStatusAndAssignee()
    {
        var status = "open"; var assignee = ""; var attention = false; var comments = new List<object>();
        IssueSnapshot Snapshot() => IssueExport.Parse(JsonSerializer.Serialize(new
        {
            id = "web-a", status, assignee,
            labels = attention ? new[] { Beads.NeedsUserAttentionLabel } : Array.Empty<string>(), comments
        }));
        var commands = new List<string[]>();
        var actions = new IssueActions(_ => Task.FromResult(Snapshot()), (args, _) =>
        {
            commands.Add(args.ToArray());
            if (args[0] == "comment") comments.Add(new { id = "stored-" + comments.Count, text = args[^1] });
            else
            {
                if (args.Contains("--add-label")) attention = true;
                if (args.Contains("--remove-label")) attention = false;
                if (args.Contains("blocked")) status = "blocked";
                if (args.Contains("open")) status = "open";
                if (args.Contains("--assignee")) assignee = "";
            }
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default, attentionEnabled: true);
        IssueAction Action(string kind, string? text) => Parse(new { requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            expectedRevision = Snapshot().Issues["web-a"].Revision, action = kind, text });
        var request = Action("attention-request-block", "Please review");
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", request, default)).Outcome);
        Assert.Equal("blocked", status); Assert.True(attention);
        Assert.Equal(new[] { "update", "web-a", "--add-label", Beads.NeedsUserAttentionLabel, "--status", "blocked", "--json" }, commands[1]);
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", Action("attention-resolve-reopen", "Reviewed"), default)).Outcome);
        Assert.Equal("open", status); Assert.False(attention); Assert.Equal("", assignee);
        Assert.Equal(new[] { "update", "web-a", "--remove-label", Beads.NeedsUserAttentionLabel, "--status", "open", "--assignee", "", "--json" }, commands[3]);
        Assert.Equal(2, comments.Count);
    }

    [Fact]
    public async Task FailedBlockStepKeepsVerifiedCommentForNoDuplicateRecovery()
    {
        var status = "open"; var attention = false; var failBlock = true; var comments = new List<object>(); var calls = 0;
        IssueSnapshot Snapshot() => IssueExport.Parse(JsonSerializer.Serialize(new
        {
            id = "web-a", status, labels = attention ? new[] { Beads.NeedsUserAttentionLabel } : Array.Empty<string>(), comments
        }));
        var actions = new IssueActions(_ => Task.FromResult(Snapshot()), (args, _) =>
        {
            calls++;
            if (args[0] == "comment") comments.Add(new { id = "stored", text = args[^1] });
            else if (failBlock) return Task.FromResult(new CommandResult(1, "", "private detail"));
            else { attention = true; status = "blocked"; }
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default, attentionEnabled: true);
        string Id() => $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}";
        var initial = Parse(new { requestId = Id(), expectedRevision = Snapshot().Issues["web-a"].Revision,
            action = "attention-request-block", text = "Please review" });
        var partial = await actions.SubmitAsync("web-a", initial, default);
        Assert.Equal("partially-applied", partial.Outcome); Assert.Equal("stored", partial.RecordedCommentId);
        Assert.Same(partial, await actions.SubmitAsync("web-a", initial, default));
        failBlock = false;
        var finish = Parse(new { requestId = Id(), expectedRevision = Snapshot().Issues["web-a"].Revision,
            action = "attention-request-block", existingCommentId = "stored" });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", finish, default)).Outcome);
        Assert.Single(comments); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PartialReopenCanFinishWithoutRepeatingResponse()
    {
        var status = "blocked"; var attention = true; var comments = new List<object>(); var updates = 0;
        IssueSnapshot Snapshot() => IssueExport.Parse(JsonSerializer.Serialize(new
        {
            id = "web-a", status, labels = attention ? new[] { Beads.NeedsUserAttentionLabel } : Array.Empty<string>(), comments
        }));
        var actions = new IssueActions(_ => Task.FromResult(Snapshot()), (args, _) =>
        {
            if (args[0] == "comment") comments.Add(new { id = "stored", text = args[^1] });
            else
            {
                updates++;
                if (updates == 1) attention = false; // Simulate a partially applied CLI update.
                else status = "open";
            }
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default, attentionEnabled: true);
        string Id() => $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}";
        var initial = Parse(new { requestId = Id(), expectedRevision = Snapshot().Issues["web-a"].Revision,
            action = "attention-resolve-reopen", text = "Reviewed" });
        var partial = await actions.SubmitAsync("web-a", initial, default);
        Assert.Equal("partially-applied", partial.Outcome); Assert.Equal("stored", partial.RecordedCommentId);
        var finish = Parse(new { requestId = Id(), expectedRevision = Snapshot().Issues["web-a"].Revision,
            action = "attention-resolve-reopen" });
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", finish, default)).Outcome);
        Assert.Single(comments); Assert.Equal(2, updates); Assert.Equal("open", status);
    }

    [Theory]
    [InlineData("status", "reasoningLevel", "high")]
    [InlineData("reasoning-set", "status", "open")]
    [InlineData("labels", "status", "open")]
    [InlineData("attention-request-block", "status", "blocked")]
    public void SchemaRejectsMixedFields(string action, string field, string value)
    {
        var json = $"{{\"requestId\":\"unused\",\"expectedRevision\":\"{new string('a', 64)}\",\"action\":\"{action}\",\"{field}\":\"{value}\"}}";
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => IssueActions.Parse(document.RootElement));
    }

    [Fact]
    public void OwnershipConfirmationIsOnlyValidForStatusActions()
    {
        Assert.Throws<ArgumentException>(() => Parse(new { requestId = "unused", expectedRevision = new string('a', 64),
            action = "labels", addLabels = new[] { "team:ui" }, confirmOwnershipRisk = true }));
    }
}
