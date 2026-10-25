using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>One stored backup of a profile file.</summary>
    public sealed class BackupInfo
    {
        /// <summary>File name without extension, e.g. "20261025T101500123Z-r12-auto" (also the backup id on the wire).</summary>
        public string Id { get; set; }

        public DateTime DateUtc { get; set; }

        public long Revision { get; set; }

        /// <summary>Why it was taken: auto, delete, replace, wipe, corrupt, restore.</summary>
        public string Reason { get; set; }

        public long Size { get; set; }

        public string Path { get; set; }
    }

    /// <summary>
    /// Rotating copies of each account's profile file in &lt;data&gt;/storage/backups/&lt;account&gt;/
    /// (report 03 §12.1). Taken before destructive writes (profile delete, root replace, a write that clears the
    /// game profile — e.g. the R7 "_mcvs = {gameProfile:null}" reset that a 403/404 would trigger, verify-C3 §3.1)
    /// and at most every <c>storage.backups.intervalMinutes</c> before a normal save. They also serve the Seshat
    /// backups endpoint. Callers hold the account lock.
    /// </summary>
    internal sealed class ProfileBackups
    {
        private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
        private const string Extension = ".json";

        private readonly string root;
        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> lastBackup = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public ProfileBackups(string root)
        {
            this.root = root;
            Directory.CreateDirectory(root);
        }

        public string DirectoryFor(string accountId)
        {
            return System.IO.Path.Combine(root, DocumentStore.EncodeKey(accountId));
        }

        /// <summary>True when the newest backup is older than <paramref name="intervalMinutes"/> (or there is none).</summary>
        public bool IsDue(string accountId, DateTime nowUtc, long intervalMinutes)
        {
            DateTime last;
            bool known;
            lock (gate) known = lastBackup.TryGetValue(accountId, out last);
            if (!known)
            {
                List<BackupInfo> all = List(accountId);
                if (all.Count == 0) return true;
                last = all[0].DateUtc;
                lock (gate) lastBackup[accountId] = last;
            }
            return (nowUtc - last).TotalMinutes >= intervalMinutes;
        }

        /// <summary>Writes a backup of <paramref name="fileText"/> and prunes to <paramref name="keep"/>.</summary>
        public BackupInfo Create(string accountId, string fileText, long revision, string reason, DateTime nowUtc, int keep)
        {
            string dir = DirectoryFor(accountId);
            Directory.CreateDirectory(dir);
            string stem = nowUtc.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture)
                          + "-r" + revision.ToString(CultureInfo.InvariantCulture) + "-" + SafeReason(reason);
            string path = System.IO.Path.Combine(dir, stem + Extension);
            for (int n = 2; File.Exists(path); n++)
                path = System.IO.Path.Combine(dir, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + Extension);
            AtomicFile.WriteAllText(path, fileText);
            lock (gate) lastBackup[accountId] = nowUtc;
            Prune(accountId, keep);
            return Describe(path);
        }

        /// <summary>All backups of an account, newest first.</summary>
        public List<BackupInfo> List(string accountId)
        {
            var result = new List<BackupInfo>();
            string dir = DirectoryFor(accountId);
            if (!Directory.Exists(dir)) return result;
            foreach (string file in Directory.GetFiles(dir, "*" + Extension))
            {
                BackupInfo info = Describe(file);
                if (info != null) result.Add(info);
            }
            result.Sort((a, b) => string.CompareOrdinal(b.Id, a.Id));
            return result;
        }

        /// <summary>The backup's file text, or null for an unknown or malformed id.</summary>
        public string Read(string accountId, string id)
        {
            if (!IsSafeId(id)) return null;
            string path = System.IO.Path.Combine(DirectoryFor(accountId), id + Extension);
            return AtomicFile.ReadAllTextOrNull(path);
        }

        public void Prune(string accountId, int keep)
        {
            List<BackupInfo> all = List(accountId);
            for (int i = Num.Max(keep, 0); i < all.Count; i++)
            {
                try
                {
                    File.Delete(all[i].Path);
                }
                catch (Exception)
                {
                    // best effort: an undeletable old backup only costs space
                }
            }
        }

        /// <summary>Backup ids are our own file stems: [0-9A-Za-z-] only (no path tricks through the URL).</summary>
        public static bool IsSafeId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (char c in id)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        private static string SafeReason(string reason)
        {
            var sb = new StringBuilder();
            foreach (char c in reason ?? "")
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.Length == 0 ? "backup" : sb.ToString();
        }

        private static BackupInfo Describe(string path)
        {
            string id = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!IsSafeId(id)) return null;
            string[] parts = id.Split('-');
            DateTime date;
            if (parts.Length < 3 || !TryParseStamp(parts[0], out date)) return null;
            long revision = 0;
            if (parts[1].Length > 1 && parts[1][0] == 'r')
                long.TryParse(parts[1].Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out revision);
            var info = new BackupInfo();
            info.Id = id;
            info.DateUtc = date;
            info.Revision = revision;
            info.Reason = parts[2];
            info.Path = path;
            try
            {
                info.Size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                info.Size = 0;
            }
            return info;
        }

        /// <summary>Parses "yyyyMMddTHHmmssfffZ" by hand (no culture or ParseExact surprises on the device).</summary>
        private static bool TryParseStamp(string s, out DateTime utc)
        {
            utc = default(DateTime);
            if (s.Length != 19 || s[8] != 'T' || s[18] != 'Z') return false;
            int year, month, day, hour, minute, second, millis;
            if (!Digits(s, 0, 4, out year) || !Digits(s, 4, 2, out month) || !Digits(s, 6, 2, out day)
                || !Digits(s, 9, 2, out hour) || !Digits(s, 11, 2, out minute) || !Digits(s, 13, 2, out second)
                || !Digits(s, 15, 3, out millis))
                return false;
            try
            {
                // (the trimmed mscorlib has no (y, m, d, h, m, s, ms, kind) constructor)
                utc = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddTicks(millis * 10000L);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool Digits(string s, int start, int count, out int value)
        {
            value = 0;
            for (int i = start; i < start + count; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }
    }
}
