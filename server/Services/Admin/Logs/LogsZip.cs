using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin.Saves;

namespace McvsOffline.Server.Services.Admin.Logs
{
    /// <summary>A file of a logs directory as GET logs lists it.</summary>
    internal sealed class LogFile
    {
        /// <summary>0 = primary, 1 = the host's fallback directory.</summary>
        public int Dir { get; set; }

        public string Name { get; set; }

        /// <summary>server, bootstrap, game, rotated, crash, exception, threaddump, exit, unknown-endpoints, other.</summary>
        public string Kind { get; set; }

        public long Size { get; set; }

        public DateTime ModifiedUtc { get; set; }

        public string Path { get; set; }

        /// <summary>The name inside a zip: the file name, under "fallback/" for the fallback directory.</summary>
        public string EntryName
        {
            get { return Dir == 0 ? Name : "fallback/" + Name; }
        }
    }

    internal sealed class LogsZipResult
    {
        public string File { get; set; }

        public long Size { get; set; }

        public int Entries { get; set; }

        public List<string> Skipped { get; set; }
    }

    /// <summary>
    /// The logs zip for a bug report (design §7.2). Scope <c>recent</c> (default): server.log, bootstrap.log and game.log
    /// (tails ≤ 4 MiB), their newest rotation .1 (≤ 2 MiB), crash-/exception-/exit-/threaddump- files of the last 72 h
    /// (newest 10 per kind, ≤ 1 MiB each) and unknown-endpoints.jsonl (≤ 512 KiB), in that priority order, with a hard cap
    /// of 8 MiB compressed: an entry that would cross it is left out and listed as skipped. Scope <c>all</c>: every listed
    /// file, ≤ 8 MiB each, 48 MiB uncompressed in total. Plus status.json, settings.json (without superPassword),
    /// note.txt and manifest.txt. Every text entry goes through <see cref="LogRedaction.Redact"/> before its CRC is
    /// computed; binary entries (*.pb) are copied as they are.
    /// </summary>
    internal static class LogsZip
    {
        public const long RecentCap = 8L * 1024 * 1024;
        public const long CurrentTail = 4L * 1024 * 1024;
        public const long RotatedTail = 2L * 1024 * 1024;
        public const long EventTail = 1024 * 1024;
        public const long UnknownTail = 512 * 1024;
        public const long AllPerFile = 8L * 1024 * 1024;
        public const long AllTotal = 48L * 1024 * 1024;
        public const int NewestPerKind = 10;
        public static readonly TimeSpan RecentAge = TimeSpan.FromHours(72);

        /// <summary>Room kept for manifest.txt under the recent cap.</summary>
        private const long ManifestReserve = 64 * 1024;

        private static readonly string[] CurrentLogs = { "server.log", "bootstrap.log", "game.log" };
        private static readonly string[] EventKinds = { "crash", "exception", "exit", "threaddump" };

        /// <summary>The files of a scope, in priority order, with the tail each may contribute.</summary>
        public static List<KeyValuePair<LogFile, long>> Select(List<LogFile> files, bool all, DateTime nowUtc)
        {
            var picked = new List<KeyValuePair<LogFile, long>>();
            if (all)
            {
                foreach (LogFile f in files) picked.Add(new KeyValuePair<LogFile, long>(f, AllPerFile));
                return picked;
            }
            foreach (string name in CurrentLogs) AddNamed(picked, files, name, CurrentTail);
            foreach (string name in CurrentLogs) AddNamed(picked, files, name + ".1", RotatedTail);
            foreach (string kind in EventKinds)
            {
                var ofKind = new List<LogFile>();
                foreach (LogFile f in files)
                {
                    if (f.Kind == kind && nowUtc - f.ModifiedUtc <= RecentAge) ofKind.Add(f);
                }
                ofKind.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
                for (int i = 0; i < ofKind.Count && i < NewestPerKind; i++) picked.Add(new KeyValuePair<LogFile, long>(ofKind[i], EventTail));
            }
            AddNamed(picked, files, "unknown-endpoints.jsonl", UnknownTail);
            return picked;
        }

        private static void AddNamed(List<KeyValuePair<LogFile, long>> picked, List<LogFile> files, string name, long cap)
        {
            foreach (LogFile f in files)
            {
                if (f.Name == name) picked.Add(new KeyValuePair<LogFile, long>(f, cap));
            }
        }

