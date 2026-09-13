using Abacus;

namespace Abacus.Tests;

public sealed class SoundPlayerTests
{
    [Fact]
    public void BundledClipsAreEmbeddedWithoutTheReplacedEntranceTracks()
    {
        var assembly = typeof(Program).Assembly;

        foreach (var clip in new[] { SoundClip.Intro, SoundClip.UserAttention, SoundClip.Supervisor, SoundClip.SupervisorFailed })
        {
            using var stream = assembly.GetManifestResourceStream(clip.ResourceName);
            Assert.NotNull(stream);
            Assert.True(stream.Length > 0);
        }

        Assert.DoesNotContain("Abacus.Media.abacus_jingle.mp3", assembly.GetManifestResourceNames());
        Assert.DoesNotContain("Abacus.Media.abacus_welcome.mp3", assembly.GetManifestResourceNames());
    }

    [Fact]
    public void ClipsExtractToDistinctTemporaryFileNames()
    {
        Assert.Equal("abacus_intro.mp3", SoundClip.Intro.FileName);
        Assert.Equal("Abacus.Media.abacus_intro.mp3", SoundClip.Intro.ResourceName);
        Assert.Equal("abacus_attention.mp3", SoundClip.UserAttention.FileName);
        Assert.Equal("Abacus.Media.abacus_attention.mp3", SoundClip.UserAttention.ResourceName);
    }

    [Theory]
    [InlineData(0, new[] { "/tmp/attention.mp3" })]
    [InlineData(1, new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", "/tmp/attention.mp3" })]
    [InlineData(2, new[] { "--no-video", "--really-quiet", "/tmp/attention.mp3" })]
    [InlineData(3, new[] { "-q", "/tmp/attention.mp3" })]
    public void PlayerCommandsPassTheAudioPathLiterally(
        int kind,
        string[] expectedArguments)
    {
        var command = SoundPlayer.CreateStartInfo(new((AudioPlayerKind)kind, "/player"), "/tmp/attention.mp3");

        Assert.Equal("/player", command.FileName);
        Assert.Equal(expectedArguments, command.ArgumentList);
        Assert.False(command.UseShellExecute);
        Assert.True(command.RedirectStandardOutput);
        Assert.True(command.RedirectStandardError);
    }
}
