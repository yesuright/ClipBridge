namespace ClipBridge.Core.Caching;

/// <summary>
/// Thread-safe bounded TTL cache of strings (used for content hashes and clip ids).
/// Insertion order is preserved; the oldest entries are evicted beyond capacity
/// and every entry expires after its TTL.
/// </summary>
public sealed class RecentHashCache
{
    private readonly object _lock = new();
    private readonly TimeSpan _ttl;
    private readonly int _capacity;
    private readonly Func<DateTime> _clock;
    private readonly Queue<(string Key, DateTime ExpiresAt)> _order = new();
    private readonly HashSet<string> _keys = new();

    public RecentHashCache(TimeSpan ttl, int capacity = 512, Func<DateTime>? clock = null)
    {
        _ttl = ttl;
        _capacity = capacity;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public bool Contains(string key)
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            return _keys.Contains(key);
        }
    }

    public void Add(string key)
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            if (!_keys.Contains(key))
            {
                _keys.Add(key);
                _order.Enqueue((key, _clock().Add(_ttl)));
            }
            while (_keys.Count > _capacity && _order.Count > 0)
            {
                var (oldestKey, _) = _order.Dequeue();
                _keys.Remove(oldestKey);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _keys.Clear();
            _order.Clear();
        }
    }

    private void PruneExpiredLocked()
    {
        var now = _clock();
        while (_order.Count > 0 && _order.Peek().ExpiresAt <= now)
        {
            var (key, _) = _order.Dequeue();
            _keys.Remove(key);
        }
    }
}
