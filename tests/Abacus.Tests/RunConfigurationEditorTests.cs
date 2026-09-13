using Abacus;

namespace Abacus.Tests;

public sealed class RunConfigurationEditorTests
{
    [Theory]
    [InlineData('j', ConsoleKey.J, ConsoleKey.DownArrow)]
    [InlineData('k', ConsoleKey.K, ConsoleKey.UpArrow)]
    [InlineData('\0', ConsoleKey.DownArrow, ConsoleKey.DownArrow)]
    [InlineData('\0', ConsoleKey.UpArrow, ConsoleKey.UpArrow)]
    [InlineData('\r', ConsoleKey.Enter, ConsoleKey.Enter)]
    [InlineData('s', ConsoleKey.S, ConsoleKey.S)]
    public void MenuKeysMapToExpectedActions(char character, ConsoleKey key, ConsoleKey expected)
    {
        Assert.Equal(expected, RunConfigurationEditor.MenuAction(new(character, key, false, false, false)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ModifiedJAndKAreNotNavigation(bool alt, bool control)
    {
        Assert.Equal(ConsoleKey.J, RunConfigurationEditor.MenuAction(new('j', ConsoleKey.J, false, alt, control)));
        Assert.Equal(ConsoleKey.K, RunConfigurationEditor.MenuAction(new('k', ConsoleKey.K, false, alt, control)));
    }

    [Fact]
    public void ControlCStillQuits() => Assert.Equal(ConsoleKey.Q,
        RunConfigurationEditor.MenuAction(new('\x03', ConsoleKey.C, false, false, true)));
}
