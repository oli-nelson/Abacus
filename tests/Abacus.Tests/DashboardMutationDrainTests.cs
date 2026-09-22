using Abacus.Dashboard;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Abacus.Tests;

public sealed class DashboardMutationDrainTests
{
    private static IssueAction Comment(IssueActions actions, IssueSnapshot snapshot, string text) =>
        new($"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}", snapshot.Issues["web-a"].Revision, "comment", text, null, null, null, null);

    [Fact]
    public async Task DrainRejectsNewWorkButPreservesAcceptedWriteVerificationAndRetries()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\"}");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), async (_, token) =>
        {
            calls++; started.SetResult(); await release.Task.WaitAsync(token);
            state = IssueExport.Parse("{\"id\":\"web-a\",\"comments\":[{\"id\":\"1\",\"text\":\"hello\"}]}");
            return new(0, "", "");
        }, () => true, () => { }, default);
        var request = Comment(actions, state, "hello");
        var write = actions.SubmitAsync("web-a", request, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = actions.DrainAsync();
        Assert.Same(drain, actions.DrainAsync());
        Assert.False(drain.IsCompleted);
        Assert.Equal("rejected", (await actions.SubmitAsync("web-a", Comment(actions, state, "new"), default)).Outcome);
        var retry = actions.SubmitAsync("web-a", request, default);
        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", (await write).Outcome);
        Assert.Same(await write, await retry);
        Assert.Same(await write, await actions.SubmitAsync("web-a", request, default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraceExpiryCancelsActiveCommandAndRejectsQueuedWriteWithoutReplay(bool throwingCallback)
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\"}");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var calls = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), async (_, token) =>
        {
            using var callback = token.Register(() => { if (throwingCallback) throw new InvalidOperationException("fixture callback"); });
            calls++; started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled = true; }
            return new(0, "", "");
        }, () => true, () => { }, default);
        var first = Comment(actions, state, "first");
        var active = actions.SubmitAsync("web-a", first, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = actions.SubmitAsync("web-a", Comment(actions, state, "queued"), default);
        await actions.DrainAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled);
        Assert.Equal("outcome-unknown", (await active).Outcome);
        Assert.Equal("rejected", (await queued).Outcome);
        Assert.Same(await active, await actions.SubmitAsync("web-a", first, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationAfterSourceReadCannotStartAnAlreadyCancelledWrite()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\"}");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new IssueActions(async token =>
        {
            reading.SetResult();
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => finish.TrySetResult());
            await finish.Task; // Source can finish successfully concurrently with cancellation.
            return state;
        }, (_, _) => throw new Exception("Cancelled work must never reach execution"), () => true, () => { }, default);
        var pending = actions.SubmitAsync("web-a", Comment(actions, state, "hello"), default);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await actions.DrainAsync(TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("rejected", (await pending).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHttpHostWaitsForAcceptedMutationBeforeClosing(bool sessionOwner)
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\"}");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actions = new IssueActions(_ => Task.FromResult(state), async (_, token) =>
        {
            started.SetResult(); await release.Task.WaitAsync(token);
            state = IssueExport.Parse("{\"id\":\"web-a\",\"comments\":[{\"id\":\"1\",\"text\":\"hello\"}]}");
            return new(0, "", "");
        }, () => true, () => { }, default);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        var binding = new DashboardBinding([IPAddress.Loopback], new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" }, port);
        await using var host = await DashboardHost.StartAsync(binding, new DashboardStream(), "fixture", "operator", default, actions: actions);
        await using var session = sessionOwner ? new DashboardSession(host, new CancellationTokenSource(), token => Task.Delay(Timeout.InfiniteTimeSpan, token)) : null;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(binding.Urls.Single()) };
        async Task<HttpResponseMessage> Post(IssueAction action)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/issues/web-a/actions")
            { Content = System.Net.Http.Json.JsonContent.Create(new { action.RequestId, action.ExpectedRevision, action.Action, action.Text }) };
            message.Headers.Add("X-Abacus-Request", "1");
            return await http.SendAsync(message);
        }
        try
        {
            var pending = Post(Comment(actions, state, "hello"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = session is null ? host.DisposeAsync().AsTask() : session.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            using var rejected = await Post(Comment(actions, state, "new"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            release.SetResult();
            using var completed = await pending;
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            using var body = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
            Assert.Equal("completed", body.RootElement.GetProperty("outcome").GetString());
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
    }
}
