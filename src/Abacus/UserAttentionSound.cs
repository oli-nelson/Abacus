namespace Abacus;

/// <summary>
/// Plays the bundled attention clip once whenever an issue newly starts needing
/// the operator's attention, yielding to supervisor clips. Playback is best effort,
/// requires TUI audio, and never changes an orchestration outcome or desktop
/// notification behavior.
/// </summary>
internal sealed class UserAttentionSound : IAsyncDisposable
{
    private readonly bool enabled;
    private readonly Func<ISoundPlayback?> start;
    private readonly HashSet<string> knownIssueIds = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private ISoundPlayback? playback;
    private ISoundPlayback? supervisorPlayback;
    private bool supervisorStarting;
    private bool disposed;
    internal Func<SoundClip, ISoundPlayback?> StartSupervisorClip { get; init; } = SoundPlayer.TryStart;

    internal UserAttentionSound(bool enabled)
        : this(enabled, StartClip)
    {
    }

    internal UserAttentionSound(bool enabled, Func<ISoundPlayback?> start)
    {
        this.enabled = enabled;
        this.start = start;
    }

    private static ISoundPlayback? StartClip() => SoundPlayer.TryStart(SoundClip.UserAttention);

    /// <summary>
    /// Records the current attention snapshot and plays the clip for issues that
    /// the previous one did not carry. An issue that keeps needing attention never
    /// replays, and neither does a clip that is still playing, so a burst of new
    /// attention never stacks the same clip.
    /// </summary>
    internal void Changed(IReadOnlyList<BeadsIssue> issues)
    {
        lock (gate)
        {
            var currentIds = issues
                .Select(static issue => issue.Id)
                .ToHashSet(StringComparer.Ordinal);
            var appeared = currentIds.Any(id => !knownIssueIds.Contains(id));
            knownIssueIds.Clear();
            knownIssueIds.UnionWith(currentIds);
            if (disposed || !enabled || !appeared || supervisorStarting
                || supervisorPlayback is { IsPlaying: true } || playback is { IsPlaying: true }) return;

            playback = start();
            playback?.ContinueInBackground();
        }
    }

    private readonly SemaphoreSlim supervisorAudio = new(1, 1);

    // Serialize calls from both roles, including maintenance failure announcements.
    internal async Task PlaySupervisorAsync(SoundClip clip)
    {
        await supervisorAudio.WaitAsync();
        try { await PlaySupervisorCoreAsync(clip); }
        finally { supervisorAudio.Release(); }
    }

    // Reserve priority before awaiting
    // cleanup so dashboard refreshes cannot start attention audio in the gap.
    private async Task PlaySupervisorCoreAsync(SoundClip clip)
    {
        ISoundPlayback? attention;
        ISoundPlayback? previousSupervisor;
        lock (gate)
        {
            if (disposed || !enabled) return;
            supervisorStarting = true;
            attention = playback;
            playback = null;
            previousSupervisor = supervisorPlayback;
            supervisorPlayback = null;
        }
        try
        {
            if (attention is not null) await attention.DisposeAsync();
            if (previousSupervisor is not null) await previousSupervisor.DisposeAsync();
            lock (gate)
            {
                if (disposed) return;
                supervisorPlayback = StartSupervisorClip(clip);
                supervisorPlayback?.ContinueInBackground();
            }
        }
        finally
        {
            lock (gate) supervisorStarting = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ISoundPlayback? current;
        ISoundPlayback? supervisor;
        lock (gate)
        {
            disposed = true;
            supervisor = supervisorPlayback;
            supervisorPlayback = null;
            current = playback;
            playback = null;
        }

        if (current is not null) await current.DisposeAsync();
        if (supervisor is not null) await supervisor.DisposeAsync();
    }
}
