namespace Abacus;

public sealed class Git(CommandRunner runner, string executable = "git")
{
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
