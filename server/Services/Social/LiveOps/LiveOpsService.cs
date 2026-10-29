using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm.Collector;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>What the CRM document needs from live ops at one moment.</summary>
    public sealed class LiveOpsSnapshot
    {
        public LiveOpsSettings Settings;
        public LiveOpsWindow Season;
        public LiveOpsWindow Day;
        public LiveOpsModifier Modifier;

        /// <summary>The Special Event window now and its event id (null: no Special Event is listed).</summary>
        public ArenaWindow ArenaWindow;
        public string ArenaEventId;

        /// <summary>The next window's event id (listed with the current one: the chain never has a gap).</summary>
        public string NextArenaEventId;

        /// <summary>Event ids for <c>groupware.events.participation</c> (empty when live ops are off).</summary>
        public List<string> EventIds = new List<string>();

        /// <summary>The next schedule change (strictly in the future); the CRM <c>expiry</c>, so the TS refetches CRM and
        /// events exactly then (EN/CRMComponent.cs OnConstantUpdate → OnCrmExpired → RefreshCrmConfig). Null when off.</summary>
        public DateTime? Expiry;

        /// <summary><c>_OverrideTables</c> MUL cells: the season's modifier and the season-reward multiplier.</summary>
        public List<LiveOpsCells> Cells = new List<LiveOpsCells>();

        /// <summary>Live ops serve events (<c>liveops.enabled</c> and a non-empty catalogue): the "live ops on" the CRM
        /// builder passes to <c>CollectorRuntime.Status</c>.</summary>
        public bool On;

        /// <summary>
        /// liveops.json <c>collector.since</c> (null: no valid collector block), whether or not live ops are on: the CRM
        /// builder passes it to <c>CollectorRuntime.Status</c>, so the layer starts exactly where the crates do (D5) and
        /// the guard at <c>CollectorRuntime.GuardSince(since)</c> (a day before <c>min(since, CollectorGuard.ShippedSince)</c>).
        /// </summary>
        public DateTime? CollectorSince;

        /// <summary>Changes whenever <see cref="EventIds"/>, <see cref="Expiry"/>, <see cref="Cells"/> or the events' content
        /// do (the settings fingerprint with the collector inputs, the catalogue hash, the windows).</summary>
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
        private bool catalogReadFailing;
        private LiveOpsCatalog catalog = new LiveOpsCatalog();
        private string settingsKey;
        private LiveOpsSettings settings = LiveOpsSettings.Defaults();
        private bool collectorFailing;
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The mode of the last Special Event handed to the TS in this process (the one in progress when it was served),
        /// or null: none yet. Once the TS has one, the client holds it until the game restarts (it never clears its current
        /// Arena event), so from then on the chain must not break: switching Special Events off keeps them running,
        /// pinned to this mode, until the next start (docs/LIVEOPS.md §8a.3). In memory on purpose: the client's state ends
        /// with the process, and so does this.
        /// </summary>
        private LiveOpsArenaMode servedArenaMode;

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

        /// <summary>The settings, parsed again when the <c>liveops</c> subtree or the collector's
        /// <see cref="CollectorRuntime.InputsKey"/> changed (the fingerprint carries it).</summary>
        public LiveOpsSettings Settings
        {
            get
            {
                string collectorKey = CollectorKey();
                string key = MiniJson.Serialize(JsonValue.Get(context.Settings.Snapshot(), "liveops")) + "|" + collectorKey;
                lock (gate)
                {
                    if (settingsKey == null || !string.Equals(settingsKey, key, StringComparison.Ordinal))
                    {
                        settings = LiveOpsSettings.From(context.Settings, log, collectorKey);
                        settingsKey = key;
                    }
                    return settings;
                }
            }
        }

        /// <summary>The server's collector runtime (the one predicate for the Cosmetic crate, economy-collector.md §2.12).</summary>
        public CollectorRuntime Collector
        {
            get { return CollectorRuntime.For(context); }
        }

        /// <summary><see cref="CollectorRuntime.InputsKey"/>; never throws (a failure is logged once per failing streak).</summary>
        private string CollectorKey()
        {
            try
            {
                string key = Collector.InputsKey;
                lock (gate) collectorFailing = false;
                return key;
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (!collectorFailing)
                    {
                        collectorFailing = true;
                        log.Error("liveops: the collector's inputs cannot be read: no Cosmetic crate in the events until they can", ex);
                    }
                }
                return "collector:unavailable";
            }
        }

        /// <summary>
        /// liveops.json, parsed again when its text changed. A read that throws (the &lt;data&gt;/content override changed
        /// between the provider's File.Exists and its read: an edit, a delete, a sharing violation) never reaches the
        /// request: the last read is kept (before any, the empty catalogue, read again on the next call), logged once per
        /// failing streak.
        /// </summary>
        public LiveOpsCatalog Catalog
        {
            get
            {
                string text;
                Exception failure = null;
                try
                {
                    if (!context.Content.TryReadContentText(LiveOpsCatalog.ContentName, out text)) text = null;
                }
                catch (Exception ex)
                {
                    text = null;
                    failure = ex;
                }
                lock (gate)
                {
                    if (failure != null)
                    {
                        if (!catalogReadFailing)
                        {
                            catalogReadFailing = true;
                            string why = LiveOpsCatalog.ContentName + " could not be read (" + failure.GetType().Name + ": " + failure.Message + ")";
                            if (catalogParsed) log.Warn("liveops: " + why + "; keeping the last read until it can be read again");
                            else log.Error("liveops: " + why + "; live ops are off until it can be read");
                        }
                        // Keep the last read: a transient failure must not switch live ops off and on (or replace events).
                        return catalog;
                    }
                    catalogReadFailing = false;
                    if (!catalogParsed || !string.Equals(text, catalogText, StringComparison.Ordinal))
                    {
                        catalogParsed = true;
                        catalogText = text;
                        catalog = LiveOpsCatalog.Parse(text, log);
                        if (!catalog.IsEmpty)
                            log.Info("liveops: " + catalog.Divisions.Count + " divisions, " + catalog.Objectives.Count + " objectives, "
                                     + catalog.Modifiers.Count + " modifiers, "
                                     + (catalog.Collector != null
                                            ? "Cosmetic crates from " + TimeFormat.Iso8601(catalog.Collector.Since)
                                            : "no Cosmetic crates (" + catalog.CollectorProblem + ")")
                                     + " (content " + catalog.Hash + ")");
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
            if (c.Collector != null) snap.CollectorSince = c.Collector.Since;
            // A document served a moment before a boundary must not expire before the TS reads it: the TS refetches
            // CRM on every frame while the expiry is in the past.
            DateTime at = utc.AddSeconds(2);
            snap.Season = LiveOpsSchedule.SeasonAt(s, at);
            snap.Day = LiveOpsSchedule.DayAt(s, at);
            if (!s.Enabled || c.IsEmpty)
            {
                // Only a Special Event chain the TS has seen survives this (ArenaPlan): the client keeps it.
                AddArena(snap, s, c, at);
                snap.Key = "off|" + s.Fingerprint + "|" + c.Hash + "|" + ArenaKey(snap);
                return snap;
            }
            snap.On = true;
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
            AddArena(snap, s, c, at);
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
                snap.Modifier == null ? "-" : snap.Modifier.Id, ArenaKey(snap),
            });
            return snap;
        }

        // ------------------------------------------------------------------------------------------ Special Events

        /// <summary>What the Special Event chain runs now: the modes, a pinned mode (or null: the rotation) and the map.</summary>
        private sealed class ArenaPlan
        {
            public LiveOpsArena Arena;
            public LiveOpsArenaMode Pinned;
            public string Map;

            /// <summary>Switched off, but kept running because the TS has seen a Special Event (<see cref="servedArenaMode"/>).</summary>
            public bool Kept;

            public LiveOpsArenaMode ModeOf(ArenaWindow window)
            {
                return Pinned ?? (Arena == null ? null : Arena.Rotating(window));
            }

            public LiveOpsArenaMode Lookup(string modeId, LiveOpsArenaMode served)
            {
                LiveOpsArenaMode m = Arena == null ? null : Arena.Mode(modeId);
                if (m == null && Pinned != null && Pinned.Id == modeId) m = Pinned;
                if (m == null && served != null && served.Id == modeId) m = served;
                return m;
            }

            public string MapFor(LiveOpsArenaMode mode)
            {
                return string.Equals(Map, "Random", StringComparison.Ordinal) ? mode.Map : Map;
            }
        }

        private static bool Is(string value, string word)
        {
            return string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The Special Event plan, or null when none runs. On: live ops on (with a catalogue), <c>liveops.arenaEvents</c>,
        /// <c>liveops.arenaRotation</c> not "off" and a usable <c>arena</c> block. Otherwise null, unless the TS was handed a
        /// Special Event in this process: then the chain keeps running, pinned to that mode, until the game restarts (the
        /// client keeps its current Arena event, and with no event on the TS the GS throws on the missing
        /// <c>_arenaParams</c>; special-event.md §5.3).
        /// </summary>
        private ArenaPlan PlanFor(LiveOpsSettings s, LiveOpsCatalog c)
        {
            LiveOpsArenaMode served;
            lock (gate) served = servedArenaMode;
            bool on = s.Enabled && !c.IsEmpty && s.ArenaEvents && !Is(s.ArenaRotation, "off") && c.Arena != null;
            ArenaPlan plan;
            if (on)
            {
                plan = new ArenaPlan { Arena = c.Arena };
                if (!Is(s.ArenaRotation, "rotate"))
                {
                    plan.Pinned = c.Arena.Mode(s.ArenaRotation);
                    if (plan.Pinned == null) WarnOnce("arena:" + s.ArenaRotation, "liveops: unknown Special Event mode '" + s.ArenaRotation + "' (liveops.arenaRotation); rotating");
                }
            }
            else if (served != null)
            {
                plan = new ArenaPlan { Arena = c.Arena, Pinned = served, Kept = true };
                WarnOnce("arena-kept:" + served.Id, "liveops: Special Events are switched off, but the game already has one: they keep running ("
                                                    + served.Id + ") until the game restarts");
            }
            else
            {
                return null;
            }
            plan.Map = "Random";
            if (!Is(s.ArenaMap, "Random"))
            {
                List<string> maps = plan.Arena != null && plan.Arena.Maps.Count > 0 ? plan.Arena.Maps : new List<string>(TleTemplateValidator.ArenaMaps);
                if (maps.Contains(s.ArenaMap)) plan.Map = s.ArenaMap;
                else WarnOnce("arena-map:" + s.ArenaMap, "liveops: '" + s.ArenaMap + "' is not a playable map (liveops.arenaMap); each mode's own map is used");
            }
            return plan;
        }

        private void WarnOnce(string key, string message)
        {
            lock (gate)
                if (warned.Add(key)) log.Warn(message);
        }

        private Dictionary<string, object> ArenaEventFor(LiveOpsCatalog c, ArenaPlan plan, ArenaWindow window, LiveOpsArenaMode mode)
        {
            return LiveOpsEvents.ArenaMode(c, plan.Arena, window, mode, plan.MapFor(mode));
        }

        /// <summary>
        /// Lists the Special Event of the window at <paramref name="at"/> and of the next one (participation: the client
        /// unlocks only events it can take part in), and moves the expiry to the window's end. Both events go through
        /// <see cref="TleTemplateValidator"/> and the chain check first: an event the TS would drop or choke on is never
        /// listed (the error is logged once).
        /// </summary>
        private void AddArena(LiveOpsSnapshot snap, LiveOpsSettings s, LiveOpsCatalog c, DateTime at)
        {
            ArenaPlan plan = PlanFor(s, c);
            if (plan == null) return;
            ArenaWindow current = LiveOpsSchedule.ArenaAt(s, at);
            ArenaWindow next = current.Next;
            LiveOpsArenaMode currentMode = plan.ModeOf(current), nextMode = plan.ModeOf(next);
            if (currentMode == null || nextMode == null) return;
            var events = new List<Dictionary<string, object>> { ArenaEventFor(c, plan, current, currentMode), ArenaEventFor(c, plan, next, nextMode) };
            string problem = null;
            foreach (Dictionary<string, object> e in events)
            {
                problem = TleTemplateValidator.Problem(e);
                if (problem != null) break;
            }
            if (problem == null) problem = TleTemplateValidator.ArenaChainProblem(events, at);
            if (problem != null)
            {
                lock (gate)
                    if (warned.Add("arena-problem:" + problem)) log.Error("liveops: no Special Event listed: " + problem);
                return;
            }
            snap.ArenaWindow = current;
            snap.ArenaEventId = (string)events[0]["id"];
            snap.NextArenaEventId = (string)events[1]["id"];
            snap.EventIds.Add(snap.ArenaEventId);
            snap.EventIds.Add(snap.NextArenaEventId);
            if (!snap.Expiry.HasValue || current.End < snap.Expiry.Value) snap.Expiry = current.End;
        }

        private static string ArenaKey(LiveOpsSnapshot snap)
        {
            return snap.ArenaEventId == null ? "arena:-" : "arena:" + snap.ArenaEventId + "," + snap.NextArenaEventId;
        }

        /// <summary>A Special Event the TS asked for: any window and mode while Special Events run (also a kept chain).
        /// Handing one over sets <see cref="servedArenaMode"/> (the mode of the window in progress).</summary>
        private Dictionary<string, object> ArenaEvent(string eventId, ArenaWindow window, string modeId, LiveOpsSettings s, LiveOpsCatalog c, DateTime utcNow)
        {
            ArenaPlan plan = PlanFor(s, c);
            if (plan == null) return null;
            LiveOpsArenaMode served;
            lock (gate) served = servedArenaMode;
            LiveOpsArenaMode mode = plan.Lookup(modeId, served);
            if (mode == null) return null;
            Dictionary<string, object> e = ArenaEventFor(c, plan, window, mode);
            string problem = TleTemplateValidator.Problem(e);
            if (problem != null)
            {
                log.Error("liveops: Special Event " + eventId + " not served: " + problem);
                return null;
            }
            lock (gate)
            {
                if (servedArenaMode == null || window.Contains(utcNow))
                {
                    if (servedArenaMode == null) log.Info("liveops: first Special Event handed to the TS (" + eventId + "); the chain now runs until the game restarts");
                    servedArenaMode = mode;
                }
            }
            return e;
        }

        // ------------------------------------------------------------------------------------------ events

        public Dictionary<string, object> FindEvent(string eventId, string language, DateTime utcNow)
        {
            LiveOpsSettings s = Settings;
            ArenaWindow arenaWindow;
            string arenaMode;
            // Before the live-ops switch: a Special Event chain the TS has seen keeps running (PlanFor).
            if (LiveOpsSchedule.TryParseArena(eventId, out arenaWindow, out arenaMode))
                return ArenaEvent(eventId, arenaWindow, arenaMode, s, Catalog, utcNow);
            if (!s.Enabled) return null;
            LiveOpsCatalog c = Catalog;
            LiveOpsWindow window;
            int division;
            if (LiveOpsSchedule.TryParseDivisionEvent(eventId, out window, out division))
                return s.DivisionSeasons && c.Divisions.Contains(division) ? LiveOpsEvents.DivisionSeason(c, window, division) : null;
            // The Cosmetic crate (economy-collector.md §3.2): only in windows CollectorRuntime.CratesInWindow allows; the
            // events of every other window are exactly as without the collector.
            window = LiveOpsSchedule.TryParseDaily(eventId);
            if (window != null) return s.DailyQuests ? Served(eventId, window, LiveOpsEvents.DailyQuest(c, window, Collector), utcNow) : null;
            window = LiveOpsSchedule.TryParseWeekly(eventId);
            if (window != null)
                return s.WeeklyEvents ? Served(eventId, window, LiveOpsEvents.WeeklyEvent(c, window, ModifierFor(window, s, c), Collector), utcNow) : null;
            return null;
        }

        /// <summary>
        /// An event handed to the TS. When it carries the Cosmetic crate the collector's latch is set
        /// (<see cref="CollectorRuntime.NoteCratesServed"/>): the CRM document keeps the guard from then on, whatever later
        /// happens to collector.since or the clock (economy-collector.md §2.12, D9).
        /// </summary>
        private Dictionary<string, object> Served(string eventId, LiveOpsWindow window, Dictionary<string, object> e, DateTime utcNow)
        {
            string template = e == null ? null : e["_template"] as string;
            if (template != null && template.IndexOf("\"" + CollectorGuard.CrateId + "\"", StringComparison.Ordinal) >= 0)
            {
                try
                {
                    Collector.NoteCratesServed(eventId, window.Start, utcNow);
                }
                catch (Exception ex)
                {
                    log.Error("liveops: the collector's latch could not be set for " + eventId, ex);
                }
            }
            return e;
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
            CollectorRuntime runtime = Collector;
            var quests = new List<object>();
            Dictionary<string, object> daily = snap.Settings.DailyQuests ? LiveOpsEvents.DailyQuest(c, snap.Day, runtime) : null;
            if (daily != null) quests.Add(Summary(daily));
            Dictionary<string, object> weekly = snap.Settings.WeeklyEvents ? LiveOpsEvents.WeeklyEvent(c, snap.Season, snap.Modifier, runtime) : null;
            if (weekly != null) quests.Add(Summary(weekly));
            o["achievementEvents"] = quests;
            o["collector"] = DescribeCollector(snap, c, runtime, utc);
            o["divisionTablesFromDataPack"] = Tables.IsLoaded;
            o["arena"] = DescribeArena(snap, c);
            o["settings"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["seasonStart"] = TimeFormat.Iso8601(snap.Settings.SeasonAnchor),
                ["seasonDays"] = (long)snap.Settings.SeasonDays,
                ["dailyResetHour"] = (long)snap.Settings.DailyResetHour,
                ["modifiers"] = snap.Settings.Modifiers,
                ["difficulty"] = snap.Settings.Difficulty,
                ["seasonRewardMultiplier"] = snap.Settings.SeasonRewardMultiplier,
                ["arenaEvents"] = snap.Settings.ArenaEvents,
                ["arenaRotation"] = snap.Settings.ArenaRotation,
                ["arenaHours"] = (long)snap.Settings.ArenaHours,
                ["arenaMap"] = snap.Settings.ArenaMap,
            };
            return o;
        }

        /// <summary>
        /// <c>arena: {listed, kept, current, next, upcoming, modes, maps, problem}</c>: the Special Events a CRM document served
        /// now lists (current and next with mode, loc ids, match type, map and window), whether they only run because the TS
        /// has seen one (switched off: until the game restarts), the modes of the seven windows after the next, the
        /// catalogue's modes ("*" marks the ones outside the rotation) and maps, and why the catalogue has no or fewer modes.
        /// </summary>
        private Dictionary<string, object> DescribeArena(LiveOpsSnapshot snap, LiveOpsCatalog c)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            ArenaPlan plan = PlanFor(snap.Settings, c);
            o["listed"] = snap.ArenaEventId != null;
            o["kept"] = plan != null && plan.Kept;
            o["current"] = null;
            o["next"] = null;
            var upcoming = new List<object>();
            if (plan != null && snap.ArenaWindow != null)
            {
                o["current"] = DescribeArenaEvent(c, plan, snap.ArenaWindow);
                o["next"] = DescribeArenaEvent(c, plan, snap.ArenaWindow.Next);
                ArenaWindow w = snap.ArenaWindow.Next;
                for (int i = 0; i < 7; i++)
                {
                    w = w.Next;
                    LiveOpsArenaMode m = plan.ModeOf(w);
                    upcoming.Add(TimeFormat.Iso8601(w.Start) + " " + (m == null ? "-" : m.Id));
                }
            }
            o["upcoming"] = upcoming;
            var modes = new List<object>();
            var maps = new List<object>();
            if (c.Arena != null)
            {
                foreach (LiveOpsArenaMode m in c.Arena.Modes) modes.Add(m.Rotate ? m.Id : m.Id + "*");
                foreach (string map in c.Arena.Maps) maps.Add(map);
            }
            o["modes"] = modes;
            o["maps"] = maps;
            o["problem"] = c.ArenaProblem;
            return o;
        }

        private static Dictionary<string, object> DescribeArenaEvent(LiveOpsCatalog c, ArenaPlan plan, ArenaWindow w)
        {
            LiveOpsArenaMode m = plan.ModeOf(w);
            if (m == null) return null;
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = LiveOpsSchedule.ArenaEventId(w, m.Id),
                ["mode"] = m.Id,
                ["name"] = m.EventName,
                ["rule"] = m.EventRule,
                ["matchType"] = m.MatchType,
                ["parameterSets"] = new List<object>(m.ParameterSets.ToArray()),
                ["map"] = plan.MapFor(m),
                ["agents"] = (long)m.Agents.Count,
                ["creditsVictory"] = m.CreditsVictory,
                ["versusCoinsVictory"] = m.CoinsVictory,
                ["start"] = TimeFormat.Iso8601(w.Start),
                ["end"] = TimeFormat.Iso8601(w.End),
            };
        }

        /// <summary>
        /// <c>collector: {phase, reason, since, activeFor, block, cratesServed}</c> (economy-collector.md §3.2): the phase a
        /// CRM document served now has (<see cref="CollectorRuntime.Status"/>), <c>since</c> from the catalogue, the listed
        /// daily and weekly event ids whose window gets the Cosmetic crate, why the catalogue has no collector block (null
        /// when it has one) and when live ops first served the crate (the latch, null: never).
        /// </summary>
        private Dictionary<string, object> DescribeCollector(LiveOpsSnapshot snap, LiveOpsCatalog c, CollectorRuntime runtime, DateTime utc)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                CollectorStatus status = runtime.Status(utc, snap.On, snap.CollectorSince);
                o["phase"] = status.Phase.ToString();
                o["reason"] = status.Reason;
            }
            catch (Exception ex)
            {
                log.Error("liveops: the collector status cannot be read", ex);
                o["phase"] = null;
                o["reason"] = ex.GetType().Name + ": " + ex.Message;
            }
            o["since"] = snap.CollectorSince.HasValue ? (object)TimeFormat.Iso8601(snap.CollectorSince.Value) : null;
            var active = new List<object>();
            foreach (string id in snap.EventIds)
            {
                LiveOpsWindow window = LiveOpsSchedule.TryParseDaily(id) ?? LiveOpsSchedule.TryParseWeekly(id);
                if (window != null && LiveOpsEvents.CratesIn(c, window, runtime)) active.Add(id);
            }
            o["activeFor"] = active;
            o["block"] = c.CollectorProblem;
            try
            {
                DateTime? served = runtime.CratesServedAt;
                o["cratesServed"] = served.HasValue ? (object)TimeFormat.Iso8601(served.Value) : null;
            }
            catch (Exception ex)
            {
                o["cratesServed"] = "unavailable (" + ex.Message + ")";
            }
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
