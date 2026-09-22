namespace Abacus.Dashboard;

/// <summary>
/// One repository's HTTP host and collectors, with no process signals, controller
/// lease or worker ownership. The caller owns shutdown and decides how to report
/// Completion failures; a dashboard failure never cancels its owner's token.
/// </summary>
internal sealed class DashboardSession : IAsyncDisposable
{
    private readonly DashboardHost host;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration stopped;
    private readonly object sync = new();
    private Task? disposal;
    public Task Completion { get; }

    internal DashboardSession(DashboardHost host, CancellationTokenSource lifetime,
        params Func<CancellationToken, Task>[] collectors)
    {
        this.host = host;
        this.lifetime = lifetime;
        stopped = host.Stopped.Register(lifetime.Cancel);
        Completion = MonitorAsync(collectors);
    }

    private async Task MonitorAsync(Func<CancellationToken, Task>[] collectors)
    {
        // All loops are started only after binding succeeded. Convert synchronous
        // startup exceptions to tasks so every already-started loop is observed.
        async Task Run(Func<CancellationToken, Task> collect) => await collect(lifetime.Token);
        var tasks = collectors.Select(Run).ToArray();
        try
        {
            if (tasks.Length > 0) await Task.WhenAny(tasks);
        }
        finally { lifetime.Cancel(); }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public static async Task<DashboardSession> StartAsync(string repository, DashboardOptions options,
        TextWriter diagnostics, CancellationToken startupToken, bool integrated = false, DashboardRuntime? runtime = null)
    {
        // Startup cancellation is linked only during startup, not for the whole
        // session: the owner disposes explicitly after its own cleanup sequence.
        var lifetime = new CancellationTokenSource();
        using var startup = startupToken.Register(lifetime.Cancel);
        DashboardHost? host = null;
        IssueActions? actions = null;
        try
        {
            var token = lifetime.Token;
            var binding = await options.ResolveAsync(token);
            var runner = new CommandRunner(TextWriter.Null);
            await DashboardApplication.ValidateRepositoryAsync(runner, repository, token);
            var collector = IssueCollector.ForRepository(runner, repository, token);
            var initial = await collector.RefreshAsync(token);
            if (initial.Stale) throw new InvalidOperationException("Cannot read the Beads issue export; dashboard was not started.");
            var stream = new DashboardStream();
            stream.Publish(initial);
            var git = new GitCollector(new DashboardGit(runner, repository, token), repository);
            var gitInitial = await git.RefreshAsync(token);
            stream.PublishGit(gitInitial);
            if (gitInitial.View.PolicyError is { } policyError) await diagnostics.WriteLineAsync(policyError);
            var activity = IssueActivity.ForRepository(runner, repository, token);
            await activity.RefreshAsync(token);
            stream.PublishHistory(activity.State);
            var loop = new CollectionLoop(collector, options.PollInterval, stream.Publish,
                refreshDetails: async cancellation => { await activity.RefreshAsync(cancellation); stream.PublishHistory(activity.State); });
            actions = IssueActions.ForRepository(runner, repository, options.Actor, () => !collector.State.Stale, loop.MarkDirty, CancellationToken.None);
            if (runtime is not null) stream.PublishRuntime(runtime.Refresh());
            host = await DashboardHost.StartAsync(binding, stream, Path.GetFileName(repository), options.Actor, token, git, actions, activity, integrated, runtime, collector);
            foreach (var url in binding.Urls) await diagnostics.WriteLineAsync($"Dashboard: {url}");
            await diagnostics.WriteLineAsync($"Repository: {repository}\nActor: {options.Actor}\nBeads: readable; {(integrated ? "hosted by this run; see project capabilities for available controls" : "orchestrator not connected")}.");
            await diagnostics.WriteLineAsync("WARNING: unauthenticated read/write access on a trusted network only. HTTP has no encryption. Issue comments, content edits and plain attention actions are enabled.");
            token.ThrowIfCancellationRequested();
            var collectors = new List<Func<CancellationToken, Task>> { loop.RunAsync,
                cancellation => git.RunAsync(options.PollInterval, stream.PublishGit, cancellation) };
            if (runtime is not null) collectors.Add(cancellation => runtime.RunAsync(stream.PublishRuntime, cancellation));
            return new(host, lifetime, collectors.ToArray());
        }
        catch
        {
            lifetime.Cancel();
            try
            {
                if (host is not null) await host.DisposeAsync();
                else if (actions is not null) await actions.DrainAsync();
            }
            finally { lifetime.Dispose(); }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new(disposal ??= StopAsync());
    }

    private async Task StopAsync()
    {
        lifetime.Cancel();
        try { await Completion; }
        finally
        {
            stopped.Dispose();
            try { await host.DisposeAsync(); }
            finally { lifetime.Dispose(); }
        }
    }
}
