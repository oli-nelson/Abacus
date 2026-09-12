using System.Text.Json;
using System.Text.Json.Nodes;
using Abacus;

namespace Abacus.Tests;

public sealed class TargetRoutingTests
{
    [Theory]
    [InlineData("main", true)]
    [InlineData("release/1.2", true)]
    [InlineData("123", true)]
    [InlineData("HEAD", false)]
    [InlineData("origin/main~1", false)]
    [InlineData("refs/heads/main", false)]
    [InlineData("abacus/ticket", false)]
    [InlineData("release//1", false)]
    [InlineData("release/.secret", false)]
    [InlineData("release/x.lock", false)]
    [InlineData("-main", false)]
    public void OnlyLiteralTargetBranchesAreAccepted(string branch, bool valid) =>
        Assert.Equal(valid, Git.IsValidTargetBranch(branch));

    [Fact]
    public void ParsesStandaloneCommandsAndRunFilters()
    {
        var check = Options.Parse(["targets", "check", "abc-1", "abc-2", "--repo", "/tmp/repo"]).TargetCommand!;
        Assert.True(check.Check);
        Assert.Equal(["abc-1", "abc-2"], check.IssueIds);
        var set = Options.Parse(["targets", "set", "release/1.2", "abc-1"]).TargetCommand!;
        Assert.False(set.Check);
        Assert.Equal("release/1.2", set.Target);
        var run = Options.Parse(["run", "--model", "p/m", "--tmux-session", "x", "-a", "a", "/tmp/a",
            "--target-filter", "main", "--target-filter", "release/1.2", "--repo", "/tmp/repo"]).Value!;
        Assert.Equal(["main", "release/1.2"], run.TargetBranches);
        Assert.Throws<OptionsException>(() => Options.Parse(["targets", "set", "main"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["targets", "check", "--once"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["targets", "check", "--target-filter", "main"]));
    }

    [Theory]
    [InlineData("{\"version\":2,\"targets\":{\"main\":{}}}")]
    [InlineData("{\"version\":1,\"defaultTarget\":\"missing\",\"targets\":{\"main\":{}}}")]
    [InlineData("{\"version\":1,\"targets\":{\"main\":{},\"main\":{}}}")]
    [InlineData("{\"version\":1,\"targets\":{}}")]
    [InlineData("{\"version\":1,\"targets\":{\"main\":{\"mergeInstructions\":\"missing.md\"}}}")]
    public async Task ConfigurationFailsClosed(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            await Assert.ThrowsAsync<TargetException>(() => TargetRegistry.LoadAsync(path, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"enforceTargetBranch\":\"false\",")]
    [InlineData("\"enforceTargetBranch\":null,")]
    [InlineData("\"defaultTarget\":null,")]
    [InlineData("\"defaultTarget\":\"HEAD\",")]
    [InlineData("\"defaultTarget\":\"missing\",")]
    [InlineData("\"enforceTargetBranch\":false,\"enforceTargetBranch\":true,")]
    public async Task InvalidDefaultPolicyFailsConfiguration(string property)
    {
        using var f = await RoutingFixture.CreateAsync();
        await File.WriteAllTextAsync(f.ConfigPath, "{\"version\":1," + property + "\"targets\":{\"main\":{}}}");
        await Assert.ThrowsAsync<TargetException>(() => TargetRegistry.LoadAsync(f.ConfigPath, CancellationToken.None));
    }

    [Fact]
    public async Task OmittedSettingsDefaultToUnenforcedMain()
    {
        using var f = await RoutingFixture.CreateAsync();
        await File.WriteAllTextAsync(f.ConfigPath, """{"version":1,"targets":{"main":{}}}""");
        var registry = await TargetRegistry.LoadAsync(f.ConfigPath, CancellationToken.None);
        Assert.False(registry.EnforceTargetBranch);
        Assert.Equal("main", registry.DefaultTarget);
        Assert.Equal("main", registry.Validate(new("abc-1", IssueStatus.Open)).Branch);
    }

    [Fact]
    public async Task MissingTargetUsesDefaultForAuditFilteringBindingAndBranchWithoutStampingMetadata()
    {
        using var f = await RoutingFixture.CreateAsync(false, "release/1.2");
        var releaseTip = (await f.RunGitAsync("rev-parse", "release/1.2")).Trim();
        await f.RunGitAsync("commit", "--allow-empty", "-qm", "main only");
        await f.AddIssueAsync("abc-1", null);
        var output = new StringWriter();
        Assert.Equal(0, await new TicketTargets(f.Beads, f.Git).RunAsync(f.Workspace,
            new(true, null, ["abc-1"], f.Workspace), output, CancellationToken.None));
        Assert.Contains("release/1.2 (default)", output.ToString());
        Assert.Null(f.ReadIssue("abc-1")["metadata"]!["abacus_execution"]);
        Assert.Null(await f.ClaimAsync(["main"]));
        var claim = await f.ClaimAsync(["release/1.2"]);
        Assert.NotNull(claim);
        Assert.Null(claim.Issue.TargetBranch);
        Assert.Equal("refs/heads/release/1.2", claim.Issue.Binding!.TargetRef);
        Assert.Equal(releaseTip, (await f.RunGitAsync("rev-parse", "HEAD")).Trim());
        Assert.Null(f.ReadIssue("abc-1")["metadata"]!["abacus_target"]);
        Assert.Equal("in_progress", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    [InlineData("\"unknown\"")]
    public async Task ExplicitInvalidTargetsAreNotDefaulted(string value)
    {
        using var f = await RoutingFixture.CreateAsync(false);
        await f.AddIssueAsync("abc-1", null);
        f.EditIssue("abc-1", issue => issue["metadata"]!["abacus_target"] = JsonNode.Parse(value));
        Assert.Null(await f.ClaimAsync());
        Assert.Equal("blocked", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
        Assert.Contains(Beads.NeedsUserAttentionLabel, f.ReadIssue("abc-1")["labels"]!.ToJsonString());
    }

    [Fact]
    public async Task ExplicitTargetOverridesDefaultAndDefaultChangesCannotRetargetBindings()
    {
        using var f = await RoutingFixture.CreateAsync(false);
        await f.AddIssueAsync("abc-1", "release/1.2");
        var claim = await f.ClaimAsync(["release/1.2"]);
        Assert.NotNull(claim);
        Assert.Equal("refs/heads/release/1.2", claim.Issue.Binding!.TargetRef);
        // Removing the explicit target cannot silently send this bound release ticket to main.
        f.EditIssue("abc-1", issue => issue["metadata"]!.AsObject().Remove("abacus_target"));
        var reread = await f.Beads.GetIssueAsync(f.Workspace, "alice", "abc-1", CancellationToken.None);
        Assert.Throws<TargetException>(() => f.Agent().Targets!.Validate(reread!));
        // The same applies when an implicitly bound ticket's controller default changes.
        var boundDefault = claim.Issue with { TargetBranch = null };
        var releaseDefault = new TargetRegistry(f.Agent().Targets!.Targets, defaultTarget: "release/1.2");
        releaseDefault.Validate(boundDefault);
        Assert.Throws<TargetException>(() => f.Agent().Targets!.Validate(boundDefault));
        var strict = new TargetRegistry(f.Agent().Targets!.Targets, enforceTargetBranch: true, defaultTarget: "release/1.2");
        Assert.Throws<TargetException>(() => strict.Validate(boundDefault));
    }

    [Fact]
    public async Task ConfigSnapshotUsesControllerInstructionsAndDetectsPolicyChanges()
    {
        using var f = await RoutingFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(f.ConfigDirectory, "merge-instructions.md"), "  custom merge  ");
        var first = await TargetRegistry.LoadAsync(f.ConfigPath, CancellationToken.None);
        Assert.Equal("custom merge", first.Targets["main"].MergeInstructions);
        await File.WriteAllTextAsync(Path.Combine(f.ConfigDirectory, "merge-instructions.md"), "changed");
        var second = await TargetRegistry.LoadAsync(f.ConfigPath, CancellationToken.None);
        Assert.Equal("custom merge", first.Targets["main"].MergeInstructions);
        Assert.NotEqual(first.Targets["main"].Identity, second.Targets["main"].Identity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown/target")]
    public async Task InvalidTicketIsBlockedWithAttentionAndReasonWithoutChangingWorkspace(string? target)
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", target);
        var before = await f.RunGitAsync("rev-parse", "HEAD");
        var claim = await f.ClaimAsync();
        Assert.Null(claim);
        var issue = f.ReadIssue("abc-1");
        Assert.Equal("blocked", issue["status"]!.GetValue<string>());
        Assert.Contains(Beads.NeedsUserAttentionLabel, issue["labels"]!.ToJsonString());
        Assert.Contains("abacus_target", issue["notes"]!.GetValue<string>());
        Assert.Contains("targets set", issue["notes"]!.GetValue<string>());
        Assert.Equal(before, await f.RunGitAsync("rev-parse", "HEAD"));
        Assert.Equal("main", (await f.RunGitAsync("branch", "--show-current")).Trim());
        Assert.False(await f.Git.IssueBranchExistsAsync(f.Workspace, "alice", "abc-1", CancellationToken.None));
    }

    [Theory]
    [InlineData(ExecutionMode.Once, false)]
    [InlineData(ExecutionMode.Drain, true)]
    public async Task QuarantineRespectsFiniteClaimBudget(ExecutionMode mode, bool continues)
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-invalid", null, priority: 0);
        await f.AddIssueAsync("abc-valid", "main", priority: 4);
        var claim = await f.ClaimAsync(mode: mode);
        Assert.Equal(continues, claim is not null);
        Assert.Equal("blocked", f.ReadIssue("abc-invalid")["status"]!.GetValue<string>());
        Assert.Equal(continues ? "in_progress" : "open", f.ReadIssue("abc-valid")["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task BranchStartsAtTicketTargetNotIncidentalHeadAndBindingPrecedesSwitch()
    {
        using var f = await RoutingFixture.CreateAsync();
        var releaseTip = (await f.RunGitAsync("rev-parse", "release/1.2")).Trim();
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "main-only.txt"), "not a release change");
        await f.RunGitAsync("add", ".");
        await f.RunGitAsync("commit", "-qm", "main only");
        await f.AddIssueAsync("abc-1", "release/1.2");
        var claim = await f.ClaimAsync();
        Assert.NotNull(claim);
        Assert.Equal(releaseTip, claim.Issue.Binding!.StartCommit);
        Assert.Equal("refs/heads/release/1.2", claim.Issue.Binding.TargetRef);
        Assert.Equal(releaseTip, (await f.RunGitAsync("rev-parse", "HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(f.Workspace, "main-only.txt")));
        Assert.NotNull(f.ReadIssue("abc-1")["metadata"]!["abacus_execution"]);
        Assert.Null(f.ReadIssue("abc-1")["metadata"]!["abacus_execution"]!["repository"]);
        var prompt = Prompt.Render("alice", "abc-1", f.Workspace, targetBranch: claim.Issue.TargetBranch!);
        Assert.Contains("refs/heads/release/1.2", prompt);
        Assert.DoesNotContain("local main branch", prompt);
    }

    [Fact]
    public async Task LegacyRepositoryBindingFieldIsIgnoredOnReadAndValidation()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        var claim = await f.ClaimAsync();
        Assert.NotNull(claim);
        f.EditIssue("abc-1", issue => issue["metadata"]!["abacus_execution"]!["repository"] = "old-repository-id");
        var reread = await f.Beads.GetIssueAsync(f.Workspace, "alice", "abc-1", CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Null(reread.MetadataError);
        Assert.Equal(claim.Issue.Binding, reread.Binding);
        f.Agent().Targets!.Validate(reread);
    }

    [Fact]
    public async Task RoutingFiltersBeforePrioritySelectionWithoutChangingOtherTickets()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-main", "main", priority: 0);
        await f.AddIssueAsync("abc-release", "release/1.2", priority: 4);
        var claim = await f.ClaimAsync(["release/1.2"]);
        Assert.Equal("abc-release", claim!.Issue.Id);
        Assert.Equal("open", f.ReadIssue("abc-main")["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task TargetChangeDuringClaimIsBlockedBeforeGitMutation()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "change-target-on-claim"), "release/1.2");
        Assert.Null(await f.ClaimAsync());
        Assert.Contains("changed between selection", f.ReadIssue("abc-1")["notes"]!.GetValue<string>());
        Assert.False(await f.Git.IssueBranchExistsAsync(f.Workspace, "alice", "abc-1", CancellationToken.None));
    }

    [Fact]
    public async Task DirtyBoundBranchResumesIgnoringPoolFilterButNotBindingMismatch()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "release/1.2");
        var claim = await f.ClaimAsync();
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "work.txt"), "preserve this");
        f.EditIssue("abc-1", i => { i["status"] = "open"; i["assignee"] = ""; });
        var resumed = await f.ClaimAsync(["main"]);
        Assert.Equal(claim!.Issue.Binding, resumed!.Issue.Binding);
        Assert.Equal("preserve this", await File.ReadAllTextAsync(Path.Combine(f.Workspace, "work.txt")));
        f.EditIssue("abc-1", i => { i["status"] = "open"; i["assignee"] = ""; i["metadata"]!["abacus_target"] = "main"; });
        await Assert.ThrowsAsync<AgentHaltedException>(() => f.ClaimAsync());
        Assert.Equal("blocked", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
        Assert.Equal("preserve this", await File.ReadAllTextAsync(Path.Combine(f.Workspace, "work.txt")));
    }

    [Fact]
    public async Task LegacyBranchIsNotAutomaticallyAdoptedOrReset()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        await f.RunGitAsync("branch", "abacus/abc-1");
        Assert.Null(await f.ClaimAsync());
        Assert.Contains("operator adoption", f.ReadIssue("abc-1")["notes"]!.GetValue<string>());
        Assert.Equal("main", (await f.RunGitAsync("branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task ExplicitAdoptionPreservesDirtyFilesAndAllowsRecovery()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", null);
        await f.RunGitAsync("switch", "-c", "abacus/abc-1", "release/1.2");
        var start = (await f.RunGitAsync("rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "work.txt"), "unfinished work");
        var command = Options.Parse(["targets", "set", "release/1.2", "abc-1", "--repo", f.Workspace,
            "--adopt-existing-branch", "--start-commit", start]).TargetCommand!;
        Assert.Equal(0, await new TicketTargets(f.Beads, f.Git)
            .RunAsync(f.Workspace, command, TextWriter.Null, CancellationToken.None));
        var claim = await f.ClaimAsync();
        Assert.Equal(start, claim!.Issue.Binding!.StartCommit);
        Assert.Equal("unfinished work", await File.ReadAllTextAsync(Path.Combine(f.Workspace, "work.txt")));
    }

    [Fact]
    public async Task BindingReadbackFailureBlocksWithoutCreatingBranch()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "drop-binding-write"), "");
        Assert.Null(await f.ClaimAsync());
        Assert.Equal("blocked", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
        Assert.False(await f.Git.IssueBranchExistsAsync(f.Workspace, "alice", "abc-1", CancellationToken.None));
    }

    [Fact]
    public async Task IncompleteQuarantineHaltsRatherThanClaimingMoreWork()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", null);
        await f.AddIssueAsync("abc-2", "main", priority: 4);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "drop-attention-label"), "");
        await Assert.ThrowsAsync<AgentHaltedException>(() => f.ClaimAsync());
        Assert.Equal("open", f.ReadIssue("abc-2")["status"]!.GetValue<string>());
        Assert.False(await f.Git.IssueBranchExistsAsync(f.Workspace, "alice", "abc-2", CancellationToken.None));
    }

    [Fact]
    public async Task BindingWithoutBranchResumesFromRecordedCommitNotNewTargetTip()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        var original = await f.ClaimAsync();
        await f.RunGitAsync("switch", "main");
        await f.RunGitAsync("branch", "-D", "abacus/abc-1");
        await f.RunGitAsync("commit", "--allow-empty", "-qm", "target advanced");
        f.EditIssue("abc-1", i => { i["status"] = "open"; i["assignee"] = ""; });
        var resumed = await f.ClaimAsync();
        Assert.Equal(original!.Issue.Binding, resumed!.Issue.Binding);
        Assert.Equal(original.Issue.Binding!.StartCommit, (await f.RunGitAsync("rev-parse", "HEAD")).Trim());
    }

    [Fact]
    public async Task MissingTargetInDirtyRecoveryIsBlockedAndFilesArePreserved()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", null);
        await f.RunGitAsync("switch", "-c", "abacus/abc-1");
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "work.txt"), "keep me");
        await Assert.ThrowsAsync<AgentHaltedException>(() => f.ClaimAsync());
        Assert.Equal("blocked", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(f.Workspace, "work.txt")));
    }

