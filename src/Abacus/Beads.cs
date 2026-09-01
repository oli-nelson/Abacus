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

public sealed class Beads(CommandRunner runner, string executable = "bd")
{
    private const string MergeSlotLabel = "gt:slot";
    public const string NeedsUserAttentionLabel = "abacus:needs-user-attention";

    public async Task<BeadsIssue?> TryClaimReadyAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var result = await RunClaimWithRetryAsync(workspace, agentName, cancellationToken);
        EnsureCommandSuccess(result, "claim ready work");

        var claim = ParseSingleClaim(result.StandardOutput, "claim result");
        if (claim is not null)
        {
            return claim;
        }

        // Reopened work created by older Abacus versions can remain assigned to
        // this agent. It is invisible to bd ready --claim, so resume only work
        // already owned by this identity and leave other agents' work alone.
        var assignedResult = await RunWithActorAsync(
            workspace,
            agentName,
            ["ready", "--assignee", agentName, "--exclude-label", MergeSlotLabel, "--json"],
            cancellationToken);
        EnsureCommandSuccess(assignedResult, "find ready work assigned to this agent");

        var assignedIssues = ParseIssues(assignedResult.StandardOutput, "assigned ready result");
        if (assignedIssues.Count is 0)
        {
            return null;
        }

        var assignedIssue = assignedIssues[0];
        if (assignedIssue.Status is not IssueStatus.Open)
        {
            throw new BeadsException($"ready issue '{assignedIssue.Id}' was not open");
        }

        var reclaimResult = await RunWithActorAsync(
            workspace,
            agentName,
            ["update", assignedIssue.Id, "--claim", "--json"],
            cancellationToken);
        EnsureCommandSuccess(reclaimResult, $"reclaim ready work '{assignedIssue.Id}'");
        return ParseSingleClaim(reclaimResult.StandardOutput, "reclaim result")
            ?? throw new BeadsException($"bd update --claim returned no issue for '{assignedIssue.Id}'");
    }

    private async Task<CommandResult> RunClaimWithRetryAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await RunWithActorAsync(
                workspace,
                agentName,
                ["ready", "--claim", "--exclude-label", MergeSlotLabel, "--json"],
                cancellationToken);
            if (result.Succeeded
                || !IsSerializationFailure(result))
            {
                return result;
            }

            // Dolt uses optimistic concurrency. A conflicting transaction is
            // rolled back safely and reports SQL error 1213/SQLSTATE 40001,
            // so retry the complete atomic claim with capped backoff and
            // jitter. This is process-independent when several Abacus instances
            // share the same Beads database.
            var exponentialMilliseconds = 25 * (1 << Math.Min(attempt - 1, 4));
            var jitterMilliseconds = Random.Shared.Next(0, exponentialMilliseconds + 1);
            await Task.Delay(
                TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds),
                cancellationToken);
        }
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
