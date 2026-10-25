using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Alerts;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Messaging
{
    /// <summary>A message to store (what <c>POST messages/{transport}/{credential}</c> carries).</summary>
    public sealed class OutgoingMessage
    {
        public OutgoingMessage()
        {
            Body = "";
            Fields = Fed.Obj();
            Alert = true;
        }

        /// <summary>The "body" parameter (a gift's body is the JSON text {"gifts":[{"name","value"}],"message"}).</summary>
        public string Body { get; set; }

        /// <summary>"gift", a custom type ("multipleConnectionDetected", tournament types...) or null.</summary>
        public string Type { get; set; }

        public string Attachment { get; set; }

        /// <summary>"message_label": with <see cref="ReplaceMatchingLabel"/> a new message replaces older ones with the same label.</summary>
        public string Label { get; set; }

        public bool ReplaceMatchingLabel { get; set; }

        /// <summary>Seconds before the message becomes visible ("delay").</summary>
        public int DelaySeconds { get; set; }

        /// <summary>Whether to send the recipient a Kairos {"type":"message","transport":...} alert ("alert_kairos").</summary>
        public bool Alert { get; set; }

        /// <summary>The sender's fed id, used for "from" unless <see cref="Fields"/> has one.</summary>
        public string SenderFedId { get; set; }

        /// <summary>Custom fields stored on the message as given ("created", "title", "from", "gift_type", "expirationDate", "roomId"...).</summary>
        public Dictionary<string, object> Fields { get; set; }
    }

    /// <summary>
    /// Hermes mailboxes: one document per (account, transport) in &lt;data&gt;/messages/.
    /// <para>
    /// Who stores what (report 02 §4.7, verify-C5 (b)): the game server posts live-tournament rewards, tournament
    /// tokens, CRM "give_*_secured" gifts and "multipleConnectionDetected" notices to the player's <c>secured</c>
    /// mailbox; the TS reads <c>secured</c> and <c>inbox</c> when the player's session starts and again on every
    /// Kairos "message" alert, turns rewards into profile inbox messages and deletes what it processed
    /// (<c>EN/MessagingTransport.cs</c>, <c>GM/InboxComponent.cs InboxMessage_MessageReceived</c>). Coupon rewards do
    /// not pass through here: <c>PlayerProfileComponent.PushCouponDataToInbox</c> writes them straight into the
    /// profile's inbox (stored by the STORAGE module).
    /// </para>
    /// Push transports (apns, gcm, mpns, wns, and "any") are notifications to a phone, never read back: they are
    /// accepted and dropped.
    /// </summary>
    public sealed class Mailboxes
    {
        /// <summary>
        /// EventHub topic other modules can use to deliver a message without referencing this file. Payload:
        /// {"to": "&lt;account id&gt;" | "fed_id:&lt;id&gt;", "transport": "secured" (default), "body": "...",
        /// "type": "gift"?, "alert": true?, "fields": {...}?}.
        /// </summary>
        public const string PostTopic = "messaging.post";

        public const string StoreName = "messages";

        private static readonly object ForGate = new object();
        /// <summary>
        /// Push-notification transports; "any" = whichever push service the device has (clan join-request
        /// notifications, <c>GM/GameClanComponent.cs PostMultiplePNMessages</c>).
        /// </summary>
        private static readonly string[] PushTransports = { "apns", "gcm", "mpns", "wns", "any" };

        private readonly ServerContext context;
        private readonly DocumentStore store;
        private readonly AlertHub alerts;
        private readonly Log log;

        public Mailboxes(ServerContext context, AlertHub alerts)
        {
            this.context = context;
            this.alerts = alerts;
            store = context.OpenStore(StoreName);
            log = context.Logger.For("messaging");
        }

        /// <summary>The server's shared instance (also listening on <see cref="PostTopic"/>).</summary>
        public static Mailboxes For(ServerContext context)
        {
            lock (ForGate)
            {
                Mailboxes mailboxes = context.Features.Get<Mailboxes>();
                if (mailboxes != null) return mailboxes;
                mailboxes = new Mailboxes(context, AlertHub.For(context));
                context.Features.Set(mailboxes);
                Mailboxes captured = mailboxes;
                context.Events.Subscribe(PostTopic, payload => captured.PostFromEvent(payload));
                return mailboxes;
            }
        }

        public static bool IsPushTransport(string transport)
        {
            return Array.IndexOf(PushTransports, (transport ?? "").ToLowerInvariant()) >= 0;
        }

        /// <summary>Maximum messages kept per mailbox; the oldest are dropped beyond it.</summary>
        public int Capacity
        {
            get { return (int)Fed.LongSetting(context, "messaging.maxMessagesPerMailbox", 500, 10, 100000); }
        }

        /// <summary>
        /// Stores a message for <paramref name="accountId"/> and alerts the recipient. Returns the stored message as
        /// the client will read it, or null for push transports (dropped).
        /// </summary>
        public Dictionary<string, object> Post(string accountId, string transport, OutgoingMessage message)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("recipient account required", "accountId");
            transport = NormalizeTransport(transport);
            if (IsPushTransport(transport))
            {
                log.Info("push notification for " + Fed.FedIdOf(accountId) + " on '" + transport + "' dropped (no push service offline)");
                return null;
            }

            DateTime now = context.Clock.UtcNow;
            Dictionary<string, object> stored = BuildMessage(message, now);
            string id = (string)stored["id"];
            var record = Fed.Obj();
            record["message"] = stored;
            record["label"] = string.IsNullOrEmpty(message.Label) ? null : message.Label;
            record["stored"] = TimeFormat.Iso8601(now);
            record["deliverAt"] = message.DelaySeconds > 0 ? TimeFormat.Iso8601(now.AddSeconds(message.DelaySeconds)) : null;

            int capacity = Capacity;
            int replaced = 0, dropped = 0;
            store.Update(Key(accountId, transport), doc =>
            {
                List<object> list = Records(doc);
                if (message.ReplaceMatchingLabel && !string.IsNullOrEmpty(message.Label))
                {
                    replaced = list.RemoveAll(o => JsonValue.GetString(o as Dictionary<string, object>, "label") == message.Label);
                }
                list.Add(record);
                while (list.Count > capacity)
                {
                    list.RemoveAt(0);
                    dropped++;
                }
            });

            log.Info("message " + id + " stored for " + Fed.FedIdOf(accountId) + " on '" + transport + "'"
                     + (stored.ContainsKey("type") ? " type=" + JsonValue.GetString(stored, "type") : "")
                     + (replaced > 0 ? ", replaced " + replaced + " with label '" + message.Label + "'" : "")
                     + (dropped > 0 ? ", dropped " + dropped + " old message(s) over the mailbox capacity" : "")
                     + (message.DelaySeconds > 0 ? ", visible in " + message.DelaySeconds + " s" : ""));

            // A delayed message is picked up by the next mailbox read; alerting now would find nothing.
            if (message.Alert && message.DelaySeconds <= 0)
            {
                var alert = Fed.Obj();
                alert["type"] = "message";
                alert["transport"] = transport;
                alerts.Push(Fed.FedIdOf(accountId), alert);
            }
            return JsonValue.DeepCloneObject(stored);
        }

        /// <summary>The visible messages of a mailbox (oldest first); with <paramref name="delete"/> they are removed.</summary>
        public List<object> Retrieve(string accountId, string transport, bool delete)
        {
            transport = NormalizeTransport(transport);
            DateTime now = context.Clock.UtcNow;
            var result = new List<object>();
            string key = Key(accountId, transport);
            if (!delete)
            {
                foreach (var record in Records(store.Read(key)))
                {
                    var r = record as Dictionary<string, object>;
                    if (IsVisible(r, now)) result.Add(JsonValue.DeepClone(JsonValue.Get(r, "message")));
                }
                return result;
            }
            if (!store.Exists(key)) return result;
            store.Update(key, doc =>
            {
                List<object> list = Records(doc);
                list.RemoveAll(o =>
                {
                    var r = o as Dictionary<string, object>;
                    if (!IsVisible(r, now)) return false;
                    result.Add(JsonValue.DeepClone(JsonValue.Get(r, "message")));
                    return true;
                });
            });
            if (result.Count > 0) log.Info(result.Count + " message(s) read and deleted from " + Fed.FedIdOf(accountId) + "/" + transport);
            return result;
        }

        /// <summary>Deletes the given message ids (all messages when <paramref name="ids"/> is null). Returns how many.</summary>
        public int Delete(string accountId, string transport, ICollection<string> ids)
        {
            transport = NormalizeTransport(transport);
            string key = Key(accountId, transport);
            if (!store.Exists(key)) return 0;
            int removed = store.Update(key, doc =>
            {
                List<object> list = Records(doc);
                return list.RemoveAll(o =>
                {
                    if (ids == null) return true;
                    var message = JsonValue.GetObject(o as Dictionary<string, object>, "message");
                    return ids.Contains(JsonValue.GetString(message, "id") ?? "");
                });
            });
            log.Info("deleted " + removed + " message(s) from " + Fed.FedIdOf(accountId) + "/" + transport
                     + (ids == null ? " (all)" : " (" + ids.Count + " id(s) requested)"));
            return removed;
        }

        private Dictionary<string, object> BuildMessage(OutgoingMessage message, DateTime now)
        {
            var m = Fed.Obj();
            m["id"] = Fed.NewId();
            object from;
            m["from"] = message.Fields.TryGetValue("from", out from) && !string.IsNullOrEmpty(JsonValue.AsString(from))
                ? JsonValue.AsString(from)
                : (message.SenderFedId ?? "");
            m["body"] = message.Body ?? "";
            // The client parses "created" with DateTime.Parse and fails the whole message without it (OP/Messaging.cs).
            object created;
            DateTime parsed;
            m["created"] = message.Fields.TryGetValue("created", out created) && TimeFormat.TryParse(JsonValue.AsString(created), out parsed)
                ? JsonValue.AsString(created)
                : TimeFormat.Iso8601(now);
            if (!string.IsNullOrEmpty(message.Type)) m["type"] = message.Type;
            if (!string.IsNullOrEmpty(message.Attachment)) m["attachment"] = message.Attachment;
            foreach (var kv in message.Fields)
            {
                if (m.ContainsKey(kv.Key)) continue;
                m[kv.Key] = JsonValue.DeepClone(kv.Value);
            }
            return m;
        }

        private void PostFromEvent(object payload)
        {
            var obj = payload as Dictionary<string, object>;
            string to = JsonValue.GetString(obj, "to");
            if (obj == null || string.IsNullOrEmpty(to))
            {
                log.Warn(PostTopic + ": payload must be an object with 'to', got " + MiniJson.Serialize(payload));
                return;
            }
            string accountId = to.StartsWith(McvsConstants.FedIdPrefix, StringComparison.Ordinal) ? to.Substring(McvsConstants.FedIdPrefix.Length) : to;
            var message = new OutgoingMessage();
            message.Body = JsonValue.GetString(obj, "body") ?? "";
            message.Type = JsonValue.GetString(obj, "type");
            message.Label = JsonValue.GetString(obj, "label");
            message.ReplaceMatchingLabel = JsonValue.GetBool(obj, "replaceMatchingLabel", false);
            message.Alert = JsonValue.GetBool(obj, "alert", true);
            message.SenderFedId = JsonValue.GetString(obj, "from") ?? "";
            var fields = JsonValue.GetObject(obj, "fields");
            if (fields != null) message.Fields = JsonValue.DeepCloneObject(fields);
            string problem = Validate(message);
            if (problem != null)
            {
                log.Warn(PostTopic + ": message for " + to + " rejected: " + problem);
                return;
            }
            Post(accountId, JsonValue.GetString(obj, "transport") ?? "secured", message);
        }

        /// <summary>
        /// Why the client could not use this message, or null. A gift needs a JSON body with a "gifts" array or a
        /// "gift_type" field, otherwise the client throws CorruptedGiftException and skips it forever
        /// (<c>OP/Messaging.cs GiftData</c>).
        /// </summary>
        public static string Validate(OutgoingMessage message)
        {
            if (!string.Equals(message.Type, "gift", StringComparison.Ordinal)) return null;
            if (string.IsNullOrEmpty(message.Body))
                return message.Fields.ContainsKey("gift_type") ? null : "a gift needs a body or a gift_type";
            object body;
            if (!MiniJson.TryParse(message.Body, out body) || !(body is Dictionary<string, object>))
                return "a gift body must be a JSON object {\"gifts\":[{\"name\":...,\"value\":\"<n>\"}],\"message\":...}";
            return null;
        }

        private static bool IsVisible(Dictionary<string, object> record, DateTime now)
        {
            if (record == null || JsonValue.GetObject(record, "message") == null) return false;
            string deliverAt = JsonValue.GetString(record, "deliverAt");
            DateTime at;
            return string.IsNullOrEmpty(deliverAt) || !TimeFormat.TryParse(deliverAt, out at) || at <= now;
        }

        private static List<object> Records(Dictionary<string, object> doc)
        {
            if (doc == null) return new List<object>();
            var list = JsonValue.GetArray(doc, "messages");
            if (list == null)
            {
                list = new List<object>();
                doc["messages"] = list;
            }
            return list;
        }

        private static string NormalizeTransport(string transport)
        {
            string t = (transport ?? "").Trim().ToLowerInvariant();
            return t.Length == 0 ? "inbox" : t;
        }

        private static string Key(string accountId, string transport)
        {
            return accountId + "." + transport;
        }
    }
}
