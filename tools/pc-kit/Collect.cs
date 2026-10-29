using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// 'collect' (docs/design/pc-port.md §9.2): one zip on the Desktop, <c>mcvs-offline-pc-&lt;yyyyMMdd-HHmmss&gt;.zip</c>, with
    /// our logs (McvsOffline\logs, the %TEMP% fallback, the kit's folder), settings.json, install.json, the config, args.txt, a
    /// listing of the game folder, the status and probe output, the game's Windows event-log entries (times in UTC) and decoded
    /// crash logs, and a listing (names, sizes, dates; never contents) of the engine's own id files, folders and MyLocalStorage
    /// files. Never profiles\, accounts\, tokens.json, the contents of the Gameloft id files, or the game's crash dump core.dmp
    /// (1.5-2 GB when it works: its size and time are in summary.txt).
    /// </summary>
    public static class Collect
    {
        public const string FilePrefix = "mcvs-offline-pc-";

        /// <summary>Largest part of one file put into the zip (head and tail halves of anything bigger).</summary>
        public const long FileCap = 40L * 1024 * 1024;

        /// <summary>The engine's per-user id files in Documents ([WH-V] C11): listed, never read.</summary>
        public static readonly IReadOnlyList<string> GameloftIdFiles = new[] { "GDID.bin", "DC.bin", "HDIDFV.bin" };

        /// <summary>Writes the zip; returns its path. <paramref name="statusLines"/> / <paramref name="probeLines"/> go in as text.</summary>
        public static string Run(KitEnvironment env, string game, IList<string> statusLines, IList<string> probeLines, KitLog log)
        {
            DateTime now = env.Now();
            string folder = FirstExisting(env.Desktop, env.Documents, env.TempRoot);
            string target = Path.Combine(folder, FilePrefix + KitServer.Stamp(now) + ".zip");
            var summary = new StringBuilder();
            summary.Append("MCVS Offline PC collect, ").Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                   .Append(", ").Append(KitServer.Identity(env)).Append("\r\n");
            summary.Append("game folder: ").Append(game ?? "(not found)").Append("\r\n");
            using (FileStream file = File.Create(target))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                if (game != null)
                {
                    string data = GameFolder.DataDir(game);
                    AddTree(zip, Path.Combine(data, "logs"), "logs/", summary);
                    AddTree(zip, Path.Combine(data, GameFolder.KitDirName), "kit/", summary);
                    AddIfExists(zip, Path.Combine(data, "settings.json"), "settings.json", summary);
                    AddIfExists(zip, Path.Combine(data, InstallManifest.FileName), InstallManifest.FileName, summary);
                    AddIfExists(zip, Path.Combine(game, PcExeConfig.FileName), PcExeConfig.FileName, summary);
                    AddIfExists(zip, Path.Combine(game, GameFolder.ArgsFileName), GameFolder.ArgsFileName, summary);
                    try
                    {
                        AddText(zip, "game-folder-listing.txt", GameFolder.FormatListing(game, GameFolder.Listing(game)));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        AddText(zip, "game-folder-listing.txt.error.txt", ex.Message);
                    }
                    string dump = GameFolder.DescribeCrashDump(game);
                    summary.Append("the game's own crash dump: ").Append(dump == null ? "none" : dump + " (not included)").Append("\r\n");
                }
                string tempData = Path.Combine(env.TempRoot, "McvsOffline");
                AddTree(zip, Path.Combine(tempData, "logs"), "temp-logs/", summary);
                AddTree(zip, Path.Combine(tempData, GameFolder.KitDirName), "temp-kit/", summary);
                if (statusLines != null) AddText(zip, "status.txt", string.Join("\r\n", statusLines) + "\r\n");
                if (probeLines != null) AddText(zip, "probe.txt", string.Join("\r\n", probeLines) + "\r\n");
                List<string> storage = FindLocalStorage(game, env);
                AddText(zip, "local-storage-locations.txt", string.Join("\r\n", storage.Select(f => Describe(new FileInfo(f)))));
                summary.Append("MyLocalStorage files (the in-game options; listed, not read): ").Append(storage.Count).Append("\r\n");
                AddText(zip, "gameloft-files-listing.txt", GameloftListing(game, env));
                GameloftCrashLogs(zip, env, summary);
                WindowsEvents(zip, summary);
                AddText(zip, "summary.txt", summary.ToString());
            }
            if (log != null) log.Info("collect: wrote " + target);
            return target;
        }

        /// <summary>
        /// Names, sizes and times (no contents) of the engine's own files outside our folder: the id files in Documents, every
        /// Gameloft folder under %APPDATA% and %LOCALAPPDATA%, and every MyLocalStorage file (<see cref="FindLocalStorage"/>).
        /// </summary>
        public static string GameloftListing(string game, KitEnvironment env)
        {
            var sb = new StringBuilder();
            sb.Append("# the game's own files outside McvsOffline\\ (listed, never read)\r\n");
            foreach (string name in GameloftIdFiles)
            {
                string path = string.IsNullOrEmpty(env.Documents) ? null : Path.Combine(env.Documents, name);
                sb.Append(path != null && File.Exists(path) ? Describe(new FileInfo(path)) : "Documents\\" + name + "\tabsent").Append("\r\n");
            }
            foreach (string root in new[] { env.AppData, env.LocalAppData })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (string dir in SafeDirectories(root).Where(d => Path.GetFileName(d).IndexOf("gameloft", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    sb.Append(dir).Append("\\\r\n");
                    foreach (FileInfo f in SafeFiles(dir).Take(500)) sb.Append("  ").Append(Describe(f)).Append("\r\n");
                }
            }
            foreach (string file in FindLocalStorage(game, env)) sb.Append(Describe(new FileInfo(file))).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>The engine's options file on PC (LocalStorageComponent.FileName, a SaveStream file; the in-game graphics quality).</summary>
        public const string LocalStoragePrefix = "MyLocalStorage";

        /// <summary>Folder names the engine's save root may have below %APPDATA%, %LOCALAPPDATA% or Documents (the exe name, gameloft).</summary>
        public static readonly IReadOnlyList<string> SaveRootHints = new[] { "gameloft", "ModernCombat" };

        /// <summary>
        /// Every <c>MyLocalStorage*</c> file (the PC keeps its options there, not in an app_preferences.json): in the game folder
        /// and McvsOffline\, and below the folders of %APPDATA%, %LOCALAPPDATA% and Documents whose name contains one of
        /// <see cref="SaveRootHints"/> (the native save root is built from %APPDATA%, the exe name and "gameloft"; WR2 F9).
        /// </summary>
        public static List<string> FindLocalStorage(string game, KitEnvironment env)
        {
            var found = new List<string>();
            Func<FileInfo, bool> named = f => f.Name.StartsWith(LocalStoragePrefix, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(game) && Directory.Exists(game))
            {
                found.AddRange(SafeTopFiles(game).Where(named).Select(f => f.FullName));
                string data = GameFolder.DataDir(game);
                if (Directory.Exists(data)) found.AddRange(SafeTopFiles(data).Where(named).Select(f => f.FullName));
            }
            foreach (string root in new[] { env.AppData, env.LocalAppData, env.Documents })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (string dir in SafeDirectories(root).Where(d => SaveRootHints.Any(h => Path.GetFileName(d).IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)))
                    found.AddRange(SafeFiles(dir).Where(named).Select(f => f.FullName));
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<FileInfo> SafeTopFiles(string folder)
        {
            try
            {
                return new DirectoryInfo(folder).EnumerateFiles().ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new FileInfo[0];
            }
        }

        /// <summary>The game's crash logs under %LOCALAPPDATA%\Gameloft, raw and decoded (CrashLogDecoder).</summary>
        private static void GameloftCrashLogs(ZipArchive zip, KitEnvironment env, StringBuilder summary)
        {
            if (string.IsNullOrEmpty(env.LocalAppData)) return;
            string folder = Path.Combine(env.LocalAppData, "Gameloft");
            if (!Directory.Exists(folder)) return;
            foreach (FileInfo f in SafeFiles(folder).Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
                                                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
            {
                string entry = "gameloft-logs/" + Path.GetRelativePath(folder, f.FullName).Replace('\\', '/');
                AddFile(zip, f.FullName, entry);
                summary.Append("crash log: ").Append(f.FullName).Append("\r\n");
                if (!f.Name.StartsWith("crashLog", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    AddText(zip, entry + ".decrypted.txt", CrashLogDecoder.DecryptFile(f.FullName));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.Cryptography.CryptographicException)
                {
                    AddText(zip, entry + ".decrypt-error.txt", ex.Message);
                }
            }
        }

        /// <summary>
        /// Application Error / .NET Runtime / WER / Hang entries of the last 7 days that name the game (Windows only), as
        /// <c>wevtutil /f:xml</c>: the XML carries <c>TimeCreated/@SystemTime</c> in UTC (the text format prints local times with
        /// a 'Z', WR2), and wevtutil writes ANSI to a pipe, so the output is decoded with the ANSI code page (not the OEM console
        /// one: "Informaþ§es"). windows-events.txt is <see cref="FormatEvents"/>; windows-events.xml the matching raw events;
        /// windows-events.raw.txt, only when some exist, the events naming the game that are not readable XML (kept as text, so
        /// one bad event never hides the others or itself).
        /// </summary>
        private static void WindowsEvents(ZipArchive zip, StringBuilder summary)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                const string query = "*[System[Provider[@Name='Application Error' or @Name='.NET Runtime' or @Name='Windows Error Reporting' or @Name='Application Hang']" +
                                     " and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
                var info = new ProcessStartInfo("wevtutil", "qe Application /rd:true /c:200 /f:xml \"/q:" + query + "\"")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = AnsiCodePage.Current,
                };
                string output;
                using (Process p = Process.Start(info))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                }
                List<string> unparsed;
                List<XElement> events = MatchingEvents(output, "ModernCombatVersus", out unparsed);
                summary.Append("windows events naming the game (7 days): ").Append(events.Count).Append(" (times in UTC)");
                if (unparsed.Count > 0) summary.Append(", plus ").Append(unparsed.Count).Append(" unparsed (raw text in windows-events.raw.txt)");
                summary.Append("\r\n");
                AddText(zip, "windows-events.txt", FormatEvents(events));
                AddText(zip, "windows-events.xml", string.Join("\r\n", events.Select(e => e.ToString(SaveOptions.DisableFormatting))));
                if (unparsed.Count > 0) AddText(zip, "windows-events.raw.txt", string.Join("\r\n", unparsed));
            }
            catch (Exception ex)
            {
                summary.Append("windows events: ").Append(ex.Message).Append("\r\n");
            }
        }

        /// <summary><see cref="MatchingEvents(string, string, out List{string})"/> without the unparsed ones.</summary>
        public static List<XElement> MatchingEvents(string xml, string token)
        {
            List<string> unparsed;
            return MatchingEvents(xml, token, out unparsed);
        }

        /// <summary>
        /// The <c>&lt;Event&gt;</c> elements of wevtutil's XML output (a sequence of events without a root element) whose text
        /// names <paramref name="token"/>. Each event is parsed on its own: one that is not readable XML (a bad character
        /// reference, a cut-off end) goes, when its text names the token, to <paramref name="unparsed"/> as it is, and so does
        /// text outside any event that names it. Empty lists for empty output.
        /// </summary>
        public static List<XElement> MatchingEvents(string xml, string token, out List<string> unparsed)
        {
            var result = new List<XElement>();
            unparsed = new List<string>();
            if (string.IsNullOrWhiteSpace(xml)) return result;
            const string close = "</Event>";
            int at = 0;
            while (at < xml.Length)
            {
                int start = EventStart(xml, at);
                string between = xml.Substring(at, (start < 0 ? xml.Length : start) - at);
                if (Names(between, token)) unparsed.Add(between.Trim().TrimStart('\ufeff'));
                if (start < 0) break;
                int end = xml.IndexOf(close, start, StringComparison.Ordinal);
                end = end < 0 ? xml.Length : end + close.Length;
                string chunk = xml.Substring(start, end - start);
                at = end;
                if (!Names(chunk, token)) continue;
                try
                {
                    XElement e = XElement.Parse(chunk);
                    // The token in the element's text, not only in a broken tag or a comment.
                    if (Names(e.ToString(SaveOptions.DisableFormatting), token)) result.Add(e);
                }
                catch (System.Xml.XmlException)
                {
                    unparsed.Add(chunk);
                }
            }
            return result;
        }

        /// <summary>The index of the next "&lt;Event" start tag (not &lt;EventData&gt;, &lt;EventID&gt;…) at or after <paramref name="from"/>, or -1.</summary>
        private static int EventStart(string xml, int from)
        {
            const string open = "<Event";
            for (int i = xml.IndexOf(open, from, StringComparison.Ordinal); i >= 0; i = xml.IndexOf(open, i + 1, StringComparison.Ordinal))
            {
                int next = i + open.Length;
                if (next >= xml.Length) return i;
                char c = xml[next];
                if (c == '>' || c == '/' || char.IsWhiteSpace(c)) return i;
            }
            return -1;
        }

        private static bool Names(string text, string token)
        {
            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// One block per event: "Event[i]", the UTC time (TimeCreated/@SystemTime, ISO 8601 with Z), provider, event id, level,
        /// and the event data (name=value, or the values alone), newest first as wevtutil returned them.
        /// </summary>
        public static string FormatEvents(IList<XElement> events)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < events.Count; i++)
            {
                XElement system = Child(events[i], "System");
                string time = Attribute(Child(system, "TimeCreated"), "SystemTime");
                DateTime utc;
                if (time != null && DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
                    time = utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
                if (i > 0) sb.Append("\r\n");
                sb.Append("Event[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("]\r\n");
                sb.Append("  Time (UTC): ").Append(time ?? "?").Append("\r\n");
                sb.Append("  Source: ").Append(Attribute(Child(system, "Provider"), "Name") ?? "?").Append("\r\n");
                sb.Append("  Event ID: ").Append(Text(Child(system, "EventID")) ?? "?").Append("\r\n");
                sb.Append("  Level: ").Append(Text(Child(system, "Level")) ?? "?").Append("\r\n");
                XElement data = Child(events[i], "EventData") ?? Child(events[i], "UserData");
                if (data == null) continue;
                foreach (XElement d in data.Descendants().Where(x => !x.HasElements))
                {
                    string name = Attribute(d, "Name") ?? (d.Name.LocalName == "Data" ? null : d.Name.LocalName);
                    string value = d.Value.Trim();
                    if (value.Length == 0) continue;
                    sb.Append("  ").Append(name == null ? "" : name + ": ").Append(value.Replace("\r\n", "\n").Replace("\n", "\r\n    ")).Append("\r\n");
                }
            }
            return sb.ToString();
        }

        private static XElement Child(XElement parent, string localName)
        {
            return parent == null ? null : parent.Elements().FirstOrDefault(x => x.Name.LocalName == localName);
        }

        private static string Attribute(XElement element, string name)
        {
            XAttribute a = element == null ? null : element.Attribute(name);
            return a == null ? null : a.Value;
        }

        private static string Text(XElement element)
        {
            return element == null ? null : element.Value;
        }

        /// <summary>Every file below <paramref name="folder"/> under <paramref name="prefix"/> (shared reads, capped).</summary>
        private static void AddTree(ZipArchive zip, string folder, string prefix, StringBuilder summary)
        {
            if (!Directory.Exists(folder)) return;
            int count = 0;
            foreach (FileInfo f in SafeFiles(folder).OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                AddFile(zip, f.FullName, prefix + Path.GetRelativePath(folder, f.FullName).Replace('\\', '/'));
                count++;
            }
            summary.Append(prefix).Append(": ").Append(count).Append(" files from ").Append(folder).Append("\r\n");
        }

        private static void AddIfExists(ZipArchive zip, string file, string entry, StringBuilder summary)
        {
            if (!File.Exists(file)) return;
            AddFile(zip, file, entry);
            summary.Append(entry).Append(": included\r\n");
        }

        /// <summary>Adds a file (shared read); anything above <see cref="FileCap"/> keeps its first and last halves.</summary>
        internal static void AddFile(ZipArchive zip, string file, string entryName)
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (Stream output = entry.Open())
                    {
                        if (stream.Length <= FileCap)
                        {
                            stream.CopyTo(output);
                            return;
                        }
                        var buffer = new byte[81920];
                        long left = FileCap / 2;
                        while (left > 0)
                        {
                            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (read <= 0) break;
                            output.Write(buffer, 0, read);
                            left -= read;
                        }
                        byte[] gap = Encoding.ASCII.GetBytes("\r\n\r\n[... " + (stream.Length - FileCap) + " bytes left out by collect ...]\r\n\r\n");
                        output.Write(gap, 0, gap.Length);
                        stream.Position = stream.Length - FileCap / 2;
                        stream.CopyTo(output);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AddText(zip, entryName + ".error.txt", "could not read " + file + ": " + ex.Message);
            }
        }

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            byte[] bytes = new UTF8Encoding(false).GetBytes(text ?? "");
            using (Stream output = entry.Open()) output.Write(bytes, 0, bytes.Length);
        }

        private static string Describe(FileInfo f)
        {
            return f.FullName + "\t" + f.Length.ToString(CultureInfo.InvariantCulture) + " bytes\t" +
                   f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> SafeDirectories(string root)
        {
            try
            {
                return Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        private static IEnumerable<FileInfo> SafeFiles(string folder)
        {
            try
            {
                return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 }).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new FileInfo[0];
            }
        }

        private static string FirstExisting(params string[] folders)
        {
            foreach (string f in folders)
            {
                if (!string.IsNullOrEmpty(f) && Directory.Exists(f)) return f;
            }
            return Path.GetTempPath();
        }
    }
}
