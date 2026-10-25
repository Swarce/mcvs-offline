using System;
using System.Collections.Generic;
using System.Threading;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// One first-come-first-served lock per key (per account). <see cref="System.Threading.Monitor"/> does not
    /// guarantee FIFO hand-over, but the TS/GS profile hand-off needs requests for one account applied strictly in
    /// arrival order (verify-C3 §4: a resume GET must not overtake the save that was sent before it). Each caller
    /// takes a ticket and waits until its ticket is served. Re-entrant on the same thread. Entries are dropped when
    /// nobody holds or waits for them.
    /// </summary>
    internal sealed class FairKeyedLock
    {
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Number of keys currently held or waited on (diagnostics, tests).</summary>
        public int ActiveKeys
        {
            get { lock (entries) return entries.Count; }
        }

        /// <summary>Holders plus waiters for <paramref name="key"/> (diagnostics, tests).</summary>
        public int QueueLength(string key)
        {
            Entry entry;
            lock (entries)
            {
                if (!entries.TryGetValue(key, out entry)) return 0;
            }
            lock (entry) return (int)(entry.NextTicket - entry.Serving - entry.Abandoned.Count);
        }

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

            int thread = Thread.CurrentThread.ManagedThreadId;
            bool acquired = false;
            try
            {
                lock (entry)
                {
                    if (entry.Owner == thread && entry.Recursion > 0)
                    {
                        entry.Recursion++;
                        acquired = true;
                    }
                    else
                    {
                        long ticket = entry.NextTicket++;
                        try
                        {
                            // Monitor.Wait(object) without a timeout is missing from the game's trimmed mscorlib; -1 = infinite.
                            while (entry.Serving != ticket) Monitor.Wait(entry, -1);
                        }
                        catch
                        {
                            // Interrupted while waiting: give the ticket up so later callers are not blocked forever.
                            if (entry.Serving == ticket) entry.Advance();
                            else entry.Abandoned.Add(ticket);
                            throw;
                        }
                        entry.Owner = thread;
                        entry.Recursion = 1;
                        acquired = true;
                    }
                }
            }
            finally
            {
                if (!acquired) Unreference(key, entry);
            }
            return new Releaser(this, key, entry);
        }

        private void Release(string key, Entry entry)
        {
            lock (entry)
            {
                entry.Recursion--;
                if (entry.Recursion == 0)
                {
                    entry.Owner = 0;
                    entry.Advance();
                }
            }
            Unreference(key, entry);
        }

        private void Unreference(string key, Entry entry)
        {
            lock (entries)
            {
                entry.References--;
                if (entry.References == 0) entries.Remove(key);
            }
        }

        private sealed class Entry
        {
            public readonly HashSet<long> Abandoned = new HashSet<long>();
            public int References;
            public long NextTicket;
            public long Serving;
            public int Owner;
            public int Recursion;

            /// <summary>Serves the next ticket (skipping abandoned ones). Call with the entry's monitor held.</summary>
            public void Advance()
            {
                Serving++;
                while (Abandoned.Remove(Serving)) Serving++;
                Monitor.PulseAll(this);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly FairKeyedLock owner;
            private readonly string key;
            private readonly Entry entry;
            private int disposed;

            public Releaser(FairKeyedLock owner, string key, Entry entry)
            {
                this.owner = owner;
                this.key = key;
                this.entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                owner.Release(key, entry);
            }
        }
    }
}
