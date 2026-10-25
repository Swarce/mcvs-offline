using System;
using System.Collections.Generic;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Alerts
{
    /// <summary>
    /// In-process Kairos: delivers alerts to the alert streams (<c>GET alerts/me</c>) of their recipient.
    /// <para>
    /// A recipient is an account ("fed_id:&lt;id&gt;"; a bare account id is accepted too) or a target that listeners
    /// name in <c>targets</c> (clans listen to "group:&lt;clanId&gt;", <c>EN/ClanComponent.cs ResetAlertTargets</c>).
    /// Listeners only receive the <c>alert_types</c> they asked for (the TS's per-connection listener asks for
    /// "connection" only, <c>EN/Connection.cs:576-584</c>).
    /// </para>
    /// <para>
    /// Alerts of type "connection" are never delivered: the TS treats one as "multiple connection detected" and kicks
    /// the player (<c>EN/Connection.cs:1610-1616</c>, report 02 §4.8).
    /// </para>
    /// <para>
    /// The client re-opens its stream right after one ends (<c>OP/AlertListener.cs Listen</c>). An alert published in
    /// that short gap is not lost: the recent alerts of each recipient are kept for a while and replayed to the
    /// listener that comes back with the same token and parameters (<see cref="Subscribe"/> resumeKey).
    /// </para>
    /// Other modules publish through <see cref="For"/>(context).Push, or without referencing this file through the
    /// EventHub topic <see cref="PushTopic"/>.
    /// </summary>
    public sealed class AlertHub
    {
        /// <summary>
        /// EventHub topic: payload {"to": "fed_id:&lt;account&gt;" | "&lt;account id&gt;" | "group:&lt;id&gt;", "alert": {"type": ...}}
        /// (an "alert" given as a JSON string is parsed).
        /// </summary>
        public const string PushTopic = "alerts.push";

        private const int MaxRecentPerRecipient = 64;
        private const int MaxResumeRecords = 256;

        private static readonly object ForGate = new object();

        private readonly object gate = new object();
        private readonly Dictionary<string, List<Entry>> recent = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly List<Subscription> subscribers = new List<Subscription>();
        private readonly Dictionary<string, ResumeRecord> resume = new Dictionary<string, ResumeRecord>(StringComparer.Ordinal);
        private readonly IClock clock;
        private readonly Log log;
        private long sequence;

        public AlertHub(IClock clock, Log log)
        {
            this.clock = clock ?? SystemClock.Instance;
            this.log = log ?? Log.Null;
            ReplayWindow = TimeSpan.FromSeconds(120);
        }

        /// <summary>How long recent alerts are kept for a listener that reconnects.</summary>
        public TimeSpan ReplayWindow { get; set; }

        /// <summary>
        /// The server's shared hub (created on first use, also listening on <see cref="PushTopic"/>). All MISC
        /// modules use this one instance.
        /// </summary>
        public static AlertHub For(ServerContext context)
        {
            lock (ForGate)
            {
                AlertHub hub = context.Features.Get<AlertHub>();
                if (hub != null) return hub;
                hub = new AlertHub(context.Clock, context.Logger.For("alerts"));
                hub.ReplayWindow = TimeSpan.FromSeconds(Fed.LongSetting(context, "alerts.replaySeconds", 120, 0, 3600));
                context.Features.Set(hub);
                AlertHub captured = hub;
                context.Events.Subscribe(PushTopic, payload => captured.PushFromEvent(payload));
                return hub;
            }
        }

        /// <summary>Normalizes a recipient: "fed_id:x" and "x" (an account id) both become "fed_id:x".</summary>
        public static string NormalizeRecipient(string recipient)
        {
            if (string.IsNullOrEmpty(recipient)) return null;
            string r = recipient.Trim();
            if (r.Length == 0) return null;
            int colon = r.IndexOf(':');
            if (colon < 0) return McvsConstants.FedIdPrefix + r;
            if (colon == 0 || colon == r.Length - 1) return null;
            return r.Substring(0, colon).ToLowerInvariant() + r.Substring(colon);
        }

        /// <summary>
        /// Delivers <paramref name="alert"/> (a JSON object with at least "type") to the recipient's open streams and
        /// keeps it for reconnecting listeners. Returns the number of streams it was handed to.
        /// </summary>
        public int Push(string recipient, Dictionary<string, object> alert)
        {
            string to = NormalizeRecipient(recipient);
            string type = alert == null ? null : JsonValue.GetString(alert, "type");
            if (to == null || string.IsNullOrEmpty(type))
            {
                log.Warn("alert dropped: needs a recipient and a type (to=" + recipient + ", alert=" + MiniJson.Serialize(alert) + ")");
                return 0;
            }
            if (string.Equals(type, "connection", StringComparison.Ordinal))
            {
                // The TS would close the player's connection ("multiple connection detected").
                log.Warn("alert of type 'connection' for " + to + " dropped: it would kick the player (report 02 §4.8)");
                return 0;
            }

            string json = MiniJson.Serialize(alert);
            int delivered = 0;
            lock (gate)
            {
                var entry = new Entry(++sequence, clock.UtcNow, type, json);
                List<Entry> list;
                if (!recent.TryGetValue(to, out list))
                {
                    list = new List<Entry>();
                    recent[to] = list;
                }
                list.Add(entry);
                Prune(list);
                foreach (Subscription s in subscribers)
                {
                    if (!s.Recipients.Contains(to) || !s.Accepts(type)) continue;
                    s.Queue.Add(entry);
                    delivered++;
                }
                if (delivered > 0) Monitor.PulseAll(gate);
            }
            log.Info("alert " + type + " -> " + to + " (" + delivered + " open stream" + (delivered == 1 ? "" : "s") + ")");
            return delivered;
        }

        /// <summary>Number of open streams that would receive an alert for <paramref name="recipient"/> (diagnostics, tests).</summary>
        public int ListenerCount(string recipient)
        {
            string to = NormalizeRecipient(recipient);
            int count = 0;
            lock (gate)
            {
                foreach (Subscription s in subscribers) if (s.Recipients.Contains(to)) count++;
            }
            return count;
        }

        /// <summary>
        /// Opens a listener for <paramref name="recipients"/>, receiving only <paramref name="types"/> (empty = all).
        /// When <paramref name="resumeKey"/> matches a listener that ended within <see cref="ReplayWindow"/>, the
        /// alerts it missed in between are queued first. Dispose the subscription when the stream ends.
        /// </summary>
        public Subscription Subscribe(IEnumerable<string> recipients, ICollection<string> types, string resumeKey)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (recipients != null)
            {
                foreach (string r in recipients)
                {
                    string n = NormalizeRecipient(r);
                    if (n != null) set.Add(n);
                }
            }
            var typeSet = new HashSet<string>(StringComparer.Ordinal);
            if (types != null) foreach (string t in types) if (!string.IsNullOrEmpty(t)) typeSet.Add(t);

            var subscription = new Subscription(this, set, typeSet, resumeKey);
            lock (gate)
            {
                ResumeRecord record;
                if (!string.IsNullOrEmpty(resumeKey) && resume.TryGetValue(resumeKey, out record))
                {
                    resume.Remove(resumeKey);
                    if (clock.UtcNow - record.EndedUtc <= ReplayWindow)
                    {
                        var missed = new List<Entry>();
                        foreach (string r in set)
                        {
                            List<Entry> list;
                            if (!recent.TryGetValue(r, out list)) continue;
                            Prune(list);
                            foreach (Entry e in list)
                                if (e.Sequence > record.LastSequence && subscription.Accepts(e.Type)) missed.Add(e);
                        }
                        missed.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
                        subscription.Queue.AddRange(missed);
                        if (missed.Count > 0) log.Info("replaying " + missed.Count + " alert(s) missed while the stream reconnected");
                    }
                }
                subscribers.Add(subscription);
            }
            return subscription;
        }

        private void PushFromEvent(object payload)
        {
            var obj = payload as Dictionary<string, object>;
            if (obj == null)
            {
                log.Warn(PushTopic + ": payload must be an object {to, alert}, got " + MiniJson.Serialize(payload));
                return;
            }
            object alert = JsonValue.Get(obj, "alert");
            string text = alert as string;
            if (text != null) MiniJson.TryParse(text, out alert);
            Push(JsonValue.GetString(obj, "to"), alert as Dictionary<string, object>);
        }

        private void Prune(List<Entry> list)
        {
            DateTime cutoff = clock.UtcNow - ReplayWindow;
            int remove = 0;
            while (remove < list.Count && (list.Count - remove > MaxRecentPerRecipient || list[remove].TimeUtc < cutoff)) remove++;
            if (remove > 0) list.RemoveRange(0, remove);
        }

        private List<string> Take(Subscription subscription, int timeoutMs, Func<bool> stopRequested)
        {
            int deadline = Environment.TickCount + Num.Max(0, timeoutMs);
            lock (gate)
            {
                while (subscription.Queue.Count == 0)
                {
                    int left = deadline - Environment.TickCount;
                    if (left <= 0 || (stopRequested != null && stopRequested())) break;
                    // Short slices: a server shutdown must not wait for a long keep-alive interval.
                    Monitor.Wait(gate, Num.Min(left, 500));
                }
                var result = new List<string>(subscription.Queue.Count);
                foreach (Entry e in subscription.Queue)
                {
                    result.Add(e.Json);
                    subscription.LastDelivered = e.Sequence;
                }
                subscription.Queue.Clear();
                return result;
            }
        }

        private void Unsubscribe(Subscription subscription)
        {
            lock (gate)
            {
                if (!subscribers.Remove(subscription)) return;
                if (string.IsNullOrEmpty(subscription.ResumeKey)) return;
                // Everything up to now was delivered or did not concern this listener, except what is still queued.
                long last = subscription.Queue.Count > 0 ? subscription.Queue[0].Sequence - 1 : sequence;
                resume[subscription.ResumeKey] = new ResumeRecord(last, clock.UtcNow);
                if (resume.Count > MaxResumeRecords) DropOldestResumeRecord();
            }
        }

        private void DropOldestResumeRecord()
        {
            string oldestKey = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (var kv in resume)
            {
                if (kv.Value.EndedUtc < oldest)
                {
                    oldest = kv.Value.EndedUtc;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey != null) resume.Remove(oldestKey);
        }

        /// <summary>One open alert stream.</summary>
        public sealed class Subscription : IDisposable
        {
            private readonly AlertHub hub;
            private bool disposed;

            internal Subscription(AlertHub hub, HashSet<string> recipients, HashSet<string> types, string resumeKey)
            {
                this.hub = hub;
                Recipients = recipients;
                Types = types;
                ResumeKey = resumeKey;
                Queue = new List<Entry>();
            }

            internal HashSet<string> Recipients { get; private set; }

            internal HashSet<string> Types { get; private set; }

            internal string ResumeKey { get; private set; }

            internal List<Entry> Queue { get; private set; }

            internal long LastDelivered { get; set; }

            internal bool Accepts(string type)
            {
                return Types.Count == 0 || Types.Contains(type);
            }

            /// <summary>
            /// Waits up to <paramref name="timeoutMs"/> for alerts and returns their JSON texts (empty on timeout or
            /// when <paramref name="stopRequested"/> becomes true).
            /// </summary>
            public List<string> Take(int timeoutMs, Func<bool> stopRequested)
            {
                return hub.Take(this, timeoutMs, stopRequested);
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                hub.Unsubscribe(this);
            }
        }

        internal sealed class Entry
        {
            public Entry(long sequence, DateTime timeUtc, string type, string json)
            {
                Sequence = sequence;
                TimeUtc = timeUtc;
                Type = type;
                Json = json;
            }

            public long Sequence { get; private set; }

            public DateTime TimeUtc { get; private set; }

            public string Type { get; private set; }

            public string Json { get; private set; }
        }

        private struct ResumeRecord
        {
            public readonly long LastSequence;
            public readonly DateTime EndedUtc;

            public ResumeRecord(long lastSequence, DateTime endedUtc)
            {
                LastSequence = lastSequence;
                EndedUtc = endedUtc;
            }
        }
    }
}
