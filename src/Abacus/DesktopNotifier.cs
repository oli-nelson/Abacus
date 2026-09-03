using System.Threading.Channels;

namespace Abacus;

internal enum DesktopPlatform
{
    MacOS,
    Linux,
    Unsupported,
}

internal enum NotificationTone
{
    Positive,
    Negative,
}

internal sealed record DesktopNotification(
    string Title,
    string Body,
    bool Attention,
    NotificationTone Tone);

public sealed class DesktopNotifier : IAsyncDisposable
{
    private readonly NotificationMode mode;
    private readonly bool sound;
    private readonly TextWriter log;
    private readonly TextWriter bellWriter;
    private readonly DesktopPlatform platform;
    private readonly Func<CommandSpec, CancellationToken, Task<CommandResult>> runCommand;
    private readonly Channel<DesktopNotification> notifications =
        Channel.CreateUnbounded<DesktopNotification>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly HashSet<string> attentionIssueIds = new(StringComparer.Ordinal);
    private readonly object attentionGate = new();
    private readonly Task worker;
    private int completed;

    public DesktopNotifier(
        CommandRunner runner,
        TextWriter log,
        TextWriter bellWriter,
        NotificationMode mode,
        bool sound)
        : this(
            runner.RunAsync,
            log,
            bellWriter,
            mode,
            sound,
            DetectPlatform())
    {
    }

    internal DesktopNotifier(
        Func<CommandSpec, CancellationToken, Task<CommandResult>> runCommand,
        TextWriter log,
        TextWriter bellWriter,
        NotificationMode mode,
        bool sound,
        DesktopPlatform platform)
    {
        this.runCommand = runCommand;
        this.log = log;
        this.bellWriter = bellWriter;
        this.mode = mode;
        this.sound = sound;
        this.platform = platform;
        worker = ProcessNotificationsAsync();
    }

    public void UserAttentionChanged(IReadOnlyList<BeadsIssue> issues)
    {
        if (mode is NotificationMode.Off)
        {
            return;
        }

        lock (attentionGate)
        {
            var currentIds = issues
                .Select(static issue => issue.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var issue in issues.Where(issue => !attentionIssueIds.Contains(issue.Id)))
            {
                Enqueue(new DesktopNotification(
                    "Abacus needs your attention",
                    OutputExtensions.FormatIssue(issue),
                    Attention: true,
                    Tone: NotificationTone.Negative));
            }

            attentionIssueIds.Clear();
            attentionIssueIds.UnionWith(currentIds);
        }
    }

    public void PersistentAlert(string source, string message)
    {
        if (mode is not NotificationMode.Off)
        {
            Enqueue(new DesktopNotification(
                $"Abacus: {source} needs attention",
                message,
                Attention: true,
                Tone: NotificationTone.Negative));
        }
    }

    public void NotifyTicketOutcome(
        string agentName,
        TicketOutcome outcome,
        string? issueId = null,
        string? title = null)
    {
        if (mode is NotificationMode.Off
            || mode is NotificationMode.Attention && outcome is not TicketOutcome.Blocked)
        {
            return;
        }

        var outcomeName = outcome.ToString().ToLowerInvariant();
        var ticket = issueId is null
            ? "Ticket"
            : title is null ? issueId : $"{issueId} — {title}";
        Enqueue(new DesktopNotification(
            $"Abacus: {agentName} {outcomeName}",
            $"{ticket} was {outcomeName}.",
            Attention: outcome is TicketOutcome.Blocked,
            Tone: outcome is TicketOutcome.Closed
                ? NotificationTone.Positive
                : NotificationTone.Negative));
    }

    public void RunCompleted(RunSummarySnapshot summary)
    {
        if (mode is not NotificationMode.All)
        {
            return;
        }

        var closed = summary.Agents.Sum(static agent => agent.Closed);
        var reopened = summary.Agents.Sum(static agent => agent.Reopened);
        var blocked = summary.Agents.Sum(static agent => agent.Blocked);
        var interrupted = summary.Agents.Sum(static agent => agent.Interrupted);
        var hasUnsuccessfulOutcomes = reopened > 0 || blocked > 0 || interrupted > 0;
        Enqueue(new DesktopNotification(
            "Abacus run finished",
            $"{summary.Total} outcomes: {closed} closed, {reopened} reopened, {blocked} blocked, {interrupted} interrupted.",
            Attention: blocked > 0,
            Tone: hasUnsuccessfulOutcomes
                ? NotificationTone.Negative
                : NotificationTone.Positive));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
        {
            notifications.Writer.TryComplete();
        }

        await worker;
    }

