using Abacus;

namespace Abacus.Tests;

public sealed class IntroSoundTests
{
    [Fact]
    public void MixedAudioTrackIsTheOnlyIntroAudioEmbeddedInTheApplication()
    {
        var assembly = typeof(Program).Assembly;

        using var stream = assembly.GetManifestResourceStream(IntroSound.ResourceName);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
        Assert.DoesNotContain("Abacus.Media.abacus_jingle.mp3", assembly.GetManifestResourceNames());
        Assert.DoesNotContain("Abacus.Media.abacus_welcome.mp3", assembly.GetManifestResourceNames());
    }

    [Theory]
    [InlineData(0, new[] { "/tmp/intro.mp3" })]
    [InlineData(1, new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", "/tmp/intro.mp3" })]
    [InlineData(2, new[] { "--no-video", "--really-quiet", "/tmp/intro.mp3" })]
    [InlineData(3, new[] { "-q", "/tmp/intro.mp3" })]
    public void PlayerCommandsPassTheAudioPathLiterally(
        int kind,
        string[] expectedArguments)
    {
        var command = IntroSound.CreateStartInfo(new((IntroAudioPlayerKind)kind, "/player"), "/tmp/intro.mp3");

        Assert.Equal("/player", command.FileName);
        Assert.Equal(expectedArguments, command.ArgumentList);
        Assert.False(command.UseShellExecute);
        Assert.True(command.RedirectStandardOutput);
        Assert.True(command.RedirectStandardError);
    }
}
