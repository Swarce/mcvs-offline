using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Tests.Storage
{
    /// <summary>
    /// A server with only the storage module (plus core routes), driven in-process through McvsServer.Handle (the
    /// same router and request logging as over HTTP). Tokens are issued directly through the TokenService, the way
    /// the AUTH module's authorize does.
    /// </summary>
    public sealed class StorageHarness : IDisposable
    {
        public const string SuperCredential = "game:mcvsoffline";

        /// <summary>Scopes of the game server's sessions (EN/AuthenticationComponent.cs:272-277), abridged.</summary>
        public static readonly string[] ServerScopes = { "auth", "storage", "storage_ro", "storage_restricted", "storage_admin", "config" };

        private readonly bool ownsDir;

        public StorageHarness(TempDir dir = null, Action<Settings> configure = null, DateTime? start = null)
        {
            ownsDir = dir == null;
            Dir = dir ?? new TempDir();
            Clock = new ManualClock(start ?? new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc));
            Logs = new MemoryLogSink();
            Configure = configure;
            Server = Create();
        }

        public TempDir Dir { get; }

        public ManualClock Clock { get; }

        public MemoryLogSink Logs { get; }

        public Action<Settings> Configure { get; }

        public McvsServer Server { get; private set; }

        public ServerContext Context => Server.Context;

        public string DataDir => Dir.Combine("data");

        public LocalProfileStore Store => Context.Features.Get<LocalProfileStore>();

        public IProfileAccess Access => Context.Features.Get<IProfileAccess>();

        /// <summary>Simulates an app restart: a new server instance over the same data directory.</summary>
        public void Restart()
        {
            Server.Dispose();
            Server = Create();
        }

        private McvsServer Create()
        {
            var options = new ServerOptions
            {
                DataDir = DataDir,
                Port = 0,
                ConsoleLog = false,
                FileLog = false,
                Clock = Clock,
                RegisterServices = StorageService.Register,
            };
            options.ExtraSinks.Add(Logs);
            var server = McvsServer.Create(options);
            Configure?.Invoke(server.Context.Settings);
            return server;
        }

        // ------------------------------------------------------------------ identities

        public Account Player(string anonymousUser) => Context.Accounts.GetOrCreate("anonymous:" + anonymousUser, "device-" + anonymousUser);

        /// <summary>The game server's for_username token for a player (SessionForUser): subject = the player.</summary>
        public string PlayerToken(string anonymousUser)
        {
            Account account = Player(anonymousUser);
            return Context.Tokens.Issue(account.Id, "anonymous:" + anonymousUser, McvsConstants.ClientId, ServerScopes,
                                        "device-" + anonymousUser, SuperCredential).Token;
        }

        /// <summary>The game server's own token (super credential): used with fed_id paths (currentMatch, read-only sessions).</summary>
        public string GameServerToken()
        {
            Account account = Context.Accounts.GetOrCreate(SuperCredential, "mcvsGameServer");
            return Context.Tokens.Issue(account.Id, SuperCredential, McvsConstants.ClientId, ServerScopes, "mcvsGameServer").Token;
        }

        // ------------------------------------------------------------------ requests

        /// <summary>GET with the token and parameters in the query, as the client sends them (OP/FedRequestMessage.cs:164-204).</summary>
        public HttpResponse Get(string path, string token, params (string Name, string Value)[] query)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var q in query) pairs.Add(new KeyValuePair<string, string>(q.Name, q.Value));
            if (token != null) pairs.Add(new KeyValuePair<string, string>("access_token", token));
            string target = "/" + path.TrimStart('/');
            if (pairs.Count > 0) target += "?" + UrlEncoding.EncodeForm(pairs);
            return Send(HttpRequest.Create("GET", target));
        }

        /// <summary>POST with a form body (Uri.EscapeDataString encoding) carrying the token, as the client does.</summary>
        public HttpResponse Post(string path, string token, params (string Name, string Value)[] form)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var f in form) pairs.Add(new KeyValuePair<string, string>(f.Name, f.Value));
            if (token != null) pairs.Add(new KeyValuePair<string, string>("access_token", token));
            return Send(HttpRequest.Create("POST", "/" + path.TrimStart('/'), UrlEncoding.EncodeForm(pairs)));
        }

        public HttpResponse Send(HttpRequest request) => Server.Handle(request);

        /// <summary>The stored file of an account (null if none).</summary>
        public string StoredFile(string accountId)
        {
            string path = Store.PathFor(accountId);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        public static Dictionary<string, object> Json(HttpResponse response) => MiniJson.ParseObject(response.BodyText);

        public void Dispose()
        {
            Server.Dispose();
            if (ownsDir) Dir.Dispose();
        }
    }

    /// <summary>Realistic payloads the game sends.</summary>
    public static class GameData
    {
        /// <summary>
        /// A Templates "j" dump in the shape of GameProfileSection.FromGameObject (verify-C3 §7.6): '.', '/', '@', '$'
        /// in keys, a collapsed dotted key, and number/escape spellings a re-serializer would change.
        /// </summary>
        public const string GameProfileDump =
            "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"Version\":57," +
            "\"GameVersion\":\"1.17.32\",\"PlayerName\":\"agent-123456\",\"Attachments.'0'\":{\"Id\":\"a\\/b\"}," +
            "\"Ratio\":0.30000000000000004,\"Exp\":1e5,\"Fixed\":3.140,\"Neg\":-0,\"Big\":123456789012345678901234567890," +
            "\"Esc\":\"\\u00e9\\u6f22\\n\",\"Raw\":\"é漢\",\"Empty\":{},\"List\":[1,2.50,{\"x.y\":null}]}," +
            "\"/League\":{\"Trophies\":120},\"/Gears\":{\"Slots\":[]}}}";

        /// <summary>The include_fields of the R2 sections load (verify-C3 §2.3), in one HashSet order.</summary>
        public const string SectionsIncludeFields =
            "_mcvs.matchers,_mcvs.snapshot,credential,created,modified,datacenter,last_session,total_spent,total_transactions," +
            "last_purchase,devices,level,xp,total_playtime,country,language,birthdate,gender,location,clan,pending_clan," +
            "carrier,available_transports,current_client_id,current_device,current_device_id,banned_from,inventory,_inventory";

        /// <summary>An R5 sections save: writable standard fields (absent ones as null) plus a partial _mcvs.</summary>
        public const string SectionsMerge =
            "{\"devices\":null,\"level\":7,\"xp\":5120,\"total_playtime\":3600.0,\"country\":\"US\",\"language\":\"en\"," +
            "\"birthdate\":null,\"gender\":null,\"location\":null,\"clan\":null,\"pending_clan\":null,\"carrier\":null," +
            "\"available_transports\":null,\"current_client_id\":\"2620:66102:1.17.32:android:googleplay\"," +
            "\"current_device\":null,\"current_device_id\":null," +
            "\"_mcvs\":{\"snapshot\":{\"PlayerName\":\"agent-123456\",\"PlayerLevel\":7,\"Trophies\":120,\"MatchMakingRating\":1000}," +
            "\"matchers\":{\"LoginsPerDay\":1,\"PlayerLevel\":7}}}";
    }
}
