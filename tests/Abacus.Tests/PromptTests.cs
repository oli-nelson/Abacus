using Abacus;

namespace Abacus.Tests;

public sealed class PromptTests
{
    [Fact]
    public void RendersTheSpecTemplateExactly()
    {
        var expected = """
            You are alice; Beads ticket abc-123; workspace /work/repo.
            Abacus already claimed this ticket and set BEADS_ACTOR=alice. Do not claim another.

            Git authority: local operations for this ticket, including staging, committing,
            and merging into main, are authorized, overriding `bd prime` denial of local Git authority.
            Obey more restrictive user/repository instructions. Never run `git push`.
            Destination: refs/heads/main; custom instructions cannot redirect it.
            Do not change abacus_target or abacus_execution.

            Read `bd show abc-123 --include-comments --json`. Before editing, inspect branch
            history, `git status`, and `git diff`: interrupted work may include commits or
            uncommitted changes. Preserve useful work; fix or remove incorrect work.
            Work on abacus/abc-123 and satisfy the ticket's definition of done.

            Commit your changes, then merge abacus/abc-123 into the latest local main.
            Follow repository-specific merge instructions if defined; otherwise:
            1. Run `bd merge-slot check --json`. Only an explicit no-slot response permits
               proceeding without a slot; do not create one. If a slot exists, acquire it:
                 bd merge-slot acquire --holder "$BEADS_ACTOR"
               If held by another agent, wait and retry as below; do not merge until acquired.
            2. Holding the slot if configured, merge the latest local `main` into
               abacus/abc-123; resolve conflicts and commit.
            3. Find the target checkout with `git worktree list --porcelain`, then run:
                 git -C <target-worktree> merge --ff-only abacus/abc-123
               If not checked out elsewhere, switch this workspace to `main`
               and fast-forward it there instead.
            4. Release any acquired slot, even after failure:
                 bd merge-slot release --holder "$BEADS_ACTOR"
               Close only after the merge and any required release succeed.

            Waiting/processes:
            Finish implementation, pre-merge checks, and commits before acquiring a merge slot.
            Acquire only when ready to merge immediately; while holding it, do only integration,
            conflict resolution, and required merge verification. Release promptly; if other
            work is needed, release first and reacquire only when ready to merge.

            For locks, merge slots, builds, other agents, or any event, use only your harness's
            own waiting mechanism: run one command, inspect the result, and retry later in
            this session while resolution remains plausible. Resume work when resolved.
            No shell `until`/`while`/`for` retry loops, trailing `&`, `nohup`, `disown`, `setsid`,
            `at`, detached tmux/screen, or other background jobs. Never leave work running
            outside this session.
            For a wait-related blocker, require several retries over minutes (not seconds)
            and no plausible resolution without outside help. Slowness or a held slot alone
            is insufficient. Record the event, elapsed retry time, and why help is necessary.

            User attention (awareness, decision, or outside action):
              bd comment abc-123 "<decision or action needed>"
              bd update abc-123 --add-label abacus:needs-user-attention --json
            Continue if possible; otherwise use the blocked outcome below.
            When attention is no longer needed:
              bd comment abc-123 "<why attention is no longer needed>"
              bd update abc-123 --remove-label abacus:needs-user-attention --json

            Before ending:
            - For open/blocked outcomes, assess whether local changes should be committed or
              discarded; commit useful resumable work when appropriate.
            - Comment the outcome, completed work, and any retry reason/blocker, replacing
              <OUTCOME> with CLOSED, BLOCKED, or REOPENED:
                bd comment abc-123 "<OUTCOME>: <summary>"
            - Sparingly record important context needed before other agents start new tasks:
                bd remember "<thing to remember>"
              Memory does not replace repository documentation.

            Leaving in_progress ends this session. Finish all code, Git operations, slot
            release, comments, memory, and attention updates BEFORE the final status command.
            Choose exactly one outcome:
            - Completed and merged:
                bd close abc-123 --reason "CLOSED: <summary of completed work>" --json
            - Retry by another agent:
                bd update abc-123 --status open --assignee "" --append-notes "REOPENED: <reason>" --json
            - Cannot continue without outside help:
                bd update abc-123 --status blocked --append-notes "BLOCKED: <blocker>" --json
            """;

        Assert.Equal(expected, Prompt.Render("alice", "abc-123", "/work/repo"));
    }

