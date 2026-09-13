using Abacus;

namespace Abacus.Tests;

public sealed class UserAttentionSoundTests
{
    [Fact]
    public async Task EachNewlyObservedIssuePlaysTheClipOnce()
    {
        var playbacks = new List<FakePlayback>();
        var sound = new UserAttentionSound(enabled: true, () => Track(playbacks));

        sound.Changed([Issue("abc-1")]);
        sound.Changed([Issue("abc-1")]);
        playbacks[0].IsPlaying = false;
        sound.Changed([Issue("abc-1"), Issue("abc-2")]);
        await sound.DisposeAsync();

        Assert.Equal(2, playbacks.Count);
        Assert.All(playbacks, playback => Assert.Equal(1, playback.ContinuedInBackground));
    }

    [Fact]
    public async Task ResolvedThenRestoredAttentionPlaysAgain()
    {
        var playbacks = new List<FakePlayback>();
        var sound = new UserAttentionSound(enabled: true, () => Track(playbacks));

        sound.Changed([Issue("abc-1")]);
        sound.Changed([]);
        playbacks[0].IsPlaying = false;
        sound.Changed([Issue("abc-1")]);
        await sound.DisposeAsync();

        Assert.Equal(2, playbacks.Count);
    }

    [Fact]
    public async Task ClipAlreadyPlayingIsNotRestarted()
    {
        var playbacks = new List<FakePlayback>();
        var sound = new UserAttentionSound(enabled: true, () => Track(playbacks));

        sound.Changed([Issue("abc-1")]);
        var playing = Assert.Single(playbacks);
        sound.Changed([Issue("abc-1"), Issue("abc-2")]);
        Assert.Single(playbacks);

        playing.IsPlaying = false;
        sound.Changed([Issue("abc-1"), Issue("abc-2"), Issue("abc-3")]);
        await sound.DisposeAsync();

        Assert.Equal(2, playbacks.Count);
    }

    [Fact]
    public async Task DisabledTuiAudioNeverStartsOrStopsAPlayback()
    {
        var starts = 0;
        var sound = new UserAttentionSound(
            enabled: false,
            () =>
            {
                starts++;
                return new FakePlayback();
            });

        sound.Changed([Issue("abc-1")]);
        sound.Changed([]);
        sound.Changed([Issue("abc-1"), Issue("abc-2")]);
        await sound.DisposeAsync();

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task DisposalStopsTheClipThatIsStillPlaying()
    {
        var playbacks = new List<FakePlayback>();
        var sound = new UserAttentionSound(enabled: true, () => Track(playbacks));

        sound.Changed([Issue("abc-1")]);
        await sound.DisposeAsync();
        await sound.DisposeAsync();

        var playback = Assert.Single(playbacks);
        Assert.Equal(1, playback.Disposals);
    }

    [Fact]
    public async Task MissingPlayerLeavesAttentionSilentAndNeverFails()
    {
        var sound = new UserAttentionSound(enabled: true, () => null);

        sound.Changed([Issue("abc-1")]);
        await sound.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupervisorPreemptsAttentionAndSuppressesItUntilFinished(bool failed)
    {
        var attention = new List<FakePlayback>();
        var supervisor = new FakePlayback();
        var clip = failed ? SoundClip.SupervisorFailed : SoundClip.Supervisor;
        await using var sound = new UserAttentionSound(true, () => Track(attention))
        {
            StartSupervisorClip = requested =>
            {
                Assert.Equal(clip, requested);
                Assert.False(Assert.Single(attention).IsPlaying);
                return supervisor;
            },
        };
        sound.Changed([Issue("abc-1")]);
        await sound.PlaySupervisorAsync(clip);
        Assert.Equal(1, attention[0].Disposals);
        Assert.Equal(1, supervisor.ContinuedInBackground);
        sound.Changed([Issue("abc-1"), Issue("abc-2")]);
        Assert.Single(attention);
        supervisor.IsPlaying = false;
        sound.Changed([Issue("abc-1"), Issue("abc-2")]); // No delayed replay.
        Assert.Single(attention);
        sound.Changed([Issue("abc-1"), Issue("abc-2"), Issue("abc-3")]);
        Assert.Equal(2, attention.Count);
    }

    [Fact]
    public async Task AttentionCannotStartWhilePreemptedPlaybackIsStopping()
    {
        var attention = new FakePlayback { StopCompletion = new TaskCompletionSource() };
        var starts = 0;
        var supervisor = new FakePlayback();
        await using var sound = new UserAttentionSound(true, () => { starts++; return attention; })
        { StartSupervisorClip = _ => supervisor };
        sound.Changed([Issue("abc-1")]);
        var starting = sound.PlaySupervisorAsync(SoundClip.Supervisor);
        Assert.False(starting.IsCompleted);
        sound.Changed([Issue("abc-2")]);
        Assert.Equal(1, starts);
        attention.StopCompletion.SetResult();
        await starting;
        sound.Changed([Issue("abc-3")]);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task SupervisorFirstSuppressesAttentionAndIsStoppedOnDisposal()
    {
        var supervisor = new FakePlayback();
        var starts = 0;
        var sound = new UserAttentionSound(true, () => { starts++; return new FakePlayback(); })
        { StartSupervisorClip = _ => supervisor };
        await sound.PlaySupervisorAsync(SoundClip.Supervisor);
        sound.Changed([Issue("abc-1")]);
        Assert.Equal(0, starts);
        await sound.DisposeAsync();
        await sound.DisposeAsync();
        Assert.Equal(1, supervisor.Disposals);
    }

    [Fact]
    public async Task MissingSupervisorPlayerDoesNotLeaveAttentionSuppressed()
    {
        var starts = 0;
        await using var sound = new UserAttentionSound(true, () => { starts++; return new FakePlayback(); })
        { StartSupervisorClip = _ => null };
        await sound.PlaySupervisorAsync(SoundClip.Supervisor);
        sound.Changed([Issue("abc-1")]);
        Assert.Equal(1, starts);
    }

    private static FakePlayback Track(List<FakePlayback> playbacks)
    {
        var playback = new FakePlayback();
        playbacks.Add(playback);
        return playback;
    }

    private static BeadsIssue Issue(string id) => new(id, IssueStatus.Open);

    private sealed class FakePlayback : ISoundPlayback
    {
        public bool IsPlaying { get; set; } = true;

        public int ContinuedInBackground { get; private set; }

        public int Disposals { get; private set; }

        public void ContinueInBackground() => ContinuedInBackground++;

        public TaskCompletionSource? StopCompletion { get; init; }

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (StopCompletion is not null) await StopCompletion.Task;
            IsPlaying = false;
        }
    }
}
