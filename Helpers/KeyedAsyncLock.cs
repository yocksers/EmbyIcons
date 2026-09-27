using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyIcons.Helpers
{
    internal sealed class KeyedAsyncLock<TKey> where TKey : notnull
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public int RefCount;
        }

        private sealed class Releaser : IDisposable
        {
            private readonly KeyedAsyncLock<TKey> _owner;
            private readonly TKey _key;
            private readonly Entry _entry;
            private int _disposed;

            public Releaser(KeyedAsyncLock<TKey> owner, TKey key, Entry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                _entry.Semaphore.Release();
                _owner.Return(_key, _entry);
            }
        }

        private readonly Dictionary<TKey, Entry> _entries;

        public KeyedAsyncLock(IEqualityComparer<TKey>? comparer = null)
        {
            _entries = new Dictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
        }

        public int Count
        {
            get { lock (_entries) { return _entries.Count; } }
        }

        public IDisposable Lock(TKey key, CancellationToken cancellationToken = default)
        {
            var entry = Rent(key);
            try
            {
                entry.Semaphore.Wait(cancellationToken);
            }
            catch
            {
                Return(key, entry);
                throw;
            }

            return new Releaser(this, key, entry);
        }

        public async Task<IDisposable> LockAsync(TKey key, CancellationToken cancellationToken = default)
        {
            var entry = Rent(key);
            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Return(key, entry);
                throw;
            }

            return new Releaser(this, key, entry);
        }

        private Entry Rent(TKey key)
        {
            lock (_entries)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }

                entry.RefCount++;
                return entry;
            }
        }

        private void Return(TKey key, Entry entry)
        {
            lock (_entries)
            {
                if (--entry.RefCount > 0) return;

                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(key);
                }

                entry.Semaphore.Dispose();
            }
        }
    }
}
