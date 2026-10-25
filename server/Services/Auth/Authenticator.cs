using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>Who a request's access token stands for.</summary>
    public sealed class AuthIdentity
    {
        internal AuthIdentity(TokenInfo token, bool isGameServer)
        {
            Token = token;
            IsGameServer = isGameServer;
        }

        /// <summary>The token record (scopes, client id, device id, actor...).</summary>
        public TokenInfo Token { get; private set; }

        /// <summary>The subject's account id ("account" in users/me; for an on-behalf token: the player's).</summary>
        public string AccountId { get { return Token.AccountId; } }

        /// <summary>"fed_id:" + AccountId.</summary>
        public string FedId { get { return Token.FedId; } }

        /// <summary>The subject credential the token was granted for, "type:username".</summary>
        public string Credential { get { return Token.Credential; } }

        /// <summary>The super credential that obtained the token on behalf of the subject, else null.</summary>
        public string Actor { get { return Token.Actor; } }

        public bool IsOnBehalf { get { return Token.IsOnBehalf; } }

        /// <summary>
        /// True when a game server holds the token: the subject is a "game:" credential (the TS/GS own session, also
        /// used read-only for other players' data, OP/SessionForUser.cs GetReadOnlySessionForUser) or a game
        /// credential obtained it on behalf of a player (for_username grant). Such callers may address other accounts.
        /// </summary>
        public bool IsGameServer { get; private set; }

        public override string ToString()
        {
            return IsOnBehalf ? Actor + " for " + Credential + " (" + AccountId + ")" : Credential + " (" + AccountId + ")";
        }
    }

    /// <summary>
    /// Token checks for every module (registered in <c>context.Features</c> by <see cref="AuthService"/>; define-the-
    /// interface-in-the-provider's-folder rule of docs/MODULES.md). Usage:
    /// <code>
    /// var auth = context.Features.Get&lt;IAuthenticator&gt;();
    /// AuthIdentity who = auth.Authenticate(request);
    /// if (who == null) return auth.Unauthorized(request, "storage needs a token");
    /// </code>
    /// A bad token must get 401, never 403: on 401 the client drops its tokens, re-authorizes once and retries;
    /// 403 means "banned" on authorize and "profile gone" on storage (report 02 §1.6, verify-C3 §3.3).
    /// </summary>
    public interface IAuthenticator
    {
        /// <summary>
        /// The request's access token: form body, then query (GET carries it in the query, POST in the form, DELETE in
        /// both; report 02 §1.4), then a JSON body's "access_token", then "Authorization: Bearer". Null if absent.
        /// </summary>
        string GetAccessToken(HttpRequest request);

        /// <summary>The identity behind the request's token; null if there is none, it is unknown, or its account is gone.</summary>
        AuthIdentity Authenticate(HttpRequest request);

        /// <summary>The identity behind <paramref name="token"/>, or null.</summary>
        AuthIdentity Resolve(string token);

        /// <summary>
        /// The account a credential addresses: "me" (or empty) is the token's subject; "fed_id:&lt;id&gt;" and
        /// "type:username" are looked up. Null when the account does not exist or the credential does not parse.
        /// </summary>
        Account ResolveAccount(AuthIdentity identity, string credential);

        /// <summary>A 401 with a small JSON body; logs the reason and the request line.</summary>
        HttpResponse Unauthorized(HttpRequest request, string reason);
    }

    /// <summary>Default <see cref="IAuthenticator"/> over the core AccountStore and TokenService.</summary>
    public sealed class Authenticator : IAuthenticator
    {
        private readonly ServerContext context;
        private readonly Log log;

        public Authenticator(ServerContext context, Log log)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
            this.log = log ?? Log.Null;
        }

        public string GetAccessToken(HttpRequest request)
        {
            if (request == null) return null;
            string token = request.Param("access_token");
            if (!string.IsNullOrEmpty(token)) return token;

            var json = request.Json as Dictionary<string, object>;
            token = JsonValue.GetString(json, "access_token");
            if (!string.IsNullOrEmpty(token)) return token;

            string authorization = request.Headers.Get("Authorization");
            const string bearer = "Bearer ";
            if (authorization != null && authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            {
                token = authorization.Substring(bearer.Length).Trim();
                if (token.Length > 0) return token;
            }
            return null;
        }

        public AuthIdentity Authenticate(HttpRequest request)
        {
            return Resolve(GetAccessToken(request));
        }

        public AuthIdentity Resolve(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            TokenInfo info = context.Tokens.Resolve(token);
            if (info == null)
            {
                if (!context.Tokens.IsLocal(token))
                {
                    // Self-describing tokens: a foreign issuer means another device's server issued it. Forwarding the
                    // check to that server is roadmap M3 (ARCHITECTURE §3, verify-C6 §3); until then it is unknown.
                    log.Debug("token from issuer '" + TokenService.GetIssuer(token) + "' is not ours (" + context.Tokens.Issuer + ")");
                }
                return null;
            }
            if (context.Accounts.FindById(info.AccountId) == null)
            {
                // accounts.json lost or edited: answer 401 so the client re-authorizes, which re-creates the account
                // under the same derived id (AccountStore.DeriveAccountId).
                log.Warn("token " + token + " refers to missing account " + info.AccountId);
                return null;
            }
            return new AuthIdentity(info, IsGameCredential(info.Credential) || info.IsOnBehalf);
        }

        public Account ResolveAccount(AuthIdentity identity, string credential)
        {
            Credential parsed;
            if (string.IsNullOrEmpty(credential) || !Credential.TryParse(credential, out parsed))
            {
                return string.IsNullOrEmpty(credential) && identity != null ? context.Accounts.FindById(identity.AccountId) : null;
            }
            if (parsed.IsMe) return identity == null ? null : context.Accounts.FindById(identity.AccountId);
            if (!parsed.IsValid) return null;
            return context.Accounts.Find(parsed.ToString());
        }

        public HttpResponse Unauthorized(HttpRequest request, string reason)
        {
            log.Warn("401 for " + request.Method + " " + request.Path + ": " + reason);
            return AuthResponses.Error(HttpStatus.Unauthorized, "unauthorized", reason);
        }

        /// <summary>True for "game:&lt;name&gt;" credentials, the game-server super credentials (report 02 §3.4).</summary>
        public static bool IsGameCredential(string credential)
        {
            Credential parsed;
            return Credential.TryParse(credential, out parsed) && parsed.IsValid
                   && parsed.Type == Credential.Game;
        }
    }
}
