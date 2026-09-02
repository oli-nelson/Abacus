using Abacus;

namespace Abacus.Tests;

public sealed class AttentionResolutionTests
{
    [Fact]
    public async Task RemovesAttentionLabelWithoutAddingACommentWhenMessageIsOmitted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await AttentionFixture.CreateAsync();

        var result = await fixture.Beads.ResolveUserAttentionAsync(
            fixture.Root,
            "ab-123",
            message: null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["update", "ab-123", "--remove-label", "abacus:needs-user-attention", "--json"],
            await File.ReadAllLinesAsync(fixture.CallsPath));
    }

    [Fact]
    public async Task AddsTheUserResponseAsACommentThenRemovesTheLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await AttentionFixture.CreateAsync();
        const string message = "Use option A; keep $HOME and 'quotes' literal";

        await fixture.Beads.ResolveUserAttentionAsync(
            fixture.Root,
            "ab-456",
            message,
            CancellationToken.None);

        Assert.Equal(
            [
                "comment",
                "ab-456",
                message,
                "--json",
                "update",
                "ab-456",
                "--remove-label",
                "abacus:needs-user-attention",
                "--json",
            ],
            await File.ReadAllLinesAsync(fixture.CallsPath));
    }

    [Fact]
    public async Task ReportsBeadsFailureWithoutClaimingSuccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await AttentionFixture.CreateAsync(fail: true);

        var exception = await Assert.ThrowsAsync<BeadsException>(() =>
            fixture.Beads.ResolveUserAttentionAsync(
                fixture.Root,
                "missing-123",
                message: null,
                CancellationToken.None));

        Assert.Contains("resolve user attention", exception.Message, StringComparison.Ordinal);
        Assert.Contains("issue not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesAttentionLabelInPlaceWhenAddingTheCommentFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = await AttentionFixture.CreateAsync(fail: true);

        var exception = await Assert.ThrowsAsync<BeadsException>(() =>
            fixture.Beads.ResolveUserAttentionAsync(
                fixture.Root,
                "ab-789",
                "Need another revision",
                CancellationToken.None));

        Assert.Contains("record user response", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            [
                "comment",
                "ab-789",
                "Need another revision",
                "--json",
            ],
            await File.ReadAllLinesAsync(fixture.CallsPath));
    }

    private sealed class AttentionFixture : IDisposable
    {
        private readonly DirectoryInfo root;

        private AttentionFixture(DirectoryInfo root, string executable)
        {
            this.root = root;
            Root = root.FullName;
            CallsPath = Path.Combine(Root, "calls");
            Beads = new Beads(new CommandRunner(TextWriter.Null), executable);
        }

        public string Root { get; }
        public string CallsPath { get; }
        public Beads Beads { get; }

        public static async Task<AttentionFixture> CreateAsync(bool fail = false)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var root = Directory.CreateTempSubdirectory("abacus-resolve-attention-");
            var executable = Path.Combine(root.FullName, "bd");
            var calls = Path.Combine(root.FullName, "calls");
            var outcome = fail
                ? "printf 'issue not found\\n' >&2\nexit 1"
                : "printf '[{\"id\":\"ab-123\",\"status\":\"open\"}]\\n'";
            await File.WriteAllTextAsync(executable, $$"""
                #!/bin/sh
                printf '%s\n' "$@" >> {{ShellQuote(calls)}}
                {{outcome}}
                """);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new AttentionFixture(root, executable);
        }

        public void Dispose() => root.Delete(recursive: true);

        private static string ShellQuote(string value) =>
            $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }
}
