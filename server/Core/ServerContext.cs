using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Server.Core
{
    /// <summary>
    /// Everything a service module needs, handed to each module's <c>Register(Router, ServerContext)</c>:
    /// settings, logging, clock, content, accounts/tokens, per-module durable stores, cross-module events and
    /// features, and the URLs to hand out to clients.
    /// </summary>
    public sealed class ServerContext
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, DocumentStore> stores = new Dictionary<string, DocumentStore>(StringComparer.Ordinal);
        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);

        public ServerContext(string dataDir, Settings settings, Logger logger, IClock clock, IContentProvider content)
        {
            if (string.IsNullOrEmpty(dataDir)) throw new ArgumentException("data directory required", "dataDir");
            DataDir = Path.GetFullPath(dataDir);
            Directory.CreateDirectory(DataDir);
            Settings = settings ?? new Settings();
            Logger = logger ?? new Logger();
            Log = Logger.For("core");
            Clock = clock ?? SystemClock.Instance;
            Content = content ?? new ContentProvider(null, null, Logger.For("content"));
            StartedUtc = Clock.UtcNow;
            InstanceId = LoadOrCreateInstanceId();
            Accounts = new AccountStore(Path.Combine(DataDir, AccountStore.FileName), Clock, Logger.For("accounts"));
            Tokens = new TokenService(Path.Combine(DataDir, TokenService.FileName), InstanceId, Clock, Logger.For("tokens"));
            Events = new EventHub(Logger.For("events"));
            Features = new FeatureRegistry();
            Port = Settings.Port;
        }

        /// <summary>Absolute data directory (embedded: &lt;GlfRootPath&gt;/McvsOffline, standalone: ./data).</summary>
        public string DataDir { get; private set; }

        public Settings Settings { get; private set; }

        /// <summary>Root logger; modules use <c>Logger.For("module")</c>.</summary>
        public Logger Logger { get; private set; }

        /// <summary>Logger for core messages.</summary>
        public Log Log { get; private set; }

        public IClock Clock { get; private set; }

        public IContentProvider Content { get; private set; }

        public AccountStore Accounts { get; private set; }

        public TokenService Tokens { get; private set; }

        public EventHub Events { get; private set; }

        public FeatureRegistry Features { get; private set; }

        /// <summary>Stable id of this server instance ("mcvs" + 8 hex), persisted in instance.json; the token issuer.</summary>
        public string InstanceId { get; private set; }

        public DateTime StartedUtc { get; private set; }

        /// <summary>The port actually listened on (set when the HTTP server starts).</summary>
        public int Port { get; internal set; }

        /// <summary>Signalled when the server is stopping (long-running handlers should return).</summary>
        public WaitHandle StopSignal { get { return stopSignal; } }

        public bool IsStopping { get { return stopSignal.WaitOne(0); } }

        /// <summary>
        /// The base URL for this host, always with an explicit port and a trailing slash:
        /// settings.publicUrl if set, else http://127.0.0.1:&lt;port&gt;/.
        /// </summary>
        public string BaseUrl
        {
            get
            {
                string configured = NormalizeBaseUrl(Settings.PublicUrl);
                return configured ?? "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/";
            }
        }

        /// <summary>
        /// The base URL to give the client that sent <paramref name="request"/> (eve urls, pandora locate, chat cmd_url...).
        /// In LAN mode it is built from the request's Host header, so guests get the host's LAN address and the host
        /// itself keeps 127.0.0.1 (verify-C6 §5); otherwise it is <see cref="BaseUrl"/>.
        /// </summary>
        public string BaseUrlFor(HttpRequest request)
        {
            if (Settings.LanMode && request != null && NormalizeBaseUrl(Settings.PublicUrl) == null)
            {
                string host = request.Host;
                if (IsSafeHost(host))
                {
                    bool hasPort = host.EndsWith("]", StringComparison.Ordinal) ? false : host.LastIndexOf(':') > host.LastIndexOf(']');
                    return "http://" + host + (hasPort ? "" : ":" + Port.ToString(CultureInfo.InvariantCulture)) + "/";
                }
            }
            return BaseUrl;
        }

        /// <summary>Absolute path of a file or folder inside the data directory.</summary>
        public string GetDataPath(string relativePath)
        {
            return Path.Combine(DataDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// The durable JSON document store for a module, at &lt;data&gt;/&lt;name&gt;/ (one instance per name, so
        /// per-key locks are shared by everyone using that store).
        /// </summary>
        public DocumentStore OpenStore(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\', '.' }) >= 0)
                throw new ArgumentException("store name must be a simple folder name", "name");
            lock (gate)
            {
                DocumentStore store;
                if (!stores.TryGetValue(name, out store))
                {
                    store = new DocumentStore(Path.Combine(DataDir, name));
                    stores[name] = store;
                }
                return store;
            }
        }

        internal void SignalStop()
        {
            stopSignal.Set();
        }

        internal void ResetStop()
        {
            stopSignal.Reset();
        }

        private string LoadOrCreateInstanceId()
        {
            string path = Path.Combine(DataDir, "instance.json");
            try
            {
                string text = AtomicFile.ReadAllTextOrNull(path);
                if (text != null)
                {
                    object parsed;
                    if (MiniJson.TryParse(text, out parsed))
                    {
                        string id = JsonValue.GetString(parsed as Dictionary<string, object>, "instanceId");
                        if (!string.IsNullOrEmpty(id) && id.IndexOf('.') < 0) return id;
                    }
                    Log.Warn(path + " is invalid; creating a new instance id");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("cannot read " + path, ex);
            }
            string newId = "mcvs" + Ids.RandomHex(4);
            var doc = new Dictionary<string, object>(StringComparer.Ordinal);
            doc["instanceId"] = newId;
            doc["created"] = TimeFormat.Iso8601(Clock.UtcNow);
            AtomicFile.WriteAllText(path, MiniJson.Serialize(doc, true) + "\n");
            return newId;
        }

        private static string NormalizeBaseUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string u = url.Trim();
            if (u.Length == 0) return null;
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                u = "http://" + u;
            if (!u.EndsWith("/", StringComparison.Ordinal)) u += "/";
            return u;
        }

        private static bool IsSafeHost(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 255) return false;
            foreach (char c in host)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '-' || c == ':' || c == '[' || c == ']';
                if (!ok) return false;
            }
            return true;
        }
    }
}
