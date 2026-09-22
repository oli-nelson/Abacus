using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record DraftCandidateReview(bool ContentPolicyValid, bool DraftLifecycleClear,
    string? ConfiguredTarget, IReadOnlyList<string> Reasons)
{
    // These two checks are deliberately not named CanPublish: neither establishes
    // ownership/quiescence, dependency completeness or Git integration.
    internal static DraftCandidateReview Inspect(IssueRecord record, DraftPolicy policy)
    {
        var source = record.Source;
        var reasons = new List<string>();
        static string? Text(JsonElement source, string field)
        {
            if (!source.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid field shape.");
            return value.GetString();
        }
        var lifecycle = true;
        try
        {
            if (Text(source, "status") != "blocked") { lifecycle = false; reasons.Add("Issue is not a blocked draft."); }
            if (!string.IsNullOrEmpty(Text(source, "assignee"))) { lifecycle = false; reasons.Add("Assignment must be reviewed and reconciled separately."); }
            if (!string.IsNullOrEmpty(Text(source, "defer_until"))) { lifecycle = false; reasons.Add("A deferral remains; staging or an independent deferral needs review."); }
        }
        catch (InvalidDataException)
        { lifecycle = false; reasons.Add("Status, assignment or deferral is unreadable."); }
        var routing = Beads.ReadRoutingMetadata(new(record.Id, IssueStatus.Unknown), source);
        if (routing.Binding is not null || routing.MetadataError is not null)
        { lifecycle = false; reasons.Add("Execution metadata is bound or uncertain; reservation reconciliation is required."); }
        string? configuredTarget = null;
        var content = false;
        try
        {
            // Validate the raw fields, not the permissive display projection:
            // dropping malformed labels or defaulting an unknown priority could
            // otherwise turn incomplete source data into an approval signal.
            var labels = Array.Empty<string>();
            if (source.TryGetProperty("labels", out var values))
            {
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 32 ||
                    values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) throw new InvalidDataException();
                labels = values.EnumerateArray().Select(value => value.GetString()!).ToArray();
            }
            if (!source.TryGetProperty("priority", out var priority) || priority.ValueKind != JsonValueKind.Number ||
                !priority.TryGetInt32(out var number)) throw new InvalidDataException();
            var target = policy.Targets.Validate(routing).Branch;
            var draft = new IssueDraft(Text(source, "title") ?? "", Text(source, "description") ?? "",
                Text(source, "issue_type") ?? "", number, labels, target);
            configuredTarget = Beads.ValidateDraft(draft, policy.Targets, policy.Reasoning).Target;
            content = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or TargetException)
        { reasons.Add("Issue content, labels or target/reasoning policy is invalid or incomplete."); }
        return new(content, lifecycle, configuredTarget, reasons);
    }
}
