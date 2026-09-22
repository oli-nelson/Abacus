using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardIssueGitTests
{
    [Fact]
    public async Task RealGitEvidenceRequiresRecordedPolicyValidBindingAndStartAncestry()
    {
        var directory = Directory.CreateTempSubdirectory("abacus-issue-git-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, directory.FullName));
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await Git("commit", "--allow-empty", "-m", "initial");
            var start = await Git("rev-parse", "HEAD");
            await Git("checkout", "-b", "abacus/web-a");
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "change.txt"), "new content");
            await Git("add", "change.txt"); await Git("commit", "-m", "issue change");
            var tip = await Git("rev-parse", "HEAD");
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".abacus"));
            var config = Path.Combine(directory.FullName, ".abacus", "targets.json");
            await File.WriteAllTextAsync(config, TargetRegistry.DefaultConfiguration);
            var registry = await TargetRegistry.LoadAsync(config, default);
            var binding = new ExecutionBinding(1, "refs/heads/main", "abacus/web-a", start, registry.Targets["main"].Identity);
            var collector = new GitCollector(new DashboardGit(runner, directory.FullName, default), directory.FullName);
            await collector.RefreshAsync(default);
            var projection = new IssueProjection(); var stream = new DashboardStream();
            void Publish(object metadata, bool stale = false)
            {
                projection.Apply(IssueExport.Parse(JsonSerializer.Serialize(new { id = "web-a", metadata }, DashboardStream.Json)));
                stream.Publish(new(projection.Current, DateTimeOffset.UtcNow, stale, stale ? "Stale fixture" : null, 0, TimeSpan.Zero));
            }
            Publish(new { });
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
            var hostBinding = new DashboardBinding([IPAddress.Loopback], new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1" }, port);
            await using var host = await DashboardHost.StartAsync(hostBinding, stream, "fixture", "test", default, collector);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(hostBinding.Urls.Single()) };
            async Task<JsonElement> Read(string query = "")
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/issues/web-a/git" + query)); return doc.RootElement.Clone();
            }
            Assert.Equal("unbound", (await Read()).GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, (await Read("?patch=true&history=true")).GetProperty("history").ValueKind);
            Publish(new { abacus_execution = binding, secret = "do-not-expose" });
            var result = await Read();
            Assert.Equal("validated", result.GetProperty("state").GetString());
            Assert.Equal(tip, result.GetProperty("comparison").GetProperty("issueTip").GetString());
            Assert.False(result.GetProperty("comparison").GetProperty("containedInTarget").GetBoolean());
            Assert.Equal("change.txt", result.GetProperty("comparison").GetProperty("files")[0].GetProperty("path").GetString());
            var checkout = Assert.Single(result.GetProperty("worktrees").EnumerateArray());
            Assert.Equal(await Git("rev-parse", "--show-toplevel"), checkout.GetProperty("path").GetString());
            Assert.True(checkout.GetProperty("dirty").GetBoolean()); // untracked target config is visible
            Assert.Equal(JsonValueKind.Null, result.GetProperty("patch").ValueKind);
            Assert.Equal(JsonValueKind.Null, result.GetProperty("history").ValueKind);
            var details = await Read("?patch=true&history=true");
            Assert.True(details.GetProperty("patch").GetProperty("available").GetBoolean());
            Assert.Contains("+new content", details.GetProperty("patch").GetProperty("text").GetString());
            Assert.Equal(tip, details.GetProperty("history").GetProperty("tip").GetString());
            Assert.Equal(2, details.GetProperty("history").GetProperty("commits").GetArrayLength());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/issues/web-a/git?patch=1")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/issues/web-a/git?history=true&history=false")).StatusCode);
            var summary = await http.GetStringAsync("/api/v1/issues/web-a");
            Assert.DoesNotContain("policyIdentity", summary); Assert.DoesNotContain("do-not-expose", summary); Assert.DoesNotContain("routing", summary);
            await Git("checkout", "main"); await Git("merge", "--ff-only", "abacus/web-a");
            Assert.True((await Read()).GetProperty("comparison").GetProperty("containedInTarget").GetBoolean());
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, ".abacus", "merge-instructions.md"), "New policy");
            Assert.Equal("invalid", (await Read()).GetProperty("state").GetString());
            File.Delete(Path.Combine(directory.FullName, ".abacus", "merge-instructions.md"));
            Publish(new { abacus_execution = binding with { StartCommit = new string('f', 40) } });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/v1/issues/web-a/git")).StatusCode);
            Publish(new { abacus_execution = binding with { PolicyIdentity = "wrong" } });
            Assert.Equal("invalid", (await Read()).GetProperty("state").GetString());
            Publish(new { abacus_target = 42, abacus_execution = binding });
            Assert.Equal("invalid", (await Read()).GetProperty("state").GetString());
            Publish(new { abacus_execution = binding });
            await Git("checkout", "--orphan", "unrelated"); await Git("commit", "--allow-empty", "-m", "unrelated root");
            var unrelated = await Git("rev-parse", "HEAD");
            Publish(new { abacus_execution = binding with { StartCommit = unrelated } });
            Assert.Equal("diverged-or-incomplete", (await Read()).GetProperty("state").GetString());
            Publish(new { abacus_execution = binding });
            await Git("branch", "-D", "abacus/web-a"); await collector.RefreshAsync(default);
            Assert.Equal("branch-missing", (await Read()).GetProperty("state").GetString());
            Publish(new { abacus_execution = binding }, true);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/api/v1/issues/web-a/git")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/issues/web-a/git?branch=main")).StatusCode);
        }
        finally { directory.Delete(true); }
    }
}
