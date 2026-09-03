using System.Text.Json;

namespace Abacus;

public sealed record DoltIdentity(
    bool Embedded,
    string Database,
    string? Host,
    int? Port,
    bool ConnectionOk)
{
    public bool IsShared => !Embedded && ConnectionOk && Host is not null && Port is > 0;

    public string SharedKey => IsShared
        ? $"{NormalizeHost(Host!)}:{Port}/{Database}"
        : throw new InvalidOperationException("Local Dolt storage has no shared identity.");

    private static string NormalizeHost(string host)
    {
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            return address.ToString();
        }

        return host.Trim().TrimEnd('.').ToLowerInvariant();
    }
}

public enum IssueStatus
{
    InProgress,
    Open,
    Blocked,
    Closed,
    Unknown,
}

public sealed record BeadsIssue(string Id, IssueStatus Status, string? Title = null);

public sealed record BeadsComment(
    string Id,
    string IssueId,
    string? IssueTitle,
    string Author,
    string Text,
    DateTimeOffset CreatedAt,
    bool NeedsUserAttention);

public sealed class Beads(CommandRunner runner, string executable = "bd")
{
    private sealed record ReadyCandidate(BeadsIssue Issue, int? Priority, bool HasComments);

    private const string MergeSlotLabel = "gt:slot";
    public const string NeedsUserAttentionLabel = "abacus:needs-user-attention";
    public const string DisableNoGitOpsCommand = "bd config set no-git-ops false";

