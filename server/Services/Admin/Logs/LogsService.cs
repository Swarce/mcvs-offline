// Module: admin-logs - owner: WP3 (design docs/design/settings-page.md §10.4).
// Logs on the settings page: the files of the logs directories, a parsed and filtered tail, a zip for bug reports
// (built first, its size shown, then shared through the host), and a plain-text report.
//   GET  /_mcvs/api/v1/logs                      directories and files
//   GET  logs/{name}?dir=&lines=&min=&q=&raw=     tail (entries, or raw text for crash files and dumps)
//   POST logs/zip, POST logs/share, GET logs/bundle?scope=recent|all, POST logs/share-text

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin.Saves;

namespace McvsOffline.Server.Services.Admin.Logs
{
    /// <summary>The "admin-logs" module (design §4.7, §7).</summary>
    public static class LogsService
    {
        /// <summary>Registers the routes on the admin framework; without it (no "admin" module) nothing is registered.</summary>
        public static void Register(Router router, ServerContext context)
        {
            var api = context.Features.Get<AdminApi>();
            if (api == null) return;
            if (context.Features.Get<LogsApi>() != null)
            {
                api.Log.Debug("admin-logs: the module is already registered; the second registration is ignored");
                return;
            }
            var logs = new LogsApi(api, router);
            context.Features.Set(logs);
            logs.Register();
        }
    }

    /// <summary>The routes of the admin-logs module.</summary>
    internal sealed class LogsApi
    {
        public const int ZipBodyLimit = 8 * 1024;
        public const int ShareTextBodyLimit = 8 * 1024;
        public const int MaxNoteChars = 4000;
        public const int ZipsKept = 3;
        public const int ZipsPerMinute = 6;
        public const int SharesPerMinute = 6;
        public const string ZipBucket = "admin.zip";
        public const string ShareBucket = "admin.share";
        public const long DiscordBytes = 10L * 1000 * 1000;
        public const long GmailBytes = 25L * 1000 * 1000;
        public const int ReportMaxChars = 100000;
        public const int ReportProblemEntries = 300;
        public const int ReportCrashLines = 200;

        private readonly AdminApi api;
        private readonly Router router;
        private readonly ServerContext context;
        private readonly Log log;
        private readonly Dictionary<string, string> notes = new Dictionary<string, string>(StringComparer.Ordinal);
        private int building;

        public LogsApi(AdminApi api, Router router)
        {
            this.api = api;
            this.router = router;
            context = api.Context;
            log = api.Log;
        }

        public void Register()
        {
            // Literal routes before logs/{name} (design §4.2).
            api.Get("logs", "admin.logs.list", List);
            api.Get("logs/bundle", "admin.logs.download", Bundle);
            api.Post("logs/zip", "admin.logs.zip", Zip, ZipBodyLimit);
            api.Post("logs/share", "admin.logs.share", Share, AdminApi.DefaultBodyLimit);
            api.Post("logs/share-text", "admin.logs.shareText", ShareText, ShareTextBodyLimit);
            api.Get("logs/{name}", "admin.logs.tail", Tail);
        }

        // ------------------------------------------------------------------ listing

        /// <summary>Index 0: the host's logs directory (else &lt;data&gt;/logs); index 1: the host's internal-storage fallback.</summary>
        private List<string> Directories(AdminHostInfo info)
        {
            var dirs = new List<string>();
            string primary = info != null && !string.IsNullOrEmpty(info.LogsDir) ? info.LogsDir : context.GetDataPath("logs");
            dirs.Add(Path.GetFullPath(primary));
            if (info != null && !string.IsNullOrEmpty(info.FallbackLogsDir))
            {
                string fallback = Path.GetFullPath(info.FallbackLogsDir);
                if (!string.Equals(fallback, dirs[0], StringComparison.Ordinal)) dirs.Add(fallback);
            }
            return dirs;
        }

