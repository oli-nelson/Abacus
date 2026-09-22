using System.Text.Json.Nodes;
using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DraftCandidateReviewTests
{
    private static DraftPolicy Policy(bool enforce = false) => new(new(new Dictionary<string, TargetPolicy>
        { ["main"] = new("main", null, "policy") }, enforce), new(enforce));
    private static JsonObject Source() => JsonNode.Parse("""
        {"id":"web-a","title":"Draft","description":"Literal text","issue_type":"task","priority":2,"status":"blocked","metadata":{"abacus_target":"main"},"labels":["abacus:low_reasoning"]}
        """)!.AsObject();
    private static DraftCandidateReview Review(JsonObject source, DraftPolicy? policy = null) =>
        DraftCandidateReview.Inspect(IssueExport.Parse(source.ToJsonString()).Issues["web-a"], policy ?? Policy());

    [Fact]
    public void ValidBlockedDraftHasOnlyContentAndLifecycleEvidence()
    {
        var result = Review(Source(), Policy(true));
        Assert.True(result.ContentPolicyValid); Assert.True(result.DraftLifecycleClear);
        Assert.Equal("main", result.ConfiguredTarget); Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData("title", "")]
    [InlineData("issue_type", "unsupported")]
    [InlineData("priority", "2")]
    [InlineData("description", null)]
    public void MalformedContentIsNotDefaultedIntoApproval(string field, string? value)
    {
        var source = Source(); source[field] = value is null ? JsonValue.Create(42) : JsonValue.Create(value);
        Assert.False(Review(source).ContentPolicyValid);
    }

    [Theory]
    [InlineData("status", "closed")]
    [InlineData("status", "open")]
    [InlineData("assignee", "worker")]
    [InlineData("defer_until", "9999-12-31T00:00:00Z")]
    public void LifecycleCannotSilentlyReopenUnassignOrClearDeferral(string field, string value)
    {
        var source = Source(); source[field] = value;
        var result = Review(source);
        Assert.False(result.DraftLifecycleClear); Assert.True(result.ContentPolicyValid);
    }

    [Fact]
    public void StrictPolicyAndMalformedLabelsFailClosed()
    {
        var source = Source(); source.Remove("labels");
        Assert.False(Review(source, Policy(true)).ContentPolicyValid);
        Assert.True(Review(source).ContentPolicyValid);
        source["labels"] = new JsonArray("ordinary", 3);
        Assert.False(Review(source).ContentPolicyValid);
        source["labels"] = new JsonArray("abacus:low_reasoning", "abacus:high_reasoning");
        Assert.False(Review(source).ContentPolicyValid);
        source["labels"] = new JsonArray("abacus:needs-user-attention");
        Assert.False(Review(source).ContentPolicyValid);
    }

    [Fact]
    public void InvalidMetadataIsNotReplacedByDefaultTargetAndDetailsAreSanitized()
    {
        var source = Source(); source["metadata"]!["abacus_target"] = "private-invalid-target";
        var invalid = Review(source); Assert.False(invalid.ContentPolicyValid);
        Assert.DoesNotContain("private-invalid-target", string.Join(' ', invalid.Reasons));
        source["metadata"]!["abacus_target"] = "main";
        source["metadata"]!["abacus_execution"] = new JsonObject();
        Assert.False(Review(source).DraftLifecycleClear);
        source["metadata"] = 42;
        Assert.False(Review(source).ContentPolicyValid);
    }

    [Fact]
    public void MissingTargetUsesDefaultOnlyWhenPolicyAllowsIt()
    {
        var source = Source(); source.Remove("metadata");
        Assert.True(Review(source).ContentPolicyValid);
        Assert.False(Review(source, Policy(true)).ContentPolicyValid);
    }
}
