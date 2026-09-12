using Abacus;

namespace Abacus.Tests;

public sealed class ReasoningPolicyTests
{
    [Fact]
    public async Task MissingConfigurationDefaultsToOptionalLabels()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var policy = await ReasoningPolicy.LoadAsync(path, CancellationToken.None);
        Assert.False(policy.EnforceLabels);
    }

    [Fact]
    public async Task LoadsEnforcementAndRejectsUnknownProperties()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """{"version":1,"enforceLabels":true}""");
            Assert.True((await ReasoningPolicy.LoadAsync(path, CancellationToken.None)).EnforceLabels);
            await File.WriteAllTextAsync(path, """{"version":1,"unknown":true}""");
            await Assert.ThrowsAsync<ReasoningPolicyException>(() =>
                ReasoningPolicy.LoadAsync(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EnforcementRequiresEveryRuntimeMapping()
    {
        var exception = Assert.Throws<ReasoningPolicyException>(() =>
            new ReasoningPolicy(enforceLabels: true).ValidateMappings(
                new Dictionary<string, string>
                {
                    [ReasoningPolicy.HighLabel] = "high-model",
                }));
        Assert.Contains(ReasoningPolicy.MediumLabel, exception.Message);
        Assert.Contains(ReasoningPolicy.LowLabel, exception.Message);
    }

    [Fact]
    public void ExactReasoningLabelsAreRecognizedAlongsideUnrelatedLabels()
    {
        var issue = new BeadsIssue(
            "abc-1",
            IssueStatus.Open,
            Labels: ["feature", ReasoningPolicy.MediumLabel]);
        var resolution = new ReasoningPolicy().ResolveModel(
            issue,
            new Dictionary<string, string> { [ReasoningPolicy.MediumLabel] = "medium-model" },
            "default-model");
        Assert.Equal("medium-model", resolution.Model);
        Assert.Equal("high", resolution.Effort);
        Assert.False(resolution.UsedDefault);
    }

    [Fact]
    public void ReasoningLabelResolvesModelAndEffortIndependently()
    {
        var issue = new BeadsIssue("abc-2", IssueStatus.Open, Labels: [ReasoningPolicy.LowLabel]);
        var resolution = new ReasoningPolicy().ResolveModel(
            issue,
            new Dictionary<string, string> { [ReasoningPolicy.LowLabel] = "small-model" },
            "default-model",
            new Dictionary<string, string> { [ReasoningPolicy.LowLabel] = "low" },
            "xhigh");

        Assert.Equal("small-model", resolution.Model);
        Assert.Equal("low", resolution.Effort);
        Assert.False(resolution.UsedDefaultModel);
        Assert.False(resolution.UsedDefaultEffort);
    }

    [Fact]
    public void MissingReasoningEffortFallsBackToGlobalEffort()
    {
        var issue = new BeadsIssue("abc-3", IssueStatus.Open, Labels: [ReasoningPolicy.HighLabel]);
        var resolution = new ReasoningPolicy().ResolveModel(
            issue,
            new Dictionary<string, string> { [ReasoningPolicy.HighLabel] = "large-model" },
            "default-model",
            effortMappings: null,
            defaultEffort: "max");

        Assert.Equal("max", resolution.Effort);
        Assert.True(resolution.UsedDefaultEffort);
    }

    [Fact]
    public void PerTierArgumentsOverrideTheDefaultSetAndFallBackWhenUnmapped()
    {
        var policy = new ReasoningPolicy();
        var mappings = new Dictionary<string, string>
        {
            [ReasoningPolicy.HighLabel] = "large-model",
            [ReasoningPolicy.LowLabel] = "small-model",
        };
        var argumentMappings = new Dictionary<string, IReadOnlyList<string>>
        {
            [ReasoningPolicy.HighLabel] = ["-p", "deepseek"],
        };

        var high = policy.ResolveModel(
            new BeadsIssue("abc-4", IssueStatus.Open, Labels: [ReasoningPolicy.HighLabel]),
            mappings,
            "default-model",
            argumentMappings: argumentMappings,
            defaultArguments: ["-p", "openai"]);
        Assert.Equal(new[] { "-p", "deepseek" }, high.ExtraArguments);

        var low = policy.ResolveModel(
            new BeadsIssue("abc-5", IssueStatus.Open, Labels: [ReasoningPolicy.LowLabel]),
            mappings,
            "default-model",
            argumentMappings: argumentMappings,
            defaultArguments: ["-p", "openai"]);
        Assert.Equal(new[] { "-p", "openai" }, low.ExtraArguments);

        var unlabelled = policy.ResolveModel(
            new BeadsIssue("abc-6", IssueStatus.Open),
            mappings,
            "default-model",
            argumentMappings: argumentMappings,
            defaultArguments: ["-p", "openai"]);
        Assert.Equal(new[] { "-p", "openai" }, unlabelled.ExtraArguments);

        var unmapped = policy.ResolveModel(
            new BeadsIssue("abc-7", IssueStatus.Open, Labels: [ReasoningPolicy.HighLabel]),
            mappings,
            "default-model");
        Assert.Empty(unmapped.ExtraArguments!);
    }
}