        /// <summary>Builds the zip at <paramref name="path"/> (through a temp file and a rename).</summary>
        public static LogsZipResult Build(string path, List<LogFile> files, bool all, string statusJson, string settingsJson, string note,
                                          bool deflate, DateTime nowUtc)
        {
            var manifest = new StringBuilder();
            manifest.Append("MCVS Offline logs, ").Append(all ? "everything" : "recent").Append(", built ")
                    .Append(nowUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append('\n');
            manifest.Append("entry\toriginal bytes\tincluded bytes\tstate\n");
            var skipped = new List<string>();
            string temp = path + "." + Environment.TickCount.ToString("x", CultureInfo.InvariantCulture) + AtomicFile.TempSuffix;
            int entries;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var zip = new ZipWriter(stream, deflate, nowUtc.ToLocalTime());
                    AddText(zip, "status.json", statusJson ?? "{}");
                    AddText(zip, "settings.json", settingsJson ?? "{}");
                    if (!string.IsNullOrEmpty(note)) AddText(zip, "note.txt", note + "\n");

                    long uncompressed = 0;
                    foreach (var pick in Select(files, all, nowUtc))
                    {
                        LogFile file = pick.Key;
                        long size;
                        byte[] data;
                        bool truncated;
                        try
                        {
                            data = Snapshot(file, pick.Value, out size, out truncated);
                        }
                        catch (Exception)
                        {
                            manifest.Append(file.EntryName).Append('\t').Append(file.Size).Append("\t0\tunreadable\n");
                            continue;
                        }
                        if (all && uncompressed + data.Length > AllTotal)
                        {
                            skipped.Add(file.EntryName);
                            manifest.Append(file.EntryName).Append('\t').Append(size).Append("\t0\tskipped\n");
                            continue;
                        }
                        ZipEntryData entry = zip.Prepare(file.EntryName, data);
                        if (!all && zip.FinishedSize + entry.ArchiveBytes + ManifestReserve > RecentCap)
                        {
                            skipped.Add(file.EntryName);
                            manifest.Append(file.EntryName).Append('\t').Append(size).Append("\t0\tskipped\n");
                            continue;
                        }
                        zip.Add(entry);
                        uncompressed += data.Length;
                        manifest.Append(file.EntryName).Append('\t').Append(size).Append('\t').Append(data.Length).Append('\t')
                                .Append(truncated ? "truncated" : "complete").Append('\n');
                    }
                    AddText(zip, "manifest.txt", manifest.ToString());
                    zip.Finish();
                    entries = zip.Count;
                    FileSystem.FlushToDisk(stream);
                }
                FileSystem.Rename(temp, path);
            }
            catch
            {
                AdminFiles.DeleteQuietly(temp);
                throw;
            }
            return new LogsZipResult { File = System.IO.Path.GetFileName(path), Size = new FileInfo(path).Length, Entries = entries, Skipped = skipped };
        }

        /// <summary>The tail of a file (at most <paramref name="cap"/> bytes); text is redacted and starts at a whole line.</summary>
        internal static byte[] Snapshot(LogFile file, long cap, out long size, out bool truncated)
        {
            byte[] bytes = AdminFiles.ReadTail(file.Path, cap, out size);
            truncated = size > bytes.Length;
            if (IsBinary(file.Name)) return bytes;
            int start = 0;
            if (truncated)
            {
                int newline = Array.IndexOf(bytes, (byte)'\n');
                start = newline < 0 ? 0 : newline + 1;
            }
            string text = AdminFiles.Utf8(bytes, start, bytes.Length - start);
            string redacted = LogRedaction.Redact(text);
            if (start == 0 && ReferenceEquals(redacted, text) && !truncated) return bytes;
            return AdminFiles.Utf8Bytes(redacted);
        }

        private static void AddText(ZipWriter zip, string name, string text)
        {
            zip.Add(zip.Prepare(name, AdminFiles.Utf8Bytes(LogRedaction.Redact(text))));
        }

        private static bool IsBinary(string name)
        {
            return name.EndsWith(".pb", StringComparison.OrdinalIgnoreCase);
        }
    }
}
