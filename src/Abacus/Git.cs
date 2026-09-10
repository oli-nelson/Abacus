namespace Abacus;

public sealed record BranchPruneResult(
    IReadOnlyList<string> DeletedBranches,
    IReadOnlyList<string> SkippedCheckedOutBranches);

public sealed record WorkspaceStatus(string Branch, bool IsDirty);

public sealed partial class Git(CommandRunner runner, string executable = "git")
{
    public async Task<WorkspaceStatus> GetWorkspaceStatusAsync(
        string workspace, string agentName, CancellationToken cancellationToken)
    {
        var result = await RunAsync(workspace, agentName,
            ["-C", workspace, "--no-optional-locks", "status", "--porcelain=v2", "--branch", "--untracked-files=normal", "-z"], cancellationToken);
        if (!result.Succeeded)
            throw new WorkspacePreparationException($"could not inspect workspace: {FailureDetail(result)}");
        var fields = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var head = fields.FirstOrDefault(field => field.StartsWith("# branch.head ", StringComparison.Ordinal))?[14..];
        if (string.IsNullOrEmpty(head))
            throw new WorkspacePreparationException("Git returned workspace status without a branch");
        if (head == "(detached)")
        {
            var oid = fields.FirstOrDefault(field => field.StartsWith("# branch.oid ", StringComparison.Ordinal))?[13..];
            if (!IsCommitId(oid))
                throw new WorkspacePreparationException("Git returned detached status without a commit");
            head = $"detached@{oid![..7]}";
        }
        return new WorkspaceStatus(head, fields.Any(field => !field.StartsWith("# ", StringComparison.Ordinal)));
    }

    public async Task<string> ResolveWorkspaceRootAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            workspace,
            agentName,
            ["-C", workspace, "rev-parse", "--show-toplevel"],
            cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new PreflightException($"[{agentName}] '{workspace}' is not a Git worktree");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StandardOutput.Trim()));
    }

    public static bool IsValidIssueId(string issueId)
    {
        if (string.IsNullOrWhiteSpace(issueId)
            || issueId.StartsWith('-')
            || issueId.Length > 200
            || issueId is "." or ".."
            || issueId.EndsWith(".", StringComparison.Ordinal)
            || issueId.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || issueId.Contains("..", StringComparison.Ordinal)
            || issueId.Contains("@{", StringComparison.Ordinal))
        {
            return false;
        }

        return issueId.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    public async Task VerifyWorkspaceAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var inside = await RunAsync(workspace, agentName, ["-C", workspace, "rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (!inside.Succeeded || !string.Equals(inside.StandardOutput.Trim(), "true", StringComparison.Ordinal))
        {
            throw new PreflightException($"[{agentName}] '{workspace}' is not a Git worktree");
        }
    }

    public async Task<bool> CanUseIssueBranchAsync(
        string workspace, string agentName, string issueId, CancellationToken cancellationToken)
    {
        // Leave unsafe IDs to the existing post-claim validation/quarantine path.
        if (!IsValidIssueId(issueId)) return true;
        var branchRef = $"refs/heads/abacus/{issueId}";
        var result = await RunAsync(workspace, agentName,
            ["-C", workspace, "for-each-ref", "--format=%(refname)%00%(worktreepath)%00", branchRef],
            cancellationToken);
        if (!result.Succeeded)
            throw new WorkspacePreparationException($"could not inspect issue branch ownership: {FailureDetail(result)}");
        if (result.StandardOutput.Length == 0) return true; // New branch.

        // NUL-delimited fields preserve spaces and newlines in worktree paths.
        var fields = result.StandardOutput.Split('\0');
        if (fields.Length % 2 != 1 || fields[^1] != "\n")
            throw new WorkspacePreparationException("Git returned malformed issue branch ownership");
        for (var i = 0; i < fields.Length - 1; i += 2)
        {
            var reference = fields[i].TrimStart('\n');
            if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal))
                throw new WorkspacePreparationException("Git returned malformed issue branch ownership");
            if (reference != branchRef) continue; // for-each-ref also matches descendants.
            var owner = fields[i + 1];
            if (owner.Length == 0) return true; // Existing but not checked out.
            if (!Path.IsPathFullyQualified(owner))
                throw new WorkspacePreparationException("Git returned an invalid owning worktree path");
            var root = await ResolveWorkspaceRootAsync(workspace, agentName, cancellationToken);
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(owner)), root,
                StringComparison.Ordinal);
        }
        return true;
    }

    public async Task<string> PrepareIssueBranchAsync(
        string workspace,
        string agentName,
        string issueId,
        CancellationToken cancellationToken,
        string? startCommit = null)
    {
        if (!IsValidIssueId(issueId))
        {
            throw new WorkspacePreparationException($"unsafe Beads issue ID '{issueId}'");
        }

        await EnsureCleanAsync(workspace, agentName, cancellationToken);
        var branch = $"abacus/{issueId}";
        var branchExists = await RunAsync(
            workspace,
            agentName,
            ["-C", workspace, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}"],
            cancellationToken);

        if (!branchExists.Succeeded && branchExists.ExitCode != 1)
            throw new WorkspacePreparationException($"could not inspect branch '{branch}': {FailureDetail(branchExists)}");
        startCommit ??= await ResolveTargetCommitAsync(workspace, agentName, "main", cancellationToken);
        if (!IsCommitId(startCommit)) throw new WorkspacePreparationException("invalid starting commit");
        var switchArguments = branchExists.Succeeded
            ? new[] { "-C", workspace, "switch", "--no-guess", branch }
            : new[] { "-C", workspace, "switch", "-c", branch, startCommit };
        var switchResult = await RunAsync(workspace, agentName, switchArguments, cancellationToken);
        if (!switchResult.Succeeded)
        {
            throw new WorkspacePreparationException(
                $"could not switch to branch '{branch}': {FailureDetail(switchResult)}");
        }

        var current = await RunAsync(
            workspace,
            agentName,
            ["-C", workspace, "branch", "--show-current"],
            cancellationToken);
        if (!current.Succeeded || !string.Equals(current.StandardOutput.Trim(), branch, StringComparison.Ordinal))
        {
            throw new WorkspacePreparationException($"Git did not select expected branch '{branch}'");
        }

        await EnsureCleanAsync(workspace, agentName, cancellationToken);
        return branch;
    }

    public async Task<string> GetCurrentBranchAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var current = await RunAsync(
            workspace,
            agentName,
            ["-C", workspace, "branch", "--show-current"],
            cancellationToken);
        if (!current.Succeeded || string.IsNullOrWhiteSpace(current.StandardOutput))
        {
            throw new WorkspacePreparationException(
                $"could not identify the current branch: {FailureDetail(current)}");
        }

        return current.StandardOutput.Trim();
    }

    public static bool TryGetIssueId(string branch, out string issueId)
    {
        const string prefix = "abacus/";
        if (!branch.StartsWith(prefix, StringComparison.Ordinal))
        {
            issueId = string.Empty;
            return false;
        }

        issueId = branch[prefix.Length..];
        if (IsValidIssueId(issueId))
        {
            return true;
        }

        issueId = string.Empty;
        return false;
    }

    public async Task<BranchPruneResult> PruneClosedIssueBranchesAsync(
        string workspace,
        IEnumerable<string> closedIssueIds,
        CancellationToken cancellationToken)
    {
        var closedIds = closedIssueIds
            .Where(IsValidIssueId)
            .ToHashSet(StringComparer.Ordinal);
        if (closedIds.Count == 0)
        {
            return new BranchPruneResult([], []);
        }

        var branches = await RunAsync(
            workspace,
            agentName: "abacus",
            ["-C", workspace, "for-each-ref", "--format=%(refname:short)%00%(worktreepath)", "refs/heads/abacus/"],
            cancellationToken);
        if (!branches.Succeeded)
        {
            throw new WorkspacePreparationException(
                $"could not list Abacus branches: {FailureDetail(branches)}");
        }

        var deletable = new List<string>();
        var skipped = new List<string>();
        foreach (var line in branches.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\0', 2);
            var branch = parts[0];
            const string prefix = "abacus/";
            if (!branch.StartsWith(prefix, StringComparison.Ordinal)
                || !closedIds.Contains(branch[prefix.Length..]))
            {
                continue;
            }

            if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1]))
            {
                skipped.Add(branch);
            }
            else
            {
                deletable.Add(branch);
            }
        }

        deletable.Sort(StringComparer.Ordinal);
        skipped.Sort(StringComparer.Ordinal);
        if (deletable.Count > 0)
        {
            var deleteArguments = new List<string> { "-C", workspace, "branch", "-D", "--" };
            deleteArguments.AddRange(deletable);
            var delete = await RunAsync(workspace, "abacus", deleteArguments, cancellationToken);
            if (!delete.Succeeded)
            {
                throw new WorkspacePreparationException(
                    $"could not delete closed ticket branches: {FailureDetail(delete)}");
            }
        }

        return new BranchPruneResult(deletable, skipped);
    }

    public async Task<bool> IsWorkspaceCleanAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var status = await RunAsync(workspace, agentName, ["-C", workspace, "status", "--porcelain"], cancellationToken);
        if (!status.Succeeded)
        {
            throw new WorkspacePreparationException($"could not inspect Git status: {FailureDetail(status)}");
        }

        return string.IsNullOrEmpty(status.StandardOutput);
    }

    public async Task CleanWorkspaceAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var reset = await RunAsync(
            workspace,
            agentName,
            ["-C", workspace, "reset", "--hard"],
            cancellationToken);
        if (!reset.Succeeded)
        {
            throw new WorkspacePreparationException(
                $"could not reset the workspace: {FailureDetail(reset)}");
        }

        var clean = await RunAsync(
            workspace,
            agentName,
            ["-C", workspace, "clean", "-fd"],
            cancellationToken);
        if (!clean.Succeeded)
        {
            throw new WorkspacePreparationException(
                $"could not remove untracked files: {FailureDetail(clean)}");
        }

        await EnsureCleanAsync(workspace, agentName, cancellationToken);
    }

    private async Task EnsureCleanAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        if (!await IsWorkspaceCleanAsync(workspace, agentName, cancellationToken))
        {
            throw new WorkspacePreparationException("workspace became dirty before the agent CLI could start");
        }
    }

    private Task<CommandResult> RunAsync(
        string workspace,
        string agentName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        runner.RunAsync(new CommandSpec(executable, arguments, workspace, AgentName: agentName), cancellationToken);

    private static string FailureDetail(CommandResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {result.ExitCode}"
            : result.StandardError.Trim();
}

public sealed class WorkspacePreparationException(string message) : Exception(message);
