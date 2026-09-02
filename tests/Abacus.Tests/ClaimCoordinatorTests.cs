using Abacus;

namespace Abacus.Tests;

public sealed class ClaimCoordinatorTests
{
    [Fact]
    public async Task PullFailureDelaysThenNoWorkIsIdleThenClaimSucceeds()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(recoverFirstClaim: false);
        var claim = await fixture.Coordinator.WaitForPreparedClaimAsync(
            fixture.Agent(hasRemote: true),
            singleAgentMode: true,
            CancellationToken.None);

        Assert.Equal("abc-good", claim.Issue.Id);
        Assert.Equal("abacus/abc-good", claim.Branch);
        Assert.Equal("3", await fixture.ReadAsync("pull-count"));
        Assert.Equal("2", await fixture.ReadAsync("ready-count"));
        Assert.Equal(["alice", "alice", "alice"], await File.ReadAllLinesAsync(fixture.PathOf("actors")));
        Assert.Contains("pull failed", fixture.Log.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyClaimIsReopenedAndPushedBeforeNextClaim()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(recoverFirstClaim: true);
        var claim = await fixture.Coordinator.WaitForPreparedClaimAsync(
            fixture.Agent(hasRemote: true),
            singleAgentMode: false,
            CancellationToken.None);

        Assert.Equal("abc-good", claim.Issue.Id);
        Assert.Contains("abc-bad --status open --assignee  --append-notes", await fixture.ReadAsync("updates"), StringComparison.Ordinal);
        Assert.Equal("1", await fixture.ReadAsync("push-count"));
        Assert.False(File.Exists(fixture.PathOf("pull-count")));
    }

