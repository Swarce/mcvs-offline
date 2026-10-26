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

        /// <summary>
        /// The token of why it was taken (the third part of the id): auto, delete, replace, wipe, unlockall, preimport,
        /// prerestore, presettings, prerevert, manual...
        /// </summary>
        public string Reason { get; set; }

        /// <summary>The pool of the token (<see cref="ProfileBackups.PoolOf"/>): auto, event, system or manual.</summary>
        public string Pool { get; set; }

        /// <summary>Only the player's own (manual) backups can be deleted, and only by the player (design I4).</summary>
        public bool Deletable { get; set; }

        public long Size { get; set; }

        public string Path { get; set; }
    }

    /// <summary>
    /// Raised by <see cref="ProfileBackups.Create"/> for an 11th manual backup of an account. Nothing is written: manual
    /// backups are never deleted automatically (design I4), so the player deletes one of their own first.
    /// </summary>
    public sealed class ManualBackupLimitException : InvalidOperationException
    {
        public ManualBackupLimitException(int limit)
            : base("there are already " + limit.ToString(CultureInfo.InvariantCulture) + " manual backups")
        {
            Limit = limit;
        }

        public int Limit { get; private set; }
    }

    /// <summary>
    /// Copies of each account's profile file in &lt;data&gt;/storage/backups/&lt;account&gt;/ (report 03 §12.1), in four
    /// pools that are pruned separately (design §6.3):
    /// <list type="bullet">
    /// <item><c>auto</c>: at most every <c>storage.backups.intervalMinutes</c> before a normal save; the newest
    /// <c>storage.backups.keep</c> are kept (0: none are taken and none are pruned).</item>
    /// <item><c>event</c>: before destructive writes (profile delete, root replace, a write that clears the game profile —
    /// e.g. the R7 "_mcvs = {gameProfile:null}" reset that a 403/404 would trigger, verify-C3 §3.1 — and one-way changes
    /// such as unlock-all); newest 20.</item>
    /// <item><c>system</c>: before an import, a restore, a risky setting or an automatic revert (the settings page);
    /// newest 10.</item>
    /// <item><c>manual</c>: the player's own, at most 10, never deleted automatically.</item>
    /// </list>
    /// Ids are <c>&lt;yyyyMMddTHHmmssfffZ&gt;-r&lt;revision&gt;-&lt;token&gt;</c> where the token keeps only [a-z0-9] of the
    /// reason ("preimport", never "pre-import"). They also serve the Seshat backups endpoint. Callers hold the account lock.
    /// </summary>
    internal sealed class ProfileBackups
    {
        public const string PoolAuto = "auto";
        public const string PoolEvent = "event";
        public const string PoolSystem = "system";
        public const string PoolManual = "manual";

        public const int EventCap = 20;
        public const int SystemCap = 10;
        public const int ManualCap = 10;

        /// <summary>The reasons of the <c>system</c> pool (the settings page's backups before it changes something).</summary>
        public static readonly string[] SystemReasons = { "preimport", "prerestore", "presettings", "prerevert" };

        private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";
        private const string Extension = ".json";

        private readonly string root;
        private readonly object gate = new object();
        private readonly Dictionary<string, DateTime> lastAuto = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public ProfileBackups(string root)
        {
            this.root = root;
            Directory.CreateDirectory(root);
        }

        public string DirectoryFor(string accountId)
        {
            return System.IO.Path.Combine(root, DocumentStore.EncodeKey(accountId));
        }

        /// <summary>The pool of a backup token (the reason as it appears in the id).</summary>
        public static string PoolOf(string token)
        {
            if (token == PoolAuto) return PoolAuto;
            if (token == PoolManual) return PoolManual;
            if (Array.IndexOf(SystemReasons, token) >= 0) return PoolSystem;
            return PoolEvent;
        }

        /// <summary>How many backups of a pool are kept per account (<paramref name="autoKeep"/> for <c>auto</c>).</summary>
        public static int CapOf(string pool, int autoKeep)
        {
            switch (pool)
            {
                case PoolAuto: return Num.Max(autoKeep, 0);
                case PoolSystem: return SystemCap;
                case PoolManual: return ManualCap;
                default: return EventCap;
            }
        }

        /// <summary>True when the newest automatic backup is older than <paramref name="intervalMinutes"/> (or there is none).</summary>
        public bool IsDue(string accountId, DateTime nowUtc, long intervalMinutes)
        {
            DateTime last;
            bool known;
            lock (gate) known = lastAuto.TryGetValue(accountId, out last);
            if (!known)
            {
                BackupInfo newest = null;
                foreach (BackupInfo b in List(accountId))
                {
                    if (b.Pool != PoolAuto) continue;
                    newest = b;
                    break;
                }
                if (newest == null) return true;
                last = newest.DateUtc;
                lock (gate) lastAuto[accountId] = last;
            }
            return (nowUtc - last).TotalMinutes >= intervalMinutes;
        }

        /// <summary>
        /// Writes a backup of <paramref name="fileText"/> and prunes its pool (<see cref="CapOf"/>; an <c>auto</c> pool
        /// with <paramref name="autoKeep"/> ≤ 0 and the <c>manual</c> pool are never pruned). An 11th manual backup throws
        /// <see cref="ManualBackupLimitException"/> before anything is written. The prune never deletes
        /// <paramref name="spare"/> (the backup a staged restore copies: taking the backup before it must not delete it).
        /// </summary>
        public BackupInfo Create(string accountId, string fileText, long revision, string reason, DateTime nowUtc, int autoKeep,
                                 string spare = null)
        {
            string token = SafeReason(reason);
            string pool = PoolOf(token);
            if (pool == PoolManual && Count(accountId, PoolManual) >= ManualCap) throw new ManualBackupLimitException(ManualCap);

            string dir = DirectoryFor(accountId);
            Directory.CreateDirectory(dir);
            string stem = nowUtc.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture)
                          + "-r" + revision.ToString(CultureInfo.InvariantCulture) + "-" + token;
            string path = System.IO.Path.Combine(dir, stem + Extension);
            for (int n = 2; File.Exists(path); n++)
                path = System.IO.Path.Combine(dir, stem + "-" + n.ToString(CultureInfo.InvariantCulture) + Extension);
            AtomicFile.WriteAllText(path, fileText);
            if (pool == PoolAuto) lock (gate) lastAuto[accountId] = nowUtc;
            if (pool != PoolManual && !(pool == PoolAuto && autoKeep <= 0)) Prune(accountId, pool, CapOf(pool, autoKeep), spare);
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

        /// <summary>The backup with this id, or null for an unknown or malformed id.</summary>
        public BackupInfo Find(string accountId, string id)
        {
            if (!IsSafeId(id)) return null;
            string path = System.IO.Path.Combine(DirectoryFor(accountId), id + Extension);
            return File.Exists(path) ? Describe(path) : null;
        }

        /// <summary>The backup's file text, or null for an unknown or malformed id.</summary>
        public string Read(string accountId, string id)
        {
            if (!IsSafeId(id)) return null;
            string path = System.IO.Path.Combine(DirectoryFor(accountId), id + Extension);
            return AtomicFile.ReadAllTextOrNull(path);
        }

        /// <summary>Deletes one backup file (the caller decides whether it may). False when it does not exist.</summary>
        public bool Delete(string accountId, string id)
        {
            BackupInfo info = Find(accountId, id);
            if (info == null) return false;
            File.Delete(info.Path);
            return true;
        }

        /// <summary>
        /// Deletes the oldest backups of <paramref name="pool"/> beyond <paramref name="cap"/>; never the manual pool, never
        /// <paramref name="spare"/> (which then does not count towards the cap).
        /// </summary>
        public void Prune(string accountId, string pool, int cap, string spare = null)
        {
            if (pool == PoolManual) return;
            int kept = 0;
            foreach (BackupInfo b in List(accountId))
            {
                if (b.Pool != pool || (spare != null && b.Id == spare)) continue;
                if (kept < Num.Max(cap, 0))
                {
                    kept++;
                    continue;
                }
                try
                {
                    File.Delete(b.Path);
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

        /// <summary>The token a reason becomes in the id: only [a-z0-9] ("pre-import" → "preimport"), "backup" when empty.</summary>
        public static string SafeReason(string reason)
        {
            var sb = new StringBuilder();
            foreach (char c in reason ?? "")
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.Length == 0 ? "backup" : sb.ToString();
        }

        private int Count(string accountId, string pool)
        {
            int count = 0;
            foreach (BackupInfo b in List(accountId))
            {
                if (b.Pool == pool) count++;
            }
            return count;
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
            info.Pool = PoolOf(parts[2]);
            info.Deletable = info.Pool == PoolManual;
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
