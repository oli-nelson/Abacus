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
}
