using System.Diagnostics;
using Abacus;

namespace Abacus.Tests;

public sealed class TmuxTests
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
        var tmux = fixture.CreateTmux();

        var run = await tmux.StartOpenCodeAsync(
            agent,
            new BeadsIssue("abc-123", IssueStatus.InProgress),
            "provider/model",
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
            ["--model", "provider/model", "--attach", "http://127.0.0.1:1234", "--dir", workspace],
            await File.ReadAllLinesAsync(Path.Combine(workspace, "received-arguments")));
        Assert.Equal("visible OpenCode output", await wrapper.StandardOutput.ReadLineAsync());

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
        var tmux = fixture.CreateTmux();

        var local = await tmux.StartOpenCodeAsync(
            Agent("alice", firstWorkspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/exact-model", null, CancellationToken.None);
        var attached = await tmux.StartOpenCodeAsync(
            Agent("bob", secondWorkspace),
            new BeadsIssue("abc-2", IssueStatus.InProgress),
            "provider/exact-model", "http://server:1234", CancellationToken.None);

        Assert.NotEqual(local.PaneId, attached.PaneId);
        var localWrapper = await File.ReadAllTextAsync(local.WrapperPath);
        var attachedWrapper = await File.ReadAllTextAsync(attached.WrapperPath);
        Assert.Contains("--mini --prompt \"$prompt\" --model 'provider/exact-model'", localWrapper, StringComparison.Ordinal);
        Assert.Contains("--model 'provider/exact-model'", attachedWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain(" run ", localWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("--mini", attachedWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("--attach", localWrapper, StringComparison.Ordinal);
        Assert.Contains("run \"$prompt\" --model 'provider/exact-model' --attach 'http://server:1234'", attachedWrapper, StringComparison.Ordinal);
        Assert.NotEqual(local.RunDirectory, attached.RunDirectory);
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
        var run = await tmux.StartOpenCodeAsync(
            Agent("alice", workspace),
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/model", null, CancellationToken.None);

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
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(path), $"Timed out waiting for {path}");
    }

    private sealed class TmuxFixture : IDisposable
    {
        private TmuxFixture(DirectoryInfo root, string tmuxPath, string openCodePath)
        {
            Directory = root;
            TmuxPath = tmuxPath;
            OpenCodePath = openCodePath;
            CallsPath = Path.Combine(root.FullName, "tmux-calls");
            TemporaryRoot = System.IO.Directory.CreateDirectory(Path.Combine(root.FullName, "runs")).FullName;
        }

        public DirectoryInfo Directory { get; }
        public string Root => Directory.FullName;
        public string TmuxPath { get; }
        public string OpenCodePath { get; }
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
            var openCode = Path.Combine(root.FullName, "fake opencode's");
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
            await File.WriteAllTextAsync(openCode, """
                #!/bin/sh
                test "$1" = run || exit 90
                shift
                printf '%s' "$1" > received-prompt
                shift
                printf '%s\n' "$@" > received-arguments
                printf '%s' "$BEADS_ACTOR" > received-actor
                pwd | tr -d '\n' > received-directory
                printf 'visible OpenCode output\n'
                exit 7
                """);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(tmux, mode);
            File.SetUnixFileMode(openCode, mode);
            return new TmuxFixture(root, tmux, openCode);
        }

        public Tmux CreateTmux(TimeSpan? gracePeriod = null) => new(
            new CommandRunner(TextWriter.Null),
            TmuxPath,
            OpenCodePath,
            "workers",
            TemporaryRoot,
            gracePeriod);

        private static string QuoteForShell(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        public void Dispose() => Directory.Delete(recursive: true);
    }
}
