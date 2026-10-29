using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>One game-folder original as the installer found it (<c>originals.&lt;file&gt;</c>).</summary>
    public sealed class InstallOriginal
    {
        public string File { get; set; }

        public string Sha256 { get; set; }

        public Guid Mvid { get; set; }
    }

    /// <summary>One file the installer wrote (<c>written.&lt;path&gt;</c>); null members are left out of the JSON.</summary>
    public sealed class InstallWritten
    {
        /// <summary>Relative to the game folder with '/': "McvsOffline/bin/Game.dll", "ModernCombatVersus.exe.config".</summary>
        public string Path { get; set; }

        public string Sha256 { get; set; }

        /// <summary>Assemblies: the written copy's MVID (a patched copy's DerivedMvid).</summary>
        public Guid? Mvid { get; set; }

        /// <summary>Assemblies: the assembly version (the config's codeBase version).</summary>
        public Version Version { get; set; }

        /// <summary>Patched copies: the patch ids applied to it, in order (empty: a byte copy of the original).</summary>
        public List<string> Patches { get; set; }

        /// <summary>The server DLL: McvsVersion.Current, when it has one.</summary>
        public string ServerVersion { get; set; }

        /// <summary>The server DLL: whether it references Gameloft.Logs (built with MCVS_GAMELOG).</summary>
        public bool? GameLog { get; set; }
    }

    /// <summary>
    /// <c>McvsOffline\install.json</c>, format 1 (docs/design/pc-port.md §3.10, §7.5): what the installer read and wrote, so
    /// <c>status</c> can tell "current" from "Steam updated the game" (STALE) and "a written file changed" (DAMAGED), and the
    /// host can make the same check at boot. UTF-8 without BOM, keys in the order of §7.5, MiniJson-compatible. Readers other
    /// than the installer rely only on <c>format</c>, <c>kind</c>, <c>originals.*.sha256</c>, <c>written.*.sha256</c> and
    /// <c>options.port</c>.
    /// </summary>
    public sealed class InstallManifest
    {
        public const string FileName = "install.json";
        public const int CurrentFormat = 1;
        public const string KindValue = "mcvs-pc-install";

        public InstallManifest()
        {
            Format = CurrentFormat;
            Kind = KindValue;
            Originals = new List<InstallOriginal>();
            Written = new List<InstallWritten>();
            Patches = new List<string>();
            Notes = new List<string>();
        }

        public int Format { get; set; }

        public string Kind { get; set; }

        public string InstallerName { get; set; }

        public string InstallerVersion { get; set; }

        /// <summary>McvsVersion.Build of the server DLL (the device build number), when it has one.</summary>
        public string ServerBuild { get; set; }

        /// <summary>"yyyy-MM-ddTHH:mm:ssZ".</summary>
        public string WrittenUtc { get; set; }

        public string BuildId { get; set; }

        public string GameVersion { get; set; }

        public string ClientId { get; set; }

        public string ExeSha256 { get; set; }

        public List<InstallOriginal> Originals { get; private set; }

        public List<InstallWritten> Written { get; private set; }

        public int Port { get; set; }

        /// <summary>Every applied patch id, in the plan's order.</summary>
        public List<string> Patches { get; private set; }

        public string Bcl { get; set; }

        public List<string> Notes { get; private set; }

        public InstallWritten FindWritten(string path)
        {
            return Written.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.Ordinal));
        }

        public InstallOriginal FindOriginal(string file)
        {
            return Originals.FirstOrDefault(o => string.Equals(o.File, file, StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------------------------------------ writing

        /// <summary>The file text (LF line ends, final LF).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"format\": ").Append(Format.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"kind\": ").Append(Quote(Kind)).Append(",\n");
            sb.Append("  \"installer\": { \"name\": ").Append(Quote(InstallerName)).Append(", \"version\": ").Append(Quote(InstallerVersion))
              .Append(", \"serverBuild\": ").Append(Quote(ServerBuild)).Append(", \"writtenUtc\": ").Append(Quote(WrittenUtc)).Append(" },\n");
            sb.Append("  \"build\": { \"id\": ").Append(Quote(BuildId)).Append(", \"gameVersion\": ").Append(Quote(GameVersion))
              .Append(", \"clientId\": ").Append(Quote(ClientId)).Append(", \"exeSha256\": ").Append(Quote(ExeSha256)).Append(" },\n");
            sb.Append("  \"originals\": {");
            for (int i = 0; i < Originals.Count; i++)
            {
                InstallOriginal o = Originals[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(Quote(o.File)).Append(": { \"sha256\": ").Append(Quote(o.Sha256))
                  .Append(", \"mvid\": ").Append(Quote(o.Mvid.ToString("D"))).Append(" }");
            }
            sb.Append(Originals.Count == 0 ? "},\n" : "\n  },\n");
            sb.Append("  \"written\": {");
            for (int i = 0; i < Written.Count; i++)
            {
                InstallWritten w = Written[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append("    ").Append(Quote(w.Path)).Append(": { \"sha256\": ").Append(Quote(w.Sha256));
                if (w.Mvid.HasValue) sb.Append(", \"mvid\": ").Append(Quote(w.Mvid.Value.ToString("D")));
                if (w.Version != null) sb.Append(", \"version\": ").Append(Quote(w.Version.ToString()));
                if (w.Patches != null) sb.Append(", \"patches\": ").Append(Array(w.Patches));
                // The server entry (the only one with GameLog): its version even when unknown (null).
                if (w.GameLog.HasValue)
                {
                    sb.Append(", \"serverVersion\": ").Append(Quote(w.ServerVersion));
                    sb.Append(", \"gameLog\": ").Append(w.GameLog.Value ? "true" : "false");
                }
                sb.Append(" }");
            }
            sb.Append(Written.Count == 0 ? "},\n" : "\n  },\n");
            sb.Append("  \"options\": { \"port\": ").Append(Port.ToString(CultureInfo.InvariantCulture)).Append(", \"patches\": ").Append(Array(Patches))
              .Append(", \"bcl\": ").Append(Quote(Bcl)).Append(" },\n");
            sb.Append("  \"notes\": ").Append(Array(Notes)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>UTF-8 without BOM.</summary>
        public byte[] ToBytes()
        {
            return new UTF8Encoding(false).GetBytes(ToJson());
        }

        private static string Array(IEnumerable<string> items)
        {
            return "[" + string.Join(", ", items.Select(Quote)) + "]";
        }

        /// <summary>A JSON string literal (null: <c>null</c>); everything outside printable ASCII is escaped.</summary>
        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------ reading

        /// <summary>
        /// Reads install.json; <see cref="FormatException"/> for text that is not JSON, not ours ("kind"), or another format
        /// ("unknown install.json format": a reader then makes no stale check).
        /// </summary>
        public static InstallManifest Parse(string json)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json ?? "");
            }
            catch (JsonException ex)
            {
                throw new FormatException("install.json is not valid JSON: " + ex.Message, ex);
            }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new FormatException("install.json is not a JSON object");
                int format;
                JsonElement f;
                if (!root.TryGetProperty("format", out f) || f.ValueKind != JsonValueKind.Number || !f.TryGetInt32(out format) || format != CurrentFormat)
                    throw new FormatException("unknown install.json format");
                if (Str(root, "kind") != KindValue) throw new FormatException("install.json is not an MCVS Offline install record (kind)");
                var m = new InstallManifest();
                JsonElement e;
                if (root.TryGetProperty("installer", out e) && e.ValueKind == JsonValueKind.Object)
                {
                    m.InstallerName = Str(e, "name");
                    m.InstallerVersion = Str(e, "version");
                    m.ServerBuild = Str(e, "serverBuild");
                    m.WrittenUtc = Str(e, "writtenUtc");
                }
                if (root.TryGetProperty("build", out e) && e.ValueKind == JsonValueKind.Object)
                {
                    m.BuildId = Str(e, "id");
                    m.GameVersion = Str(e, "gameVersion");
                    m.ClientId = Str(e, "clientId");
                    m.ExeSha256 = Str(e, "exeSha256");
                }
                if (root.TryGetProperty("originals", out e) && e.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Object) throw new FormatException("install.json: originals." + p.Name + " is not an object");
                        Guid mvid;
                        Guid.TryParse(Str(p.Value, "mvid"), out mvid);
                        m.Originals.Add(new InstallOriginal { File = p.Name, Sha256 = Str(p.Value, "sha256"), Mvid = mvid });
                    }
                }
                if (root.TryGetProperty("written", out e) && e.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in e.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Object) throw new FormatException("install.json: written." + p.Name + " is not an object");
                        var w = new InstallWritten { Path = p.Name, Sha256 = Str(p.Value, "sha256"), ServerVersion = Str(p.Value, "serverVersion") };
                        Guid mvid;
                        if (Guid.TryParse(Str(p.Value, "mvid"), out mvid)) w.Mvid = mvid;
                        Version version;
                        if (Version.TryParse(Str(p.Value, "version"), out version)) w.Version = version;
                        JsonElement list;
                        if (p.Value.TryGetProperty("patches", out list) && list.ValueKind == JsonValueKind.Array) w.Patches = Strings(list);
                        JsonElement gameLog;
                        if (p.Value.TryGetProperty("gameLog", out gameLog) && (gameLog.ValueKind == JsonValueKind.True || gameLog.ValueKind == JsonValueKind.False))
                            w.GameLog = gameLog.ValueKind == JsonValueKind.True;
                        m.Written.Add(w);
                    }
                }
                if (root.TryGetProperty("options", out e) && e.ValueKind == JsonValueKind.Object)
                {
                    JsonElement port;
                    int value;
                    if (e.TryGetProperty("port", out port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out value)) m.Port = value;
                    JsonElement list;
                    if (e.TryGetProperty("patches", out list) && list.ValueKind == JsonValueKind.Array) m.Patches.AddRange(Strings(list));
                    m.Bcl = Str(e, "bcl");
                }
                if (root.TryGetProperty("notes", out e) && e.ValueKind == JsonValueKind.Array) m.Notes.AddRange(Strings(e));
                return m;
            }
        }

        private static string Str(JsonElement obj, string name)
        {
            JsonElement value;
            return obj.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static List<string> Strings(JsonElement array)
        {
            return array.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()).ToList();
        }
    }
}
