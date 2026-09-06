namespace Abacus;

public sealed partial class ClaimCoordinator
{
    private static bool IsTargetEligible(ValidatedAgent agent, BeadsIssue issue)
    {
        // Invalid destinations must reach the atomic claim boundary so exactly one owner blocks them.
        TargetPolicy policy;
        try { policy = agent.Targets!.Validate(issue); }
        catch (TargetException) { return true; }
        return agent.TargetBranches is not { Count: > 0 } || agent.TargetBranches.Contains(policy.Branch);
    }

    private async Task<BeadsIssue> BindTargetAsync(ValidatedAgent agent, BeadsIssue claim, bool requireExisting, CancellationToken token)
    {
        var registry = agent.Targets!;
        var current = await beads.GetIssueAsync(agent.WorkspacePath, agent.Name, claim.Id, token)
            ?? throw new TargetException($"ticket '{claim.Id}' disappeared after claim");
        if (current.Status != IssueStatus.InProgress || current.Assignee != agent.Name)
            throw new TargetException("ticket ownership changed after claim");
        var policy = registry.Validate(current);
        if (claim.Binding is not null && claim.Binding != current.Binding)
            throw new TargetException("execution binding changed after claim; operator recovery is required");
        if (claim.HasDispatchSnapshot && claim.DispatchTarget != current.TargetBranch)
            throw new TargetException("abacus_target changed between selection and claim; review the intended destination");
        var targetCommit = await git.ResolveTargetCommitAsync(agent.WorkspacePath, agent.Name, policy.Branch, token);
        var exists = await git.IssueBranchExistsAsync(agent.WorkspacePath, agent.Name, current.Id, token);
        if (requireExisting && !exists) throw new TargetException("interrupted issue branch is missing");
        if (exists && current.Binding is null)
            throw new TargetException("existing issue branch has no execution binding; operator adoption is required (do not reset the branch)");
        if (current.Binding is null)
        {
            var binding = new ExecutionBinding(1, $"refs/heads/{policy.Branch}",
                $"abacus/{current.Id}", targetCommit, policy.Identity);
            await beads.SetExecutionBindingAsync(agent.WorkspacePath, agent.Name, current.Id, binding, token);
            current = await beads.GetIssueAsync(agent.WorkspacePath, agent.Name, current.Id, token)
                ?? throw new TargetException("ticket disappeared while recording its execution binding");
            registry.Validate(current);
            if (current.Binding != binding || current.Assignee != agent.Name || current.Status != IssueStatus.InProgress)
                throw new TargetException("could not verify execution binding and ownership before branch preparation");
            if (await recovery.PushWithRetryAsync(agent, token) == PushOutcome.Failed)
                await HaltAsync(agent.Name, "Could not synchronize execution binding; workspace was not changed");
        }
        await git.VerifyBoundHistoryAsync(agent.WorkspacePath, agent.Name, current.Binding!.StartCommit, policy.Branch, token);
        if (exists)
            await git.VerifyBoundHistoryAsync(agent.WorkspacePath, agent.Name, current.Binding!.StartCommit,
                current.Binding.IssueBranch, token);
        await log.SetAgentAsync(agent.Name, AgentActivity.Preparing, $"{current.Id} → {policy.Branch}");
        return current;
    }

    private async Task RejectTargetAsync(ValidatedAgent agent, BeadsIssue issue, string detail, CancellationToken token)
    {
        var reason = $"Abacus target validation failed: {detail}. " +
            $"Run abacus targets check {issue.Id}; for missing targets use " +
            $"abacus targets set <branch> {issue.Id}, then abacus attention resolve {issue.Id} --reopen after review.";
        try
        {
            await beads.BlockTargetIssueAsync(agent.WorkspacePath, agent.Name, issue.Id, reason, token);
            summary?.Record(agent.Name, TicketOutcome.Blocked, issue.Id, issue.Title);
            await WarnAsync(agent.Name, reason);
            if (await recovery.PushWithRetryAsync(agent, token) == PushOutcome.Failed)
                await HaltAsync(agent.Name, $"Could not synchronize target-validation block for {issue.Id}");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or AgentHaltedException))
        {
            await HaltAsync(agent.Name, $"Could not safely block {issue.Id}: {exception.Message}. Workspace preserved.");
        }
    }
}
