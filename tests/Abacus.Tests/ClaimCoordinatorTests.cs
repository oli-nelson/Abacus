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
    public async Task DirtyIssueWorkspaceResumesExactTicketWithoutCleaning()
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

        Assert.Equal("abc-resume", claim.Issue.Id);
        Assert.Equal("abacus/abc-resume", claim.Branch);
        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
        Assert.False(File.Exists(fixture.PathOf("updates")));
        var gitCalls = await fixture.ReadAsync("git-calls");
        Assert.DoesNotContain("reset --hard HEAD", gitCalls, StringComparison.Ordinal);
        Assert.DoesNotContain("clean -fd", gitCalls, StringComparison.Ordinal);
        Assert.Contains("uncommitted workspace changes preserved", fixture.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirtyNonIssueWorkspaceIsPreservedAndAgentHalts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true,
            initialBranch: "feature/manual-work");

        await Assert.ThrowsAsync<AgentHaltedException>(() =>
            fixture.Coordinator.WaitForPreparedClaimAsync(
                fixture.Agent(hasRemote: false),
                singleAgentMode: true,
                CancellationToken.None));

        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
        var gitCalls = await fixture.ReadAsync("git-calls");
        Assert.DoesNotContain("reset --hard HEAD", gitCalls, StringComparison.Ordinal);
        Assert.DoesNotContain("clean -fd", gitCalls, StringComparison.Ordinal);
        Assert.Contains("changes were preserved", fixture.Log.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyIssueWorkspaceIsPreservedWhenTicketCannotBeResumed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true,
            resumeIssueStatus: "blocked");

        await Assert.ThrowsAsync<AgentHaltedException>(() =>
            fixture.Coordinator.WaitForPreparedClaimAsync(
                fixture.Agent(hasRemote: false),
                singleAgentMode: true,
                CancellationToken.None));

        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
        Assert.Contains("not open for recovery", fixture.Log.ToString(), StringComparison.Ordinal);
        Assert.Contains("workspace changes were preserved", fixture.Log.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyIssueWorkspaceIsPreservedWhenTicketBelongsToAnotherAgent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true,
            resumeIssueAssignee: "bob");

        await Assert.ThrowsAsync<AgentHaltedException>(() =>
            fixture.Coordinator.WaitForPreparedClaimAsync(
                fixture.Agent(hasRemote: false),
                singleAgentMode: false,
                CancellationToken.None));

        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
        Assert.Contains("assigned to 'bob'", fixture.Log.ToString(), StringComparison.Ordinal);
        Assert.Contains("workspace changes were preserved", fixture.Log.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanAgentsWaitForInterruptedWorkspacesToClaimBeforeReadyLookup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var barrier = new InitialClaimBarrier(2);
        using var cleanFixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initialClaimBarrier: barrier);
        using var interruptedFixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true,
            initialClaimBarrier: barrier);

        var cleanClaimTask = cleanFixture.Coordinator.WaitForPreparedClaimAsync(
            cleanFixture.Agent(hasRemote: false),
            singleAgentMode: false,
            CancellationToken.None);
        await CoordinatorFixture.WaitUntilAsync(
            async () => await cleanFixture.ReadAsync("status-count") == "1");

        Assert.False(cleanClaimTask.IsCompleted);
        Assert.Equal("0", await cleanFixture.ReadAsync("ready-count"));

        var interruptedClaimTask = interruptedFixture.Coordinator.WaitForPreparedClaimAsync(
            interruptedFixture.Agent(hasRemote: false),
            singleAgentMode: false,
            CancellationToken.None);
        var claims = await Task.WhenAll(cleanClaimTask, interruptedClaimTask);

        Assert.Contains(claims, claim => claim.Issue.Id == "abc-resume");
        Assert.Contains(claims, claim => claim.Issue.Id == "abc-good");
        Assert.Equal("0", await interruptedFixture.ReadAsync("ready-count"));
    }

    [Fact]
    public async Task UnsafeDirtyWorkspaceDoesNotHoldCleanAgentsAtStartupBarrier()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var barrier = new InitialClaimBarrier(2);
        using var cleanFixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initialClaimBarrier: barrier);
        using var unsafeFixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true,
            initialBranch: "feature/manual-work",
            initialClaimBarrier: barrier);

        var cleanClaimTask = cleanFixture.Coordinator.WaitForPreparedClaimAsync(
            cleanFixture.Agent(hasRemote: false),
            singleAgentMode: false,
            CancellationToken.None);
        await CoordinatorFixture.WaitUntilAsync(
            async () => await cleanFixture.ReadAsync("status-count") == "1");

        var unsafeClaimTask = unsafeFixture.Coordinator.WaitForPreparedClaimAsync(
            unsafeFixture.Agent(hasRemote: false),
            singleAgentMode: false,
            CancellationToken.None);

        await Assert.ThrowsAsync<AgentHaltedException>(() => unsafeClaimTask);
        var cleanClaim = await cleanClaimTask;
        Assert.Equal("abc-good", cleanClaim.Issue.Id);
    }

    [Fact]
    public async Task RestartUsesReservedInProgressTicketWithoutAReadyLookupOrNewClaim()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(
            recoverFirstClaim: false,
            initiallyDirty: true,
            resumeIssueStatus: "in_progress",
            resumeIssueAssignee: "alice");

        var claim = await fixture.Coordinator.ResumeReservedClaimAsync(
            fixture.Agent(hasRemote: false),
            new BeadsIssue("abc-resume", IssueStatus.InProgress, "Resume interrupted work", "alice"),
            CancellationToken.None);

        Assert.NotNull(claim);
        Assert.Equal("abc-resume", claim.Issue.Id);
        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
        Assert.False(File.Exists(fixture.PathOf("updates")));
        Assert.DoesNotContain("update abc-resume --claim", fixture.Log.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public async Task PausedClaimsDoNotQueryBeadsUntilResumed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await CoordinatorFixture.CreateAsync(recoverFirstClaim: false);
        fixture.ClaimGate.SetEnabled(false);
        var pendingClaim = fixture.Coordinator.WaitForPreparedClaimAsync(
            fixture.Agent(hasRemote: false),
            singleAgentMode: true,
            CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(25));
        Assert.False(pendingClaim.IsCompleted);
        Assert.Equal("0", await fixture.ReadAsync("ready-count"));
        Assert.Contains("New ticket claims paused", fixture.Log.ToString(), StringComparison.Ordinal);

        fixture.ClaimGate.SetEnabled(true);
        var claim = await pendingClaim;

        Assert.Equal("abc-good", claim.Issue.Id);
        Assert.Equal("2", await fixture.ReadAsync("ready-count"));
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string workspace;
        private readonly string bd;
        private readonly string git;

        private CoordinatorFixture(
            DirectoryInfo root,
            string workspace,
            string bd,
            string git,
            InitialClaimBarrier? initialClaimBarrier)
        {
            this.root = root;
            this.workspace = workspace;
            this.bd = bd;
            this.git = git;
            Log = new StringWriter();
            var runner = new CommandRunner(Log);
            var beads = new Beads(runner, bd);
            var recovery = new TicketRecovery(beads, Log, retryDelay: TimeSpan.Zero);
            ClaimGate = new ClaimGate();
            Coordinator = new ClaimCoordinator(
                beads,
                new Git(runner, git),
                recovery,
                Log,
                TimeSpan.FromMilliseconds(1),
                claimGate: ClaimGate,
                initialClaimBarrier: initialClaimBarrier);
        }

        public ClaimCoordinator Coordinator { get; }
        public ClaimGate ClaimGate { get; }
        public StringWriter Log { get; }

        public static async Task<CoordinatorFixture> CreateAsync(
            bool recoverFirstClaim,
            bool initiallyDirty = false,
            string initialBranch = "abacus/abc-resume",
            string resumeIssueStatus = "open",
            string? resumeIssueAssignee = null,
            InitialClaimBarrier? initialClaimBarrier = null)
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
            if (initiallyDirty)
            {
                await File.WriteAllTextAsync(Path.Combine(root.FullName, "branch"), initialBranch);
            }
            var resumeAssigneeJson = resumeIssueAssignee is null
                ? string.Empty
                : $",\"assignee\":\"{resumeIssueAssignee}\"";
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
                    printf '[{"id":"%s","status":"open"}]\n' "$id"
                  elif test "$count" -eq 1; then
                    printf '[]\n'
                  else
                    printf '[{"id":"abc-good","status":"open"}]\n'
                  fi
                  fi
                elif test "$1" = update; then
                  if test "$3" = --claim; then
                    printf '[{"id":"%s","status":"in_progress","assignee":"%s"}]\n' "$2" "$BEADS_ACTOR"
                  else
                    printf '%s\n' "$*" >> "$root/updates"
                    touch "$root/recovered"
                    printf '[{"id":"abc-bad","status":"open"}]\n'
                  fi
                elif test "$1" = show; then
                  if test "$3" = --children; then
                    printf '{"schema_version":1,"%s":[]}\n' "$2"
                  elif test {{(initiallyDirty ? "1" : "0")}} -eq 1; then
                    printf '[{"id":"abc-resume","status":"{{resumeIssueStatus}}","title":"Resume interrupted work"{{resumeAssigneeJson}}}]\n'
                  elif test -f "$root/recovered"; then
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
                  if test {{(initiallyDirty ? "1" : "0")}} -eq 1; then
                    printf ' M dirty\n'
                  elif test {{(recoverFirstClaim ? "1" : "0")}} -eq 1 && test "$count" -eq 2 && ! test -f "$root/recovered"; then
                    printf ' M dirty\n'
                  fi
                elif test "$3" = reset; then
                  exit 0
                elif test "$3" = clean; then
                  touch "$root/cleaned"
                elif test "$3" = rev-parse; then
                  printf '1111111111111111111111111111111111111111\n'
                elif test "$3" = for-each-ref; then
                  exit 0
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
            return new CoordinatorFixture(root, workspace, bd, git, initialClaimBarrier);
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

        public static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!await condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }

        public void Dispose() => root.Delete(recursive: true);
    }
}
