using Abacus.Dashboard;
using System.Text.Json;

namespace Abacus.Tests;

public sealed class DashboardWorktreeWatchTests
{
    private static WorktreeDiff Diff(string revision) => new("id", "head", revision, "", "", "", "fixture");

    [Fact]
    public async Task TenClientsShareReadsAndOnlyChangedLatestStatesArePublished()
    {
        var hub = new WorktreeWatchHub(); var revision = "one";
        var readers = Enumerable.Range(0, 10).Select(_ => hub.Subscribe("id", _ => Task.FromResult(Diff(revision)))).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var initial = await Task.WhenAll(readers.Select(async r => await r.Reader.ReadAsync(timeout.Token)));
            Assert.All(initial, body => Assert.Same(initial[0], body)); Assert.Equal(1, hub.SourceReads);
            await hub.ReconcileAsync(default);
            Assert.All(readers, r => Assert.False(r.Reader.TryRead(out _)));
            revision = "two"; await hub.ReconcileAsync(default);
            revision = "three"; await hub.ReconcileAsync(default);
            foreach (var reader in readers)
            {
                using var body = JsonDocument.Parse(await reader.Reader.ReadAsync(timeout.Token));
                Assert.Equal("three", body.RootElement.GetProperty("diff").GetProperty("revision").GetString());
                Assert.False(reader.Reader.TryRead(out _));
            }
            using var reconnected = hub.Subscribe("id", _ => throw new Exception("Must reuse topic"));
            Assert.True(reconnected.Reader.TryRead(out _)); Assert.Equal(4, hub.SourceReads);
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
        Assert.Equal(0, hub.TopicCount);
        var count = hub.SourceReads; await hub.ReconcileAsync(default); Assert.Equal(count, hub.SourceReads);
    }

    [Fact]
    public async Task FailureClearsContentAndSameRevisionRecoveryPublishesAgain()
    {
        var hub = new WorktreeWatchHub(); var fail = false;
        using var reader = hub.Subscribe("id", _ => fail ? throw new InvalidDataException("secret diagnostics") : Task.FromResult(Diff("one")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await reader.Reader.ReadAsync(timeout.Token);
        fail = true; await hub.ReconcileAsync(default);
        using var error = JsonDocument.Parse(await reader.Reader.ReadAsync(timeout.Token));
        Assert.True(error.RootElement.GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, error.RootElement.GetProperty("diff").ValueKind);
        Assert.DoesNotContain("secret", error.RootElement.GetRawText());
        fail = false; await hub.ReconcileAsync(default);
        using var restored = JsonDocument.Parse(await reader.Reader.ReadAsync(timeout.Token));
        Assert.False(restored.RootElement.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task CompletionClosesStreamsRejectsAdmissionAndDropsLateReads()
    {
        var hub = new WorktreeWatchHub();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorktreeDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = hub.Subscribe("id", _ => { started.SetResult(); return release.Task; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = hub.ReconcileAsync(default);
        hub.Complete(); hub.Complete();
        Assert.False(await reader.Reader.WaitToReadAsync());
        Assert.Throws<InvalidOperationException>(() => hub.Subscribe("id", _ => Task.FromResult(Diff("new"))));
        release.SetResult(Diff("late")); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reader.Reader.TryRead(out _)); Assert.Equal(0, hub.TopicCount);
        await hub.ReconcileAsync(default); Assert.Equal(1, hub.SourceReads);
    }

    [Fact]
    public async Task RecreatedTopicCannotReceivePreviousGenerationCompletion()
    {
        var hub = new WorktreeWatchHub();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WorktreeDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var old = hub.Subscribe("id", _ => { started.SetResult(); return release.Task; });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = hub.ReconcileAsync(default); old.Dispose();
        using var current = hub.Subscribe("id", _ => Task.FromResult(Diff("current")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var initial = JsonDocument.Parse(await current.Reader.ReadAsync(timeout.Token));
        Assert.Equal("current", initial.RootElement.GetProperty("diff").GetProperty("revision").GetString());
        release.SetResult(Diff("old")); await pending;
        Assert.False(current.Reader.TryRead(out _));
    }

    [Fact]
    public void CapacityBoundsAndDisposalAreIdempotent()
    {
        var hub = new WorktreeWatchHub();
        var readers = Enumerable.Range(0, 64).Select(i => hub.Subscribe((i % 8).ToString(), _ => Task.FromResult(Diff("one")))).ToArray();
        try
        {
            Assert.Throws<InvalidOperationException>(() => hub.Subscribe("0", _ => Task.FromResult(Diff("one"))));
            readers[0].Dispose(); readers[0].Dispose();
            Assert.Throws<InvalidOperationException>(() => hub.Subscribe("ninth", _ => Task.FromResult(Diff("one"))));
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
        Assert.Equal(0, hub.TopicCount);
    }
}
