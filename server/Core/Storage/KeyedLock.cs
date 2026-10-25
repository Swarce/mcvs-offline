using System;
using System.Collections.Generic;
using System.Threading;

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>
    /// One mutual-exclusion lock per key (e.g. per account), created on demand and dropped when unused.
    /// Locks are re-entrant on the same thread. Typical use:
    /// <code>using (locks.Acquire(accountId)) { read; modify; AtomicFile.Write...; }</code>
    /// Requests for one account are then applied strictly in arrival order with read-your-writes, which the
    /// TS/GS hand-off depends on (verify-C3 §4).
    /// </summary>
    public sealed class KeyedLock
    {
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public IDisposable Acquire(string key)
        {
            if (key == null) throw new ArgumentNullException("key");
            Entry entry;
            lock (entries)
            {
                if (!entries.TryGetValue(key, out entry))
                {
                    entry = new Entry();
                    entries[key] = entry;
                }
                entry.References++;
            }
            try
            {
                Monitor.Enter(entry);
            }
            catch
            {
                Release(key, entry, false);
                throw;
            }
            return new Releaser(this, key, entry);
        }

        /// <summary>Number of keys currently locked or waited on (diagnostics, tests).</summary>
        public int ActiveKeys
        {
            get { lock (entries) return entries.Count; }
        }

        private void Release(string key, Entry entry, bool exit)
        {
            if (exit) Monitor.Exit(entry);
            lock (entries)
            {
                entry.References--;
                if (entry.References == 0) entries.Remove(key);
            }
        }

        private sealed class Entry
        {
            public int References;
        }

        private sealed class Releaser : IDisposable
        {
            private readonly KeyedLock owner;
            private readonly string key;
            private readonly Entry entry;
            private int disposed;

            public Releaser(KeyedLock owner, string key, Entry entry)
            {
                this.owner = owner;
                this.key = key;
                this.entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                owner.Release(key, entry, true);
            }
        }
    }
}
