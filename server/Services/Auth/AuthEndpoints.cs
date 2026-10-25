using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>
    /// The auth (Janus) endpoints. Wire formats and the evidence for each answer: docs/protocol/auth.md.
    /// </summary>
    internal sealed class AuthEndpoints
    {
        /// <summary>
        /// expires_in we report. Tokens never expire here and the C# client ignores the field (report 02 §3.3); a year
        /// keeps "now + expires_in" far from 32-bit overflow for any native consumer.
        /// </summary>
        public const long ReportedTokenLifetimeSeconds = 365L * 24 * 3600;

        private readonly ServerContext context;
        private readonly Log log;
        private readonly Authenticator auth;
        private readonly AliasRegistry aliases;
        private readonly TransferCodes transferCodes;

        public AuthEndpoints(ServerContext context, Log log, Authenticator auth, AliasRegistry aliases, TransferCodes transferCodes)
        {
            this.context = context;
            this.log = log;
            this.auth = auth;
            this.aliases = aliases;
            this.transferCodes = transferCodes;
        }

        // ------------------------------------------------------------------------------------------- authorize

        /// <summary>
        /// POST authorize (password grant, OP/Session.cs GetAuthorizeRequest; on-behalf grant, OP/SessionForUser.cs) and
        /// GET authorize?grant_type=transfer_code (OP/Session.cs GetAuthorizeTransferCodeRequest).
        /// </summary>
        public HttpResponse Authorize(HttpRequest request)
        {
            string grantType = request.Param("grant_type");
            if (!string.IsNullOrEmpty(grantType) && !string.Equals(grantType, "password", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(grantType, "transfer_code", StringComparison.OrdinalIgnoreCase)) return AuthorizeTransferCode(request);
                log.Warn("authorize with unsupported grant_type '" + grantType + "'");
                return AuthResponses.Error(HttpStatus.BadRequest, "unsupported_grant_type", "grant_type '" + grantType + "' is not supported");
            }

            string error;
            Credential login;
            if (!TryCredential(request.Param("credential_type"), request.Param("username"), "username/credential_type", out login, out error))
                return AuthResponses.BadRequest(error);

            string deviceId = request.Param("device_id");
            string clientId = ClientIdOf(request);
            List<string> scopes = TokenService.ParseScopes(request.Param("scope"));
            string forUsername = request.Param("for_username");

            Credential subject = login;
            string actor = null;
            if (!string.IsNullOrEmpty(forUsername))
            {
                // Game-server session for a player (OP/SessionForUser.cs GetAuthorizeRequest): the TS/GS authorizes with
                // its super credential plus for_username/for_credential_type and must get the player's own account,
                // the one the client's anonymous session got (verify-C2 §1.5).
                if (login.Type != Credential.Game)
                    return auth.Unauthorized(request, login + " may not act on behalf of other users (only game: credentials can)");
                string forType = request.Param("for_credential_type");
                if (string.IsNullOrEmpty(forType))
                {
                    log.Warn("on-behalf authorize without for_credential_type; assuming anonymous");
                    forType = Credential.Anonymous;
                }
                if (!TryCredential(forType, forUsername, "for_username/for_credential_type", out subject, out error))
                    return AuthResponses.BadRequest(error);
                if (subject.Type == Credential.Game)
                    return AuthResponses.BadRequest("on-behalf grants are for players, not for " + subject);
                actor = login.ToString();
            }

            if (login.Type == Credential.Game)
            {
                string refusal = CheckGameCredential(login, request.Param("password"));
                if (refusal != null) return auth.Unauthorized(request, refusal);
            }

            // SessionForUser sends "Unknown" when the game server has no device id for the player: not worth recording.
            bool recordDevice = !string.IsNullOrEmpty(deviceId) && !string.Equals(deviceId, "Unknown", StringComparison.Ordinal);
            Account account = context.Accounts.GetOrCreate(subject.ToString(), recordDevice ? deviceId : null);
            TokenInfo token = context.Tokens.Issue(account.Id, subject.ToString(), clientId, scopes, deviceId, actor);

            log.Info("authorize " + (actor != null ? actor + " on behalf of " : "") + subject + " -> account " + account.Id
                     + ", scope [" + token.ScopeString + "]");
            return TokenResponse(token);
        }

        private HttpResponse AuthorizeTransferCode(HttpRequest request)
        {
            string code = request.Param("transfer_code");
            string accountId = transferCodes.Redeem(code);
            Account account = accountId == null ? null : context.Accounts.FindById(accountId);
            if (account == null)
            {
                log.Warn("transfer code '" + code + "' is unknown, used or expired");
                return AuthResponses.Error(HttpStatus.NotFound, "invalid_grant", "unknown or expired transfer code");
            }
            TokenInfo token = context.Tokens.Issue(account.Id, account.FedId, ClientIdOf(request),
                                                   TokenService.ParseScopes(request.Param("scope")), null, null);
            log.Info("transfer code redeemed for account " + account.Id);
            return TokenResponse(token);
        }

        /// <summary>
        /// The authorize answer. The client reads only access_token and scope, and both are required: a missing key
        /// is a NullReferenceException outside the online-error handling (OP/Session.cs Authorize, report 02 §0.5).
        /// "scope" echoes the request, in its order (the client asserts the two sets are equal).
        /// </summary>
        private static HttpResponse TokenResponse(TokenInfo token)
        {
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["access_token"] = token.Token;
            body["token_type"] = "bearer";
            body["expires_in"] = ReportedTokenLifetimeSeconds;
            body["scope"] = token.ScopeString;
            return HttpResponse.Json(body);
        }

        /// <summary>
        /// Null if <paramref name="login"/> may log in as a game server. Any "game:" credential may (the TS/GS take it
        /// from cmd_args, args.txt or ServerInfos.json); one that differs from settings superCredential/superPassword
        /// is logged, and refused only when settings <c>auth.requireSuperPassword</c> is true (for LAN hosts that do not
        /// want guests to obtain on-behalf tokens). A refused game server cannot load any profile, so the default is
        /// to let it in.
        /// </summary>
        private string CheckGameCredential(Credential login, string password)
        {
            bool sameCredential = string.Equals(login.ToString(), context.Settings.SuperCredential, StringComparison.Ordinal);
            bool samePassword = string.Equals(password ?? "", context.Settings.SuperPassword, StringComparison.Ordinal);
            if (sameCredential && samePassword) return null;
            string problem = !sameCredential
                ? login + " is not the configured super credential " + context.Settings.SuperCredential
                : "wrong password for " + login;
            if (context.Settings.GetBool("auth.requireSuperPassword", false)) return problem;
            log.Warn("game-server login accepted although " + problem + " (set auth.requireSuperPassword to refuse)");
            return null;
        }

        // ------------------------------------------------------------------------------------------- users

        /// <summary>
        /// GET users/{credential}: "me" needs the caller's token (OP/Session.cs GetAccountInfoRequest, scope auth); any
        /// other credential is a lookup (scope auth_admin_ro, or no token at all from Federation.CheckIfUserExists) and
        /// answers 404 when unknown, which SocialComponent treats as "no such account".
        /// </summary>
        public HttpResponse GetAccount(HttpRequest request)
        {
            string credential = request.RouteValue("credential");
            Credential parsed;
            if (!Credential.TryParse(credential, out parsed) || (!parsed.IsMe && !parsed.IsValid))
                return AuthResponses.NotFound("'" + credential + "' is not a credential");

            if (parsed.IsMe)
            {
                AuthIdentity identity = auth.Authenticate(request);
                if (identity == null) return auth.Unauthorized(request, "users/me needs a valid access_token");
                Account me = context.Accounts.FindById(identity.AccountId);
                if (me == null) return auth.Unauthorized(request, "account " + identity.AccountId + " no longer exists");
                return HttpResponse.Json(AuthResponses.AccountInfo(me));
            }

            Account account = auth.ResolveAccount(null, parsed.ToString());
            if (account == null) return AuthResponses.NotFound("no account for " + parsed);
            return HttpResponse.Json(AuthResponses.AccountInfo(account));
        }

        /// <summary>
        /// GET users?device_id=&lt;id&gt;[&amp;all_games=True] (OP/Session.cs GetAccountsForDeviceId): always [].
        /// The TS calls it on first run when the account has exactly one anonymous credential and must get a JSON
        /// array (verify-C2 §1.6); any account listed would be merged into, then the handshake restarts
        /// (GM/GameProfileSection.cs CreateProfile). Every player keeps exactly one account here, so there is never
        /// anything to merge.
        /// </summary>
        public HttpResponse AccountsForDevice(HttpRequest request)
        {
            if (auth.Authenticate(request) == null)
                log.Debug("users?device_id without a valid token; answering [] anyway");
            return HttpResponse.EmptyArray();
        }

        /// <summary>
        /// GET users/{credential}/games/{clientId} (OP/Session.cs CheckIfUserExists): only the status matters,
        /// 200 when the account exists, 404 otherwise.
        /// </summary>
        public HttpResponse UserPlaysGame(HttpRequest request)
        {
            AuthIdentity identity = auth.Authenticate(request);
            Account account = auth.ResolveAccount(identity, request.RouteValue("credential"));
            if (account == null) return AuthResponses.NotFound("no account for " + request.RouteValue("credential"));
            Dictionary<string, object> body = AuthResponses.AccountInfo(account);
            body["client_id"] = request.RouteValue("clientId");
            return HttpResponse.Json(body);
        }

        /// <summary>
        /// POST|GET users/{credential}/compliance (OP/Session.cs AddCompliances/GetCompliances): an object of rules,
        /// each {"date", "compliant", "reasons"}. All compliant gives the game's lowest age limit, 13
        /// (GM/AgeGateChecker.cs GetAgeLimit); errors are caught there and give 16.
        /// </summary>
        public HttpResponse Compliance(HttpRequest request)
        {
            var rule = new Dictionary<string, object>(StringComparer.Ordinal);
            rule["date"] = TimeFormat.Iso8601(context.Clock.UtcNow);
            rule["compliant"] = true;
            rule["reasons"] = new Dictionary<string, object>(StringComparer.Ordinal);
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["default"] = rule;
            return HttpResponse.Json(body);
        }

        /// <summary>
        /// POST users/me/credentials (OP/Session.cs GetLinkCredentialRequest, EN/AuthenticationComponent.cs
        /// GetLinkAnonymousRequest): links <c>credential</c> to the caller's account. if_exists says what to do when
        /// another account owns it: error (409), relink (move this credential), relink_all (move every credential of
        /// that account, i.e. merge it in). Profiles stay with their account ids; the moved-from account keeps its
        /// data but nobody logs into it any more.
        /// </summary>
        public HttpResponse LinkCredential(HttpRequest request)
        {
            AuthIdentity identity = auth.Authenticate(request);
            if (identity == null) return auth.Unauthorized(request, "linking a credential needs a valid access_token");

            string text = request.Param("credential");
            Credential credential;
            if (!Credential.TryParse(text, out credential) || !credential.IsValid || credential.IsMe
                || credential.Type == Credential.FedId || credential.Type == Credential.Game || credential.Type == "me")
            {
                return AuthResponses.BadRequest("cannot link credential '" + text + "'");
            }

            string normalized = credential.ToString();
            string ifExists = (request.Param("if_exists") ?? "error").Trim().ToLowerInvariant();
            Account owner = context.Accounts.Find(normalized);
            if (owner != null && owner.Id != identity.AccountId)
            {
                if (ifExists == "relink")
                {
                    context.Accounts.LinkCredential(identity.AccountId, normalized);
                    log.Info("relinked " + normalized + " from account " + owner.Id + " to " + identity.AccountId);
                }
                else if (ifExists == "relink_all")
                {
                    foreach (string c in owner.Credentials) context.Accounts.LinkCredential(identity.AccountId, c);
                    log.Info("relinked all credentials of account " + owner.Id + " (" + string.Join(", ", owner.Credentials.ToArray())
                             + ") to " + identity.AccountId);
                }
                else
                {
                    log.Info("not linking " + normalized + ": it belongs to account " + owner.Id + " and if_exists=" + ifExists);
                    return AuthResponses.Error(HttpStatus.Conflict, "credential_in_use", normalized + " is linked to another account");
                }
            }
            else if (owner == null)
            {
                context.Accounts.LinkCredential(identity.AccountId, normalized);
                log.Info("linked " + normalized + " to account " + identity.AccountId);
            }

            Account account = context.Accounts.FindById(identity.AccountId);
            return HttpResponse.Json(AuthResponses.AccountInfo(account));
        }

        /// <summary>POST users/me/transfer_code (OP/Session.cs GetCreateTransferCodeRequest): {"value", "expiration"}.</summary>
        public HttpResponse CreateTransferCode(HttpRequest request)
        {
            AuthIdentity identity = auth.Authenticate(request);
            if (identity == null) return auth.Unauthorized(request, "a transfer code needs a valid access_token");
            DateTime expires;
            string code = transferCodes.Create(identity.AccountId, out expires);
            log.Info("transfer code created for account " + identity.AccountId + ", expires " + TimeFormat.Iso8601(expires));
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["value"] = code;
            body["expiration"] = TimeFormat.Iso8601(expires);
            return HttpResponse.Json(body);
        }

        /// <summary>POST devices/mydevice (OP/Session.cs SetDeviceInfo, unused by the game): accepted, nothing kept.</summary>
        public HttpResponse SetDeviceInfo(HttpRequest request)
        {
            if (auth.Authenticate(request) == null) return auth.Unauthorized(request, "devices/mydevice needs a valid access_token");
            return HttpResponse.EmptyObject();
        }

        // ------------------------------------------------------------------------------------------- lobby tokens

        /// <summary>
        /// GET encrypt_token?nonce=&amp;access_token= (OP/Session.cs EncryptedAccessToken, scope lobby / lobby_admin):
        /// plain text, sent to the Lobby/Controller in "login" and checked there through verify
        /// (EN/AnubisClientSocket.cs Login, LA/BaseServer.cs Login). Our "encryption" is the identity: the token is
        /// opaque to the client and already self-describing, and verify resolves it.
        /// </summary>
        public HttpResponse EncryptToken(HttpRequest request)
        {
            string token = auth.GetAccessToken(request);
            AuthIdentity identity = auth.Resolve(token);
            if (identity == null) return auth.Unauthorized(request, "encrypt_token needs a valid access_token");
            return HttpResponse.Text(token);
        }

        /// <summary>
        /// GET verify?access_token=&lt;token to check&gt;[&amp;nonce=&amp;credential=system%3AAnubis] (OP/Session.cs Verify;
        /// LA/FinderHTTPMessageHandler.cs ParseUser, LA/BaseServer.cs Login). The request carries no token of its own.
        /// The caller dereferences credential, account and client_id (OP/RoomUser.cs FromAccountInfo builds
        /// fed_id:&lt;account&gt;), so all three are always strings; account is the id storage keys the profile by.
        /// </summary>
        public HttpResponse Verify(HttpRequest request)
        {
            string token = request.Param("access_token");
            if (string.IsNullOrEmpty(token)) return AuthResponses.BadRequest("verify needs the access_token to check");
            AuthIdentity identity = auth.Resolve(token);
            if (identity == null) return auth.Unauthorized(request, "token to verify is unknown");

            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["credential"] = string.IsNullOrEmpty(identity.Credential) ? identity.FedId : identity.Credential;
            body["account"] = identity.AccountId;
            body["client_id"] = string.IsNullOrEmpty(identity.Token.ClientId) ? McvsConstants.ClientId : identity.Token.ClientId;
            body["scope"] = identity.Token.ScopeString;
            return HttpResponse.Json(body);
        }

        // ------------------------------------------------------------------------------------------- aliases

        /// <summary>
        /// POST games/mygame/alias (OP/Session.cs CreateAlias; uncaught in InitPlayerName, verify-C2 §1.6) and
        /// GET games/mygame/alias: {"alias": "&lt;9 digits&gt;"}, the same value for an account every time.
        /// </summary>
        public HttpResponse GetOrCreateAlias(HttpRequest request)
        {
            AuthIdentity identity = auth.Authenticate(request);
            if (identity == null) return auth.Unauthorized(request, "an alias needs a valid access_token");
            NoteGame(request.RouteValue("game"));
            string alias = aliases.GetOrCreate(identity.AccountId);
            if (alias == null) return auth.Unauthorized(request, "account " + identity.AccountId + " no longer exists");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["alias"] = alias;
            return HttpResponse.Json(body);
        }

        /// <summary>GET games/mygame/alias/{alias} (OP/Session.cs LookupAlias): {"credential": "fed_id:&lt;account&gt;"}, 404 if unknown.</summary>
        public HttpResponse LookupAlias(HttpRequest request)
        {
            NoteGame(request.RouteValue("game"));
            string alias = request.RouteValue("alias");
            Account owner = aliases.FindOwner(alias);
            if (owner == null) return AuthResponses.NotFound("no account has alias '" + alias + "'");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["credential"] = owner.FedId;
            return HttpResponse.Json(body);
        }

        // ------------------------------------------------------------------------------------------- helpers

        /// <summary>
        /// Parses a credential from its form fields. The type is required and must not be none/me; fed_id ids must be
        /// plain identifiers because they become account ids (and storage keys).
        /// </summary>
        private static bool TryCredential(string type, string username, string fields, out Credential credential, out string error)
        {
            credential = default(Credential);
            error = null;
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(username))
            {
                error = fields + " are required";
                return false;
            }
            credential = new Credential(type, username);
            if (!credential.IsValid || credential.Type == Credential.Me)
            {
                error = "'" + type + ":" + username + "' is not a usable credential";
                return false;
            }
            if (credential.Type == Credential.FedId && !IsPlainId(credential.Username))
            {
                error = "'" + username + "' is not a valid account id";
                return false;
            }
            return true;
        }

        /// <summary>1-64 of [A-Za-z0-9_-].</summary>
        internal static bool IsPlainId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        private static string ClientIdOf(HttpRequest request)
        {
            string clientId = request.Param("client_id");
            return string.IsNullOrEmpty(clientId) ? McvsConstants.ClientId : clientId;
        }

        private void NoteGame(string game)
        {
            if (!string.Equals(game, "mygame", StringComparison.OrdinalIgnoreCase))
                log.Info("alias request for game '" + game + "' (the client uses 'mygame'); treating it as this game");
        }
    }
}
