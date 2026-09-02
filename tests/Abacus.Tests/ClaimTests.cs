using Abacus;

namespace Abacus.Tests;

public sealed class ClaimTests
{
    [Fact]
    public async Task RetriesDoltSerializationFailuresUntilAtomicClaimSucceeds()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-claim-retry-");
        try
        {
            var attempts = Path.Combine(root.FullName, "attempts");
            await File.WriteAllTextAsync(attempts, "0");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                if test "$1" = ready; then
                  printf '[{"id":"abc-retried","status":"open","priority":1,"comment_count":0}]\n'
                  exit 0
                fi
                count=$(cat {{Q(attempts)}})
                count=$((count + 1))
                printf '%s' "$count" > {{Q(attempts)}}
                if test "$count" -lt 3; then
                  printf '{"error":"dolt commit: Error 1213 (40001): serialization failure: try restarting transaction"}\n'
                  exit 1
                fi
                printf '[{"id":"abc-retried","status":"in_progress"}]\n'
                """);
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var claim = await new Beads(new CommandRunner(TextWriter.Null), script)
                .TryClaimReadyAsync(root.FullName, "alice", CancellationToken.None);

            Assert.NotNull(claim);
            Assert.Equal("abc-retried", claim.Id);
            Assert.Equal("3", await File.ReadAllTextAsync(attempts));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DoesNotRetryOrdinaryClaimFailures()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-claim-failure-");
        try
        {
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(
                script,
                "#!/bin/sh\nprintf '{\"error\":\"permission denied\"}\\n'\nexit 1\n");
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var exception = await Assert.ThrowsAsync<BeadsException>(() =>
                new Beads(new CommandRunner(TextWriter.Null), script)
                    .TryClaimReadyAsync(root.FullName, "alice", CancellationToken.None));

            Assert.Contains("permission denied", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

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
                if test "$1" = ready; then
                  if test -s "$root/queue"; then
                    issue=$(cat "$root/queue")
                    printf '[{"id":"%s","status":"open","priority":1,"comment_count":0}]\n' "$issue"
                  else
                    printf '[]\n'
                  fi
                  exit 0
                fi
                if test "$1" != update; then exit 2; fi
                while ! mkdir "$root/lock" 2>/dev/null; do sleep 0.01; done
                trap 'rmdir "$root/lock"' EXIT
                if test -s "$root/queue" && test "$(cat "$root/queue")" = "$2"; then
                  : > "$root/queue"
                  printf '[{"id":"%s","status":"in_progress","assignee":"%s"}]\n' "$2" "$BEADS_ACTOR"
                else
                  printf 'Error claiming %s: issue already claimed by another agent\n' "$2" >&2
                  exit 1
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
                if test "$1" = ready && test "$2" = --unassigned; then
                  printf '[]\n'
                elif test "$1" = ready && test "$2" = --assignee; then
                  printf '[{"id":"abc-retry","status":"open","assignee":"alice","priority":1,"comment_count":0}]\n'
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
                    "ready --unassigned --exclude-label gt:slot --limit 0 --json actor=alice",
                    "ready --assignee alice --exclude-label gt:slot --limit 0 --json actor=alice",
                    "update abc-retry --claim --json actor=alice",
                ],
                invocations);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DispatchFiltersApplyToFreshClaimsAndAssignedReclaims()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-filtered-claim-");
        try
        {
            var calls = Path.Combine(root.FullName, "calls");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                printf '%s actor=%s\n' "$*" "$BEADS_ACTOR" >> {{Q(calls)}}
                printf '[]\n'
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var filters = new DispatchFilters(
                ["abacus-ready", "team:rendering"],
                ["needs-human"],
                "bug,task",
                1);
            var result = await new Beads(new CommandRunner(TextWriter.Null), script)
                .TryClaimReadyAsync(root.FullName, "alice", filters, CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(
                [
                    "ready --unassigned --exclude-label gt:slot --label abacus-ready --label team:rendering --exclude-label needs-human --type bug,task --priority 1 --limit 0 --json actor=alice",
                    "ready --assignee alice --exclude-label gt:slot --label abacus-ready --label team:rendering --exclude-label needs-human --type bug,task --priority 1 --limit 0 --json actor=alice",
                ],
                await File.ReadAllLinesAsync(calls));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ClaimsTheNewestCommentWithinTheHighestPriorityTie()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-comment-claim-");
        try
        {
            var calls = Path.Combine(root.FullName, "calls");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> {{Q(calls)}}
                if test "$1" = ready; then
                  printf '%s\n' '[
                    {"id":"abc-first","status":"open","priority":1,"comment_count":1},
                    {"id":"abc-newest","status":"open","priority":1,"comment_count":2},
                    {"id":"abc-lower-priority","status":"open","priority":2,"comment_count":1}
                  ]'
                elif test "$1" = show; then
                  printf '%s\n' '[
                    {"id":"abc-first","comments":[
                      {"created_at":"2026-09-02T10:00:00Z"}
                    ]},
                    {"id":"abc-newest","comments":[
                      {"created_at":"2026-09-02T09:00:00Z"},
                      {"created_at":"2026-09-02T11:00:00Z"}
                    ]}
                  ]'
                elif test "$1" = update && test "$2" = abc-newest; then
                  printf '[{"id":"abc-newest","status":"in_progress"}]\n'
                else
                  exit 2
                fi
                """);
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await new Beads(new CommandRunner(TextWriter.Null), script)
                .TryClaimReadyAsync(root.FullName, "alice", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("abc-newest", result.Id);
            Assert.Equal(
                [
                    "ready --unassigned --exclude-label gt:slot --limit 0 --json",
                    "show abc-first abc-newest --include-comments --json",
                    "update abc-newest --claim --json",
                ],
                await File.ReadAllLinesAsync(calls));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ClaimsTheFirstHighestPriorityCandidateWhenTheTieHasNoComments()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-first-claim-");
        try
        {
            var calls = Path.Combine(root.FullName, "calls");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> {{Q(calls)}}
                if test "$1" = ready; then
                  printf '%s\n' '[
                    {"id":"abc-first","status":"open","priority":1,"comment_count":0},
                    {"id":"abc-second","status":"open","priority":1,"comment_count":0},
                    {"id":"abc-lower-priority","status":"open","priority":2,"comment_count":3}
                  ]'
                elif test "$1" = update && test "$2" = abc-first; then
                  printf '[{"id":"abc-first","status":"in_progress"}]\n'
                else
                  exit 2
                fi
                """);
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await new Beads(new CommandRunner(TextWriter.Null), script)
                .TryClaimReadyAsync(root.FullName, "alice", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("abc-first", result.Id);
            Assert.Equal(
                [
                    "ready --unassigned --exclude-label gt:slot --limit 0 --json",
                    "update abc-first --claim --json",
                ],
                await File.ReadAllLinesAsync(calls));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ListsAllIssuesNeedingUserAttentionByLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-attention-");
        try
        {
            var calls = Path.Combine(root.FullName, "calls");
            var script = Path.Combine(root.FullName, "bd");
            await File.WriteAllTextAsync(script, $$"""
                #!/bin/sh
                printf '%s actor=%s\n' "$*" "$BEADS_ACTOR" > {{Q(calls)}}
                printf '[{"id":"abc-9","status":"closed","title":"Choose a save format"}]\n'
                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var issues = await new Beads(new CommandRunner(TextWriter.Null), script)
                .GetIssuesNeedingUserAttentionAsync(root.FullName, "alice", CancellationToken.None);

            var issue = Assert.Single(issues);
            Assert.Equal("abc-9", issue.Id);
            Assert.Equal("Choose a save format", issue.Title);
            Assert.Equal(
                "list --label abacus:needs-user-attention --all --limit 0 --json actor=alice",
                (await File.ReadAllTextAsync(calls)).TrimEnd());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string Q(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
