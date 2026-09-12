namespace Abacus;

public static class Prompt
{
    public static readonly string RepositoryAppendPromptPath = Path.Combine(".abacus", "append-prompt.md");
    public static readonly string RepositoryMergeInstructionsPath = Path.Combine(".abacus", "merge-instructions.md");

    public static string Render(
        string agentName,
        string issueId,
        string workspacePath,
        string? appendedPrompt = null,
        string? mergeInstructionsOverride = null,
        string targetBranch = "main")
    {
        var effectiveMergeInstructions = mergeInstructionsOverride is null
            ? $$"""
              Commit your changes, then merge the branch into the latest local {{targetBranch}} branch.

              Follow any repository-specific merge instructions when they define a merge process.
              Otherwise, use this basic merge strategy:

              1. Check for a Beads merge slot with `bd merge-slot check --json`. If the response
                 reports that no merge slot exists, continue without one; do not create one. If a
                 slot exists, acquire it before merging by running this command once:

                   bd merge-slot acquire --holder "$BEADS_ACTOR"

                 If another agent holds the slot, wait with your harness's own waiting mechanism
                 and run that same single command again later in this session. Never wrap it in a
                 shell retry loop, and never block the ticket just because the slot is held.

              2. While holding the merge slot when one is configured, merge the latest local
                 `{{targetBranch}}` into the issue branch. Resolve any conflicts and commit the result.
              3. Locate the worktree where `{{targetBranch}}` is checked out with
                 `git worktree list --porcelain`, then fast-forward it to the issue branch with
                 `git -C <target-worktree> merge --ff-only <issue-branch>`. If `{{targetBranch}}` is not checked
                 out elsewhere, switch this workspace to `{{targetBranch}}` and fast-forward it there.
              4. If you acquired a merge slot, release it with
                 `bd merge-slot release --holder "$BEADS_ACTOR"`. Always release it, including
                 when the merge fails. Only close the ticket after the merge and release succeed.
              """
            : mergeInstructionsOverride.Trim();

        var prompt = $$"""
        You are {{agentName}}, working on Beads ticket {{issueId}} in {{workspacePath}}.

        Abacus has already claimed the ticket for you and set BEADS_ACTOR to your agent
        name. Do not claim another ticket.

        Abacus grants you authority to perform the local Git operations needed for this
        ticket, including staging, committing, and merging into the local {{targetBranch}} branch.
        You do not have authority to push; do not run `git push`. If `bd prime` says
        there is no Git authority, this explicit Abacus instruction overrides that.
        Follow any more restrictive user or repository instruction.
        The bound destination is refs/heads/{{targetBranch}}. Custom instructions cannot
        redirect this ticket to another branch. Do not change abacus_target or abacus_execution.

        Waiting and long-running processes:
        Waiting for a lock, merge slot, build, another agent, or any other event is
        normal and is not by itself a reason to block this ticket. When you need to
        wait, use only your harness's own waiting mechanism: run one command, inspect
        its result, and try again later in this session, for as long as the event can
        still plausibly occur. Keep working the ticket once the wait resolves.

        Never leave work running outside this session. Do not write shell
        `until`/`while`/`for` retry loops or use trailing `&`, `nohup`, `disown`,
        `setsid`, `at`, or detached tmux, screen, or other background jobs. A process
        that survives this harness exiting, such as
        `until bd merge-slot acquire --holder "$BEADS_ACTOR"; do sleep 2; done`, keeps
        running after Abacus cleans up this session and can break shared coordination
        such as the merge slot for every other agent.

        Mark the ticket blocked only when the wait looks hopeless to resolve without
        outside help: you have retried in this session several times over a sustained
        period (minutes, not seconds), the event still has not happened, and nothing
        you can do will make it happen (for example, `bd merge-slot check` keeps
        reporting a held slot that never becomes available). When you do block, state
        in the note what you were waiting for, how long you retried, and why you
        concluded the wait cannot resolve. A slow wait is not a hopeless wait: while
        there is still a plausible path, keep waiting and keep retrying.

        Read the ticket with:

          bd show {{issueId}} --include-comments --json

        Work on the branch abacus/{{issueId}} and satisfy the ticket's definition of done.
        {{effectiveMergeInstructions}}

        You might not be the first agent to work on this ticket. The branch can contain
        commits or uncommitted changes preserved from an interrupted run. Inspect the
        branch history, `git status`, and `git diff` before making changes so you preserve
        useful existing work. If earlier work is incorrect, you can fix or remove it.

        If the issue needs user awareness, a decision, or outside action, bring it to the
        user's attention with:

          bd comment {{issueId}} "<decision or action needed>"
          bd update {{issueId}} --add-label {{Beads.NeedsUserAttentionLabel}} --json

        Continue working when possible. If work cannot continue, also mark the issue
        blocked below. If user attention is no longer needed, remove the alert with:

          bd comment {{issueId}} "<why user attention is no longer needed>"
          bd update {{issueId}} --remove-label {{Beads.NeedsUserAttentionLabel}} --json

        When you are completely finished, add a summary of what you did as a comment:

          bd comment {{issueId}} "CLOSED/BLOCKED/REOPENED/etc: <summary of completed work>"

        If your work introduces important things for other agents to remember before they start new tasks, add them to memory:

          bd remember "<thing to remember>"

        But use memory sparingly; it is not a substitute for good documentation in the repository.

        Then finally update the ticket:

        - Success:
            bd close {{issueId}} --reason "CLOSED: <summary of completed work>" --json
        - Work should be retried:
            bd update {{issueId}} --status open --assignee "" --append-notes "REOPENED: <reason>" --json
        - Work is blocked:
            bd update {{issueId}} --status blocked --append-notes "BLOCKED: <blocker>" --json

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

    public static async Task<string?> ReadRepositoryMergeInstructionsAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspaceRoot, RepositoryMergeInstructionsPath);
        if (!File.Exists(path))
        {
            return null;
        }

        return (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
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
