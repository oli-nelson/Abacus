namespace Abacus.Dashboard;

/// <summary>Bounded single-flight LRU. Pending work is never evicted or duplicated.</summary>
internal sealed class DetailCache<TKey, TValue>(int capacity, CancellationToken lifetime) where TKey : notnull
{
    private sealed record Entry(Task<TValue> Task, LinkedListNode<TKey> Node);
    private readonly object sync = new();
    private readonly Dictionary<TKey, Entry> entries = [];
    private readonly LinkedList<TKey> lru = [];

    public Task<TValue> GetAsync(TKey key, Func<CancellationToken, Task<TValue>> load,
        CancellationToken cancellationToken = default)
    {
        Task<TValue> task;
        lock (sync)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (entries.TryGetValue(key, out var found))
            {
                lru.Remove(found.Node);
                lru.AddLast(found.Node);
                task = found.Task;
            }
            else
            {
                if (entries.Count == capacity)
                {
                    var candidate = lru.First;
                    while (candidate is not null && !entries[candidate.Value].Task.IsCompleted) candidate = candidate.Next;
                    if (candidate is null) throw new InvalidOperationException("Dashboard detail capacity is busy; retry later.");
                    entries.Remove(candidate.Value);
                    lru.Remove(candidate);
                }
                var completion = new TaskCompletionSource<TValue>(TaskCreationOptions.RunContinuationsAsynchronously);
                var node = lru.AddLast(key);
                entries.Add(key, new Entry(completion.Task, node));
                task = completion.Task;
                _ = LoadAsync(key, load, completion);
            }
        }
        return task.WaitAsync(cancellationToken);
    }

    private async Task LoadAsync(TKey key, Func<CancellationToken, Task<TValue>> load, TaskCompletionSource<TValue> completion)
    {
        // Never invoke a potentially synchronous loader while holding the cache lock.
        await Task.Yield();
        try { completion.TrySetResult(await load(lifetime)); }
        catch (Exception exception)
        {
            lock (sync)
            {
                if (entries.TryGetValue(key, out var entry) && entry.Task == completion.Task)
                {
                    entries.Remove(key);
                    lru.Remove(entry.Node);
                }
            }
            if (exception is OperationCanceledException) completion.TrySetCanceled(lifetime);
            else completion.TrySetException(exception);
        }
    }
}
