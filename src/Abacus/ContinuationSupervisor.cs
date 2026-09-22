using System.Text.Json;

namespace Abacus;

/// <summary>Optional one-shot planning at an empty epic backlog. No task queue or implementation decisions.</summary>
public sealed class ContinuationSupervisor(
    PreflightResult preflight, Beads beads, IAgentHost host, TextWriter log, string temporaryRoot,
    ContinuationState state, ClaimGate claimGate, TimeSpan? pollingInterval = null)
{
    public const string Name = "continuation";
    private readonly SemaphoreSlim check = new(1, 1);
    private readonly AgentControl control = new();
    private readonly Dictionary<string, int> finiteChecks = new(StringComparer.Ordinal);
    private readonly TimeSpan interval = pollingInterval ?? TimeSpan.FromSeconds(1);
    private bool enabled = true;
    private bool attemptedInFiniteRun;
    private int generation;
    private readonly object forceGate = new();
    private string? forcedPrompt;
    private SupervisorForceReceipt? queuedForceReceipt;
    private bool forceClosed;
    private bool forcedRunActive;

    public bool IsForcedRun { get { lock (forceGate) return forcedRunActive; } }

    public void ForceRun(string prompt) => QueueForce(prompt, null);

    internal SupervisorForceReceipt ForceRunTracked(string prompt)
    {
        var receipt = new SupervisorForceReceipt();
        QueueForce(prompt, receipt);
        return receipt;
    }

    private void QueueForce(string prompt, SupervisorForceReceipt? receipt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("force-run prompt must not be empty");
        lock (forceGate)
        {
            if (forceClosed) throw new InvalidOperationException("Supervisor has finished.");
            if (forcedPrompt is not null) throw new InvalidOperationException("continuation already has a pending force run");
            forcedPrompt = prompt;
            queuedForceReceipt = receipt;
            Volatile.Write(ref enabled, true);
        }
    }

    internal Func<SoundClip, Task> StartSound { get; init; } = clip =>
    {
        SoundPlayer.TryStart(clip)?.ContinueInBackground();
        return Task.CompletedTask;
    };

    internal AgentControlReceipt RequestTracked(AgentControlAction action)
    {
        if (action is not (AgentControlAction.Stop or AgentControlAction.Restart))
            throw new ArgumentException("Supervisors support tracked Stop/Restart only; main-checkout cleanup is forbidden.");
        return control.TryRequestTracked(action)
            ?? throw new InvalidOperationException("Supervisor already has a pending control request.");
    }

    public void Request(AgentControlAction action)
    {
        if (action == AgentControlAction.CleanWorkspace)
            throw new InvalidOperationException("The continuation supervisor cannot clean the main checkout; use Stop or Restart.");
        if (!control.TryRequest(action)) throw new InvalidOperationException("Continuation already has a pending request");
    }

    public async Task RunAsync(Func<bool> workersFinished, CancellationToken token)
    {
        if (log is ConsoleOutput console) console.EnableSupervisor(Name);
        await log.SetAgentAsync(Name, AgentActivity.Idle, "Enabled; waiting for no unfinished epics");
        try
        {
            while (!workersFinished())
            {
                await CheckAsync(token);
                if (!workersFinished()) await Task.Delay(interval, token);
            }
        }
        finally
        {
            lock (forceGate)
            {
                forceClosed = true;
                queuedForceReceipt?.Finish("outcome-unknown");
                queuedForceReceipt = null;
                forcedPrompt = null;
            }
            control.Dispose();
        }
    }

    public async Task<bool> CheckFiniteCompletionAsync(string name, CancellationToken token)
    {
        await CheckAsync(token);
        lock (finiteChecks)
        {
            var previous = finiteChecks.GetValueOrDefault(name);
            finiteChecks[name] = generation;
            return generation > previous;
        }
    }

    private async Task CheckAsync(CancellationToken token)
    {
        await check.WaitAsync(token);
        try
        {
            var action = control.TakeRequestedAction();
            if (action == AgentControlAction.Stop)
            {
                enabled = false;
                lock (forceGate)
                {
                    forcedPrompt = null;
                    queuedForceReceipt?.Finish("cancelled");
                    queuedForceReceipt = null;
                }
                await log.SetAgentAsync(Name, AgentActivity.Stopped, "Disabled by operator; Restart to enable");
                control.CompleteTrackedAction(AgentControlAction.Stop);
            }
            else if (action == AgentControlAction.Restart)
            {
                state.Rearm();
                attemptedInFiniteRun = false;
                enabled = true;
                await log.ClearPersistentAlertAsync(Name);
                control.CompleteTrackedAction(AgentControlAction.Restart);
            }
            if (!Volatile.Read(ref enabled)) return;
            using var operation = control.CreateOperationCancellation(token);
            try
            {
                // Observe the entire epic backlog, independent of ready/label/target filters.
                string? extraPrompt;
                lock (forceGate) extraPrompt = forcedPrompt;
                if (extraPrompt is null && await beads.HasUnfinishedEpicsAsync(preflight.RepositoryRoot, operation.Token))
                {
                    state.ObserveUnfinished();
                    return;
                }
                if (extraPrompt is null && (!claimGate.IsEnabled || (preflight.Options.Schedule is { } schedule
                    && !schedule.CanClaimAt(TimeProvider.System.GetUtcNow(), out _)))) return;
                if (extraPrompt is null && preflight.Options.ExecutionMode != ExecutionMode.Continuous && attemptedInFiniteRun) return;
                if (extraPrompt is null && !state.TryConsume()) return;
                SupervisorForceReceipt? forceReceipt = null;
                lock (forceGate)
                {
                    if (extraPrompt is not null)
                    {
                        forcedPrompt = null; forcedRunActive = true;
                        forceReceipt = queuedForceReceipt; queuedForceReceipt = null;
                    }
                }
                attemptedInFiniteRun = true;
                try
                {
                    var result = await RunHarnessAsync(extraPrompt, operation.Token);
                    if (!result.StartsWith("deferred", StringComparison.Ordinal))
                        await log.SetSupervisorLastRunAsync(Name, result);
                    var hasEpics = await beads.HasUnfinishedEpicsAsync(preflight.RepositoryRoot, operation.Token);
                    if (hasEpics) state.ObserveUnfinished();
                    var deferred = result.StartsWith("deferred", StringComparison.Ordinal);
                    var failed = !deferred && !result.StartsWith("completed", StringComparison.Ordinal);
                    var detail = deferred ? $"Last check: {result}; waiting for eligibility"
                        : $"Last run: {result}; {(hasEpics ? "unfinished epics now present" : "backlog still empty; automatic retry suppressed")}";
                    if (!deferred) await log.SetSupervisorLastRunAsync(Name, detail);
                    await log.SetAgentAsync(Name, failed ? AgentActivity.Stopped : AgentActivity.Idle, detail);
                    if (failed) await log.SetPersistentAlertAsync(Name, detail);
                    else await log.ClearPersistentAlertAsync(Name);
                    forceReceipt?.Finish(deferred ? "deferred" : failed ? "failed" : "completed");
                }
                finally
                {
                    forceReceipt?.Finish("outcome-unknown");
                    lock (forceGate) forcedRunActive = false;
                    lock (finiteChecks) generation++;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await log.SetAgentAsync(Name, AgentActivity.Stopped, "Cancelled; automatic retry suppressed");
            }
            catch (SupervisorCleanupException ex)
            {
                await log.SetSupervisorLastRunAsync(Name, $"Last run failed during cleanup: {ex.Message}");
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await log.SetPersistentAlertAsync(Name, $"Continuation check failed: {ex.Message}");
                if (preflight.Options.ExecutionMode != ExecutionMode.Continuous) throw;
            }
        }
        finally { check.Release(); }
    }

    private async Task<string> RunHarnessAsync(string? extraPrompt, CancellationToken token)
    {
        var runId = Guid.NewGuid().ToString("N");
        var completionPath = Path.Combine(temporaryRoot, $"continuation-{runId}.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(preflight.Options.EffectiveContinuationTimeout);
        IAgentRun? run = null;
        try
        {
            var additive = await ReadAdditivePromptAsync(preflight.RepositoryRoot,
                preflight.Options.ContinuationPromptFile, timeout.Token);
            var prompt = RenderPrompt(runId, completionPath, preflight.Agents[0].Targets, additive, extraPrompt is not null);
            if (extraPrompt is not null) prompt += "\n\nAdditional instructions for this operator-requested run:\n" + extraPrompt;
            var agent = preflight.Agents[0] with
            {
                Name = Name, WorkspacePath = preflight.RepositoryRoot, Targets = null, Reasoning = null,
                AppendedPrompt = null, HarnessPromptOverride = prompt,
            };
            await log.SetModelAsync(Name, preflight.Options.ContinuationModel!, preflight.Options.ContinuationEffort);
            await log.SetAgentAsync(Name, AgentActivity.Starting, $"Starting continuation run {runId}");
            run = await host.StartAgentAsync(agent, new BeadsIssue(runId, IssueStatus.Open, "Continuation supervisor"),
                preflight.Options.ContinuationModel!, preflight.Options.ContinuationEffort,
                preflight.OpenCodeServerUrl, timeout.Token, preflight.Options.ContinuationExtraArguments ?? AgentArguments.Empty);
            if (preflight.Options.TuiAudio && log is ConsoleOutput { IsInteractiveDashboard: true })
            {
                try { await StartSound(SoundClip.ContinuationStarting); }
                catch { /* Audio is best effort, never an orchestration failure. */ }
            }
            await log.SetRunLocationAsync(Name, run.Location);
            await log.SetAgentAsync(Name, AgentActivity.Working, "Planning from user policy; completion signal pending");
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var completion = MaintenanceSupervisor.ReadCompletion(completionPath, runId);
                if (completion is not null) return $"completed ({completion})";
                if (run.HasExited || !await host.IsRunningAsync(run, timeout.Token))
                {
                    await log.SetLastExitCodeAsync(Name, run.TryReadExitCode());
                    completion = MaintenanceSupervisor.ReadCompletion(completionPath, runId);
                    return completion is not null ? $"completed ({completion})" : $"exited without completion (exit {run.TryReadExitCode()})";
                }
                await Task.Delay(interval, timeout.Token);
            }
        }
        catch (ContinuationDeferredException)
        {
            state.Rearm();
            attemptedInFiniteRun = false;
            return "deferred (backlog or claim gate changed while waiting for checkout)";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return "timed out"; }
        catch (OperationCanceledException)
        {
            await log.SetSupervisorLastRunAsync(Name, "Last run cancelled; no completed planning result confirmed.");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return $"failed: {ex.Message}"; }
        finally
        {
            try
            {
                if (run is not null) await host.StopAndCleanupAsync(run, CancellationToken.None);
            }
            catch (Exception ex) { throw new SupervisorCleanupException($"Continuation cleanup failed: {ex.Message}"); }
            finally
            {
                await log.ClearRunAsync(Name);
                try { File.Delete(completionPath); } catch (IOException) { }
            }
        }
    }

    internal static async Task<string?> ReadAdditivePromptAsync(string repository, string? customPath, CancellationToken token)
    {
        var parts = new List<string>();
        try
        {
            var path = Path.Combine(repository, ".abacus", "continuation.md");
            if (File.Exists(path)) parts.Add(await File.ReadAllTextAsync(path, token));
            if (customPath is not null) parts.Add(await File.ReadAllTextAsync(customPath, token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new PreflightException($"Cannot read continuation policy: {ex.Message}"); }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    internal static string RenderPrompt(string runId, string completionPath, TargetRegistry? targets, string? additive, bool forced = false) => $$"""
        You are Abacus's optional continuation supervisor, acting on the user's explicit policy below.
        {{(forced ? "This run was explicitly requested by the operator, regardless of epic backlog. Inspect existing epics with bd list --type epic --all --limit 0 --json and avoid duplicate or conflicting work." : "The repository currently has no unfinished epics, including blocked epics. Recheck with bd list --type epic --all --limit 0 --json before planning; if unfinished epics appeared, stop.")}}
        This is one bounded planning session, not an endless development loop. Do not launch background
        processes, other supervisors, or edit Abacus runtime files or your own policy.
        Read repository instructions, existing specs, closed history, and current tasks before proposing work.
        Follow the user's policy to develop specs and create useful new epics/tasks with clear definitions
        of done, dependencies, and target/reasoning metadata. Avoid duplicates, filler, and speculative scope.
        Without an actionable user policy, make no project decisions or changes; report that more direction
        is needed and complete this session. Do not invent a roadmap just to keep agents busy.
        Use the installed abacus-beads-planner skill and abacus targets check for newly created tickets.
        Explicit user-authored policy authorizing autonomous graph creation counts as scoped standing
        approval for that skill; merely enabling continuation or approving a concept does not.
        Do not wait for fresh interactive approval for actions already explicitly authorized. Report
        unresolved decisions outside that scope without inventing authorization or expanding the plan.
        Do not claim normal implementation work or reopen closed epics merely to trigger another cycle.
        Preserve user changes, active claims/worktrees, and merge-slot ownership. Never git clean or reset
        another workspace. Git pushes are prohibited unless explicitly authorized by the user policy.
        Local Git operations needed for authorized specification/planning work are permitted, overriding
        blanket bd prime denials, subject to more restrictive repository/user instructions.
        Do planning edits on a separate branch/worktree, not directly on a target branch.
        Do not borrow a managed pool slot for planning, even if it looks idle. If a separate planning
        worktree is needed, keep it outside the managed pool; never register it by editing the pool manifest. Before merging,
        resolve the configured destination and follow its effective merge procedure below. Substitute the
        actual planning issue ID/branch; never redirect an existing ticket's bound target or execution metadata.
        Keep newly drafted work non-ready from creation, then blocked until prerequisite specs are integrated.
        Do not assume bd create supports --status: use the installed planner skill's verified deferred-create
        staging sequence when needed, never create open and block later while workers can claim.
        Publish actionable tickets
        only after their required specification is available to workers. Do not expose half-written plans.
        Finish all edits, checks, commits, integration, and merge-slot release before signalling completion.
        If safe integration is not possible, preserve the branch, report the blocker, and leave actionable
        follow-up work rather than overwriting other work. Synchronize Beads if it has a remote.

        {{Prompt.PoolSafetyInstructions}}

        {{Prompt.RenderSupervisorMergeInstructions(targets)}}

        User-authored continuation policy (.abacus/continuation.md first, then --continuation-prompt-file):
        {{additive ?? "(absent; no project changes authorized)"}}

        Completion protocol: after this bounded attempt, atomically
        write a UTF-8 JSON object to this absolute path: {{JsonSerializer.Serialize(completionPath)}}
        with this runId and a concise summary of specs, created epic IDs, or why no work was created:
        {{JsonSerializer.Serialize(new { runId, summary = "Continuation finished; describe actual results" })}}
        Write a sibling temporary file then rename it into place. Then wait for Abacus to stop the harness.
        Abacus independently rechecks epics. A completion message is not evidence that new work exists.
        """;
}

public sealed class ContinuationDeferredException() : Exception("continuation no longer eligible");
