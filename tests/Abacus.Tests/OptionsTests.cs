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
            "--tmux-layout", "tiled",
            "--model", "provider/model",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", first,
            "-a", "bob", second,
        ]);

        Assert.False(result.ShowHelp);
        Assert.NotNull(result.Value);
        Assert.Equal("workers", result.Value.TmuxSession);
        Assert.Equal("agents", result.Value.TmuxWindow);
        Assert.Equal("tiled", result.Value.TmuxLayout);
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
    [InlineData("--once", ExecutionMode.Once)]
    [InlineData("--drain", ExecutionMode.Drain)]
    public void ParsesFiniteExecutionModes(string option, ExecutionMode expected)
    {
        var result = Options.Parse([
            "--tmux-session", "s",
            "--model", "provider/model",
            option,
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(expected, result.Value!.ExecutionMode);
        Assert.False(result.Value.CheckOnly);
    }

    [Fact]
    public void ParsesCheckOnlyMode()
    {
        var result = Options.Parse([
            "--tmux-session", "s",
            "--model", "provider/model",
            "--check",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.True(result.Value!.CheckOnly);
        Assert.Equal(ExecutionMode.Continuous, result.Value.ExecutionMode);
    }

    [Theory]
    [InlineData("--once", "--drain")]
    [InlineData("--check", "--once")]
    [InlineData("--check", "--drain")]
    public void RejectsConflictingExecutionModes(string first, string second)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "--tmux-session", "s",
            "--model", "provider/model",
            first,
            second,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Fact]
    public void AttachedServerDoesNotRequireTmux()
    {
        var result = Options.Parse([
            "--model", "provider/model",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Null(result.Value!.TmuxSession);
        Assert.Null(result.Value.TmuxWindow);
    }

    [Fact]
    public void TmuxWindowStillRequiresTmuxSession()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "--tmux-window", "agents",
            "--model", "provider/model",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("requires --tmux-session", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TmuxLayoutStillRequiresTmuxSession()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "--tmux-layout", "tiled",
            "--model", "provider/model",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("requires --tmux-session", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("even-horizontal")]
    [InlineData("even-vertical")]
    [InlineData("main-horizontal")]
    [InlineData("main-vertical")]
    [InlineData("tiled")]
    public void ParsesSupportedTmuxLayouts(string layout)
    {
        var result = Options.Parse([
            "--tmux-session", "s",
            "--tmux-layout", layout,
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(layout, result.Value!.TmuxLayout);
    }

    [Fact]
    public void RejectsUnknownTmuxLayout()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "--tmux-session", "s",
            "--tmux-layout", "spiral",
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("must be one of", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData()]
    [InlineData("--model", "provider/model", "-a", "alice", "/tmp/a")]
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
