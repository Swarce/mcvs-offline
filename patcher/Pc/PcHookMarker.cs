using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using McvsOffline.Patcher.Patches;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>One entry of hook-pc's marker file: a start of our patched EngineBridge, or the exception McvsOfflineInit threw.</summary>
    public sealed class PcHookEntry
    {
        public PcHookEntry(DateTime utc, bool failed, string text)
        {
            Utc = utc;
            Failed = failed;
            Text = text;
        }

        public DateTime Utc { get; }

        /// <summary>A "McvsOfflineInit failed" entry (else a start).</summary>
        public bool Failed { get; }

        /// <summary>After " hook-pc ": "start pid 1234 exe … dll …", or "McvsOfflineInit failed (pid 1234): System.IO.FileNotFoundException: …" (first line only).</summary>
        public string Text { get; }

        public string Time
        {
            get { return Utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); }
        }

        /// <summary>The pid after "pid " ("1234", or "?" when kernel32 could not tell), or null.</summary>
        public string Pid
        {
            get
            {
                int at = Text.IndexOf("pid ", StringComparison.Ordinal);
                if (at < 0) return null;
                int start = at + "pid ".Length, end = start;
                while (end < Text.Length && Text[end] != ' ' && Text[end] != ')') end++;
                return end > start ? Text.Substring(start, end - start) : null;
            }
        }

        /// <summary>A start's exe path (between " exe " and " dll "), or null.</summary>
        public string Exe
        {
            get
            {
                if (Failed) return null;
                int exe = Text.IndexOf(" exe ", StringComparison.Ordinal), dll = Text.LastIndexOf(" dll ", StringComparison.Ordinal);
                return exe < 0 || dll < exe ? null : Text.Substring(exe + " exe ".Length, dll - exe - " exe ".Length);
            }
        }

        /// <summary>A start's EngineBridge.dll path (after the last " dll "), or null.</summary>
        public string Dll
        {
            get
            {
                if (Failed) return null;
                int dll = Text.LastIndexOf(" dll ", StringComparison.Ordinal);
                return dll < 0 ? null : Text.Substring(dll + " dll ".Length);
            }
        }
    }

    /// <summary>
    /// <c>%TEMP%\McvsOffline\hook.txt</c> as <see cref="EngineBridgeHookPatch"/> writes it: one line per start of our patched
    /// EngineBridge ("&lt;utc&gt; hook-pc start pid …"), and one entry per exception of McvsOfflineInit ("&lt;utc&gt; hook-pc
    /// McvsOfflineInit failed (pid …): &lt;exception text, which may run over several lines&gt;"). Lines that do not start an entry
    /// belong to the one before them.
    /// </summary>
    public static class PcHookMarker
    {
        public static List<PcHookEntry> Read(string text)
        {
            var list = new List<PcHookEntry>();
            if (string.IsNullOrEmpty(text)) return list;
            int timeLength = EngineBridgeHookPatch.MarkerTimeFormat.Replace("'", "").Length;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                if (raw.Length < timeLength) continue;
                DateTime utc;
                if (!DateTime.TryParseExact(raw.Substring(0, timeLength), EngineBridgeHookPatch.MarkerTimeFormat, CultureInfo.InvariantCulture,
                                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
                    continue;
                string rest = raw.Substring(timeLength);
                if (rest.StartsWith(EngineBridgeHookPatch.MarkerStart, StringComparison.Ordinal))
                    list.Add(new PcHookEntry(utc, false, rest.Substring(" hook-pc ".Length)));
                else if (rest.StartsWith(EngineBridgeHookPatch.MarkerFailed, StringComparison.Ordinal))
                    list.Add(new PcHookEntry(utc, true, rest.Substring(" hook-pc ".Length)));
            }
            return list;
        }

        /// <summary>The newest start at or after <paramref name="sinceUtc"/>, or null.</summary>
        public static PcHookEntry LastStart(IEnumerable<PcHookEntry> entries, DateTime sinceUtc)
        {
            return entries.Where(e => !e.Failed && e.Utc >= sinceUtc).OrderBy(e => e.Utc).LastOrDefault();
        }

        /// <summary>
        /// The newest start at or after <paramref name="sinceUtc"/> of the game in <paramref name="game"/>: its exe or its
        /// EngineBridge.dll lies in that folder. %TEMP%\McvsOffline\hook.txt is shared by every copy of the game this user runs.
        /// </summary>
        public static PcHookEntry LastStart(IEnumerable<PcHookEntry> entries, DateTime sinceUtc, string game)
        {
            return LastStart(entries.Where(e => !e.Failed && (Under(e.Exe, game) || Under(e.Dll, game))), sinceUtc);
        }

        /// <summary>The newest failure at or after <paramref name="sinceUtc"/>, or null.</summary>
        public static PcHookEntry LastFailure(IEnumerable<PcHookEntry> entries, DateTime sinceUtc)
        {
            return entries.Where(e => e.Failed && e.Utc >= sinceUtc).OrderBy(e => e.Utc).LastOrDefault();
        }

        /// <summary>The newest failure of the process of <paramref name="start"/> (same pid, not before it), or null.</summary>
        public static PcHookEntry FailureOf(IEnumerable<PcHookEntry> entries, PcHookEntry start)
        {
            return entries.Where(e => e.Failed && e.Utc >= start.Utc && string.Equals(e.Pid, start.Pid, StringComparison.Ordinal))
                          .OrderBy(e => e.Utc).LastOrDefault();
        }

        /// <summary>Whether <paramref name="path"/> (a Windows path from the marker) lies in the folder <paramref name="game"/>.</summary>
        public static bool Under(string path, string game)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(game)) return false;
            string folder = game.Replace('/', '\\').TrimEnd('\\') + "\\";
            return path.Replace('/', '\\').StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }
    }
}
