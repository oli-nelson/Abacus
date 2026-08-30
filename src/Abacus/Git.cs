namespace Abacus;

public sealed class Git(CommandRunner runner, string executable = "git")
{
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

        var status = await RunAsync(workspace, agentName, ["-C", workspace, "status", "--porcelain"], cancellationToken);
        if (!status.Succeeded)
        {
            throw new PreflightException($"[{agentName}] could not inspect Git status: {FailureDetail(status)}");
        }

        if (!string.IsNullOrEmpty(status.StandardOutput))
        {
            throw new PreflightException($"[{agentName}] workspace is dirty: '{workspace}'");
        }
    }

    public async Task<string> PrepareIssueBranchAsync(
        string workspace,
        string agentName,
        string issueId,
        CancellationToken cancellationToken)
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

        var switchArguments = branchExists.Succeeded
            ? new[] { "-C", workspace, "switch", branch }
            : new[] { "-C", workspace, "switch", "-c", branch };
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

    private async Task EnsureCleanAsync(
        string workspace,
        string agentName,
        CancellationToken cancellationToken)
    {
        var status = await RunAsync(workspace, agentName, ["-C", workspace, "status", "--porcelain"], cancellationToken);
        if (!status.Succeeded)
        {
            throw new WorkspacePreparationException($"could not inspect Git status: {FailureDetail(status)}");
        }

        if (!string.IsNullOrEmpty(status.StandardOutput))
        {
            throw new WorkspacePreparationException("workspace became dirty before OpenCode could start");
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
