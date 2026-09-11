using System.Diagnostics;

namespace Abacus;

internal sealed class IntroSound : IAsyncDisposable
{
    internal const string ResourceName = "Abacus.Media.abacus_intro.mp3";

    private readonly Process process;
    private readonly string temporaryDirectory;

    private IntroSound(Process process, string temporaryDirectory)
    {
        this.process = process;
        this.temporaryDirectory = temporaryDirectory;
    }

    internal static IntroSound? TryStart()
    {
        var player = FindPlayer();
        if (player is null) return null;

        var directory = Path.Combine(Path.GetTempPath(), $"abacus-intro-{Guid.NewGuid():N}");
        Process? process = null;
        try
        {
            Directory.CreateDirectory(directory);
            var file = Extract(ResourceName, Path.Combine(directory, "abacus_intro.mp3"));
            var startInfo = CreateStartInfo(player.Value, file);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"could not start {player.Value.Executable}");
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            return new IntroSound(process, directory);
        }
        catch
        {
            Stop(process);
            process?.Dispose();
            DeleteDirectory(directory);
            return null;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(IntroAudioPlayer player, string audioPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = player.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in player.Kind switch
        {
            IntroAudioPlayerKind.Afplay => new[] { audioPath },
            IntroAudioPlayerKind.Ffplay => new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", audioPath },
            IntroAudioPlayerKind.Mpv => new[] { "--no-video", "--really-quiet", audioPath },
            IntroAudioPlayerKind.Sox => new[] { "-q", audioPath },
            _ => throw new ArgumentOutOfRangeException(nameof(player)),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public async ValueTask DisposeAsync()
    {
        Stop(process);
        await WaitAndCleanupAsync();
    }

    internal void ContinueInBackground() => _ = WaitAndCleanupAsync();

    private async Task WaitAndCleanupAsync()
    {
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally
        {
            process.Dispose();
            DeleteDirectory(temporaryDirectory);
        }
    }

    private static string Extract(string resourceName, string destination)
    {
        using var resource = typeof(IntroSound).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"missing embedded intro sound '{resourceName}'");
        using var file = File.Create(destination);
        resource.CopyTo(file);
        return destination;
    }

    private static IntroAudioPlayer? FindPlayer()
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/afplay"))
            return new(IntroAudioPlayerKind.Afplay, "/usr/bin/afplay");

        if (!OperatingSystem.IsLinux()) return null;
        foreach (var (kind, executable) in new[]
        {
            (IntroAudioPlayerKind.Ffplay, "ffplay"),
            (IntroAudioPlayerKind.Mpv, "mpv"),
            (IntroAudioPlayerKind.Sox, "play"),
        })
        {
            if (FindOnPath(executable) is { } path) return new(kind, path);
        }
        return null;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, executable);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal enum IntroAudioPlayerKind
{
    Afplay,
    Ffplay,
    Mpv,
    Sox,
}

internal readonly record struct IntroAudioPlayer(IntroAudioPlayerKind Kind, string Executable);
