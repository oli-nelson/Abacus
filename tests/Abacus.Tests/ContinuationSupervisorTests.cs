using System.Text.Json;
using Abacus;

namespace Abacus.Tests;

public sealed class ContinuationSupervisorTests
{
    [Theory]
    [InlineData(false, true, false, 0)]
    [InlineData(true, false, false, 0)]
    [InlineData(true, true, false, 1)]
    [InlineData(true, true, true, 1)]
    public async Task StartupAudioUsesContinuationClipAndIsBestEffort(bool audio, bool interactive, bool failAudio, int expected)
    {
        using var fixture = new Fixture();
        using var output = new ConsoleOutput(new StringWriter(), ["alice"], "p/w", false,
            interactive: interactive, terminalSize: () => (120, 40), color: false);
        var supervisor = fixture.Supervisor(audio: audio, log: output, failAudio: failAudio);
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token); // No replay on the unchanged empty backlog.
        Assert.Equal(expected, fixture.Clips.Count);
        if (expected > 0) Assert.Equal(SoundClip.ContinuationStarting, Assert.Single(fixture.Clips));
        Assert.Equal(1, fixture.Host.Starts);
        Assert.Equal(1, fixture.Host.Stops);
    }

    [Fact]
    public void IndependentOptionsValidateModelsArgumentsAndDefaults()
    {
        var options = Options.Parse(["run", "--model", "p/worker", "--continuation-model", "p/planner#low",
            "--continuation-extra-args", "--profile 'planning only'", "--supervisor-model", "p/repair"]).Value!;
        Assert.Equal("p/planner", options.ContinuationModel);
        Assert.Equal("low", options.ContinuationEffort);
        Assert.Equal(TimeSpan.FromMinutes(90), options.EffectiveContinuationTimeout);
        Assert.Equal(["--profile", "planning only"], options.ContinuationExtraArguments);
        Assert.Null(options.SupervisorExtraArguments);
        Assert.Equal("p/repair", options.SupervisorModel);
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--continuation-model", "bad"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--continuation-timeout", "0m"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["run", "--model", "p/m", "--continuation-model", "p/c", "-a", "continuation", "/tmp/a"]));
    }

    [Fact]
    public void ProcessStateConsumesOnceUntilWorkOrExplicitRetryAndFreshRunsStartArmed()
    {
        var state = new ContinuationState();
        Assert.True(state.TryConsume());
        Assert.False(state.TryConsume());
        state.ObserveUnfinished();
        Assert.True(state.TryConsume());
        state.Rearm();
        Assert.True(state.TryConsume());
        Assert.True(new ContinuationState().TryConsume());
    }

    [Fact]
    public void ObsoleteOfflineRetryCommandIsRejected()
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["continuation", "retry"]));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("in_progress")]
    [InlineData("blocked")]
    [InlineData("deferred")]
    [InlineData("unknown-future-status")]
    public async Task EveryUnfinishedEpicPreventsLaunchRegardlessOfDispatchFilters(string status)
    {
        using var fixture = new Fixture();
        fixture.Epic(status);
        Assert.False(await fixture.Supervisor().CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(0, fixture.Host.Starts);
        Assert.Contains("list --type epic --all --limit 0 --json", File.ReadAllText(Path.Combine(fixture.Root, "calls")));
    }

    [Fact]
    public async Task EmptyBacklogRunsOncePerProcessAndCanBeExplicitlyRetried()
    {
        using var fixture = new Fixture();
        var first = fixture.Supervisor();
        Assert.True(await first.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.False(await first.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.True(await fixture.Supervisor().CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(2, fixture.Host.Starts);
        first.Request(AgentControlAction.Restart);
        Assert.True(await first.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(3, fixture.Host.Starts);
        Assert.Equal(3, fixture.Host.Stops);
        Assert.Empty(Directory.GetFiles(fixture.Root, "continuation-*.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForceRunBypassesAutomaticGatesAndAppendsToNormalPrompt(bool tracked)
    {
        using var fixture = new Fixture();
        fixture.Epic("open");
        fixture.Gate.SetEnabled(false);
        var supervisor = fixture.Supervisor();
        var receipt = tracked ? supervisor.ForceRunTracked("Review epic-1 specifically.") : null;
        if (!tracked) supervisor.ForceRun("Review epic-1 specifically.");
        if (receipt is not null) Assert.False(receipt.Completion.IsCompleted);
        Assert.True(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(1, fixture.Host.Starts);
        Assert.Contains("You are Abacus's optional continuation supervisor", fixture.Host.LastPrompt);
        Assert.EndsWith("Additional instructions for this operator-requested run:\nReview epic-1 specifically.", fixture.Host.LastPrompt);
        Assert.Contains("regardless of epic backlog", fixture.Host.LastPrompt);
        if (receipt is not null) Assert.Equal("completed", (await receipt.Completion.WaitAsync(fixture.Token)).Outcome);
    }

    [Fact]
    public async Task ObservedWorkRearmsContinuousContinuationAfterItCloses()
    {
        using var fixture = new Fixture();
        var supervisor = fixture.Supervisor();
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        fixture.Epic("blocked");
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        fixture.Epic("closed");
        Assert.True(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(2, fixture.Host.Starts);
    }

    [Fact]
    public async Task FiniteRunAllowsAtMostOneAutomaticPlanningAttempt()
    {
        using var fixture = new Fixture();
        var supervisor = fixture.Supervisor(ExecutionMode.Drain);
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        fixture.Epic("open");
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        fixture.Epic("closed");
        Assert.False(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(1, fixture.Host.Starts);
        Assert.True(fixture.State.IsArmed()); // Rearmed in memory, but the finite-run cap still applies.
    }

    [Fact]
    public async Task PausedClaimsDoNotConsumeTheEmptyBacklogTrigger()
    {
        using var fixture = new Fixture();
        fixture.Gate.SetEnabled(false);
        var supervisor = fixture.Supervisor();
        Assert.False(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.True(fixture.State.IsArmed());
        fixture.Gate.SetEnabled(true);
        Assert.True(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("timeout")]
    [InlineData("startup")]
    public async Task FailedAttemptsStaySuppressedWithinRunButFreshRunCanRetry(string failure)
    {
        using var fixture = new Fixture();
        fixture.Host.Failure = failure;
        var supervisor = fixture.Supervisor();
        Assert.True(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.False(await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(1, fixture.Host.Starts);
        Assert.False(fixture.State.IsArmed());
        Assert.True(await fixture.Supervisor().CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(2, fixture.Host.Starts);
    }

    [Fact]
    public async Task MalformedOrFailedEpicQueryNeverLaunchesOrConsumes()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.EpicsPath, "{not an array}");
        await Assert.ThrowsAsync<BeadsException>(() => fixture.Supervisor(ExecutionMode.Once)
            .CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.Equal(0, fixture.Host.Starts);
        Assert.True(fixture.State.IsArmed());
        File.Delete(fixture.EpicsPath);
        await Assert.ThrowsAsync<BeadsException>(() => fixture.Supervisor(ExecutionMode.Once)
            .CheckFiniteCompletionAsync("alice", fixture.Token));
    }

    [Fact]
    public async Task CleanupFailureIsFatalAndStopPreservesSuppression()
    {
        using var fixture = new Fixture();
        fixture.Host.FailCleanup = true;
        await Assert.ThrowsAsync<SupervisorCleanupException>(() => fixture.Supervisor()
            .CheckFiniteCompletionAsync("alice", fixture.Token));
        Assert.False(fixture.State.IsArmed());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorStopCancelsActivePlanningWithoutRearmingIt(bool tracked)
    {
        using var fixture = new Fixture();
        fixture.Host.Failure = "timeout";
        var supervisor = fixture.Supervisor();
        var check = supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        while (fixture.Host.Starts == 0) await Task.Delay(1, fixture.Token);
        var stop = tracked ? supervisor.RequestTracked(AgentControlAction.Stop) : null;
        if (!tracked) supervisor.Request(AgentControlAction.Stop);
        await check;
        Assert.Equal(1, fixture.Host.Stops);
        if (stop is not null) Assert.False(stop.Completion.IsCompleted);
        Assert.False(fixture.State.IsArmed());
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token); // Consume Stop; remain disabled.
        Assert.Equal(1, fixture.Host.Starts);
        if (stop is not null) Assert.Equal("completed", (await stop.Completion.WaitAsync(fixture.Token)).Outcome);
        fixture.Host.Failure = null;
        var restart = tracked ? supervisor.RequestTracked(AgentControlAction.Restart) : null;
        if (!tracked) supervisor.Request(AgentControlAction.Restart);
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        Assert.Equal(2, fixture.Host.Starts);
        if (restart is not null) Assert.Equal("completed", (await restart.Completion.WaitAsync(fixture.Token)).Outcome);
    }

    [Fact]
    public async Task SupervisorCleanupFailureNeverReleasesCheckoutToAnotherRole()
    {
        using var fixture = new Fixture();
        var registry = new AgentRunRegistry();
        var host = new SupervisorHost(fixture.Host, registry);
        var first = await host.StartAgentAsync(fixture.Agent with { Name = "maintenance" },
            new("one", IssueStatus.Open), "p/m", "high", null, fixture.Token);
        fixture.Host.FailCleanup = true;
        await Assert.ThrowsAsync<IOException>(() => host.StopAndCleanupAsync(first, fixture.Token));
        Assert.True(registry.IsRunning("maintenance"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAgentAsync(
            fixture.Agent with { Name = "continuation" }, new("two", IssueStatus.Open), "p/m", "high", null, deadline.Token));
        Assert.Equal(1, fixture.Host.Starts);
    }

    [Fact]
    public async Task SupervisorHostSerializesRolesAndKeepsTheirMergeSlotOwnershipLive()
    {
        using var fixture = new Fixture();
        var runs = new AgentRunRegistry();
        var waiting = new List<string>();
        var host = new SupervisorHost(fixture.Host, runs)
        {
            WaitingForCheckoutAsync = name => { waiting.Add(name); return Task.CompletedTask; },
        };
        var maintenance = fixture.Agent with { Name = MaintenanceSupervisor.Name };
        var continuation = fixture.Agent with { Name = ContinuationSupervisor.Name };
        var first = await host.StartAgentAsync(maintenance, new("one", IssueStatus.Open), "p/m", "high", null, fixture.Token);
        Assert.True(runs.IsRunning(maintenance.Name));
        var pending = host.StartAgentAsync(continuation, new("two", IssueStatus.Open), "p/m", "high", null, fixture.Token);
        Assert.False(pending.IsCompleted);
        Assert.False(runs.IsRunning(continuation.Name));
        Assert.Equal([continuation.Name], waiting);
        await host.StopAndCleanupAsync(first, fixture.Token);
        var second = await pending;
        Assert.False(runs.IsRunning(maintenance.Name));
        Assert.True(runs.IsRunning(continuation.Name));
        await host.StopAndCleanupAsync(second, fixture.Token);
        Assert.False(runs.IsRunning(continuation.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Use our reviewed merge script.")]
    public void ContinuationUsesEffectiveMergePolicyWithoutMaintenancePolicyLeakage(string? merge)
    {
        var targets = new TargetRegistry(new Dictionary<string, TargetPolicy>
            { ["release"] = new("release", merge, "id") }, defaultTarget: "release");
        var prompt = ContinuationSupervisor.RenderPrompt("run", "/tmp/done", targets, "Write specs and create epics.");
        Assert.Contains("Write specs and create epics.", prompt);
        Assert.Contains("Destination: refs/heads/release", prompt);
        Assert.DoesNotContain(".abacus/supervisor.md", prompt);
        Assert.DoesNotContain("Failed agents are parked", prompt);
        if (merge is null) Assert.Contains("merge --ff-only", prompt);
        else Assert.DoesNotContain("merge --ff-only", prompt);
    }

    [Fact]
    public async Task PromptFilesAreIndependentOrderedAndConfigRelative()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".abacus"));
        File.WriteAllText(Path.Combine(fixture.Root, ".abacus", "continuation.md"), "first");
        File.WriteAllText(Path.Combine(fixture.Root, ".abacus", "supervisor.md"), "never include");
        var custom = Path.Combine(fixture.Root, "custom.md");
        File.WriteAllText(custom, "second");
        Assert.Equal("first\n\nsecond", await ContinuationSupervisor.ReadAdditivePromptAsync(fixture.Root, custom, fixture.Token));
        var path = Path.Combine(fixture.Root, "base.json");
        File.WriteAllText(path, """{"version":1,"model":"p/m","continuationModel":"p/c","continuationPromptFile":"custom.md"}""");
        var derived = Path.Combine(fixture.Root, "child.json");
        File.WriteAllText(derived, """{"version":1,"baseConfig":"base.json","continuationExtraArgs":"--profile plan"}""");
        var options = Options.Parse(["preflight", "--config", derived]).Value!;
        Assert.Equal(custom, options.ContinuationPromptFile);
        Assert.Null(options.SupervisorModel);
        Assert.Equal("p/c", options.ContinuationModel);
        Assert.Equal("../custom.md", RunConfiguration.Load(path).DocumentFor(Path.Combine(fixture.Root, "sub", "new.json"))["continuationPromptFile"]!.GetValue<string>());
        File.Delete(custom);
        await Assert.ThrowsAsync<PreflightException>(() => ContinuationSupervisor.ReadAdditivePromptAsync(fixture.Root, custom, fixture.Token));
    }

    [Fact]
    public async Task TrackedStopIsUnknownWhenActivePlanningCleanupFails()
    {
        using var fixture = new Fixture();
        fixture.Host.Failure = "timeout";
        fixture.Host.FailCleanup = true;
        var supervisor = fixture.Supervisor();
        var monitor = supervisor.RunAsync(() => false, fixture.Token);
        while (fixture.Host.Starts == 0) await Task.Delay(1, fixture.Token);
        var stop = supervisor.RequestTracked(AgentControlAction.Stop);
        await Assert.ThrowsAsync<SupervisorCleanupException>(() => monitor);
        Assert.Equal("outcome-unknown", (await stop.Completion.WaitAsync(fixture.Token)).Outcome);
    }

    [Fact]
    public async Task QueuedForceIsCancelledByStopOrUnknownOnLoopExit()
    {
        using var fixture = new Fixture();
        var supervisor = fixture.Supervisor();
        var cancelled = supervisor.ForceRunTracked("Pending prompt");
        Assert.Throws<InvalidOperationException>(() => supervisor.ForceRunTracked("Replacement"));
        supervisor.Request(AgentControlAction.Stop);
        await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        Assert.Equal("cancelled", (await cancelled.Completion.WaitAsync(fixture.Token)).Outcome);
        Assert.Equal(0, fixture.Host.Starts);
        var abandoned = supervisor.ForceRunTracked("New request");
        await supervisor.RunAsync(() => true, fixture.Token);
        Assert.Equal("outcome-unknown", (await abandoned.Completion.WaitAsync(fixture.Token)).Outcome);
        Assert.Throws<InvalidOperationException>(() => supervisor.ForceRun("Too late"));
    }

    [Theory]
    [InlineData(false, "failed")]
    [InlineData(true, "outcome-unknown")]
    public async Task ForceReceiptDoesNotClaimSuccessForCrashOrFailedCleanup(bool cleanupFailure, string expected)
    {
        using var fixture = new Fixture();
        fixture.Host.Failure = "crash";
        fixture.Host.FailCleanup = cleanupFailure;
        var supervisor = fixture.Supervisor();
        var receipt = supervisor.ForceRunTracked("Check failure");
        if (cleanupFailure)
            await Assert.ThrowsAsync<SupervisorCleanupException>(() => supervisor.CheckFiniteCompletionAsync("alice", fixture.Token));
        else await supervisor.CheckFiniteCompletionAsync("alice", fixture.Token);
        Assert.Equal(expected, (await receipt.Completion.WaitAsync(fixture.Token)).Outcome);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        public string Root { get; } = Directory.CreateTempSubdirectory("abacus-continuation-test-").FullName;
        public string EpicsPath => Path.Combine(Root, "epics.json");
        public ContinuationState State { get; private set; } = new();
        public CancellationToken Token => cancellation.Token;
        public ClaimGate Gate { get; } = new();
        public TestHost Host { get; }
        public ValidatedAgent Agent { get; } = new("alice", "/tmp/worker", new(true, "db", null, null, true), false);
        private readonly Beads beads;
        public Fixture()
        {
            File.WriteAllText(EpicsPath, "[]");
            var bd = Path.Combine(Root, "bd");
            File.WriteAllText(bd, $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{Root}/calls'\ncat '{EpicsPath}'\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(bd, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            beads = new(new CommandRunner(TextWriter.Null), bd);
            Host = new(Root);
        }
        public List<SoundClip> Clips { get; } = [];
        public ContinuationSupervisor Supervisor(ExecutionMode mode = ExecutionMode.Continuous, bool audio = false, TextWriter? log = null, bool failAudio = false) => new(
            new(new Options(null, "p/w", null, [new("alice", Agent.WorkspacePath)], ExecutionMode: mode,
                ContinuationModel: "p/c", ContinuationTimeout: TimeSpan.FromMilliseconds(80), TuiAudio: audio),
                [Agent], new("bd", "git", "opencode", null), null, Root),
            beads, Host, log ?? TextWriter.Null, Root, State = new(), Gate, TimeSpan.FromMilliseconds(5))
            { StartSound = clip => { Clips.Add(clip); if (failAudio) throw new IOException("no audio player"); return Task.CompletedTask; } };
        public void Epic(string status) => File.WriteAllText(EpicsPath, JsonSerializer.Serialize(new[] { new { id = "epic-1", status } }));
        public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); Directory.Delete(Root, true); }
    }

    private sealed class TestHost(string root) : IAgentHost
    {
        public int Starts;
        public int Stops;
        public string? LastPrompt;
        public string? Failure;
        public bool FailCleanup;
        public Task<IAgentRun> StartAgentAsync(ValidatedAgent agent, BeadsIssue issue, string model, string effort,
            string? serverUrl, CancellationToken token, IReadOnlyList<string>? extraArguments = null)
        {
            Starts++;
            LastPrompt = agent.HarnessPromptOverride;
            if (Failure == "startup") throw new IOException("startup failure");
            if (Failure is null)
                File.WriteAllText(Path.Combine(root, $"continuation-{issue.Id}.json"), JsonSerializer.Serialize(new { runId = issue.Id, summary = "no-op" }));
            return Task.FromResult<IAgentRun>(new Run(Failure == "crash"));
        }
        public Task<bool> IsRunningAsync(IAgentRun run, CancellationToken token) => Task.FromResult(!run.HasExited);
        public Task StopAndCleanupAsync(IAgentRun run, CancellationToken token)
        {
            Stops++;
            if (FailCleanup) throw new IOException("still running");
            return Task.CompletedTask;
        }
        private sealed record Run(bool HasExited) : IAgentRun
        {
            public string Location => "fake";
            public int? TryReadExitCode() => HasExited ? 1 : null;
        }
    }
}
