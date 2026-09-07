using System.Text;

namespace Abacus;

internal static class AsciiIntro
{
    internal const int FrameCount = 64;
    internal const int FrameDelayMilliseconds = 60;

    internal static bool ShouldPlay(Options options, bool inputRedirected, bool outputRedirected,
        bool errorRedirected, string? term) => !options.Stdio && !options.NoIntro && !options.Verbose
        && !options.CheckOnly && !inputRedirected && !outputRedirected && !errorRedirected && term != "dumb";

    internal static string Frame(int frame, int width, int height, bool color)
    {
        string[] logo =
        [
            "    _    ____    _    ____ _   _ ____  ",
            "   / \\  | __ )  / \\  / ___| | | / ___| ",
            "  / _ \\ |  _ \\ / _ \\| |   | | | \\___ \\ ",
            " / ___ \\| |_) / ___ \\ |___| |_| |___) |",
            "/_/   \\_\\____/_/   \\_\\____|\\___/|____/ ",
        ];
        var lines = new List<string> { "", "[ A B A C U S // ORCHESTRATION ENGINE ]", "" };
        lines.AddRange(logo);
        lines.Add("");
        lines.Add("+--------------------------------------+");
        for (var rail = 0; rail < 3; rail++)
        {
            var position = (int)((Math.Sin(frame * 0.24 + rail * 1.4) + 1) * 13);
            lines.Add("|" + new string('-', position) + "[o][o][o]" + new string('-', 29 - position) + "|");
        }
        lines.Add("+--------------------------------------+");
        lines.Add("");
        var progress = Math.Clamp(frame * 24 / (FrameCount - 1), 0, 24);
        lines.Add("[" + new string('#', progress) + new string('.', 24 - progress) + "]");
        lines.Add(frame < FrameCount / 3
            ? "ALIGNING THE BEADS"
            : frame < FrameCount * 2 / 3
                ? "SYNCHRONIZING THE SWARM"
                : "EVERY AGENT. EVERY TICKET. IN SYNC.");
        lines.Add("any key to skip");
        if (width < 44 || height < lines.Count + 2)
            lines = ["ABACUS", "[ " + new string('#', progress) + " ]", "WAKING THE SWARM", "any key to skip"];
        var builder = new StringBuilder("\u001b[H");
        var availableWidth = Math.Max(1, width - 1);
        if (color) builder.Append(frame % 10 < 5 ? "\u001b[36m" : "\u001b[96m");
        foreach (var line in lines.Take(Math.Max(1, height - 1)))
        {
            var clipped = line[..Math.Min(line.Length, availableWidth)];
            builder.Append(' ', Math.Max(0, (availableWidth - clipped.Length) / 2));
            builder.Append(clipped).Append("\u001b[K\n");
        }
        if (color) builder.Append("\u001b[0m");
        return builder.Append("\u001b[J").ToString();
    }

    public static async Task PlayAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            writer.Write("\u001b[?25l\u001b[2J");
            for (var frame = 0; frame < FrameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Console.KeyAvailable) { Console.ReadKey(intercept: true); break; }
                writer.Write(Frame(frame, Console.WindowWidth, Console.WindowHeight,
                    Environment.GetEnvironmentVariable("NO_COLOR") is null));
                writer.Flush();
                await Task.Delay(FrameDelayMilliseconds, cancellationToken);
            }
        }
        finally
        {
            writer.Write("\u001b[0m\u001b[?25h\u001b[2J\u001b[H");
            writer.Flush();
        }
    }
}
