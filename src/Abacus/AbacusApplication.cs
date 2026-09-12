namespace Abacus;

public sealed class AbacusApplication(
    CommandRunner runner,
    TextWriter log,
    DesktopNotifier notifier)
{
    public async Task RunAsync(PreflightResult preflight, CancellationToken cancellationToken)
    {
        using var ownership = await WorkspaceOwnership.AcquireAsync(
            new Git(runner, preflight.Tools.Git), preflight.Agents, cancellationToken);
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
                agentRuns,
                preflight.Agents,
                preflight.Options.LatestCommentCount,
                includeLatestComments: log is ConsoleOutput dashboard && (dashboard.IsInteractiveDashboard || dashboard.Events is not null),
                linkedCancellation.Token);
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
                    defaultArguments: preflight.Options.EffectiveExtraArguments);
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
                    preflight.Options.EffectiveExtraArguments).RunAsync(linkedCancellation.Token);
            }).ToArray();

            if (log is ConsoleOutput consoleOutput)
            {
                void RequestAction(string name, AgentControlAction action)
                {
                    if (!agentControls.TryGetValue(name, out var control))
                        throw new ArgumentException($"unknown agent '{name}'");
                    var index = preflight.Agents.ToList().FindIndex(agent => agent.Name == name);
                    if (loops[index].IsCompleted)
                        throw new InvalidOperationException($"agent '{name}' has finished; start a new run");
                    if (!control.TryRequest(action))
                        throw new InvalidOperationException($"agent '{name}' already has a pending control request");
                }
                inputMonitor = preflight.Options.Stdio
                    ? new StdioControl(Console.In, consoleOutput, claimGate, RequestAction, () =>
                    {
                        controlShutdown = true;
                        linkedCancellation.Cancel();
                    }).RunAsync(linkedCancellation.Token)
                    : consoleOutput.MonitorDashboardInputAsync(claimGate,
                        (name, action) => agentControls[name].Request(action), linkedCancellation.Token);
            }

            foreach (var loop in loops)
            {
                _ = loop.ContinueWith(
                    _ => linkedCancellation.Cancel(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            try
            {
                await Task.WhenAll(loops);
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
    }

    private async Task MonitorDashboardAsync(
        Beads beads,
        Git git,
        MergeSlotReclaimer mergeSlotReclaimer,
        AgentRunRegistry agentRuns,
        IReadOnlyList<ValidatedAgent> agents,
        int latestCommentCount,
        bool includeLatestComments,
        CancellationToken cancellationToken)
    {
        var agent = agents[0];
        var configuredAgents = agents
            .Select(static validated => validated.Name)
            .ToHashSet(StringComparer.Ordinal);
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
                        var status = await git.GetWorkspaceStatusAsync(
                            workspaceAgent.WorkspacePath, workspaceAgent.Name, cancellationToken);
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
