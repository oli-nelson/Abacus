using Abacus;

namespace Abacus.Tests;

public sealed class TerminalUiTests
{
    [Fact]
    public void RedirectedDumbAndNoColorTerminalsDisableAnsi()
    {
        Assert.False(TerminalUi.ShouldUseColor(true, "xterm-256color", null));
        Assert.False(TerminalUi.ShouldUseColor(false, "dumb", null));
        Assert.False(TerminalUi.ShouldUseColor(false, "xterm-256color", string.Empty));
        Assert.True(TerminalUi.ShouldUseColor(false, "xterm-256color", null));
    }

    [Fact]
    public void ReportsRemainStructuredWithoutAnsiWhenColorIsDisabled()
    {
        var writer = new StringWriter();
        var ui = new TerminalUi(false);

        ui.WriteTitle(writer, "Example report");
        ui.WriteSection(writer, "Details");
        ui.WriteKeyValue(writer, "Path", "/tmp/example");
        ui.WriteStatus(writer, "OK", "Ready.");

        var rendered = writer.ToString();
        Assert.Contains("Example report\n==============", rendered, StringComparison.Ordinal);
        Assert.Contains("Details\n  Path:", rendered, StringComparison.Ordinal);
        Assert.Contains("[OK] Ready.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveReportsColorSemanticElementsAndSanitizeValues()
    {
        var writer = new StringWriter();
        var ui = new TerminalUi(true);

        ui.WriteTitle(writer, "Example report");
        ui.WriteKeyValue(writer, "Path", "safe\u001b[31munsafe");
        ui.WriteStatus(writer, "WARN", "Review this.");

        var rendered = writer.ToString();
        Assert.Contains("\u001b[1m\u001b[36mExample report\u001b[0m", rendered, StringComparison.Ordinal);
        Assert.Contains("\u001b[33m[WARN]\u001b[0m", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("safe\u001b[31munsafe", rendered, StringComparison.Ordinal);
        Assert.Contains("safe [31munsafe", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpHighlightsUsageHeadingsAndCommandsOnlyWhenEnabled()
    {
        const string help = "Usage: abacus thing\n\nCommands:\n  run     Start work.";
        var plain = new TerminalUi(false).RenderHelp(help);
        var colored = new TerminalUi(true).RenderHelp(help);

        Assert.Equal(help, plain);
        Assert.Contains("\u001b[1m\u001b[33mUsage:", colored, StringComparison.Ordinal);
        Assert.Contains("\u001b[1m\u001b[36mCommands:", colored, StringComparison.Ordinal);
        Assert.Contains("\u001b[32mrun", colored, StringComparison.Ordinal);
    }
}