    [Fact]
    public async Task AuditIsReadOnlyAndSetterPreservesMetadataAndBlockedState()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", null);
        f.EditIssue("abc-1", i => { i["status"] = "blocked"; i["metadata"]!["team"] = "framework"; });
        var commands = new TicketTargets(f.Beads, f.Git);
        var before = await File.ReadAllTextAsync(f.StatePath);
        Assert.Equal(1, await commands.RunAsync(f.Workspace, new(true, null, [], f.Workspace), TextWriter.Null, CancellationToken.None));
        Assert.Equal(before, await File.ReadAllTextAsync(f.StatePath));
        Assert.Equal(0, await commands.RunAsync(f.Workspace, new(false, "release/1.2", ["abc-1"], f.Workspace), TextWriter.Null, CancellationToken.None));
        var issue = f.ReadIssue("abc-1");
        Assert.Equal("framework", issue["metadata"]!["team"]!.GetValue<string>());
        Assert.Equal("blocked", issue["status"]!.GetValue<string>());
        Assert.Equal(0, await commands.RunAsync(f.Workspace, new(true, null, [], f.Workspace), TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public async Task NumericBranchMetadataRemainsAStringEvenOnClosedHistory()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.RunGitAsync("branch", "123");
        await File.WriteAllTextAsync(f.ConfigPath, """{"version":1,"defaultTarget":"123","targets":{"123":{}}}""");
        await f.AddIssueAsync("abc-1", null);
        f.EditIssue("abc-1", i => i["status"] = "closed");
        await new TicketTargets(f.Beads, f.Git).RunAsync(f.Workspace,
            new(false, "123", ["abc-1"], f.Workspace), TextWriter.Null, CancellationToken.None);
        Assert.Equal("123", f.ReadIssue("abc-1")["metadata"]!["abacus_target"]!.GetValue<string>());
        Assert.Equal("closed", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task NonStringTargetIsQuarantinedRatherThanCrashingReadySelection()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        f.EditIssue("abc-1", i => i["metadata"]!["abacus_target"] = 123);
        Assert.Null(await f.ClaimAsync());
        Assert.Equal("blocked", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
        Assert.Contains(Beads.NeedsUserAttentionLabel, f.ReadIssue("abc-1")["labels"]!.ToJsonString());
        Assert.Equal(0, await new TicketTargets(f.Beads, f.Git).RunAsync(f.Workspace,
            new(false, "main", ["abc-1"], f.Workspace), TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public async Task SetterCanRestoreCorruptedDestinationWithoutRetargetingBinding()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "release/1.2");
        var claim = await f.ClaimAsync();
        f.EditIssue("abc-1", i => { i["status"] = "blocked"; i["metadata"]!["abacus_target"] = "main"; });
        await new TicketTargets(f.Beads, f.Git).RunAsync(f.Workspace,
            new(false, "release/1.2", ["abc-1"], f.Workspace), TextWriter.Null, CancellationToken.None);
        var corrected = await f.Beads.GetIssueAsync(f.Workspace, "alice", "abc-1", CancellationToken.None);
        Assert.Equal(claim!.Issue.Binding, corrected!.Binding);
        Assert.Equal("release/1.2", corrected.TargetBranch);
    }

    [Fact]
    public async Task SetterRejectsActiveAndBoundRetargetingBeforeBatchWrites()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        await f.ClaimAsync();
        await f.AddIssueAsync("abc-2", null);
        var commands = new TicketTargets(f.Beads, f.Git);
        await Assert.ThrowsAsync<TargetException>(() => commands.RunAsync(f.Workspace,
            new(false, "release/1.2", ["abc-2", "abc-1"], f.Workspace), TextWriter.Null, CancellationToken.None));
        Assert.Null(f.ReadIssue("abc-2")["metadata"]!["abacus_target"]);
        f.EditIssue("abc-1", i => i["status"] = "open");
        await Assert.ThrowsAsync<TargetException>(() => commands.RunAsync(f.Workspace,
            new(false, "release/1.2", ["abc-1"], f.Workspace), TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceOwnershipIsExclusiveAndReleasedOnDispose()
    {
        using var f = await RoutingFixture.CreateAsync();
        using (var ownership = await WorkspaceOwnership.AcquireAsync(f.Git, [f.Agent()], CancellationToken.None))
            await Assert.ThrowsAsync<StartupInvariantException>(() => WorkspaceOwnership.AcquireAsync(f.Git, [f.Agent()], CancellationToken.None));
        using var second = await WorkspaceOwnership.AcquireAsync(f.Git, [f.Agent()], CancellationToken.None);
        Assert.Equal("", await f.RunGitAsync("status", "--porcelain"));
    }

    [Theory]
    [InlineData(null, null, "default-model")]
    [InlineData(ReasoningPolicy.HighLabel, null, "default-model")]
    [InlineData(ReasoningPolicy.HighLabel, "high-model", "high-model")]
    public async Task OptionalReasoningRoutingUsesMappingOrDefault(
        string? label,
        string? mappedModel,
        string expectedModel)
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main", labels: label is null ? [] : [label]);
        var mappings = mappedModel is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [ReasoningPolicy.HighLabel] = mappedModel };
        var claim = await f.ClaimAsync(
            reasoning: new ReasoningPolicy(), mappings: mappings);
        Assert.Equal(expectedModel, claim!.Model);
    }

    [Fact]
    public async Task MultipleReasoningLabelsAreBlockedBeforeGitMutation()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main", labels:
            [ReasoningPolicy.HighLabel, ReasoningPolicy.LowLabel]);
        var before = await f.RunGitAsync("rev-parse", "HEAD");
        Assert.Null(await f.ClaimAsync(reasoning: new ReasoningPolicy()));
        var issue = f.ReadIssue("abc-1");
        Assert.Equal("blocked", issue["status"]!.GetValue<string>());
        Assert.Contains(Beads.NeedsUserAttentionLabel, issue["labels"]!.ToJsonString());
        Assert.Contains("multiple reasoning labels", issue["notes"]!.GetValue<string>());
        Assert.Equal(before, await f.RunGitAsync("rev-parse", "HEAD"));
        Assert.False(await f.Git.IssueBranchExistsAsync(
            f.Workspace, "alice", "abc-1", CancellationToken.None));
    }

    [Fact]
    public async Task ReasoningRoutingCarriesPerTierEffortIntoPreparedClaim()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main", labels: [ReasoningPolicy.LowLabel]);
        var claim = await f.ClaimAsync(
            reasoning: new ReasoningPolicy(),
            mappings: new Dictionary<string, string> { [ReasoningPolicy.LowLabel] = "low-model" },
            effortMappings: new Dictionary<string, string> { [ReasoningPolicy.LowLabel] = "low" },
            defaultEffort: "xhigh");

        Assert.Equal("low-model", claim!.Model);
        Assert.Equal("low", claim.Effort);
    }

    [Fact]
    public async Task ReasoningRoutingCarriesPerTierExtraArgumentsIntoPreparedClaim()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main", labels: [ReasoningPolicy.LowLabel]);
        var claim = await f.ClaimAsync(
            reasoning: new ReasoningPolicy(),
            argumentMappings: new Dictionary<string, IReadOnlyList<string>>
            {
                [ReasoningPolicy.HighLabel] = ["-p", "deepseek"],
            },
            defaultArguments: ["-p", "openai"]);

        Assert.Equal(new[] { "-p", "openai" }, claim!.ExtraArguments);
    }

