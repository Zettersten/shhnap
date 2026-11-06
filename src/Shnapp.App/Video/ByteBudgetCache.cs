namespace Shnapp.App.Video;

/// <summary>Keeps recent decoded surfaces within both an item and byte budget.</summary>
internal sealed class ByteBudgetCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _maximumItems;
    private readonly long _maximumBytes;
    private readonly Dictionary<TKey, (TValue Value, long Bytes)> _items = [];
    private readonly Queue<TKey> _order = new();

    internal ByteBudgetCache(int maximumItems, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _maximumItems = maximumItems;
        _maximumBytes = maximumBytes;
    }

    internal int Count => _items.Count;
    internal long StoredBytes { get; private set; }

    internal bool TryGetValue(TKey key, out TValue value)
    {
        if (_items.TryGetValue(key, out var item))
        {
            value = item.Value;
            return true;
        }

        value = default!;
        return false;
    }

    internal void Add(TKey key, TValue value, long estimatedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedBytes);
        if (!_items.TryAdd(key, (value, estimatedBytes))) return;

        _order.Enqueue(key);
        StoredBytes += estimatedBytes;
        while (_items.Count > 1 &&
            (_items.Count > _maximumItems || StoredBytes > _maximumBytes))
        {
            TKey oldest = _order.Dequeue();
            StoredBytes -= _items[oldest].Bytes;
            _items.Remove(oldest);
        }
    }

    internal void Clear()
    {
        _items.Clear();
        _order.Clear();
        StoredBytes = 0;
    }
}
