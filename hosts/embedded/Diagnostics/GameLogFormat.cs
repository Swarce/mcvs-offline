// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs (Android and PC) and into the net8 tests. GameLogBridge (which
// needs Gameloft.Logs) keeps only the calls into the game; what it writes and which entries it keeps is decided here.
using System;
using System.Globalization;
using System.Text;
using LogRedaction = McvsOffline.Server.Core.Http.LogRedaction;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// The lines of logs/game.log and the level filter of the PC host (docs/design/pc-port.md; WR2 F5).
    /// <para>
    /// A line is <c>2026-10-28T17:53:25.781Z Debug [Online] message</c>: the entry's time in UTC like server.log and
    /// bootstrap.log (the game stamps entries with its local <c>DateTime.Now</c>), the level and category names as the game
    /// prints them, then the message (cut to the configured length), and the exception's text on the following lines; the
    /// whole text goes through <see cref="LogRedaction"/>.
    /// </para>
    /// <para>
    /// On PC the game enables every level itself (Gameloft.Log's static constructor on Win32NT, GameApplication's constructor
    /// again), so <c>gameLog.levels</c> cannot narrow what the game logs; game.log keeps Fatal plus the configured levels
    /// instead (<see cref="AcceptedLevels"/>). On Android the copy keeps every entry the game logs (unchanged).
    /// </para>
    /// </summary>
    internal static class GameLogFormat
    {
        /// <summary>Gameloft.Logs.LogLevel's names in value order (None = 0 … All = 7); a real-assembly test pins them.</summary>
        public static readonly string[] LevelNames = { "None", "Fatal", "Error", "Warning", "Info", "Debug", "Server", "All" };

        /// <summary>(int)Gameloft.Logs.LogLevel.Fatal: always kept.</summary>
        public const int Fatal = 1;

        /// <summary>
        /// Which levels game.log keeps, indexed by <c>(int)LogLevel</c>: Fatal, plus every word of <paramref name="levels"/>
        /// that names a level the way <c>Gameloft.Log.GetLogLevelFromString</c> reads it (any letter case; "none" and unknown
        /// words count for nothing). As in the game's own check, a level keeps only entries of exactly that level ("all" is
        /// the level All, not every level).
        /// </summary>
        public static bool[] AcceptedLevels(string levels)
        {
            var accepted = new bool[LevelNames.Length];
            accepted[Fatal] = true;
            foreach (string word in (levels ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                for (int i = 1; i < LevelNames.Length; i++)
                {
                    if (string.Equals(LevelNames[i], word, StringComparison.OrdinalIgnoreCase)) accepted[i] = true;
                }
            }
            return accepted;
        }

        /// <summary>True when <paramref name="accepted"/> (null: no filter) keeps an entry of level <paramref name="level"/>.</summary>
        public static bool Keeps(bool[] accepted, int level)
        {
            return accepted == null || (level >= 0 && level < accepted.Length && accepted[level]);
        }

        /// <summary>"Fatal Error Warning": the names <paramref name="accepted"/> keeps, in level order.</summary>
        public static string Describe(bool[] accepted)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < accepted.Length && i < LevelNames.Length; i++)
            {
                if (!accepted[i]) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(LevelNames[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// One entry as written to game.log (redacted). <paramref name="time"/> is the entry's own time (a local
        /// <c>DateTime.Now</c>; an Unspecified kind counts as local, a Utc one is kept).
        /// </summary>
        public static string Line(DateTime time, string level, string category, string message, Exception exception, int maxMessageChars)
        {
            message = message ?? "";
            if (message.Length > maxMessageChars)
                message = message.Substring(0, maxMessageChars) + "...[" + message.Length.ToString(CultureInfo.InvariantCulture) + " chars]";
            var sb = new StringBuilder(message.Length + 80);
            sb.Append(Stamp(time)).Append(' ').Append(level).Append(" [").Append(category).Append("] ").Append(message);
            if (exception != null) sb.Append('\n').Append(exception.ToString());
            return LogRedaction.Redact(sb.ToString());
        }

        /// <summary>"2026-10-28T17:53:25.781Z".</summary>
        public static string Stamp(DateTime time)
        {
            DateTime utc = time.Kind == DateTimeKind.Utc ? time : time.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }
    }
}
