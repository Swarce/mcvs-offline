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
        /// recoverable from its key alone, whatever the settings are later.</summary>
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

        /// <summary>Parses a <see cref="Key"/>; null when it is not one.</summary>
        public static LiveOpsWindow Parse(string key, bool multiDay)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 10) return null;
            int year, month, day, hour, days = 1;
            if (!Int(key, 0, 4, out year) || !Int(key, 4, 2, out month) || !Int(key, 6, 2, out day) || !Int(key, 8, 2, out hour)) return null;
            if (multiDay)
            {
                if (key.Length < 12 || key[10] != 'x' || !Int(key, 11, key.Length - 11, out days)) return null;
                if (days < 1 || days > 28) return null;
            }
            else if (key.Length != 10)
            {
                return null;
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