    [Fact]
    public async Task DirtyWorkspaceIsCleanedBeforeClaiming()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true);
        var claim = await fixture.Coordinator.WaitForPreparedClaimAsync(
            fixture.Agent(hasRemote: false),
            singleAgentMode: true,
            CancellationToken.None);

        Assert.Equal("abc-good", claim.Issue.Id);
        Assert.Equal("2", await fixture.ReadAsync("ready-count"));
        Assert.False(File.Exists(fixture.PathOf("updates")));
        var gitCalls = await fixture.ReadAsync("git-calls");
        Assert.Contains("reset --hard HEAD", gitCalls, StringComparison.Ordinal);
        Assert.Contains("clean -fd", gitCalls, StringComparison.Ordinal);
        Assert.Contains("workspace is dirty; discarding local changes", fixture.Log.ToString(), StringComparison.Ordinal);
        Assert.Contains("workspace cleaned; continuing claims", fixture.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiniteModeReturnsImmediatelyWhenNoWorkIsReady()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(recoverFirstClaim: false);
        var claim = await fixture.Coordinator.WaitForPreparedClaimAsync(
            fixture.Agent(hasRemote: false),
            singleAgentMode: true,
            ExecutionMode.Drain,
            CancellationToken.None);

        Assert.Null(claim);
        Assert.Equal("1", await fixture.ReadAsync("ready-count"));
        Assert.Contains("finite run is complete", fixture.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FiniteModeFailsFastWhenPullFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(recoverFirstClaim: false);
        await Assert.ThrowsAsync<BeadsException>(() => fixture.Coordinator.WaitForPreparedClaimAsync(
            fixture.Agent(hasRemote: true),
            singleAgentMode: true,
            ExecutionMode.Once,
            CancellationToken.None));

        Assert.Equal("1", await fixture.ReadAsync("pull-count"));
        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string workspace;
        private readonly string bd;
        private readonly string git;

        private CoordinatorFixture(DirectoryInfo root, string workspace, string bd, string git)
        {
            this.root = root;
            this.workspace = workspace;
            this.bd = bd;
            this.git = git;
            Log = new StringWriter();
            var runner = new CommandRunner(Log);
            var beads = new Beads(runner, bd);
            var recovery = new TicketRecovery(beads, Log, retryDelay: TimeSpan.Zero);
            Coordinator = new ClaimCoordinator(
                beads,
                new Git(runner, git),
                recovery,
                Log,
                TimeSpan.FromMilliseconds(1));
        }

        public ClaimCoordinator Coordinator { get; }
        public StringWriter Log { get; }

        public static async Task<CoordinatorFixture> CreateAsync(
            bool recoverFirstClaim,
            bool initiallyDirty = false)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var root = Directory.CreateTempSubdirectory("abacus-coordinator-");
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            var bd = Path.Combine(root.FullName, "bd");
            var git = Path.Combine(root.FullName, "git");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "pull-count"), recoverFirstClaim ? string.Empty : "0");
            if (recoverFirstClaim)
            {
                File.Delete(Path.Combine(root.FullName, "pull-count"));
            }

            await File.WriteAllTextAsync(Path.Combine(root.FullName, "ready-count"), "0");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "push-count"), "0");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "status-count"), "0");
            await File.WriteAllTextAsync(bd, $$"""
                #!/bin/sh
                root={{Q(root.FullName)}}
                if test "$1" = dolt && test "$2" = pull; then
                  count=0; test -f "$root/pull-count" && count=$(cat "$root/pull-count")
                  count=$((count + 1)); printf '%s' "$count" > "$root/pull-count"
                  test "$count" -eq 1 && { printf 'pull failed\n' >&2; exit 1; }
                  exit 0
                elif test "$1" = ready; then
                  printf '%s\n' "$BEADS_ACTOR" >> "$root/actors"
                  if test "$2" = --assignee; then
                    printf '[]\n'
                  else
                    count=$(cat "$root/ready-count"); count=$((count + 1)); printf '%s' "$count" > "$root/ready-count"
                  if test {{(recoverFirstClaim ? "1" : "0")}} -eq 1; then
                    test "$count" -eq 1 && id=abc-bad || id=abc-good
                    printf '[{"id":"%s","status":"in_progress"}]\n' "$id"
                  elif test "$count" -eq 1; then
                    printf '[]\n'
                  else
                    printf '[{"id":"abc-good","status":"in_progress"}]\n'
                  fi
                  fi
                elif test "$1" = update; then
                  printf '%s\n' "$*" >> "$root/updates"
                  touch "$root/recovered"
                  printf '[{"id":"abc-bad","status":"open"}]\n'
                elif test "$1" = show; then
                  if test -f "$root/recovered"; then
                    printf '[{"id":"abc-bad","status":"open"}]\n'
                  else
                    printf '[{"id":"abc-bad","status":"in_progress"}]\n'
                  fi
                elif test "$1" = dolt && test "$2" = push; then
                  count=$(cat "$root/push-count"); count=$((count + 1)); printf '%s' "$count" > "$root/push-count"
                  exit 0
                else
                  exit 2
                fi
                """);
            await File.WriteAllTextAsync(git, $$"""
                #!/bin/sh
                root={{Q(root.FullName)}}
                printf '%s\n' "$*" >> "$root/git-calls"
                if test "$3" = status; then
                  count=$(cat "$root/status-count"); count=$((count + 1)); printf '%s' "$count" > "$root/status-count"
                  if test {{(initiallyDirty ? "1" : "0")}} -eq 1 && ! test -f "$root/cleaned"; then
                    printf ' M dirty\n'
                  elif test {{(recoverFirstClaim ? "1" : "0")}} -eq 1 && test "$count" -eq 2 && ! test -f "$root/recovered"; then
                    printf ' M dirty\n'
                  fi
                elif test "$3" = reset; then
                  exit 0
                elif test "$3" = clean; then
                  touch "$root/cleaned"
                elif test "$3" = show-ref; then
                  exit 1
                elif test "$3" = switch; then
                  test "$4" = -c && printf '%s' "$5" > "$root/branch" || printf '%s' "$4" > "$root/branch"
                elif test "$3" = branch; then
                  cat "$root/branch"
                else
                  exit 2
                fi
                """);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(bd, mode);
            File.SetUnixFileMode(git, mode);
            return new CoordinatorFixture(root, workspace, bd, git);
        }

        public ValidatedAgent Agent(bool hasRemote) => new(
            "alice",
            workspace,
            new DoltIdentity(true, "abc", null, null, true),
            hasRemote);

        public string PathOf(string name) => Path.Combine(root.FullName, name);

        public async Task<string> ReadAsync(string name) =>
            (await File.ReadAllTextAsync(PathOf(name))).Trim();

        private static string Q(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        public void Dispose() => root.Delete(recursive: true);
    }
}