        /// <summary>The regular files (no links) with a plain name in both directories, sorted as GET logs shows them.</summary>
        internal List<LogFile> Files(List<string> dirs)
        {
            var files = new List<LogFile>();
            for (int d = 0; d < dirs.Count; d++)
            {
                foreach (FileInfo f in AdminFiles.PlainFiles(dirs[d]))
                {
                    if (!IsLogName(f.Name) || f.Name.EndsWith(AtomicFile.TempSuffix, StringComparison.Ordinal)) continue;
                    files.Add(new LogFile { Dir = d, Name = f.Name, Kind = KindOf(f.Name), Size = f.Length, ModifiedUtc = f.LastWriteTimeUtc, Path = f.FullName });
                }
            }
            files.Sort((a, b) =>
            {
                int c = GroupOf(a.Kind).CompareTo(GroupOf(b.Kind));
                if (c != 0) return c;
                if (GroupOf(a.Kind) == 0)
                {
                    c = CurrentOrder(a.Name).CompareTo(CurrentOrder(b.Name));
                    if (c != 0) return c;
                    return a.Dir.CompareTo(b.Dir);
                }
                c = b.ModifiedUtc.CompareTo(a.ModifiedUtc);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            return files;
        }

        /// <summary>^[A-Za-z0-9._-]{1,128}$ (and never "." or "..").</summary>
        internal static bool IsLogName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 || name == "." || name == "..") return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        internal static string KindOf(string name)
        {
            switch (name)
            {
                case "server.log": return "server";
                case "bootstrap.log": return "bootstrap";
                case "game.log": return "game";
            }
            if (name.StartsWith("crash-", StringComparison.Ordinal)) return "crash";
            if (name.StartsWith("exception-", StringComparison.Ordinal)) return "exception";
            if (name.StartsWith("threaddump-", StringComparison.Ordinal)) return "threaddump";
            if (name.StartsWith("exit-", StringComparison.Ordinal)) return "exit";
            if (name.StartsWith("unknown-endpoints.jsonl", StringComparison.Ordinal)) return "unknown-endpoints";
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1 && name.IndexOf(".log.", StringComparison.Ordinal) > 0)
            {
                bool digits = true;
                for (int i = dot + 1; i < name.Length; i++) digits &= name[i] >= '0' && name[i] <= '9';
                if (digits) return "rotated";
            }
            return "other";
        }

        /// <summary>Current logs first, then crash/exception/exit, thread dumps, rotated, other.</summary>
        private static int GroupOf(string kind)
        {
            switch (kind)
            {
                case "server":
                case "bootstrap":
                case "game": return 0;
                case "crash":
                case "exception":
                case "exit": return 1;
                case "threaddump": return 2;
                case "rotated": return 3;
                default: return 4;
            }
        }

        private static int CurrentOrder(string name)
        {
            return name == "server.log" ? 0 : name == "bootstrap.log" ? 1 : 2;
        }

