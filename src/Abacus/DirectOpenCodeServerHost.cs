using System.Diagnostics;

namespace Abacus;

public sealed class DirectAgentRun(
    Process process,
    string agentName,
    string workspacePath,
    Task stdoutDrain,
    Task stderrDrain) : IAgentRun
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

public sealed class DirectOpenCodeServerHost(
    CommandRunner runner,
    TextWriter log,
    string executable,
    TimeSpan? interruptGracePeriod = null,
    TimeSpan? cleanupTimeout = null) : IAgentHost
{
    private readonly TimeSpan gracePeriod = interruptGracePeriod ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan cleanupDeadline = cleanupTimeout ?? TimeSpan.FromSeconds(10);

    public async Task<DirectAgentRun> StartAgentAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string effort,
        string? serverUrl,
        CancellationToken cancellationToken)
    {
        if (serverUrl is null)
        {
            throw new InvalidOperationException("direct OpenCode Server processes require an attached server");
        }

        var command = AgentCommandFactory.Create(
            AgentMode.OpenCodeServer,
            executable,
            model,
            agent.WorkspacePath,
            serverUrl,
            $"{agent.Name} • {issue.Id}",
            effort);
        var prompt = Prompt.Render(agent.Name, issue.Id, agent.WorkspacePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            WorkingDirectory = agent.WorkspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in command.WithPrompt(prompt))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["BEADS_ACTOR"] = agent.Name;
        await log.DebugCommandAsync(
            agent.Name,
            $"{executable} run <prompt for {issue.Id}> --model {model} --variant {effort} --attach {serverUrl} --dir {agent.WorkspacePath}");

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

        var run = new DirectAgentRun(
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

    public Task<bool> IsRunningAsync(DirectAgentRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!run.HasExited);
    }

    public async Task StopAndCleanupAsync(
        DirectAgentRun run,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(cleanupDeadline);
        await run.CleanupLock.WaitAsync(budget.Token);
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
                    AgentName: run.AgentName), budget.Token);

                using var grace = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                grace.CancelAfter(gracePeriod);
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

                    await run.Process.WaitForExitAsync(budget.Token);
                }
            }

            await DrainAsync(run.StdoutDrain).WaitAsync(budget.Token);
            await DrainAsync(run.StderrDrain).WaitAsync(budget.Token);
            run.Process.Dispose();
            run.Cleaned = true;
        }
        finally
        {
            run.CleanupLock.Release();
        }
    }

    async Task<IAgentRun> IAgentHost.StartAgentAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string effort,
        string? serverUrl,
        CancellationToken cancellationToken) =>
        await StartAgentAsync(agent, issue, model, effort, serverUrl, cancellationToken);

    Task<bool> IAgentHost.IsRunningAsync(
        IAgentRun run,
        CancellationToken cancellationToken) =>
        IsRunningAsync(RequireDirectRun(run), cancellationToken);

    Task IAgentHost.StopAndCleanupAsync(
        IAgentRun run,
        CancellationToken cancellationToken) =>
        StopAndCleanupAsync(RequireDirectRun(run), cancellationToken);

    private static DirectAgentRun RequireDirectRun(IAgentRun run) =>
        run as DirectAgentRun
        ?? throw new ArgumentException("run was not created by the direct OpenCode Server host", nameof(run));

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
