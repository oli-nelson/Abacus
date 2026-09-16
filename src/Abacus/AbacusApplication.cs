namespace Abacus;

/// <summary>How a run ended, so the caller can pick the process exit code.</summary>
public enum RunOutcome
{
    /// <summary>Every loop finished its own work, including a drained ready queue.</summary>
    Completed,

    /// <summary>A finite run stopped claiming because a schedule window was closed.</summary>
    Deferred,
}

public sealed class AbacusApplication(
    CommandRunner runner,
    TextWriter log,
    DesktopNotifier notifier)
{
    public async Task<RunOutcome> RunAsync(PreflightResult preflight, CancellationToken cancellationToken)
    {
        var schedule = preflight.Options.Schedule;
        // A finite run has nothing to wait for, so it never takes workspace locks or
        // creates tmux sessions just to idle until the next window opens.
        var now = TimeProvider.System.GetUtcNow();
        if (schedule is not null
            && preflight.Options.ExecutionMode is not ExecutionMode.Continuous
            && !schedule.CanClaimAt(now, out _))
        {
            await log.SystemAsync($"Schedule: {schedule.DescribeAt(now)}; finite run deferred without claiming");
            return RunOutcome.Deferred;
        }

        var pool = await WorktreePool.OpenAsync(runner, preflight.RepositoryRoot, cancellationToken, preflight.Tools.Git);
        using var poolLease = pool.AcquireLease();
        // An old execution can still own the repository's merge slot or be integrating in the
        // main checkout. Do not let a new controller reclaim its holder or launch supervisors.
        foreach (var slot in pool.ReadManifest()?.Slots ?? [])
            if (pool.ReadAssignment(slot.Name)?.Phase == "execution-uncertain")
                throw new StartupInvariantException($"{slot.Name}: previous execution may still be alive. Stop all surviving harnesses/subprocesses using this slot, then run abacus worktrees recover {slot.Name} --confirm before restarting.");
        if (preflight.Options.ManagedAgentCount is not null)
        {
            var original = preflight.Options;
            var targets = preflight.Agents[0].Targets!;
            var start = await new Git(runner, preflight.Tools.Git).ResolveTargetCommitAsync(
                preflight.RepositoryRoot, "abacus", targets.Targets.ContainsKey(targets.DefaultTarget) ? targets.DefaultTarget : targets.Targets.Keys.First(), cancellationToken);
            await pool.EnsureSlotsAsync(original.ManagedAgentCount!.Value, start, cancellationToken);
            var slots = pool.ReadManifest()!.Slots.Select(s => new AgentOptions(s.Name, s.Path)).ToArray();
            var validatedPool = await new Preflight(runner).RunAsync(original with
                { Agents = slots, ManagedAgentCount = null }, cancellationToken);
            pool.ValidatedSlots = validatedPool.Agents.ToDictionary(a => a.WorkspacePath, StringComparer.Ordinal);

        }

        // New user-attention issues play the bundled attention clip, but only when
        // TUI audio is enabled; the monitor records the snapshot either way.
        await using var attentionSound = new UserAttentionSound(preflight.Options.TuiAudio);
        using var ownership = await WorkspaceOwnership.AcquireAsync(
            new Git(runner, preflight.Tools.Git), preflight.Options.ManagedAgentCount is null ? preflight.Agents : [], cancellationToken);
        var beads = new Beads(runner, preflight.Tools.Bd);
        var baselineAgent = preflight.Agents[0];
        if (preflight.Agents.Count == 1 && baselineAgent.HasRemote)
        {
            await log.SetAgentAsync(
                baselineAgent.Name,
                AgentActivity.Syncing,
                "Pulling Beads before recording the run baseline");
            var pull = await beads.PullAsync(
                baselineAgent.WorkspacePath,
                baselineAgent.Name,
                cancellationToken);
            if (!pull.Succeeded)
            {
                throw new BeadsException(
                    $"failed to pull Beads before recording the run baseline: {Beads.FailureDetail(pull)}");
            }
        }

        var initialDoltCommit = await beads.ReadCurrentDoltCommitAsync(
            baselineAgent.WorkspacePath,
            baselineAgent.Name,
            cancellationToken);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"abacus-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var summary = new RunSummary(
            preflight.Agents.Select(static agent => agent.Name),
            initialDoltCommit,
            notifier,
            (log as ConsoleOutput)?.Events);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var claimGate = new ClaimGate();
        claimGate.SetEnabled(!preflight.Options.StartPaused);
        var initialClaimBarrier = new InitialClaimBarrier(preflight.Agents.Count);
        var agentControls = preflight.Agents.ToDictionary(
            static agent => agent.Name,
            static _ => new AgentControl(),
            StringComparer.Ordinal);
        var agentRuns = new AgentRunRegistry();
        var mergeSlotReclaimer = new MergeSlotReclaimer(beads, log);
        var inputMonitor = Task.CompletedTask;
        var maintenanceMonitor = Task.CompletedTask;
        var continuationMonitor = Task.CompletedTask;
        var controlShutdown = false;
        TmuxSessionLease? tmuxSessionLease = null;
        try
        {
            if (preflight.Options.UsesTmux)
            {
                tmuxSessionLease = await TmuxSessionLease.CreateAsync(
                    runner,
                    log,
                    preflight.Tools.Tmux!,
                    preflight.Options,
                    preflight.RepositoryRoot,
                    cancellationToken);
                await log.SetTmuxTargetAsync(
                    tmuxSessionLease.SessionName,
                    tmuxSessionLease.WindowName);
            }

            await log.SystemAsync("Agent loops started");
            var git = new Git(runner, preflight.Tools.Git);
            var dashboardMonitor = MonitorDashboardAsync(
                beads,
                git,
                mergeSlotReclaimer,
                attentionSound,
                agentRuns,
                preflight.Agents,
                preflight.Options,
                preflight.Options.LatestCommentCount,
                includeLatestComments: log is ConsoleOutput dashboard && (dashboard.IsInteractiveDashboard || dashboard.Events is not null),
                linkedCancellation.Token, preflight.Options.ManagedAgentCount is null ? null : pool);
            IAgentHost agentHost = !preflight.Options.UsesTmux
                ? new DirectOpenCodeServerHost(runner, log, preflight.Tools.AgentExecutable)
                : new TmuxAgentHost(
                    runner,
                    preflight.Tools.Tmux!,
                    preflight.Tools.AgentExecutable,
                    preflight.Options.AgentMode,
                    tmuxSessionLease!.SessionName,
                    temporaryRoot,
                    tmuxLayout: preflight.Options.EffectiveTmuxLayout,
                    remote: preflight.Options.Remote,
                    tmuxWindowId: tmuxSessionLease.WindowId,
                    projectId: tmuxSessionLease.ProjectId);

            if (preflight.Options.ManagedAgentCount is not null) agentHost = new PoolAgentHost(agentHost);

            ContinuationSupervisor? continuation = null;
            var supervisorHost = new SupervisorHost(agentHost, agentRuns)
            {
                WaitingForCheckoutAsync = name => log.SetAgentAsync(name, AgentActivity.Waiting,
                    "Waiting for the other supervisor to release the main checkout"),
                BeforeStartAsync = async (name, token) =>
                {
                    if (name == ContinuationSupervisor.Name && continuation?.IsForcedRun != true && (!claimGate.IsEnabled
                        || (schedule is not null && !schedule.CanClaimAt(TimeProvider.System.GetUtcNow(), out _))
                        || await beads.HasUnfinishedEpicsAsync(preflight.RepositoryRoot, token)))
                        throw new ContinuationDeferredException();
                    await log.SetAgentAsync(name, AgentActivity.Starting, "Main checkout acquired; launching supervisor");
                },
            };
            continuation = preflight.Options.ContinuationModel is null ? null
                : new ContinuationSupervisor(preflight, beads, supervisorHost, log, temporaryRoot,
                    new ContinuationState(), claimGate)
                { StartSound = attentionSound.PlaySupervisorAsync };
            var maintenance = preflight.Options.SupervisorModel is null ? null
                : new MaintenanceSupervisor(preflight, beads, supervisorHost, log, temporaryRoot, pool: pool)
                { StartSound = attentionSound.PlaySupervisorAsync };
            var coordinators = new Dictionary<string, ClaimCoordinator>(StringComparer.Ordinal);
            var loops = preflight.Agents.Select(agent =>
            {
                var recovery = new TicketRecovery(beads, log);
                var claims = new ClaimCoordinator(
                    beads,
                    git,
                    recovery,
                    log,
                    summary: summary,
                    dispatchFilters: preflight.Options.DispatchFilters,
                    notifier: notifier,
                    claimGate: claimGate,
                    initialClaimBarrier: initialClaimBarrier,
                    reasoningModels: preflight.Options.EffectiveReasoningModels,
                    defaultModel: preflight.Options.Model,
                    reasoningEfforts: preflight.Options.EffectiveReasoningEfforts,
                    defaultEffort: preflight.Options.Effort,
                    reasoningArguments: preflight.Options.EffectiveReasoningArguments,
                    defaultArguments: preflight.Options.EffectiveExtraArguments,
                    schedule: schedule,
                    maintenance: maintenance);
                coordinators[agent.Name] = claims;
                var supervisor = new TicketSupervisor(
                    beads,
                    agentHost,
                    recovery,
                    log,
                    summary: summary,
                    ticketTimeout: preflight.Options.TicketTimeout,
                    notifier: notifier,
                    preserveClaimOnCancellation: () =>
                        agentControls[agent.Name].ShouldPreserveClaimOnInterruption);
                return new AgentLoop(
                    agent,
                    preflight.Agents.Count == 1,
                    preflight.Options.Model,
                    preflight.Options.Effort,
                    preflight.OpenCodeServerUrl,
                    claims,
                    agentHost,
                    supervisor,
                    recovery,
                    preflight.Options.ExecutionMode,
                    summary,
                    log,
                    git,
                    agentControls[agent.Name],
                    agentRuns,
                    notifier,
                    preflight.Options.EffectiveExtraArguments, maintenance, continuation,
                    preflight.Options.ManagedAgentCount is null ? null : pool, beads).RunAsync(linkedCancellation.Token);
            }).ToArray();

            if (maintenance is not null)
                maintenanceMonitor = maintenance.RunAsync(() => loops.All(loop => loop.IsCompleted), linkedCancellation.Token);

            if (continuation is not null)
                continuationMonitor = continuation.RunAsync(() => loops.All(loop => loop.IsCompleted), linkedCancellation.Token);

            if (log is ConsoleOutput consoleOutput)
            {
                void RequestAction(string name, AgentControlAction action)
                {
                    if (name == ContinuationSupervisor.Name && continuation is not null)
                    {
                        continuation.Request(action);
                        return;
                    }
                    if (name == MaintenanceSupervisor.Name && maintenance is not null)
                    {
                        maintenance.Request(action);
                        return;
                    }
                    if (!agentControls.TryGetValue(name, out var control))
                        throw new ArgumentException($"unknown agent '{name}'");
                    var index = preflight.Agents.ToList().FindIndex(agent => agent.Name == name);
                    if (loops[index].IsCompleted)
                        throw new InvalidOperationException($"agent '{name}' has finished; start a new run");
                    if (!control.TryRequest(action))
                        throw new InvalidOperationException($"agent '{name}' already has a pending control request");
                }
                void ForceSupervisor(string name, string prompt)
                {
                    if (name == ContinuationSupervisor.Name && continuation is not null) continuation.ForceRun(prompt);
                    else if (name == MaintenanceSupervisor.Name && maintenance is not null) maintenance.ForceRun(prompt);
                    else throw new ArgumentException($"unknown or disabled supervisor '{name}'");
                }
                inputMonitor = preflight.Options.Stdio
                    ? new StdioControl(Console.In, consoleOutput, claimGate, RequestAction, () =>
                    {
                        controlShutdown = true;
                        linkedCancellation.Cancel();
                    }, ForceSupervisor).RunAsync(linkedCancellation.Token)
                    : consoleOutput.MonitorDashboardInputAsync(claimGate,
                        RequestAction, ForceSupervisor, linkedCancellation.Token);
            }

            foreach (var loop in loops.Append(maintenanceMonitor).Append(continuationMonitor))
            {
                _ = loop.ContinueWith(
                    _ => linkedCancellation.Cancel(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                await Task.WhenAll(loops.Append(maintenanceMonitor).Append(continuationMonitor));
            }
            catch (OperationCanceledException) when (controlShutdown && !cancellationToken.IsCancellationRequested)
            {
                // EOF and the shutdown command request the normal recovery/cleanup path.
            }
            finally
            {
                linkedCancellation.Cancel();
                try
                {
                    await dashboardMonitor;
                }
                catch (OperationCanceledException)
                {
                    // The monitor shares the application lifetime.
                }
            }

            var deferral = coordinators.Values
                .Select(static coordinator => coordinator.DeferredReason)
                .FirstOrDefault(static reason => reason is not null);
            if (deferral is not null)
            {
                await log.SystemAsync(deferral);
                return RunOutcome.Deferred;
            }
        }
        finally
        {
            linkedCancellation.Cancel();
            try
            {
                await inputMonitor;
            }
            catch (OperationCanceledException)
            {
                // The input monitor shares the application lifetime.
            }

            if (tmuxSessionLease is not null)
            {
                await tmuxSessionLease.DisposeAsync();
            }

            try
            {
                Directory.Delete(temporaryRoot, recursive: false);
            }
            catch (IOException)
            {
                await log.WarningAsync("abacus", $"temporary files retained in {temporaryRoot}");
            }

            foreach (var control in agentControls.Values)
            {
                control.Dispose();
            }

            var snapshot = summary.Snapshot();
            notifier.RunCompleted(snapshot);
            await log.SummaryAsync(snapshot);
        }

        return RunOutcome.Completed;
    }

    private async Task MonitorDashboardAsync(
        Beads beads,
        Git git,
        MergeSlotReclaimer mergeSlotReclaimer,
        UserAttentionSound attentionSound,
        AgentRunRegistry agentRuns,
        IReadOnlyList<ValidatedAgent> agents,
        Options options,
        int latestCommentCount,
        bool includeLatestComments,
        CancellationToken cancellationToken, WorktreePool? pool = null)
    {
        var agent = agents[0];
        var configuredAgents = agents
            .Select(static validated => validated.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (options.SupervisorModel is not null) configuredAgents.Add(MaintenanceSupervisor.Name);
        if (options.ContinuationModel is not null) configuredAgents.Add(ContinuationSupervisor.Name);
        string? lastAttentionFailure = null;
        string? lastCommentsFailure = null;
        string? lastMergeSlotFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var issues = await beads.GetIssuesNeedingUserAttentionAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    cancellationToken);
                notifier.UserAttentionChanged(issues);
                attentionSound.Changed(issues);
                await log.SetUserAttentionIssuesAsync(issues);
                lastAttentionFailure = null;
            }
            catch (BeadsException exception)
            {
                if (!string.Equals(lastAttentionFailure, exception.Message, StringComparison.Ordinal))
                {
                    await log.WarningAsync("abacus", exception.Message);
                    lastAttentionFailure = exception.Message;
                }
            }

            try
            {
                var mergeSlot = await beads.ReadMergeSlotStatusAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    cancellationToken);
                await log.SetMergeSlotAsync(await mergeSlotReclaimer.ReclaimAsync(
                    agent.WorkspacePath,
                    configuredAgents,
                    agentRuns.IsRunning,
                    mergeSlot,
                    cancellationToken));
                lastMergeSlotFailure = null;
            }
            catch (BeadsException exception)
            {
                if (!string.Equals(lastMergeSlotFailure, exception.Message, StringComparison.Ordinal))
                {
                    await log.WarningAsync("abacus", exception.Message);
                    lastMergeSlotFailure = exception.Message;
                }
            }

            if (includeLatestComments)
            {
                foreach (var workspaceAgent in agents)
                {
                    try
                    {
                        var path = pool is null ? workspaceAgent.WorkspacePath : pool.ActiveWorkspace(workspaceAgent.Name);
                        if (path is null) { await log.SetWorkspaceAsync(workspaceAgent.Name, null, null); continue; }
                        var status = await git.GetWorkspaceStatusAsync(
                            path, workspaceAgent.Name, cancellationToken);
                        await log.SetWorkspaceAsync(workspaceAgent.Name, status.Branch, status.IsDirty);
                    }
                    catch (Exception exception) when (exception is WorkspacePreparationException or CommandStartException or CommandTimeoutException)
                    {
                        // Display failure must not kill an agent or leave stale clean/branch information.
                        await log.SetWorkspaceAsync(workspaceAgent.Name, null, null);
                    }
                }
                try
                {
                    var comments = await beads.GetLatestCommentsAsync(
                        agent.WorkspacePath,
                        agent.Name,
                        latestCommentCount,
                        cancellationToken);
                    await log.SetLatestCommentsAsync(comments);
                    lastCommentsFailure = null;
                }
                catch (BeadsException exception)
                {
                    if (!string.Equals(lastCommentsFailure, exception.Message, StringComparison.Ordinal))
                    {
                        await log.WarningAsync("abacus", exception.Message);
                        lastCommentsFailure = exception.Message;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}
