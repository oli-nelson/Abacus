using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;

namespace Abacus.Dashboard;

internal sealed record RuntimeWorker(string Name, string Activity, string? IssueId, string? Branch,
    bool? Dirty, bool RunActive, int? ExitCode, int RetryCount, bool Supervisor);
internal sealed record RuntimeClaims(bool ManualEnabled, bool ScheduleAllows, bool GateAllows, string Reason);
internal sealed record RuntimeView(long Revision, ImmutableArray<RuntimeWorker> Workers, RuntimeClaims? Claims = null)
{
    public bool WorkerControlsAvailable { get; init; }
    public bool SupervisorControlsAvailable { get; init; }
    public ImmutableArray<WorkerActionView> SupervisorActions { get; init; } = [];
    public bool StopRunAvailable { get; init; }
    public ImmutableArray<WorkerActionView> WorkerActions { get; init; } = [];
}
internal sealed record RuntimeControl(string Session, string RequestId, string Command, bool ExpectedManualEnabled);
internal sealed record RuntimeControlResult(int StatusCode, string Outcome, string Message, string RequestId);
internal sealed record RuntimePublication(RuntimeView View, byte[] Body);

/// <summary>Safe, immutable projection of this process's authoritative worker rows.
/// Output callbacks only queue a bounded hint; serialization never holds its lock.</summary>
internal sealed class DashboardRuntime(ConsoleOutput output, ClaimGate? claimGate = null, ClaimSchedule? schedule = null, TimeProvider? clock = null)
{
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private DashboardRunActions? runActions;
    internal DashboardRunActions RunActions => LazyInitializer.EnsureInitialized(ref runActions, () => new DashboardRunActions(Changed));
    private DashboardWorkerActions? supervisorActions;
    internal DashboardWorkerActions SupervisorActions => LazyInitializer.EnsureInitialized(ref supervisorActions, () => new DashboardWorkerActions(Changed, supervisors: true));
    private DashboardWorkerActions? workerActions;
    internal DashboardWorkerActions WorkerActions => LazyInitializer.EnsureInitialized(ref workerActions, () => new DashboardWorkerActions(Changed));
    private RuntimePublication state = Serialize(new(0, []));
    private readonly object controls = new();
    private readonly Dictionary<string, (RuntimeControl Request, RuntimeControlResult Result)> requests = [];
    private bool stopping;
    public bool CanControlClaims { get { lock (controls) return claimGate is not null && !stopping; } }
    internal void StopControls() { lock (controls) stopping = true; WorkerActions.Stop(); SupervisorActions.Stop(); RunActions.Stop(); }

    internal static RuntimeControl ParseControl(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected control object.");
        var fields = body.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != 4 || fields.Distinct().Count() != 4 ||
            fields.Any(p => p is not ("session" or "requestId" or "command" or "expectedManualEnabled")))
            throw new ArgumentException("Unexpected control fields.");
        string Text(string name) => body.GetProperty(name).ValueKind == JsonValueKind.String
            ? body.GetProperty(name).GetString()! : throw new ArgumentException("Expected string.");
        var session = Text("session"); var id = Text("requestId"); var command = Text("command");
        var expected = body.GetProperty("expectedManualEnabled");
        if (!Guid.TryParseExact(session, "N", out _) || !Guid.TryParse(id, out _) || command is not ("pause" or "resume") ||
            expected.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Invalid control.");
        return new(session, id, command, expected.GetBoolean());
    }

