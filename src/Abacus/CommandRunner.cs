using System.Diagnostics;

namespace Abacus;

public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? AgentName = null);

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class CommandRunner(TextWriter log)
{
    public async Task<CommandResult> RunAsync(
        CommandSpec command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

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

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
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
