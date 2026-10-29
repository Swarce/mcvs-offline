using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Patcher.Core;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>One file of the build identity: what is in the game folder against what the build profile expects.</summary>
    public sealed class PcFileIdentity
    {
        public string File { get; set; }

        public bool Missing { get; set; }

        public string Sha256 { get; set; }

        public string ExpectedSha256 { get; set; }

        /// <summary>Assemblies only; null for the exe or an unreadable file.</summary>
        public Guid? Mvid { get; set; }

        public Guid? ExpectedMvid { get; set; }

        /// <summary>Present, and its SHA-256 (and MVID, where one is expected) is the verified build's.</summary>
        public bool Known
        {
            get
            {
                if (Missing) return false;
                if (ExpectedSha256 != null && !string.Equals(Sha256, ExpectedSha256, StringComparison.Ordinal)) return false;
                if (ExpectedMvid.HasValue && Mvid != ExpectedMvid) return false;
                return true;
            }
        }

        public override string ToString()
        {
            if (Missing) return File + ": missing";
            string mvid = Mvid.HasValue ? ", MVID " + Mvid.Value.ToString("D") : "";
            return File + ": " + (Known ? "build 6372049" : "UNKNOWN") + " (SHA-256 " + Sha256 + mvid + ")";
        }
    }

    /// <summary>One top-level entry of the game folder, as the kit's listing records it.</summary>
    public sealed class GameFolderEntry
    {
        public string Name { get; set; }

        public bool IsDirectory { get; set; }

        /// <summary>A file's size, or a folder's total size (all files below it).</summary>
        public long Size { get; set; }

        public DateTime LastWriteUtc { get; set; }

        /// <summary>Files only (lower-case hex); null when the file could not be read.</summary>
        public string Sha256 { get; set; }

        /// <summary>Folders only: the number of files below it.</summary>
        public int FileCount { get; set; }
    }

    /// <summary>How a top-level entry of the game folder is classified by status and the listing diff (docs/design/pc-port.md §7.6).</summary>
    public enum GameFolderOwner
    {
        /// <summary>Shipped by Steam (in the depot's top level).</summary>
        Depot,

        /// <summary>Ours: McvsOffline\, the config (and its temporary file), args.txt.</summary>
        Ours,

        /// <summary>Written by the game itself (dr0.bin..dr3.bin, OF_PP.bin, GameVersion, core.dmp), not ours.</summary>
        Game,

        /// <summary>Anything else (e.g. a file from another tool): informational, not ours.</summary>
        Other,
    }

    /// <summary>
    /// The Steam game folder as the PC installer sees it (docs/design/pc-port.md §3.5, §7.1): the files it reads, their SHA-256
    /// and MVIDs, whether the game runs, and the top-level listing the kit records before and after an install.
    /// </summary>
    public static class GameFolder
    {
        public const string ExeName = "ModernCombatVersus.exe";
        public const string ProcessName = "ModernCombatVersus";
        public const string DataDirName = "McvsOffline";
        public const string BinDirName = "bin";
        public const string KitDirName = "kit";
        public const string ArgsFileName = "args.txt";
        public const string GameMarker = "Data/MCVS.game";

        /// <summary>The installer's temporary files end in this and lie next to their target (§3.5, §7.4).</summary>
        public const string TempSuffix = ".mcvs-tmp";

        /// <summary>The WP0 spike's copy folder (tools/pc-spike RedirectStep.CopyFolder).</summary>
        public const string SpikeCopyFolder = "McvsOffline/spike";

        /// <summary>The assemblies the PC patches edit and the config redirects, in the order written (§7.4).</summary>
        public static readonly IReadOnlyList<string> PatchedFiles = new[] { "EngineBridge.dll", "Game.dll", "Gameloft.Engine.dll", "Gameloft.Util.dll" };

        public const string ServerFile = "McvsOffline.Server.dll";

        /// <summary>
        /// The game's crash dump: ModernCombatVersus.exe's own unhandled-exception filter writes it (MiniDumpWriteDump with full
        /// memory, CREATE_ALWAYS, so only the last crash's survives; WR2 F9). A working one is 1.5-2 GB: the listing does not hash
        /// it and 'collect' never zips it, only reports its size and time.
        /// </summary>
        public const string CrashDumpName = "core.dmp";

        /// <summary>
        /// Files the game itself writes into its folder at run time ([ES-V] claim 9, the WP0 listing; GameVersion and
        /// <see cref="CrashDumpName"/> from the WR2 listings): not ours.
        /// </summary>
        public static readonly IReadOnlyList<string> GameWrittenNames = new[] { "dr0.bin", "dr1.bin", "dr2.bin", "dr3.bin", "OF_PP.bin", "GameVersion", CrashDumpName };

        /// <summary>What the listing writes instead of a SHA-256 for <see cref="CrashDumpName"/>.</summary>
        public const string NotHashed = "not-hashed";

        public static bool IsCrashDump(string name)
        {
            return string.Equals(name, CrashDumpName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// "core.dmp, 0 bytes, written 2026-10-29 18:22:25 UTC" for the game's crash dump in <paramref name="game"/>, or null when
        /// there is none (or it cannot be read).
        /// </summary>
        public static string DescribeCrashDump(string game)
        {
            try
            {
                var dump = new FileInfo(Path.Combine(game, CrashDumpName));
                if (!dump.Exists) return null;
                return CrashDumpName + ", " + dump.Length.ToString(CultureInfo.InvariantCulture) + " bytes, written " +
                       dump.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>The top-level entries of the read-only Steam install of build 6372049 (MCVS_PC_REFS; BuildProfileTests checks it).</summary>
        public static readonly IReadOnlyList<string> DepotTopLevel = new[]
        {
            "Assets", "Data", "EngineBridge.dll", "Game.dll", "GameManager.dll", "Gameloft.Aspects.dll", "Gameloft.Collections.dll",
            "Gameloft.Engine.Math.dll", "Gameloft.Engine.dll", "Gameloft.Logs.dll", "Gameloft.OnlinePlatform.dll", "Gameloft.Sequences.dll",
            "Gameloft.Templates.dll", "Gameloft.Util.dll", "ModernCombatVersus.exe", "NativeEngine.dll", "Newtonsoft.Json.dll",
            "Shaders_high_end.pak", "Shaders_low_end.pak", "Strings", "WCPToolkit.dll", "_CommonRedist", "nativegame.dll", "steam_api.dll",
            "steam_appid.txt",
        };

        public static string DataDir(string game)
        {
            return Path.Combine(game, DataDirName);
        }

        public static string BinDir(string game)
        {
            return Path.Combine(game, DataDirName, BinDirName);
        }

        /// <summary>A path relative to the game folder with '/' (as install.json records it) made absolute.</summary>
        public static string Resolve(string game, string relative)
        {
            return Path.Combine(new[] { game }.Concat(relative.Split('/')).ToArray());
        }

        /// <summary>The bin path of one written assembly, relative with '/': "McvsOffline/bin/Game.dll".</summary>
        public static string BinRelative(string file)
        {
            return DataDirName + "/" + BinDirName + "/" + file;
        }

        /// <summary>Whether ModernCombatVersus.exe runs (Process.GetProcessesByName; false when processes cannot be listed).</summary>
        public static bool IsGameRunning()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(ProcessName);
                foreach (Process p in processes) p.Dispose();
                return processes.Length > 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException || ex is System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// <c>*_args.txt</c> files in the game folder: the native reader prefers <c>&lt;prefix&gt;_args.txt</c> over args.txt
        /// (tools/pc-spike/ArgsTxtStep.cs), so MCVS Offline would be ignored and the game would go online ([CR] F9).
        /// </summary>
        public static List<string> PrefixedArgsFiles(string game)
        {
            if (!Directory.Exists(game)) return new List<string>();
            return Directory.GetFiles(game, "*_args.txt").Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>What install step 1 needs: the exe, the four DLLs and Data\MCVS.game; null when complete, else what is missing.</summary>
        public static string MissingGameFiles(string game)
        {
            if (string.IsNullOrEmpty(game) || !Directory.Exists(game)) return "the folder " + game + " does not exist";
            var missing = new List<string>();
            foreach (string file in new[] { ExeName }.Concat(PatchedFiles).Concat(new[] { GameMarker }))
            {
                if (!File.Exists(Resolve(game, file))) missing.Add(file.Replace('/', '\\'));
            }
            return missing.Count == 0 ? null : game + " is not a Modern Combat Versus (Steam) folder: missing " + string.Join(", ", missing);
        }

        /// <summary>The build identity of the folder: every file of <see cref="BuildProfile.OriginalSha256"/> and its MVID where the profile has one.</summary>
        public static List<PcFileIdentity> Identity(string game, BuildProfile build)
        {
            var list = new List<PcFileIdentity>();
            var files = new List<string> { ExeName };
            files.AddRange(PatchedFiles);
            files.AddRange(build.UnchangedWitnesses.Keys.OrderBy(k => k, StringComparer.Ordinal));
            files.AddRange(build.OriginalSha256.Keys.Where(k => !files.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
            foreach (string file in files)
            {
                var id = new PcFileIdentity { File = file };
                string expected;
                if (build.OriginalSha256.TryGetValue(file, out expected)) id.ExpectedSha256 = expected;
                Guid mvid;
                if (build.PatchedOriginals.TryGetValue(file, out mvid) || build.UnchangedWitnesses.TryGetValue(file, out mvid)) id.ExpectedMvid = mvid;
                string path = Path.Combine(game, file);
                if (!File.Exists(path))
                {
                    id.Missing = true;
                    list.Add(id);
                    continue;
                }
                byte[] bytes = File.ReadAllBytes(path);
                id.Sha256 = Sha256(bytes);
                if (id.ExpectedMvid.HasValue) id.Mvid = TryReadMvid(bytes);
                list.Add(id);
            }
            return list;
        }

        public static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(bytes));
            }
        }

        /// <summary>The SHA-256 of a file, read shared (the game or a log writer may have it open); null when it cannot be read.</summary>
        public static string TrySha256(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 sha = SHA256.Create())
                {
                    return Hex(sha.ComputeHash(stream));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static string Hex(byte[] bytes)
        {
            return string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        /// <summary>The module MVID from the metadata (no resolver, no BCL needed); null for anything that is not an assembly.</summary>
        public static Guid? TryReadMvid(byte[] assembly)
        {
            try
            {
                using (var pe = new PEReader(new MemoryStream(assembly)))
                {
                    if (!pe.HasMetadata) return null;
                    MetadataReader md = pe.GetMetadataReader();
                    return md.GetGuid(md.GetModuleDefinition().Mvid);
                }
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        /// <summary>The assembly's own version (the config's codeBase version), from the metadata.</summary>
        public static Version ReadAssemblyVersion(byte[] assembly)
        {
            using (var pe = new PEReader(new MemoryStream(assembly)))
            {
                MetadataReader md = pe.GetMetadataReader();
                return md.GetAssemblyDefinition().Version;
            }
        }

        /// <summary>The assembly's simple name, from the metadata.</summary>
        public static string ReadAssemblyName(byte[] assembly)
        {
            using (var pe = new PEReader(new MemoryStream(assembly)))
            {
                MetadataReader md = pe.GetMetadataReader();
                return md.GetString(md.GetAssemblyDefinition().Name);
            }
        }

        // ------------------------------------------------------------------------------------------------ the listing

        /// <summary>How a top-level name is classified (case-insensitive, as Windows compares names).</summary>
        public static GameFolderOwner Classify(string name)
        {
            if (string.Equals(name, DataDirName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PcExeConfig.FileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, PcExeConfig.FileName + TempSuffix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ArgsFileName, StringComparison.OrdinalIgnoreCase))
                return GameFolderOwner.Ours;
            if (GameWrittenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return GameFolderOwner.Game;
            if (DepotTopLevel.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return GameFolderOwner.Depot;
            return GameFolderOwner.Other;
        }

        /// <summary>
        /// Every top-level entry of the game folder: files with size, time and SHA-256, folders with their file count and total
        /// size (read shared; nothing is changed).
        /// </summary>
        public static List<GameFolderEntry> Listing(string game)
        {
            var entries = new List<GameFolderEntry>();
            var dir = new DirectoryInfo(game);
            foreach (FileSystemInfo info in dir.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new GameFolderEntry { Name = info.Name, LastWriteUtc = info.LastWriteTimeUtc };
                var sub = info as DirectoryInfo;
                if (sub != null)
                {
                    entry.IsDirectory = true;
                    try
                    {
                        foreach (FileInfo f in sub.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                        {
                            entry.FileCount++;
                            entry.Size += f.Length;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                    }
                }
                else
                {
                    entry.Size = ((FileInfo)info).Length;
                    // The crash dump can be 2 GB: its size and time say enough.
                    if (!IsCrashDump(info.Name)) entry.Sha256 = TrySha256(info.FullName);
                }
                entries.Add(entry);
            }
            return entries;
        }

        /// <summary>
        /// The listing as text: one line per entry, tab-separated (name, kind, size, UTC time, SHA-256 or file count; <see cref="NotHashed"/>
        /// for the crash dump).
        /// </summary>
        public static string FormatListing(string game, IEnumerable<GameFolderEntry> entries)
        {
            var sb = new StringBuilder();
            sb.Append("# MCVS Offline game-folder listing of ").Append(game).Append('\n');
            sb.Append("# name\tkind\tsize\tmodified (UTC)\tsha256 | files\n");
            foreach (GameFolderEntry e in entries)
            {
                sb.Append(e.Name).Append('\t').Append(e.IsDirectory ? "dir" : "file").Append('\t')
                  .Append(e.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.LastWriteUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.IsDirectory ? e.FileCount.ToString(CultureInfo.InvariantCulture) + " files" : e.Sha256 ?? (IsCrashDump(e.Name) ? NotHashed : "unreadable"))
                  .Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Reads a listing written by <see cref="FormatListing"/> (comment lines and malformed lines are skipped).</summary>
        public static List<GameFolderEntry> ParseListing(string text)
        {
            var entries = new List<GameFolderEntry>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] f = line.Split('\t');
                if (f.Length < 5) continue;
                var e = new GameFolderEntry { Name = f[0], IsDirectory = f[1] == "dir" };
                long size;
                if (long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out size)) e.Size = size;
                DateTime time;
                if (DateTime.TryParseExact(f[3], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time))
                    e.LastWriteUtc = time;
                if (e.IsDirectory)
                {
                    int count;
                    if (int.TryParse(f[4].Replace(" files", ""), NumberStyles.None, CultureInfo.InvariantCulture, out count)) e.FileCount = count;
                }
                else
                {
                    e.Sha256 = f[4] == "unreadable" || f[4] == NotHashed ? null : f[4];
                }
                entries.Add(e);
            }
            return entries;
        }

        /// <summary>
        /// The differences between two listings, one line each, classified: "ours", "the game's own" (§7.6) or "other". An
        /// entry whose content is unchanged but whose time differs counts as changed only for files (a folder's time changes with
        /// anything inside it; its count and size are compared instead).
        /// </summary>
        public static List<string> DiffListings(IList<GameFolderEntry> before, IList<GameFolderEntry> after)
        {
            var lines = new List<string>();
            var old = before.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            var now = after.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
            foreach (GameFolderEntry e in after)
            {
                GameFolderEntry was;
                if (!old.TryGetValue(e.Name, out was)) lines.Add(Describe("added", e));
                else if (Changed(was, e)) lines.Add(Describe("changed", e));
            }
            foreach (GameFolderEntry e in before)
            {
                if (!now.ContainsKey(e.Name)) lines.Add(Describe("removed", e));
            }
            return lines;
        }

        private static bool Changed(GameFolderEntry a, GameFolderEntry b)
        {
            if (a.IsDirectory != b.IsDirectory) return true;
            if (a.IsDirectory) return a.FileCount != b.FileCount || a.Size != b.Size;
            // Not hashed: a new crash can leave a dump of the same size (0 bytes in WR2), so its time counts.
            if (IsCrashDump(b.Name)) return a.Size != b.Size || a.LastWriteUtc != b.LastWriteUtc;
            return a.Size != b.Size || !string.Equals(a.Sha256, b.Sha256, StringComparison.Ordinal);
        }

        private static string Describe(string what, GameFolderEntry e)
        {
            string owner;
            switch (Classify(e.Name))
            {
                case GameFolderOwner.Ours: owner = "ours"; break;
                case GameFolderOwner.Game:
                    owner = IsCrashDump(e.Name)
                        ? "the game's own crash dump (" + e.Size.ToString(CultureInfo.InvariantCulture) + " bytes, written " +
                          e.LastWriteUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC; not ours, never collected)"
                        : "the game's own (written by the game, not ours)";
                    break;
                case GameFolderOwner.Depot: owner = "a Steam file"; break;
                default: owner = "other (not ours)"; break;
            }
            return what + "\t" + e.Name + (e.IsDirectory ? "\\" : "") + "\t" + owner;
        }
    }
}
