namespace Abacus.Tests;

public sealed class BeadsGraphValidationTests
{
    private const string Clean = "{\"clean\":true,\"cycles\":null,\"schema_version\":1,\"summary\":{\"cycle_count\":0}}";

    [Fact]
    public async Task RequiresBothReadOnlyCliChecksAndDoesNotClaimReadiness()
    {
        var calls = new List<string[]>();
        var result = await Beads.CheckDependencyGraphAsync((args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new CommandResult(0, calls.Count == 1 ? Clean : "[]", ""));
        }, default);
        Assert.True(result.CyclesClear);
        Assert.Equal(new[] { "--readonly", "graph", "check", "--json" }, calls[0]);
        Assert.Equal(new[] { "--readonly", "dep", "cycles", "--json" }, calls[1]);
        Assert.Contains("does not prove readiness", result.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not JSON")]
    [InlineData("{\"clean\":true}")]
    [InlineData("{\"clean\":true,\"cycles\":null,\"schema_version\":2,\"summary\":{\"cycle_count\":0}}")]
    [InlineData("{\"clean\":true,\"cycles\":null,\"schema_version\":1,\"summary\":{\"cycle_count\":1}}")]
    [InlineData("{\"clean\":true,\"clean\":true,\"cycles\":null,\"schema_version\":1,\"summary\":{\"cycle_count\":0}}")]
    [InlineData("{\"clean\":true,\"cycles\":null,\"schema_version\":1,\"summary\":{\"cycle_count\":0,\"orphan_count\":1}}")]
    public async Task UnknownOrAmbiguousOutputNeverAuthorizesPublication(string output)
    {
        var calls = 0;
        var result = await Beads.CheckDependencyGraphAsync((_, _) =>
        { calls++; return Task.FromResult(new CommandResult(0, output, "secret diagnostics")); }, default);
        Assert.Null(result.CyclesClear); Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Theory]
    [InlineData(1, "[]")]
    [InlineData(0, "{}")]
    [InlineData(0, "[{\"cycle\":[\"abc-1\",\"abc-2\"]}]")]
    public async Task FailedOrDisagreeingSecondCheckIsUnknown(int code, string output)
    {
        var calls = 0;
        var result = await Beads.CheckDependencyGraphAsync((_, _) =>
            Task.FromResult(++calls == 1 ? new CommandResult(0, Clean, "") : new(code, output, "private")), default);
        Assert.Null(result.CyclesClear);
    }

    [Fact]
    public async Task FailedCommandCannotAssertCleanUsingSuccessShapedOutput()
    {
        var result = await Beads.CheckDependencyGraphAsync((_, _) => Task.FromResult(new CommandResult(1, Clean, "")), default);
        Assert.Null(result.CyclesClear);
    }

    [Fact]
    public async Task ConsistentCycleReportStopsWithoutQueryingAgain()
    {
        var calls = 0;
        var result = await Beads.CheckDependencyGraphAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(new CommandResult(1,
                "{\"clean\":false,\"cycles\":[[\"abc-1\",\"abc-2\"]],\"schema_version\":1,\"summary\":{\"cycle_count\":1}}", ""));
        }, default);
        Assert.False(result.CyclesClear); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationBeforeOrBetweenChecksNeverStartsAnotherCommand()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Beads.CheckDependencyGraphAsync(
            (_, _) => throw new Exception("No command expected"), before.Token));
        using var between = new CancellationTokenSource(); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Beads.CheckDependencyGraphAsync((_, _) =>
        { calls++; between.Cancel(); return Task.FromResult(new CommandResult(0, Clean, "")); }, between.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SourceFailureIsUnknownAndSanitized()
    {
        var result = await Beads.CheckDependencyGraphAsync((_, _) => throw new IOException("secret path"), default);
        Assert.Null(result.CyclesClear); Assert.DoesNotContain("secret", result.Message);
    }

    [DraftCliFact]
    public async Task InstalledCliCleanGraphContractMatchesParser()
    {
        const string repository = "/tmp/abacus-web-contract";
        Assert.True(Directory.Exists(Path.Combine(repository, ".beads")));
        var runner = new CommandRunner(TextWriter.Null);
        var result = await Beads.CheckDependencyGraphAsync((args, token) =>
            runner.RunAsync(new("bd", args, repository, MaxOutputCharacters: 1024 * 1024), token), default);
        Assert.True(result.CyclesClear, result.Message);
    }
}
