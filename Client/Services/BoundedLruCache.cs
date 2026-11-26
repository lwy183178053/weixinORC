namespace Client.Services
{
    /// <summary>
    /// Thread-safe O(1) Least-Recently-Used (LRU) cache with both entry-count and optional total-byte budgets.
    /// Prevents unbounded memory growth when browsing large numbers of chat images, avatars, or voice recordings.
    /// </summary>
    public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
    {
        private sealed class CacheEntry
        {
            public required TKey Key { get; init; }
            public required TValue Value { get; set; }
            public long SizeBytes { get; set; }
        }

        private readonly object _lock = new();
        private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lru = new();
        private readonly int _maxEntries;
        private readonly long _maxTotalBytes;
        private readonly Func<TValue, long>? _sizeSelector;
        private long _totalBytes;

        public BoundedLruCache(
            int maxEntries,
            long maxTotalBytes = 0,
            Func<TValue, long>? sizeSelector = null,
            IEqualityComparer<TKey>? comparer = null)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _maxTotalBytes = Math.Max(0, maxTotalBytes);
            _sizeSelector = sizeSelector;
            _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(comparer);
        }

        public int Count
        {
            get { lock (_lock) return _map.Count; }
        }

        public long TotalBytes
        {
            get { lock (_lock) return _totalBytes; }
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    if (!ReferenceEquals(_lru.First, node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    value = node.Value.Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            long size = _sizeSelector is not null ? Math.Max(0, _sizeSelector(value)) : 0;

            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _totalBytes = Math.Max(0, _totalBytes - existing.Value.SizeBytes) + size;
                    existing.Value.Value = value;
                    existing.Value.SizeBytes = size;
                    if (!ReferenceEquals(_lru.First, existing))
                    {
                        _lru.Remove(existing);
                        _lru.AddFirst(existing);
                    }
                }
                else
                {
                    var node = new LinkedListNode<CacheEntry>(new CacheEntry
                    {
                        Key = key,
                        Value = value,
                        SizeBytes = size
                    });
                    _lru.AddFirst(node);
                    _map[key] = node;
                    _totalBytes += size;
                }

                EvictIfNeededLocked();
            }
        }

        public bool Remove(TKey key)
        {
            lock (_lock)
            {
                if (_map.Remove(key, out var node))
                {
                    _lru.Remove(node);
                    _totalBytes = Math.Max(0, _totalBytes - node.Value.SizeBytes);
                    return true;
                }
                return false;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
                _totalBytes = 0;
            }
        }

        private void EvictIfNeededLocked()
        {
            while (_lru.Last is { } tail &&
                   (_map.Count > _maxEntries || (_maxTotalBytes > 0 && _totalBytes > _maxTotalBytes && _map.Count > 1)))
            {
                _lru.RemoveLast();
                _map.Remove(tail.Value.Key);
                _totalBytes = Math.Max(0, _totalBytes - tail.Value.SizeBytes);
            }
        }
    }
}
