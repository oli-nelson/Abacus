using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardDraftActionsTests
{
    private sealed class Fixture
    {
        public DraftPolicy Policy = new(new(new Dictionary<string, TargetPolicy> { ["main"] = new("main", null, "v1") }), new());
        public readonly IssueActions Actions;
        public int Creates, Dirty;
        public bool Healthy = true;
        public TaskCompletionSource? CreateEntered, ReleaseCreate;
        public string? FailCreate;
        public Fixture()
        {
            string status = "open"; string? defer = Beads.DraftDeferral;
            Actions = new(_ => Task.FromResult(IssueExport.Parse("")), async (args, token) =>
            {
                if (args[0] == "create")
                {
                    Creates++; CreateEntered?.TrySetResult();
                    if (ReleaseCreate is not null) await ReleaseCreate.Task.WaitAsync(token);
                    if (FailCreate is not null) return new(0, FailCreate, "");
                    return new(0, "{\"id\":\"web-new\"}", "");
                }
                if (args.Contains("--status=blocked")) status = "blocked";
                if (args.Contains("--defer=")) defer = null;
                if (args.Contains("show")) return new(0, JsonSerializer.Serialize(new[] { new
                {
                    id = "web-new", title = "Draft", description = "", issue_type = "task", priority = 2,
                    status, defer_until = defer, labels = Array.Empty<string>(), metadata = new { abacus_target = "main" }
                } }), "");
                return new(0, "[]", "");
            }, () => Healthy, () => Dirty++, default, draftPolicy: _ => Task.FromResult(Policy));
        }
        public CreateDraftAction Request() => new($"{Actions.Session}:{Actions.ServerUnixMilliseconds}:{Guid.NewGuid():N}",
            Policy.Revision, new("Draft", "", "task", 2, [], "main"));
    }

    [Fact]
    public async Task DuplicateCreationSharesReceiptAndRequestIdsCannotCrossActionTypes()
    {
        var fixture = new Fixture(); var request = fixture.Request();
        var first = await fixture.Actions.SubmitDraftAsync(request, default);
        Assert.Equal(201, first.StatusCode); Assert.Equal("web-new", first.CreatedIssueId);
        Assert.Same(first, await fixture.Actions.SubmitDraftAsync(request, default));
        Assert.Equal(400, (await fixture.Actions.SubmitDraftAsync(request with { Draft = request.Draft with { Title = "other" } }, default)).StatusCode);
        Assert.Equal(400, (await fixture.Actions.SubmitAsync("web-new", new(request.RequestId, new string('a', 64), "comment", "comment", null, null, null, null), default)).StatusCode);
        Assert.Equal(1, fixture.Creates); Assert.Equal(1, fixture.Dirty);
    }

    [Fact]
    public async Task CallerDisconnectDoesNotCancelAcceptedCreateAndDrainJoinsIt()
    {
        var fixture = new Fixture { CreateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseCreate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var request = fixture.Request(); using var caller = new CancellationTokenSource();
        var pending = fixture.Actions.SubmitDraftAsync(request, caller.Token);
        await fixture.CreateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var drain = fixture.Actions.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.Equal(503, (await fixture.Actions.SubmitDraftAsync(fixture.Request(), default)).StatusCode);
        fixture.ReleaseCreate.SetResult(); await drain;
        Assert.Equal(201, (await fixture.Actions.SubmitDraftAsync(request, default)).StatusCode);
        Assert.Equal(1, fixture.Creates);
    }

    [Fact]
    public async Task PolicyChangesAndStaleSourcesRejectBeforeCreate()
    {
        var fixture = new Fixture(); var request = fixture.Request();
        fixture.Policy = fixture.Policy with { Reasoning = new(true) };
        Assert.Equal(409, (await fixture.Actions.SubmitDraftAsync(request, default)).StatusCode);
        fixture.Healthy = false;
        Assert.Equal(503, (await fixture.Actions.SubmitDraftAsync(fixture.Request(), default)).StatusCode);
        Assert.Equal(0, fixture.Creates);
    }

    [Fact]
    public async Task UnknownCreateOutcomeIsCachedRatherThanRepeated()
    {
        var fixture = new Fixture { FailCreate = "broken receipt" }; var request = fixture.Request();
        var first = await fixture.Actions.SubmitDraftAsync(request, default);
        Assert.Equal("outcome-unknown", first.Outcome); Assert.Null(first.CreatedIssueId);
        Assert.Same(first, await fixture.Actions.SubmitDraftAsync(request, default));
        Assert.Equal(1, fixture.Creates);
    }

    [Fact]
    public async Task HttpCreationChecksOriginSchemaAndPolicyAndReplaysSameReceipt()
    {
        var fixture = new Fixture();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port),
            new DashboardStream(), "fixture", "actor", default, actions: fixture.Actions);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{port}") };
        var policy = await http.GetFromJsonAsync<JsonObject>("/api/v1/issues/drafts/context");
        Assert.Equal(fixture.Policy.Revision, policy!["revision"]!.GetValue<string>());
        Assert.False(policy["publicationAvailable"]!.GetValue<bool>());
        var request = fixture.Request();
        var body = new JsonObject { ["requestId"] = request.RequestId, ["expectedRevision"] = request.ExpectedRevision,
            ["title"] = "Draft", ["description"] = "", ["type"] = "task", ["priority"] = 2, ["labels"] = new JsonArray(), ["target"] = "main" };
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/issues/drafts", body)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Abacus-Request", "1");
        http.DefaultRequestHeaders.Add("Origin", "http://foreign.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/v1/issues/drafts", body)).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        body["metadata"] = new JsonObject();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/issues/drafts", body)).StatusCode);
        body.Remove("metadata");
        using var first = await http.PostAsJsonAsync("/api/v1/issues/drafts", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var retry = await http.PostAsJsonAsync("/api/v1/issues/drafts", body);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Creates);
    }
}
