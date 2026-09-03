using Abacus;

namespace Abacus.Tests;

public sealed class PromptTests
{
    [Fact]
    public void RendersTheSpecTemplateExactly()
    {
        var expected = """
            You are alice, working on Beads ticket abc-123 in /work/repo.

            Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
            name. Do not claim another ticket.
            Read the ticket with:

              bd show abc-123 --json

            Work on the branch abacus/abc-123 and satisfy the ticket's definition of done.
            Commit your changes, then merge the branch into the latest local main branch.

            Follow any repository-specific merge instructions when they define a merge process.
            Otherwise, use this basic merge strategy:

            1. Check for a Beads merge slot with `bd merge-slot check --json`. If the response
               reports that no merge slot exists, continue without one; do not create one. If a
               slot exists, acquire it before merging, waiting and retrying while another agent
               holds it:

                 until bd merge-slot acquire --holder "$BEADS_ACTOR"; do sleep 2; done

            2. While holding the merge slot when one is configured, merge the latest local
               `main` into the issue branch. Resolve any conflicts and commit the result.
            3. Locate the worktree where `main` is checked out with
               `git worktree list --porcelain`, then fast-forward it to the issue branch with
               `git -C <main-worktree> merge --ff-only <issue-branch>`. If `main` is not checked
               out elsewhere, switch this workspace to `main` and fast-forward it there.
            4. If you acquired a merge slot, release it with
               `bd merge-slot release --holder "$BEADS_ACTOR"`. Always release it, including
               when the merge fails. Only close the ticket after the merge and release succeed.

            You might not be the first agent to work on this ticket, there might be commits
            in this branch that are already contributing to the ticket. Make sure you
            understand the current state of the branch before you make changes. If you think
            the original commits are incorrect, you can fix/remove them.

            If the issue needs user awareness, a decision, or outside action, bring it to the
            user's attention with:

              bd comment abc-123 "<decision or action needed>"
              bd update abc-123 --add-label abacus:needs-user-attention --json

            Continue working when possible. If work cannot continue, also mark the issue
            blocked below. If user attention is no longer needed, remove the alert with:

              bd comment abc-123 "<why user attention is no longer needed>"
              bd update abc-123 --remove-label abacus:needs-user-attention --json

            When you are completely finished, add a summary of what you did as a comment:

              bd comment abc-123 "<summary of completed work>"

            If your work introduces important things for other agents to remember before they start new tasks, add them to memory:

              bd remember "<thing to remember>"

            But use memory sparingly; it is not a substitute for good documentation in the repository.

            Then finally update the ticket:

            - Success:
                bd close abc-123 --reason "<summary of completed work>" --json
            - Work should be retried:
                bd update abc-123 --status open --assignee "" --append-notes "<reason>" --json
            - Work is blocked:
                bd update abc-123 --status blocked --append-notes "<blocker>" --json

            If you need to set the status of the ticket to anything other than closed, assess if your current local
            changes need to be committed or discarded. For example, if you just need to block the ticket to get some
            user attention, you can commit your changes and then block the ticket. Eventually an agent will come back
            to the ticket and continue working on it.

            Changing the ticket from in_progress tells Abacus to end this session. Make the
            status change one of your final actions, after all code, commits, merges, and
            ticket updates are complete.
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
}
