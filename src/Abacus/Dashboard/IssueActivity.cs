using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record HistorySourceState(string? Revision, bool Stale, string? Error);
internal sealed record ActivityPage(string IssueId, string IssueRevision, string HistoryRevision,
    ImmutableArray<IssueVersion> Versions, HistoryCoverage Coverage, string? Continuation);
internal sealed class ActivityConflictException : Exception;

/// <summary>Host-owned revision probe; lazy bounded history shared by all clients.</summary>
internal sealed class IssueActivity(
    Func<CancellationToken, Task<string>> revision,
    Func<HistoryKey, CancellationToken, Task<IssueHistory>> history,
    CancellationToken lifetime)
{
    private readonly DetailCache<HistoryKey, IssueHistory> cache = new(64, lifetime);
    private readonly SemaphoreSlim refresh = new(1);
    private HistorySourceState state = new(null, true, "History not collected.");
    public HistorySourceState State => Volatile.Read(ref state);

    public static IssueActivity ForRepository(CommandRunner runner, string repository, CancellationToken lifetime)
    {
        async Task<string> Revision(CancellationToken token)
        {
            var result = await runner.RunAsync(new CommandSpec("bd", ["--readonly", "vc", "status", "--json"],
                repository, MaxOutputCharacters: 64 * 1024), token);
            if (!result.Succeeded) throw new InvalidDataException("History revision unavailable.");
            return ParseRevision(result.StandardOutput);
        }
        var reader = new IssueHistoryReader(runner, repository, lifetime);
        return new(Revision, async (key, token) =>
        {
            if (await Revision(token) != key.HistoryRevision) throw new ActivityConflictException();
            var result = await reader.ReadAsync(key, token);
            if (await Revision(token) != key.HistoryRevision) throw new ActivityConflictException();
            return result;
        }, lifetime);
    }

    internal static string ParseRevision(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String ||
            commit.GetString() is not { Length: 32 } value || !value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'v') ||
            !root.TryGetProperty("branch", out var branch) || branch.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(branch.GetString()))
            throw new InvalidDataException("Invalid history revision.");
        // A branch switch can change history even if the observed issue fields do not.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(branch.GetString() + ":" + value)));
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        await refresh.WaitAsync(token);
        try
        {
            HistorySourceState next;
            try { next = new(await revision(token), false, null); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or CommandStartException or CommandTimeoutException or CommandOutputLimitException)
            { next = State with { Stale = true, Error = "Committed history unavailable; current issue data is separate." }; }
            if (next != State) Volatile.Write(ref state, next);
        }
        finally { refresh.Release(); }
    }

    public async Task<ActivityPage> ReadAsync(IssueSummary issue, int limit, string? continuation, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var source = State;
        if (source.Stale || source.Revision is null) throw new InvalidDataException("History unavailable.");
        var key = new HistoryKey(issue.Id, issue.Revision, source.Revision, IssueHistoryReader.MaximumEntries);
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.IssueId}:{key.IssueRevision}:{key.HistoryRevision}")));
        var offset = 0;
        if (continuation is not null)
        {
            var parts = continuation.Split(':');
            if (continuation.Length > 80 || parts.Length != 2 ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset is < 1 or >= IssueHistoryReader.MaximumEntries)
                throw new ArgumentException("Invalid continuation.");
            if (parts[0] != identity) throw new ActivityConflictException();
        }
        var loaded = await cache.GetAsync(key, ct => history(key, ct), token);
        if (State.Stale || State.Revision != source.Revision) throw new ActivityConflictException();
        if (offset > loaded.Versions.Length) throw new ArgumentException("Invalid continuation.");
        var page = loaded.Versions.Skip(offset).Take(limit).ToImmutableArray();
        var next = offset + page.Length;
        return new(issue.Id, issue.Revision, source.Revision, page, loaded.Coverage,
            next < loaded.Versions.Length ? $"{identity}:{next}" : null);
    }
}
