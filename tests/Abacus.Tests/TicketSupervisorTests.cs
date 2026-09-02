using Abacus;

namespace Abacus.Tests;

public sealed class TicketSupervisorTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("open")]
    [InlineData("blocked")]
    public async Task AgentTerminalStatesStopWithoutBeingOverwritten(string status)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await SupervisorFixture.CreateAsync([status]);
        await fixture.SuperviseAsync(hasRemote: false);

        Assert.False(File.Exists(fixture.UpdateCalls));
        Assert.Contains($"send-keys -t {fixture.Run.PaneId} C-c", await File.ReadAllTextAsync(fixture.TmuxCalls));
        var totals = Assert.Single(fixture.Summary.Snapshot().Agents);
        Assert.Equal(status == "closed" ? 1 : 0, totals.Closed);
        Assert.Equal(status == "open" ? 1 : 0, totals.Reopened);
        Assert.Equal(status == "blocked" ? 1 : 0, totals.Blocked);
    }

    [Fact]
    public async Task UnexpectedExitReopensInProgressIssueWithAgentAndExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await SupervisorFixture.CreateAsync(
            ["in_progress", "in_progress", "in_progress"], markerExitCode: 17);
        await fixture.SuperviseAsync(hasRemote: true);

        var update = await File.ReadAllTextAsync(fixture.UpdateCalls);
        Assert.Contains("--status open", update, StringComparison.Ordinal);
        Assert.Contains("alice", update, StringComparison.Ordinal);
        Assert.Contains("17", update, StringComparison.Ordinal);
        Assert.Equal("1", (await File.ReadAllTextAsync(fixture.PushCount)).Trim());
        Assert.Equal(1, Assert.Single(fixture.Summary.Snapshot().Agents).Reopened);
    }

    [Fact]
    public async Task TerminalStatusWinningExitRaceIsNeverOverwritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await SupervisorFixture.CreateAsync(
            ["in_progress", "closed"], markerExitCode: 0);
        await fixture.SuperviseAsync(hasRemote: false);

        Assert.False(File.Exists(fixture.UpdateCalls));
    }

    [Fact]
    public async Task PushFailuresAreVisibleAndRetriedThreeTimes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await SupervisorFixture.CreateAsync(["closed"], pushFailures: 2);
        await fixture.SuperviseAsync(hasRemote: true);

        Assert.Equal("3", (await File.ReadAllTextAsync(fixture.PushCount)).Trim());
        Assert.Contains("attempt 2/3", fixture.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownInterruptsAndReopensInProgressIssue()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await SupervisorFixture.CreateAsync(["in_progress"]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.SuperviseAsync(hasRemote: true, cancellation.Token));

        var update = await File.ReadAllTextAsync(fixture.UpdateCalls);
        Assert.Contains("Abacus shut down", update, StringComparison.Ordinal);
        Assert.Contains("send-keys", await File.ReadAllTextAsync(fixture.TmuxCalls), StringComparison.Ordinal);
        Assert.Equal("1", (await File.ReadAllTextAsync(fixture.PushCount)).Trim());
        Assert.Equal(1, Assert.Single(fixture.Summary.Snapshot().Agents).Interrupted);
    }

    [Fact]
    public async Task ThreeInvalidPollsStopWithoutChangingUnknownTicket()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await SupervisorFixture.CreateAsync(["INVALID", "INVALID", "INVALID"]);

        await Assert.ThrowsAsync<SupervisionException>(() => fixture.SuperviseAsync(hasRemote: false));
        Assert.False(File.Exists(fixture.UpdateCalls));
        Assert.Contains("3/3", fixture.Log.ToString(), StringComparison.Ordinal);
    }

    private sealed class SupervisorFixture : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string bd;
        private readonly string tmux;

        private SupervisorFixture(DirectoryInfo root, string bd, string tmux, TmuxAgentRun run)
        {
            this.root = root;
            this.bd = bd;
            this.tmux = tmux;
            Run = run;
            UpdateCalls = Path.Combine(root.FullName, "update-calls");
            TmuxCalls = Path.Combine(root.FullName, "tmux-calls");
            PushCount = Path.Combine(root.FullName, "push-count");
        }

        public TmuxAgentRun Run { get; }
        public string UpdateCalls { get; }
        public string TmuxCalls { get; }
        public string PushCount { get; }
        public StringWriter Log { get; } = new();
        public RunSummary Summary { get; } = new(["alice"]);

        public static async Task<SupervisorFixture> CreateAsync(
            IReadOnlyList<string> statuses,
            int? markerExitCode = null,
            int pushFailures = 0)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var root = Directory.CreateTempSubdirectory("abacus-supervisor-");
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            var runs = Directory.CreateDirectory(Path.Combine(root.FullName, "runs")).FullName;
            var runDirectory = Directory.CreateDirectory(Path.Combine(runs, "run")).FullName;
            var prompt = Path.Combine(runDirectory, "prompt");
            var wrapper = Path.Combine(runDirectory, "wrapper");
            var marker = Path.Combine(runDirectory, "marker");
            await File.WriteAllTextAsync(prompt, "prompt");
            await File.WriteAllTextAsync(wrapper, "wrapper");
            if (markerExitCode is not null)
            {
                await File.WriteAllTextAsync(marker, $"{markerExitCode}\n");
            }

            var statusesPath = Path.Combine(root.FullName, "statuses");
            await File.WriteAllLinesAsync(statusesPath, statuses);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "push-failures"), pushFailures.ToString());
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "push-count"), "0");

            var bd = Path.Combine(root.FullName, "bd");
            var tmux = Path.Combine(root.FullName, "tmux");
            await File.WriteAllTextAsync(bd, $$"""
                #!/bin/sh
                root={{Q(root.FullName)}}
                if test "$1" = show; then
                  status=$(sed -n '1p' "$root/statuses")
                  sed '1d' "$root/statuses" > "$root/statuses.tmp"
                  mv "$root/statuses.tmp" "$root/statuses"
                  if test "$status" = INVALID; then
                    printf '{invalid\n'
                  elif test "$status" = MISSING || test -z "$status"; then
                    printf '[]\n'
                  else
                    printf '[{"id":"abc-1","status":"%s"}]\n' "$status"
                  fi
                elif test "$1" = update; then
                  printf '%s\n' "$*" >> "$root/update-calls"
                  printf '[{"id":"abc-1","status":"open"}]\n'
                elif test "$1" = dolt && test "$2" = push; then
                  count=$(cat "$root/push-count")
                  count=$((count + 1))
                  printf '%s' "$count" > "$root/push-count"
                  failures=$(cat "$root/push-failures")
                  test "$count" -le "$failures" && { printf 'push failed\n' >&2; exit 1; }
                  exit 0
                else
                  exit 2
                fi
                """);
            await File.WriteAllTextAsync(tmux, $$"""
                #!/bin/sh
                root={{Q(root.FullName)}}
                printf '%s\n' "$*" >> "$root/tmux-calls"
                if test "$1" = display-message; then
                  printf '%s\n' "$4"
                fi
                exit 0
                """);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(bd, mode);
            File.SetUnixFileMode(tmux, mode);

            return new SupervisorFixture(
                root,
                bd,
                tmux,
                new TmuxAgentRun("%9", runDirectory, prompt, wrapper, marker));
        }

        public Task SuperviseAsync(bool hasRemote, CancellationToken cancellationToken = default)
        {
            var runner = new CommandRunner(Log);
            var beads = new Beads(runner, bd);
            var tmuxClient = new TmuxAgentHost(
                runner,
                tmux,
                "/unused/opencode",
                AgentMode.OpenCode,
                "workers",
                Path.Combine(root.FullName, "runs"),
                TimeSpan.Zero);
            var recovery = new TicketRecovery(beads, Log, retryDelay: TimeSpan.Zero);
            var supervisor = new TicketSupervisor(
                beads,
                tmuxClient,
                recovery,
                Log,
                pollingInterval: TimeSpan.FromMilliseconds(1),
                summary: Summary);
            var agent = new ValidatedAgent(
                "alice",
                Path.Combine(root.FullName, "workspace"),
                new DoltIdentity(true, "abc", null, null, true),
                hasRemote);
            return supervisor.SuperviseAsync(
                agent,
                new BeadsIssue("abc-1", IssueStatus.InProgress),
                Run,
                cancellationToken);
        }

        private static string Q(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        public void Dispose() => root.Delete(recursive: true);
    }
}
