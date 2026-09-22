using System.Text.Json;
using System.Text.Json.Nodes;

namespace Abacus.Tests;

public sealed class BeadsDraftCreationTests
{
    private static readonly IssueDraft Draft = new("--literal title", "--literal\n<script>", "task", 2,
        ["ordinary", ReasoningPolicy.LowLabel], "main");
    private static TargetRegistry Targets(bool enforce = false) =>
        new(new Dictionary<string, TargetPolicy> { ["main"] = new("main", null, "policy") }, enforce);

    private sealed class Fixture
    {
        public readonly List<string[]> Calls = [];
        public JsonObject Row = new()
        {
            ["id"] = "web-a", ["title"] = Draft.Title, ["description"] = Draft.Description,
            ["issue_type"] = "task", ["priority"] = 2, ["status"] = "open",
            ["defer_until"] = Beads.DraftDeferral,
            ["labels"] = new JsonArray("ordinary", ReasoningPolicy.LowLabel),
            ["metadata"] = new JsonObject { ["abacus_target"] = "main" }
        };
        public Func<int, string[], CommandResult?>? Override;
        public Task<CommandResult> Execute(IReadOnlyList<string> args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var copy = args.ToArray(); Calls.Add(copy);
            var custom = Override?.Invoke(Calls.Count, copy);
            if (custom is not null) return Task.FromResult(custom);
            if (args[0] == "create") return Task.FromResult(new CommandResult(0, "{\"id\":\"web-a\"}", ""));
            if (args[0] == "update")
            {
                if (args.Contains("--status=blocked")) Row["status"] = "blocked";
                if (args.Contains("--defer=")) Row.Remove("defer_until");
            }
            var output = args.Contains("show") ? "[" + Row.ToJsonString() + "]" : "[]";
            return Task.FromResult(new CommandResult(0, output, ""));
        }
        public Task<DraftCreationResult> Run(IssueDraft? draft = null, CancellationToken token = default) =>
            Beads.CreateDraftAsync(draft ?? Draft, Targets(), new(true), Execute, token);
    }

