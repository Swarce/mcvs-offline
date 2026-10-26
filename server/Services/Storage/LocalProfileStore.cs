using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Services.Storage
{
    /// <summary>
    /// The profiles of this device: one file per account in &lt;data&gt;/storage/profiles/ (format:
    /// <see cref="ProfileFile"/>), backups in &lt;data&gt;/storage/backups/.
    /// <para>
    /// Consistency rules (verify-C3 §4, §7.1): every operation on an account runs under that account's FIFO lock,
    /// so requests are applied in arrival order and a read sees every write that arrived before it; a write is on
    /// disk (temp file + fsync + rename, <see cref="AtomicFile"/>) before it is answered, so a crash leaves the old
    /// or the new file, never a partial one.
    /// </para>
    /// <para>
    /// Parsed documents are cached per account and validated against the file's length and last-write time on
    /// every request, so a hand edit made while the server runs is still picked up. Each request works on its own
    /// copy (shared immutable leaves: the dump is never copied or re-parsed), which keeps reads cheap even with a
    /// large game profile. <c>storage.cache: false</c> turns the cache off.
    /// </para>
    /// <para>
    /// A file that exists but cannot be read is never reported as missing (that would make the game reset the
    /// profile, verify-C3 §3.1): it is replaced by its newest readable backup when possible, otherwise the request
    /// fails with 503 so the game retries and the file can be repaired.
    /// </para>
    /// </summary>
    public sealed class LocalProfileStore : IProfileStore
    {
        /// <summary>Published after every saved change: {account, fedId, revision, operation, selector, deleted, source}.</summary>
        public const string ChangedTopic = "storage.profile.changed";

        private const string FileExtension = ".json";

        /// <summary>Read-only fields synthesized for every stored document, in StandardProfile order.</summary>
        private static readonly string[] SynthesizedFields = { "credential", "created", "modified", "datacenter", "last_session" };

        private readonly FairKeyedLock locks = new FairKeyedLock();
        private readonly IClock clock;
        private readonly Log log;
        private readonly StorageSettings settings;
        private readonly AccountStore accounts;
        private readonly EventHub events;
        private readonly ProfileBackups backups;
        private readonly HashSet<string> reportedWarnings = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, CachedDocument> cache = new Dictionary<string, CachedDocument>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> backupLevels = new Dictionary<string, long>(StringComparer.Ordinal);

        internal LocalProfileStore(string rootDirectory, IClock clock, Log log, StorageSettings settings,
                                   AccountStore accounts, EventHub events)
        {
            if (string.IsNullOrEmpty(rootDirectory)) throw new ArgumentException("root directory required", "rootDirectory");
            RootDirectory = Path.GetFullPath(rootDirectory);
            ProfilesDirectory = Path.Combine(RootDirectory, "profiles");
            Directory.CreateDirectory(ProfilesDirectory);
            AtomicFile.DeleteLeftovers(ProfilesDirectory);
            this.clock = clock ?? SystemClock.Instance;
            this.log = log ?? Log.Null;
            this.settings = settings ?? new StorageSettings(null);
            this.accounts = accounts;
            this.events = events;
            backups = new ProfileBackups(Path.Combine(RootDirectory, "backups"));
        }

        public string Name { get { return "local"; } }

        /// <summary>&lt;data&gt;/storage.</summary>
        public string RootDirectory { get; private set; }

        /// <summary>&lt;data&gt;/storage/profiles.</summary>
        public string ProfilesDirectory { get; private set; }

        internal FairKeyedLock Locks { get { return locks; } }

        internal ProfileBackups Backups { get { return backups; } }

        public string PathFor(string accountId)
        {
            return Path.Combine(ProfilesDirectory, DocumentStore.EncodeKey(accountId) + FileExtension);
        }

        /// <summary>Accounts that have a stored profile.</summary>
        public List<string> AccountIds()
        {
            var ids = new List<string>();
            foreach (string file in Directory.GetFiles(ProfilesDirectory, "*" + FileExtension))
            {
                string name = Path.GetFileName(file);
                ids.Add(DocumentStore.DecodeKey(name.Substring(0, name.Length - FileExtension.Length)));
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        // ------------------------------------------------------------------ IProfileStore

        public StorageResult Get(StorageTarget target, string selector, IList<string> includeFields)
        {
            string[] path;
            if (!ProfileJson.TryParsePath(selector, out path))
                return StorageResult.Error(HttpStatus.BadRequest, "malformed selector '" + selector + "'");

            ProfileDocument doc = ReadDocument(target.AccountId);
            Dictionary<string, object> view = BuildView(doc);
            bool project = includeFields != null && includeFields.Count > 0;

            if (path.Length == 0)
                return StorageResult.Ok(ProfileJson.Serialize(project ? ProfileJson.Project(view, includeFields) : view));

            object node;
            if (!ProfileJson.TryGet(view, path, out node)) return AbsentField(path);
            if (project && ProfileJson.IsObjectNode(node))
                return StorageResult.Ok(ProfileJson.Serialize(ProfileJson.Project(node, includeFields)));
            return StorageResult.Ok(ProfileJson.ResponseText(node));
        }

        public StorageResult Write(StorageTarget target, StorageWrite write)
        {
            WriteOutcome outcome = Commit(target.AccountId, write.EffectiveOperation, write.Selector, "http",
                                          doc => SeshatOperations.Apply(doc, write));
            if (outcome.Error != null) return StorageResult.Error(HttpStatus.BadRequest, outcome.Error);
            return StorageResult.OkEmpty();
        }

        public StorageResult DeleteProfile(StorageTarget target)
        {
            Commit(target.AccountId, "delete_profile", "", "http", doc =>
            {
                var outcome = new WriteOutcome();
                outcome.DeletesDocument = true;
                outcome.BackupReason = "delete";
                return outcome;
            });
            return StorageResult.OkEmpty();
        }

        public StorageResult ListBackups(StorageTarget target, DateTime? fromUtc, DateTime? toUtc)
        {
            var list = new List<object>();
            using (locks.Acquire(target.AccountId))
            {
                List<BackupInfo> all = backups.List(target.AccountId);
                for (int i = 0; i < all.Count; i++)
                {
                    BackupInfo b = all[i];
                    if (fromUtc.HasValue && b.DateUtc < fromUtc.Value) continue;
                    if (toUtc.HasValue && b.DateUtc > toUtc.Value) continue;
                    // Shape read by OP/StorageService.cs ProfileBackup: level, size, totalSpend, date, id, index.
                    var item = ProfileJson.NewObject();
                    item["id"] = b.Id;
                    item["date"] = TimeFormat.Iso8601(b.DateUtc);
                    item["level"] = BackupLevel(b);
                    item["size"] = b.Size;
                    item["totalSpend"] = 0L;
                    item["index"] = i.ToString(CultureInfo.InvariantCulture);
                    item["revision"] = b.Revision;
                    item["reason"] = b.Reason;
                    list.Add(item);
                }
            }
            return StorageResult.Ok(MiniJson.Serialize(list));
        }

        public StorageResult GetBackup(StorageTarget target, string backupId)
        {
            string text;
            using (locks.Acquire(target.AccountId)) text = backups.Read(target.AccountId, backupId);
            if (text == null)
            {
                // GetValidUpd10Profile reads fields of this object inside a try/catch; {} is safer than a 404, which
                // would abort the whole profile load (GM/ProfileManagerComponent.cs:131-160).
                log.Warn("backup '" + backupId + "' of " + target.AccountId + " not found; answering {}");
                return StorageResult.OkEmpty();
            }
            try
            {
                ProfileDocument doc = ProfileFile.Parse(text, target.AccountId, null);
                return StorageResult.Ok(ProfileJson.Serialize(BuildView(doc)));
            }
            catch (ProfileFileException ex)
            {
                log.Warn("backup '" + backupId + "' of " + target.AccountId + " is unreadable; answering {}", ex);
                return StorageResult.OkEmpty();
            }
        }

        public StorageResult GetVisibility(StorageTarget target)
        {
            ProfileDocument doc = ReadDocument(target.AccountId);
            return StorageResult.Ok(MiniJson.Serialize(doc.Visibility ?? ProfileJson.NewObject()));
        }

        public StorageResult SetVisibility(StorageTarget target, string selector, string visibility)
        {
            WriteOutcome outcome = Commit(target.AccountId, "visibility", selector, "http",
                                          doc => SeshatOperations.ApplyVisibility(doc, selector, visibility));
            if (outcome.Error != null) return StorageResult.Error(HttpStatus.BadRequest, outcome.Error);
            return StorageResult.OkEmpty();
        }

        // ------------------------------------------------------------------ in-process API

        /// <summary>The account's document as stored now (a private copy; IsStored is false when there is none).</summary>
        public ProfileDocument ReadDocument(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", "accountId");
            using (locks.Acquire(accountId))
            {
                return LoadUnlocked(accountId);
            }
        }

        /// <summary>
        /// Runs <paramref name="apply"/> on the account's document under its lock and saves the result
        /// (with backups and the change event) according to the returned outcome.
        /// </summary>
        public WriteOutcome Commit(string accountId, string operation, string selector, string source,
                                   Func<ProfileDocument, WriteOutcome> apply)
        {
            return Commit(accountId, operation, selector, source, apply, true);
        }

        /// <summary>
        /// As <see cref="Commit(string, string, string, string, Func{ProfileDocument, WriteOutcome})"/>; with
        /// <paramref name="allowAutoBackup"/> false no <c>auto</c> backup is taken (the settings page's staged operations
        /// take their own <c>system</c> backup first).
        /// </summary>
        internal WriteOutcome Commit(string accountId, string operation, string selector, string source,
                                     Func<ProfileDocument, WriteOutcome> apply, bool allowAutoBackup)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", "accountId");
            if (apply == null) throw new ArgumentNullException("apply");
            Dictionary<string, object> change = null;
            WriteOutcome outcome;
            using (locks.Acquire(accountId))
            {
                ProfileDocument doc = LoadUnlocked(accountId);
                bool existed = doc.IsStored;
                bool hadGameProfile = SeshatOperations.HasGameProfile(doc);
                outcome = apply(doc) ?? new WriteOutcome();
                foreach (string note in outcome.Notes) log.Debug(accountId + " " + operation + " '" + selector + "': " + note);
                if (outcome.Error != null || outcome.IsUnknownOperation) return outcome;

                try
                {
                    if (outcome.DeletesDocument)
                    {
                        if (!existed) return outcome; // nothing stored: nothing to delete
                        Backup(accountId, doc.Revision, outcome.BackupReason ?? "delete");
                        File.Delete(PathFor(accountId));
                        Forget(accountId);
                        log.Warn("profile of " + accountId + " deleted (" + operation + "); a backup was kept");
                        change = ChangePayload(accountId, doc.Revision, operation, selector, true, source);
                    }
                    else if (outcome.Changed)
                    {
                        string reason = outcome.BackupReason;
                        if (reason == null && hadGameProfile && !SeshatOperations.HasGameProfile(doc))
                        {
                            reason = "wipe";
                            log.Warn("profile of " + accountId + ": " + operation + " '" + selector
                                     + "' clears the stored game profile; the previous state was backed up");
                        }
                        if (existed)
                        {
                            if (reason != null) Backup(accountId, doc.Revision, reason);
                            else if (allowAutoBackup && settings.BackupsKeep > 0
                                     && backups.IsDue(accountId, clock.UtcNow, settings.BackupIntervalMinutes))
                                Backup(accountId, doc.Revision, "auto");
                        }
                        string now = TimeFormat.Iso8601(clock.UtcNow);
                        doc.Revision = doc.Revision + 1;
                        doc.Modified = now;
                        if (string.IsNullOrEmpty(doc.Created)) doc.Created = now;
                        doc.IsStored = true;
                        AtomicFile.WriteAllText(PathFor(accountId), ProfileFile.Serialize(doc));
                        RememberSaved(accountId, doc);
                        change = ChangePayload(accountId, doc.Revision, operation, selector, false, source);
                    }
                }
                catch (IOException ex)
                {
                    Forget(accountId);
                    // Disk full, storage removed...: a 5xx makes the game retry (report 02 §1.6); never pretend success.
                    throw new StorageUnavailableException("cannot save the profile of " + accountId + ": " + ex.Message, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Forget(accountId);
                    throw new StorageUnavailableException("cannot save the profile of " + accountId + ": " + ex.Message, ex);
                }
            }
            if (change != null && events != null) events.Publish(ChangedTopic, change);
            return outcome;
        }

        // ------------------------------------------------------------------ backups for the settings page (design §6.3)

        /// <summary>
        /// Copies the account's current file into its backups, whatever <c>storage.backups.keep</c> says. Reasons:
        /// <c>manual</c> (the player's own; an 11th throws <see cref="ManualBackupLimitException"/>), <c>preimport</c>,
        /// <c>prerestore</c>, <c>presettings</c>, <c>prerevert</c>. Null when the account has no stored profile.
        /// </summary>
        public BackupInfo CreateBackup(string accountId, string reason)
        {
            string copied;
            return CreateBackup(accountId, reason, out copied);
        }

        /// <summary>As <see cref="CreateBackup(string, string)"/>, with the file text that was copied.</summary>
        internal BackupInfo CreateBackup(string accountId, string reason, out string copiedText)
        {
            copiedText = null;
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("account id required", "accountId");
            if (reason != ProfileBackups.PoolManual && Array.IndexOf(ProfileBackups.SystemReasons, reason) < 0)
                throw new ArgumentException("not a backup reason of the settings page: '" + reason + "'", "reason");
            using (locks.Acquire(accountId))
            {
                ProfileDocument doc = LoadUnlocked(accountId);
                if (!doc.IsStored) return null;
                string fileText = AtomicFile.ReadAllTextOrNull(PathFor(accountId));
                if (fileText == null) return null;
                BackupInfo info = backups.Create(accountId, fileText, doc.Revision, reason, clock.UtcNow, settings.BackupsKeep);
                copiedText = fileText;
                log.Info("backup " + info.Id + " of " + accountId);
                return info;
            }
        }

        /// <summary>Deletes one of the player's own (manual) backups. False for an unknown id or any other pool (design I4).</summary>
        public bool DeleteBackup(string accountId, string id)
        {
            if (string.IsNullOrEmpty(accountId)) return false;
            using (locks.Acquire(accountId))
            {
                BackupInfo info = backups.Find(accountId, id);
                if (info == null || info.Pool != ProfileBackups.PoolManual) return false;
                if (!backups.Delete(accountId, id)) return false;
                log.Info("backup " + id + " of " + accountId + " deleted by the player");
                return true;
            }
        }

        /// <summary>The text of one backup file, or null for an unknown or malformed id.</summary>
        public string ReadBackupText(string accountId, string id)
        {
            if (string.IsNullOrEmpty(accountId)) return null;
            using (locks.Acquire(accountId)) return backups.Read(accountId, id);
        }

        /// <summary>
        /// The player level in a backup (the profile root's <c>level</c>), 0 when absent. Backups never change, so the
        /// value is remembered per file; the file is scanned, not parsed (a backup is up to ~1.4 MB).
        /// </summary>
        public long BackupLevel(BackupInfo backup)
        {
            if (backup == null || backup.Path == null) return 0;
            string key = backup.Path + "|" + backup.Size.ToString(CultureInfo.InvariantCulture);
            long level;
            lock (backupLevels)
            {
                if (backupLevels.TryGetValue(key, out level)) return level;
            }
            string text;
            try
            {
                text = AtomicFile.ReadAllTextOrNull(backup.Path);
            }
            catch (IOException)
            {
                return 0;
            }
            level = LevelOfFileText(text);
            lock (backupLevels)
            {
                if (backupLevels.Count > 1000) backupLevels.Clear();
                backupLevels[key] = level;
            }
            return level;
        }

        /// <summary>The root <c>level</c> of a profile file's text ("profile.level", or "level" in a plain document), or 0.</summary>
        internal static long LevelOfFileText(string text)
        {
            if (text == null) return 0;
            foreach (string[] path in LevelPaths)
            {
                int start, length;
                if (!JsonSpanScanner.TryFindValue(text, path, out start, out length) || length > 64) continue;
                object value;
                if (MiniJson.TryParse(text.Substring(start, length), out value)) return JsonValue.AsLong(value) ?? 0;
            }
            return 0;
        }

        private static readonly string[][] LevelPaths = { new[] { "profile", "level" }, new[] { "level" } };

        /// <summary>
        /// The document as the game sees it: for a stored document, the server-maintained read-only fields
        /// (credential, created, modified, datacenter, last_session) followed by the stored fields; {} for an account
        /// that has never saved (a new account must read as 200 {}, verify-C3 §3.2). Shares nodes with
        /// <paramref name="doc"/>: read-only use.
        /// </summary>
        public Dictionary<string, object> BuildView(ProfileDocument doc)
        {
            var view = ProfileJson.NewObject();
            if (doc == null || !doc.IsStored) return view;
            Dictionary<string, object> root = doc.Root ?? ProfileJson.NewObject();
            var synthesized = ProfileJson.NewObject();
            synthesized["credential"] = PrimaryCredential(doc.AccountId);
            synthesized["created"] = doc.Created ?? doc.Modified;
            synthesized["modified"] = doc.Modified;
            synthesized["datacenter"] = settings.DataCenter;
            synthesized["last_session"] = doc.Modified;
            foreach (string key in SynthesizedFields)
            {
                object value = synthesized[key];
                object stored;
                if (value == null && root.TryGetValue(key, out stored)) value = stored; // e.g. an imported document
                if (value != null) view[key] = value;
            }
            foreach (var kv in root)
            {
                if (Array.IndexOf(SynthesizedFields, kv.Key) < 0) view[kv.Key] = kv.Value;
            }
            return view;
        }

        // ------------------------------------------------------------------ internals

        private string PrimaryCredential(string accountId)
        {
            Account account = accounts == null ? null : accounts.FindById(accountId);
            if (account != null && account.Credentials.Count > 0) return account.Credentials[0];
            return McvsConstants.FedIdPrefix + accountId;
        }

        private StorageResult AbsentField(string[] path)
        {
            if (settings.AbsentFieldIsNotFound && !IsCriticalSelector(path))
                return StorageResult.Error(HttpStatus.NotFound, "no value at '" + ProfileJson.FormatPath(path) + "'");
            // verify-C3 §3.1/§3.4: {} for a missing _mcvs.gameProfile (new profile without the R7 reset) and,
            // required, for _mcvs.currentMatch (a 404 leaves the GS with a null accessor and an NRE).
            return StorageResult.OkEmpty();
        }

        /// <summary>_mcvs.gameProfile, _mcvs.currentMatch and the object sections: always {} when absent.</summary>
        private static bool IsCriticalSelector(string[] path)
        {
            if (path.Length == 2 && string.Equals(path[0], McvsConstants.GameProfileSelector, StringComparison.Ordinal)
                && (path[1] == "gameProfile" || path[1] == "currentMatch"))
                return true;
            return SeshatOperations.IsObjectSection(path);
        }

        private ProfileDocument LoadUnlocked(string accountId)
        {
            string path = PathFor(accountId);
            try
            {
                FileStamp stamp;
                if (!FileStamp.TryRead(path, out stamp))
                {
                    Forget(accountId);
                    return new ProfileDocument(accountId);
                }
                ProfileDocument cached = Recall(accountId, stamp);
                if (cached != null) return cached;

                string text = AtomicFile.ReadAllTextOrNull(path);
                if (text == null)
                {
                    Forget(accountId);
                    return new ProfileDocument(accountId);
                }
                ProfileDocument doc;
                try
                {
                    doc = Parse(text, accountId, path);
                }
                catch (ProfileFileException ex)
                {
                    log.Error("profile file " + path + " is unreadable: " + ex.Message);
                    doc = RestoreFromBackup(accountId, path);
                    if (doc == null)
                    {
                        Forget(accountId);
                        throw new StorageUnavailableException("the profile of " + accountId + " exists but cannot be read (" + path
                                                              + "); fix or remove the file, or restore a backup from "
                                                              + backups.DirectoryFor(accountId), ex);
                    }
                    if (!FileStamp.TryRead(path, out stamp)) return doc;
                }
                // The stamp was taken before reading: if the file changed in between, the next stat differs and the
                // document is simply parsed again.
                Remember(accountId, stamp, doc);
                return doc;
            }
            catch (IOException ex)
            {
                throw new StorageUnavailableException("cannot read " + path + ": " + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new StorageUnavailableException("cannot read " + path + ": " + ex.Message, ex);
            }
        }

        /// <summary>A private copy of the cached document if the file is unchanged since it was cached, else null.</summary>
        private ProfileDocument Recall(string accountId, FileStamp stamp)
        {
            if (!settings.CacheEnabled) return null;
            CachedDocument entry;
            lock (cache)
            {
                if (!cache.TryGetValue(accountId, out entry) || !entry.Stamp.Equals(stamp)) return null;
            }
            return CloneDocument(entry.Document);
        }

        private void Remember(string accountId, FileStamp stamp, ProfileDocument doc)
        {
            if (!settings.CacheEnabled)
            {
                Forget(accountId);
                return;
            }
            var entry = new CachedDocument { Stamp = stamp, Document = CloneDocument(doc) };
            lock (cache) cache[accountId] = entry;
        }

        private void RememberSaved(string accountId, ProfileDocument doc)
        {
            FileStamp stamp;
            if (FileStamp.TryRead(PathFor(accountId), out stamp)) Remember(accountId, stamp, doc);
            else Forget(accountId);
        }

        private void Forget(string accountId)
        {
            lock (cache) cache.Remove(accountId);
        }

        /// <summary>
        /// A copy that can be modified freely: new containers, shared immutable leaves (scalars and verbatim nodes,
        /// so the game-profile dump is never copied).
        /// </summary>
        private static ProfileDocument CloneDocument(ProfileDocument doc)
        {
            var copy = new ProfileDocument(doc.AccountId);
            copy.Revision = doc.Revision;
            copy.Created = doc.Created;
            copy.Modified = doc.Modified;
            copy.IsStored = doc.IsStored;
            copy.Visibility = ProfileJson.CloneObject(doc.Visibility ?? ProfileJson.NewObject());
            copy.Root = ProfileJson.CloneObject(doc.Root ?? ProfileJson.NewObject());
            return copy;
        }

        private ProfileDocument Parse(string text, string accountId, string path)
        {
            var warnings = new List<string>();
            ProfileDocument doc = ProfileFile.Parse(text, accountId, warnings);
            foreach (string w in warnings)
            {
                bool first;
                lock (reportedWarnings) first = reportedWarnings.Add(accountId + "|" + w);
                if (first) log.Warn(path + ": " + w);
            }
            return doc;
        }

        private ProfileDocument RestoreFromBackup(string accountId, string path)
        {
            if (!settings.AutoRestoreCorrupt) return null;
            foreach (BackupInfo b in backups.List(accountId))
            {
                string candidate = AtomicFile.ReadAllTextOrNull(b.Path);
                if (candidate == null) continue;
                ProfileDocument doc;
                try
                {
                    doc = Parse(candidate, accountId, b.Path);
                }
                catch (ProfileFileException)
                {
                    continue;
                }
                string aside = path + ".corrupt-" + TimeFormat.ToUnixMillis(clock.UtcNow).ToString(CultureInfo.InvariantCulture);
                FileSystem.Rename(path, aside);
                AtomicFile.WriteAllText(path, candidate);
                log.Error("profile of " + accountId + " restored from backup " + b.Id + "; the unreadable file was kept as " + aside);
                return doc;
            }
            return null;
        }

        /// <summary>
        /// Copies the current file (the state before the write being committed) into the backups. Every reason is
        /// backed up; <c>storage.backups.keep</c> only caps the <c>auto</c> pool (and 0 means Commit asks for none).
        /// </summary>
        private void Backup(string accountId, long revision, string reason)
        {
            try
            {
                string fileText = AtomicFile.ReadAllTextOrNull(PathFor(accountId));
                if (fileText == null) return;
                BackupInfo info = backups.Create(accountId, fileText, revision, reason, clock.UtcNow, settings.BackupsKeep);
                log.Debug("backup " + info.Id + " of " + accountId);
            }
            catch (IOException ex)
            {
                // A failed backup must not fail the save itself.
                log.Warn("cannot back up the profile of " + accountId, ex);
            }
            catch (ManualBackupLimitException ex)
            {
                log.Warn("cannot back up the profile of " + accountId + ": " + ex.Message);
            }
        }

        private sealed class CachedDocument
        {
            public FileStamp Stamp;
            public ProfileDocument Document;
        }

        /// <summary>Length and last-write time of a file: changes with every rewrite, ours or a hand edit.</summary>
        private struct FileStamp
        {
            public long Length;
            public long WriteTicks;

            public static bool TryRead(string path, out FileStamp stamp)
            {
                stamp = default(FileStamp);
                if (!File.Exists(path)) return false;
                stamp.Length = new FileInfo(path).Length;
                stamp.WriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
                return true;
            }

            public bool Equals(FileStamp other)
            {
                return Length == other.Length && WriteTicks == other.WriteTicks;
            }
        }

        private static Dictionary<string, object> ChangePayload(string accountId, long revision, string operation, string selector,
                                                                bool deleted, string source)
        {
            var payload = ProfileJson.NewObject();
            payload["account"] = accountId;
            payload["fedId"] = McvsConstants.FedIdPrefix + accountId;
            payload["revision"] = revision;
            payload["operation"] = operation;
            payload["selector"] = selector ?? "";
            payload["deleted"] = deleted;
            payload["source"] = source;
            return payload;
        }
    }
}