    [Fact]
    public async Task ReasoningRoutingSendsTheMappedTierArgumentsForALabelledTicket()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main", labels: [ReasoningPolicy.HighLabel]);
        var claim = await f.ClaimAsync(
            reasoning: new ReasoningPolicy(),
            argumentMappings: new Dictionary<string, IReadOnlyList<string>>
            {
                [ReasoningPolicy.HighLabel] = ["-p", "deepseek"],
            },
            defaultArguments: ["-p", "openai"]);

        Assert.Equal(new[] { "-p", "deepseek" }, claim!.ExtraArguments);
    }

    [Fact]
    public async Task EnforcedReasoningLabelIsRequired()
    {
        using var f = await RoutingFixture.CreateAsync();
        await f.AddIssueAsync("abc-1", "main");
        var mappings = ReasoningPolicy.Labels.ToDictionary(
            static label => label,
            static label => label + "-model",
            StringComparer.Ordinal);
        Assert.Null(await f.ClaimAsync(
            reasoning: new ReasoningPolicy(enforceLabels: true), mappings: mappings));
        Assert.Equal("blocked", f.ReadIssue("abc-1")["status"]!.GetValue<string>());
        Assert.Contains("requires exactly one reasoning label",
            f.ReadIssue("abc-1")["notes"]!.GetValue<string>());
    }

    private sealed class RoutingFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("abacus-routing-").FullName;
        public string Workspace => Path.Combine(Root, "repo");
        public string StatePath => Path.Combine(Root, "issues.json");
        public string ConfigDirectory => Path.Combine(Workspace, ".abacus");
        public string ConfigPath => Path.Combine(ConfigDirectory, "targets.json");
        public Git Git { get; } = new(new CommandRunner(TextWriter.Null));
        public Beads Beads { get; private set; } = null!;
        private TargetRegistry registry = null!;

        public static async Task<RoutingFixture> CreateAsync(bool enforce = true, string defaultTarget = "main")
        {
            var f = new RoutingFixture();
            Directory.CreateDirectory(f.Workspace);
            Directory.CreateDirectory(f.ConfigDirectory);
            await f.RunGitAsync("init", "-q", "--initial-branch=main");
            await f.RunGitAsync("-c", "user.name=Abacus Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-qm", "initial");
            await f.RunGitAsync("config", "user.name", "Abacus Test");
            await f.RunGitAsync("config", "user.email", "test@example.invalid");
            await f.RunGitAsync("branch", "release/1.2");
            await File.AppendAllTextAsync(Path.Combine(f.Workspace, ".git", "info", "exclude"), "\n.abacus/\n");
            await File.WriteAllTextAsync(f.StatePath, "{}");
            await File.WriteAllTextAsync(f.ConfigPath, JsonSerializer.Serialize(new { version = 1, enforceTargetBranch = enforce, defaultTarget,
                targets = new Dictionary<string, object> { ["main"] = new { }, ["release/1.2"] = new { } } }));
            f.registry = await TargetRegistry.LoadAsync(f.ConfigPath, CancellationToken.None);
            var script = Path.Combine(f.Root, "bd");
            await File.WriteAllTextAsync(script, """
                #!/usr/bin/env python3
                import sys, os, json, fcntl
                root = os.path.dirname(os.path.abspath(__file__))
                args = sys.argv[1:]
                with open(root + '/db.lock', 'w') as lock:
                    fcntl.flock(lock, fcntl.LOCK_EX)
                    with open(root + '/issues.json') as source: issues = json.load(source)
                    actor = os.environ.get('BEADS_ACTOR', 'abacus')
                    def val(flag): return args[args.index(flag)+1]
                    result = []
                    if args[0] == 'ready':
                        assignee = val('--assignee') if '--assignee' in args else ''
                        result = sorted([i for i in issues.values() if i['status']=='open' and i.get('assignee','')==assignee], key=lambda i:i['priority'])
                    elif args[0] == 'list': result = list(issues.values())
                    elif args[0] == 'show':
                        result = { 'schema_version':1, args[1]:[] } if '--children' in args else ([issues[args[1]]] if args[1] in issues else [])
                    elif args[0] == 'update':
                        issue = issues[args[1]]
                        if '--claim' in args:
                            if issue.get('assignee','') not in ['', actor] or issue['status'] not in ['open','in_progress']:
                                print('issue not claimable', file=sys.stderr); sys.exit(1)
                            issue['status']='in_progress'; issue['assignee']=actor
                            if os.path.exists(root+'/change-target-on-claim'):
                                with open(root+'/change-target-on-claim') as change: issue['metadata']['abacus_target']=change.read()
                        if '--metadata' in args:
                            patch=json.loads(val('--metadata'))
                            if os.path.exists(root+'/drop-binding-write'): patch.pop('abacus_execution',None)
                            issue.setdefault('metadata',{}).update(patch)
                        if '--status' in args: issue['status']=val('--status')
                        if '--assignee' in args: issue['assignee']=val('--assignee')
                        if '--add-label' in args and not os.path.exists(root+'/drop-attention-label'):
                            issue.setdefault('labels',[]).append(val('--add-label'))
                        if '--append-notes' in args: issue['notes']=issue.get('notes','')+'\n'+val('--append-notes')
                        with open(root+'/issues.json','w') as dest: json.dump(issues,dest)
                        result=[issue]
                    else: print('unsupported command', args, file=sys.stderr); sys.exit(2)
                    print(json.dumps(result))
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            f.Beads = new(new CommandRunner(TextWriter.Null), script);
            return f;
        }

        public Task AddIssueAsync(
            string id,
            string? target,
            int priority = 1,
            IReadOnlyList<string>? labels = null)
        {
            var issues = JsonNode.Parse(File.ReadAllText(StatePath))!;
            var metadata = new JsonObject();
            if (target is not null) metadata["abacus_target"] = target;
            issues[id] = new JsonObject
            {
                ["id"] = id,
                ["status"] = "open",
                ["assignee"] = "",
                ["priority"] = priority,
                ["comment_count"] = 0,
                ["metadata"] = metadata,
                ["labels"] = new JsonArray((labels ?? [])
                    .Select(static label => (JsonNode?)JsonValue.Create(label)).ToArray()),
            };
            File.WriteAllText(StatePath, issues.ToJsonString());
            return Task.CompletedTask;
        }

        public JsonNode ReadIssue(string id) => JsonNode.Parse(File.ReadAllText(StatePath))![id]!;
        public void EditIssue(string id, Action<JsonNode> edit)
        {
            var issues = JsonNode.Parse(File.ReadAllText(StatePath))!;
            edit(issues[id]!);
            File.WriteAllText(StatePath, issues.ToJsonString());
        }
        public ValidatedAgent Agent(
            IReadOnlyList<string>? filters = null,
            ReasoningPolicy? reasoning = null) => new("alice", Workspace,
            new(true, "test", null, null, true), false, Targets: registry,
            TargetBranches: filters, Reasoning: reasoning);
        public async Task<PreparedClaim?> ClaimAsync(
            IReadOnlyList<string>? filters = null,
            ExecutionMode mode = ExecutionMode.Once,
            ReasoningPolicy? reasoning = null,
            IReadOnlyDictionary<string, string>? mappings = null,
            string defaultModel = "default-model",
            IReadOnlyDictionary<string, string>? effortMappings = null,
            string defaultEffort = "high",
            IReadOnlyDictionary<string, IReadOnlyList<string>>? argumentMappings = null,
            IReadOnlyList<string>? defaultArguments = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await new ClaimCoordinator(
                    Beads,
                    Git,
                    new TicketRecovery(Beads, TextWriter.Null),
                    TextWriter.Null,
                    reasoningModels: mappings,
                    defaultModel: defaultModel,
                    reasoningEfforts: effortMappings,
                    defaultEffort: defaultEffort,
                    reasoningArguments: argumentMappings,
                    defaultArguments: defaultArguments)
                .WaitForPreparedClaimAsync(Agent(filters, reasoning), true, mode, timeout.Token);
        }
        public async Task<string> RunGitAsync(params string[] args)
        {
            var result = await new CommandRunner(TextWriter.Null).RunAsync(new("git", args, Workspace));
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
