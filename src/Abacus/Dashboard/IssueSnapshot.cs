using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus.Dashboard;

// Source records stay internal to the collector. HTTP projections must explicitly
// select public fields: exports can contain execution metadata and private context.
internal sealed record IssueRecord(string Id, string Revision, JsonElement Source);
internal sealed record IssueSnapshot(ImmutableDictionary<string, IssueRecord> Issues, long ExportBytes);

internal static class IssueExport
{
    public const int MaximumCharacters = 32 * 1024 * 1024;
    public const int MaximumIssues = 100_000;

    public static IssueSnapshot Parse(string jsonl) => Parse(jsonl, out _);

    public static IssueSnapshot Parse(string jsonl, out TimeSpan hashTime)
    {
        hashTime = TimeSpan.Zero;
        if (jsonl.Length > MaximumCharacters) throw new InvalidDataException("Issue export exceeds the dashboard limit.");
        var records = ImmutableDictionary.CreateBuilder<string, IssueRecord>(StringComparer.Ordinal);
        using var lines = new StringReader(jsonl);
        while (lines.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idValue.GetString()))
                throw new InvalidDataException("Issue export contains an invalid issue ID.");
            var id = idValue.GetString()!;
            if (id.Length > 256 || id.StartsWith('-') || id.Any(char.IsControl))
                throw new InvalidDataException("Issue export contains an unsafe issue ID.");
            // Exclude internal records even if an older CLI exports them by default.
            if (root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array &&
                labels.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "gt:slot")) continue;
            var hashStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var canonical = Canonicalize(root);
            var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            hashTime += System.Diagnostics.Stopwatch.GetElapsedTime(hashStart);
            if (!records.TryAdd(id, new IssueRecord(id, revision, root.Clone())))
                throw new InvalidDataException("Issue export contains duplicate issue IDs.");
            if (records.Count > MaximumIssues) throw new InvalidDataException("Issue export exceeds the issue limit.");
        }
        return new IssueSnapshot(records.ToImmutable(), Encoding.UTF8.GetByteCount(jsonl));
    }

    private static string Canonicalize(JsonElement value, string? field = null)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new InvalidDataException("Issue export contains duplicate JSON properties.");
                return "{" + string.Join(',', properties.Select(x => JsonSerializer.Serialize(x.Name) + ":" + Canonicalize(x.Value, x.Name))) + "}";
            case JsonValueKind.Array:
                IEnumerable<string> elements = value.EnumerateArray().Select(x => Canonicalize(x));
                // These collections have identity, not meaningful export order.
                if (field is "labels" or "dependencies" or "comments") elements = elements.Order(StringComparer.Ordinal);
                return "[" + string.Join(',', elements) + "]";
            case JsonValueKind.String: return JsonSerializer.Serialize(value.GetString());
            default: return value.GetRawText();
        }
    }
}

internal sealed record IssueDependency(string IssueId, string DependsOnId, string Type);
internal sealed record IssueComment(string Id, string? Author, string Text, DateTimeOffset? CreatedAt);
internal sealed record IssueSummary(string Id, string Revision, string Title, string Status,
    string? Description, string? Notes, string? Assignee, int? Priority,
    ImmutableArray<string> Labels, ImmutableArray<IssueComment> Comments)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public BeadsIssue? Routing { get; init; }
    // Null means incomplete/unavailable source coverage, never a known empty graph.
    public ImmutableArray<IssueDependency>? Dependencies { get; init; }
    public string? IssueType { get; init; }
    public string? Target { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
}

internal sealed record IssueView(long Revision, ImmutableDictionary<string, IssueSummary> Issues,
    ImmutableArray<string> ChangedIds, ImmutableArray<string> RemovedIds);

internal sealed class IssueProjection
{
    public IssueView Current { get; private set; } = new(0,
        ImmutableDictionary<string, IssueSummary>.Empty, [], []);
    public long RebuiltIssues { get; private set; }

    internal static string? ReadDeclaredTarget(JsonElement issue) =>
        issue.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
        metadata.TryGetProperty("abacus_target", out var target) && target.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(target.GetString()) ? target.GetString() : null;

