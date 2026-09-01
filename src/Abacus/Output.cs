using System.Text;

namespace Abacus;

public enum AgentActivity
{
    Starting,
    Waiting,
    Idle,
    Syncing,
    Cleaning,
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
    Task SetTicketAsync(string agentName, string issueId, string? title);
    Task SetUserAttentionIssuesAsync(IReadOnlyList<BeadsIssue> issues);
    Task ClearTicketAsync(string agentName);
    Task SetRunLocationAsync(string agentName, string location);
    Task SetLastExitCodeAsync(string agentName, int? exitCode);
    Task WarningAsync(string source, string message);
    Task SystemAsync(string message);
    Task DebugCommandAsync(string source, string command);
    Task SummaryAsync(RunSummarySnapshot summary);
}

internal static class OutputExtensions
{
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

    public static Task SetRunLocationAsync(this TextWriter output, string agentName, string location) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetRunLocationAsync(agentName, location)
            : Task.CompletedTask;

    public static Task SetLastExitCodeAsync(this TextWriter output, string agentName, int? exitCode) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SetLastExitCodeAsync(agentName, exitCode)
            : Task.CompletedTask;

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
        AgentActivity.Waiting => "WAITING",
        AgentActivity.Idle => "IDLE",
        AgentActivity.Syncing => "SYNCING",
        AgentActivity.Cleaning => "CLEANING",
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
    private readonly object gate = new();
    private readonly bool verbose;
    private readonly bool interactive;
    private readonly bool color;
    private readonly string model;
    private readonly Dictionary<string, AgentRow> agents;
    private readonly Queue<string> warnings = new();
    private IReadOnlyList<BeadsIssue> userAttentionIssues = [];
    private readonly Timer? refreshTimer;
    private string systemStatus = "Running preflight checks";
    private bool rendered;
    private bool dashboardFrozen;
    private bool disposed;