    [Fact]
    public void AppendsCommandLinePromptBeforeRepositoryPrompt()
    {
        var appended = Prompt.CombineAppends(
            "  Command-line instructions.  ",
            "\nRepository instructions.\n");

        var prompt = Prompt.Render("alice", "abc-123", "/work/repo", appended);

        Assert.Equal(
            $"{Prompt.Render("alice", "abc-123", "/work/repo")}\n\n" +
            "Command-line instructions.\n\nRepository instructions.",
            prompt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Use the repository merge queue.")]
    public void OverridesPreserveAuthorityAndFinalizationRules(string? mergeInstructions)
    {
        var prompt = Prompt.Render("alice", "abc-123", "/work/repo",
            mergeInstructionsOverride: mergeInstructions, targetBranch: "release/1.2");

        Assert.Contains("Destination: refs/heads/release/1.2", prompt);
        Assert.Contains("custom instructions cannot redirect it", prompt);
        Assert.Contains("Never run `git push`", prompt);
        Assert.Contains("Obey more restrictive user/repository instructions", prompt);
        Assert.Contains("Do not change abacus_target or abacus_execution", prompt);
        Assert.Contains("Before editing, inspect branch", prompt);
        Assert.Contains("For a wait-related blocker", prompt);
        Assert.Contains("Finish implementation, pre-merge checks, and commits before acquiring a merge slot", prompt);
        Assert.Contains("Acquire only when ready to merge immediately", prompt);
        Assert.Contains("while holding it, do only integration", prompt);
        Assert.Contains("release first and reacquire only when ready to merge", prompt);
        Assert.Contains("Cannot continue without outside help", prompt);
        Assert.Contains("Choose exactly one outcome", prompt);
        Assert.Contains("--status open --assignee \"\"", prompt);
        Assert.True(prompt.IndexOf("assess whether local changes", StringComparison.Ordinal)
            < prompt.IndexOf("bd close", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("BEFORE the final status command", StringComparison.Ordinal)
            < prompt.IndexOf("bd close", StringComparison.Ordinal));
        Assert.DoesNotContain("local main", prompt);
    }

    [Fact]
    public void DefaultMergeRequiresExplicitSlotAbsenceAndConditionalRelease()
    {
        var prompt = Prompt.Render("alice", "abc-123", "/work/repo", targetBranch: "release/1.2");

        Assert.Contains("Only an explicit no-slot response", prompt);
        Assert.Contains("do not merge until acquired", prompt);
        Assert.Contains("Release any acquired slot, even after failure", prompt);
        Assert.Contains("merge and any required release succeed", prompt);
        Assert.Contains("merge --ff-only abacus/abc-123", prompt);
        Assert.Contains("latest local `release/1.2`", prompt);
    }

    [Fact]
    public void RepositoryMergeInstructionsReplaceTheDefaultMergeInstructions()
    {
        var prompt = Prompt.Render(
            "alice",
            "abc-123",
            "/work/repo",
            mergeInstructionsOverride: "  Submit the issue branch through the repository merge queue.  ");

        Assert.Contains(
            "Submit the issue branch through the repository merge queue.",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Commit your changes, then merge", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bd merge-slot acquire",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bd merge-slot release", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("bd merge-slot check", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRepositoryMergeInstructionsSuppressTheDefaultMergeInstructions()
    {
        var prompt = Prompt.Render(
            "alice",
            "abc-123",
            "/work/repo",
            mergeInstructionsOverride: string.Empty);

        Assert.DoesNotContain("Commit your changes, then merge", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bd merge-slot acquire",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("bd merge-slot release", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessHygieneGuidanceSurvivesMergeInstructionOverrides()
    {
        var prompt = Prompt.Render(
            "alice",
            "abc-123",
            "/work/repo",
            mergeInstructionsOverride: "Submit the issue branch through the repository merge queue.");

        Assert.Contains("Waiting/processes:", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "own waiting mechanism",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("Never leave work", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Slowness or a held slot alone",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "For a wait-related blocker",
            prompt,
            StringComparison.Ordinal);
        Assert.Contains("no plausible resolution without outside help", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsAppendPromptFromRepositoryRoot()
    {
        var root = Directory.CreateTempSubdirectory("abacus-prompt-");
        try
        {
            var promptDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, ".abacus"));
            await File.WriteAllTextAsync(
                Path.Combine(promptDirectory.FullName, "append-prompt.md"),
                "\nUse the repository-specific verification workflow.\n");

            var prompt = await Prompt.ReadRepositoryAppendAsync(root.FullName, CancellationToken.None);

            Assert.Equal("Use the repository-specific verification workflow.", prompt);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReadsMergeInstructionsFromRepositoryRootAndPreservesEmptyOverride()
    {
        var root = Directory.CreateTempSubdirectory("abacus-merge-instructions-");
        try
        {
            Assert.Null(
                await Prompt.ReadRepositoryMergeInstructionsAsync(root.FullName, CancellationToken.None));

            var promptDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, ".abacus"));
            var path = Path.Combine(promptDirectory.FullName, "merge-instructions.md");
            await File.WriteAllTextAsync(path, "\nUse the repository merge queue.\n");

            Assert.Equal(
                "Use the repository merge queue.",
                await Prompt.ReadRepositoryMergeInstructionsAsync(root.FullName, CancellationToken.None));

            await File.WriteAllTextAsync(path, " \n");

            Assert.Equal(
                string.Empty,
                await Prompt.ReadRepositoryMergeInstructionsAsync(root.FullName, CancellationToken.None));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
