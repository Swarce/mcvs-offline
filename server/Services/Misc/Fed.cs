using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Misc
{
    /// <summary>
    /// The account a Federation request acts for, resolved from its <c>access_token</c>. For an on-behalf token (a
    /// game server's for_username session) this is the player, not the game server.
    /// </summary>
    public sealed class Caller
    {
        public Caller(TokenInfo token)
        {
            if (token == null) throw new ArgumentNullException("token");
            Token = token;
        }

        public TokenInfo Token { get; private set; }

        public string AccountId { get { return Token.AccountId; } }

        /// <summary>"fed_id:&lt;account&gt;".</summary>
        public string FedId { get { return McvsConstants.FedIdPrefix + Token.AccountId; } }

        /// <summary>The subject's own credential ("anonymous:..." for players, "game:..." for a game server).</summary>
        public string Credential { get { return Token.Credential; } }

        public override string ToString()
        {
            return FedId + (Token.IsOnBehalf ? " (via " + Token.Actor + ")" : "");
        }
    }

    /// <summary>
    /// Helpers shared by the MISC modules: token resolution, credential paths, Gameloft's custom-field encoding,
    /// parameter parsing and the error responses the client copes with (report 02 §1.6: 401 makes the client drop its
    /// tokens and re-authorize once; 404 is a normal "absent" for several callers; never 403).
    /// </summary>
    public static class Fed
    {
        /// <summary>Prefix of JSON-typed custom fields (<c>JsonInterpretedConverter</c>, report 02 §1.4).</summary>
        public const string JsonValuePrefix = "_json_:";

        /// <summary>The token sent with the request: query for GET, form body for POST, both for DELETE.</summary>
        public static string AccessToken(HttpRequest request)
        {
            string token = request.Param("access_token");
            return string.IsNullOrEmpty(token) ? null : token;
        }

        /// <summary>The caller, or null when the request has no token or this server does not know it.</summary>
        public static Caller TryGetCaller(ServerContext context, HttpRequest request)
        {
            string token = AccessToken(request);
            if (token == null) return null;
            TokenInfo info = context.Tokens.Resolve(token);
            return info == null || string.IsNullOrEmpty(info.AccountId) ? null : new Caller(info);
        }

        /// <summary>
        /// Resolves the caller, or produces the 401 the client recovers from: with an access_token parameter it
        /// clears its tokens, re-authorizes and retries once (report 02 §1.6). Unknown tokens only happen after
        /// tokens.json was lost, so this is the correct way back.
        /// </summary>
        public static bool RequireCaller(ServerContext context, HttpRequest request, Log log, out Caller caller, out HttpResponse failure)
        {
            caller = TryGetCaller(context, request);
            failure = null;
            if (caller != null) return true;
            string token = AccessToken(request);
            string reason = token == null ? "missing access_token" : "unknown access_token";
            if (log != null) log.Warn(request.Method + " " + request.Path + ": " + reason + " -> 401 (the client re-authorizes once)");
            failure = HttpResponse.Error(HttpStatus.Unauthorized, reason);
            return false;
        }

        /// <summary>"fed_id:" + account id.</summary>
        public static string FedIdOf(string accountId)
        {
            return McvsConstants.FedIdPrefix + accountId;
        }

        /// <summary>
        /// The account id a credential path segment or parameter names: "me" (the caller), "fed_id:&lt;id&gt;", or any
        /// other "type:username" (looked up, else the id the account would get: ids are derived from credentials,
        /// see AccountStore). Null when it names no account (empty, malformed, "me" without a caller).
        /// </summary>
        public static string ResolveAccountId(ServerContext context, Caller caller, string credential)
        {
            if (string.IsNullOrEmpty(credential)) return null;
            string text = credential.Trim();
            if (string.Equals(text, Credential.Me, StringComparison.OrdinalIgnoreCase)) return caller == null ? null : caller.AccountId;
            Credential parsed;
            if (!Credential.TryParse(text, out parsed) || !parsed.IsValid) return null;
            if (parsed.Type == Credential.FedId) return parsed.Username.IndexOf(':') >= 0 ? null : parsed.Username;
            if (parsed.Type == "group") return null;
            Account account = context.Accounts.Find(parsed.ToString());
            if (account != null) return account.Id;
            try
            {
                return AccountStore.DeriveAccountId(parsed.ToString());
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Decodes a custom field value: "_json_:&lt;json&gt;" becomes the JSON value (JsonInterpretedConverter, used for
        /// clan fields); anything else stays the string the client sent.
        /// </summary>
        public static object ParseFieldValue(string raw)
        {
            if (raw == null) return null;
            if (raw.StartsWith(JsonValuePrefix, StringComparison.Ordinal))
            {
                object value;
                if (MiniJson.TryParse(raw.Substring(JsonValuePrefix.Length), out value)) return value;
            }
            return raw;
        }

        /// <summary>
        /// Every parameter of the form (or query) that is not a known control parameter, decoded with
        /// <see cref="ParseFieldValue"/>. <paramref name="underscoreOnly"/> keeps only "_"-prefixed custom fields.
        /// </summary>
        public static Dictionary<string, object> CustomFields(HttpRequest request, ICollection<string> reserved, bool underscoreOnly)
        {
            var result = Obj();
            ParamCollection source = request.Form.Count > 0 ? request.Form : request.Query;
            foreach (var kv in source)
            {
                if (kv.Key.Length == 0 || kv.Key == "access_token") continue;
                if (reserved != null && reserved.Contains(kv.Key)) continue;
                if (underscoreOnly && kv.Key[0] != '_') continue;
                if (!result.ContainsKey(kv.Key)) result[kv.Key] = ParseFieldValue(kv.Value);
            }
            return result;
        }

        /// <summary>Integer parameter with bounds (invalid or missing values give the default).</summary>
        public static int IntParam(HttpRequest request, string name, int defaultValue, int min, int max)
        {
            string text = request.Param(name);
            long value;
            if (string.IsNullOrEmpty(text) || !long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return defaultValue;
            return (int)Num.Clamp(value, min, max);
        }

        /// <summary>Lenient boolean parameter ("True"/"False" in C# casing, "1"/"0", "true"/"false").</summary>
        public static bool BoolParam(HttpRequest request, string name, bool defaultValue)
        {
            return JsonValue.AsBool(request.Param(name)) ?? defaultValue;
        }

        /// <summary>A comma- (or space-) separated list parameter, trimmed, empty entries dropped.</summary>
        public static List<string> ListParam(HttpRequest request, string name, params char[] separators)
        {
            var list = new List<string>();
            string text = request.Param(name);
            if (string.IsNullOrEmpty(text)) return list;
            char[] seps = separators == null || separators.Length == 0 ? new[] { ',' } : separators;
            foreach (string part in text.Split(seps))
            {
                string item = part.Trim();
                if (item.Length > 0 && !list.Contains(item)) list.Add(item);
            }
            return list;
        }

        /// <summary>A new JSON object (ordinal keys, insertion order kept).</summary>
        public static Dictionary<string, object> Obj()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>The current time as the services wrote it ("2026-10-25T10:00:00Z").</summary>
        public static string Now(ServerContext context)
        {
            return TimeFormat.Iso8601(context.Clock.UtcNow);
        }

        /// <summary>A new random id (24 lower-case hex characters, like Gameloft's object ids).</summary>
        public static string NewId()
        {
            return Ids.RandomHex(12);
        }

        /// <summary>
        /// An integer setting under the module's own key, clamped to [min, max], e.g.
        /// LongSetting(context, "alerts.keepAliveSeconds", 20, 1, 300).
        /// </summary>
        public static long LongSetting(ServerContext context, string path, long defaultValue, long min, long max)
        {
            return Num.Clamp(context.Settings.GetLong(path, defaultValue), min, max);
        }

        public static HttpResponse NotFound(string message)
        {
            return HttpResponse.Error(HttpStatus.NotFound, message);
        }

        public static HttpResponse BadRequest(string message)
        {
            return HttpResponse.Error(HttpStatus.BadRequest, message);
        }

        /// <summary>A comma list of names for logs.</summary>
        public static string Describe(IEnumerable<string> items)
        {
            var list = new List<string>(items ?? new string[0]);
            return list.Count == 0 ? "-" : string.Join(",", list.ToArray());
        }
    }
}
