using System.Security.Cryptography;
using System.Text;

namespace Abacus;

public sealed class TmuxSessionLease : IAsyncDisposable
{
    public const string DefaultWindowName = "Abacus Agents";

    private readonly CommandRunner runner;
    private readonly TextWriter log;
    private readonly string tmuxExecutable;
    private readonly string workingDirectory;
    private bool disposed;

    private TmuxSessionLease(
        CommandRunner runner,
        TextWriter log,
        string tmuxExecutable,
        string workingDirectory,
        string sessionName,
        string windowName,
        string windowId,
        string projectId,
        bool ownsSession)
    {
        this.runner = runner;
        this.log = log;
        this.tmuxExecutable = tmuxExecutable;
        this.workingDirectory = workingDirectory;
        SessionName = sessionName;
        WindowName = windowName;
        WindowId = windowId;
        ProjectId = projectId;
        OwnsSession = ownsSession;
    }

    public string SessionName { get; }
    public string WindowName { get; }
    public string WindowId { get; }
    public string ProjectId { get; }
    public bool OwnsSession { get; }

    public static async Task<TmuxSessionLease> CreateAsync(
        CommandRunner runner,
        TextWriter log,
        string tmuxExecutable,
        Options options,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var projectId = SafeProjectId(repositoryRoot);
        var sessionName = options.TmuxSession ?? $"abacus - {projectId}";
        var windowName = options.TmuxWindow ?? DefaultWindowName;
        var resolvedWindowName = windowName;
        var workingDirectory = repositoryRoot;
        var sessionCreated = false;
        string? windowId = null;

        var hasSession = await RunAsync(
            runner,
            tmuxExecutable,
            workingDirectory,
            ["has-session", "-t", sessionName],
            cancellationToken);
        if (!hasSession.Succeeded)
        {
            var createSession = await RunAsync(
                runner,
                tmuxExecutable,
                workingDirectory,
                [
                    "new-session", "-d", "-P", "-F", "#{window_id}",
                    "-s", sessionName,
                    "-n", windowName,
                ],
                cancellationToken);
            if (createSession.Succeeded)
            {
                sessionCreated = true;
                windowId = createSession.StandardOutput.Trim();
                if (string.IsNullOrWhiteSpace(windowId))
                {
                    await TryKillSessionAsync(runner, tmuxExecutable, workingDirectory, sessionName);
                    throw new TmuxException("tmux did not return the initial window ID");
                }
            }
            else
            {
                // Another Abacus process may have created the same derived session
                // after our initial check. Only accept that specific race when the
                // session can now be observed.
                var racedSession = await RunAsync(
                    runner,
                    tmuxExecutable,
                    workingDirectory,
                    ["has-session", "-t", sessionName],
                    cancellationToken);
                if (!racedSession.Succeeded)
                {
                    throw new TmuxException(
                        $"could not create tmux session '{sessionName}': {Beads.FailureDetail(createSession)}");
                }
            }
        }

        try
        {
            if (windowId is null)
            {
                var resolvedWindow = await ResolveOrCreateWindowAsync(
                    runner,
                    tmuxExecutable,
                    workingDirectory,
                    sessionName,
                    windowName,
                    cancellationToken);
                windowId = resolvedWindow.Id;
                resolvedWindowName = resolvedWindow.Name;
            }

            var remainOnExit = await RunAsync(
                runner,
                tmuxExecutable,
                workingDirectory,
                ["set-option", "-w", "-t", windowId, "remain-on-exit", "on"],
                cancellationToken);
            if (!remainOnExit.Succeeded)
            {
                throw new TmuxException(
                    $"could not enable remain-on-exit for tmux window '{windowName}': {Beads.FailureDetail(remainOnExit)}");
            }
        }
        catch
        {
            if (sessionCreated)
            {
                await TryKillSessionAsync(runner, tmuxExecutable, workingDirectory, sessionName);
            }

            throw;
        }

        var ownsSession = sessionCreated
            && options.TmuxSession is null
            && !options.DisownTmuxSession;
        await log.SystemAsync(
            $"tmux target ready: session '{sessionName}', window '{resolvedWindowName}' ({windowId})"
            + (ownsSession ? "; Abacus owns the session for this run" : string.Empty));
        return new TmuxSessionLease(
            runner,
            log,
            tmuxExecutable,
            workingDirectory,
            sessionName,
            resolvedWindowName,
            windowId,
            projectId,
            ownsSession);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!OwnsSession)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var result = await RunAsync(
                runner,
                tmuxExecutable,
                workingDirectory,
                ["kill-session", "-t", SessionName],
                cancellation.Token);
            if (!result.Succeeded)
            {
                await log.WarningAsync(
                    "abacus",
                    $"could not remove owned tmux session '{SessionName}': {Beads.FailureDetail(result)}");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await log.WarningAsync(
                "abacus",
                $"could not remove owned tmux session '{SessionName}': {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            await log.WarningAsync(
                "abacus",
                $"timed out removing owned tmux session '{SessionName}'");
        }
    }

    internal static string SafeProjectId(string repositoryRoot)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repositoryRoot));
        var slug = new StringBuilder();
        var previousWasSeparator = false;
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && slug.Length > 0)
            {
                slug.Append('-');
                previousWasSeparator = true;
            }
        }

        var safeName = slug.ToString().Trim('-');
        if (safeName.Length == 0)
        {
            safeName = "project";
        }

        const int maximumSlugLength = 48;
        if (safeName.Length > maximumSlugLength)
        {
            safeName = safeName[..maximumSlugLength].TrimEnd('-');
        }

        var canonicalPath = Path.GetFullPath(repositoryRoot);
        if (OperatingSystem.IsMacOS())
        {
            canonicalPath = canonicalPath.ToLowerInvariant();
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        var suffix = Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        return $"{safeName}-{suffix}";
    }

    private static async Task<TmuxWindowTarget> ResolveOrCreateWindowAsync(
        CommandRunner runner,
        string tmuxExecutable,
        string workingDirectory,
        string sessionName,
        string requestedWindow,
        CancellationToken cancellationToken)
    {
        var existing = await FindWindowAsync(
            runner,
            tmuxExecutable,
            workingDirectory,
            sessionName,
            requestedWindow,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = await RunAsync(
            runner,
            tmuxExecutable,
            workingDirectory,
            [
                "new-window", "-d", "-P", "-F", "#{window_id}",
                "-t", sessionName,
                "-n", requestedWindow,
            ],
            cancellationToken);
        if (created.Succeeded && !string.IsNullOrWhiteSpace(created.StandardOutput))
        {
            return new TmuxWindowTarget(created.StandardOutput.Trim(), requestedWindow);
        }

        var racedWindow = await FindWindowAsync(
            runner,
            tmuxExecutable,
            workingDirectory,
            sessionName,
            requestedWindow,
            cancellationToken);
        if (racedWindow is not null)
        {
            return racedWindow;
        }

        throw new TmuxException(
            $"could not create tmux window '{requestedWindow}' in session '{sessionName}': {Beads.FailureDetail(created)}");
    }

    private static async Task<TmuxWindowTarget?> FindWindowAsync(
        CommandRunner runner,
        string tmuxExecutable,
        string workingDirectory,
        string sessionName,
        string requestedWindow,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            runner,
            tmuxExecutable,
            workingDirectory,
            ["list-windows", "-t", sessionName, "-F", "#{window_id}\t#{window_index}\t#{window_name}"],
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new TmuxException(
                $"could not list windows in tmux session '{sessionName}': {Beads.FailureDetail(result)}");
        }

        var windows = result.StandardOutput
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split('\t', 3))
            .Where(static fields => fields.Length == 3 && !string.IsNullOrWhiteSpace(fields[0]))
            .Select(static fields => new { Id = fields[0], Index = fields[1], Name = fields[2] })
            .ToArray();
        var named = windows.Where(window =>
            string.Equals(window.Name, requestedWindow, StringComparison.Ordinal)).ToArray();
        if (named.Length > 1)
        {
            throw new TmuxException(
                $"tmux session '{sessionName}' contains multiple windows named '{requestedWindow}'");
        }

        if (named.Length == 1)
        {
            return new TmuxWindowTarget(named[0].Id, named[0].Name);
        }

        var indexed = windows.FirstOrDefault(window =>
            string.Equals(window.Index, requestedWindow, StringComparison.Ordinal));
        return indexed is null ? null : new TmuxWindowTarget(indexed.Id, indexed.Name);
    }

    private static Task<CommandResult> RunAsync(
        CommandRunner runner,
        string tmuxExecutable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        runner.RunAsync(
            new CommandSpec(tmuxExecutable, arguments, workingDirectory),
            cancellationToken);

    private static async Task TryKillSessionAsync(
        CommandRunner runner,
        string tmuxExecutable,
        string workingDirectory,
        string sessionName)
    {
        try
        {
            await RunAsync(
                runner,
                tmuxExecutable,
                workingDirectory,
                ["kill-session", "-t", sessionName],
                CancellationToken.None);
        }
        catch (Exception)
        {
            // Rollback is best effort; preserve the original initialization error.
        }
    }

    private sealed record TmuxWindowTarget(string Id, string Name);
}
