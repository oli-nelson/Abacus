using System.Text.Json;

namespace Abacus;

public sealed partial class Beads
{
    private static readonly JsonSerializerOptions BindingJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static BeadsIssue ReadRoutingMetadata(BeadsIssue issue, JsonElement element)
    {
        if (!element.TryGetProperty("metadata", out var metadata) || metadata.ValueKind == JsonValueKind.Null)
            return issue;
        if (metadata.ValueKind != JsonValueKind.Object)
            return issue with { MetadataError = "ticket metadata must be a JSON object containing abacus_target" };
        string? target = null;
        ExecutionBinding? binding = null;
        try
        {
            if (metadata.TryGetProperty("abacus_target", out var value))
            {
                if (value.ValueKind == JsonValueKind.String) target = value.GetString();
                else issue = issue with { HasInvalidTargetMetadata = true };
            }
            if (metadata.TryGetProperty("abacus_execution", out value))
            {
                binding = value.Deserialize<ExecutionBinding>(BindingJson)
                    ?? throw new JsonException("abacus_execution cannot be null");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return issue with { TargetBranch = target, MetadataError = $"invalid Abacus ticket metadata: {exception.Message}" };
        }
        return issue with { TargetBranch = target, Binding = binding };
    }

    public async Task<IReadOnlyList<BeadsIssue>> GetTargetAuditIssuesAsync(string workspace, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspace, null,
            ["list", "--all", "--limit", "0", "--exclude-label", "gt:slot", "--json"], cancellationToken);
        EnsureCommandSuccess(result, "list tickets for target audit");
        return ParseIssues(result.StandardOutput, "target audit");
    }

    public async Task SetTargetMetadataAsync(string workspace, string issueId, string target, CancellationToken cancellationToken)
    {
        // bd 1.2.2 --metadata merges object keys. --set-metadata is scalar-oriented:
        // quoted strings/objects become literal strings, so it cannot encode our binding.
        var result = await RunAsync(workspace, null,
            ["update", issueId, "--metadata", JsonSerializer.Serialize(new { abacus_target = target }), "--json"], cancellationToken);
        EnsureCommandSuccess(result, $"set target for '{issueId}'");
    }

    public async Task SetExecutionBindingAsync(string workspace, string agentName, string issueId,
        ExecutionBinding binding, CancellationToken cancellationToken)
    {
        var result = await RunWithActorAsync(workspace, agentName,
            ["update", issueId, "--metadata", JsonSerializer.Serialize(new { abacus_execution = binding }, BindingJson), "--json"], cancellationToken);
        EnsureCommandSuccess(result, $"bind execution for '{issueId}'");
    }

    public async Task BlockTargetIssueAsync(string workspace, string agentName, string issueId,
        string reason, CancellationToken cancellationToken)
    {
        // Only mutate a claim we still own; do not overwrite an observed terminal-state race.
        var current = await GetIssueAsync(workspace, agentName, issueId, cancellationToken);
        if (current is not { Status: IssueStatus.InProgress } || current.Assignee != agentName)
            throw new BeadsException($"cannot block '{issueId}': the target-validation claim is no longer owned by '{agentName}'");
        var result = await RunWithActorAsync(workspace, agentName,
            ["update", issueId, "--status", "blocked", "--assignee", "",
                "--add-label", NeedsUserAttentionLabel, "--append-notes", $"BLOCKED: {reason}", "--json"], cancellationToken);
        EnsureCommandSuccess(result, $"block invalid target on '{issueId}'");
        var verification = await RunWithActorAsync(workspace, agentName,
            ["show", issueId, "--json"], cancellationToken);
        EnsureCommandSuccess(verification, $"verify target-validation block for '{issueId}'");
        var verified = ParseIssues(verification.StandardOutput, "target block verification").SingleOrDefault();
        if (verified?.Id != issueId || verified.Status != IssueStatus.Blocked || !string.IsNullOrEmpty(verified.Assignee))
            throw new BeadsException($"could not verify target-validation block for '{issueId}'");
        using var document = JsonDocument.Parse(verification.StandardOutput);
        var element = document.RootElement[0];
        if (!element.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array
            || !labels.EnumerateArray().Any(l => l.ValueKind == JsonValueKind.String && l.GetString() == NeedsUserAttentionLabel)
            || !element.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.String
            || !notes.GetString()!.Contains(reason, StringComparison.Ordinal))
            throw new BeadsException($"could not verify attention label and target-validation reason for '{issueId}'");
    }
}
