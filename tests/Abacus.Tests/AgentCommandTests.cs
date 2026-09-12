using Abacus;

namespace Abacus.Tests;

public sealed class AgentCommandTests
{
    [Fact]
    public void ExtraArgumentsPrecedeThePromptForCodexAndClaude()
    {
        var codex = AgentCommandFactory.Create(
            AgentMode.Codex,
            "/bin/codex",
            "gpt-5.6-terra",
            "/work/repo",
            null,
            "alice • abc-1",
            "high",
            extraArguments: ["-p", "deepseek"]);
        Assert.Equal(
            [
                "--cd", "/work/repo",
                "--model", "gpt-5.6-terra",
                "--config", "model_reasoning_effort=high",
                "--approve-for-me",
                "-p", "deepseek",
                "ticket prompt",
            ],
            codex.WithPrompt("ticket prompt"));

        var claude = AgentCommandFactory.Create(
            AgentMode.Claude,
            "/bin/claude",
            "sonnet",
            "/work/repo",
            null,
            "alice • abc-1",
            "high",
            true,
            "abc-1 • ticket title",
            ["-p", "deepseek"]);
        Assert.Equal(
            [
                "--model", "sonnet",
                "--effort", "high",
                "--permission-mode", "auto",
                "--name", "alice • abc-1",
                "--remote-control", "abc-1 • ticket title",
                "-p", "deepseek",
                "ticket prompt",
            ],
            claude.WithPrompt("ticket prompt"));
    }

    [Fact]
    public void ExtraArgumentsFollowThePromptForOpenCodeModes()
    {
        var interactive = AgentCommandFactory.Create(
            AgentMode.OpenCode,
            "/bin/opencode",
            "provider/model",
            "/work/repo",
            null,
            "alice • abc-1",
            "high",
            extraArguments: ["-p", "deepseek"]);
        Assert.Equal(
            ["--prompt", "ticket prompt", "--model", "provider/model", "-p", "deepseek"],
            interactive.WithPrompt("ticket prompt"));

        var attached = AgentCommandFactory.Create(
            AgentMode.OpenCodeServer,
            "/bin/opencode",
            "provider/model",
            "/work/repo",
            "http://127.0.0.1:1234",
            "alice • abc-1",
            "high",
            extraArguments: ["-p", "deepseek"]);
        Assert.Equal(
            [
                "run", "ticket prompt",
                "--model", "provider/model",
                "--variant", "high",
                "--attach", "http://127.0.0.1:1234",
                "--dir", "/work/repo",
                "-p", "deepseek",
            ],
            attached.WithPrompt("ticket prompt"));
    }

    [Fact]
    public void OpenCodeCommandKeepsVariantOutOfModelId()
    {
        var command = AgentCommandFactory.Create(
            AgentMode.OpenCode,
            "/bin/opencode",
            "provider/model",
            "/work/repo",
            null,
            "alice • abc-1",
            "xhigh");

        Assert.Equal("/bin/opencode", command.Executable);
        Assert.Equal(
            ["--prompt", "ticket prompt", "--model", "provider/model"],
            command.WithPrompt("ticket prompt"));
    }

    [Fact]
    public void CodexCommandStartsInteractiveTuiWithWorkspaceModelAndNonBlockingPermissions()
    {
        var command = AgentCommandFactory.Create(
            AgentMode.Codex,
            "/bin/codex",
            "gpt-5.6-terra",
            "/work/repo with spaces",
            null,
            "alice • abc-1",
            "xhigh");

        Assert.Equal(
            [
                "--cd", "/work/repo with spaces",
                "--model", "gpt-5.6-terra",
                "--config", "model_reasoning_effort=xhigh",
                "--approve-for-me",
                "ticket prompt",
            ],
            command.WithPrompt("ticket prompt"));
        Assert.DoesNotContain("exec", command.WithPrompt("ticket prompt"));
    }

    [Fact]
    public void ClaudeCommandStartsInteractiveSessionWithNameModelAndAutoPermissions()
    {
        var command = AgentCommandFactory.Create(
            AgentMode.Claude,
            "/bin/claude",
            "sonnet",
            "/work/repo",
            null,
            "alice • abc-1",
            "xhigh");

        Assert.Equal(
            [
                "--model", "sonnet",
                "--effort", "xhigh",
                "--permission-mode", "auto",
                "--name", "alice • abc-1",
                "ticket prompt",
            ],
            command.WithPrompt("ticket prompt"));
        Assert.DoesNotContain("--print", command.WithPrompt("ticket prompt"));
        Assert.DoesNotContain("-p", command.WithPrompt("ticket prompt"));
    }

    [Fact]
    public void RemoteClaudeUsesIssueIdAndTitleAsRemoteSessionName()
    {
        var command = AgentCommandFactory.Create(
            AgentMode.Claude,
            "/bin/claude",
            "opus",
            "/work/repo",
            null,
            "alice • abc-1",
            "high",
            remote: true,
            remoteSessionName: "abc-1 • Add remote control");

        Assert.Contains("--remote-control", command.ArgumentsBeforePrompt);
        var remoteIndex = command.ArgumentsBeforePrompt.ToList().IndexOf("--remote-control");
        Assert.Equal("abc-1 • Add remote control", command.ArgumentsBeforePrompt[remoteIndex + 1]);
        Assert.DoesNotContain("--print", command.WithPrompt("ticket prompt"));
    }

    [Fact]
    public void OpenCodeServerCommandMatchesAttachedContract()
    {
        var command = AgentCommandFactory.Create(
            AgentMode.OpenCodeServer,
            "/bin/opencode",
            "provider/model",
            "/work/repo",
            "http://127.0.0.1:4096",
            "alice • abc-1",
            "xhigh");

        Assert.Equal(
            [
                "run", "ticket prompt",
                "--model", "provider/model",
                "--variant", "xhigh",
                "--attach", "http://127.0.0.1:4096",
                "--dir", "/work/repo",
            ],
            command.WithPrompt("ticket prompt"));
    }

    [Theory]
    [InlineData(AgentMode.OpenCode, "opencode")]
    [InlineData(AgentMode.OpenCodeServer, "opencode")]
    [InlineData(AgentMode.Codex, "codex")]
    [InlineData(AgentMode.Claude, "claude")]
    public void SelectsExpectedExecutable(AgentMode mode, string expected)
    {
        Assert.Equal(expected, AgentCommandFactory.ExecutableName(mode));
    }
}
