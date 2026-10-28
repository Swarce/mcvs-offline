using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using McvsOffline.Embedded;

namespace McvsOffline.PcSpike
{
    /// <summary>One route of the server log: how often it was hit and with which statuses.</summary>
    public sealed class RouteCount
    {
        public string Route;
        public int Count;
        public SortedSet<int> Statuses = new SortedSet<int>();
        public string FirstTarget;
    }

    /// <summary>
    /// 'collect' (WP0, docs/PC-SPIKE.md): one zip on the desktop with everything the spike recorded and a summary.txt that
    /// answers the WP0 questions where the files can: how far the stock client got against our server (step 1), whether
    /// the codeBase redirect worked (step 2), the memory peaks against the 2 GB limit (step 3), the game's own debug
    /// output and crash logs, matching Windows event-log entries, and the owner's answers to a few questions (step 4:
    /// what a news link opened). No game file goes into the zip: only logs, our own files, args.txt/.exe.config (ours)
    /// and a listing of the game folder's top level.
    /// </summary>
    public static class Collector
    {
        /// <summary>Largest part of one file put into the zip (head and tail halves of anything bigger).</summary>
        public const long FileCap = 40L * 1024 * 1024;

        private static readonly Regex RequestLine = new Regex(@"#(\d+) (GET|POST|PUT|DELETE|HEAD|OPTIONS|PATCH) (\S+) -> (\d{3}) \[([^\]]*)\]",
                                                              RegexOptions.CultureInvariant);

        public static readonly string[] Questions =
        {
            "Did the game reach the main menu? (yes/no/other)",
            "Did a bot match start, and did it finish? (describe)",
            "What happened when you opened a news/event link? (in-game page / web browser outside the game / nothing / freeze or crash)",
            "Did Windows Firewall (or any other window) ask something while the game ran? When?",
            "Graphics quality setting in the game's options (if you saw it):",
            "Anything else you noticed:",
        };

        /// <summary>Routes hit, in order of first appearance, from server.log lines ("#12 GET /eve/... -> 200 [eve.config] ...").</summary>
        public static List<RouteCount> SummariseRequests(IEnumerable<string> lines)
        {
            var routes = new List<RouteCount>();
            var byName = new Dictionary<string, RouteCount>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                Match m = RequestLine.Match(line);
                if (!m.Success) continue;
                string route = m.Groups[5].Value;
                RouteCount count;
                if (!byName.TryGetValue(route, out count))
                {
                    count = new RouteCount { Route = route, FirstTarget = m.Groups[2].Value + " " + m.Groups[3].Value };
                    byName[route] = count;
                    routes.Add(count);
                }
                count.Count++;
                count.Statuses.Add(int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
            }
            return routes;
        }

        /// <summary>Writes the zip into <paramref name="outputFolder"/> (null: the desktop) and returns its path.</summary>
        public static string Run(SpikeOptions options, GameLocation game, SpikeLog log, bool askQuestions, string outputFolder = null)
        {
            string dataDir = options.DataDir;
            var answers = new List<KeyValuePair<string, string>>();
            if (askQuestions)
            {
                Console.WriteLine();
                Console.WriteLine("A few questions for the report (press Enter to skip one):");
                foreach (string q in Questions)
                {
                    Console.Write("  " + q + " ");
                    string a = Console.ReadLine();
                    answers.Add(new KeyValuePair<string, string>(q, string.IsNullOrWhiteSpace(a) ? "(no answer)" : a.Trim()));
                }
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string target = Path.Combine(outputFolder ?? OutputFolder(dataDir),"McvsOffline-pc-spike-" + stamp + ".zip");
            var summary = new StringBuilder();
            using (FileStream file = File.Create(target))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Header(summary, game);
                GameFolder(summary, zip, game);

                summary.AppendLine();
                summary.AppendLine("== Results recorded by the kit (pc-spike.log)");
                string spikeLog = Path.Combine(dataDir, SpikeLog.FileName);
                if (File.Exists(spikeLog))
                {
                    foreach (string line in ReadLinesShared(spikeLog).Where(l => l.Contains(" RESULT "))) summary.AppendLine("  " + line);
                    AddFile(zip, spikeLog, SpikeLog.FileName);
                }
                else
                {
                    summary.AppendLine("  (no pc-spike.log: the kit was not run with this data folder)");
                }

                summary.AppendLine();
                summary.AppendLine("== Step 2: ModernCombatVersus.exe.config <codeBase> redirect");
                summary.AppendLine("  " + RedirectStep.DescribeMarker(dataDir));
                AddFileIfExists(zip, RedirectStep.MarkerPath(dataDir), RedirectStep.MarkerFileName);

                ServerLogs(summary, zip, dataDir);
                Telemetry(summary, zip, dataDir);
                GameDebug(summary, zip, dataDir);
                GameloftLogs(summary, zip);
                WindowsEvents(summary, zip);

                if (answers.Count > 0)
                {
                    summary.AppendLine();
                    summary.AppendLine("== Owner's answers");
                    foreach (var a in answers) summary.AppendLine("  " + a.Key + "\r\n    -> " + a.Value);
                }

                AddText(zip, "summary.txt", summary.ToString());
            }
            log.Info("collect: wrote " + target);
            return target;
        }

