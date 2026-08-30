using Abacus;

namespace Abacus.Tests;

public sealed class ClaimTests
{
    [Fact]
    public async Task ConcurrentClaimsReturnAnIssueToOnlyOneAgent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-claims-");
        try
        {
            var firstWorkspace = Directory.CreateDirectory(Path.Combine(root.FullName, "one")).FullName;
            var secondWorkspace = Directory.CreateDirectory(Path.Combine(root.FullName, "two")).FullName;
            var queue = Path.Combine(root.FullName, "queue");
            await File.WriteAllTextAsync(queue, "abc-123");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, """
                #!/bin/sh
                root=$(dirname "$0")
                if test "$1" != ready; then exit 2; fi
                while ! mkdir "$root/lock" 2>/dev/null; do sleep 0.01; done
                trap 'rmdir "$root/lock"' EXIT
                if test -s "$root/queue"; then
                  issue=$(cat "$root/queue")
                  : > "$root/queue"
                  printf '[{"id":"%s","status":"in_progress","assignee":"%s"}]\n' "$issue" "$BEADS_ACTOR"
                else
                  printf '[]\n'
                fi
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var beads = new Beads(new CommandRunner(TextWriter.Null), script);
            var claims = await Task.WhenAll(
                beads.TryClaimReadyAsync(firstWorkspace, "alice", CancellationToken.None),
                beads.TryClaimReadyAsync(secondWorkspace, "bob", CancellationToken.None));

            Assert.Single(claims, static claim => claim is not null);
            Assert.Equal("abc-123", claims.Single(static claim => claim is not null)!.Id);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task NoReadyWorkIsNotAnError()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-no-claim-");
        try
        {
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '[]\\n'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await new Beads(new CommandRunner(TextWriter.Null), script)
                .TryClaimReadyAsync(root.FullName, "alice", CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReclaimsReadyWorkAlreadyAssignedToTheSameAgent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-reclaim-");
        try
        {
            var calls = Path.Combine(root.FullName, "calls");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                printf '%s actor=%s\n' "$*" "$BEADS_ACTOR" >> {{Q(calls)}}
                if test "$1" = ready && test "$2" = --claim; then
                  printf '[]\n'
                elif test "$1" = ready && test "$2" = --assignee; then
                  printf '[{"id":"abc-retry","status":"open","assignee":"alice"}]\n'
                elif test "$1" = update && test "$2" = abc-retry && test "$3" = --claim; then
                  printf '[{"id":"abc-retry","status":"in_progress","assignee":"alice"}]\n'
                else
                  exit 2
                fi
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await new Beads(new CommandRunner(TextWriter.Null), script)
                .TryClaimReadyAsync(root.FullName, "alice", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("abc-retry", result.Id);
            Assert.Equal(IssueStatus.InProgress, result.Status);
            var invocations = await File.ReadAllLinesAsync(calls);
            Assert.Equal(
                [
                    "ready --claim --exclude-label gt:slot --json actor=alice",
                    "ready --assignee alice --exclude-label gt:slot --json actor=alice",
                    "update abc-retry --claim --json actor=alice",
                ],
                invocations);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string Q(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