    [Fact]
    public async Task StagesOnlyAfterVerifiedDeferredCreationAndNeverPublishes()
    {
        var fixture = new Fixture();
        var result = await fixture.Run();
        Assert.Equal("completed", result.Outcome);
        Assert.Equal("web-a", result.IssueId);
        Assert.Equal(9, fixture.Calls.Count);
        Assert.Equal(new[] { "create", "--readonly", "--readonly", "update", "--readonly", "--readonly", "update", "--readonly", "--readonly" },
            fixture.Calls.Select(c => c[0]));
        Assert.Contains("--title=" + Draft.Title, fixture.Calls[0]);
        Assert.Contains("--description=" + Draft.Description, fixture.Calls[0]);
        Assert.Contains("--defer=" + Beads.DraftDeferral, fixture.Calls[0]);
        Assert.DoesNotContain(fixture.Calls.SelectMany(c => c), s => s == "--status=open" || s.StartsWith("--assignee"));
        Assert.Equal("blocked", fixture.Row["status"]!.GetValue<string>());
        Assert.Null(fixture.Row["defer_until"]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public async Task EveryFailedCommandStopsWithoutRetryOrRollback(int failure)
    {
        var fixture = new Fixture { Override = (step, _) => step == failure ? new(1, "", "private failure") : null };
        var result = await fixture.Run();
        Assert.Equal(failure == 1 ? "outcome-unknown" : "partially-applied", result.Outcome);
        Assert.Equal(failure == 1 ? null : "web-a", result.IssueId);
        Assert.Equal(failure, fixture.Calls.Count);
        Assert.DoesNotContain("private", result.Message);
        if (failure <= 7) Assert.Equal(Beads.DraftDeferral, fixture.Row["defer_until"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("assignee", "worker")]
    [InlineData("status", "in_progress")]
    [InlineData("status", "closed")]
    [InlineData("defer_until", "2030-01-01T00:00:00Z")]
    [InlineData("title", "external title")]
    public async Task ChangedDraftStopsBeforeChangingStatus(string field, string value)
    {
        var fixture = new Fixture(); fixture.Row[field] = value;
        var result = await fixture.Run();
        Assert.Equal("partially-applied", result.Outcome);
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Fact]
    public async Task ReadyMembershipPreventsBlockingAndClearingDeferral()
    {
        var fixture = new Fixture { Override = (_, args) => args.Contains("ready") ? new(0, "[{\"id\":\"web-a\"}]", "") : null };
        Assert.Equal("partially-applied", (await fixture.Run()).Outcome);
        Assert.Equal(3, fixture.Calls.Count);
    }

    [Theory]
    [InlineData("bad json")]
    [InlineData("{\"id\":\"web-a\",\"id\":\"web-b\"}")]
    [InlineData("{\"id\":\"--flag\"}")]
    public async Task UntrustedCreationReceiptDoesNotGuessAnId(string output)
    {
        var fixture = new Fixture { Override = (_, _) => new(0, output, "") };
        var result = await fixture.Run();
        Assert.Equal("outcome-unknown", result.Outcome);
        Assert.Null(result.IssueId);
        Assert.Single(fixture.Calls);
    }

    [Fact]
    public async Task SuccessfulUpdateWithoutPersistedBlockedStatusNeverClearsDeferral()
    {
        var fixture = new Fixture { Override = (_, args) => args.Contains("--status=blocked") ? new(0, "[]", "") : null };
        Assert.Equal("partially-applied", (await fixture.Run()).Outcome);
        Assert.DoesNotContain(fixture.Calls, args => args.Contains("--defer="));
    }

    [Fact]
    public async Task CancellationBeforeWriteIsRejected()
    {
        var fixture = new Fixture();
        Assert.Equal("rejected", (await fixture.Run(token: new CancellationToken(true))).Outcome);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData("abacus:needs-user-attention")]
    [InlineData("gt:slot")]
    [InlineData("ordinary,abacus:needs-user-attention")]
    [InlineData("ABACUS:low_reasoning")]
    public async Task ReservedOrCsvExpandedLabelsAreRejectedBeforeCommands(string label)
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Run(Draft with { Labels = [ReasoningPolicy.LowLabel, label] }));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task MissingOrConflictingReasoningAndUnknownTargetAreRejected()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Run(Draft with { Labels = [] }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Run(Draft with { Labels = [ReasoningPolicy.LowLabel, ReasoningPolicy.HighLabel] }));
        await Assert.ThrowsAsync<TargetException>(() => fixture.Run(Draft with { Target = "unknown" }));
        await Assert.ThrowsAsync<TargetException>(() => Beads.CreateDraftAsync(Draft with { Target = null }, Targets(true), new(), fixture.Execute, default));
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task OptionalEmptyLabelsAndDefaultTargetAreSupported()
    {
        var fixture = new Fixture(); fixture.Row.Remove("labels");
        var result = await Beads.CreateDraftAsync(Draft with { Labels = [], Target = null },
            Targets(), new(), fixture.Execute, default);
        Assert.Equal("completed", result.Outcome);
        Assert.DoesNotContain(fixture.Calls[0], value => value.StartsWith("--labels="));
        Assert.Contains("--metadata={\"abacus_target\":\"main\"}", fixture.Calls[0]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"id\":\"web-a\",\"priority\":\"2\"}]")]
    [InlineData("[{\"id\":\"web-a\",\"id\":\"web-b\"}]")]
    public async Task MalformedShowResponsesStopBeforeFurtherWrites(string output)
    {
        var fixture = new Fixture { Override = (_, args) => args.Contains("show") ? new(0, output, "") : null };
        Assert.Equal("partially-applied", (await fixture.Run()).Outcome);
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"id\":false}]")]
    [InlineData("[{\"id\":\"web-b\",\"id\":\"web-c\"}]")]
    public async Task UnknownReadyResponseNeverCountsAsAbsence(string output)
    {
        var fixture = new Fixture { Override = (_, args) => args.Contains("ready") ? new(0, output, "") : null };
        Assert.Equal("partially-applied", (await fixture.Run()).Outcome);
        Assert.Equal(3, fixture.Calls.Count);
    }

    [Fact]
    public async Task AddedExecutionMetadataPreventsFurtherStaging()
    {
        var fixture = new Fixture(); fixture.Row["metadata"]!["abacus_execution"] = new JsonObject();
        Assert.Equal("partially-applied", (await fixture.Run()).Outcome);
        Assert.Equal(2, fixture.Calls.Count);
    }

    [Fact]
    public async Task CancellationAfterBlockingLeavesDeferralAndKnownIdForReview()
    {
        using var source = new CancellationTokenSource();
        var fixture = new Fixture { Override = (step, _) => { if (step == 4) source.Cancel(); return null; } };
        var result = await fixture.Run(token: source.Token);
        Assert.Equal("partially-applied", result.Outcome);
        Assert.Equal("web-a", result.IssueId);
        Assert.Equal("blocked", fixture.Row["status"]!.GetValue<string>());
        Assert.Equal(Beads.DraftDeferral, fixture.Row["defer_until"]!.GetValue<string>());
        Assert.Equal(4, fixture.Calls.Count);
    }

    [Fact]
    public async Task MutatingCallerLabelsDuringCreateCannotChangeVerification()
    {
        var labels = Draft.Labels.ToList();
        var fixture = new Fixture { Override = (step, _) => { if (step == 1) labels.Clear(); return null; } };
        Assert.Equal("completed", (await fixture.Run(Draft with { Labels = labels })).Outcome);
    }
}
