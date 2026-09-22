using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record DraftPolicy(TargetRegistry Targets, ReasoningPolicy Reasoning)
{
    public string Revision => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Targets.EnforceTargetBranch, Targets.DefaultTarget, Reasoning.EnforceLabels,
        targets = Targets.Targets.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { branch = p.Key, p.Value.Identity })
    })));

    public static async Task<DraftPolicy> LoadAsync(string repository, CancellationToken token) => new(
        await TargetRegistry.LoadAsync(Path.Combine(repository, ".abacus", "targets.json"), token),
        await ReasoningPolicy.LoadAsync(Path.Combine(repository, ".abacus", "reasoning.json"), token));
}

internal sealed record CreateDraftAction(string RequestId, string ExpectedRevision, IssueDraft Draft);

internal sealed partial class IssueActions
{
    private readonly SemaphoreSlim publicationReads = new(1, 1);

    // Explicit, read-only review. Never interprets closed prerequisites as merged
    // and never grants publication permission based on CLI cycle checks alone.
    public async Task<object> ReviewPublicationAsync(string issueId, CancellationToken token)
    {
        if (!Git.IsValidIssueId(issueId)) throw new ArgumentException("Invalid issue ID.");
        if (draftPolicy is null || !healthy() || !await publicationReads.WaitAsync(0, token))
            throw new InvalidDataException("Publication review unavailable or busy.");
        try
        {
            var before = await read(token);
            if (!before.Issues.TryGetValue(issueId, out var record)) throw new KeyNotFoundException("Issue unavailable.");
            var policy = await draftPolicy(token);
            var projection = new IssueProjection(); projection.Apply(before);
            var issue = projection.Current.Issues[issueId];
            var cycles = await Beads.CheckDependencyGraphAsync(execute, token);
            var after = await read(token);
            var currentPolicy = await draftPolicy(token);
            if (!healthy() || policy.Revision != currentPolicy.Revision || before.Issues.Count != after.Issues.Count ||
                before.Issues.Any(pair => !after.Issues.TryGetValue(pair.Key, out var next) || pair.Value.Revision != next.Revision))
                throw new InvalidDataException("Sources changed during publication review.");
            var dependencies = issue.Dependencies?.Select(edge => new
            {
                edge.IssueId, edge.DependsOnId, edge.Type,
                present = projection.Current.Issues.ContainsKey(edge.DependsOnId),
                status = projection.Current.Issues.GetValueOrDefault(edge.DependsOnId)?.Status,
                integration = "not-verified"
            }).ToArray();
            return new
            {
                issueId, issueRevision = record.Revision, policyRevision = policy.Revision,
                reviewRevision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = 1, issueId, cyclesClear = cycles.CyclesClear, policyRevision = policy.Revision,
                    issues = before.Issues.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new { id = pair.Key, revision = pair.Value.Revision })
                }))),
                candidate = DraftCandidateReview.Inspect(record, policy), cycles, dependencies,
                reachableDependencyCoverage = PublicationDependencyReview.Inspect(issueId, projection.Current.Issues, token),
                dependencyCoverage = dependencies is null ? "unknown" : dependencies.Any(edge => !edge.present) ? "missing-targets" : "recorded-direct-edges",
                ownership = "not-verified", publicationAvailable = false,
                explanation = "Read-only observations, not publication approval. Closed prerequisites do not prove integration. Review ownership, external writers, target/reasoning policy and prerequisite integration separately."
            };
        }
        finally { publicationReads.Release(); }
    }

    public bool DraftsEnabled => draftPolicy is not null;

    public async Task<object> DraftContextAsync(CancellationToken token)
    {
        if (draftPolicy is null || !healthy()) throw new InvalidDataException("Draft creation unavailable.");
        var policy = await draftPolicy(token);
        // New issues have no issue revision yet. Their displayed revision is the
        // target/reasoning policy being approved, not an unrelated issue's revision.
        return new
        {
            Session, ServerUnixMilliseconds, retryMinutes = RetryMinutes, revision = policy.Revision,
            targets = policy.Targets.Targets.Keys.Order(StringComparer.Ordinal).ToArray(),
            defaultTarget = policy.Targets.EnforceTargetBranch ? null : policy.Targets.DefaultTarget,
            requireTarget = policy.Targets.EnforceTargetBranch, requireReasoning = policy.Reasoning.EnforceLabels,
            reasoningLabels = ReasoningPolicy.Labels,
            issueTypes = new[] { "task", "bug", "feature", "epic", "chore", "decision" },
            mode = "blocked-draft", publicationAvailable = false
        };
    }

    public static CreateDraftAction ParseDraft(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Draft must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
            if (!seen.Add(field.Name) || field.Name is not ("requestId" or "expectedRevision" or "title" or "description" or "type" or "priority" or "labels" or "target"))
                throw new ArgumentException("Unknown or duplicate draft field.");
        string? Text(string key, int maximum, bool optional = false)
        {
            if (!body.TryGetProperty(key, out var value))
                return optional ? null : throw new ArgumentException("Missing draft field.");
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > maximum || value.GetString()!.Contains('\0'))
                throw new ArgumentException("Invalid draft field.");
            return value.GetString();
        }
        var requestId = Text("requestId", 160)!;
        var revision = Text("expectedRevision", 64)!;
        if (revision.Length != 64 || !revision.All(char.IsAsciiHexDigit)) throw new ArgumentException("Draft policy revision required.");
        if (!body.TryGetProperty("priority", out var priority) || priority.ValueKind != JsonValueKind.Number || !priority.TryGetInt32(out var number) || number is < 0 or > 4)
            throw new ArgumentException("Priority must be 0–4.");
        if (!body.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array || labels.GetArrayLength() > 32 ||
            labels.EnumerateArray().Any(label => label.ValueKind != JsonValueKind.String || label.GetString()!.Length > 100))
            throw new ArgumentException("Labels must be bounded strings.");
        return new(requestId, revision, new(Text("title", 500)!, Text("description", 32_000)!, Text("type", 20)!,
            number, labels.EnumerateArray().Select(label => label.GetString()!).ToArray(), Text("target", 256, true)));
    }

    public Task<ActionResult> SubmitDraftAsync(CreateDraftAction action, CancellationToken caller)
    {
        if (draftPolicy is null)
            return Task.FromResult(new ActionResult(503, "rejected", "Draft creation is unavailable; no write attempted.", action.RequestId));
        action = action with { Draft = action.Draft with { Labels = action.Draft.Labels.ToArray() } };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("create-draft\n" + JsonSerializer.Serialize(action))));
        return Enqueue(action.RequestId, fingerprint, () => ApplyDraftAsync(action), caller);
    }

    private async Task<ActionResult> ApplyDraftAsync(CreateDraftAction action)
    {
        await Task.Yield();
        var entered = false; var attempted = false;
        try
        {
            await writes.WaitAsync(mutationLifetime.Token); entered = true;
            if (!healthy()) return new(503, "rejected", "Issue source is stale; no write attempted.", action.RequestId);
            _ = await read(mutationLifetime.Token); // Fresh authoritative source availability, not only cached health.
            var policy = await draftPolicy!(mutationLifetime.Token);
            if (policy.Revision != action.ExpectedRevision)
                return new(409, "rejected", "Creation policy changed. Review targets and reasoning requirements before creating a draft.", action.RequestId);
            async Task<CommandResult> Execute(IReadOnlyList<string> args, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (!healthy()) throw new InvalidDataException("Issue source became stale.");
                if (args[0] is "create" or "update") attempted = true;
                return await execute(args, token);
            }
            var result = await Beads.CreateDraftAsync(action.Draft, policy.Targets, policy.Reasoning, Execute, mutationLifetime.Token);
            return new(result.Outcome == "completed" ? 201 : 503, result.Outcome, result.Message,
                action.RequestId, CreatedIssueId: result.IssueId);
        }
        catch (Exception error) when (error is ArgumentException or TargetException or ReasoningPolicyException)
        {
            return new(400, "rejected", "Draft fields or target/reasoning policy are invalid; no write attempted.", action.RequestId);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or
            CommandStartException or CommandTimeoutException or CommandOutputLimitException or OperationCanceledException)
        {
            return new(503, attempted ? "outcome-unknown" : "rejected", attempted
                ? "Creation outcome unknown. Review current issues before creating another draft."
                : "Issue source or creation policy unavailable; no write attempted.", action.RequestId);
        }
        finally { if (attempted) dirty(); if (entered) writes.Release(); }
    }
}
