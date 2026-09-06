using System.Diagnostics;
using System.Text.Json;

namespace Abacus.Tests;

public sealed partial class EndToEndTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StdioControllerCanInspectResumeAndShutdown(bool eof)
    {
        var root = Directory.CreateTempSubdirectory("abacus-stdio-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var startInfo = DirectStartInfo(root.FullName, bin, workspace, null);
            startInfo.RedirectStandardInput = true;
            startInfo.ArgumentList.Add("--stdio");
            startInfo.ArgumentList.Add("--start-paused");
            process = Process.Start(startInfo)!;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            async Task<JsonElement> ReadUntil(string type)
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                    Assert.DoesNotContain("\u001b", line, StringComparison.Ordinal);
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.GetProperty("type").GetString() == type)
                        return json.RootElement.GetProperty("data").Clone();
                }
            }
            await ReadUntil("control.ready");
            Assert.False(File.Exists(Path.Combine(root.FullName, "ready-count")));
            await process.StandardInput.WriteLineAsync("""{"id":"status","command":"status"}""");
            var status = await ReadUntil("control.result");
            Assert.False(status.GetProperty("status").GetProperty("claimsEnabled").GetBoolean());
            await process.StandardInput.WriteLineAsync("{bad json");
            Assert.False((await ReadUntil("control.result")).GetProperty("ok").GetBoolean());
            await process.StandardInput.WriteLineAsync("""{"id":"resume","command":"resume"}""");
            Assert.True((await ReadUntil("control.result")).GetProperty("ok").GetBoolean());
            Assert.Equal("closed", (await ReadUntil("ticket.outcome")).GetProperty("outcome").GetString());
            if (eof) process.StandardInput.Close();
            else await process.StandardInput.WriteLineAsync("""{"id":"bye","command":"shutdown"}""");
            var exited = await ReadUntil("run.exited");
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, exited.GetProperty("exitCode").GetInt32());
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FiniteRunRecordsEventsWithStdinStillOpen(bool stdio)
    {
        var root = Directory.CreateTempSubdirectory("abacus-stdio-finite-");
        Process? process = null;
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var eventPath = Path.Combine(root.FullName, "events.jsonl");
            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "--once");
            startInfo.RedirectStandardInput = true;
            if (stdio) startInfo.ArgumentList.Add("--stdio");
            startInfo.ArgumentList.Add("--event-log=" + eventPath);
            process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            var text = await File.ReadAllTextAsync(eventPath);
            if (stdio) Assert.Equal(text, await stdout);
            else Assert.Empty(await stdout);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines) using (JsonDocument.Parse(line)) { }
            Assert.Contains("\"type\":\"ticket.outcome\"", text);
            Assert.Contains("\"type\":\"run.summary\"", text);
            Assert.Contains("\"type\":\"run.exited\"", lines[^1]);
            Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
            if (stdio) Assert.Empty(await stderr);
            else Assert.Contains("ABACUS RUN SUMMARY", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            root.Delete(true);
        }
    }

    [Fact]
    public async Task StartPausedWithoutInteractiveInputOrStdioFailsBeforePreflight()
    {
        var startInfo = DirectStartInfo(Path.GetTempPath(), Path.GetTempPath(), Path.GetTempPath(), null);
        startInfo.RedirectStandardInput = true;
        startInfo.ArgumentList.Add("--start-paused");
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        Assert.Equal(1, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains("--start-paused requires an interactive dashboard or --stdio", await stderr);
    }

    [Fact]
    public async Task StdioPreflightFailureIsReportedAsEvents()
    {
        var root = Directory.CreateTempSubdirectory("abacus-stdio-failure-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            var startInfo = DirectStartInfo(root.FullName, bin, workspace, "--once");
            startInfo.ArgumentList.Add("--stdio");
            startInfo.Environment["ABACUS_TEST_NO_GIT_OPS"] = "1";
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(1, process.ExitCode);
            var text = await stdout;
            Assert.Contains("\"type\":\"run.error\"", text);
            Assert.Contains("\"exitCode\":1", text);
            Assert.DoesNotContain("\"type\":\"control.ready\"", text);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                using (JsonDocument.Parse(line)) { }
            Assert.Contains("no-git-ops", await stderr);
        }
        finally { root.Delete(true); }
    }
}
