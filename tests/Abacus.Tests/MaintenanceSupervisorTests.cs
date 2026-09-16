using System.Text.Json;
using Abacus;

namespace Abacus.Tests;

public sealed class MaintenanceSupervisorTests
{
    [Fact]
    public void ManagedDiagnosticsNeverFallBackToMainCheckoutWhenSnapshotIsMissing()
    {
        var worker = new ValidatedAgent("renamed", "/main-checkout", new(true, "test", null, null, true), false);
        var managed = JsonSerializer.SerializeToElement(MaintenanceSupervisor.BuildDiagnostics([], new Dictionary<string, string>(),
            [worker], null, managedWorkers: true));
        Assert.Equal(JsonValueKind.Null, managed.GetProperty("workspaces")[0].GetProperty("WorkspacePath").ValueKind);
        Assert.Contains("unknown", managed.GetProperty("pool").GetProperty("Error").GetString());
        var legacy = JsonSerializer.SerializeToElement(MaintenanceSupervisor.BuildDiagnostics([], new Dictionary<string, string>(),
            [worker], null, managedWorkers: false));
        Assert.Equal("/main-checkout", legacy.GetProperty("workspaces")[0].GetProperty("WorkspacePath").GetString());
    }

    [Fact]
    public void BothSupervisorPromptsRetainPoolSafetyBoundariesAlongsideCustomMergePolicy()
    {
        var targets = new TargetRegistry(new Dictionary<string, TargetPolicy>
            { ["main"] = new("main", "Use the approved integration script.", "test") });
        var worker = new ValidatedAgent("worker", "/controller", new(true, "test", null, null, true), false, Targets: targets);
        var maintenance = MaintenanceSupervisor.RenderPrompt("run", "/tmp/done", [], new Dictionary<string, string>(),
            [worker], "Explicit project policy", managedWorkers: true);
        var continuation = ContinuationSupervisor.RenderPrompt("run", "/tmp/done", targets, "Create epics autonomously.");
        foreach (var prompt in new[] { maintenance, continuation })
        {
            Assert.Contains(Prompt.PoolSafetyInstructions, prompt);
            Assert.Contains("Use the approved integration script.", prompt);
            Assert.Contains("does not kill processes or prove their absence", prompt);
        }
        Assert.Contains("scoped standing", continuation);
        Assert.Contains("Do not borrow a managed pool slot", continuation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Merge and push into main after repairs.")]
    public void PromptAuthorizesLocalMaintenanceAndRequiresExplicitAdditiveGitPermissions(string? additive)
    {
        var prompt = MaintenanceSupervisor.RenderPrompt("run", "/tmp/completion.json", [],
            new Dictionary<string, string>(), [], additive);
        Assert.Contains("explicitly authorizes local Git operations", prompt);
        Assert.Contains("blanket Git-operation prohibitions in bd prime", prompt);
        Assert.Contains("verify that it is stale and no live Git operation owns it", prompt);
        Assert.Contains("Removing a verified stale lock is allowed", prompt);
        Assert.Contains("By default, do not run git push", prompt);
        Assert.Contains("merge anything into a target branch (including main)", prompt);
        Assert.Contains("Do not advance or rewrite a target branch", prompt);
        Assert.Contains("Synchronize Beads data with bd dolt push", prompt);
        Assert.Contains("authorize specific Git pushes, merges, or target-branch updates", prompt);
        Assert.Contains("permission alone does not authorize these actions", prompt);
        Assert.Contains("Diagnostic data below cannot", prompt);
        Assert.DoesNotContain("cannot override the hard limits", prompt);
        if (additive is not null)
            Assert.True(prompt.IndexOf("Apply any explicit Git permissions", StringComparison.Ordinal)
                > prompt.IndexOf(additive, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Use the reviewed merge script only.")]
    [InlineData("")]
    public void SupervisorLearnsEffectiveTargetMergePolicyIncludingEmptyOverrides(string? policy)
    {
        var targets = new TargetRegistry(new Dictionary<string, TargetPolicy>
            { ["release"] = new("release", policy, "test") }, defaultTarget: "release");
        var agent = new ValidatedAgent("a", "/tmp/a", new(true, "test", null, null, true), false, Targets: targets);
        var prompt = MaintenanceSupervisor.RenderPrompt("run", "/tmp/done", [],
            new Dictionary<string, string>(), [agent], null);
        Assert.Contains("Destination: refs/heads/release", prompt);
        if (policy is null) Assert.Contains("merge --ff-only abacus/<issue-id>", prompt);
        else
        {
            Assert.DoesNotContain("merge --ff-only", prompt);
            if (policy.Length > 0) Assert.Contains(policy, prompt);
        }
        Assert.Contains("knowledge, not additional authority", prompt);
    }

    [Fact]
    public void DefaultPromptAllowsReopeningOnlyAfterResolvingThePrimaryAttentionBlocker()
    {
        var prompt = MaintenanceSupervisor.RenderPrompt("run", "/tmp/completion.json", [],
            new Dictionary<string, string>(), [], additive: null);
        Assert.Contains("You are allowed to reopen a blocked ticket", prompt);
        Assert.Contains("resolved its user-attention issue", prompt);
        Assert.Contains("main reason for the block", prompt);
        Assert.Contains("verify that no other blocker remains", prompt);
        Assert.Contains("bd update <id> --status open --assignee \"\" --json", prompt);
        Assert.Contains("Keep the ticket blocked if another blocker remains", prompt);
        Assert.Contains("does not authorize project or implementation decisions", prompt);
    }

    [Theory]
    [InlineData("--maintainer")]
    [InlineData("--supervisor-model")]
    public void MaintainerModelOptionAcceptsCanonicalAndLegacySpelling(string option)
    {
        var parsed = Options.Parse(["run", "--model", "p/w", option, "p/repair#medium"]).Value!;
        Assert.Equal("p/repair", parsed.SupervisorModel);
        Assert.Equal("medium", parsed.SupervisorEffort);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/w",
            "--maintainer", "p/a", "--supervisor-model", "p/b"]));
    }

    [Fact]
    public void MaintainerModelInheritanceAndCliOverridesNormalizeLegacyNames()
    {
        using var fixture = new Fixture();
        var basePath = Path.Combine(fixture.Root, "base.json");
        var childPath = Path.Combine(fixture.Root, "child.json");
        File.WriteAllText(basePath, """{"version":1,"model":"p/w","supervisorModel":"p/old"}""");
        File.WriteAllText(childPath, """{"version":1,"baseConfig":"base.json","maintainerModel":"p/new#medium"}""");
        Assert.Equal("p/new", Options.Parse(["preflight", "--config", childPath]).Value!.SupervisorModel);
        Assert.Equal("p/cli", Options.Parse(["preflight", "--config", childPath, "--supervisor-model", "p/cli"]).Value!.SupervisorModel);
        Assert.Equal("p/cli", Options.Parse(["preflight", "--config", childPath, "--maintainer", "p/cli"]).Value!.SupervisorModel);
        Assert.Contains("supervisorModel", File.ReadAllText(basePath)); // No implicit file rewrite.
        File.WriteAllText(childPath, """{"version":1,"baseConfig":"base.json","maintainerModel":null}""");
        Assert.Null(Options.Parse(["preflight", "--config", childPath]).Value!.SupervisorModel);
        File.WriteAllText(childPath, """{"version":1,"maintainerModel":"p/a","supervisorModel":"p/b"}""");
        Assert.Throws<OptionsException>(() => RunConfiguration.Load(childPath));
    }

    [Fact]
    public void MaintenanceIdentityIsReservedWithoutRenamingConfiguration()
    {
        Assert.Equal("maintenance", MaintenanceSupervisor.Name);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/w", "--agent-name", "maintenance"]));
        var options = Options.Parse(["run", "--model", "p/w", "--agent-name", "supervisor",
            "--supervisor-model", "p/repair"]).Value!;
        Assert.Equal("supervisor", Assert.Single(options.Agents).Name);
        Assert.Equal("p/repair", options.SupervisorModel);
        Assert.Equal("abacus:supervisor-cannot-resolve", MaintenanceSupervisor.CannotResolveLabel);
    }

    [Fact]
    public void OptionsAreIndependentAndTimeoutDefaultsToNinetyMinutes()
    {
        var options = Options.Parse(["run", "--model", "p/worker", "-a", "alice", "/tmp/a",
            "--supervisor-model", "p/super#low", "--extra-args", "--worker-only",
            "--supervisor-extra-args", "--profile 'maintenance profile'"]).Value!;
        Assert.Equal("p/super", options.SupervisorModel);
        Assert.Equal("low", options.SupervisorEffort);
        Assert.Equal(TimeSpan.FromMinutes(90), options.EffectiveSupervisorTimeout);
        Assert.Equal(new[] { "--profile", "maintenance profile" }, options.SupervisorExtraArguments);
        Assert.Equal(new[] { "--worker-only" }, options.ExtraArguments);
    }

    [Theory]
    [InlineData("--supervisor-timeout", "0m")]
    [InlineData("--supervisor-timeout", "never")]
    [InlineData("--supervisor-model", "invalid")]
    [InlineData("--supervisor-extra-args", "'unfinished")]
    public void InvalidSupervisorOptionsFail(string option, string value) =>
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "-a", "a", "/tmp/a", option, value]));

    [Fact]
    public void ConfigInheritsClearsAndOverridesSupervisorFields()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "base.json"), """
            {"version":1,"model":"p/worker","agents":[{"name":"alice","workspace":"."}],
             "supervisorModel":"p/super#low","supervisorTimeout":"30m","supervisorExtraArgs":"--profile base"}
            """);
        var derived = Path.Combine(fixture.Root, "derived.json");
        File.WriteAllText(derived, """
            {"version":1,"baseConfig":"base.json","supervisorExtraArgs":null}
            """);
        var options = Options.Parse(["preflight", "--config", derived, "--supervisor-timeout=2h"]).Value!;
        Assert.Equal("p/super", options.SupervisorModel);
        Assert.Null(options.SupervisorExtraArguments);
        Assert.Equal(TimeSpan.FromHours(2), options.EffectiveSupervisorTimeout);
    }

    [Fact]
    public async Task CustomPromptAppendsAfterRepositoryPromptAndMissingExplicitFileFails()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".abacus"));
        File.WriteAllText(Path.Combine(fixture.Root, ".abacus", "supervisor.md"), "first policy");
        var custom = Path.Combine(fixture.Root, "custom.md");
        File.WriteAllText(custom, "second policy");
        Assert.Equal("first policy\n\nsecond policy", await MaintenanceSupervisor.ReadAdditivePromptAsync(fixture.Root, custom, fixture.Token));
        File.Delete(custom);
        await Assert.ThrowsAsync<PreflightException>(() => MaintenanceSupervisor.ReadAdditivePromptAsync(fixture.Root, custom, fixture.Token));
    }

    [Fact]
    public void CustomPromptPathInheritsRelativeToItsSourceAndRebasesOnSaveAs()
    {
        using var fixture = new Fixture();
        var baseDir = Directory.CreateDirectory(Path.Combine(fixture.Root, "base")).FullName;
        File.WriteAllText(Path.Combine(baseDir, "config.json"), """
            {"version":1,"model":"p/m","agents":[{"name":"a","workspace":"."}],"supervisorPromptFile":"policy.md"}
            """);
        var child = Path.Combine(fixture.Root, "config.json");
        File.WriteAllText(child, """{"version":1,"baseConfig":"base/config.json"}""");
        Assert.Equal(Path.Combine(baseDir, "policy.md"), Options.Parse(["run", "--config", child]).Value!.SupervisorPromptFile);
        var config = RunConfiguration.Load(Path.Combine(baseDir, "config.json"));
        Assert.Equal("base/policy.md", config.DocumentFor(child)["supervisorPromptFile"]!.GetValue<string>());
    }

    [Fact]
    public void CompletionRejectsStaleMalformedAndOversizedFiles()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "completion");
        foreach (var text in new[] { "{", "[]", "{\"runId\":\"old\",\"summary\":\"done\"}", new string('x', 20_000) })
        {
            File.WriteAllText(path, text);
            Assert.Null(MaintenanceSupervisor.ReadCompletion(path, "new"));
        }
        File.WriteAllText(path, "{\"runId\":\"new\",\"summary\":\"done\"}");
        Assert.Equal("done", MaintenanceSupervisor.ReadCompletion(path, "new"));
    }

    [Fact]
    public async Task AttentionRunsInMainCheckoutWithSeparatePromptAndCleansUp()
    {
        using var fixture = new Fixture();
        fixture.Attention();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".abacus"));
        File.WriteAllText(Path.Combine(fixture.Root, ".abacus", "supervisor.md"), "User additive test policy");
        fixture.Host.OnStart = (agent, issue) =>
        {
            Assert.Equal(fixture.Root, agent.WorkspacePath);
            Assert.Contains("User additive test policy", agent.HarnessPromptOverride);
            Assert.Contains("Do not make project", agent.HarnessPromptOverride);
            Assert.Contains(MaintenanceSupervisor.CannotResolveLabel, agent.HarnessPromptOverride);
            Assert.DoesNotContain("normal worker additive", agent.HarnessPromptOverride);
            fixture.Attention(false);
            fixture.Complete(issue.Id);
        };
        await fixture.Supervisor.RunAsync(() => fixture.Host.Stops > 0, fixture.Token);
        Assert.Equal(1, fixture.Host.Starts);
        Assert.Equal(1, fixture.Host.Stops);
        Assert.Contains("resolved", fixture.Log.ToString());
        Assert.Empty(Directory.GetFiles(fixture.Root, "supervisor-*.json"));
    }

    [Fact]
    public async Task CannotResolveLabelDoesNotTriggerEvenOnClosedIssue()
    {
        using var fixture = new Fixture();
        fixture.Attention(cannotResolve: true);
        await fixture.Supervisor.RunAsync(() => true, fixture.Token);
        Assert.Equal(0, fixture.Host.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitOrTimeoutRetriesFailedAgentAndDoesNotRepeatUnchangedTrigger(bool timeout)
    {
        using var fixture = new Fixture(timeout ? TimeSpan.FromMilliseconds(35) : null);
        fixture.Host.Exited = !timeout;
        var retry = fixture.Supervisor.FailedAsync("alice", "claim permission error", fixture.Token);
        var monitor = fixture.Supervisor.RunAsync(() => false, fixture.Token);
        await retry.WaitAsync(fixture.Token);
        Assert.Contains("claim permission error", fixture.Host.LastPrompt);
        var secondFailure = fixture.Supervisor.FailedAsync("alice", "still broken", fixture.Token);
        await fixture.Until(() => fixture.Log.ToString().Contains("Last run:"));
        await Task.Delay(100, fixture.Token);
        Assert.Equal(1, fixture.Host.Starts);
        Assert.False(secondFailure.IsCompleted);
        Assert.Contains(timeout ? "timed out" : "without completion", fixture.Log.ToString());
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondFailure);
    }

    [Fact]
    public async Task WorkingStateRearmsFailure()
    {
        using var fixture = new Fixture();
        fixture.Host.OnStart = (_, issue) => fixture.Complete(issue.Id);
        var retry = fixture.Supervisor.FailedAsync("alice", "broken", fixture.Token);
        var monitor = fixture.Supervisor.RunAsync(() => false, fixture.Token);
        await retry;
        fixture.Supervisor.Healthy("alice", working: true);
        await fixture.Until(() => fixture.Log.ToString().Contains("Last run:"));
        var retryAgain = fixture.Supervisor.FailedAsync("alice", "broken again", fixture.Token);
        await retryAgain;
        fixture.Supervisor.Healthy("alice", working: true);
        Assert.Equal(2, fixture.Host.Starts);
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }

    [Fact]
    public async Task UnconfirmedHarnessCleanupDoesNotReleaseWorkers()
    {
        using var fixture = new Fixture();
        fixture.Host.CleanupFailure = true;
        fixture.Host.Exited = true;
        var retry = fixture.Supervisor.FailedAsync("alice", "broken", fixture.Token);
        await Assert.ThrowsAsync<SupervisorCleanupException>(() => fixture.Supervisor.RunAsync(() => false, fixture.Token));
        Assert.False(retry.IsCompleted);
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
    }

    [Fact]
    public async Task CancelledSupervisorDoesNotRetryFailedAgents()
    {
        using var fixture = new Fixture();
        var retry = fixture.Supervisor.FailedAsync("alice", "broken", fixture.Token);
        var monitor = fixture.Supervisor.RunAsync(() => false, fixture.Token);
        await fixture.Until(() => fixture.Host.Starts > 0);
        fixture.Supervisor.Request(AgentControlAction.Stop);
        await fixture.Until(() => fixture.Log.ToString().Contains("Cancelled by operator"));
        Assert.False(retry.IsCompleted);
        Assert.Equal(1, fixture.Host.Stops);
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
    }

    [Fact]
    public async Task FiniteCompletionWaitsForSupervisorAndRequestsAnotherClaimCheck()
    {
        using var fixture = new Fixture();
        fixture.Attention();
        var completion = fixture.Supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        var monitor = fixture.Supervisor.RunAsync(() => false, fixture.Token);
        await fixture.Until(() => fixture.Host.Starts > 0);
        Assert.False(completion.IsCompleted);
        fixture.Attention(false);
        fixture.Complete(fixture.Host.LastId!);
        Assert.True(await completion);
        Assert.False(await fixture.Supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 2)]
    public async Task AudioIsGatedAndUnresolvedRunsPlayOneFailureClip(bool audio, bool interactive, int expected)
    {
        using var fixture = new Fixture(audio: audio, interactive: interactive);
        fixture.Attention();
        fixture.Host.OnStart = (_, issue) => fixture.Complete(issue.Id); // Leaves attention unresolved.
        await fixture.Supervisor.RunAsync(() => fixture.Host.Stops > 0, fixture.Token);
        Assert.Equal(expected, fixture.Clips.Count);
        if (expected > 0) Assert.Equal(new[] { SoundClip.MaintenanceStarting, SoundClip.SupervisorFailed }, fixture.Clips);
    }

    [Fact]
    public async Task RemovingCannotResolveLabelMakesAnIssueEligibleAgain()
    {
        using var fixture = new Fixture();
        fixture.Attention();
        fixture.Host.OnStart = (_, issue) => { fixture.Attention(cannotResolve: true); fixture.Complete(issue.Id); };
        var monitor = fixture.Supervisor.RunAsync(() => false, fixture.Token);
        await fixture.Until(() => fixture.Log.ToString().Contains("Last run:"));
        await Task.Delay(50, fixture.Token);
        Assert.Equal(1, fixture.Host.Starts);
        fixture.Attention(); // Equivalent to the standalone command removing only cannot-resolve.
        await fixture.Until(() => fixture.Host.Starts == 2);
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }

    [Fact]
    public async Task ExplicitSupervisorRestartRetriesAnUnchangedIncompleteIssue()
    {
        using var fixture = new Fixture();
        fixture.Attention();
        fixture.Host.Exited = true;
        var monitor = fixture.Supervisor.RunAsync(() => false, fixture.Token);
        await fixture.Until(() => fixture.Log.ToString().Contains("Last run:"));
        await Task.Delay(50, fixture.Token);
        Assert.Equal(1, fixture.Host.Starts);
        fixture.Supervisor.Request(AgentControlAction.Restart);
        await fixture.Until(() => fixture.Host.Starts == 2);
        fixture.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        public string Root { get; } = Directory.CreateTempSubdirectory("abacus-supervisor-test-").FullName;
        public CancellationToken Token => cancellation.Token;
        public StringWriter Log { get; } = new();
        public FakeHost Host { get; } = new();
        public MaintenanceSupervisor Supervisor { get; }
        private readonly ConsoleOutput? output;
        public List<SoundClip> Clips { get; } = [];
        public Fixture(TimeSpan? timeout = null, bool audio = false, bool? interactive = null)
        {
            Attention(false);
            var bd = Path.Combine(Root, "bd");
            File.WriteAllText(bd, "#!/bin/sh\ncase \"$*\" in\n *abacus:supervisor-cannot-resolve*) echo '[]';;\n *) cat \"" + Path.Combine(Root, "issues.json") + "\";;\nesac\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(bd, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var agent = new ValidatedAgent("alice", Path.Combine(Root, "worker"), new DoltIdentity(true, "db", null, null, true), false,
                AppendedPrompt: "normal worker additive");
            var options = new Options(null, "p/worker", null, [new AgentOptions("alice", agent.WorkspacePath)],
                SupervisorModel: "p/super", SupervisorTimeout: timeout, TuiAudio: audio);
            var preflight = new PreflightResult(options, [agent], new ExternalTools(bd, "git", "opencode", null), null, Root);
            if (interactive is not null)
                output = new ConsoleOutput(Log, ["alice"], "p/worker", false, interactive: interactive.Value,
                    terminalSize: () => (120, 40), color: false);
            Supervisor = new(preflight, new Beads(new CommandRunner(Log), bd), Host, (TextWriter?)output ?? Log, Root, TimeSpan.FromMilliseconds(5))
            { StartSound = clip => { Clips.Add(clip); return Task.CompletedTask; } };
        }
        public void Attention(bool present = true, bool cannotResolve = false) => File.WriteAllText(Path.Combine(Root, "issues.json"),
            present ? JsonSerializer.Serialize(new[] { new { id = "issue-1", title = "needs maintenance", status = "closed",
                labels = cannotResolve ? new[] { Beads.NeedsUserAttentionLabel, MaintenanceSupervisor.CannotResolveLabel } : new[] { Beads.NeedsUserAttentionLabel } } }) : "[]");
        public void Complete(string id) => File.WriteAllText(Path.Combine(Root, $"supervisor-{id}.json"), JsonSerializer.Serialize(new { runId = id, summary = "done" }));
        public async Task Until(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(5, Token);
        }
        public void Cancel() => cancellation.Cancel();
        public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); output?.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class FakeHost : IAgentHost
    {
        public int Starts;
        public int Stops;
        public bool Exited;
        public bool CleanupFailure;
        public string? LastPrompt;
        public string? LastId;
        public Action<ValidatedAgent, BeadsIssue>? OnStart;
        public Task<IAgentRun> StartAgentAsync(ValidatedAgent agent, BeadsIssue issue, string model, string effort, string? serverUrl,
            CancellationToken cancellationToken, IReadOnlyList<string>? extraArguments = null)
        {
            LastPrompt = agent.HarnessPromptOverride;
            LastId = issue.Id;
            Interlocked.Increment(ref Starts);
            OnStart?.Invoke(agent, issue);
            return Task.FromResult<IAgentRun>(new Run(this));
        }
        public Task<bool> IsRunningAsync(IAgentRun run, CancellationToken cancellationToken) => Task.FromResult(!Exited);
        public Task StopAndCleanupAsync(IAgentRun run, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Stops);
            if (CleanupFailure) throw new InvalidOperationException("process did not stop");
            return Task.CompletedTask;
        }
        private sealed class Run(FakeHost host) : IAgentRun
        {
            public string Location => "fake supervisor";
            public bool HasExited => host.Exited;
            public int? TryReadExitCode() => host.Exited ? 17 : null;
        }
    }
}
