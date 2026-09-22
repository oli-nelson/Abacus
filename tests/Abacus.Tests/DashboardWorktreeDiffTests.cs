using Abacus.Dashboard;
using System.Net;
using System.Net.Sockets;

namespace Abacus.Tests;

public sealed class DashboardWorktreeDiffTests
{
    [Fact]
    public async Task RepeatedDirtyTextBinaryAndIndexEditsChangeContentRevisionWithoutChangingStatusOrHead()
    {
        var root = Directory.CreateTempSubdirectory("abacus-worktree-diff-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, root.FullName)); Assert.True(result.Succeeded, result.StandardError);
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await Git("config", "diff.external", "false");
            var file = Path.Combine(root.FullName, "tracked.txt"); var binary = Path.Combine(root.FullName, "binary.dat");
            await File.WriteAllTextAsync(file, "base\n"); await File.WriteAllBytesAsync(binary, [0, 1, 2]);
            await Git("add", "."); await Git("commit", "-m", "base");
            var source = new DashboardGit(runner, root.FullName, default);
            var snapshot = await source.ProbeAsync(default); var id = Assert.Single(snapshot.Worktrees).Id;
            var collector = new GitCollector(source, root.FullName); await collector.RefreshAsync(default);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); var port = ((IPEndPoint)socket.LocalEndPoint!).Port; socket.Close();
            await using var host = await DashboardHost.StartAsync(new([IPAddress.Loopback], new() { "127.0.0.1" }, port), new DashboardStream(), "fixture", "test", default, collector);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxResponseDrainSize = 0 }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var route = "/api/v1/worktrees/diff?id=" + id;
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/v1/worktrees/diff?id=/etc/passwd")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync(route + "&path=/tmp")).StatusCode);
            var clean = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.Empty(clean.Staged); Assert.Empty(clean.Unstaged);
            await File.WriteAllTextAsync(file, "first\n");
            var first = await source.WorktreeDiffAsync(snapshot, id, default);
            var dirtyStatus = await source.ProbeAsync(default);
            var response = await http.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var etag = response.Headers.ETag!;
            using var eventsCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var events = await http.GetAsync("/api/v1/worktrees/events?id=" + id, HttpCompletionOption.ResponseHeadersRead, eventsCancellation.Token);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            // Bound connection setup and each event wait, not unrelated Git work
            // performed while this long-lived subscription remains open.
            eventsCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            using var eventReader = new StreamReader(await events.Content.ReadAsStreamAsync(eventsCancellation.Token));
            async Task<string> ReadEvent()
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(eventsCancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (await eventReader.ReadLineAsync(deadline.Token) is { } line)
                    if (line.StartsWith("data: ", StringComparison.Ordinal)) return line[6..];
                throw new InvalidDataException("Worktree stream ended.");
            }
            var firstEvent = await ReadEvent(); Assert.Contains(first.Revision, firstEvent);
            var watches = Enumerable.Range(0, 9).Select(_ => collector.WatchWorktree(id)).ToArray();
            using var watchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var watch in watches) await watch.Reader.ReadAsync(watchTimeout.Token);
            watchTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            await File.WriteAllTextAsync(file, "second\n");
            var second = await source.WorktreeDiffAsync(snapshot, id, default);
            await collector.RefreshAsync(default);
            watchTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            foreach (var watch in watches)
            {
                using var update = System.Text.Json.JsonDocument.Parse(await watch.Reader.ReadAsync(watchTimeout.Token));
                Assert.Equal(second.Revision, update.RootElement.GetProperty("diff").GetProperty("revision").GetString());
            }
            Assert.Contains(second.Revision, await ReadEvent());
            Assert.Equal(2, collector.WorktreeWatches.SourceReads);
            foreach (var watch in watches) watch.Dispose();
            eventsCancellation.Cancel(); eventReader.Dispose(); events.Dispose();
            for (var wait = 0; wait < 100 && collector.WorktreeWatches.TopicCount != 0; wait++) await Task.Delay(10);
            Assert.Equal(0, collector.WorktreeWatches.TopicCount);
            using var changedRequest = new HttpRequestMessage(HttpMethod.Get, route);
            changedRequest.Headers.IfNoneMatch.Add(etag);
            var changedResponse = await http.SendAsync(changedRequest);
            Assert.Equal(HttpStatusCode.OK, changedResponse.StatusCode);
            Assert.NotEqual(etag, changedResponse.Headers.ETag);
            using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, route);
            unchangedRequest.Headers.IfNoneMatch.Add(changedResponse.Headers.ETag!);
            Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(unchangedRequest)).StatusCode);
            var clients = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => source.ReadWorktreeDiffAsync(snapshot, id, default)));
            Assert.All(clients, value => Assert.Same(clients[0], value));
            Assert.NotEqual(first.Revision, second.Revision); Assert.Contains("+second", second.Unstaged);
            Assert.Equal(dirtyStatus.Revision, (await source.ProbeAsync(default)).Revision);
            var patchQueries = source.GitDiffCommands;
            Assert.Same(second, await source.WorktreeDiffAsync(snapshot, id, default));
            Assert.Equal(patchQueries, source.GitDiffCommands);
            var attributeFile = Path.Combine(root.FullName, ".git", "info", "attributes");
            await File.WriteAllTextAsync(attributeFile, "tracked.txt -diff\n");
            var attributesChanged = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.NotEqual(second.Revision, attributesChanged.Revision);
            Assert.Contains("GIT binary patch", attributesChanged.Unstaged);
            await File.WriteAllTextAsync(attributeFile, "tracked.txt filter=external\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => source.WorktreeDiffAsync(snapshot, id, default));
            File.Delete(attributeFile);
            Assert.Same(second, await source.WorktreeDiffAsync(snapshot, id, default));
            await Git("add", "tracked.txt"); await File.WriteAllTextAsync(file, "base\n");
            var reversed = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.Contains("+second", reversed.Staged); Assert.Contains("-second", reversed.Unstaged);
            await File.WriteAllBytesAsync(binary, [0, 3, 4]);
            var binaryFirst = await source.WorktreeDiffAsync(snapshot, id, default);
            await File.WriteAllBytesAsync(binary, [0, 5, 6]);
            var binarySecond = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.NotEqual(binaryFirst.Revision, binarySecond.Revision);
            Assert.Contains("GIT binary patch", binarySecond.Unstaged);
            var untracked = Path.Combine(root.FullName, "--untracked.txt");
            await File.WriteAllTextAsync(untracked, "first untracked\n");
            var untrackedFirst = await source.WorktreeDiffAsync(snapshot, id, default);
            await File.WriteAllTextAsync(untracked, "second untracked\n");
            var untrackedSecond = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.NotEqual(untrackedFirst.Revision, untrackedSecond.Revision);
            Assert.Contains("+second untracked", untrackedSecond.Untracked);
            var untrackedBinary = Path.Combine(root.FullName, "new-binary.dat");
            await File.WriteAllBytesAsync(untrackedBinary, [0, 7, 8]);
            var untrackedBinaryFirst = await source.WorktreeDiffAsync(snapshot, id, default);
            await File.WriteAllBytesAsync(untrackedBinary, [0, 9, 10]);
            var untrackedBinarySecond = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.NotEqual(untrackedBinaryFirst.Revision, untrackedBinarySecond.Revision);
            Assert.Contains("GIT binary patch", untrackedBinarySecond.Untracked);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, ".git", "private-fixture"), "PRIVATE SENTINEL MUST NOT APPEAR");
            File.CreateSymbolicLink(Path.Combine(root.FullName, "link"), ".git/private-fixture");
            var link = await source.WorktreeDiffAsync(snapshot, id, default);
            Assert.Contains("120000", link.Untracked); Assert.Contains("+.git/private-fixture", link.Untracked);
            Assert.DoesNotContain("PRIVATE SENTINEL", link.Untracked);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, ".git", "info", "exclude"), "ignored-cache/\n");
            Directory.CreateDirectory(Path.Combine(root.FullName, "ignored-cache"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "ignored-cache", "value"), "ignored");
            Assert.Equal(link.Revision, (await source.WorktreeDiffAsync(snapshot, id, default)).Revision);
            var many = Directory.CreateDirectory(Path.Combine(root.FullName, "many"));
            for (var i = 0; i < 257; i++) await File.WriteAllTextAsync(Path.Combine(many.FullName, i.ToString()), "");
            await Assert.ThrowsAsync<InvalidDataException>(() => source.WorktreeDiffAsync(snapshot, id, default));
            many.Delete(true);
            var oversized = Path.Combine(root.FullName, "oversized.txt");
            await File.WriteAllTextAsync(oversized, new string('x', 5 * 1024 * 1024));
            await Assert.ThrowsAsync<CommandOutputLimitException>(() => source.WorktreeDiffAsync(snapshot, id, default));
            File.Delete(oversized);
            await Assert.ThrowsAsync<ArgumentException>(() => source.WorktreeDiffAsync(snapshot, "/etc/passwd", default));
            await Git("commit", "-m", "index changed HEAD");
            await Assert.ThrowsAsync<InvalidDataException>(() => source.WorktreeDiffAsync(snapshot, id, default));
            await collector.RefreshAsync(default);
            using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var shutdownStream = await http.GetAsync("/api/v1/worktrees/events?id=" + id, HttpCompletionOption.ResponseHeadersRead, shutdownDeadline.Token);
            using var shutdownReader = new StreamReader(await shutdownStream.Content.ReadAsStreamAsync(shutdownDeadline.Token));
            while (await shutdownReader.ReadLineAsync(shutdownDeadline.Token) is { } line)
                if (line.StartsWith("data: ", StringComparison.Ordinal)) break;
            await collector.WorktreeWatches.ReconcileAsync(default);
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await shutdownReader.ReadToEndAsync(shutdownDeadline.Token);
            Assert.Equal(0, collector.WorktreeWatches.TopicCount);
            Assert.Throws<InvalidOperationException>(() => collector.WatchWorktree(id));
        }
        finally { root.Delete(true); }
    }
}
