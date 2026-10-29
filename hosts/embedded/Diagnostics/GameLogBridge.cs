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
using System.Globalization;
using System.Text;
using Gameloft.Logs;
using LogRedaction = McvsOffline.Server.Core.Http.LogRedaction;
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
    /// every level itself in GameApplication's constructor, a superset); <c>mute</c> takes "category:level" pairs
    /// ("profile:debug", "online:all"), which survive that constructor because they live in a separate table.
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

        private GameLogBridge(SafeLogFile file, int maxMessageChars, GameFatalLines fatalLines)
        {
            this.file = file;
            this.maxMessageChars = maxMessageChars;
            this.fatalLines = fatalLines;
        }

        /// <summary>Returns a description of what was applied, e.g. "Fatal Error Warning, muted profile:Debug, copy to game.log".</summary>
        public static string Apply(string levels, string mute, SafeLogFile file, int maxMessageChars)
        {
            return Apply(levels, mute, file, maxMessageChars, null);
        }

        /// <summary><see cref="Apply(string,string,SafeLogFile,int)"/>, and every Fatal entry to <paramref name="fatalLines"/> (PC).</summary>
        public static string Apply(string levels, string mute, SafeLogFile file, int maxMessageChars, GameFatalLines fatalLines)
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
                Gameloft.Log.Settings.AddLogger(new GameLogBridge(file, maxMessageChars < 256 ? 256 : maxMessageChars, fatalLines));
                if (file != null) text += ", copy to " + file.Path;
                if (fatalLines != null) text += ", Fatal lines watched";
            }
            return text;
        }

        /// <summary>
        /// Called synchronously by Gameloft.Log.AddEntry on the logging thread (mostly the game thread), which has no try/catch
        /// around it: an enqueue plus the redaction's fast path (a few ordinal searches; the game may log the URL it opens, which
        /// can carry the settings page's one-time ticket, design §5.4), never throws.
        /// </summary>
        public void EntryAdded(Gameloft.Logs.LogEntry entry)
        {
            try
            {
                string message = entry.Message ?? "";
                if (message.Length > maxMessageChars)
                    message = message.Substring(0, maxMessageChars) + "...[" + message.Length.ToString(CultureInfo.InvariantCulture) + " chars]";
                var sb = new StringBuilder(message.Length + 64);
                sb.Append(entry.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(entry.Level.ToString()).Append(" [").Append(entry.Category.ToString()).Append("] ").Append(message);
                if (entry.Exception != null) sb.Append('\n').Append(entry.Exception.ToString());
                string line = LogRedaction.Redact(sb.ToString());
                if (file != null) file.AppendLine(line);
                // After the copy: the report flushes the log files, game.log with this line included.
                if (fatalLines != null && entry.Level == LogLevel.Fatal) fatalLines.Fatal(entry.Member, entry.Message, entry.Exception, line);
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
