#define ENABLE_LOGGER
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Compiled into the embedded DLLs that reference Gameloft.Logs (MCVS_GAMELOG: always on Android, on PC when the build saw
// the Steam install's Gameloft.Logs.dll; the PC Gameloft.Logs has the same API), never into the net8 build.
// ENABLE_LOGGER must be defined in THIS file: Gameloft.Log.EnableLogCategory and DisableLogLevelForCategory are
// [Conditional("ENABLE_LOGGER")], so without it the compiler drops our calls (that is how the game's own
// SetLogCategoryFilter call vanished from the release Gameloft.Engine; its GameLog.cs defines it the same way).
#if MCVS_GAMELOG
using System;
using System.Collections.Generic;
using Gameloft.Logs;
using SafeLogFile = McvsOffline.Server.Core.SafeLogFile;

namespace McvsOffline.Embedded.Diagnostics
{
    /// <summary>
    /// The game's own log (Gameloft.Logs, which writes to logcat "mono-stdout" through its ConsoleLogger) at the levels the
    /// player chooses, plus a copy in McvsOffline/logs/game.log. settings.json:
    /// <code>
    /// "gameLog": { "levels": "error warning", "mute": "profile:debug", "file": true, "maxMessageChars": 8192 }
    /// </code>
    /// <c>levels</c> adds levels to the game's Fatal-only default (with the patcher's default --game-log all the game enables
    /// every level itself in GameApplication's constructor, a superset; on PC Gameloft.Log's static constructor already does,
    /// so there game.log keeps only Fatal plus <c>levels</c>, <see cref="GameLogFormat"/>); <c>mute</c> takes "category:level"
    /// pairs ("profile:debug", "online:all"), which survive that constructor because they live in a separate table.
    /// Lines: <see cref="GameLogFormat.Line"/> (UTC time, redacted).
    /// Applied once from Bootstrap.Init (UI thread, before GameApplication exists and before any game thread logs), so the
    /// game's List/Dictionary inside Gameloft.Log are never mutated while it reads them.
    /// <para>
    /// PC only: with a <see cref="GameFatalLines"/> (WindowsPlatform.ApplyGameLog) every Fatal entry also goes there, which
    /// writes the "PROBABLY FATAL" report of an exception the game rethrows into native code (docs/design/pc-port.md §6.4).
    /// </para>
    /// </summary>
    internal sealed class GameLogBridge : ILogger
    {
        private readonly SafeLogFile file;
        private readonly int maxMessageChars;
        private readonly GameFatalLines fatalLines;

        /// <summary>The levels game.log keeps, indexed by (int)LogLevel; null: every entry the game logs (Android).</summary>
        private readonly bool[] accepted;

        private GameLogBridge(SafeLogFile file, int maxMessageChars, bool[] accepted, GameFatalLines fatalLines)
        {
            this.file = file;
            this.maxMessageChars = maxMessageChars;
            this.accepted = accepted;
            this.fatalLines = fatalLines;
        }

        /// <summary>
        /// Returns a description of what was applied, e.g. "Fatal Error Warning, muted profile:Debug, copy to game.log".
        /// <paramref name="fileKeepsLevels"/>: game.log keeps only Fatal and <paramref name="levels"/> (PC, where the game
        /// itself enables every level); false: every entry the game logs (Android).
        /// </summary>
        public static string Apply(string levels, string mute, SafeLogFile file, int maxMessageChars, bool fileKeepsLevels)
        {
            return Apply(levels, mute, file, maxMessageChars, fileKeepsLevels, null);
        }

        /// <summary><see cref="Apply(string,string,SafeLogFile,int,bool)"/>, and every Fatal entry to <paramref name="fatalLines"/> (PC).</summary>
        public static string Apply(string levels, string mute, SafeLogFile file, int maxMessageChars, bool fileKeepsLevels, GameFatalLines fatalLines)
        {
            var applied = new List<string> { "Fatal" };
            foreach (string word in Split(levels))
            {
                Gameloft.Logs.LogLevel level = Gameloft.Log.GetLogLevelFromString(word);
                if (level == LogLevel.None || level == LogLevel.Fatal) continue;
                Gameloft.Log.EnableLogCategory(level);
                applied.Add(level.ToString());
            }
            var muted = new List<string>();
            foreach (string pair in Split(mute))
            {
                int colon = pair.IndexOf(':');
                if (colon <= 0 || colon == pair.Length - 1) continue;
                string category = pair.Substring(0, colon);
                string levelName = pair.Substring(colon + 1);
                var targets = new List<LogLevel>();
                if (levelName.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(LogLevel.Error);
                    targets.Add(LogLevel.Warning);
                    targets.Add(LogLevel.Info);
                    targets.Add(LogLevel.Debug);
                    targets.Add(LogLevel.Server);
                    targets.Add(LogLevel.All);
                }
                else
                {
                    LogLevel level = Gameloft.Log.GetLogLevelFromString(levelName);
                    if (level != LogLevel.None && level != LogLevel.Fatal) targets.Add(level);
                }
                foreach (LogLevel level in targets) Gameloft.Log.DisableLogLevelForCategory(category, level);
                if (targets.Count > 0) muted.Add(category + ":" + levelName);
            }
            string text = string.Join(" ", applied.ToArray());
            if (muted.Count > 0) text += ", muted " + string.Join(" ", muted.ToArray());
            if (file != null || fatalLines != null)
            {
                bool[] keep = file != null && fileKeepsLevels ? GameLogFormat.AcceptedLevels(levels) : null;
                Gameloft.Log.Settings.AddLogger(new GameLogBridge(file, maxMessageChars < 256 ? 256 : maxMessageChars, keep, fatalLines));
                if (file != null) text += ", copy to " + file.Path;
                if (keep != null) text += " (" + GameLogFormat.Describe(keep) + " only; the game itself logs every level here)";
                if (fatalLines != null) text += ", Fatal lines watched";
            }
            return text;
        }

        /// <summary>
        /// Called synchronously by Gameloft.Log.AddEntry on the logging thread (mostly the game thread), which has no try/catch
        /// around it: a level check, then an enqueue plus the redaction's fast path (a few ordinal searches; the game may log
        /// the URL it opens, which can carry the settings page's one-time ticket, design §5.4, and its own access tokens),
        /// never throws.
        /// </summary>
        public void EntryAdded(Gameloft.Logs.LogEntry entry)
        {
            try
            {
                bool fatal = entry.Level == LogLevel.Fatal;
                bool keep = file != null && GameLogFormat.Keeps(accepted, (int)entry.Level);
                if (!keep && !(fatal && fatalLines != null)) return;
                string line = GameLogFormat.Line(entry.Time, entry.Level.ToString(), entry.Category.ToString(), entry.Message,
                                                 entry.Exception, maxMessageChars);
                if (keep) file.AppendLine(line);
                // After the copy: the report flushes the log files, game.log with this line included.
                if (fatal && fatalLines != null) fatalLines.Fatal(entry.Member, entry.Message, entry.Exception, line);
            }
            catch (Exception)
            {
            }
        }

        private static string[] Split(string text)
        {
            return (text ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
#endif
