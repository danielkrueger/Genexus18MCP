using System;
using System.Collections.Generic;

namespace GxMcp.Worker.Services
{
    internal sealed class BoundedStringCache
    {
        private readonly int _capacity;
        // issue #372: entry count alone lets a few multi-MiB responses outweigh hundreds of small
        // ones inside a 32-bit process, so the retained UTF-16 bytes are bounded as well.
        private readonly long _maxBytes;
        private long _bytes;
        private readonly Dictionary<string, Entry> _map;
        private readonly LinkedList<string> _lru = new LinkedList<string>();
        private readonly object _lock = new object();

        // PERFORMANCE (observability): track cache hit/miss/eviction counts so a degraded
        // hit ratio (e.g. capacity too small for the query mix) is visible from whoami /
        // gateway:metrics without standing up an external profiler.
        private long _hits;
        private long _misses;
        private long _evictions;
        public long Hits => System.Threading.Interlocked.Read(ref _hits);
        public long Misses => System.Threading.Interlocked.Read(ref _misses);
        public long Evictions => System.Threading.Interlocked.Read(ref _evictions);
        public int Count { get { lock (_lock) return _map.Count; } }
        public int Capacity => _capacity;
        public long EstimatedBytes { get { lock (_lock) return _bytes; } }

        public BoundedStringCache(int capacity, long maxBytes = long.MaxValue)
        {
            _capacity = Math.Max(1, capacity);
            _maxBytes = Math.Max(1, maxBytes);
            _map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        public bool TryGetValue(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) { System.Threading.Interlocked.Increment(ref _misses); return false; }

            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var entry))
                {
                    System.Threading.Interlocked.Increment(ref _misses);
                    return false;
                }
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                value = entry.Value;
            }
            System.Threading.Interlocked.Increment(ref _hits);
            return true;
        }

        public void TryAdd(string key, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null) return;

            lock (_lock)
            {
                long size = SizeOf(key, value);
                // An entry larger than the whole budget would only flush the cache for nothing.
                if (size > _maxBytes) { RemoveLocked(key); return; }

                if (_map.TryGetValue(key, out var existing))
                {
                    _bytes += size - SizeOf(key, existing.Value);
                    existing.Value = value;
                    _lru.Remove(existing.Node);
                    _lru.AddFirst(existing.Node);
                    EvictWhileOverBudget(keep: key);
                    return;
                }

                _bytes += size;
                var node = new LinkedListNode<string>(key);
                _lru.AddFirst(node);
                _map[key] = new Entry { Value = value, Node = node };
                EvictWhileOverBudget(keep: key);
            }
        }

        public bool TryRemove(string key) => TryRemove(key, out _);

        public bool TryRemove(string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var entry))
                {
                    value = entry.Value;
                    RemoveLocked(key);
                    return true;
                }
                return false;
            }
        }

        private static long SizeOf(string key, string value) => 2L * (key.Length + value.Length);

        private void RemoveLocked(string key)
        {
            if (!_map.TryGetValue(key, out var entry)) return;
            _bytes -= SizeOf(key, entry.Value);
            _map.Remove(key);
            if (entry.Node != null) _lru.Remove(entry.Node);
        }

        // Called with the lock held. The entry just written is never its own victim.
        private void EvictWhileOverBudget(string keep)
        {
            while (_map.Count > _capacity || _bytes > _maxBytes)
            {
                var last = _lru.Last;
                if (last == null || last.Value == keep) break;
                RemoveLocked(last.Value);
                System.Threading.Interlocked.Increment(ref _evictions);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        private sealed class Entry
        {
            public string Value;
            public LinkedListNode<string> Node;
        }
    }
}
