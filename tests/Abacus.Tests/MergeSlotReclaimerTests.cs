using Abacus;

namespace Abacus.Tests;

public sealed class MergeSlotReclaimerTests
{
    private static readonly MergeSlotStatus HeldByAliceWithQueue = new(
        Exists: true,
        "abc-merge-slot",
        "alice",
        ["bob", "carol"]);

    [Fact]
    public async Task AbandonedHolderIsReleasedAndItsQueueIsPruned()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture();

        var updated = await fixture.ReclaimAsync(
            HeldByAliceWithQueue,
            configured: ["alice", "bob", "carol"],
            running: []);

        Assert.Equal(
            [
                "merge-slot release --holder alice --json",
                """update abc-merge-slot --metadata {"waiters":[]} --json""",
            ],
            fixture.Calls());
        Assert.Null(updated.Holder);
        Assert.Empty(updated.Queue);
        Assert.Contains("released the Beads merge slot held by alice", fixture.Log, StringComparison.Ordinal);
        Assert.Contains(
            "removed merge-slot waiters that have no running harness",
            fixture.Log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveHolderKeepsTheSlot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture();

        var updated = await fixture.ReclaimAsync(
            HeldByAliceWithQueue,
            configured: ["alice", "bob", "carol"],
            running: ["alice", "bob", "carol"]);

        Assert.Empty(fixture.Calls());
        Assert.Equal(HeldByAliceWithQueue, updated);
    }

    [Fact]
    public async Task HolderFromAnotherRunIsNeverTouched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture();
        var status = new MergeSlotStatus(Exists: true, "abc-merge-slot", "remote-agent", ["remote-waiter"]);

        var updated = await fixture.ReclaimAsync(
            status,
            configured: ["alice", "bob"],
            running: []);

        Assert.Empty(fixture.Calls());
        Assert.Equal(status, updated);
    }

    [Fact]
    public async Task OnlyStaleWaitersLeaveTheQueue()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture();

        var updated = await fixture.ReclaimAsync(
            HeldByAliceWithQueue,
            configured: ["alice", "bob", "carol"],
            running: ["alice", "carol"]);

        Assert.Equal(
            ["""update abc-merge-slot --metadata {"waiters":["carol"]} --json"""],
            fixture.Calls());
        Assert.Equal("alice", updated.Holder);
        Assert.Equal(["carol"], updated.Queue);
        Assert.Contains(
            "removed merge-slot waiters that have no running harness",
            fixture.Log,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentHolderIsDroppedFromTheStoredQueueWithoutAWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture();
        // Beads leaves the holder in the waiter list after it acquires the slot.
        var status = new MergeSlotStatus(Exists: true, "abc-merge-slot", "alice", ["alice", "bob"]);

        var updated = await fixture.ReclaimAsync(
            status,
            configured: ["alice", "bob"],
            running: ["alice", "bob"]);

        Assert.Equal(
            ["""update abc-merge-slot --metadata {"waiters":["bob"]} --json"""],
            fixture.Calls());
        Assert.Equal(["bob"], updated.Queue);
        Assert.DoesNotContain("warning", fixture.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMergeSlotIsLeftAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture();

        var updated = await fixture.ReclaimAsync(
            MergeSlotStatus.NotConfigured,
            configured: ["alice"],
            running: []);

        Assert.Empty(fixture.Calls());
        Assert.False(updated.Exists);
    }

    [Fact]
    public async Task FailedReclaimIsWarnedOnceWithBeadsDetail()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReclaimerFixture(failWrites: true);

        await fixture.ReclaimAsync(
            HeldByAliceWithQueue,
            configured: ["alice", "bob", "carol"],
            running: []);
        await fixture.ReclaimAsync(
            HeldByAliceWithQueue,
            configured: ["alice", "bob", "carol"],
            running: []);

        var warnings = fixture.Log
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(static line => line.Contains("could not release", StringComparison.Ordinal));
        Assert.Equal(1, warnings);
        Assert.Contains("slot held by alice", fixture.Log, StringComparison.Ordinal);
    }

    private sealed class ReclaimerFixture : IDisposable
    {
        private readonly DirectoryInfo root;

        public ReclaimerFixture(bool failWrites = false)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            root = Directory.CreateTempSubdirectory("abacus-merge-reclaim-");
            var script = Path.Combine(root.FullName, "bd");
            File.WriteAllText(script, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> "$PWD/calls"
                case "$*" in
                  "merge-slot release"*)
                    {{(failWrites
                        ? "printf '%s\\n' 'error: slot held by alice, not abacus' >&2; exit 1"
                        : "printf '%s\\n' '{\"released\":true}'")}}
                    ;;
                  *)
                    printf '%s\n' '{"released":true}'
                    ;;
                esac
                """);
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            LogWriter = new StringWriter();
            Beads = new Beads(new CommandRunner(TextWriter.Null), script);
            Reclaimer = new MergeSlotReclaimer(Beads, LogWriter);
        }

        public Beads Beads { get; }

        public MergeSlotReclaimer Reclaimer { get; }

        public StringWriter LogWriter { get; }

        public string Log => LogWriter.ToString();

        public string[] Calls() => File.Exists(Path.Combine(root.FullName, "calls"))
            ? File.ReadAllLines(Path.Combine(root.FullName, "calls"))
            : [];

        public Task<MergeSlotStatus> ReclaimAsync(
            MergeSlotStatus status,
            string[] configured,
            string[] running) =>
            Reclaimer.ReclaimAsync(
                root.FullName,
                configured.ToHashSet(StringComparer.Ordinal),
                running.Contains,
                status,
                CancellationToken.None);

        public void Dispose() => root.Delete(recursive: true);
    }
}