        private static string OutputFolder(string dataDir)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
            Directory.CreateDirectory(dataDir);
            return dataDir;
        }

        private static void Header(StringBuilder s, GameLocation game)
        {
            s.AppendLine("MCVS Offline - PC spike (WP0) summary");
            s.AppendLine("collected:   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            s.AppendLine("kit:         McvsOffline.PcSpike " + typeof(Collector).Assembly.GetName().Version + " (server " + McvsOffline.Server.Core.McvsVersion.Current + ")");
            s.AppendLine("windows:     " + RuntimeInformation.OSDescription + ", " + RuntimeInformation.OSArchitecture + ", " +
                         Environment.ProcessorCount + " logical CPUs, " +
                         (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB RAM");
            if (game == null)
            {
                s.AppendLine("game folder: NOT FOUND");
                return;
            }
            s.AppendLine("game folder: " + game.Directory + " (" + game.How + ")");
            s.AppendLine("exe:         " + KnownPcBuild.Describe(game.Exe, KnownPcBuild.ExeSha256));
            s.AppendLine("exe header:  " + PeHeader.Describe(game.Exe));
            s.AppendLine("EngineBridge.dll: " + KnownPcBuild.Describe(Path.Combine(game.Directory, RedirectStep.AssemblyFileName), KnownPcBuild.EngineBridgeSha256));
        }

        private static void GameFolder(StringBuilder s, ZipArchive zip, GameLocation game)
        {
            if (game == null) return;
            s.AppendLine();
            s.AppendLine("== Game folder now");
            string args = ArgsTxtStep.ArgsPath(game.Directory);
            if (File.Exists(args))
            {
                byte[] bytes = File.ReadAllBytes(args);
                bool ours = ArgsFile.ReadManagedValue(bytes, "ServerLocation") != null;
                s.AppendLine("  args.txt: present, " + (ours ? "with the MCVS Offline block (run 'stop' to restore the original)" : "without our block"));
                AddBytes(zip, "game-folder/args.txt", bytes);
            }
            else
            {
                s.AppendLine("  args.txt: absent");
            }
            List<string> prefixed = ArgsTxtStep.PrefixedArgsFiles(game.Directory);
            s.AppendLine("  *_args.txt files (would be read instead of args.txt): " + (prefixed.Count == 0 ? "none" : string.Join(", ", prefixed)));
            string config = RedirectStep.ConfigPath(game.Directory);
            if (File.Exists(config))
            {
                string text = File.ReadAllText(config);
                s.AppendLine("  " + ExeConfig.FileName + ": present" + (ExeConfig.IsOurs(text) ? " (ours; run 'redirect-undo')" : " (NOT ours)"));
                AddText(zip, "game-folder/" + ExeConfig.FileName, text);
            }
            else
            {
                s.AppendLine("  " + ExeConfig.FileName + ": absent");
            }

            var listing = new StringBuilder();
            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(game.Directory).OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(entry);
                    bool dir = Directory.Exists(entry);
                    listing.AppendLine((dir ? "<dir>" : info.Length.ToString(CultureInfo.InvariantCulture)).PadLeft(12) + "  " +
                                       File.GetLastWriteTime(entry).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  " + Path.GetFileName(entry));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                listing.AppendLine("(cannot list: " + ex.Message + ")");
            }
            AddText(zip, "game-folder/listing.txt", listing.ToString());
            foreach (string logFile in SafeFiles(game.Directory, "*.log")) AddFile(zip, logFile, "game-folder/" + Path.GetFileName(logFile));
        }

        private static void ServerLogs(StringBuilder s, ZipArchive zip, string dataDir)
        {
            s.AppendLine();
            s.AppendLine("== Step 1: the stock client against our server (server.log)");
            string logs = Path.Combine(dataDir, "logs");
            List<string> files = SafeFiles(logs, "*").ToList();
            if (files.Count == 0)
            {
                s.AppendLine("  no server logs ('start' was not run)");
                return;
            }
            foreach (string f in files) AddFile(zip, f, "server-logs/" + Path.GetFileName(f));
            // server.log.N are older rotations: read oldest first.
            IEnumerable<string> ordered = files.Where(f => Path.GetFileName(f).StartsWith("server.log", StringComparison.OrdinalIgnoreCase))
                                               .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (string f in ordered) lines.AddRange(ReadLinesShared(f));
            List<RouteCount> routes = SummariseRequests(lines);
            int requests = routes.Sum(r => r.Count);
            s.AppendLine("  " + requests + " requests, " + routes.Count + " routes (in order of first use):");
            foreach (RouteCount r in routes)
            {
                s.AppendLine("    " + r.Route.PadRight(32) + " x" + r.Count.ToString(CultureInfo.InvariantCulture).PadRight(5) +
                             " status " + string.Join("/", r.Statuses) + "   first: " + Truncate(r.FirstTarget, 90));
            }
            int unknown = lines.Count(l => l.Contains("UNKNOWN ENDPOINT"));
            int warnings = lines.Count(l => l.Contains(" WARN ") || l.Contains(" ERROR "));
            s.AppendLine("  unknown endpoints: " + unknown + ", warning/error lines: " + warnings);
        }

        private static void Telemetry(StringBuilder s, ZipArchive zip, string dataDir)
        {
            s.AppendLine();
            s.AppendLine("== Step 3: memory of ModernCombatVersus.exe (32-bit, 2048 MB of address space without LAA)");
            string folder = Path.Combine(dataDir, TelemetrySampler.FolderName);
            List<string> csvs = SafeFiles(folder, "telemetry-*.csv").ToList();
            if (csvs.Count == 0) s.AppendLine("  no telemetry recorded");
            foreach (string csv in csvs)
            {
                AddFile(zip, csv, "telemetry/" + Path.GetFileName(csv));
                double privateMb, virtualMb;
                int rows;
                if (TelemetryCsv.Peaks(csv, out privateMb, out virtualMb, out rows))
                {
                    s.AppendLine("  " + Path.GetFileName(csv) + ": " + rows + " samples, peak private " + privateMb.ToString("0", CultureInfo.InvariantCulture) +
                                 " MB, peak virtual " + virtualMb.ToString("0", CultureInfo.InvariantCulture) + " MB (" +
                                 (virtualMb / 20.48).ToString("0", CultureInfo.InvariantCulture) + " % of 2048 MB)");
                }
                else
                {
                    s.AppendLine("  " + Path.GetFileName(csv) + ": no samples (the game was not running)");
                }
            }
            foreach (string sockets in SafeFiles(folder, "sockets-*.txt"))
            {
                AddFile(zip, sockets, "telemetry/" + Path.GetFileName(sockets));
                s.AppendLine("  sockets the game listened on (" + Path.GetFileName(sockets) + "):");
                foreach (string line in ReadLinesShared(sockets).Take(40)) s.AppendLine("    " + line);
            }
        }

        private static void GameDebug(StringBuilder s, ZipArchive zip, string dataDir)
        {
            s.AppendLine();
            s.AppendLine("== The game's debug output (OutputDebugString)");
            List<string> files = SafeFiles(Path.Combine(dataDir, DebugOutputCapture.FolderName), "*.log").ToList();
            if (files.Count == 0)
            {
                s.AppendLine("  none captured");
                return;
            }
            foreach (string f in files)
            {
                AddFile(zip, f, "game-debug/" + Path.GetFileName(f));
                var lines = ReadLinesShared(f).ToList();
                var problems = lines.Where(l => l.IndexOf("Exception", StringComparison.Ordinal) >= 0 || l.Contains(" Error:") || l.Contains(" Fatal:")).ToList();
                s.AppendLine("  " + Path.GetFileName(f) + ": " + lines.Count + " lines, " + problems.Count + " with Exception/Error/Fatal; the first ones:");
                foreach (string line in problems.Take(25)) s.AppendLine("    " + Truncate(line, 220));
                if (lines.Count > 0) s.AppendLine("  last line: " + Truncate(lines[lines.Count - 1], 220));
            }
        }

        private static void GameloftLogs(StringBuilder s, ZipArchive zip)
        {
            s.AppendLine();
            s.AppendLine("== The game's crash logs (%LOCALAPPDATA%\\Gameloft)");
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gameloft");
            List<string> files = SafeFiles(folder, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(30).ToList();
            if (files.Count == 0)
            {
                s.AppendLine("  none");
                return;
            }
            foreach (string f in files)
            {
                AddFile(zip, f, "gameloft-logs/" + Path.GetFileName(f));
                s.AppendLine("  " + Path.GetFileName(f) + " (" + File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
                if (Path.GetFileName(f).StartsWith("crashLog", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string plain = CrashLogDecoder.DecryptFile(f);
                        AddText(zip, "gameloft-logs/" + Path.GetFileNameWithoutExtension(f) + ".decrypted.txt", plain);
                        s.AppendLine("    " + Truncate(plain.Replace("\r\n", " | "), 300));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        /// <summary>Application Error / .NET Runtime / WER entries of the last 7 days that name the game (crash reasons).</summary>
        private static void WindowsEvents(StringBuilder s, ZipArchive zip)
        {
            if (!OperatingSystem.IsWindows()) return;
            s.AppendLine();
            s.AppendLine("== Windows event log (Application: crashes of ModernCombatVersus.exe, last 7 days)");
            try
            {
                const string query = "*[System[Provider[@Name='Application Error' or @Name='.NET Runtime' or @Name='Windows Error Reporting' or @Name='Application Hang']" +
                                     " and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
                var info = new ProcessStartInfo("wevtutil", "qe Application /rd:true /c:200 /f:text \"/q:" + query + "\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                string output;
                using (Process p = Process.Start(info))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                }
                List<string> events = output.Split(new[] { "Event[" }, StringSplitOptions.RemoveEmptyEntries)
                                            .Where(e => e.IndexOf("ModernCombatVersus", StringComparison.OrdinalIgnoreCase) >= 0)
                                            .Select(e => "Event[" + e.Trim())
                                            .ToList();
                s.AppendLine("  " + events.Count + " matching entries" + (events.Count > 0 ? " (windows-events.txt)" : ""));
                if (events.Count > 0) AddText(zip, "windows-events.txt", string.Join("\r\n\r\n", events));
            }
            catch (Exception ex)
            {
                s.AppendLine("  could not read the event log: " + ex.Message);
            }
        }

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return new string[0];
                return Directory.GetFiles(folder, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        /// <summary>Reads a file another process may still be writing.</summary>
        private static IEnumerable<string> ReadLinesShared(string file)
        {
            var lines = new List<string>();
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return lines;
        }

        private static void AddFileIfExists(ZipArchive zip, string file, string entryName)
        {
            if (File.Exists(file)) AddFile(zip, file, entryName);
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
                        Copy(stream, output, FileCap / 2);
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

        private static void Copy(Stream input, Stream output, long count)
        {
            var buffer = new byte[81920];
            while (count > 0)
            {
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                count -= read;
            }
        }

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            AddBytes(zip, entryName, new UTF8Encoding(false).GetBytes(text));
        }

        private static void AddBytes(ZipArchive zip, string entryName, byte[] bytes)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (Stream output = entry.Open()) output.Write(bytes, 0, bytes.Length);
        }

        private static string Truncate(string text, int max)
        {
            return text == null || text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
