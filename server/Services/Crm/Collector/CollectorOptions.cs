using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Crm.Collector
{
    /// <summary>
    /// The collector settings (settings.json <c>"economy": {"collector": {...}}</c>, all optional;
    /// docs/design/economy-collector.md §4.4). <see cref="FromSettings"/> is the only parser of <c>economy.collector.*</c>:
    /// live ops and the CRM read these options through <see cref="CollectorRuntime"/>, never the keys.
    /// <code>
    /// "collector": {
    ///   "enabled": true,                  // false (or unreadable): the guard from since on, events as before (O7)
    ///   "itemsPerCrate": 5,               // 1..10: CratesCount of the Cosmetic crate (O3); tier crates band × N
    ///   "baseShare": 0.75,                // 0..1: share of draws for the no-source tier while both tiers have items
    ///   "weights": {"avatars": 1, "backgrounds": 1, "sounds": 1, "skins": 1, "hiddenJerseys": 1, "agents": 2,
    ///               "camos": 1, "jerseys": 1},   // each 0..100 (0 removes the category; agents = units per agent)
    ///   "seasonCrates": [1, 1, 2, 2, 3, 3, 4],   // band per tier, Starter..Master, 0..10 (0: today's tier crate)
    ///   "maxSeasonItems": 40,             // 10..100: cap of one season claim
    ///   "seasonEmblems": true,            // ranks 1-3 also get the season emblem
    ///   "unlockableWindow": 160,          // 0..400: Unlockable "true" cells per document
    ///   "levelUpAvatars": 0,              // 0..3: PlayerLevelXP AvatarRewards for levels 2-50
    ///   "exhaustedReward": "VersusCoins_Tiers4"  // the exhausted row (row 1): VersusCoins_Tiers0..6
    /// }
    /// </code>
    /// Out-of-range values are clamped, values of the wrong type replaced by the default; both are logged
    /// (<c>settings: economy.collector.&lt;key&gt; …</c>). The bools also read 1/0, "true"/"false", "on"/"off" and
    /// "yes"/"no", and <c>"collector": false</c> (a bool instead of the object) is read as <c>enabled: false</c>, so the
    /// plain ways of writing the opt-out (O7) switch it off. The kill switch fails toward today's behaviour (owner
    /// decision on O7): an <c>enabled</c> value that cannot be read, or a <c>collector</c> that is neither an object nor
    /// a bool, switches the collector <b>off</b>, logged as an error; so does a settings.json the server cannot parse at
    /// all (<see cref="Settings.IsFallback"/>: its in-memory defaults say on, but the player's switch is unknown). Absent or
    /// null means the default, on.
    /// </summary>
    public sealed class CollectorOptions
    {
        public const string SettingsPath = "economy.collector";

        /// <summary>O3 (owner, after the pace table of §4.3).</summary>
        public const int DefaultItemsPerCrate = 5;

        /// <summary>The division tiers, Starter..Master (<c>seasonCrates</c> has one band per tier).</summary>
        public const int TierCount = 7;

        public const string DefaultExhaustedReward = "VersusCoins_Tiers4";

        /// <summary>The highest <c>VersusCoinsTiers</c> row (Tiers0..Tiers6 in 1.17.32).</summary>
        public const int MaxVersusCoinsTier = 6;

        private static readonly string[] WeightKeys =
        {
            CollectorContent.Avatars, CollectorContent.Backgrounds, CollectorContent.Sounds, CollectorContent.Skins,
            CollectorContent.HiddenJerseys, CollectorContent.Agents, CollectorContent.Camos, CollectorContent.Jerseys,
        };

        private static readonly string[] Keys =
        {
            "enabled", "itemsPerCrate", "baseShare", "weights", "seasonCrates", "maxSeasonItems", "seasonEmblems",
            "unlockableWindow", "levelUpAvatars", "exhaustedReward",
        };

        public bool Enabled { get; set; }

        public int ItemsPerCrate { get; set; }

        public double BaseShare { get; set; }

        /// <summary>The weight of every category (§4.1), keyed by the row category names of collector.json.</summary>
        public Dictionary<string, double> Weights { get; set; }

        /// <summary>The band of each tier, Starter..Master (<see cref="TierCount"/> values).</summary>
        public int[] SeasonCrates { get; set; }

        public int MaxSeasonItems { get; set; }

        public bool SeasonEmblems { get; set; }

        public int UnlockableWindow { get; set; }

        public int LevelUpAvatars { get; set; }

        /// <summary><c>VersusCoins_Tiers&lt;n&gt;</c>: the only exhausted rewards the crate preview shows (§2.3.1).</summary>
        public string ExhaustedReward { get; set; }

        public CollectorOptions()
        {
            Enabled = true;
            ItemsPerCrate = DefaultItemsPerCrate;
            BaseShare = 0.75;
            Weights = DefaultWeights();
            SeasonCrates = new[] { 1, 1, 2, 2, 3, 3, 4 };
            MaxSeasonItems = 40;
            SeasonEmblems = true;
            UnlockableWindow = 160;
            LevelUpAvatars = 0;
            ExhaustedReward = DefaultExhaustedReward;
        }

        /// <summary><c>Type</c> of the exhausted row ("VersusCoins").</summary>
        public string ExhaustedType
        {
            get { return ExhaustedReward.Substring(0, ExhaustedReward.IndexOf('_')); }
        }

        /// <summary><c>Subtype</c> of the exhausted row ("Tiers4").</summary>
        public string ExhaustedSubtype
        {
            get { return ExhaustedReward.Substring(ExhaustedReward.IndexOf('_') + 1); }
        }

        /// <summary>The weight of a category; 0 for one it does not know (the exhausted row has none).</summary>
        public double Weight(string category)
        {
            double weight;
            return category != null && Weights != null && Weights.TryGetValue(category, out weight) ? weight : 0;
        }

        /// <summary>A string that changes whenever any option does.</summary>
        public string Fingerprint
        {
            get
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                d["enabled"] = Enabled;
                d["itemsPerCrate"] = (long)ItemsPerCrate;
                d["baseShare"] = BaseShare;
                var weights = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (string key in WeightKeys) weights[key] = Weight(key);
                d["weights"] = weights;
                var bands = new List<object>();
                foreach (int band in SeasonCrates ?? new int[0]) bands.Add((long)band);
                d["seasonCrates"] = bands;
                d["maxSeasonItems"] = (long)MaxSeasonItems;
                d["seasonEmblems"] = SeasonEmblems;
                d["unlockableWindow"] = (long)UnlockableWindow;
                d["levelUpAvatars"] = (long)LevelUpAvatars;
                d["exhaustedReward"] = ExhaustedReward;
                return MiniJson.Serialize(d);
            }
        }

        public static CollectorOptions FromSettings(Settings settings, Log log)
        {
            log = log ?? Log.Null;
            var o = new CollectorOptions();
            if (settings != null && settings.IsFallback)
            {
                // The running settings are the defaults (enabled: true), not the player's file: the kill switch in it
                // cannot be read, so it counts as off (O7) until the file is fixed or repaired.
                o.Enabled = false;
                log.Error("settings: settings.json cannot be read, so neither can " + SettingsPath + ".enabled; the collector is "
                          + "switched off (an unreadable kill switch restores the original rewards) until the file is fixed");
                return o;
            }
            object raw = settings == null ? null : settings.Get(SettingsPath);
            if (raw == null) return o;
            var s = raw as Dictionary<string, object>;
            if (s == null)
            {
                // "collector": false is a plain opt-out (O7): honour it rather than fall back to the default "on".
                bool? shorthand = Switch(raw);
                if (shorthand.HasValue)
                {
                    o.Enabled = shorthand.Value;
                    log.Warn("settings: " + SettingsPath + " is " + MiniJson.Serialize(raw) + ", not an object; read as " + SettingsPath
                             + ".enabled = " + (o.Enabled ? "true" : "false") + " with the other defaults");
                    return o;
                }
                // The kill switch cannot be read: fail toward today's behaviour (O7), loudly.
                o.Enabled = false;
                log.Error("settings: " + SettingsPath + " = " + MiniJson.Serialize(raw) + " is not an object; " + SwitchedOff);
                return o;
            }
            foreach (string key in s.Keys)
                if (Array.IndexOf(Keys, key) < 0) log.Warn("settings: " + SettingsPath + "." + key + " is not a collector setting; ignored");

            o.Enabled = ReadEnabled(s, log);
            o.ItemsPerCrate = Int(s, "itemsPerCrate", o.ItemsPerCrate, 1, 10, log);
            o.BaseShare = Real(s, "baseShare", o.BaseShare, 0, 1, log);
            o.Weights = ReadWeights(s, log);
            o.SeasonCrates = ReadSeasonCrates(s, o.SeasonCrates, log);
            o.MaxSeasonItems = Int(s, "maxSeasonItems", o.MaxSeasonItems, 10, 100, log);
            o.SeasonEmblems = Bool(s, "seasonEmblems", o.SeasonEmblems, log);
            o.UnlockableWindow = Int(s, "unlockableWindow", o.UnlockableWindow, 0, 400, log);
            o.LevelUpAvatars = Int(s, "levelUpAvatars", o.LevelUpAvatars, 0, 3, log);
            o.ExhaustedReward = ReadExhaustedReward(s, log);
            return o;
        }

        /// <summary>
        /// True for <c>VersusCoins_Tiers0</c>..<c>VersusCoins_Tiers6</c>. §4.4 also lists <c>UpgradeTokens_Tiers&lt;n&gt;</c>,
        /// but the preview-safety rule (§2.3.1) and content-gen admit only VersusCoins tier rows with weight: the rule wins.
        /// </summary>
        public static bool IsExhaustedReward(string value)
        {
            const string prefix = "VersusCoins_Tiers";
            if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 1) return false;
            char tier = value[prefix.Length];
            return tier >= '0' && tier <= (char)('0' + MaxVersusCoinsTier);
        }

        private static Dictionary<string, double> DefaultWeights()
        {
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (string key in WeightKeys) weights[key] = 1.0;
            weights[CollectorContent.Agents] = 2.0;
            return weights;
        }

        private static Dictionary<string, double> ReadWeights(Dictionary<string, object> s, Log log)
        {
            Dictionary<string, double> weights = DefaultWeights();
            object raw;
            if (!s.TryGetValue("weights", out raw) || raw == null) return weights;
            var given = raw as Dictionary<string, object>;
            if (given == null)
            {
                log.Warn("settings: " + SettingsPath + ".weights is not an object; using the default weights");
                return weights;
            }
            foreach (string key in given.Keys)
            {
                if (Array.IndexOf(WeightKeys, key) < 0)
                {
                    log.Warn("settings: " + SettingsPath + ".weights." + key + " is not a category (" + string.Join(", ", WeightKeys) + "); ignored");
                    continue;
                }
                weights[key] = Real(given, key, weights[key], 0, 100, log, "weights.");
            }
            return weights;
        }

        private static int[] ReadSeasonCrates(Dictionary<string, object> s, int[] defaults, Log log)
        {
            object raw;
            if (!s.TryGetValue("seasonCrates", out raw) || raw == null) return defaults;
            var list = raw as List<object>;
            if (list == null || list.Count != TierCount)
            {
                log.Warn("settings: " + SettingsPath + ".seasonCrates is not a list of " + Text(TierCount) + " bands (Starter..Master); using the defaults");
                return defaults;
            }
            var bands = new int[TierCount];
            for (int i = 0; i < TierCount; i++)
            {
                long? band = JsonValue.AsLong(list[i]);
                if (!band.HasValue || list[i] is bool)
                {
                    log.Warn("settings: " + SettingsPath + ".seasonCrates[" + Text(i) + "] is not an integer; using " + Text(defaults[i]));
                    bands[i] = defaults[i];
                    continue;
                }
                bands[i] = (int)Clamp(band.Value, 0, 10, "seasonCrates[" + Text(i) + "]", log);
            }
            return bands;
        }

        private static string ReadExhaustedReward(Dictionary<string, object> s, Log log)
        {
            object raw;
            if (!s.TryGetValue("exhaustedReward", out raw) || raw == null) return DefaultExhaustedReward;
            string value = raw as string;
            if (IsExhaustedReward(value)) return value;
            log.Warn("settings: " + SettingsPath + ".exhaustedReward '" + JsonValue.AsString(raw) + "' is not VersusCoins_Tiers0.."
                     + Text(MaxVersusCoinsTier) + " (the crate preview shows only those, §2.3.1); using " + DefaultExhaustedReward);
            return DefaultExhaustedReward;
        }

        private const string SwitchedOff = "the collector is switched off (an unreadable kill switch restores the original rewards); "
                                           + "write \"enabled\": true or false";

        /// <summary>
        /// <c>economy.collector.enabled</c> as the collector reads it: null (absent) is the default, on; true/false and the
        /// spellings of <see cref="FromSettings"/>; anything else is off (O7: the kill switch fails toward today's behaviour).
        /// </summary>
        public static bool ReadEnabled(object raw)
        {
            if (raw == null) return true;
            bool? value = Switch(raw);
            return value.HasValue && value.Value;
        }

        /// <summary><c>enabled</c>: absent or null is the default (on); a value that cannot be read is off (O7), an error.</summary>
        private static bool ReadEnabled(Dictionary<string, object> s, Log log)
        {
            object raw;
            if (!s.TryGetValue("enabled", out raw) || raw == null) return true;
            if (!Switch(raw).HasValue) log.Error("settings: " + SettingsPath + ".enabled = " + MiniJson.Serialize(raw) + " is not a bool; " + SwitchedOff);
            return ReadEnabled(raw);
        }

        private static bool Bool(Dictionary<string, object> s, string key, bool defaultValue, Log log)
        {
            object raw;
            if (!s.TryGetValue(key, out raw) || raw == null) return defaultValue;
            bool? value = Switch(raw);
            if (value.HasValue) return value.Value;
            log.Warn("settings: " + SettingsPath + "." + key + " = " + MiniJson.Serialize(raw) + " is not a bool; using " + (defaultValue ? "true" : "false"));
            return defaultValue;
        }

        /// <summary>
        /// A bool as <see cref="JsonValue.AsBool"/> reads it (true/false, 1/0, "true"/"false"/"1"/"0"), and also the words
        /// "on"/"off" and "yes"/"no" (any case): someone switching the collector off with one of them means off. Null otherwise.
        /// </summary>
        internal static bool? Switch(object raw)
        {
            bool? value = JsonValue.AsBool(raw);
            if (value.HasValue) return value;
            string text = raw as string;
            if (text == null) return null;
            text = text.Trim();
            if (string.Equals(text, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        private static int Int(Dictionary<string, object> s, string key, int defaultValue, int min, int max, Log log)
        {
            object raw;
            if (!s.TryGetValue(key, out raw) || raw == null) return defaultValue;
            long? value = raw is bool ? null : JsonValue.AsLong(raw);
            if (!value.HasValue)
            {
                log.Warn("settings: " + SettingsPath + "." + key + " = " + JsonValue.AsString(raw) + " is not an integer; using " + Text(defaultValue));
                return defaultValue;
            }
            return (int)Clamp(value.Value, min, max, key, log);
        }

        private static long Clamp(long value, long min, long max, string key, Log log)
        {
            long clamped = Num.Clamp(value, min, max);
            if (clamped != value)
                log.Warn("settings: " + SettingsPath + "." + key + " = " + value.ToString(CultureInfo.InvariantCulture) + " is out of range "
                         + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture)
                         + "; using " + clamped.ToString(CultureInfo.InvariantCulture));
            return clamped;
        }

        private static double Real(Dictionary<string, object> s, string key, double defaultValue, double min, double max, Log log,
                                   string prefix = "")
        {
            object raw;
            if (!s.TryGetValue(key, out raw) || raw == null) return defaultValue;
            double? value = raw is bool ? null : JsonValue.AsDouble(raw);
            if (!value.HasValue || double.IsNaN(value.Value))
            {
                log.Warn("settings: " + SettingsPath + "." + prefix + key + " = " + JsonValue.AsString(raw) + " is not a number; using "
                         + defaultValue.ToString("R", CultureInfo.InvariantCulture));
                return defaultValue;
            }
            double clamped = value.Value < min ? min : value.Value > max ? max : value.Value;
            if (clamped != value.Value)
                log.Warn("settings: " + SettingsPath + "." + prefix + key + " = " + value.Value.ToString("R", CultureInfo.InvariantCulture)
                         + " is out of range " + min.ToString("R", CultureInfo.InvariantCulture) + ".." + max.ToString("R", CultureInfo.InvariantCulture)
                         + "; using " + clamped.ToString("R", CultureInfo.InvariantCulture));
            return clamped;
        }

        private static string Text(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
