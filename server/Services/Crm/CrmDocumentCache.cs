using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// One built CRM document: the tree (for selectors), its serialized form and its collector part
    /// (<see cref="Collector"/>), from which the per-player documents are made (<see cref="JsonFor(ICollection{string}, IList{FieldPatch})"/>).
    /// The tree is never changed after construction.
    /// </summary>
    public sealed class CrmDocument
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, string> selectorJson = new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, object> layerValues;

        public CrmDocument(Dictionary<string, object> root, bool hasPreconfiguration, List<string> problems, DateTime builtUtc)
            : this(root, hasPreconfiguration, problems, builtUtc, null)
        {
        }

        /// <param name="collector">What the builder wrote for the collector (null: nothing, as Dormant).</param>
        public CrmDocument(Dictionary<string, object> root, bool hasPreconfiguration, List<string> problems, DateTime builtUtc,
                           CrmCollector collector)
        {
            Root = root;
            Json = MiniJson.Serialize(root);
            HasPreconfiguration = hasPreconfiguration;
            Problems = problems ?? new List<string>();
            BuiltUtc = builtUtc;
            Collector = collector;
        }

        /// <summary>The collector part (status, phase, layer, per-player mirror); null for a document built without one.</summary>
        public CrmCollector Collector { get; private set; }

        /// <summary>What this document carries for the collector: Dormant (nothing), Guard or Active.</summary>
        public CollectorPhase CollectorPhase
        {
            get { return Collector == null ? CollectorPhase.Dormant : Collector.Phase; }
        }

        /// <summary>
        /// The <c>game._ServerConfig.default._OverrideTables</c> of every per-player document while the collector is
        /// active: the static mirror with the collector's cells replaced by the guard (D10, §2.9.3). Null otherwise (the
        /// per-player documents then carry the static mirror).
        /// </summary>
        public Dictionary<string, object> PlayerMirrorTables
        {
            get { return Collector == null || Collector.Phase != CollectorPhase.Active ? null : Collector.PlayerMirrorTables; }
        }

        public Dictionary<string, object> Root { get; private set; }

        public string Json { get; private set; }

        public bool HasPreconfiguration { get; private set; }

        public List<string> Problems { get; private set; }

        public DateTime BuiltUtc { get; private set; }

        /// <summary>
        /// The JSON of the sub-tree at a Hestia selector ("game", "game.CrashlyticsParams.Enable"; '/' also separates).
        /// The GS parses the body of <c>configs/users/me/game</c> directly as the game object (report 04 §5.1).
        /// </summary>
        public bool TryGetSelectorJson(string selector, out string json)
        {
            string path = NormalizeSelector(selector);
            if (path.Length == 0)
            {
                json = Json;
                return true;
            }
            lock (gate)
            {
                if (selectorJson.TryGetValue(path, out json)) return json != null;
                object value;
                bool found;
                try
                {
                    found = JsonPath.TryGet(Root, path, out value);
                }
                catch (ArgumentException)
                {
                    found = false;
                    value = null;
                }
                json = found ? MiniJson.Serialize(value) : null;
                if (selectorJson.Count < 64) selectorJson[path] = json;
                return found;
            }
        }

        /// <summary>
        /// <see cref="Json"/> without the <c>offline_store.prices</c> entries whose <c>item</c> is in
        /// <paramref name="items"/> (<see cref="CrmOwnedStoreItems"/>); the same text when none matches.
        /// </summary>
        public string JsonWithout(ICollection<string> items, out int omitted)
        {
            var root = new Dictionary<string, object>(Root, StringComparer.Ordinal);
            omitted = FilterStore(root, items);
            return omitted == 0 ? Json : MiniJson.Serialize(root);
        }

        /// <summary>
        /// A player's document (docs/design/economy-collector.md §2.9.4): <see cref="Json"/> without the
        /// <c>offline_store.prices</c> entries named in <paramref name="omitStoreItems"/> (as <see cref="JsonWithout"/>),
        /// with the collector's field changers of <c>game._OverrideTables</c> replaced by <paramref name="patches"/>
        /// (<see cref="CollectorPlan.PlayerCells(CollectorContent, CollectorOptions, PlayerInventory, LiveOpsSnapshot)"/>)
        /// and the mirror replaced by <see cref="PlayerMirrorTables"/>. The patches apply only while this document carries
        /// the collector layer (<see cref="CollectorPhase"/> Active); a patched key is replaced only where the document
        /// still holds the collector's own static value (a <c>crm.overlay</c> cell on the same key wins). If the patched
        /// tree fails <see cref="CrmDocumentValidator.CheckCollector"/>, the static document is served (store filtered).
        /// Dictionaries along the patched paths are copied: the cached tree is never changed.
        /// </summary>
        public string JsonFor(ICollection<string> omitStoreItems, IList<FieldPatch> patches)
        {
            int omitted;
            string problem;
            return JsonFor(omitStoreItems, patches, out omitted, out problem);
        }

        /// <summary><see cref="JsonFor(ICollection{string}, IList{FieldPatch})"/>, also returning how many store entries were
        /// left out and why the patches were not used (null when they were, or when there were none).</summary>
        public string JsonFor(ICollection<string> omitStoreItems, IList<FieldPatch> patches, out int omitted, out string problem)
        {
            problem = null;
            if (patches == null || CollectorPhase != CollectorPhase.Active) return JsonWithout(omitStoreItems, out omitted);

            var root = new Dictionary<string, object>(Root, StringComparer.Ordinal);
            omitted = FilterStore(root, omitStoreItems);
            var game = CopyChild(root, "game");
            var tables = CopyChild(game, "_OverrideTables");
            Dictionary<string, object> statics = LayerValues();
            var cells = new List<CollectorCell>();
            var patched = new HashSet<string>(StringComparer.Ordinal);
            var copies = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (FieldPatch patch in patches)
            {
                Dictionary<string, object> db, table;
                if (!copies.TryGetValue(patch.Database, out db)) copies[patch.Database] = db = CopyChild(tables, patch.Database);
                if (!copies.TryGetValue(patch.Database + "\n" + patch.Table, out table)) copies[patch.Database + "\n" + patch.Table] = table = CopyChild(db, patch.Table);
                Dictionary<string, object> changer = Patched(JsonValue.AsObject(JsonValue.Get(table, patch.Changer)), patch, statics);
                if (changer.Count > 0) table[patch.Changer] = changer;
                else JsonPath.RemoveKey(table, patch.Changer);
                if (table.Count == 0)
                {
                    JsonPath.RemoveKey(db, patch.Table);
                    copies.Remove(patch.Database + "\n" + patch.Table);
                }
                patched.Add(patch.Table + "\n" + patch.Changer);
                foreach (var kv in changer) cells.Add(new CollectorCell(patch.Table, patch.Index, patch.Field, kv.Key, kv.Value));
            }
            foreach (CollectorCell cell in Collector.GameCells)
                if (!patched.Contains(cell.Table + "\n" + cell.Changer)) cells.Add(cell);
            List<string> problems = CrmDocumentValidator.CheckCollector(tables, cells, "per-player game._OverrideTables");
            if (problems.Count > 0)
            {
                problem = problems[0];
                return JsonWithout(omitStoreItems, out omitted);
            }
            Dictionary<string, object> mirror = PlayerMirrorTables;
            if (mirror != null) CopyChild(CopyChild(game, "_ServerConfig"), "default")["_OverrideTables"] = mirror;
            return MiniJson.Serialize(root);
        }

        /// <summary>
        /// The cells of one patched changer: every key of the document's changer and of the patch, in ascending numeric
        /// order; a key the collector owns (the document holds the layer's static value there, or neither has one) takes
        /// the patch's value or goes; any other key keeps the document's value (crm.overlay wins).
        /// </summary>
        private static Dictionary<string, object> Patched(Dictionary<string, object> current, FieldPatch patch, Dictionary<string, object> statics)
        {
            var keys = new List<string>();
            if (current != null) keys.AddRange(current.Keys);
            foreach (string key in patch.Cells.Keys)
                if (current == null || !current.ContainsKey(key)) keys.Add(key);
            keys.Sort(CompareKeys);
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                object inDocument = null, inLayer;
                bool documentHas = current != null && current.TryGetValue(key, out inDocument);
                bool layerHas = statics.TryGetValue(LayerKey(patch.Table, patch.Changer, key), out inLayer);
                bool ours = documentHas ? layerHas && JsonValue.DeepEquals(inDocument, inLayer) : !layerHas;
                object value;
                if (!ours)
                {
                    if (documentHas) result[key] = inDocument;
                }
                else if (patch.Cells.TryGetValue(key, out value))
                {
                    result[key] = value;
                }
            }
            return result;
        }

        /// <summary>Ids in ascending numeric order ("2" before "10"), anything else after them, ordinally.</summary>
        private static int CompareKeys(string a, string b)
        {
            bool da = IsDigits(a), db = IsDigits(b);
            if (da && db)
            {
                string ta = a.TrimStart('0'), tb = b.TrimStart('0');
                if (ta.Length != tb.Length) return ta.Length.CompareTo(tb.Length);
                int c = string.CompareOrdinal(ta, tb);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            }
            if (da != db) return da ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char ch in s)
                if (ch < '0' || ch > '9') return false;
            return true;
        }

        /// <summary>The layer's static values by table/changer/key (built once).</summary>
        private Dictionary<string, object> LayerValues()
        {
            lock (gate)
            {
                if (layerValues != null) return layerValues;
                var values = new Dictionary<string, object>(StringComparer.Ordinal);
                if (Collector != null && Collector.Layer != null)
                    foreach (CollectorCell cell in Collector.Layer.Game) values[LayerKey(cell.Table, cell.Changer, cell.Key)] = cell.Value;
                layerValues = values;
                return values;
            }
        }

        private static string LayerKey(string table, string changer, string key)
        {
            return table + "\n" + changer + "\n" + key;
        }

        /// <summary>Leaves the named entries out of the <c>offline_store.prices</c> of <paramref name="root"/> (a copy of
        /// <see cref="Root"/>), copying the store; returns how many were left out (0: root unchanged).</summary>
        private int FilterStore(Dictionary<string, object> root, ICollection<string> items)
        {
            var store = JsonValue.AsObject(JsonValue.Get(Root, "offline_store"));
            var prices = JsonValue.AsArray(JsonValue.Get(store, "prices"));
            if (prices == null || items == null || items.Count == 0) return 0;
            var kept = new List<object>(prices.Count);
            int omitted = 0;
            foreach (object entry in prices)
            {
                string item = JsonValue.GetString(JsonValue.AsObject(entry), "item");
                if (item != null && items.Contains(item)) omitted++;
                else kept.Add(entry);
            }
            if (omitted == 0) return 0;
            var filteredStore = new Dictionary<string, object>(store, StringComparer.Ordinal);
            filteredStore["prices"] = kept;
            root["offline_store"] = filteredStore;
            return omitted;
        }

        /// <summary>A shallow copy of the object at <paramref name="key"/> (a new object when there is none), stored in
        /// <paramref name="parent"/>, which must itself be a copy.</summary>
        private static Dictionary<string, object> CopyChild(Dictionary<string, object> parent, string key)
        {
            object value;
            var child = parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
            var copy = child == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(child, StringComparer.Ordinal);
            parent[key] = copy;
            return copy;
        }

        public static string NormalizeSelector(string selector)
        {
            return (selector ?? "").Replace('/', '.').Trim('.');
        }
    }

    /// <summary>
    /// Builds and caches the CRM document. The cache key is the effective settings plus whether the preconfiguration is
    /// available, the live-ops schedule step, the inbox item (<see cref="CrmNewsfeed"/>) and the collector's phase and
    /// content (<see cref="CollectorRuntime"/>, decided per request from the clock), so editing settings (or the data pack
    /// appearing after a fresh install, a new week, or <c>collector.since</c> passing) yields a new document.
    /// <para>
    /// Timing matters: the TS waits only 1 s for <c>configs/users/me/game</c> on its game-server session before using
    /// its defaults (WaitForCRM, verify-C5 (a); IsDevelopment would then stay true), and the GS waits forever for a 200
    /// (verify-C4 §3.2). <see cref="StartWarmup"/> therefore loads the preconfiguration and builds the document on a
    /// background thread at startup, and selector requests wait only briefly for the preconfiguration.
    /// </para>
    /// </summary>
    public sealed class CrmDocumentCache
    {
        /// <summary>How long the warm-up waits for the admin module's portal (<see cref="WaitForAdminPortal"/>).</summary>
        public const int PortalWaitMs = 2000;

        private const int PortalPollMs = 10;

        /// <summary>
        /// The collector's phase is decided this far ahead of the clock, as <see cref="LiveOpsService.Snapshot"/> decides the
        /// events: a document served a moment before a boundary (collector.since, a season start) already announces the
        /// next window's events, so it must already carry what those events hand out (§2.12).
        /// </summary>
        private const int BoundaryLeadSeconds = 2;

        private readonly ServerContext context;
        private readonly Log log;
        private readonly CrmPreconfiguration preconfiguration;
        private readonly ManualResetEvent firstAttemptDone = new ManualResetEvent(false);
        private readonly object gate = new object();
        private string cachedKey;
        private CrmDocument cached;
        private int generation;
        private string optionsKey;
        private CrmOptions options;
        private bool warmupStarted;
        private bool retryRunning;
        private bool retried;
        private int lastRetryTick;

        public CrmDocumentCache(ServerContext context, CrmPreconfiguration preconfiguration, Log log)
        {
            this.context = context;
            this.preconfiguration = preconfiguration ?? new CrmPreconfiguration();
            this.log = log ?? Log.Null;
        }

        public CrmPreconfiguration Preconfiguration { get { return preconfiguration; } }

        /// <summary>The CRM options of the current settings (parsed again only when the settings changed).</summary>
        public CrmOptions Options
        {
            get
            {
                string ignored;
                return CurrentOptions(out ignored);
            }
        }

        /// <summary>Loads the preconfiguration and builds the document on a background thread. Never throws.</summary>
        public void StartWarmup()
        {
            lock (gate) warmupStarted = true;
            var thread = new Thread(() =>
            {
                try
                {
                    string ignored;
                    CrmOptions o = CurrentOptions(out ignored);
                    LoadPreconfiguration(o);
                    firstAttemptDone.Set();
                    WaitForAdminPortal(o);
                    Get(0);
                }
                catch (Exception ex)
                {
                    log.Error("CRM warm-up failed", ex);
                }
                finally
                {
                    firstAttemptDone.Set();
                }
            });
            thread.IsBackground = true;
            thread.Name = "mcvs-crm-warmup";
            thread.Start();
        }

        /// <summary>
        /// The inbox item depends on the admin module's portal, which registers after this module (ServiceRegistry order),
        /// and it is part of the document's key. Without a data pack (or with <c>crm.usePreconfiguration</c> off) the
        /// warm-up gets here before the portal exists: a document built now would lack the item, and the first request
        /// would rebuild the whole document on its own thread, the very cost the warm-up is there to take off the TS's
        /// 1 s WaitForCRM. So while <c>crm.newsfeedLink</c> is on, wait up to <see cref="PortalWaitMs"/> for the portal
        /// (module registration takes milliseconds); a server without the admin module builds without the item after
        /// that. Requests do not wait for this: the preconfiguration is already marked done.
        /// </summary>
        private void WaitForAdminPortal(CrmOptions o)
        {
            if (!o.NewsfeedLink) return;
            int start = Environment.TickCount;
            while (context.Features.Get<IAdminPortal>() == null)
            {
                // TickCount wraps: compare the difference.
                if (unchecked(Environment.TickCount - start) >= PortalWaitMs) return;
                if (context.StopSignal.WaitOne(PortalPollMs)) return;
            }
        }

        /// <summary>Forgets the cached document (content overrides edited on disk, /_mcvs/crm/reload).</summary>
        public void Invalidate()
        {
            lock (gate)
            {
                generation++;
                cached = null;
                cachedKey = null;
            }
        }

        /// <summary>
        /// The current document. Waits up to <paramref name="preconfigWaitMs"/> for the startup load of the
        /// preconfiguration; when it is still not there, the document is built without it (and rebuilt later).
        /// </summary>
        public CrmDocument Get(int preconfigWaitMs)
        {
            string optionsFingerprint;
            CrmOptions o = CurrentOptions(out optionsFingerprint);
            if (o.UsePreconfiguration)
            {
                bool started;
                lock (gate) started = warmupStarted;
                if (!started)
                {
                    LoadPreconfiguration(o);   // no warm-up thread (tests, tools): load inline
                }
                else
                {
                    if (preconfigWaitMs > 0 && !preconfiguration.IsLoaded) firstAttemptDone.WaitOne(preconfigWaitMs);
                    // A missing file (fresh install) is retried on a background thread: parsing the 2 MB file on a
                    // request thread could push the TS past its 1 s WaitForCRM. This request uses what we have.
                    if (preconfiguration.IsLoaded && !preconfiguration.IsFinal) RetryInBackground(o);
                }
            }
            bool withPre = o.UsePreconfiguration && preconfiguration.HasDocument;

            // Live ops: the event ids, expiry and weekly modifier change with the clock; the key changes with them,
            // so the document is rebuilt once per schedule step (a day or a season), not per request.
            LiveOpsSnapshot liveOps = null;
            try
            {
                liveOps = LiveOpsService.For(context).Snapshot(context.Clock.UtcNow);
            }
            catch (Exception ex)
            {
                log.Error("CRM: live ops unavailable for this document", ex);
            }

            // The inbox item (design §9.1): its link (the port), the week of its timestamp, and whether it is there at
            // all (crm.newsfeedLink, admin.enabled, and the admin module, which registers after this one: the warm-up
            // waits for it, see WaitForAdminPortal) are part of the key, so the document is rebuilt when any changes.
            List<object> newsfeeds = CrmNewsfeed.Build(o, context.Features.Get<IAdminPortal>(), context.Clock.UtcNow);
            string newsKey = MiniJson.Serialize(newsfeeds);

            // The collector (economy-collector.md §2.12): its phase changes with the clock (collector.since), its layer with
            // collector.json; both are in the key. Its options are part of the settings fingerprint.
            string collectorKey;
            CrmCollector collector = CollectorFor(liveOps, out collectorKey);

            lock (gate)
            {
                string key = optionsFingerprint + "|" + (withPre ? "pre" : "nopre") + "|" + generation + "|" + (liveOps == null ? "-" : liveOps.Key)
                             + "|" + newsKey + "|" + collectorKey;
                if (cached != null && cachedKey == key) return cached;

                var content = CrmContent.Load(context.Content, log);
                var root = CrmDocumentBuilder.Build(withPre ? preconfiguration.GetCopy() : null, content, o, log, liveOps, newsfeeds, collector);
                var problems = CrmDocumentValidator.Validate(root);
                foreach (string p in problems) log.Warn("CRM document: " + p);
                cached = new CrmDocument(root, withPre, problems, context.Clock.UtcNow, collector);
                cachedKey = key;
                var store = JsonValue.AsArray(JsonPath.Get(root, "offline_store.prices"));
                log.Info("CRM document built: " + (store == null ? 0 : store.Count) + " offline_store items, preconfiguration "
                         + (withPre ? "from " + preconfiguration.Source : "not used (" + (preconfiguration.Source ?? "disabled") + ")")
                         + ", IsDevelopment=" + (o.DevMenu ? "true" : "false")
                         + ", instant bots " + (o.InstantBots ? "on" : "off")
                         + (liveOps != null && liveOps.Expiry.HasValue
                                ? ", live ops: " + liveOps.EventIds.Count + " event ids, modifier " + (liveOps.Modifier == null ? "none" : liveOps.Modifier.Id)
                                  + ", expires " + TimeFormat.Iso8601(liveOps.Expiry.Value)
                                : "")
                         + ", inbox item " + (newsfeeds.Count > 0 ? "on" : "off")
                         + ", collector " + DescribeCollector(collector)
                         + ", " + cached.Json.Length + " chars" + (problems.Count > 0 ? ", " + problems.Count + " problem(s)" : ""));
                return cached;
            }
        }

        private static string DescribeCollector(CrmCollector c)
        {
            switch (c.Phase)
            {
                case CollectorPhase.Active:
                    return "active (" + c.GameCells.Count + " cells, mirror " + c.MirrorCells.Count + ", per-player mirror "
                           + (c.PlayerMirrorTables != null ? "guard" : "static") + ")";
                case CollectorPhase.Guard:
                    return "guard (" + (c.Failure ?? (c.Status == null ? "" : c.Status.Reason)) + ")";
                default:
                    return "dormant";
            }
        }

        /// <summary>
        /// The collector part of the document served now, and its cache key (phase, content hash or problem). Never throws:
        /// a runtime that fails gives the guard (the crate stays safe).
        /// </summary>
        private CrmCollector CollectorFor(LiveOpsSnapshot liveOps, out string key)
        {
            try
            {
                CollectorRuntime runtime = CollectorRuntime.For(context);
                // since comes from the live-ops snapshot (set whether or not live ops are on), the same value live ops
                // decide the crates with (CratesInWindow), so the layer starts exactly where the crates do (D5). Without a
                // snapshot it is unknown: never Active, and the guard from ShippedSince.
                bool liveOpsOn = liveOps != null && liveOps.On;
                DateTime? since = liveOps == null ? null : liveOps.CollectorSince;
                CollectorStatus status = runtime.Status(context.Clock.UtcNow.AddSeconds(BoundaryLeadSeconds), liveOpsOn, since);
                CollectorContent content = runtime.Content;
                if (status.Phase == CollectorPhase.Active && content == null)
                    status = new CollectorStatus { Phase = CollectorPhase.Guard, Reason = CollectorContent.FileName + " changed while read", Since = status.Since };
                key = "collector:" + status.Phase + "|" + (content != null ? content.Hash : "none:" + runtime.ContentProblem);
                return new CrmCollector
                {
                    Status = status,
                    Content = status.Phase == CollectorPhase.Active ? content : null,
                    Options = runtime.Options,
                };
            }
            catch (Exception ex)
            {
                log.Error("CRM: the collector's state is unavailable; the document carries the guard", ex);
                key = "collector:error";
                return CrmCollector.GuardOnly("the collector's state is unavailable (" + ex.Message + ")");
            }
        }

        private void RetryInBackground(CrmOptions o)
        {
            lock (gate)
            {
                // TickCount wraps (negative after ~25 days of uptime): compare differences, and only once one exists.
                if (retryRunning || (retried && unchecked(Environment.TickCount - lastRetryTick) < preconfiguration.RetryIntervalMs)) return;
                retryRunning = true;
                retried = true;
                lastRetryTick = Environment.TickCount;
            }
            var thread = new Thread(() =>
            {
                try
                {
                    LoadPreconfiguration(o);
                }
                catch (Exception ex)
                {
                    log.Error("CRM preconfiguration retry failed", ex);
                }
                finally
                {
                    lock (gate) retryRunning = false;
                }
            });
            thread.IsBackground = true;
            thread.Name = "mcvs-crm-retry";
            thread.Start();
        }

        private void LoadPreconfiguration(CrmOptions o)
        {
            preconfiguration.Load(context.Content, context.DataDir, o.DataPackDir, log, KeepIapLanguages(o));
        }

        private static string[] KeepIapLanguages(CrmOptions o)
        {
            // The per-language iap catalogues are ~95% of the file; keep only what crm.iap=preconfig could use.
            return o.Iap == IapMode.Preconfig ? new[] { o.IapLanguage ?? "en", "en" } : new string[0];
        }

        /// <summary>The options for the current settings (parsed again only when the settings changed).</summary>
        private CrmOptions CurrentOptions(out string fingerprint)
        {
            string key = MiniJson.Serialize(context.Settings.Snapshot());
            lock (gate)
            {
                if (options == null || optionsKey != key)
                {
                    options = CrmOptions.FromSettings(context.Settings, log);
                    optionsKey = key;
                }
                fingerprint = key;
                return options;
            }
        }
    }
}
