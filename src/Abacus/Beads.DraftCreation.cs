using System.Globalization;
using System.Text.Json;

namespace Abacus;

internal sealed record IssueDraft(string Title, string Description, string Type, int Priority,
    IReadOnlyList<string> Labels, string? Target);
internal sealed record DraftCreationResult(string Outcome, string? IssueId, string Message);

public sealed partial class Beads
{
    internal const string DraftDeferral = "9999-12-31T00:00:00Z";

    // Only a staging primitive. The dashboard admission/retry ledger and current
    // policy/ownership checks must wrap it; this never publishes or auto-retries.
    internal static async Task<DraftCreationResult> CreateDraftAsync(IssueDraft draft,
        TargetRegistry targets, ReasoningPolicy reasoning,
        Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> execute,
        CancellationToken token)
    {
        var (target, labels) = ValidateDraft(draft, targets, reasoning);
        // Only the resolved target is authored, never caller-supplied metadata or bindings.
        List<string> arguments = ["create", "--json", "--title=" + draft.Title,
            "--description=" + draft.Description, "--type=" + draft.Type,
            "--priority=" + draft.Priority.ToString(CultureInfo.InvariantCulture),
            "--metadata=" + JsonSerializer.Serialize(new { abacus_target = target }),
            "--defer=" + DraftDeferral];
        if (labels.Length > 0) arguments.Add("--labels=" + string.Join(',', labels));

        string? id = null;
        var attempted = false;
        var stage = "creation";
        try
        {
            token.ThrowIfCancellationRequested();
            attempted = true;
            var created = await execute(arguments, token);
            if (!created.Succeeded) throw new InvalidDataException("Creation was not confirmed.");
            using (var document = JsonDocument.Parse(created.StandardOutput))
            {
                var root = document.RootElement;
                RequireObject(root);
                var value = String(root, "id");
                if (value is null || !Git.IsValidIssueId(value)) throw new InvalidDataException("Creation ID unavailable.");
                id = value;
            }

            stage = "deferred creation verification";
            await VerifyAsync(blocked: false, deferred: true);
            stage = "blocked staging";
            await UpdateAsync("--status=blocked");
            await VerifyAsync(blocked: true, deferred: true);
            // Never remove the non-ready creation guard until persisted blocked
            // status AND absence from ready work have both been verified.
            stage = "staging deferral removal";
            await UpdateAsync("--defer=");
            await VerifyAsync(blocked: true, deferred: false);
            return new("completed", id, "Blocked, unassigned draft verified. Publication is a separate reviewed operation; no automatic remote synchronization.");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or CommandStartException or
            CommandTimeoutException or CommandOutputLimitException or OperationCanceledException or IOException)
        {
            return new(!attempted ? "rejected" : id is null ? "outcome-unknown" : "partially-applied", id,
                !attempted ? "Creation cancelled before any write."
                : id is null ? "Creation outcome unknown. Inspect current issues before starting another create request; do not blindly duplicate it."
                : $"Draft {id} needs review after {stage}. No publication, rollback or automatic retry was attempted.");
        }

        async Task UpdateAsync(string field)
        {
            token.ThrowIfCancellationRequested();
            var result = await execute(["update", id!, field, "--json"], token);
            if (!result.Succeeded) throw new InvalidDataException("Draft stage was not confirmed.");
        }

        async Task VerifyAsync(bool blocked, bool deferred)
        {
            var result = await execute(["--readonly", "show", id!, "--json"], token);
            if (!result.Succeeded) throw new InvalidDataException("Draft unreadable.");
            using var document = JsonDocument.Parse(result.StandardOutput);
            var rows = document.RootElement;
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 1)
                throw new InvalidDataException("Draft response unavailable.");
            var row = rows[0]; RequireObject(row);
            if (String(row, "id") != id || String(row, "title") != draft.Title ||
                (String(row, "description") ?? "") != draft.Description || String(row, "issue_type") != draft.Type ||
                !row.TryGetProperty("priority", out var priority) || priority.ValueKind != JsonValueKind.Number ||
                !priority.TryGetInt32(out var number) || number != draft.Priority ||
                !string.IsNullOrEmpty(String(row, "assignee")) ||
                (blocked ? String(row, "status") != "blocked" : String(row, "status") is not ("open" or "deferred")))
                throw new InvalidDataException("Draft content or ownership changed.");
            var until = String(row, "defer_until");
            if (deferred ? !DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    date != DateTimeOffset.Parse(DraftDeferral, CultureInfo.InvariantCulture)
                : !string.IsNullOrEmpty(until)) throw new InvalidDataException("Draft deferral not verified.");
            var storedLabels = row.TryGetProperty("labels", out var labelArray) ? labelArray : default;
            if (storedLabels.ValueKind == JsonValueKind.Undefined && labels.Length == 0)
            { /* Beads omits empty optional collections. */ }
            else if (storedLabels.ValueKind != JsonValueKind.Array ||
                storedLabels.EnumerateArray().Any(label => label.ValueKind != JsonValueKind.String) ||
                !storedLabels.EnumerateArray().Select(label => label.GetString()!).Order(StringComparer.Ordinal)
                    .SequenceEqual(labels.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Draft labels changed.");
            if (!row.TryGetProperty("metadata", out var metadata)) throw new InvalidDataException("Draft target unavailable.");
            RequireObject(metadata);
            if (String(metadata, "abacus_target") != target || metadata.EnumerateObject().Any(field => field.Name != "abacus_target"))
                throw new InvalidDataException("Draft routing changed.");

            var ready = await execute(["--readonly", "ready", "--unassigned", "--limit", "0", "--json"], token);
            if (!ready.Succeeded) throw new InvalidDataException("Draft readiness unavailable.");
            using var readyDocument = JsonDocument.Parse(ready.StandardOutput);
            if (readyDocument.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Readiness response invalid.");
            foreach (var item in readyDocument.RootElement.EnumerateArray())
            {
                RequireObject(item);
                var readyId = String(item, "id");
                if (readyId is null || !Git.IsValidIssueId(readyId) || readyId == id)
                    throw new InvalidDataException("Draft non-ready state not verified.");
            }
        }
    }

    internal static (string Target, string[] Labels) ValidateDraft(IssueDraft draft, TargetRegistry targets, ReasoningPolicy reasoning)
    {
        static void Content(string value, int limit, bool required)
        {
            if (value is null || value.Length > limit || value.Contains('\0') || required && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Invalid draft content.");
        }
        Content(draft.Title, 500, true);
        Content(draft.Description, 32_000, false);
        if (draft.Type is not ("task" or "bug" or "feature" or "epic" or "chore" or "decision") || draft.Priority is < 0 or > 4)
            throw new ArgumentException("Unsupported draft type or priority.");
        if (draft.Labels is null || draft.Labels.Count > 32)
            throw new ArgumentException("Draft labels must be a bounded list.");
        var labels = draft.Labels.ToArray(); // Do not retain a caller-owned mutable list across awaits.
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
            throw new ArgumentException("Duplicate draft labels.");
        var reasoningLabels = labels.Where(ReasoningPolicy.Labels.Contains).ToArray();
        if (reasoningLabels.Length > 1 || reasoning.EnforceLabels && reasoningLabels.Length != 1)
            throw new ArgumentException("Draft requires a single valid reasoning label under the current policy.");
        _ = LabelDeltaArguments(labels.Except(reasoningLabels, StringComparer.Ordinal).ToArray(), null);
        var target = targets.Validate(new("draft", IssueStatus.Open, TargetBranch: draft.Target)).Branch;
        return (target, labels);
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
            throw new InvalidDataException("Ambiguous draft response.");
    }

    private static string? String(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid draft field.");
        return item.GetString();
    }
}
