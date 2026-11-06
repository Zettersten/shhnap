namespace Shnapp.App.Video;

/// <summary>Serializes work for one key without retaining idle keys.</summary>
internal sealed class AsyncKeyedGate<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = new();

    internal async ValueTask<IDisposable> EnterAsync(TKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }

            entry.References++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    private void Release(TKey key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(TKey key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.References != 0) return;
            _entries.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int References;
    }

    private sealed class Lease(AsyncKeyedGate<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private AsyncKeyedGate<TKey>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
    }
}
