using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed partial class EndToEndTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContinuationCreatesWorkBeforeFiniteExitAndFreshProcessesCanPlanAgain(bool alsoMaintenance, bool createsWork)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-e2e-continuation-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var legacyPath = Path.Combine(root.FullName, "git-dir", "abacus", "continuation.json");
            var legacyContent = alsoMaintenance ? "{malformed legacy state" : "{\"version\":1,\"armed\":false}";
            if (!createsWork)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
                await File.WriteAllTextAsync(legacyPath, legacyContent);
            }
            var created = Path.Combine(root.FullName, "planned");
            var starts = Path.Combine(root.FullName, "continuation-starts");
            var bd = Path.Combine(bin, "bd");
            var script = await File.ReadAllTextAsync(bd);
            script = script.Replace("#!/bin/sh", $$"""
                #!/bin/sh
                if test "$1" = where; then printf '{"path":"%s/.beads"}' {{Q(root.FullName)}}; exit 0; fi
                if test "$1" = ready && ! test -f {{Q(created)}}; then echo '[]'; exit 0; fi
                if test "$1" = list && test "$2" = --type && test "$3" = epic; then
                  if test -f {{Q(created)}} && test "$(cat {{Q(Path.Combine(root.FullName, "status"))}})" != closed; then
                    echo '[{"id":"planned-epic","status":"open"}]'
                  else echo '[]'; fi
                  exit 0
                fi
                """);
            await File.WriteAllTextAsync(bd, script);
            var opencode = Path.Combine(bin, "opencode");
            script = await File.ReadAllTextAsync(opencode);
            script = script.Replace("#!/bin/sh", $$"""
                #!/bin/sh
                if test "$BEADS_ACTOR" = continuation; then
                  echo run >> {{Q(starts)}}
                  printf '%s' "$2" > {{Q(Path.Combine(root.FullName, "continuation-prompt"))}}
                  test {{(createsWork ? "1" : "0")}} = 1 && touch {{Q(created)}}
                  completion=$(printf '%s\n' "$2" | sed -n 's/^write a UTF-8 JSON object to this absolute path: "\(.*\)"$/\1/p')
                  id=$(printf '%s\n' "$2" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p' | tail -1)
                  printf '{"runId":"%s","summary":"created planned-epic"}' "$id" > "$completion"
                  exit 0
                fi
                """);
            await File.WriteAllTextAsync(opencode, script);
            var policy = Path.Combine(root.FullName, "plan.md");
            await File.WriteAllTextAsync(policy, "Create one useful epic from the approved specification.");
            for (var attempt = 0; attempt < (createsWork ? 1 : 2); attempt++)
            {
                var start = DirectStartInfo(root.FullName, bin, workspace, "--drain");
                start.ArgumentList.Add("--continuation-model"); start.ArgumentList.Add("provider/planner");
                start.ArgumentList.Add("--continuation-prompt-file"); start.ArgumentList.Add(policy);
                if (alsoMaintenance)
                {
                    start.ArgumentList.Add("--supervisor-model"); start.ArgumentList.Add("provider/repair");
                }
                process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token);
                Assert.True(process.ExitCode == 0, await stderr);
                Assert.Empty(await stdout);
                process.Dispose(); process = null;
                if (createsWork) Assert.False(File.Exists(legacyPath));
                else Assert.Equal(legacyContent, await File.ReadAllTextAsync(legacyPath));

            }
            Assert.Equal(createsWork ? 1 : 2, (await File.ReadAllLinesAsync(starts)).Length);
            Assert.Equal(createsWork ? "closed" : "open", await File.ReadAllTextAsync(Path.Combine(root.FullName, "status")));
            var prompt = await File.ReadAllTextAsync(Path.Combine(root.FullName, "continuation-prompt"));
            Assert.Contains("Create one useful epic", prompt);
            Assert.Contains("merge --ff-only", prompt);
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
            process?.Dispose(); root.Delete(true);
        }
    }
}
