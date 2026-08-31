using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed class EndToEndTests
{
    [Fact]
    public async Task ProgramRunsOneFakeCliTicketAndCleansUpOnCtrlC()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = new ProcessStartInfo(FindOnPath("dotnet"))
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("--tmux-session");
            startInfo.ArgumentList.Add("workers");
            startInfo.ArgumentList.Add("--tmux-window");
            startInfo.ArgumentList.Add("agents");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("provider/exact-model");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add("alice");
            startInfo.ArgumentList.Add(workspace);
            startInfo.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

            process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await WaitForFileAsync(Path.Combine(root.FullName, "pane-cleaned"), TimeSpan.FromSeconds(15));

            await RunAsync("/bin/kill", "-INT", process.Id.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(130, process.ExitCode);
            Assert.Equal("alice", await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-actor")));
            Assert.Equal(Prompt.Render("alice", "abc-1", workspace),
                await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-prompt")));
            Assert.Equal(
                ["--model", "provider/exact-model"],
                await File.ReadAllLinesAsync(Path.Combine(root.FullName, "opencode-arguments")));
            Assert.Contains("ready --claim --exclude-label gt:slot --json", await File.ReadAllTextAsync(Path.Combine(root.FullName, "bd-calls")), StringComparison.Ordinal);
            Assert.Contains("send-keys -t %1 C-c", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Contains("split-window -t workers:agents", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Empty(await stdout);
            Assert.Contains("[alice]", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            root.Delete(recursive: true);
        }
    }

    private static async Task WriteFakeToolsAsync(string root, string bin)
    {
        await WriteExecutableAsync(Path.Combine(bin, "bd"), $$"""
            #!/bin/sh
            root={{Q(root)}}
            printf '%s actor=%s\n' "$*" "$BEADS_ACTOR" >> "$root/bd-calls"
            if test "$1" = dolt && test "$2" = show; then
              printf '{"backend":"dolt","data_dir":"/tmp/db","database":"abc","embedded":true,"schema_version":1}\n'
            elif test "$1" = dolt && test "$2" = remote; then
              printf '[]\n'
            elif test "$1" = ready; then
              if ! test -f "$root/claimed"; then
                touch "$root/claimed"; printf 'in_progress' > "$root/status"
                printf '[{"id":"abc-1","status":"in_progress"}]\n'
              else
                printf '[]\n'
              fi
            elif test "$1" = show; then
              status=$(cat "$root/status")
              printf '[{"id":"abc-1","status":"%s"}]\n' "$status"
            elif test "$1" = update; then
              printf 'open' > "$root/status"
              printf '[{"id":"abc-1","status":"open"}]\n'
            elif test "$1" = dolt && test "$2" = push; then
              exit 0
            else
              exit 2
            fi
            """);
        await WriteExecutableAsync(Path.Combine(bin, "git"), $$"""
            #!/bin/sh
            root={{Q(root)}}
            printf '%s\n' "$*" >> "$root/git-calls"
            if test "$3" = rev-parse; then
              test "$4" = --show-toplevel && printf '%s\n' "$2" || printf 'true\n'
            elif test "$3" = status; then
              exit 0
            elif test "$3" = show-ref; then
              exit 1
            elif test "$3" = switch; then
              test "$4" = -c && printf '%s' "$5" > "$root/branch" || printf '%s' "$4" > "$root/branch"
            elif test "$3" = branch; then
              cat "$root/branch"
            else
              exit 2
            fi
            """);
        await WriteExecutableAsync(Path.Combine(bin, "opencode"), $$"""
            #!/bin/sh
            root={{Q(root)}}
            test "$1" = --mini || exit 2
            shift
            test "$1" = --prompt || exit 2
            shift
            printf '%s' "$1" > "$root/opencode-prompt"
            shift
            printf '%s\n' "$@" > "$root/opencode-arguments"
            printf '%s' "$BEADS_ACTOR" > "$root/opencode-actor"
            printf 'closed' > "$root/status"
            exit 0
            """);
        await WriteExecutableAsync(Path.Combine(bin, "tmux"), $$"""
            #!/bin/sh
            root={{Q(root)}}
            printf '%s\n' "$*" >> "$root/tmux-calls"
            if test "$1" = has-session; then
              exit 0
            elif test "$1" = display-message && test "$5" = '#{window_id}'; then
              printf '@1\n'
              exit 0
            elif test "$1" = split-window; then
              for command do :; done
              /bin/sh -c "$command" >/dev/null 2>&1 &
              printf '%s' "$!" > "$root/pane-pid"
              printf '%%1\n'
            elif test "$1" = display-message; then
              pid=$(cat "$root/pane-pid")
              kill -0 "$pid" 2>/dev/null || exit 1
              printf '%%1\n'
            elif test "$1" = send-keys; then
              pid=$(cat "$root/pane-pid")
              kill -INT "$pid" 2>/dev/null || true
              touch "$root/pane-cleaned"
            elif test "$1" = kill-pane; then
              pid=$(cat "$root/pane-pid")
              kill -TERM "$pid" 2>/dev/null || true
              touch "$root/pane-cleaned"
            else
              exit 2
            fi
            """);
    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        await File.WriteAllTextAsync(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(20, cancellation.Token);
        }
    }

    private static async Task RunAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static string FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"{executable} not found");
    }

    private static string Q(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
