using System.ComponentModel;
using System.Diagnostics;

namespace Abacus;

/// <summary>An MP3 clip bundled with the application and played from a temporary file.</summary>
internal sealed record SoundClip(string ResourceName, string FileName)
{
    internal static SoundClip MaintenanceStarting { get; } = new("Abacus.Media.abacus_supervisor_maintanence.mp3", "abacus_supervisor_maintanence.mp3");
    internal static SoundClip ContinuationStarting { get; } = new("Abacus.Media.abacus_supervisor_continue.mp3", "abacus_supervisor_continue.mp3");
    internal static SoundClip SupervisorFailed { get; } = new("Abacus.Media.abacus_supervisor_failed.mp3", "abacus_supervisor_failed.mp3");

    /// <summary>The entrance mix played behind the interactive ASCII animation.</summary>
    internal static SoundClip Intro { get; } = new("Abacus.Media.abacus_intro.mp3", "abacus_intro.mp3");

    /// <summary>Played when an issue newly starts needing the operator's attention.</summary>
    internal static SoundClip UserAttention { get; } =
        new("Abacus.Media.abacus_attention.mp3", "abacus_attention.mp3");
}

/// <summary>A clip in flight that the caller may either let finish or stop.</summary>
internal interface ISoundPlayback : IAsyncDisposable
{
    /// <summary>True until the clip finishes or is stopped.</summary>
    bool IsPlaying { get; }

    /// <summary>Wait for the clip in the background instead of blocking the caller.</summary>
    void ContinueInBackground();
}

/// <summary>
/// Plays a bundled clip through the native macOS player or the first available
/// Linux command-line player. Playback is best effort: a missing player, a failed
/// extraction, or a failed process start leaves the caller with no audio and no
/// error, and never changes an orchestration outcome.
/// </summary>
internal sealed class SoundPlayer : ISoundPlayback
{
    private readonly Process process;
    private readonly string temporaryDirectory;

    private SoundPlayer(Process process, string temporaryDirectory)
    {
        this.process = process;
        this.temporaryDirectory = temporaryDirectory;
    }

    internal static SoundPlayer? TryStart(SoundClip clip)
    {
        var player = FindPlayer();
        if (player is null) return null;

        var directory = Path.Combine(Path.GetTempPath(), $"abacus-sound-{Guid.NewGuid():N}");
        Process? process = null;
        try
        {
            Directory.CreateDirectory(directory);
            var file = Extract(clip, Path.Combine(directory, clip.FileName));
            var startInfo = CreateStartInfo(player.Value, file);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"could not start {player.Value.Executable}");
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            return new SoundPlayer(process, directory);
        }
        catch
        {
            Stop(process);
            process?.Dispose();
            DeleteDirectory(directory);
            return null;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(AudioPlayer player, string audioPath)
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
            AudioPlayerKind.Afplay => new[] { audioPath },
            AudioPlayerKind.Ffplay => new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", audioPath },
            AudioPlayerKind.Mpv => new[] { "--no-video", "--really-quiet", audioPath },
            AudioPlayerKind.Sox => new[] { "-q", audioPath },
            _ => throw new ArgumentOutOfRangeException(nameof(player)),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public bool IsPlaying
    {
        get
        {
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop(process);
        await WaitAndCleanupAsync();
    }

    public void ContinueInBackground() => _ = WaitAndCleanupAsync();

    private async Task WaitAndCleanupAsync()
    {
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        finally
        {
            process.Dispose();
            DeleteDirectory(temporaryDirectory);
        }
    }

    private static string Extract(SoundClip clip, string destination)
    {
        using var resource = typeof(SoundPlayer).Assembly.GetManifestResourceStream(clip.ResourceName)
            ?? throw new InvalidOperationException($"missing embedded sound '{clip.ResourceName}'");
        using var file = File.Create(destination);
        resource.CopyTo(file);
        return destination;
    }

    private static AudioPlayer? FindPlayer()
    {
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/afplay"))
            return new(AudioPlayerKind.Afplay, "/usr/bin/afplay");

        if (!OperatingSystem.IsLinux()) return null;
        foreach (var (kind, executable) in new[]
        {
            (AudioPlayerKind.Ffplay, "ffplay"),
            (AudioPlayerKind.Mpv, "mpv"),
            (AudioPlayerKind.Sox, "play"),
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
        catch (Win32Exception) { }
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal enum AudioPlayerKind
{
    Afplay,
    Ffplay,
    Mpv,
    Sox,
}

internal readonly record struct AudioPlayer(AudioPlayerKind Kind, string Executable);
