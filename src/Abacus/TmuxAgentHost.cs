namespace Abacus;

public sealed class TmuxAgentRun(
    string paneId,
    string runDirectory,
    string promptPath,
    string wrapperPath,
    string markerPath) : IAgentRun
{
    private readonly SemaphoreSlim cleanupLock = new(1, 1);
    private bool cleaned;

    public string PaneId { get; } = paneId;
    public string RunDirectory { get; } = runDirectory;
    public string PromptPath { get; } = promptPath;
    public string WrapperPath { get; } = wrapperPath;
    public string MarkerPath { get; } = markerPath;
    public string Location => $"pane {PaneId}";
    public bool HasExited => File.Exists(MarkerPath);

    internal SemaphoreSlim CleanupLock => cleanupLock;
    internal bool Cleaned { get => cleaned; set => cleaned = value; }

    public int? TryReadExitCode()
    {
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return null;
            }

            return int.TryParse(File.ReadAllText(MarkerPath).Trim(), out var exitCode)
                ? exitCode
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

public sealed class TmuxAgentHost(
    CommandRunner runner,
    string tmuxExecutable,
    string agentExecutable,
    AgentMode agentMode,
    string tmuxSession,
    string temporaryRoot,
    TimeSpan? interruptGracePeriod = null,
    string? tmuxWindow = null,
    string? tmuxLayout = null,
    bool remote = false) : IAgentHost
{
    private readonly TimeSpan gracePeriod = interruptGracePeriod ?? TimeSpan.FromSeconds(1);
    private readonly string splitTarget = Target(tmuxSession, tmuxWindow);

    public async Task<TmuxAgentRun> StartAgentAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string effort,
        string? serverUrl,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new TmuxException("Abacus supports macOS and Linux only");
        }

        var runDirectory = Path.Combine(
            temporaryRoot,
            $"{SanitizeFileName(agent.Name)}-{SanitizeFileName(issue.Id)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        var promptPath = Path.Combine(runDirectory, "prompt.txt");
        var wrapperPath = Path.Combine(runDirectory, "run.sh");
        var markerPath = Path.Combine(runDirectory, "exit-code");

        try
        {
            await File.WriteAllTextAsync(
                promptPath,
                Prompt.Render(agent.Name, issue.Id, agent.WorkspacePath),
                cancellationToken);
            await File.WriteAllTextAsync(
                wrapperPath,
                RenderWrapper(agent, issue, model, effort, serverUrl, promptPath, markerPath),
                cancellationToken);
            File.SetUnixFileMode(
                wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await runner.RunAsync(new CommandSpec(
                tmuxExecutable,
                [
                    "split-window",
                    "-t", splitTarget,
                    "-d",
                    "-P",
                    "-F", "#{pane_id}",
                    ShellQuote(wrapperPath),
                ],
                agent.WorkspacePath,
                AgentName: agent.Name), CancellationToken.None);
            if (!result.Succeeded)
            {
                throw new TmuxException($"could not create agent pane: {Beads.FailureDetail(result)}");
            }

            var paneId = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(paneId))
            {
                throw new TmuxException("tmux did not return the created pane ID");
            }

            var run = new TmuxAgentRun(paneId, runDirectory, promptPath, wrapperPath, markerPath);
            var title = await runner.RunAsync(new CommandSpec(
                tmuxExecutable,
                [
                    "set-option",
                    "-p",
                    "-t", run.PaneId,
                    "allow-set-title", "off",
                ],
                temporaryRoot,
                AgentName: agent.Name), CancellationToken.None);
            if (!title.Succeeded)
            {
                await StopAndCleanupAsync(run, CancellationToken.None);
                throw new TmuxException($"could not protect agent pane title: {Beads.FailureDetail(title)}");
            }

            title = await runner.RunAsync(new CommandSpec(
                tmuxExecutable,
                [
                    "select-pane",
                    "-t", run.PaneId,
                    "-T", EscapeFormat($"{agent.Name} • {issue.Id}"),
                ],
                temporaryRoot,
                AgentName: agent.Name), CancellationToken.None);
            if (!title.Succeeded)
            {
                await StopAndCleanupAsync(run, CancellationToken.None);
                throw new TmuxException($"could not title agent pane: {Beads.FailureDetail(title)}");
            }

            if (tmuxLayout is not null)
            {
                var layout = await runner.RunAsync(new CommandSpec(
                    tmuxExecutable,
                    ["select-layout", "-t", splitTarget, tmuxLayout],
                    temporaryRoot,
                    AgentName: agent.Name), CancellationToken.None);
                if (!layout.Succeeded)
                {
                    await StopAndCleanupAsync(run, CancellationToken.None);
                    throw new TmuxException($"could not arrange agent panes: {Beads.FailureDetail(layout)}");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await StopAndCleanupAsync(run, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return run;
        }
        catch
        {
            TryDeleteDirectory(runDirectory);
            throw;
        }
    }

    public async Task<bool> PaneExistsAsync(TmuxAgentRun run, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new CommandSpec(
            tmuxExecutable,
            ["display-message", "-p", "-t", run.PaneId, "#{pane_id}"],
            temporaryRoot), cancellationToken);
        return result.Succeeded && string.Equals(result.StandardOutput.Trim(), run.PaneId, StringComparison.Ordinal);
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
        PaneExistsAsync(RequireTmuxRun(run), cancellationToken);

    Task IAgentHost.StopAndCleanupAsync(
        IAgentRun run,
        CancellationToken cancellationToken) =>
        StopAndCleanupAsync(RequireTmuxRun(run), cancellationToken);

    public async Task StopAndCleanupAsync(TmuxAgentRun run, CancellationToken cancellationToken)
    {
        await run.CleanupLock.WaitAsync(CancellationToken.None);
        try
        {
            if (run.Cleaned)
            {
                return;
            }

            if (await PaneExistsAsync(run, CancellationToken.None))
            {
                await runner.RunAsync(new CommandSpec(
                    tmuxExecutable,
                    ["send-keys", "-t", run.PaneId, "C-c"],
                    temporaryRoot), CancellationToken.None);

                try
                {
                    await Task.Delay(gracePeriod, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Cleanup must continue even when application cancellation is active.
                }

                if (await PaneExistsAsync(run, CancellationToken.None))
                {
                    await runner.RunAsync(new CommandSpec(
                        tmuxExecutable,
                        ["kill-pane", "-t", run.PaneId],
                        temporaryRoot), CancellationToken.None);
                }
            }

            CleanupRunFiles(run);
            run.Cleaned = true;
        }
        finally
        {
            run.CleanupLock.Release();
        }
    }

    private string RenderWrapper(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
        string effort,
        string? serverUrl,
        string promptPath,
        string markerPath)
    {
        var command = AgentCommandFactory.Create(
            agentMode,
            agentExecutable,
            model,
            agent.WorkspacePath,
            serverUrl,
            $"{agent.Name} • {issue.Id}",
            effort,
            remote,
            RemoteSessionName(issue));
        var arguments = new List<string>
        {
            ShellQuote(command.Executable),
        };
        arguments.AddRange(command.ArgumentsBeforePrompt.Select(ShellQuote));
        arguments.Add("\"$prompt\"");
        arguments.AddRange(command.ArgumentsAfterPrompt.Select(ShellQuote));

        return $"""
            #!/bin/sh
            set +e
            export BEADS_ACTOR={ShellQuote(agent.Name)}
            code=125
            if cd {ShellQuote(agent.WorkspacePath)}; then
              prompt=$(cat {ShellQuote(promptPath)})
              if test $? -eq 0; then
                {string.Join(' ', arguments)}
                code=$?
              fi
            fi
            marker_tmp={ShellQuote(markerPath)}.tmp.$$
            printf '%s\n' "$code" >"$marker_tmp"
            mv "$marker_tmp" {ShellQuote(markerPath)}
            trap 'exit 0' INT TERM HUP
            while :; do sleep 1; done
            """;
    }

    internal static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    internal static string EscapeFormat(string value) =>
        value.Replace("#", "##", StringComparison.Ordinal);

    internal static string Target(string session, string? window) =>
        window is null ? session : $"{session}:{window}";

    internal static string RemoteSessionName(BeadsIssue issue)
    {
        var title = string.Join(
            ' ',
            (issue.Title ?? string.Empty).Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        return title.Length == 0 ? issue.Id : $"{issue.Id} • {title}";
    }

    private static TmuxAgentRun RequireTmuxRun(IAgentRun run) =>
        run as TmuxAgentRun
        ?? throw new ArgumentException("run was not created by the tmux host", nameof(run));

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static void CleanupRunFiles(TmuxAgentRun run)
    {
        TryDeleteFile(run.PromptPath);
        TryDeleteFile(run.WrapperPath);
        TryDeleteFile(run.MarkerPath);

        try
        {
            Directory.Delete(run.RunDirectory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class TmuxException(string message) : Exception(message);
