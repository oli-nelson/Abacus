using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardActionHttpTests
{
    [Fact]
    public async Task HttpActionChecksRevisionsSchemasBodiesAndDoesNotDuplicateComments()
    {
        var state = IssueExport.Parse("{\"id\":\"web-a\",\"title\":\"A\"}");
        var revision = state.Issues["web-a"].Revision; var writes = 0;
        var actions = new IssueActions(_ => Task.FromResult(state), (_, _) =>
        {
            writes++;
            state = IssueExport.Parse("{\"id\":\"web-a\",\"title\":\"A\",\"comments\":[{\"id\":\"comment-id\",\"text\":\"literal\",\"author\":\"actor\",\"created_at\":\"2026-09-21T10:00:00Z\"}]}");
            return Task.FromResult(new CommandResult(0, "", ""));
        }, () => true, () => { }, default);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        var projection = new IssueProjection(); projection.Apply(state); var stream = new DashboardStream();
        stream.Publish(new(projection.Current, DateTimeOffset.UtcNow, false, null, 0, TimeSpan.Zero));
        await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port), stream, "fixture", "actor", default, actions: actions);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-Abacus-Request", "1");
        using var context = JsonDocument.Parse(await http.GetStringAsync("/api/v1/mutations/context"));
        var session = context.RootElement.GetProperty("session").GetString();
        var timestamp = context.RootElement.GetProperty("serverUnixMilliseconds").GetInt64();
        string RequestId() => $"{session}:{timestamp}:{Guid.NewGuid():N}";
        var body = new { requestId = RequestId(), expectedRevision = revision, action = "comment", text = "literal" };
        var first = await http.PostAsJsonAsync("/api/v1/issues/web-a/actions", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var resultText = await first.Content.ReadAsStringAsync();
        using var result = JsonDocument.Parse(resultText);
        Assert.Equal("completed", result.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("actor", result.RootElement.GetProperty("issue").GetProperty("comments")[0].GetProperty("author").GetString());
        var replay = await http.PostAsJsonAsync("/api/v1/issues/web-a/actions", body);
        Assert.Equal(resultText, await replay.Content.ReadAsStringAsync()); Assert.Equal(1, writes);
        var stale = await http.PostAsJsonAsync("/api/v1/issues/web-a/actions", new { requestId = RequestId(), expectedRevision = revision, action = "comment", text = "literal" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal(1, writes);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/issues/web-a/actions", new { action = "shell", command = "forbidden" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/v1/issues/actions", body)).StatusCode);
        using var chunked = new HttpRequestMessage(HttpMethod.Post, "/api/v1/issues/web-a/actions") { Content = new StringContent(new string('x', 70_000), Encoding.UTF8, "application/json") };
        chunked.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.SendAsync(chunked)).StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/issues/web-a/actions") { Content = JsonContent.Create(body) };
        crossOrigin.Headers.Add("Origin", "https://evil.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(crossOrigin)).StatusCode); Assert.Equal(1, writes);
    }
}
