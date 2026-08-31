namespace Abacus;

public sealed class OpenCodeRun(
    string paneId,
    string runDirectory,
    string promptPath,
    string wrapperPath,
    string markerPath)
{
    private readonly SemaphoreSlim cleanupLock = new(1, 1);
    private bool cleaned;

    public string PaneId { get; } = paneId;
    public string RunDirectory { get; } = runDirectory;
    public string PromptPath { get; } = promptPath;
    public string WrapperPath { get; } = wrapperPath;
    public string MarkerPath { get; } = markerPath;

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

public sealed class Tmux(
    CommandRunner runner,
    string executable,
    string openCodeExecutable,
    string tmuxSession,
    string temporaryRoot,
    TimeSpan? interruptGracePeriod = null)
{
    private readonly TimeSpan gracePeriod = interruptGracePeriod ?? TimeSpan.FromSeconds(1);

    public async Task<OpenCodeRun> StartOpenCodeAsync(
        ValidatedAgent agent,
        BeadsIssue issue,
        string model,
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
                RenderWrapper(agent, model, serverUrl, promptPath, markerPath),
                cancellationToken);
            File.SetUnixFileMode(
                wrapperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await runner.RunAsync(new CommandSpec(
                executable,
                [
                    "split-window",
                    "-t", tmuxSession,
                    "-d",
                    "-P",
                    "-F", "#{pane_id}",
                    ShellQuote(wrapperPath),
                ],
                agent.WorkspacePath,
                AgentName: agent.Name), CancellationToken.None);
            if (!result.Succeeded)
            {
                throw new TmuxException($"could not create OpenCode pane: {Beads.FailureDetail(result)}");
            }

            var paneId = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(paneId))
            {
                throw new TmuxException("tmux did not return the created pane ID");
            }

            var run = new OpenCodeRun(paneId, runDirectory, promptPath, wrapperPath, markerPath);
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

    public async Task<bool> PaneExistsAsync(OpenCodeRun run, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(new CommandSpec(
            executable,
            ["display-message", "-p", "-t", run.PaneId, "#{pane_id}"],
            temporaryRoot), cancellationToken);
        return result.Succeeded && string.Equals(result.StandardOutput.Trim(), run.PaneId, StringComparison.Ordinal);
    }

    public async Task StopAndCleanupAsync(OpenCodeRun run, CancellationToken cancellationToken)
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
                    executable,
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
                        executable,
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
        string model,
        string? serverUrl,
        string promptPath,
        string markerPath)
    {
        List<string> arguments;
        if (serverUrl is null)
        {
            arguments =
            [
                ShellQuote(openCodeExecutable),
                "--mini",
                "--prompt", "\"$prompt\"",
                "--model", ShellQuote(model),
            ];
        }
        else
        {
            arguments =
            [
                ShellQuote(openCodeExecutable),
                "run",
                "\"$prompt\"",
                "--model", ShellQuote(model),
                "--attach", ShellQuote(serverUrl),
                "--dir", ShellQuote(agent.WorkspacePath),
            ];
        }

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

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static void CleanupRunFiles(OpenCodeRun run)
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
