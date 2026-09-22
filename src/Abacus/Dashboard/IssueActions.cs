using System.Collections.Immutable;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record IssueAction(string RequestId, string ExpectedRevision, string Action,
    string? Text, string? Title, string? Description, int? Priority, string? AppendNotes, string? ExistingCommentId = null,
    IReadOnlyList<string>? AddLabels = null, IReadOnlyList<string>? RemoveLabels = null);
internal sealed record ActionResult(int StatusCode, string Outcome, string Message, string RequestId,
    string? Revision = null, IssueSummary? Issue = null, string? RecordedCommentId = null, string? CreatedIssueId = null);

/// <summary>Bounded same-process idempotency and serialized, verified CLI writes.
/// This is not a transaction or cross-process compare-and-swap.</summary>
internal sealed partial class IssueActions(
    Func<CancellationToken, Task<IssueSnapshot>> read,
    Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> execute,
    Func<bool> healthy, Action dirty, CancellationToken lifetime, TimeProvider? time = null,
    bool attentionEnabled = false, Func<CancellationToken, Task<DraftPolicy>>? draftPolicy = null)
{
    private sealed record Entry(string Fingerprint, DateTimeOffset IssuedAt, Task<ActionResult> Task);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> ledger = [];
    private readonly SemaphoreSlim writes = new(1);
    private readonly CancellationTokenSource mutationLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private bool stopping;
    private Task? draining;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    public bool AttentionEnabled => attentionEnabled;
    public string Session { get; } = Guid.NewGuid().ToString("N");
    public long ServerUnixMilliseconds => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public const int RetryMinutes = 15;

    public static IssueActions ForRepository(CommandRunner runner, string repository, string actor,
        Func<bool> healthy, Action dirty, CancellationToken lifetime) => new(async token =>
        {
            var result = await runner.RunAsync(new("bd", ["--readonly", "export"], repository, MaxOutputCharacters: IssueExport.MaximumCharacters), token);
            if (!result.Succeeded) throw new InvalidDataException("Issue source unreadable.");
            return IssueExport.Parse(result.StandardOutput);
        }, (args, token) => runner.RunAsync(new("bd", args, repository,
            new Dictionary<string, string?> { ["BEADS_ACTOR"] = actor }, MaxOutputCharacters: 1024 * 1024), token), healthy, dirty, lifetime, attentionEnabled: true,
            draftPolicy: token => DraftPolicy.LoadAsync(repository, token));

    public static IssueAction Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Action must be a JSON object.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in body.EnumerateObject())
            if (!keys.Add(field.Name) || field.Name is not ("requestId" or "expectedRevision" or "action" or "text" or "title" or "description" or "priority" or "appendNotes" or "existingCommentId" or "addLabels" or "removeLabels"))
                throw new ArgumentException("Unknown or duplicate action field.");
        string? String(string name, int maximum, bool required = false)
        {
            if (!body.TryGetProperty(name, out var field))
            { if (required) throw new ArgumentException($"{name} is required."); return null; }
            if (field.ValueKind != JsonValueKind.String || field.GetString()!.Length > maximum || field.GetString()!.Contains('\0'))
                throw new ArgumentException($"Invalid {name}.");
            return field.GetString();
        }
        var id = String("requestId", 160, true)!;
        var revision = String("expectedRevision", 64, true)!;
        if (revision.Length != 64 || !revision.All(char.IsAsciiHexDigit)) throw new ArgumentException("Expected issue revision is required.");
        var action = String("action", 30, true)!;
        var text = String("text", 16_000);
        var title = String("title", 500);
        var description = String("description", 32_000);
        var notes = String("appendNotes", 16_000);
        var existingCommentId = String("existingCommentId", 256);
        string[]? Labels(string name)
        {
            if (!body.TryGetProperty(name, out var field)) return null;
            if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > 32 || field.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                throw new ArgumentException("Labels must be bounded string arrays.");
            return field.EnumerateArray().Select(v => v.GetString()!).ToArray();
        }
        var addLabels = Labels("addLabels"); var removeLabels = Labels("removeLabels");
        var labelArguments = Beads.LabelDeltaArguments(addLabels, removeLabels);
        if (action != "edit" && (addLabels is not null || removeLabels is not null)) throw new ArgumentException("Label deltas require an edit action.");
        int? priority = null;
        if (body.TryGetProperty("priority", out var p))
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var number) || number is < 0 or > 4) throw new ArgumentException("Priority must be 0–4.");
            priority = number;
        }
        if (action is "attention-request" or "attention-resolve")
        {
            if (title is not null || description is not null || priority is not null || notes is not null ||
                (text is not null && string.IsNullOrWhiteSpace(text)) ||
                (action == "attention-resolve" && existingCommentId is not null) ||
                (action == "attention-request" && ((text is null) == (existingCommentId is null))) ||
                (existingCommentId is not null && string.IsNullOrWhiteSpace(existingCommentId)))
                throw new ArgumentException("Attention requires an explanation or a previously verified comment, without content edits.");
        }
        else if (action == "comment")
        {
            if (existingCommentId is not null || string.IsNullOrWhiteSpace(text) || title is not null || description is not null || priority is not null || notes is not null)
                throw new ArgumentException("Comment requires only nonempty text.");
        }
        else if (action == "edit")
        {
            if (existingCommentId is not null || text is not null || (title is null && description is null && priority is null && notes is null && labelArguments.Count == 0) ||
                (title is not null && string.IsNullOrWhiteSpace(title)) || (notes is not null && string.IsNullOrWhiteSpace(notes)))
                throw new ArgumentException("Edit requires content fields, without comment text.");
        }
        else throw new ArgumentException("Unsupported action.");
        return new(id, revision, action, text, title, description, priority, notes, existingCommentId, addLabels, removeLabels);
    }

    public Task<ActionResult> SubmitAsync(string issueId, IssueAction action, CancellationToken caller)
    {
        if (!Git.IsValidIssueId(issueId)) throw new ArgumentException("Invalid issue ID.");
        if (!attentionEnabled && action.Action is "attention-request" or "attention-resolve")
            return Task.FromResult(new ActionResult(503, "rejected", "Attention writes are not enabled; no write attempted.", action.RequestId));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issueId + "\n" + JsonSerializer.Serialize(action))));
        return Enqueue(action.RequestId, fingerprint, () => ApplyAsync(issueId, action), caller);
    }

    private Task<ActionResult> Enqueue(string requestId, string fingerprint, Func<Task<ActionResult>> apply, CancellationToken caller)
    {
        var parts = requestId.Split(':');
        var now = clock.GetUtcNow();
        if (parts.Length != 3 || parts[0] != Session || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) ||
            !Guid.TryParse(parts[2], out _) || milliseconds < now.AddMinutes(-RetryMinutes).ToUnixTimeMilliseconds() || milliseconds > now.AddSeconds(30).ToUnixTimeMilliseconds())
            return Task.FromResult(new ActionResult(409, "rejected", "Request expired or belongs to another server. Review current state before starting a new operation.", requestId));
        lock (sync)
        {
            foreach (var key in ledger.Where(p => p.Value.Task.IsCompleted && p.Value.IssuedAt < now.AddMinutes(-RetryMinutes)).Select(p => p.Key).ToArray()) ledger.Remove(key);
            if (ledger.TryGetValue(requestId, out var previous))
                return previous.Fingerprint == fingerprint ? previous.Task.WaitAsync(caller)
                    : Task.FromResult(new ActionResult(400, "rejected", "Request ID was already used with different content.", requestId));
            if (stopping) return Task.FromResult(new ActionResult(503, "rejected", "Server is draining accepted writes; no new write attempted.", requestId));
            if (ledger.Count >= 1024) return Task.FromResult(new ActionResult(503, "rejected", "Mutation retry cache is full; wait before starting new operations.", requestId));
            var operation = apply();
            ledger.Add(requestId, new(fingerprint, DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), operation));
            return operation.WaitAsync(caller);
        }
    }

    internal Task DrainAsync(TimeSpan? grace = null)
    {
        lock (sync)
        {
            if (draining is not null) return draining;
            stopping = true;
            // Acceptance and snapshotting share the ledger lock: no accepted task
            // can be added after the drain captures its work. Retries still work.
            return draining = DrainCoreAsync(Task.WhenAll(ledger.Values.Select(e => e.Task)), grace ?? TimeSpan.FromSeconds(10));
        }
    }

    private async Task DrainCoreAsync(Task pending, TimeSpan grace)
    {
        await Task.Yield(); // Never invoke cancellation callbacks under ledger lock.
        try
        {
            try { await pending.WaitAsync(grace); }
            catch (TimeoutException)
            {
                try { mutationLifetime.Cancel(); }
                catch (AggregateException) { /* A callback failure must not skip joining accepted tasks. */ }
                // CLI cancellation owns bounded process-tree termination and joins
                // its readers. Observe every accepted operation before host exit.
                await pending;
            }
        }
        finally { mutationLifetime.Dispose(); }
    }

    private async Task<ActionResult> ApplyAsync(string issueId, IssueAction action)
    {
        await Task.Yield();
        var entered = false; var attempted = false;
        try
        {
            await writes.WaitAsync(mutationLifetime.Token); entered = true;
            if (!healthy()) return new(503, "rejected", "Issue source is stale; no write attempted.", action.RequestId);
            var before = (await read(mutationLifetime.Token)).Issues.GetValueOrDefault(issueId);
            if (before is null) return new(404, "rejected", "Issue no longer exists.", action.RequestId);
            if (before.Revision != action.ExpectedRevision) return new(409, "rejected", "Issue changed. Review the current issue before submitting again.", action.RequestId, before.Revision, Summary(before));
            if (action.Action is "attention-request" or "attention-resolve")
                return await ApplyAttentionAsync(before, action, () => attempted = true);
            List<string> args;
            if (action.Action == "comment") args = ["comment", "--json", issueId, "--", action.Text!];
            else
            {
                args = ["update", issueId, "--json"];
                if (action.Title is not null) args.Add("--title=" + action.Title);
                if (action.Description is not null) args.Add("--description=" + action.Description);
                if (action.Priority is not null) args.Add("--priority=" + action.Priority.Value.ToString(CultureInfo.InvariantCulture));
                if (action.AppendNotes is not null) args.Add("--append-notes=" + action.AppendNotes);
                args.AddRange(Beads.LabelDeltaArguments(action.AddLabels, action.RemoveLabels));
            }
            mutationLifetime.Token.ThrowIfCancellationRequested();
            attempted = true;
            var result = await execute(args, mutationLifetime.Token);
            var after = (await read(mutationLifetime.Token)).Issues.GetValueOrDefault(issueId);
            if (after is null) return new(503, "outcome-unknown", "Issue disappeared after the command. Review before retrying.", action.RequestId);
            var verified = Verify(before, after, action);
            var summary = Summary(after);
            if (result.Succeeded && verified) return new(200, "completed", "Stored result verified. No automatic remote synchronization was requested.", action.RequestId, after.Revision, summary);
            return new(503, after.Revision == before.Revision ? "outcome-unknown" : "partially-applied",
                "Command result could not be fully verified. Review current content; do not blindly repeat an append.", action.RequestId, after.Revision, summary);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or CommandStartException or CommandTimeoutException or CommandOutputLimitException or OperationCanceledException or IOException)
        {
            return new(503, attempted ? "outcome-unknown" : "rejected", attempted
                ? "Write outcome unknown. Review current content before attempting any new write."
                : "Source unavailable or server stopping; no write attempted.", action.RequestId);
        }
        finally { if (attempted) dirty(); if (entered) writes.Release(); }
    }

    private async Task<ActionResult> ApplyAttentionAsync(IssueRecord before, IssueAction action, Action attempted)
    {
        var request = action.Action == "attention-request";
        var labelPresent = HasAttention(before);
        if (request == labelPresent)
            return new(409, "rejected", request ? "Attention is already requested. Add a comment instead."
                : "Attention is already clear. No write attempted.", action.RequestId, before.Revision, Summary(before));
        string? commentId = action.ExistingCommentId;
        if (commentId is not null && !Summary(before).Comments.Any(c => c.Id == commentId && !string.IsNullOrWhiteSpace(c.Text)))
            return new(409, "rejected", "The reviewed explanation comment no longer exists; no write attempted.",
                action.RequestId, before.Revision, Summary(before));
        var commentVerified = action.Text is null;
        var labelAttempted = false;
        var sequenceSucceeded = false;
        async Task<CommandResult> ExecuteStep(IReadOnlyList<string> args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (args[0] == "update")
            {
                if (!commentVerified || !healthy()) throw new InvalidDataException("Attention step cannot continue.");
                labelAttempted = true;
            }
            attempted();
            var result = await execute(args, token);
            if (args[0] == "comment")
            {
                var current = (await read(token)).Issues.GetValueOrDefault(before.Id);
                if (current is not null)
                {
                    commentId = FindAddedComment(before, current, action.Text!);
                    commentVerified = commentId is not null;
                }
                if (!result.Succeeded || !commentVerified)
                    throw new InvalidDataException("Comment was not successfully verified; label step not attempted.");
            }
            return result;
        }
        try
        {
            if (request)
            {
                if (action.Text is not null)
                    await ExecuteStep(["comment", "--json", before.Id, "--", action.Text], mutationLifetime.Token);
                var result = await ExecuteStep(["update", before.Id, "--add-label", Beads.NeedsUserAttentionLabel, "--json"], mutationLifetime.Token);
                sequenceSucceeded = result.Succeeded;
            }
            else
            {
                await Beads.ResolveUserAttentionAsync(before.Id, action.Text, reopen: false, ExecuteStep, mutationLifetime.Token);
                sequenceSucceeded = true;
            }
        }
        catch (Exception ex) when (ex is BeadsException or InvalidDataException or JsonException or CommandStartException or
            CommandTimeoutException or CommandOutputLimitException or OperationCanceledException or IOException)
        {
            // Re-read below when possible, even after a failed/timed-out command.
            // Never replay a response or roll back a racing status/assignee.
        }
        IssueRecord? after;
        try { after = (await read(mutationLifetime.Token)).Issues.GetValueOrDefault(before.Id); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or CommandStartException or CommandTimeoutException or
            CommandOutputLimitException or OperationCanceledException or IOException)
        { after = null; }
        if (after is null)
            return new(503, "outcome-unknown", "Attention outcome unknown. Inspect current comments and label before a new operation.",
                action.RequestId, RecordedCommentId: commentId);
        if (action.Text is not null) commentId = FindAddedComment(before, after, action.Text);
        var responseStored = action.Text is null || commentId is not null;
        var labelVerified = HasAttention(after) == request;
        if (sequenceSucceeded && responseStored && labelVerified)
            return new(200, "completed", request ? "Explanation and attention label verified; no status or assignment change was requested. No automatic remote synchronization."
                : "Attention resolution verified; no status or assignment change was requested. No automatic remote synchronization.",
                action.RequestId, after.Revision, Summary(after), commentId);
        var changed = after.Revision != before.Revision;
        var message = commentId is not null
            ? $"Explanation comment {commentId} is stored. Attention label {(labelVerified ? "is in the requested state, but command completion is uncertain" : labelAttempted ? "was not verified" : "was not attempted")}. Review and retry only the missing step; do not append the explanation again."
            : "Attention result could not be verified. Inspect current comments and label before any new operation.";
        return new(503, changed ? "partially-applied" : "outcome-unknown", message, action.RequestId, after.Revision, Summary(after), commentId);
    }

    private static bool HasAttention(IssueRecord record) => Summary(record).Labels.Contains(Beads.NeedsUserAttentionLabel);

    private static string? FindAddedComment(IssueRecord before, IssueRecord after, string text)
    {
        var old = Summary(before).Comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return Summary(after).Comments.FirstOrDefault(c => !old.Contains(c.Id) && c.Text == text)?.Id;
    }

    private static IssueSummary Summary(IssueRecord record)
    {
        var projection = new IssueProjection();
        projection.Apply(new(new Dictionary<string, IssueRecord> { [record.Id] = record }.ToImmutableDictionary(), 0));
        return projection.Current.Issues[record.Id];
    }

    private static bool Verify(IssueRecord before, IssueRecord after, IssueAction action)
    {
        static string? Get(JsonElement element, string field) => element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (action.Action == "comment")
        {
            if (!after.Source.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) return false;
            var old = before.Source.TryGetProperty("comments", out var original) && original.ValueKind == JsonValueKind.Array
                ? original.EnumerateArray().Where(c => c.TryGetProperty("id", out _)).Select(c => c.GetProperty("id").GetRawText()).ToHashSet() : [];
            return comments.EnumerateArray().Any(c => c.TryGetProperty("id", out var id) && !old.Contains(id.GetRawText()) && Get(c, "text") == action.Text);
        }
        var labels = Summary(after).Labels.ToHashSet(StringComparer.Ordinal);
        var removed = (action.RemoveLabels ?? []).ToHashSet(StringComparer.Ordinal);
        var labelDelta = action.AddLabels is not null || action.RemoveLabels is not null;
        return (!labelDelta || (action.AddLabels ?? []).All(labels.Contains) && removed.All(label => !labels.Contains(label)) &&
            Summary(before).Labels.Where(label => !removed.Contains(label)).All(labels.Contains)) &&
            (action.Title is null || Get(after.Source, "title") == action.Title) &&
            (action.Description is null || (Get(after.Source, "description") ?? "") == action.Description) &&
            (action.Priority is null || (after.Source.TryGetProperty("priority", out var priority) && priority.TryGetInt32Safe(out var number) && number == action.Priority)) &&
            (action.AppendNotes is null || Get(after.Source, "notes") == (string.IsNullOrEmpty(Get(before.Source, "notes")) ? action.AppendNotes : Get(before.Source, "notes") + "\n" + action.AppendNotes));
    }
}
