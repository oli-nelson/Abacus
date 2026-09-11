using System.Text;

namespace Abacus;

/// <summary>
/// Small, dependency-free styling helper for Abacus's standalone terminal
/// commands. Structured or redirected output stays free of ANSI escapes.
/// </summary>
internal sealed class TerminalUi(bool color)
{
    internal const string Reset = "\u001b[0m";
    internal const string Bold = "\u001b[1m";
    internal const string Dim = "\u001b[2m";
    internal const string Reverse = "\u001b[7m";
    internal const string Red = "\u001b[31m";
    internal const string Green = "\u001b[32m";
    internal const string Yellow = "\u001b[33m";
    internal const string Blue = "\u001b[34m";
    internal const string Magenta = "\u001b[35m";
    internal const string Cyan = "\u001b[36m";

    public bool ColorEnabled { get; } = color;

    public static bool ShouldUseColor(bool outputRedirected, string? term, string? noColor) =>
        !outputRedirected
        && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase)
        && noColor is null;

    public static TerminalUi ForConsoleOut() => new(ShouldUseColor(
        Console.IsOutputRedirected,
        Environment.GetEnvironmentVariable("TERM"),
        Environment.GetEnvironmentVariable("NO_COLOR")));

    public static TerminalUi ForConsoleError() => new(ShouldUseColor(
        Console.IsErrorRedirected,
        Environment.GetEnvironmentVariable("TERM"),
        Environment.GetEnvironmentVariable("NO_COLOR")));

    public string Style(string value, params string[] styles)
    {
        var safe = Sanitize(value);
        return ColorEnabled && styles.Length > 0
            ? string.Concat(styles) + safe + Reset
            : safe;
    }

    public string Title(string value) => Style(value, Bold, Cyan);
    public string Heading(string value) => Style(value, Bold, Blue);
    public string Label(string value) => Style(value, Cyan);
    public string Value(string value) => Style(value, Bold);
    public string Command(string value) => Style(value, Green);
    public string Muted(string value) => Style(value, Dim);
    public string Success(string value) => Style(value, Green);
    public string Warning(string value) => Style(value, Yellow);
    public string Error(string value) => Style(value, Bold, Red);
    public string Info(string value) => Style(value, Cyan);

    public string Status(string marker)
    {
        var value = $"[{marker}]";
        return marker switch
        {
            "OK" or "PASS" or "READY" => Success(value),
            "WARN" => Warning(value),
            "INFO" => Info(value),
            _ => Error(value),
        };
    }

    public void WriteTitle(TextWriter writer, string title)
    {
        writer.WriteLine(Title(title));
        writer.WriteLine(Muted(new string('=', Math.Max(3, title.Length))));
    }

    public void WriteSection(TextWriter writer, string heading)
    {
        writer.WriteLine();
        writer.WriteLine(Heading(heading));
    }

    public void WriteKeyValue(TextWriter writer, string label, string value) =>
        writer.WriteLine($"  {Label((label + ":").PadRight(18))} {Sanitize(value)}");

    public void WriteStatus(TextWriter writer, string marker, string detail) =>
        writer.WriteLine($"  {Status(marker)} {Sanitize(detail)}");

    public void WriteStep(TextWriter writer, int number, string text) =>
        writer.WriteLine($"  {Command(number + ".")} {Sanitize(text)}");

    /// <summary>Applies lightweight syntax highlighting to the existing help layout.</summary>
    public string RenderHelp(string help)
    {
        if (!ColorEnabled)
        {
            return help;
        }

        var rendered = new StringBuilder();
        var lines = help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            var indentation = line[..(line.Length - trimmed.Length)];
            if (trimmed.StartsWith("Usage:", StringComparison.Ordinal))
            {
                rendered.Append(Style(line, Bold, Yellow));
            }
            else if (trimmed.EndsWith(':') && !trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                rendered.Append(indentation).Append(Style(trimmed, Bold, Cyan));
            }
            else if (indentation.Length >= 2 && TrySplitEntry(trimmed, out var subject, out var detail))
            {
                rendered.Append(indentation).Append(Command(subject)).Append(detail);
            }
            else
            {
                rendered.Append(Sanitize(line));
            }

            if (index < lines.Length - 1)
            {
                rendered.AppendLine();
            }
        }

        return rendered.ToString();
    }

    internal static string Sanitize(string value) =>
        new(value.Select(static character => char.IsControl(character) && character is not '\t'
            ? ' '
            : character).ToArray());

    private static bool TrySplitEntry(string line, out string subject, out string detail)
    {
        for (var index = 1; index < line.Length - 1; index++)
        {
            if (line[index] == ' ' && line[index + 1] == ' ')
            {
                subject = line[..index];
                detail = line[index..];
                return subject.StartsWith('-') || char.IsLetterOrDigit(subject[0]);
            }
        }

        subject = string.Empty;
        detail = string.Empty;
        return false;
    }
}
