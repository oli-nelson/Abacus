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

        public ValueTask DisposeAsync()
        {
            Disposals++;
            IsPlaying = false;
            return ValueTask.CompletedTask;
        }
    }
}
