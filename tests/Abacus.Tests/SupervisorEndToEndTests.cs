using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed partial class EndToEndTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task FiniteSupervisorRepairsAttentionOrClaimErrorsAndVerifiesRetry(bool claimError, bool repair)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-e2e-supervisor-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var blocked = Path.Combine(root.FullName, "supervisor-blocker");
            await File.WriteAllTextAsync(blocked, "blocked");
            var bd = Path.Combine(bin, "bd");
            var script = await File.ReadAllTextAsync(bd);
            script = script.Replace("#!/bin/sh", $$"""
                #!/bin/sh
                if test "$1" = where; then
                  printf '{"path":"%s/.beads"}' {{Q(root.FullName)}}; exit 0
                fi
                if test "$1" = ready && test -f {{Q(blocked)}} && test {{(claimError ? "1" : "0")}} = 1; then
                  echo 'claim fixture error' >&2; exit 1
                fi
                if test "$1" = list && test "$3" = abacus:needs-user-attention && test -f {{Q(blocked)}} && test {{(claimError ? "1" : "0")}} = 0; then
                  echo '[{"id":"attention-1","status":"blocked","title":"Maintenance needed","labels":["abacus:needs-user-attention"]}]'; exit 0
                fi
                """);
            await File.WriteAllTextAsync(bd, script);
            var opencode = Path.Combine(bin, "opencode");
            script = await File.ReadAllTextAsync(opencode);
            script = script.Replace("#!/bin/sh", $$"""
                #!/bin/sh
                if test "$BEADS_ACTOR" = maintenance; then
                  printf '%s\n' "$PWD" > {{Q(Path.Combine(root.FullName, "supervisor-directory"))}}
                  printf '%s\n' "$2" > {{Q(Path.Combine(root.FullName, "supervisor-prompt"))}}
                  echo run >> {{Q(Path.Combine(root.FullName, "supervisor-starts"))}}
                  completion=$(printf '%s\n' "$2" | sed -n 's/^write a UTF-8 JSON object to this absolute path: "\(.*\)"$/\1/p')
                  id=$(printf '%s\n' "$2" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p' | tail -1)
                  test {{(repair ? "1" : "0")}} = 1 && rm -f {{Q(blocked)}}
                  printf '{"runId":"%s","summary":"fixture maintenance"}' "$id" > "$completion"
                  exit 0
                fi
                """);
            await File.WriteAllTextAsync(opencode, script);
            var start = DirectStartInfo(root.FullName, bin, workspace, "--drain");
            start.ArgumentList.Add("--supervisor-model");
            start.ArgumentList.Add("provider/maintenance");
            process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await process.WaitForExitAsync(timeout.Token);
            var logs = await stderr;
            Assert.True(repair ? process.ExitCode == 0 : process.ExitCode != 0, logs);
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(root.FullName, "supervisor-starts")));
            Assert.Equal(root.Name, Path.GetFileName((await File.ReadAllTextAsync(Path.Combine(root.FullName, "supervisor-directory"))).Trim()));
            Assert.Contains("Last run:", logs);
            if (claimError) Assert.Contains("claim fixture error", await File.ReadAllTextAsync(Path.Combine(root.FullName, "supervisor-prompt")));
            if (repair) Assert.Contains("closed 1", logs);
            else Assert.Contains("supervisor retry failed", logs);
            Assert.Empty(await stdout);
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
            process?.Dispose();
            root.Delete(true);
        }
    }
}
