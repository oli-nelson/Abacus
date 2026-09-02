namespace Abacus;

public sealed class AbacusApplication(CommandRunner runner, TextWriter log)
{
    public async Task RunAsync(PreflightResult preflight, CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"abacus-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var summary = new RunSummary(preflight.Agents.Select(static agent => agent.Name));

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await log.SystemAsync("Agent loops started");
            var beads = new Beads(runner, preflight.Tools.Bd);
            var git = new Git(runner, preflight.Tools.Git);
            var attentionMonitor = MonitorUserAttentionAsync(
                beads,
                preflight.Agents[0],
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
                    dispatchFilters: preflight.Options.DispatchFilters);
                var supervisor = new TicketSupervisor(
                    beads,
                    agentHost,
                    recovery,
                    log,
                    summary: summary,
                    ticketTimeout: preflight.Options.TicketTimeout);
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
                    log).RunAsync(linkedCancellation.Token);
            }).ToArray();

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
            finally
            {
                linkedCancellation.Cancel();
                try
                {
                    await attentionMonitor;
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
                Directory.Delete(temporaryRoot, recursive: false);
            }
            catch (IOException)
            {
                await log.WarningAsync("abacus", $"temporary files retained in {temporaryRoot}");
            }

            await log.SummaryAsync(summary.Snapshot());
        }
    }

    private async Task MonitorUserAttentionAsync(
        Beads beads,
        ValidatedAgent agent,
        CancellationToken cancellationToken)
    {
        string? lastFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var issues = await beads.GetIssuesNeedingUserAttentionAsync(
                    agent.WorkspacePath,
                    agent.Name,
                    cancellationToken);
                await log.SetUserAttentionIssuesAsync(issues);
                lastFailure = null;
            }
            catch (BeadsException exception)
            {
                if (!string.Equals(lastFailure, exception.Message, StringComparison.Ordinal))
                {
                    await log.WarningAsync("abacus", exception.Message);
                    lastFailure = exception.Message;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }
}
