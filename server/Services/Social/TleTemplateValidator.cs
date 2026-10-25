using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Social
{
    /// <summary>
    /// Checks an event's <c>_template</c> the way the TS will parse it, so that one malformed event is dropped here
    /// instead of aborting the whole event list on the TS.
    /// <para>
    /// <c>TimeLimitedEventsComponent.RetrieveEventsAsync</c> rethrows every exception that is not an online error; the
    /// TS then never sets its event list and the main menu waits for it (verify-C2 §1.8). Its template parser
    /// (<c>EN/TimeLimitedEventsComponent.cs ParseTemplate/ParseElement</c> and the Parse* helpers) throws on:
    /// a template that is not a JSON object (<c>Json.LoadObject</c>), an unknown SEM "type" ("Unhandled SEM type !"),
    /// a non-string "type" or "private", a "sub" that is not an object (<c>Value&lt;JObject&gt;</c> cast), a
    /// list_multi value that is neither array nor object, a time/date value that is a scalar (child access on a
    /// JValue or JArray), a non-numeric "duration", a localized text holding non-scalars, and a game item whose
    /// "consumable"/"value" do not convert. This mirrors those rules (report 04 §9.1).
    /// </para>
    /// It also checks the one Initialize that can throw on content: <c>DivisionSeasonEvent</c> parses the division
    /// out of <c>_participation</c> ("... CurrentDivision == &lt;n&gt;)") with int.Parse (<c>GM/DivisionSeasonEvent.cs</c>).
    /// </summary>
    public static class TleTemplateValidator
    {
        private static readonly string[] SemTypes =
        {
            "text", "text_plain", "list_single", "list_append", "list_multi", "range", "time", "duration", "date", "timestamp",
        };

        /// <summary>Why the TS would throw on this event, or null when it parses.</summary>
        public static string Problem(Dictionary<string, object> evt)
        {
            string template = JsonValue.GetString(evt, "_template");
            object parsed;
            if (template == null || !MiniJson.TryParse(template, out parsed) || !(parsed is Dictionary<string, object>))
                return "_template is not a JSON object";
            var root = (Dictionary<string, object>)parsed;

            string eventType = null;
            var tuning = JsonValue.GetObject(root, "event_tuning");
            if (tuning != null)
            {
                foreach (var kv in tuning)
                {
                    string problem = Element(kv.Value, "event_tuning." + kv.Key);
                    if (problem != null) return problem;
                }
                eventType = EventType(JsonValue.Get(tuning, "event_type"));
            }
            var milestones = JsonValue.GetObject(root, "milestones");
            if (milestones != null && milestones.Count > 0)
            {
                string problem = Element(milestones, "milestones");
                if (problem != null) return problem;
            }

            if (eventType != null && eventType.EndsWith("DivisionSeasonEvent", StringComparison.Ordinal))
            {
                string participation = JsonValue.GetString(evt, "_participation");
                const string marker = "CurrentDivision == ";
                int at = participation == null ? -1 : participation.IndexOf(marker, StringComparison.Ordinal);
                if (at > 0)
                {
                    int start = at + marker.Length;
                    int end = participation.IndexOf(')', at);
                    int division;
                    if (end > 0 && (end < start || !int.TryParse(participation.Substring(start, end - start), System.Globalization.NumberStyles.Integer,
                                                                 System.Globalization.CultureInfo.InvariantCulture, out division)))
                        return "_participation '" + participation + "' has no integer division (DivisionSeasonEvent int.Parse)";
                }
            }
            return null;
        }

        /// <summary>The event type a parsed event_type element yields (a "private" raw value or a plain text value).</summary>
        private static string EventType(object element)
        {
            var obj = element as Dictionary<string, object>;
            if (obj == null) return null;
            if (JsonValue.Get(obj, "private") != null) return JsonValue.AsString(JsonValue.Get(obj, "value"));
            object value = JsonValue.Get(obj, "value");
            var localized = value as Dictionary<string, object>;
            if (localized != null)
            {
                foreach (var kv in localized) return JsonValue.AsString(kv.Value);
            }
            return JsonValue.AsString(value);
        }

        private static string Element(object element, string where)
        {
            var obj = element as Dictionary<string, object>;
            if (obj == null) return null; // ParseElement ignores non-objects

            object typeValue;
            if (obj.TryGetValue("type", out typeValue) && typeValue != null)
            {
                string type = typeValue as string;
                if (type == null || Array.IndexOf(SemTypes, type) < 0)
                    return where + ": unknown SEM type " + MiniJson.Serialize(typeValue) + " (\"Unhandled SEM type !\")";
                object value = JsonValue.Get(obj, "value");
                switch (type)
                {
                    case "text":
                    case "text_plain":
                        var texts = value as Dictionary<string, object>;
                        if (texts != null)
                            foreach (var kv in texts)
                                if (IsContainer(kv.Value)) return where + ": localized text values must be strings";
                        return null;
                    case "list_single":
                        if (value is List<object>) return ((List<object>)value).Count > 0 ? Element(((List<object>)value)[0], where + "[0]") : null;
                        if (value is Dictionary<string, object>)
                            foreach (var kv in (Dictionary<string, object>)value) return Element(kv.Value, where + "." + kv.Key);
                        return null;
                    case "list_append":
                        if (value is List<object>) return Items((List<object>)value, where);
                        return null;
                    case "list_multi":
                        if (value == null) return null;
                        if (value is List<object>) return Items((List<object>)value, where);
                        if (value is Dictionary<string, object>)
                        {
                            foreach (var kv in (Dictionary<string, object>)value)
                            {
                                string p = Element(kv.Value, where + "." + kv.Key);
                                if (p != null) return p;
                            }
                            return null;
                        }
                        return where + ": list_multi value must be an array or an object";
                    case "range":
                        return obj.ContainsKey("itemtype") ? GameItem(obj, where) : null;
                    case "time":
                    case "duration":
                        if (value == null) return null;
                        var time = value as Dictionary<string, object>;
                        if (time == null) return where + ": time value must be an object {duration} or {time:[h,m,s]}";
                        // "duration" goes through ToObject<int>; "time" through SafeGet, which never throws.
                        object duration;
                        if (time.TryGetValue("duration", out duration) && !ConvertsToInt(duration))
                            return where + ": duration must be an integer";
                        return null;
                    case "date":
                    case "timestamp":
                        return value == null || value is Dictionary<string, object> ? null : where + ": date value must be an object {date} or {timestamp}";
                }
                return null;
            }

            object sub;
            if (obj.TryGetValue("sub", out sub) && sub != null)
            {
                var subObject = sub as Dictionary<string, object>;
                if (subObject == null) return where + ": sub must be an object";
                foreach (var kv in subObject)
                {
                    string p = Element(kv.Value, where + "." + kv.Key);
                    if (p != null) return p;
                }
                return null;
            }
            if (obj.ContainsKey("itemtype") && JsonValue.Get(obj, "itemtype") != null) return GameItem(obj, where);
            object priv;
            if (obj.TryGetValue("private", out priv) && IsContainer(priv)) return where + ": private must be a string";
            return null;
        }

        private static string Items(List<object> items, string where)
        {
            for (int i = 0; i < items.Count; i++)
            {
                string p = Element(items[i], where + "[" + i + "]");
                if (p != null) return p;
            }
            return null;
        }

        private static string GameItem(Dictionary<string, object> item, string where)
        {
            // Value<bool?>("consumable") and Value<int>("value") go through Convert: "1"/"yes" are not booleans.
            object consumable = JsonValue.Get(item, "consumable");
            bool isConsumable = false;
            if (consumable is bool) isConsumable = (bool)consumable;
            else if (consumable is string)
            {
                string c = ((string)consumable).Trim();
                if (string.Equals(c, "true", StringComparison.OrdinalIgnoreCase)) isConsumable = true;
                else if (!string.Equals(c, "false", StringComparison.OrdinalIgnoreCase)) return where + ": consumable must be a boolean";
            }
            else if (consumable != null)
            {
                double? n = JsonValue.AsDouble(consumable);
                if (!n.HasValue) return where + ": consumable must be a boolean";
                isConsumable = n.Value != 0;
            }
            // A missing "value" reads as 0; a null or non-integer one throws.
            object value;
            if (isConsumable && item.TryGetValue("value", out value) && !ConvertsToInt(value))
                return where + ": a consumable item's value must be an integer";
            foreach (string key in new[] { "id", "category", "itemtype" })
                if (IsContainer(JsonValue.Get(item, key))) return where + ": " + key + " must be a string";
            return null;
        }

        /// <summary>Whether Newtonsoft's Convert/ToObject&lt;int&gt; accepts the value (numbers, booleans, integer strings).</summary>
        private static bool ConvertsToInt(object value)
        {
            if (value == null) return false;
            if (value is string)
            {
                int n;
                return int.TryParse((string)value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n);
            }
            return value is bool || JsonValue.AsDouble(value).HasValue;
        }

        private static bool IsContainer(object value)
        {
            return value is Dictionary<string, object> || value is List<object>;
        }
    }
}
