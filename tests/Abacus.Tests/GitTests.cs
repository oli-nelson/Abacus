using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed class GitTests
{
    [Fact]
    public async Task WorkspaceStatusReportsActualBranchDetachedHeadAndTrackedOrUntrackedChanges()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = await TemporaryGitRepository.CreateAsync();
        var git = new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable);
        async Task<WorkspaceStatus> Status() => await git.GetWorkspaceStatusAsync(repository.Path, "alice", CancellationToken.None);
        Assert.Equal(new WorkspaceStatus("main", false), await Status());
        var untracked = Path.Combine(repository.Path, "new file\nwith newline");
        await repository.RunAsync("config", "status.showUntrackedFiles", "no");
        await File.WriteAllTextAsync(untracked, "untracked");
        Assert.True((await Status()).IsDirty);
        File.Delete(untracked);
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "file.txt"), "modified");
        Assert.True((await Status()).IsDirty);
        await repository.RunAsync("restore", "file.txt");
        await repository.RunAsync("switch", "--detach");
        var detached = await Status();
        Assert.StartsWith("detached@", detached.Branch);
        Assert.False(detached.IsDirty);
    }

    [Theory]
    [InlineData("printf 'bad data'", false)]
    [InlineData("echo 'ownership failure' >&2; exit 1", false)]
    [InlineData("printf 'refs/heads/abacus/abc-1\\000relative/path\\000\\n'", false)]
    [InlineData("printf 'refs/heads/abacus/abc-1\\000\\000\\n'", true)]
    public async Task OwnershipInspectionRejectsMalformedOrFailedReads(string response, bool allowed)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-git-ownership-");
        try
        {
            var script = Path.Combine(root.FullName, "git");
            await File.WriteAllTextAsync(script, "#!/bin/sh\n" + response + "\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var git = new Git(new CommandRunner(TextWriter.Null), script);
            if (allowed)
                Assert.True(await git.CanUseIssueBranchAsync(root.FullName, "alice", "abc-1", CancellationToken.None));
            else
                await Assert.ThrowsAsync<WorkspacePreparationException>(() =>
                    git.CanUseIssueBranchAsync(root.FullName, "alice", "abc-1", CancellationToken.None));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("abc-123", true)]
    [InlineData("ABC_1.2", true)]
    [InlineData("../main", false)]
    [InlineData("abc/other", false)]
    [InlineData("abc..other", false)]
    [InlineData("abc.lock", false)]
    [InlineData("abc@{1}", false)]
    [InlineData("abc 1", false)]
    [InlineData("", false)]
    public void ValidatesIssueIdsBeforeBranchUse(string issueId, bool expected)
    {
        Assert.Equal(expected, Git.IsValidIssueId(issueId));
    }

    [Theory]
    [InlineData("abacus/abc-123", true, "abc-123")]
    [InlineData("abacus/ABC_1.2", true, "ABC_1.2")]
    [InlineData("main", false, "")]
    [InlineData("abacus/", false, "")]
    [InlineData("abacus/abc/other", false, "")]
    public void ExtractsSafeIssueIdsFromAbacusBranches(
        string branch,
        bool expected,
        string expectedIssueId)
    {
        Assert.Equal(expected, Git.TryGetIssueId(branch, out var issueId));
        Assert.Equal(expectedIssueId, issueId);
    }

    [Fact]
    public async Task CreatesNewAndSwitchesToExistingIssueBranches()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = await TemporaryGitRepository.CreateAsync();
        var git = new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable);

        var newBranch = await git.PrepareIssueBranchAsync(
            repository.Path, "alice", "abc-new", CancellationToken.None);
        Assert.Equal("abacus/abc-new", newBranch);
        Assert.Equal(newBranch, await repository.CurrentBranchAsync());

        await repository.RunAsync("switch", repository.InitialBranch);
        await repository.RunAsync("branch", "abacus/abc-existing");
        var existingBranch = await git.PrepareIssueBranchAsync(
            repository.Path, "alice", "abc-existing", CancellationToken.None);
        Assert.Equal("abacus/abc-existing", existingBranch);
        Assert.Equal(existingBranch, await repository.CurrentBranchAsync());
    }

    [Fact]
    public async Task RefusesIssueBranchCheckedOutInAnotherWorktree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = await TemporaryGitRepository.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var parent = Directory.GetParent(repository.Path)!.FullName;
        var staleWorkspace = Path.Combine(parent, $"abacus-stale-{suffix}");
        var targetWorkspace = Path.Combine(parent, $"abacus-target-{suffix}");
        try
        {
            await repository.RunAsync("branch", "abacus/abc-resume");
            await repository.RunAsync("branch", "worker-target");
            await repository.RunAsync("worktree", "add", staleWorkspace, "abacus/abc-resume");
            await repository.RunAsync("worktree", "add", targetWorkspace, "worker-target");

            var git = new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable);
            Assert.False(await git.CanUseIssueBranchAsync(targetWorkspace, "alice", "abc-resume", CancellationToken.None));
            Assert.True(await git.CanUseIssueBranchAsync(staleWorkspace, "bob", "abc-resume", CancellationToken.None));
            Assert.True(await git.CanUseIssueBranchAsync(targetWorkspace, "alice", "abc-new", CancellationToken.None));
            await Assert.ThrowsAsync<WorkspacePreparationException>(() => git.PrepareIssueBranchAsync(
                targetWorkspace, "alice", "abc-resume", CancellationToken.None));
            Assert.Equal("worker-target", (await repository.RunInAsync(
                targetWorkspace, "branch", "--show-current")).Trim());
            Assert.Equal("abacus/abc-resume", (await repository.RunInAsync(
                staleWorkspace,
                "branch",
                "--show-current")).Trim());
        }
        finally
        {
            await repository.RunAsync("worktree", "remove", "--force", staleWorkspace);
            await repository.RunAsync("worktree", "remove", "--force", targetWorkspace);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopenedCleanTicketCanOnlyBeClaimedInItsOwningWorktree(bool assignedFallback)
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = await TemporaryGitRepository.CreateAsync();
        var other = repository.Path + " other workspace";
        var script = Path.Combine(repository.Path, ".git", "fake-bd");
        var claimLog = Path.Combine(repository.Path, ".git", "claims");
        var state = Path.Combine(repository.Path, ".git", "ticket-status");
        await File.WriteAllTextAsync(state, "blocked");
        static string Q(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        await File.WriteAllTextAsync(script, $$"""
            #!/bin/sh
            state={{Q(state)}}
            if test "$1" = comment; then
              echo '{}'
            elif test "$1" = ready; then
              if test "$(cat "$state")" != open; then echo '[]'; exit 0; fi
              if test {{(assignedFallback ? "1" : "0")}} = 1 && test "$2" = --unassigned; then
                echo '[]'; exit 0
              fi
              echo '[{"id":"abc-resume","title":"Name the root","status":"open"}]'
            elif test "$1" = show && test "$3" = --children; then
              echo '{"schema_version":1,"abc-resume":[]}'
            elif test "$1" = update && test "$3" = --claim; then
              echo "$BEADS_ACTOR" >> {{Q(claimLog)}}
              echo in_progress > "$state"
              printf '[{"id":"abc-resume","status":"in_progress","assignee":"%s"}]\n' "$BEADS_ACTOR"
            elif test "$1" = update; then
              echo open > "$state"
              echo '{"id":"abc-resume","status":"open"}'
            else
              echo "unexpected: $*" >&2; exit 1
            fi
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await repository.RunAsync("switch", "-c", "abacus/abc-resume");
            await repository.RunAsync("worktree", "add", "--detach", other);
            var runner = new CommandRunner(TextWriter.Null);
            var beads = new Beads(runner, script);
            var git = new Git(runner, repository.GitExecutable);
            ClaimCoordinator Coordinator() => new(beads, git, new TicketRecovery(beads, TextWriter.Null), TextWriter.Null);
            ValidatedAgent Agent(string name, string path) => new(name, path,
                new DoltIdentity(true, "abc", null, null, true), false);

            // The naming gate leaves a clean workspace on its issue branch.
            Assert.True(await git.IsWorkspaceCleanAsync(repository.Path, "owner", CancellationToken.None));
            Assert.Null(await Coordinator().WaitForPreparedClaimAsync(Agent("owner", repository.Path),
                false, ExecutionMode.Once, CancellationToken.None));
            await beads.ResolveUserAttentionAsync(repository.Path, "abc-resume", "Root node name: Oak", true, CancellationToken.None);
            // Other agents must never claim/reopen it, even across repeated polls.
            for (var i = 0; i < 3; i++)
                Assert.Null(await Coordinator().WaitForPreparedClaimAsync(Agent("other", other),
                    false, ExecutionMode.Once, CancellationToken.None));
            Assert.False(File.Exists(claimLog));
            Assert.Equal("open", (await File.ReadAllTextAsync(state)).Trim());
            var claim = await Coordinator().WaitForPreparedClaimAsync(Agent("owner", repository.Path),
                false, ExecutionMode.Once, CancellationToken.None);
            Assert.Equal("abc-resume", claim!.Issue.Id);
            Assert.Equal(["owner"], await File.ReadAllLinesAsync(claimLog));
            Assert.Equal("abacus/abc-resume", await repository.CurrentBranchAsync());
            Assert.Equal(string.Empty, (await repository.RunInAsync(other, "branch", "--show-current")).Trim());
            // Releasing a clean branch makes it eligible elsewhere without force.
            await repository.RunAsync("switch", "--detach");
            Assert.True(await git.CanUseIssueBranchAsync(other, "other", "abc-resume", CancellationToken.None));
        }
        finally
        {
            await repository.RunAsync("worktree", "remove", "--force", other);
        }
    }

    [Fact]
    public async Task DirtyWorkspaceNeverCreatesIssueBranch()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = await TemporaryGitRepository.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(repository.Path, "file.txt"), "dirty\n");
        var git = new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable);

        await Assert.ThrowsAsync<WorkspacePreparationException>(() => git.PrepareIssueBranchAsync(
            repository.Path, "alice", "abc-dirty", CancellationToken.None));
        Assert.Equal(repository.InitialBranch, await repository.CurrentBranchAsync());
    }

    [Fact]
    public async Task CleanWorkspaceDiscardsTrackedAndUntrackedChanges()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = await TemporaryGitRepository.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(repository.Path, "file.txt"), "changed\n");
        var untrackedDirectory = Directory.CreateDirectory(Path.Combine(repository.Path, "scratch"));
        await File.WriteAllTextAsync(Path.Combine(untrackedDirectory.FullName, "notes.txt"), "temporary\n");

        await new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable)
            .CleanWorkspaceAsync(repository.Path, "alice", CancellationToken.None);

        Assert.Equal("clean\n", await File.ReadAllTextAsync(Path.Combine(repository.Path, "file.txt")));
        Assert.False(Directory.Exists(untrackedDirectory.FullName));
        Assert.Equal(repository.InitialBranch, await repository.CurrentBranchAsync());
    }

    [Fact]
    public async Task PrunesOnlyClosedTicketBranchesAndSkipsCheckedOutWorktrees()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = await TemporaryGitRepository.CreateAsync();
        var checkedOutWorkspace = System.IO.Path.Combine(
            Directory.GetParent(repository.Path)!.FullName,
            $"abacus-prune-checked-out-{Guid.NewGuid():N}");
        try
        {
            await repository.RunAsync("branch", "abacus/closed-ticket");
            await repository.RunAsync("branch", "abacus/open-ticket");
            await repository.RunAsync("branch", "abacus/checked-out-ticket");
            await repository.RunAsync(
                "worktree",
                "add",
                checkedOutWorkspace,
                "abacus/checked-out-ticket");

            var result = await new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable)
                .PruneClosedIssueBranchesAsync(
                    repository.Path,
                    ["closed-ticket", "checked-out-ticket", "missing-ticket"],
                    CancellationToken.None);

            Assert.Equal(["abacus/closed-ticket"], result.DeletedBranches);
            Assert.Equal(["abacus/checked-out-ticket"], result.SkippedCheckedOutBranches);
            var remainingBranches = await repository.RunAsync("branch", "--format=%(refname:short)");
            Assert.DoesNotContain("abacus/closed-ticket", remainingBranches, StringComparison.Ordinal);
            Assert.Contains("abacus/open-ticket", remainingBranches, StringComparison.Ordinal);
            Assert.Contains("abacus/checked-out-ticket", remainingBranches, StringComparison.Ordinal);
        }
        finally
        {
            await repository.RunAsync("worktree", "remove", "--force", checkedOutWorkspace);
        }
    }

    private sealed class TemporaryGitRepository : IDisposable
    {
        private TemporaryGitRepository(string path, string gitExecutable, string initialBranch)
        {
            Path = path;
            GitExecutable = gitExecutable;
            InitialBranch = initialBranch;
        }

        public string Path { get; }
        public string GitExecutable { get; }
        public string InitialBranch { get; }

        public static async Task<TemporaryGitRepository> CreateAsync()
        {
            var path = Directory.CreateTempSubdirectory("abacus-git-").FullName;
            var git = FindGit();
            var repository = new TemporaryGitRepository(path, git, string.Empty);
            await repository.RunAsync("init", "-q", "--initial-branch=main");
            await repository.RunAsync("config", "user.name", "Abacus Test");
            await repository.RunAsync("config", "user.email", "abacus@example.invalid");
            await File.WriteAllTextAsync(System.IO.Path.Combine(path, "file.txt"), "clean\n");
            await repository.RunAsync("add", "file.txt");
            await repository.RunAsync("commit", "-qm", "initial");
            var initialBranch = (await repository.RunAsync("branch", "--show-current")).Trim();
            return new TemporaryGitRepository(path, git, initialBranch);
        }

        public async Task<string> CurrentBranchAsync() =>
            (await RunAsync("branch", "--show-current")).Trim();

        public async Task<string> RunAsync(params string[] arguments)
            => await RunInAsync(Path, arguments);

        public async Task<string> RunInAsync(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo(GitExecutable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, error);
            return output;
        }

        private static string FindGit()
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = System.IO.Path.Combine(directory, "git");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("git not found");
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
