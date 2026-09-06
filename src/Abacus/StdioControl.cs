using System.Text;
using System.Text.Json;

namespace Abacus;

internal sealed class StdioControl(
    TextReader input,
    ConsoleOutput output,
    ClaimGate claims,
    Action<string, AgentControlAction> requestAction,
    Action shutdown)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        output.Events!.Emit("control.ready", new
        {
            commands = new[] { "status", "pause", "resume", "stop", "restart", "clean-workspace", "shutdown" },
            claimsEnabled = claims.IsEnabled,
        });
        try
        {
            while (true)
            {
                // Console.In may implement async reads synchronously. Keep the input read off
                // the orchestration thread and stop waiting when finite runs finish.
                var line = await Task.Run(() => ReadCommandAsync(cancellationToken),
                    CancellationToken.None).WaitAsync(cancellationToken);
                if (line is null)
                {
                    output.Events.Emit("control.eof");
                    shutdown();
                    return;
                }
                if (!Handle(line)) return;
            }
        }
        catch (IOException exception)
        {
            output.Events.Emit("control.error", new { message = exception.Message });
            shutdown();
        }
    }

    private async Task<string?> ReadCommandAsync(CancellationToken cancellationToken)
    {
        // Bound retained input even if a controller sends an oversized line. Drain the
        // remainder so the following command is still framed correctly.
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await input.ReadAsync(buffer.AsMemory(), cancellationToken) != 0)
        {
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length <= 65536) line.Append(buffer[0]);
        }
        return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
    }

    internal bool Handle(string line)
    {
        string? id = null;
        string? command = null;
        try
        {
            if (line.Length > 65536) throw new FormatException("command exceeds 65536 characters");
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("expected a JSON object");
            var properties = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (properties.Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw new FormatException("duplicate properties are not allowed");
            id = RequiredString(root, "id");
            command = RequiredString(root, "command");
            var agentAction = command is "stop" or "restart" or "clean-workspace";
            if (properties.Any(p => p is not ("id" or "command")
                && !(agentAction && p == "agent") && !(command == "clean-workspace" && p == "confirm")))
                throw new FormatException("unexpected command property");
            switch (command)
            {
                case "status": output.ReportStatus(id, claims.IsEnabled); return true;
                case "pause":
                case "resume":
                    claims.SetEnabled(command == "resume");
                    output.Events!.Emit("claims.changed", new { enabled = claims.IsEnabled });
                    break;
                case "stop":
                case "restart":
                case "clean-workspace":
                    var agent = RequiredString(root, "agent");
                    if (command == "clean-workspace" && (!root.TryGetProperty("confirm", out var confirm)
                        || confirm.ValueKind != JsonValueKind.True))
                        throw new FormatException("clean-workspace discards workspace changes; requires confirm: true");
                    requestAction(agent, command switch
                    {
                        "stop" => AgentControlAction.Stop,
                        "restart" => AgentControlAction.Restart,
                        _ => AgentControlAction.CleanWorkspace,
                    });
                    break;
                case "shutdown": break;
                default: throw new FormatException($"unknown command '{command}'");
            }
            output.Events!.Emit("control.result", new { id, command, ok = true });
            if (command == "shutdown") { shutdown(); return false; }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException
            or InvalidOperationException)
        {
            output.Events!.Emit("control.result", new { id, command, ok = false, error = exception.Message });
        }
        return true;
    }

    private static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"'{property}' must be a nonempty string");
        return value.GetString()!;
    }
}
