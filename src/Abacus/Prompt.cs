namespace Abacus;

public static class Prompt
{
    public static readonly string RepositoryAppendPromptPath = Path.Combine(".abacus", "append-prompt.md");

    public static string Render(
        string agentName,
        string issueId,
        string workspacePath,
        string? appendedPrompt = null)
    {
        var prompt = $$"""
        You are {{agentName}}, working on Beads ticket {{issueId}} in {{workspacePath}}.

        Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
        name. Do not claim another ticket.
        Read the ticket with:

          bd show {{issueId}} --json

        Work on the branch abacus/{{issueId}} and satisfy the ticket's definition of done.
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

          bd comment {{issueId}} "<decision or action needed>"
          bd update {{issueId}} --add-label {{Beads.NeedsUserAttentionLabel}} --json

        Continue working when possible. If work cannot continue, also mark the issue
        blocked below. If user attention is no longer needed, remove the alert with:

          bd comment {{issueId}} "<why user attention is no longer needed>"
          bd update {{issueId}} --remove-label {{Beads.NeedsUserAttentionLabel}} --json

        When you are completely finished, add a summary of what you did as a comment:

          bd comment {{issueId}} "<summary of completed work>"

        If your work introduces important things for other agents to remember before they start new tasks, add them to memory:

          bd remember "<thing to remember>"

        But use memory sparingly; it is not a substitute for good documentation in the repository.

        Then finally update the ticket:

        - Success:
            bd close {{issueId}} --reason "<summary of completed work>" --json
        - Work should be retried:
            bd update {{issueId}} --status open --assignee "" --append-notes "<reason>" --json
        - Work is blocked:
            bd update {{issueId}} --status blocked --append-notes "<blocker>" --json

        If you need to set the status of the ticket to anything other than closed, assess if your current local
        changes need to be committed or discarded. For example, if you just need to block the ticket to get some
        user attention, you can commit your changes and then block the ticket. Eventually an agent will come back
        to the ticket and continue working on it.

        Changing the ticket from in_progress tells Abacus to end this session. Make the
        status change one of your final actions, after all code, commits, merges, and
        ticket updates are complete.
        """;

        return string.IsNullOrWhiteSpace(appendedPrompt)
            ? prompt
            : $"{prompt}\n\n{appendedPrompt.Trim()}";
    }

    public static async Task<string?> ReadRepositoryAppendAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspaceRoot, RepositoryAppendPromptPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var contents = await File.ReadAllTextAsync(path, cancellationToken);
        return string.IsNullOrWhiteSpace(contents) ? null : contents.Trim();
    }

    public static string? CombineAppends(string? commandLinePrompt, string? repositoryPrompt)
    {
        var fragments = new[] { commandLinePrompt, repositoryPrompt }
            .Where(static fragment => !string.IsNullOrWhiteSpace(fragment))
            .Select(static fragment => fragment!.Trim())
            .ToArray();
        return fragments.Length == 0 ? null : string.Join("\n\n", fragments);
    }
}
