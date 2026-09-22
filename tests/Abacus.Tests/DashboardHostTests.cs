using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardHostTests
{
    private static IssueSourceState State(string title = "Camera controls", string status = "open")
    {
        var projection = new IssueProjection();
        projection.Apply(IssueExport.Parse(JsonSerializer.Serialize(new { id = "web-a", title, status })));
        return new(projection.Current, DateTimeOffset.UtcNow, false, null, 100, TimeSpan.Zero);
    }

    private static int FreePort(IPAddress address)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(address, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task RealHttpServesBundledAssetsSnapshotsAndRejectsHostAndCrossSiteWrites(string ip)
    {
        var address = IPAddress.Parse(ip);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) return;
        var binding = new DashboardBinding([address], new(StringComparer.OrdinalIgnoreCase) { ip }, FreePort(address));
        var stream = new DashboardStream();
        stream.Publish(State("<img src=x onerror=alert(1)>"));
        var issueCollector = new IssueCollector(_ => Task.FromResult("{\"id\":\"private-fixture\",\"notes\":\"not diagnostic data\"}"), default);
        await issueCollector.RefreshAsync();
        await using var host = await DashboardHost.StartAsync(binding, stream, "fixture", "abacus-web", default, issueCollector: issueCollector);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(binding.Urls.Single()) };
        var diagnostics = await client.GetStringAsync("/api/v1/diagnostics");
        Assert.Equal(diagnostics, await client.GetStringAsync("/api/v1/diagnostics"));
        Assert.DoesNotContain("private-fixture", diagnostics); Assert.DoesNotContain("not diagnostic data", diagnostics);
        using (var metrics = JsonDocument.Parse(diagnostics))
        {
            Assert.Equal(1, metrics.RootElement.GetProperty("issues").GetProperty("successfulExports").GetInt64());
            Assert.True(metrics.RootElement.GetProperty("issues").GetProperty("hashSeconds").GetDouble() >= 0);
            Assert.Equal(0, metrics.RootElement.GetProperty("stream").GetProperty("snapshotBuilds").GetInt32());
        }
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("SELECTED ISSUE", html);
        foreach (var tab in new[] { "overview", "activity", "git" })
        {
            Assert.Contains($"id=\"inspector-tab-{tab}\" role=\"tab\"", html);
            Assert.Contains($"id=\"inspector-panel-{tab}\" role=\"tabpanel\"", html);
        }
        Assert.DoesNotContain("onerror", await page.Content.ReadAsStringAsync());
        var js = await client.GetStringAsync("/dashboard.js");
        Assert.DoesNotContain("innerHTML", js);
        foreach (var asset in new[] { "timeline.js", "inspector-resize.js", "timeline-gl.js", "timeline-model.js", "issue-table.js", "issue-filters.js", "issue-relations.js" })
        {
            var module = await client.GetAsync("/" + asset);
            Assert.Equal(HttpStatusCode.OK, module.StatusCode);
            Assert.Equal("text/javascript", module.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("innerHTML", await module.Content.ReadAsStringAsync());
        }
        var snapshot = await client.GetAsync("/api/v1/snapshot");
        using var doc = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync());
        Assert.Single(doc.RootElement.GetProperty("issues").EnumerateArray());
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/snapshot");
        conditional.Headers.IfNoneMatch.Add(snapshot.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/issues/web-a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/issues/missing")).StatusCode);
        using var sseCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var cursor = doc.RootElement.GetProperty("cursor").GetString();
        using var events = await client.GetAsync("/api/v1/events?after=" + Uri.EscapeDataString(cursor!), HttpCompletionOption.ResponseHeadersRead, sseCancellation.Token);
        using var eventReader = new StreamReader(await events.Content.ReadAsStreamAsync(sseCancellation.Token));
        Assert.Equal(": connected", await eventReader.ReadLineAsync(sseCancellation.Token));
        stream.Publish(State("SSE changed"));
        string? eventLine;
        do { eventLine = await eventReader.ReadLineAsync(sseCancellation.Token); } while (eventLine is not null && !eventLine.StartsWith("data:"));
        Assert.Contains("SSE changed", eventLine);
        sseCancellation.Cancel();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/issues/web-a/../../private")).StatusCode);
        var hostile = new HttpRequestMessage(HttpMethod.Get, "/api/v1/snapshot");
        hostile.Headers.Host = "attacker.invalid:" + binding.Port;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(hostile)).StatusCode);
        var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/v1/issues") { Content = JsonContent.Create(new { title = "ignored" }) };
        crossSite.Headers.Add("Origin", "https://attacker.invalid");
        crossSite.Headers.Add("X-Abacus-Request", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
        var missingHeader = await client.PostAsJsonAsync("/api/v1/issues", new { title = "ignored" });
        Assert.Equal(HttpStatusCode.BadRequest, missingHeader.StatusCode);
        var large = new HttpRequestMessage(HttpMethod.Post, "/api/v1/issues") { Content = new StringContent(new string('a', 70_000), Encoding.UTF8, "application/json") };
        large.Headers.Add("X-Abacus-Request", "1");
        using var oversized = await client.SendAsync(large);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.True(oversized.Headers.ConnectionClose);
        using var project = JsonDocument.Parse(await client.GetStringAsync("/api/v1/project"));
        Assert.False(project.RootElement.GetProperty("runtimeConnected").GetBoolean());
    }

    [Fact]
    public async Task OccupiedBindingFailsRatherThanChangingPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stream = new DashboardStream(); stream.Publish(State());
        await Assert.ThrowsAnyAsync<IOException>(() => DashboardHost.StartAsync(
            new([IPAddress.Loopback], new() { "127.0.0.1" }, port), stream, "fixture", "actor", default));
    }

    [Fact]
    public void IdenticalStateKeepsSerializedSnapshotAndCursor()
    {
        var stream = new DashboardStream();
        var state = State(); stream.Publish(state);
        var first = stream.Snapshot();
        for (var i = 0; i < 120; i++) stream.Publish(state with { LastSuccess = DateTimeOffset.UtcNow });
        var next = stream.Snapshot();
        Assert.Equal(first.ETag, next.ETag);
        Assert.Same(first.Body, next.Body);
    }

    [Fact]
    public async Task SnapshotCursorClosesSubscriptionRaceAndRestartRequiresResync()
    {
        var stream = new DashboardStream(); stream.Publish(State());
        var cursor = stream.Snapshot().ETag.Trim('"');
        stream.Publish(State("Changed"));
        using var subscription = stream.Subscribe(cursor);
        var change = await subscription.Reader.ReadAsync();
        Assert.Equal("change", change.Kind);
        Assert.Contains("Changed", Encoding.UTF8.GetString(change.Data));
        using var restarted = stream.Subscribe("another-instance:1");
        Assert.Equal("resync", (await restarted.Reader.ReadAsync()).Kind);
    }

    [Fact]
    public async Task SlowClientsAndEvictedReplayRequireResyncWithoutBlockingPublication()
    {
        var stream = new DashboardStream(); stream.Publish(State());
        var cursor = stream.Snapshot().ETag.Trim('"');
        using var slow = stream.Subscribe(cursor);
        for (var i = 0; i < 200; i++) stream.Publish(State("change " + i));
        while (slow.Reader.TryRead(out _)) { }
        Assert.False(await slow.Reader.WaitToReadAsync());
        using var gap = stream.Subscribe(cursor);
        Assert.Equal("resync", (await gap.Reader.ReadAsync()).Kind);
    }

    [Fact]
    public void CliDashboardIsStandaloneAndScoped()
    {
        var parsed = Options.Parse(["dashboard", "--bind", "::1", "--port=8089", "--actor", "Ollie", "--poll-interval", "2s"]);
        Assert.Null(parsed.Value);
        Assert.Equal("::1", parsed.Dashboard!.Bind);
        Assert.Equal(8089, parsed.Dashboard.Port);
        Assert.Equal(TimeSpan.FromSeconds(2), parsed.Dashboard.PollInterval);
        Assert.Equal(TimeSpan.FromHours(48), Options.Parse(["dashboard", "--poll-interval", "48h"]).Dashboard!.PollInterval);
        Assert.Contains("without starting agents", Options.Parse(["dashboard", "--help"]).HelpText);
        Assert.Throws<OptionsException>(() => Options.Parse(["dashboard", "--model", "sonnet"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--bind", "::1"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["dashboard", "--port", "0"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["dashboard", "--poll-interval", "0.5s"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["dashboard", "--bind", "http://localhost"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["dashboard", "--port", "8080", "--port", "8081"]));
    }
}
