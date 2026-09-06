namespace Abacus;

/// <summary>Read-only auditing and conservative metadata repair; no agent preflight or Git mutations.</summary>
public sealed class TicketTargets(Beads beads, Git git)
{
    public async Task<int> RunAsync(string workspace, TicketTargetCommand command, TextWriter output, CancellationToken token)
    {
        var root = await git.ResolveMainRepositoryAsync(workspace, command.RepositoryPath, token);
        workspace = root;
        var registry = await TargetRegistry.LoadAsync(Path.Combine(root, ".abacus", "targets.json"), token);
        var issues = command.IssueIds.Count == 0
            ? await beads.GetTargetAuditIssuesAsync(workspace, token)
            : await ReadIssuesAsync(workspace, command.IssueIds, token);
        if (command.Check)
        {
            var failures = 0;
            foreach (var issue in issues.OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                try
                {
                    var policy = registry.Validate(issue);
                    await git.ResolveTargetCommitAsync(workspace, "abacus", policy.Branch, token);
                    if (issue.Binding is { } binding)
                        await git.VerifyBoundHistoryAsync(workspace, "abacus", binding.StartCommit, policy.Branch, token);
                    if (await git.IssueBranchExistsAsync(workspace, "abacus", issue.Id, token))
                    {
                        if (issue.Binding is null) throw new TargetException("existing issue branch has no execution binding; operator adoption is required");
                        await git.VerifyBoundHistoryAsync(workspace, "abacus", issue.Binding.StartCommit, issue.Binding.IssueBranch, token);
                    }
                    await output.WriteLineAsync($"OK {issue.Id}: {policy.Branch}{(issue.TargetBranch is null ? " (default)" : "")}");
                }
                catch (TargetException exception)
                {
                    failures++;
                    await output.WriteLineAsync($"INVALID {issue.Id}: {exception.Message}");
                }
            }
            await output.WriteLineAsync($"Checked {issues.Count} ticket(s); {failures} invalid.");
            return failures == 0 ? 0 : 1;
        }

        var target = registry.Resolve(command.Target);
        await git.ResolveTargetCommitAsync(workspace, "abacus", target.Branch, token);
        // Validate the entire batch before writing anything. CLI failures after that may leave a partial batch.
        foreach (var issue in issues) ValidateEditable(issue, target.Branch, registry);
        ExecutionBinding? adoption = null;
        if (command.AdoptExistingBranch)
        {
            if (issues.Count != 1 || issues[0].Binding is not null || !Git.IsCommitId(command.StartCommit))
                throw new TargetException("adoption requires one unbound ticket and an explicitly reviewed full starting commit");
            var issue = issues[0];
            if (!await git.IssueBranchExistsAsync(workspace, "abacus", issue.Id, token))
                throw new TargetException("cannot adopt a missing issue branch");
            await git.VerifyBoundHistoryAsync(workspace, "abacus", command.StartCommit!, $"abacus/{issue.Id}", token);
            await git.VerifyBoundHistoryAsync(workspace, "abacus", command.StartCommit!, target.Branch, token);
            adoption = new(1, $"refs/heads/{target.Branch}", $"abacus/{issue.Id}", command.StartCommit!, target.Identity);
        }
        foreach (var issue in issues)
        {
            var current = await beads.GetIssueAsync(workspace, "abacus", issue.Id, token)
                ?? throw new TargetException($"ticket '{issue.Id}' disappeared");
            ValidateEditable(current, target.Branch, registry);
            if (adoption is not null && current.Binding is not null)
                throw new TargetException("ticket acquired an execution binding during adoption; stop and inspect it");
            await beads.SetTargetMetadataAsync(workspace, issue.Id, target.Branch, token);
            if (adoption is not null)
                await beads.SetExecutionBindingAsync(workspace, "abacus", issue.Id, adoption, token);
            var verified = await beads.GetIssueAsync(workspace, "abacus", issue.Id, token)
                ?? throw new TargetException($"ticket '{issue.Id}' disappeared after update");
            registry.Validate(verified);
            if (adoption is not null && verified.Binding != adoption)
                throw new TargetException($"adoption verification failed for '{issue.Id}'");
            if (verified.TargetBranch != target.Branch) throw new TargetException($"target write verification failed for '{issue.Id}'");
            await output.WriteLineAsync($"Set {issue.Id}: abacus_target={target.Branch} (status and attention unchanged).");
        }
        return 0;
    }

    private static void ValidateEditable(BeadsIssue issue, string target, TargetRegistry registry)
    {
        if (issue.Status is not (IssueStatus.Open or IssueStatus.Blocked or IssueStatus.Closed))
            throw new TargetException($"'{issue.Id}' must be inactive before setting its target; stop dispatch and active work first");
        if (issue.Binding is not null)
        {
            // Repairing a missing/corrupt target back to its bound destination is safe.
            // A different destination or changed policy still fails binding validation.
            registry.Validate(issue with { TargetBranch = target, HasInvalidTargetMetadata = false });
        }
        if (issue.MetadataError is not null)
            throw new TargetException($"'{issue.Id}' has malformed metadata requiring manual inspection: {issue.MetadataError}");
    }

    private async Task<IReadOnlyList<BeadsIssue>> ReadIssuesAsync(string workspace, IReadOnlyList<string> ids, CancellationToken token)
    {
        var issues = new List<BeadsIssue>();
        foreach (var id in ids)
            issues.Add(await beads.GetIssueAsync(workspace, "abacus", id, token)
                ?? throw new TargetException($"ticket '{id}' does not exist"));
        return issues;
    }
}
