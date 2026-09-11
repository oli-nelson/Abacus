using Abacus;

namespace Abacus.Tests;

public sealed class TmuxSessionLeaseTests
{
    [Fact]
    public async Task ImplicitMissingSessionIsCreatedConfiguredAndOwned()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await TmuxLeaseFixture.CreateAsync();
        var options = PaneOptions();

        var lease = await fixture.CreateLeaseAsync(options);

        Assert.StartsWith("abacus - project-name-", lease.SessionName, StringComparison.Ordinal);
        Assert.Equal(TmuxSessionLease.DefaultWindowName, lease.WindowName);
        Assert.Equal("@1", lease.WindowId);
        Assert.True(lease.OwnsSession);
        var calls = await fixture.CallsAsync();
        Assert.Contains(calls, call => call.Contains(
            $"new-session -d -P -F #{{window_id}} -s {lease.SessionName} -n {TmuxSessionLease.DefaultWindowName}",
            StringComparison.Ordinal));
        Assert.Contains("set-option -w -t @1 remain-on-exit on", calls);

        await lease.DisposeAsync();
        Assert.Contains($"kill-session -t {lease.SessionName}", await fixture.CallsAsync());
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "user-session")]
    public async Task DisownedOrExplicitlyNamedCreatedSessionIsNotRemoved(
        bool disown,
        string? explicitSession)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await TmuxLeaseFixture.CreateAsync();
        var options = PaneOptions() with
        {
            TmuxSession = explicitSession,
            DisownTmuxSession = disown,
        };

        var lease = await fixture.CreateLeaseAsync(options);
        Assert.False(lease.OwnsSession);
        await lease.DisposeAsync();

        Assert.DoesNotContain(
            await fixture.CallsAsync(),
            static call => call.StartsWith("kill-session", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abacus")]
    public async Task ExistingSessionIsNotOwnedAndGetsMissingDefaultWindow(string? sessionName)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await TmuxLeaseFixture.CreateAsync(
            sessionExists: true,
            windows: [$"@4\t0\t{sessionName ?? "shell"}"]);

        var lease = await fixture.CreateLeaseAsync(PaneOptions() with { TmuxSession = sessionName });

        Assert.False(lease.OwnsSession);
        Assert.Equal("@2", lease.WindowId);
        Assert.Contains(
            await fixture.CallsAsync(),
            call => call.Contains(
                $"new-window -d -P -F #{{window_id}} -t {lease.SessionName}: -n {TmuxSessionLease.DefaultWindowName}",
                StringComparison.Ordinal));
        Assert.Contains("set-option -w -t @2 remain-on-exit on", await fixture.CallsAsync());

        await lease.DisposeAsync();
        Assert.DoesNotContain(
            await fixture.CallsAsync(),
            static call => call.StartsWith("kill-session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingRequestedWindowIsResolvedByNameOrIndexWithoutCreatingAnother()
    {
        if (OperatingSystem.IsWindows()) return;
        using var byName = await TmuxLeaseFixture.CreateAsync(
            sessionExists: true,
            windows: ["@7\t3\tAbacus Agents"]);
        using var byIndex = await TmuxLeaseFixture.CreateAsync(
            sessionExists: true,
            windows: ["@8\t5\tworkers"]);

        var namedLease = await byName.CreateLeaseAsync(PaneOptions() with { TmuxSession = "shared" });
        var indexedLease = await byIndex.CreateLeaseAsync(PaneOptions() with
        {
            TmuxSession = "shared",
            TmuxWindow = "5",
        });

        Assert.Equal("@7", namedLease.WindowId);
        Assert.Equal("@8", indexedLease.WindowId);
        Assert.Equal("workers", indexedLease.WindowName);
        Assert.DoesNotContain(await byName.CallsAsync(), static call => call.StartsWith("new-window", StringComparison.Ordinal));
        Assert.DoesNotContain(await byIndex.CallsAsync(), static call => call.StartsWith("new-window", StringComparison.Ordinal));
    }

    [Fact]
    public void SafeProjectIdUsesReadableSanitizedNameAndStablePathHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "My Project!!!");

        var first = TmuxSessionLease.SafeProjectId(root);
        var second = TmuxSessionLease.SafeProjectId(root);

        Assert.StartsWith("my-project-", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Matches("^[a-z0-9-]+$", first);
    }

    [Fact]
    public void SafeProjectIdCapsLongReadableName()
    {
        var projectId = TmuxSessionLease.SafeProjectId(
            "/work/This Is An Extremely Long Project Name That Should Not Dominate The Tmux Session Name Forever");

        var readablePart = projectId[..projectId.LastIndexOf('-')];
        Assert.True(readablePart.Length <= 48);
        Assert.Matches("^[a-z0-9-]+-[a-f0-9]{8}$", projectId);
    }

    private static Options PaneOptions() => new(
        TmuxSession: null,
        Model: "provider/model",
        OpenCodeServer: null,
        Agents: [new AgentOptions("alice", "/tmp/workspace")]);

    private sealed class TmuxLeaseFixture : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string tmux;
        private readonly string calls;

        private TmuxLeaseFixture(DirectoryInfo root, string tmux, string calls)
        {
            this.root = root;
            this.tmux = tmux;
            this.calls = calls;
            RepositoryRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "Project Name!")).FullName;
        }

        public string RepositoryRoot { get; }

        public static async Task<TmuxLeaseFixture> CreateAsync(
            bool sessionExists = false,
            IReadOnlyList<string>? windows = null)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var root = Directory.CreateTempSubdirectory("abacus-tmux-lease-");
            var calls = Path.Combine(root.FullName, "calls");
            var session = Path.Combine(root.FullName, "session");
            var windowState = Path.Combine(root.FullName, "windows");
            if (sessionExists) await File.WriteAllTextAsync(session, "1");
            if (windows is not null) await File.WriteAllLinesAsync(windowState, windows);
            var tmux = Path.Combine(root.FullName, "tmux");
            await File.WriteAllTextAsync(tmux, $$"""
                #!/bin/sh
                printf '%s\n' "$*" >> '{{calls}}'
                case "$1" in
                  has-session)
                    test -f '{{session}}'
                    ;;
                  new-session)
                    test ! -f '{{session}}' || exit 1
                    printf '1' > '{{session}}'
                    printf '@1\t0\t%s\n' "$9" > '{{windowState}}'
                    printf '@1\n'
                    ;;
                  list-windows)
                    test -f '{{session}}' || exit 1
                    test -f '{{windowState}}' && cat '{{windowState}}'
                    ;;
                  new-window)
                    test -f '{{session}}' || exit 1
                    case "$7" in
                      *:) ;;
                      *) printf 'create window failed: index 0 in use\n' >&2; exit 1 ;;
                    esac
                    printf '@2\t1\t%s\n' "$9" >> '{{windowState}}'
                    printf '@2\n'
                    ;;
                  set-option)
                    test "$2 $3 $4 $5 $6" = '-w -t @1 remain-on-exit on' \
                      || test "$2 $3 $4 $5 $6" = '-w -t @2 remain-on-exit on' \
                      || test "$2 $3 $4 $5 $6" = '-w -t @7 remain-on-exit on' \
                      || test "$2 $3 $4 $5 $6" = '-w -t @8 remain-on-exit on'
                    ;;
                  kill-session)
                    rm -f '{{session}}'
                    ;;
                  *) exit 2 ;;
                esac
                """);
            File.SetUnixFileMode(
                tmux,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new TmuxLeaseFixture(root, tmux, calls);
        }

        public Task<TmuxSessionLease> CreateLeaseAsync(Options options) =>
            TmuxSessionLease.CreateAsync(
                new CommandRunner(TextWriter.Null),
                TextWriter.Null,
                tmux,
                options,
                RepositoryRoot,
                CancellationToken.None);

        public async Task<IReadOnlyList<string>> CallsAsync() =>
            File.Exists(calls) ? await File.ReadAllLinesAsync(calls) : [];

        public void Dispose() => root.Delete(recursive: true);
    }
}
