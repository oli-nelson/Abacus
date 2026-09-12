using System.Text;

namespace Abacus;

public enum AgentActivity
{
    Starting,
    Paused,
    Waiting,
    Idle,
    Syncing,
    Preparing,
    Working,
    Finalizing,
    Recovering,
    Retrying,
    Stopped,
}

internal interface IAgentOutput
{
    Task SetAgentAsync(string agentName, AgentActivity activity, string detail);
    Task SetWorkspaceAsync(string agentName, string? branch, bool? isDirty);
    Task SetModelAsync(string agentName, string model, string effort);
    Task SetTicketAsync(string agentName, string issueId, string? title);
    Task SetUserAttentionIssuesAsync(IReadOnlyList<BeadsIssue> issues);
    Task SetLatestCommentsAsync(IReadOnlyList<BeadsComment> comments);
    Task SetPersistentAlertAsync(string source, string message);
    Task ClearPersistentAlertAsync(string source);
    Task ClearTicketAsync(string agentName);
    Task SetRunLocationAsync(string agentName, string location);
    Task ClearRunAsync(string agentName);
    Task SetLastExitCodeAsync(string agentName, int? exitCode);
    Task SetTmuxTargetAsync(string sessionName, string windowName);
    Task WarningAsync(string source, string message);
    Task SystemAsync(string message);
    Task DebugCommandAsync(string source, string command);
    Task SummaryAsync(RunSummarySnapshot summary);
}

internal static class OutputExtensions
{
    public static Task SetWorkspaceAsync(this TextWriter output, string agentName, string? branch, bool? isDirty) =>
        output is IAgentOutput agentOutput ? agentOutput.SetWorkspaceAsync(agentName, branch, isDirty) : Task.CompletedTask;

    public static Task SetModelAsync(this TextWriter output, string agentName, string model, string effort) =>
        output is IAgentOutput agentOutput ? agentOutput.SetModelAsync(agentName, model, effort) : Task.CompletedTask;

    public static Task SetAgentAsync(
        this TextWriter output,
        string agentName,
        AgentActivity activity,
        string detail) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetAgentAsync(agentName, activity, detail)
            : output.WriteLineAsync($"[{agentName}] {ActivityName(activity)}: {detail}");

    public static Task WarningAsync(this TextWriter output, string source, string message) =>
        output is IAgentOutput agentOutput
            ? agentOutput.WarningAsync(source, message)
            : output.WriteLineAsync($"[{source}] warning: {message}");

    public static Task SetTicketAsync(
        this TextWriter output,
        string agentName,
        string issueId,
        string? title) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetTicketAsync(agentName, issueId, title)
            : Task.CompletedTask;

    public static Task ClearTicketAsync(this TextWriter output, string agentName) =>
        output is IAgentOutput agentOutput
            ? agentOutput.ClearTicketAsync(agentName)
            : Task.CompletedTask;

    public static Task SetUserAttentionIssuesAsync(
        this TextWriter output,
        IReadOnlyList<BeadsIssue> issues) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetUserAttentionIssuesAsync(issues)
            : issues.Count is 0
                ? Task.CompletedTask
                : output.WriteLineAsync(
                    $"[abacus] ATTENTION: {string.Join(", ", issues.Select(FormatIssue))}");

    public static Task SetLatestCommentsAsync(
        this TextWriter output,
        IReadOnlyList<BeadsComment> comments) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetLatestCommentsAsync(comments)
            : Task.CompletedTask;

    public static Task SetPersistentAlertAsync(
        this TextWriter output,
        string source,
        string message) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetPersistentAlertAsync(source, message)
            : output.WriteLineAsync($"[{source}] ATTENTION: {message}");

    public static Task ClearPersistentAlertAsync(this TextWriter output, string source) =>
        output is IAgentOutput agentOutput
            ? agentOutput.ClearPersistentAlertAsync(source)
            : Task.CompletedTask;

    public static Task SetRunLocationAsync(this TextWriter output, string agentName, string location) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetRunLocationAsync(agentName, location)
            : Task.CompletedTask;

    public static Task ClearRunAsync(this TextWriter output, string agentName) =>
        output is IAgentOutput agentOutput
            ? agentOutput.ClearRunAsync(agentName)
            : Task.CompletedTask;

    public static Task SetLastExitCodeAsync(this TextWriter output, string agentName, int? exitCode) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetLastExitCodeAsync(agentName, exitCode)
            : Task.CompletedTask;

    public static Task SetTmuxTargetAsync(
        this TextWriter output,
        string sessionName,
        string windowName) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetTmuxTargetAsync(sessionName, windowName)
            : output.WriteLineAsync($"[abacus] tmux session '{sessionName}' • window '{windowName}'");

    public static Task SystemAsync(this TextWriter output, string message) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SystemAsync(message)
            : output.WriteLineAsync($"[abacus] {message}");

    public static Task DebugCommandAsync(this TextWriter output, string source, string command) =>
        output is IAgentOutput agentOutput
            ? agentOutput.DebugCommandAsync(source, command)
            : output.WriteLineAsync($"[{source}] {command}");

    public static Task SummaryAsync(this TextWriter output, RunSummarySnapshot summary) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SummaryAsync(summary)
            : WritePlainSummaryAsync(output, summary);

    public static string ActivityName(AgentActivity activity) => activity switch
    {
        AgentActivity.Starting => "STARTING",
        AgentActivity.Paused => "PAUSED",
        AgentActivity.Waiting => "WAITING",
        AgentActivity.Idle => "IDLE",
        AgentActivity.Syncing => "SYNCING",
        AgentActivity.Preparing => "PREPARING",
        AgentActivity.Working => "WORKING",
        AgentActivity.Finalizing => "FINALIZING",
        AgentActivity.Recovering => "RECOVERING",
        AgentActivity.Retrying => "RETRYING",
        AgentActivity.Stopped => "STOPPED",
        _ => activity.ToString().ToUpperInvariant(),
    };

