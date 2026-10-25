using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Auth;
using Xunit;

namespace McvsOffline.Tests.Auth
{
    /// <summary>The auth endpoints after login: lobby token hand-off, users lookups, alias, compliance, linking, transfer codes.</summary>
    public class AuthEndpointsTests
    {
        /// <summary>
        /// Embedded boot (verify-C2 §1.3-1.4): LocalAnubis opens its own super session (scopes auth auth_admin_ro, HTTPS
        /// forced); the client gets an "encrypted" lobby token; the Controller and the Finder verify tokens; the account
        /// they get is the one users/me gave the client, i.e. the id storage keys the profile by.
        /// </summary>
        [Fact]
        public void LobbyTokenHandOffVerifiesToTheClientsAccount()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            var anubis = new FedSession(server.Federation(protocol: "https"), Settings.DefaultSuperCredential, Settings.DefaultSuperPassword, "default");
            anubis.Open(GameScopes.LocalAnubis);

            string encrypted = client.EncryptedAccessToken("LocalAnubisNonce", "lobby");
            var request = client.Federation.Exchanges.Last().Request;
            Assert.Equal("/encrypt_token", request.Path);
            Assert.StartsWith("nonce=LocalAnubisNonce&access_token=", request.RawQuery); // nonce first, token appended
            Assert.False(string.IsNullOrEmpty(encrypted));
            Assert.DoesNotContain("\"", encrypted);
            Assert.DoesNotContain("\n", encrypted);

            // Controller login (LA/BaseServer.cs Login).
            var login = anubis.Verify(encrypted, "LocalAnubisNonce", "system:Anubis");
            Assert.Equal("access_token=" + Uri.EscapeDataString(encrypted) + "&nonce=LocalAnubisNonce&credential=system%3AAnubis",
                         anubis.Federation.Exchanges.Last().Request.RawQuery);
            Assert.Equal("anonymous:abc123", login["credential"]);
            Assert.Equal(client.FedId, "fed_id:" + login["account"]);
            Assert.Equal(FedFederation.GameClientId, login["client_id"]);

            // Finder ParseUser (LA/FinderHTTPMessageHandler.cs) verifies the raw lobby-scoped token.
            var finder = anubis.Verify(client.GetAccessToken(new[] { "lobby" }));
            Assert.Equal(login["account"], finder["account"]);
            Assert.All(new[] { "credential", "account", "client_id" }, k => Assert.IsType<string>(finder[k]));
        }

        [Fact]
        public void GameServerLobbyLoginUsesTheLobbyAdminToken()
        {
            using var server = new AuthTestServer();
            var ts = server.OpenGameServer();
            var anubis = new FedSession(server.Federation(protocol: "https"), Settings.DefaultSuperCredential, Settings.DefaultSuperPassword, "default");
            anubis.Open(GameScopes.LocalAnubis);
            var login = anubis.Verify(ts.EncryptedAccessToken("LocalAnubisNonce", "lobby_admin"), "LocalAnubisNonce", "system:Anubis");
            Assert.Equal("game:mcvsoffline", login["credential"]);
            Assert.Equal(AccountStore.DeriveAccountId("game:mcvsoffline"), login["account"]);
            Assert.Equal("lobby_admin", login["scope"]);
        }

        [Fact]
        public void VerifyRejectsMissingAndUnknownTokens()
        {
            using var server = new AuthTestServer();
            Assert.Equal(400, server.Get("/verify?nonce=LocalAnubisNonce").StatusCode);
            Assert.Equal(401, server.Get("/verify?access_token=nope").StatusCode);
        }

