using Abacus;

namespace Abacus.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void ParsesNewMultiAgentRepositoryInitialization()
    {
        var result = Options.Parse([
            "new",
            "sample-project",
            "--agents", "4",
        ]);

        var initialization = Assert.IsType<NewMultiAgentRepositoryOptions>(
            result.NewMultiAgentRepository);
        Assert.Equal("sample-project", initialization.ProjectName);
        Assert.Equal(4, initialization.AgentCount);
        Assert.Null(result.Value);
        Assert.False(result.ShowHelp);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("new", "sample-project")]
    [InlineData("new", "sample-project", "--agents", "0")]
    [InlineData("new", "sample-project", "--agents", "many")]
    [InlineData("new", "../sample-project", "--agents", "2")]
    [InlineData("--verbose", "new", "sample-project", "2")]
    public void RejectsInvalidNewMultiAgentRepositoryInitialization(params string[] arguments)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(arguments));
    }

    [Fact]
    public void ParsesExactCliAndCanonicalizesWorkspaces()
    {
        var first = Path.Combine(Path.GetTempPath(), "abacus", "one", "..");
        var second = Path.Combine(Path.GetTempPath(), "abacus", "two");

        var result = Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--tmux-window", "agents",
            "--tmux-layout", "tiled",
            "--model", "provider/model",
            "--effort", "xhigh",
            "--mode", "opencode-server", "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", first,
            "-a", "bob", second,
        ]);

        Assert.False(result.ShowHelp);
        Assert.NotNull(result.Value);
        Assert.Equal("workers", result.Value.TmuxSession);
        Assert.Equal("agents", result.Value.TmuxWindow);
        Assert.Equal("tiled", result.Value.TmuxLayout);
        Assert.Equal("provider/model", result.Value.Model);
        Assert.Equal("xhigh", result.Value.Effort);
        Assert.Equal("127.0.0.1:1234", result.Value.OpenCodeServer);
        Assert.Equal(AgentMode.OpenCodeServer, result.Value.AgentMode);
        Assert.False(result.Value.Verbose);
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "abacus")), result.Value.Agents[0].WorkspacePath);
        Assert.Equal("bob", result.Value.Agents[1].Name);
    }

    [Theory]
    [InlineData("opencode", "provider/model", AgentMode.OpenCode)]
    [InlineData("codex", "gpt-5.6-terra", AgentMode.Codex)]
    [InlineData("claude", "sonnet", AgentMode.Claude)]
    public void ParsesPaneHostedAgentModes(string value, string model, AgentMode expected)
    {
        var result = Options.Parse([
            "run",
            "--mode", value,
            "--tmux-session", "workers",
            "--model", model,
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(expected, result.Value!.AgentMode);
    }

    [Fact]
    public void DefaultsToOpenCodeMode()
    {
        var result = Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(AgentMode.OpenCode, result.Value!.AgentMode);
        Assert.Equal("high", result.Value.Effort);
        Assert.False(result.Value.Remote);
        Assert.Equal(NotificationMode.Off, result.Value.NotificationMode);
        Assert.False(result.Value.NotificationSound);
        Assert.Equal(8, result.Value.LatestCommentCount);
        Assert.Null(result.Value.TmuxLayout);
        Assert.Equal("tiled", result.Value.EffectiveTmuxLayout);
    }

    [Fact]
    public void ParsesLatestCommentCount()
    {
        var result = Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--latest-comments", "24",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(24, result.Value!.LatestCommentCount);
    }

    [Fact]
    public void ParsesAdditionalAgentPrompt()
    {
        var result = Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--append-prompt", "Run the focused integration checks before merging.",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(
            "Run the focused integration checks before merging.",
            result.Value!.AppendAgentPrompt);
    }

    [Fact]
    public void RejectsDuplicateAdditionalAgentPrompt()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--append-prompt", "first",
            "--append-prompt", "second",
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Fact]
    public void RejectsEmptyAdditionalAgentPrompt()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--append-prompt", "   ",
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("many")]
    public void RejectsInvalidLatestCommentCount(string count)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--latest-comments", count,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Fact]
    public void RejectsDuplicateLatestCommentCount()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--latest-comments", "8",
            "--latest-comments", "12",
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("off", NotificationMode.Off)]
    [InlineData("attention", NotificationMode.Attention)]
    [InlineData("all", NotificationMode.All)]
    public void ParsesDesktopNotificationModes(string value, NotificationMode expected)
    {
        var arguments = new List<string>
        {
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--notify", value,
        };
        if (expected is not NotificationMode.Off)
        {
            arguments.Add("--notify-sound");
        }

        arguments.AddRange(["-a", "alice", "/tmp/a"]);
        var result = Options.Parse(arguments);

        Assert.Equal(expected, result.Value!.NotificationMode);
        Assert.Equal(expected is not NotificationMode.Off, result.Value.NotificationSound);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("true")]
    [InlineData("ATTENTION")]
    public void RejectsUnknownDesktopNotificationMode(string value)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--notify", value,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Fact]
    public void NotificationSoundRequiresEnabledNotifications()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--notify-sound",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("requires --notify", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateDesktopNotificationMode()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--notify", "attention",
            "--notify", "all",
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Fact]
    public void ParsesRemoteForClaudeMode()
    {
        var result = Options.Parse([
            "run",
            "--mode", "claude",
            "--tmux-session", "workers",
            "--model", "sonnet",
            "--remote-control",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.True(result.Value!.Remote);
    }

    [Fact]
    public void ParsesDispatchFiltersAndTicketTimeout()
    {
        var result = Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--label", "abacus-ready",
            "--label", "team:rendering",
            "--exclude-label", "needs-human",
            "--exclude-label", "on-hold",
            "--type", "bug,task",
            "--priority", "1",
            "--ticket-timeout", "45m",
            "-a", "alice", "/tmp/a",
        ]);

        var filters = Assert.IsType<DispatchFilters>(result.Value!.DispatchFilters);
        Assert.Equal(["abacus-ready", "team:rendering"], filters.Labels);
        Assert.Equal(["needs-human", "on-hold"], filters.ExcludedLabels);
        Assert.Equal("bug,task", filters.IssueType);
        Assert.Equal(1, filters.Priority);
        Assert.Equal(TimeSpan.FromMinutes(45), result.Value.TicketTimeout);
    }

    [Theory]
    [InlineData("0s")]
    [InlineData("1d")]
    [InlineData("1.5h")]
    [InlineData("forever")]
    [InlineData("999999999999999999999h")]
    public void RejectsInvalidTicketTimeout(string timeout)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--ticket-timeout", timeout,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("5")]
    [InlineData("high")]
    public void RejectsInvalidDispatchPriority(string priority)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--priority", priority,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("--type", "bug", "--type", "task")]
    [InlineData("--priority", "1", "--priority", "2")]
    [InlineData("--ticket-timeout", "1h", "--ticket-timeout", "2h")]
    public void RejectsDuplicateSingularDispatchAndTimeoutOptions(
        string firstOption,
        string firstValue,
        string secondOption,
        string secondValue)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            firstOption, firstValue,
            secondOption, secondValue,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("opencode-server")]
    public void RejectsRemoteForUnsupportedModes(string mode)
    {
        var arguments = new List<string>
        {
            "run",
            "--mode", mode,
            "--model", "provider/model",
            "--remote-control",
        };
        if (mode == "opencode-server")
        {
            arguments.AddRange(["--opencode-server", "127.0.0.1:1234"]);
        }
        else
        {
            arguments.AddRange(["--tmux-session", "workers"]);
        }

        arguments.AddRange(["-a", "alice", "/tmp/a"]);
        var exception = Assert.Throws<OptionsException>(() => Options.Parse(arguments));

        Assert.Contains("--mode claude", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("extra high")]
    [InlineData("high#other")]
    public void RejectsInvalidEffort(string effort)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--model", "provider/model",
            "--effort", effort,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("-v")]
    public void ParsesVerboseAliases(string verbosityOption)
    {
        var result = Options.Parse([
            "run",
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
            "run",
            "--tmux-session", "s",
            "--model", "provider/model",
            option,
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(expected, result.Value!.ExecutionMode);
        Assert.False(result.Value.CheckOnly);
    }

    [Fact]
    public void ParsesPreflightCommand()
    {
        var result = Options.Parse([
            "preflight",
            "--tmux-session", "s",
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.True(result.Value!.CheckOnly);
        Assert.Equal(ExecutionMode.Continuous, result.Value.ExecutionMode);
    }

    [Theory]
    [InlineData("--once", "--drain")]

    public void RejectsConflictingExecutionModes(string first, string second)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
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
            "run",
            "--model", "provider/model",
            "--mode", "opencode-server", "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Null(result.Value!.TmuxSession);
        Assert.Null(result.Value.TmuxWindow);
        Assert.Equal(AgentMode.OpenCodeServer, result.Value.AgentMode);
        Assert.False(result.Value.UsesTmux);
    }

    [Fact]
    public void ExplicitOpenCodeServerModeParsesWithoutTmux()
    {
        var result = Options.Parse([
            "run",
            "--mode", "opencode-server",
            "--model", "provider/model",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Equal(AgentMode.OpenCodeServer, result.Value!.AgentMode);
        Assert.Null(result.Value.TmuxSession);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("opencode")]
    public void PaneHostedModesUseAnImplicitTmuxSession(string mode)
    {
        var model = mode == "opencode" ? "provider/model" : "model";
        var result = Options.Parse([
            "run",
            "--mode", mode,
            "--model", model,
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Null(result.Value!.TmuxSession);
        Assert.True(result.Value.UsesTmux);
    }

    [Fact]
    public void ExplicitOpenCodeServerModeRequiresAddress()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--mode", "opencode-server",
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("requires --opencode-server", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerAddressIsRejectedForOtherExplicitModes()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--mode", "codex",
            "--tmux-session", "workers",
            "--model", "gpt-5.6-terra",
            "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("only be used", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex", "gpt model")]
    [InlineData("claude", "sonnet model")]
    public void CodexAndClaudeModelsRejectWhitespace(string mode, string model)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--mode", mode,
            "--tmux-session", "workers",
            "--model", model,
            "-a", "alice", "/tmp/a",
        ]));
    }

    [Fact]
    public void TmuxWindowRequestsAnImplicitSessionForServerMode()
    {
        var result = Options.Parse([
            "run",
            "--tmux-window", "agents",
            "--model", "provider/model",
            "--mode", "opencode-server", "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Null(result.Value!.TmuxSession);
        Assert.Equal("agents", result.Value.TmuxWindow);
        Assert.True(result.Value.UsesTmux);
        Assert.Equal("tiled", result.Value.EffectiveTmuxLayout);
    }

    [Fact]
    public void DirectServerModeDoesNotApplyTheDefaultTmuxLayout()
    {
        var result = Options.Parse([
            "run",
            "--model", "provider/model",
            "--mode", "opencode-server", "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.False(result.Value!.UsesTmux);
        Assert.Null(result.Value.TmuxLayout);
        Assert.Null(result.Value.EffectiveTmuxLayout);
    }

    [Fact]
    public void TmuxLayoutRequestsAnImplicitSessionForServerMode()
    {
        var result = Options.Parse([
            "run",
            "--tmux-layout", "tiled",
            "--model", "provider/model",
            "--mode", "opencode-server", "--opencode-server", "127.0.0.1:1234",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.Null(result.Value!.TmuxSession);
        Assert.Equal("tiled", result.Value.TmuxLayout);
        Assert.True(result.Value.UsesTmux);
    }

    [Fact]
    public void ParsesDisownedImplicitTmuxSession()
    {
        var result = Options.Parse([
            "run",
            "--model", "provider/model",
            "--disown-tmux-session",
            "-a", "alice", "/tmp/a",
        ]);

        Assert.True(result.Value!.DisownTmuxSession);
        Assert.True(result.Value.UsesTmux);
        Assert.Null(result.Value.TmuxSession);
    }

    [Fact]
    public void RejectsDisownWithExplicitTmuxSession()
    {
        var exception = Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "workers",
            "--disown-tmux-session",
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("cannot be combined", exception.Message, StringComparison.Ordinal);
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
            "run",
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
            "run",
            "--tmux-session", "s",
            "--tmux-layout", "spiral",
            "--model", "provider/model",
            "-a", "alice", "/tmp/a",
        ]));

        Assert.Contains("must be one of", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--tmux-session", "s", "--model", "provider/model")]
    [InlineData("run", "--tmux-session", "s", "--tmux-window", "--model", "provider/model", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "--model", "model", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "--model", "/model", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "--model", "provider/", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "--model", "provider/model#high", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "--model", "one/two/three", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--mode", "invalid", "--tmux-session", "s", "--model", "provider/model", "-a", "alice", "/tmp/a")]
    [InlineData("run", "--tmux-session", "s", "--model", "provider/model", "--unknown", "x", "-a", "alice", "/tmp/a")]
    public void RejectsInvalidArguments(params string[] arguments)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(arguments));
    }

    [Fact]
    public void RejectsDuplicateAgentNames()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
            "--tmux-session", "s", "--model", "p/m",
            "-a", "alice", "/tmp/a",
            "-a", "alice", "/tmp/b",
        ]));
    }

    [Fact]
    public void RejectsEquivalentWorkspacePaths()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run",
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

    [Fact]
    public void InstallSkillsDoesNotRequireAgentOptions()
    {
        var result = Options.Parse(["skills", "install"]);

        Assert.True(result.InstallSkills);
        Assert.False(result.ShowHelp);
        Assert.Null(result.Value);
    }

    [Fact]
    public void InstallSkillsCannotBeCombinedWithAgentOptions()
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["skills", "install", "--verbose"]));
    }

    [Fact]
    public void InitDoesNotRequireAgentOptions()
    {
        var result = Options.Parse(["init"]);
        Assert.True(result.InitializeRepository);
        Assert.Null(result.Value);
    }

    [Fact]
    public void HealthDoesNotRequireAgentOptions()
    {
        var result = Options.Parse(["health"]);

        Assert.True(result.ShowHealth);
        Assert.False(result.ShowHelp);
        Assert.Null(result.Value);
    }

    [Fact]
    public void HealthCannotBeCombinedWithAgentOptions()
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["health", "--verbose"]));
    }

    [Fact]
    public void ModelsDoesNotRequireAgentOptions()
    {
        var result = Options.Parse(["models"]);

        Assert.True(result.ShowModels);
        Assert.False(result.ShowHelp);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ModelsCannotBeCombinedWithOtherOptions()
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["models", "--verbose"]));
    }

    [Theory]
    [InlineData("branches", "prune")]
    [InlineData("attention", "list")]
    public void RepositoryCommandsDoNotRequireAgentOptions(string group, string command)
    {
        var result = Options.Parse([group, command]);

        Assert.Equal(command == "prune", result.PruneClosedBranches);
        Assert.Equal(command == "list", result.ListUserAttention);
        Assert.False(result.ShowHelp);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("branches", "prune")]
    [InlineData("attention", "list")]
    public void RepositoryCommandsCannotBeCombinedWithOtherOptions(string group, string command)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([group, command, "--verbose"]));
    }

    [Fact]
    public void ResolveDoesNotRequireAgentOptions()
    {
        var result = Options.Parse(["attention", "resolve", "ab-123"]);

        var resolution = Assert.IsType<AttentionResolutionOptions>(result.AttentionResolution);
        Assert.Equal("ab-123", resolution.IssueId);
        Assert.Null(resolution.Message);
        Assert.False(resolution.Reopen);
        Assert.False(result.ShowHelp);
        Assert.Null(result.Value);
    }

    [Fact]
    public void PreviousResolveAttentionNameIsRejected()
    {
        Assert.Throws<OptionsException>(() =>
            Options.Parse(["--resolve-attention", "ab-123"]));
    }

    [Fact]
    public void ResolveAttentionAcceptsAnOptionalQuotedMessage()
    {
        var result = Options.Parse([
            "attention", "resolve",
            "ab-123",
            "--message", "Use option A after QA",
        ]);

        var resolution = Assert.IsType<AttentionResolutionOptions>(result.AttentionResolution);
        Assert.Equal("Use option A after QA", resolution.Message);
        Assert.False(resolution.Reopen);
    }

    [Theory]
    [InlineData("--reopen")]
    [InlineData("--message", "Approved option A", "--reopen")]
    [InlineData("--reopen", "--message", "Approved option A")]
    public void ResolveAttentionAcceptsReopenWithOrWithoutAMessage(params string[] trailingArguments)
    {
        var result = Options.Parse([
            "attention", "resolve",
            "ab-123",
            .. trailingArguments,
        ]);

        var resolution = Assert.IsType<AttentionResolutionOptions>(result.AttentionResolution);
        Assert.Equal("ab-123", resolution.IssueId);
        Assert.Equal(
            trailingArguments.SingleOrDefault(static argument => argument is not ("--reopen" or "--message")),
            resolution.Message);
        Assert.True(resolution.Reopen);
    }

    [Theory]
    [InlineData("attention", "resolve")]
    [InlineData("-r")]
    [InlineData("attention", "resolve", "ab-123", "message", "extra")]
    [InlineData("attention", "resolve", "ab-123", "--reopen", "--reopen")]
    [InlineData("--verbose", "attention", "resolve", "ab-123")]
    public void ResolveAttentionRejectsMissingOrCombinedArguments(params string[] arguments)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(arguments));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAttentionRejectsAnEmptyMessage(string message)
    {
        Assert.Throws<OptionsException>(() =>
            Options.Parse(["attention", "resolve", "ab-123", "--message", message]));
    }
}
