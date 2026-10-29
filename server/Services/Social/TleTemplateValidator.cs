using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
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
    /// It also checks the Initialize methods that can throw on content: <c>DivisionSeasonEvent</c> parses the division
    /// out of <c>_participation</c> ("... CurrentDivision == &lt;n&gt;)") with int.Parse (<c>GM/DivisionSeasonEvent.cs</c>);
    /// <c>ArenaModeEvent</c> (<see cref="ArenaProblem"/>) needs an <c>img</c> and a non-empty agent list the client can
    /// use. The third Special Event trap, a gap between two windows, is a property of the list:
    /// <see cref="ArenaChainProblem"/>.
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

            if (eventType != null && eventType.EndsWith("ArenaModeEvent", StringComparison.Ordinal))
            {
                string problem = ArenaProblem(tuning);
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

        // ------------------------------------------------------------------------------------------ Special Event (Arena)

        /// <summary>The playable maps: MapsList rows available in PVP whose level is in the data pack (Barrage, Bridge and
        /// Downtown are listed but have no level: the GS cannot load them). content-gen derives the same list from the
        /// player's data and writes it as liveops.json <c>arena.maps</c>; a test keeps the two equal.</summary>
        public static readonly string[] ArenaMaps = { "Snow", "Desert", "Favelas", "Rooftop", "DogDays", "Port", "Stadium", "Stadium2" };

        /// <summary>The template keys ArenaModeEvent.Initialize reads with SafeGet&lt;float&gt;.</summary>
        public static readonly string[] ArenaMultiplierKeys =
        {
            "CreditsVictoryAmountMultiplier", "CreditsDefeatAmountMultiplier", "CreditsMVPAmountMultiplier",
            "VersusCoinsVictoryAmountMultiplier", "VersusCoinsDefeatAmountMultiplier", "VersusCoinsMVPAmountMultiplier",
        };

        /// <summary>The <c>Game.MatchType</c> names an event may name (CaptureFlag is Active=NO in MatchTypeSpecs; None
        /// lets the GS pick one per map).</summary>
        public static readonly string[] ArenaMatchTypes = { "None", "TeamDeathMatch", "ControlPoint", "Uphold", "Dominion" };

        /// <summary>An <c>ArenaParams.MapName</c> the GS can load: absent/empty or "Random" (a random PVP map), or one of
        /// <see cref="ArenaMaps"/> (GM/GameServerComponent.cs GetStartLevelArguments → MapsComponent.GetMapPathFromIndex).</summary>
        public static bool IsArenaMap(string map)
        {
            return string.IsNullOrEmpty(map) || string.Equals(map, "Random", StringComparison.Ordinal) || Array.IndexOf(ArenaMaps, map) >= 0;
        }

        /// <summary>
        /// Why an <c>AgentRestrictions</c> entry breaks the client, or null: it must be a string "&lt;letters&gt;_&lt;id&gt;"
        /// (ArenaModeEvent.Initialize int.Parses what follows the first '_'), never contain "Any" (the whole list then
        /// becomes null: <c>GetArenaUIInfo</c>'s null-conditional LINQ chain hands the Flash box
        /// <c>arenaEventRestrictedAgents = null</c>, whose <c>.length</c> MatchTypeBox.setup reads; checked on both builds
        /// by LiveOpsArenaConformanceTests), and never be a "MercClass_" entry (its LINQ Contains over a JValue is always
        /// false, so it reaches int.Parse, which throws inside Initialize).
        /// </summary>
        public static string AgentProblem(string agent)
        {
            if (agent == null) return "an agent entry is not a string";
            if (agent.IndexOf("Any", StringComparison.Ordinal) >= 0) return "agent '" + agent + "' contains Any (the client then has no agent list)";
            if (agent.StartsWith("MercClass_", StringComparison.Ordinal)) return "agent '" + agent + "' is a MercClass_ entry (int.Parse throws)";
            int at = agent.IndexOf('_');
            if (at <= 0) return "agent '" + agent + "' is not <name>_<id>";
            for (int i = 0; i < at; i++)
                if (!char.IsLetter(agent[i])) return "agent '" + agent + "' is not <name>_<id>";
            string id = agent.Substring(at + 1);
            int n;
            if (id.Length == 0 || id.Length > 4 || id[0] == '0'
                || !int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n))
                return "agent '" + agent + "' has no agent id after '_'";
            return null;
        }

        /// <summary>
        /// Why an <c>ArenaModeEvent</c>'s tuning would break the client (GM/ArenaModeEvent.cs Initialize,
        /// GM/GameClientComponent.cs GetArenaUIInfo, the GS's GetStartLevelArguments), or null:
        /// <list type="bullet">
        /// <item><c>img</c> is a non-empty string: <c>DownloadableAsset(string)</c> calls <c>Trim()</c>; a missing img
        /// throws inside Initialize and aborts RetrieveEventsAsync, so every event (seasons, quests) is lost;</item>
        /// <item><c>AgentRestrictions</c> is a non-empty raw array of <see cref="AgentProblem"/>-clean strings (null or
        /// empty gives the Flash box a null agent list);</item>
        /// <item><c>MatchType</c> one of <see cref="ArenaMatchTypes"/>; <c>MapName</c> <see cref="IsArenaMap"/>;
        /// <c>MatchParameterSets</c> an array of strings; the multipliers numbers; the four booleans booleans
        /// (<c>Value&lt;bool&gt;</c> throws on other text); <c>EventName</c>, <c>EventRule</c>, <c>MatchTypeText</c> non-empty
        /// strings (the tile's loc ids).</item>
        /// </list>
        /// A value the TS's parser would not hand over (a bare JSON value instead of a SEM element, which ParseElement
        /// ignores) counts as missing.
        /// </summary>
        public static string ArenaProblem(Dictionary<string, object> tuning)
        {
            object img;
            if (!TryRaw(tuning, "img", out img) || !(img is string) || ((string)img).Trim().Length == 0)
                return "ArenaModeEvent: img must be a non-empty string (a missing img throws in Initialize and drops every event)";
            object agents;
            if (!TryRaw(tuning, "AgentRestrictions", out agents) || !(agents is List<object>) || ((List<object>)agents).Count == 0)
                return "ArenaModeEvent: AgentRestrictions must be a non-empty list (otherwise the Special Event box gets a null agent list)";
            foreach (object a in (List<object>)agents)
            {
                string p = AgentProblem(a as string);
                if (p != null) return "ArenaModeEvent: " + p;
            }
            foreach (string key in new[] { "EventName", "EventRule", "MatchTypeText" })
            {
                object text;
                if (!TryRaw(tuning, key, out text) || !(text is string) || ((string)text).Length == 0)
                    return "ArenaModeEvent: " + key + " must be a non-empty loc id";
            }
            object value;
            if (TryRaw(tuning, "MatchType", out value) && value != null && (!(value is string) || Array.IndexOf(ArenaMatchTypes, (string)value) < 0))
                return "ArenaModeEvent: MatchType " + MiniJson.Serialize(value) + " is not one of " + string.Join("|", ArenaMatchTypes);
            if (TryRaw(tuning, "MapName", out value) && value != null && (!(value is string) || !IsArenaMap((string)value)))
                return "ArenaModeEvent: MapName " + MiniJson.Serialize(value) + " is not Random or a playable map (the GS cannot load it)";
            if (TryRaw(tuning, "MatchParameterSets", out value) && value != null)
            {
                var sets = value as List<object>;
                if (sets == null) return "ArenaModeEvent: MatchParameterSets must be a list";
                foreach (object set in sets)
                    if (!(set is string)) return "ArenaModeEvent: MatchParameterSets must hold table names";
            }
            foreach (string key in ArenaMultiplierKeys)
                if (TryRaw(tuning, key, out value) && value != null && !(value is long || value is double))
                    return "ArenaModeEvent: " + key + " must be a number";
            foreach (string key in new[] { "AllowDuplicateMercs", "ForcedMercModTier", "GearsAllowed", "PlayerLevelBonus" })
                if (TryRaw(tuning, key, out value) && value != null && !(value is bool))
                    return "ArenaModeEvent: " + key + " must be a boolean";
            return null;
        }

        /// <summary>
        /// Why a list of Special Events (the ones the CRM lists, in any order) would leave the client holding an event the
        /// TS no longer has, or null. The client never clears its current event (GameClientComponent
        /// UpdateCurrentAndNextArenaEvent only assigns), while the TS takes the first event in progress for the room
        /// (GameMatchmakingComponent.AddArenaEventParametersToArgs); without one the room gets <c>_arena</c> but no
        /// <c>_arenaParams</c> and the GS throws. So at <paramref name="utc"/> one event must be in progress and the next
        /// must start exactly when it ends (listed already: it starts on time even if the CRM refresh is late), with no
        /// gap and no overlap anywhere in the chain.
        /// </summary>
        public static string ArenaChainProblem(IList<Dictionary<string, object>> events, DateTime utc)
        {
            var windows = new List<KeyValuePair<DateTime, DateTime>>();
            foreach (Dictionary<string, object> e in events)
            {
                DateTime start, end;
                if (!TimeFormat.TryParse(JsonValue.GetString(e, "start_date"), out start)
                    || !TimeFormat.TryParse(JsonValue.GetString(e, "end_date"), out end) || end <= start)
                    return "Special Event " + JsonValue.GetString(e, "id") + " has no valid start_date/end_date";
                windows.Add(new KeyValuePair<DateTime, DateTime>(start, end));
            }
            windows.Sort((a, b) => a.Key.CompareTo(b.Key));
            int current = -1;
            for (int i = 0; i < windows.Count; i++)
            {
                if (windows[i].Key <= utc && utc < windows[i].Value) current = i;
                if (i > 0 && windows[i].Key != windows[i - 1].Value)
                    return "Special Events " + (windows[i].Key > windows[i - 1].Value ? "leave a gap" : "overlap") + " at "
                           + TimeFormat.Iso8601(windows[i - 1].Value);
            }
            if (current < 0) return "no Special Event is in progress at " + TimeFormat.Iso8601(utc);
            if (current == windows.Count - 1)
                return "the next Special Event is not listed (the chain ends at " + TimeFormat.Iso8601(windows[current].Value) + ")";
            return null;
        }

        /// <summary>The value the TS's parser yields for a tuning element: a "private" element's raw value, or a text
        /// element's (first) string. False for anything else (a bare value, another SEM type, absent).</summary>
        private static bool TryRaw(Dictionary<string, object> tuning, string key, out object value)
        {
            value = null;
            var element = JsonValue.Get(tuning, key) as Dictionary<string, object>;
            if (element == null) return false;
            if (element.ContainsKey("private"))
            {
                value = JsonValue.Get(element, "value");
                return true;
            }
            string type = JsonValue.GetString(element, "type");
            if (type == "text" || type == "text_plain")
            {
                object text = JsonValue.Get(element, "value");
                var localized = text as Dictionary<string, object>;
                if (localized != null)
                {
                    foreach (var kv in localized)
                    {
                        value = kv.Value;
                        return true;
                    }
                    return false;
                }
                value = text;
                return true;
            }
            return false;
        }


        private static bool IsContainer(object value)
        {
            return value is Dictionary<string, object> || value is List<object>;
        }
    }
}
