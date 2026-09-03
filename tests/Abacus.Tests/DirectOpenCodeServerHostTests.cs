using Abacus;

namespace Abacus.Tests;

public sealed class DirectOpenCodeServerHostTests
{
    [Fact]
    public async Task AttachedProcessReceivesPromptModelServerDirectoryAndActor()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await DirectFixture.CreateAsync(exitImmediately: true);
        var host = fixture.CreateHost();
        var agent = fixture.Agent with
        {
            AppendedPrompt = "Command-line prompt\n\nRepository prompt",
        };
        var run = await host.StartAgentAsync(
            agent,
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/exact-model",
            "xhigh",
            "http://127.0.0.1:4096",
            CancellationToken.None);

        await WaitUntilAsync(() => run.HasExited);

        Assert.Equal(7, run.TryReadExitCode());
        Assert.StartsWith("process ", run.Location, StringComparison.Ordinal);
        Assert.Equal("alice", await fixture.ReadAsync("actor"));
        Assert.EndsWith(
            $"/{fixture.RootName}/workspace",
            await fixture.ReadAsync("directory"),
            StringComparison.Ordinal);
        Assert.Equal(
            Prompt.Render(
                "alice",
                "abc-1",
                fixture.Workspace,
                "Command-line prompt\n\nRepository prompt"),
            await fixture.ReadAsync("prompt"));
        Assert.Equal(
            ["--model", "provider/exact-model", "--variant", "xhigh", "--attach", "http://127.0.0.1:4096", "--dir", fixture.Workspace],
            await File.ReadAllLinesAsync(fixture.PathOf("arguments")));

        await host.StopAndCleanupAsync(run, CancellationToken.None);
    }

    [Fact]
    public async Task CleanupInterruptsDirectProcessAndIsIdempotent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await DirectFixture.CreateAsync(exitImmediately: false);
        var host = fixture.CreateHost();
        var run = await host.StartAgentAsync(
            fixture.Agent,
            new BeadsIssue("abc-1", IssueStatus.InProgress),
            "provider/model",
            "high",
            "http://server:1234",
            CancellationToken.None);

        await WaitUntilAsync(() => File.Exists(fixture.PathOf("started")));
        await host.StopAndCleanupAsync(run, CancellationToken.None);
        await host.StopAndCleanupAsync(run, CancellationToken.None);

        Assert.True(run.HasExited);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1000 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class DirectFixture : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string executable;

        private DirectFixture(DirectoryInfo root, string executable, string workspace)
        {
            this.root = root;
            this.executable = executable;
            Workspace = workspace;
            Agent = new ValidatedAgent(
                "alice",
                workspace,
                new DoltIdentity(true, "abc", null, null, true),
                HasRemote: false);
        }

        public string Workspace { get; }
        public string RootName => root.Name;
        public ValidatedAgent Agent { get; }

        public static async Task<DirectFixture> CreateAsync(bool exitImmediately)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var root = Directory.CreateTempSubdirectory("abacus-direct-");
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            var executable = Path.Combine(root.FullName, "opencode");
            await File.WriteAllTextAsync(executable, $$"""
                #!/bin/sh
                root={{Quote(root.FullName)}}
                test "$1" = run || exit 90
                shift
                printf '%s' "$1" > "$root/prompt"
                shift
                printf '%s\n' "$@" > "$root/arguments"
                printf '%s' "$BEADS_ACTOR" > "$root/actor"
                pwd | tr -d '\n' > "$root/directory"
                touch "$root/started"
                if test {{(exitImmediately ? "1" : "0")}} -eq 1; then
                  exit 7
                fi
                trap 'exit 0' INT TERM
                while :; do sleep 0.1; done
                """);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new DirectFixture(root, executable, workspace);
        }

        public DirectOpenCodeServerHost CreateHost() => new(
            new CommandRunner(TextWriter.Null),
            TextWriter.Null,
            executable,
            TimeSpan.FromSeconds(2));

        public string PathOf(string name) => Path.Combine(root.FullName, name);
        public Task<string> ReadAsync(string name) => File.ReadAllTextAsync(PathOf(name));

        private static string Quote(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

        public void Dispose() => root.Delete(recursive: true);
    }
}
