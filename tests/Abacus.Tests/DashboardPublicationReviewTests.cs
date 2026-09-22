using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardPublicationReviewTests
{
    private static readonly DraftPolicy Policy = new(new(new Dictionary<string, TargetPolicy> { ["main"] = new("main", null, "v1") }), new());
    private const string Graph = "{\"clean\":true,\"cycles\":null,\"schema_version\":1,\"summary\":{\"cycle_count\":0}}";
    private static Task<CommandResult> Execute(IReadOnlyList<string> args, CancellationToken token)
    {
        Assert.Equal("--readonly", args[0]);
        return Task.FromResult(new CommandResult(0, args[1] == "graph" ? Graph : "[]", ""));
    }

    [Theory]
    [InlineData("", "unknown")]
    [InlineData(",\"dependencies\":[]", "recorded-direct-edges")]
    [InlineData(",\"dependencies\":[{\"issue_id\":\"web-a\",\"depends_on_id\":\"missing\",\"type\":\"blocks\"}]", "missing-targets")]
    public async Task ReviewPreservesUnknownEmptyAndMissingDependencyDistinctions(string fields, string expected)
    {
        var snapshot = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"blocked\"" + fields + "}");
        var actions = new IssueActions(_ => Task.FromResult(snapshot), Execute, () => true,
            () => throw new Exception("Read must not dirty collectors"), default, draftPolicy: _ => Task.FromResult(Policy));
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(await actions.ReviewPublicationAsync("web-a", default), DashboardStream.Json));
        Assert.Equal(expected, result.RootElement.GetProperty("dependencyCoverage").GetString());
        Assert.False(result.RootElement.GetProperty("publicationAvailable").GetBoolean());
        Assert.True(result.RootElement.GetProperty("cycles").GetProperty("cyclesClear").GetBoolean());
        Assert.Equal("not-verified", result.RootElement.GetProperty("ownership").GetString());
    }

    [Fact]
    public async Task ClosedPrerequisiteNeverBecomesIntegrationProof()
    {
        var snapshot = IssueExport.Parse("""
            {"id":"web-a","status":"blocked","dependencies":[{"issue_id":"web-a","depends_on_id":"web-b","type":"blocks"}]}
            {"id":"web-b","status":"closed"}
            """);
        var actions = new IssueActions(_ => Task.FromResult(snapshot), Execute, () => true, () => { }, default,
            draftPolicy: _ => Task.FromResult(Policy));
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(await actions.ReviewPublicationAsync("web-a", default), DashboardStream.Json));
        var dependency = result.RootElement.GetProperty("dependencies")[0];
        Assert.Equal("closed", dependency.GetProperty("status").GetString());
        Assert.Equal("not-verified", dependency.GetProperty("integration").GetString());
    }

    [Fact]
    public async Task ChangedSourceDiscardsReviewRatherThanPublishingMixedEvidence()
    {
        var reads = 0;
        var actions = new IssueActions(_ => Task.FromResult(IssueExport.Parse(
            ++reads == 1 ? "{\"id\":\"web-a\",\"status\":\"blocked\"}" : "{\"id\":\"web-a\",\"status\":\"open\"}")),
            Execute, () => true, () => { }, default, draftPolicy: _ => Task.FromResult(Policy));
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.ReviewPublicationAsync("web-a", default));
    }

    [Fact]
    public async Task PolicyChangeDuringReviewDiscardsEvidenceAndReleasesAdmission()
    {
        var snapshot = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"blocked\"}");
        var reads = 0;
        var actions = new IssueActions(_ => Task.FromResult(snapshot), Execute, () => true, () => { }, default,
            draftPolicy: _ => Task.FromResult(++reads == 1 ? Policy : Policy with { Reasoning = new(true) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.ReviewPublicationAsync("web-a", default));
        Assert.NotNull(await actions.ReviewPublicationAsync("web-a", default));
    }

    [Fact]
    public async Task UnchangedSelectedIssueCannotMaskDependencySourceChanges()
    {
        var before = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"blocked\"}\n{\"id\":\"web-b\",\"status\":\"open\"}");
        var after = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"blocked\"}\n{\"id\":\"web-b\",\"status\":\"closed\"}");
        var reads = 0;
        var actions = new IssueActions(_ => Task.FromResult(++reads == 1 ? before : after), Execute,
            () => true, () => { }, default, draftPolicy: _ => Task.FromResult(Policy));
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.ReviewPublicationAsync("web-a", default));
    }

    [Fact]
    public async Task ReviewRevisionCoversWholeSourceAndPolicyButNotExportOrder()
    {
        const string selected = "{\"id\":\"web-a\",\"status\":\"blocked\"}";
        const string other = "{\"id\":\"web-b\",\"status\":\"open\"}";
        async Task<string> Revision(string json, DraftPolicy policy, string id = "web-a", bool readableCycles = true)
        {
            var snapshot = IssueExport.Parse(json);
            var actions = new IssueActions(_ => Task.FromResult(snapshot),
                (args, token) => readableCycles ? Execute(args, token) : Task.FromResult(new CommandResult(1, "", "")), () => true, () => { }, default,
                draftPolicy: _ => Task.FromResult(policy));
            using var review = JsonDocument.Parse(JsonSerializer.Serialize(await actions.ReviewPublicationAsync(id, default), DashboardStream.Json));
            return review.RootElement.GetProperty("reviewRevision").GetString()!;
        }
        var baseline = await Revision(selected + "\n" + other, Policy);
        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, await Revision(other + "\n" + selected, Policy));
        Assert.NotEqual(baseline, await Revision(selected + "\n" + other.Replace("open", "closed"), Policy));
        Assert.NotEqual(baseline, await Revision(selected, Policy));
        Assert.NotEqual(baseline, await Revision(selected + "\n" + other, Policy with { Reasoning = new(true) }));
        Assert.NotEqual(baseline, await Revision(selected + "\n" + other, Policy, id: "web-b"));
        Assert.NotEqual(baseline, await Revision(selected + "\n" + other, Policy, readableCycles: false));
    }

    [Fact]
    public async Task ConcurrentReviewIsBoundedAndCancellationReleasesAdmission()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var actions = new IssueActions(async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return IssueExport.Parse(""); },
            Execute, () => true, () => { }, default, draftPolicy: _ => Task.FromResult(Policy));
        var pending = actions.ReviewPublicationAsync("web-a", cancel.Token);
        await entered.Task;
        await Assert.ThrowsAsync<InvalidDataException>(() => actions.ReviewPublicationAsync("web-a", default));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var second = new CancellationTokenSource();
        var next = actions.ReviewPublicationAsync("web-a", second.Token);
        Assert.False(next.IsCompleted); second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task HttpReviewRejectsMalformedPathsAndDoesNotExposeRawMetadata()
    {
        var snapshot = IssueExport.Parse("{\"id\":\"web-a\",\"status\":\"blocked\",\"dependencies\":[],\"metadata\":{\"private\":\"secret-token\"}}");
        var actions = new IssueActions(_ => Task.FromResult(snapshot), Execute, () => true, () => { }, default,
            draftPolicy: _ => Task.FromResult(Policy));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
        await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port),
            new DashboardStream(), "fixture", "actor", default, actions: actions);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{port}") };
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/issues/publication-review")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/issues/web-a/publication-review?force=true")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/issues/web-missing/publication-review")).StatusCode);
        var body = await http.GetStringAsync("/api/v1/issues/web-a/publication-review");
        Assert.DoesNotContain("secret-token", body);
        using var result = JsonDocument.Parse(body);
        Assert.False(result.RootElement.GetProperty("publicationAvailable").GetBoolean());
        Assert.Equal(snapshot.Issues["web-a"].Revision, result.RootElement.GetProperty("issueRevision").GetString());
    }
}