    private static async Task WritePlainSummaryAsync(TextWriter output, RunSummarySnapshot summary)
    {
        await output.WriteLineAsync($"[abacus] run summary • {FormatDuration(summary.Elapsed)} • {summary.Total} outcomes");
        await output.WriteLineAsync($"[abacus] initial Beads Dolt commit • {summary.InitialDoltCommit}");
        foreach (var agent in summary.Agents)
        {
            await output.WriteLineAsync(
                $"[{agent.AgentName}] closed {agent.Closed} • reopened {agent.Reopened} • blocked {agent.Blocked} • interrupted {agent.Interrupted}");
        }
    }

    internal static string FormatDuration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
        : elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s"
            : $"{Math.Max(0, elapsed.Seconds)}s";

    internal static string FormatIssue(BeadsIssue issue) => issue.Title is null
        ? issue.Id
        : $"{issue.Id} — {issue.Title}";
}

public sealed class ConsoleOutput : TextWriter, IAgentOutput
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Dim = "\u001b[2m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Magenta = "\u001b[35m";
    private const string Red = "\u001b[31m";

    private readonly TextWriter writer;
    internal EventReporter? Events { get; }
    private readonly object gate = new();
    private readonly bool verbose;
    private readonly bool interactive;
    private readonly bool color;
    private readonly Func<(int Width, int Height)>? terminalSize;
    private readonly string model;
    private readonly string effort;
    private readonly bool effortIsRequested;
    private readonly Dictionary<string, AgentRow> agents;
    private readonly Queue<string> warnings = new();
    private readonly Dictionary<string, string> persistentAlerts = new(StringComparer.Ordinal);
    private IReadOnlyList<BeadsIssue> userAttentionIssues = [];
    private IReadOnlyList<BeadsComment> latestComments = [];
    private readonly Timer? refreshTimer;
    private string systemStatus = "Running preflight checks";
    private string? tmuxSessionName;
    private string? tmuxWindowName;
    private bool claimingEnabled = true;
    private bool rendered;
    private bool dashboardFrozen;
    private int selectedAgentIndex = -1;
    private int selectedCommentIndex = -1;
    private int commentScrollOffset;
    private BeadsComment? openComment;
    private DashboardPanel panel;
    private bool disposed;

    public ConsoleOutput(
        TextWriter writer,
        IEnumerable<string> agentNames,
        string model,
        bool verbose,
        bool? interactive = null,
        bool? color = null,
        IReadOnlyDictionary<string, string>? workspacePaths = null,
        EventReporter? events = null,
        bool startPaused = false,
        string effort = "high",
        bool effortIsRequested = false,
        Func<(int Width, int Height)>? terminalSize = null)
    {
        this.writer = writer;
        this.terminalSize = terminalSize;
        Events = events;
        claimingEnabled = !startPaused;
        this.verbose = verbose;
        this.interactive = !verbose && (interactive ?? (!Console.IsErrorRedirected && !Console.IsInputRedirected && !Console.IsOutputRedirected
            && Environment.GetEnvironmentVariable("TERM") != "dumb"));
        this.color = color ?? TerminalUi.ShouldUseColor(
            Console.IsErrorRedirected,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("NO_COLOR"));
        this.model = model;
        this.effort = effort;
        this.effortIsRequested = effortIsRequested;
        agents = agentNames.ToDictionary(
            static name => name,
            name => AgentRow.Create(
                name,
                workspacePaths is not null && workspacePaths.TryGetValue(name, out var workspace)
                    ? workspace
                    : null) with { Model = model, Effort = effort },
            StringComparer.Ordinal);

        if (this.interactive)
        {
            lock (gate)
            {
                writer.Write("\u001b[?25l");
                RenderDashboard();
            }

            refreshTimer = new Timer(
                static state => ((ConsoleOutput)state!).RefreshDashboard(),
                this,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1));
        }
    }

    public override Encoding Encoding => writer.Encoding;

    internal bool IsInteractiveDashboard => interactive;

    internal async Task MonitorDashboardInputAsync(
        ClaimGate claimGate,
        Action<string, AgentControlAction> requestAgentAction,
        CancellationToken cancellationToken)
    {
        if (!interactive || Console.IsInputRedirected)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Console.KeyAvailable)
                {
                    HandleDashboardKey(
                        Console.ReadKey(intercept: true),
                        claimGate,
                        requestAgentAction);
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    internal bool HandleDashboardKey(ConsoleKeyInfo key, ClaimGate claimGate)
        => HandleDashboardKey(key, claimGate, null);

    internal bool HandleDashboardKey(
        ConsoleKeyInfo key,
        ClaimGate claimGate,
        Action<string, AgentControlAction>? requestAgentAction)
    {
        if (IsClaimToggle(key))
        {
            var enabled = claimGate.Toggle();
            lock (gate)
            {
                claimingEnabled = enabled;
                Events?.Emit("claims.changed", new { enabled });
                systemStatus = enabled
                    ? "New ticket claims enabled"
                    : "New ticket claims paused; active tickets continue";
                RenderDashboard();
            }

            return true;
        }

        string? selectedAgent = null;
        AgentControlAction? requestedAction = null;
        lock (gate)
        {
            if (!HandleSelectionKey(key, out selectedAgent, out requestedAction))
            {
                return false;
            }

            RenderDashboard();
        }

        if (selectedAgent is not null && requestedAction is not null)
        {
            requestAgentAction?.Invoke(selectedAgent, requestedAction.Value);
            Events?.Emit("control.requested", new { agent = selectedAgent, action = requestedAction.Value });
        }

        return true;
    }

    internal static bool IsClaimToggle(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Tab
        && key.Modifiers is ConsoleModifiers.Shift;

    public Task SetAgentAsync(string agentName, AgentActivity activity, string detail)
    {
        lock (gate)
        {
            var stateChanged = !agents.TryGetValue(agentName, out var current)
                || current.Activity != activity;
            var changed = stateChanged
                || !string.Equals(current!.Detail, detail, StringComparison.Ordinal);
            var retryCount = current?.RetryCount ?? 0;
            if (stateChanged && activity is AgentActivity.Retrying)
            {
                retryCount++;
            }

            agents[agentName] = (current ?? AgentRow.Create(agentName)) with
            {
                Activity = activity,
                Detail = detail,
                ChangedAt = stateChanged ? DateTimeOffset.UtcNow : current!.ChangedAt,
                RetryCount = retryCount,
            };

            if (changed) Events?.Emit("agent.state", agents[agentName]);
            if (interactive)
            {
                RenderDashboard();
            }
            else if (verbose || changed)
            {
                WriteEvent(agentName, OutputExtensions.ActivityName(activity), detail);
            }
        }

        return Task.CompletedTask;
    }

    public Task SetWorkspaceAsync(string agentName, string? branch, bool? isDirty) =>
        UpdateRowAsync(agentName, row => row with { Branch = branch, IsDirty = isDirty });

    public Task SetModelAsync(string agentName, string model, string effort) =>
        UpdateRowAsync(agentName, row => row with { Model = model, Effort = effort });

    public Task SetTicketAsync(string agentName, string issueId, string? title) =>
        UpdateRowAsync(agentName, row => row with
        {
            IssueId = issueId,
            TicketTitle = title,
            RunLocation = null,
            RunActive = false,
            RetryCount = 0,
        });

    public Task SetUserAttentionIssuesAsync(IReadOnlyList<BeadsIssue> issues)
    {
        lock (gate)
        {
            var ordered = issues
                .OrderBy(static issue => issue.Id, StringComparer.Ordinal)
                .ToArray();
            if (userAttentionIssues.Count == ordered.Length
                && userAttentionIssues.Zip(ordered).All(static pair =>
                    string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                    && string.Equals(pair.First.Title, pair.Second.Title, StringComparison.Ordinal)))
            {
                return Task.CompletedTask;
            }

            var previouslyHadIssues = userAttentionIssues.Count > 0;
            userAttentionIssues = ordered;
            Events?.Emit("attention.changed", new { issues = ordered });
            if (interactive)
            {
                RenderDashboard();
            }
            else if (ordered.Length > 0)
            {
                WriteEvent(
                    "abacus",
                    "ATTENTION",
                    string.Join(", ", ordered.Select(OutputExtensions.FormatIssue)));
            }
            else if (previouslyHadIssues)
            {
                WriteEvent("abacus", "ATTENTION", "No issues currently need user attention");
            }
        }

        return Task.CompletedTask;
    }

    public Task SetPersistentAlertAsync(string source, string message)
    {
        lock (gate)
        {
            var changed = !persistentAlerts.TryGetValue(source, out var current)
                || !string.Equals(current, message, StringComparison.Ordinal);
            persistentAlerts[source] = message;
            if (changed) Events?.Emit("alert.raised", new { source, message });
            if (interactive)
            {
                RenderDashboard();
            }
            else if (changed)
            {
                WriteEvent(source, "ATTENTION", message);
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearPersistentAlertAsync(string source)
    {
        lock (gate)
        {
            if (persistentAlerts.Remove(source))
            {
                Events?.Emit("alert.cleared", new { source });
                if (interactive) RenderDashboard();
            }
        }

        return Task.CompletedTask;
    }

    public Task SetLatestCommentsAsync(IReadOnlyList<BeadsComment> comments)
    {
        lock (gate)
        {
            var snapshot = comments.ToArray();
            if (latestComments.SequenceEqual(snapshot))
            {
                return Task.CompletedTask;
            }

            latestComments = snapshot;
            Events?.Emit("comments.changed", new { comments = snapshot });
            if (selectedCommentIndex >= latestComments.Count)
            {
                selectedCommentIndex = latestComments.Count - 1;
            }

            if (interactive)
            {
                RenderDashboard();
            }
        }

        return Task.CompletedTask;
    }

    public Task ClearTicketAsync(string agentName) =>
        UpdateRowAsync(agentName, row => row with
        {
            IssueId = null,
            TicketTitle = null,
            RunLocation = null,
            RunActive = false,
            Model = model,
            Effort = effort,
        });

    public Task SetRunLocationAsync(string agentName, string location) =>
        UpdateRowAsync(agentName, row => row with
        {
            RunLocation = location,
            RunActive = true,
            LastExitCode = null,
            HasExitObservation = false,
        });

    public Task ClearRunAsync(string agentName) =>
        UpdateRowAsync(agentName, row => row with { RunActive = false });

    public Task SetLastExitCodeAsync(string agentName, int? exitCode) =>
        UpdateRowAsync(agentName, row => row with
        {
            LastExitCode = exitCode,
            HasExitObservation = true,
        });

    public Task SetTmuxTargetAsync(string sessionName, string windowName)
    {
        lock (gate)
        {
            tmuxSessionName = sessionName;
            tmuxWindowName = windowName;
            Events?.Emit("tmux.target", new { session = sessionName, window = windowName });
            if (interactive)
            {
                RenderDashboard();
            }
            else
            {
                WriteEvent("abacus", "INFO", $"tmux session '{sessionName}' • window '{windowName}'");
            }
        }

        return Task.CompletedTask;
    }

    public Task WarningAsync(string source, string message)
    {
        lock (gate)
        {
            if (warnings.Count == 3)
            {
                warnings.Dequeue();
            }

            Events?.Emit("warning", new { source, message });
            warnings.Enqueue($"{source}: {message}");
            if (interactive)
            {
                RenderDashboard();
            }
            else
            {
                WriteEvent(source, "WARNING", message);
            }
        }

        return Task.CompletedTask;
    }

    public Task SystemAsync(string message)
    {
        lock (gate)
        {
            systemStatus = message;
            Events?.Emit("system", new { message });
            if (interactive)
            {
                RenderDashboard();
            }
            else
            {
                WriteEvent("abacus", "INFO", message);
            }
        }

        return Task.CompletedTask;
    }

    public Task DebugCommandAsync(string source, string command)
    {
        lock (gate)
        {
            Events?.Emit("command", new { source, command });
            if (!verbose) return Task.CompletedTask;
            WriteEvent(source, "DEBUG", command);
        }

        return Task.CompletedTask;
    }

    public Task SummaryAsync(RunSummarySnapshot summary)
    {
        lock (gate)
        {
            Events?.Emit("run.summary", summary);
            dashboardFrozen = true;
            refreshTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (interactive)
            {
                writer.Write("\u001b[2J\u001b[H");
            }

            writer.WriteLine(
                $"{Color(Bold + Cyan, "ABACUS RUN SUMMARY")}"
                + Color(Dim, $"  •  {OutputExtensions.FormatDuration(summary.Elapsed)}  •  {summary.Total} outcomes"));
            writer.WriteLine($"{Color(Bold, "Initial Beads Dolt commit")}  {Color(Cyan, summary.InitialDoltCommit)}");
            writer.WriteLine(Color(Dim, new string('─', 72)));
            foreach (var agent in summary.Agents)
            {
                writer.WriteLine(
                    $"{Color(Bold + Cyan, TerminalUi.Sanitize(agent.AgentName).PadRight(16))} "
                    + $"{Color(Green, $"closed {agent.Closed}")}  "
                    + $"{Color(Yellow, $"reopened {agent.Reopened}")}  "
                    + $"{Color(Red, $"blocked {agent.Blocked}")}  "
                    + Color(Magenta, $"interrupted {agent.Interrupted}"));
            }

            if (persistentAlerts.Count > 0)
            {
                writer.WriteLine();
                writer.WriteLine(Color(Bold + Red, "USER ATTENTION"));
                foreach (var (source, message) in persistentAlerts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WriteLine(Color(Red,
                        $"! {TerminalUi.Sanitize(source)} — {TerminalUi.Sanitize(message)}"));
                }
            }

            writer.Flush();
        }

        return Task.CompletedTask;
    }

    public override Task WriteLineAsync(string? value)
    {
        if (value is null)
        {
            return Task.CompletedTask;
        }

        lock (gate)
        {
            Events?.Emit("system", new { message = value });
            if (interactive)
            {
                systemStatus = value;
                RenderDashboard();
            }
            else
            {
                writer.WriteLine(value);
                writer.Flush();
            }
        }

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                if (!disposed)
                {
                    refreshTimer?.Dispose();
                    if (interactive)
                    {
                        writer.Write($"\u001b[?25h{Reset}\n");
                    }

                    writer.Flush();
                    disposed = true;
                }
            }
        }

        base.Dispose(disposing);
    }

    internal void ReportStatus(string id, bool claimsEnabled)
    {
        lock (gate)
        {
            Events?.Emit("control.result", new
            {
                id, command = "status", ok = true,
                status = new { claimsEnabled, systemStatus, agents = agents.Values.ToArray(),
                    tmux = tmuxSessionName is null ? null : new { session = tmuxSessionName, window = tmuxWindowName },
                    attention = userAttentionIssues, alerts = new Dictionary<string, string>(persistentAlerts),
                    comments = latestComments, warnings = warnings.ToArray() },
            });
        }
    }

    private void WriteEvent(string source, string level, string detail)
    {
        var levelColor = level switch
        {
            "ATTENTION" or "WARNING" or "STOPPED" => Red,
            "RETRYING" or "RECOVERING" or "PAUSED" => Yellow,
            "WORKING" or "FINALIZING" => Green,
            "DEBUG" => Dim,
            _ => Cyan,
        };
        writer.WriteLine(
            $"{Color(Dim, DateTimeOffset.Now.ToString("HH:mm:ss"))} "
            + $"{Color(Cyan, $"[{TerminalUi.Sanitize(source)}]")} "
            + $"{Color(levelColor, TerminalUi.Sanitize(level).PadRight(10))} "
            + TerminalUi.Sanitize(detail));
        writer.Flush();
    }

    private void RenderDashboard()
    {
        if (dashboardFrozen)
        {
            return;
        }

        var width = GetWidth();
        var line = new string('─', width);
        var builder = new StringBuilder();
        builder.Append(rendered ? "\u001b[H" : "\u001b[2J\u001b[H");
        var claimLabel = claimingEnabled ? "CLAIMS ON" : "CLAIMS PAUSED";
        foreach (var headerLine in FormatHeaderLines(width, agents.Count, model, effort,
                     effortIsRequested, claimingEnabled, tmuxSessionName, tmuxWindowName))
        {
            var claimIndex = headerLine.IndexOf(claimLabel, StringComparison.Ordinal);
            if (headerLine.StartsWith(" ABACUS", StringComparison.Ordinal) && claimIndex >= 0)
            {
                builder.Append(Color(Bold + Cyan, headerLine[..claimIndex]));
                builder.Append(Color(claimingEnabled ? Green : Yellow, claimLabel));
                builder.Append(Color(Dim, headerLine[(claimIndex + claimLabel.Length)..]));
            }
            else builder.Append(Color(Dim, headerLine));
            builder.Append("\u001b[K\n");
        }
        builder.Append(Color(Dim, line)).Append("\u001b[K\n");

        if (panel is DashboardPanel.CommentDetail && openComment is not null)
        {
            RenderCommentDetail(builder, openComment, width, line);
            builder.Append("\u001b[J");
            writer.Write(builder.ToString());
            writer.Flush();
            rendered = true;
            return;
        }

        var nameWidth = Math.Clamp(agents.Keys.DefaultIfEmpty(string.Empty).Max(static name => name.Length), 8, 20);
        var rowIndex = 0;
        foreach (var row in agents.Values)
        {
            var state = OutputExtensions.ActivityName(row.Activity);
            var stateColor = row.Activity switch
            {
                AgentActivity.Working => Green,
                AgentActivity.Recovering or AgentActivity.Retrying => Red,
                AgentActivity.Preparing or AgentActivity.Syncing or AgentActivity.Finalizing => Yellow,
                AgentActivity.Starting => Magenta,
                AgentActivity.Paused => Yellow,
                _ => Cyan,
            };
            var icon = row.Activity == AgentActivity.Working ? "●" : "○";
            var elapsed = OutputExtensions.FormatDuration(DateTimeOffset.UtcNow - row.ChangedAt).PadLeft(7);
            var selector = rowIndex == selectedAgentIndex ? "›" : " ";
            var prefix = $"{selector}{icon} {Truncate(row.Name, nameWidth).PadRight(nameWidth)}  ";
            var status = state.PadRight(10);
            var ticket = row.IssueId is null ? string.Empty
                : string.IsNullOrEmpty(row.TicketTitle) ? row.IssueId : $"{row.IssueId} — {row.TicketTitle}";
            var headerLines = WrapCommentText(ticket, Math.Max(1, width - prefix.Length));
            builder.Append(Color(stateColor, prefix));
            builder.Append(Color(Bold, headerLines.FirstOrDefault() ?? string.Empty));
            builder.Append("\u001b[K\n");
            foreach (var continuation in headerLines.Skip(1))
                builder.Append(new string(' ', prefix.Length)).Append(continuation).Append("\u001b[K\n");
            var detail = row.Detail;
            // Keep structured/plain logs unchanged; the TUI header already identifies this ticket.
            if (row.IssueId is not null && detail.StartsWith($"{row.IssueId} • ", StringComparison.Ordinal))
                detail = detail[(row.IssueId.Length + 3)..];
            var available = Math.Max(0, width - prefix.Length - status.Length - elapsed.Length - 1);
            if (row.Activity == AgentActivity.Working && row.RunLocation is not null)
            {
                var location = $" • {row.RunLocation}";
                // Reserve room for the location when the progress text needs truncation.
                detail = Truncate(detail, Math.Max(0, available - location.Length)) + location;
            }
            builder.Append(new string(' ', prefix.Length)).Append(Color(stateColor, status));
            builder.Append(Color(Dim, elapsed)).Append(' ').Append(Truncate(detail, available));
            builder.Append("\u001b[K\n");

            foreach (var metadata in FormatMetadataLines(row))
            {
                builder.Append(Color(Dim, $"   {new string(' ', nameWidth)}  ↳ "));
                builder.Append(Truncate(metadata, Math.Max(0, width - nameWidth - 7)));
                builder.Append("\u001b[K\n");
            }

            rowIndex++;
        }

        if (panel is DashboardPanel.AgentMenu or DashboardPanel.ConfirmClean
            && SelectedAgent() is { } selected)
        {
            builder.Append(Color(Bold + Cyan, Truncate($" AGENT ACTIONS — {selected.Name}", width)));
            builder.Append("\u001b[K\n");
            builder.Append(Truncate(
                $"   {OutputExtensions.ActivityName(selected.Activity)} • {selected.Detail}",
                width));
            builder.Append("\u001b[K\n");
            if (selected.WorkspacePath is not null)
            {
                builder.Append(Color(Dim, Truncate($"   {selected.WorkspacePath}", width)));
                builder.Append("\u001b[K\n");
            }

            if (panel is DashboardPanel.ConfirmClean)
            {
                builder.Append(Color(Red, Truncate(
                    "   Permanently discard tracked and untracked workspace changes?",
                    width)));
                builder.Append("\u001b[K\n");
                builder.Append(Truncate("   [Y] Clean workspace   [N/Esc] Cancel", width));
                builder.Append("\u001b[K\n");
            }
            else
            {
                foreach (var option in new[]
                {
                    "   [S] Stop agent",
                    "   [R] Restart agent",
                    "   [C] Clean workspace",
                    "   [Esc] Close",
                })
                {
                    builder.Append(Truncate(option, width)).Append("\u001b[K\n");
                }
            }
        }

        if (userAttentionIssues.Count > 0 || persistentAlerts.Count > 0)
        {
            builder.Append(Color(Bold + Red, $" ! USER ATTENTION ({userAttentionIssues.Count + persistentAlerts.Count})"));
            builder.Append("\u001b[K\n");
            foreach (var issue in userAttentionIssues)
            {
                builder.Append(Color(Red, "   ! "));
                builder.Append(Truncate(
                    OutputExtensions.FormatIssue(issue),
                    Math.Max(0, width - 5)));
                builder.Append("\u001b[K\n");
            }

            foreach (var (source, message) in persistentAlerts.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                builder.Append(Color(Red, "   ! "));
                builder.Append(Truncate($"{source} — {message}", Math.Max(0, width - 5)));
                builder.Append("\u001b[K\n");
            }
        }

        builder.Append(Color(Dim, line)).Append("\u001b[K\n");
        builder.Append(Color(Dim, " " + Truncate(systemStatus, Math.Max(0, width - 1)))).Append("\u001b[K\n");
        foreach (var warning in warnings)
        {
            builder.Append(Color(Yellow, " ! " + Truncate(warning, Math.Max(0, width - 3))));
            builder.Append("\u001b[K\n");
        }

        builder.Append(Color(Dim, line)).Append("\u001b[K\n");
        builder.Append(Color(Bold, $" LATEST COMMENTS ({latestComments.Count})")).Append("\u001b[K\n");
        if (latestComments.Count == 0)
        {
            builder.Append(Color(Dim, "   No comments yet")).Append("\u001b[K\n");
        }
        else
        {
            for (var commentIndex = 0; commentIndex < latestComments.Count; commentIndex++)
            {
                var comment = latestComments[commentIndex];
                var commentColor = comment.NeedsUserAttention
                    ? Red
                    : agents.ContainsKey(comment.Author) ? Green : Cyan;
                var lines = FormatLatestCommentLines(comment, width);
                var header = commentIndex == selectedCommentIndex
                    ? "›" + lines.Header[1..]
                    : lines.Header;
                builder.Append(Color(commentColor, header));
                builder.Append("\u001b[K\n");
                foreach (var commentLine in lines.Comments)
                {
                    builder.Append(commentLine);
                    builder.Append("\u001b[K\n");
                }
            }
        }

        builder.Append("\u001b[J");
        writer.Write(builder.ToString());
        writer.Flush();
        rendered = true;
    }

    private string Color(string ansi, string value) => color ? ansi + value + Reset : value;

    private bool HandleSelectionKey(
        ConsoleKeyInfo key,
        out string? selectedAgent,
        out AgentControlAction? requestedAction)
    {
        // Normalize Vim navigation before panel handling so it follows exactly
        // the same selection, wrapping, and comment-scrolling rules as arrows.
        if (key.Modifiers == 0 && key.KeyChar is 'j' or 'k')
            key = new ConsoleKeyInfo('\0', key.KeyChar == 'j' ? ConsoleKey.DownArrow : ConsoleKey.UpArrow,
                false, false, false);
        selectedAgent = null;
        requestedAction = null;
        if (agents.Count + latestComments.Count == 0)
        {
            return false;
        }

        if (panel is DashboardPanel.CommentDetail)
        {
            if (key.Key is ConsoleKey.Escape)
            {
                panel = DashboardPanel.Closed;
                openComment = null;
                commentScrollOffset = 0;
                return true;
            }

            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.PageUp)
            {
                var amount = key.Key is ConsoleKey.PageUp ? CommentViewportHeight() : 1;
                commentScrollOffset = Math.Max(0, commentScrollOffset - amount);
                return true;
            }

            if (key.Key is ConsoleKey.DownArrow or ConsoleKey.PageDown)
            {
                var amount = key.Key is ConsoleKey.PageDown ? CommentViewportHeight() : 1;
                commentScrollOffset = Math.Min(CommentMaximumScrollOffset(), commentScrollOffset + amount);
                return true;
            }

            return false;
        }

        if (panel is DashboardPanel.ConfirmClean)
        {
            if (key.Key is ConsoleKey.Y)
            {
                selectedAgent = SelectedAgent()!.Name;
                requestedAction = AgentControlAction.CleanWorkspace;
                panel = DashboardPanel.Closed;
                systemStatus = $"Cleaning {selectedAgent}'s workspace";
                return true;
            }

            if (key.Key is ConsoleKey.N or ConsoleKey.Escape)
            {
                panel = DashboardPanel.AgentMenu;
                return true;
            }

            return false;
        }

        if (panel is DashboardPanel.AgentMenu)
        {
            var action = key.Key switch
            {
                ConsoleKey.S => AgentControlAction.Stop,
                ConsoleKey.R => AgentControlAction.Restart,
                _ => (AgentControlAction?)null,
            };
            if (action is not null)
            {
                selectedAgent = SelectedAgent()!.Name;
                requestedAction = action;
                panel = DashboardPanel.Closed;
                systemStatus = action is AgentControlAction.Stop
                    ? $"Stopping {selectedAgent}"
                    : $"Restarting {selectedAgent}";
                return true;
            }

            if (key.Key is ConsoleKey.C)
            {
                panel = DashboardPanel.ConfirmClean;
                return true;
            }

            if (key.Key is ConsoleKey.Escape)
            {
                panel = DashboardPanel.Closed;
                return true;
            }

            return false;
        }

        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
        {
            var direction = key.Key is ConsoleKey.UpArrow ? -1 : 1;
            var selectableCount = agents.Count + latestComments.Count;
            var current = selectedAgentIndex >= 0
                ? selectedAgentIndex
                : selectedCommentIndex >= 0 ? agents.Count + selectedCommentIndex : -1;
            var next = current < 0
                ? direction < 0 ? selectableCount - 1 : 0
                : (current + direction + selectableCount) % selectableCount;
            if (next < agents.Count)
            {
                selectedAgentIndex = next;
                selectedCommentIndex = -1;
            }
            else
            {
                selectedAgentIndex = -1;
                selectedCommentIndex = next - agents.Count;
            }

            return true;
        }

        if (key.Key is ConsoleKey.Enter)
        {
            if (selectedCommentIndex >= 0)
            {
                openComment = latestComments[selectedCommentIndex];
                commentScrollOffset = 0;
                panel = DashboardPanel.CommentDetail;
                return true;
            }

            if (selectedAgentIndex < 0 && agents.Count > 0)
            {
                selectedAgentIndex = 0;
            }

            if (selectedAgentIndex >= 0)
            {
                panel = DashboardPanel.AgentMenu;
                return true;
            }

            return false;
        }

        if (key.Key is ConsoleKey.Escape
            && (selectedAgentIndex >= 0 || selectedCommentIndex >= 0))
        {
            selectedAgentIndex = -1;
            selectedCommentIndex = -1;
            return true;
        }

        return false;
    }

    private AgentRow? SelectedAgent() => selectedAgentIndex >= 0
        ? agents.Values.ElementAt(selectedAgentIndex)
        : null;

    private int CommentViewportHeight() => Math.Max(3, GetHeight() - 11);

    private int CommentMaximumScrollOffset()
    {
        if (openComment is null)
        {
            return 0;
        }

        return Math.Max(
            0,
            WrapCommentText(openComment.Text, Math.Max(1, GetWidth() - 3)).Count
                - CommentViewportHeight());
    }

    private void RenderCommentDetail(
        StringBuilder builder,
        BeadsComment comment,
        int width,
        string line)
    {
        builder.Append(Color(Bold + Cyan, Truncate($" COMMENT — {SingleLine(comment.IssueId)}", width)));
        builder.Append("\u001b[K\n");
        builder.Append(Truncate($"   {SingleLine(comment.IssueTitle ?? "(untitled)")}", width));
        builder.Append("\u001b[K\n");
        builder.Append(Color(
            Dim,
            Truncate($"   {SingleLine(comment.Author)} • {comment.CreatedAt:u}", width)));
        builder.Append("\u001b[K\n");
        builder.Append(Color(Dim, line)).Append("\u001b[K\n");

        var wrapped = WrapCommentText(comment.Text, Math.Max(1, width - 3));
        var viewportHeight = CommentViewportHeight();
        var maximumOffset = Math.Max(0, wrapped.Count - viewportHeight);
        commentScrollOffset = Math.Clamp(commentScrollOffset, 0, maximumOffset);
        foreach (var messageLine in wrapped
            .Skip(commentScrollOffset)
            .Take(viewportHeight))
        {
            builder.Append("   ").Append(messageLine).Append("\u001b[K\n");
        }

        builder.Append(Color(Dim, line)).Append("\u001b[K\n");
        var visibleEnd = Math.Min(wrapped.Count, commentScrollOffset + viewportHeight);
        builder.Append(Color(
            Dim,
            Truncate(" ↑↓/jk scroll • PgUp/PgDn • Esc close", width)));
        builder.Append("\u001b[K\n");
        if (wrapped.Count > viewportHeight)
        {
            builder.Append(Color(
                Dim,
                Truncate(
                    $" Lines {commentScrollOffset + 1}-{visibleEnd} of {wrapped.Count}",
                    width)));
            builder.Append("\u001b[K\n");
        }
    }

    private Task UpdateRowAsync(string agentName, Func<AgentRow, AgentRow> update)
    {
        lock (gate)
        {
            var row = agents.TryGetValue(agentName, out var current)
                ? current
                : AgentRow.Create(agentName);
            agents[agentName] = update(row);
            if (agents[agentName] != row) Events?.Emit("agent.state", agents[agentName]);
            if (interactive)
            {
                RenderDashboard();
            }
        }

        return Task.CompletedTask;
    }

    private void RefreshDashboard()
    {
        lock (gate)
        {
            if (!disposed && !dashboardFrozen && interactive)
            {
                RenderDashboard();
            }
        }
    }

    internal static IReadOnlyList<string> FormatHeaderLines(int width, int agentCount,
        string model, string effort, bool effortIsRequested, bool claimingEnabled,
        string? session, string? window)
    {
        var lines = new List<string>();
        var summary = $" ABACUS • {agentCount} agent{(agentCount == 1 ? string.Empty : "s")} • {(claimingEnabled ? "CLAIMS ON" : "CLAIMS PAUSED")}";
        var effortText = $" • effort {effort}{(effortIsRequested ? " (requested)" : "")}";
        const string modelLabel = "Default model: ";
        var settings = modelLabel + model + effortText;
        if (summary.Length + settings.Length + 3 <= width)
            lines.Add(summary.PadRight(width - settings.Length) + settings);
        else
        {
            lines.Add(Truncate(summary, width));
            // Reserve the effort label instead of wrapping a long model over several rows.
            lines.Add(Truncate(modelLabel + Truncate(model,
                Math.Max(1, width - modelLabel.Length - effortText.Length)) + effortText, width));
        }
        const string controls = "↑↓/jk move  Enter open  Shift-Tab pause  Ctrl-C stop";
        if (session is not null)
        {
            var location = $" tmux session: {session} • window: {window}";
            if (location.Length + controls.Length + 3 <= width)
            {
                lines.Add(location.PadRight(width - controls.Length) + controls);
                return lines;
            }
            const string sessionLabel = " tmux session: ";
            const string windowLabel = " • window: ";
            var space = Math.Max(2, width - sessionLabel.Length - windowLabel.Length);
            var windowWidth = Math.Min(window?.Length ?? 0, space / 2);
            lines.Add(Truncate(sessionLabel + Truncate(session, space - windowWidth)
                + windowLabel + Truncate(window ?? string.Empty, windowWidth), width));
        }
        lines.Add(Truncate(controls, width));
        return lines;
    }

    private IEnumerable<string> FormatMetadataLines(AgentRow row)
    {
        var idleWorkspace = row.Activity is not (AgentActivity.Preparing or AgentActivity.Working or AgentActivity.Finalizing);
        var dirty = idleWorkspace && row.IsDirty == true ? "DIRTY • " : string.Empty;
        yield return $"{dirty}branch: {row.Branch ?? "unknown"}";
        // Only surface the running harness settings while an agent process is actually hosted.
        if (row.RunActive)
        {
            yield return $"effort {row.Effort ?? effort}{(effortIsRequested ? " (requested)" : "")} • model: {row.Model ?? model}";
        }

        var runParts = new List<string>();
        if (row.RunLocation is not null && row.Activity != AgentActivity.Working)
        {
            runParts.Add(row.RunLocation);
        }

        if (row.RetryCount > 0)
        {
            runParts.Add($"retries {row.RetryCount}");
        }

        if (row.HasExitObservation)
        {
            runParts.Add($"last exit {row.LastExitCode?.ToString() ?? "unknown"}");
        }

        if (runParts.Count > 0)
        {
            yield return string.Join(" • ", runParts);
        }
    }

    internal static (string Header, IReadOnlyList<string> Comments) FormatLatestCommentLines(
        BeadsComment comment,
        int width)
    {
        if (width <= 0)
        {
            return (string.Empty, []);
        }

        var issueWidth = Math.Clamp(width / 5, 8, 18);
        var authorWidth = Math.Clamp(width / 7, 6, 16);
        const int headerSeparatorsWidth = 9;
        var titleWidth = Math.Max(1, width - issueWidth - authorWidth - headerSeparatorsWidth);
        var issueTitle = string.IsNullOrWhiteSpace(comment.IssueTitle)
            ? "(untitled)"
            : SingleLine(comment.IssueTitle);
        var author = string.IsNullOrWhiteSpace(comment.Author)
            ? "(unknown)"
            : SingleLine(comment.Author);
        var header = $" • {Truncate(SingleLine(comment.IssueId), issueWidth).PadRight(issueWidth)}" +
            $" — {Truncate(issueTitle, titleWidth).PadRight(titleWidth)}" +
            $" • {Truncate(author, authorWidth).PadRight(authorWidth)}";

        return (Truncate(header, width), FormatCommentMessageLines(comment.Text, width));
    }

    private static IReadOnlyList<string> FormatCommentMessageLines(string value, int width)
    {
        const string firstPrefix = "   ↳ ";
        const string continuationPrefix = "     ";
        var contentWidth = Math.Max(0, width - firstPrefix.Length);
        var text = SingleLine(value);
        if (contentWidth == 0 || text.Length <= contentWidth)
        {
            return [Truncate(firstPrefix + text, width)];
        }

        var breakAt = text.LastIndexOf(' ', contentWidth);
        if (breakAt <= 0)
        {
            breakAt = contentWidth;
        }

        var firstLine = text[..breakAt].TrimEnd();
        var remainder = text[breakAt..].TrimStart();
        return
        [
            Truncate(firstPrefix + firstLine, width),
            Truncate(continuationPrefix + remainder, width),
        ];
    }

    private static string SingleLine(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static IReadOnlyList<string> WrapCommentText(string value, int width)
    {
        if (width <= 0)
        {
            return [];
        }

        var sanitized = new string(value.Select(static character => character switch
        {
            '\n' or '\r' => character,
            '\t' => ' ',
            _ when char.IsControl(character) => '�',
            _ => character,
        }).ToArray());
        var result = new List<string>();
        foreach (var logicalLine in sanitized
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n'))
        {
            var remaining = logicalLine;
            if (remaining.Length == 0)
            {
                result.Add(string.Empty);
                continue;
            }

            while (remaining.Length > width)
            {
                var segmentLength = width;
                var wordBreak = remaining.LastIndexOf(' ', width - 1, width);
                if (wordBreak > 0)
                {
                    segmentLength = wordBreak;
                }

                result.Add(remaining[..segmentLength].TrimEnd());
                remaining = remaining[segmentLength..].TrimStart();
            }

            result.Add(remaining);
        }

        return result;
    }

    internal static string Truncate(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return value.Length <= width
            ? value
            : width == 1 ? "…" : value[..(width - 1)] + "…";
    }

    private int GetWidth()
    {
        try
        {
            // Headless consoles may report zero rather than throwing IOException.
            var width = terminalSize?.Invoke().Width ?? Console.WindowWidth;
            return width > 0 ? Math.Clamp(width, 52, 140) : 80;
        }
        catch (IOException)
        {
            return 80;
        }
    }

    private int GetHeight()
    {
        try
        {
            var height = terminalSize?.Invoke().Height ?? Console.WindowHeight;
            return height > 0 ? Math.Clamp(height, 12, 80) : 24;
        }
        catch (IOException)
        {
            return 24;
        }
    }

    private sealed record AgentRow(
        string Name,
        AgentActivity Activity,
        string Detail,
        DateTimeOffset ChangedAt,
        string? IssueId,
        string? TicketTitle,
        string? RunLocation,
        string? WorkspacePath,
        int? LastExitCode,
        bool HasExitObservation,
        int RetryCount,
        string? Branch = null,
        bool? IsDirty = null,
        string? Model = null,
        string? Effort = null,
        bool RunActive = false)
    {
        public static AgentRow Create(string name, string? workspacePath = null) => new(
            name,
            AgentActivity.Starting,
            "Waiting for preflight",
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            workspacePath,
            null,
            false,
            0);
    }

    private enum DashboardPanel
    {
        Closed,
        AgentMenu,
        ConfirmClean,
        CommentDetail,
    }
}
