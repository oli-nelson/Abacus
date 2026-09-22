using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Abacus.Dashboard;

namespace Abacus.Tests;

// Explicit opt-in: never create issues in a developer's current repository.
public sealed class DraftCliFactAttribute : FactAttribute
{
    public DraftCliFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ABACUS_DRAFT_CLI_FIXTURE") != "1")
            Skip = "Requires explicit opt-in and the disposable /tmp/abacus-web-contract Beads fixture.";
    }
}

public sealed class BeadsDraftCreationCliTests
{
    [DraftCliFact]
    public async Task ActualHttpCreationPersistsActorAndSameRequestCreatesOnlyOneDraft()
    {
        const string repository = "/tmp/abacus-web-contract";
        Assert.True(Directory.Exists(Path.Combine(repository, ".beads")));
        var runner = new CommandRunner(TextWriter.Null);
        var actions = IssueActions.ForRepository(runner, repository, "draft-http-contract", () => true, () => { }, default);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port),
            new DashboardStream(), "fixture", "draft-http-contract", default, actions: actions);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-Abacus-Request", "1");
        using var context = JsonDocument.Parse(await http.GetStringAsync("/api/v1/issues/drafts/context"));
        var policy = context.RootElement;
        var title = "--HTTP draft contract " + Guid.NewGuid().ToString("N");
        var requestId = $"{actions.Session}:{actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}";
        await File.AppendAllTextAsync("/tmp/abacus-draft-http-requests.jsonl", JsonSerializer.Serialize(new { requestId, title }) + "\n");
        var body = new { requestId, expectedRevision = policy.GetProperty("revision").GetString(),
            title, description = "literal <script>\n--not-a-flag", type = "task", priority = 2,
            labels = new[] { ReasoningPolicy.LowLabel }, target = "main" };
        string? createdId = null;
        try
        {
            using var first = await http.PostAsJsonAsync("/api/v1/issues/drafts", body);
            var firstBody = await first.Content.ReadAsStringAsync();
            using var receipt = JsonDocument.Parse(firstBody);
            createdId = receipt.RootElement.GetProperty("createdIssueId").GetString();
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.NotNull(createdId);
            using var retry = await http.PostAsJsonAsync("/api/v1/issues/drafts", body);
            Assert.Equal(firstBody, await retry.Content.ReadAsStringAsync());
            var export = await runner.RunAsync(new("bd", ["--readonly", "export"], repository), default);
            Assert.True(export.Succeeded);
            var snapshot = IssueExport.Parse(export.StandardOutput);
            var matches = snapshot.Issues.Values.Where(row => row.Source.TryGetProperty("title", out var t) && t.GetString() == title).ToArray();
            var issue = Assert.Single(matches);
            Assert.Equal(createdId, issue.Id);
            Assert.Equal("blocked", issue.Source.GetProperty("status").GetString());
            Assert.Equal("draft-http-contract", issue.Source.GetProperty("created_by").GetString());
            using var review = JsonDocument.Parse(await http.GetStringAsync($"/api/v1/issues/{createdId}/publication-review"));
            Assert.True(review.RootElement.GetProperty("candidate").GetProperty("contentPolicyValid").GetBoolean());
            Assert.True(review.RootElement.GetProperty("candidate").GetProperty("draftLifecycleClear").GetBoolean());
            Assert.False(review.RootElement.GetProperty("publicationAvailable").GetBoolean());
        }
        finally
        {
            if (createdId is not null)
            {
                Assert.True(Git.IsValidIssueId(createdId));
                var cleanup = await runner.RunAsync(new("bd", ["close", createdId, "--reason=Disposable HTTP draft contract cleanup", "--json"], repository), default);
                Assert.True(cleanup.Succeeded, $"Inspect disposable draft {createdId}; cleanup failed.");
                var shown = await runner.RunAsync(new("bd", ["--readonly", "show", createdId, "--json"], repository), default);
                Assert.True(shown.Succeeded);
                using var document = JsonDocument.Parse(shown.StandardOutput);
                Assert.Equal("closed", document.RootElement[0].GetProperty("status").GetString());
            }
        }
    }

    [DraftCliFact]
    public async Task ActualHelperStagesBothLabelledAndUnlabelledDraftsWithoutPublishing()
    {
        const string repository = "/tmp/abacus-web-contract";
        Assert.True(Directory.Exists(Path.Combine(repository, ".beads")));
        var runner = new CommandRunner(TextWriter.Null);
        var targets = await TargetRegistry.LoadAsync(Path.Combine(repository, ".abacus/targets.json"), default);
        foreach (var labelled in new[] { true, false })
        {
            var title = "--C# draft helper contract " + Guid.NewGuid().ToString("N");
            string? createdId = null;
            async Task<CommandResult> Execute(IReadOnlyList<string> args, CancellationToken token)
            {
                var result = await runner.RunAsync(new("bd", args, repository,
                    new Dictionary<string, string?> { ["BEADS_ACTOR"] = "draft-helper-contract" },
                    MaxOutputCharacters: 1024 * 1024), token);
                if (args[0] == "create" && result.Succeeded)
                {
                    using var document = JsonDocument.Parse(result.StandardOutput);
                    createdId = document.RootElement.GetProperty("id").GetString();
                    Assert.True(Git.IsValidIssueId(createdId!));
                    await File.AppendAllTextAsync("/tmp/abacus-draft-helper-created.jsonl",
                        JsonSerializer.Serialize(new { id = createdId, title }) + "\n", token);
                }
                return result;
            }
            try
            {
                var result = await Beads.CreateDraftAsync(new(title, "--literal\n<script>text</script>", "task", 2,
                    labelled ? [ReasoningPolicy.LowLabel] : [], "main"), targets, new(labelled), Execute, default);
                Assert.Equal("completed", result.Outcome);
                Assert.Equal(createdId, result.IssueId);
            }
            finally
            {
                if (createdId is not null)
                {
                    var closed = await Execute(["close", createdId, "--reason=Disposable helper contract cleanup", "--json"], default);
                    Assert.True(closed.Succeeded, $"Inspect disposable issue {createdId}; cleanup failed.");
                    var shown = await Execute(["--readonly", "show", createdId, "--json"], default);
                    Assert.True(shown.Succeeded);
                    using var document = JsonDocument.Parse(shown.StandardOutput);
                    Assert.Equal("closed", document.RootElement[0].GetProperty("status").GetString());
                }
            }
        }
    }
}
