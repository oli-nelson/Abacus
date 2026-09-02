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
            Commit your changes, then use the repository's serialized merge process to merge
            the branch into the latest main branch.

            If the issue needs user awareness, a decision, or outside action, bring it to the
            user's attention with:

              bd update abc-123 --add-label abacus:needs-user-attention --append-notes "<decision or action needed>" --json

            Continue working when possible. If work cannot continue, also mark the issue
            blocked below. If user attention is no longer needed, remove the alert with:

              bd update abc-123 --remove-label abacus:needs-user-attention --json

            When you are completely finished, add a summary of what you did to the ticket notes:

              bd update abc-123 --append-notes "<summary of completed work>" --json

            Then finally update the ticket:

            - Success:
                bd close abc-123 --reason "<summary of completed work>" --json
            - Work should be retried:
                bd update abc-123 --status open --assignee "" --append-notes "<reason>" --json
            - Work is blocked:
                bd update abc-123 --status blocked --append-notes "<blocker>" --json

            Changing the ticket from in_progress tells Abacus to end this session. Make the
            status change one of your final actions, after all code, commits, merges, and
            ticket notes are complete.
            """;

        Assert.Equal(expected, Prompt.Render("alice", "abc-123", "/work/repo"));
    }
}
