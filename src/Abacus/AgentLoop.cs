namespace Abacus;

public sealed record PreparedClaim(
    BeadsIssue Issue,
    string Branch,
    string? Model = null,
    string? Effort = null,
    IReadOnlyList<string>? ExtraArguments = null);

public sealed partial class ClaimCoordinator(
    Beads beads,
    Git git,
    TicketRecovery recovery,
    TextWriter log,
    TimeSpan? pollingInterval = null,
    RunSummary? summary = null,
    DispatchFilters? dispatchFilters = null,
    DesktopNotifier? notifier = null,
    ClaimGate? claimGate = null,
    InitialClaimBarrier? initialClaimBarrier = null,
    IReadOnlyDictionary<string, string>? reasoningModels = null,
    string? defaultModel = null,
    IReadOnlyDictionary<string, string>? reasoningEfforts = null,
    string? defaultEffort = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? reasoningArguments = null,
    IReadOnlyList<string>? defaultArguments = null,
    ClaimSchedule? schedule = null,
    TimeProvider? clock = null,
    MaintenanceSupervisor? maintenance = null)
{
    private readonly DispatchFilters filters = dispatchFilters ?? DispatchFilters.Empty;
    private readonly ClaimGate claimsAllowed = claimGate ?? new ClaimGate();
    private readonly InitialClaimBarrier initialRecovery = initialClaimBarrier ?? new InitialClaimBarrier(1);
    private readonly IReadOnlyDictionary<string, string> modelMappings = reasoningModels
        ?? new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> effortMappings = reasoningEfforts
        ?? new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> argumentMappings = reasoningArguments
        ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    private readonly IReadOnlyList<string> defaultArguments = defaultArguments ?? AgentArguments.Empty;
    private readonly ClaimSchedule? claimSchedule = schedule;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private bool initialRecoveryPending = true;
    public TimeSpan PollingInterval { get; } = pollingInterval ?? TimeSpan.FromSeconds(5);

    /// <summary>
    /// Set when a finite run stopped claiming because the schedule blocked it. The
    /// text explains why, so the run can report a deferral instead of a drained queue.
    /// </summary>
    public string? DeferredReason { get; private set; }

    public async Task<PreparedClaim> WaitForPreparedClaimAsync(
        ValidatedAgent agent,
        bool singleAgentMode,
        CancellationToken cancellationToken) =>
        await WaitForPreparedClaimAsync(
            agent,
            singleAgentMode,
            ExecutionMode.Continuous,
            cancellationToken)
        ?? throw new InvalidOperationException("continuous claim polling returned without a ticket");

    public async Task<PreparedClaim?> WaitForPreparedClaimAsync(
        ValidatedAgent agent,
        bool singleAgentMode,
        ExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        await log.ClearTicketAsync(agent.Name);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForClaimPermissionAsync(agent.Name, executionMode, cancellationToken);
            if (DeferredReason is not null) return null;
            await log.SetAgentAsync(agent.Name, AgentActivity.Waiting, "Looking for a ready ticket");
            if (!Directory.Exists(agent.WorkspacePath))
            {
                LeaveInitialRecoveryPhase();
                throw new StartupInvariantException(
                    $"[{agent.Name}] workspace disappeared: '{agent.WorkspacePath}'");
            }

            bool workspaceIsClean;
            string? interruptedIssueId = agent.PoolAssignment?.RecoveryIssueId;
            var preserveBranch = false;
            try
            {
                workspaceIsClean = await git.IsWorkspaceCleanAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    cancellationToken);
                if (agent.PoolAssignment?.RecoveryIssueId is { } recovered)
                {
                    var branch = (await git.GetWorkspaceStatusAsync(agent.WorkspacePath, agent.Name, cancellationToken)).Branch;
                    preserveBranch = branch == $"abacus/{recovered}";
                    if (!preserveBranch && !workspaceIsClean)
                        await HaltAsync(agent.Name, "Pool assignment branch changed; workspace preserved");
                }
                else if (!workspaceIsClean)
                {
                    var currentBranch = await git.GetCurrentBranchAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        cancellationToken);
                    if (!Git.TryGetIssueId(currentBranch, out interruptedIssueId))
                    {
                        LeaveInitialRecoveryPhase();
                        await HaltAsync(
                            agent.Name,
                            $"Workspace '{agent.WorkspacePath}' has uncommitted changes on branch " +
                            $"'{currentBranch}'. The changes were preserved; resolve them before restarting this agent.");
                    }

                    preserveBranch = true;
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{interruptedIssueId} • preserving interrupted workspace");
                }
            }
            catch (WorkspacePreparationException exception)
            {
                LeaveInitialRecoveryPhase();
                throw new StartupInvariantException(
                    $"[{agent.Name}] could not inspect workspace '{agent.WorkspacePath}': {exception.Message}");
            }

            if (workspaceIsClean)
            {
                await WaitForInitialRecoveryPhaseAsync(agent.Name, cancellationToken);
            }

            if (singleAgentMode && agent.HasRemote)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Syncing, "Pulling the latest Beads data");
                var pull = await beads.PullAsync(agent.WorkspacePath, agent.Name, cancellationToken);
                if (!pull.Succeeded)
                {
                    var detail = Beads.FailureDetail(pull);
                    await WarnAsync(agent.Name, $"Beads pull failed; claim delayed: {detail}");
                    if (executionMode is not ExecutionMode.Continuous)
                    {
                        throw new BeadsException($"Beads pull failed during finite execution: {detail}");
                    }

                    if (maintenance is not null)
                    {
                        await maintenance.FailedAsync(agent.Name, $"Beads pull failed: {detail}", cancellationToken);
                        continue;
                    }
                    await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Beads pull failed; retrying soon");
                    await Task.Delay(PollingInterval, cancellationToken);
                    continue;
                }
            }

            BeadsIssue? issue;
            try
            {
                // Re-checked after workspace inspection: a window can close during it.
                await WaitForClaimPermissionAsync(agent.Name, executionMode, cancellationToken);
                if (DeferredReason is not null) return null;
                if (interruptedIssueId is not null)
                {
                    issue = agent.PoolAssignment is { } assignment
                        ? await beads.ResumePoolIssueAsync(agent.WorkspacePath, agent.Name, interruptedIssueId,
                            assignment.PreviousAgentName, cancellationToken)
                        : await beads.ResumeOpenIssueAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        interruptedIssueId,
                        cancellationToken);
                    await WarnAsync(
                        agent.Name,
                        $"resuming {interruptedIssueId} with its uncommitted workspace changes preserved");
                    await WaitForInitialRecoveryPhaseAsync(agent.Name, cancellationToken);
                }
                else
                {
                    issue = await beads.TryClaimReadyAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        filters,
                        cancellationToken,
                        agent.Targets is null ? null : candidate => IsTargetEligible(agent, candidate),
                        candidate => git.CanUseIssueBranchAsync(
                            agent.WorkspacePath, agent.Name, candidate.Id, cancellationToken),
                        candidate => agent.PoolAssignment?.TryClaiming(candidate.Id) ?? true,
                        () => agent.PoolAssignment?.ClaimContended(), includeAssigned: agent.PoolAssignment is null);
                }
            }
            catch (Exception exception) when (exception is BeadsException or WorkspacePreparationException or PreflightException)
            {
                if (interruptedIssueId is not null)
                {
                    LeaveInitialRecoveryPhase();
                    await HaltAsync(
                        agent.Name,
                        $"Could not safely resume {interruptedIssueId}: {exception.Message}. " +
                        "The workspace changes were preserved.");
                }

                await WarnAsync(agent.Name, exception.Message);
                if (agent.PoolAssignment is not null) throw;
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                if (maintenance is not null)
                {
                    await maintenance.FailedAsync(agent.Name, exception.Message, cancellationToken);
                    continue;
                }
                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Could not claim work; retrying soon");
                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            if (issue is null)
            {
                maintenance?.Healthy(agent.Name);
                if (maintenance is not null) await log.ClearPersistentAlertAsync(agent.Name);
                var idleDetail = executionMode is ExecutionMode.Continuous
                    ? "No ready tickets; checking again soon"
                    : "No ready tickets; finite run is complete";
                await log.SetAgentAsync(agent.Name, AgentActivity.Idle, idleDetail);
                if (executionMode is not ExecutionMode.Continuous || agent.PoolAssignment is not null)
                {
                    return null;
                }

                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            try
            {
                ModelResolution? modelResolution = null;
                if (agent.Reasoning is not null)
                    modelResolution = await ResolveReasoningAsync(agent, issue, cancellationToken);
                if (agent.Targets is not null)
                    issue = await BindTargetAsync(agent, issue, preserveBranch, cancellationToken);
                await log.SetTicketAsync(agent.Name, issue.Id, issue.Title);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Preparing,
                    $"{issue.Id} • preparing workspace and branch");
                var branch = preserveBranch
                    ? $"abacus/{interruptedIssueId}"
                    : await git.PrepareIssueBranchAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        issue.Id,
                        cancellationToken,
                        issue.Binding?.StartCommit);
                if (agent.Targets is not null)
                {
                    var verified = await beads.GetIssueAsync(agent.WorkspacePath, agent.Name, issue.Id, cancellationToken)
                        ?? throw new TargetException("ticket disappeared after branch preparation");
                    agent.Targets.Validate(verified);
                    if (verified.Status != IssueStatus.InProgress || verified.Assignee != agent.Name
                        || verified.Binding != issue.Binding || verified.TargetBranch != issue.TargetBranch)
                        throw new TargetException("ticket target, binding, or ownership changed during branch preparation");
                    await git.VerifyBoundHistoryAsync(agent.WorkspacePath, agent.Name,
                        issue.Binding!.StartCommit, branch, cancellationToken);
                    issue = verified;
                }
                agent.PoolAssignment?.Prepared();
                return new PreparedClaim(issue, branch, modelResolution?.Model, modelResolution?.Effort,
                    modelResolution?.ExtraArguments);
            }
            catch (AgentHaltedException) { throw; }
            catch (ReasoningLabelException exception)
            {
                await RejectReasoningAsync(agent, issue, exception.Message, cancellationToken);
                if (interruptedIssueId is not null)
                    await HaltAsync(agent.Name, $"{issue.Id}: {exception.Message}. Workspace preserved.");
                await log.ClearTicketAsync(agent.Name);
                if (executionMode is ExecutionMode.Once || agent.PoolAssignment is not null) return null;
            }
            catch (TargetException exception)
            {
                await RejectTargetAsync(agent, issue, exception.Message, cancellationToken);
                if (interruptedIssueId is not null)
                    await HaltAsync(agent.Name, $"{issue.Id}: {exception.Message}. Workspace preserved.");
                await log.ClearTicketAsync(agent.Name);
                if (executionMode is ExecutionMode.Once || agent.PoolAssignment is not null) return null;
            }
            catch (OperationCanceledException)
            {
                await RecoverClaimAsync(
                    agent,
                    issue,
                    $"Abacus shut down while preparing the workspace for {agent.Name}",
                    CancellationToken.None);
                summary?.Record(agent.Name, TicketOutcome.Interrupted, issue.Id, issue.Title);
                throw;
            }
            catch (Exception exception)
            {
                var note = $"Abacus could not prepare the workspace for {agent.Name}: {exception.Message}";
                await log.SetAgentAsync(agent.Name, AgentActivity.Recovering, $"{issue.Id} • reopening ticket");
                await WarnAsync(agent.Name, note);
                await RecoverClaimAsync(agent, issue, note, cancellationToken);
                if (agent.PoolAssignment is not null) throw;
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                if (maintenance is not null)
                {
                    await maintenance.FailedAsync(agent.Name, exception.Message, cancellationToken);
                    continue;
                }
                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Workspace preparation failed; retrying soon");
                await Task.Delay(PollingInterval, cancellationToken);
                await log.ClearTicketAsync(agent.Name);
            }
        }
    }

    public async Task<PreparedClaim?> ResumeReservedClaimAsync(
        ValidatedAgent agent,
        BeadsIssue reservedIssue,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(agent.WorkspacePath))
        {
            throw new StartupInvariantException(
                $"[{agent.Name}] workspace disappeared: '{agent.WorkspacePath}'");
        }

        var expectedBranch = $"abacus/{reservedIssue.Id}";
        var currentBranch = await git.GetCurrentBranchAsync(
            agent.WorkspacePath,
            agent.Name,
            cancellationToken);
        if (!string.Equals(currentBranch, expectedBranch, StringComparison.Ordinal))
        {
            await HaltAsync(
                agent.Name,
                $"Could not restart reserved ticket {reservedIssue.Id}: workspace is on " +
                $"'{currentBranch}', expected '{expectedBranch}'. The workspace was preserved.");
        }

        var current = await beads.GetIssueAsync(
            agent.WorkspacePath,
            agent.Name,
            reservedIssue.Id,
            cancellationToken)
            ?? throw new BeadsException($"reserved issue '{reservedIssue.Id}' no longer exists");
        if (current.Status is IssueStatus.Closed or IssueStatus.Blocked)
        {
            var outcome = current.Status is IssueStatus.Closed
                ? TicketOutcome.Closed
                : TicketOutcome.Blocked;
            summary?.Record(
                agent.Name,
                outcome,
                current.Id,
                current.Title ?? reservedIssue.Title);
            await log.ClearTicketAsync(agent.Name);
            return null;
        }

        BeadsIssue resumed;
        if (current.Status is IssueStatus.Open)
        {
            if (!string.IsNullOrWhiteSpace(current.Assignee)
                && !string.Equals(current.Assignee, agent.Name, StringComparison.Ordinal))
            {
                await HaltAsync(
                    agent.Name,
                    $"Could not restart reserved ticket {reservedIssue.Id}: it is assigned to " +
                    $"'{current.Assignee}'. The workspace was preserved.");
            }

            resumed = await beads.ResumeOpenIssueAsync(
                agent.WorkspacePath,
                agent.Name,
                reservedIssue.Id,
                cancellationToken);
        }
        else if (current.Status is IssueStatus.InProgress
                 && string.Equals(current.Assignee, agent.Name, StringComparison.Ordinal))
        {
            resumed = current;
        }
        else
        {
            var detail = current.Status is IssueStatus.InProgress
                ? $"it is assigned to '{current.Assignee ?? "(nobody)"}'"
                : $"its status is {current.Status.ToString().ToLowerInvariant()}";
            await HaltAsync(
                agent.Name,
                $"Could not restart reserved ticket {reservedIssue.Id}: {detail}. " +
                "The workspace was preserved.");
            return null;
        }

        ModelResolution? modelResolution = null;
        if (agent.Reasoning is not null)
        {
            try
            {
                modelResolution = await ResolveReasoningAsync(agent, resumed, cancellationToken);
            }
            catch (ReasoningLabelException exception)
            {
                await RejectReasoningAsync(agent, resumed, exception.Message, cancellationToken);
                await HaltAsync(agent.Name, $"{resumed.Id}: {exception.Message}. Workspace preserved.");
            }
        }

        if (agent.Targets is not null)
        {
            try { resumed = await BindTargetAsync(agent, resumed, requireExisting: true, cancellationToken); }
            catch (TargetException exception)
            {
                await RejectTargetAsync(agent, resumed, exception.Message, cancellationToken);
                await HaltAsync(agent.Name, $"{resumed.Id}: {exception.Message}. Workspace preserved.");
            }
        }
        return new PreparedClaim(
            resumed with { Title = resumed.Title ?? reservedIssue.Title },
            expectedBranch,
            modelResolution?.Model,
            modelResolution?.Effort,
            modelResolution?.ExtraArguments);
    }

    private async Task<ModelResolution> ResolveReasoningAsync(
        ValidatedAgent agent,
        BeadsIssue claim,
        CancellationToken token)
    {
        var current = await beads.GetIssueAsync(agent.WorkspacePath, agent.Name, claim.Id, token)
            ?? throw new ReasoningLabelException($"ticket '{claim.Id}' disappeared after claim");
        if (current.Status != IssueStatus.InProgress || current.Assignee != agent.Name)
            throw new ReasoningLabelException("ticket ownership changed after claim");
        return agent.Reasoning!.ResolveModel(
            current,
            modelMappings,
            defaultModel ?? throw new ReasoningPolicyException("the default model is unavailable"),
            effortMappings,
            defaultEffort ?? throw new ReasoningPolicyException("the default effort is unavailable"),
            argumentMappings,
            defaultArguments);
    }

    private async Task RejectReasoningAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string detail,
        CancellationToken token)
    {
        var reason = $"Abacus reasoning-label validation failed: {detail}. "
            + $"Remove all but at most one of {string.Join(", ", ReasoningPolicy.Labels)}; "
            + "when enforcement is enabled, keep exactly one. "
            + $"Then run abacus attention resolve {issue.Id} --reopen after review.";
        try
        {
            await beads.BlockReasoningIssueAsync(
                agent.WorkspacePath, agent.Name, issue.Id, reason, token);
            summary?.Record(agent.Name, TicketOutcome.Blocked, issue.Id, issue.Title);
            await WarnAsync(agent.Name, reason);
            if (await recovery.PushWithRetryAsync(agent, token) == PushOutcome.Failed)
                await HaltAsync(agent.Name, $"Could not synchronize reasoning-label block for {issue.Id}");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or AgentHaltedException))
        {
            await HaltAsync(
                agent.Name,
                $"Could not safely block {issue.Id}: {exception.Message}. Workspace preserved.");
        }
    }

    internal async Task WaitForClaimPermissionAsync(
        string agentName,
        ExecutionMode executionMode,
        CancellationToken cancellationToken)
    {
        if (!claimsAllowed.IsEnabled)
        {
            await log.SetAgentAsync(
                agentName,
                AgentActivity.Paused,
                "New ticket claims paused; press Shift-Tab to resume");
        }

        await claimsAllowed.WaitUntilEnabledAsync(cancellationToken);

        // The schedule is a second, independent gate: pausing or resuming claims by
        // hand never bypasses it. Continuous runs wait for the next window; finite
        // runs stop claiming and report the deferral.
        while (true)
        {
            var now = clock.GetUtcNow();
            if (claimSchedule is null || claimSchedule.CanClaimAt(now, out _)) return;
            var detail = $"Schedule: {claimSchedule.DescribeAt(now)}";
            if (executionMode is not ExecutionMode.Continuous)
            {
                DeferredReason = $"{detail}; deferred without claiming";
                LeaveInitialRecoveryPhase();
                return;
            }

            await log.SetAgentAsync(agentName, AgentActivity.Paused, detail);
            var wait = claimSchedule.NextClaimableAt(now) is { } opening ? opening - now : PollingInterval;
            if (wait <= TimeSpan.Zero || wait > PollingInterval) wait = PollingInterval;
            await Task.Delay(wait, cancellationToken);
        }
    }

    private async Task WaitForInitialRecoveryPhaseAsync(
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!initialRecoveryPending)
        {
            return;
        }

        initialRecoveryPending = false;
        await log.SetAgentAsync(
            agentName,
            AgentActivity.Waiting,
            "Waiting for interrupted workspaces to reserve their tickets");
        initialRecovery.Arrive();
        await initialRecovery.WaitAsync(cancellationToken);
    }

    internal void LeaveInitialRecoveryPhase()
    {
        if (!initialRecoveryPending)
        {
            return;
        }

        initialRecoveryPending = false;
        initialRecovery.Arrive();
    }

    private Task WarnAsync(string agentName, string message) =>
        log.WarningAsync(agentName, message);

    private async Task RecoverClaimAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string note,
        CancellationToken cancellationToken)
    {
        var result = await recovery.ReopenKnownClaimAsync(agent, issue.Id, note, cancellationToken);
        if (result.Outcome is RecoveryOutcome.Failed)
        {
            await HaltAsync(agent.Name, $"Could not reopen and verify {issue.Id}; no more work will be claimed");
        }

        if (result.Outcome is RecoveryOutcome.Reopened)
        {
            summary?.Record(agent.Name, TicketOutcome.Reopened, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is IssueStatus.Closed)
        {
            summary?.Record(agent.Name, TicketOutcome.Closed, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is IssueStatus.Open)
        {
            summary?.Record(agent.Name, TicketOutcome.Reopened, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is IssueStatus.Blocked)
        {
            summary?.Record(agent.Name, TicketOutcome.Blocked, issue.Id, issue.Title);
        }

        if (await recovery.PushWithRetryAsync(agent, CancellationToken.None) is PushOutcome.Failed)
        {
            await HaltAsync(agent.Name, "All Beads push attempts failed; no more work will be claimed");
        }
    }

    private async Task HaltAsync(string agentName, string message)
    {
        await WarnAsync(agentName, message);
        await log.SetPersistentAlertAsync(agentName, message);
        notifier?.PersistentAlert(agentName, message);
        throw new AgentHaltedException($"[{agentName}] {message}");
    }
}

public sealed class AgentLoop(
    ValidatedAgent agent,
    bool singleAgentMode,
    string model,
    string effort,
    string? serverUrl,
    ClaimCoordinator claims,
    IAgentHost agentHost,
    TicketSupervisor supervisor,
    TicketRecovery recovery,
    ExecutionMode executionMode,
    RunSummary summary,
    TextWriter log,
    Git git,
    AgentControl agentControl,
    AgentRunRegistry? agentRuns = null,
    DesktopNotifier? notifier = null,
    IReadOnlyList<string>? defaultArguments = null,
    MaintenanceSupervisor? maintenance = null,
    ContinuationSupervisor? continuation = null,
    WorktreePool? pool = null,
    Beads? poolBeads = null)
{
    private ValidatedAgent agent = agent;
    private readonly string workerId = Guid.NewGuid().ToString("N");
    private PoolAssignment? assignment;
    private readonly AgentControl control = agentControl;
    private readonly Git workspaceGit = git;
    private readonly AgentRunRegistry runs = agentRuns ?? new AgentRunRegistry();
    private readonly IReadOnlyList<string> extraArguments = defaultArguments ?? AgentArguments.Empty;
    private BeadsIssue? suspendedIssue;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AgentControlAction? action = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action ??= control.TakeRequestedAction();
                if (action is AgentControlAction.CleanWorkspace)
                {
                    maintenance?.OperatorStopped(agent.Name);
                    var cleaned = await CleanWorkspaceAsync(cancellationToken);
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Stopped,
                        cleaned
                            ? "Workspace cleaned; press Enter and choose Restart to resume"
                            : "Workspace cleanup failed; review the persistent alert before retrying");
                    action = await control.WaitForRequestedActionAsync(cancellationToken);
                    continue;
                }

                if (action is AgentControlAction.Stop)
                {
                    maintenance?.OperatorStopped(agent.Name);
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Stopped,
                        "Stopped by operator; press Enter and choose Restart to resume");
                    action = await control.WaitForRequestedActionAsync(cancellationToken);
                    continue;
                }

                if (action is AgentControlAction.Restart)
                {
                    maintenance?.OperatorStopped(agent.Name);
                    await log.ClearPersistentAlertAsync(agent.Name);
                    await log.SetAgentAsync(agent.Name, AgentActivity.Starting, "Restart requested by operator");
                }

                action = null;
                using var operation = control.CreateOperationCancellation(cancellationToken);
                try
                {
                    await RunActiveAsync(operation.Token);
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (assignment?.Record.Phase == "execution-uncertain")
                        throw new SupervisorCleanupException("interrupted pool launch/cleanup may have left execution alive; stop the controller and confirm execution shutdown before retrying");
                    claims.LeaveInitialRecoveryPhase();
                    if (suspendedIssue is null) ReleaseWorkspace();
                    action = control.TakeRequestedAction() ?? AgentControlAction.Stop;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (suspendedIssue is not null && assignment?.Record.Phase != "execution-uncertain")
            {
                await RecoverClaimAsync(
                    suspendedIssue,
                    $"Abacus shut down while {agent.Name} was stopped");
                suspendedIssue = null;
            }

            await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Shutting down");
            throw;
        }
        finally { ReleaseWorkspace(); }
    }

    private async Task RunActiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (pool is not null && assignment is null)
                {
                    await claims.WaitForClaimPermissionAsync(agent.Name, executionMode, cancellationToken);
                    if (claims.DeferredReason is not null) return;
                    assignment = await pool.AcquireAssignmentAsync(agent, poolBeads!, workerId, cancellationToken);
                    if (assignment is null)
                    {
                        claims.LeaveInitialRecoveryPhase();
                        var message = "No safe available pool slot; inspect abacus worktrees list (unfinished work preserved)";
                        await log.SetPersistentAlertAsync(agent.Name, message);
                        if (executionMode is not ExecutionMode.Continuous) throw new StartupInvariantException(message);
                        await Task.Delay(claims.PollingInterval, cancellationToken);
                        continue;
                    }
                    agent = pool.AssignWorker(agent, assignment);
                    // Use the preflight-validated policy and identity of the actual leased checkout.
                    if (log is ConsoleOutput output) await output.SetWorkspacePathAsync(agent.Name, agent.WorkspacePath);
                    await log.SystemAsync($"{agent.Name} leased {assignment.Slot.Name}: {agent.WorkspacePath}");
                }
                var reserved = suspendedIssue;
                var claim = reserved is not null
                    ? await claims.ResumeReservedClaimAsync(agent, reserved, cancellationToken)
                    : await claims.WaitForPreparedClaimAsync(
                        agent,
                        singleAgentMode,
                        executionMode,
                        cancellationToken);
                suspendedIssue = null;
                if (claim is null)
                {
                    ReleaseWorkspace();
                    if (executionMode is ExecutionMode.Continuous && pool is not null)
                    {
                        await Task.Delay(claims.PollingInterval, cancellationToken);
                        continue;
                    }
                    if (reserved is not null && executionMode is ExecutionMode.Continuous)
                    {
                        continue;
                    }

                    maintenance?.Healthy(agent.Name);
                    if (maintenance is not null && claims.DeferredReason is null
                        && await maintenance.CheckFiniteCompletionAsync(agent.Name, cancellationToken)) continue;
                    if (continuation is not null && claims.DeferredReason is null
                        && await continuation.CheckFiniteCompletionAsync(agent.Name, cancellationToken)) continue;
                    var detail = claims.DeferredReason
                        ?? (executionMode is ExecutionMode.Once
                            ? "No executable ticket; once complete"
                            : "No ready tickets; drain complete");
                    await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, detail);
                    return;
                }

                if (reserved is not null)
                {
                    await log.SetTicketAsync(agent.Name, claim.Issue.Id, claim.Issue.Title);
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{claim.Issue.Id} • restarting reserved ticket");
                }

                IAgentRun run;
                var resolvedModel = claim.Model ?? model;
                var resolvedEffort = claim.Effort ?? effort;
                var resolvedArguments = claim.ExtraArguments ?? extraArguments;
                await log.SetModelAsync(agent.Name, resolvedModel, resolvedEffort);
                try
                {
                    var launchAgent = agent.Targets is null ? agent : agent with
                    {
                        MergeInstructionsOverride = agent.Targets.Validate(claim.Issue).MergeInstructions,
                    };
                    if (assignment?.RecoveryIssueId is not null)
                        launchAgent = launchAgent with
                        {
                            AppendedPrompt = Prompt.CombineAppends(launchAgent.AppendedPrompt,
                                "This is a recovered pool assignment, possibly previously run by a differently named worker. " +
                                "Preserve existing work. Inspect the issue history, current Git state, and target ancestry before proceeding. " +
                                "A previous merge or external step may already have succeeded: verify its result rather than blindly " +
                                "repeating merge, push, release, or other non-idempotent custom steps. If completion is ambiguous, " +
                                "request user attention instead of guessing. Apply the effective merge instructions above to remaining work only."),
                        };
                    runs.MarkRunning(agent.Name); // Protect merge ownership throughout launch, not only after the host returns.
                    run = await agentHost.StartAgentAsync(
                        launchAgent,
                        claim.Issue,
                        resolvedModel,
                        resolvedEffort,
                        serverUrl,
                        cancellationToken,
                        resolvedArguments);
                }
                catch (OperationCanceledException)
                {
                    if (assignment?.Record.Phase == "execution-uncertain") throw;
                    runs.MarkStopped(agent.Name);
                    if (control.ShouldPreserveClaimOnInterruption)
                    {
                        suspendedIssue = claim.Issue;
                    }
                    else
                    {
                        await RecoverClaimAsync(
                            claim.Issue,
                            $"Abacus shut down before the agent CLI started for {agent.Name}");
                    }

                    summary.Record(
                        agent.Name,
                        TicketOutcome.Interrupted,
                        claim.Issue.Id,
                        claim.Issue.Title);
                    throw;
                }
                catch (Exception) when (assignment?.Record.Phase == "execution-uncertain") { throw; }
                catch (Exception exception)
                {
                    runs.MarkStopped(agent.Name);
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Recovering,
                        $"{claim.Issue.Id} • agent CLI could not start; reopening ticket");
                    await RecoverClaimAsync(
                        claim.Issue,
                        $"Abacus could not start the agent CLI for {agent.Name}: {exception.Message}");
                    throw;
                }

                await log.SetRunLocationAsync(agent.Name, run.Location);
                runs.MarkRunning(agent.Name);
                maintenance?.Healthy(agent.Name, working: true);
                if (maintenance is not null) await log.ClearPersistentAlertAsync(agent.Name);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Working,
                    $"{claim.Issue.Id} • agent CLI running");
                try
                {
                    try
                    {
                        await supervisor.SuperviseAsync(agent, claim.Issue, run, cancellationToken);
                    }
                    catch (OperationCanceledException) when (control.ShouldPreserveClaimOnInterruption)
                    {
                        suspendedIssue = claim.Issue;
                        throw;
                    }
                }
                finally
                {
                    // Only verified cleanup permits reclaiming this harness's merge slot.
                    // Failed cleanup keeps its holder protected until controller shutdown.
                    if (assignment?.Record.Phase != "execution-uncertain") runs.MarkStopped(agent.Name);
                    await log.ClearRunAsync(agent.Name);
                }

                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Finalizing,
                    $"{claim.Issue.Id} • session finished");
                ReleaseWorkspace();
                if (executionMode is ExecutionMode.Once)
                {
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Stopped,
                        "One ticket processed; once complete");
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (SupervisorRecoveryFailedException) { throw; }
            catch (SupervisorCleanupException) { throw; }
            catch (Exception) when (assignment?.Record.Phase == "execution-uncertain") { throw; }
            catch (Exception exception) when (maintenance is not null)
            {
                claims.LeaveInitialRecoveryPhase();
                ReleaseWorkspace();
                await maintenance.FailedAsync(agent.Name, exception.Message, cancellationToken);
            }
            catch (StartupInvariantException)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Workspace invariant failed");
                throw;
            }
            catch (AgentHaltedException exception)
            {
                await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Persistent recovery failure needs user attention");
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                await log.WarningAsync(agent.Name, exception.Message);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }
            catch (Exception exception)
            {
                ReleaseWorkspace();
                await log.WarningAsync(agent.Name, $"agent loop failed: {exception.Message}");
                if (executionMode is not ExecutionMode.Continuous)
                {
                    await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Finite execution failed");
                    throw;
                }

                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Agent loop failed; retrying soon");
                await Task.Delay(claims.PollingInterval, cancellationToken);
            }
        }
    }

    private void ReleaseWorkspace()
    {
        assignment?.Dispose();
        assignment = null;
        agent = agent with { PoolAssignment = null };
    }

    private async Task<bool> CleanWorkspaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (pool is not null && (assignment is null || assignment.Record.Phase == "execution-uncertain"))
                throw new WorkspacePreparationException("worker has no safely stopped leased workspace; inspect the pool instead of cleaning a previous assignment");
            if (suspendedIssue is not null)
            {
                await RecoverClaimAsync(
                    suspendedIssue,
                    $"Abacus released {suspendedIssue.Id} because {agent.Name}'s workspace was cleaned");
                suspendedIssue = null;
            }

            await log.SetAgentAsync(agent.Name, AgentActivity.Recovering, "Cleaning workspace");
            await workspaceGit.CleanWorkspaceAsync(
                agent.WorkspacePath,
                agent.Name,
                cancellationToken);
            await log.ClearTicketAsync(agent.Name);
            await log.ClearPersistentAlertAsync(agent.Name);
            await log.SystemAsync($"{agent.Name} tracked workspace changes reset; untracked files and ignored caches preserved");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Could not clean workspace '{agent.WorkspacePath}': {exception.Message}";
            await log.WarningAsync(agent.Name, message);
            await log.SetPersistentAlertAsync(agent.Name, message);
            return false;
        }
    }

    private async Task RecoverClaimAsync(BeadsIssue issue, string note)
    {
        var result = await recovery.ReopenKnownClaimAsync(
            agent, issue.Id, note, CancellationToken.None);
        if (result.Outcome is RecoveryOutcome.Failed)
        {
            await HaltAsync($"Could not reopen and verify {issue.Id}; no more work will be claimed");
        }

        if (result.Outcome is RecoveryOutcome.Reopened)
        {
            summary.Record(agent.Name, TicketOutcome.Reopened, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is IssueStatus.Closed)
        {
            summary.Record(agent.Name, TicketOutcome.Closed, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is IssueStatus.Open)
        {
            summary.Record(agent.Name, TicketOutcome.Reopened, issue.Id, issue.Title);
        }
        else if (result.VerifiedStatus is IssueStatus.Blocked)
        {
            summary.Record(agent.Name, TicketOutcome.Blocked, issue.Id, issue.Title);
        }

        if (await recovery.PushWithRetryAsync(agent, CancellationToken.None) is PushOutcome.Failed)
        {
            await HaltAsync("All Beads push attempts failed; no more work will be claimed");
        }
    }

    private async Task HaltAsync(string message)
    {
        await log.WarningAsync(agent.Name, message);
        await log.SetPersistentAlertAsync(agent.Name, message);
        notifier?.PersistentAlert(agent.Name, message);
        throw new AgentHaltedException($"[{agent.Name}] {message}");
    }
}

public sealed class StartupInvariantException(string message) : Exception(message);
