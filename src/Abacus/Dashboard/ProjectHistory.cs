using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record ProjectHistory(string HistoryRevision,
    ImmutableDictionary<string, ImmutableArray<IssueVersion>> Issues, HistoryCoverage Coverage);

/// <summary>One issue's recorded history, fenced by the issue revision it was read against.</summary>
internal sealed record ProjectActivityIssue(string IssueId, string IssueRevision, ImmutableArray<IssueVersion> Versions);
internal sealed record ProjectActivityPage(string HistoryRevision, HistoryCoverage Coverage,
    ImmutableArray<ProjectActivityIssue> Issues);

/// <summary>
/// Whole-project recorded history in one command.
/// <para>
/// Dolt stores a row per issue per commit, so <c>bd history &lt;id&gt;</c> repeats an
/// untouched issue's full body once for every project commit: on a real repository that
/// is megabytes of identical rows per issue, one process per issue, and three processes
/// once the revision fence is counted. Reading <c>dolt_history_issues</c> directly asks
/// the same table the same question once for every issue at once, and discards repeated
/// states in SQL before they are transferred.
/// </para>
/// <para>
/// This is the same recorded source as the per-issue reader and carries the same limits:
/// committed snapshots only, no working-set edits, no invented transitions. It is not a
/// substitute for that reader, which stays as the fallback whenever this query cannot run
/// (older <c>bd</c>, SQLite storage, schema drift).
/// </para>
/// </summary>
internal sealed class ProjectHistoryReader(CommandRunner runner, string repository, CancellationToken lifetime)
{
    public const int MaximumVersions = 20000;
    public const int MaximumCharacters = 32 * 1024 * 1024;

    // Consecutive rows that project to the same recorded state are one snapshot. The
    // window keeps the OLDEST commit of each run: the earliest time the state is known
    // to have existed. A later commit that merely repeats it is not a second edit.
    private const string StateExpression =
        "CONCAT_WS(0x1f, title, status, COALESCE(priority, ''), COALESCE(issue_type, ''), " +
        "COALESCE(assignee, ''), COALESCE(notes, ''), COALESCE(JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.abacus_target')), ''))";

    internal static readonly string Query =
        "SELECT id, commit_hash, committer, commit_date, title, status, priority, issue_type, assignee, notes, declared_target FROM (" +
        "SELECT id, commit_hash, committer, commit_date, title, status, priority, issue_type, assignee, notes, " +
        "JSON_UNQUOTE(JSON_EXTRACT(metadata, '$.abacus_target')) AS declared_target, " +
        StateExpression + " AS state, " +
        "LAG(" + StateExpression + ") OVER (PARTITION BY id ORDER BY commit_date ASC, commit_hash ASC) AS previous " +
        "FROM dolt_history_issues) t WHERE previous IS NULL OR state <> previous " +
        "ORDER BY id ASC, commit_date DESC, commit_hash DESC LIMIT " + MaximumVersions;

    private readonly DetailCache<string, ProjectHistory> cache = new(4, lifetime);

    public Task<ProjectHistory> ReadAsync(string historyRevision, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(historyRevision)) throw new ArgumentException("Project history requires a Dolt revision.");
        return cache.GetAsync(historyRevision, async token =>
        {
            var result = await runner.RunAsync(new CommandSpec("bd", ["--readonly", "sql", "--json", Query],
                repository, MaxOutputCharacters: MaximumCharacters), token);
            if (!result.Succeeded) throw new InvalidDataException("Project history is unavailable.");
            return Parse(historyRevision, result.StandardOutput);
        }, cancellationToken);
    }

    public static ProjectHistory Parse(string historyRevision, string json)
    {
        if (string.IsNullOrWhiteSpace(historyRevision)) throw new ArgumentException("Project history requires a Dolt revision.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        // A failed query answers with an error object, never with an empty history.
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid bounded project history response.");
        if (root.GetArrayLength() > MaximumVersions) throw new InvalidDataException("Project history exceeds its row limit.");
        var grouped = new Dictionary<string, ImmutableArray<IssueVersion>.Builder>(StringComparer.Ordinal);
        var seen = new HashSet<(string Issue, string Commit)>();
        var rows = 0;
        DateTimeOffset? oldest = null, newest = null;
        foreach (var entry in root.EnumerateArray())
        {
            rows++;
            var id = RequiredString(entry, "id");
            var commit = RequiredString(entry, "commit_hash");
            if (commit.Length != 32 || !commit.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'v'))
                throw new InvalidDataException("Invalid project history commit.");
            if (!seen.Add((id, commit))) throw new InvalidDataException("Duplicate project history commit.");
            if (!DateTimeOffset.TryParse(RequiredString(entry, "commit_date"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date)) throw new InvalidDataException("Invalid project history timestamp.");
            date = date.ToUniversalTime();
            if (oldest is null || date < oldest) oldest = date;
            if (newest is null || date > newest) newest = date;
            if (!grouped.TryGetValue(id, out var versions)) grouped[id] = versions = ImmutableArray.CreateBuilder<IssueVersion>();
            versions.Add(new IssueVersion($"beads:{id}:{commit}", commit, date,
                RequiredString(entry, "committer"), RequiredString(entry, "title"), RequiredString(entry, "status"),
                OptionalString(entry, "notes"))
            {
                Assignee = OptionalString(entry, "assignee"),
                Priority = ReadPriority(entry),
                // Labels live outside the issues table, so recorded label history is not
                // part of this source. Null says unknown; it never claims "no labels".
                Labels = null,
                IssueType = OptionalString(entry, "issue_type"),
                Target = OptionalString(entry, "declared_target"),
            });
        }
        var issues = grouped.ToImmutableDictionary(x => x.Key,
            x => IssueVersionSequence.Collapse(x.Value.ToImmutable()), StringComparer.Ordinal);
        return new ProjectHistory(historyRevision, issues,
            new HistoryCoverage(rows == MaximumVersions, false,
                "Committed issue snapshots only; working-set edits and compacted history may be absent. " +
                "Repeated identical states are recorded once, at the earliest commit that shows them. " +
                "Recorded label history is not available from this source. Committer is not necessarily the edit author.",
                oldest, newest));
    }

    private static string? OptionalString(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid historical metadata string.");
        return value.GetString();
    }
    private static int? ReadPriority(JsonElement row)
    {
        if (!row.TryGetProperty("priority", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (!value.TryGetInt32Safe(out var priority) || priority is < 0 or > 4) throw new InvalidDataException("Invalid historical priority.");
        return priority;
    }
    private static string RequiredString(JsonElement row, string property) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
            ? field.GetString()! : throw new InvalidDataException("Invalid project history field.");
}
