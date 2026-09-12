using System.Diagnostics;
using System.Text;
using Abacus;

namespace Abacus.Tests;

public sealed partial class EndToEndTests
{
    [Theory]
    [InlineData("--once", "1")]
    [InlineData("--drain", "2")]
    public async Task FiniteExecutionModesExitWithoutCancellation(
        string executionOption,
        string expectedReadyCalls)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-finite-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = DirectStartInfo(root.FullName, bin, workspace, executionOption);
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expectedReadyCalls, await File.ReadAllTextAsync(Path.Combine(root.FullName, "ready-count")));
            Assert.Empty(await stdout);
            var errorText = await stderr;
            Assert.Contains("ABACUS RUN SUMMARY", errorText, StringComparison.Ordinal);
            Assert.Contains("Initial Beads Dolt commit  pjmrvjigiph28prpf6ir4uv0tuv88vnn", errorText, StringComparison.Ordinal);
            Assert.Contains("closed 1", errorText, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReasoningLabelLaunchesAgentWithMappedModel()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-e2e-reasoning-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "--once");
            startInfo.ArgumentList.Add("--reasoning-model");
            startInfo.ArgumentList.Add("high");
            startInfo.ArgumentList.Add("provider/mapped-model#low");
            startInfo.Environment["ABACUS_TEST_REASONING_LABEL"] = ReasoningPolicy.HighLabel;
            using var process = Process.Start(startInfo)!;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            var arguments = await File.ReadAllLinesAsync(Path.Combine(root.FullName, "opencode-arguments"));
            Assert.Contains("provider/mapped-model", arguments);
            Assert.Contains("--variant", arguments);
            Assert.Contains("low", arguments);
            Assert.Contains("closed 1", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReasoningTierArgumentsReplaceDefaultArgumentsForTheAgentCli()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-e2e-arguments-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "--once");
            startInfo.ArgumentList.Add("--extra-args");
            startInfo.ArgumentList.Add("-p openai");
            startInfo.ArgumentList.Add("--reasoning-args");
            startInfo.ArgumentList.Add("high");
            startInfo.ArgumentList.Add("-p deepseek");
            startInfo.Environment["ABACUS_TEST_REASONING_LABEL"] = ReasoningPolicy.HighLabel;
            using var process = Process.Start(startInfo)!;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(
                [
                    "--model", "provider/exact-model",
                    "--variant", "high",
                    "--attach", "http://127.0.0.1:4096",
                    "--dir", workspace,
                    "-p", "deepseek",
                ],
                await File.ReadAllLinesAsync(Path.Combine(root.FullName, "opencode-arguments")));
            Assert.Contains("closed 1", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FiniteRunExitsNonzeroWhenAllPushAttemptsFail()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-push-failure-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "--once");
            startInfo.Environment["ABACUS_TEST_REMOTE"] = "1";
            startInfo.Environment["ABACUS_TEST_PUSH_FAIL"] = "1";
            using var process = Process.Start(startInfo)!;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(1, process.ExitCode);
            Assert.Contains("ATTENTION", await stderr, StringComparison.Ordinal);
            var calls = await File.ReadAllTextAsync(Path.Combine(root.FullName, "bd-calls"));
            Assert.True(
                calls.IndexOf("dolt pull", StringComparison.Ordinal)
                < calls.IndexOf("--readonly vc status --json", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ContinuousRunStopsClaimingAndKeepsAttentionVisibleAfterPushFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-push-attention-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = DirectStartInfo(root.FullName, bin, workspace, executionOption: null);
            startInfo.Environment["ABACUS_TEST_REMOTE"] = "1";
            startInfo.Environment["ABACUS_TEST_PUSH_FAIL"] = "1";
            process = Process.Start(startInfo)!;
            var stderr = process.StandardError.ReadToEndAsync();

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var callsPath = Path.Combine(root.FullName, "bd-calls");
            while (!File.Exists(callsPath)
                   || (await File.ReadAllTextAsync(callsPath, wait.Token))
                       .Split("dolt push", StringSplitOptions.None).Length - 1 < 3)
            {
                await Task.Delay(20, wait.Token);
            }

            Assert.False(process.HasExited);
            Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(root.FullName, "ready-count"), wait.Token));

            await RunAsync("/bin/kill", "-INT", process.Id.ToString());
            await process.WaitForExitAsync(wait.Token);
            Assert.Equal(130, process.ExitCode);
            Assert.Contains("ATTENTION", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CheckModeRunsPreflightWithoutClaimingOrStartingOpenCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-check-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "preflight");
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stdout);
            var errorText = await stderr;
            Assert.Contains("Preflight checks passed", errorText, StringComparison.Ordinal);
            Assert.DoesNotContain("RUN SUMMARY", errorText, StringComparison.Ordinal);
            Assert.DoesNotContain("ready", await File.ReadAllTextAsync(Path.Combine(root.FullName, "bd-calls")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root.FullName, "opencode-actor")));
            Assert.False(File.Exists(Path.Combine(root.FullName, "tmux-calls")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AbandonedMergeSlotClaimIsReleasedWithoutARunningHarness()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-merge-slot-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            // No ready ticket, so no agent harness ever runs during this test.
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "claimed"), string.Empty);
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "merge-slot"),
                """{"available":false,"holder":"alice","id":"abc-merge-slot","waiters":["alice","bob"]}""");

            var startInfo = DirectStartInfo(root.FullName, bin, workspace, executionOption: null);
            process = Process.Start(startInfo)!;
            var errorText = new StringBuilder();
            var stderr = DrainAsync(process.StandardError, errorText);
            var stdout = process.StandardOutput.ReadToEndAsync();
            string Errors()
            {
                lock (errorText)
                {
                    return errorText.ToString();
                }
            }

            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!Errors().Contains("no running harness", StringComparison.Ordinal))
            {
                await Task.Delay(20, wait.Token);
            }

            await RunAsync("/bin/kill", "-INT", process.Id.ToString());
            await process.WaitForExitAsync(wait.Token);
            await stderr;
            var calls = await File.ReadAllTextAsync(
                Path.Combine(root.FullName, "bd-calls"),
                wait.Token);
            Assert.Contains("merge-slot release --holder alice --json", calls, StringComparison.Ordinal);
            Assert.Contains(
                """update abc-merge-slot --metadata {"waiters":["bob"]} --json""",
                calls,
                StringComparison.Ordinal);
            Assert.Contains(
                "released the Beads merge slot held by alice, which has no running harness",
                Errors(),
                StringComparison.Ordinal);
            Assert.Empty(await stdout);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EnabledNoGitOpsExitsBeforeClaimsOrAgentStartup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-no-git-ops-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "--once");
            startInfo.Environment["ABACUS_TEST_NO_GIT_OPS"] = "1";
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(1, process.ExitCode);
            Assert.Empty(await stdout);
            var errorText = await stderr;
            Assert.Contains("Abacus cannot continue", errorText, StringComparison.Ordinal);
            Assert.Contains(Beads.DisableNoGitOpsCommand, errorText, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root.FullName, "ready-count")));
            Assert.False(File.Exists(Path.Combine(root.FullName, "opencode-actor")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProgramRunsOneFakeCliTicketAndCleansUpOnCtrlC()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = new ProcessStartInfo(FindOnPath("dotnet"))
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--tmux-session");
            startInfo.ArgumentList.Add("workers");
            startInfo.ArgumentList.Add("--tmux-window");
            startInfo.ArgumentList.Add("agents");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("provider/exact-model");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add("alice");
            startInfo.ArgumentList.Add(workspace);
            startInfo.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

            process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await WaitForFileAsync(Path.Combine(root.FullName, "pane-cleaned"), TimeSpan.FromSeconds(15));

            await RunAsync("/bin/kill", "-INT", process.Id.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(130, process.ExitCode);
            Assert.Equal("alice", await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-actor")));
            Assert.Equal(Prompt.Render("alice", "abc-1", workspace),
                await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-prompt")));
            Assert.Equal(
                ["--model", "provider/exact-model"],
                await File.ReadAllLinesAsync(Path.Combine(root.FullName, "opencode-arguments")));
            Assert.Contains("ready --unassigned --exclude-label gt:slot --limit 0 --json", await File.ReadAllTextAsync(Path.Combine(root.FullName, "bd-calls")), StringComparison.Ordinal);
            Assert.Contains("send-keys -t %1 C-c", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Contains("split-window -t @1", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Contains("set-option -p -t %1 allow-set-title off", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Contains("select-pane -t %1 -T alice • abc-1", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Contains("select-layout -t @1 tiled", await File.ReadAllTextAsync(Path.Combine(root.FullName, "tmux-calls")), StringComparison.Ordinal);
            Assert.Empty(await stdout);
            Assert.Contains("[alice]", await stderr, StringComparison.Ordinal);
            Assert.Contains("ABACUS RUN SUMMARY", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AttachedServerRunsDirectlyWithoutTmux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("abacus-e2e-direct-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = new ProcessStartInfo(FindOnPath("dotnet"))
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("provider/exact-model");
            startInfo.ArgumentList.Add("--mode");
            startInfo.ArgumentList.Add("opencode-server");
            startInfo.ArgumentList.Add("--opencode-server");
            startInfo.ArgumentList.Add("127.0.0.1:4096");
            startInfo.ArgumentList.Add("-a");
            startInfo.ArgumentList.Add("alice");
            startInfo.ArgumentList.Add(workspace);
            startInfo.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

            process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await WaitForFileAsync(Path.Combine(root.FullName, "direct-finished"), TimeSpan.FromSeconds(15));

            await RunAsync("/bin/kill", "-INT", process.Id.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(130, process.ExitCode);
            Assert.Equal("alice", await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-actor")));
            Assert.Equal(Prompt.Render("alice", "abc-1", workspace),
                await File.ReadAllTextAsync(Path.Combine(root.FullName, "opencode-prompt")));
            Assert.Equal(
                ["--model", "provider/exact-model", "--variant", "high", "--attach", "http://127.0.0.1:4096", "--dir", workspace],
                await File.ReadAllLinesAsync(Path.Combine(root.FullName, "opencode-arguments")));
            Assert.False(File.Exists(Path.Combine(root.FullName, "tmux-calls")));
            Assert.Empty(await stdout);
            // Redirected output has the run summary, not the TUI-only process location.
            Assert.Contains("ABACUS RUN SUMMARY", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process?.Dispose();
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("codex", "gpt-5.6-terra", false)]
    [InlineData("claude", "sonnet", false)]
    [InlineData("claude", "sonnet", true)]
    public async Task InteractiveCodexAndClaudeRunInTmuxWithPromptModelWorkspaceAndActor(
        string mode,
        string model,
        bool remote)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory($"abacus-e2e-{mode}-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "work space's")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);

            var startInfo = new ProcessStartInfo(FindOnPath("dotnet"))
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var startArguments = new List<string>
            {
                typeof(Program).Assembly.Location, "run",
                "--mode", mode,
                "--tmux-session", "workers",
                "--model", model + "#xhigh",
                "--once",
                "-a", "alice", workspace,
            };
            if (remote)
            {
                startArguments.Insert(startArguments.IndexOf("--once"), "--remote-control");
            }

            foreach (var argument in startArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("alice", await File.ReadAllTextAsync(Path.Combine(root.FullName, $"{mode}-actor")));
            Assert.Equal(workspace, await File.ReadAllTextAsync(Path.Combine(root.FullName, $"{mode}-directory")));
            Assert.Equal(
                Prompt.Render("alice", "abc-1", workspace),
                await File.ReadAllTextAsync(Path.Combine(root.FullName, $"{mode}-prompt")));
            var arguments = await File.ReadAllLinesAsync(Path.Combine(root.FullName, $"{mode}-arguments"));
            Assert.Contains(model, arguments);
            if (mode == "codex")
            {
                Assert.Contains("--cd", arguments);
                Assert.Contains(workspace, arguments);
                Assert.Contains("--approve-for-me", arguments);
                Assert.Contains("model_reasoning_effort=xhigh", arguments);
                Assert.DoesNotContain("exec", arguments);
                Assert.DoesNotContain("--remote-control", arguments);
                Assert.DoesNotContain("unix://", arguments);
            }
            else
            {
                Assert.Contains("--permission-mode", arguments);
                Assert.Contains("auto", arguments);
                Assert.Contains("--effort", arguments);
                Assert.Contains("xhigh", arguments);
                Assert.Contains("--name", arguments);
                Assert.Contains("alice • abc-1", arguments);
                Assert.DoesNotContain("--print", arguments);
                Assert.Equal(remote, arguments.Contains("--remote-control"));
                Assert.Equal(remote, arguments.Contains("abc-1 • Implement remote control"));
            }

            Assert.Empty(await stdout);
            Assert.Contains("closed 1", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task WriteFakeToolsAsync(string root, string bin)
    {
        Directory.CreateDirectory(Path.Combine(root, ".abacus"));
        await File.WriteAllTextAsync(Path.Combine(root, ".abacus", "targets.json"),
            """{"version":1,"targets":{"main":{}}}""");
        await File.WriteAllTextAsync(Path.Combine(root, "status"), "open");
        Directory.CreateDirectory(Path.Combine(root, "git-dir"));
        await WriteExecutableAsync(Path.Combine(bin, "bd"), $$$"""
            #!/bin/sh
            root={{{Q(root)}}}
            printf '%s actor=%s\n' "$*" "$BEADS_ACTOR" >> "$root/bd-calls"
            if test "$1" = config && test "$2" = get && test "$3" = no-git-ops; then
              test "$ABACUS_TEST_NO_GIT_OPS" = 1 && value=true || value=false
              printf '{"key":"no-git-ops","location":"config.yaml","schema_version":1,"value":"%s"}\n' "$value"
            elif test "$1" = dolt && test "$2" = show; then
              printf '{"backend":"dolt","data_dir":"/tmp/db","database":"abc","embedded":true,"schema_version":1}\n'
            elif test "$1" = dolt && test "$2" = remote; then
              test "$ABACUS_TEST_REMOTE" = 1 && printf '[{"name":"origin"}]\n' || printf '[]\n'
            elif test "$1" = --readonly && test "$2" = vc && test "$3" = status; then
              printf '{"branch":"main","commit":"pjmrvjigiph28prpf6ir4uv0tuv88vnn","schema_version":1}\n'
            elif test "$1" = ready; then
              if test "$2" = --unassigned; then
                count=0
                test -f "$root/ready-count" && count=$(cat "$root/ready-count")
                count=$((count + 1))
                printf '%s' "$count" > "$root/ready-count"
              fi
              if test "$2" = --unassigned && ! test -f "$root/claimed"; then
                printf '[{"id":"abc-1","title":"Implement remote control","status":"open","priority":1,"comment_count":0}]\n'
              else
                printf '[]\n'
              fi
            elif test "$1" = merge-slot && test "$2" = check; then
              if test -f "$root/merge-slot"; then cat "$root/merge-slot"; else printf '{"available":false,"error":"not found","id":"abc-merge-slot"}\n'; fi
            elif test "$1" = merge-slot && test "$2" = release; then
              rm -f "$root/merge-slot"
              printf '{"id":"abc-merge-slot","released":true}\n'
            elif test "$1" = list; then
              printf '[]\n'
            elif test "$1" = show; then
              if test "$3" = --children; then
                printf '{"schema_version":1,"abc-1":[]}\n'
              else
                status=$(cat "$root/status")
                binding=
                test -f "$root/binding" && binding=",\"abacus_execution\":$(cat "$root/binding")"
                assignee=
                test -f "$root/claimed" && assignee=alice
                labels='[]'
                test -n "$ABACUS_TEST_REASONING_LABEL" && labels="[\"$ABACUS_TEST_REASONING_LABEL\"]"
                printf '[{"id":"abc-1","title":"Implement remote control","status":"%s","assignee":"%s","labels":%s,"metadata":{"abacus_target":"main"%s}}]\n' "$status" "$assignee" "$labels" "$binding"
              fi
            elif test "$1" = update; then
              if test "$2" = abc-merge-slot; then
                printf '[]\n'
              elif test "$3" = --claim; then
                touch "$root/claimed"; printf 'in_progress' > "$root/status"
                printf '[{"id":"abc-1","title":"Implement remote control","status":"in_progress"}]\n'
              elif test "$3" = --metadata; then
                printf '%s' "$4" | sed 's/^{"abacus_execution"://;s/}$//' > "$root/binding"
                printf '[]\n'
              else
                printf 'open' > "$root/status"
                printf '[{"id":"abc-1","status":"open"}]\n'
              fi
            elif test "$1" = dolt && test "$2" = pull; then
              exit 0
            elif test "$1" = dolt && test "$2" = push; then
              test "$ABACUS_TEST_PUSH_FAIL" = 1 && { printf 'push failed\n' >&2; exit 1; }
              exit 0
            else
              exit 2
            fi
            """);
        await WriteExecutableAsync(Path.Combine(bin, "git"), $$$"""
            #!/bin/sh
            root={{{Q(root)}}}
            printf '%s\n' "$*" >> "$root/git-calls"
            if test "$3" = rev-parse; then
              if test "$4" = --verify; then printf '1111111111111111111111111111111111111111\n'
              elif test "$4" = --absolute-git-dir || test "$4" = --path-format=absolute; then printf '%s/git-dir\n' "$root"
              else test "$4" = --show-toplevel && printf '%s\n' "$2" || printf 'true\n'; fi
            elif test "$3" = status; then
              exit 0
            elif test "$3" = merge-base; then
              exit 0
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
        await WriteExecutableAsync(Path.Combine(bin, "opencode"), $$$"""
            #!/bin/sh
            root={{{Q(root)}}}
            direct=0
            if test "$1" = --prompt; then
              shift
              printf '%s' "$1" > "$root/opencode-prompt"
              shift
            elif test "$1" = run; then
              shift
              printf '%s' "$1" > "$root/opencode-prompt"
              shift
              direct=1
            else
              exit 2
            fi
            printf '%s\n' "$@" > "$root/opencode-arguments"
            printf '%s' "$BEADS_ACTOR" > "$root/opencode-actor"
            printf 'closed' > "$root/status"
            test "$direct" -eq 1 && touch "$root/direct-finished"
            exit 0
            """);
        foreach (var agentCli in new[] { "codex", "claude" })
        {
            await WriteExecutableAsync(Path.Combine(bin, agentCli), $$$"""
                #!/bin/sh
                root={{{Q(root)}}}
                cli={{{Q(agentCli)}}}
                printf '%s\n' "$@" > "$root/$cli-arguments"
                prompt=
                for argument do prompt=$argument; done
                printf '%s' "$prompt" > "$root/$cli-prompt"
                printf '%s' "$BEADS_ACTOR" > "$root/$cli-actor"
                pwd | tr -d '\n' > "$root/$cli-directory"
                printf 'closed' > "$root/status"
                exit 0
                """);
        }
        await WriteExecutableAsync(Path.Combine(bin, "tmux"), $$$"""
            #!/bin/sh
            root={{{Q(root)}}}
            printf '%s\n' "$*" >> "$root/tmux-calls"
            if test "$1" = has-session; then
              exit 0
            elif test "$1" = list-windows; then
              printf '@1\t0\tagents\n@2\t1\tAbacus Agents\n'
              exit 0
            elif test "$1" = list-panes; then
              exit 0
            elif test "$1" = split-window; then
              for command do :; done
              /bin/sh -c "$command" >/dev/null 2>&1 &
              printf '%s' "$!" > "$root/pane-pid"
              printf '%%1\n'
            elif test "$1" = select-layout; then
              exit 0
            elif test "$1" = set-option || test "$1" = select-pane; then
              exit 0
            elif test "$1" = display-message; then
              pid=$(cat "$root/pane-pid")
              kill -0 "$pid" 2>/dev/null || { printf "can't find pane: %%1\n" >&2; exit 1; }
              printf '%%1\t0\n'
            elif test "$1" = send-keys; then
              pid=$(cat "$root/pane-pid")
              kill -INT "$pid" 2>/dev/null || true
              touch "$root/pane-cleaned"
            elif test "$1" = respawn-pane && test "$2" = -k; then
              pid=$(cat "$root/pane-pid")
              kill -KILL "$pid" 2>/dev/null || true
              touch "$root/pane-cleaned"
            else
              exit 2
            fi
            """);
    }

    private static async Task WriteExecutableAsync(string path, string contents)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        await File.WriteAllTextAsync(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(20, cancellation.Token);
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder target)
    {
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            lock (target)
            {
                target.Append(buffer, 0, read);
            }
        }
    }

    private static async Task RunAsync(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static ProcessStartInfo DirectStartInfo(
        string root,
        string bin,
        string workspace,
        string? executionOption)
    {
        var startInfo = new ProcessStartInfo(FindOnPath("dotnet"))
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(executionOption == "preflight" ? "preflight" : "run");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("provider/exact-model");
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("opencode-server");
        startInfo.ArgumentList.Add("--opencode-server");
        startInfo.ArgumentList.Add("127.0.0.1:4096");
        if (executionOption is not null and not "preflight")
        {
            startInfo.ArgumentList.Add(executionOption);
        }
        startInfo.ArgumentList.Add("-a");
        startInfo.ArgumentList.Add("alice");
        startInfo.ArgumentList.Add(workspace);
        startInfo.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return startInfo;
    }

    private static string FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"{executable} not found");
    }

    private static string Q(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
