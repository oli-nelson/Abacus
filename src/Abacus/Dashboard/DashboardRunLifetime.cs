namespace Abacus.Dashboard;

/// <summary>Isolates web failures from the owner run; startup failures still fail fast.</summary>
internal sealed class DashboardRunLifetime : IAsyncDisposable
{
    private readonly DashboardSession session;
    private readonly TextWriter diagnostics;
    private readonly Task monitor;
    private bool stopping;
    private readonly DashboardRuntime? runtime;
    internal void BindWorkerControls(RunControlRouter router)
    {
        runtime?.WorkerActions.Bind(router);
        runtime?.SupervisorActions.Bind(router);
    }
    internal void BindRunControls(Action shutdown) => runtime?.RunActions.Bind(shutdown);
    internal void StopControls() => runtime?.StopControls();

    internal DashboardRunLifetime(DashboardSession session, TextWriter diagnostics, DashboardRuntime? runtime = null)
    {
        this.runtime = runtime;
        this.session = session;
        this.diagnostics = diagnostics;
        monitor = MonitorAsync();
    }

    public static async Task<DashboardRunLifetime> StartAsync(string repository, DashboardOptions options,
        TextWriter diagnostics, CancellationToken token, ConsoleOutput? output = null, ClaimGate? claimGate = null, ClaimSchedule? schedule = null)
    {
        var runtime = output is null ? null : new DashboardRuntime(output, claimGate, schedule);
        var session = await DashboardSession.StartAsync(repository, options, diagnostics, token, integrated: true, runtime: runtime);
        return new(session, diagnostics, runtime);
    }

    private async Task ReportAsync()
    {
        // Do not forward raw exceptions, CLI output or secrets to browser/stdout.
        try { await diagnostics.WriteLineAsync("Dashboard stopped unexpectedly; web access disabled. The run continues without it."); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task MonitorAsync()
    {
        try { await session.Completion; }
        catch (Exception) { /* Cleanup and report without faulting worker control. */ }
        if (!Volatile.Read(ref stopping)) await ReportAsync();
        try { await session.DisposeAsync(); }
        catch (Exception) { /* Completion failure already reported; never rebind. */ }
    }

    public async ValueTask DisposeAsync()
    {
        StopControls();
        Volatile.Write(ref stopping, true);
        try { await session.DisposeAsync(); }
        catch (Exception) { await ReportAsync(); }
        await monitor;
    }
}
