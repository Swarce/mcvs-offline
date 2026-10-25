using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>What the CRM document needs from live ops at one moment.</summary>
    public sealed class LiveOpsSnapshot
    {
        public LiveOpsSettings Settings;
        public LiveOpsWindow Season;
        public LiveOpsWindow Day;
        public LiveOpsModifier Modifier;

        /// <summary>Event ids for <c>groupware.events.participation</c> (empty when live ops are off).</summary>
        public List<string> EventIds = new List<string>();

        /// <summary>The next schedule change (strictly in the future); the CRM <c>expiry</c>, so the TS refetches CRM and
        /// events exactly then (EN/CRMComponent.cs OnConstantUpdate → OnCrmExpired → RefreshCrmConfig). Null when off.</summary>
        public DateTime? Expiry;

        /// <summary><c>_OverrideTables</c> MUL cells: the season's modifier and the season-reward multiplier.</summary>
        public List<LiveOpsCells> Cells = new List<LiveOpsCells>();

        /// <summary>Changes exactly when <see cref="EventIds"/>, <see cref="Expiry"/> or <see cref="Cells"/> do.</summary>
        public string Key = "";
    }

    /// <summary>Everything the leaderboard needs to generate a season board's rivals.</summary>
    public sealed class SeasonBoardInfo
    {
        public LiveOpsWindow Season;
        public int Division;
        public int PromotionZone;
        public int DemotionZone;
        public int TrophiesWin;
        public int TrophiesLose;
        public double MatchesPerDay;
        public double TopFactor;
        public double SafeFraction;
        public int MasterTopRank;
        public int DefaultLevel;
        public double Difficulty;
        public double RivalFactor;
        public bool FromDataPack;
        public Func<int, int?> LevelBonus;

        /// <summary>Division trophies a win pays at <paramref name="level"/> (GM/GameplayComponent.cs ApplyTrophiesResult).</summary>
        public int TrophiesPerWin(int level)
        {
            int? bonus = LevelBonus == null ? null : LevelBonus(level);
            int win = TrophiesWin + (bonus.HasValue ? bonus.Value : (level < 2 ? 2 : level));
            return win < 1 ? 1 : win;
        }
    }

    /// <summary>
    /// The live-ops service: weekly division seasons, daily quests and weekly achievement events generated from the clock
    /// (<see cref="LiveOpsSchedule"/>), the CRM layer that announces them (ids, expiry, weekly modifiers) and the season
    /// boards' rival tuning. Registered as <see cref="ISocialEventProvider"/>; one shared instance per server
    /// (<see cref="For"/>), stateless apart from caches: restarts change nothing.
    /// </summary>
    public sealed class LiveOpsService : ISocialEventProvider
    {
        private static readonly object ForGate = new object();

        private readonly ServerContext context;
        private readonly Log log;
        private readonly object gate = new object();
        private string catalogText;
        private bool catalogParsed;
        private LiveOpsCatalog catalog = new LiveOpsCatalog();
        private string settingsKey;
        private LiveOpsSettings settings = LiveOpsSettings.Defaults();
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        public LiveOpsService(ServerContext context)
        {
            this.context = context;
            log = context.Logger.For("liveops");
            Tables = new DivisionTables(context.Clock, ReadDesignTables, log);
        }

        /// <summary>
        /// Design/MCVS_Meta.xlsx from the player's data pack, located like the CRM preconfiguration: the host's data pack,
        /// then <c>crm.dataPackDir</c>, then <c>&lt;data&gt;/../Data</c> (the embedded layout, where our data folder sits
        /// next to the game's Data folder).
        /// </summary>
        private string ReadDesignTables()
        {
            string text;
            if (context.Content.DataPack != null && context.Content.TryReadGameText(DivisionTables.LogicalPath, out text)) return text;
            var candidates = new List<string>();
            string configured = context.Settings.GetString("crm.dataPackDir", null);
            if (!string.IsNullOrEmpty(configured)) candidates.Add(configured);
            if (!string.IsNullOrEmpty(context.DataDir)) candidates.Add(System.IO.Path.Combine(System.IO.Path.Combine(context.DataDir, ".."), "Data"));
            foreach (string dir in candidates)
            {
                try
                {
                    var pack = new McvsOffline.Server.Core.Content.DataPack(dir);
                    if (pack.IsValid && pack.TryReadText(DivisionTables.LogicalPath, out text)) return text;
                }
                catch (Exception ex)
                {
                    log.Warn("liveops: cannot read the data pack at " + dir + ": " + ex.Message);
                }
            }
            return null;
        }

        /// <summary>Reads the division tables on a background thread (the first season board otherwise pays for it).</summary>
        public void StartWarmup()
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    bool loaded = Tables.IsLoaded;
                    log.Debug("liveops: warm-up done (division tables " + (loaded ? "from the data pack" : "not available yet") + ")");
                }
                catch (Exception ex)
                {
                    log.Error("liveops: warm-up failed", ex);
                }
            });
            thread.IsBackground = true;
            thread.Name = "mcvs-liveops-warmup";
            thread.Start();
        }

        /// <summary>The server's shared instance (created on first use, also registered as the event provider).</summary>
        public static LiveOpsService For(ServerContext context)
        {
            lock (ForGate)
            {
                LiveOpsService liveOps = context.Features.Get<LiveOpsService>();
                if (liveOps != null) return liveOps;
                liveOps = new LiveOpsService(context);
                context.Features.Set(liveOps);
                if (context.Features.Get<ISocialEventProvider>() == null) context.Features.Set<ISocialEventProvider>(liveOps);
                return liveOps;
            }
        }

        public DivisionTables Tables { get; private set; }

        public LiveOpsSettings Settings
        {
            get
            {
                string key = MiniJson.Serialize(JsonValue.Get(context.Settings.Snapshot(), "liveops"));
                lock (gate)
                {
                    if (settingsKey == null || !string.Equals(settingsKey, key, StringComparison.Ordinal))
                    {
                        settings = LiveOpsSettings.From(context.Settings, log);
                        settingsKey = key;
                    }
                    return settings;
                }
            }
        }

        public LiveOpsCatalog Catalog
        {
            get
            {
                string text;
                if (!context.Content.TryReadContentText(LiveOpsCatalog.ContentName, out text)) text = null;
                lock (gate)
                {
                    if (!catalogParsed || !string.Equals(text, catalogText, StringComparison.Ordinal))
                    {
                        catalogParsed = true;
                        catalogText = text;
                        catalog = LiveOpsCatalog.Parse(text, log);
                        if (!catalog.IsEmpty)
                            log.Info("liveops: " + catalog.Divisions.Count + " divisions, " + catalog.Objectives.Count + " objectives, "
                                     + catalog.Modifiers.Count + " modifiers (content " + catalog.Hash + ")");
                    }
                    return catalog;
                }
            }
        }

        /// <summary>The modifier a season runs with (null: none).</summary>
        public LiveOpsModifier ModifierFor(LiveOpsWindow season)
        {
            return ModifierFor(season, Settings, Catalog);
        }

        private LiveOpsModifier ModifierFor(LiveOpsWindow season, LiveOpsSettings s, LiveOpsCatalog c)
        {
            if (string.Equals(s.Modifiers, "off", StringComparison.OrdinalIgnoreCase)) return null;
            string id;
            if (string.Equals(s.Modifiers, "rotate", StringComparison.OrdinalIgnoreCase))
            {
                if (c.Rotation.Count == 0) return null;
                long n = LiveOpsSchedule.Ordinal(season) % c.Rotation.Count;
                id = c.Rotation[(int)(n < 0 ? n + c.Rotation.Count : n)];
            }
            else
            {
                id = s.Modifiers;
            }
            LiveOpsModifier m = c.Modifier(id);
            if (m == null)
            {
                lock (gate)
                    if (warned.Add("modifier:" + id)) log.Warn("liveops: unknown modifier '" + id + "' (liveops.modifiers); none applied");
            }
            return m;
        }

        // ------------------------------------------------------------------------------------------ CRM layer

        /// <summary>The ids, expiry and cells for a CRM document served at <paramref name="utc"/>.</summary>
        public LiveOpsSnapshot Snapshot(DateTime utc)
        {
            LiveOpsSettings s = Settings;
            LiveOpsCatalog c = Catalog;
            var snap = new LiveOpsSnapshot { Settings = s };
            // A document served a moment before a boundary must not expire before the TS reads it: the TS refetches
            // CRM on every frame while the expiry is in the past.
            DateTime at = utc.AddSeconds(2);
            snap.Season = LiveOpsSchedule.SeasonAt(s, at);
            snap.Day = LiveOpsSchedule.DayAt(s, at);
            if (!s.Enabled || c.IsEmpty)
            {
                snap.Key = "off|" + s.Fingerprint + "|" + c.Hash;
                return snap;
            }
            var next = new LiveOpsWindow(snap.Season.End, snap.Season.Days);
            DateTime expiry = snap.Season.End;
            bool advertise = false;

            if (s.DivisionSeasons)
            {
                foreach (int d in c.Divisions) snap.EventIds.Add(LiveOpsSchedule.DivisionEventId(snap.Season, d));
                // The next season too: an event already in the TS's list starts on time (OnEventsStarted) even if
                // the CRM refresh at the boundary is late.
                foreach (int d in c.Divisions) snap.EventIds.Add(LiveOpsSchedule.DivisionEventId(next, d));
            }
            if (s.DailyQuests)
            {
                snap.EventIds.Add(LiveOpsSchedule.DailyEventId(snap.Day));
                snap.EventIds.Add(LiveOpsSchedule.DailyEventId(new LiveOpsWindow(snap.Day.End, 1)));
                if (snap.Day.End < expiry) expiry = snap.Day.End;
            }
            if (s.WeeklyEvents)
            {
                snap.EventIds.Add(LiveOpsSchedule.WeeklyEventId(snap.Season));
                // The next event is announced during the last day of a season (shown as "coming soon"); the TS adds
                // every listed achievement event to the profile, so it is not listed earlier.
                DateTime announce = snap.Season.End.AddDays(-1);
                if (snap.Season.Days > 1 && at >= announce)
                {
                    advertise = true;
                    snap.EventIds.Add(LiveOpsSchedule.WeeklyEventId(next));
                }
                else if (snap.Season.Days > 1 && announce < expiry)
                {
                    expiry = announce;
                }
            }
            snap.Expiry = expiry;
            snap.Modifier = ModifierFor(snap.Season, s, c);
            if (snap.Modifier != null) snap.Cells.AddRange(snap.Modifier.Cells);
            if (s.SeasonRewardMultiplier != 1.0 && c.SeasonRewards != null)
            {
                snap.Cells.Add(new LiveOpsCells
                {
                    Database = c.SeasonRewards.Database, Table = c.SeasonRewards.Table, Index = c.SeasonRewards.Index,
                    Ids = c.SeasonRewards.Ids, Fields = c.SeasonRewards.Fields, Factor = s.SeasonRewardMultiplier,
                });
            }
            snap.Key = string.Join("|", new[]
            {
                "on", s.Fingerprint, c.Hash, snap.Season.Key, snap.Day.Key, advertise ? "a" : "-",
                snap.Modifier == null ? "-" : snap.Modifier.Id,
            });
            return snap;
        }

        // ------------------------------------------------------------------------------------------ events

        public Dictionary<string, object> FindEvent(string eventId, string language, DateTime utcNow)
        {
            LiveOpsSettings s = Settings;
            if (!s.Enabled) return null;
            LiveOpsCatalog c = Catalog;
            LiveOpsWindow window;
            int division;
            if (LiveOpsSchedule.TryParseDivisionEvent(eventId, out window, out division))
                return s.DivisionSeasons && c.Divisions.Contains(division) ? LiveOpsEvents.DivisionSeason(c, window, division) : null;
            window = LiveOpsSchedule.TryParseDaily(eventId);
            if (window != null) return s.DailyQuests ? LiveOpsEvents.DailyQuest(c, window) : null;
            window = LiveOpsSchedule.TryParseWeekly(eventId);
            if (window != null) return s.WeeklyEvents ? LiveOpsEvents.WeeklyEvent(c, window, ModifierFor(window, s, c)) : null;
            return null;
        }

        // ------------------------------------------------------------------------------------------ season boards

        /// <summary>The rival tuning of a division board (<see cref="LiveOpsSchedule.BoardName"/>, also "_QA"); false for
        /// any other board.</summary>
        public bool TryGetSeasonBoard(string boardName, out SeasonBoardInfo info)
        {
            info = null;
            LiveOpsWindow season;
            int division;
            if (!LiveOpsSchedule.TryParseBoard(boardName, out season, out division)) return false;
            LiveOpsSettings s = Settings;
            LiveOpsCatalog c = Catalog;
            DivisionSpec spec = Tables.Division(division);
            LiveOpsModifier modifier = ModifierFor(season, s, c);
            info = new SeasonBoardInfo
            {
                Season = season,
                Division = division,
                PromotionZone = spec != null ? spec.PromotionZone : c.FallbackPromotionZone,
                DemotionZone = spec != null ? spec.DemotionZone : c.FallbackDemotionZone,
                TrophiesWin = spec != null ? spec.TrophiesWin : 0,
                TrophiesLose = spec != null ? spec.TrophiesLose : 0,
                MatchesPerDay = c.PromotionMatches(division),
                TopFactor = c.TopFactor,
                SafeFraction = c.SafeFraction,
                MasterTopRank = c.MasterTopRank,
                DefaultLevel = c.DefaultLevel,
                Difficulty = s.Difficulty,
                RivalFactor = modifier != null ? modifier.RivalFactor : 1.0,
                FromDataPack = spec != null,
                LevelBonus = level => Tables.LevelBonus(level),
            };
            return true;
        }

        // ------------------------------------------------------------------------------------------ diagnostics

        /// <summary><c>GET _mcvs/liveops</c>: the schedule now (no player data).</summary>
        public Dictionary<string, object> Describe(DateTime utc)
        {
            LiveOpsSnapshot snap = Snapshot(utc);
            LiveOpsCatalog c = Catalog;
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["now"] = TimeFormat.Iso8601(utc);
            o["enabled"] = snap.Settings.Enabled && !c.IsEmpty;
            o["season"] = Window(snap.Season, LiveOpsSchedule.SeasonId(snap.Season));
            o["day"] = Window(snap.Day, LiveOpsSchedule.DailyEventId(snap.Day));
            o["modifier"] = snap.Modifier == null ? null : snap.Modifier.Id;
            o["crmExpiry"] = snap.Expiry.HasValue ? (object)TimeFormat.Iso8601(snap.Expiry.Value) : null;
            var ids = new List<object>();
            foreach (string id in snap.EventIds) ids.Add(id);
            o["eventIds"] = ids;
            var quests = new List<object>();
            Dictionary<string, object> daily = snap.Settings.DailyQuests ? LiveOpsEvents.DailyQuest(c, snap.Day) : null;
            if (daily != null) quests.Add(Summary(daily));
            Dictionary<string, object> weekly = snap.Settings.WeeklyEvents ? LiveOpsEvents.WeeklyEvent(c, snap.Season, snap.Modifier) : null;
            if (weekly != null) quests.Add(Summary(weekly));
            o["achievementEvents"] = quests;
            o["divisionTablesFromDataPack"] = Tables.IsLoaded;
            o["settings"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["seasonStart"] = TimeFormat.Iso8601(snap.Settings.SeasonAnchor),
                ["seasonDays"] = (long)snap.Settings.SeasonDays,
                ["dailyResetHour"] = (long)snap.Settings.DailyResetHour,
                ["modifiers"] = snap.Settings.Modifiers,
                ["difficulty"] = snap.Settings.Difficulty,
                ["seasonRewardMultiplier"] = snap.Settings.SeasonRewardMultiplier,
            };
            return o;
        }

        private static Dictionary<string, object> Window(LiveOpsWindow w, string id)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["start"] = TimeFormat.Iso8601(w.Start),
                ["end"] = TimeFormat.Iso8601(w.End),
            };
        }

        private static Dictionary<string, object> Summary(Dictionary<string, object> e)
        {
            var s = new Dictionary<string, object>(StringComparer.Ordinal);
            s["id"] = e["id"];
            s["start"] = e["start_date"];
            s["end"] = e["end_date"];
            s["_version"] = e["_version"];
            object parsed;
            var objectives = new List<object>();
            if (MiniJson.TryParse(JsonValue.AsString(e["_template"]), out parsed))
            {
                var milestones = JsonValue.AsArray(JsonValue.Get(JsonValue.GetObject(parsed as Dictionary<string, object>, "milestones"), "value"));
                foreach (object a in milestones ?? new List<object>())
                {
                    var achievement = a as Dictionary<string, object>;
                    var goals = new List<object>();
                    foreach (object m in JsonValue.GetArray(achievement, "milestones") ?? new List<object>())
                        goals.Add(JsonValue.Get(m as Dictionary<string, object>, "milestone_value"));
                    objectives.Add(JsonValue.GetString(achievement, "primary_tracker") + " " + MiniJson.Serialize(goals));
                }
            }
            s["objectives"] = objectives;
            return s;
        }

        /// <summary>For logs: "2026-10-19 → 2026-10-26".</summary>
        public static string Describe(LiveOpsWindow w)
        {
            return w.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " -> " + w.End.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
