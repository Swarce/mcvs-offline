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
    /// <item>a brand-new player's first load creates each absent section with an R3 Set of "{}" (OP/ProfileSection.cs
    /// 232-239, 326-333), inventory included, and that Set replaces the whole ledger. So a grant only starts once the
    /// stored profile holds a game-profile dump and an inventory section: after R4, the TS's first save, which follows
    /// LoadSections (GM/ProfileManagerComponent.cs:891-894, 980-987). An account that is not ready yet is retried on
    /// its next save. Before every later batch the ledger is read again, and a missing UnlockMerc_ALL_MAX (a late R3
    /// Set, a hand edit) is written again first, alone;</item>
    /// <item>the TS's own start-up diff can run before its handler is attached (the "subscription race"), and an alert
    /// sent before its AlertsComponent stream is open only reaches the next login: the first batch waits for the
    /// player's stream (unlocks.streamWaitSeconds), then unlocks.startDelaySeconds more;</item>
    /// <item>one-way: the profile (with its game profile) is backed up before the first key is written.</item>
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

        private int StreamWaitMs { get { return (int)Num.Clamp(context.Settings.GetLong("unlocks.streamWaitSeconds", 60), 0, 600) * 1000; } }

        private int StartDelayMs { get { return (int)Num.Clamp(context.Settings.GetLong("unlocks.startDelaySeconds", 10), 0, 600) * 1000; } }

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
        /// EventHub handler for <c>storage.profile.changed</c>; runs on the request thread, so it only queues. Only a
        /// save by the game (source "http") of one of the game-profile sections counts: R4 <c>_mcvs.gameProfile</c>, and
        /// the R6 <c>_mcvs.matchers</c>/<c>_mcvs.snapshot</c> Sets of an existing profile's TS load (verify-C3 §2.3). The
        /// same selectors also arrive as R3 section creations during a new player's first load: <see cref="Grant"/>
        /// then finds no game profile yet and waits for the next save.
        /// </summary>
        public void OnProfileChanged(object payload)
        {
            var change = payload as Dictionary<string, object>;
            if (change == null || !Enabled) return;
            if (!string.Equals(JsonValue.GetString(change, "source"), "http", StringComparison.Ordinal)) return;
            if (JsonValue.GetBool(change, "deleted")) return;
            if (!IsTriggerSelector(JsonValue.GetString(change, "selector"))) return;
            string account = JsonValue.GetString(change, "account");
            if (!string.IsNullOrEmpty(account)) Enqueue(account);
        }

        /// <summary>True for exactly <c>_mcvs.gameProfile</c>, <c>_mcvs.matchers</c> and <c>_mcvs.snapshot</c>.</summary>
        internal static bool IsTriggerSelector(string selector)
        {
            string[] path;
            if (string.IsNullOrEmpty(selector) || !ProfileJson.TryParsePath(selector, out path) || path.Length != 2) return false;
            if (!string.Equals(path[0], McvsConstants.GameProfileSelector, StringComparison.Ordinal)) return false;
            return path[1] == "gameProfile" || path[1] == "matchers" || path[1] == "snapshot";
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

        /// <summary>
        /// Per-account state for diagnostics: "queued", "waiting: ..." (not ready yet, or waiting for the alert stream),
        /// "granting n/m", "done (...)", "failed: ...".
        /// </summary>
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
        /// Returns the number of keys written (0 when nothing is missing or the profile is not ready yet). Synchronous
        /// (the worker thread and tests call it).
        /// </summary>
        public int Grant(string accountId)
        {
            bool complete;
            return Grant(accountId, out complete);
        }

        /// <param name="complete">false when the account must be tried again on its next save: not ready yet, or the
        /// server stopped half-way.</param>
        private int Grant(string accountId, out bool complete)
        {
            complete = false;
            IProfileAccess profiles = context.Features.Get<IProfileAccess>();
            if (profiles == null)
            {
                log.Warn("unlockAll: the storage module is not available; nothing granted to " + accountId);
                complete = true;
                return 0;
            }
            string notReady = NotReadyReason(profiles, accountId);
            if (notReady != null)
            {
                SetStatus(accountId, "waiting: " + notReady);
                log.Info("unlockAll: " + accountId + ": " + notReady + "; trying again at the next game-profile save");
                return 0;
            }

            List<string> all = AllKeys();
            var mercSet = new HashSet<string>(CouponCatalog.Load(context.Content, context.Settings, log).Group(MercsGroup), StringComparer.Ordinal);
            List<string> mercs = all.FindAll(mercSet.Contains); // UnlockMerc_ALL_MAX, in grant order
            Dictionary<string, object> ledger;
            List<string> missing = Missing(profiles, accountId, all, out ledger);
            if (missing.Count == 0)
            {
                SetStatus(accountId, "done (nothing missing)");
                complete = true;
                return 0;
            }

            int total = missing.Count;
            log.Info("unlockAll: granting " + total + " unlock keys to " + accountId + " through the inventory ledger");
            if (WaitUntilListening(accountId)) return 0; // server stopping

            // Safety net: in a normal run every batch adds keys for good; a ledger that keeps losing them is a bug.
            int maxBatches = 2 * (1 + (total + BatchSize - 1) / BatchSize) + 4;
            int written = 0;
            for (int batchNo = 0; ; batchNo++)
            {
                if (batchNo > 0)
                {
                    if (Wait(BatchDelayMs)) return written; // server stopping: the next save picks up the rest
                    missing = Missing(profiles, accountId, all, out ledger);
                    if (missing.Count == 0) break;
                }
                if (batchNo >= maxBatches)
                {
                    log.Warn("unlockAll: " + accountId + ": the inventory ledger keeps losing keys (" + missing.Count
                             + " missing after " + batchNo + " batches); giving up for this run");
                    SetStatus(accountId, "failed: the inventory ledger keeps losing keys");
                    return written;
                }

                List<string> missingMercs = missing.FindAll(mercSet.Contains);
                List<string> batch = missingMercs.Count > 0 ? missingMercs : missing.GetRange(0, Num.Min(BatchSize, missing.Count));
                if (batchNo > 0 && missingMercs.Count > 0)
                    log.Warn("unlockAll: " + accountId + ": " + string.Join(", ", missingMercs.ToArray()) + " missing from the ledger again "
                             + "(replaced by an R3 section creation or edited by hand); writing it again first, alone (verify-C5 (c))");

                // Every merge starts with the merc unlock, at its current count (a no-op while it is in the ledger): if
                // the section is replaced between our read and this write, the merc unlock is still recreated ahead of
                // the batch, so the TS never sees a merc-dependent key without it.
                var patch = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string key in mercs) patch[key] = Num.Max(Count(ledger, key), 1);
                foreach (string key in batch) patch[key] = 1L;
                profiles.Merge(accountId, "inventory", patch, written == 0 ? "unlockall" : null);
                written += batch.Count;

                var alert = new Dictionary<string, object>(StringComparer.Ordinal);
                alert["type"] = "set_profile"; // OP/AlertType.cs SetProfile
                var push = new Dictionary<string, object>(StringComparer.Ordinal);
                push["to"] = accountId;
                push["alert"] = alert;
                context.Events.Publish(AlertHub.PushTopic, push);

                int granted = Num.Max(0, total - (missing.Count - batch.Count));
                SetStatus(accountId, "granting " + granted.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture));
                log.Debug("unlockAll: " + accountId + ": " + granted + "/" + total + " keys granted, set_profile alert sent");
            }
            complete = true;
            return written;
        }

        /// <summary>
        /// Why the account cannot be granted yet, or null when it can: the stored profile must hold the game profile
        /// (R4 done) and the inventory section (its R3 creation done), otherwise the R3 Set of "{}" that a first load
        /// sends for inventory could still replace what we write.
        /// </summary>
        private static string NotReadyReason(IProfileAccess profiles, string accountId)
        {
            if (!profiles.HasGameProfile(accountId)) return "no game profile saved yet";
            if (!(profiles.Read(accountId, "inventory") is Dictionary<string, object>)) return "no inventory section yet";
            return null;
        }

        /// <summary>
        /// The keys of <paramref name="all"/> (in order) that the ledger does not hold with a count of at least 1;
        /// <paramref name="ledger"/> is the ledger as read (null when absent).
        /// </summary>
        private static List<string> Missing(IProfileAccess profiles, string accountId, List<string> all, out Dictionary<string, object> ledger)
        {
            ledger = profiles.Read(accountId, "inventory") as Dictionary<string, object>;
            var missing = new List<string>();
            foreach (string key in all)
                if (Count(ledger, key) < 1) missing.Add(key);
            return missing;
        }

        private static long Count(Dictionary<string, object> ledger, string key)
        {
            object value;
            return ledger != null && ledger.TryGetValue(key, out value) ? JsonValue.AsLong(value) ?? 0 : 0;
        }

        /// <summary>
        /// Before the first batch: waits (at most unlocks.streamWaitSeconds) until the player has an alert stream that
        /// takes set_profile, i.e. the TS's AlertsComponent is listening, then unlocks.startDelaySeconds more so the TS's
        /// own start-up diff and its PurchaseCompleted handler are in place (verify-C5 (c) subscription race). Without
        /// a stream the keys are written anyway: the TS diffs them at its next start. True when the server is stopping.
        /// </summary>
        private bool WaitUntilListening(string accountId)
        {
            AlertHub hub = context.Features.Get<AlertHub>(); // null without the alerts module (tests)
            string fedId = McvsConstants.FedIdPrefix + accountId;
            int waitMs = StreamWaitMs;
            if (hub != null && waitMs > 0 && hub.ListenerCount(fedId, "set_profile") == 0)
            {
                SetStatus(accountId, "waiting: for the player's alert stream");
                int started = Environment.TickCount;
                while (hub.ListenerCount(fedId, "set_profile") == 0)
                {
                    if (unchecked(Environment.TickCount - started) >= waitMs)
                    {
                        log.Warn("unlockAll: no alert stream of " + fedId + " takes set_profile after " + waitMs / 1000
                                 + " s; writing the ledger anyway (the TS diffs it at its next start)");
                        break;
                    }
                    if (context.StopSignal.WaitOne(250)) return true;
                }
            }
            return Wait(StartDelayMs);
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
                    bool complete;
                    int written = Grant(account, out complete);
                    lock (gate)
                    {
                        queued.Remove(account);
                        if (complete) done.Add(account); // otherwise the next game-profile save queues it again
                        if (complete && written > 0) status[account] = "done (" + written.ToString(CultureInfo.InvariantCulture) + " keys written)";
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
