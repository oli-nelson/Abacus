using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed class TmuxAgentHostTests
{
    [Fact]
    public async Task WrapperPassesPromptModelAttachDirectoryAndActorAndWritesMarker()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "work space's")).FullName;
        var agent = Agent("alice", workspace);
        var tmux = fixture.CreateTmux(mode: AgentMode.OpenCodeServer);

        var run = await tmux.StartAgentAsync(
            agent,
            new BeadsIssue("abc-123", IssueStatus.InProgress),
            "provider/model",
            "xhigh",
            "http://127.0.0.1:1234",
            CancellationToken.None);

        var startInfo = new ProcessStartInfo(run.WrapperPath)
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var wrapper = Process.Start(startInfo)!;
        await WaitForFileAsync(run.MarkerPath);

        Assert.Equal(7, run.TryReadExitCode());
        Assert.Equal(Prompt.Render("alice", "abc-123", workspace),
            await File.ReadAllTextAsync(Path.Combine(workspace, "received-prompt")));
        Assert.Equal("alice", await File.ReadAllTextAsync(Path.Combine(workspace, "received-actor")));
        Assert.Equal(workspace, await File.ReadAllTextAsync(Path.Combine(workspace, "received-directory")));
        Assert.Equal(
            ["--model", "provider/model", "--variant", "xhigh", "--attach", "http://127.0.0.1:1234", "--dir", workspace],
            await File.ReadAllLinesAsync(Path.Combine(workspace, "received-arguments")));
        Assert.Equal("visible agent output", await wrapper.StandardOutput.ReadLineAsync());

        wrapper.Kill(entireProcessTree: true);
        await wrapper.WaitForExitAsync();
    }

    [Fact]
    public async Task LocalAndAttachedRunsUseDistinctPanesAndSameRequestedModel()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var firstWorkspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "one")).FullName;
        var secondWorkspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "two")).FullName;
        var localTmux = fixture.CreateTmux(mode: AgentMode.OpenCode);
        var attachedTmux = fixture.CreateTmux(mode: AgentMode.OpenCodeServer);

        var local = await localTmux.StartAgentAsync(
            Agent("alice", firstWorkspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/exact-model", "high", null, CancellationToken.None);
        var attached = await attachedTmux.StartAgentAsync(
            Agent("bob", secondWorkspace),
            new BeadsIssue("abc-2", IssueStatus.InProgress),
            "provider/exact-model", "high", "http://server:1234", CancellationToken.None);

        Assert.NotEqual(local.PaneId, attached.PaneId);
        var localWrapper = await File.ReadAllTextAsync(local.WrapperPath);
        var attachedWrapper = await File.ReadAllTextAsync(attached.WrapperPath);
        Assert.Contains("'--prompt' \"$prompt\" '--model' 'provider/exact-model'", localWrapper, StringComparison.Ordinal);
        Assert.Contains("'--model' 'provider/exact-model'", attachedWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'run'", localWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'--mini'", localWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'--mini'", attachedWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'--attach'", localWrapper, StringComparison.Ordinal);
        Assert.Contains("'run' \"$prompt\" '--model' 'provider/exact-model' '--variant' 'high' '--attach' 'http://server:1234'", attachedWrapper, StringComparison.Ordinal);
        Assert.NotEqual(local.RunDirectory, attached.RunDirectory);
    }

    [Fact]
    public async Task CodexWrapperUsesInteractiveTuiWithWorkspaceAndNonBlockingPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "codex work space's")).FullName;
        var tmux = fixture.CreateTmux(mode: AgentMode.Codex);

        var run = await tmux.StartAgentAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "gpt-5.6-terra",
            "high",
            null,
            CancellationToken.None);

        var wrapper = await File.ReadAllTextAsync(run.WrapperPath);
        Assert.Contains($"'--cd' {TmuxAgentHost.ShellQuote(workspace)}", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--model' 'gpt-5.6-terra'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--config' 'model_reasoning_effort=high'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--approve-for-me' \"$prompt\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'exec'", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaudeWrapperUsesInteractiveSessionWithStableNameAndAutoPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "claude")).FullName;
        var tmux = fixture.CreateTmux(mode: AgentMode.Claude);

        var run = await tmux.StartAgentAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "sonnet",
            "high",
            null,
            CancellationToken.None);

        var wrapper = await File.ReadAllTextAsync(run.WrapperPath);
        Assert.Contains("'--model' 'sonnet'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--effort' 'high'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--permission-mode' 'auto'", wrapper, StringComparison.Ordinal);
        Assert.Contains("'--name' 'alice • abc-1' \"$prompt\"", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("'--print'", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteClaudeNamesRemoteSessionAfterIssueIdAndTitle()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "claude-remote")).FullName;
        var tmux = fixture.CreateTmux(mode: AgentMode.Claude, remote: true);

        var run = await tmux.StartAgentAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress, "  Add   remote\ncontrol  "),
            "opus",
            "high",
            null,
            CancellationToken.None);

        var wrapper = await File.ReadAllTextAsync(run.WrapperPath);
        Assert.Contains(
            "'--remote-control' 'abc-1 • Add remote control'",
            wrapper,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpecifiedWindowIsUsedAsTheSplitTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var tmux = fixture.CreateTmux(window: "agents");

        await tmux.StartAgentAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/model", "high", null, CancellationToken.None);

        Assert.Contains(
            await File.ReadAllLinesAsync(fixture.CallsPath),
            static call => call.StartsWith("split-window -t workers:agents ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PaneTitleIdentifiesAgentAndIssueAndCannotBeOverwrittenByAgentCli()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var tmux = fixture.CreateTmux();

        var run = await tmux.StartAgentAsync(
            Agent("alice #{pane_id}", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/model", "high", null, CancellationToken.None);

        var calls = await File.ReadAllLinesAsync(fixture.CallsPath);
        Assert.Contains($"set-option -p -t {run.PaneId} allow-set-title off", calls);
        Assert.Contains($"select-pane -t {run.PaneId} -T alice ##{{pane_id}} • abc-1", calls);
    }

    [Fact]
    public async Task RequestedLayoutIsAppliedToTheSpecifiedWindow()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var tmux = fixture.CreateTmux(window: "agents", layout: "tiled");

        await tmux.StartAgentAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/model", "high", null, CancellationToken.None);

        Assert.Contains(
            "select-layout -t workers:agents tiled",
            await File.ReadAllLinesAsync(fixture.CallsPath));
    }

    [Fact]
    public async Task CleanupIsIdempotentAndTargetsOnlyTheRecordedPane()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await TmuxFixture.CreateAsync();
        var workspace = Directory.CreateDirectory(Path.Combine(fixture.Root, "workspace")).FullName;
        var tmux = fixture.CreateTmux(TimeSpan.Zero);
        var run = await tmux.StartAgentAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/model", "high", null, CancellationToken.None);

        await File.WriteAllTextAsync(run.MarkerPath, "1\n");
        await tmux.StopAndCleanupAsync(run, CancellationToken.None);
        await tmux.StopAndCleanupAsync(run, CancellationToken.None);

        var calls = await File.ReadAllLinesAsync(fixture.CallsPath);
        Assert.Single(calls, line => line == $"send-keys -t {run.PaneId} C-c");
        Assert.Single(calls, line => line == $"kill-pane -t {run.PaneId}");
        Assert.False(File.Exists(run.PromptPath));
        Assert.False(File.Exists(run.WrapperPath));
        Assert.False(File.Exists(run.MarkerPath));
        Assert.False(Directory.Exists(run.RunDirectory));
    }

    private static ValidatedAgent Agent(string name, string workspace) =>
        new(name, workspace, new DoltIdentity(true, "abc", null, null, true), HasRemote: false);

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 500 && !File.Exists(path); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(path), $"Timed out waiting for {path}");
    }

    private sealed class TmuxFixture : IDisposable
    {
        private TmuxFixture(DirectoryInfo root, string tmuxPath, string agentExecutablePath)
        {
            Directory = root;
            TmuxPath = tmuxPath;
            AgentExecutablePath = agentExecutablePath;
            CallsPath = Path.Combine(root.FullName, "tmux-calls");
            TemporaryRoot = System.IO.Directory.CreateDirectory(Path.Combine(root.FullName, "runs")).FullName;
        }

        public DirectoryInfo Directory { get; }
        public string Root => Directory.FullName;
        public string TmuxPath { get; }
        public string AgentExecutablePath { get; }
        public string CallsPath { get; }
        public string TemporaryRoot { get; }

        public static async Task<TmuxFixture> CreateAsync()
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var root = System.IO.Directory.CreateTempSubdirectory("abacus-tmux-");
            var tmux = Path.Combine(root.FullName, "tmux");
            var agentExecutable = Path.Combine(root.FullName, "fake opencode's");
            await File.WriteAllTextAsync(tmux, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> {{QuoteForShell(Path.Combine(root.FullName, "tmux-calls"))}}
                if test "$1" = split-window; then
                  counter={{QuoteForShell(Path.Combine(root.FullName, "counter"))}}
                  value=0
                  test -f "$counter" && value=$(cat "$counter")
                  value=$((value + 1))
                  printf '%s' "$value" > "$counter"
                  printf '%%%s\n' "$value"
                elif test "$1" = display-message; then
                  printf '%s\n' "$4"
                fi
                exit 0
                """);
            await File.WriteAllTextAsync(agentExecutable, """
                #!/bin/sh
                test "$1" = run || exit 90
                shift
                printf '%s' "$1" > received-prompt
                shift
                printf '%s\n' "$@" > received-arguments
                printf '%s' "$BEADS_ACTOR" > received-actor
                pwd | tr -d '\n' > received-directory
                printf 'visible agent output\n'
                exit 7
                """);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(tmux, mode);
            File.SetUnixFileMode(agentExecutable, mode);
            return new TmuxFixture(root, tmux, agentExecutable);
        }

        public TmuxAgentHost CreateTmux(
            TimeSpan? gracePeriod = null,
            string? window = null,
            string? layout = null,
            AgentMode mode = AgentMode.OpenCode,
            bool remote = false) => new(
            new CommandRunner(TextWriter.Null),
            TmuxPath,
            AgentExecutablePath,
            mode,
            "workers",
            TemporaryRoot,
            gracePeriod,
            window,
            layout,
            remote);

        private static string QuoteForShell(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        public void Dispose() => Directory.Delete(recursive: true);
    }
}
