using Abacus;

namespace Abacus.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public async Task CollectsSortedVisibleModelsAndExplainsClaudeLimitation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = new ModelEnvironment();
        await environment.WriteExecutableAsync("opencode", """
            if [ "$1" != "models" ]; then
              exit 2
            fi
            printf 'zeta/model\nprovider/model-b\nprovider/model-a\nprovider/model-a\n'
            """);
        await environment.WriteExecutableAsync("codex", """
            if [ "$1 $2" != "debug models" ]; then
              exit 2
            fi
            printf '%s\n' '{"models":[{"slug":"gpt-z","visibility":"list"},{"slug":"hidden","visibility":"hide"},{"slug":"gpt-a","visibility":"list"},{"slug":"gpt-a","visibility":"list"}]}'
            """);
        await environment.WriteExecutableAsync("claude", "exit 0\n");

        var report = await environment.CollectAsync();

        Assert.True(report.HasModels);
        Assert.Equal(["provider/model-a", "provider/model-b", "zeta/model"], report.Harnesses[0].ModelIds);
        Assert.Equal(["gpt-a", "gpt-z"], report.Harnesses[1].ModelIds);
        Assert.Empty(report.Harnesses[2].ModelIds);
        Assert.Contains("use /model inside Claude Code", report.Harnesses[2].Note, StringComparison.Ordinal);

        var rendered = report.Render();
        Assert.Contains("OpenCode:\n  provider/model-a", rendered, StringComparison.Ordinal);
        Assert.Contains("Codex:\n  gpt-a", rendered, StringComparison.Ordinal);
        Assert.Contains("Claude Code:\n  (installed CLI", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsMissingAndFailedHarnessesWithoutSuppressingOtherGroups()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = new ModelEnvironment();
        await environment.WriteExecutableAsync("opencode", "printf 'provider/model\n'\n");
        await environment.WriteExecutableAsync("codex", "printf 'catalog unavailable\n' >&2\nexit 7\n");

        var report = await environment.CollectAsync();

        Assert.True(report.HasModels);
        Assert.Single(report.Harnesses[0].ModelIds);
        Assert.Contains("exited with code 7: catalog unavailable", report.Harnesses[1].Note, StringComparison.Ordinal);
        Assert.Equal("not installed or not executable on PATH", report.Harnesses[2].Note);
    }

    [Fact]
    public async Task NoInstalledHarnessesProducesAnEmptyReport()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var environment = new ModelEnvironment();

        var report = await environment.CollectAsync();

        Assert.False(report.HasModels);
        Assert.All(report.Harnesses, static harness => Assert.Empty(harness.ModelIds));
        Assert.All(report.Harnesses, static harness =>
            Assert.Equal("not installed or not executable on PATH", harness.Note));
    }

    private sealed class ModelEnvironment : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("abacus-models-");
        private readonly string bin;

        public ModelEnvironment()
        {
            bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
        }

        public async Task WriteExecutableAsync(string name, string body)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            var path = Path.Combine(bin, name);
            await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public Task<ModelCatalogReport> CollectAsync() =>
            new ModelCatalog(new CommandRunner(TextWriter.Null), bin)
                .CollectAsync(root.FullName, CancellationToken.None);

        public void Dispose() => root.Delete(recursive: true);
    }
}
