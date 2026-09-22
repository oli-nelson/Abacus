using System.Net;
using System.Net.Sockets;
using Abacus.Dashboard;

namespace Abacus.Tests;

// These tests deliberately rebind the same released port to prove disposal.
// Do not let another socket fixture claim that port between stop and rebind.
[CollectionDefinition("Dashboard lifecycle", DisableParallelization = true)]
public sealed class DashboardLifecycleCollection { }

[Collection("Dashboard lifecycle")]
public sealed class DashboardSessionTests
{
    private static DashboardBinding Binding()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return new([IPAddress.Loopback], new() { "127.0.0.1" }, ((IPEndPoint)socket.LocalEndPoint!).Port);
    }

    [Fact]
    public async Task RuntimeHttpUsesConnectedSafeStateAndConditionalResponses()
    {
        using var output = new ConsoleOutput(TextWriter.Null, ["alice"], "model", verbose: false, interactive: false);
        var runtime = new DashboardRuntime(output);
        runtime.Refresh();
        var binding = Binding();
        await using var host = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default,
            integrated: true, runtime: runtime);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var url = binding.Urls.Single();
        var project = await http.GetStringAsync(url + "api/v1/project");
        Assert.Contains("\"runtimeConnected\":true", project);
        Assert.Contains("\"runtimeControl\":false", project);
        using var first = await http.GetAsync(url + "api/v1/runtime");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, url + "api/v1/runtime");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional)).StatusCode);
        await output.SetAgentAsync("alice", AgentActivity.Working, "raw secret detail");
        runtime.Refresh();
        using var changed = await http.GetAsync(url + "api/v1/runtime");
        Assert.NotEqual(first.Headers.ETag, changed.Headers.ETag);
        var body = await changed.Content.ReadAsStringAsync();
        Assert.Contains("Working", body);
        Assert.DoesNotContain("secret", body);
    }

    [Fact]
    public async Task RunAdapterReportsWebFailureAndDisposesWithoutThrowing()
    {
        var binding = Binding();
        var host = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default, integrated: true);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var project = await http.GetStringAsync(binding.Urls.Single() + "api/v1/project");
        Assert.Contains("\"mode\":\"integrated\"", project);
        Assert.Contains("\"runtimeControl\":false", project);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new DashboardSession(host, new(), _ => fail.Task);
        using var diagnostics = new StringWriter();
        var run = new DashboardRunLifetime(session, diagnostics);
        fail.SetException(new InvalidOperationException("secret exception detail"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Completion);
        await run.DisposeAsync();
        Assert.DoesNotContain("secret exception detail", diagnostics.ToString());
        await using var replacement = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default);
    }

    [Fact]
    public async Task DisposalJoinsEveryCollectorAndReleasesListenerIdempotently()
    {
        var binding = Binding();
        var host = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default);
        var starts = 0;
        var stops = 0;
        async Task Collect(CancellationToken token)
        {
            Interlocked.Increment(ref starts);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Interlocked.Increment(ref stops); }
        }
        var session = new DashboardSession(host, new(), Collect, Collect);
        Assert.Equal(2, starts);
        Assert.False(session.Completion.IsCompleted);
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        Assert.Equal(2, stops);
        Assert.True(session.Completion.IsCompletedSuccessfully);
        await using var replacement = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default);
    }

    [Fact]
    public async Task CollectorFailureIsObservedAndStopsPeers()
    {
        var binding = Binding();
        var host = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        async Task Peer(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
        }
        var session = new DashboardSession(host, new(), _ => fail.Task, Peer);
        fail.SetException(new InvalidOperationException("fixture failure"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("fixture failure", error.Message);
        Assert.True(stopped);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
        await using var replacement = await DashboardHost.StartAsync(binding, new(), "fixture", "actor", default);
    }

    [Fact]
    public async Task SynchronousCollectorFailureStillJoinsAlreadyStartedPeer()
    {
        var host = await DashboardHost.StartAsync(Binding(), new(), "fixture", "actor", default);
        var stopped = false;
        async Task Peer(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { stopped = true; }
        }
        var session = new DashboardSession(host, new(), Peer, _ => throw new InvalidDataException("fixture"));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stopped);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.DisposeAsync().AsTask());
    }
}
