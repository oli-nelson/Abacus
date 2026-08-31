using System.Text;

namespace Abacus;

public enum AgentActivity
{
    Starting,
    Waiting,
    Syncing,
    Cleaning,
    Preparing,
    Working,
    Finalizing,
    Recovering,
    Stopped,
}

internal interface IAgentOutput
{
    Task SetAgentAsync(string agentName, AgentActivity activity, string detail);
    Task WarningAsync(string source, string message);
    Task SystemAsync(string message);
    Task DebugCommandAsync(string source, string command);
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

    public static Task SystemAsync(this TextWriter output, string message) =>
        output is IAgentOutput agentOutput
            ? agentOutput.SystemAsync(message)
            : output.WriteLineAsync($"[abacus] {message}");

    public static Task DebugCommandAsync(this TextWriter output, string source, string command) =>
        output is IAgentOutput agentOutput
            ? agentOutput.DebugCommandAsync(source, command)
            : output.WriteLineAsync($"[{source}] {command}");

    public static string ActivityName(AgentActivity activity) => activity switch
    {
        AgentActivity.Starting => "STARTING",
        AgentActivity.Waiting => "WAITING",
        AgentActivity.Syncing => "SYNCING",
        AgentActivity.Cleaning => "CLEANING",
        AgentActivity.Preparing => "PREPARING",
        AgentActivity.Working => "WORKING",
        AgentActivity.Finalizing => "FINALIZING",
        AgentActivity.Recovering => "RECOVERING",
        AgentActivity.Stopped => "STOPPED",
        _ => activity.ToString().ToUpperInvariant(),
    };
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
    private string systemStatus = "Running preflight checks";
    private bool rendered;
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
            static name => new AgentRow(name, AgentActivity.Starting, "Waiting for preflight"),
            StringComparer.Ordinal);

        if (this.interactive)
        {
            lock (gate)
            {
                writer.Write("\u001b[?25l");
                RenderDashboard();
            }
        }
    }

    public override Encoding Encoding => writer.Encoding;

    public Task SetAgentAsync(string agentName, AgentActivity activity, string detail)
    {
        lock (gate)
        {
            var changed = !agents.TryGetValue(agentName, out var current)
                || current.Activity != activity
                || !string.Equals(current.Detail, detail, StringComparison.Ordinal);
            agents[agentName] = new AgentRow(agentName, activity, detail);

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
                AgentActivity.Recovering => Red,
                AgentActivity.Cleaning or AgentActivity.Preparing or AgentActivity.Syncing or AgentActivity.Finalizing => Yellow,
                AgentActivity.Starting => Magenta,
                _ => Cyan,
            };
            var icon = row.Activity == AgentActivity.Working ? "●" : "○";
            var prefix = $" {icon} {Truncate(row.Name, nameWidth).PadRight(nameWidth)}  ";
            var status = state.PadRight(10);
            var available = Math.Max(0, width - prefix.Length - 12);
            builder.Append(Color(stateColor, prefix + status));
            builder.Append(' ').Append(Truncate(row.Detail, available));
            builder.Append("\u001b[K\n");
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

    private sealed record AgentRow(string Name, AgentActivity Activity, string Detail);
}
