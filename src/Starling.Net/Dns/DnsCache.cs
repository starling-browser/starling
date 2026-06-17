namespace Starling.Net.Dns;

/// <summary>
/// TTL-aware LRU cache for DNS lookup results. Thread-safe via a single
/// internal lock; reads and writes are O(1) on average.
/// </summary>
public sealed class DnsCache
{
    private readonly int _maxEntries;
    private readonly Dictionary<string, Entry> _entries;
    private readonly LinkedList<string> _lru = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    public DnsCache(int maxEntries = 256, Func<DateTimeOffset>? now = null)
    {
        if (maxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        _maxEntries = maxEntries;
        _entries = new(StringComparer.OrdinalIgnoreCase);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool TryGet(string hostname, out DnsResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(hostname, out var entry) && entry.ExpiresAt > _now())
            {
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                result = entry.Result;
                return true;
            }
            // Expired or absent.
            if (_entries.Remove(hostname, out var stale))
            {
                _lru.Remove(stale.Node);
            }

            result = default!;
            return false;
        }
    }

    public void Put(string hostname, DnsResult result)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(hostname, out var existing))
            {
                _lru.Remove(existing.Node);
            }
            var node = _lru.AddFirst(hostname);
            _entries[hostname] = new Entry(result, _now() + result.Ttl, node);
            while (_entries.Count > _maxEntries)
            {
                var oldest = _lru.Last;
                if (oldest is null)
                {
                    break;
                }

                _entries.Remove(oldest.Value);
                _lru.RemoveLast();
            }
        }
    }

    public int Count
    {
        get { lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private readonly record struct Entry(
        DnsResult Result, DateTimeOffset ExpiresAt, LinkedListNode<string> Node);
}