    private void Enqueue(DesktopNotification notification)
    {
        if (Volatile.Read(ref completed) == 0)
        {
            notifications.Writer.TryWrite(notification);
        }
    }

    private async Task ProcessNotificationsAsync()
    {
        await foreach (var notification in notifications.Reader.ReadAllAsync())
        {
            try
            {
                await DeliverAsync(notification);
            }
            catch
            {
                // Desktop notifications are best effort and must never change
                // an orchestration result, including when diagnostics or the
                // terminal bell cannot be written.
            }
        }
    }

    private async Task DeliverAsync(DesktopNotification notification)
    {
        try
        {
            var command = BuildCommand(notification);
            if (command is not null)
            {
                var result = await runCommand(command, CancellationToken.None);
                if (result.Succeeded)
                {
                    return;
                }

                await log.DebugCommandAsync(
                    "abacus",
                    $"desktop notification failed with exit code {result.ExitCode}: {CommandFailure(result)}");
            }
            else
            {
                await log.DebugCommandAsync("abacus", "desktop notifications are unsupported on this platform");
            }
        }
        catch (Exception exception)
        {
            await log.DebugCommandAsync("abacus", $"desktop notification failed: {exception.Message}");
        }

        if (sound)
        {
            await bellWriter.WriteAsync("\a");
            await bellWriter.FlushAsync();
        }
    }

    internal CommandSpec? BuildCommand(DesktopNotification notification) => platform switch
    {
        DesktopPlatform.MacOS => BuildMacOSCommand(notification),
        DesktopPlatform.Linux => BuildLinuxCommand(notification),
        _ => null,
    };

    private CommandSpec BuildMacOSCommand(DesktopNotification notification)
    {
        var script = "set notificationTitle to system attribute \"ABACUS_NOTIFICATION_TITLE\"\n"
            + "set notificationBody to system attribute \"ABACUS_NOTIFICATION_BODY\"\n"
            + "display notification notificationBody with title notificationTitle"
            + (sound ? $" sound name \"{MacOSSoundName(notification.Tone)}\"" : string.Empty);
        return new CommandSpec(
            "/usr/bin/osascript",
            ["-e", script],
            Path.GetTempPath(),
            new Dictionary<string, string?>
            {
                ["ABACUS_NOTIFICATION_TITLE"] = notification.Title,
                ["ABACUS_NOTIFICATION_BODY"] = notification.Body,
            });
    }

    private CommandSpec BuildLinuxCommand(DesktopNotification notification)
    {
        var arguments = new List<string>
        {
            "--app-name=Abacus",
            notification.Attention ? "--urgency=critical" : "--urgency=normal",
        };
        if (sound)
        {
            arguments.Add($"--hint=string:sound-name:{LinuxSoundName(notification.Tone)}");
        }

        arguments.Add(notification.Title);
        arguments.Add(notification.Body);
        return new CommandSpec("notify-send", arguments, Path.GetTempPath());
    }

    private static string MacOSSoundName(NotificationTone tone) => tone switch
    {
        NotificationTone.Positive => "Hero",
        NotificationTone.Negative => "Basso",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    private static string LinuxSoundName(NotificationTone tone) => tone switch
    {
        NotificationTone.Positive => "complete",
        NotificationTone.Negative => "dialog-warning",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    private static DesktopPlatform DetectPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return DesktopPlatform.MacOS;
        }

        return OperatingSystem.IsLinux()
            ? DesktopPlatform.Linux
            : DesktopPlatform.Unsupported;
    }

    private static string CommandFailure(CommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return string.IsNullOrWhiteSpace(detail) ? "no error detail" : detail.Trim();
    }
}
