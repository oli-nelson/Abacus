using Abacus;

namespace Abacus.Tests;

public sealed class PreflightTests
{
    private const string EmbeddedIdentity = """
        {"backend":"dolt","data_dir":"/tmp/db","database":"abc","embedded":true,"schema_version":1}
        """;

    private const string SharedIdentity = """
        {"backend":"dolt","connection_ok":true,"database":"abc","embedded":false,"host":"LOCALHOST.","port":3307,"schema_version":1}
        """;

    [Fact]
    public async Task ValidSingleAgentLocalDatabasePasses()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync();
        var workspace = await fixture.AddWorkspaceAsync("one", EmbeddedIdentity, "[]");

        var result = await fixture.RunAsync(
            new Options("workers", "provider/model", "127.0.0.1:1234", [new("alice", workspace)]));

        Assert.Single(result.Agents);
        Assert.True(result.Agents[0].DoltIdentity.Embedded);
        Assert.False(result.Agents[0].HasRemote);
        Assert.Equal("http://127.0.0.1:1234", result.OpenCodeServerUrl);
    }

    [Fact]
    public async Task AttachedServerWithoutTmuxDoesNotRequireTmuxExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync(includeTmux: false);
        var workspace = await fixture.AddWorkspaceAsync("one", EmbeddedIdentity, "[]");

        var result = await fixture.RunAsync(
            new Options(
                null,
                "provider/model",
                "127.0.0.1:1234",
                [new("alice", workspace)]));

        Assert.Null(result.Tools.Tmux);
        Assert.Equal("http://127.0.0.1:1234", result.OpenCodeServerUrl);
    }

    [Fact]
    public async Task ValidMultiAgentSharedDatabasePasses()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync();
        var first = await fixture.AddWorkspaceAsync("one", SharedIdentity, "[{}]");
        var second = await fixture.AddWorkspaceAsync(
            "two",
            SharedIdentity.Replace("LOCALHOST.", "localhost", StringComparison.Ordinal),
            "[]");

        var result = await fixture.RunAsync(new Options(
            "workers", "provider/model", null, [new("alice", first), new("bob", second)]));

        Assert.Equal(2, result.Agents.Count);
        Assert.All(result.Agents, static agent => Assert.True(agent.DoltIdentity.IsShared));
    }

    [Fact]
    public async Task DirtyWorkspacePassesForAgentCleanup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync();
        var workspace = await fixture.AddWorkspaceAsync("dirty", EmbeddedIdentity, "[]", gitStatus: " M file");

        var result = await fixture.RunAsync(
            new Options("workers", "provider/model", null, [new("alice", workspace)]));

        Assert.Single(result.Agents);
        Assert.Equal("alice", result.Agents[0].Name);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("http://localhost:1234")]
    [InlineData("localhost:0")]
    [InlineData("localhost:70000")]
    [InlineData("host:abc")]
    [InlineData("host:1234/path")]
    public void InvalidOpenCodeServerIsRejected(string server)
    {
        Assert.Throws<PreflightException>(() => Preflight.NormalizeServer(server));
    }

    [Fact]
    public async Task MismatchedSharedDatabasesAreRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync();
        var first = await fixture.AddWorkspaceAsync("one", SharedIdentity, "[]");
        var second = await fixture.AddWorkspaceAsync(
            "two", SharedIdentity.Replace("\"abc\"", "\"other\"", StringComparison.Ordinal), "[]");

        var exception = await Assert.ThrowsAsync<PreflightException>(() => fixture.RunAsync(new Options(
            "workers", "provider/model", null, [new("alice", first), new("bob", second)])));

        Assert.Contains("same shared Dolt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddedStorageIsRejectedForMultipleAgents()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync();
        var first = await fixture.AddWorkspaceAsync("one", EmbeddedIdentity, "[]");
        var second = await fixture.AddWorkspaceAsync("two", EmbeddedIdentity, "[]");

        var exception = await Assert.ThrowsAsync<PreflightException>(() => fixture.RunAsync(new Options(
            "workers", "provider/model", null, [new("alice", first), new("bob", second)])));

        Assert.Contains("server-backed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTmuxSessionIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync(tmuxSucceeds: false);
        var workspace = await fixture.AddWorkspaceAsync("one", EmbeddedIdentity, "[]");

        var exception = await Assert.ThrowsAsync<PreflightException>(() => fixture.RunAsync(
            new Options("missing", "provider/model", null, [new("alice", workspace)])));

        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingTmuxWindowIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync(tmuxWindowSucceeds: false);
        var workspace = await fixture.AddWorkspaceAsync("one", EmbeddedIdentity, "[]");

        var exception = await Assert.ThrowsAsync<PreflightException>(() => fixture.RunAsync(
            new Options(
                "workers",
                "provider/model",
                null,
                [new("alice", workspace)],
                TmuxWindow: "missing")));

        Assert.Contains("window 'missing'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("session 'workers'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntermediateSymlinkAliasesCannotAssignOneWorktreeTwice()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await PreflightFixture.CreateAsync();
        var workspace = await fixture.AddWorkspaceAsync("real/workspace", EmbeddedIdentity, "[]");
        var realParent = Directory.GetParent(workspace)!.FullName;
        var aliasParent = Path.Combine(Directory.GetParent(realParent)!.FullName, "alias");
        Directory.CreateSymbolicLink(aliasParent, realParent);
        var aliasWorkspace = Path.Combine(aliasParent, "workspace");

        var exception = await Assert.ThrowsAsync<PreflightException>(() => fixture.RunAsync(new Options(
            "workers",
            "provider/model",
            null,
            [new("alice", workspace), new("bob", aliasWorkspace)])));

        Assert.Contains("same workspace", exception.Message, StringComparison.Ordinal);
    }

    private sealed class PreflightFixture : IDisposable
    {
        private readonly DirectoryInfo root;
        private readonly string bin;

        private PreflightFixture(DirectoryInfo root)
        {
            this.root = root;
            bin = Path.Combine(root.FullName, "bin");
        }

        public static async Task<PreflightFixture> CreateAsync(
            bool tmuxSucceeds = true,
            bool tmuxWindowSucceeds = true,
            bool includeTmux = true)
        {
            var fixture = new PreflightFixture(Directory.CreateTempSubdirectory("abacus-preflight-"));
            Directory.CreateDirectory(fixture.bin);
            await fixture.WriteToolAsync("opencode", "#!/bin/sh\nexit 0\n");
            if (includeTmux)
            {
                await fixture.WriteToolAsync("tmux", $$"""
                    #!/bin/sh
                    if test "$1" = has-session; then
                      exit {{(tmuxSucceeds ? 0 : 1)}}
                    elif test "$1" = display-message; then
                      exit {{(tmuxWindowSucceeds ? 0 : 1)}}
                    fi
                    exit 2
                    """);
            }
            await fixture.WriteToolAsync("git", """
                #!/bin/sh
                workspace="$2"
                case "$3" in
                  rev-parse)
                    test -f "$workspace/.git-invalid" && exit 1
                    test "$4" = --show-toplevel && { cd "$workspace" && pwd -P; } || printf 'true\n'
                    ;;
                  status)
                    test -f "$workspace/.git-status" && cat "$workspace/.git-status"
                    true
                    ;;
                  *) exit 2 ;;
                esac
                """);
            await fixture.WriteToolAsync("bd", """
                #!/bin/sh
                if test "$1" = dolt && test "$2" = show; then
                  cat .dolt.json
                elif test "$1" = dolt && test "$2" = remote && test "$3" = list; then
                  cat .remotes.json
                else
                  exit 2
                fi
                """);
            return fixture;
        }

        public async Task<string> AddWorkspaceAsync(
            string name,
            string doltIdentity,
            string remotes,
            string? gitStatus = null)
        {
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName;
            await File.WriteAllTextAsync(Path.Combine(workspace, ".dolt.json"), doltIdentity);
            await File.WriteAllTextAsync(Path.Combine(workspace, ".remotes.json"), remotes);
            if (gitStatus is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, ".git-status"), gitStatus);
            }

            return workspace;
        }

        public Task<PreflightResult> RunAsync(Options options) =>
            new Preflight(new CommandRunner(TextWriter.Null), bin)
                .RunAsync(options, CancellationToken.None);

        private async Task WriteToolAsync(string name, string contents)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var path = Path.Combine(bin, name);
            await File.WriteAllTextAsync(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => root.Delete(recursive: true);
    }
}
