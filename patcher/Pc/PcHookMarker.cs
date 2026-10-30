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

        /// <summary>The newest failure at or after <paramref name="sinceUtc"/>, or null.</summary>
        public static PcHookEntry LastFailure(IEnumerable<PcHookEntry> entries, DateTime sinceUtc)
        {
            return entries.Where(e => e.Failed && e.Utc >= sinceUtc).OrderBy(e => e.Utc).LastOrDefault();
        }
    }
}
