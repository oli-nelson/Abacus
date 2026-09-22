using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardBranchesTests
{
    [Fact]
    public async Task RealGitCollectorAndHttpValidatePolicyCacheAndPublishIndependently()
    {
        var directory = Directory.CreateTempSubdirectory("abacus-branches-http-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, directory.FullName));
            Assert.True(result.Succeeded, result.StandardError);
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await Git("commit", "--allow-empty", "-m", "initial"); await Git("branch", "abacus/web-a");
            var collector = new GitCollector(new DashboardGit(runner, directory.FullName, default), directory.FullName);
            var invalid = await collector.RefreshAsync(default);
            Assert.NotNull(invalid.View.PolicyError);
            Assert.False(invalid.View.Stale);
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CompareAsync("refs/heads/main", "refs/heads/abacus/web-a", false, default));
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".abacus"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, ".abacus", "targets.json"), TargetRegistry.DefaultConfiguration);
            var valid = await collector.RefreshAsync(default);
            Assert.Null(valid.View.PolicyError); Assert.Equal("main", Assert.Single(valid.View.Targets));
            Assert.Same(valid, await collector.RefreshAsync(default));
            var stream = new DashboardStream();
            var issueProjection = new IssueProjection(); issueProjection.Apply(IssueExport.Parse("{\"id\":\"web-a\"}"));
            stream.Publish(new(issueProjection.Current, DateTimeOffset.UtcNow, false, null, 0, TimeSpan.Zero));
            stream.PublishGit(valid);
            var snapshot = stream.Snapshot(); stream.PublishGit(await collector.RefreshAsync(default));
            Assert.Same(snapshot.Body, stream.Snapshot().Body);
            using var portSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            portSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)portSocket.LocalEndPoint!).Port; portSocket.Close();
            await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port), stream, "fixture", "actor", default, collector);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var response = await http.GetAsync("/api/v1/branches");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, document.RootElement.GetProperty("facts").GetProperty("branches").GetArrayLength());
            var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/branches"); conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional)).StatusCode);
            const string query = "/api/v1/branches/compare?target=refs%2Fheads%2Fmain&branch=refs%2Fheads%2Fabacus%2Fweb-a&patch=true";
            using var comparison = JsonDocument.Parse(await http.GetStringAsync(query));
            Assert.True(comparison.RootElement.GetProperty("comparison").GetProperty("containedInTarget").GetBoolean());
            Assert.True(comparison.RootElement.GetProperty("patch").GetProperty("available").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/branches/compare?target=--help&branch=HEAD")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(query + "&path=/etc/passwd")).StatusCode);
            const string historyQuery = "/api/v1/branches/history?branch=refs%2Fheads%2Fmain&limit=1";
            var historyResponse = await http.GetAsync(historyQuery);
            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
            using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
            Assert.Equal("Fixture", history.RootElement.GetProperty("commits")[0].GetProperty("author").GetString());
            Assert.Contains("initial", history.RootElement.GetProperty("commits")[0].GetProperty("message").GetString());
            Assert.False(history.RootElement.GetProperty("clockSkew").GetBoolean());
            Assert.True(history.RootElement.GetProperty("limitReached").GetBoolean());
            var historyConditional = new HttpRequestMessage(HttpMethod.Get, historyQuery);
            historyConditional.Headers.IfNoneMatch.Add(historyResponse.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(historyConditional)).StatusCode);
            var shared = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => collector.HistoryAsync("refs/heads/main", 1, default)));
            Assert.All(shared, item => Assert.Same(shared[0], item));
            foreach (var invalidHistory in new[] { "branch=HEAD", "branch=--help", "branch=refs/heads/main&limit=0",
                "branch=refs/heads/main&limit=1001", "branch=refs/heads/main&limit=1&limit=2", "branch=refs/heads/main&path=/etc/passwd" })
                Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/branches/history?" + invalidHistory)).StatusCode);
            await Git("branch", "new-branch");
            var changed = await collector.RefreshAsync(default); Assert.NotEqual(valid.View.Revision, changed.View.Revision);
            using var subscriber = stream.Subscribe(stream.Snapshot().ETag.Trim('"'));
            stream.PublishGit(changed);
            Assert.Equal("git", (await subscriber.Reader.ReadAsync()).Kind);
            Assert.Equal(1, issueProjection.RebuiltIssues);
            using var newSnapshot = JsonDocument.Parse(stream.Snapshot().Body);
            Assert.Equal(3, newSnapshot.RootElement.GetProperty("git").GetProperty("facts").GetProperty("branches").GetArrayLength());
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, ".abacus", "targets.json"), "{}");
            var badPolicy = await collector.RefreshAsync(default); Assert.NotNull(badPolicy.View.PolicyError);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(query)).StatusCode);
            Assert.Same(badPolicy, await collector.RefreshAsync(default));
        }
        finally { directory.Delete(true); }
    }
}
