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
    /// settings.unlockAll and the unlock codes through the IAP inventory ledger (verify-C5 (c) and "Recommended design").
    /// <para>
    /// The TS diffs the profile's storage section <c>inventory</c> ({rewardId: int}) against what it already granted
    /// (CrmStandardData.Inventory, saved in the game profile) when its IapStoreComponent starts and again on every
    /// <c>set_profile</c> alert, and claims each new key as a reward (EN/IapStoreComponent.cs CheckInventory,
    /// GM/PlayerProfileComponent.cs IapStore_PurchaseCompleted). So once the player's game profile exists, we write
    /// every unlock key with count 1 and push a set_profile alert; nothing in the game is patched.
    /// </para>
    /// <para>
    /// Two kinds of job run on one worker thread: settings.unlockAll per account (<see cref="Grant"/>, every key of
    /// unlocks.json "order"), and a redeemed unlock code per (account, code) (<see cref="GrantKeys"/>, the code's keys;
    /// queued by AssetsService, which answers the game an empty payload instead of one inbox message that froze the
    /// inbox, inbox-freeze report §3 F1). Both use the same loop.
    /// </para>
    /// The traps from verify-C5 (c), and how they are handled:
    /// <list type="bullet">
    /// <item>values must be JSON integers: we write 1 (long);</item>
    /// <item>a camo, accessory, weapon or skin claimed while its merc is locked becomes an alternate reward and is never
    /// retried: the "mercs" group (<c>UnlockMerc_&lt;id&gt;</c> for every merc, then <c>ModTier_&lt;id&gt;_4</c>) goes first, alone in its own batch, then the groups in unlocks.json "order"
    /// (skins last, as the diff does anyway); keys keep insertion order in the stored section. A code's own merc keys
    /// (UNLOCKPAID: the merc unlocks without the mods) go first in the same way;</item>
    /// <item>reward popups: the rest goes in batches (unlocks.batchSize, default 400) one set_profile alert apart
    /// (unlocks.batchDelaySeconds, default 10 s); a missed alert is harmless, the next login diffs everything that is
    /// left, still in order;</item>
    /// <item>a brand-new player's first load creates each absent section with an R3 Set of "{}" (OP/ProfileSection.cs
    /// 232-239, 326-333), inventory included, and that Set replaces the whole ledger. So a grant only starts once the
    /// stored profile holds a game-profile dump and an inventory section: after R4, the TS's first save, which follows
    /// LoadSections (GM/ProfileManagerComponent.cs:891-894, 980-987). An account that is not ready yet is retried on
    /// its next save. Before every later batch the ledger is read again, and a missing merc unlock (a late R3
    /// Set, a hand edit) is written again first, alone;</item>
    /// <item>the TS's own start-up diff can run before its handler is attached (the "subscription race"), and an alert
    /// sent before its AlertsComponent stream is open only reaches the next login: the first batch waits for the
    /// player's stream (unlocks.streamWaitSeconds), then unlocks.startDelaySeconds more;</item>
    /// <item>one-way: the profile (with its game profile) is backed up before the first key is written.</item>
    /// </list>
    /// Turning unlockAll off later keeps what was granted. Keys already in the ledger with a count of at least 1 are left
    /// alone, so this is idempotent, and that is also how a restart carries on: unlockAll is queued again by the
    /// account's next game-profile save and writes only what the ledger still lacks; an unfinished code is kept in
    /// &lt;data&gt;/unlocks/codes.json and queued again by the account's next game-profile save in the same way.
    /// </summary>
    public sealed class UnlockGranter
    {
        public const string MercsGroup = "mercs";

        /// <summary>
        /// The single reward the "mercs" group was before device test 3 (every merc and every mod at once). A ledger that
        /// holds it already had the whole group claimed: writing the per-merc keys as well would only pay the game's duplicate
        /// compensation (40 random gear per owned merc, AlternateRewards <c>UnlockMerc_&lt;rarity&gt;</c>), so they count as
        /// present. It is never written again (its reward display crashed: "Could not find merc data for mercId 'ALL'").
        /// </summary>
        public const string LegacyMercsKey = "UnlockMerc_ALL_MAX";

        /// <summary>The backup token before an unlock code's first write (settings.unlockAll uses "unlockall").</summary>
        public const string CodeBackupReason = "unlockcode";

        /// <summary>&lt;data&gt;/unlocks/codes.json: the unlock codes not granted completely yet.</summary>
        internal const string StoreName = "unlocks";
        internal const string PendingKey = "codes";

        private readonly object gate = new object();
        private readonly ServerContext context;
        private readonly Log log;
        private readonly Queue<Job> queue = new Queue<Job>();
        private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> done = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> status = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> codeStatus =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private List<PendingCode> pending; // codes.json, loaded on first use
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
        /// How many set_profile batches <paramref name="keys"/> take at most (fewer when the ledger holds some): one for
        /// the merc keys among them, then one per unlocks.batchSize of the rest.
        /// </summary>
        public int PlannedBatches(List<string> keys)
        {
            var mercSet = MercSet();
            int mercs = 0;
            foreach (string key in keys) if (mercSet.Contains(key)) mercs++;
            int rest = keys.Count - mercs;
            return (mercs > 0 ? 1 : 0) + (rest + BatchSize - 1) / BatchSize;
        }

        /// <summary>
        /// EventHub handler for <c>storage.profile.changed</c>; runs on the request thread, so it only queues. Only a
        /// save by the game (source "http") of one of the game-profile sections counts: R4 <c>_mcvs.gameProfile</c>, and
        /// the R6 <c>_mcvs.matchers</c>/<c>_mcvs.snapshot</c> Sets of an existing profile's TS load (verify-C3 §2.3). The
        /// same selectors also arrive as R3 section creations during a new player's first load: <see cref="Grant"/>
        /// then finds no game profile yet and waits for the next save. Queues unlockAll (when on) and every unfinished
        /// unlock code of the account.
        /// </summary>
        public void OnProfileChanged(object payload)
        {
            var change = payload as Dictionary<string, object>;
            if (change == null) return;
            if (!string.Equals(JsonValue.GetString(change, "source"), "http", StringComparison.Ordinal)) return;
            if (JsonValue.GetBool(change, "deleted")) return;
            if (!IsTriggerSelector(JsonValue.GetString(change, "selector"))) return;
            string account = JsonValue.GetString(change, "account");
            if (string.IsNullOrEmpty(account)) return;
            if (Enabled) Enqueue(account);
            EnqueuePendingCodes(account);
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
                if (done.Contains(accountId)) return false;
                if (!QueueLocked(new Job { Account = accountId })) return false;
                status[accountId] = "queued";
                return true;
            }
        }

        /// <summary>
        /// Queues the grant of a redeemed unlock code (<paramref name="code"/> as redeemed, normalised: UNLOCKCAMOS2) for
        /// <paramref name="accountId"/> and records it in codes.json until it is complete, so a restart carries on with it
        /// at the account's next game-profile save. False when that (account, code) is already queued.
        /// </summary>
        public bool EnqueueCode(string accountId, string code, List<string> keys)
        {
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(code) || keys == null) throw new ArgumentException("account, code and keys required");
            lock (gate)
            {
                AddPendingLocked(accountId, code);
                if (!QueueLocked(new Job { Account = accountId, Code = code, Keys = new List<string>(keys) })) return false;
                SetCodeStatusLocked(accountId, code, "queued");
                return true;
            }
        }

        /// <summary>
        /// Per-account state of settings.unlockAll for diagnostics: "queued", "waiting: ..." (not ready yet, or waiting
        /// for the alert stream), "granting n/m", "done (...)", "failed: ...".
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

        /// <summary>The unlock codes of this run, {account: {code: state}} with the states of <see cref="Status"/>.</summary>
        public Dictionary<string, object> CodeStatus()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            lock (gate)
            {
                foreach (var kv in codeStatus)
                {
                    var codes = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var c in kv.Value) codes[c.Key] = c.Value;
                    result[kv.Key] = codes;
                }
            }
            return result;
        }

        /// <summary>The unlock codes not granted completely yet (codes.json): [{account, code, since}].</summary>
        public List<object> PendingCodes()
        {
            var result = new List<object>();
            lock (gate)
            {
                foreach (PendingCode p in PendingLocked()) result.Add(p.ToJson());
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
            return GrantLoop(accountId, AllKeys(), "unlockAll", "unlockall", text => SetStatus(accountId, text), out complete);
        }

        /// <summary>
        /// As <see cref="Grant"/> for the keys of one redeemed unlock code (<paramref name="reason"/>, e.g. UNLOCKCAMOS):
        /// the merc keys among them first and alone, the rest in unlocks.batchSize batches, a set_profile alert after
        /// each, the stream wait, a backup ("unlockcode") before the first write; keys the ledger already holds are
        /// skipped, so a numbered repeat (UNLOCKCAMOS2) writes nothing. Synchronous (the worker thread and tests call it).
        /// </summary>
        public int GrantKeys(string accountId, List<string> keys, string reason)
        {
            bool complete;
            return GrantKeys(accountId, keys, reason, out complete);
        }

        private int GrantKeys(string accountId, List<string> keys, string reason, out bool complete)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in keys ?? new List<string>())
                if (!string.IsNullOrEmpty(key) && seen.Add(key)) ordered.Add(key);
            return GrantLoop(accountId, ordered, "unlock code " + reason, CodeBackupReason,
                       text => { lock (gate) SetCodeStatusLocked(accountId, reason, text); }, out complete);
        }

        /// <summary>The loop of <see cref="Grant"/> and <see cref="GrantKeys"/> for the keys <paramref name="all"/>, in order.</summary>
        private int GrantLoop(string accountId, List<string> all, string label, string backupReason, Action<string> setStatus, out bool complete)
        {
            complete = false;
            IProfileAccess profiles = context.Features.Get<IProfileAccess>();
            if (profiles == null)
            {
                log.Warn(label + ": the storage module is not available; nothing granted to " + accountId);
                complete = true;
                return 0;
            }
            string notReady = NotReadyReason(profiles, accountId);
            if (notReady != null)
            {
                setStatus("waiting: " + notReady);
                log.Info(label + ": " + accountId + ": " + notReady + "; trying again at the next game-profile save");
                return 0;
            }

            HashSet<string> mercSet = MercSet();
            List<string> mercs = all.FindAll(mercSet.Contains); // UnlockMerc_<id>..., ModTier_<id>_4..., in grant order
            Dictionary<string, object> ledger;
            List<string> missing = Missing(profiles, accountId, all, mercSet, out ledger);
            if (missing.Count == 0)
            {
                setStatus("done (nothing missing)");
                log.Info(label + ": " + accountId + ": done, nothing missing (all " + all.Count + " keys already in the inventory ledger)");
                complete = true;
                return 0;
            }

            int total = missing.Count;
            log.Info(label + ": granting " + total + " of " + all.Count + " unlock keys to " + accountId + " through the inventory ledger");
            if (WaitUntilListening(accountId, label, setStatus)) return 0; // server stopping

            // Safety net: in a normal run every batch adds keys for good; a ledger that keeps losing them is a bug.
            int maxBatches = 2 * (1 + (total + BatchSize - 1) / BatchSize) + 4;
            int written = 0;
            int batchNo = 0;
            for (; ; batchNo++)
            {
                if (batchNo > 0)
                {
                    if (Wait(BatchDelayMs)) return written; // server stopping: the next save picks up the rest
                    missing = Missing(profiles, accountId, all, mercSet, out ledger);
                    if (missing.Count == 0) break;
                }
                if (batchNo >= maxBatches)
                {
                    log.Warn(label + ": " + accountId + ": the inventory ledger keeps losing keys (" + missing.Count
                             + " missing after " + batchNo + " batches); giving up for this run");
                    setStatus("failed: the inventory ledger keeps losing keys");
                    return written;
                }

                List<string> missingMercs = missing.FindAll(mercSet.Contains);
                List<string> batch = missingMercs.Count > 0 ? missingMercs : missing.GetRange(0, Num.Min(BatchSize, missing.Count));
                if (batchNo > 0 && missingMercs.Count > 0)
                    log.Warn(label + ": " + accountId + ": " + string.Join(", ", missingMercs.ToArray()) + " missing from the ledger again "
                             + "(replaced by an R3 section creation or edited by hand); writing it again first, alone (verify-C5 (c))");

                // Every merge starts with the merc unlock, at its current count (a no-op while it is in the ledger): if
                // the section is replaced between our read and this write, the merc unlock is still recreated ahead of
                // the batch, so the TS never sees a merc-dependent key without it.
                var patch = new Dictionary<string, object>(StringComparer.Ordinal);
                if (Count(ledger, LegacyMercsKey) < 1)
                    foreach (string key in mercs) patch[key] = Num.Max(Count(ledger, key), 1);
                foreach (string key in batch) patch[key] = 1L;
                profiles.Merge(accountId, "inventory", patch, written == 0 ? backupReason : null);
                written += batch.Count;

                var alert = new Dictionary<string, object>(StringComparer.Ordinal);
                alert["type"] = "set_profile"; // OP/AlertType.cs SetProfile
                var push = new Dictionary<string, object>(StringComparer.Ordinal);
                push["to"] = accountId;
                push["alert"] = alert;
                context.Events.Publish(AlertHub.PushTopic, push);

                int granted = Num.Max(0, total - (missing.Count - batch.Count));
                setStatus("granting " + granted.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture));
                log.Debug(label + ": " + accountId + ": " + granted + "/" + total + " keys granted, set_profile alert sent");
            }
            complete = true;
            setStatus("done (" + written.ToString(CultureInfo.InvariantCulture) + " keys written)");
            log.Info(label + ": " + accountId + ": done, " + written + " keys written in " + batchNo + " batch(es) ("
                     + (all.Count - total) + " already in the inventory ledger)");
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
        /// The keys of <paramref name="all"/> (in order) that the ledger does not hold with a count of at least 1 (the
        /// merc group's keys count as held when the ledger holds <see cref="LegacyMercsKey"/>);
        /// <paramref name="ledger"/> is the ledger as read (null when absent).
        /// </summary>
        private static List<string> Missing(IProfileAccess profiles, string accountId, List<string> all, HashSet<string> mercSet,
                                            out Dictionary<string, object> ledger)
        {
            ledger = profiles.Read(accountId, "inventory") as Dictionary<string, object>;
            bool legacy = Count(ledger, LegacyMercsKey) >= 1;
            var missing = new List<string>();
            foreach (string key in all)
                if (Count(ledger, key) < 1 && !(legacy && mercSet.Contains(key))) missing.Add(key);
            return missing;
        }

        private static long Count(Dictionary<string, object> ledger, string key)
        {
            object value;
            return ledger != null && ledger.TryGetValue(key, out value) ? JsonValue.AsLong(value) ?? 0 : 0;
        }

        /// <summary>The "mercs" group: every merc unlock, then every merc's mods.</summary>
        private HashSet<string> MercSet()
        {
            return new HashSet<string>(CouponCatalog.Load(context.Content, context.Settings, log).Group(MercsGroup), StringComparer.Ordinal);
        }

        /// <summary>
        /// Before the first batch: waits (at most unlocks.streamWaitSeconds) until the player has an alert stream that
        /// takes set_profile, i.e. the TS's AlertsComponent is listening, then unlocks.startDelaySeconds more so the TS's
        /// own start-up diff and its PurchaseCompleted handler are in place (verify-C5 (c) subscription race). Without
        /// a stream the keys are written anyway: the TS diffs them at its next start. True when the server is stopping.
        /// </summary>
        private bool WaitUntilListening(string accountId, string label, Action<string> setStatus)
        {
            AlertHub hub = context.Features.Get<AlertHub>(); // null without the alerts module (tests)
            string fedId = McvsConstants.FedIdPrefix + accountId;
            int waitMs = StreamWaitMs;
            if (hub != null && waitMs > 0 && hub.ListenerCount(fedId, "set_profile") == 0)
            {
                setStatus("waiting: for the player's alert stream");
                int started = Environment.TickCount;
                while (hub.ListenerCount(fedId, "set_profile") == 0)
                {
                    if (unchecked(Environment.TickCount - started) >= waitMs)
                    {
                        log.Warn(label + ": no alert stream of " + fedId + " takes set_profile after " + waitMs / 1000
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
                Job job;
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
                    job = queue.Dequeue();
                }
                try
                {
                    if (job.Code == null) RunUnlockAll(job);
                    else RunCode(job);
                }
                catch (Exception ex)
                {
                    // Never let an exception escape a server thread (it would kill the game). Retried on the next save.
                    log.Error((job.Code == null ? "unlockAll" : "unlock code " + job.Code) + ": granting to " + job.Account + " failed", ex);
                    lock (gate)
                    {
                        queued.Remove(job.Id);
                        if (job.Code == null) status[job.Account] = "failed: " + ex.Message;
                        else SetCodeStatusLocked(job.Account, job.Code, "failed: " + ex.Message);
                    }
                }
            }
        }

        private void RunUnlockAll(Job job)
        {
            bool complete;
            Grant(job.Account, out complete);
            lock (gate)
            {
                queued.Remove(job.Id);
                if (complete) done.Add(job.Account); // otherwise the next game-profile save queues it again
            }
        }

        private void RunCode(Job job)
        {
            List<string> keys = job.Keys;
            if (keys == null)
            {
                // Resumed after a restart: the code's keys as the catalog defines them now.
                CouponMatch match = CouponCatalog.Load(context.Content, context.Settings, log).Match(job.Code);
                if (match == null || match.Delivery != CouponCatalog.LedgerDelivery)
                {
                    log.Warn("unlock code " + job.Code + ": " + job.Account + ": the code is no longer an unlock code of this server "
                             + "(settings changed since it was redeemed); the rest of it is dropped");
                    lock (gate)
                    {
                        queued.Remove(job.Id);
                        RemovePendingLocked(job.Account, job.Code);
                        SetCodeStatusLocked(job.Account, job.Code, "failed: the code is no longer defined");
                    }
                    return;
                }
                keys = match.Keys;
            }
            bool complete;
            GrantKeys(job.Account, keys, job.Code, out complete);
            lock (gate)
            {
                queued.Remove(job.Id);
                if (complete) RemovePendingLocked(job.Account, job.Code); // otherwise the next game-profile save queues it again
            }
        }

        /// <summary>Queues every unfinished code of <paramref name="accountId"/> (codes.json) that is not queued already.</summary>
        private void EnqueuePendingCodes(string accountId)
        {
            lock (gate)
            {
                foreach (PendingCode p in PendingLocked())
                {
                    if (!string.Equals(p.Account, accountId, StringComparison.Ordinal)) continue;
                    if (QueueLocked(new Job { Account = p.Account, Code = p.Code })) SetCodeStatusLocked(p.Account, p.Code, "queued (unfinished)");
                }
            }
        }

        /// <summary>Adds the job unless one with its id is queued; starts the worker. Under <see cref="gate"/>.</summary>
        private bool QueueLocked(Job job)
        {
            if (!queued.Add(job.Id)) return false;
            queue.Enqueue(job);
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

        // ------------------------------------------------------------------ codes.json

        private List<PendingCode> PendingLocked()
        {
            if (pending != null) return pending;
            pending = new List<PendingCode>();
            // Not created before the first code: a server that never grants one has no unlocks folder.
            if (!System.IO.File.Exists(context.GetDataPath(StoreName + "/" + PendingKey + ".json"))) return pending;
            try
            {
                Dictionary<string, object> doc = context.OpenStore(StoreName).Read(PendingKey);
                foreach (object o in JsonValue.GetArray(doc, "pending") ?? new List<object>())
                {
                    PendingCode p = PendingCode.FromJson(o as Dictionary<string, object>);
                    if (p != null) pending.Add(p);
                }
                if (pending.Count > 0)
                    log.Info("unlock codes not granted completely before the last stop: " + pending.Count
                             + "; each carries on at its account's next game-profile save");
            }
            catch (Exception ex)
            {
                log.Warn("unlocks: " + StoreName + "/" + PendingKey + ".json is unreadable (" + ex.Message + "); unfinished codes are not resumed");
            }
            return pending;
        }

        private void AddPendingLocked(string accountId, string code)
        {
            List<PendingCode> list = PendingLocked();
            foreach (PendingCode p in list)
                if (p.Account == accountId && p.Code == code) return;
            list.Add(new PendingCode { Account = accountId, Code = code, Since = TimeFormat.Iso8601(context.Clock.UtcNow) });
            SavePendingLocked();
        }

        private void RemovePendingLocked(string accountId, string code)
        {
            List<PendingCode> list = PendingLocked();
            if (list.RemoveAll(p => p.Account == accountId && p.Code == code) > 0) SavePendingLocked();
        }

        private void SavePendingLocked()
        {
            try
            {
                var items = new List<object>();
                foreach (PendingCode p in pending) items.Add(p.ToJson());
                var doc = new Dictionary<string, object>(StringComparer.Ordinal);
                doc["format"] = 1L;
                doc["pending"] = items;
                context.OpenStore(StoreName).Write(PendingKey, doc);
            }
            catch (Exception ex)
            {
                // The grant still runs; only a restart before it ends would lose the rest of the code.
                log.Warn("unlocks: could not save " + StoreName + "/" + PendingKey + ".json (" + ex.Message + ")");
            }
        }

        private void SetStatus(string accountId, string text)
        {
            lock (gate) status[accountId] = text;
        }

        private void SetCodeStatusLocked(string accountId, string code, string text)
        {
            Dictionary<string, string> codes;
            if (!codeStatus.TryGetValue(accountId, out codes))
            {
                codes = new Dictionary<string, string>(StringComparer.Ordinal);
                codeStatus[accountId] = codes;
            }
            codes[code] = text;
        }

        /// <summary>Waits <paramref name="ms"/>; true when the server is stopping.</summary>
        private bool Wait(int ms)
        {
            return ms > 0 ? context.StopSignal.WaitOne(ms) : context.IsStopping;
        }

        /// <summary>settings.unlockAll for an account (<see cref="Code"/> null) or one redeemed unlock code.</summary>
        private sealed class Job
        {
            public string Account;
            public string Code;
            public List<string> Keys; // a code's keys; null after a restart (taken from the catalog again)

            public string Id
            {
                get { return Code == null ? Account : Account + " " + Code; }
            }
        }

        /// <summary>One entry of codes.json.</summary>
        private sealed class PendingCode
        {
            public string Account;
            public string Code;
            public string Since;

            public Dictionary<string, object> ToJson()
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["account"] = Account;
                o["code"] = Code;
                o["since"] = Since;
                return o;
            }

            public static PendingCode FromJson(Dictionary<string, object> o)
            {
                string account = JsonValue.GetString(o, "account");
                string code = JsonValue.GetString(o, "code");
                if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(code)) return null;
                return new PendingCode { Account = account, Code = CouponCatalog.Normalize(code), Since = JsonValue.GetString(o, "since") };
            }
        }
    }
}