    public ConsoleOutput(
        TextWriter writer,
        IEnumerable<string> agentNames,
        string model,
        bool verbose,
        bool? interactive = null,
        bool? color = null)
    {
        this.writer = writer;
        this.verbose = verbose;
        this.interactive = !verbose && (interactive ?? !Console.IsErrorRedirected);
        this.color = color ?? (Environment.GetEnvironmentVariable("NO_COLOR") is null);
        this.model = model;
        agents = agentNames.ToDictionary(
            static name => name,
            static name => AgentRow.Create(name),
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

    public Task SetTicketAsync(string agentName, string issueId, string? title) =>
        UpdateRowAsync(agentName, row => row with
        {
            IssueId = issueId,
            TicketTitle = title,
            RunLocation = null,
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

    public Task ClearTicketAsync(string agentName) =>
        UpdateRowAsync(agentName, row => row with
        {
            IssueId = null,
            TicketTitle = null,
            RunLocation = null,
        });

    public Task SetRunLocationAsync(string agentName, string location) =>
        UpdateRowAsync(agentName, row => row with
        {
            RunLocation = location,
            LastExitCode = null,
            HasExitObservation = false,
        });

    public Task SetLastExitCodeAsync(string agentName, int? exitCode) =>
        UpdateRowAsync(agentName, row => row with
        {
            LastExitCode = exitCode,
            HasExitObservation = true,
        });

    public Task WarningAsync(string source, string message)
    {
        lock (gate)
        {
            if (warnings.Count == 3)
            {
                warnings.Dequeue();
            }

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
        if (!verbose)
        {
            return Task.CompletedTask;
        }

        lock (gate)
        {
            WriteEvent(source, "DEBUG", command);
        }

        return Task.CompletedTask;
    }

    public Task SummaryAsync(RunSummarySnapshot summary)
    {
        lock (gate)
        {
            dashboardFrozen = true;
            refreshTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (interactive)
            {
                writer.Write("\u001b[2J\u001b[H");
            }

            writer.WriteLine($"ABACUS RUN SUMMARY  •  {OutputExtensions.FormatDuration(summary.Elapsed)}  •  {summary.Total} outcomes");
            writer.WriteLine(new string('─', 72));
            foreach (var agent in summary.Agents)
            {
                writer.WriteLine(
                    $"{agent.AgentName,-16} closed {agent.Closed}  reopened {agent.Reopened}  blocked {agent.Blocked}  interrupted {agent.Interrupted}");
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

    private void WriteEvent(string source, string level, string detail)
    {
        writer.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} [{source}] {level,-10} {detail}");
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
        builder.Append(Color(Bold + Cyan, " ABACUS"));
        builder.Append(Color(Dim, $"  {agents.Count} agent{(agents.Count == 1 ? string.Empty : "s")}  •  {model}  •  Ctrl-C to stop"));
        builder.Append("\u001b[K\n");
        builder.Append(Color(Dim, line)).Append("\u001b[K\n");

        var nameWidth = Math.Clamp(agents.Keys.DefaultIfEmpty(string.Empty).Max(static name => name.Length), 8, 20);
        foreach (var row in agents.Values)
        {
            var state = OutputExtensions.ActivityName(row.Activity);
            var stateColor = row.Activity switch
            {
                AgentActivity.Working => Green,
                AgentActivity.Recovering or AgentActivity.Retrying => Red,
                AgentActivity.Cleaning or AgentActivity.Preparing or AgentActivity.Syncing or AgentActivity.Finalizing => Yellow,
                AgentActivity.Starting => Magenta,
                _ => Cyan,
            };
            var icon = row.Activity == AgentActivity.Working ? "●" : "○";
            var elapsed = OutputExtensions.FormatDuration(DateTimeOffset.UtcNow - row.ChangedAt).PadLeft(7);
            var prefix = $" {icon} {Truncate(row.Name, nameWidth).PadRight(nameWidth)}  ";
            var status = state.PadRight(10);
            var available = Math.Max(0, width - prefix.Length - 20);
            builder.Append(Color(stateColor, prefix + status));
            builder.Append(Color(Dim, elapsed)).Append(' ').Append(Truncate(row.Detail, available));
            builder.Append("\u001b[K\n");

            foreach (var metadata in FormatMetadataLines(row))
            {
                builder.Append(Color(Dim, $"   {new string(' ', nameWidth)}  ↳ "));
                builder.Append(Truncate(metadata, Math.Max(0, width - nameWidth - 7)));
                builder.Append("\u001b[K\n");
            }
        }

        if (userAttentionIssues.Count > 0)
        {
            builder.Append(Color(Bold + Red, $" ! USER ATTENTION ({userAttentionIssues.Count})"));
            builder.Append("\u001b[K\n");
            foreach (var issue in userAttentionIssues)
            {
                builder.Append(Color(Red, "   ! "));
                builder.Append(Truncate(
                    OutputExtensions.FormatIssue(issue),
                    Math.Max(0, width - 5)));
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

        builder.Append("\u001b[J");
        writer.Write(builder.ToString());
        writer.Flush();
        rendered = true;
    }

    private string Color(string ansi, string value) => color ? ansi + value + Reset : value;

    private Task UpdateRowAsync(string agentName, Func<AgentRow, AgentRow> update)
    {
        lock (gate)
        {
            var row = agents.TryGetValue(agentName, out var current)
                ? current
                : AgentRow.Create(agentName);
            agents[agentName] = update(row);
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

    private static IEnumerable<string> FormatMetadataLines(AgentRow row)
    {
        if (row.IssueId is not null)
        {
            yield return row.TicketTitle is null
                ? row.IssueId
                : $"{row.IssueId} — {row.TicketTitle}";
        }

        var runParts = new List<string>();
        if (row.RunLocation is not null)
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

    private static string Truncate(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        return value.Length <= width
            ? value
            : width == 1 ? "…" : value[..(width - 1)] + "…";
    }

    private static int GetWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth, 52, 140);
        }
        catch (IOException)
        {
            return 80;
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
        int? LastExitCode,
        bool HasExitObservation,
        int RetryCount)
    {
        public static AgentRow Create(string name) => new(
            name,
            AgentActivity.Starting,
            "Waiting for preflight",
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            false,
            0);
    }
}
