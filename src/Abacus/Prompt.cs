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
        Commit your changes, then use the repository's serialized merge process to merge
        the branch into the latest main branch.

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
