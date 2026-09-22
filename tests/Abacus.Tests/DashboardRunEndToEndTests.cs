using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Abacus.Tests;

public sealed partial class EndToEndTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IntegratedDashboardFiniteExitAndOccupiedPortBeforeClaims(bool occupied, bool stopRun)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("abacus-web-run-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root.FullName, "workspace")).FullName;
            await WriteFakeToolsAsync(root.FullName, bin);
            // Keep the disposable finite harness alive long enough to inspect
            // the real integrated endpoint, without changing production timing.
            File.Move(Path.Combine(bin, "opencode"), Path.Combine(bin, "opencode-inner"));
            await WriteExecutableAsync(Path.Combine(bin, "opencode"), $"#!/bin/sh\nsleep 1\nexec {Q(Path.Combine(bin, "opencode-inner"))} \"$@\"\n");
            File.Move(Path.Combine(bin, "bd"), Path.Combine(bin, "bd-inner"));
            await WriteExecutableAsync(Path.Combine(bin, "bd"), $$$"""
                #!/bin/sh
                root={{{Q(root.FullName)}}}
                if test "$1" = --readonly && test "$2" = where; then
                  printf '{"path":"%s"}\n' "$root"
                elif test "$1" = --readonly && test "$2" = export; then
                  printf '{"id":"abc-1","title":"Fixture","status":"open"}\n'
                elif test "$2" = --help; then
                  printf 'bd comment comments dependencies --limit --json --title --description --priority --append-notes --add-label --remove-label\n'
                else exec {{{Q(Path.Combine(bin, "bd-inner"))}}} "$@"
                fi
                """);
            File.Move(Path.Combine(bin, "git"), Path.Combine(bin, "git-inner"));
            await WriteExecutableAsync(Path.Combine(bin, "git"), $$$"""
                #!/bin/sh
                root={{{Q(root.FullName)}}}
                test "$1" = --no-optional-locks && shift
                test "$1" = --no-replace-objects && shift
                if test "$1" = rev-parse; then
                  if test "$3" = --git-path; then printf '%s/git-dir/shallow\n' "$root"
                  else printf '%s/git-dir\n' "$root"; fi
                elif test "$1" = for-each-ref; then
                  printf 'refs/heads/main\0001111111111111111111111111111111111111111\n'
                elif test "$1" = worktree; then
                  printf 'worktree %s\000HEAD 1111111111111111111111111111111111111111\000branch refs/heads/main\000\000' "$root"
                else exec {{{Q(Path.Combine(bin, "git-inner"))}}} "$@"
                fi
                """);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            if (occupied) socket.Listen(); else socket.Close();
            var start = DirectStartInfo(root.FullName, bin, workspace, "--once");
            if (!occupied) start.ArgumentList.Add("--start-paused");
            start.ArgumentList.Add("--dashboard");
            start.ArgumentList.Add("--dashboard-port");
            start.ArgumentList.Add(port.ToString());
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                if (!occupied)
                {
                    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                    string? project = null;
                    while (!process.HasExited && project is null)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        try { project = await http.GetStringAsync($"http://127.0.0.1:{port}/api/v1/project", timeout.Token); }
                        catch (HttpRequestException) { await Task.Delay(25, timeout.Token); }
                    }
                    Assert.NotNull(project);
                    Assert.Contains("\"runtimeConnected\":true", project);
                    var workers = await http.GetStringAsync($"http://127.0.0.1:{port}/api/v1/runtime", timeout.Token);
                    Assert.Contains("alice", workers);
                    Assert.Contains("\"manualEnabled\":false", workers);
                    Assert.False(File.Exists(Path.Combine(root.FullName, "claimed")));
                    using var projectJson = System.Text.Json.JsonDocument.Parse(project);
                    var session = projectJson.RootElement.GetProperty("runtimeSession").GetString();
                    // Stop/restart while claims remain paused. Receipt completion
                    // must come from the actual AgentLoop, not HTTP acceptance.
                    async Task WorkerAction(string command)
                    {
                        var action = new { session, requestId = Guid.NewGuid().ToString(), worker = "alice", command, confirm = false };
                        while (true)
                        {
                            using var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/v1/runtime/workers/actions")
                                { Content = System.Net.Http.Json.JsonContent.Create(action) };
                            message.Headers.Add("X-Abacus-Request", "1");
                            using var response = await http.SendAsync(message, timeout.Token);
                            using var outcome = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                            { await Task.Delay(25, timeout.Token); continue; } // router still starting
                            var state = outcome.RootElement.GetProperty("outcome").GetString();
                            Assert.True(state is "accepted" or "completed", outcome.RootElement.ToString());
                            if (state == "completed") break;
                            await Task.Delay(25, timeout.Token);
                        }
                    }
                    await WorkerAction("stop");
                    Assert.False(File.Exists(Path.Combine(root.FullName, "claimed")));
                    await WorkerAction("restart");
                    Assert.False(File.Exists(Path.Combine(root.FullName, "claimed")));
                    if (stopRun)
                    {
                        async Task<HttpStatusCode> Stop(bool confirm, string? origin = null)
                        {
                            using var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/v1/run/actions")
                                { Content = System.Net.Http.Json.JsonContent.Create(new { session, requestId = Guid.NewGuid().ToString(), command = "stop", confirm }) };
                            message.Headers.Add("X-Abacus-Request", "1");
                            if (origin is not null) message.Headers.Add("Origin", origin);
                            using var response = await http.SendAsync(message, timeout.Token);
                            return response.StatusCode;
                        }
                        Assert.Equal(HttpStatusCode.Forbidden, await Stop(true, "https://attacker.invalid"));
                        Assert.Equal(HttpStatusCode.BadRequest, await Stop(false));
                        Assert.False(process.HasExited);
                        Assert.Equal(HttpStatusCode.Accepted, await Stop(true));
                    }
                    else
                    {
                    var control = new { session = projectJson.RootElement.GetProperty("runtimeSession").GetString(),
                        requestId = Guid.NewGuid().ToString(), command = "resume", expectedManualEnabled = false };
                    using var forbidden = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/v1/runtime/actions")
                        { Content = System.Net.Http.Json.JsonContent.Create(control) };
                    forbidden.Headers.Add("X-Abacus-Request", "1"); forbidden.Headers.Add("Origin", "https://attacker.invalid");
                    Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(forbidden, timeout.Token)).StatusCode);
                    Assert.False(File.Exists(Path.Combine(root.FullName, "claimed")));
                    using var resume = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/v1/runtime/actions")
                        { Content = System.Net.Http.Json.JsonContent.Create(control) };
                    resume.Headers.Add("X-Abacus-Request", "1");
                    Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(resume, timeout.Token)).StatusCode);
                    }
                }
                await process.WaitForExitAsync(timeout.Token);
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            var error = await stderr;
            Assert.Empty(await stdout);
            if (occupied)
            {
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains("bind", error, StringComparison.OrdinalIgnoreCase);
                Assert.False(File.Exists(Path.Combine(root.FullName, "claimed")));
                Assert.False(File.Exists(Path.Combine(root.FullName, "opencode-arguments")));
                Assert.False(File.Exists(Path.Combine(root.FullName, "ready-count")));
            }
            else
            {
                Assert.True(process.ExitCode == 0, error);
                Assert.Contains("Dashboard:", error);
                Assert.Contains("unauthenticated read/write", error);
                Assert.Contains("ABACUS RUN SUMMARY", error);
                Assert.Equal(!stopRun, File.Exists(Path.Combine(root.FullName, "claimed")));
                using var check = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Assert.Throws<SocketException>(() => check.Connect(IPAddress.Loopback, port));
            }
        }
        finally { root.Delete(true); }
    }
}
