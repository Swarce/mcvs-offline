using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Crm.Collector
{
    /// <summary>What the CRM document carries for the collector (docs/design/economy-collector.md §2.12).</summary>
    public enum CollectorPhase
    {
        /// <summary>No cells at all: documents and events exactly as before the collector existed.</summary>
        Dormant,

        /// <summary>The 5 guard cells (<see cref="CollectorGuard"/>): a stale Cosmetic crate pays VersusCoins.</summary>
        Guard,

        /// <summary>The collector layer: live ops hand out the crate.</summary>
        Active,
    }

    public sealed class CollectorStatus
    {
        public CollectorPhase Phase;

        /// <summary>Why the phase is what it is ("active since …", "economy.collector.enabled is false", …).</summary>
        public string Reason;

        /// <summary>The <c>since</c> the phase was decided with (null: unknown).</summary>
        public DateTime? Since;
    }

    /// <summary>
    /// The one place that decides whether the collector runs (§2.12, D5, D9): the CRM builder asks <see cref="Status"/>,
    /// live ops ask <see cref="CratesInWindow"/>, and both come from the same inputs, so the crate is never handed out
    /// unless the document carries its rows. One instance per server (<see cref="For"/>). Inputs:
    /// <list type="bullet">
    /// <item><see cref="Options"/>: <c>economy.collector.*</c>, parsed once per settings snapshot;</item>
    /// <item><see cref="Content"/>: collector.json through <c>context.Content</c> (the &lt;data&gt;/content override
    /// wins), re-read when its text changes; null with <see cref="ContentProblem"/> when missing, broken or failing §2.3.1;
    /// a read that throws keeps the last read (unreadable before any: null), logged once per failing streak;</item>
    /// <item><see cref="StorePatch"/>: <see cref="ClientCapabilities.StorePatch"/> (§2.13);</item>
    /// <item>from the caller: live ops on, <c>since</c> (liveops.json <c>collector</c> block) and the time;</item>
    /// <item>the latch (<see cref="NoteCratesServed"/>, <see cref="LatchFileName"/> in the data folder): once live ops
    /// served an event carrying the crate, the document is never <c>Dormant</c> again, whatever later happens to
    /// <c>since</c> or the clock (a stale crate can sit in a kept-alive event for weeks, D9).</item>
    /// </list>
    /// </summary>
    public sealed class CollectorRuntime
    {
        /// <summary>The latch file in the data folder (deleted with the server stopped, the server can be Dormant again).</summary>
        public const string LatchFileName = "collector-guard.json";

        private static readonly object ForGate = new object();

        private readonly ServerContext context;
        private readonly Log log;
        private readonly object gate = new object();
        private string optionsKey;
        private CollectorOptions options;
        private bool contentRead;
        private string contentText;
        private CollectorContent content;
        private string contentProblem;
        private bool readFailing;
        private bool latchLoaded;
        private DateTime? servedAt;
        private DateTime? servedWindow;
        private string servedEvent;
        private bool latchWriteFailing;

        public CollectorRuntime(ServerContext context)
        {
            this.context = context;
            log = context.Logger.For("collector");
        }

        /// <summary>The server's shared instance (created on first use).</summary>
        public static CollectorRuntime For(ServerContext context)
        {
            lock (ForGate)
            {
                CollectorRuntime runtime = context.Features.Get<CollectorRuntime>();
                if (runtime != null) return runtime;
                runtime = new CollectorRuntime(context);
                context.Features.Set(runtime);
                return runtime;
            }
        }

        /// <summary>The options of the current settings (parsed again only when <c>economy.collector</c> changed).</summary>
        public CollectorOptions Options
        {
            get
            {
                string key = MiniJson.Serialize(context.Settings.Get(CollectorOptions.SettingsPath));
                lock (gate)
                {
                    if (options == null || !string.Equals(optionsKey, key, StringComparison.Ordinal))
                    {
                        options = CollectorOptions.FromSettings(context.Settings, log);
                        optionsKey = key;
                    }
                    return options;
                }
            }
        }

        /// <summary>collector.json, or null when it is missing, broken or fails §2.3.1 (<see cref="ContentProblem"/>).</summary>
        public CollectorContent Content
        {
            get
            {
                string problem;
                return ReadContent(out problem);
            }
        }

        /// <summary>Why <see cref="Content"/> is null; null when it is valid.</summary>
        public string ContentProblem
        {
            get
            {
                string problem;
                ReadContent(out problem);
                return problem;
            }
        }

        /// <summary>The client's <c>store</c> patch (§2.13): true, false, or null when the host cannot tell (assumed present).</summary>
        public bool? StorePatch
        {
            get
            {
                ClientCapabilities client = context.Features.Get<ClientCapabilities>();
                return client == null ? null : client.StorePatch;
            }
        }

        /// <summary>
        /// Changes whenever an input of <see cref="CratesInWindow"/> other than <c>since</c> does (enabled, content validity,
        /// store patch): live ops include it in their settings fingerprint.
        /// </summary>
        public string InputsKey
        {
            get
            {
                bool? patch = StorePatch;
                return "collector:" + (Options.Enabled ? "on" : "off") + "|" + (Content != null ? "content" : "no-content") + "|patch:"
                       + (patch == null ? "unknown" : patch.Value ? "yes" : "no");
            }
        }

        /// <summary>
        /// The phase of a CRM document served at <paramref name="utcNow"/>:
        /// <list type="bullet">
        /// <item><c>Active</c> ⇔ enabled ∧ live ops on ∧ content valid ∧ store patch not known missing ∧ since known ∧
        /// utcNow ≥ since;</item>
        /// <item><c>Guard</c> ⇔ not Active ∧ (utcNow ≥ guardSince ∨ crates were served, <see cref="CratesServedAt"/>),
        /// guardSince = <see cref="GuardSince"/>(since);</item>
        /// <item><c>Dormant</c> otherwise.</item>
        /// </list>
        /// </summary>
        public CollectorStatus Status(DateTime utcNow, bool liveOpsOn, DateTime? since)
        {
            CollectorOptions o = Options;
            string problem;
            CollectorContent c = ReadContent(out problem);
            bool? patch = StorePatch;

            string why = null;
            if (!o.Enabled) why = "economy.collector.enabled is false";
            else if (!liveOpsOn) why = "live ops are off";
            else if (c == null) why = CollectorContent.FileName + ": " + problem;
            else if (patch == false) why = "store patch missing";
            else if (!since.HasValue) why = "no collector.since (liveops.json has no valid collector block)";
            else if (utcNow < since.Value) why = "before collector.since " + TimeFormat.Iso8601(since.Value);
            if (why == null)
                return new CollectorStatus { Phase = CollectorPhase.Active, Reason = "active since " + TimeFormat.Iso8601(since.Value), Since = since };

            DateTime guardSince = GuardSince(since);
            if (utcNow >= guardSince) return new CollectorStatus { Phase = CollectorPhase.Guard, Reason = why, Since = since };
            DateTime? served = CratesServedAt;
            if (served.HasValue)
                return new CollectorStatus
                {
                    Phase = CollectorPhase.Guard,
                    Reason = why + " (guard kept: Cosmetic crates were served at " + TimeFormat.Iso8601(served.Value) + ")",
                    Since = since,
                };
            return new CollectorStatus { Phase = CollectorPhase.Dormant, Reason = why + " (guard from " + TimeFormat.Iso8601(guardSince) + ")", Since = since };
        }

        /// <summary>
        /// When live ops first served an event carrying the Cosmetic crate (server clock), from <see cref="LatchFileName"/>;
        /// null when they never did. From then on <see cref="Status"/> is never <c>Dormant</c>.
        /// </summary>
        public DateTime? CratesServedAt
        {
            get
            {
                lock (gate)
                {
                    LoadLatch();
                    return servedAt;
                }
            }
        }

        /// <summary>The earliest window start of a served event carrying the crate (null: none), for diagnostics.</summary>
        public DateTime? CratesServedWindow
        {
            get
            {
                lock (gate)
                {
                    LoadLatch();
                    return servedWindow;
                }
            }
        }

        /// <summary>
        /// Live ops served <paramref name="eventId"/> (its window starts at <paramref name="windowStart"/>) with the Cosmetic
        /// crate in it: sets the latch (written to <see cref="LatchFileName"/> the first time, and again for an earlier
        /// window). The TS keeps an ended event with unclaimed milestones and a completed daily that blocks the next one, so
        /// such a crate can be claimed long after <c>since</c> moved (a liveops.json override removed) or the device clock
        /// went back; the guard must still be there then. Never throws (a failed write keeps the latch in memory, logged).
        /// </summary>
        public void NoteCratesServed(string eventId, DateTime windowStart, DateTime utcNow)
        {
            lock (gate)
            {
                LoadLatch();
                if (servedAt.HasValue && servedWindow.HasValue && windowStart >= servedWindow.Value) return;
                bool first = !servedAt.HasValue;
                if (first) servedAt = utcNow;
                servedWindow = windowStart;
                servedEvent = eventId;
                if (first)
                    log.Info("Cosmetic crates served for the first time (" + eventId + "): the document keeps at least the guard from now on ("
                             + LatchFileName + ")");
                WriteLatch();
            }
        }

        private string LatchPath
        {
            get { return string.IsNullOrEmpty(context.DataDir) ? null : Path.Combine(context.DataDir, LatchFileName); }
        }

        /// <summary>Reads the latch file once (under <see cref="gate"/>). An unreadable or broken file counts as a set latch:
        /// the guard is the safe side.</summary>
        private void LoadLatch()
        {
            if (latchLoaded) return;
            latchLoaded = true;
            string path = LatchPath;
            if (path == null) return;
            string text;
            try
            {
                text = AtomicFile.ReadAllTextOrNull(path);
            }
            catch (Exception ex)
            {
                log.Warn(LatchFileName + " could not be read (" + ex.Message + "); keeping the guard as if Cosmetic crates had been served");
                servedAt = DateTime.MinValue;
                return;
            }
            if (text == null) return;
            object parsed;
            DateTime at, window;
            var o = MiniJson.TryParse(text, out parsed) ? parsed as Dictionary<string, object> : null;
            if (o == null || !TimeFormat.TryParse(JsonValue.GetString(o, "cratesServed"), out at))
            {
                log.Warn(LatchFileName + " is not valid; keeping the guard as if Cosmetic crates had been served");
                servedAt = DateTime.MinValue;
                return;
            }
            servedAt = at;
            if (TimeFormat.TryParse(JsonValue.GetString(o, "firstWindow"), out window)) servedWindow = window;
            servedEvent = JsonValue.GetString(o, "event");
        }

        private void WriteLatch()
        {
            string path = LatchPath;
            if (path == null) return;
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["cratesServed"] = TimeFormat.Iso8601(servedAt.Value);
            o["firstWindow"] = servedWindow.HasValue ? (object)TimeFormat.Iso8601(servedWindow.Value) : null;
            o["event"] = servedEvent;
            o["_comment"] = "Live ops served the Cosmetic crate: the CRM document keeps the collector's guard (docs/design/economy-collector.md §2.12). "
                            + "Delete this file with the server stopped to make the server dormant again.";
            try
            {
                AtomicFile.WriteAllText(path, MiniJson.Serialize(o, true) + "\n");
                latchWriteFailing = false;
            }
            catch (Exception ex)
            {
                if (!latchWriteFailing) log.Warn(LatchFileName + " could not be written (" + ex.Message + "); the guard is kept until the server stops");
                latchWriteFailing = true;
            }
        }

        /// <summary>
        /// True when live ops may hand out the Cosmetic crate in a window (a quest day or a season) starting at
        /// <paramref name="windowStart"/>: enabled ∧ content valid ∧ store patch not known missing ∧ since known ∧
        /// windowStart ≥ since. A crate can only be claimed once its window has started, and the TS refetches CRM at the
        /// window start, so whenever this is true and the crate can be claimed, <see cref="Status"/> is <c>Active</c>
        /// under the same inputs (live ops serve no event while they are off).
        /// </summary>
        public bool CratesInWindow(DateTime windowStart, DateTime? since)
        {
            if (!Options.Enabled || StorePatch == false || !since.HasValue) return false;
            string problem;
            return ReadContent(out problem) != null && windowStart >= since.Value;
        }

        /// <summary>
        /// <c>min(since, ShippedSince) − Lead</c> (since unknown: ShippedSince − Lead): the first window that gets the crate
        /// starts at or after since and is listed, so fetched, from a day before its start (<see cref="CollectorGuard.Lead"/>).
        /// </summary>
        public static DateTime GuardSince(DateTime? since)
        {
            DateTime shipped = CollectorGuard.ShippedSince;
            return (since.HasValue && since.Value < shipped ? since.Value : shipped) - CollectorGuard.Lead;
        }

        private CollectorContent ReadContent(out string problem)
        {
            string text;
            Exception failure = null;
            try
            {
                if (!context.Content.TryReadContentText(CollectorContent.FileName, out text)) text = null;
            }
            catch (Exception ex)
            {
                // The <data>/content override changed between the provider's File.Exists and its read (an edit, a delete,
                // a sharing violation). Status runs on every CRM request and InputsKey in the live-ops fingerprint: never throw.
                text = null;
                failure = ex;
            }
            lock (gate)
            {
                if (failure != null)
                {
                    if (!readFailing)
                    {
                        readFailing = true;
                        if (contentRead)
                            log.Warn(CollectorContent.FileName + " could not be read (" + failure.GetType().Name + ": " + failure.Message
                                     + "); keeping the last read until it can be read again");
                        else
                            log.Error(CollectorContent.FileName + " could not be read (" + failure.GetType().Name + ": " + failure.Message
                                      + "); the collector stays inactive (guard only)");
                    }
                    // Keep the last read: a transient failure must not flip InputsKey and so replace the running events
                    // (§3.3). Before any read the file counts as unreadable, which is the guard; nothing is cached, so
                    // the next call reads again.
                    if (contentRead)
                    {
                        problem = contentProblem;
                        return content;
                    }
                    problem = "unreadable (" + failure.Message + ")";
                    return null;
                }
                readFailing = false;
                if (!contentRead || !string.Equals(text, contentText, StringComparison.Ordinal))
                {
                    contentRead = true;
                    contentText = text;
                    if (text == null)
                    {
                        content = null;
                        contentProblem = "missing (run tools/content-gen)";
                        log.Error(CollectorContent.FileName + " is missing: the collector stays inactive (guard only)");
                    }
                    else
                    {
                        content = CollectorContent.Parse(text, out contentProblem);
                        if (content == null)
                            log.Error(CollectorContent.FileName + " refused: " + contentProblem + "; the collector stays inactive (guard only)");
                        else
                            log.Info(CollectorContent.FileName + ": " + content.Rows.Count + " crate rows, " + content.AgentIds.Count
                                     + " agents (content " + content.Hash + ")");
                    }
                }
                problem = contentProblem;
                return content;
            }
        }
    }
}
