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
              Commit your changes, then merge abacus/{{issueId}} into the latest local {{targetBranch}}.
              Follow repository-specific merge instructions if defined; otherwise:
              1. Run `bd merge-slot check --json`. Only an explicit no-slot response permits
                 proceeding without a slot; do not create one. If a slot exists, acquire it:
                   bd merge-slot acquire --holder "$BEADS_ACTOR"
                 If held by another agent, wait and retry as below; do not merge until acquired.
              2. Holding the slot if configured, merge the latest local `{{targetBranch}}` into
                 abacus/{{issueId}}; resolve conflicts and commit.
              3. Find the target checkout with `git worktree list --porcelain`, then run:
                   git -C <target-worktree> merge --ff-only abacus/{{issueId}}
                 If not checked out elsewhere, switch this workspace to `{{targetBranch}}`
                 and fast-forward it there instead.
              4. Release any acquired slot, even after failure:
                   bd merge-slot release --holder "$BEADS_ACTOR"
                 Close only after the merge and any required release succeed.
              """
            : mergeInstructionsOverride.Trim();

        var prompt = $$"""
        You are {{agentName}}; Beads ticket {{issueId}}; workspace {{workspacePath}}.
        Abacus already claimed this ticket and set BEADS_ACTOR={{agentName}}. Do not claim another.

        Git authority: local operations for this ticket, including staging, committing,
        and merging into {{targetBranch}}, are authorized, overriding `bd prime` denial of local Git authority.
        Obey more restrictive user/repository instructions. Never run `git push`.
        Destination: refs/heads/{{targetBranch}}; custom instructions cannot redirect it.
        Do not change abacus_target or abacus_execution.

        Read `bd show {{issueId}} --include-comments --json`. Before editing, inspect branch
        history, `git status`, and `git diff`: interrupted work may include commits or
        uncommitted changes. Preserve useful work; fix or remove incorrect work.
        Work on abacus/{{issueId}} and satisfy the ticket's definition of done.

        {{effectiveMergeInstructions}}

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
          bd comment {{issueId}} "<decision or action needed>"
          bd update {{issueId}} --add-label {{Beads.NeedsUserAttentionLabel}} --json
        Continue if possible; otherwise use the blocked outcome below.
        When attention is no longer needed:
          bd comment {{issueId}} "<why attention is no longer needed>"
          bd update {{issueId}} --remove-label {{Beads.NeedsUserAttentionLabel}} --json

        Before ending:
        - For open/blocked outcomes, assess whether local changes should be committed or
          discarded; commit useful resumable work when appropriate.
        - Comment the outcome, completed work, and any retry reason/blocker, replacing
          <OUTCOME> with CLOSED, BLOCKED, or REOPENED:
            bd comment {{issueId}} "<OUTCOME>: <summary>"
        - Sparingly record important context needed before other agents start new tasks:
            bd remember "<thing to remember>"
          Memory does not replace repository documentation.

        Leaving in_progress ends this session. Finish all code, Git operations, slot
        release, comments, memory, and attention updates BEFORE the final status command.
        Choose exactly one outcome:
        - Completed and merged:
            bd close {{issueId}} --reason "CLOSED: <summary of completed work>" --json
        - Retry by another agent:
            bd update {{issueId}} --status open --assignee "" --append-notes "REOPENED: <reason>" --json
        - Cannot continue without outside help:
            bd update {{issueId}} --status blocked --append-notes "BLOCKED: <blocker>" --json
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
