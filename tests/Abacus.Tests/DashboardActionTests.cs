using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardActionTests
{
    [Fact]
    public async Task AttentionWritesDefaultToDisabledWithoutReadingOrWriting()
    {
        var actions = new IssueActions(_ => throw new Exception("Must not read"),
            (_, _) => throw new Exception("Must not write"), () => true, () => { }, default);
        var result = await actions.SubmitAsync("web-a",
            new("unused", new string('a', 64), "attention-request", "explanation", null, null, null, null), default);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("rejected", result.Outcome);
        Assert.Contains("not enabled", result.Message);
    }

    [Fact]
    public async Task InMemoryAttentionResolutionReportsStoredResponseAndFailedLabelWithoutReplay()
    {
        var state = Parse("{\"id\":\"web-a\",\"status\":\"blocked\",\"assignee\":\"worker\",\"labels\":[\"abacus:needs-user-attention\"]}");
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            calls++;
            Assert.DoesNotContain("--status", args);
            Assert.DoesNotContain("--assignee", args);
            if (args[0] == "comment")
            {
                Assert.Equal(new[] { "comment", "--json", "web-a", "--", "--literal" }, args);
                state = Parse("{\"id\":\"web-a\",\"status\":\"blocked\",\"assignee\":\"worker\",\"labels\":[\"abacus:needs-user-attention\"],\"comments\":[{\"id\":\"stored\",\"text\":\"--literal\"}]}");
                return Task.FromResult(new CommandResult(0, "", ""));
            }
            return Task.FromResult(new CommandResult(1, "", "private diagnostic"));
        }, () => true, () => { }, default, attentionEnabled: true);
        var action = Comment(actions, state, "--literal") with { Action = "attention-resolve" };
        var result = await actions.SubmitAsync("web-a", action, default);
        Assert.Equal("partially-applied", result.Outcome);
        Assert.Equal("stored", result.RecordedCommentId);
        Assert.Equal("blocked", result.Issue!.Status);
        Assert.Equal("worker", result.Issue.Assignee);
        Assert.Contains("abacus:needs-user-attention", result.Issue.Labels);
        Assert.DoesNotContain("private", result.Message);
        Assert.Same(result, await actions.SubmitAsync("web-a", action, default));
        Assert.Equal(2, calls);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static IssueAction Comment(IssueActions actions, IssueSnapshot snapshot, string text = "hello") =>
        new($"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}", snapshot.Issues["web-a"].Revision, "comment", text, null, null, null, null);
    private static IssueSnapshot Parse(string data) => IssueExport.Parse(data);

    [Fact]
    public async Task DisconnectDoesNotCancelSharedWriteAndRetryReturnsVerifiedResult()
    {
        var state = Parse("{\"id\":\"web-a\"}");
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), async (_, token) =>
        {
            calls++;writing.SetResult();await release.Task.WaitAsync(token);
            state = Parse("{\"id\":\"web-a\",\"comments\":[{\"id\":\"new\",\"text\":\"hello\"}]}");
            return new CommandResult(0, "", "");
        }, () => true, () => { }, default);
        var request = Comment(actions, state);
        using var disconnected = new CancellationTokenSource();
        var client = actions.SubmitAsync("web-a", request, disconnected.Token);
        await writing.Task; disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client);
        release.SetResult();
        Assert.Equal("completed", (await actions.SubmitAsync("web-a", request, default)).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CommentIsLiteralVerifiedAndConcurrentRetryExecutesOnce()
    {
        var state = Parse("{\"id\":\"web-a\",\"title\":\"A\"}");
        var initial = state;
        var text = "--help\n<script>alert(1)</script> $HOME; \"quotes\"";
        var calls = 0; var dirty = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            Assert.Equal(new[] { "comment", "--json", "web-a", "--", text }, args);
            Interlocked.Increment(ref calls);
            state = Parse(JsonSerializer.Serialize(new { id = "web-a", title = "A", comments = new[] { new { id = "stored-id", author = "actor", text } } }));
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => Interlocked.Increment(ref dirty), default);
        var command = Comment(actions, initial, text);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => actions.SubmitAsync("web-a", command, default)));
        Assert.All(results, r => { Assert.Equal("completed", r.Outcome); Assert.Same(results[0], r); });
        Assert.Equal(1, calls); Assert.Equal(1, dirty);
        Assert.Equal(400, (await actions.SubmitAsync("web-a", command with { Text = "different" }, default)).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task StaleAndUnavailableSourcesCannotWrite()
    {
        var old = Parse("{\"id\":\"web-a\",\"title\":\"Old\"}");
        var state = Parse("{\"id\":\"web-a\",\"title\":\"New\"}");
        var healthy = true;
        var actions = new IssueActions(_ => Task.FromResult(state), (_, _) => throw new Exception("No write allowed"), () => healthy, () => { }, default);
        Assert.Equal(409, (await actions.SubmitAsync("web-a", Comment(actions, old), default)).StatusCode);
        healthy = false;
        Assert.Equal(503, (await actions.SubmitAsync("web-a", Comment(actions, state), default)).StatusCode);
    }

    [Fact]
    public async Task TimeoutResultIsUnknownAndSameIdNeverAppendsAgain()
    {
        var state = Parse("{\"id\":\"web-a\"}"); var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (_, _) =>
        { calls++; throw new CommandTimeoutException("bd", TimeSpan.FromSeconds(1)); }, () => true, () => { }, default);
        var request = Comment(actions, state);
        Assert.Equal("outcome-unknown", (await actions.SubmitAsync("web-a", request, default)).Outcome);
        Assert.Equal("outcome-unknown", (await actions.SubmitAsync("web-a", request, default)).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExpiredAndRestartedRequestsAreRejectedRatherThanRetried()
    {
        var state = Parse("{\"id\":\"web-a\"}"); var time = new Clock();
        var actions = new IssueActions(_ => Task.FromResult(state), (_, _) => throw new Exception("No write allowed"), () => true, () => { }, default, time);
        var request = Comment(actions, state);
        time.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(409, (await actions.SubmitAsync("web-a", request, default)).StatusCode);
        Assert.Equal(409, (await actions.SubmitAsync("web-a", request with { RequestId = $"other:{time.Now.ToUnixTimeMilliseconds()}:{Guid.NewGuid():N}" }, default)).StatusCode);
    }

    [Fact]
    public async Task ContentEditUsesLiteralOptionValuesAndDetectsPartialResults()
    {
        var state = Parse("{\"id\":\"web-a\",\"title\":\"A\",\"notes\":\"Original\",\"priority\":2}");
        var actions = new IssueActions(_ => Task.FromResult(state), (args, _) =>
        {
            Assert.Contains("--title=--help", args); Assert.Contains("--append-notes=literal", args);
            state = Parse("{\"id\":\"web-a\",\"title\":\"--help\",\"notes\":\"Original\",\"priority\":2}");
            return Task.FromResult(new CommandResult(1, "", "credentials must not escape"));
        }, () => true, () => { }, default);
        var request = Comment(actions, state) with { Action = "edit", Text = null, Title = "--help", AppendNotes = "literal" };
        var result = await actions.SubmitAsync("web-a", request, default);
        Assert.Equal("partially-applied", result.Outcome); Assert.DoesNotContain("credentials", result.Message);
    }

    [Theory]
    [InlineData("{\"action\":\"comment\",\"shell\":\"bad\"}")]
    [InlineData("{\"requestId\":\"a\",\"requestId\":\"b\"}")]
    [InlineData("{\"metadata\":{\"abacus_execution\":{}}}")]
    public void RejectsUnknownAndDuplicateFields(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => IssueActions.Parse(doc.RootElement));
    }
}
