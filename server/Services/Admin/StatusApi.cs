using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Auth;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// <c>GET status[?storage=1]</c> (design §4.3): version, server, host, settings file, current player, staged
    /// operations (from admin-saves through <see cref="AdminApi.SetStatusSection"/>), the save/restart sequence, recent
    /// problems of the server and host loggers, the CRM document and, on demand, the storage usage of the data folder.
    /// While a save/restart sequence runs no account lock is taken (the player's level and revision are left out).
    /// </summary>
    internal sealed class StatusApi
    {
        public const int MaxProblems = 20;
        public const int MaxProblemChars = 2000;
        public const int ScanMaxFiles = 20000;
        public const int ScanMaxMs = 500;

        private readonly AdminApi api;
        private readonly SettingsApi settings;

        public StatusApi(AdminApi api, SettingsApi settings)
        {
            this.api = api;
            this.settings = settings;
        }

        public void Register()
        {
            api.Get("status", "admin.status", call => AdminJson.Ok(Build(call.Query("storage") == "1")));
        }

        public Dictionary<string, object> Build(bool storage)
        {
            ServerContext context = api.Context;
            DateTime now = context.Clock.UtcNow;
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["version"] = McvsVersion.Current;
            body["build"] = McvsVersion.Build;

            var server = new Dictionary<string, object>(StringComparer.Ordinal);
            server["started"] = TimeFormat.Iso8601Millis(context.StartedUtc);
            server["uptimeSeconds"] = (long)Math.Max(0, (now - context.StartedUtc).TotalSeconds);
            server["instance"] = context.InstanceId;
            server["port"] = (long)context.Port;
            server["bind"] = context.Settings.Bind;
            server["lanMode"] = context.Settings.LanMode;
            server["hostCheck"] = context.Settings.GetBool("security.hostCheck", true);
            server["dataDir"] = context.DataDir;
            server["dataPack"] = context.Content.DataPack == null ? null : context.Content.DataPack.DataDirectory;
            body["server"] = server;

            AdminHostInfo info = api.DescribeHost();
            body["host"] = info == null ? null : Host(info);

            var file = new Dictionary<string, object>(StringComparer.Ordinal);
            file["file"] = context.Settings.FilePath;
            file["fallback"] = context.Settings.IsFallback;
            file["error"] = context.Settings.LoadError;
            file["restartPending"] = (long)settings.RestartPending().Count;
            body["settings"] = file;

            body["player"] = Player();

            object staged = Section("staged");
            if (staged == null)
            {
                var empty = new Dictionary<string, object>(StringComparer.Ordinal);
                empty["pending"] = null;
                empty["probation"] = null;
                empty["last"] = null;
                staged = empty;
            }
            body["staged"] = staged;
            foreach (var section in api.StatusSections)
            {
                if (section.Key != "staged" && !body.ContainsKey(section.Key)) body[section.Key] = Section(section.Key);
            }

            var exit = new Dictionary<string, object>(StringComparer.Ordinal);
            exit["state"] = api.ExitState;
            exit["last"] = api.Exit == null ? null : api.Exit.Last;
            body["exit"] = exit;

            body["problems"] = Problems();
            body["crm"] = Crm();
            if (storage) body["storage"] = Storage();
            return body;
        }

        private static Dictionary<string, object> Host(AdminHostInfo info)
        {
            var host = new Dictionary<string, object>(StringComparer.Ordinal);
            host["platform"] = info.Platform;
            host["sdkInt"] = (long)info.SdkInt;
            host["deviceId"] = info.DeviceId;
            host["logsDir"] = info.LogsDir;
            host["fallbackLogsDir"] = info.FallbackLogsDir;
            host["publicLogsDir"] = info.PublicLogsDir;
            host["mirror"] = info.MirrorStatus;
            host["watchdog"] = info.WatchdogStatus;
            host["browser"] = info.BrowserStatus;
            host["browserOpen"] = info.BrowserOpen;
            host["freeBytes"] = info.FreeBytes;
            host["gameCredential"] = AdminApi.MaskCredential(info.GameCredential);
            host["restartVia"] = info.RestartVia;
            host["relaunchUnconfirmed"] = info.RelaunchUnconfirmed;
            host["notifications"] = info.Notifications;
            host["graphics"] = info.Graphics;
            host["hdTextures"] = info.HdTextures;
            host["hdInstalled"] = info.HdInstalled;
            var missing = new List<object>();
            if (info.MissingJni != null) foreach (string m in info.MissingJni) missing.Add(m);
            host["missingJni"] = missing;
            var caps = new Dictionary<string, object>(StringComparer.Ordinal);
            caps["openPage"] = info.CanOpenPage;
            caps["shareFile"] = info.CanShareFile;
            caps["saveToDownloads"] = info.CanSaveToDownloads;
            caps["shareText"] = info.CanShareText;
            caps["clipboard"] = info.CanCopy;
            caps["toast"] = info.CanToast;
            caps["save"] = info.CanSave;
            caps["exit"] = info.CanExit;
            host["capabilities"] = caps;
            return host;
        }

        private Dictionary<string, object> Player()
        {
            CurrentPlayer player = api.CurrentPlayer();
            if (player == null || player.AccountId == null) return null;
            ServerContext context = api.Context;
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["account"] = player.AccountId;
            o["fedId"] = McvsConstants.FedIdPrefix + player.AccountId;
            Account account = context.Accounts.FindById(player.AccountId);
            o["alias"] = account == null ? null : JsonValue.AsString(JsonValue.Get(account.Data, AliasRegistry.DataKey));
            o["source"] = player.Source;
            DateTime? lastSave = api.Saves.LastGameSave(player.AccountId);
            o["lastGameSave"] = lastSave.HasValue ? TimeFormat.Iso8601Millis(lastSave.Value) : null;
            if (api.ExitState != ExitSequence.Idle) return o; // no account lock while the game saves or exits

            var store = context.Features.Get<LocalProfileStore>();
            if (store == null) return o;
            try
            {
                ProfileDocument doc = store.ReadDocument(player.AccountId);
                o["hasGameProfile"] = SeshatOperations.HasGameProfile(doc);
                if (doc.IsStored)
                {
                    var access = context.Features.Get<IProfileAccess>();
                    o["level"] = access == null ? null : JsonValue.AsLong(access.Read(player.AccountId, "level"));
                    o["revision"] = doc.Revision;
                    o["modified"] = doc.Modified;
                }
            }
            catch (Exception ex)
            {
                o["error"] = ex.Message;
            }
            return o;
        }

        private object Section(string name)
        {
            foreach (var section in api.StatusSections)
            {
                if (section.Key != name) continue;
                try
                {
                    return section.Value();
                }
                catch (Exception ex)
                {
                    api.Log.Warn("admin: status section '" + name + "' failed", ex);
                    return null;
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ problems

        /// <summary>The newest WARN/ERROR lines of the server and host loggers, newest first, at most 20.</summary>
        private List<object> Problems()
        {
            var all = new List<KeyValuePair<string, Dictionary<string, object>>>();
            MemoryLogSink sink = api.Context.Problems;
            if (sink != null)
            {
                foreach (LogEntry entry in sink.Snapshot())
                {
                    if (entry.Level < LogLevel.Warn) continue;
                    string message = entry.Message ?? "";
                    if (entry.Exception != null) message += " (" + entry.Exception.GetType().Name + ": " + entry.Exception.Message + ")";
                    string time = TimeFormat.Iso8601Millis(entry.TimeUtc);
                    all.Add(new KeyValuePair<string, Dictionary<string, object>>(
                        time, Problem(time, LogEntry.LevelName(entry.Level).Trim(), entry.Category, message, "server")));
                }
            }
            IAdminHost host = api.Host;
            if (host != null)
            {
                List<string> lines = null;
                try
                {
                    lines = host.RecentProblems(MaxProblems);
                }
                catch (Exception ex)
                {
                    api.Log.Debug("admin: the host's RecentProblems failed: " + ex.Message);
                }
                if (lines != null)
                {
                    // A line without a time stamp sorts as "now": the host gives its lines newest first.
                    string now = TimeFormat.Iso8601Millis(api.Context.Clock.UtcNow);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        Dictionary<string, object> parsed = ParseLine(line);
                        all.Add(new KeyValuePair<string, Dictionary<string, object>>(JsonValue.GetString(parsed, "time") ?? now, parsed));
                    }
                }
            }
            // Newest first; a stable sort keeps the sinks' own order for equal times.
            var indexed = new List<KeyValuePair<int, KeyValuePair<string, Dictionary<string, object>>>>();
            for (int i = 0; i < all.Count; i++) indexed.Add(new KeyValuePair<int, KeyValuePair<string, Dictionary<string, object>>>(i, all[i]));
            indexed.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(b.Value.Key, a.Value.Key);
                return c != 0 ? c : b.Key.CompareTo(a.Key);
            });
            var result = new List<object>();
            foreach (var item in indexed)
            {
                if (result.Count >= MaxProblems) break;
                result.Add(item.Value.Value);
            }
            return result;
        }

        private static Dictionary<string, object> Problem(string time, string level, string category, string message, string source)
        {
            string text = LogRedaction.Redact(message ?? "");
            if (text.Length > MaxProblemChars) text = text.Substring(0, MaxProblemChars) + "…";
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["time"] = time;
            o["level"] = level;
            o["category"] = category;
            o["message"] = text;
            o["source"] = source;
            return o;
        }

        /// <summary>"2026-10-25T10:00:00.123Z WARN  [cat] message..." (LogEntry.Format) → its parts; anything else as a WARN.</summary>
        internal static Dictionary<string, object> ParseLine(string line)
        {
            string time = null, level = "WARN", category = "host", message = line;
            DateTime parsed;
            if (line.Length > 31 && line[24] == ' '
                && DateTime.TryParseExact(line.Substring(0, 24), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                time = line.Substring(0, 24);
                level = line.Substring(25, 5).Trim();
                string rest = line.Substring(30).TrimStart();
                if (rest.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = rest.IndexOf(']');
                    if (close > 0)
                    {
                        category = rest.Substring(1, close - 1);
                        rest = rest.Substring(close + 1).TrimStart();
                    }
                }
                message = rest;
            }
            return Problem(time, level, category, message, "host");
        }

        // ------------------------------------------------------------------ CRM

        private Dictionary<string, object> Crm()
        {
            var cache = api.Context.Features.Get<CrmDocumentCache>();
            if (cache == null) return null;
            try
            {
                CrmDocument doc = cache.Get(0);
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["built"] = TimeFormat.Iso8601Millis(doc.BuiltUtc);
                o["chars"] = (long)doc.Json.Length;
                o["problems"] = (long)doc.Problems.Count;
                // The Cosmetic crate (docs/design/economy-collector.md §2.12): the phase the document carries and why.
                CrmCollector collector = doc.Collector;
                o["collectorPhase"] = doc.CollectorPhase.ToString();
                o["collectorReason"] = collector == null ? null
                    : collector.Failure ?? (collector.Status != null ? collector.Status.Reason : null);
                return o;
            }
            catch (Exception ex)
            {
                api.Log.Warn("admin: status: the CRM document is unavailable", ex);
                return null;
            }
        }

        // ------------------------------------------------------------------ storage usage

        private static readonly string[] Categories = { "profiles", "backups", "blobs", "messages", "logs", "admin", "other" };

        /// <summary>Files and bytes per kind under &lt;data&gt; (never the game's Data/), at most 20,000 files or 500 ms.</summary>
        private Dictionary<string, object> Storage()
        {
            string root = api.Context.DataDir;
            string pack = api.Context.Content.DataPack == null ? null : Path.GetFullPath(api.Context.Content.DataPack.DataDirectory);
            var files = new long[Categories.Length];
            var bytes = new long[Categories.Length];
            long total = 0;
            int count = 0;
            bool capped = false;
            int deadline = Environment.TickCount + ScanMaxMs;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0 && !capped)
            {
                string dir = pending.Pop();
                if (pack != null && string.Equals(Path.GetFullPath(dir), pack, StringComparison.Ordinal)) continue;
                string[] entries;
                try
                {
                    entries = Directory.GetFiles(dir);
                    foreach (string sub in Directory.GetDirectories(dir)) pending.Push(sub);
                }
                catch (Exception)
                {
                    continue;
                }
                int category = CategoryOf(root, dir);
                foreach (string path in entries)
                {
                    if (count >= ScanMaxFiles || Environment.TickCount - deadline > 0)
                    {
                        capped = true;
                        break;
                    }
                    long length;
                    try
                    {
                        length = new FileInfo(path).Length;
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    count++;
                    files[category]++;
                    bytes[category] += length;
                    total += length;
                }
            }
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i < Categories.Length; i++)
            {
                var c = new Dictionary<string, object>(StringComparer.Ordinal);
                c["files"] = files[i];
                c["bytes"] = bytes[i];
                o[Categories[i]] = c;
            }
            o["total"] = total;
            o["scanCapped"] = capped;
            return o;
        }

        private static int CategoryOf(string root, string dir)
        {
            string relative = dir.Length > root.Length ? dir.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/') + "/" : "";
            if (relative.StartsWith("storage/profiles/", StringComparison.Ordinal)) return 0;
            if (relative.StartsWith("storage/backups/", StringComparison.Ordinal)) return 1;
            if (relative.StartsWith("storage/data/", StringComparison.Ordinal)) return 2;
            if (relative.StartsWith("messages/", StringComparison.Ordinal)) return 3;
            if (relative.StartsWith("logs/", StringComparison.Ordinal)) return 4;
            if (relative.StartsWith("admin/", StringComparison.Ordinal)) return 5;
            return 6;
        }
    }
}
