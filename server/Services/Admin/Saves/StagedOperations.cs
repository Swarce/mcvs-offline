using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Auth;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>Which credential an operation moves, from which account to which (design §6.4 step 7, §6.7).</summary>
    internal sealed class RelinkPlan
    {
        /// <summary>The full "anonymous:&lt;name&gt;" (masked whenever it is shown or logged).</summary>
        public string Credential { get; set; }

        /// <summary>The account that owned the credential when the operation was staged.</summary>
        public string From { get; set; }

        /// <summary>The account the credential opens afterwards.</summary>
        public string To { get; set; }

        public Dictionary<string, object> ToJson(bool mask)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["credential"] = mask ? AdminApi.MaskCredential(Credential) : Credential;
            o["from"] = From;
            o["to"] = To;
            return o;
        }

        public static RelinkPlan FromJson(Dictionary<string, object> o, string defaultTo)
        {
            if (o == null) return null;
            return new RelinkPlan
            {
                Credential = JsonValue.GetString(o, "credential"),
                From = JsonValue.GetString(o, "from"),
                To = JsonValue.GetString(o, "to") ?? defaultTo,
            };
        }
    }

    /// <summary>One staged operation: the manifest of design §6.4 (pending.json).</summary>
    internal sealed class StagedOperation
    {
        public const string KindImport = "import";
        public const string KindRestore = "restore";
        public const string KindRelink = "relink";
        public const string KindUndo = "undo";

        public StagedOperation()
        {
            Done = new List<string>();
        }

        public string Id { get; set; }

        public string Kind { get; set; }

        /// <summary>replace, switch, restore-other (restores), switch (relink), undo.</summary>
        public string Plan { get; set; }

        public string Created { get; set; }

        public long Attempts { get; set; }

        /// <summary>The apply steps already carried out (account, profile, messages, relink, probation).</summary>
        public List<string> Done { get; private set; }

        public string Target { get; set; }

        public RelinkPlan Relink { get; set; }

        public string PayloadCrc32 { get; set; }

        /// <summary>The backup of <see cref="Target"/> whose text a restore or an undo writes.</summary>
        public string RestoreBackupId { get; set; }

        /// <summary>The system backup taken when it was staged, and the CRC of the text it copied.</summary>
        public string StageBackupId { get; set; }

        public string StageBackupCrc32 { get; set; }

        /// <summary>The backup of the target's state before the apply (what an undo or a revert restores).</summary>
        public string PreBackupId { get; set; }

        /// <summary>paste, file, backup, relink, undo.</summary>
        public string SourceType { get; set; }

        public string SourceName { get; set; }

        public string UndoOf { get; set; }

        public string SummaryAlias { get; set; }

        public long? SummaryLevel { get; set; }

        public long? SummaryRevision { get; set; }

        /// <summary>^yyyyMMddTHHmmssfffZ-kind(-n)?$: ids are made here, never taken from a request.</summary>
        public static bool IsId(string id)
        {
            if (id == null || id.Length < 21 || id.Length > 40 || id[8] != 'T' || id[18] != 'Z' || id[19] != '-') return false;
            for (int i = 0; i < 18; i++)
            {
                if (i == 8) continue;
                if (id[i] < '0' || id[i] > '9') return false;
            }
            for (int i = 20; i < id.Length; i++)
            {
                char c = id[i];
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
            }
            return true;
        }

        public string PayloadFile
        {
            get { return Id + ".payload.json"; }
        }

        public Dictionary<string, object> SummaryJson()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["alias"] = SummaryAlias;
            o["level"] = SummaryLevel;
            o["revision"] = SummaryRevision;
            return o;
        }

        public Dictionary<string, object> ToJson(bool mask)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["id"] = Id;
            o["kind"] = Kind;
            o["plan"] = Plan;
            o["created"] = Created;
            o["attempts"] = Attempts;
            var done = new List<object>();
            foreach (string step in Done) done.Add(step);
            o["done"] = done;
            o["target"] = Target;
            o["relink"] = Relink == null ? null : Relink.ToJson(mask);
            o["payload"] = Kind == KindImport ? PayloadFile : null;
            o["payloadCrc32"] = PayloadCrc32;
            o["restoreBackupId"] = RestoreBackupId;
            if (StageBackupId == null) o["stageBackup"] = null;
            else
            {
                var stage = new Dictionary<string, object>(StringComparer.Ordinal);
                stage["id"] = StageBackupId;
                stage["fileCrc32"] = StageBackupCrc32;
                o["stageBackup"] = stage;
            }
            o["preBackupId"] = PreBackupId;
            var source = new Dictionary<string, object>(StringComparer.Ordinal);
            source["type"] = SourceType;
            source["name"] = SourceName;
            source["undoOf"] = UndoOf;
            o["source"] = source;
            o["summary"] = SummaryJson();
            return o;
        }

        public static StagedOperation FromJson(Dictionary<string, object> o)
        {
            var op = new StagedOperation();
            op.Id = JsonValue.GetString(o, "id");
            op.Kind = JsonValue.GetString(o, "kind");
            op.Plan = JsonValue.GetString(o, "plan");
            op.Created = JsonValue.GetString(o, "created");
            op.Attempts = JsonValue.GetLong(o, "attempts", 0);
            List<object> done = JsonValue.GetArray(o, "done");
            if (done != null) foreach (object step in done) if (step is string) op.Done.Add((string)step);
            op.Target = JsonValue.GetString(o, "target");
            op.Relink = RelinkPlan.FromJson(JsonValue.GetObject(o, "relink"), op.Target);
            op.PayloadCrc32 = JsonValue.GetString(o, "payloadCrc32");
            op.RestoreBackupId = JsonValue.GetString(o, "restoreBackupId");
            Dictionary<string, object> stage = JsonValue.GetObject(o, "stageBackup");
            if (stage != null)
            {
                op.StageBackupId = JsonValue.GetString(stage, "id");
                op.StageBackupCrc32 = JsonValue.GetString(stage, "fileCrc32");
            }
            op.PreBackupId = JsonValue.GetString(o, "preBackupId");
            Dictionary<string, object> source = JsonValue.GetObject(o, "source");
            if (source != null)
            {
                op.SourceType = JsonValue.GetString(source, "type");
                op.SourceName = JsonValue.GetString(source, "name");
                op.UndoOf = JsonValue.GetString(source, "undoOf");
            }
            Dictionary<string, object> summary = JsonValue.GetObject(o, "summary");
            if (summary != null)
            {
                op.SummaryAlias = JsonValue.GetString(summary, "alias");
                op.SummaryLevel = JsonValue.AsLong(JsonValue.Get(summary, "level"));
                op.SummaryRevision = JsonValue.AsLong(JsonValue.Get(summary, "revision"));
            }
            return op;
        }
    }

    /// <summary>Raised by <see cref="StagedOperations.Stage"/> when an operation is already pending (409 staged_exists).</summary>
    internal sealed class StagedExistsException : Exception
    {
        public StagedExistsException(StagedOperation pending) : base("an operation is already staged: " + pending.Id)
        {
            Pending = pending;
        }

        public StagedOperation Pending { get; private set; }
    }

    /// <summary>
    /// Imports, restores, relinks and undos are <b>staged</b> and applied at the next start, before the port opens (design
    /// §6.4, invariant I3: the running menu server holds the current profile in memory and saves it back). Files in
    /// &lt;data&gt;/admin/staged/: pending.json (the one pending operation), &lt;id&gt;.payload.json (an import's bundle as
    /// validated), probation.json (§6.5), history/&lt;id&gt;.json (results, newest 20) and history/&lt;id&gt;/messages/
    /// (the mailboxes an import replaced).
    /// <para>
    /// <see cref="ApplyPending"/> records every step in the manifest's <c>done</c>, so a crash in the middle resumes where it
    /// stopped; after 3 attempts it gives up. The relink comes after the profile, so a failed profile write never
    /// redirects the player. It never throws: it runs inside the module registration, before the server starts.
    /// </para>
    /// </summary>
    internal sealed class StagedOperations
    {
        public const int MaxAttempts = 3;
        public const int HistoryKept = 20;

        public const string StepAccount = "account";
        public const string StepProfile = "profile";
        public const string StepMessages = "messages";
        public const string StepRelink = "relink";
        public const string StepProbation = "probation";

        private const string PendingFile = "pending.json";
        private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

        private readonly ServerContext context;
        private readonly LocalProfileStore store;
        private readonly Log log;
        private readonly string folder;
        private readonly string historyFolder;
        private readonly Probation probation;
        private readonly object gate = new object();

        public StagedOperations(ServerContext context, LocalProfileStore store, Log log)
        {
            this.context = context;
            this.store = store;
            this.log = log ?? Log.Null;
            folder = AdminFiles.Folder(context, "admin/staged");
            historyFolder = AdminFiles.Folder(context, "admin/staged/history");
            AtomicFile.DeleteLeftovers(folder);
            AtomicFile.DeleteLeftovers(historyFolder);
            probation = new Probation(folder);
        }

        private DateTime Now
        {
            get { return context.Clock.UtcNow; }
        }

        // ------------------------------------------------------------------ reading

        /// <summary>The pending operation, or null.</summary>
        public StagedOperation Pending()
        {
            lock (gate) return LoadPending();
        }

        /// <summary>The probation file's content, or null.</summary>
        public Dictionary<string, object> ProbationState()
        {
            lock (gate) return probation.Load();
        }

        /// <summary>History entries, newest first (full, with credentials: never hand these to the page unmasked).</summary>
        public List<Dictionary<string, object>> History(int max)
        {
            var result = new List<Dictionary<string, object>>();
            lock (gate)
            {
                foreach (string id in HistoryIds())
                {
                    if (result.Count >= max) break;
                    Dictionary<string, object> entry = LoadHistory(id);
                    if (entry != null) result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>The newest history entry as the page shows it (with <c>undoable</c>), or null.</summary>
        public Dictionary<string, object> Last()
        {
            lock (gate)
            {
                foreach (string id in HistoryIds())
                {
                    Dictionary<string, object> entry = LoadHistory(id);
                    if (entry == null) continue;
                    Dictionary<string, object> shown = HistoryForPage(entry);
                    string reason;
                    Dictionary<string, object> undoable = LoadPending() == null ? FindUndoable(out reason) : null;
                    shown["undoable"] = undoable != null && JsonValue.GetString(undoable, "id") == JsonValue.GetString(entry, "id");
                    return shown;
                }
            }
            return null;
        }

        /// <summary>GET status's "staged" section: {pending, probation, last} as the page sees them.</summary>
        public Dictionary<string, object> StatusSection()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            StagedOperation pending = Pending();
            o["pending"] = pending == null ? null : pending.ToJson(true);
            o["probation"] = Probation.ForPage(ProbationState());
            o["last"] = Last();
            return o;
        }

        public static Dictionary<string, object> HistoryForPage(Dictionary<string, object> entry)
        {
            var o = JsonValue.DeepCloneObject(entry);
            var relink = JsonValue.GetObject(o, "relink");
            if (relink != null) relink["credential"] = AdminApi.MaskCredential(JsonValue.GetString(relink, "credential"));
            return o;
        }

        // ------------------------------------------------------------------ staging

        /// <summary>
        /// Stages <paramref name="op"/> (Kind, Plan, Target, Relink, RestoreBackupId, source and summary set by the caller):
        /// writes the payload (imports), then pending.json, then takes a <c>system</c> backup of the target's current file
        /// (preimport/prerestore; none for a relink) so the player has a restore point before the restart. Throws
        /// <see cref="StagedExistsException"/> unless <paramref name="replace"/>, which drops the pending operation first.
        /// </summary>
        public StagedOperation Stage(StagedOperation op, string payloadText, bool replace)
        {
            lock (gate)
            {
                StagedOperation existing = LoadPending();
                if (existing != null)
                {
                    if (!replace) throw new StagedExistsException(existing);
                    DeletePending(existing);
                    log.Info("admin: staged " + existing.Kind + " " + existing.Id + " replaced");
                }
                op.Id = NewId(op.Kind);
                op.Created = TimeFormat.Iso8601Millis(Now);
                op.Attempts = 0;
                op.Done.Clear();
                op.PreBackupId = null;
                op.StageBackupId = null;
                op.StageBackupCrc32 = null;
                if (op.Kind == StagedOperation.KindImport)
                {
                    byte[] payload = AdminFiles.Utf8Bytes(payloadText);
                    AtomicFile.WriteAllBytes(Path.Combine(folder, op.PayloadFile), payload);
                    op.PayloadCrc32 = SaveBundle.Crc32Hex(payload);
                }
                SavePending(op);

                string reason = BackupReasonOf(op);
                if (reason != null)
                {
                    try
                    {
                        string copied;
                        BackupInfo info = store.CreateBackup(op.Target, reason, out copied);
                        if (info != null)
                        {
                            op.StageBackupId = info.Id;
                            op.StageBackupCrc32 = SaveBundle.Crc32Hex(AdminFiles.Utf8Bytes(copied));
                            SavePending(op);
                        }
                    }
                    catch (Exception ex)
                    {
                        // The apply takes its own backup when this one is missing.
                        log.Warn("admin: staged " + op.Kind + " " + op.Id + ": the backup before it failed", ex);
                    }
                }
                log.Info("admin: staged " + op.Kind + " " + op.Id + " for account " + op.Target
                         + (op.Relink == null ? "" : " (relink " + AdminApi.MaskCredential(op.Relink.Credential) + " from " + op.Relink.From
                                                     + (op.Relink.To == op.Target ? "" : " to " + op.Relink.To) + ")"));
                return op;
            }
        }

        /// <summary>Deletes the pending operation and its payload. False when none was pending.</summary>
        public bool Unstage()
        {
            lock (gate)
            {
                StagedOperation pending = LoadPending();
                if (pending == null) return false;
                DeletePending(pending);
                log.Info("admin: staged " + pending.Kind + " " + pending.Id + " cancelled");
                return true;
            }
        }

        /// <summary>
        /// The newest applied operation if it can still be undone (no later operation applied, not an undo itself, its
        /// backup still there), else null with the reason.
        /// </summary>
        public Dictionary<string, object> FindUndoable(out string reason)
        {
            lock (gate)
            {
                reason = "Nothing to undo.";
                foreach (string id in HistoryIds())
                {
                    Dictionary<string, object> entry = LoadHistory(id);
                    if (entry == null) continue;
                    string result = JsonValue.GetString(entry, "result");
                    if (result != "applied" && result != "confirmed") continue;
                    if (JsonValue.GetString(entry, "kind") == StagedOperation.KindUndo)
                    {
                        reason = "The last operation was an undo.";
                        return null;
                    }
                    string pre = JsonValue.GetString(entry, "preBackupId");
                    string target = JsonValue.GetString(entry, "target");
                    bool restore = pre != null && store.Backups.Find(target, pre) != null;
                    bool relinkBack = JsonValue.GetBool(entry, "relinked", false) && JsonValue.GetObject(entry, "relink") != null;
                    if (pre != null && !restore)
                    {
                        reason = "The backup from before the last operation is gone.";
                        return null;
                    }
                    if (!restore && !relinkBack)
                    {
                        reason = "The last operation changed nothing that can be undone.";
                        return null;
                    }
                    return entry;
                }
                return null;
            }
        }

        // ------------------------------------------------------------------ boot

        /// <summary>
        /// Runs at registration, before the port opens: probation first (count this start, revert when due), then the
        /// pending operation. Never throws.
        /// </summary>
        public void ApplyPending()
        {
            lock (gate)
            {
                try
                {
                    CountProbationStart();
                }
                catch (Exception ex)
                {
                    log.Error("admin: the probation check failed", ex);
                }
                try
                {
                    ApplyPendingLocked();
                }
                catch (Exception ex)
                {
                    log.Error("admin: applying the staged operation failed", ex);
                }
            }
        }

        private void ApplyPendingLocked()
        {
            StagedOperation op;
            try
            {
                op = LoadPendingOrThrow();
            }
            catch (Exception ex)
            {
                log.Error("admin: the staged operation is unreadable and was dropped (" + ex.Message + ")");
                AdminFiles.DeleteQuietly(Path.Combine(folder, PendingFile));
                return;
            }
            if (op == null) return;

            op.Attempts++;
            SavePending(op);
            if (op.Attempts > MaxAttempts)
            {
                Finish(op, "failed", "Gave up after " + MaxAttempts + " attempts.", false, null, 0);
                log.Error("admin: staged " + op.Kind + " " + op.Id + " gave up after " + MaxAttempts + " attempts (done: "
                          + string.Join(", ", op.Done.ToArray()) + ")");
                return;
            }

            int started = Environment.TickCount;
            var warnings = new List<string>();
            SaveBundle bundle = null;
            ProfileDocument document = null;
            string problem = Validate(op, out bundle, out document);
            if (problem != null)
            {
                Finish(op, "failed", problem, false, warnings, 0);
                log.Error("admin: staged " + op.Kind + " " + op.Id + " not applied: " + problem);
                return;
            }

            bool relinked = op.Done.Contains(StepRelink) && op.Relink != null;
            foreach (string step in Steps(op))
            {
                if (op.Done.Contains(step)) continue;
                try
                {
                    switch (step)
                    {
                        case StepAccount: ApplyAccount(op, bundle); break;
                        case StepProfile: ApplyProfile(op, document); break;
                        case StepMessages: ApplyMessages(op, bundle); break;
                        case StepRelink: relinked = ApplyRelink(op, warnings); break;
                        case StepProbation: ApplyProbation(op, relinked); break;
                    }
                }
                catch (Exception ex)
                {
                    log.Error("admin: staged " + op.Kind + " " + op.Id + ": step " + step + " failed (attempt " + op.Attempts
                              + " of " + MaxAttempts + "); it is retried at the next start", ex);
                    return;
                }
                op.Done.Add(step);
                SavePending(op);
            }
            int duration = Environment.TickCount - started;
            Finish(op, "applied", AppliedMessage(op), relinked, warnings, duration);
            log.Info("admin: staged " + op.Kind + " " + op.Id + " applied to " + op.Target + " in " + duration + " ms");
        }

        private static List<string> Steps(StagedOperation op)
        {
            var steps = new List<string>();
            if (op.Kind == StagedOperation.KindImport || op.Kind == StagedOperation.KindRestore) steps.Add(StepAccount);
            if (op.Kind == StagedOperation.KindImport || op.Kind == StagedOperation.KindRestore
                || (op.Kind == StagedOperation.KindUndo && op.RestoreBackupId != null))
                steps.Add(StepProfile);
            if (op.Kind == StagedOperation.KindImport) steps.Add(StepMessages);
            if (op.Relink != null) steps.Add(StepRelink);
            steps.Add(StepProbation);
            return steps;
        }

        /// <summary>Re-validates what will be written (not retryable when it fails). Null when fine.</summary>
        private string Validate(StagedOperation op, out SaveBundle bundle, out ProfileDocument document)
        {
            bundle = null;
            document = null;
            if (!SaveBundle.IsAccountId(op.Target)) return "The staged operation names no valid account.";
            if (op.Relink != null && (!SaveBundle.IsAccountId(op.Relink.From) || !SaveBundle.IsAccountId(op.Relink.To)))
                return "The staged operation's relink is damaged.";
            switch (op.Kind)
            {
                case StagedOperation.KindImport:
                {
                    string path = Path.Combine(folder, op.PayloadFile);
                    byte[] payload = AdminFiles.IsPlainFile(path) ? AdminFiles.ReadAtMost(path, SaveTransfer.MaxInflatedBytes) : null;
                    if (payload == null || SaveBundle.Crc32Hex(payload) != op.PayloadCrc32) return "The staged save is damaged or missing.";
                    bundle = SaveBundle.Parse(AdminFiles.Utf8(payload, 0, payload.Length), Now);
                    if (!bundle.IsValid) return string.Join(" ", bundle.Errors.ToArray());
                    if (bundle.AccountId != op.Target) return "The staged save belongs to another account.";
                    document = bundle.Document;
                    return null;
                }
                case StagedOperation.KindRestore:
                case StagedOperation.KindUndo:
                {
                    if (op.RestoreBackupId == null)
                        return op.Kind == StagedOperation.KindUndo && op.Relink != null ? null : "The staged operation names no backup.";
                    string text = store.ReadBackupText(op.Target, op.RestoreBackupId);
                    if (text == null) return "The backup " + op.RestoreBackupId + " is gone.";
                    List<string> problems = SaveBundle.ProfileProblems(text, op.Target, op.Kind == StagedOperation.KindRestore, null, out document);
                    return problems.Count == 0 ? null : string.Join(" ", problems.ToArray());
                }
                case StagedOperation.KindRelink:
                    if (op.Relink == null) return "The staged relink names no credential.";
                    return context.Accounts.FindById(op.Relink.To) == null ? "The account to play is gone." : null;
                default:
                    return "Unknown operation '" + op.Kind + "'.";
            }
        }

        /// <summary>Step account: the target account exists (created with that exact id), with the bundle's alias when free.</summary>
        private void ApplyAccount(StagedOperation op, SaveBundle bundle)
        {
            Account account = context.Accounts.FindById(op.Target) ?? context.Accounts.GetOrCreate(McvsConstants.FedIdPrefix + op.Target);
            if (account.Id != op.Target) throw new InvalidOperationException("the account was created as " + account.Id + ", not " + op.Target);
            string alias = bundle == null ? null : bundle.Alias;
            if (alias == null || !AliasRegistry.IsWellFormed(alias)) return;
            if (!string.IsNullOrEmpty(JsonValue.AsString(JsonValue.Get(account.Data, AliasRegistry.DataKey)))) return;
            Account owner = new AliasRegistry(context.Accounts, log).FindOwner(alias);
            if (owner != null && owner.Id != op.Target) return;
            context.Accounts.Update(op.Target, a => a.Data[AliasRegistry.DataKey] = alias);
        }

        /// <summary>
        /// Step profile: the target's current state is backed up (or the stage-time backup is reused when the file did not
        /// change since), the backup id is saved before the write, then the document is replaced.
        /// </summary>
        private void ApplyProfile(StagedOperation op, ProfileDocument document)
        {
            string current;
            using (store.Locks.Acquire(op.Target)) current = AtomicFile.ReadAllTextOrNull(store.PathFor(op.Target));
            if (current != null)
            {
                bool unchanged = op.StageBackupId != null && SaveBundle.Crc32Hex(AdminFiles.Utf8Bytes(current)) == op.StageBackupCrc32
                                 && store.Backups.Find(op.Target, op.StageBackupId) != null;
                if (unchanged)
                {
                    if (op.PreBackupId == null) op.PreBackupId = op.StageBackupId;
                }
                else
                {
                    // The TS saved after staging (or a retry after a crash): back up what is there now. The first backup
                    // stays the operation's pre-backup.
                    BackupInfo info = store.CreateBackup(op.Target, BackupReasonOf(op) ?? "prerestore");
                    if (op.PreBackupId == null && info != null) op.PreBackupId = info.Id;
                }
                SavePending(op);
            }
            store.Commit(op.Target, "admin." + op.Kind, "", "admin", doc => Replace(doc, document), false);
        }

        /// <summary>Replaces a stored document's content (the revision only grows; Commit adds one).</summary>
        internal static WriteOutcome Replace(ProfileDocument doc, ProfileDocument parsed)
        {
            doc.Root = ProfileJson.CloneObject(parsed.Root ?? ProfileJson.NewObject());
            doc.Visibility = ProfileJson.CloneObject(parsed.Visibility ?? ProfileJson.NewObject());
            if (!string.IsNullOrEmpty(parsed.Created)) doc.Created = parsed.Created;
            if (parsed.Revision > doc.Revision) doc.Revision = parsed.Revision;   // (no Math.Max(long, long) in the trimmed BCL)
            return new WriteOutcome { Changed = true };
        }

        /// <summary>Step messages: each mailbox of the bundle replaces the target's; the replaced one is kept in history.</summary>
        private void ApplyMessages(StagedOperation op, SaveBundle bundle)
        {
            DocumentStore messages = context.OpenStore(SaveBundle.MessagesStore);
            foreach (var kv in bundle.Messages)
            {
                string key = SaveBundle.MessageKey(op.Target, kv.Key);
                using (messages.Lock(key))
                {
                    string path = messages.PathFor(key);
                    string existing = AtomicFile.ReadAllTextOrNull(path);
                    if (existing != null)
                    {
                        string dir = Path.Combine(historyFolder, op.Id);
                        Directory.CreateDirectory(Path.Combine(dir, "messages"));
                        // CreateNew: a retry never overwrites the first copy.
                        AdminFiles.TryCreateNew(Path.Combine(dir, "messages", kv.Key + ".json"), AdminFiles.Utf8Bytes(existing));
                    }
                    AtomicFile.WriteAllText(path, kv.Value);
                }
            }
        }

        /// <summary>Step relink: moves the credential when it is still where it was at staging. True when it moved (or already had).</summary>
        private bool ApplyRelink(StagedOperation op, List<string> warnings)
        {
            RelinkPlan relink = op.Relink;
            if (relink.Credential == null || !relink.Credential.StartsWith("anonymous:", StringComparison.Ordinal))
            {
                warnings.Add("Only an anonymous login can be moved; not relinked.");
                return false;
            }
            Account owner = context.Accounts.Find(relink.Credential);
            if (owner != null && owner.Id == relink.To) return true;
            if (owner == null || owner.Id != relink.From)
            {
                warnings.Add("The phone's login changed since staging; not relinked.");
                log.Warn("admin: staged " + op.Kind + " " + op.Id + ": " + AdminApi.MaskCredential(relink.Credential)
                         + " no longer opens " + relink.From + "; not relinked");
                return false;
            }
            if (!context.Accounts.LinkCredential(relink.To, relink.Credential)) throw new InvalidOperationException("account " + relink.To + " is gone");
            context.Tokens.RevokeCredential(relink.Credential);
            log.Info("admin: credential " + AdminApi.MaskCredential(relink.Credential) + " now opens account " + relink.To + " (was " + relink.From + ")");
            return true;
        }

        private void ApplyProbation(StagedOperation op, bool relinked)
        {
            Dictionary<string, object> previous = probation.Load();
            string previousId = previous == null ? null : JsonValue.GetString(previous, "id");
            if (previousId != null && previousId != op.Id)
            {
                UpdateHistory(previousId, entry =>
                {
                    entry["result"] = "superseded";
                    entry["message"] = "Superseded by " + op.Kind + " " + op.Id + " before the game loaded it.";
                });
            }
            probation.Save(Probation.For(op, relinked, Now));
        }

        // ------------------------------------------------------------------ probation (design §6.5)

        private void CountProbationStart()
        {
            Dictionary<string, object> p = probation.Load();
            if (p == null) return;
            bool due = Probation.CountStart(p, Now);
            probation.Save(p);
            if (due) Revert(p);
        }

        /// <summary>The automatic revert: the pre-operation state of the target and the credential's previous account.</summary>
        private void Revert(Dictionary<string, object> p)
        {
            string id = JsonValue.GetString(p, "id");
            string target = JsonValue.GetString(p, "target");
            string pre = JsonValue.GetString(p, "preBackupId");
            var notes = new List<string>();
            if (pre != null)
            {
                string text = store.ReadBackupText(target, pre);
                ProfileDocument doc = null;
                if (text == null || SaveBundle.ProfileProblems(text, target, false, null, out doc).Count > 0)
                {
                    notes.Add("the backup from before it is gone or unreadable, so the save was not restored");
                }
                else
                {
                    store.CreateBackup(target, "prerevert");
                    store.Commit(target, "admin.revert", "", "admin", d => Replace(d, doc), false);
                }
            }
            RelinkPlan relink = RelinkPlan.FromJson(JsonValue.GetObject(p, "relink"), target);
            if (relink != null && relink.Credential != null)
            {
                Account owner = context.Accounts.Find(relink.Credential);
                if (owner != null && owner.Id == relink.To && context.Accounts.FindById(relink.From) != null)
                {
                    context.Accounts.LinkCredential(relink.From, relink.Credential);
                    context.Tokens.RevokeCredential(relink.Credential);
                    log.Info("admin: credential " + AdminApi.MaskCredential(relink.Credential) + " opens account " + relink.From + " again");
                }
            }
            string message = "Reverted automatically: the game did not load this save in " + Probation.RevertAtStarts + " starts."
                             + (notes.Count == 0 ? "" : " (" + string.Join("; ", notes.ToArray()) + ")");
            UpdateHistory(id, entry =>
            {
                entry["result"] = "reverted";
                entry["revertedAt"] = TimeFormat.Iso8601Millis(Now);
                entry["message"] = message;
            });
            probation.Delete();
            log.Warn("admin: " + id + " reverted: the game did not load this save in " + Probation.RevertAtStarts + " starts");
        }

        /// <summary>A save of the game (source http) of the target confirms the probation.</summary>
        public void OnProfileChanged(object payload)
        {
            var change = payload as Dictionary<string, object>;
            if (change == null || JsonValue.GetString(change, "source") != "http") return;
            string account = JsonValue.GetString(change, "account");
            if (account == null) return;
            try
            {
                lock (gate)
                {
                    Dictionary<string, object> p = probation.Load();
                    if (p == null || JsonValue.GetString(p, "target") != account) return;
                    string id = JsonValue.GetString(p, "id");
                    probation.Delete();
                    UpdateHistory(id, entry =>
                    {
                        entry["result"] = "confirmed";
                        entry["confirmedAt"] = TimeFormat.Iso8601Millis(Now);
                    });
                    log.Info("admin: " + id + " confirmed: the game saved " + account);
                }
            }
            catch (Exception ex)
            {
                // Runs on the thread of the game's save: never fail it.
                log.Warn("admin: confirming the probation failed", ex);
            }
        }

        // ------------------------------------------------------------------ files

        private StagedOperation LoadPending()
        {
            try
            {
                return LoadPendingOrThrow();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private StagedOperation LoadPendingOrThrow()
        {
            string path = Path.Combine(folder, PendingFile);
            if (!AdminFiles.IsPlainFile(path)) return null;
            StagedOperation op = StagedOperation.FromJson(MiniJson.ParseObject(AtomicFile.ReadAllTextOrNull(path)));
            if (!StagedOperation.IsId(op.Id)) throw new FormatException("the manifest has no valid id");
            return op;
        }

        private void SavePending(StagedOperation op)
        {
            AtomicFile.WriteAllText(Path.Combine(folder, PendingFile), MiniJson.Serialize(op.ToJson(false), true) + "\n");
        }

        private void DeletePending(StagedOperation op)
        {
            AdminFiles.DeleteQuietly(Path.Combine(folder, PendingFile));
            if (StagedOperation.IsId(op.Id)) AdminFiles.DeleteQuietly(Path.Combine(folder, op.PayloadFile));
        }

        /// <summary>Writes the history entry, deletes pending.json and the payload, prunes the history.</summary>
        private void Finish(StagedOperation op, string result, string message, bool relinked, List<string> warnings, int durationMs)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["id"] = op.Id;
            entry["kind"] = op.Kind;
            entry["plan"] = op.Plan;
            entry["result"] = result;
            entry["at"] = TimeFormat.Iso8601Millis(Now);
            entry["target"] = op.Target;
            entry["preBackupId"] = op.PreBackupId;
            entry["relinked"] = relinked;
            entry["relink"] = op.Relink == null ? null : op.Relink.ToJson(false);
            var done = new List<object>();
            foreach (string step in op.Done) done.Add(step);
            entry["done"] = done;
            var warningList = new List<object>();
            if (warnings != null) foreach (string w in warnings) warningList.Add(w);
            entry["warnings"] = warningList;
            entry["attempts"] = op.Attempts;
            entry["durationMs"] = (long)durationMs;
            entry["message"] = message + (warnings != null && warnings.Count > 0 ? " " + string.Join(" ", warnings.ToArray()) : "");
            entry["summary"] = op.SummaryJson();
            entry["source"] = op.SourceType;
            entry["undoOf"] = op.UndoOf;
            AtomicFile.WriteAllText(Path.Combine(historyFolder, op.Id + ".json"), MiniJson.Serialize(entry, true) + "\n");
            DeletePending(op);
            PruneHistory();
        }

        private void UpdateHistory(string id, Action<Dictionary<string, object>> change)
        {
            if (!StagedOperation.IsId(id)) return;
            Dictionary<string, object> entry = LoadHistory(id);
            if (entry == null) return;
            change(entry);
            AtomicFile.WriteAllText(Path.Combine(historyFolder, id + ".json"), MiniJson.Serialize(entry, true) + "\n");
        }

        private Dictionary<string, object> LoadHistory(string id)
        {
            string path = AdminFiles.FileIn(historyFolder, id + ".json");
            if (path == null) return null;
            try
            {
                return MiniJson.ParseObject(AtomicFile.ReadAllTextOrNull(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>History ids, newest first (ids start with their UTC time).</summary>
        private List<string> HistoryIds()
        {
            var ids = new List<string>();
            foreach (FileInfo f in AdminFiles.PlainFiles(historyFolder))
            {
                if (!f.Name.EndsWith(".json", StringComparison.Ordinal)) continue;
                string id = f.Name.Substring(0, f.Name.Length - 5);
                if (StagedOperation.IsId(id)) ids.Add(id);
            }
            ids.Sort((a, b) => string.CompareOrdinal(b, a));
            return ids;
        }

        private void PruneHistory()
        {
            List<string> ids = HistoryIds();
            for (int i = HistoryKept; i < ids.Count; i++)
            {
                AdminFiles.DeleteQuietly(Path.Combine(historyFolder, ids[i] + ".json"));
                string dir = Path.Combine(historyFolder, ids[i]);
                AdminFiles.DeleteFolderQuietly(Path.Combine(dir, "messages"));
                AdminFiles.DeleteFolderQuietly(dir);
            }
        }

        private string NewId(string kind)
        {
            string stem = Now.ToString(StampFormat, CultureInfo.InvariantCulture) + "-" + kind;
            string id = stem;
            for (int n = 2; File.Exists(Path.Combine(historyFolder, id + ".json")) || File.Exists(Path.Combine(folder, id + ".payload.json")); n++)
                id = stem + "-" + n.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        /// <summary>The system backup reason before an operation that writes the target's profile; null for a relink-only one.</summary>
        private static string BackupReasonOf(StagedOperation op)
        {
            if (op.Kind == StagedOperation.KindImport) return "preimport";
            if (op.Kind == StagedOperation.KindRestore) return "prerestore";
            if (op.Kind == StagedOperation.KindUndo && op.RestoreBackupId != null) return "prerestore";
            return null;
        }

        private static string AppliedMessage(StagedOperation op)
        {
            string who = op.SummaryAlias ?? op.Target;
            var sb = new StringBuilder();
            switch (op.Kind)
            {
                case StagedOperation.KindImport: sb.Append("Imported ").Append(who); break;
                case StagedOperation.KindRestore: sb.Append("Restored a backup of ").Append(who); break;
                case StagedOperation.KindRelink: sb.Append("This phone now plays ").Append(who); break;
                default: sb.Append("Undid the last operation"); break;
            }
            if (op.SummaryLevel.HasValue && op.Kind != StagedOperation.KindUndo)
                sb.Append(" (level ").Append(op.SummaryLevel.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
            sb.Append('.');
            return sb.ToString();
        }
    }
}
