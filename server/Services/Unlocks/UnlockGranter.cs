using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Alerts;
using McvsOffline.Server.Services.Assets;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Unlocks
{
    /// <summary>
    /// settings.unlockAll through the IAP inventory ledger (verify-C5 (c) and "Recommended design").
    /// <para>
    /// The TS diffs the profile's storage section <c>inventory</c> ({rewardId: int}) against what it already granted
    /// (CrmStandardData.Inventory, saved in the game profile) when its IapStoreComponent starts and again on every
    /// <c>set_profile</c> alert, and claims each new key as a reward (EN/IapStoreComponent.cs CheckInventory,
    /// GM/PlayerProfileComponent.cs IapStore_PurchaseCompleted). So once the player's game profile exists, we write
    /// every unlock key with count 1 and push a set_profile alert; nothing in the game is patched.
    /// </para>
    /// The traps from verify-C5 (c), and how they are handled:
    /// <list type="bullet">
    /// <item>values must be JSON integers: we write 1 (long);</item>
    /// <item>a camo, accessory, weapon or skin claimed while its merc is locked becomes an alternate reward and is never
    /// retried: <c>UnlockMerc_ALL_MAX</c> goes first, alone in its own batch, then the groups in unlocks.json "order"
    /// (skins last, as the diff does anyway); keys keep insertion order in the stored section;</item>
    /// <item>reward popups: the rest goes in batches (unlocks.batchSize, default 400) one set_profile alert apart
    /// (unlocks.batchDelaySeconds, default 10 s); a missed alert is harmless, the next login diffs everything that is
    /// left, still in order;</item>
    /// <item>the TS's own start-up diff can run before its handler is attached (the "subscription race"): we only write
    /// after the TS has saved the game profile, i.e. while that TS is running and listening;</item>
    /// <item>one-way: the profile is backed up before the first key is written.</item>
    /// </list>
    /// Turning unlockAll off later keeps what was granted. Keys already in the ledger with a count of at least 1 are left
    /// alone, so this is idempotent across restarts.
    /// </summary>
    public sealed class UnlockGranter
    {
        public const string MercsGroup = "mercs";

        private readonly object gate = new object();
        private readonly ServerContext context;
        private readonly Log log;
        private readonly Queue<string> queue = new Queue<string>();
        private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> done = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> status = new Dictionary<string, string>(StringComparer.Ordinal);
        private Thread worker;

        public UnlockGranter(ServerContext context, Log log)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
            this.log = log ?? Log.Null;
        }

        public bool Enabled { get { return context.Settings.UnlockAll; } }

        private int BatchSize { get { return (int)Num.Clamp(context.Settings.GetLong("unlocks.batchSize", 400), 1, 20000); } }

        private int BatchDelayMs { get { return (int)Num.Clamp(context.Settings.GetLong("unlocks.batchDelaySeconds", 10), 0, 600) * 1000; } }

        /// <summary>
        /// Every unlock key in grant order: the unlocks.json groups in its "order" list (mercs first, skins last),
        /// de-duplicated.
        /// </summary>
        public List<string> AllKeys()
        {
            CouponCatalog catalog = CouponCatalog.Load(context.Content, context.Settings, log);
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string group in GroupOrder())
            {
                foreach (string key in catalog.Group(group))
                    if (seen.Add(key)) keys.Add(key);
            }
            return keys;
        }

        /// <summary>
        /// EventHub handler for <c>storage.profile.changed</c>; runs on the request thread, so it only queues. A save by
        /// the game (source "http") of anything under "_mcvs" means the TS has the game profile loaded.
        /// </summary>
        public void OnProfileChanged(object payload)
        {
            var change = payload as Dictionary<string, object>;
            if (change == null || !Enabled) return;
            if (!string.Equals(JsonValue.GetString(change, "source"), "http", StringComparison.Ordinal)) return;
            if (JsonValue.GetBool(change, "deleted")) return;
            string selector = JsonValue.GetString(change, "selector") ?? "";
            if (!selector.StartsWith(McvsConstants.GameProfileSelector, StringComparison.Ordinal)) return;
            string account = JsonValue.GetString(change, "account");
            if (!string.IsNullOrEmpty(account)) Enqueue(account);
        }

        /// <summary>Queues a grant for <paramref name="accountId"/> (diagnostics route, tests). False when already done or queued.</summary>
        public bool Enqueue(string accountId)
        {
            lock (gate)
            {
                if (done.Contains(accountId) || !queued.Add(accountId)) return false;
                queue.Enqueue(accountId);
                status[accountId] = "queued";
                if (worker == null)
                {
                    worker = new Thread(Run);
                    worker.IsBackground = true;
                    worker.Name = "mcvs-unlocks";
                    worker.Start();
                }
                Monitor.PulseAll(gate);
                return true;
            }
        }

        /// <summary>Per-account state for diagnostics: "queued", "granting n/m", "done (n keys)", "failed: ...".</summary>
        public Dictionary<string, object> Status()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            lock (gate)
            {
                foreach (var kv in status) result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>
        /// Writes every missing unlock key for one account, in order and in batches, with a set_profile alert after each.
        /// Returns the number of keys written. Synchronous (the worker thread and tests call it).
        /// </summary>
        public int Grant(string accountId)
        {
            IProfileAccess profiles = context.Features.Get<IProfileAccess>();
            if (profiles == null)
            {
                log.Warn("unlockAll: the storage module is not available; nothing granted to " + accountId);
                return 0;
            }
            if (!profiles.HasProfile(accountId)) return 0;

            var inventory = profiles.Read(accountId, "inventory") as Dictionary<string, object>;
            var missing = new List<string>();
            foreach (string key in AllKeys())
            {
                object value;
                long count = inventory != null && inventory.TryGetValue(key, out value) ? JsonValue.AsLong(value) ?? 0 : 0;
                if (count < 1) missing.Add(key);
            }
            if (missing.Count == 0)
            {
                SetStatus(accountId, "done (nothing missing)");
                return 0;
            }

            log.Info("unlockAll: granting " + missing.Count + " unlock keys to " + accountId + " through the inventory ledger");
            int written = 0;
            bool first = true;
            foreach (List<string> batch in Batches(missing))
            {
                if (!first && Wait(BatchDelayMs)) break; // server stopping: the next login picks up the rest
                var patch = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string key in batch) patch[key] = 1L;
                profiles.Merge(accountId, "inventory", patch, first ? "unlockall" : null);
                written += batch.Count;
                first = false;

                var alert = new Dictionary<string, object>(StringComparer.Ordinal);
                alert["type"] = "set_profile"; // OP/AlertType.cs SetProfile
                var push = new Dictionary<string, object>(StringComparer.Ordinal);
                push["to"] = accountId;
                push["alert"] = alert;
                context.Events.Publish(AlertHub.PushTopic, push);
                SetStatus(accountId, "granting " + written.ToString(CultureInfo.InvariantCulture) + "/" + missing.Count.ToString(CultureInfo.InvariantCulture));
                log.Debug("unlockAll: " + accountId + ": " + written + "/" + missing.Count + " keys written, set_profile alert sent");
            }
            return written;
        }

        /// <summary>The mercs group alone first, then the rest in batches of <see cref="BatchSize"/>.</summary>
        private IEnumerable<List<string>> Batches(List<string> keys)
        {
            var mercs = new HashSet<string>(CouponCatalog.Load(context.Content, context.Settings, log).Group(MercsGroup), StringComparer.Ordinal);
            var head = new List<string>();
            var rest = new List<string>();
            foreach (string key in keys) (mercs.Contains(key) ? head : rest).Add(key);
            if (head.Count > 0) yield return head;
            int size = BatchSize;
            for (int i = 0; i < rest.Count; i += size)
                yield return rest.GetRange(i, Num.Min(size, rest.Count - i));
        }

        private List<string> GroupOrder()
        {
            var order = new List<string>();
            string text;
            object parsed;
            if (context.Content.TryReadContentText(CouponCatalog.UnlocksFile, out text) && MiniJson.TryParse(text, out parsed))
            {
                foreach (object g in JsonValue.GetArray(parsed as Dictionary<string, object>, "order") ?? new List<object>())
                {
                    string id = JsonValue.AsString(g);
                    if (!string.IsNullOrEmpty(id) && !order.Contains(id)) order.Add(id);
                }
            }
            if (!order.Contains(MercsGroup)) order.Insert(0, MercsGroup);
            else if (order[0] != MercsGroup)
            {
                order.Remove(MercsGroup);
                order.Insert(0, MercsGroup); // must come first whatever the file says (verify-C5 ordering trap)
            }
            return order;
        }

        private void Run()
        {
            while (true)
            {
                string account;
                lock (gate)
                {
                    while (queue.Count == 0)
                    {
                        if (context.IsStopping)
                        {
                            worker = null; // a later Enqueue (after a restart) starts a new one
                            return;
                        }
                        Monitor.Wait(gate, 1000);
                    }
                    account = queue.Dequeue();
                }
                try
                {
                    int written = Grant(account);
                    lock (gate)
                    {
                        queued.Remove(account);
                        done.Add(account);
                        if (written > 0) status[account] = "done (" + written.ToString(CultureInfo.InvariantCulture) + " keys written)";
                    }
                }
                catch (Exception ex)
                {
                    // Never let an exception escape a server thread (it would kill the game). Retried on the next save.
                    log.Error("unlockAll: granting to " + account + " failed", ex);
                    lock (gate)
                    {
                        queued.Remove(account);
                        status[account] = "failed: " + ex.Message;
                    }
                }
            }
        }

        private void SetStatus(string accountId, string text)
        {
            lock (gate) status[accountId] = text;
        }

        /// <summary>Waits <paramref name="ms"/>; true when the server is stopping.</summary>
        private bool Wait(int ms)
        {
            return ms > 0 ? context.StopSignal.WaitOne(ms) : context.IsStopping;
        }
    }
}
