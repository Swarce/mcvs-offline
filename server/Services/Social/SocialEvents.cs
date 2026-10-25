using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Social
{
    /// <summary>
    /// Supplies Osiris event definitions (time-limited events: crate shops, bundles, division seasons...). The module
    /// that owns the TLE content (CONTENT, which also lists the ids in CRM) registers an implementation with
    /// <c>context.Features.Set&lt;ISocialEventProvider&gt;(...)</c>; rolling events (weekly seasons) need code, static ones
    /// can live in the content file <see cref="SocialEvents.ContentName"/>.
    /// </summary>
    public interface ISocialEventProvider
    {
        /// <summary>
        /// The event with id <paramref name="eventId"/> as Osiris returned it (id, name, start_date, end_date, _version,
        /// tournament?, _template, _priority, ...), or null if this provider does not know it.
        /// </summary>
        Dictionary<string, object> FindEvent(string eventId, string language, DateTime utcNow);
    }

    /// <summary>
    /// Resolves event ids for <c>GET events?event_ids=</c> and <c>GET events/{id}</c>, from the registered
    /// <see cref="ISocialEventProvider"/> first, then the content file. Unknown ids are left out.
    /// <para>
    /// Every event handed to the client is normalized so that the TS's parser cannot throw: an exception that is not
    /// an online error aborts <c>TimeLimitedEventsComponent.RetrieveEventsAsync</c> and the menu waits forever for
    /// its events (verify-C2 §1.8):
    /// <list type="bullet">
    /// <item><c>_template</c> is required and must be a JSON <b>string</b> (<c>item.Fields["_template"].Value&lt;string&gt;()</c>):
    /// an object is serialized, an event without one is dropped;</item>
    /// <item><c>_version</c> must be an integer (<c>Convert.ToInt32</c>), otherwise it is removed;</item>
    /// <item>top-level nulls become "" (image fields go through <c>DownloadableAsset(string).Trim()</c>, report 04 §9.1).</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class SocialEvents
    {
        /// <summary>Content file with static events: an array of events, {"events": [...]}, or {"&lt;id&gt;": {...}}.</summary>
        public const string ContentName = "social/events.json";

        private readonly ServerContext context;
        private readonly Log log;
        private readonly object gate = new object();
        private string cachedText;
        private Dictionary<string, Dictionary<string, object>> cachedEvents;

        public SocialEvents(ServerContext context)
        {
            this.context = context;
            log = context.Logger.For("social");
        }

        /// <summary>The known events among <paramref name="ids"/>, in request order, normalized.</summary>
        public List<object> Find(IEnumerable<string> ids, string language)
        {
            var result = new List<object>();
            var unknown = new List<string>();
            foreach (string id in ids)
            {
                Dictionary<string, object> e = FindOne(id, language);
                if (e != null) result.Add(e);
                else unknown.Add(id);
            }
            if (unknown.Count > 0)
                log.Info("no definition for event id(s) " + string.Join(",", unknown.ToArray()) + " (left out of the answer)");
            return result;
        }

        /// <summary>One normalized event, or null.</summary>
        public Dictionary<string, object> FindOne(string id, string language)
        {
            if (string.IsNullOrEmpty(id)) return null;
            Dictionary<string, object> raw = null;
            ISocialEventProvider provider = context.Features.Get<ISocialEventProvider>();
            if (provider != null)
            {
                try
                {
                    raw = provider.FindEvent(id, language, context.Clock.UtcNow);
                }
                catch (Exception ex)
                {
                    log.Error("event provider failed for '" + id + "'", ex);
                }
            }
            if (raw == null)
            {
                Dictionary<string, Dictionary<string, object>> fromContent = ContentEvents();
                Dictionary<string, object> found;
                if (fromContent != null && fromContent.TryGetValue(id, out found)) raw = found;
            }
            return raw == null ? null : Normalize(id, raw, log);
        }

        /// <summary>Makes an event safe for the client's parser (see the class remarks); null if it cannot be.</summary>
        public static Dictionary<string, object> Normalize(string id, Dictionary<string, object> raw, Log log)
        {
            Dictionary<string, object> e = JsonValue.DeepCloneObject(raw);
            e["id"] = id;
            if (!e.ContainsKey("name")) e["name"] = "";

            object template = JsonValue.Get(e, "_template");
            if (template is Dictionary<string, object> || template is List<object>)
            {
                e["_template"] = MiniJson.Serialize(template);
            }
            else if (!(template is string))
            {
                if (log != null) log.Warn("event '" + id + "' has no string _template; dropped (the TS cannot parse it)");
                return null;
            }

            object version = JsonValue.Get(e, "_version");
            if (version != null)
            {
                long? v = JsonValue.AsLong(version);
                if (v.HasValue) e["_version"] = v.Value.ToString(CultureInfo.InvariantCulture);
                else
                {
                    if (log != null) log.Warn("event '" + id + "': _version '" + JsonValue.AsString(version) + "' is not an integer; removed");
                    JsonPath.RemoveKey(e, "_version");
                }
            }

            var keys = new List<string>(e.Keys);
            foreach (string key in keys)
            {
                if (e[key] == null) e[key] = "";
            }
            return e;
        }

        private Dictionary<string, Dictionary<string, object>> ContentEvents()
        {
            string text;
            if (!context.Content.TryReadContentText(ContentName, out text)) return null;
            lock (gate)
            {
                if (ReferenceEquals(text, cachedText) || string.Equals(text, cachedText, StringComparison.Ordinal)) return cachedEvents;
                cachedText = text;
                cachedEvents = ParseContent(text);
                return cachedEvents;
            }
        }

        private Dictionary<string, Dictionary<string, object>> ParseContent(string text)
        {
            var events = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            object parsed;
            if (!MiniJson.TryParse(text, out parsed))
            {
                log.Error(ContentName + " is not valid JSON; serving no events from it");
                return events;
            }
            List<object> list = parsed as List<object> ?? JsonValue.GetArray(parsed as Dictionary<string, object>, "events");
            if (list != null)
            {
                foreach (object item in list)
                {
                    var e = item as Dictionary<string, object>;
                    string id = JsonValue.GetString(e, "id");
                    if (!string.IsNullOrEmpty(id)) events[id] = e;
                }
            }
            else if (parsed is Dictionary<string, object>)
            {
                foreach (var kv in (Dictionary<string, object>)parsed)
                {
                    var e = kv.Value as Dictionary<string, object>;
                    if (e != null) events[JsonValue.GetString(e, "id") ?? kv.Key] = e;
                }
            }
            log.Info(ContentName + ": " + events.Count + " event definition(s)");
            return events;
        }
    }
}
