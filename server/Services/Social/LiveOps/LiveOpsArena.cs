using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>
    /// One Special Event (Arena) mode of liveops.json <c>arena.modes</c> (docs/LIVEOPS.md §8a): what an
    /// <c>ArenaModeEvent</c> of this mode carries (GM/ArenaModeEvent.cs Initialize). content-gen expands every field
    /// (agents, multipliers, the match-type text and image) and checks it against the player's data pack.
    /// </summary>
    public sealed class LiveOpsArenaMode
    {
        /// <summary>Letters and digits only (the event id ends with "_&lt;id&gt;").</summary>
        public string Id;

        /// <summary>Loc ids (the Flash calls setStringID): the title, the rules and the match-type line of the tile.</summary>
        public string EventName, EventRule, MatchTypeText;

        /// <summary>A <c>Game.MatchType</c> name, or "None" (the GS then picks one per map, MatchTypeProbability).</summary>
        public string MatchType;

        /// <summary>MCVS_Gameplay tables the GS applies on top of the match type's own (GameMatch.ApplySettingTableForMap).</summary>
        public List<string> ParameterSets = new List<string>();

        /// <summary>"Random" or a playable map index (<see cref="LiveOpsArena.Maps"/>).</summary>
        public string Map;

        public bool AllowDuplicates;

        /// <summary>The tile's background (a data-pack path; never empty: a missing img aborts every event on the TS).</summary>
        public string Img;

        /// <summary>"Merc_&lt;id&gt;" for every agent the event allows (never empty, never "Any" or "MercClass_").</summary>
        public List<string> Agents = new List<string>();

        public double CreditsVictory = 1.0, CreditsDefeat = 1.0, CreditsMvp = 1.0;
        public double CoinsVictory = 1.0, CoinsDefeat = 1.0, CoinsMvp = 1.0;

        /// <summary>In the rotation (false: only when pinned with liveops.arenaRotation).</summary>
        public bool Rotate = true;
    }

    /// <summary>
    /// liveops.json <c>arena</c>: the Special Event modes and the playable maps, and which mode each window runs.
    /// <para>
    /// The rotation shows every rotating mode once per cycle of n windows (a shuffle of the pool seeded by the cycle) and
    /// never the same mode twice in a row (when a cycle would start with the previous cycle's last mode, its first two
    /// swap). Everything is a function of the window: nothing is stored.
    /// </para>
    /// </summary>
    public sealed class LiveOpsArena
    {
        /// <summary>The <c>Game.MatchType</c> names an event may name (CaptureFlag is Active=NO in MatchTypeSpecs).</summary>
        public static readonly string[] MatchTypes = TleTemplateValidator.ArenaMatchTypes;

        public int Priority = 3;

        public List<LiveOpsArenaMode> Modes = new List<LiveOpsArenaMode>();

        /// <summary>The playable maps (MapsList rows available in PVP whose level is in the data pack).</summary>
        public List<string> Maps = new List<string>();

        public LiveOpsArenaMode Mode(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (LiveOpsArenaMode m in Modes)
                if (string.Equals(m.Id, id, StringComparison.Ordinal)) return m;
            return null;
        }

        /// <summary>The modes of the rotation: every mode with <c>rotate</c> (all modes when none has it).</summary>
        public List<LiveOpsArenaMode> RotationPool()
        {
            var pool = new List<LiveOpsArenaMode>();
            foreach (LiveOpsArenaMode m in Modes)
                if (m.Rotate) pool.Add(m);
            return pool.Count > 0 ? pool : new List<LiveOpsArenaMode>(Modes);
        }

        /// <summary>The rotating mode of <paramref name="window"/> (null without modes).</summary>
        public LiveOpsArenaMode Rotating(ArenaWindow window)
        {
            List<LiveOpsArenaMode> pool = RotationPool();
            int n = pool.Count;
            if (n == 0) return null;
            if (n == 1) return pool[0];
            long k = window.Ordinal;
            if (n == 2) return pool[(int)Mod(k, 2)];
            long cycle = FloorDiv(k, n);
            int[] order = Cycle(cycle, n);
            int[] previous = Cycle(cycle - 1, n);
            if (order[0] == previous[n - 1])
            {
                int t = order[0];
                order[0] = order[1];
                order[1] = t;
            }
            return pool[order[(int)Mod(k, n)]];
        }

        /// <summary>The shuffle of cycle <paramref name="cycle"/> before the no-repeat swap (which only touches the first
        /// two places, so a cycle's last mode is the unswapped one for n ≥ 3).</summary>
        private static int[] Cycle(long cycle, int n)
        {
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            ulong state = LiveOpsHash.Fnv64("mcvs-offline/arena/v1|" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                                            + cycle.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = n - 1; i > 0; i--)
            {
                int j = LiveOpsHash.Below(ref state, i + 1);
                int t = order[i];
                order[i] = order[j];
                order[j] = t;
            }
            return order;
        }

        private static long Mod(long value, long n)
        {
            long m = value % n;
            return m < 0 ? m + n : m;
        }

        private static long FloorDiv(long value, long n)
        {
            return value >= 0 ? value / n : -((-value + n - 1) / n);
        }

        /// <summary>Letters and digits, 1..40 characters.</summary>
        public static bool IsModeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40) return false;
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }

        /// <summary>
        /// The <c>arena</c> block, every mode checked with the rules <see cref="TleTemplateValidator"/> applies to the
        /// event (a mode that breaks one is left out and named in <paramref name="problem"/>). Null with
        /// <paramref name="problem"/> when the block is absent or has no usable mode.
        /// </summary>
        public static LiveOpsArena Parse(Dictionary<string, object> root, out string problem)
        {
            object value = JsonValue.Get(root, "arena");
            if (value == null)
            {
                problem = "no arena block";
                return null;
            }
            var block = value as Dictionary<string, object>;
            if (block == null)
            {
                problem = "arena must be an object";
                return null;
            }
            var arena = new LiveOpsArena { Priority = (int)Num.Clamp(JsonValue.GetLong(block, "priority", 3), 1, 100) };
            foreach (object m in JsonValue.GetArray(block, "maps") ?? new List<object>())
            {
                string map = JsonValue.AsString(m);
                if (TleTemplateValidator.IsArenaMap(map) && !string.Equals(map, "Random", StringComparison.Ordinal) && !arena.Maps.Contains(map))
                    arena.Maps.Add(map);
            }
            var refused = new List<string>();
            foreach (object item in JsonValue.GetArray(block, "modes") ?? new List<object>())
            {
                var o = item as Dictionary<string, object>;
                string why;
                LiveOpsArenaMode mode = ParseMode(o, arena, out why);
                if (mode == null) refused.Add((JsonValue.GetString(o, "id") ?? "?") + ": " + why);
                else arena.Modes.Add(mode);
            }
            if (arena.Modes.Count == 0)
            {
                problem = "no usable arena mode" + (refused.Count > 0 ? " (" + string.Join("; ", refused.ToArray()) + ")" : "");
                return null;
            }
            problem = refused.Count > 0 ? "modes left out: " + string.Join("; ", refused.ToArray()) : null;
            return arena;
        }

        private static LiveOpsArenaMode ParseMode(Dictionary<string, object> o, LiveOpsArena arena, out string why)
        {
            why = null;
            if (o == null)
            {
                why = "not an object";
                return null;
            }
            var mode = new LiveOpsArenaMode
            {
                Id = JsonValue.GetString(o, "id"),
                EventName = JsonValue.GetString(o, "eventName"),
                EventRule = JsonValue.GetString(o, "rule"),
                MatchTypeText = JsonValue.GetString(o, "matchTypeText"),
                MatchType = JsonValue.GetString(o, "matchType") ?? "None",
                Map = JsonValue.GetString(o, "map") ?? "Random",
                AllowDuplicates = JsonValue.GetBool(o, "allowDuplicates", true),
                Img = JsonValue.GetString(o, "img"),
                Rotate = JsonValue.GetBool(o, "rotate", true),
            };
            if (!IsModeId(mode.Id)) why = "the id must be letters and digits";
            else if (arena.Mode(mode.Id) != null) why = "duplicate id";
            else if (string.IsNullOrEmpty(mode.EventName) || string.IsNullOrEmpty(mode.EventRule) || string.IsNullOrEmpty(mode.MatchTypeText))
                why = "eventName, rule and matchTypeText are required";
            else if (string.IsNullOrEmpty(mode.Img) || mode.Img.Trim().Length == 0) why = "no img";
            else if (Array.IndexOf(MatchTypes, mode.MatchType) < 0) why = "unknown matchType " + mode.MatchType;
            else if (!TleTemplateValidator.IsArenaMap(mode.Map)) why = "map " + mode.Map + " is not playable";
            if (why != null) return null;
            foreach (object p in JsonValue.GetArray(o, "parameterSets") ?? new List<object>())
            {
                string set = JsonValue.AsString(p);
                if (string.IsNullOrEmpty(set))
                {
                    why = "parameterSets must be table names";
                    return null;
                }
                mode.ParameterSets.Add(set);
            }
            foreach (object a in JsonValue.GetArray(o, "agents") ?? new List<object>())
            {
                string agent = a as string;
                string problem = TleTemplateValidator.AgentProblem(agent);
                if (problem != null)
                {
                    why = problem;
                    return null;
                }
                mode.Agents.Add(agent);
            }
            if (mode.Agents.Count == 0)
            {
                why = "no agents (the client's Special Event box then gets a null agent list)";
                return null;
            }
            var mult = JsonValue.GetObject(o, "multipliers");
            mode.CreditsVictory = Multiplier(mult, "creditsVictory");
            mode.CreditsDefeat = Multiplier(mult, "creditsDefeat");
            mode.CreditsMvp = Multiplier(mult, "creditsMvp");
            mode.CoinsVictory = Multiplier(mult, "versusCoinsVictory");
            mode.CoinsDefeat = Multiplier(mult, "versusCoinsDefeat");
            mode.CoinsMvp = Multiplier(mult, "versusCoinsMvp");
            return mode;
        }

        /// <summary>0.1..10, 1 when absent or not a number.</summary>
        private static double Multiplier(Dictionary<string, object> o, string key)
        {
            double? v = JsonValue.AsDouble(JsonValue.Get(o, key));
            if (!v.HasValue || double.IsNaN(v.Value)) return 1.0;
            return v.Value < 0.1 ? 0.1 : v.Value > 10 ? 10 : v.Value;
        }
    }
}
