using Abacus;

namespace Abacus.Tests;

public sealed class CommandSyntaxTests
{
    private static readonly string[] RunConfiguration =
        ["--mode", "codex", "--model", "model", "--tmux-session", "workers", "--agent", "alice", "/tmp/alice"];

    [Fact]
    public void BareInvocationIsHelpAndRunIsExplicit()
    {
        Assert.True(Options.Parse([]).ShowHelp);
        Assert.Throws<OptionsException>(() => Options.Parse(RunConfiguration));
        Assert.NotNull(Options.Parse(["run", .. RunConfiguration]).Value);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("preflight")]
    [InlineData("new")]
    [InlineData("init")]
    [InlineData("skills")]
    [InlineData("skills", "install")]
    [InlineData("health")]
    [InlineData("info")]
    [InlineData("models")]
    [InlineData("version")]
    [InlineData("branches")]
    [InlineData("branches", "prune")]
    [InlineData("attention")]
    [InlineData("attention", "list")]
    [InlineData("attention", "resolve")]
    [InlineData("targets")]
    [InlineData("targets", "check")]
    [InlineData("targets", "set")]
    public void CommandHelpIsScopedWithoutPrerequisites(params string[] command)
    {
        var longHelp = Options.Parse([.. command, "--help"]);
        var shortHelp = Options.Parse([.. command, "-h"]);
        var topicHelp = Options.Parse(["help", .. command]);
        Assert.True(longHelp.ShowHelp);
        Assert.Null(longHelp.Value);
        Assert.Equal(longHelp.HelpText, shortHelp.HelpText);
        Assert.Equal(longHelp.HelpText, topicHelp.HelpText);
        Assert.Contains("Usage: abacus " + string.Join(" ", command), longHelp.HelpText);
    }

    [Theory]
    [InlineData("--init")]
    [InlineData("--init-new-multi-agent-repo")]
    [InlineData("--install-skills")]
    [InlineData("--health")]
    [InlineData("--info")]
    [InlineData("--models")]
    [InlineData("--prune-closed-branches")]
    [InlineData("--list-user-attention")]
    [InlineData("--resolve")]
    [InlineData("-r")]
    [InlineData("--check-ticket-targets")]
    [InlineData("--set-ticket-target")]
    [InlineData("--check")]
    public void LegacyOperationFlagsAreRejectedEvenWithHelp(string oldCommand)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([oldCommand]));
        Assert.Throws<OptionsException>(() => Options.Parse([oldCommand, "--help"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", .. RunConfiguration, oldCommand]));
    }

    [Theory]
    [InlineData("--check")]
    [InlineData("--debug")]
    [InlineData("--remote")]
    [InlineData("--target-branch")]
    [InlineData("--append-agent-prompt")]
    [InlineData("--no-tui-sound")]
    public void LegacyRunOptionsAreRejected(string oldOption)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["run", .. RunConfiguration, oldOption]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--repo")]
    [InlineData("--init")]
    [InlineData("--reopen")]
    [InlineData("--")]
    [InlineData("targets set main abc-1")]
    public void TextValuesCannotSelectOperationsOrOptions(string text)
    {
        var resolve = Options.Parse(["attention", "resolve", "abc-1", "--message", text]);
        Assert.False(resolve.ShowHelp);
        Assert.Null(resolve.RepositoryPath);
        Assert.Equal(text, resolve.AttentionResolution!.Message);
        Assert.False(resolve.AttentionResolution.Reopen);
        var run = Options.Parse(["run", .. RunConfiguration, "--append-prompt", text]);
        Assert.False(run.ShowHelp);
        Assert.Equal(text, run.Value!.AppendAgentPrompt);
    }

    [Fact]
    public void EqualsValuesAndEndOfOptionsAreSupported()
    {
        var resolve = Options.Parse(["attention", "resolve", "--message=--help", "--repo=/tmp/main", "--", "abc-1"]);
        Assert.Equal("--help", resolve.AttentionResolution!.Message);
        Assert.Equal("/tmp/main", resolve.RepositoryPath);
        var targets = Options.Parse(["targets", "set", "--repo=/tmp/main", "--", "main", "abc-1", "abc-2"]);
        Assert.Equal("main", targets.TargetCommand!.Target);
        Assert.Equal(["abc-1", "abc-2"], targets.TargetCommand.IssueIds);
        Assert.Equal(4, Options.Parse(["new", "--agents=4", "--", "sample"]).NewMultiAgentRepository!.AgentCount);
        Assert.Equal("--help", Options.Parse(["run", .. RunConfiguration, "--append-prompt=--help"]).Value!.AppendAgentPrompt);
        Assert.Throws<OptionsException>(() => Options.Parse(["attention", "resolve", "abc-1", "--", "--reopen"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", .. RunConfiguration, "--", "--once"]));
    }

    [Theory]
    [InlineData("preflight", "--once")]
    [InlineData("preflight", "--drain")]
    [InlineData("preflight", "--tui-audio")]
    public void PreflightRejectsRunOnlyOptions(string command, string option)
    {
        Assert.Throws<OptionsException>(() => Options.Parse([command, .. RunConfiguration, option]));
    }

    [Theory]
    [InlineData("init", "--model", "model")]
    [InlineData("new", "sample", "4")]
    [InlineData("new", "sample", "--agents", "4", "--repo", "/tmp/main")]
    [InlineData("--repo", "/tmp/main", "new", "sample", "--agents", "4")]
    [InlineData("--repo", "/tmp/main", "models")]
    [InlineData("attention", "list", "--reopen")]
    [InlineData("attention", "resolve", "abc-1", "positional message")]
    [InlineData("attention", "resolve", "abc-1", "--message")]
    [InlineData("attention", "resolve", "abc-1", "--message", "one", "--message", "two")]
    [InlineData("attention", "resolve", "abc-1", "--reopen=true")]
    [InlineData("targets", "check", "--adopt-existing-branch")]
    [InlineData("targets", "set", "main", "abc-1", "--start-commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("targets", "set", "main", "abc-1", "--adopt-existing-branch")]
    [InlineData("targets", "check", "abc-1", "abc-1")]
    [InlineData("health", "--repo", "/tmp/one", "--repo", "/tmp/two")]
    [InlineData("--repo", "/tmp/one", "health", "--repo", "/tmp/two")]
    [InlineData("help", "unknown")]
    [InlineData("unknown", "--help")]
    public void RejectsInvalidCommandScopeAndArguments(params string[] arguments)
    {
        Assert.Throws<OptionsException>(() => Options.Parse(arguments));
    }

    [Fact]
    public void ServerAddressDoesNotImplicitlySelectServerMode()
    {
        Assert.Throws<OptionsException>(() => Options.Parse([
            "run", "--model", "p/m", "--opencode-server", "localhost:4096", "--agent", "a", "/tmp/a"]));
    }

    [Fact]
    public void RepeatableOptionsAndRetainedShortOptionsWork()
    {
        var options = Options.Parse(["run", .. RunConfiguration, "-a", "bob", "/tmp/bob", "-v",
            "--target-filter", "main", "--target-filter", "release/1.2",
            "--label", "one", "--label", "two", "--exclude-label", "three", "--exclude-label", "four"]).Value!;
        Assert.Equal(2, options.Agents.Count);
        Assert.True(options.Verbose);
        Assert.Equal(["main", "release/1.2"], options.TargetBranches);
        Assert.Equal(["one", "two"], options.DispatchFilters!.Labels);
        Assert.Equal(["three", "four"], options.DispatchFilters.ExcludedLabels);
    }
}
