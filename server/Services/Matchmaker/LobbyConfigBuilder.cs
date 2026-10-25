using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Matchmaker
{
    /// <summary>
    /// Settings of the matchmaker module (settings.json <c>"matchmaker"</c>, all optional):
    /// <code>
    /// "matchmaker": {
    ///   "tsArgs": "", "gsArgs": "",          // extra cmd_args, e.g. "-AllowP2P" or "-LocalAnubisHost 0.0.0.0" (verify-C6)
    ///   "passDataCenter": true,              // add -DataCenter (verify-C2 §3 row 8)
    ///   "tsRoomCapacity": 5, "gsRoomCapacity": 8,
    ///   "midgameJoin": null,                 // null: true in LAN mode (several humans share a room), else false
    ///   "automatchers": ["pvpdeathmatch", "dev"]
    /// }
    /// </code>
    /// </summary>
    public sealed class MatchmakerOptions
    {
        public MatchmakerOptions()
        {
            TsArgs = "";
            GsArgs = "";
            PassDataCenter = true;
            TsRoomCapacity = 5;
            GsRoomCapacity = 8;
            Automatchers = new List<string> { "pvpdeathmatch", "dev" };
        }

        public string TsArgs { get; set; }

        public string GsArgs { get; set; }

        public bool PassDataCenter { get; set; }

        public int TsRoomCapacity { get; set; }

        public int GsRoomCapacity { get; set; }

        /// <summary>Explicit midgame_join; null means "true only in LAN mode".</summary>
        public bool? MidgameJoin { get; set; }

        public List<string> Automatchers { get; set; }

        public static MatchmakerOptions FromSettings(Settings settings)
        {
            var o = new MatchmakerOptions();
            o.TsArgs = settings.GetString("matchmaker.tsArgs", "");
            o.GsArgs = settings.GetString("matchmaker.gsArgs", "");
            o.PassDataCenter = settings.GetBool("matchmaker.passDataCenter", true);
            o.TsRoomCapacity = (int)Num.Clamp(settings.GetLong("matchmaker.tsRoomCapacity", 5), 1, 64);
            o.GsRoomCapacity = (int)Num.Clamp(settings.GetLong("matchmaker.gsRoomCapacity", 8), 1, 64);
            o.MidgameJoin = JsonValue.AsBool(settings.Get("matchmaker.midgameJoin"));
            var names = JsonValue.AsArray(settings.Get("matchmaker.automatchers"));
            if (names != null)
            {
                o.Automatchers = new List<string>();
                foreach (object n in names)
                {
                    string s = JsonValue.AsString(n);
                    if (!string.IsNullOrEmpty(s) && !o.Automatchers.Contains(s)) o.Automatchers.Add(s);
                }
            }
            return o;
        }
    }

    /// <summary>
    /// Builds the <c>lobbyconfigs/{cid}/{serverType}</c> answer that LocalAnubis' <c>GameConfig.FindGame</c> reads
    /// (LA/GameConfig.cs; report 06 §4.8).
    /// <para>
    /// The Controller swaps <c>GameApplication.Args</c> for the parsed <c>cmd_args</c> while it builds the in-process
    /// server World (LA/Controller.cs:71-98), so cmd_args must carry everything that World reads from Args: the eve URL
    /// (otherwise it talks to the real eve.gameloft.com), the super credential, <c>-ServerType</c>, and for the TS
    /// <c>-Level Levels/Menus.level</c> (otherwise it loads a random PvP map). The GS must not get <c>-Level</c>
    /// (verify-C4 §4.2). <c>GetConfigArgs</c> splits on every '-' and turns the first space of each piece into the
    /// key/value separator, so no value may contain '-' or whitespace (verify-C2 §1.4, ARCHITECTURE §2.2).
    /// </para>
    /// </summary>
    public static class LobbyConfigBuilder
    {
        public const string MenusLevel = "Levels/Menus.level";

        public static Dictionary<string, object> Build(string serverType, string baseUrl, int port, Settings settings, MatchmakerOptions options, Log log)
        {
            log = log ?? Log.Null;
            string type = (serverType ?? "").Trim().ToLowerInvariant();
            bool isTs = type == "ts";
            bool lan = settings.LanMode;
            bool midgame = options.MidgameJoin ?? lan;

            var game = NewObject();
            // ServerType = game.server_type ?? "default"; "ts" makes the Lobby create a TS room, "gs" makes IsGS true
            // (verify-C2 §1.3, verify-C4 §4.2).
            game["server_type"] = type;
            game["room_capacity"] = (long)(isTs ? options.TsRoomCapacity : options.GsRoomCapacity);
            game["max_tags"] = 2L;
            game["room_timeout"] = 600L;
            // Keep 0: a launch timeout makes the Lobby reaper call DateTime.Parse on the room's "created " key, which
            // LocalAnubis writes with a trailing space, i.e. on null (report 06 §4.8/§9).
            game["room_launch_timeout"] = 0L;
            game["reconnect_grace_period"] = 10L;
            game["socket_timeout"] = 10L;
            // Solo: every automatch makes its own room (rooms launch at once). LAN: humans must be able to join a
            // launched room (report 06 §4.5).
            game["midgame_join"] = midgame;
            game["room_info_on_start"] = true;
            game["cmd_args"] = BuildCmdArgs(type, baseUrl, port, settings, options, log);

            // GameConfig.GetAutomacher uses First(name == ...): an automatcher name the game uses but we do not list
            // throws inside the Finder (fatal, report 06 B1/B5). The quick-match template uses "pvpdeathmatch"
            // (SceneComponents.tx /Matchmaking); "dev" is the engine default (EN/MatchmakingComponent.cs:244).
            var automatchers = NewObject();
            int i = 0;
            foreach (string name in options.Automatchers)
            {
                var a = NewObject();
                a["name"] = name;
                a["midgame_join"] = midgame;
                a["instant"] = false;
                a["automatch_wait_launch"] = true;
                a["score_automatch_enabled"] = false;
                a["region_isolation"] = false;
                a["country_geolocation"] = false;
                a["group_automatch_by_team_capacity"] = false;
                automatchers["a" + (++i).ToString(CultureInfo.InvariantCulture)] = a;
            }

            var result = NewObject();
            result["game"] = game;
            result["automatchers"] = automatchers;
            return result;
        }

        /// <summary>The cmd_args string for a server type (see the class remarks for the format rules).</summary>
        /// <param name="port">The port this server listens on (used when <paramref name="baseUrl"/> is not cmd-arg safe).</param>
        public static string BuildCmdArgs(string serverType, string baseUrl, int port, Settings settings, MatchmakerOptions options, Log log)
        {
            log = log ?? Log.Null;
            string type = (serverType ?? "").Trim().ToLowerInvariant();
            var args = new List<KeyValuePair<string, string>>();
            if (type == "ts") args.Add(Pair("Level", MenusLevel));
            args.Add(Pair("ServerType", type));

            string url = baseUrl;
            if (!Settings.IsCmdArgSafe(url))
            {
                // Possible in LAN mode with a host name like "my-pc": the in-process worlds run on this device anyway.
                string fallback = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                log.Warn("lobbyconfigs: base URL '" + url + "' contains '-' or spaces (not allowed in cmd_args); using " + fallback);
                url = fallback;
            }
            args.Add(Pair("EnvironmentSwitcherUrl", url));
            if (options.PassDataCenter) args.Add(Pair("DataCenter", settings.DataCenter));
            // The server Worlds read the super credential only from the swapped Args (EN/AuthenticationComponent.cs:312),
            // the client's own -SuperCredential does not reach them (verify-C2 §2).
            args.Add(Pair("SuperCredential", settings.SuperCredential));
            args.Add(Pair("SuperPassword", settings.SuperPassword));

            var sb = new StringBuilder();
            foreach (var kv in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('-').Append(kv.Key).Append(' ').Append(kv.Value);
            }
            string extra = type == "ts" ? options.TsArgs : options.GsArgs;
            string checkedExtra = CheckExtraArgs(extra, type, log);
            if (checkedExtra.Length > 0) sb.Append(' ').Append(checkedExtra);
            return sb.ToString();
        }

        /// <summary>
        /// Validates user-supplied extra arguments ("-AllowP2P -LocalAnubisHost 0.0.0.0"): keys are letters/digits,
        /// values must be cmd-arg safe. Invalid input is ignored as a whole (and logged) rather than corrupting the
        /// Args of the in-process server Worlds.
        /// </summary>
        public static string CheckExtraArgs(string extra, string serverType, Log log)
        {
            if (string.IsNullOrEmpty(extra) || extra.Trim().Length == 0) return "";
            string[] tokens = extra.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            bool expectKey = true;
            foreach (string token in tokens)
            {
                if (token.StartsWith("-", StringComparison.Ordinal))
                {
                    string key = token.Substring(1);
                    if (key.Length == 0 || !IsIdentifier(key))
                        return Reject(extra, serverType, "'" + token + "' is not a valid argument name", log);
                    expectKey = false;
                }
                else
                {
                    if (expectKey) return Reject(extra, serverType, "value '" + token + "' has no argument name", log);
                    if (!Settings.IsCmdArgSafe(token)) return Reject(extra, serverType, "value '" + token + "' contains '-'", log);
                    expectKey = true;
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(token);
            }
            return sb.ToString();
        }

        private static string Reject(string extra, string serverType, string why, Log log)
        {
            log.Warn("settings: matchmaker." + serverType + "Args '" + extra + "' ignored: " + why
                     + " (cmd_args are split on '-', verify-C2 §1.4)");
            return "";
        }

        private static bool IsIdentifier(string s)
        {
            foreach (char c in s)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
            }
            return true;
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Dictionary<string, object> NewObject()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }
}