        private HttpResponse List(AdminCall call)
        {
            AdminHostInfo info = api.DescribeHost();
            List<string> dirs = Directories(info);
            var directories = new List<object>();
            for (int d = 0; d < dirs.Count; d++)
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["index"] = (long)d;
                o["path"] = dirs[d];
                o["kind"] = d == 0 ? "primary" : "fallback";
                directories.Add(o);
            }
            var files = new List<object>();
            foreach (LogFile f in Files(dirs))
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["dir"] = (long)f.Dir;
                o["name"] = f.Name;
                o["kind"] = f.Kind;
                o["size"] = f.Size;
                o["modified"] = TimeFormat.Iso8601Millis(f.ModifiedUtc);
                files.Add(o);
            }
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["directories"] = directories;
            body["files"] = files;
            body["mirror"] = info == null ? null : info.MirrorStatus;
            return AdminJson.Ok(body);
        }

        // ------------------------------------------------------------------ tail

        private HttpResponse Tail(AdminCall call)
        {
            string name = call.Route("name");
            string dirText = call.Query("dir") ?? "0";
            if (!IsLogName(name) || (dirText != "0" && dirText != "1")) return SavesApi.NotFound("There is no such log file.");
            int dir = dirText == "1" ? 1 : 0;
            List<string> dirs = Directories(api.DescribeHost());
            if (dir >= dirs.Count) return SavesApi.NotFound("There is no such log file.");
            // The exact name, else the only one that differs just in case (one file on Windows, pc-port.md §4.6).
            LogFile file = null, folded = null;
            int foldedCount = 0;
            foreach (LogFile f in Files(dirs))
            {
                if (f.Dir != dir) continue;
                if (string.Equals(f.Name, name, StringComparison.Ordinal))
                {
                    file = f;
                    break;
                }
                if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                folded = f;
                foldedCount++;
            }
            if (file == null && foldedCount == 1) file = folded;
            string path = file == null ? null : AdminFiles.FileIn(dirs[dir], file.Name);
            if (path == null) return SavesApi.NotFound("There is no such log file.");

            int lines = 200;
            string linesText = call.Query("lines");
            if (linesText != null && (!int.TryParse(linesText, NumberStyles.None, CultureInfo.InvariantCulture, out lines) || lines < 1 || lines > LogTail.MaxLines))
                return SavesApi.Invalid("lines", "lines is 1 to " + LogTail.MaxLines + ".");
            string minText = call.Query("min") ?? "trace";
            LogLevel min = LogEntry.ParseLevel(minText, LogLevel.None);
            if (min == LogLevel.None) return SavesApi.Invalid("min", "min is trace, debug, info, warn or error.");
            string q = call.Query("q");
            if (q != null && q.Length > 100) return SavesApi.Invalid("q", "Search for at most 100 characters.");

            if (call.Query("raw") == "1")
            {
                long rawSize;
                bool truncated;
                string text = LogTail.Raw(path, out rawSize, out truncated);
                var raw = new HttpResponse(HttpStatus.Ok);
                raw.Body = Encoding.UTF8.GetBytes(text);
                raw.ContentType = HttpResponse.TextContentType;
                return AdminJson.WithApiHeaders(raw);
            }

            LogTailResult result = LogTail.Read(path, lines, min, q);
            var entries = new List<object>();
            foreach (LogTailEntry e in result.Entries) entries.Add(e.ToJson());
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["name"] = file.Name;
            body["dir"] = (long)dir;
            body["size"] = result.Size;
            body["window"] = result.Window;
            body["truncatedHead"] = result.TruncatedHead;
            body["entries"] = entries;
            return AdminJson.Ok(body);
        }

        // ------------------------------------------------------------------ zip and share

        private HttpResponse Zip(AdminCall call)
        {
            HttpResponse error;
            string scope, note;
            if (!SavesApi.OptionalString(call, "scope", out scope, out error) || !SavesApi.OptionalString(call, "note", out note, out error)) return error;
            scope = scope ?? "recent";
            if (scope != "recent" && scope != "all") return SavesApi.Invalid("scope", "scope is recent or all.");
            if (note != null && note.Length > MaxNoteChars) return SavesApi.Invalid("note", "A note has at most " + MaxNoteChars + " characters.");
            if (Interlocked.CompareExchange(ref building, 1, 0) != 0)
                return AdminJson.Error(HttpStatus.Conflict, "busy", "A logs zip is being built. Wait a moment.");
            try
            {
                if (!api.Allow(ZipBucket, ZipsPerMinute)) return api.RateLimited(ZipBucket, ZipsPerMinute);
                LogsZipResult zip = BuildZip(call, scope == "all", note);
                var body = new Dictionary<string, object>(StringComparer.Ordinal);
                body["file"] = zip.File;
                body["size"] = zip.Size;
                body["entries"] = (long)zip.Entries;
                body["skipped"] = (long)zip.Skipped.Count;
                var fits = new Dictionary<string, object>(StringComparer.Ordinal);
                fits["discord"] = zip.Size <= DiscordBytes;
                fits["gmail"] = zip.Size <= GmailBytes;
                body["fits"] = fits;
                return AdminJson.Ok(body);
            }
            finally
            {
                Interlocked.Exchange(ref building, 0);
            }
        }

        /// <summary>GET logs/bundle?scope=: a fresh zip as an attachment (desktop browsers, standalone).</summary>
        private HttpResponse Bundle(AdminCall call)
        {
            string scope = call.Query("scope") ?? "recent";
            if (scope != "recent" && scope != "all") return SavesApi.Invalid("scope", "scope is recent or all.");
            if (Interlocked.CompareExchange(ref building, 1, 0) != 0)
                return AdminJson.Error(HttpStatus.Conflict, "busy", "A logs zip is being built. Wait a moment.");
            try
            {
                if (!api.Allow(ZipBucket, ZipsPerMinute)) return api.RateLimited(ZipBucket, ZipsPerMinute);
                LogsZipResult zip = BuildZip(call, scope == "all", null);
                string path = Path.Combine(ShareFolder(), zip.File);
                var response = new HttpResponse(HttpStatus.Ok);
                response.Body = AdminFiles.ReadAtMost(path, 256L * 1024 * 1024);
                response.ContentType = "application/zip";
                response.Headers.Set("Content-Disposition", "attachment; filename=\"" + zip.File + "\"");
                return AdminJson.WithApiHeaders(response);
            }
            finally
            {
                Interlocked.Exchange(ref building, 0);
            }
        }

        private LogsZipResult BuildZip(AdminCall call, bool all, string note)
        {
            DateTime now = context.Clock.UtcNow;
            string folder = ShareFolder();
            string stem = "mcvs-logs-" + now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string name = stem + ".zip";
            for (int n = 2; File.Exists(Path.Combine(folder, name)); n++) name = stem + "-" + n.ToString(CultureInfo.InvariantCulture) + ".zip";

            List<string> dirs = Directories(api.DescribeHost());
            LogsZipResult zip = LogsZip.Build(Path.Combine(folder, name), Files(dirs), all, StatusJson(call.Request), SettingsJson(), note,
                                              SaveTransfer.CanDeflate(log), now);
            AdminFiles.KeepNewest(folder, IsZipName, ZipsKept, zip.File);
            lock (notes)
            {
                notes[zip.File] = note;
                var stale = new List<string>();
                foreach (string key in notes.Keys) if (!File.Exists(Path.Combine(folder, key))) stale.Add(key);
                foreach (string key in stale) notes.Remove(key);
            }
            log.Info("admin: logs zip " + zip.File + " (" + (all ? "all" : "recent") + ", " + zip.Size + " bytes, " + zip.Entries
                     + " entries, " + zip.Skipped.Count + " left out)");
            return zip;
        }

        private HttpResponse Share(AdminCall call)
        {
            HttpResponse error;
            string file, target;
            if (!SavesApi.OptionalString(call, "file", out file, out error) || !SavesApi.OptionalString(call, "target", out target, out error)) return error;
            target = target ?? "share";
            if (target != "share" && target != "downloads") return SavesApi.Invalid("target", "target is share or downloads.");
            string path = IsZipName(file) ? AdminFiles.FileIn(ShareFolder(), file) : null;
            if (path == null) return SavesApi.NotFound("That zip is gone. Build it again.");
            IAdminHost host = api.Host;
            AdminHostInfo info = api.DescribeHost();
            if (target == "share" && (host == null || info == null || !info.CanShareFile)) return SavesApi.NotSupported("This build cannot share files.");
            if (target == "downloads" && (host == null || info == null || !info.CanSaveToDownloads))
                return SavesApi.NotSupported("This phone cannot save to Downloads from the game.");
            if (!api.Allow(ShareBucket, SharesPerMinute)) return api.RateLimited(ShareBucket, SharesPerMinute);
            string note;
            lock (notes) notes.TryGetValue(Path.GetFileName(path), out note); // the zip's own name (FileIn may fold case)
            AdminShareResult share;
            try
            {
                share = host.ShareFile(path, "application/zip", file, target == "downloads" ? null : "MCVS Offline logs",
                                       string.IsNullOrEmpty(note) ? "MCVS Offline logs" : LogRedaction.Redact(note));
            }
            catch (Exception ex)
            {
                log.Warn("admin: sharing " + file + " failed", ex);
                share = AdminShareResult.None("Sharing failed: " + ex.Message);
            }
            log.Info("admin: logs zip " + file + " shared (" + target + ": " + (share == null ? "none" : share.Method) + ")");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["share"] = SavesApi.ShareJson(share);
            return AdminJson.Ok(body);
        }

        private HttpResponse ShareText(AdminCall call)
        {
            HttpResponse error;
            string note;
            if (!SavesApi.OptionalString(call, "note", out note, out error)) return error;
            if (note != null && note.Length > MaxNoteChars) return SavesApi.Invalid("note", "A note has at most " + MaxNoteChars + " characters.");
            if (!api.Allow(ShareBucket, SharesPerMinute)) return api.RateLimited(ShareBucket, SharesPerMinute);
            string text = Report(call.Request, note);
            IAdminHost host = api.Host;
            AdminHostInfo info = api.DescribeHost();
            if (host == null || info == null || !info.CanShareText)
            {
                var details = new Dictionary<string, object>(StringComparer.Ordinal);
                details["error"] = "not_supported";
                details["message"] = "This build cannot share text: copy the report instead.";
                details["text"] = text;
                return AdminJson.Json(details, HttpStatus.NotImplemented);
            }
            AdminShareResult share;
            try
            {
                share = host.ShareText("MCVS Offline report", text);
            }
            catch (Exception ex)
            {
                log.Warn("admin: sharing the text report failed", ex);
                share = AdminShareResult.None("Sharing failed: " + ex.Message);
            }
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["share"] = SavesApi.ShareJson(share);
            body["chars"] = (long)text.Length;
            return AdminJson.Ok(body);
        }

        /// <summary>
        /// The plain-text report (design §4.7): status summary, the last 300 WARN+ entries of server.log, bootstrap.log and
        /// game.log, the newest crash file's first 200 lines; at most 100,000 characters, redacted.
        /// </summary>
        private string Report(HttpRequest request, string note)
        {
            var sb = new StringBuilder();
            sb.Append("MCVS Offline report, ").Append(TimeFormat.Iso8601(context.Clock.UtcNow)).Append('\n');
            if (!string.IsNullOrEmpty(note)) sb.Append("Note: ").Append(note).Append('\n');
            sb.Append('\n').Append("== Status ==\n").Append(StatusSummary(request)).Append('\n');

            List<string> dirs = Directories(api.DescribeHost());
            List<LogFile> files = Files(dirs);
            foreach (string name in new[] { "server.log", "bootstrap.log", "game.log" })
            {
                LogFile file = files.Find(f => f.Name == name);
                if (file == null) continue;
                sb.Append("== ").Append(file.EntryName).Append(" (last warnings and errors) ==\n");
                try
                {
                    foreach (LogTailEntry e in LogTail.Read(file.Path, ReportProblemEntries, LogLevel.Warn, null).Entries)
                        sb.Append(e.Time).Append(' ').Append(e.Level).Append(" [").Append(e.Category).Append("] ").Append(e.Message).Append('\n');
                }
                catch (Exception ex)
                {
                    sb.Append("(unreadable: ").Append(ex.Message).Append(")\n");
                }
                sb.Append('\n');
            }
            LogFile crash = null;
            foreach (LogFile f in files)
            {
                if (f.Kind == "crash" && (crash == null || f.ModifiedUtc > crash.ModifiedUtc)) crash = f;
            }
            if (crash != null)
            {
                sb.Append("== ").Append(crash.EntryName).Append(" (first ").Append(ReportCrashLines).Append(" lines) ==\n");
                try
                {
                    byte[] head = AdminFiles.ReadHead(crash.Path, 256 * 1024);
                    string[] lines = AdminFiles.Utf8(head, 0, head.Length).Split('\n');
                    for (int i = 0; i < lines.Length && i < ReportCrashLines; i++) sb.Append(lines[i].TrimEnd('\r')).Append('\n');
                    if (lines.Length > ReportCrashLines) sb.Append("…\n");
                }
                catch (Exception ex)
                {
                    sb.Append("(unreadable: ").Append(ex.Message).Append(")\n");
                }
            }
            string text = LogRedaction.Redact(sb.ToString());
            if (text.Length > ReportMaxChars) text = text.Substring(0, ReportMaxChars - 2) + "…\n";
            return text;
        }

        private string StatusSummary(HttpRequest request)
        {
            Dictionary<string, object> status;
            try
            {
                status = MiniJson.ParseObject(StatusJson(request));
            }
            catch (Exception)
            {
                return "(status unavailable)\n";
            }
            var sb = new StringBuilder();
            sb.Append("version ").Append(JsonValue.GetString(status, "version")).Append(" (build ").Append(JsonValue.GetString(status, "build")).Append(")\n");
            Line(sb, "server", JsonValue.GetObject(status, "server"), "started", "uptimeSeconds", "port", "bind", "lanMode", "instance");
            Line(sb, "host", JsonValue.GetObject(status, "host"), "platform", "sdkInt", "browser", "mirror", "watchdog", "restartVia", "freeBytes");
            Line(sb, "settings", JsonValue.GetObject(status, "settings"), "fallback", "error", "restartPending");
            Line(sb, "player", JsonValue.GetObject(status, "player"), "account", "source", "level", "revision", "modified", "lastGameSave", "hasGameProfile");
            Dictionary<string, object> staged = JsonValue.GetObject(status, "staged");
            if (staged != null)
            {
                Line(sb, "staged", JsonValue.GetObject(staged, "pending"), "id", "kind", "plan", "attempts");
                Line(sb, "probation", JsonValue.GetObject(staged, "probation"), "id", "kind", "startsWithoutSave");
                Line(sb, "last operation", JsonValue.GetObject(staged, "last"), "id", "kind", "result", "at", "message");
            }
            Line(sb, "exit", JsonValue.GetObject(status, "exit"), "state");
            List<object> problems = JsonValue.GetArray(status, "problems");
            if (problems != null && problems.Count > 0)
            {
                sb.Append("recent problems:\n");
                foreach (object p in problems)
                {
                    var o = p as Dictionary<string, object>;
                    if (o == null) continue;
                    sb.Append("  ").Append(JsonValue.GetString(o, "time")).Append(' ').Append(JsonValue.GetString(o, "level")).Append(" [")
                      .Append(JsonValue.GetString(o, "category")).Append("] ").Append(JsonValue.GetString(o, "message")).Append('\n');
                }
            }
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, string label, Dictionary<string, object> o, params string[] keys)
        {
            if (o == null) return;
            sb.Append(label).Append(':');
            foreach (string key in keys)
            {
                object value;
                if (!o.TryGetValue(key, out value) || value == null) continue;
                sb.Append(' ').Append(key).Append('=').Append(value is string ? (string)value : MiniJson.Serialize(value));
            }
            sb.Append('\n');
        }

        // ------------------------------------------------------------------ helpers

        private string ShareFolder()
        {
            return AdminFiles.Folder(context, "admin/share");
        }

        /// <summary>^mcvs-logs-\d{8}-\d{6}(-\d{1,3})?\.zip$: the names this module gives its zips.</summary>
        internal static bool IsZipName(string name)
        {
            const string prefix = "mcvs-logs-";
            if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal)) return false;
            string middle = name.Substring(prefix.Length, name.Length - prefix.Length - 4);
            if (middle.Length < 15 || middle[8] != '-') return false;
            for (int i = 0; i < middle.Length; i++)
            {
                char c = middle[i];
                if (i == 8 || (i == 15 && c == '-')) continue;
                if (c < '0' || c > '9') return false;
            }
            return middle.Length == 15 || (middle.Length > 16 && middle.Length <= 19 && middle[15] == '-');
        }

        /// <summary>
        /// The GET status body with ?storage=1, asked from the status route with the caller's own session (so the zip shows
        /// exactly what the page shows).
        /// </summary>
        private string StatusJson(HttpRequest original)
        {
            try
            {
                var headers = new HttpHeaders();
                foreach (string name in new[] { "Host", "Cookie", "User-Agent" })
                {
                    string value = original.Headers.Get(name);
                    if (value != null) headers.Add(name, value);
                }
                var request = new HttpRequest("GET", "/" + AdminApi.Prefix + "status?storage=1", headers, null);
                request.RemoteEndPoint = original.RemoteEndPoint;
                HttpResponse response = router.Dispatch(request);
                if (response.StatusCode == HttpStatus.Ok) return response.BodyText;
                return "{\"error\": \"status answered " + response.StatusCode.ToString(CultureInfo.InvariantCulture) + "\"}";
            }
            catch (Exception ex)
            {
                return MiniJson.Serialize(new Dictionary<string, object>(StringComparer.Ordinal) { { "error", ex.Message } });
            }
        }

        /// <summary>Settings.Snapshot() without superPassword.</summary>
        private string SettingsJson()
        {
            Dictionary<string, object> snapshot = context.Settings.Snapshot();
            JsonPath.RemoveKey(snapshot, "superPassword");
            return MiniJson.Serialize(snapshot, true);
        }
    }
}
