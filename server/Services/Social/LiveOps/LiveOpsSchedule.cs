using System;
using System.Globalization;

namespace McvsOffline.Server.Services.Social.LiveOps
{
    /// <summary>A time window of the schedule: a season (and its weekly event) or a daily-quest day.</summary>
    public sealed class LiveOpsWindow
    {
        public LiveOpsWindow(DateTime start, int days)
        {
            Start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
            Days = days;
            End = Start.AddDays(days);
        }

        public DateTime Start { get; private set; }

        public DateTime End { get; private set; }

        public int Days { get; private set; }

        /// <summary>"yyyyMMddHH" of the start, plus "x&lt;days&gt;" for multi-day windows: the window is
        /// recoverable from its key alone, whatever the settings are later. A one-day season (liveops.seasonDays 1)
        /// has the bare form too; <see cref="Parse"/> reads it back as such.</summary>
        public string Key
        {
            get
            {
                string start = Start.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
                return Days == 1 ? start : start + "x" + Days.ToString(CultureInfo.InvariantCulture);
            }
        }

        public bool Contains(DateTime utc)
        {
            return Start <= utc && utc < End;
        }

        /// <summary>0..1: how much of the window has passed at <paramref name="utc"/>.</summary>
        public double Progress(DateTime utc)
        {
            if (utc <= Start) return 0;
            if (utc >= End) return 1;
            return (utc - Start).TotalSeconds / (End - Start).TotalSeconds;
        }

