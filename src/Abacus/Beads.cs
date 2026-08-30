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

public sealed class Beads(CommandRunner runner, string executable = "bd")
{
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
