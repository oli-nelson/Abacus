using Abacus;

namespace Abacus.Tests;

public sealed class OutputTests
{
    [Fact]
    public async Task DefaultRedirectedOutputShowsStatesButSuppressesCommands()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: false,
            color: false);

        await output.SetAgentAsync("alice", AgentActivity.Working, "abc-1 • agent CLI in pane %1");
        await output.DebugCommandAsync("alice", "bd show abc-1 --json");

        var text = writer.ToString();
        Assert.Contains("[alice] WORKING", text, StringComparison.Ordinal);
        Assert.Contains("abc-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bd show", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseOutputShowsStatesWarningsAndCommands()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: true,
            interactive: false,
            color: false);

        await output.SetAgentAsync("alice", AgentActivity.Preparing, "abc-1 • preparing workspace");
        await output.WarningAsync("alice", "something needs attention");
        await output.DebugCommandAsync("alice", "bd show abc-1 --json");

        var text = writer.ToString();
        Assert.Contains("[alice] PREPARING", text, StringComparison.Ordinal);
        Assert.Contains("[alice] WARNING", text, StringComparison.Ordinal);
        Assert.Contains("[alice] DEBUG", text, StringComparison.Ordinal);
        Assert.Contains("bd show abc-1 --json", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractiveDashboardRendersAllAgentRowsAndRecentWarnings()
    {
        var writer = new StringWriter();
        using (var output = new ConsoleOutput(
            writer,
            ["alice", "bob"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false))
        {
            await output.SetTicketAsync("alice", "abc-1", "Make the dashboard useful");
            await output.SetRunLocationAsync("alice", "pane %1");
            await output.SetAgentAsync("alice", AgentActivity.Working, "abc-1 • agent CLI in pane %1");
            await output.SetAgentAsync("bob", AgentActivity.Idle, "No ready tickets");
            await output.SetAgentAsync("alice", AgentActivity.Retrying, "Agent CLI failed; retrying soon");
            await output.SetLastExitCodeAsync("alice", 17);
            await output.WarningAsync("alice", "example warning");
            await output.SetUserAttentionIssuesAsync(
                [new BeadsIssue("abc-9", IssueStatus.Blocked, "Choose a save format")]);
            await output.SetPersistentAlertAsync("alice", "Recovery could not be verified");
            await output.SetLatestCommentsAsync([
                Comment("comment-1", "abc-9", "Choose a save format", "alice", "Agent update", attention: true),
                Comment("comment-2", "abc-2", "Implement parser", "alice", "Managed agent update"),
                Comment("comment-3", "abc-3", "Review output", "reviewer", "Unknown author update"),
            ]);
        }

        var text = writer.ToString();
        Assert.Contains("ABACUS", text, StringComparison.Ordinal);
        Assert.Contains("CLAIMS ON", text, StringComparison.Ordinal);
        Assert.Contains("Shift-Tab", text, StringComparison.Ordinal);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("WORKING", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
        Assert.Contains("IDLE", text, StringComparison.Ordinal);
        Assert.Contains("abc-1 — Make the dashboard useful", text, StringComparison.Ordinal);
        Assert.Contains("pane %1", text, StringComparison.Ordinal);
        Assert.Contains("retries 1", text, StringComparison.Ordinal);
        Assert.Contains("last exit 17", text, StringComparison.Ordinal);
        Assert.Contains("example warning", text, StringComparison.Ordinal);
        Assert.Contains("USER ATTENTION (2)", text, StringComparison.Ordinal);
        Assert.Contains("abc-9 — Choose a save format", text, StringComparison.Ordinal);
        Assert.Contains("alice — Recovery could not be verified", text, StringComparison.Ordinal);
        Assert.Contains("LATEST COMMENTS (3)", text, StringComparison.Ordinal);
        Assert.Contains("abc-9", text, StringComparison.Ordinal);
        Assert.Contains("Choose a save format", text, StringComparison.Ordinal);
        Assert.Contains("Agent update", text, StringComparison.Ordinal);
        Assert.Contains("Managed agent update", text, StringComparison.Ordinal);
        Assert.Contains("Unknown author update", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[?25h", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShiftTabCyclesWhetherNewClaimsAreAllowed()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false);
        var claimGate = new ClaimGate();

        Assert.True(claimGate.IsEnabled);
        Assert.False(output.HandleDashboardKey(
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: false, alt: false, control: false),
            claimGate));
        Assert.True(claimGate.IsEnabled);

        Assert.True(output.HandleDashboardKey(
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: false),
            claimGate));
        Assert.False(claimGate.IsEnabled);
        Assert.Contains("CLAIMS PAUSED", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("active tickets continue", writer.ToString(), StringComparison.Ordinal);

        Assert.True(output.HandleDashboardKey(
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: false),
            claimGate));
        Assert.True(claimGate.IsEnabled);
        Assert.Contains("New ticket claims enabled", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ClaimToggleRequiresShiftTabWithoutAdditionalModifiers()
    {
        Assert.True(ConsoleOutput.IsClaimToggle(
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: false)));
        Assert.False(ConsoleOutput.IsClaimToggle(
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: false, alt: false, control: false)));
        Assert.False(ConsoleOutput.IsClaimToggle(
            new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: true, alt: false, control: true)));
    }

    [Fact]
    public void AgentActionMenuSelectsRowsAndConfirmsWorkspaceCleanup()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice", "bob"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false,
            workspacePaths: new Dictionary<string, string>
            {
                ["alice"] = "/worktrees/alice",
                ["bob"] = "/worktrees/bob",
            });
        var actions = new List<(string Agent, AgentControlAction Action)>();
        var claimGate = new ClaimGate();

        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.DownArrow), claimGate, Record));
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.DownArrow), claimGate, Record));
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.Enter), claimGate, Record));
        Assert.Contains("AGENT ACTIONS — bob", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("[S] Stop agent", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("/worktrees/bob", writer.ToString(), StringComparison.Ordinal);

        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.C, 'c'), claimGate, Record));
        Assert.Contains("Permanently discard tracked and untracked", writer.ToString(), StringComparison.Ordinal);
        Assert.Empty(actions);

        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.Y, 'y'), claimGate, Record));
        Assert.Equal([("bob", AgentControlAction.CleanWorkspace)], actions);

        void Record(string agent, AgentControlAction action) => actions.Add((agent, action));
    }

    [Theory]
    [InlineData(ConsoleKey.S, AgentControlAction.Stop)]
    [InlineData(ConsoleKey.R, AgentControlAction.Restart)]
    public void AgentActionMenuDispatchesLifecycleAction(
        ConsoleKey key,
        AgentControlAction expected)
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false);
        (string Agent, AgentControlAction Action)? requested = null;
        var claimGate = new ClaimGate();

        output.HandleDashboardKey(Key(ConsoleKey.Enter), claimGate, (_, _) => { });
        output.HandleDashboardKey(
            Key(key, char.ToLowerInvariant(key.ToString()[0])),
            claimGate,
            (agent, action) => requested = (agent, action));

        Assert.Equal(("alice", expected), requested);
    }

    [Fact]
    public async Task LatestCommentHeadersUseAttentionAgentAndUnknownAuthorColorsWhileMessagesAreUncolored()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: true);

        await output.SetLatestCommentsAsync([
            Comment("comment-1", "abc-1", "Attention", "alice", "red", attention: true),
            Comment("comment-2", "abc-2", "Agent", "alice", "green"),
            Comment("comment-3", "abc-3", "Unknown", "reviewer", "cyan"),
        ]);

        var text = writer.ToString();
        Assert.Contains("\u001b[31m • abc-1", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[32m • abc-2", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[36m • abc-3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m   ↳ red", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[32m   ↳ green", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[36m   ↳ cyan", text, StringComparison.Ordinal);
        Assert.Contains("   ↳ red\u001b[K\n", text, StringComparison.Ordinal);
        Assert.Contains("   ↳ green\u001b[K\n", text, StringComparison.Ordinal);
        Assert.Contains("   ↳ cyan\u001b[K\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LatestCommentLinesFlattenAndWrapMessageAcrossTwoTerminalWidthLines()
    {
        var lines = ConsoleOutput.FormatLatestCommentLines(
            Comment(
                "comment-1",
                "abc-123",
                "A very long issue title\nthat continues on another line",
                "external-reviewer",
                "A very long comment\nthat also continues and must be truncated for the dashboard " +
                "because only two message lines are available and anything beyond them is omitted"),
            52);

        Assert.Equal(52, lines.Header.Length);
        Assert.DoesNotContain('\n', lines.Header);
        Assert.Contains('…', lines.Header);
        Assert.StartsWith(" • abc-123", lines.Header, StringComparison.Ordinal);
        Assert.Collection(
            lines.Comments,
            first =>
            {
                Assert.True(first.Length <= 52);
                Assert.DoesNotContain('\n', first);
                Assert.StartsWith("   ↳ ", first, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal(52, second.Length);
                Assert.DoesNotContain('\n', second);
                Assert.StartsWith("     ", second, StringComparison.Ordinal);
                Assert.Contains('…', second);
            });
    }

    [Fact]
    public async Task EnterOnSelectedCommentShowsItsCompleteWrappedText()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false);
        var message = "This comment is deliberately long enough to exceed the two-line dashboard preview. " +
            "The detail view keeps wrapping instead of truncating it, including this final phrase.";
        await output.SetLatestCommentsAsync([
            Comment("comment-1", "abc-1", "Review the complete comment", "reviewer", message),
        ]);
        var claimGate = new ClaimGate();

        output.HandleDashboardKey(Key(ConsoleKey.DownArrow), claimGate);
        output.HandleDashboardKey(Key(ConsoleKey.DownArrow), claimGate);
        Assert.Contains("›• abc-1", writer.ToString(), StringComparison.Ordinal);

        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.Enter), claimGate));
        output.HandleDashboardKey(Key(ConsoleKey.PageDown), claimGate);

        var text = writer.ToString();
        Assert.Contains("COMMENT — abc-1", text, StringComparison.Ordinal);
        Assert.Contains("Review the complete comment", text, StringComparison.Ordinal);
        Assert.Contains("reviewer", text, StringComparison.Ordinal);
        Assert.Contains("phrase.", text, StringComparison.Ordinal);
        Assert.Contains("Esc close", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommentDetailSupportsScrollingThroughLongMessages()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false);
        var message = string.Join('\n', Enumerable.Range(1, 100).Select(line => $"comment line {line}"));
        await output.SetLatestCommentsAsync([
            Comment("comment-1", "abc-1", "Long comment", "reviewer", message),
        ]);
        var claimGate = new ClaimGate();
        output.HandleDashboardKey(Key(ConsoleKey.UpArrow), claimGate);
        output.HandleDashboardKey(Key(ConsoleKey.Enter), claimGate);

        for (var page = 0; page < 10; page++)
        {
            output.HandleDashboardKey(Key(ConsoleKey.PageDown), claimGate);
        }

        Assert.Contains("comment line 100", writer.ToString(), StringComparison.Ordinal);
        Assert.True(output.HandleDashboardKey(Key(ConsoleKey.Escape), claimGate));
        Assert.Contains("LATEST COMMENTS (1)", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FullCommentWrappingPreservesEveryLogicalLine()
    {
        var lines = ConsoleOutput.WrapCommentText(
            "first paragraph with several words\n\nsecond paragraph ends here",
            12);

        Assert.Contains(string.Empty, lines);
        Assert.Equal(
            "first paragraph with several words second paragraph ends here",
            string.Join(' ', lines.Where(static line => line.Length > 0)));
        Assert.All(lines, line => Assert.True(line.Length <= 12));
    }

    [Fact]
    public async Task RedirectedOutputReportsAttentionChangesWithoutRepeatingThem()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice"],
            "provider/model",
            verbose: false,
            interactive: false,
            color: false);
        var issue = new BeadsIssue("abc-9", IssueStatus.Open, "Choose a save format");

        await output.SetUserAttentionIssuesAsync([issue]);
        await output.SetUserAttentionIssuesAsync([issue with { Status = IssueStatus.Blocked }]);
        await output.SetUserAttentionIssuesAsync([]);

        var attentionLines = writer.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.Contains("ATTENTION", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, attentionLines.Length);
        Assert.Contains("abc-9 — Choose a save format", attentionLines[0], StringComparison.Ordinal);
        Assert.Contains("No issues currently need user attention", attentionLines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryReplacesInteractiveDashboardWithOutcomeTotals()
    {
        var writer = new StringWriter();
        using var output = new ConsoleOutput(
            writer,
            ["alice", "bob"],
            "provider/model",
            verbose: false,
            interactive: true,
            color: false);

        await output.SetPersistentAlertAsync("alice", "Could not verify recovery");
        await output.SummaryAsync(new RunSummarySnapshot(
            TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3),
            "pjmrvjigiph28prpf6ir4uv0tuv88vnn",
            [
                new AgentRunSummary("alice", 2, 1, 0, 0),
                new AgentRunSummary("bob", 0, 0, 1, 1),
            ]));

        var text = writer.ToString();
        Assert.Contains("ABACUS RUN SUMMARY", text, StringComparison.Ordinal);
        Assert.Contains("2m 3s", text, StringComparison.Ordinal);
        Assert.Contains("5 outcomes", text, StringComparison.Ordinal);
        Assert.Contains("Initial Beads Dolt commit  pjmrvjigiph28prpf6ir4uv0tuv88vnn", text, StringComparison.Ordinal);
        Assert.Contains("alice", text, StringComparison.Ordinal);
        Assert.Contains("closed 2", text, StringComparison.Ordinal);
        Assert.Contains("bob", text, StringComparison.Ordinal);
        Assert.Contains("blocked 1", text, StringComparison.Ordinal);
        Assert.Contains("interrupted 1", text, StringComparison.Ordinal);
        Assert.Contains("USER ATTENTION", text, StringComparison.Ordinal);
        Assert.Contains("alice — Could not verify recovery", text, StringComparison.Ordinal);
    }

    private static BeadsComment Comment(
        string id,
        string issueId,
        string title,
        string author,
        string text,
        bool attention = false) =>
        new(id, issueId, title, author, text, DateTimeOffset.Parse("2026-09-02T12:00:00Z"), attention);

    private static ConsoleKeyInfo Key(ConsoleKey key, char character = '\0') =>
        new(character, key, shift: false, alt: false, control: false);
}
