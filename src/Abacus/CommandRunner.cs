using System.Diagnostics;

namespace Abacus;

public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? AgentName = null,
    int? MaxOutputCharacters = null);

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class CommandRunner(
    TextWriter log,
    TimeSpan? commandTimeout = null,
    TimeSpan? terminationTimeout = null)
{
    private readonly TimeSpan timeout = commandTimeout ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan killTimeout = terminationTimeout ?? TimeSpan.FromSeconds(5);

    public async Task<CommandResult> RunAsync(
        CommandSpec command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MaxOutputCharacters is <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.MaxOutputCharacters));

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (command.Environment is not null)
        {
            foreach (var (key, value) in command.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var prefix = command.AgentName is null ? "abacus" : command.AgentName;
        await log.DebugCommandAsync(
            prefix,
            $"{command.FileName} {FormatArgumentsForLog(command.Arguments)}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"failed to start '{command.FileName}'");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CommandStartException(command.FileName, exception);
        }

        using var deadline = new CancellationTokenSource(timeout);
        using var outputExceeded = new CancellationTokenSource();
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token, outputExceeded.Token);
        var stdout = ReadOutputAsync(process.StandardOutput);
        var stderr = ReadOutputAsync(process.StandardError);

        async Task<string> ReadOutputAsync(StreamReader reader)
        {
            var output = new System.Text.StringBuilder();
            var buffer = new char[8192];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), commandCancellation.Token);
                if (count == 0) return output.ToString();
                if (command.MaxOutputCharacters is int limit && output.Length > limit - count)
                {
                    outputExceeded.Cancel();
                    throw new CommandOutputLimitException(command.FileName, limit);
                }
                output.Append(buffer, 0, count);
            }
        }

        try
        {
            await process.WaitForExitAsync(commandCancellation.Token);
            // A child can inherit the pipes after the original process exits.
            // Keep draining under the same deadline rather than waiting forever.
            await Task.WhenAll(stdout, stderr).WaitAsync(commandCancellation.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or CommandOutputLimitException)
        {
            TryKillProcessTree(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(killTimeout);
            }
            catch (TimeoutException)
            {
                // The original cancellation or deadline remains the useful failure.
            }

            // Observe both reader tasks, including when one stream exceeded its cap.
            try { await Task.WhenAll(stdout, stderr); }
            catch (Exception readException) when (readException is OperationCanceledException or CommandOutputLimitException) { }

            cancellationToken.ThrowIfCancellationRequested();
            if (outputExceeded.IsCancellationRequested)
                throw new CommandOutputLimitException(command.FileName, command.MaxOutputCharacters!.Value);
            if (deadline.IsCancellationRequested)
            {
                throw new CommandTimeoutException(command.FileName, timeout);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        var result = new CommandResult(
            process.ExitCode,
            await stdout,
            await stderr);

        if (!result.Succeeded)
        {
            await log.DebugCommandAsync(prefix, $"{command.FileName} exited {result.ExitCode}");
        }

        return result;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static string FormatArgumentsForLog(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(static argument =>
            argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument));
}

public sealed class CommandStartException(string fileName, Exception innerException)
    : Exception($"could not start '{fileName}': {innerException.Message}", innerException);

public sealed class CommandTimeoutException(string fileName, TimeSpan timeout)
    : Exception($"'{fileName}' exceeded its {timeout.TotalSeconds:0.###}-second command deadline");

public sealed class CommandOutputLimitException(string fileName, int limit)
    : Exception($"'{fileName}' exceeded its {limit}-character per-stream output limit");
