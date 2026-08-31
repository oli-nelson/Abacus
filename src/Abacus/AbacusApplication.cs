namespace Abacus;

public sealed class AbacusApplication(CommandRunner runner, TextWriter log)
{
    public async Task RunAsync(PreflightResult preflight, CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"abacus-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var beads = new Beads(runner, preflight.Tools.Bd);
            var git = new Git(runner, preflight.Tools.Git);
            var tmux = new Tmux(
                runner,
                preflight.Tools.Tmux,
                preflight.Tools.OpenCode,
                preflight.Options.TmuxSession,
                temporaryRoot);

            var loops = preflight.Agents.Select(agent =>
            {
                var recovery = new TicketRecovery(beads, log);
                var claims = new ClaimCoordinator(beads, git, recovery, log);
                var supervisor = new TicketSupervisor(beads, tmux, recovery, log);
                return new AgentLoop(
                    agent,
                    preflight.Agents.Count == 1,
                    preflight.Options.Model,
                    preflight.OpenCodeServerUrl,
                    claims,
                    tmux,
                    supervisor,
                    recovery,
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

            await Task.WhenAll(loops);
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
                await log.WriteLineAsync($"[abacus] temporary files retained in {temporaryRoot}");
            }
        }
    }
}
