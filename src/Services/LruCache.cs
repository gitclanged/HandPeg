namespace HandPegApp.Services;

/// <summary>
/// A dictionary that holds no more than a set number of entries: when one more is put in, the one that was
/// used longest ago is dropped. For pictures made of files (strips of frames, waveforms), which are quick
/// to make again and should not pile up for as long as the session lasts.
///
/// Dropping an entry only lets go of the cache's hold on it: whatever is still showing the picture keeps it.
/// </summary>
public sealed class LruCache<TKey, TValue>(int capacity, IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly int _capacity = Math.Max(capacity, 1);
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _entries = new(comparer);

    // The most recently used first.
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>The keys held right now, the most recently used first.</summary>
    public List<TKey> Keys
    {
        get
        {
            lock (_gate)
                return _order.Select(entry => entry.Key).ToList();
        }
    }

    /// <summary>Looks an entry up, which counts as using it.</summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public TValue? GetValueOrDefault(TKey key) => TryGetValue(key, out var value) ? value : default;

    /// <summary>Puts an entry in, or replaces it; the entry used longest ago goes when that is one too many.</summary>
    public TValue this[TKey key]
    {
        set
        {
            lock (_gate)
            {
                if (_entries.Remove(key, out var old))
                    _order.Remove(old);

                _entries[key] = _order.AddFirst(new KeyValuePair<TKey, TValue>(key, value));
                while (_entries.Count > _capacity && _order.Last is { } oldest)
                {
                    _order.RemoveLast();
                    _entries.Remove(oldest.Value.Key);
                }
            }
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            if (!_entries.Remove(key, out var node))
                return false;

            _order.Remove(node);
            return true;
        }
    }
}
