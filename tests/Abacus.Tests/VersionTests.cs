using System.Diagnostics;
using System.Reflection;
using Abacus;

namespace Abacus.Tests;

public sealed class VersionTests
{
    [Fact]
    public void VersionIsStandalone()
    {
        var result = Options.Parse(["version"]);
        Assert.True(result.ShowVersion);
        Assert.False(result.ShowHelp);
        Assert.Null(result.Value);
        Assert.Null(result.RepositoryPath);
    }

    [Theory]
    [InlineData("version", "extra")]
    [InlineData("version", "--model", "test")]
    [InlineData("version", "--repo", "/tmp")]
    [InlineData("--repo", "/tmp", "version")]
    [InlineData("--version")]
    public void VersionRejectsUnsupportedArguments(params string[] args) =>
        Assert.Throws<OptionsException>(() => Options.Parse(args));

    [Fact]
    public async Task VersionPrintsOnlyAssemblyVersionOutsideRepositoryWithoutTools()
    {
        var root = Directory.CreateTempSubdirectory("abacus-version-");
        try
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator)
                    .Select(path => Path.Combine(path, "dotnet"))
                    .First(File.Exists);
            var start = new ProcessStartInfo(dotnet)
            {
                WorkingDirectory = root.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("version");
            start.Environment["PATH"] = root.FullName;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.Equal(version + Environment.NewLine, await stdout);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
