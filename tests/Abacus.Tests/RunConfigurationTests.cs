using System.Text.Json.Nodes;
using Abacus;

namespace Abacus.Tests;

public sealed class RunConfigurationTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("abacus-config-");
    private string Write(string json, string name = "run.json")
    {
        var path = Path.Combine(root.FullName, name);
        File.WriteAllText(path, json);
        return path;
    }
    private const string Complete = """
        {"version":1,"repo":"repo","mode":"codex","model":"saved",
         "agents":[{"name":"one","workspace":"worktrees/one"}],"eventLog":"events.jsonl",
         "labels":["saved"],"reasoningModels":{"high":"high-saved","low":"low-saved"},
         "once":true,"noIntro":true,"tuiAudio":true}
        """;

    [Fact]
    public void LoadsConfigRelativePathsAndDefaultValues()
    {
        var options = Options.Parse(["run", "--config=" + Write(Complete)]).Value!;
        Assert.Equal("saved", options.Model);
        Assert.Equal("high", options.Effort);
        Assert.Equal(Path.Combine(root.FullName, "repo"), options.RepositoryPath);
        Assert.Equal(Path.Combine(root.FullName, "worktrees/one"), options.Agents.Single().WorkspacePath);
        Assert.Equal(Path.Combine(root.FullName, "events.jsonl"), options.EventLogPath);
        Assert.Equal(ExecutionMode.Once, options.ExecutionMode);
    }

    [Fact]
    public void CliOverridesScalarsListsFlagsAndIndividualReasoningTiers()
    {
        var path = Write(Complete);
        var options = Options.Parse(["--repo", "/tmp/controller", "run", "--model", "cli", "--config", path,
            "-a", "replacement", "/tmp/replacement", "--label", "new", "--label", "another",
            "--reasoning-model", "high", "high-cli", "--drain", "--no-intro=false",
            "--tui-audio=false"]).Value!;
        Assert.Equal("cli", options.Model);
        Assert.Equal("/tmp/controller", options.RepositoryPath);
        Assert.Equal("replacement", options.Agents.Single().Name);
        Assert.Equal(new[] { "new", "another" }, options.DispatchFilters!.Labels);
        Assert.Equal("high-cli", options.EffectiveReasoningModels[ReasoningPolicy.HighLabel]);
        Assert.Equal("low-saved", options.EffectiveReasoningModels[ReasoningPolicy.LowLabel]);
        Assert.Equal(ExecutionMode.Drain, options.ExecutionMode);
        Assert.False(options.NoIntro);
        Assert.False(options.TuiAudio);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", path, "--model", "a", "--model", "b"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", path, "--no-intro=perhaps"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", path, "--tui-audio=perhaps"]));
    }

    [Fact]
    public void PreflightIgnoresSavedRunOnlyControls()
    {
        var options = Options.Parse(["preflight", "--config", Write(Complete)]).Value!;
        Assert.True(options.CheckOnly);
        Assert.Equal(ExecutionMode.Continuous, options.ExecutionMode);
        Assert.Null(options.EventLogPath);
        Assert.False(options.NoIntro);
        Assert.False(options.TuiAudio);
    }

    [Fact]
    public void SaveAsPreservesInputAndRebasesRelativePaths()
    {
        var source = Write(Complete);
        var config = RunConfiguration.Load(source);
        config.Document["model"] = "edited";
        var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
        var destination = Path.Combine(directory.FullName, "copy.json");
        config.Save(destination, false);
        Assert.Equal(Complete, File.ReadAllText(source));
        Assert.Equal("../repo", config.Document["repo"]!.GetValue<string>());
        var options = Options.Parse(["run", "--config", destination]).Value!;
        Assert.Equal("edited", options.Model);
        Assert.Equal(Path.Combine(root.FullName, "repo"), options.RepositoryPath);
        Assert.Equal(Path.Combine(root.FullName, "worktrees/one"), options.Agents.Single().WorkspacePath);
        Assert.Equal(Path.Combine(root.FullName, "events.jsonl"), options.EventLogPath);
        Assert.Throws<IOException>(() => config.Save(destination, false));
        Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
    }

    [Fact]
    public void IncompleteDraftSavesWithWarningsButCannotRun()
    {
        var config = RunConfiguration.Create(root.FullName);
        Assert.Contains(config.Warnings(), w => w.Contains("model"));
        Assert.Contains(config.Warnings(), w => w.Contains("workspace"));
        var path = Path.Combine(root.FullName, "draft.json");
        config.Save(path, false);
        Assert.NotEmpty(RunConfiguration.Load(path).Warnings());
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", path]));
        config.Document["agents"] = new JsonArray(new JsonObject { ["name"] = "unfinished" });
        config.Save(path, true);
        Assert.NotEmpty(RunConfiguration.Load(path).Warnings());
    }

    [Fact]
    public void EmptyRepositoryPathStillWarnsWithOtherwiseCompleteSettings()
    {
        var config = RunConfiguration.Load(Write(Complete));
        config.Document["repo"] = "";
        Assert.NotEmpty(config.Warnings());
    }

    [Fact]
    public void SemanticErrorsAreEditableAndOverridable()
    {
        var path = Write("""{"version":1,"mode":"wrong","model":"saved","priority":99}""");
        var config = RunConfiguration.Load(path);
        Assert.NotEmpty(config.Warnings());
        config.Save(path, true);
        var options = Options.Parse(["run", "--config", path, "--mode", "codex", "--priority", "0",
            "--agent", "one", "/tmp/one"]).Value!;
        Assert.Equal(0, options.DispatchFilters!.Priority);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"version\":1,\"unknown\":true}")]
    [InlineData("{\"version\":1,\"noTuiSound\":true}")]
    [InlineData("{\"version\":1,\"model\":false}")]
    [InlineData("{\"version\":1,\"agents\":[null]}")]
    [InlineData("{\"version\":1,\"agents\":[{\"workspce\":\"a\"}]}")]
    [InlineData("{\"version\":1,\"model\":\"a\",\"model\":\"b\"}")]
    [InlineData("{\"version\":1,\"reasoningModels\":{\"other\":\"a\"}}")]
    public void MalformedDocumentsFailClearly(string json) =>
        Assert.Throws<OptionsException>(() => RunConfiguration.Load(Write(json)));

    [Fact]
    public void SupportsAllRemainingRunSettingsAndFalseOverrides()
    {
        var path = Write("""
            {"version":1,"mode":"claude","model":"opus","effort":"max",
             "agents":[{"name":"one","workspace":"one"}],
             "tmuxSession":"workers","tmuxWindow":"agents","tmuxLayout":"even-horizontal",
             "remoteControl":true,"targetFilters":["main"],"excludeLabels":["skip"],
             "type":"task,bug","priority":2,"ticketTimeout":"15m","appendPrompt":"More instructions",
             "latestComments":25,"notify":"all","notifySound":true,"tuiAudio":true,"startPaused":true}
            """);
        var options = Options.Parse(["run", "--config", path]).Value!;
        Assert.Equal("max", options.Effort);
        Assert.Equal("workers", options.TmuxSession);
        Assert.Equal("agents", options.TmuxWindow);
        Assert.Equal("even-horizontal", options.TmuxLayout);
        Assert.True(options.Remote);
        Assert.Equal(new[] { "main" }, options.TargetBranches);
        Assert.Equal(new[] { "skip" }, options.DispatchFilters!.ExcludedLabels);
        Assert.Equal("task,bug", options.DispatchFilters.IssueType);
        Assert.Equal(2, options.DispatchFilters.Priority);
        Assert.Equal(TimeSpan.FromMinutes(15), options.TicketTimeout);
        Assert.Equal("More instructions", options.AppendAgentPrompt);
        Assert.Equal(25, options.LatestCommentCount);
        Assert.Equal(NotificationMode.All, options.NotificationMode);
        Assert.True(options.NotificationSound);
        Assert.True(options.TuiAudio);
        Assert.True(options.StartPaused);
        var overridden = Options.Parse(["run", "--config", path, "--mode", "codex",
            "--remote-control=false", "--notify", "off", "--notify-sound=false",
            "--tui-audio=false", "--start-paused=false", "--verbose=true"]).Value!;
        Assert.False(overridden.Remote);
        Assert.False(overridden.NotificationSound);
        Assert.False(overridden.TuiAudio);
        Assert.False(overridden.StartPaused);
        Assert.True(overridden.Verbose);
    }

    [Fact]
    public void EditorAndHelpDoNotNeedRunPrerequisites()
    {
        var parsed = Options.Parse(["config", "edit", "input.json", "--output", "copy.json"]);
        Assert.True(parsed.EditConfiguration);
        Assert.Equal("input.json", parsed.ConfigurationInput);
        Assert.Equal("copy.json", parsed.ConfigurationOutput);
        Assert.True(Options.Parse(["config", "edit"]).EditConfiguration);
        Assert.True(Options.Parse(["run", "--config", "/missing.json", "--help"]).ShowHelp);
        Assert.True(Options.Parse(["config", "edit", "--help"]).ShowHelp);
        Assert.Throws<OptionsException>(() => Options.Parse(["config", "edit", "--repo", "/tmp"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["config", "edit", "a", "b"]));
    }

    [Theory]
    [InlineData("--config")]
    [InlineData("--help")]
    [InlineData("--repo")]
    public void PromptValuesRemainLiteral(string prompt)
    {
        var config = RunConfiguration.Load(Write(Complete));
        config.Document["appendPrompt"] = prompt;
        var path = Path.Combine(root.FullName, "literal.json");
        config.Save(path, false);
        Assert.Equal(prompt, Options.Parse(["run", "--config", path]).Value!.AppendAgentPrompt);
        Assert.Equal(prompt, Options.Parse(["run", "--config", path, "--append-prompt", prompt]).Value!.AppendAgentPrompt);
    }

    private string Child(string parent, string json, string name = "child.json")
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document["baseConfig"] = Path.GetRelativePath(Path.GetDirectoryName(Path.Combine(root.FullName, name))!, parent);
        return Write(document.ToJsonString(), name);
    }

    [Fact]
    public void DerivedSettingsOverrideBaseAndCliHasFinalPrecedence()
    {
        var first = Write(Complete);
        var second = Child(first, """{"version":1,"model":"second","effort":"medium"}""", "second.json");
        var last = Child(second, """{"version":1,"model":"last"}""");
        var options = Options.Parse(["run", "--config", last]).Value!;
        Assert.Equal("last", options.Model);
        Assert.Equal("medium", options.Effort);
        Assert.Single(options.Agents);
        Assert.Equal("cli", Options.Parse(["run", "--model", "cli", "--config", last]).Value!.Model);
        Assert.Equal(Complete, File.ReadAllText(first));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", first, "--config", last]));
    }

    [Fact]
    public void InheritedPathsStayRelativeToTheirSourceAndSaveAsRebasesBaseReference()
    {
        var first = Write(Complete);
        var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
        var second = Child(first, """
            {"version":1,"agents":[{"name":"new","workspace":"worktrees/new"}],"eventLog":"log.jsonl"}
            """, "nested/second.json");
        var options = Options.Parse(["run", "--config", second]).Value!;
        Assert.Equal(Path.Combine(root.FullName, "repo"), options.RepositoryPath);
        Assert.Equal(Path.Combine(nested.FullName, "worktrees/new"), options.Agents.Single().WorkspacePath);
        Assert.Equal(Path.Combine(nested.FullName, "log.jsonl"), options.EventLogPath);
        var third = Child(first, """{"version":1,"repo":"another-repo"}""", "nested/third.json");
        options = Options.Parse(["run", "--config", third]).Value!;
        Assert.Equal(Path.Combine(nested.FullName, "another-repo"), options.RepositoryPath);
        Assert.Equal(Path.Combine(root.FullName, "worktrees/one"), options.Agents.Single().WorkspacePath);
        var editable = RunConfiguration.Load(second);
        Assert.Empty(editable.Warnings());
        var copy = Path.Combine(root.FullName, "copy.json");
        editable.Save(copy, false);
        Assert.Equal("run.json", editable.Document["baseConfig"]!.GetValue<string>());
        Assert.False(editable.Document.ContainsKey("model")); // Save As doesn't flatten inherited fields.
        Assert.Equal(Path.Combine(nested.FullName, "worktrees/new"), Options.Parse(["run", "--config", copy]).Value!.Agents.Single().WorkspacePath);
        Assert.Equal("../run.json", RunConfiguration.Load(second).Document["baseConfig"]!.GetValue<string>());
    }

    [Fact]
    public void ListsReplaceAndModelsMergePerTierWithExplicitClears()
    {
        var first = Write(Complete);
        var second = Child(first, """
            {"version":1,"labels":[],"noIntro":false,"repo":null,"eventLog":null,"once":null,
             "reasoningModels":{"high":null,"medium":"new-medium"}}
            """);
        var options = Options.Parse(["run", "--config", second]).Value!;
        Assert.Empty(options.DispatchFilters!.Labels);
        Assert.False(options.NoIntro);
        Assert.Null(options.RepositoryPath);
        Assert.Null(options.EventLogPath);
        Assert.Equal(ExecutionMode.Continuous, options.ExecutionMode);
        Assert.False(options.EffectiveReasoningModels.ContainsKey(ReasoningPolicy.HighLabel));
        Assert.Equal("new-medium", options.EffectiveReasoningModels[ReasoningPolicy.MediumLabel]);
        Assert.Equal("low-saved", options.EffectiveReasoningModels[ReasoningPolicy.LowLabel]);
        options = Options.Parse(["run", "--config", second, "--reasoning-model", "high", "cli-high", "--label", "cli-label"]).Value!;
        Assert.Equal("cli-high", options.EffectiveReasoningModels[ReasoningPolicy.HighLabel]);
        Assert.Equal(new[] { "cli-label" }, options.DispatchFilters!.Labels);
        var third = Child(second, """{"version":1,"agents":[],"reasoningModels":null}""", "third.json");
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", third]));
        options = Options.Parse(["run", "--config", third, "-a", "cli", "/tmp/cli"]).Value!;
        Assert.Empty(options.EffectiveReasoningModels);
        Assert.Equal("cli", options.Agents.Single().Name);
    }

    [Fact]
    public void PartialBaseIsValidatedOnlyAfterCompositionAndExecutionChoiceReplacesBase()
    {
        var first = Write("""{"version":1,"mode":"invalid","once":true,"agents":[{"name":"one","workspace":"one"}]}""");
        var second = Child(first, """{"version":1,"mode":"codex","model":"model","drain":true}""");
        var options = Options.Parse(["run", "--config", second]).Value!;
        Assert.Equal(ExecutionMode.Drain, options.ExecutionMode);
        Assert.Equal(AgentMode.Codex, options.AgentMode);
        var third = Child(second, """{"version":1,"drain":false}""", "third.json");
        Assert.Equal(ExecutionMode.Continuous, Options.Parse(["run", "--config", third]).Value!.ExecutionMode);
        Assert.Equal(ExecutionMode.Once, Options.Parse(["run", "--config", second, "--once"]).Value!.ExecutionMode);
        var bad = Child(second, """{"version":1,"once":true,"drain":true}""", "bad.json");
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", bad]));
        Assert.True(Options.Parse(["preflight", "--config", second]).Value!.CheckOnly);
    }

    [Theory]
    [InlineData("missing.json")]
    [InlineData("self.json")]
    [InlineData("./self.json")]
    [InlineData("")]
    public void MissingOrCyclicBasesFailClearlyButRemainEditable(string reference)
    {
        var path = Write(new JsonObject { ["version"] = 1, ["baseConfig"] = reference }.ToJsonString(), "self.json");
        var editable = RunConfiguration.Load(path);
        Assert.NotEmpty(editable.Warnings());
        editable.Save(path, true);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", path]));
    }

    [Fact]
    public void MultiFileCycleAndMalformedBaseAreRejected()
    {
        var a = Write("""{"version":1,"baseConfig":"b.json"}""", "a.json");
        Write("""{"version":1,"baseConfig":"a.json"}""", "b.json");
        Assert.Contains("cycle", Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", a])).Message);
        Write("not json", "b.json");
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--config", a]));
        Assert.True(Options.Parse(["run", "--config", a, "--help"]).ShowHelp);
        Write("""{"version":1,"baseConfig":[]}""", "a.json");
        Assert.Throws<OptionsException>(() => RunConfiguration.Load(a));
    }

    public void Dispose() => root.Delete(true);
}
