namespace Abacus;

public sealed record PreparedClaim(BeadsIssue Issue, string Branch, string? Model = null, string? Effort = null);

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
    string? defaultEffort = null)
{
    private readonly DispatchFilters filters = dispatchFilters ?? DispatchFilters.Empty;
    private readonly ClaimGate claimsAllowed = claimGate ?? new ClaimGate();
    private readonly InitialClaimBarrier initialRecovery = initialClaimBarrier ?? new InitialClaimBarrier(1);
    private readonly IReadOnlyDictionary<string, string> modelMappings = reasoningModels
        ?? new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> effortMappings = reasoningEfforts
        ?? new Dictionary<string, string>(StringComparer.Ordinal);
    private bool initialRecoveryPending = true;
    public TimeSpan PollingInterval { get; } = pollingInterval ?? TimeSpan.FromSeconds(5);

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
            await WaitForClaimPermissionAsync(agent.Name, cancellationToken);
            await log.SetAgentAsync(agent.Name, AgentActivity.Waiting, "Looking for a ready ticket");
            if (!Directory.Exists(agent.WorkspacePath))
            {
                LeaveInitialRecoveryPhase();
                throw new StartupInvariantException(
                    $"[{agent.Name}] workspace disappeared: '{agent.WorkspacePath}'");
            }

            bool workspaceIsClean;
            string? interruptedIssueId = null;
            try
            {
                workspaceIsClean = await git.IsWorkspaceCleanAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    cancellationToken);
                if (!workspaceIsClean)
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

                    await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Beads pull failed; retrying soon");
                    await Task.Delay(PollingInterval, cancellationToken);
                    continue;
                }
            }

            BeadsIssue? issue;
            try
            {
                await WaitForClaimPermissionAsync(agent.Name, cancellationToken);
                if (interruptedIssueId is not null)
                {
                    issue = await beads.ResumeOpenIssueAsync(
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
                            agent.WorkspacePath, agent.Name, candidate.Id, cancellationToken));
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
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
                }

                await log.SetAgentAsync(agent.Name, AgentActivity.Retrying, "Could not claim work; retrying soon");
                await Task.Delay(PollingInterval, cancellationToken);
                continue;
            }

            if (issue is null)
            {
                var idleDetail = executionMode is ExecutionMode.Continuous
                    ? "No ready tickets; checking again soon"
                    : "No ready tickets; finite run is complete";
                await log.SetAgentAsync(agent.Name, AgentActivity.Idle, idleDetail);
                if (executionMode is not ExecutionMode.Continuous)
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
                    issue = await BindTargetAsync(agent, issue, interruptedIssueId is not null, cancellationToken);
                await log.SetTicketAsync(agent.Name, issue.Id, issue.Title);
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Preparing,
                    $"{issue.Id} • preparing workspace and branch");
                var branch = interruptedIssueId is not null
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
                return new PreparedClaim(issue, branch, modelResolution?.Model, modelResolution?.Effort);
            }
            catch (AgentHaltedException) { throw; }
            catch (ReasoningLabelException exception)
            {
                await RejectReasoningAsync(agent, issue, exception.Message, cancellationToken);
                if (interruptedIssueId is not null)
                    await HaltAsync(agent.Name, $"{issue.Id}: {exception.Message}. Workspace preserved.");
                await log.ClearTicketAsync(agent.Name);
                if (executionMode is ExecutionMode.Once) return null;
            }
            catch (TargetException exception)
            {
                await RejectTargetAsync(agent, issue, exception.Message, cancellationToken);
                if (interruptedIssueId is not null)
                    await HaltAsync(agent.Name, $"{issue.Id}: {exception.Message}. Workspace preserved.");
                await log.ClearTicketAsync(agent.Name);
                if (executionMode is ExecutionMode.Once) return null;
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
                if (executionMode is not ExecutionMode.Continuous)
                {
                    throw;
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
            modelResolution?.Effort);
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
            defaultEffort ?? throw new ReasoningPolicyException("the default effort is unavailable"));
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

    private async Task WaitForClaimPermissionAsync(
        string agentName,
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
    DesktopNotifier? notifier = null)
{
    private readonly AgentControl control = agentControl;
    private readonly Git workspaceGit = git;
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
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Stopped,
                        "Stopped by operator; press Enter and choose Restart to resume");
                    action = await control.WaitForRequestedActionAsync(cancellationToken);
                    continue;
                }

                if (action is AgentControlAction.Restart)
                {
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
                    claims.LeaveInitialRecoveryPhase();
                    action = control.TakeRequestedAction() ?? AgentControlAction.Stop;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (suspendedIssue is not null)
            {
                await RecoverClaimAsync(
                    suspendedIssue,
                    $"Abacus shut down while {agent.Name} was stopped");
                suspendedIssue = null;
            }

            await log.SetAgentAsync(agent.Name, AgentActivity.Stopped, "Shutting down");
            throw;
        }
    }

    private async Task RunActiveAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
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
                    if (reserved is not null && executionMode is ExecutionMode.Continuous)
                    {
                        continue;
                    }

                    var detail = executionMode is ExecutionMode.Once
                        ? "No executable ticket; once complete"
                        : "No ready tickets; drain complete";
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
                await log.SetModelAsync(agent.Name, resolvedModel, resolvedEffort);
                try
                {
                    var launchAgent = agent.Targets is null ? agent : agent with
                    {
                        MergeInstructionsOverride = agent.Targets.Validate(claim.Issue).MergeInstructions,
                    };
                    run = await agentHost.StartAgentAsync(
                        launchAgent,
                        claim.Issue,
                        resolvedModel,
                        resolvedEffort,
                        serverUrl,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
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
                catch (Exception exception)
                {
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
                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Working,
                    $"{claim.Issue.Id} • agent CLI running");
                try
                {
                    await supervisor.SuperviseAsync(agent, claim.Issue, run, cancellationToken);
                }
                catch (OperationCanceledException) when (control.ShouldPreserveClaimOnInterruption)
                {
                    suspendedIssue = claim.Issue;
                    throw;
                }

                await log.SetAgentAsync(
                    agent.Name,
                    AgentActivity.Finalizing,
                    $"{claim.Issue.Id} • session finished");
                if (executionMode is ExecutionMode.Once)
                {
                    await log.SetAgentAsync(
                        agent.Name,
                        AgentActivity.Stopped,
                        "One ticket processed; once complete");
                    return;
                }
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

    private async Task<bool> CleanWorkspaceAsync(CancellationToken cancellationToken)
    {
        try
        {
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
            await log.SystemAsync($"{agent.Name} workspace cleaned with git reset --hard and git clean -fd");
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