        /// <summary>Parses a <see cref="Key"/>; null when it is not one. <paramref name="multiDay"/> (season-type ids)
        /// accepts every season length, 1 to 28 days ("yyyyMMddHH" for one day, "yyyyMMddHHx&lt;days&gt;" otherwise); without
        /// it only a one-day key. Only the exact form <see cref="Key"/> writes is accepted ("x1" or "x07" are not), so
        /// an id rebuilt from the parsed window is the id that was asked for.</summary>
        public static LiveOpsWindow Parse(string key, bool multiDay)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 10) return null;
            int year, month, day, hour, days = 1;
            if (!Int(key, 0, 4, out year) || !Int(key, 4, 2, out month) || !Int(key, 6, 2, out day) || !Int(key, 8, 2, out hour)) return null;
            if (key.Length != 10)
            {
                if (!multiDay || key.Length < 12 || key[10] != 'x' || !Int(key, 11, key.Length - 11, out days)) return null;
                if (days < 2 || days > 28 || key[11] == '0') return null;
            }
            if (year < 2000 || year > 2200 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23) return null;
            return new LiveOpsWindow(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc), days);
        }

        private static bool Int(string text, int start, int length, out int value)
        {
            value = 0;
            if (length <= 0 || start + length > text.Length) return false;
            for (int i = start; i < start + length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
                if (value > 100000) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A Special Event (Arena) window: <c>arenaHours</c> long, on a grid of whole windows from 2024-01-01
    /// <c>dailyResetHour</c>:00 UTC (<see cref="LiveOpsSchedule.ArenaAt"/>). Its <see cref="Key"/> carries start and length,
    /// so an event id names its window whatever the settings are later.
    /// </summary>
    public sealed class ArenaWindow
    {
        public ArenaWindow(DateTime start, int hours)
        {
            Start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
            Hours = hours;
            End = new DateTime(Start.Ticks + hours * LiveOpsSchedule.TicksPerHour, DateTimeKind.Utc);
        }

        public DateTime Start { get; private set; }

        public DateTime End { get; private set; }

        public int Hours { get; private set; }

        /// <summary>The window that starts when this one ends (same length).</summary>
        public ArenaWindow Next
        {
            get { return new ArenaWindow(End, Hours); }
        }

        /// <summary>"yyyyMMddHHx&lt;hours&gt;h", e.g. "2026102900x24h".</summary>
        public string Key
        {
            get { return Start.ToString("yyyyMMddHH", CultureInfo.InvariantCulture) + "x" + Hours.ToString(CultureInfo.InvariantCulture) + "h"; }
        }

        public bool Contains(DateTime utc)
        {
            return Start <= utc && utc < End;
        }

        /// <summary>Whole windows of this length between 2024-01-01 00:00 UTC and the start (the rotation index).</summary>
        public long Ordinal
        {
            get
            {
                DateTime anchor = LiveOpsSettings.DefaultAnchor;
                long length = Hours * LiveOpsSchedule.TicksPerHour;
                long offset = Start.Ticks - anchor.Ticks;
                return offset >= 0 ? offset / length : -((-offset + length - 1) / length);
            }
        }

        /// <summary>Parses a <see cref="Key"/> (only the exact form it writes: 1..168 hours, no leading zero); null otherwise.</summary>
        public static ArenaWindow Parse(string key)
        {
            if (key == null || key.Length < 13 || key[10] != 'x' || key[key.Length - 1] != 'h') return null;
            LiveOpsWindow start = LiveOpsWindow.Parse(key.Substring(0, 10), false);
            if (start == null) return null;
            string hoursText = key.Substring(11, key.Length - 12);
            if (hoursText.Length == 0 || hoursText.Length > 3 || hoursText[0] == '0') return null;
            int hours = 0;
            foreach (char c in hoursText)
            {
                if (c < '0' || c > '9') return null;
                hours = hours * 10 + (c - '0');
            }
            if (hours < 1 || hours > 168) return null;
            return new ArenaWindow(start.Start, hours);
        }
    }

    /// <summary>
    /// The deterministic calendar: everything is a function of the clock and the settings, nothing is stored.
    /// <list type="bullet">
    /// <item>Season k starts at <c>seasonStart + k × seasonDays</c> (default: every Monday 00:00 UTC) and lasts
    /// seasonDays; its weekly achievement event spans the same window.</item>
    /// <item>A quest day starts at <c>dailyResetHour</c> UTC and lasts 24 h.</item>
    /// <item>The modifier of a season is <c>rotation[ordinal mod n]</c>, ordinal = whole seasons since 2024-01-01
    /// (computed from the season's own start and length, so an old season keeps its modifier).</item>
    /// </list>
    /// Event and board names embed their window's <see cref="LiveOpsWindow.Key"/>, so a season stays what it was
    /// (dates, rivals, modifier) even after the settings change.
    /// </summary>
    public static class LiveOpsSchedule
    {
        /// <summary>TimeSpan.TicksPerDay/TicksPerHour are missing from the game's trimmed mscorlib.</summary>
        public const long TicksPerHour = 36000000000L;
        public const long TicksPerDay = 24 * TicksPerHour;

        public const string SeasonPrefix = "mcvs_season_";
        public const string DailyPrefix = "mcvs_daily_";
        public const string WeeklyPrefix = "mcvs_weekly_";
        public const string BoardPrefix = "mcvs_division_";
        public const string ArenaPrefix = "mcvs_arena_";

        /// <summary>The division boards end with this word: the TS turns it into "Leaderboard_QA" for a developer
        /// profile (GM/DivisionsComponent.cs DivisionData.LeaderboardId, and every loopback player is one).</summary>
        public const string BoardSuffix = "_Leaderboard";

        public static LiveOpsWindow SeasonAt(LiveOpsSettings settings, DateTime utc)
        {
            long length = TicksPerDay * settings.SeasonDays;
            long offset = utc.Ticks - settings.SeasonAnchor.Ticks;
            long index = offset >= 0 ? offset / length : -((-offset + length - 1) / length);
            return new LiveOpsWindow(new DateTime(settings.SeasonAnchor.Ticks + index * length, DateTimeKind.Utc), settings.SeasonDays);
        }

        public static LiveOpsWindow DayAt(LiveOpsSettings settings, DateTime utc)
        {
            DateTime start = new DateTime(utc.Year, utc.Month, utc.Day, settings.DailyResetHour, 0, 0, DateTimeKind.Utc);
            if (start > utc) start = start.AddDays(-1);
            return new LiveOpsWindow(start, 1);
        }

        /// <summary>The Special Event window at <paramref name="utc"/>: <c>arenaHours</c> long, on the grid of whole windows
        /// from 2024-01-01 <c>dailyResetHour</c>:00 UTC (24 h windows start at the daily reset, like the quests).</summary>
        public static ArenaWindow ArenaAt(LiveOpsSettings settings, DateTime utc)
        {
            long length = TicksPerHour * settings.ArenaHours;
            long anchor = AnchorTicks() + settings.DailyResetHour * TicksPerHour;
            long offset = utc.Ticks - anchor;
            long index = offset >= 0 ? offset / length : -((-offset + length - 1) / length);
            return new ArenaWindow(new DateTime(anchor + index * length, DateTimeKind.Utc), settings.ArenaHours);
        }

        /// <summary>Whole seasons of this length between 2024-01-01 and the season start (the modifier rotation index).</summary>
        public static long Ordinal(LiveOpsWindow season)
        {
            long length = TicksPerDay * season.Days;
            long offset = season.Start.Ticks - AnchorTicks();
            return offset >= 0 ? offset / length : -((-offset + length - 1) / length);
        }

        /// <summary>Whole days between 2024-01-01 and the day start (the quest rotation index).</summary>
        public static long DayOrdinal(LiveOpsWindow day)
        {
            long offset = day.Start.Ticks - AnchorTicks();
            return offset >= 0 ? offset / TicksPerDay : -((-offset + TicksPerDay - 1) / TicksPerDay);
        }

        private static long AnchorTicks()
        {
            // Copy the readonly field first: calling a member on it directly takes its address, which ILVerify reports
            // against the game's mscorlib ("cannot change initonly field outside its .ctor"); see TimeFormat.
            DateTime anchor = LiveOpsSettings.DefaultAnchor;
            return anchor.Ticks;
        }

        // ------------------------------------------------------------------------------------------ names

        /// <summary>The season id (<c>_batch_tag</c>): the TS starts a new season when it changes.</summary>
        public static string SeasonId(LiveOpsWindow season)
        {
            return SeasonPrefix + season.Key;
        }

        public static string DivisionEventId(LiveOpsWindow season, int division)
        {
            return SeasonId(season) + "_d" + division.ToString("00", CultureInfo.InvariantCulture);
        }

        public static string BoardName(LiveOpsWindow season, int division)
        {
            return BoardPrefix + season.Key + "_d" + division.ToString("00", CultureInfo.InvariantCulture) + BoardSuffix;
        }

        public static string DailyEventId(LiveOpsWindow day)
        {
            return DailyPrefix + day.Key;
        }

        public static string WeeklyEventId(LiveOpsWindow season)
        {
            return WeeklyPrefix + season.Key;
        }

        /// <summary>"mcvs_arena_&lt;window key&gt;_&lt;mode id&gt;": the window and the mode the event runs (mode ids have no '_').</summary>
        public static string ArenaEventId(ArenaWindow window, string modeId)
        {
            return ArenaPrefix + window.Key + "_" + modeId;
        }

        /// <summary>A Special Event id: its window and mode id; false otherwise.</summary>
        public static bool TryParseArena(string id, out ArenaWindow window, out string modeId)
        {
            window = null;
            modeId = null;
            if (id == null || !id.StartsWith(ArenaPrefix, StringComparison.Ordinal)) return false;
            string rest = id.Substring(ArenaPrefix.Length);
            int at = rest.IndexOf('_');
            if (at < 0 || at == rest.Length - 1) return false;
            string mode = rest.Substring(at + 1);
            if (!LiveOpsArena.IsModeId(mode)) return false;
            window = ArenaWindow.Parse(rest.Substring(0, at));
            if (window == null) return false;
            modeId = mode;
            return true;
        }

        /// <summary>A division event id: its season and division; false otherwise.</summary>
        public static bool TryParseDivisionEvent(string id, out LiveOpsWindow season, out int division)
        {
            season = null;
            division = 0;
            if (id == null || !id.StartsWith(SeasonPrefix, StringComparison.Ordinal)) return false;
            string rest = id.Substring(SeasonPrefix.Length);
            int at = rest.LastIndexOf("_d", StringComparison.Ordinal);
            if (at < 0 || !TryDivision(rest.Substring(at + 2), out division)) return false;
            season = LiveOpsWindow.Parse(rest.Substring(0, at), true);
            return season != null;
        }

        /// <summary>A division board name (also the "_QA" form): its season and division.</summary>
        public static bool TryParseBoard(string name, out LiveOpsWindow season, out int division)
        {
            season = null;
            division = 0;
            if (name == null || !name.StartsWith(BoardPrefix, StringComparison.Ordinal)) return false;
            string rest = name.Substring(BoardPrefix.Length);
            if (rest.EndsWith(BoardSuffix + "_QA", StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - BoardSuffix.Length - 3);
            else if (rest.EndsWith(BoardSuffix, StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - BoardSuffix.Length);
            else return false;
            int at = rest.LastIndexOf("_d", StringComparison.Ordinal);
            if (at < 0 || !TryDivision(rest.Substring(at + 2), out division)) return false;
            season = LiveOpsWindow.Parse(rest.Substring(0, at), true);
            return season != null;
        }

        public static LiveOpsWindow TryParseDaily(string id)
        {
            if (id == null || !id.StartsWith(DailyPrefix, StringComparison.Ordinal)) return null;
            return LiveOpsWindow.Parse(id.Substring(DailyPrefix.Length), false);
        }

        public static LiveOpsWindow TryParseWeekly(string id)
        {
            if (id == null || !id.StartsWith(WeeklyPrefix, StringComparison.Ordinal)) return null;
            return LiveOpsWindow.Parse(id.Substring(WeeklyPrefix.Length), true);
        }

        private static bool TryDivision(string text, out int division)
        {
            division = 0;
            if (text.Length != 2 || text[0] < '0' || text[0] > '9' || text[1] < '0' || text[1] > '9') return false;
            division = (text[0] - '0') * 10 + (text[1] - '0');
            return division >= 1;
        }

        /// <summary>"2026-10-19 00:00:00Z" (the "u" pattern). Deliberately without the ISO 'T': Newtonsoft turns a
        /// "yyyy-MM-ddT..." string into a DateTime token, and the client then re-reads it through
        /// <c>JValue.ToString()</c> in the device's culture before <c>DateTime.TryParse(..., InvariantInfo)</c>
        /// (OP/SocialEvent.cs, EN/DateTimeUtil.cs): on a day-first locale that swaps day and month. A string without
        /// the 'T' stays a string and parses the same everywhere.</summary>
        public static string WireDate(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
