using System.Collections.Immutable;
using System.Text.Json;

namespace Abacus.Dashboard;

internal sealed record WorkerActionRequest(string Session, string RequestId, string Command, string Worker, bool Confirm, string? Prompt = null);
internal sealed record WorkerActionView(string RequestId, string Worker, string Command, string Outcome);
internal sealed record WorkerActionResult(int StatusCode, string Outcome, string Message, string RequestId);

/// <summary>Bounded per-run retries. Completion comes only from controller or supervisor force receipts.</summary>
internal sealed class DashboardWorkerActions(Action changed, bool supervisors = false)
{
    private sealed record Entry(WorkerActionRequest Request, Task<AgentControlOutcome> Completion);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = [];
    private RunControlRouter? router;
    private bool stopped;
    public bool Available { get { lock (gate) return router is not null && !stopped; } }
    internal void Bind(RunControlRouter value)
    {
        lock (gate)
        {
            if (router is not null || stopped) throw new InvalidOperationException("Worker controls cannot be rebound.");
            router = value;
        }
        changed();
    }
    internal void Stop() { lock (gate) stopped = true; changed(); }

    internal static WorkerActionRequest Parse(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected worker action object.");
        var fields = body.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length is < 5 or > 6 || fields.Distinct().Count() != fields.Length ||
            new[] { "session", "requestId", "command", "worker", "confirm" }.Any(p => !fields.Contains(p)) ||
            fields.Any(p => p is not ("session" or "requestId" or "command" or "worker" or "confirm" or "prompt")))
            throw new ArgumentException("Invalid worker action fields.");
        string Text(string name) => body.GetProperty(name).ValueKind == JsonValueKind.String
            ? body.GetProperty(name).GetString()! : throw new ArgumentException("Expected string.");
        var session = Text("session"); var id = Text("requestId"); var command = Text("command"); var worker = Text("worker");
        var confirm = body.GetProperty("confirm");
        if (!Guid.TryParseExact(session, "N", out _) || !Guid.TryParse(id, out _) ||
            command is not ("stop" or "restart" or "clean-workspace" or "force-run") || string.IsNullOrWhiteSpace(worker) || worker.Length > 100 || worker.Any(char.IsControl) ||
            confirm.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Invalid worker action.");
        var prompt = body.TryGetProperty("prompt", out var value) && value.ValueKind != JsonValueKind.Null ? Text("prompt") : null;
        if (command == "force-run" ? string.IsNullOrWhiteSpace(prompt) || prompt.Length > 16000 || prompt.Contains('\0') : prompt is not null)
            throw new ArgumentException("Invalid force prompt.");
        return new(session, id, command, worker, confirm.GetBoolean(), prompt);
    }

    internal WorkerActionResult Submit(string session, WorkerActionRequest request)
    {
        lock (gate)
        {
            WorkerActionResult Reject(int code, string message) => new(code, "rejected", message, request.RequestId);
            if (request.Session != session) return Reject(409, "Different run session; reload before acting.");
            if (entries.TryGetValue(request.RequestId, out var existing))
                return existing.Request == request ? Result(existing) : Reject(400, "Request ID already used with different content.");
            if (stopped || router is null) return Reject(503, "Worker controls are unavailable or stopping.");
            if (entries.Count >= 1024) return Reject(503, "Worker retry ledger is full; use TUI or stdio for this run.");
            if (request.Command == "clean-workspace" && !request.Confirm) return Reject(400, "Workspace cleanup requires explicit confirmation.");
            if (request.Command == "force-run")
            {
                if (!supervisors || !request.Confirm || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 16000 || request.Prompt.Contains('\0'))
                    return Reject(400, "Force Run requires a supervisor, a bounded nonempty prompt and explicit confirmation.");
                if (entries.Values.Any(e => e.Request.Worker == request.Worker && !e.Completion.IsCompleted))
                    return Reject(409, "Supervisor already has a pending dashboard action; Stop/Restart remain available.");
            }
            else if (request.Prompt is not null) return Reject(400, "Prompts are only allowed for Force Run.");
            var action = request.Command switch { "stop" => AgentControlAction.Stop, "restart" => AgentControlAction.Restart,
                "clean-workspace" => AgentControlAction.CleanWorkspace, _ => (AgentControlAction)(-1) };
            Task<AgentControlOutcome> completion;
            try
            {
                completion = request.Command == "force-run"
                    ? router.ForceSupervisorTracked(request.Worker, request.Prompt!).Completion
                    : (supervisors ? router.RequestTrackedSupervisor(request.Worker, action) : router.RequestTrackedWorker(request.Worker, action)).Completion;
            }
            catch (ArgumentException) { return Reject(400, "Unknown target or unsupported action; worker and supervisor controls are separate."); }
            catch (InvalidOperationException) { return Reject(409, "Target is finished, already handling an action, or the run is stopping."); }
            catch (AggregateException)
            {
                // Cancellation callbacks may throw after the request was installed.
                // Retain an unknown outcome rather than allowing that ID to replay.
                completion = Task.FromResult(new AgentControlOutcome("outcome-unknown"));
            }
            var entry = new Entry(request, completion);
            entries.Add(request.RequestId, entry);
            // Only a bounded hint runs on completion; never browser I/O or worker locks.
            _ = completion.ContinueWith(_ => changed(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            changed();
            return Result(entry);
        }
    }

    private static string Outcome(Entry entry) => entry.Completion.IsCompletedSuccessfully
        ? entry.Completion.Result.Outcome : "accepted";
    private WorkerActionResult Result(Entry entry)
    {
        var outcome = Outcome(entry);
        var message = outcome switch
        {
            "accepted" => "Accepted; waiting for the target to acknowledge the action.",
            "completed" when entry.Request.Command == "force-run" => "Supervisor force run completed cleanup and its verification checks. Review the run result before further action.",
            "cancelled" => "Queued force request was cancelled before it started.",
            "deferred" => "Force request was deferred by supervisor policy; no automatic replay of this request.",
            "failed" when entry.Request.Command == "force-run" => "Supervisor force run or verification failed. Review its state before requesting another run.",
            "completed" when supervisors && entry.Request.Command == "restart" => "Supervision re-enabled and retry state reset; no new harness is guaranteed to have started.",
            "completed" when supervisors => "Supervisor acknowledged the stopped boundary after any active harness cleanup.",
            "completed" when entry.Request.Command == "restart" => "Dispatch resumed; this does not guarantee that a new harness has started.",
            "completed" => "Worker acknowledged the action as completed.",
            "failed" => "Worker cleanup failed. Review its state before starting any new action.",
            _ => "Completion is unknown. Review worker state before acting again.",
        };
        return new(outcome == "accepted" ? 202 : outcome is "completed" or "cancelled" or "deferred" ? 200 : 503, outcome, message, entry.Request.RequestId);
    }
    internal ImmutableArray<WorkerActionView> Snapshot()
    {
        lock (gate)
        {
            var all = entries.Values.Select(e => new WorkerActionView(e.Request.RequestId, e.Request.Worker, e.Request.Command, Outcome(e))).ToArray();
            // Keep every pending action and only the latest terminal outcomes in the
            // live view. Older IDs remain in the ledger for safe retry responses.
            return all.Where(a => a.Outcome == "accepted").Concat(all.Where(a => a.Outcome != "accepted").TakeLast(32)).ToImmutableArray();
        }
    }
}