        [Fact]
        public void AccountsForDeviceIsAlwaysAnEmptyArray()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123", "device-1");
            var ts = server.OpenForUser("anonymous:abc123", "device-1");
            Assert.Empty(ts.AccountsForDevice("device-1"));
            Assert.Empty(ts.AccountsForDevice("device-1", allGames: true));
            Assert.Equal("device_id=device-1&all_games=True&access_token=" + ts.GetAccessToken(new[] { "auth_admin_ro" }),
                         ts.Federation.Exchanges.Last().Request.RawQuery);
            Assert.Equal("[]", server.Get("/users?device_id=device-1").BodyText); // even without a token
        }

        [Fact]
        public void UserLookupsAnswerKnownCredentialsAnd404ForUnknownOnes()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string id = client.FedId.Substring("fed_id:".Length);

            var byCredential = client.AccountInfo("anonymous:abc123");
            Assert.Equal(200, byCredential.StatusCode);
            Assert.Equal(id, MiniJson.ParseObject(byCredential.BodyText)["account"]);
            Assert.Equal("/users/anonymous%3Aabc123", client.Federation.Exchanges.Last().Request.RawPath);

            Assert.Equal(200, server.Get("/users/" + Uri.EscapeDataString(client.FedId)).StatusCode);
            Assert.Equal(200, server.Get("/users/" + client.FedId).StatusCode); // Mono may unescape %3A
            Assert.Equal(404, client.AccountInfo("facebook:12345").StatusCode); // SocialComponent: 404 = no such account
            Assert.Equal(404, server.Get("/users/fed_id%3Anobody").StatusCode);

            // Federation.CheckIfUserExists / Session.CheckIfUserExists: status only.
            Assert.Equal(200, server.Get("/users/anonymous%3Aabc123?client_id=" + Uri.EscapeDataString(FedFederation.GameClientId)).StatusCode);
            Assert.Equal(200, server.Get("/users/anonymous%3Aabc123/games/" + Uri.EscapeDataString(FedFederation.GameClientId)).StatusCode);
            Assert.Equal(404, server.Get("/users/anonymous%3Azzz/games/x").StatusCode);
        }

        [Fact]
        public void AliasIsStableNineDigitsAndCanBeLookedUp()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string alias = client.CreateAlias();
            Assert.Matches("^[1-9][0-9]{8}$", alias);
            Assert.Equal("POST", client.Federation.Exchanges.Last().Request.Method);
            Assert.StartsWith("access_token=", client.Federation.Exchanges.Last().Request.BodyText);
            Assert.Equal(alias, client.CreateAlias());
            Assert.Equal(AliasRegistry.Derive(client.FedId.Substring("fed_id:".Length), 0), alias);

            // The TS creates it on behalf of the player (InitPlayerName runs server-side): same alias.
            var ts = server.OpenForUser("anonymous:abc123");
            Assert.Equal(alias, ts.CreateAlias());
            Assert.Equal(alias, MiniJson.ParseObject(server.Get("/games/mygame/alias?access_token=" + client.GetAccessToken(new[] { "auth" })).BodyText)["alias"]);

            Assert.Equal(client.FedId, client.LookupAlias(alias));
            string other = alias == "123456789" ? "987654321" : "123456789";
            Assert.Equal(404, server.Get("/games/mygame/alias/" + other).StatusCode);
            Assert.Equal(404, server.Get("/games/mygame/alias/not-an-alias").StatusCode);

            server.Restart();
            Assert.Equal(alias, server.OpenClient("abc123").CreateAlias());
        }

        [Fact]
        public void AliasCollisionsMoveToTheNextDerivation()
        {
            using var dir = new TempDir();
            var accounts = new AccountStore(dir.Combine("accounts.json"), null, null);
            var a = accounts.GetOrCreate("anonymous:a");
            var b = accounts.GetOrCreate("anonymous:b");
            // b already owns a's first candidate.
            accounts.Update(b.Id, x => x.Data[AliasRegistry.DataKey] = AliasRegistry.Derive(a.Id, 0));
            var registry = new AliasRegistry(accounts, null);
            string alias = registry.GetOrCreate(a.Id);
            Assert.Equal(AliasRegistry.Derive(a.Id, 1), alias);
            Assert.Equal(a.Id, registry.FindOwner(alias).Id);
            Assert.Null(registry.GetOrCreate("missing"));
        }

        [Fact]
        public void CompliancesAreAllCompliant()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            var ts = server.OpenForUser("anonymous:abc123");
            // GM/AgeGateChecker.cs IsCompliant: AddCompliances(connection.FedId, country, 15), every value an object.
            var rules = ts.AddCompliances(client.FedId, "US", 15);
            Assert.Equal("country=US&age=15&access_token=" + ts.GetAccessToken(new[] { "auth" }), ts.Federation.Exchanges.Last().Request.BodyText);
            Assert.NotEmpty(rules);
            foreach (var rule in rules.Values.Cast<Dictionary<string, object>>())
            {
                Assert.Equal(true, rule["compliant"]);
                Assert.True(TimeFormat.TryParse((string)rule["date"], out _));
                Assert.IsType<Dictionary<string, object>>(rule["reasons"]);
            }
            Assert.Equal(200, server.Get("/users/" + Uri.EscapeDataString(client.FedId) + "/compliance").StatusCode);
        }

        [Fact]
        public void LinkingFollowsIfExists()
        {
            using var server = new AuthTestServer();
            var a = server.OpenClient("aaa");
            var b = server.OpenClient("bbb");
            string idA = a.FedId.Substring(7), idB = b.FedId.Substring(7);

            // A new credential is simply linked.
            Assert.Equal(200, a.LinkCredential("9splay:np1", "pw", "relink").StatusCode);
            Assert.Equal(idA, server.Context.Accounts.Find("9splay:np1").Id);

            // Owned by another account: error -> 409, relink moves only that credential.
            Assert.Equal(409, b.LinkCredential("9splay:np1", "pw", "error").StatusCode);
            Assert.Equal(200, b.LinkCredential("9splay:np1", "pw", "relink").StatusCode);
            Assert.Equal(idB, server.Context.Accounts.Find("9splay:np1").Id);
            Assert.Equal(idA, server.Context.Accounts.Find("anonymous:aaa").Id);

            // relink_all merges every credential of the owning account in.
            var info = MiniJson.ParseObject(b.LinkCredential("anonymous:aaa", null, "relink_all").BodyText);
            Assert.Equal(idB, info["account"]);
            Assert.Equal(idB, server.Context.Accounts.Find("anonymous:aaa").Id);
            Assert.Empty(server.Context.Accounts.FindById(idA).Credentials);

            // The next login of anonymous:aaa now lands on account B.
            Assert.Equal(b.FedId, server.OpenClient("aaa").FedId);

            // Never link super credentials or fed ids.
            Assert.Equal(400, b.LinkCredential("game:mcvsoffline", "mcvsoffline", "relink").StatusCode);
            Assert.Equal(400, b.LinkCredential("fed_id:" + idA, null, "relink").StatusCode);
        }

        [Fact]
        public void TransferCodeMovesTheAnonymousCredentialToTheOldAccount()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));
            using var server = new AuthTestServer(clock: clock);
            var oldDevice = server.OpenClient("old", "device-old");
            var created = oldDevice.GetTransferCode();
            string code = (string)created["value"];
            Assert.Matches("^[A-Z2-9]{10}$", code);
            Assert.True(TimeFormat.TryParse((string)created["expiration"], out var expires));
            Assert.Equal(clock.UtcNow.AddHours(168), expires);

            // New device: EN/AuthenticationComponent.cs DoLinkAnonymousSession.
            var newDevice = server.OpenClient("new", "device-new");
            var redeemed = newDevice.AuthorizeTransferCode(code.ToLowerInvariant());
            Assert.Equal(200, redeemed.StatusCode);
            var redeemRequest = newDevice.Federation.Exchanges.Last().Request;
            Assert.Equal("GET", redeemRequest.Method);
            Assert.Equal("grant_type=transfer_code&scope=auth&client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay&transfer_code="
                         + code.ToLowerInvariant(), redeemRequest.RawQuery);
            string transferToken = (string)MiniJson.ParseObject(redeemed.BodyText)["access_token"];

            var link = new FedRequest("auth", "users/me/credentials", "POST");
            link.Parameters["access_token"] = transferToken;
            link.Parameters["if_exists"] = "relink_all";
            link.Parameters["resolve_conflicts"] = false.ToString();
            link.Parameters["credential"] = "anonymous:new";
            link.Parameters["password"] = "anon-pass";
            Assert.Equal(200, newDevice.Send(link).StatusCode);
            Assert.Equal(oldDevice.FedId, server.OpenClient("new", "device-new").FedId);

            // Single use, and expiring.
            Assert.Equal(404, newDevice.AuthorizeTransferCode(code).StatusCode);
            string second = (string)oldDevice.GetTransferCode()["value"];
            clock.Advance(TimeSpan.FromHours(169));
            Assert.Equal(404, newDevice.AuthorizeTransferCode(second).StatusCode);
        }

        [Fact]
        public void AuthenticatorFindsTokensWhereverModulesGetThem()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string token = client.GetAccessToken(GameScopes.Client);
            var auth = server.Context.Features.Get<IAuthenticator>();

            var get = HttpRequest.Create("GET", "/x?access_token=" + token);
            var post = HttpRequest.Create("POST", "/x", "a=1&access_token=" + token);
            var delete = HttpRequest.Create("DELETE", "/x?access_token=" + token, "msgids=1&access_token=" + token);
            var json = HttpRequest.Create("POST", "/x", "{\"access_token\":\"" + token + "\"}", "application/json");
            var bearer = HttpRequest.Create("GET", "/x");
            bearer.Headers.Set("Authorization", "Bearer " + token);
            foreach (var request in new[] { get, post, delete, json, bearer })
            {
                AuthIdentity who = auth.Authenticate(request);
                Assert.NotNull(who);
                Assert.Equal(client.FedId, who.FedId);
                Assert.False(who.IsGameServer);
            }
            Assert.Null(auth.Authenticate(HttpRequest.Create("GET", "/x")));

            var me = auth.Authenticate(get);
            Assert.Equal(me.AccountId, auth.ResolveAccount(me, "me").Id);
            Assert.Equal(me.AccountId, auth.ResolveAccount(me, client.FedId).Id);
            Assert.Equal(me.AccountId, auth.ResolveAccount(null, "anonymous:abc123").Id);
            Assert.Null(auth.ResolveAccount(me, "fed_id:nobody"));
            Assert.Null(auth.ResolveAccount(null, "me"));

            var gs = server.OpenGameServer();
            Assert.True(auth.Resolve(gs.GetAccessToken(GameScopes.Server)).IsGameServer);
        }
    }
}
