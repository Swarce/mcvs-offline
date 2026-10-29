using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Pc;
using McvsOffline.PcSpike;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// 'collect' (docs/design/pc-port.md §9.2): one zip on the Desktop, <c>mcvs-offline-pc-&lt;yyyyMMdd-HHmmss&gt;.zip</c>, with
    /// our logs (McvsOffline\logs, the %TEMP% fallback, the kit's folder), settings.json, install.json, the config, args.txt, a
    /// listing of the game folder, the status and probe output, the game's Windows event-log entries and decoded crash logs,
    /// and a listing (names, sizes, dates; never contents) of the engine's own id files and folders. Never profiles\,
    /// accounts\, tokens.json, and never the contents of the Gameloft id files.
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
                   .Append(", kit ").Append(KitServer.Version()).Append("\r\n");
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
                    AddText(zip, "app_preferences-locations.txt", string.Join("\r\n", FindPreferences(game, env)));
                }
                string tempData = Path.Combine(env.TempRoot, "McvsOffline");
                AddTree(zip, Path.Combine(tempData, "logs"), "temp-logs/", summary);
                AddTree(zip, Path.Combine(tempData, GameFolder.KitDirName), "temp-kit/", summary);
                if (statusLines != null) AddText(zip, "status.txt", string.Join("\r\n", statusLines) + "\r\n");
                if (probeLines != null) AddText(zip, "probe.txt", string.Join("\r\n", probeLines) + "\r\n");
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
        /// Gameloft folder under %APPDATA% and %LOCALAPPDATA%, and every app_preferences.json in those places and the game folder.
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
            foreach (string pref in FindPreferences(game, env)) sb.Append(Describe(new FileInfo(pref))).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>Every app_preferences.json in the game folder and under the Gameloft folders of %APPDATA%, %LOCALAPPDATA% and Documents.</summary>
        public static List<string> FindPreferences(string game, KitEnvironment env)
        {
            const string name = "app_preferences.json";
            var found = new List<string>();
            if (!string.IsNullOrEmpty(game) && File.Exists(Path.Combine(game, name))) found.Add(Path.Combine(game, name));
            foreach (string root in new[] { env.AppData, env.LocalAppData, env.Documents })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (string dir in SafeDirectories(root).Where(d => Path.GetFileName(d).IndexOf("gameloft", StringComparison.OrdinalIgnoreCase) >= 0))
                    found.AddRange(SafeFiles(dir).Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).Select(f => f.FullName));
            }
            return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        /// <summary>Application Error / .NET Runtime / WER / Hang entries of the last 7 days that name the game (Windows only).</summary>
        private static void WindowsEvents(ZipArchive zip, StringBuilder summary)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                const string query = "*[System[Provider[@Name='Application Error' or @Name='.NET Runtime' or @Name='Windows Error Reporting' or @Name='Application Hang']" +
                                     " and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
                var info = new ProcessStartInfo("wevtutil", "qe Application /rd:true /c:200 /f:text \"/q:" + query + "\"")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                };
                string output;
                using (Process p = Process.Start(info))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                }
                List<string> events = output.Split(new[] { "Event[" }, StringSplitOptions.RemoveEmptyEntries)
                                            .Where(e => e.IndexOf("ModernCombatVersus", StringComparison.OrdinalIgnoreCase) >= 0)
                                            .Select(e => "Event[" + e.Trim()).ToList();
                summary.Append("windows events naming the game (7 days): ").Append(events.Count).Append("\r\n");
                AddText(zip, "windows-events.txt", string.Join("\r\n\r\n", events));
            }
            catch (Exception ex)
            {
                summary.Append("windows events: ").Append(ex.Message).Append("\r\n");
            }
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
