using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Tests.Auth
{
    /// <summary>
    /// Ordered request parameters with the semantics of Gameloft.OnlinePlatform's RequestParameters
    /// (a Dictionary&lt;string, RequestValue&gt;): assigning an existing key replaces the value in place, a new key is
    /// appended, and serialization is <c>Uri.EscapeDataString(key)=Uri.EscapeDataString(value)</c> joined by '&amp;'
    /// (OP/RequestParameters.cs ToUrlEncodedString, OP/MoreUri.cs).
    /// </summary>
    public sealed class FedParameters
    {
        private readonly List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();

        public string this[string key]
        {
            get { return pairs.FirstOrDefault(p => p.Key == key).Value; }
            set
            {
                int i = pairs.FindIndex(p => p.Key == key);
                var pair = new KeyValuePair<string, string>(key, value ?? "");
                if (i >= 0) pairs[i] = pair;
                else pairs.Add(pair);
            }
        }

        public int Count => pairs.Count;

        public bool ContainsKey(string key) => pairs.Any(p => p.Key == key);

        /// <summary>RequestValue(bool) prints C# casing ("True"/"False"), ints invariant (report 02 §1.4).</summary>
        public void Set(string key, bool value) => this[key] = value.ToString();

        public void Set(string key, int value) => this[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string ToString() =>
            string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

        public FedParameters Clone()
        {
            var copy = new FedParameters();
            copy.pairs.AddRange(pairs);
            return copy;
        }
    }

    /// <summary>One logical request as the game builds it (OP/FedRequestMessage.cs).</summary>
    public sealed class FedRequest
    {
        public FedRequest(string service, string path, string method = "GET", string scope = null)
        {
            Service = service;
            Path = path;
            Method = method;
            Scopes = scope == null ? new List<string>() : scope.Split(' ').ToList();
        }

        /// <summary>OnlineService key ("eve", "pandora", "auth"...), or null when <see cref="AbsoluteUri"/> is used.</summary>
        public string Service { get; }

        public string Path { get; }

        public Uri AbsoluteUri { get; set; }

        public string Method { get; }

        /// <summary>Empty = no token injected (e.g. verify, authorize, locate).</summary>
        public List<string> Scopes { get; }

        public FedParameters Parameters { get; } = new FedParameters();

        public static FedRequest Absolute(Uri uri, string method = "GET") => new FedRequest(null, null, method) { AbsoluteUri = uri };
    }

    /// <summary>
    /// A replay of the client stack (OP/Federation.cs, OP/Session.cs, OP/SessionForUser.cs, OP/FedRequestMessage.cs)
    /// that sends through any transport, in-process (<c>McvsServer.Handle</c>) or over a socket. Requests are built
    /// byte-for-byte the way the game builds them: service location through eve and pandora with FormatUri, relative
    /// path resolution with System.Uri (RFC 3986, as in ResolveUri), token placement by method, headers.
    /// </summary>
    public sealed class FedFederation
    {
        public const string GameClientId = "2620:66102:1.17.32:android:googleplay";

        private readonly Func<HttpRequest, HttpResponse> transport;
        private readonly Dictionary<string, string> located = new Dictionary<string, string>();
        private Dictionary<string, object> config;

        public FedFederation(Func<HttpRequest, HttpResponse> transport, string eveUrl, string datacenter = null,
                             string protocol = null, string clientId = GameClientId)
        {
            this.transport = transport;
            EveUrl = eveUrl;
            Datacenter = datacenter;
            Protocol = protocol;
            ClientId = clientId;
        }

        public static readonly string[] FedServices =
            { "auth", "storage", "leaderboard", "alert", "config", "groupchat", "lobby", "matchmaker", "message", "social", "asset", "game_stats" };

        public string EveUrl { get; }

        public string Datacenter { get; private set; }

        /// <summary>Federation.Protocol forced by the caller: null (Unspecified), "http" or "https" (LocalAnubis).</summary>
        public string Protocol { get; }

        public string ClientId { get; }

        /// <summary>Every request sent, with the response (for assertions on the exact wire form).</summary>
        public List<(HttpRequest Request, HttpResponse Response)> Exchanges { get; } = new List<(HttpRequest, HttpResponse)>();

        public Dictionary<string, object> Config => config ?? LoadConfig();

        /// <summary>Federation.FormatUri (OP/Federation.cs:456-466).</summary>
        public static string FormatUri(string uri, string protocol = null, bool preservePort = true)
        {
            var builder = new UriBuilder(uri.StartsWith("http") ? uri : "https://" + uri);
            bool defaultPort = builder.Port == 80 || builder.Port == 443 || builder.Port == -1;
            if (protocol != null && (defaultPort || !preservePort))
            {
                builder.Scheme = protocol;
                builder.Port = -1;
            }
            return builder.Uri.AbsoluteUri.TrimEnd('/');
        }

        /// <summary>LoadDataCenter + LoadConfig: datacenters (when none is set), then the urls object.</summary>
        public Dictionary<string, object> LoadConfig()
        {
            if (string.IsNullOrWhiteSpace(Datacenter))
            {
                var dcResponse = Send(new FedRequest("eve", $"config/{ClientId}/datacenters"));
                Ensure(dcResponse);
                var list = MiniJson.ParseArray(dcResponse.BodyText);
                var preferred = list.Cast<Dictionary<string, object>>().FirstOrDefault(d => JsonValue.GetBool(d, "preferred"));
                string name = preferred == null ? null : JsonValue.GetString(preferred, "name");
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Incorrect datacenter");
                Datacenter = name;
            }
            var response = Send(new FedRequest("eve", $"config/{ClientId}/datacenters/{Datacenter}/urls"));
            Ensure(response);
            var parsed = MiniJson.ParseObject(response.BodyText);
            if (parsed.ContainsKey("status") || parsed.ContainsKey("code"))
            {
                // OP/Configuration.cs: status (or code) is removed and parsed; anything but these throws FormatException.
                // "critical" is left out: the PC client's Configuration.ParseStatus no longer knows it (pc-port.md §4.4).
                string status = JsonValue.GetString(parsed, parsed.ContainsKey("status") ? "status" : "code");
                var known = new[] { "none", "maintenance", "maintenance_block", "maintenance_notice", "optional", "optional_with_wifi",
                                    "required", "upgrade_required", "upgrade_required_with_wifi" };
                if (!known.Contains(status)) throw new FormatException("Invalid status format : " + status);
                parsed.Remove("status");
                parsed.Remove("code");
            }
            config = parsed;
            return config;
        }

        /// <summary>Federation.LocateService (cached per key, like ServicesCache).</summary>
        public string Locate(string service)
        {
            if (located.TryGetValue(service, out var cached)) return cached;
            string url;
            if (service == "eve")
            {
                url = EveUrl; // returned as-is, no FormatUri
            }
            else if (!FedServices.Contains(service))
            {
                if (!Config.TryGetValue(service, out var value))
                    throw new InvalidOperationException($"Invalid online configuration. Config does not contain service [{service}]");
                url = FormatUri(JsonValue.AsString(value), Protocol);
            }
            else
            {
                var request = new FedRequest("pandora", $"locate/{service}");
                request.Parameters["client_id"] = ClientId;
                var response = Send(request);
                Ensure(response);
                url = FormatUri(response.BodyText, Protocol);
            }
            located[service] = url;
            return url;
        }

        /// <summary>FedRequestHandler.ResolveUri + PrepareToSend, then the transport. <paramref name="token"/> is injected when the request has scopes.</summary>
        public HttpResponse Send(FedRequest request, string token = null, string userAgentPrefix = null)
        {
            Uri uri = request.Service == null ? request.AbsoluteUri : new Uri(new Uri(Locate(request.Service)), request.Path);
            FedParameters parameters = request.Parameters.Clone();
            if (token != null && request.Scopes.Count != 0 && string.IsNullOrEmpty(uri.Query))
                parameters["access_token"] = token;

            string target;
            byte[] body = null;
            string contentType = null;
            if (request.Method == "POST")
            {
                target = uri.PathAndQuery;
                body = Encoding.UTF8.GetBytes(parameters.ToString());
                contentType = "application/x-www-form-urlencoded; charset=utf-8";
            }
            else if (request.Method == "DELETE")
            {
                var builder = new UriBuilder(uri);
                if (parameters.ContainsKey("access_token"))
                {
                    var only = new FedParameters();
                    only["access_token"] = parameters["access_token"];
                    builder.Query = only.ToString();
                }
                target = builder.Uri.PathAndQuery;
                body = Encoding.UTF8.GetBytes(parameters.ToString());
                contentType = "application/x-www-form-urlencoded; charset=utf-8";
            }
            else
            {
                var builder = new UriBuilder(uri);
                if (parameters.Count > 0) builder.Query = parameters.ToString();
                target = builder.Uri.PathAndQuery;
            }

            var headers = new HttpHeaders();
            headers.Add("Host", uri.Authority);
            headers.Add("Cache-Control", "NO-CACHE");
            headers.Add("User-Agent", $"{userAgentPrefix} ({ClientId}; {Datacenter})".Trim());
            if (contentType != null) headers.Add("Content-Type", contentType);
            var httpRequest = new HttpRequest(request.Method, target, headers, body);
            HttpResponse response = transport(httpRequest);
            Exchanges.Add((httpRequest, response));
            return response;
        }

        /// <summary>EnsureSuccessStatusCode: 2xx and 302 are success.</summary>
        public static void Ensure(HttpResponse response)
        {
            if (response.StatusCode == 302 || (response.StatusCode >= 200 && response.StatusCode < 300)) return;
            throw new FedRequestFailed(response);
        }
    }

    public sealed class FedRequestFailed : Exception
    {
        public FedRequestFailed(HttpResponse response) : base("request failed with " + response.StatusCode + " " + response.BodyText)
        {
            Response = response;
        }

        public HttpResponse Response { get; }
    }

    /// <summary>
    /// OP/Session.cs and OP/SessionForUser.cs: credential, password, device id, the per-scope-set token cache, Open
    /// (authorize + users/me) and the 401 rule of Federation.Inspect (drop every token, re-authorize once, retry).
    /// </summary>
    public class FedSession
    {
        private readonly List<(HashSet<string> Scopes, string Token)> tokens = new List<(HashSet<string>, string)>();

        public FedSession(FedFederation federation, string credential, string password, string deviceId)
        {
            Federation = federation;
            Credential = credential;
            Password = password;
            DeviceId = deviceId;
        }

        public FedFederation Federation { get; }

        /// <summary>The subject credential ("anonymous:abc"; for SessionForUser the for-user credential).</summary>
        public string Credential { get; }

        public string Password { get; }

        public string DeviceId { get; }

        public string FedId { get; private set; }

        public IReadOnlyDictionary<string, string> DeviceArgs { get; set; }

        public int AuthorizeCount { get; private set; }

        public string CredentialType => Credential.Substring(0, Credential.IndexOf(':'));

        public string Username => Credential.Substring(Credential.IndexOf(':') + 1);

        /// <summary>Session.Open(["auth"], initialScopes): scopes = auth ∪ initial, authorize, then users/me unless fed_id.</summary>
        public void Open(IEnumerable<string> initialScopes)
        {
            var scopes = new List<string> { "auth" }.Union(initialScopes ?? Enumerable.Empty<string>()).ToList();
            GetAccessToken(scopes);
            if (CredentialType != "fed_id")
            {
                var me = MiniJson.ParseObject(Json(new FedRequest("auth", "users/me", "GET", "auth")).BodyText);
                FedId = "fed_id:" + JsonValue.GetString(me, "account");
            }
            else
            {
                FedId = Credential;
            }
        }

        /// <summary>Session.GetAccessToken: the first cached token whose scope set contains the request's, else a new authorize with exactly those scopes.</summary>
        public string GetAccessToken(IEnumerable<string> scopes)
        {
            var wanted = new HashSet<string>(scopes);
            foreach (var record in tokens)
                if (wanted.IsSubsetOf(record.Scopes)) return record.Token;
            string token = Authorize(scopes.ToList());
            tokens.Add((wanted, token));
            return token;
        }

        public void ResetToken() => tokens.Clear();

        /// <summary>Sends with token injection and the client's 401 handling (OP/Federation.cs Inspect).</summary>
        public HttpResponse Send(FedRequest request)
        {
            string token = request.Scopes.Count == 0 ? null : GetAccessToken(request.Scopes);
            HttpResponse response = Federation.Send(request, token);
            if (response.StatusCode == 401 && request.Path != "authorize")
            {
                bool hasToken = token != null || request.Parameters.ContainsKey("access_token");
                if (!hasToken) throw new FedRequestFailed(response); // "Unauthorize. Did you forget to supply an access token?"
                ResetToken();
                token = request.Scopes.Count == 0 ? null : GetAccessToken(request.Scopes);
                response = Federation.Send(request, token);
            }
            return response;
        }

        /// <summary>Session.JsonRequest: success required, body parsed as JSON (an empty body would be a null token).</summary>
        public HttpResponse Json(FedRequest request)
        {
            HttpResponse response = Send(request);
            FedFederation.Ensure(response);
            if (string.IsNullOrEmpty(response.BodyText)) throw new InvalidOperationException("empty JSON body");
            MiniJson.Parse(response.BodyText);
            return response;
        }

        protected virtual FedRequest BuildAuthorize(List<string> scopes)
        {
            var request = new FedRequest("auth", "authorize", "POST");
            request.Parameters["username"] = Username;
            request.Parameters["credential_type"] = CredentialType;
            request.Parameters["password"] = Password;
            request.Parameters["device_id"] = DeviceId;
            request.Parameters["client_id"] = Federation.ClientId;
            request.Parameters["scope"] = string.Join(" ", scopes);
            if (DeviceArgs != null)
                foreach (var kv in DeviceArgs) request.Parameters["device_" + kv.Key] = kv.Value;
            return request;
        }

        /// <summary>Session.Authorize: access_token and scope are both dereferenced; the scope set should equal the request's.</summary>
        private string Authorize(List<string> scopes)
        {
            AuthorizeCount++;
            HttpResponse response = Federation.Send(BuildAuthorize(scopes));
            FedFederation.Ensure(response);
            var body = MiniJson.ParseObject(response.BodyText);
            if (!body.ContainsKey("access_token") || !body.ContainsKey("scope"))
                throw new NullReferenceException("authorize response without access_token/scope");
            var echoed = JsonValue.AsString(body["scope"]).Split(' ');
            if (!new HashSet<string>(echoed).SequenceEqual(new HashSet<string>(scopes)))
                throw new InvalidOperationException("new token does not contain the requested scopes " + string.Join(",", scopes));
            return JsonValue.AsString(body["access_token"]);
        }

        // ----------------------------------------------------------------------------- Session API replays

        /// <summary>Session.EncryptedAccessToken.</summary>
        public string EncryptedAccessToken(string nonce, string scope)
        {
            var request = new FedRequest("auth", "encrypt_token", "GET", scope);
            request.Parameters["nonce"] = nonce;
            HttpResponse response = Send(request);
            FedFederation.Ensure(response);
            return response.BodyText;
        }

        /// <summary>Session.Verify(token, nonce, credential) (LocalAnubis Controller) or Session.Verify(token) (Finder).</summary>
        public Dictionary<string, object> Verify(string accessToken, string nonce = null, string credential = null)
        {
            var request = new FedRequest("auth", "verify");
            request.Parameters["access_token"] = accessToken;
            if (nonce != null) request.Parameters["nonce"] = nonce;
            if (credential != null) request.Parameters["credential"] = credential;
            return MiniJson.ParseObject(Json(request).BodyText);
        }

        /// <summary>Session.GetAccountsForDeviceId.</summary>
        public List<object> AccountsForDevice(string deviceId, bool allGames = false)
        {
            var request = new FedRequest("auth", "users", "GET", "auth_admin_ro");
            request.Parameters["device_id"] = deviceId;
            if (allGames) request.Parameters.Set("all_games", true);
            var parsed = MiniJson.Parse(Json(request).BodyText);
            return parsed as List<object> ?? throw new InvalidCastException("users?device_id did not return an array");
        }

        /// <summary>Session.CreateAlias.</summary>
        public string CreateAlias()
        {
            var body = MiniJson.ParseObject(Json(new FedRequest("auth", "games/mygame/alias", "POST", "auth")).BodyText);
            return JsonValue.GetString(body, "alias");
        }

        /// <summary>Session.LookupAlias.</summary>
        public string LookupAlias(string alias)
        {
            var body = MiniJson.ParseObject(Json(new FedRequest("auth", $"games/mygame/alias/{alias}", "GET", "auth")).BodyText);
            return JsonValue.GetString(body, "credential");
        }

        /// <summary>Session.GetAccountInfo(credential): users/me (scope auth) or users/{escaped} (scope auth_admin_ro).</summary>
        public HttpResponse AccountInfo(string credential = "me")
        {
            string path = "users/" + Uri.EscapeDataString(credential);
            return Send(new FedRequest("auth", path, "GET", credential == "me" ? "auth" : "auth_admin_ro"));
        }

        /// <summary>Session.AddCompliances.</summary>
        public Dictionary<string, object> AddCompliances(string credential, string country, int age)
        {
            var request = new FedRequest("auth", $"users/{Uri.EscapeDataString(credential)}/compliance", "POST", "auth");
            request.Parameters["country"] = country;
            request.Parameters.Set("age", age);
            return MiniJson.ParseObject(Json(request).BodyText);
        }

        /// <summary>Session.LinkCredential.</summary>
        public HttpResponse LinkCredential(string credential, string secret, string ifExists)
        {
            var request = new FedRequest("auth", "users/me/credentials", "POST", string.IsNullOrEmpty(secret) ? "auth_game_admin" : "auth");
            request.Parameters["if_exists"] = ifExists;
            request.Parameters["credential"] = credential;
            if (!string.IsNullOrEmpty(secret)) request.Parameters["password"] = secret;
            return Send(request);
        }

        /// <summary>Session.GetTransferCode.</summary>
        public Dictionary<string, object> GetTransferCode() =>
            MiniJson.ParseObject(Json(new FedRequest("auth", "users/me/transfer_code", "POST", "auth")).BodyText);

        /// <summary>Session.AuthorizeTransferCode (GET authorize, no token).</summary>
        public HttpResponse AuthorizeTransferCode(string code)
        {
            var request = new FedRequest("auth", "authorize");
            request.Parameters["grant_type"] = "transfer_code";
            request.Parameters["scope"] = "auth";
            request.Parameters["client_id"] = Federation.ClientId;
            request.Parameters["transfer_code"] = code;
            return Send(request);
        }
    }

    /// <summary>SessionForUser: the game server's super credential authorizes on behalf of <c>forUser</c>.</summary>
    public sealed class FedSessionForUser : FedSession
    {
        public FedSessionForUser(FedFederation federation, string superCredential, string superPassword, string forUser, string deviceId)
            : base(federation, forUser, "", deviceId ?? "Unknown")
        {
            Super = superCredential;
            SuperPassword = superPassword;
        }

        public string Super { get; }

        public string SuperPassword { get; }

        /// <summary>SessionForUser.GetAuthorizeRequest: base fields, then username/credential_type/password replaced in place and for_* appended.</summary>
        protected override FedRequest BuildAuthorize(List<string> scopes)
        {
            FedRequest request = base.BuildAuthorize(scopes);
            request.Parameters["username"] = Super.Substring(Super.IndexOf(':') + 1);
            request.Parameters["credential_type"] = Super.Substring(0, Super.IndexOf(':'));
            request.Parameters["password"] = SuperPassword;
            request.Parameters["for_username"] = Username;
            request.Parameters["for_credential_type"] = CredentialType;
            return request;
        }

        /// <summary>GetSessionForUser: initialScopes minus "auth", then Open(["auth"], ...).</summary>
        public new void Open(IEnumerable<string> initialScopes) => base.Open(initialScopes.Where(s => s != "auth"));
    }

    /// <summary>Scope lists the game uses (EN/AuthenticationComponent.cs, EN/LocalAnubisManager.cs).</summary>
    public static class GameScopes
    {
        public static readonly string[] Client = { "auth", "lobby", "config" };

        public static readonly string[] Server =
        {
            "storage_restricted", "chat", "message", "storage_ro", "leaderboard_override", "social_trophies", "storage", "social_group",
            "leaderboard_ro", "social_event", "clusters", "config", "message_secured", "leaderboard_restricted", "message_broadcast",
            "alert", "auth", "lobby", "leaderboard", "social", "social_clan",
        };

        public static readonly string[] LocalAnubis = { "auth", "auth_admin_ro" };

        /// <summary>The device args of OpenAnonymousSession, in insertion order (firmware assigned twice keeps its slot).</summary>
        public static IReadOnlyDictionary<string, string> DeviceArgs() => new Dictionary<string, string>
        {
            ["firmware"] = "10",
            ["country"] = "US",
            ["carrier"] = "T-Mobile",
            ["model"] = "Pixel 3",
            ["resolution"] = "1080x2160",
            ["language"] = "en",
        };
    }
}
