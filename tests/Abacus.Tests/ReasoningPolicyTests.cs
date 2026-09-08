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
        Assert.False(resolution.UsedDefault);
    }
}
