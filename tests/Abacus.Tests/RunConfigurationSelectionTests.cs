using Abacus;

namespace Abacus.Tests;

public sealed class RunConfigurationSelectionTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("abacus-selection-");
    private string Write(string name, string json)
    {
        var path = Path.Combine(root.FullName, name);
        File.WriteAllText(path, json);
        return path;
    }
    private const string Complete = """{"version":1,"mode":"codex","model":"saved","agents":[{"name":"one","workspace":"one"}]}""";

    [Fact]
    public void MissingArgumentsAreCollectedThenOneConfigIsSelectedWithCliOverrides()
    {
        var path = Write("run.json", Complete);
        var output = new StringWriter();
        var calls = 0;
        var options = Options.Parse(["run", "--model", "provider/cli#low", "--no-intro=false", "--"], missing =>
        {
            calls++;
            Assert.Contains(missing, m => m.Contains("workspace"));
            return RunConfigurationSelection.Select(root.FullName, missing, new StringReader("1\n"), output);
        }).Value!;
        Assert.Equal(1, calls);
        Assert.Equal("provider/cli", options.Model);
        Assert.Equal("low", options.Effort);
        Assert.False(options.NoIntro);
        Assert.Contains("Missing required", output.ToString());
        Assert.Contains("run.json", output.ToString());
        Assert.Equal(Path.Combine(root.FullName, "one"), options.Agents.Single().WorkspacePath);
    }

    [Fact]
    public void InteractivePickerUsesColorForStructureWhenEnabled()
    {
        var path = Write("run.json", Complete);
        var output = new StringWriter();

        var selected = RunConfigurationSelection.Select(
            root.FullName, ["--model <model>"], new StringReader("1\n"), output, color: true);

        Assert.Equal(path, selected);
        Assert.Contains("\u001b[1m\u001b[36mRun configuration required\u001b[0m", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[33m[WARN]\u001b[0m", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("\u001b[32m1.\u001b[0m", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FailedSelectionReportsAllRemainingMissingArgumentsWithoutRetrying()
    {
        var path = Write("incomplete.json", """{"version":1,"mode":"opencode-server"}""");
        var calls = 0;
        var error = Assert.Throws<OptionsException>(() => Options.Parse(["run"], _ => { calls++; return path; }));
        Assert.Equal(1, calls);
        Assert.Contains("--model", error.Message);
        Assert.Contains("workspace", error.Message);
        Assert.Contains("--opencode-server", error.Message);
        Assert.Equal(3, error.Missing!.Count);
    }

    [Fact]
    public void ExplicitConfigNeverOffersPickerAndReportsMissingAgentFields()
    {
        var path = Write("incomplete.json", """{"version":1,"agents":[{}]}""");
        var error = Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", path], _ => throw new Exception("must not prompt")));
        Assert.Contains("--model", error.Message);
        Assert.Contains("requires a name", error.Message);
        Assert.Contains("requires a workspace", error.Message);
    }

    [Theory]
    [InlineData("run", "--stdio")]
    [InlineData("run", "--stdio=true")]
    [InlineData("run", "--verbose")]
    [InlineData("run", "-v")]
    [InlineData("run", "--verbose=true")]
    [InlineData("preflight")]
    public void NonInteractiveRoutesDoNotCallSelector(params string[] args)
    {
        var error = Assert.Throws<OptionsException>(() => Options.Parse(args, _ => throw new Exception("must not prompt")));
        Assert.NotNull(error.Missing);
    }

    [Fact]
    public void CompleteCliHelpAndInvalidOptionsNeverCallSelector()
    {
        string? Never(IReadOnlyList<string> _) => throw new Exception("must not prompt");
        Assert.NotNull(Options.Parse(["run", "--mode", "codex", "--model", "model", "-a", "one", "/tmp/one"], Never).Value);
        Assert.True(Options.Parse(["run", "--help"], Never).ShowHelp);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--bogus"], Never));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--priority", "99"], Never));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model"], Never));
    }

    [Theory]
    [InlineData("--stdio")]
    [InlineData("--config")]
    [InlineData("--help")]
    public void LiteralPromptDoesNotChangeSelectionEligibility(string text)
    {
        var path = Write("run.json", Complete);
        var options = Options.Parse(["run", "--append-prompt", text, "--stdio=false", "--verbose=false"], _ => path).Value!;
        Assert.Equal(text, options.AppendAgentPrompt);
    }

    [Fact]
    public void DiscoveryOnlyIncludesTopLevelStructurallyValidRunConfigs()
    {
        var first = Write("a.JSON", Complete);
        var second = Write("b.json", """{"version":1,"baseConfig":"missing.json"}""");
        Write("unrelated.json", """{"version":1,"targets":{"main":{}}}""");
        Write("malformed.json", "{");
        Write("config.txt", Complete);
        Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
        Write("nested/run.json", Complete);
        Assert.Equal(new[] { first, second }, RunConfigurationSelection.Discover(root.FullName));
        Assert.Equal(second, RunConfigurationSelection.Select(root.FullName, ["--model"], new StringReader("2\n"), new StringWriter()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("q\n")]
    [InlineData("\n")]
    public void CancellationDoesNotRunAnything(string response)
    {
        Write("run.json", Complete);
        Assert.Null(RunConfigurationSelection.Select(root.FullName, ["--model"], new StringReader(response), new StringWriter()));
        Assert.Throws<OptionsException>(() => Options.Parse(["run"], _ => null));
    }

    [Fact]
    public void NoCandidatesOrBadSelectionFailsOnce()
    {
        Assert.Throws<OptionsException>(() => RunConfigurationSelection.Select(root.FullName, ["--model"], new StringReader("1\n"), new StringWriter()));
        Write("run.json", Complete);
        Assert.Throws<OptionsException>(() => RunConfigurationSelection.Select(root.FullName, ["--model"], new StringReader("5\n"), new StringWriter()));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--stdio")]
    [InlineData("run", "--verbose")]
    [InlineData("preflight")]
    public async Task RedirectedProcessesFailWithoutSearchingOrReadingInput(params string[] args)
    {
        Write("run.json", Complete);
        var result = await new CommandRunner(TextWriter.Null).RunAsync(new CommandSpec(
            "dotnet", [typeof(Program).Assembly.Location, .. args], root.FullName));
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Missing required", result.StandardError);
        Assert.DoesNotContain("Searching", result.StandardError);
        Assert.DoesNotContain("Select", result.StandardError);
        Assert.DoesNotContain("\u001b", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(result.StandardOutput);
    }

    public void Dispose() => root.Delete(true);
}
