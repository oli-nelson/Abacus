using Abacus;

namespace Abacus.Tests;

public sealed class ProjectInfoTests
{
    [Fact]
    public async Task CollectsAndRendersGitDoltTicketAndPolicyInformation()
    {
        if (OperatingSystem.IsWindows()) return;

        using var environment = await ProjectInfoEnvironment.CreateAsync(beadsAvailable: true);
        var report = await environment.CollectAsync();
        var rendered = report.Render();

        Assert.True(report.IsComplete);
        Assert.Equal("main", report.Workspace.Branch);
        Assert.True(report.Workspace.IsDirty);
        Assert.Equal("git@example.test:team/project.git", report.Origin);
        Assert.Equal(2, report.Worktrees.Count);
        Assert.Equal("project_tasks", report.DoltIdentity!.Database);
        Assert.Equal("db.internal:3307/project_tasks", report.DoltIdentity.SharedKey);
        Assert.True(report.DoltRemoteConfigured);
        Assert.Equal("dolt-commit-123", report.DoltCommit);
        Assert.Equal(new TicketSummary(4, 1, 1, 1, 1, 0, 1), report.Tickets);
        Assert.Equal(["main", "release/1.0"], report.Targets!.Targets.Keys.Order(StringComparer.Ordinal));
        Assert.True(report.Reasoning!.EnforceLabels);
        Assert.Empty(report.Errors);

        Assert.Contains("Abacus project info", rendered, StringComparison.Ordinal);
        Assert.Contains("Storage:           shared server", rendered, StringComparison.Ordinal);
        Assert.Contains("Server:            db.internal:3307", rendered, StringComparison.Ordinal);
        Assert.Contains("Dolt commit:       dolt-commit-123", rendered, StringComparison.Ordinal);
        Assert.Contains("Needs attention:   1", rendered, StringComparison.Ordinal);
        Assert.Contains("Targets:           main, release/1.0", rendered, StringComparison.Ordinal);
        Assert.Contains("Reasoning labels:  required", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsUnavailableBeadsDataWithoutHidingGitAndConfiguration()
    {
        if (OperatingSystem.IsWindows()) return;

        using var environment = await ProjectInfoEnvironment.CreateAsync(beadsAvailable: false);
        var report = await environment.CollectAsync();
        var rendered = report.Render();

        Assert.False(report.IsComplete);
        Assert.Null(report.DoltIdentity);
        Assert.Null(report.Tickets);
        Assert.Single(report.Errors);
        Assert.Contains("Project", rendered, StringComparison.Ordinal);
        Assert.Contains(environment.Root, rendered, StringComparison.Ordinal);
        Assert.Contains("Database:          unavailable", rendered, StringComparison.Ordinal);
        Assert.Contains("Unavailable data", rendered, StringComparison.Ordinal);
        Assert.Contains("query Beads Dolt configuration", rendered, StringComparison.Ordinal);
    }

    private sealed class ProjectInfoEnvironment : IDisposable
    {
        private readonly DirectoryInfo directory;
        private readonly string git;
        private readonly string beads;

        private ProjectInfoEnvironment(DirectoryInfo directory, string git, string beads)
        {
            this.directory = directory;
            this.git = git;
            this.beads = beads;
        }

        public string Root => Path.Combine(directory.FullName, "sample-project");

        public static async Task<ProjectInfoEnvironment> CreateAsync(bool beadsAvailable)
        {
            var directory = Directory.CreateTempSubdirectory("abacus-info-");
            var root = Directory.CreateDirectory(Path.Combine(directory.FullName, "sample-project")).FullName;
            Directory.CreateDirectory(Path.Combine(root, ".abacus"));
            await File.WriteAllTextAsync(Path.Combine(root, ".abacus", "targets.json"),
                """{"version":1,"enforceTargetBranch":false,"defaultTarget":"main","targets":{"main":{},"release/1.0":{}}}""");
            await File.WriteAllTextAsync(Path.Combine(root, ".abacus", "reasoning.json"),
                """{"version":1,"enforceLabels":true}""");

            var bin = Directory.CreateDirectory(Path.Combine(directory.FullName, "bin")).FullName;
            var git = await WriteExecutableAsync(bin, "git", """
                if [ "$3" = "--no-optional-locks" ]; then
                  printf '# branch.oid aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\0# branch.head main\0? changed.txt\0'
                elif [ "$3 $4" = "rev-parse HEAD" ]; then
                  printf 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n'
                elif [ "$3 $4 $5" = "worktree list --porcelain" ]; then
                  printf 'worktree %s\nHEAD aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nbranch refs/heads/main\n\n' "$2"
                  printf 'worktree %s/wt\nHEAD bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\ndetached\n\n' "$2"
                elif [ "$3 $4 $5" = "remote get-url origin" ]; then
                  printf 'git@example.test:team/project.git\n'
                else
                  printf 'unexpected git arguments\n' >&2
                  exit 2
                fi
                """);
            var beads = await WriteExecutableAsync(bin, "bd", beadsAvailable ? """
                if [ "$1 $2" = "dolt show" ]; then
                  printf '{"embedded":false,"database":"project_tasks","host":"db.internal","port":3307,"connection_ok":true}\n'
                elif [ "$1 $2 $3 $4" = "dolt remote list --json" ]; then
                  printf '[{"name":"origin"}]\n'
                elif [ "$1 $2 $3 $4" = "--readonly vc status --json" ]; then
                  printf '{"commit":"dolt-commit-123"}\n'
                elif [ "$1" = "list" ]; then
                  printf '[{"id":"ab-1","status":"open","labels":[]},{"id":"ab-2","status":"in_progress","labels":[]},{"id":"ab-3","status":"blocked","labels":["abacus:needs-user-attention"]},{"id":"ab-4","status":"closed","labels":[]}]\n'
                else
                  printf 'unexpected bd arguments\n' >&2
                  exit 2
                fi
                """ : """
                printf 'no beads database found\n' >&2
                exit 1
                """);

            return new ProjectInfoEnvironment(directory, git, beads);
        }

        public Task<ProjectInfoReport> CollectAsync() =>
            new ProjectInfoCollector(new CommandRunner(TextWriter.Null), git, beads)
                .CollectAsync(Root, CancellationToken.None);

        public void Dispose() => directory.Delete(recursive: true);

        private static async Task<string> WriteExecutableAsync(string directory, string name, string body)
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return path;
        }
    }
}