    public async Task<bool> IsNoGitOpsEnabledAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            workspace,
            agentName: null,
            ["config", "get", "no-git-ops", "--json"],
            cancellationToken);
        EnsureSuccess(result, "read Beads no-git-ops configuration");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var value = document.RootElement.GetProperty("value");
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind is not JsonValueKind.String)
            {
                throw new JsonException("value must be a string or boolean");
            }

            var text = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (bool.TryParse(text, out var enabled))
            {
                return enabled;
            }

            throw new JsonException($"value '{text}' is not a boolean");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new PreflightException($"Beads returned invalid no-git-ops configuration JSON: {exception.Message}");
        }
    }

    public async Task<CommandResult> ResolveUserAttentionAsync(
        string workspace,
        string issueId,
        string? message,
        bool reopen,
        CancellationToken cancellationToken)
    {
        if (message is not null)
        {
            var comment = await RunAsync(
                workspace,
                agentName: null,
                ["comment", issueId, message, "--json"],
                cancellationToken);
            EnsureCommandSuccess(comment, $"record user response for '{issueId}'");
        }

        var updateArguments = new List<string>
        {
            "update",
            issueId,
            "--remove-label",
            NeedsUserAttentionLabel,
        };
        if (reopen)
        {
            updateArguments.AddRange(["--status", "open", "--assignee", ""]);
        }

        updateArguments.Add("--json");
        var result = await RunAsync(
            workspace,
            agentName: null,
            updateArguments,
            cancellationToken);
        EnsureCommandSuccess(result, $"resolve user attention for '{issueId}'");
        return result;
    }

    public async Task<BeadsIssue?> TryClaimReadyAsync(
        string workspace,
        string agentName,
        DispatchFilters filters,
        CancellationToken cancellationToken)
    {
        var claim = await TryClaimPreferredReadyAsync(
            workspace,
            agentName,
            filters,
            assignee: null,
            cancellationToken);
        if (claim is not null)
        {
            return claim;
        }

        // Reopened work created by older Abacus versions can remain assigned to
        // this agent. It is excluded by the fresh unassigned lookup, so resume
        // only work already owned by this identity and leave other agents' work alone.
        return await TryClaimPreferredReadyAsync(
            workspace,
            agentName,
            filters,
            agentName,
            cancellationToken);
    }

    private async Task<BeadsIssue?> TryClaimPreferredReadyAsync(
        string workspace,
        string agentName,
        DispatchFilters filters,
        string? assignee,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var readyResult = await RunWithActorAsync(
                workspace,
                agentName,
                ReadyArguments(filters, assignee, unassigned: assignee is null),
                cancellationToken);
            EnsureCommandSuccess(
                readyResult,
                assignee is null
                    ? "find ready work"
                    : "find ready work assigned to this agent");

            var candidates = ParseReadyCandidates(readyResult.StandardOutput, "ready result");
            if (candidates.Count is 0)
            {
                return null;
            }

            var remainingCandidates = candidates.ToList();
            ReadyCandidate? selected = null;
            while (remainingCandidates.Count > 0)
            {
                var preferred = await SelectPreferredCandidateAsync(
                    workspace,
                    agentName,
                    remainingCandidates,
                    cancellationToken);
                if (!await HasUnclosedChildrenAsync(
                        workspace,
                        agentName,
                        preferred.Issue.Id,
                        cancellationToken))
                {
                    selected = preferred;
                    break;
                }

                remainingCandidates.Remove(preferred);
            }

            if (selected is null)
            {
                return null;
            }

            if (selected.Issue.Status is not IssueStatus.Open)
            {
                throw new BeadsException($"ready issue '{selected.Issue.Id}' was not open");
            }

            var claimResult = await RunWithActorAsync(
                workspace,
                agentName,
                ["update", selected.Issue.Id, "--claim", "--json"],
                cancellationToken);
            if (claimResult.Succeeded)
            {
                return ParseSingleClaim(claimResult.StandardOutput, "claim result")
                    ?? throw new BeadsException(
                        $"bd update --claim returned no issue for '{selected.Issue.Id}'");
            }

            if (!IsClaimContention(claimResult))
            {
                EnsureCommandSuccess(claimResult, $"claim ready work '{selected.Issue.Id}'");
            }

            // The chosen issue can be claimed by another agent after the ready
            // snapshot. Dolt serialization failures represent the same safe,
            // rolled-back race. Re-read the complete candidate set so the next
            // atomic claim honors both Beads priority and the comment tie-break.
            await DelayClaimRetryAsync(attempt, cancellationToken);
        }
    }

    private async Task<bool> HasUnclosedChildrenAsync(
        string workspace,
        string agentName,
        string issueId,
        CancellationToken cancellationToken)
    {
        var childrenResult = await RunWithActorAsync(
            workspace,
            agentName,
            ["show", issueId, "--children", "--json"],
            cancellationToken);
        EnsureCommandSuccess(childrenResult, $"read children of ready issue '{issueId}'");
        return ParseHasUnclosedChildren(childrenResult.StandardOutput, issueId);
    }

    private async Task<ReadyCandidate> SelectPreferredCandidateAsync(
        string workspace,
        string agentName,
        IReadOnlyList<ReadyCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var first = candidates[0];
        var finalists = first.Priority is null
            ? candidates
            : candidates.Where(candidate => candidate.Priority == first.Priority).ToArray();
        if (finalists.Count is 1)
        {
            return first;
        }

        var candidatesWithComments = finalists.Where(static candidate => candidate.HasComments).ToArray();
        if (candidatesWithComments.Length is 0)
        {
            return first;
        }

        var arguments = new List<string> { "show" };
        arguments.AddRange(candidatesWithComments.Select(static candidate => candidate.Issue.Id));
        arguments.Add("--include-comments");
        arguments.Add("--json");
        var detailsResult = await RunWithActorAsync(
            workspace,
            agentName,
            arguments,
            cancellationToken);
        EnsureCommandSuccess(detailsResult, "read ready issue comments");

        var newestComments = ParseNewestCommentTimes(detailsResult.StandardOutput);
        var selected = first;
        DateTimeOffset? newestSelectedComment = null;
        foreach (var candidate in finalists)
        {
            if (!newestComments.TryGetValue(candidate.Issue.Id, out var newestComment)
                || newestSelectedComment is not null && newestComment <= newestSelectedComment)
            {
                continue;
            }

            selected = candidate;
            newestSelectedComment = newestComment;
        }

        return selected;
    }

    private static bool IsClaimContention(CommandResult result)
    {
        if (IsSerializationFailure(result))
        {
            return true;
        }

        var detail = $"{result.StandardOutput}\n{result.StandardError}";
        return detail.Contains("already claimed", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("issue not claimable", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DelayClaimRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponentialMilliseconds = 25 * (1 << Math.Min(attempt - 1, 4));
        var jitterMilliseconds = Random.Shared.Next(0, exponentialMilliseconds + 1);
        await Task.Delay(
            TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds),
            cancellationToken);
    }

    public Task<BeadsIssue?> TryClaimReadyAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken) =>
        TryClaimReadyAsync(workspace, agentName, DispatchFilters.Empty, cancellationToken);

    private static IReadOnlyList<string> ReadyArguments(
        DispatchFilters filters,
        string? assignee = null,
        bool unassigned = false)
    {
        var arguments = new List<string> { "ready" };
        if (unassigned)
        {
            arguments.Add("--unassigned");
        }

        if (assignee is not null)
        {
            arguments.Add("--assignee");
            arguments.Add(assignee);
        }

        arguments.Add("--exclude-label");
        arguments.Add(MergeSlotLabel);
        foreach (var label in filters.Labels)
        {
            arguments.Add("--label");
            arguments.Add(label);
        }

        foreach (var label in filters.ExcludedLabels)
        {
            arguments.Add("--exclude-label");
            arguments.Add(label);
        }

        if (filters.IssueType is not null)
        {
            arguments.Add("--type");
            arguments.Add(filters.IssueType);
        }

        if (filters.Priority is not null)
        {
            arguments.Add("--priority");
            arguments.Add(filters.Priority.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add("--limit");
        arguments.Add("0");
        arguments.Add("--json");
        return arguments;
    }

    internal static bool IsSerializationFailure(CommandResult result)
    {
        var detail = $"{result.StandardOutput}\n{result.StandardError}";
        return detail.Contains("serialization failure", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("SQLSTATE 40001", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Error 1213", StringComparison.OrdinalIgnoreCase);
    }

    private static BeadsIssue? ParseSingleClaim(string json, string context)
    {
        var issues = ParseIssues(json, context);
        return issues.Count switch
        {
            0 => null,
            1 when issues[0].Status is IssueStatus.InProgress => issues[0],
            1 => throw new BeadsException($"claimed issue '{issues[0].Id}' was not in_progress"),
            _ => throw new BeadsException("Beads returned more than one claimed issue"),
        };
    }

    public async Task<BeadsIssue?> GetIssueAsync(
        string workspace,
        string agentName,
        string issueId,
        CancellationToken cancellationToken)
    {
        var result = await RunWithActorAsync(
            workspace,
            agentName,
            ["show", issueId, "--json"],
            cancellationToken);
        EnsureCommandSuccess(result, $"read issue '{issueId}'");

        var issues = ParseIssues(result.StandardOutput, "issue result");
        return issues.Count switch
        {
            0 => null,
            1 when string.Equals(issues[0].Id, issueId, StringComparison.Ordinal) => issues[0],
            1 => throw new BeadsException($"bd show returned unexpected issue '{issues[0].Id}'"),
            _ => throw new BeadsException($"bd show returned more than one issue for '{issueId}'"),
        };
    }

    public async Task<IReadOnlyList<BeadsIssue>> GetIssuesNeedingUserAttentionAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var result = await RunWithActorAsync(
            workspace,
            agentName,
            ["list", "--label", NeedsUserAttentionLabel, "--all", "--limit", "0", "--json"],
            cancellationToken);
        EnsureCommandSuccess(result, "list issues needing user attention");
        return ParseIssues(result.StandardOutput, "user-attention issue result");
    }

    public async Task<IReadOnlyList<BeadsIssue>> GetIssuesNeedingUserAttentionAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            workspace,
            agentName: null,
            ["list", "--label", NeedsUserAttentionLabel, "--all", "--limit", "0", "--json"],
            cancellationToken);
        EnsureCommandSuccess(result, "list issues needing user attention");
        return ParseIssues(result.StandardOutput, "user-attention issue result");
    }

    public async Task<IReadOnlyList<BeadsIssue>> GetClosedIssuesAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            workspace,
            agentName: null,
            ["list", "--status", "closed", "--all", "--limit", "0", "--json"],
            cancellationToken);
        EnsureCommandSuccess(result, "list closed issues");
        return ParseIssues(result.StandardOutput, "closed issue result");
    }

    public async Task<IReadOnlyList<BeadsComment>> GetLatestCommentsAsync(
        string workspace,
        string agentName,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        // Export is available in both embedded and server-backed Dolt modes,
        // unlike bd sql. Read-only mode guards this dashboard-only query from
        // accidentally mutating the project as the CLI evolves.
        var result = await RunWithActorAsync(
            workspace,
            agentName,
            ["--readonly", "export"],
            cancellationToken);
        EnsureCommandSuccess(result, "read latest comments");
        return ParseLatestComments(result.StandardOutput, count);
    }

    public Task<CommandResult> PullAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken) =>
        RunWithActorAsync(workspace, agentName, ["dolt", "pull"], cancellationToken);

    public Task<CommandResult> PushAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken) =>
        RunWithActorAsync(workspace, agentName, ["dolt", "push"], cancellationToken);

    public async Task<string> ReadCurrentDoltCommitAsync(
        string workspace,
        string? agentName,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            workspace,
            agentName,
            ["--readonly", "vc", "status", "--json"],
            cancellationToken);
        EnsureCommandSuccess(result, "read the current Beads Dolt commit");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var commit = document.RootElement.GetProperty("commit").GetString();
            if (string.IsNullOrWhiteSpace(commit))
            {
                throw new JsonException("commit is missing");
            }

            return commit;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BeadsException($"Beads returned invalid version-control status JSON: {exception.Message}");
        }
    }

    public async Task<CommandResult> ReopenAsync(
        string workspace,
        string agentName,
        string issueId,
        string reason,
        CancellationToken cancellationToken) =>
        await RunWithActorAsync(
            workspace,
            agentName,
            ["update", issueId, "--status", "open", "--assignee", "", "--append-notes", reason, "--json"],
            cancellationToken);

    public async Task<DoltIdentity> ReadDoltIdentityAsync(
        string workspace,
        string? agentName,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspace, agentName, ["dolt", "show", "--json"], cancellationToken);
        EnsureSuccess(result, "query Beads Dolt configuration");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var embedded = root.GetProperty("embedded").GetBoolean();
            var database = root.GetProperty("database").GetString();
            if (string.IsNullOrWhiteSpace(database))
            {
                throw new JsonException("database is missing");
            }

            string? host = null;
            int? port = null;
            var connectionOk = embedded;
            if (!embedded)
            {
                host = root.TryGetProperty("host", out var hostElement) ? hostElement.GetString() : null;
                port = root.TryGetProperty("port", out var portElement) && portElement.TryGetInt32(out var parsedPort)
                    ? parsedPort
                    : null;
                connectionOk = root.TryGetProperty("connection_ok", out var connectionElement)
                    && connectionElement.ValueKind is JsonValueKind.True;
            }

            return new DoltIdentity(embedded, database, host, port, connectionOk);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new PreflightException($"Beads returned invalid Dolt configuration JSON: {exception.Message}");
        }
    }

    public async Task<bool> HasRemoteAsync(
        string workspace,
        string? agentName,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspace, agentName, ["dolt", "remote", "list", "--json"], cancellationToken);
        EnsureSuccess(result, "list Beads Dolt remotes");

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new JsonException("expected an array");
            }

            return document.RootElement.GetArrayLength() > 0;
        }
        catch (JsonException exception)
        {
            throw new PreflightException($"Beads returned invalid remote-list JSON: {exception.Message}");
        }
    }

    private Task<CommandResult> RunAsync(
        string workspace,
        string? agentName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        runner.RunAsync(new CommandSpec(executable, arguments, workspace, AgentName: agentName), cancellationToken);

    private Task<CommandResult> RunWithActorAsync(
        string workspace,
        string agentName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        runner.RunAsync(new CommandSpec(
            executable,
            arguments,
            workspace,
            new Dictionary<string, string?> { ["BEADS_ACTOR"] = agentName },
            agentName), cancellationToken);

    internal static IReadOnlyList<BeadsIssue> ParseIssues(string json, string context)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new JsonException("expected an array");
            }

            var issues = new List<BeadsIssue>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = element.GetProperty("id").GetString();
                var status = element.GetProperty("status").GetString();
                var title = element.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(status))
                {
                    throw new JsonException("issue id or status is missing");
                }

                issues.Add(new BeadsIssue(id, ParseStatus(status), title));
            }

            return issues;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BeadsException($"Beads returned invalid {context} JSON: {exception.Message}");
        }
    }

    private static IReadOnlyList<ReadyCandidate> ParseReadyCandidates(string json, string context)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new JsonException("expected an array");
            }

            var candidates = new List<ReadyCandidate>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = element.GetProperty("id").GetString();
                var status = element.GetProperty("status").GetString();
                var title = element.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(status))
                {
                    throw new JsonException("issue id or status is missing");
                }

                int? priority = element.TryGetProperty("priority", out var priorityElement)
                    && priorityElement.TryGetInt32(out var parsedPriority)
                    ? parsedPriority
                    : null;
                var hasComments = element.TryGetProperty("comment_count", out var commentCountElement)
                    && commentCountElement.TryGetInt32(out var commentCount)
                    && commentCount > 0;
                candidates.Add(new ReadyCandidate(
                    new BeadsIssue(id, ParseStatus(status), title),
                    priority,
                    hasComments));
            }

            return candidates;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BeadsException($"Beads returned invalid {context} JSON: {exception.Message}");
        }
    }

    internal static bool ParseHasUnclosedChildren(string json, string issueId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new JsonException("expected an object");
            }

            if (!document.RootElement.TryGetProperty(issueId, out var children)
                || children.ValueKind is not JsonValueKind.Array)
            {
                throw new JsonException($"children are missing for issue '{issueId}'");
            }

            foreach (var child in children.EnumerateArray())
            {
                var status = child.GetProperty("status").GetString();
                if (string.IsNullOrWhiteSpace(status))
                {
                    throw new JsonException($"child status is missing for issue '{issueId}'");
                }

                if (!string.Equals(status, "closed", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BeadsException($"Beads returned invalid child issue JSON: {exception.Message}");
        }
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> ParseNewestCommentTimes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                throw new JsonException("expected an array");
            }

            var newestComments = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                var issueId = issue.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(issueId))
                {
                    throw new JsonException("issue id is missing");
                }

                if (!issue.TryGetProperty("comments", out var comments)
                    || comments.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var comment in comments.EnumerateArray())
                {
                    var createdAtText = comment.GetProperty("created_at").GetString();
                    if (!DateTimeOffset.TryParse(
                        createdAtText,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                            | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var createdAt))
                    {
                        throw new JsonException($"comment timestamp is missing for issue '{issueId}'");
                    }

                    if (!newestComments.TryGetValue(issueId, out var existing)
                        || createdAt > existing)
                    {
                        newestComments[issueId] = createdAt;
                    }
                }
            }

            return newestComments;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BeadsException($"Beads returned invalid ready issue comment JSON: {exception.Message}");
        }
    }

    internal static IReadOnlyList<BeadsComment> ParseLatestComments(string jsonLines, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        try
        {
            var comments = new List<BeadsComment>();
            using var reader = new StringReader(jsonLines);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var issue = document.RootElement;
                if (!issue.TryGetProperty("id", out var issueIdElement))
                {
                    continue;
                }

                var issueId = issueIdElement.GetString();
                if (string.IsNullOrWhiteSpace(issueId))
                {
                    throw new JsonException("issue id is missing");
                }

                var issueTitle = issue.TryGetProperty("title", out var titleElement)
                    ? titleElement.GetString()
                    : null;
                var needsUserAttention = issue.TryGetProperty("labels", out var labelsElement)
                    && labelsElement.ValueKind is JsonValueKind.Array
                    && labelsElement.EnumerateArray().Any(static label =>
                        string.Equals(
                            label.GetString(),
                            NeedsUserAttentionLabel,
                            StringComparison.Ordinal));

                if (!issue.TryGetProperty("comments", out var commentsElement)
                    || commentsElement.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var comment in commentsElement.EnumerateArray())
                {
                    var id = comment.GetProperty("id").GetString();
                    var author = comment.GetProperty("author").GetString();
                    var text = comment.GetProperty("text").GetString();
                    var createdAtText = comment.GetProperty("created_at").GetString();
                    if (string.IsNullOrWhiteSpace(id)
                        || author is null
                        || text is null
                        || !DateTimeOffset.TryParse(
                            createdAtText,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal
                                | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var createdAt))
                    {
                        throw new JsonException($"comment data is missing for issue '{issueId}'");
                    }

                    comments.Add(new BeadsComment(
                        id,
                        issueId,
                        issueTitle,
                        author,
                        text,
                        createdAt,
                        needsUserAttention));
                }
            }

            return comments
                .OrderByDescending(static comment => comment.CreatedAt)
                .ThenByDescending(static comment => comment.Id, StringComparer.Ordinal)
                .Take(count)
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new BeadsException($"Beads returned invalid comment export JSON: {exception.Message}");
        }
    }

    internal static IssueStatus ParseStatus(string status) => status switch
    {
        "in_progress" => IssueStatus.InProgress,
        "open" => IssueStatus.Open,
        "blocked" => IssueStatus.Blocked,
        "closed" => IssueStatus.Closed,
        _ => IssueStatus.Unknown,
    };

    private static void EnsureCommandSuccess(CommandResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new BeadsException($"failed to {operation}: {FailureDetail(result)}");
        }
    }

    internal static string FailureDetail(CommandResult result) =>
        !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError.Trim()
            : !string.IsNullOrWhiteSpace(result.StandardOutput)
                ? result.StandardOutput.Trim()
                : $"exit code {result.ExitCode}";

    private static void EnsureSuccess(CommandResult result, string operation)
    {
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : result.StandardError.Trim();
            throw new PreflightException($"failed to {operation}: {detail}");
        }
    }
}

public sealed class BeadsException(string message) : Exception(message);
