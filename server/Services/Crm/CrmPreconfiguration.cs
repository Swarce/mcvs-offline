using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// The game's own default CRM, <c>Config/crmpreconfiguration.json</c>, read at runtime from the player's data pack
    /// (on disk <c>Data/b/de76cd6.f</c>: the name is crc32 of the logical path, the file a GCBF/LZ4 container; report 11,
    /// decoded by <see cref="DataPack"/>/<see cref="Gcbf"/> in Core). It is never redistributed: only the player's copy is
    /// read, and only to build the CRM answer on their device.
    /// <para>
    /// Lookup order: the host's configured data pack (<c>context.Content</c>); then <c>crm.dataPackDir</c>; then
    /// <c>&lt;data&gt;/../Data</c>, which is where the game keeps it in the embedded layout (data dir
    /// <c>&lt;GlfRootPath&gt;/McvsOffline</c>, pack <c>&lt;GlfRootPath&gt;/Data</c>). The parsed document is cached for the
    /// life of the process; callers get deep copies.
    /// </para>
    /// <para>
    /// On a fresh install the server starts before the game's installer has downloaded the data pack (verify-C7 §5:
    /// Bootstrap.Init runs before the Java GameInstaller), so a missing file is not final: <see cref="Load"/> tries
    /// again after <see cref="RetryIntervalMs"/>. An unreadable or corrupt file is final until restart.
    /// </para>
    /// </summary>
    public sealed class CrmPreconfiguration
    {
        public const string LogicalPath = "Config/crmpreconfiguration.json";

        /// <summary>Default minimum time between two attempts while the file is missing.</summary>
        public const int DefaultRetryIntervalMs = 10000;

        private readonly object gate = new object();
        private readonly object loadGate = new object();
        private Dictionary<string, object> document;
        private bool loaded;
        private bool final;
        private int lastAttemptTick;

        public CrmPreconfiguration()
        {
            RetryIntervalMs = DefaultRetryIntervalMs;
        }

        /// <summary>Minimum time between two attempts while the file is missing (tests lower it).</summary>
        public int RetryIntervalMs { get; set; }

        /// <summary>Where the document came from (for logs and diagnostics), or why it is missing.</summary>
        public string Source { get; private set; }

        /// <summary>True once <see cref="Load"/> ran at least once (successfully or not).</summary>
        public bool IsLoaded { get { lock (gate) return loaded; } }

        /// <summary>True when no further attempt will be made (document loaded, or the file is corrupt).</summary>
        public bool IsFinal { get { lock (gate) return final; } }

        /// <summary>True when a document is available.</summary>
        public bool HasDocument { get { lock (gate) return document != null; } }

        /// <summary>A deep copy of the cached document, or null when none could be loaded.</summary>
        public Dictionary<string, object> GetCopy()
        {
            lock (gate) return document == null ? null : JsonValue.DeepCloneObject(document);
        }

        /// <summary>
        /// Loads the preconfiguration unless it is already loaded (or known corrupt), or the last attempt found no file
        /// less than <see cref="RetryIntervalMs"/> ago. Never throws; failures are logged and leave no document.
        /// </summary>
        /// <param name="keepIapLanguages">Languages of the per-language <c>iap</c> catalogue to keep in memory; the rest
        /// (about 95% of the 2.2 MB file) is dropped right after parsing.</param>
        public void Load(IContentProvider content, string dataDir, string explicitPackDir, Log log, string[] keepIapLanguages = null)
        {
            log = log ?? Log.Null;
            // loadGate serialises loads; gate only guards the state, so IsLoaded/HasDocument never wait for the I/O.
            lock (loadGate)
            {
                bool wasLoaded;
                string previousSource;
                lock (gate)
                {
                    if (final) return;
                    if (loaded && unchecked(Environment.TickCount - lastAttemptTick) < RetryIntervalMs) return;
                    lastAttemptTick = Environment.TickCount;
                    wasLoaded = loaded;
                    previousSource = Source;
                }

                Dictionary<string, object> doc = null;
                string source;
                bool isFinal = false;
                try
                {
                    string text = Read(content, dataDir, explicitPackDir, log, out source);
                    if (text == null)
                    {
                        if (!wasLoaded || previousSource != source)
                            log.Warn("CRM preconfiguration not found (" + source + "): the CRM document is built from our content only"
                                     + " until it appears");
                    }
                    else
                    {
                        object parsed;
                        isFinal = true;
                        if (!MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>))
                        {
                            log.Error("CRM preconfiguration from " + source + " is not a JSON object; ignoring it");
                            source = "unparseable " + source;
                        }
                        else
                        {
                            doc = (Dictionary<string, object>)parsed;
                            Compact(doc, keepIapLanguages ?? new string[0]);
                            log.Info("CRM preconfiguration loaded from " + source + " (" + text.Length + " chars, keys: "
                                     + string.Join(", ", new List<string>(doc.Keys).ToArray()) + ")");
                        }
                    }
                }
                catch (Exception ex)
                {
                    source = "error: " + ex.Message;
                    isFinal = true;
                    log.Error("cannot load the CRM preconfiguration", ex);
                }

                lock (gate)
                {
                    document = doc;
                    Source = source;
                    final = isFinal;
                    loaded = true;
                }
            }
        }

        /// <summary>
        /// Drops what can never reach the client, to keep the game process's memory down: the empty
        /// <c>_OverrideTables</c> changers (~175 KB of them in 1.17.32) and the iap catalogues of other languages.
        /// </summary>
        public static void Compact(Dictionary<string, object> doc, string[] keepIapLanguages)
        {
            var game = JsonValue.GetObject(doc, "game");
            var tables = JsonValue.GetObject(game, "_OverrideTables");
            if (tables != null) CrmDocumentBuilder.PruneOverrideTables(tables, "preconfiguration", Log.Null);
            var iap = JsonValue.GetObject(doc, "iap");
            if (iap != null && !iap.ContainsKey("prices"))
            {
                foreach (string lang in new List<string>(iap.Keys))
                {
                    if (Array.IndexOf(keepIapLanguages, lang) < 0) JsonPath.RemoveKey(iap, lang);
                }
            }
        }

        /// <summary>Installs a document directly (tests, or a host that already parsed it).</summary>
        public void Set(Dictionary<string, object> doc, string source)
        {
            lock (gate)
            {
                document = doc == null ? null : JsonValue.DeepCloneObject(doc);
                Source = source;
                loaded = true;
                final = true;
            }
        }

        private static string Read(IContentProvider content, string dataDir, string explicitPackDir, Log log, out string source)
        {
            string text;
            if (content != null && content.DataPack != null)
            {
                if (content.TryReadGameText(LogicalPath, out text))
                {
                    source = content.DataPack.DataDirectory;
                    return text;
                }
            }

            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(explicitPackDir)) candidates.Add(explicitPackDir);
            if (!string.IsNullOrEmpty(dataDir))
            {
                // Embedded layout: <GlfRootPath>/McvsOffline (our data) next to <GlfRootPath>/Data (the game's pack).
                candidates.Add(Path.Combine(Path.Combine(dataDir, ".."), "Data"));
            }
            foreach (string dir in candidates)
            {
                try
                {
                    var pack = new DataPack(dir);
                    if (!pack.IsValid) continue;
                    if (pack.TryReadText(LogicalPath, out text))
                    {
                        source = pack.DataDirectory;
                        return text;
                    }
                }
                catch (DataPackException ex)
                {
                    log.Error("data pack " + dir + ": " + ex.Message, ex);
                }
                catch (Exception ex)
                {
                    log.Warn("cannot read the data pack at " + dir, ex);
                }
            }
            source = content != null && content.DataPack != null
                ? "missing in " + content.DataPack.DataDirectory
                : "no data pack (configure the host's data pack or crm.dataPackDir)";
            return null;
        }
    }
}