    internal RuntimeControlResult Submit(RuntimeControl request)
    {
        lock (controls)
        {
            RuntimeControlResult Result(int code, string outcome, string message) => new(code, outcome, message, request.RequestId);
            if (request.Session != Instance) return Result(409, "rejected", "This request belongs to a different run. Reload before acting.");
            if (requests.TryGetValue(request.RequestId, out var prior))
                return prior.Request == request ? prior.Result : Result(400, "rejected", "Request ID already used for different content.");
            if (claimGate is null || stopping) return Result(503, "rejected", "Runtime controls are unavailable or the run is stopping.");
            if (requests.Count >= 1024) return Result(503, "rejected", "This run's control retry ledger is full; use TUI or stdio controls.");
            if (request.Command is not ("pause" or "resume")) return Result(400, "rejected", "Unsupported runtime command.");
            var result = claimGate.TrySetEnabled(request.ExpectedManualEnabled, request.Command == "resume")
                ? Result(200, "completed", request.Command == "pause"
                    ? "Manual claim gate paused. Running work is not stopped."
                    : "Manual claim gate enabled. Schedule and ownership restrictions still apply.")
                : Result(409, "rejected", "Manual gate changed. Review current state before a new request.");
            requests.Add(request.RequestId, (request, result));
            if (result.StatusCode == 200) output.Events?.Emit("claims.changed", new { enabled = claimGate.IsEnabled, source = "dashboard" });
            return result;
        }
    }

    public string Instance { get; } = Guid.NewGuid().ToString("N");
    public RuntimePublication State => Volatile.Read(ref state);
    private static RuntimePublication Serialize(RuntimeView view) => new(view, JsonSerializer.SerializeToUtf8Bytes(view, DashboardStream.Json));
    private void Changed() => changes.Writer.TryWrite(true);

    internal RuntimePublication Refresh(bool refreshWorkers = true)
    {
        var previous = State;
        var workers = refreshWorkers ? output.ReadRuntimeWorkers() : previous.View.Workers;
        RuntimeClaims? claims = null;
        if (claimGate is not null)
        {
            var manual = claimGate.IsEnabled;
            output.SynchronizeClaimGate(manual);
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var allowed = schedule?.CanClaimAt(now, out _) ?? true;
            var reason = !manual ? "Manually paused" : allowed ? "Claim gates allow dispatch" :
                schedule!.IsBlockedAt(now) ? "Inside a blocked schedule window" : "Insufficient time before the next blocked window";
            claims = new(manual, allowed, manual && allowed, reason);
        }
        var supervisorActions = SupervisorActions.Snapshot();
        var supervisorAvailable = SupervisorActions.Available;
        var actions = WorkerActions.Snapshot();
        var available = WorkerActions.Available;
        var stopAvailable = RunActions.Available;
        if (previous.View.Workers.SequenceEqual(workers) && previous.View.Claims == claims &&
            previous.View.SupervisorControlsAvailable == supervisorAvailable && previous.View.SupervisorActions.SequenceEqual(supervisorActions) &&
            previous.View.StopRunAvailable == stopAvailable && previous.View.WorkerControlsAvailable == available && previous.View.WorkerActions.SequenceEqual(actions)) return previous;
        var next = Serialize(new RuntimeView(previous.View.Revision + 1, workers, claims)
            { SupervisorActions = supervisorActions, SupervisorControlsAvailable = supervisorAvailable, WorkerActions = actions, WorkerControlsAvailable = available, StopRunAvailable = stopAvailable });
        Volatile.Write(ref state, next);
        return next;
    }

    public async Task RunAsync(Action<RuntimePublication> publish, CancellationToken token)
    {
        output.RuntimeChanged += Changed;
        if (claimGate is not null) claimGate.Changed += Changed;
        try
        {
            RuntimePublication? previous = null;
            var refreshWorkers = true;
            while (true)
            {
                var next = Refresh(refreshWorkers);
                if (!ReferenceEquals(previous, next)) publish(next);
                previous = next;
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                // Schedule boundaries are wall-clock changes, not worker events.
                // This in-memory check never calls Beads/Git or rebuilds worker rows.
                if (schedule is not null) wait.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await changes.Reader.ReadAsync(wait.Token);
                    while (changes.Reader.TryRead(out _)) { }
                    refreshWorkers = true;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { refreshWorkers = false; }
            }
        }
        finally
        {
            StopControls();
            output.RuntimeChanged -= Changed;
            if (claimGate is not null) claimGate.Changed -= Changed;
        }
    }
}
