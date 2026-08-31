using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed class GitTests
{
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
    public async Task CleanupDiscardsTrackedAndUntrackedChanges()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var repository = await TemporaryGitRepository.CreateAsync();
        var tracked = Path.Combine(repository.Path, "file.txt");
        var untracked = Path.Combine(repository.Path, "untracked");
        await File.AppendAllTextAsync(tracked, "dirty\n");
        await File.WriteAllTextAsync(untracked, "temporary\n");
        var git = new Git(new CommandRunner(TextWriter.Null), repository.GitExecutable);

        await git.CleanWorkspaceAsync(repository.Path, "alice", CancellationToken.None);

        Assert.Equal("clean\n", await File.ReadAllTextAsync(tracked));
        Assert.False(File.Exists(untracked));
        Assert.True(await git.IsWorkspaceCleanAsync(repository.Path, "alice", CancellationToken.None));
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
            await repository.RunAsync("init", "-q");
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
        {
            var startInfo = new ProcessStartInfo(GitExecutable)
            {
                WorkingDirectory = Path,
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
