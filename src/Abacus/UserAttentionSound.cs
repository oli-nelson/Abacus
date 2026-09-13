namespace Abacus;

/// <summary>
/// Plays the bundled attention clip once whenever an issue newly starts needing
/// the operator's attention. Playback is best effort, requires TUI audio, and
/// never changes an orchestration outcome or desktop notification behavior.
/// </summary>
internal sealed class UserAttentionSound : IAsyncDisposable
{
    private readonly bool enabled;
    private readonly Func<ISoundPlayback?> start;
    private readonly HashSet<string> knownIssueIds = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private ISoundPlayback? playback;

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
        ISoundPlayback? started = null;
        lock (gate)
        {
            var currentIds = issues
                .Select(static issue => issue.Id)
                .ToHashSet(StringComparer.Ordinal);
            var appeared = currentIds.Any(id => !knownIssueIds.Contains(id));
            knownIssueIds.Clear();
            knownIssueIds.UnionWith(currentIds);
            if (!enabled || !appeared || playback is { IsPlaying: true }) return;

            started = start();
            playback = started;
        }

        started?.ContinueInBackground();
    }

    public async ValueTask DisposeAsync()
    {
        ISoundPlayback? current;
        lock (gate)
        {
            current = playback;
            playback = null;
        }

        if (current is not null) await current.DisposeAsync();
    }
}
