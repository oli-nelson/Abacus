using Abacus.Dashboard;

namespace Abacus.Tests;

public sealed class DashboardGitTests
{
    [Theory]
    [InlineData("fast-forward", true)]
    [InlineData("squash", false)]
    [InlineData("cherry-pick", false)]
    public async Task IdenticalIntegratedContentDoesNotSubstituteForAncestry(string method, bool contained)
    {
        var root = Directory.CreateTempSubdirectory("abacus-git-integration-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, root.FullName));
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }
        try
        {
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Fixture");
            await Git("config", "user.email", "fixture@example.invalid");
            await Git("commit", "--allow-empty", "-m", "base");
            await Git("checkout", "-b", "abacus/integration");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "feature.txt"), "same delivered content\n");
            await Git("add", "feature.txt");
            await Git("commit", "-m", "recorded issue outcome");
            var issueTip = await Git("rev-parse", "HEAD");
            var issueTree = await Git("rev-parse", "HEAD^{tree}");
            await Git("checkout", "main");
            var source = new DashboardGit(runner, root.FullName, default);
            var before = await source.ProbeAsync(default);
            var comparison = await source.CompareRefsAsync(before, "refs/heads/main", "refs/heads/abacus/integration", default);
            Assert.False(comparison.ContainedInTarget);
            if (method == "fast-forward") await Git("merge", "--ff-only", "abacus/integration");
            else
            {
                // A distinct parent guarantees cherry-pick cannot reproduce the original commit ID.
                await Git("commit", "--allow-empty", "-m", "target advancement");
                if (method == "squash")
                {
                    await Git("merge", "--squash", "abacus/integration");
                    await Git("commit", "-m", "sourced squash outcome");
                }
                else await Git("cherry-pick", issueTip);
            }
            Assert.Equal(issueTree, await Git("rev-parse", "HEAD^{tree}"));
            var after = await source.ProbeAsync(default);
            Assert.NotEqual(before.Revision, after.Revision);
            var integrated = await source.CompareRefsAsync(after, "refs/heads/main", "refs/heads/abacus/integration", default);
            Assert.Equal(contained, integrated.ContainedInTarget);
            Assert.Equal(issueTip, integrated.IssueTip);
            Assert.NotEqual(comparison.TargetTip, integrated.TargetTip);
            if (contained)
            {
                Assert.Equal(issueTip, integrated.TargetTip);
                Assert.Empty(integrated.Files);
            }
            else
            {
                Assert.NotEqual(issueTip, integrated.TargetTip);
                // Triple-dot remains the issue's change from the common base, not remaining work.
                Assert.Equal("feature.txt", Assert.Single(integrated.Files).Path);
                Assert.Equal(1, integrated.Ahead);
            }
            var history = await source.HistoryRefAsync(after, "refs/heads/main", 100, default);
            Assert.Single(history.Commits[0].Parents); // None of these operations creates a merge commit.
            Assert.Contains("not a verified issue integration time", history.Coverage);
            Assert.Same(integrated, await source.CompareRefsAsync(after, "refs/heads/main", "refs/heads/abacus/integration", default));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SameTipDeepeningInvalidatesSnapshotAndHistoryCache()
    {
        var root = Directory.CreateTempSubdirectory("abacus-git-shallow-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task Run(string directory, params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, directory));
            Assert.True(result.Succeeded, result.StandardError);
        }
        try
        {
            var origin = Directory.CreateDirectory(Path.Combine(root.FullName, "origin")).FullName;
            var clone = Path.Combine(root.FullName, "clone");
            await Run(origin, "init", "-b", "main");
            await Run(origin, "config", "user.name", "Fixture");
            await Run(origin, "config", "user.email", "fixture@example.invalid");
            for (var i = 0; i < 3; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(origin, "file.txt"), "line " + i + "\n");
                await Run(origin, "add", "file.txt");
                await Run(origin, "commit", "-m", "commit " + i);
                if (i == 0) await Run(origin, "branch", "base");
            }
            await Run(root.FullName, "clone", "--depth=1", "--no-single-branch", new Uri(origin).AbsoluteUri, clone);
            await Run(clone, "branch", "base", "origin/base");
            Directory.CreateDirectory(Path.Combine(clone, ".abacus"));
            await File.WriteAllTextAsync(Path.Combine(clone, ".abacus", "targets.json"), TargetRegistry.DefaultConfiguration);
            var source = new DashboardGit(runner, clone, default);
            var first = await source.ProbeAsync(default);
            var history = await source.HistoryRefAsync(first, "refs/heads/main", 100, default);
            Assert.Single(history.Commits);
            Assert.Same(history, await source.HistoryRefAsync(first, "refs/heads/main", 100, default));
            var collector = new GitCollector(source, clone);
            var stream = new DashboardStream();
            stream.PublishGit(await collector.RefreshAsync(default));
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            var port = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
            socket.Close();
            await using var host = await DashboardHost.StartAsync(new([System.Net.IPAddress.Loopback], new() { "127.0.0.1" }, port),
                stream, "fixture", "actor", default, collector);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            const string query = "/api/v1/branches/history?branch=refs%2Fheads%2Fmain&limit=100";
            using var response = await http.GetAsync(query);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            const string comparisonQuery = "/api/v1/branches/compare?target=refs%2Fheads%2Fmain&branch=refs%2Fheads%2Fbase&patch=true";
            using var beforeComparison = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(comparisonQuery));
            Assert.False(beforeComparison.RootElement.GetProperty("comparison").GetProperty("containedInTarget").GetBoolean());
            Assert.False(beforeComparison.RootElement.GetProperty("patch").GetProperty("available").GetBoolean());
            var comparisonBefore = await collector.CompareAsync("refs/heads/main", "refs/heads/base", true, default);
            var cachedBefore = await collector.CompareAsync("refs/heads/main", "refs/heads/base", true, default);
            Assert.Same(comparisonBefore.Comparison, cachedBefore.Comparison);
            Assert.Same(comparisonBefore.Patch, cachedBefore.Patch);
            using var subscription = stream.Subscribe(stream.Snapshot().ETag.Trim('"'));
            await Run(clone, "fetch", "--deepen=1");
            stream.PublishGit(await collector.RefreshAsync(default));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("git", (await subscription.Reader.ReadAsync(timeout.Token)).Kind);
            using var conditional = new HttpRequestMessage(HttpMethod.Get, query);
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
            using var refreshed = await http.SendAsync(conditional);
            Assert.Equal(System.Net.HttpStatusCode.OK, refreshed.StatusCode);
            Assert.NotEqual(response.Headers.ETag, refreshed.Headers.ETag);
            using var body = System.Text.Json.JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("commits").GetArrayLength());
            var second = await source.ProbeAsync(default);
            Assert.Equal(first.Branches[0].Tip, second.Branches[0].Tip);
            Assert.NotEqual(first.Revision, second.Revision);
            Assert.NotEqual(first.HistoryBoundary, second.HistoryBoundary);
            var deeper = await source.HistoryRefAsync(second, "refs/heads/main", 100, default);
            Assert.Equal(2, deeper.Commits.Length);
            Assert.NotEqual(history.HistoryBoundary, deeper.HistoryBoundary);
            await Run(clone, "fetch", "--unshallow");
            stream.PublishGit(await collector.RefreshAsync(default));
            var comparisonAfter = await collector.CompareAsync("refs/heads/main", "refs/heads/base", true, default);
            Assert.True(comparisonAfter.Comparison.ContainedInTarget);
            Assert.True(comparisonAfter.Patch!.Available);
            Assert.Equal(comparisonBefore.Comparison.TargetTip, comparisonAfter.Comparison.TargetTip);
            Assert.Equal(comparisonBefore.Comparison.IssueTip, comparisonAfter.Comparison.IssueTip);
            Assert.NotEqual(comparisonBefore.Comparison.HistoryBoundary, comparisonAfter.Comparison.HistoryBoundary);
            using var afterComparison = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(comparisonQuery));
            Assert.True(afterComparison.RootElement.GetProperty("comparison").GetProperty("containedInTarget").GetBoolean());
            Assert.True(afterComparison.RootElement.GetProperty("patch").GetProperty("available").GetBoolean());
            var third = await source.ProbeAsync(default);
            Assert.NotEqual(second.Revision, third.Revision);
            Assert.Equal(first.Branches[0].Tip, third.Branches[0].Tip);
            var complete = await source.HistoryRefAsync(third, "refs/heads/main", 100, default);
            Assert.Equal(3, complete.Commits.Length);
            Assert.NotEqual(deeper.HistoryBoundary, complete.HistoryBoundary);
            Assert.Same(complete, await source.HistoryRefAsync(third, "refs/heads/main", 100, default));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task GitHistoryPreservesTopologyAndLabelsParentClockSkew()
    {
        var root = Directory.CreateTempSubdirectory("abacus-git-clock-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task Run(string[] args, string? date = null)
        {
            var environment = date is null ? null : new Dictionary<string, string?>
                { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date };
            var result = await runner.RunAsync(new("git", args, root.FullName, environment));
            Assert.True(result.Succeeded, result.StandardError);
        }
        try
        {
            await Run(["init", "-b", "main"]);
            await Run(["config", "user.name", "Fixture"]);
            await Run(["config", "user.email", "fixture@example.invalid"]);
            await Run(["commit", "--allow-empty", "-m", "parent"], "2026-09-21T12:00:00Z");
            await Run(["commit", "--allow-empty", "-m", "child"], "2026-09-21T10:00:00Z");
            var source = new DashboardGit(runner, root.FullName, default);
            var snapshot = await source.ProbeAsync(default);
            var history = await source.HistoryRefAsync(snapshot, "refs/heads/main", 100, default);
            Assert.True(history.ClockSkew);
            Assert.Contains("child", history.Commits[0].Message);
            Assert.Contains("parent", history.Commits[1].Message);
            Assert.Equal(history.Commits[1].Id, Assert.Single(history.Commits[0].Parents));
            Assert.False(history.LimitReached);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void MalformedSourceCannotMasqueradeAsEmptyWorktreeState()
    {
        Assert.Throws<InvalidDataException>(() => DashboardGit.ParseWorktrees(""));
        Assert.Throws<InvalidDataException>(() => DashboardGit.ParseWorktrees("worktree /tmp/repo"));
        Assert.Throws<InvalidDataException>(() => DashboardGit.ParseBranches("refs/heads/main\0not-a-commit\n"));
        Assert.Empty(DashboardGit.ParseBranches("")); // unborn repositories have no refs
    }

    [Fact]
    public void NumstatPreservesHostileNamesAndBinaryUnknownCounts()
    {
        var files = DashboardGit.ParseNumstat("2\t1\tfile\twith\nnewlines\0-\t-\tbinary.dat\0");
        Assert.Equal("file\twith\nnewlines", files[0].Path);
        Assert.Equal(2, files[0].Additions);
        Assert.True(files[1].Binary);
        Assert.Null(files[1].Additions);
        Assert.Throws<InvalidDataException>(() => DashboardGit.ParseNumstat("1\t2\ttruncated"));
        Assert.Throws<InvalidDataException>(() => DashboardGit.ParseNumstat("-\t2\tmixed\0"));
    }

    [Fact]
    public async Task RealGitTripleDotCacheTargetMovementContainmentAndRevocation()
    {
        var root = Directory.CreateTempSubdirectory("abacus-dashboard-git-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, root.FullName));
            Assert.True(result.Succeeded, result.StandardError);
            return result.StandardOutput.Trim();
        }
        try
        {
            await Git("init", "-b", "main");
            await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await Git("config", "diff.external", "false"); // comparisons must disable external drivers
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "base.txt"), "base\n");
            await Git("add", "."); await Git("commit", "-m", "base");
            var start = await Git("rev-parse", "HEAD");
            await Git("checkout", "-b", "abacus/web-a");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "issue\tfile.txt"), "one\ntwo\n");
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "binary.dat"), [0, 1, 2, 3]);
            await Git("add", "."); await Git("commit", "-m", "issue work");
            var issue = await Git("rev-parse", "HEAD");
            await Git("checkout", "main");
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "target-only.txt"), "not part of issue changes\n");
            await Git("add", "."); await Git("commit", "-m", "target work");
            var target = await Git("rev-parse", "HEAD");
            var source = new DashboardGit(runner, root.FullName, default);
            var first = await source.ProbeAsync(default);
            Assert.Equal(first.Revision, (await source.ProbeAsync(default)).Revision);
            Assert.Equal(2, first.Branches.Length); Assert.Single(first.Worktrees);
            var key = new GitComparisonKey(target, issue);
            var clients = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => source.CompareAsync(key, default)));
            Assert.All(clients, c => Assert.Same(clients[0], c));
            var comparison = clients[0];
            Assert.False(comparison.ContainedInTarget);
            var fastForward = await source.CompareAsync(new(issue, issue), default);
            Assert.True(fastForward.ContainedInTarget);
            Assert.Empty(fastForward.Files);
            Assert.Equal(start, comparison.MergeBase);
            Assert.Equal(1, comparison.Ahead); Assert.Equal(1, comparison.Behind);
            Assert.DoesNotContain(comparison.Files, f => f.Path == "target-only.txt");
            Assert.Equal(2, comparison.Files.Length);
            Assert.True(comparison.Files.Single(f => f.Path == "binary.dat").Binary);
            var patch = await source.PatchAsync(key, default);
            Assert.True(patch.Available); Assert.Contains("+two", patch.Text);
            Assert.Same(patch, await source.PatchAsync(key, default));
            await Git("merge", "--no-ff", "abacus/web-a", "-m", "integrated");
            var integrated = await source.CompareRefsAsync(first, "refs/heads/main", "refs/heads/abacus/web-a", default);
            Assert.True(integrated.ContainedInTarget);
            Assert.Empty(integrated.Files);
            Assert.NotEqual(target, integrated.TargetTip);
            var history = await source.HistoryAsync(new(integrated.TargetTip, 2), default);
            Assert.Equal(integrated.TargetTip, history.Commits[0].Id);
            Assert.Equal(2, history.Commits[0].Parents.Length);
            Assert.Contains("integrated", history.Commits[0].Message);
            Assert.True(history.LimitReached);
            Assert.Same(history, await source.HistoryAsync(new(integrated.TargetTip, 2), default));
            // Revoke containment by moving main backward in this disposable fixture.
            await Git("reset", "--hard", target);
            var revoked = await source.CompareRefsAsync(first, "refs/heads/main", "refs/heads/abacus/web-a", default);
            Assert.False(revoked.ContainedInTarget);
            Assert.Same(comparison, revoked);
            await Git("pack-refs", "--all");
            Assert.Equal(first.Revision, (await source.ProbeAsync(default)).Revision);
            await Git("branch", "-D", "abacus/web-a");
            Assert.DoesNotContain((await source.ProbeAsync(default)).Branches, b => b.Ref == "refs/heads/abacus/web-a");
            await Assert.ThrowsAsync<InvalidDataException>(() => source.CompareRefsAsync(first, "refs/heads/main", "refs/heads/abacus/web-a", default));
            await Assert.ThrowsAsync<ArgumentException>(() => source.CompareRefsAsync(first, "--help", "refs/heads/main", default));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnrelatedHistoryIsExplicitAndRegisteredWorktreeNamesUseNul()
    {
        var root = Directory.CreateTempSubdirectory("abacus-dashboard-unrelated-");
        var runner = new CommandRunner(TextWriter.Null);
        async Task<string> Git(params string[] args)
        {
            var result = await runner.RunAsync(new("git", args, root.FullName));
            Assert.True(result.Succeeded, result.StandardError); return result.StandardOutput.Trim();
        }
        try
        {
            await Git("init", "-b", "main"); await Git("config", "user.name", "Fixture"); await Git("config", "user.email", "fixture@example.invalid");
            await Git("commit", "--allow-empty", "-m", "main"); var main = await Git("rev-parse", "HEAD");
            await Git("checkout", "--orphan", "other"); await Git("commit", "--allow-empty", "-m", "unrelated");
            var other = await Git("rev-parse", "HEAD");
            var treePath = Path.Combine(root.FullName, "tree\nwith-tab\t");
            await Git("worktree", "add", treePath, "main");
            var source = new DashboardGit(runner, root.FullName, default);
            var snapshot = await source.ProbeAsync(default);
            Assert.EndsWith("/tree\nwith-tab\t", snapshot.Worktrees.Single(w => w.Branch == "refs/heads/main").Path);
            var result = await source.CompareAsync(new(main, other), default);
            Assert.Null(result.MergeBase); Assert.Contains("Unrelated", result.Warning);
            Assert.False(result.ContainedInTarget);
            Assert.False((await source.PatchAsync(new(main, other), default)).Available);
        }
        finally { root.Delete(true); }
    }
}
