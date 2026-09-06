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
        var inputMonitor = Task.CompletedTask;
        var controlShutdown = false;
        try
        {
            await log.SystemAsync("Agent loops started");
            var git = new Git(runner, preflight.Tools.Git);
            var dashboardMonitor = MonitorDashboardAsync(
                beads,
                preflight.Agents[0],
                preflight.Options.LatestCommentCount,
                includeLatestComments: log is ConsoleOutput dashboard && (dashboard.IsInteractiveDashboard || dashboard.Events is not null),
                linkedCancellation.Token);
            IAgentHost agentHost = preflight.Options.TmuxSession is null
                ? new DirectOpenCodeServerHost(runner, log, preflight.Tools.AgentExecutable)
                : new TmuxAgentHost(
                    runner,
                    preflight.Tools.Tmux!,
                    preflight.Tools.AgentExecutable,
                    preflight.Options.AgentMode,
                    preflight.Options.TmuxSession,
                    temporaryRoot,
                    tmuxWindow: preflight.Options.TmuxWindow,
                    tmuxLayout: preflight.Options.TmuxLayout,
                    remote: preflight.Options.Remote);

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
                    initialClaimBarrier: initialClaimBarrier);
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
                    notifier).RunAsync(linkedCancellation.Token);
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
        ValidatedAgent agent,
        int latestCommentCount,
        bool includeLatestComments,
        CancellationToken cancellationToken)
    {
        string? lastAttentionFailure = null;
        string? lastCommentsFailure = null;
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

            if (includeLatestComments)
            {
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
