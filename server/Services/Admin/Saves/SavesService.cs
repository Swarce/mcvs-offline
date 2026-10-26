// Module: admin-saves - owner: WP3 (design docs/design/settings-page.md §10.4).
// Saves on the settings page: backups, export as a file or as the MCVS-SAVE:1 transfer text, import by paste or from
// <data>/import/, and staged operations applied at the next start (before the port opens), then on probation.
//   GET    /_mcvs/api/v1/saves                              accounts, current player, staged/probation/last
//   GET    saves/import/files, POST saves/import/paste|validate|stage
//   GET|DELETE saves/staged, POST saves/staged/undo
//   GET|POST saves/{account}/backups, DELETE saves/{account}/backups/{id}
//   POST   saves/{account}/restore|relink|export, GET saves/{account}/export?format=transfer|json
// Registered last (§8.9): it applies a staged operation even without the admin framework (no routes then).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Auth;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>The "admin-saves" module (design §4.6, §6).</summary>
    public static class SavesService
    {
        /// <summary>
        /// Registers the module: applies the probation and a pending staged operation (synchronously, before the server
        /// starts), subscribes the probation to the game's saves, hooks <see cref="AdminApi.CreateBackup"/> and the status
        /// section, and registers the routes. A second call on the same context is ignored.
        /// </summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("admin");
            if (context.Features.Get<StagedOperations>() != null)
            {
                log.Debug("admin-saves: the module is already registered; the second registration is ignored");
                return;
            }
            var store = context.Features.Get<LocalProfileStore>();
            if (store == null)
            {
                log.Warn("admin-saves: no profile store (storage module missing): saves are not available");
                return;
            }
            StagedOperations staged;
            try
            {
                staged = new StagedOperations(context, store, log);
            }
            catch (Exception ex)
            {
                log.Error("admin-saves: cannot use " + context.GetDataPath("admin/staged") + "; staged operations are off", ex);
                return;
            }
            context.Features.Set(staged);
            staged.ApplyPending();
            context.Events.Subscribe(LocalProfileStore.ChangedTopic, staged.OnProfileChanged);

            var api = context.Features.Get<AdminApi>();
            if (api == null) return;
            api.CreateBackup = (account, reason) => store.CreateBackup(account, reason);
            api.SetStatusSection("staged", () => staged.StatusSection());
            new SavesApi(api, store, staged).Register();
        }
    }

    /// <summary>The routes of the admin-saves module.</summary>
    internal sealed class SavesApi
    {
        public const int PasteBodyLimit = 512 * 1024;
        public const int MaxPasteChars = 128000;
        public const long MaxImportFileBytes = 16L * 1024 * 1024;
        public const int ExportsKept = 5;
        public const int PastesPerMinute = 30;
        public const int ExportsPerMinute = 6;
        public const string PasteBucket = "admin.paste";
        public const string ExportBucket = "admin.export";

        public const string CurrentUnknown =
            "The page does not know which account this phone plays, so it cannot switch to another one. Open the page from the game.";

        public const string WritableBy =
            "On Android 10 and older any file manager can put files here; on Android 11 and later use a PC (USB file transfer or adb).";

        private readonly AdminApi api;
        private readonly ServerContext context;
        private readonly LocalProfileStore store;
        private readonly StagedOperations staged;
        private readonly Log log;
        private readonly object uploadsGate = new object();
        private ImportUploads uploads;

        public SavesApi(AdminApi api, LocalProfileStore store, StagedOperations staged)
        {
            this.api = api;
            context = api.Context;
            this.store = store;
            this.staged = staged;
            log = api.Log;
        }

        public void Register()
        {
            // Literal routes before the templated ones of the same shape (design §4.2).
            api.Get("saves", "admin.saves.list", List);
            api.Get("saves/import/files", "admin.saves.importFiles", ImportFiles);
            api.Post("saves/import/paste", "admin.saves.paste", Paste, PasteBodyLimit);
            api.Post("saves/import/validate", "admin.saves.validate", ValidateFile, AdminApi.DefaultBodyLimit);
            api.Post("saves/import/stage", "admin.saves.stage", StageImport, AdminApi.DefaultBodyLimit);
            api.Get("saves/staged", "admin.saves.staged", Staged);
            api.Delete("saves/staged", "admin.saves.unstage", Unstage);
            api.Post("saves/staged/undo", "admin.saves.undo", Undo, AdminApi.DefaultBodyLimit);
            api.Get("saves/{account}/backups", "admin.saves.backups", Backups);
            api.Post("saves/{account}/backups", "admin.saves.backup", BackupNow, AdminApi.DefaultBodyLimit);
            api.Delete("saves/{account}/backups/{id}", "admin.saves.backupDelete", DeleteBackup);
            api.Post("saves/{account}/restore", "admin.saves.restore", Restore, AdminApi.DefaultBodyLimit);
            api.Post("saves/{account}/relink", "admin.saves.relink", RelinkAccount, AdminApi.DefaultBodyLimit);
            api.Post("saves/{account}/export", "admin.saves.export", Export, AdminApi.DefaultBodyLimit);
            api.Get("saves/{account}/export", "admin.saves.download", Download);
        }

        private bool Idle
        {
            get { return api.ExitState == ExitSequence.Idle; }
        }

        private ImportUploads Uploads
        {
            get
            {
                // Created on first use (the folder exists only once somebody imports); one instance, one lock.
                lock (uploadsGate)
                {
                    if (uploads == null) uploads = new ImportUploads(AdminFiles.Folder(context, "admin/uploads"), context.Clock, log);
                    return uploads;
                }
            }
        }

        // ------------------------------------------------------------------ GET saves

        private HttpResponse List(AdminCall call)
        {
            CurrentPlayer player = api.CurrentPlayer();
            bool idle = Idle;
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            if (player.AccountId == null) body["current"] = null;
            else
            {
                var current = new Dictionary<string, object>(StringComparer.Ordinal);
                current["account"] = player.AccountId;
                current["source"] = player.Source;
                current["credential"] = AdminApi.MaskCredential(player.Credential);
                body["current"] = current;
            }

            var accounts = new List<object>();
            var others = new List<object>();
            foreach (Account account in context.Accounts.All())
            {
                bool isCurrent = account.Id == player.AccountId;
                bool stored = File.Exists(store.PathFor(account.Id));
                if (!stored && !isCurrent) continue;
                Dictionary<string, object> entry = AccountJson(account, isCurrent, stored, idle);
                if (isCurrent) accounts.Add(entry);
                else others.Add(entry);
            }
            accounts.AddRange(others);
            body["accounts"] = accounts;

            StagedOperation pending = staged.Pending();
            body["staged"] = pending == null ? null : pending.ToJson(true);
            body["probation"] = Probation.ForPage(staged.ProbationState());
            body["last"] = staged.Last();
            return AdminJson.Ok(body);
        }

        /// <summary>One account of GET saves. While a save/restart sequence runs no account lock is taken (no profile read).</summary>
        private Dictionary<string, object> AccountJson(Account account, bool isCurrent, bool stored, bool idle)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["account"] = account.Id;
            o["fedId"] = account.FedId;
            o["alias"] = AliasOf(account);
            o["created"] = account.Created;
            var credentials = new List<object>();
            foreach (string credential in account.Credentials) credentials.Add(AdminApi.MaskCredential(credential));
            o["credentials"] = credentials;
            o["isCurrent"] = isCurrent;
            DateTime? lastSave = api.Saves.LastGameSave(account.Id);
            o["lastGameSave"] = lastSave.HasValue ? TimeFormat.Iso8601Millis(lastSave.Value) : null;
            long size = 0;
            try
            {
                if (stored) size = new FileInfo(store.PathFor(account.Id)).Length;
            }
            catch (Exception)
            {
                size = 0;
            }
            o["size"] = size;
            o["backups"] = (long)store.Backups.List(account.Id).Count;
            if (!stored)
            {
                o["hasGameProfile"] = false;
                return o;
            }
            if (!idle) return o;
            try
            {
                ProfileDocument doc = store.ReadDocument(account.Id);
                o["hasGameProfile"] = SeshatOperations.HasGameProfile(doc);
                o["level"] = SaveBundle.LevelOf(doc);
                o["revision"] = doc.Revision;
                o["modified"] = doc.Modified;
            }
            catch (Exception ex)
            {
                o["hasGameProfile"] = false;
                o["error"] = ex.Message;
            }
            return o;
        }

        // ------------------------------------------------------------------ backups

        private HttpResponse Backups(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            List<BackupInfo> list;
            if (Idle)
            {
                using (store.Locks.Acquire(account)) list = store.Backups.List(account);
            }
            else
            {
                list = store.Backups.List(account);
            }
            var backups = new List<object>();
            foreach (BackupInfo b in list) backups.Add(BackupJson(b));
            var limits = new Dictionary<string, object>(StringComparer.Ordinal);
            limits["auto"] = (long)context.Settings.GetLong("storage.backups.keep", 20);
            limits["event"] = (long)ProfileBackups.EventCap;
            limits["system"] = (long)ProfileBackups.SystemCap;
            limits["manual"] = (long)ProfileBackups.ManualCap;
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["account"] = account;
            body["backups"] = backups;
            body["limits"] = limits;
            return AdminJson.Ok(body);
        }

        private Dictionary<string, object> BackupJson(BackupInfo b)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["id"] = b.Id;
            o["date"] = TimeFormat.Iso8601Millis(b.DateUtc);
            o["revision"] = b.Revision;
            o["reason"] = b.Reason;
            o["pool"] = b.Pool;
            o["size"] = b.Size;
            o["level"] = store.BackupLevel(b);
            o["deletable"] = b.Deletable;
            return o;
        }

        private HttpResponse BackupNow(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            string note;
            if (!OptionalString(call, "note", out note, out error)) return error;
            if (note != null && !IsNote(note)) return Invalid("note", "A note has at most 80 letters, digits, spaces, '.', '_' or '-'.");
            BackupInfo info;
            try
            {
                info = store.CreateBackup(account, "manual");
            }
            catch (ManualBackupLimitException)
            {
                return AdminJson.Error(HttpStatus.Conflict, "manual_full",
                                       "You already have " + ProfileBackups.ManualCap + " backups of your own. Delete one of your backups first.");
            }
            if (info == null) return NotFound("This account has no save yet.");
            log.Info("admin: manual backup " + info.Id + " of " + account + (string.IsNullOrEmpty(note) ? "" : " (" + note + ")"));
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["backup"] = BackupJson(info);
            body["asOf"] = store.ReadDocument(account).Modified;
            return AdminJson.Ok(body);
        }

        private HttpResponse DeleteBackup(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            string id = call.Route("id");
            BackupInfo info = ProfileBackups.IsSafeId(id) ? store.Backups.Find(account, id) : null;
            if (info == null) return NotFound("There is no such backup.");
            if (!info.Deletable)
                return AdminJson.Error(HttpStatus.BadRequest, "not_deletable", "Only your own backups can be deleted; the others are removed automatically.");
            if (!store.DeleteBackup(account, id)) return NotFound("There is no such backup.");
            return Ok();
        }

        // ------------------------------------------------------------------ restore, relink, undo, unstage

        private HttpResponse Restore(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            string backupId, plan, confirm;
            bool replace;
            if (!OptionalString(call, "backup", out backupId, out error) || !OptionalString(call, "plan", out plan, out error)
                || !OptionalString(call, "confirm", out confirm, out error) || !OptionalBool(call, "replace", out replace, out error))
                return error;
            BackupInfo info = ProfileBackups.IsSafeId(backupId) ? store.Backups.Find(account, backupId) : null;
            if (info == null) return NotFound("There is no such backup.");

            CurrentPlayer player = api.CurrentPlayer();
            bool isCurrent = player.AccountId == account;
            bool planOk = isCurrent ? plan == "replace" : plan == "restore-other" || plan == "switch";
            if (!planOk)
                return Invalid("plan", isCurrent ? "A backup of the account you play is restored with the plan 'replace'."
                                                 : "A backup of another account is restored with the plan 'restore-other' or 'switch'.");
            if (confirm != plan) return Invalid("confirm", "Confirm the plan '" + plan + "'.");
            if (plan == "switch" && (player.Credential == null || player.AccountId == null)) return CurrentPlayerUnknown();

            string text = store.ReadBackupText(account, backupId);
            ProfileDocument doc;
            List<string> problems = text == null ? new List<string> { "The backup is gone." }
                                                 : SaveBundle.ProfileProblems(text, account, true, null, out doc);
            if (problems.Count > 0) return Invalid("backup", problems);

            var op = new StagedOperation
            {
                Kind = StagedOperation.KindRestore,
                Plan = plan,
                Target = account,
                RestoreBackupId = backupId,
                Relink = plan == "switch" ? new RelinkPlan { Credential = player.Credential, From = player.AccountId, To = account } : null,
                SourceType = "backup",
                SourceName = backupId,
                SummaryAlias = AliasOf(account),
                SummaryLevel = store.BackupLevel(info),
                SummaryRevision = info.Revision,
            };
            return Stage(op, null, replace);
        }

        private HttpResponse RelinkAccount(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            string confirm;
            bool replace;
            if (!OptionalString(call, "confirm", out confirm, out error) || !OptionalBool(call, "replace", out replace, out error)) return error;
            if (confirm != "switch") return Invalid("confirm", "Confirm the plan 'switch'.");
            CurrentPlayer player = api.CurrentPlayer();
            if (player.Credential == null || player.AccountId == null) return CurrentPlayerUnknown();
            if (player.AccountId == account) return Invalid("account", "This phone already plays this account.");
            ProfileDocument doc = store.ReadDocument(account);
            if (!SeshatOperations.HasGameProfile(doc))
                return AdminJson.Error(HttpStatus.Conflict, "no_game_profile", "This account has no game progress to play.");

            var op = new StagedOperation
            {
                Kind = StagedOperation.KindRelink,
                Plan = "switch",
                Target = account,
                Relink = new RelinkPlan { Credential = player.Credential, From = player.AccountId, To = account },
                SourceType = "relink",
                SummaryAlias = AliasOf(account),
                SummaryLevel = SaveBundle.LevelOf(doc),
                SummaryRevision = doc.Revision,
            };
            return Stage(op, null, replace);
        }

        private HttpResponse Undo(AdminCall call)
        {
            HttpResponse error;
            string confirm;
            bool replace;
            if (!OptionalString(call, "confirm", out confirm, out error) || !OptionalBool(call, "replace", out replace, out error)) return error;
            if (confirm != "undo") return Invalid("confirm", "Confirm with 'undo'.");
            string reason;
            Dictionary<string, object> last = staged.FindUndoable(out reason);
            if (last == null) return AdminJson.Error(HttpStatus.Conflict, "nothing_to_undo", reason);

            string target = JsonValue.GetString(last, "target");
            string pre = JsonValue.GetString(last, "preBackupId");
            RelinkPlan done = JsonValue.GetBool(last, "relinked", false) ? RelinkPlan.FromJson(JsonValue.GetObject(last, "relink"), target) : null;
            var op = new StagedOperation
            {
                Kind = StagedOperation.KindUndo,
                Plan = "undo",
                Target = target,
                RestoreBackupId = pre,
                Relink = done == null ? null : new RelinkPlan { Credential = done.Credential, From = done.To, To = done.From },
                SourceType = "undo",
                UndoOf = JsonValue.GetString(last, "id"),
            };
            string back = op.Relink != null ? op.Relink.To : target;
            op.SummaryAlias = AliasOf(back);
            if (op.Relink == null && pre != null)
            {
                BackupInfo info = store.Backups.Find(target, pre);
                op.SummaryLevel = info == null ? (long?)null : store.BackupLevel(info);
                op.SummaryRevision = info == null ? (long?)null : info.Revision;
            }
            else
            {
                ProfileDocument doc = store.ReadDocument(back);
                op.SummaryLevel = SaveBundle.LevelOf(doc);
                op.SummaryRevision = doc.Revision;
            }

            HttpResponse response = Stage(op, null, replace);
            if (response.StatusCode == HttpStatus.Ok)
            {
                Dictionary<string, object> body = MiniJson.ParseObject(response.BodyText);
                body["description"] = UndoDescription(op, JsonValue.GetString(last, "kind"));
                return AdminJson.Ok(body);
            }
            return response;
        }

        private string UndoDescription(StagedOperation op, string undoneKind)
        {
            string level = op.SummaryLevel.HasValue ? " (level " + op.SummaryLevel.Value.ToString(CultureInfo.InvariantCulture) + ")" : "";
            if (op.Relink != null)
            {
                string text = "Go back to " + NameOf(op.Relink.To) + level + " on this phone; " + NameOf(op.Relink.From) + " stays on the phone.";
                if (op.RestoreBackupId != null)
                    text += " The save of " + NameOf(op.Target) + " goes back to how it was before the " + undoneKind + "; the progress made since on it is lost.";
                return text;
            }
            return "Restore " + NameOf(op.Target) + " to how it was before the " + undoneKind + level + "; the progress made since is lost.";
        }

        private HttpResponse Staged(AdminCall call)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            StagedOperation pending = staged.Pending();
            body["pending"] = pending == null ? null : pending.ToJson(true);
            body["probation"] = Probation.ForPage(staged.ProbationState());
            var history = new List<object>();
            foreach (Dictionary<string, object> entry in staged.History(10)) history.Add(StagedOperations.HistoryForPage(entry));
            body["history"] = history;
            return AdminJson.Ok(body);
        }

        private HttpResponse Unstage(AdminCall call)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = true;
            body["removed"] = staged.Unstage();
            return AdminJson.Ok(body);
        }

        private HttpResponse Stage(StagedOperation op, string payload, bool replace)
        {
            try
            {
                staged.Stage(op, payload, replace);
            }
            catch (StagedExistsException ex)
            {
                return AdminJson.Error(HttpStatus.Conflict, "staged_exists",
                                       "Another operation already waits for a restart. Replace it, or cancel it first.",
                                       AdminJson.Details("pending", ex.Pending.ToJson(true)));
            }
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["staged"] = op.ToJson(true);
            return AdminJson.Ok(body);
        }

        // ------------------------------------------------------------------ import

        private HttpResponse ImportFiles(AdminCall call)
        {
            string folder = AdminFiles.Folder(context, "import");
            var files = new List<object>();
            foreach (FileInfo f in ImportFileList(folder))
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["name"] = f.Name;
                o["size"] = f.Length;
                o["modified"] = TimeFormat.Iso8601Millis(f.LastWriteTimeUtc);
                files.Add(o);
            }
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["directory"] = folder;
            body["files"] = files;
            body["writableBy"] = WritableBy;
            return AdminJson.Ok(body);
        }

        /// <summary>The import folder's candidates: regular *.json/*.txt files with a plain name, at most 16 MiB, newest first.</summary>
        private static List<FileInfo> ImportFileList(string folder)
        {
            var list = new List<FileInfo>();
            foreach (FileInfo f in AdminFiles.PlainFiles(folder))
            {
                if (!IsImportName(f.Name) || f.Length > MaxImportFileBytes) continue;
                list.Add(f);
            }
            list.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return list;
        }

        /// <summary>^[A-Za-z0-9._ -]{1,128}$ ending in .json or .txt.</summary>
        internal static bool IsImportName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || name == "." || name == "..") return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == ' ' || c == '-';
                if (!ok) return false;
            }
            return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        }

        private HttpResponse Paste(AdminCall call)
        {
            HttpResponse error;
            string text;
            if (!OptionalString(call, "text", out text, out error)) return error;
            if (text == null) return AdminJson.Error(HttpStatus.BadRequest, "bad_request", "Paste the copied text.");
            if (text.Length > MaxPasteChars) return AdminJson.TooLarge(MaxPasteChars);
            if (!api.Allow(PasteBucket, PastesPerMinute)) return api.RateLimited(PasteBucket, PastesPerMinute);
            PasteOutcome outcome = Uploads.AddPaste(text);
            if (outcome.NothingFound) return Invalid("text", outcome.Message);
            return ImportAnswer(outcome, "paste", null);
        }

        private HttpResponse ValidateFile(AdminCall call)
        {
            HttpResponse error;
            string name;
            if (!OptionalString(call, "file", out name, out error)) return error;
            string folder = AdminFiles.Folder(context, "import");
            FileInfo file = null;
            foreach (FileInfo f in ImportFileList(folder))
            {
                if (string.Equals(f.Name, name, StringComparison.Ordinal)) file = f;
            }
            string path = file == null ? null : AdminFiles.FileIn(folder, file.Name);
            if (path == null) return NotFound("There is no such file in the import folder.");
            byte[] bytes = AdminFiles.ReadAtMost(path, MaxImportFileBytes);
            if (bytes == null) return AdminJson.TooLarge(MaxImportFileBytes);
            PasteOutcome outcome = ImportUploads.FromText(AdminFiles.Utf8(bytes, 0, bytes.Length));
            return ImportAnswer(outcome, "file", file.Name);
        }

        /// <summary>The answer to a paste or a file: the parts still missing, or the validation of the complete save.</summary>
        private HttpResponse ImportAnswer(PasteOutcome outcome, string sourceType, string name)
        {
            if (outcome.TooLarge) return AdminJson.TooLarge(SaveTransfer.MaxInflatedBytes);
            var rejected = new List<object>();
            foreach (TransferRejection r in outcome.Rejected)
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["part"] = (long)r.Part;
                o["reason"] = r.Reason;
                rejected.Add(o);
            }
            if (!outcome.Complete)
            {
                var partial = new Dictionary<string, object>(StringComparer.Ordinal);
                partial["complete"] = false;
                partial["set"] = outcome.Set;
                partial["total"] = (long)outcome.Total;
                partial["have"] = Numbers(outcome.Have);
                partial["missing"] = Numbers(outcome.Missing);
                partial["rejected"] = rejected;
                partial["message"] = outcome.Message;
                if (sourceType == "file")
                {
                    partial["errors"] = new List<object> { outcome.Message ?? SaveBundle.ErrorIncomplete };
                    partial["canStage"] = false;
                }
                return AdminJson.Ok(partial);
            }

            Dictionary<string, object> result;
            if (outcome.Undecodable || outcome.BundleText == null)
            {
                result = new Dictionary<string, object>(StringComparer.Ordinal);
                result["complete"] = true;
                result["errors"] = new List<object> { SaveBundle.ErrorIncomplete };
                result["warnings"] = new List<object>();
                result["canStage"] = false;
            }
            else
            {
                result = Validation(SaveBundle.Parse(outcome.BundleText, context.Clock.UtcNow), outcome.BundleText, sourceType, name);
            }
            result["set"] = outcome.Set;
            result["rejected"] = rejected;
            result["message"] = outcome.Message;
            return AdminJson.Ok(result);
        }

        /// <summary>The validation result of a complete save (design §4.6); a stageable save is kept as an upload.</summary>
        private Dictionary<string, object> Validation(SaveBundle bundle, string text, string sourceType, string name)
        {
            CurrentPlayer player = api.CurrentPlayer();
            var errors = new List<object>();
            foreach (string e in bundle.Errors) errors.Add(e);
            var warnings = new List<object>();
            foreach (string w in bundle.Warnings) warnings.Add(w);
            var codes = new List<object>();

            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["complete"] = true;
            o["uploadId"] = null;
            o["bundle"] = bundle.Summary();

            Dictionary<string, object> local = null;
            if (SaveBundle.IsAccountId(bundle.AccountId))
            {
                local = new Dictionary<string, object>(StringComparer.Ordinal);
                ProfileDocument doc = store.ReadDocument(bundle.AccountId);
                local["exists"] = doc.IsStored;
                local["isCurrent"] = bundle.AccountId == player.AccountId;
                local["revision"] = doc.IsStored ? (object)doc.Revision : null;
                local["modified"] = doc.Modified;
                local["hasGameProfile"] = SeshatOperations.HasGameProfile(doc);
                local["level"] = doc.IsStored ? (object)SaveBundle.LevelOf(doc) : null;
                if (doc.IsStored && doc.Revision > bundle.Revision && bundle.IsValid)
                {
                    warnings.Add("This phone already has a newer save of that account (revision "
                                 + doc.Revision.ToString(CultureInfo.InvariantCulture) + " > " + bundle.Revision.ToString(CultureInfo.InvariantCulture) + ").");
                }
            }
            o["local"] = local;
            o["current"] = CurrentJson(player);

            string plan = player.AccountId != null && player.AccountId == bundle.AccountId ? "replace" : "switch";
            if (bundle.IsValid && plan == "switch" && player.Credential == null)
            {
                errors.Add(CurrentUnknown);
                codes.Add("current_player_unknown");
            }
            if (bundle.IsValid && bundle.Alias != null)
            {
                if (!AliasRegistry.IsWellFormed(bundle.Alias)) warnings.Add("The save's alias is not valid; the account keeps or gets its own.");
                else
                {
                    Account owner = new AliasRegistry(context.Accounts, log).FindOwner(bundle.Alias);
                    if (owner != null && owner.Id != bundle.AccountId)
                        warnings.Add("The alias " + bundle.Alias + " belongs to another account on this phone; the imported account keeps or gets its own.");
                }
            }
            o["plans"] = new List<object> { plan };
            o["plan"] = plan;
            o["errors"] = errors;
            o["errorCodes"] = codes;
            o["warnings"] = warnings;
            bool canStage = errors.Count == 0;
            o["canStage"] = canStage;
            if (canStage) o["uploadId"] = Uploads.Save(text, sourceType, name);
            return o;
        }

        private Dictionary<string, object> CurrentJson(CurrentPlayer player)
        {
            if (player.AccountId == null) return null;
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["account"] = player.AccountId;
            o["alias"] = AliasOf(player.AccountId);
            object level = null;
            if (Idle)
            {
                try
                {
                    ProfileDocument doc = store.ReadDocument(player.AccountId);
                    if (doc.IsStored) level = SaveBundle.LevelOf(doc);
                }
                catch (Exception)
                {
                    level = null;
                }
            }
            o["level"] = level;
            o["source"] = player.Source;
            return o;
        }

        private HttpResponse StageImport(AdminCall call)
        {
            HttpResponse error;
            string uploadId, confirm;
            bool replace, allowOlder;
            if (!OptionalString(call, "uploadId", out uploadId, out error) || !OptionalString(call, "confirm", out confirm, out error)
                || !OptionalBool(call, "replace", out replace, out error) || !OptionalBool(call, "allowOlder", out allowOlder, out error))
                return error;
            UploadRecord record = ImportUploads.IsUploadId(uploadId) ? Uploads.Load(uploadId) : null;
            if (record == null || record.Text == null) return NotFound("The checked save is no longer there (it is kept for an hour). Paste it again.");
            SaveBundle bundle = SaveBundle.Parse(record.Text, context.Clock.UtcNow);
            if (!bundle.IsValid) return Invalid("uploadId", bundle.Errors);

            CurrentPlayer player = api.CurrentPlayer();
            string plan = player.AccountId != null && player.AccountId == bundle.AccountId ? "replace" : "switch";
            if (confirm != plan) return Invalid("confirm", "Confirm the plan '" + plan + "'.");
            if (plan == "switch" && player.Credential == null) return CurrentPlayerUnknown();
            ProfileDocument local = store.ReadDocument(bundle.AccountId);
            if (local.IsStored && local.Revision > bundle.Revision && !allowOlder)
            {
                return AdminJson.Error(HttpStatus.Conflict, "newer_save",
                                       "This phone already has a newer save of that account (revision "
                                       + local.Revision.ToString(CultureInfo.InvariantCulture) + " > " + bundle.Revision.ToString(CultureInfo.InvariantCulture) + ").");
            }

            var op = new StagedOperation
            {
                Kind = StagedOperation.KindImport,
                Plan = plan,
                Target = bundle.AccountId,
                Relink = plan == "switch" ? new RelinkPlan { Credential = player.Credential, From = player.AccountId, To = bundle.AccountId } : null,
                SourceType = record.SourceType,
                SourceName = record.Name,
                SummaryAlias = bundle.Alias,
                SummaryLevel = bundle.Level,
                SummaryRevision = bundle.Revision,
            };
            HttpResponse response = Stage(op, record.Text, replace);
            if (response.StatusCode == HttpStatus.Ok) Uploads.Delete(uploadId);
            return response;
        }

        // ------------------------------------------------------------------ export

        private HttpResponse Export(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            string target, format;
            long partChars;
            bool includeSettings;
            if (!OptionalString(call, "target", out target, out error) || !OptionalString(call, "format", out format, out error)
                || !OptionalLong(call, "partChars", out partChars, out error) || !OptionalBool(call, "includeSettings", out includeSettings, out error))
                return error;
            target = target ?? "share";
            format = format ?? "transfer";
            if (partChars == 0) partChars = SaveTransfer.DefaultPartChars;
            if (target != "share" && target != "downloads" && target != "text") return Invalid("target", "target is share, downloads or text.");
            if (format != "transfer" && format != "json") return Invalid("format", "format is transfer or json.");
            if (!SaveTransfer.IsPartSize(partChars)) return Invalid("partChars", "partChars is 60000, 3900 or 1900.");
            if (target == "text" && format != "transfer") return Invalid("format", "Copy as text needs the transfer format.");

            IAdminHost host = api.Host;
            AdminHostInfo info = api.DescribeHost();
            if (target == "share" && (host == null || info == null || !info.CanShareFile)) return NotSupported("This build cannot share files.");
            if (target == "downloads" && (host == null || info == null || !info.CanSaveToDownloads))
                return NotSupported("This phone cannot save to Downloads from the game.");
            if (!api.Allow(ExportBucket, ExportsPerMinute)) return api.RateLimited(ExportBucket, ExportsPerMinute);

            SaveBundle bundle = SaveBundle.Build(context, store, account, includeSettings);
            if (bundle == null) return NotFound("This account has no save yet.");
            string json = bundle.ToJson();
            TransferExport export = format == "transfer"
                ? SaveTransfer.Encode(AdminFiles.Utf8Bytes(json), (int)partChars, Ids.RandomHex(4), SaveTransfer.CanDeflate(log))
                : null;

            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["file"] = null;
            body["size"] = export != null ? export.Chars : (long)AdminFiles.Utf8Bytes(json).Length;
            body["asOf"] = bundle.Modified;
            body["transfer"] = export == null ? null : TransferJson(export, target == "text");
            body["share"] = null;
            if (target != "text")
            {
                string content = export != null ? SaveTransfer.FileText(export, FileHeader(bundle, export)) : json;
                string fileName = ExportFileName(bundle, export != null ? ".txt" : ".json");
                string folder = AdminFiles.Folder(context, "admin/exports");
                string path = Path.Combine(folder, fileName);
                byte[] bytes = AdminFiles.Utf8Bytes(content);
                AtomicFile.WriteAllBytes(path, bytes);
                AdminFiles.KeepNewest(folder, n => n.StartsWith("mcvs-save-", StringComparison.Ordinal), ExportsKept);
                body["file"] = fileName;
                body["size"] = (long)bytes.Length;
                string who = bundle.Alias ?? account;
                AdminShareResult share;
                try
                {
                    share = host.ShareFile(path, export != null ? "text/plain" : "application/json", fileName,
                                           target == "downloads" ? null : "MCVS Offline save",
                                           "My MCVS Offline save (" + who + "). Import it in MCVS Offline › Saves › Import.");
                }
                catch (Exception ex)
                {
                    log.Warn("admin: sharing " + fileName + " failed", ex);
                    share = AdminShareResult.None("Sharing failed: " + ex.Message);
                }
                body["share"] = ShareJson(share);
                log.Info("admin: export of " + account + " as " + fileName + " (" + bytes.Length + " bytes, " + target + ": "
                         + (share == null ? "none" : share.Method) + ")");
            }
            else
            {
                log.Info("admin: export of " + account + " as text (" + export.Parts.Count + " parts of " + partChars + ")");
            }
            return AdminJson.Ok(body);
        }

        /// <summary>GET saves/{account}/export?format=transfer|json: the file as an attachment (desktop browsers, standalone).</summary>
        private HttpResponse Download(AdminCall call)
        {
            HttpResponse error;
            string account = ResolveAccount(call, out error);
            if (account == null) return error;
            string format = call.Query("format") ?? "transfer";
            if (format != "transfer" && format != "json") return Invalid("format", "format is transfer or json.");
            if (!api.Allow(ExportBucket, ExportsPerMinute)) return api.RateLimited(ExportBucket, ExportsPerMinute);
            SaveBundle bundle = SaveBundle.Build(context, store, account, false);
            if (bundle == null) return NotFound("This account has no save yet.");
            string json = bundle.ToJson();
            string content;
            string fileName;
            if (format == "transfer")
            {
                TransferExport export = SaveTransfer.Encode(AdminFiles.Utf8Bytes(json), SaveTransfer.DefaultPartChars, Ids.RandomHex(4),
                                                            SaveTransfer.CanDeflate(log));
                content = SaveTransfer.FileText(export, FileHeader(bundle, export));
                fileName = ExportFileName(bundle, ".txt");
            }
            else
            {
                content = json;
                fileName = ExportFileName(bundle, ".json");
            }
            var response = new HttpResponse(HttpStatus.Ok);
            response.Body = AdminFiles.Utf8Bytes(content);
            response.ContentType = format == "transfer" ? HttpResponse.TextContentType : HttpResponse.JsonContentType;
            response.Headers.Set("Content-Disposition", "attachment; filename=\"" + fileName + "\"");
            log.Info("admin: export of " + account + " downloaded as " + fileName);
            return AdminJson.WithApiHeaders(response);
        }

        private static Dictionary<string, object> TransferJson(TransferExport export, bool withTexts)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["set"] = export.Set;
            o["method"] = export.Method.ToString();
            o["chars"] = export.Chars;
            o["parts"] = (long)export.Parts.Count;
            o["partChars"] = (long)export.PartChars;
            if (withTexts)
            {
                var texts = new List<object>();
                foreach (string part in export.Parts) texts.Add(part);
                o["texts"] = texts;
            }
            else
            {
                o["texts"] = null;
            }
            return o;
        }

        internal static Dictionary<string, object> ShareJson(AdminShareResult share)
        {
            if (share == null) return null;
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["ok"] = share.Ok;
            o["method"] = share.Method;
            o["publicPath"] = share.PublicPath;
            o["message"] = share.Message;
            return o;
        }

        /// <summary>The instruction lines at the top of a .txt export (design §6.2).</summary>
        private static string FileHeader(SaveBundle bundle, TransferExport export)
        {
            string who = bundle.Alias ?? bundle.AccountId;
            string asOf = bundle.Modified;
            DateTime modified;
            if (asOf != null && TimeFormat.TryParse(asOf, out modified))
                asOf = modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
            int parts = export.Parts.Count;
            return "MCVS Offline save of " + who + " (level " + bundle.Level.ToString(CultureInfo.InvariantCulture) + ", revision "
                   + bundle.Revision.ToString(CultureInfo.InvariantCulture) + "), as of the game's save of " + (asOf ?? "unknown") + ".\n"
                   + parts.ToString(CultureInfo.InvariantCulture) + (parts == 1 ? " part" : " parts")
                   + ". To import it: in the game open Settings > Account > MCVS Offline > Saves > Import and paste the parts,\n"
                   + "in any order, one or several at a time (everything from MCVS-SAVE to ~).";
        }

        /// <summary>mcvs-save-&lt;alias or account&gt;-&lt;yyyyMMdd-HHmm&gt;.txt|.json (phone local time; [A-Za-z0-9_-] only).</summary>
        private string ExportFileName(SaveBundle bundle, string extension)
        {
            string name = AliasRegistry.IsWellFormed(bundle.Alias) ? bundle.Alias : bundle.AccountId;
            string stamp = context.Clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            return "mcvs-save-" + AdminFiles.SafeName(name) + "-" + stamp + extension;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>{account}: 24 lower-case hex present in AccountStore, or "current" (§6.6). Anything else: 404, no file lookup.</summary>
        private string ResolveAccount(AdminCall call, out HttpResponse error)
        {
            error = null;
            string raw = call.Route("account");
            if (raw == "current")
            {
                CurrentPlayer player = api.CurrentPlayer();
                if (player.AccountId != null) return player.AccountId;
                error = NotFound("The page does not know which account this phone plays.");
                return null;
            }
            if (SaveBundle.IsAccountId(raw) && context.Accounts.FindById(raw) != null) return raw;
            error = NotFound("There is no such account on this phone.");
            return null;
        }

        private string AliasOf(string accountId)
        {
            return AliasOf(context.Accounts.FindById(accountId));
        }

        private static string AliasOf(Account account)
        {
            return account == null ? null : JsonValue.AsString(JsonValue.Get(account.Data, AliasRegistry.DataKey));
        }

        private string NameOf(string accountId)
        {
            string alias = AliasOf(accountId);
            if (!string.IsNullOrEmpty(alias)) return alias;
            return accountId == null ? "the account" : accountId.Substring(0, Math.Min(8, accountId.Length)) + "…";
        }

        private static bool IsNote(string note)
        {
            if (note.Length > 80) return false;
            foreach (char c in note)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ' || c == '.' || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        private static List<object> Numbers(List<int> numbers)
        {
            var list = new List<object>();
            foreach (int n in numbers) list.Add((long)n);
            return list;
        }

        internal static bool OptionalString(AdminCall call, string name, out string value, out HttpResponse error)
        {
            value = null;
            error = null;
            object raw;
            if (call.Body == null || !call.Body.TryGetValue(name, out raw) || raw == null) return true;
            value = raw as string;
            if (value != null) return true;
            error = Invalid(name, name + " must be a string.");
            return false;
        }

        internal static bool OptionalBool(AdminCall call, string name, out bool value, out HttpResponse error)
        {
            value = false;
            error = null;
            object raw;
            if (call.Body == null || !call.Body.TryGetValue(name, out raw) || raw == null) return true;
            if (raw is bool)
            {
                value = (bool)raw;
                return true;
            }
            error = Invalid(name, name + " must be true or false.");
            return false;
        }

        internal static bool OptionalLong(AdminCall call, string name, out long value, out HttpResponse error)
        {
            value = 0;
            error = null;
            object raw;
            if (call.Body == null || !call.Body.TryGetValue(name, out raw) || raw == null) return true;
            if (raw is long)
            {
                value = (long)raw;
                return true;
            }
            error = Invalid(name, name + " must be a whole number.");
            return false;
        }

        internal static HttpResponse Invalid(string key, string message)
        {
            return Invalid(key, new List<string> { message });
        }

        internal static HttpResponse Invalid(string key, List<string> messages)
        {
            var problems = new List<object>();
            foreach (string m in messages)
            {
                var p = new Dictionary<string, object>(StringComparer.Ordinal);
                p["key"] = key;
                p["message"] = m;
                problems.Add(p);
            }
            return AdminJson.Error(HttpStatus.BadRequest, "invalid", string.Join(" ", messages.ToArray()), AdminJson.Details("problems", problems));
        }

        internal static HttpResponse NotFound(string message)
        {
            return AdminJson.Error(HttpStatus.NotFound, "not_found", message);
        }

        internal static HttpResponse NotSupported(string message)
        {
            return AdminJson.Error(HttpStatus.NotImplemented, "not_supported", message);
        }

        private static HttpResponse CurrentPlayerUnknown()
        {
            return AdminJson.Error(HttpStatus.Conflict, "current_player_unknown", CurrentUnknown);
        }

        private static HttpResponse Ok()
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = true;
            return AdminJson.Ok(body);
        }
    }
}
