using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Social.LiveOps;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>One built CRM document: the tree (for selectors) and its serialized form.</summary>
    public sealed class CrmDocument
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, string> selectorJson = new Dictionary<string, string>(StringComparer.Ordinal);

        public CrmDocument(Dictionary<string, object> root, bool hasPreconfiguration, List<string> problems, DateTime builtUtc)
        {
            Root = root;
            Json = MiniJson.Serialize(root);
            HasPreconfiguration = hasPreconfiguration;
            Problems = problems ?? new List<string>();
            BuiltUtc = builtUtc;
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
            omitted = 0;
            var store = JsonValue.AsObject(JsonValue.Get(Root, "offline_store"));
            var prices = JsonValue.AsArray(JsonValue.Get(store, "prices"));
            if (prices == null || items == null || items.Count == 0) return Json;
            var kept = new List<object>(prices.Count);
            foreach (object entry in prices)
            {
                string item = JsonValue.GetString(JsonValue.AsObject(entry), "item");
                if (item != null && items.Contains(item)) omitted++;
                else kept.Add(entry);
            }
            if (omitted == 0) return Json;
            var filteredStore = new Dictionary<string, object>(store, StringComparer.Ordinal);
            filteredStore["prices"] = kept;
            var root = new Dictionary<string, object>(Root, StringComparer.Ordinal);
            root["offline_store"] = filteredStore;
            return MiniJson.Serialize(root);
        }

        public static string NormalizeSelector(string selector)
        {
            return (selector ?? "").Replace('/', '.').Trim('.');
        }
    }

    /// <summary>
    /// Builds and caches the CRM document. The cache key is the effective settings plus whether the preconfiguration is
    /// available, so editing settings (or the data pack appearing after a fresh install) yields a new document.
    /// <para>
    /// Timing matters: the TS waits only 1 s for <c>configs/users/me/game</c> on its game-server session before using
    /// its defaults (WaitForCRM, verify-C5 (a); IsDevelopment would then stay true), and the GS waits forever for a 200
    /// (verify-C4 §3.2). <see cref="StartWarmup"/> therefore loads the preconfiguration and builds the document on a
    /// background thread at startup, and selector requests wait only briefly for the preconfiguration.
    /// </para>
    /// </summary>
    public sealed class CrmDocumentCache
    {
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

        /// <summary>Loads the preconfiguration and builds the document on a background thread. Never throws.</summary>
        public void StartWarmup()
        {
            lock (gate) warmupStarted = true;
            var thread = new Thread(() =>
            {
                try
                {
                    string ignored;
                    LoadPreconfiguration(CurrentOptions(out ignored));
                    firstAttemptDone.Set();
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

            lock (gate)
            {
                string key = optionsFingerprint + "|" + (withPre ? "pre" : "nopre") + "|" + generation + "|" + (liveOps == null ? "-" : liveOps.Key);
                if (cached != null && cachedKey == key) return cached;

                var content = CrmContent.Load(context.Content, log);
                var root = CrmDocumentBuilder.Build(withPre ? preconfiguration.GetCopy() : null, content, o, log, liveOps);
                var problems = CrmDocumentValidator.Validate(root);
                foreach (string p in problems) log.Warn("CRM document: " + p);
                cached = new CrmDocument(root, withPre, problems, context.Clock.UtcNow);
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
                         + ", " + cached.Json.Length + " chars" + (problems.Count > 0 ? ", " + problems.Count + " problem(s)" : ""));
                return cached;
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
