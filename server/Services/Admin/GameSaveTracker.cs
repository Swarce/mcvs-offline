using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// When the game last saved each account in this process: subscribes to storage.profile.changed and keeps the saves
    /// with source "http" only (the game's own requests; the page's staged applies, backups restored by the admin
    /// module and other in-process writes are ignored). Times come from the server clock. Thread-safe.
    /// </summary>
    public sealed class GameSaveTracker
    {
        private readonly object gate = new object();
        private readonly IClock clock;
        private readonly Dictionary<string, Save> saves = new Dictionary<string, Save>(StringComparer.Ordinal);

        /// <param name="events">The hub to subscribe to (null: feed <see cref="OnProfileChanged"/> yourself).</param>
        public GameSaveTracker(EventHub events, IClock clock)
        {
            this.clock = clock ?? SystemClock.Instance;
            if (events != null) events.Subscribe(LocalProfileStore.ChangedTopic, OnProfileChanged);
        }

        /// <summary>This process only; null when the game has not saved the account since the server started.</summary>
        public DateTime? LastGameSave(string accountId)
        {
            if (accountId == null) return null;
            lock (gate)
            {
                Save save;
                return saves.TryGetValue(accountId, out save) ? save.TimeUtc : (DateTime?)null;
            }
        }

        /// <summary>
        /// True when a save of accountId began after sinceUtc and none followed for quietMs; false when no such save
        /// arrived within timeoutMs. (A save that arrived but was followed by others until the timeout also counts: the
        /// progress is saved, the caller's account lock then waits for the save in flight.)
        /// </summary>
        public bool WaitForSave(string accountId, DateTime sinceUtc, int timeoutMs, int quietMs)
        {
            if (accountId == null) return false;
            int deadline = Environment.TickCount + Math.Max(0, timeoutMs);
            lock (gate)
            {
                while (true)
                {
                    Save save;
                    bool saved = saves.TryGetValue(accountId, out save) && save.TimeUtc >= sinceUtc;
                    int now = Environment.TickCount;
                    int left = deadline - now;
                    if (saved)
                    {
                        int quietLeft = quietMs - (now - save.Tick);
                        if (quietLeft <= 0 || left <= 0) return true;
                        Monitor.Wait(gate, Math.Min(quietLeft, left));
                    }
                    else
                    {
                        if (left <= 0) return false;
                        Monitor.Wait(gate, left);
                    }
                }
            }
        }

        /// <summary>The storage.profile.changed handler: {account, source, deleted, ...}.</summary>
        public void OnProfileChanged(object payload)
        {
            var change = payload as Dictionary<string, object>;
            if (change == null) return;
            if (!string.Equals(JsonValue.GetString(change, "source"), "http", StringComparison.Ordinal)) return;
            if (JsonValue.GetBool(change, "deleted")) return;
            string account = JsonValue.GetString(change, "account");
            if (string.IsNullOrEmpty(account)) return;
            lock (gate)
            {
                saves[account] = new Save { TimeUtc = clock.UtcNow, Tick = Environment.TickCount };
                Monitor.PulseAll(gate);
            }
        }

        private struct Save
        {
            public DateTime TimeUtc;
            public int Tick;
        }
    }
}