    public bool Apply(IssueSnapshot source)
    {
        var changed = source.Issues.Values.Where(x => !Current.Issues.TryGetValue(x.Id, out var old) || old.Revision != x.Revision).ToArray();
        var removed = Current.Issues.Keys.Where(x => !source.Issues.ContainsKey(x)).Order(StringComparer.Ordinal).ToImmutableArray();
        if (Current.Revision != 0 && changed.Length == 0 && removed.Length == 0) return false;
        var issues = Current.Issues.ToBuilder();
        foreach (var id in removed) issues.Remove(id);
        foreach (var record in changed)
        {
            var root = record.Source;
            string? Get(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            issues[record.Id] = new IssueSummary(record.Id, record.Revision, Get("title") ?? record.Id,
                Get("status") ?? "unknown", Get("description"), Get("notes"), Get("assignee"),
                root.TryGetProperty("priority", out var p) && p.TryGetInt32Safe(out var priority) ? priority : null,
                root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? labels.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Order(StringComparer.Ordinal).ToImmutableArray() : [],
                ReadComments(root))
            {
                Routing = Beads.ReadRoutingMetadata(new BeadsIssue(record.Id, IssueStatus.Unknown), root),
                Dependencies = ReadDependencies(root, record.Id),
                IssueType = Get("issue_type"),
                Target = ReadDeclaredTarget(root),
                CreatedAt = ReadDate(Get("created_at")),
                ClosedAt = ReadDate(Get("closed_at")),
            };
        }
        RebuiltIssues += changed.Length;
        Current = new IssueView(Current.Revision + 1, issues.ToImmutable(),
            changed.Select(x => x.Id).Order(StringComparer.Ordinal).ToImmutableArray(), removed);
        return true;
    }
    private static ImmutableArray<IssueDependency>? ReadDependencies(JsonElement root, string owner)
    {
        int? count = root.TryGetProperty("dependency_count", out var c) && c.TryGetInt32Safe(out var n) && n >= 0 ? n : null;
        if (root.TryGetProperty("dependency_count", out _) && !count.HasValue) return null;
        if (!root.TryGetProperty("dependencies", out var values)) return count == 0 ? [] : null;
        if (values.ValueKind != JsonValueKind.Array) return null;
        var edges = new HashSet<IssueDependency>();
        foreach (var edge in values.EnumerateArray())
        {
            string? Field(string name) => edge.ValueKind == JsonValueKind.Object &&
                edge.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var source = Field("issue_id");
            var target = Field("depends_on_id");
            var type = Field("type");
            if (source != owner || string.IsNullOrWhiteSpace(target) || target.Length > 256 ||
                target.StartsWith('-') || target.Any(char.IsControl) || string.IsNullOrWhiteSpace(type) ||
                type.Length > 100 || type.Any(char.IsControl)) return null;
            if (!edges.Add(new(owner, target, type))) return null;
        }
        if (count.HasValue && count.Value != edges.Count) return null;
        return edges.OrderBy(x => x.DependsOnId, StringComparer.Ordinal).ThenBy(x => x.Type, StringComparer.Ordinal).ToImmutableArray();
    }

    private static DateTimeOffset? ReadDate(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date.ToUniversalTime() : null;

    private static ImmutableArray<IssueComment> ReadComments(JsonElement root)
    {
        if (!root.TryGetProperty("comments", out var values)) return [];
        if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid comment collection.");
        var comments = ImmutableArray.CreateBuilder<IssueComment>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var id) ||
                id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number) ||
                !value.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid comment fields.");
            var author = value.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            DateTimeOffset? createdAt = value.TryGetProperty("created_at", out var t) && t.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date.ToUniversalTime() : null;
            comments.Add(new(id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText(), author, text.GetString()!, createdAt));
        }
        return comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id, StringComparer.Ordinal).ToImmutableArray();
    }

}

internal static class JsonNumberExtensions
{
    public static bool TryGetInt32Safe(this JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
}
