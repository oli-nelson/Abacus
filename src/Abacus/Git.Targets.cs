namespace Abacus;

public sealed partial class Git
{
    /// <summary>Resolve the controller checkout; never promote a linked worktree to its main repo silently.</summary>
    public async Task<string> ResolveMainRepositoryAsync(string workingDirectory, string? repositoryPath,
        CancellationToken token)
    {
        var selected = repositoryPath ?? workingDirectory;
        if (!Directory.Exists(selected))
            throw new PreflightException($"repository directory '{selected}' does not exist; use --repo <main-checkout>");
        string root;
        try { root = await ResolveWorkspaceRootAsync(selected, "abacus", token); }
        catch (PreflightException)
        {
            throw new PreflightException($"'{selected}' is not inside a Git checkout; run inside the main repository or pass --repo <main-checkout>");
        }
        var gitDirectory = await RunAsync(root, "abacus",
            ["-C", root, "rev-parse", "--absolute-git-dir"], token);
        var commonDirectory = await RunAsync(root, "abacus",
            ["-C", root, "rev-parse", "--path-format=absolute", "--git-common-dir"], token);
        var gitPath = gitDirectory.StandardOutput.Trim();
        var commonPath = commonDirectory.StandardOutput.Trim();
        if (!gitDirectory.Succeeded || !commonDirectory.Succeeded
            || !Path.IsPathRooted(gitPath) || !Path.IsPathRooted(commonPath))
            throw new PreflightException($"could not identify the main Git repository at '{root}'");
        if (!string.Equals(Path.GetFullPath(gitPath), Path.GetFullPath(commonPath), StringComparison.Ordinal))
            throw new PreflightException($"'{root}' is a linked Git worktree, not the main repository; pass --repo <main-checkout> (agent -a paths may still be worktrees)");
        return root;
    }

    public static bool IsCommitId(string? value) => value is { Length: 40 or 64 }
        && value.All(char.IsAsciiHexDigit);

    // Deliberately restrict configuration to literal local branch names, not revision expressions.
    public static bool IsValidTargetBranch(string branch) => !string.IsNullOrWhiteSpace(branch)
        && branch != "HEAD" && !branch.StartsWith('-') && !branch.StartsWith("refs/", StringComparison.Ordinal)
        && !branch.StartsWith("abacus/", StringComparison.Ordinal)
        && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal)
        && branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
        && branch.Split('/').All(part => part.Length > 0 && !part.StartsWith('.')
            && !part.EndsWith('.') && !part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));

    public async Task<string> ResolveTargetCommitAsync(string workspace, string agent, string target, CancellationToken token)
    {
        if (!IsValidTargetBranch(target)) throw new TargetException($"invalid target branch '{target}'");
        var result = await RunAsync(workspace, agent,
            ["-C", workspace, "rev-parse", "--verify", $"refs/heads/{target}^{{commit}}"], token);
        var commit = result.StandardOutput.Trim();
        if (!result.Succeeded || !IsCommitId(commit))
            throw new TargetException($"local target branch '{target}' is missing or does not resolve to a commit");
        return commit;
    }

    public async Task<bool> IssueBranchExistsAsync(string workspace, string agent, string issueId, CancellationToken token)
    {
        if (!IsValidIssueId(issueId)) throw new TargetException($"unsafe issue ID '{issueId}'");
        var result = await RunAsync(workspace, agent,
            ["-C", workspace, "show-ref", "--verify", "--quiet", $"refs/heads/abacus/{issueId}"], token);
        if (result.Succeeded) return true;
        if (result.ExitCode == 1) return false;
        throw new WorkspacePreparationException($"could not inspect issue branch: {FailureDetail(result)}");
    }

    public async Task VerifyBoundHistoryAsync(string workspace, string agent, string commit, string branch, CancellationToken token)
    {
        var result = await RunAsync(workspace, agent,
            ["-C", workspace, "merge-base", "--is-ancestor", commit, $"refs/heads/{branch}"], token);
        if (!result.Succeeded)
            throw new TargetException($"branch '{branch}' does not contain the recorded starting commit; operator recovery is required");
    }

    public async Task<string> ResolveGitDirectoryAsync(string workspace, string agent, CancellationToken token)
    {
        var result = await RunAsync(workspace, agent,
            ["-C", workspace, "rev-parse", "--absolute-git-dir"], token);
        if (!result.Succeeded || !Path.IsPathRooted(result.StandardOutput.Trim()))
            throw new WorkspacePreparationException("could not resolve the workspace's Git directory");
        return result.StandardOutput.Trim();
    }
}

/// <summary>OS-held ownership, outside the checkout. Never unlink the lock file (inode races).</summary>
public sealed class WorkspaceOwnership : IDisposable
{
    private readonly List<FileStream> locks = [];

    public static async Task<WorkspaceOwnership> AcquireAsync(Git git, IReadOnlyList<ValidatedAgent> agents, CancellationToken token)
    {
        var ownership = new WorkspaceOwnership();
        try
        {
            foreach (var agent in agents.OrderBy(a => a.WorkspacePath, StringComparer.Ordinal))
            {
                var directory = await git.ResolveGitDirectoryAsync(agent.WorkspacePath, agent.Name, token);
                try
                {
                    ownership.locks.Add(new FileStream(Path.Combine(directory, "abacus-workspace.lock"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }
                catch (IOException exception)
                {
                    throw new StartupInvariantException($"workspace '{agent.WorkspacePath}' is already owned or cannot be locked: {exception.Message}");
                }
            }
            return ownership;
        }
        catch { ownership.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var handle in locks) handle.Dispose();
        locks.Clear();
    }
}
