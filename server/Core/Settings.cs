using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Core
{
    /// <summary>
    /// Player/host settings, stored as settings.json in the data directory (ARCHITECTURE §3 "Settings").
    /// <para>
    /// The document is the defaults deep-merged with the file, so unknown keys are preserved and modules can keep
    /// their own settings under their own key (read them with <see cref="GetBool"/> etc. and a default; no need to
    /// touch this file). Typed properties cover the settings several modules share.
    /// </para>
    /// <para>
    /// <see cref="Override"/> sets in-memory values (command line, tests) that are never saved. Overrides are applied
    /// over the file in the order they were set: an object deep-merges into the object at its path, anything else
    /// replaces the value. So <c>Override("assets", {"couponsEnabled": false})</c> leaves every other "assets.*" key
    /// as the file has it, and <c>Override("economy.xpMultiplier", 2)</c> is also visible through
    /// <c>GetObject("economy")</c>. Thread-safe.
    /// </para>
    /// </summary>
    public sealed class Settings
    {
        public const string FileName = "settings.json";

        public const string DefaultSuperCredential = "game:mcvsoffline";
        public const string DefaultSuperPassword = "mcvsoffline";
        public const string DefaultDataCenter = "mcvs";

        private readonly object gate = new object();
        private readonly Dictionary<string, object> values;
        private readonly Dictionary<string, object> overrides = new Dictionary<string, object>(StringComparer.Ordinal);

        // values + overrides, rebuilt lazily after Set/Override (settings are read on every request; written rarely).
        private Dictionary<string, object> effective;

        /// <summary>Defaults only, not backed by a file.</summary>
        public Settings()
        {
            values = CreateDefaults();
        }

        private Settings(Dictionary<string, object> values, string filePath)
        {
            this.values = values;
            FilePath = filePath;
        }

        /// <summary>The file this instance was loaded from (null for in-memory settings).</summary>
        public string FilePath { get; private set; }

        /// <summary>
        /// True when <see cref="FilePath"/> exists but could not be read or is not a JSON object: this instance holds the
        /// defaults in memory and <see cref="Save"/> refuses to write them over the player's file (a typo must never
        /// cost the player's settings).
        /// </summary>
        public bool IsFallback { get; private set; }

        /// <summary>The default document. Order here is the order a fresh settings.json is written in.</summary>
        public static Dictionary<string, object> CreateDefaults()
        {
            var economy = new Dictionary<string, object>(StringComparer.Ordinal);
            economy["creditsMultiplier"] = 1.0;
            economy["versusCoinsMultiplier"] = 1.0;
            economy["xpMultiplier"] = 1.0;
            economy["priceMultiplier"] = 1.0;

            var logging = new Dictionary<string, object>(StringComparer.Ordinal);
            logging["consoleLevel"] = "info";
            logging["fileLevel"] = "debug";
            logging["bodyLimit"] = 64L * 1024;
            logging["fileMaxBytes"] = 5L * 1024 * 1024;
            logging["fileCount"] = 5L;

            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["port"] = (long)McvsConstants.DefaultPort;
            d["bind"] = "127.0.0.1";
            d["publicUrl"] = "";
            d["lanMode"] = false;
            d["unlockAll"] = false;
            d["devMenu"] = false;
            d["instantBots"] = true;
            d["economy"] = economy;
            d["superCredential"] = DefaultSuperCredential;
            d["superPassword"] = DefaultSuperPassword;
            d["dataCenter"] = DefaultDataCenter;
            d["logging"] = logging;
            return d;
        }

        /// <summary>
        /// Loads <paramref name="path"/> over the defaults. A missing file is created with the defaults; new default
        /// keys are added to an existing file. A file that cannot be read or parsed is left untouched: the defaults
        /// are used in memory (<see cref="IsFallback"/>) so a typo never destroys the player's settings.
        /// </summary>
        public static Settings Load(string path, Log log)
        {
            log = log ?? Log.Null;
            string full = Path.GetFullPath(path);
            Dictionary<string, object> doc = CreateDefaults();
            string text;
            try
            {
                text = AtomicFile.ReadAllTextOrNull(full);
            }
            catch (Exception ex)
            {
                log.Error("cannot read " + full + "; using defaults (the file is left unchanged)", ex);
                return Fallback(doc, full, log);
            }

            bool writeBack = text == null; // only a missing file is created
            if (text != null)
            {
                object parsed;
                if (MiniJson.TryParse(text, out parsed) && parsed is Dictionary<string, object>)
                {
                    var fileDoc = (Dictionary<string, object>)parsed;
                    writeBack = HasMissingKeys(doc, fileDoc);
                    JsonPath.DeepMerge(doc, fileDoc);
                }
                else
                {
                    log.Error(full + " is not a valid JSON object; using defaults (the file is left unchanged: fix it and restart)");
                    return Fallback(doc, full, log);
                }
            }

            var settings = new Settings(doc, full);
            settings.Validate(log);
            if (writeBack)
            {
                try
                {
                    settings.Save();
                }
                catch (Exception ex)
                {
                    log.Warn("cannot write " + full, ex);
                }
            }
            return settings;
        }

        private static Settings Fallback(Dictionary<string, object> defaults, string full, Log log)
        {
            var fallback = new Settings(defaults, full);
            fallback.IsFallback = true;
            fallback.Validate(log);
            return fallback;
        }

        /// <summary>
        /// Saves the document (without overrides) to <see cref="FilePath"/>, atomically. Throws for in-memory settings
        /// and for a <see cref="IsFallback"/> instance, which would replace the player's unreadable file with defaults.
        /// </summary>
        public void Save()
        {
            if (FilePath == null) throw new InvalidOperationException("settings are not backed by a file");
            if (IsFallback) throw new InvalidOperationException(FilePath + " could not be read; refusing to overwrite it with defaults");
            string text;
            lock (gate) text = MiniJson.Serialize(values, true) + "\n";
            AtomicFile.WriteAllText(FilePath, text);
        }

        // ------------------------------------------------------------------------- generic access

        /// <summary>A copy of the effective value (file + overrides) at a dotted path, or null.</summary>
        public object Get(string path)
        {
            lock (gate) return JsonValue.DeepClone(JsonPath.Get(Effective(), path));
        }

        public string GetString(string path, string defaultValue)
        {
            return JsonValue.AsString(Get(path)) ?? defaultValue;
        }

        public bool GetBool(string path, bool defaultValue)
        {
            return JsonValue.AsBool(Get(path)) ?? defaultValue;
        }

        public long GetLong(string path, long defaultValue)
        {
            return JsonValue.AsLong(Get(path)) ?? defaultValue;
        }

        public double GetDouble(string path, double defaultValue)
        {
            return JsonValue.AsDouble(Get(path)) ?? defaultValue;
        }

        /// <summary>A copy of the object at <paramref name="path"/>, or null.</summary>
        public Dictionary<string, object> GetObject(string path)
        {
            return Get(path) as Dictionary<string, object>;
        }

        /// <summary>Sets a persisted value (call <see cref="Save"/> to write it).</summary>
        public void Set(string path, object value)
        {
            lock (gate)
            {
                JsonPath.Set(values, path, JsonValue.DeepClone(value));
                effective = null;
            }
        }

        /// <summary>
        /// Sets an in-memory value that wins over the file and is never saved (e.g. --port). An object is deep-merged
        /// into the object at <paramref name="path"/>; see the class remarks.
        /// </summary>
        public void Override(string path, object value)
        {
            lock (gate)
            {
                overrides[path] = JsonValue.DeepClone(value);
                effective = null;
            }
        }

        /// <summary>Effective settings (file + overrides) as a new object, for diagnostics.</summary>
        public Dictionary<string, object> Snapshot()
        {
            lock (gate) return JsonValue.DeepCloneObject(Effective());
        }

        /// <summary>The effective document; callers hold <see cref="gate"/> and never hand it out uncloned.</summary>
        private Dictionary<string, object> Effective()
        {
            if (effective != null) return effective;
            var doc = JsonValue.DeepCloneObject(values);
            foreach (var kv in overrides)
            {
                var patch = kv.Value as Dictionary<string, object>;
                var existing = JsonPath.Get(doc, kv.Key) as Dictionary<string, object>;
                if (patch != null && existing != null) JsonPath.DeepMerge(existing, JsonValue.DeepCloneObject(patch));
                else JsonPath.Set(doc, kv.Key, JsonValue.DeepClone(kv.Value));
            }
            effective = doc;
            return doc;
        }

        // ------------------------------------------------------------------------- shared typed settings

        /// <summary>TCP port of the Federation server (default 51780).</summary>
        public int Port
        {
            get
            {
                long port = GetLong("port", McvsConstants.DefaultPort);
                return port >= 0 && port <= 65535 ? (int)port : McvsConstants.DefaultPort;
            }
        }

        /// <summary>Bind address: "127.0.0.1" (default), "0.0.0.0"/"*" for LAN, "::" for dual-stack.</summary>
        public string Bind { get { return GetString("bind", "127.0.0.1"); } }

        /// <summary>
        /// Base URL handed to clients (eve urls, pandora locate). Empty: http://127.0.0.1:&lt;port&gt;/, or the
        /// request's Host in LAN mode. Must keep an explicit non-80/443 port (report 02 §1.3 FormatUri).
        /// </summary>
        public string PublicUrl { get { return GetString("publicUrl", ""); } }

        /// <summary>LAN host mode: service URLs are built from the request's Host header (verify-C6 §5).</summary>
        public bool LanMode { get { return GetBool("lanMode", false); } }

        /// <summary>
        /// Opt-in unlock-all through the IAP inventory ledger (verify-C5 (c); server/Services/Unlocks). The unlock
        /// coupon codes are independent of this (assets.couponsEnabled).
        /// </summary>
        public bool UnlockAll { get { return GetBool("unlockAll", false); } }

        /// <summary>
        /// Dev cheat menu. The menu is ON by default over loopback in Embedded mode, so CRM must send
        /// IsDevelopment=false unless this is true (verify-C5 (a)).
        /// </summary>
        public bool DevMenu { get { return GetBool("devMenu", false); } }

        /// <summary>Instant bot-filled PvP: CRM Matchmaking MinPlayers 1 / MaxOpenTimeInSeconds 0 (verify-C4 §3.4).</summary>
        public bool InstantBots { get { return GetBool("instantBots", true); } }

        public double CreditsMultiplier { get { return GetDouble("economy.creditsMultiplier", 1.0); } }

        public double VersusCoinsMultiplier { get { return GetDouble("economy.versusCoinsMultiplier", 1.0); } }

        public double XpMultiplier { get { return GetDouble("economy.xpMultiplier", 1.0); } }

        public double PriceMultiplier { get { return GetDouble("economy.priceMultiplier", 1.0); } }

        /// <summary>
        /// Game-server super credential ("type:name"). It is repeated in args.txt and in the lobbyconfigs cmd_args,
        /// which the Controller splits on '-' (verify-C2 §1.4): no '-' and no whitespace allowed.
        /// </summary>
        public string SuperCredential { get { return GetString("superCredential", DefaultSuperCredential); } }

        public string SuperPassword { get { return GetString("superPassword", DefaultSuperPassword); } }

        /// <summary>Datacenter name returned by eve and passed as -DataCenter (same cmd_args restrictions).</summary>
        public string DataCenter { get { return GetString("dataCenter", DefaultDataCenter); } }

        public LogLevel ConsoleLogLevel { get { return LogEntry.ParseLevel(GetString("logging.consoleLevel", "info"), LogLevel.Info); } }

        public LogLevel FileLogLevel { get { return LogEntry.ParseLevel(GetString("logging.fileLevel", "debug"), LogLevel.Debug); } }

        /// <summary>Maximum characters of a request body/parameter written to the normal request log.</summary>
        public int LogBodyLimit { get { return (int)Num.Clamp(GetLong("logging.bodyLimit", 64 * 1024), 256, 64L * 1024 * 1024); } }

        public long LogFileMaxBytes { get { return Num.Max(64L * 1024, GetLong("logging.fileMaxBytes", 5L * 1024 * 1024)); } }

        public int LogFileCount { get { return (int)Num.Clamp(GetLong("logging.fileCount", 5), 1, 50); } }

        /// <summary>True when <paramref name="value"/> can be placed in lobbyconfigs cmd_args unchanged.</summary>
        public static bool IsCmdArgSafe(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                if (c == '-' || char.IsWhiteSpace(c)) return false;
            }
            return true;
        }

        private void Validate(Log log)
        {
            string credential = SuperCredential;
            int colon = credential.IndexOf(':');
            if (!IsCmdArgSafe(credential) || colon <= 0 || colon == credential.Length - 1 || credential.IndexOf(':', colon + 1) >= 0)
            {
                log.Warn("settings: superCredential '" + credential + "' must look like 'game:name' without '-' or spaces; using " + DefaultSuperCredential);
                Override("superCredential", DefaultSuperCredential);
            }
            if (!IsCmdArgSafe(SuperPassword))
            {
                log.Warn("settings: superPassword must be non-empty without '-' or spaces; using the default");
                Override("superPassword", DefaultSuperPassword);
            }
            if (!IsCmdArgSafe(DataCenter))
            {
                log.Warn("settings: dataCenter '" + DataCenter + "' must be non-empty without '-' or spaces; using " + DefaultDataCenter);
                Override("dataCenter", DefaultDataCenter);
            }
        }

        private static bool HasMissingKeys(Dictionary<string, object> defaults, Dictionary<string, object> actual)
        {
            foreach (var kv in defaults)
            {
                object value;
                if (!actual.TryGetValue(kv.Key, out value)) return true;
                var d = kv.Value as Dictionary<string, object>;
                var a = value as Dictionary<string, object>;
                if (d != null && a != null && HasMissingKeys(d, a)) return true;
            }
            return false;
        }
    }
}
