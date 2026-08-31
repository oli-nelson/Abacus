using Abacus;

namespace Abacus.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void ParsesExactCliAndCanonicalizesWorkspaces()
    {
        var first = Path.Combine(Path.GetTempPath(), "abacus", "one", "..");
        var second = Path.Combine(Path.GetTempPath(), "abacus", "two");

        var result = Options.Parse([
            "--tmux-session", "workers",
            "--tmux-window", "agents",
            "--model", "provider/model",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", first,
            "-a", "bob", second,
        ]);

        Assert.False(result.ShowHelp);
        Assert.NotNull(result.Value);
        Assert.Equal("workers", result.Value.TmuxSession);
        Assert.Equal("agents", result.Value.TmuxWindow);
        Assert.Equal("provider/model", result.Value.Model);
        Assert.Equal("127.0.0.1:1234", result.Value.OpenCodeServer);
        Assert.False(result.Value.Verbose);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "abacus")), result.Value.Agents[0].WorkspacePath);
        Assert.Equal("bob", result.Value.Agents[1].Name);
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("--debug")]
    [InlineData("-v")]
    public void ParsesVerboseAliases(string verbosityOption)
    {
        var result = Options.Parse([
            "--tmux-session", "s",
            "--model", "provider/model",
            verbosityOption,
            "-a", "alice", "/tmp/a",
        ]);

        Assert.True(result.Value!.Verbose);
    }

    [Theory]
    [InlineData()]
    [InlineData("--tmux-session", "s", "--model", "provider/model")]
    [InlineData("--tmux-session", "s", "--tmux-window", "--model", "provider/model", "-a", "alice", "/tmp/a")]
    [InlineData("--tmux-session", "s", "-a", "alice", "/tmp/a")]
    [InlineData("--tmux-session", "s", "--model", "model", "-a", "alice", "/tmp/a")]
    [InlineData("--tmux-session", "s", "--model", "/model", "-a", "alice", "/tmp/a")]
    [InlineData("--tmux-session", "s", "--model", "provider/", "-a", "alice", "/tmp/a")]
    [InlineData("--tmux-session", "s", "--model", "one/two/three", "-a", "alice", "/tmp/a")]
    [InlineData("--tmux-session", "s", "--model", "provider/model", "--unknown", "x", "-a", "alice", "/tmp/a")]
    public void RejectsInvalidArguments(params string[] arguments)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(arguments));
    }

    [Fact]
    public void RejectsDuplicateAgentNames()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "--tmux-session", "s", "--model", "p/m",
            "-a", "alice", "/tmp/a",
            "-a", "alice", "/tmp/b",
        ]));
    }

    [Fact]
    public void RejectsEquivalentWorkspacePaths()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "--tmux-session", "s", "--model", "p/m",
            "-a", "alice", "/tmp/a/../a",
            "-a", "bob", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpDoesNotRequireOtherOptions(string argument)
    {
        Assert.True(Options.Parse([argument]).ShowHelp);
    }
}
