using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record RunActionRequest(string Session, string RequestId, string Command, bool Confirm);

/// <summary>A run can stop only once. Keep its accepted identity until process exit.</summary>
internal sealed class DashboardRunActions(Action changed)
{
    private readonly object gate = new();
    private Action? shutdown;
    private RunActionRequest? accepted;
    private bool dispatched, stopped;
    internal bool Available { get { lock (gate) return shutdown is not null && !stopped && accepted is null; } }
    internal void Bind(Action action)
    {
        lock (gate)
        {
            if (shutdown is not null || stopped) throw new InvalidOperationException("Run controls cannot be rebound.");
            shutdown = action;
        }
        changed();
    }
    internal void Stop() { lock (gate) stopped = true; changed(); }
    internal static RunActionRequest Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected run action object.");
        var fields = body.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != 4 || fields.Distinct().Count() != 4 ||
            fields.Any(p => p is not ("session" or "requestId" or "command" or "confirm"))) throw new ArgumentException("Invalid run action fields.");
        string Text(string key) => body.GetProperty(key).ValueKind == JsonValueKind.String
            ? body.GetProperty(key).GetString()! : throw new ArgumentException("Expected string.");
        var session = Text("session"); var id = Text("requestId"); var command = Text("command");
        var confirm = body.GetProperty("confirm");
        if (!Guid.TryParseExact(session, "N", out _) || !Guid.TryParse(id, out _) || command != "stop" ||
            confirm.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Invalid run action.");
        return new(session, id, command, confirm.GetBoolean());
    }
    internal RuntimeControlResult Submit(string session, RunActionRequest request)
    {
        lock (gate)
        {
            RuntimeControlResult Reject(int code, string message) => new(code, "rejected", message, request.RequestId);
            if (session != request.Session) return Reject(409, "Different run session; reload before acting.");
            if (accepted?.RequestId == request.RequestId && accepted != request) return Reject(400, "Request ID already used with different content.");
            if (accepted == request) return Accepted(request);
            if (stopped || shutdown is null || accepted is not null) return Reject(503, "Run controls unavailable or shutdown already requested.");
            if (request.Command != "stop" || !request.Confirm) return Reject(400, "Stop Run requires explicit confirmation.");
            accepted = request;
        }
        changed();
        return Accepted(request);
    }
    private static RuntimeControlResult Accepted(RunActionRequest request) => new(202, "accepted",
        "Shutdown accepted; workers will follow normal recovery and cleanup. Disconnection is not proof of completed cleanup.", request.RequestId);

    // Dispatch independently of HTTP delivery; an accepted response may be lost.
    // Reserve dispatch under lock, but never run cancellation callbacks under it.
    internal void DispatchAccepted()
    {
        Action? action;
        lock (gate)
        {
            if (accepted is null || dispatched) return;
            dispatched = true;
            action = shutdown;
        }
        try { action?.Invoke(); }
        catch (AggregateException) { /* Cancellation was signalled; do not replay callbacks. */ }
        catch (ObjectDisposedException) { /* Owner already finished shutdown. */ }
    }
}
