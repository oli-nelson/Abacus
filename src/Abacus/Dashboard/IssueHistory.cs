using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record IssueVersion(string Id, string SourceRevision, DateTimeOffset RecordedAt,
    string Committer, string Title, string Status, string? Notes)
{
    public string? Assignee { get; init; }
    public int? Priority { get; init; }
    public ImmutableArray<string>? Labels { get; init; }
    public string? IssueType { get; init; }
    public string? Target { get; init; }
}
internal sealed record HistoryCoverage(bool LimitReached, bool Complete, string Explanation,
    DateTimeOffset? OldestReturned, DateTimeOffset? NewestReturned);
internal sealed record IssueHistory(string IssueId, string IssueRevision, string HistoryRevision,
    ImmutableArray<IssueVersion> Versions, HistoryCoverage Coverage);
internal sealed record HistoryKey(string IssueId, string IssueRevision, string HistoryRevision, int Limit);

internal sealed class IssueHistoryReader(CommandRunner runner, string repository, CancellationToken lifetime)
{
    public const int MaximumEntries = 1000;
    public const int MaximumCharacters = 4 * 1024 * 1024;
    private readonly DetailCache<HistoryKey, IssueHistory> cache = new(64, lifetime);
    private readonly SemaphoreSlim commands = new(2);

    public Task<IssueHistory> ReadAsync(HistoryKey key, CancellationToken cancellationToken = default)
    {
        Validate(key);
        return cache.GetAsync(key, async token =>
        {
            await commands.WaitAsync(token);
            try
            {
                var result = await runner.RunAsync(new CommandSpec("bd",
                    ["--readonly", "history", key.IssueId, "--limit", key.Limit.ToString(CultureInfo.InvariantCulture), "--json"],
                    repository, MaxOutputCharacters: MaximumCharacters), token);
                if (!result.Succeeded) throw new InvalidDataException("Issue history is unavailable.");
                return Parse(key, result.StandardOutput);
            }
            finally { commands.Release(); }
        }, cancellationToken);
    }

    private static void Validate(HistoryKey key)
    {
        if (string.IsNullOrWhiteSpace(key.IssueId) || key.IssueId.Length > 256 ||
            key.IssueId.StartsWith('-') || key.IssueId.Any(char.IsControl))
            throw new ArgumentException("Invalid issue ID.");
        if (key.Limit is < 1 or > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(key.Limit));
        if (string.IsNullOrWhiteSpace(key.IssueRevision) || string.IsNullOrWhiteSpace(key.HistoryRevision))
            throw new ArgumentException("History requires issue and Dolt history revisions.");
    }

    public static IssueHistory Parse(HistoryKey key, string json)
    {
        Validate(key);
        if (json.Length > MaximumCharacters) throw new InvalidDataException("Issue history exceeds its output limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > key.Limit)
            throw new InvalidDataException("Invalid bounded issue history response.");
        var versions = ImmutableArray.CreateBuilder<IssueVersion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.EnumerateArray())
        {
            var commit = RequiredString(entry, "CommitHash");
            if (commit.Length != 32 || !commit.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'v') || !seen.Add(commit))
                throw new InvalidDataException("Invalid or duplicate history commit.");
            if (!DateTimeOffset.TryParse(RequiredString(entry, "CommitDate"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date)) throw new InvalidDataException("Invalid history timestamp.");
            if (!entry.TryGetProperty("Issue", out var issue) || issue.ValueKind != JsonValueKind.Object || RequiredString(issue, "id") != key.IssueId)
                throw new InvalidDataException("History belongs to another issue.");
            versions.Add(new IssueVersion($"beads:{key.IssueId}:{commit}", commit, date.ToUniversalTime(),
                RequiredString(entry, "Committer"), RequiredString(issue, "title"), RequiredString(issue, "status"),
                issue.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String ? notes.GetString() : null)
            {
                Assignee = OptionalString(issue, "assignee"),
                Priority = ReadPriority(issue),
                Labels = ReadLabels(issue),
                IssueType = OptionalString(issue, "issue_type"),
                Target = IssueProjection.ReadDeclaredTarget(issue),
            });
        }
        // Preserve CLI version order, including equal/skewed timestamps. A Dolt
        // commit is evidence of a snapshot, not the exact time/actor of each edit.
        var result = versions.ToImmutable();
        return new IssueHistory(key.IssueId, key.IssueRevision, key.HistoryRevision, result,
            new HistoryCoverage(result.Length == key.Limit, false,
                "Committed issue snapshots only; working-set edits and compacted history may be absent. Committer is not necessarily the edit author.",
                result.Length == 0 ? null : result.Min(x => x.RecordedAt),
                result.Length == 0 ? null : result.Max(x => x.RecordedAt)));
    }

    private static string? OptionalString(JsonElement issue, string field)
    {
        if (!issue.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid historical metadata string.");
        return value.GetString();
    }
    private static int? ReadPriority(JsonElement issue)
    {
        if (!issue.TryGetProperty("priority", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt32Safe(out var priority) || priority is < 0 or > 4) throw new InvalidDataException("Invalid historical priority.");
        return priority;
    }
    private static ImmutableArray<string>? ReadLabels(JsonElement issue)
    {
        if (!issue.TryGetProperty("labels", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Invalid historical labels.");
        return value.EnumerateArray().Select(v => v.GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static string RequiredString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()! : throw new InvalidDataException("Invalid issue history field.");
}
