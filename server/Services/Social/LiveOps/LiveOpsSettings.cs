using System;
using System.Globalization;
using McvsOffline.Server.Core;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>
    /// The live-ops settings (settings.json <c>"liveops": {...}</c>, all optional; docs/LIVEOPS.md):
    /// <code>
    /// "liveops": {
    ///   "enabled": true,                       // false: no events, no CRM ids, no modifiers (the game as before)
    ///   "seasonStart": "2024-01-01T00:00:00Z", // any season start (a Monday 00:00 UTC); seasons repeat every seasonDays
    ///   "seasonDays": 7,                       // 1..28
    ///   "dailyResetHour": 0,                   // UTC hour (0..23) at which daily quests rotate
    ///   "divisionSeasons": true, "dailyQuests": true, "weeklyEvents": true,
    ///   "modifiers": "rotate",                 // "rotate" | "off" | a modifier id (always that one)
    ///   "difficulty": 1.0,                     // scales the rivals' scores (0.1..10)
    ///   "seasonRewardMultiplier": 1.0          // scales the end-of-season reward quantities (PVPDivisionRewards)
    /// }
    /// </code>
    /// The economy collector's settings are not read here: <c>CollectorOptions</c> is their only parser
    /// (docs/design/economy-collector.md §3.2, §4.4). What live ops depend on (whether the Cosmetic crate may be handed
    /// out) comes in as <c>CollectorRuntime.InputsKey</c>, which <see cref="Fingerprint"/> carries.
    /// </summary>
    public sealed class LiveOpsSettings
    {
        /// <summary>A Monday, 00:00 UTC: the default season grid and the epoch of the modifier rotation.</summary>
        public static readonly DateTime DefaultAnchor = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public bool Enabled { get; private set; }

        public DateTime SeasonAnchor { get; private set; }

        public int SeasonDays { get; private set; }

        public int DailyResetHour { get; private set; }

        public bool DivisionSeasons { get; private set; }

        public bool DailyQuests { get; private set; }

        public bool WeeklyEvents { get; private set; }

        /// <summary>"rotate", "off" or a modifier id.</summary>
        public string Modifiers { get; private set; }

        public double Difficulty { get; private set; }

        public double SeasonRewardMultiplier { get; private set; }

        /// <summary>The collector inputs these settings were read with (<c>CollectorRuntime.InputsKey</c>); null: none.</summary>
        public string CollectorKey { get; private set; }

        /// <summary>Changes whenever any value above does, <see cref="CollectorKey"/> included (part of the CRM cache key).</summary>
        public string Fingerprint { get; private set; }

        public static LiveOpsSettings Defaults()
        {
            return From(null, null);
        }

        public static LiveOpsSettings From(Settings settings, Log log)
        {
            return From(settings, log, null);
        }

        /// <param name="collectorKey"><c>CollectorRuntime.InputsKey</c> (enabled, collector.json validity, store patch):
        /// the events of windows from <c>collector.since</c> on depend on it, so the fingerprint does too. Null: left out.</param>
        public static LiveOpsSettings From(Settings settings, Log log, string collectorKey)
        {
            var s = new LiveOpsSettings();
            s.Enabled = Bool(settings, "liveops.enabled", true);
            s.SeasonAnchor = DefaultAnchor;
            string anchor = settings == null ? null : settings.GetString("liveops.seasonStart", null);
            if (!string.IsNullOrEmpty(anchor))
            {
                DateTime parsed;
                if (TimeFormat.TryParse(anchor, out parsed)) s.SeasonAnchor = new DateTime(parsed.Ticks - parsed.Ticks % LiveOpsSchedule.TicksPerHour, DateTimeKind.Utc);
                else if (log != null) log.Warn("liveops.seasonStart '" + anchor + "' is not an ISO 8601 date; using " + TimeFormat.Iso8601(DefaultAnchor));
            }
            s.SeasonDays = (int)Num.Clamp(settings == null ? 7 : settings.GetLong("liveops.seasonDays", 7), 1, 28);
            s.DailyResetHour = (int)Num.Clamp(settings == null ? 0 : settings.GetLong("liveops.dailyResetHour", 0), 0, 23);
            s.DivisionSeasons = Bool(settings, "liveops.divisionSeasons", true);
            s.DailyQuests = Bool(settings, "liveops.dailyQuests", true);
            s.WeeklyEvents = Bool(settings, "liveops.weeklyEvents", true);
            s.Modifiers = settings == null ? "rotate" : (settings.GetString("liveops.modifiers", "rotate") ?? "rotate").Trim();
            if (s.Modifiers.Length == 0) s.Modifiers = "rotate";
            s.Difficulty = Clamp(settings == null ? 1.0 : settings.GetDouble("liveops.difficulty", 1.0), 0.1, 10.0);
            s.SeasonRewardMultiplier = Clamp(settings == null ? 1.0 : settings.GetDouble("liveops.seasonRewardMultiplier", 1.0), 0.1, 100.0);
            s.CollectorKey = collectorKey;
            s.Fingerprint = string.Join("|", new[]
            {
                s.Enabled ? "on" : "off", TimeFormat.Iso8601(s.SeasonAnchor), Str(s.SeasonDays), Str(s.DailyResetHour),
                s.DivisionSeasons ? "s" : "-", s.DailyQuests ? "q" : "-", s.WeeklyEvents ? "w" : "-", s.Modifiers,
                s.Difficulty.ToString("R", CultureInfo.InvariantCulture), s.SeasonRewardMultiplier.ToString("R", CultureInfo.InvariantCulture),
            });
            if (collectorKey != null) s.Fingerprint += "|" + collectorKey;
            return s;
        }

        private static bool Bool(Settings settings, string path, bool defaultValue)
        {
            return settings == null ? defaultValue : settings.GetBool(path, defaultValue);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return 1.0;
            return value < min ? min : value > max ? max : value;
        }

        private static string Str(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
