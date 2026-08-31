using System.Diagnostics;

namespace Abacus;

public sealed class DirectOpenCodeRun(
    Process process,
    string agentName,
    string workspacePath,
    Task stdoutDrain,
    Task stderrDrain) : IOpenCodeRun
{
    private readonly SemaphoreSlim cleanupLock = new(1, 1);

    public int ProcessId { get; } = process.Id;
    public string Location => $"process {ProcessId}";

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public int? TryReadExitCode()
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    internal Process Process => process;
    internal string AgentName => agentName;
    internal string WorkspacePath => workspacePath;
    internal Task StdoutDrain => stdoutDrain;
    internal Task StderrDrain => stderrDrain;
    internal SemaphoreSlim CleanupLock => cleanupLock;
    internal bool Cleaned { get; set; }
}

public sealed class DirectOpenCode(
    CommandRunner runner,
    TextWriter log,
    string executable,
    TimeSpan? interruptGracePeriod = null) : IOpenCodeHost
{
    private readonly TimeSpan gracePeriod = interruptGracePeriod ?? TimeSpan.FromSeconds(1);

    public async Task<DirectOpenCodeRun> StartOpenCodeAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string? serverUrl,
        CancellationToken cancellationToken)
    {
        if (serverUrl is null)
        {
            throw new InvalidOperationException("direct OpenCode processes require an attached server");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = agent.WorkspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "run",
            Prompt.Render(agent.Name, issue.Id, agent.WorkspacePath),
            "--model", model,
            "--attach", serverUrl,
            "--dir", agent.WorkspacePath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["BEADS_ACTOR"] = agent.Name;
        await log.DebugCommandAsync(
            agent.Name,
            $"{executable} run <prompt for {issue.Id}> --model {model} --attach {serverUrl} --dir {agent.WorkspacePath}");

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"failed to start '{executable}'");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            process.Dispose();
            throw new CommandStartException(executable, exception);
        }

        var run = new DirectOpenCodeRun(
            process,
            agent.Name,
            agent.WorkspacePath,
            process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None),
            process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None));
        if (cancellationToken.IsCancellationRequested)
        {
            await StopAndCleanupAsync(run, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return run;
    }

    public Task<bool> IsRunningAsync(DirectOpenCodeRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!run.HasExited);
    }

    public async Task StopAndCleanupAsync(
        DirectOpenCodeRun run,
        CancellationToken cancellationToken)
    {
        await run.CleanupLock.WaitAsync(CancellationToken.None);
        try
        {
            if (run.Cleaned)
            {
                return;
            }

            if (!run.HasExited)
            {
                await runner.RunAsync(new CommandSpec(
                    "/bin/kill",
                    ["-INT", run.ProcessId.ToString()],
                    run.WorkspacePath,
                    AgentName: run.AgentName), CancellationToken.None);

                using var grace = new CancellationTokenSource(gracePeriod);
                try
                {
                    await run.Process.WaitForExitAsync(grace.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!run.HasExited)
                        {
                            run.Process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between the status check and Kill.
                    }

                    await run.Process.WaitForExitAsync(CancellationToken.None);
                }
            }

            await DrainAsync(run.StdoutDrain);
            await DrainAsync(run.StderrDrain);
            run.Process.Dispose();
            run.Cleaned = true;
        }
        finally
        {
            run.CleanupLock.Release();
        }
    }

    async Task<IOpenCodeRun> IOpenCodeHost.StartOpenCodeAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string? serverUrl,
        CancellationToken cancellationToken) =>
        await StartOpenCodeAsync(agent, issue, model, serverUrl, cancellationToken);

    Task<bool> IOpenCodeHost.IsRunningAsync(
        IOpenCodeRun run,
        CancellationToken cancellationToken) =>
        IsRunningAsync(RequireDirectRun(run), cancellationToken);

    Task IOpenCodeHost.StopAndCleanupAsync(
        IOpenCodeRun run,
        CancellationToken cancellationToken) =>
        StopAndCleanupAsync(RequireDirectRun(run), cancellationToken);

    private static DirectOpenCodeRun RequireDirectRun(IOpenCodeRun run) =>
        run as DirectOpenCodeRun
        ?? throw new ArgumentException("run was not created by the direct OpenCode host", nameof(run));

    private static async Task DrainAsync(Task drain)
    {
        try
        {
            await drain;
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
