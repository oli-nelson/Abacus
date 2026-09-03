using System.Collections.Concurrent;
using Abacus;

namespace Abacus.Tests;

public sealed class DesktopNotifierTests
{
    [Fact]
    public async Task MacOSKeepsNotificationContentOutOfAppleScriptSource()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.All,
            sound: true,
            DesktopPlatform.MacOS);

        notifier.NotifyTicketOutcome(
            "alice",
            TicketOutcome.Closed,
            "abc-1",
            "Title with \"quotes\" and $(commands)");
        await notifier.DisposeAsync();

        var command = Assert.Single(commands);
        Assert.Equal("/usr/bin/osascript", command.FileName);
        Assert.Equal("-e", command.Arguments[0]);
        Assert.Contains("sound name \"Hero\"", command.Arguments[1], StringComparison.Ordinal);
        Assert.DoesNotContain("$(commands)", command.Arguments[1], StringComparison.Ordinal);
        Assert.Equal(
            "abc-1 — Title with \"quotes\" and $(commands) was closed.",
            command.Environment!["ABACUS_NOTIFICATION_BODY"]);
    }

    [Fact]
    public async Task LinuxUsesNativeArgumentsAndSoundHint()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.All,
            sound: true,
            DesktopPlatform.Linux);

        notifier.NotifyTicketOutcome("bob", TicketOutcome.Reopened, "abc-2", "Retry it");
        await notifier.DisposeAsync();

        var command = Assert.Single(commands);
        Assert.Equal("notify-send", command.FileName);
        Assert.Contains("--app-name=Abacus", command.Arguments);
        Assert.Contains("--urgency=normal", command.Arguments);
        Assert.Contains("--hint=string:sound-name:dialog-warning", command.Arguments);
        Assert.Equal("Abacus: bob reopened", command.Arguments[^2]);
        Assert.Equal("abc-2 — Retry it was reopened.", command.Arguments[^1]);
    }

    [Fact]
    public async Task MacOSUsesNegativeSoundForBlockedOutcome()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.Attention,
            sound: true,
            DesktopPlatform.MacOS);

        notifier.NotifyTicketOutcome("alice", TicketOutcome.Blocked, "abc-3");
        await notifier.DisposeAsync();

        var command = Assert.Single(commands);
        Assert.Contains("sound name \"Basso\"", command.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttentionModeFiltersOrdinaryOutcomesAndDeduplicatesPolledIssues()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.Attention,
            sound: true,
            DesktopPlatform.Linux);
        var issue = new BeadsIssue("abc-3", IssueStatus.Open, "Choose an API");

        notifier.NotifyTicketOutcome("alice", TicketOutcome.Closed, "abc-1");
        notifier.NotifyTicketOutcome("alice", TicketOutcome.Blocked, "abc-2");
        notifier.UserAttentionChanged([issue]);
        notifier.UserAttentionChanged([issue with { Status = IssueStatus.Blocked }]);
        notifier.UserAttentionChanged([]);
        notifier.UserAttentionChanged([issue]);
        notifier.PersistentAlert("alice", "Recovery could not be verified");
        await notifier.DisposeAsync();

        Assert.Equal(4, commands.Count);
        Assert.Equal(4, commands.Count(command => command.Arguments.Contains("--urgency=critical")));
        Assert.Equal(
            4,
            commands.Count(command => command.Arguments.Contains("--hint=string:sound-name:dialog-warning")));
    }

    [Fact]
    public async Task FailedDeliveryRingsBellOnlyWhenSoundWasRequested()
    {
        var bell = new StringWriter();
        var notifier = new DesktopNotifier(
            (_, _) => Task.FromResult(new CommandResult(1, string.Empty, "no desktop session")),
            TextWriter.Null,
            bell,
            NotificationMode.All,
            sound: true,
            DesktopPlatform.Linux);

        notifier.NotifyTicketOutcome("alice", TicketOutcome.Closed, "abc-1");
        await notifier.DisposeAsync();

        Assert.Equal("\a", bell.ToString());
    }

    [Fact]
    public async Task OffModeNeverInvokesPlatformCommand()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.Off,
            sound: false,
            DesktopPlatform.Linux);

        notifier.NotifyTicketOutcome("alice", TicketOutcome.Blocked, "abc-1");
        notifier.UserAttentionChanged([new BeadsIssue("abc-2", IssueStatus.Blocked)]);
        notifier.PersistentAlert("alice", "failure");
        notifier.RunCompleted(new RunSummarySnapshot(TimeSpan.Zero, []));
        await notifier.DisposeAsync();

        Assert.Empty(commands);
    }

    [Fact]
    public async Task AllModeReportsFinalRunTotals()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.All,
            sound: true,
            DesktopPlatform.Linux);

        notifier.RunCompleted(new RunSummarySnapshot(
            TimeSpan.FromMinutes(1),
            [new AgentRunSummary("alice", 2, 1, 1, 0)]));
        await notifier.DisposeAsync();

        var command = Assert.Single(commands);
        Assert.Equal("Abacus run finished", command.Arguments[^2]);
        Assert.Equal(
            "4 outcomes: 2 closed, 1 reopened, 1 blocked, 0 interrupted.",
            command.Arguments[^1]);
        Assert.Contains("--urgency=critical", command.Arguments);
        Assert.Contains("--hint=string:sound-name:dialog-warning", command.Arguments);
    }

    [Fact]
    public async Task SuccessfulRunSummaryUsesPositiveSound()
    {
        var commands = new ConcurrentQueue<CommandSpec>();
        var notifier = CreateNotifier(
            commands,
            NotificationMode.All,
            sound: true,
            DesktopPlatform.Linux);

        notifier.RunCompleted(new RunSummarySnapshot(
            TimeSpan.FromMinutes(1),
            [new AgentRunSummary("alice", 2, 0, 0, 0)]));
        await notifier.DisposeAsync();

        var command = Assert.Single(commands);
        Assert.Contains("--hint=string:sound-name:complete", command.Arguments);
    }

    private static DesktopNotifier CreateNotifier(
        ConcurrentQueue<CommandSpec> commands,
        NotificationMode mode,
        bool sound,
        DesktopPlatform platform) =>
        new(
            (command, _) =>
            {
                commands.Enqueue(command);
                return Task.FromResult(new CommandResult(0, string.Empty, string.Empty));
            },
            TextWriter.Null,
            TextWriter.Null,
            mode,
            sound,
            platform);
}
