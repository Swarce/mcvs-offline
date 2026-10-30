using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Assets;
using McvsOffline.Server.Services.Crm.Collector;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// <c>GET/POST settings</c>, <c>settings/reset</c>, <c>settings/file</c>, <c>settings/repair</c> and <c>codes</c>
    /// (design §4.4). The page writes the server's copy of settings.json (<c>ServerContext.Settings</c>: Set + Save, never a
    /// reload); one lock serializes every write.
    /// <para>
    /// The file check: the length and last-write time of settings.json are recorded at boot and after each of this
    /// module's saves; a file changed on disk since (edited by hand) answers 409 <c>file_changed</c> unless the request says
    /// <c>force</c>, which first copies the file to settings.json.bak-&lt;stamp&gt; (newest 3 kept).
    /// </para>
    /// </summary>
    internal sealed class SettingsApi
    {
        public const int MaxFileBytes = 256 * 1024;
        public const int KeptOverwrittenCopies = 3;

        private readonly AdminApi api;
        private readonly object writeGate = new object();
        private readonly Dictionary<string, string> bootValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private string recordedStamp;

        public SettingsApi(AdminApi api)
        {
            this.api = api;
            // The values of the restart keys as this process uses them, for restartPending.
            foreach (SettingDefinition def in SettingsSchema.Keys)
            {
                if (def.Apply == SettingsSchema.Restart) bootValues[def.Key] = Serialize(Settings.Get(def.Key));
            }
            recordedStamp = DiskStamp();
        }

        private Settings Settings
        {
            get { return api.Context.Settings; }
        }

        public void Register()
        {
            api.Get("settings", "admin.settings.get", call => AdminJson.Ok(Describe()));
            api.Post("settings", "admin.settings.set", Set, 64 * 1024);
            api.Post("settings/reset", "admin.settings.reset", Reset, 16 * 1024);
            api.Get("settings/file", "admin.settings.file", FileText);
            api.Post("settings/repair", "admin.settings.repair", Repair, 1024);
            api.Get("codes", "admin.codes", call => AdminJson.Ok(Codes()));
        }

        /// <summary>The restart keys whose value differs from the one this process started with.</summary>
        public List<string> RestartPending()
        {
            var keys = new List<string>();
            foreach (var kv in bootValues)
            {
                if (Serialize(Settings.Get(kv.Key)) != kv.Value) keys.Add(kv.Key);
            }
            return keys;
        }

        // ------------------------------------------------------------------ GET settings

        public Dictionary<string, object> Describe()
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["file"] = Settings.FilePath;
            body["fallback"] = Settings.IsFallback;
            body["error"] = Settings.LoadError;
            lock (writeGate) body["fingerprint"] = recordedStamp;
            body["restartPending"] = ToObjects(RestartPending());

            var groups = new List<object>();
            // The platform's view (docs/design/pc-port.md §4.6): the PC host leaves out the Android-only keys.
            AdminHostInfo host = api.DescribeHost();
            foreach (SettingGroupView view in SettingsSchema.GroupsFor(host == null ? null : host.Platform))
            {
                SettingGroup group = view.Group;
                var keys = new List<object>();
                if (group.Id == "advanced")
                {
                    foreach (var kv in view.Advanced)
                    {
                        Dictionary<string, object> entry = DescribeAdvanced(kv.Key, kv.Value);
                        string help;
                        if (view.AdvancedHelp.TryGetValue(kv.Key, out help)) entry["help"] = help;
                        keys.Add(entry);
                    }
                }
                else
                {
                    foreach (SettingDefinition def in view.Keys)
                    {
                        Dictionary<string, object> entry = SettingsSchema.Describe(def);
                        string invalidNote;
                        entry["value"] = SettingsSchema.Read(def, Settings, out invalidNote);
                        if (invalidNote != null)
                        {
                            entry["invalidStored"] = true;
                            entry["invalidNote"] = invalidNote;
                        }
                        keys.Add(entry);
                    }
                }
                var g = new Dictionary<string, object>(StringComparer.Ordinal);
                g["id"] = group.Id;
                g["label"] = group.Label;
                g["keys"] = keys;
                groups.Add(g);
            }
            body["groups"] = groups;

            var presets = new List<object>();
            foreach (SettingPreset preset in SettingsSchema.Presets)
            {
                var p = new Dictionary<string, object>(StringComparer.Ordinal);
                p["id"] = preset.Id;
                p["label"] = preset.Label;
                p["values"] = new Dictionary<string, object>(preset.Values, StringComparer.Ordinal);
                presets.Add(p);
            }
            body["presets"] = presets;
            return body;
        }

        private Dictionary<string, object> DescribeAdvanced(string key, string label)
        {
            object value = Settings.Get(key);
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["key"] = key;
            o["label"] = label;
            o["type"] = TypeOf(value);
            o["editable"] = false;
            switch (key)
            {
                case "superPassword":
                    o["display"] = "•••• (" + (Settings.SuperPassword == Settings.DefaultSuperPassword ? "default" : "custom") + ")";
                    return o;
                case "crm.overlay":
                    o["display"] = value == null ? "not set" : "set, " + MiniJson.Serialize(value).Length.ToString(CultureInfo.InvariantCulture) + " characters";
                    return o;
                case "crm.pointcutActions":
                case "assets.coupons":
                {
                    int count = value is List<object> ? ((List<object>)value).Count
                        : value is Dictionary<string, object> ? ((Dictionary<string, object>)value).Count : 0;
                    o["display"] = value == null ? "none" : count.ToString(CultureInfo.InvariantCulture);
                    return o;
                }
            }
            if (value is Dictionary<string, object> || value is List<object>)
            {
                o["display"] = MiniJson.Serialize(value);
                return o;
            }
            o["value"] = value;
            o["display"] = value == null ? "(default)" : value is string ? (string)value : MiniJson.Serialize(value);
            return o;
        }

        private static string TypeOf(object value)
        {
            if (value is bool) return SettingsSchema.TypeBool;
            if (value is long) return SettingsSchema.TypeInt;
            if (value is double) return SettingsSchema.TypeNumber;
            if (value is string) return SettingsSchema.TypeString;
            if (value is List<object>) return "array";
            if (value is Dictionary<string, object>) return "object";
            return "unknown";
        }

        // ------------------------------------------------------------------ POST settings / settings/reset

        private HttpResponse Set(AdminCall call)
        {
            var changes = JsonValue.GetObject(call.Body, "changes");
            if (changes == null) return AdminJson.Error(HttpStatus.BadRequest, "bad_request", "\"changes\" must be an object.");
            return Apply(changes, call.Body);
        }

        private HttpResponse Reset(AdminCall call)
        {
            List<object> keys = JsonValue.GetArray(call.Body, "keys");
            if (keys == null) return AdminJson.Error(HttpStatus.BadRequest, "bad_request", "\"keys\" must be a list of setting names.");
            var changes = new Dictionary<string, object>(StringComparer.Ordinal);
            var notEditable = new List<object>();
            foreach (object k in keys)
            {
                string key = k as string;
                SettingDefinition def = key == null ? null : SettingsSchema.Find(key);
                if (def == null) notEditable.Add(key);
                else changes[key] = JsonValue.DeepClone(def.Default);
            }
            if (notEditable.Count > 0) return NotEditable(notEditable);
            return Apply(changes, call.Body);
        }

        /// <summary>The write pipeline of §4.4 (steps 1-6): all or nothing.</summary>
        private HttpResponse Apply(Dictionary<string, object> changes, Dictionary<string, object> body)
        {
            lock (writeGate)
            {
                // 1. A broken settings.json is repaired first (the page shows the repair panel).
                if (Settings.IsFallback)
                    return AdminJson.Error(HttpStatus.Conflict, "settings_fallback",
                                           "settings.json could not be read. Repair it first (Settings shows how).");

                // 2. Allowlist and validation; nothing is written when anything is wrong.
                var notEditable = new List<object>();
                foreach (var kv in changes)
                {
                    if (SettingsSchema.Find(kv.Key) == null) notEditable.Add(kv.Key);
                }
                if (notEditable.Count > 0) return NotEditable(notEditable);
                var normalized = new Dictionary<string, object>(StringComparer.Ordinal);
                var problems = new List<object>();
                foreach (var kv in changes)
                {
                    SettingDefinition def = SettingsSchema.Find(kv.Key);
                    object value;
                    string problem;
                    if (SettingsSchema.Validate(def, kv.Value, out value, out problem))
                    {
                        normalized[kv.Key] = value;
                    }
                    else
                    {
                        var p = new Dictionary<string, object>(StringComparer.Ordinal);
                        p["key"] = kv.Key;
                        p["message"] = problem;
                        problems.Add(p);
                    }
                }
                if (problems.Count > 0)
                    return AdminJson.Error(HttpStatus.BadRequest, "invalid", "Some values are not valid.", AdminJson.Details("problems", problems));

                // 3. The file on disk must still be the one this module last saw.
                bool force = JsonValue.GetBool(body, "force");
                string fingerprint = JsonValue.GetString(body, "fingerprint");
                string onDisk = DiskStamp();
                bool changedOnDisk = onDisk != recordedStamp;
                bool stale = fingerprint != null && fingerprint != recordedStamp;
                string overwrittenCopy = null;
                if ((changedOnDisk || stale) && !force)
                {
                    return AdminJson.Error(HttpStatus.Conflict, "file_changed",
                                           "settings.json was changed outside this page since it was loaded.",
                                           AdminJson.Details("fingerprint", onDisk));
                }
                var warnings = new List<object>();
                if (changedOnDisk && force)
                {
                    try
                    {
                        overwrittenCopy = KeepOverwrittenCopy();
                    }
                    catch (Exception ex)
                    {
                        api.Log.Warn("admin: could not copy settings.json before overwriting it", ex);
                        return AdminJson.Error(HttpStatus.InternalServerError, "save_failed",
                                               "settings.json could not be copied before overwriting it: " + ex.Message);
                    }
                }

                // 4. A backup of the current player's save before a risky change.
                var oldValues = new Dictionary<string, object>(StringComparer.Ordinal);
                bool backupWanted = false;
                foreach (var kv in normalized)
                {
                    SettingDefinition def = SettingsSchema.Find(kv.Key);
                    string invalidNote;
                    object old = SettingsSchema.Read(def, Settings, out invalidNote);
                    oldValues[kv.Key] = old;
                    if (def.BackupBefore == "any" && !JsonValue.DeepEquals(old, kv.Value)) backupWanted = true;
                    if (def.BackupBefore == "enable" && Equals(old, false) && Equals(kv.Value, true)) backupWanted = true;
                }
                object backup = null;
                if (backupWanted) backup = TakeBackup(warnings);

                // 5. Write everything or nothing.
                try
                {
                    Settings.SetAndSave(Writes(normalized));
                }
                catch (Exception ex)
                {
                    api.Log.Error("admin: saving settings.json failed", ex);
                    return AdminJson.Error(HttpStatus.InternalServerError, "save_failed", "settings.json could not be saved: " + ex.Message);
                }
                recordedStamp = DiskStamp();

                // 6. The answer, and one log line per change.
                var applied = new List<object>();
                bool restartRequired = false;
                foreach (var kv in normalized)
                {
                    SettingDefinition def = SettingsSchema.Find(kv.Key);
                    var a = new Dictionary<string, object>(StringComparer.Ordinal);
                    a["key"] = kv.Key;
                    a["apply"] = def.Apply;
                    applied.Add(a);
                    if (def.Apply == SettingsSchema.Restart) restartRequired = true;
                    api.Log.Info("admin: settings " + kv.Key + " " + Show(oldValues[kv.Key]) + " -> " + Show(kv.Value) + " (" + def.Apply + ")");
                }
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                result["ok"] = true;
                result["applied"] = applied;
                result["restartRequired"] = restartRequired;
                result["backup"] = backup;
                result["warnings"] = warnings;
                result["overwrittenCopy"] = overwrittenCopy;
                result["settings"] = Describe();
                return AdminJson.Ok(result);
            }
        }

        /// <summary>
        /// What step 5 writes: the changes, except that a Cosmetic crate key written while <c>economy.collector</c> is not
        /// an object (the shorthand <c>"collector": false</c>, or a value the collector cannot read, which is off) first
        /// turns it into <c>{"enabled": &lt;what the collector read&gt;}</c>. Writing the sub-key alone would replace the
        /// value with an object without <c>enabled</c>, and so switch the crate back on.
        /// </summary>
        private Dictionary<string, object> Writes(Dictionary<string, object> normalized)
        {
            object collector = Settings.Get(CollectorOptions.SettingsPath);
            bool touchesCollector = false;
            foreach (string key in normalized.Keys)
            {
                if (key.StartsWith(CollectorOptions.SettingsPath + ".", StringComparison.Ordinal)) touchesCollector = true;
            }
            if (!touchesCollector || collector == null || collector is Dictionary<string, object>) return normalized;

            var block = new Dictionary<string, object>(StringComparer.Ordinal);
            block["enabled"] = CollectorOptions.FromSettings(Settings, null).Enabled;
            api.Log.Info("admin: settings " + CollectorOptions.SettingsPath + " " + MiniJson.Serialize(collector) + " is written as an object "
                         + "with enabled = " + ((bool)block["enabled"] ? "true" : "false") + " (the kill switch kept)");
            var writes = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in normalized)
            {
                // The catalogue's collector keys are direct children of economy.collector.
                if (kv.Key.StartsWith(CollectorOptions.SettingsPath + ".", StringComparison.Ordinal))
                    block[kv.Key.Substring(CollectorOptions.SettingsPath.Length + 1)] = kv.Value;
                else writes[kv.Key] = kv.Value;
            }
            writes[CollectorOptions.SettingsPath] = block;
            return writes;
        }

        private object TakeBackup(List<object> warnings)
        {
            Func<string, string, BackupInfo> create = api.CreateBackup;
            if (create == null)
            {
                warnings.Add("Backups are not available in this build: no backup was made.");
                return null;
            }
            CurrentPlayer player = api.CurrentPlayer();
            if (player == null || player.AccountId == null)
            {
                warnings.Add("The current player is not known: no backup was made.");
                return null;
            }
            try
            {
                BackupInfo info = create(player.AccountId, "presettings");
                if (info == null)
                {
                    warnings.Add("There is no save of the current player yet: no backup was made.");
                    return null;
                }
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["id"] = info.Id;
                api.Log.Info("admin: settings: backup " + info.Id + " of " + player.AccountId + " before the change");
                return o;
            }
            catch (Exception ex)
            {
                api.Log.Warn("admin: settings: the backup before the change failed", ex);
                warnings.Add("The backup failed (" + ex.Message + "); the change was saved anyway.");
                return null;
            }
        }

        private static HttpResponse NotEditable(List<object> keys)
        {
            return AdminJson.Error(HttpStatus.BadRequest, "not_editable", "These settings cannot be changed from this page.",
                                   AdminJson.Details("keys", keys));
        }

        // ------------------------------------------------------------------ settings/file, settings/repair

        private HttpResponse FileText(AdminCall call)
        {
            if (!Settings.IsFallback || Settings.FilePath == null)
                return AdminJson.Error(HttpStatus.NotFound, "not_found", "settings.json is readable.");
            string text;
            try
            {
                byte[] bytes = ReadPrefix(Settings.FilePath, MaxFileBytes + 1);
                bool truncated = bytes.Length > MaxFileBytes;
                text = new UTF8Encoding(false, false).GetString(bytes, 0, Math.Min(bytes.Length, MaxFileBytes));
                if (truncated) text += "\n[... the file is longer than 256 KiB; the rest is not shown]\n";
            }
            catch (Exception ex)
            {
                text = "(settings.json cannot be read: " + ex.Message + ")";
            }
            return AdminJson.WithApiHeaders(HttpResponse.Text(MaskSuperPassword(text)));
        }

        private HttpResponse Repair(AdminCall call)
        {
            if (JsonValue.GetString(call.Body, "confirm") != "move-aside")
                return AdminJson.Error(HttpStatus.BadRequest, "invalid", "Confirm with \"move-aside\".");
            lock (writeGate)
            {
                if (!Settings.IsFallback)
                    return AdminJson.Error(HttpStatus.Conflict, "invalid", "settings.json is readable: there is nothing to repair.");
                string moved;
                try
                {
                    moved = Settings.MoveAsideAndReset(api.Context.Clock.UtcNow);
                }
                catch (Exception ex)
                {
                    api.Log.Error("admin: repairing settings.json failed", ex);
                    return AdminJson.Error(HttpStatus.InternalServerError, "save_failed", "settings.json could not be repaired: " + ex.Message);
                }
                recordedStamp = DiskStamp();
                string name = moved == null ? null : Path.GetFileName(moved);
                api.Log.Warn("admin: the unreadable settings.json was moved aside" + (name == null ? "" : " to " + name)
                             + "; the defaults were saved (the host adds its keys at the next start)");
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                result["ok"] = true;
                result["movedTo"] = name;
                result["restartRequired"] = true;
                return AdminJson.Ok(result);
            }
        }

        /// <summary>
        /// Every <c>"superPassword"</c> member's string value replaced by <c>"***"</c>: a character scan for the name, ':'
        /// and a JSON string literal, so it also works on a file that does not parse.
        /// </summary>
        internal static string MaskSuperPassword(string text)
        {
            const string name = "\"superPassword\"";
            if (text == null || text.IndexOf(name, StringComparison.Ordinal) < 0) return text;
            var sb = new StringBuilder(text.Length);
            int copied = 0;
            int at = text.IndexOf(name, StringComparison.Ordinal);
            while (at >= 0)
            {
                int i = at + name.Length;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i < text.Length && text[i] == ':')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i < text.Length && text[i] == '"')
                    {
                        int end = i + 1;
                        while (end < text.Length && text[end] != '"' && text[end] != '\n')
                        {
                            if (text[end] == '\\') end++;
                            end++;
                        }
                        end = Math.Min(end, text.Length);
                        bool closed = end < text.Length && text[end] == '"';
                        sb.Append(text, copied, i - copied).Append("\"***\"");
                        copied = closed ? end + 1 : end;
                        at = text.IndexOf(name, copied, StringComparison.Ordinal);
                        continue;
                    }
                }
                at = text.IndexOf(name, at + name.Length, StringComparison.Ordinal);
            }
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ codes

        private Dictionary<string, object> Codes()
        {
            var list = new List<object>();
            CouponCatalog catalog = CouponCatalog.Load(api.Context.Content, Settings, api.Log);
            foreach (CouponDescription coupon in catalog.Describe())
            {
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["code"] = coupon.Code;
                o["description"] = coupon.Description;
                o["numericSuffix"] = coupon.NumericSuffix;
                o["delivery"] = coupon.Delivery;   // "ledger": items arrive in batches; "inbox": one inbox message
                o["rewards"] = (long)coupon.Rewards;
                if (coupon.TooLargeForTheInbox)
                    o["warning"] = coupon.Rewards + " rewards are too many for one inbox message (at most "
                                   + CouponCatalog.MaxInboxRewards + "): redeeming it is refused. Split it or give it \"delivery\": \"ledger\".";
                list.Add(o);
            }
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["codes"] = list;
            body["pageCode"] = Settings.GetString("admin.redeemCode", "MCVSOFFLINE");
            body["enabled"] = Settings.GetBool("assets.couponsEnabled", true);
            return body;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>"&lt;length&gt;:&lt;last-write ticks&gt;" of settings.json, "missing" or "unknown".</summary>
        private string DiskStamp()
        {
            string path = Settings.FilePath;
            if (path == null) return "unknown";
            try
            {
                if (!System.IO.File.Exists(path)) return "missing";
                return new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) + ":"
                       + System.IO.File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>Copies settings.json to settings.json.bak-&lt;yyyyMMddTHHmmssZ&gt; (newest 3 kept); its name, or null.</summary>
        private string KeepOverwrittenCopy()
        {
            string path = Settings.FilePath;
            if (path == null || !System.IO.File.Exists(path)) return null;
            string stamp = api.Context.Clock.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string copy = path + ".bak-" + stamp;
            for (int n = 2; System.IO.File.Exists(copy); n++) copy = path + ".bak-" + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
            AtomicFile.WriteAllBytes(copy, System.IO.File.ReadAllBytes(path));
            string directory = Path.GetDirectoryName(path);
            var copies = new List<string>(Directory.GetFiles(directory, Path.GetFileName(path) + ".bak-*"));
            // Oldest first by write time (a name can be reused after its copy was pruned), then by name.
            copies.Sort((a, b) =>
            {
                int c = System.IO.File.GetLastWriteTimeUtc(a).CompareTo(System.IO.File.GetLastWriteTimeUtc(b));
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            for (int i = 0; i < copies.Count - KeptOverwrittenCopies; i++)
            {
                try { System.IO.File.Delete(copies[i]); } catch (Exception) { }
            }
            api.Log.Warn("admin: settings.json was edited outside the page; the page overwrites it (the edited file is kept as "
                         + Path.GetFileName(copy) + ")");
            return Path.GetFileName(copy);
        }

        private static byte[] ReadPrefix(string path, int max)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[(int)Math.Min(max, stream.Length)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == buffer.Length) return buffer;
                var shorter = new byte[read];
                Buffer.BlockCopy(buffer, 0, shorter, 0, read);
                return shorter;
            }
        }

        private static string Serialize(object value)
        {
            return value == null ? "null" : MiniJson.Serialize(value);
        }

        /// <summary>A value for a log line: numbers without a trailing ".0", strings quoted.</summary>
        private static string Show(object value)
        {
            if (value is double)
            {
                double d = (double)value;
                return Num.IsIntegralLong(d) ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
            }
            return Serialize(value);
        }

        private static List<object> ToObjects(List<string> values)
        {
            var list = new List<object>(values.Count);
            foreach (string v in values) list.Add(v);
            return list;
        }
    }
}
