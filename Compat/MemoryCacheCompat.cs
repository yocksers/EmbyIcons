using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Extensions.Caching.Memory
{
    internal enum EvictionReason
    {
        None,
        Removed,
        Replaced,
        Expired,
        TokenExpired,
        Capacity
    }

    internal delegate void PostEvictionDelegate(object key, object? value, EvictionReason reason, object? state);

    internal class MemoryCacheOptions
    {
        public long? SizeLimit { get; set; }
    }

    internal class MemoryCacheEntryOptions
    {
        internal long Size = 1;
        internal TimeSpan? SlidingExpiration;
        internal PostEvictionDelegate? EvictionCallback;
        internal object? EvictionState;

        public MemoryCacheEntryOptions SetSize(long size)
        {
            Size = size;
            return this;
        }

        public MemoryCacheEntryOptions SetSlidingExpiration(TimeSpan slidingExpiration)
        {
            SlidingExpiration = slidingExpiration;
            return this;
        }

        public MemoryCacheEntryOptions RegisterPostEvictionCallback(PostEvictionDelegate callback, object? state = null)
        {
            EvictionCallback = callback;
            EvictionState = state;
            return this;
        }
    }

    internal class MemoryCache : IDisposable
    {
        private sealed class Entry
        {
            public object Key = null!;
            public object? Value;
            public long Size;
            public TimeSpan? SlidingExpiration;
            public DateTime LastAccessUtc;
            public PostEvictionDelegate? EvictionCallback;
            public object? EvictionState;
        }

        private readonly Dictionary<object, Entry> _entries = new Dictionary<object, Entry>();
        private readonly object _gate = new object();
        private readonly long? _sizeLimit;
        private long _currentSize;
        private bool _disposed;

        public MemoryCache(MemoryCacheOptions options)
        {
            _sizeLimit = options?.SizeLimit;
        }

        public int Count
        {
            get { lock (_gate) { return _entries.Count; } }
        }

        public long CurrentSize
        {
            get { lock (_gate) { return _currentSize; } }
        }

        public bool TryGetValue<TItem>(object key, out TItem? value)
        {
            Entry? expired = null;
            Entry? found = null;

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    if (IsExpired(entry))
                    {
                        _entries.Remove(key);
                        _currentSize -= entry.Size;
                        expired = entry;
                    }
                    else
                    {
                        entry.LastAccessUtc = DateTime.UtcNow;
                        found = entry;
                    }
                }
            }

            if (expired != null)
                expired.EvictionCallback?.Invoke(expired.Key, expired.Value, EvictionReason.Expired, expired.EvictionState);

            if (found != null)
            {
                value = found.Value is TItem typed ? typed : default;
                return true;
            }

            value = default;
            return false;
        }

        public void Set<TItem>(object key, TItem value, MemoryCacheEntryOptions? options = null)
        {
            Entry? replaced = null;

            lock (_gate)
            {
                ThrowIfDisposed();

                if (_entries.TryGetValue(key, out var existing))
                {
                    _currentSize -= existing.Size;
                    replaced = existing;
                }

                var entry = new Entry
                {
                    Key = key,
                    Value = value,
                    Size = options?.Size ?? 1,
                    SlidingExpiration = options?.SlidingExpiration,
                    LastAccessUtc = DateTime.UtcNow,
                    EvictionCallback = options?.EvictionCallback,
                    EvictionState = options?.EvictionState
                };

                _entries[key] = entry;
                _currentSize += entry.Size;

                EvictOvercapacity();
            }

            if (replaced != null)
                replaced.EvictionCallback?.Invoke(replaced.Key, replaced.Value, EvictionReason.Replaced, replaced.EvictionState);
        }

        public void Remove(object key)
        {
            Entry? removed = null;

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    _entries.Remove(key);
                    _currentSize -= entry.Size;
                    removed = entry;
                }
            }

            if (removed != null)
                removed.EvictionCallback?.Invoke(removed.Key, removed.Value, EvictionReason.Removed, removed.EvictionState);
        }

        public void Compact(double percentage)
        {
            var evicted = new List<(Entry Entry, EvictionReason Reason)>();

            lock (_gate)
            {
                foreach (var entry in _entries.Values.Where(IsExpired).ToList())
                {
                    _entries.Remove(entry.Key);
                    _currentSize -= entry.Size;
                    evicted.Add((entry, EvictionReason.Expired));
                }

                if (percentage > 0 && _entries.Count > 0)
                {
                    var removeCount = (int)Math.Ceiling(_entries.Count * Math.Min(Math.Max(percentage, 0), 1.0));
                    foreach (var entry in _entries.Values.OrderBy(e => e.LastAccessUtc).Take(removeCount).ToList())
                    {
                        _entries.Remove(entry.Key);
                        _currentSize -= entry.Size;
                        evicted.Add((entry, EvictionReason.Capacity));
                    }
                }
            }

            foreach (var (entry, reason) in evicted)
                entry.EvictionCallback?.Invoke(entry.Key, entry.Value, reason, entry.EvictionState);
        }

        private bool IsExpired(Entry entry)
        {
            return entry.SlidingExpiration.HasValue &&
                   DateTime.UtcNow - entry.LastAccessUtc > entry.SlidingExpiration.Value;
        }

        private void EvictOvercapacity()
        {
            if (!_sizeLimit.HasValue)
                return;

            while (_currentSize > _sizeLimit.Value && _entries.Count > 0)
            {
                var oldest = _entries.Values.OrderBy(e => e.LastAccessUtc).FirstOrDefault();
                if (oldest == null)
                    break;

                _entries.Remove(oldest.Key);
                _currentSize -= oldest.Size;
                oldest.EvictionCallback?.Invoke(oldest.Key, oldest.Value, EvictionReason.Capacity, oldest.EvictionState);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MemoryCache));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            List<Entry> remaining;
            lock (_gate)
            {
                remaining = _entries.Values.ToList();
                _entries.Clear();
                _currentSize = 0;
            }

            foreach (var entry in remaining)
                entry.EvictionCallback?.Invoke(entry.Key, entry.Value, EvictionReason.Removed, entry.EvictionState);
        }
    }
}
