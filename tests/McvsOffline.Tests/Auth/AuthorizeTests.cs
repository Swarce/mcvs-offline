using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Auth;
using Xunit;

namespace McvsOffline.Tests.Auth
{
    /// <summary>authorize and users/me, replayed the way Session/SessionForUser build them.</summary>
    public class AuthorizeTests
    {
        [Fact]
        public void AnonymousSessionAutoCreatesTheAccount()
        {
            using var server = new AuthTestServer();
            var federation = server.Federation();
            var session = server.OpenClient("abc123", "device-1", federation);

            // The exact authorize body Session.GetAuthorizeRequest builds (field order included).
            var authorize = federation.Exchanges.Single(e => e.Request.Path == "/authorize").Request;
            Assert.Equal("POST", authorize.Method);
            Assert.Equal("application/x-www-form-urlencoded; charset=utf-8", authorize.ContentType);
            Assert.Equal("username=abc123&credential_type=anonymous&password=anon-pass&device_id=device-1"
                         + "&client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay&scope=auth%20lobby%20config"
                         + "&device_firmware=10&device_country=US&device_carrier=T-Mobile&device_model=Pixel%203"
                         + "&device_resolution=1080x2160&device_language=en", authorize.BodyText);

            var body = MiniJson.ParseObject(federation.Exchanges.Single(e => e.Request.Path == "/authorize").Response.BodyText);
            Assert.Equal("auth lobby config", body["scope"]);
            Assert.Equal("bearer", body["token_type"]);
            Assert.IsType<long>(body["expires_in"]);
            string token = (string)body["access_token"];
            Assert.StartsWith(server.Context.InstanceId + ".", token);
            Assert.DoesNotContain("~", token); // a '~' would make the client derive a "silo" from the token

            // users/me: token in the query (GET), account without ':', every credential parseable by the client.
            var me = federation.Exchanges.Single(e => e.Request.Path == "/users/me");
            Assert.Equal("access_token=" + token, me.Request.RawQuery);
            var info = MiniJson.ParseObject(me.Response.BodyText);
            string account = (string)info["account"];
            Assert.Equal(AccountStore.DeriveAccountId("anonymous:abc123"), account);
            Assert.DoesNotContain(":", account);
            Assert.Equal(new List<object> { "anonymous:abc123" }, info["credentials"]);
            Assert.All((List<object>)info["credentials"], c => Assert.Equal(2, ((string)c).Split(':').Length));
            Assert.Equal("fed_id:" + account, session.FedId);
            Assert.Contains("device-1", server.Context.Accounts.FindById(account).DeviceIds);
        }

        [Fact]
        public void ScopesAreEchoedInRequestOrderAndEachScopeSetGetsItsOwnStableToken()
        {
            using var server = new AuthTestServer();
            string Authorize(string scope) =>
                server.Post("/authorize", "username=p&credential_type=anonymous&password=x&device_id=d&client_id=c&scope=" + Uri.EscapeDataString(scope)).BodyText;

            var a = MiniJson.ParseObject(Authorize("config storage_ro"));
            Assert.Equal("config storage_ro", a["scope"]);
            var b = MiniJson.ParseObject(Authorize("storage_ro config"));
            Assert.Equal("storage_ro config", b["scope"]);
            Assert.Equal(a["access_token"], b["access_token"]); // same scope set: same token
            var c = MiniJson.ParseObject(Authorize("lobby_game_admin"));
            Assert.NotEqual(a["access_token"], c["access_token"]);

            // GamePortalApi builds a request with scope "": authorize must still answer a string scope.
            var empty = MiniJson.ParseObject(Authorize(""));
            Assert.Equal("", empty["scope"]);
            Assert.False(string.IsNullOrEmpty((string)empty["access_token"]));
        }

        [Fact]
        public void GameServerSessionGetsItsOwnAccount()
        {
            using var server = new AuthTestServer();
            var federation = server.Federation();
            var gs = server.OpenGameServer("device-1", federation);

            var authorize = federation.Exchanges.Single(e => e.Request.Path == "/authorize").Request;
            Assert.StartsWith("username=mcvsoffline&credential_type=game&password=mcvsoffline&device_id=device-1&client_id=", authorize.BodyText);
            Assert.DoesNotContain("device_firmware", authorize.BodyText);
            var echoed = (string)MiniJson.ParseObject(federation.Exchanges.Single(e => e.Request.Path == "/authorize").Response.BodyText)["scope"];
            Assert.Equal("auth", echoed.Split(' ')[0]);
            Assert.Equal(21, echoed.Split(' ').Length); // auth ∪ InitialScopesServer, no duplicate

            var me = MiniJson.ParseObject(federation.Exchanges.Single(e => e.Request.Path == "/users/me").Response.BodyText);
            Assert.Equal(AccountStore.DeriveAccountId("game:mcvsoffline"), me["account"]);
            Assert.Equal(new List<object> { "game:mcvsoffline" }, me["credentials"]);
        }

        /// <summary>
        /// The TS handshake opens a session for the player with the super credential (verify-C2 §1.5): it must land
        /// on the account the client's own session got, and users/me must list the player's credentials.
        /// </summary>
        [Fact]
        public void OnBehalfSessionResolvesToThePlayersOwnAccount()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            var federation = server.Federation();
            var ts = server.OpenForUser("anonymous:abc123", "device-1", federation);

            var authorize = federation.Exchanges.Single(e => e.Request.Path == "/authorize").Request;
            Assert.StartsWith("username=mcvsoffline&credential_type=game&password=mcvsoffline&device_id=device-1&client_id=", authorize.BodyText);
            Assert.EndsWith("&for_username=abc123&for_credential_type=anonymous", authorize.BodyText);
            Assert.Equal(client.FedId, ts.FedId);
            var me = MiniJson.ParseObject(federation.Exchanges.Single(e => e.Request.Path == "/users/me").Response.BodyText);
            Assert.Equal(new List<object> { "anonymous:abc123" }, me["credentials"]);

            // The on-behalf token is the player's, obtained by the game server.
            var who = server.Context.Features.Get<IAuthenticator>().Resolve(ts.GetAccessToken(new[] { "auth" }));
            Assert.True(who.IsOnBehalf);
            Assert.True(who.IsGameServer);
            Assert.Equal("game:mcvsoffline", who.Actor);
            Assert.Equal("anonymous:abc123", who.Credential);
        }

        [Fact]
        public void OnBehalfSessionForAPlayerNeverSeenCreatesTheSameDerivedAccount()
        {
            using var server = new AuthTestServer();
            // A LAN guest whose own client authorized against its own server: same derived id on every server.
            var ts = server.OpenForUser("anonymous:guest7", null);
            Assert.Equal("fed_id:" + AccountStore.DeriveAccountId("anonymous:guest7"), ts.FedId);
            Assert.DoesNotContain("Unknown", server.Context.Accounts.FindById(AccountStore.DeriveAccountId("anonymous:guest7")).DeviceIds);
        }

        [Fact]
        public void OnBehalfByFedIdAddressesTheAccountDirectly()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string id = client.FedId.Substring("fed_id:".Length);
            // GM/GameProfileSection.cs CreateProfile opens SessionForUser(new Credential(fed_id, account)); fed_id sessions skip users/me.
            var ts = server.OpenForUser("fed_id:" + id);
            Assert.Equal(client.FedId, ts.FedId);
            var who = server.Context.Features.Get<IAuthenticator>().Resolve(ts.GetAccessToken(new[] { "auth" }));
            Assert.Equal(id, who.AccountId);
            Assert.Equal("fed_id:" + id, who.Credential);
            // Only the player's account exists: on-behalf grants create the subject, never an account for the fed_id
            // credential or for the super credential itself.
            Assert.Equal(1, server.Context.Accounts.Count);
        }

        [Fact]
        public void OnlyGameCredentialsMayActOnBehalf()
        {
            using var server = new AuthTestServer();
            var response = server.Post("/authorize", "username=p&credential_type=anonymous&password=x&device_id=d&client_id=c&scope=auth&for_username=victim&for_credential_type=anonymous");
            Assert.Equal(401, response.StatusCode);
            Assert.Null(server.Context.Accounts.Find("anonymous:victim"));
        }

        [Fact]
        public void SuperPasswordMismatchIsLoggedAndOptionallyRefused()
        {
            using var server = new AuthTestServer();
            const string form = "username=mcvsoffline&credential_type=game&password=wrong&device_id=d&client_id=c&scope=auth";
            Assert.Equal(200, server.Post("/authorize", form).StatusCode);
            Assert.True(server.Logs.Contains("wrong password for game:mcvsoffline"));

            server.Context.Settings.Override("auth.requireSuperPassword", true);
            Assert.Equal(401, server.Post("/authorize", form).StatusCode);
            Assert.Equal(200, server.Post("/authorize", form.Replace("password=wrong", "password=mcvsoffline")).StatusCode);
            Assert.Equal(401, server.Post("/authorize", "username=other&credential_type=game&password=mcvsoffline&scope=auth").StatusCode);
        }

        [Theory]
        [InlineData("")]                                                         // nothing
        [InlineData("username=p&scope=auth")]                                    // no credential_type
        [InlineData("credential_type=anonymous&scope=auth")]                     // no username
        [InlineData("username=x&credential_type=me&scope=auth")]
        [InlineData("username=x&credential_type=none&scope=auth")]
        [InlineData("username=..%2Fescape&credential_type=fed_id&scope=auth")]  // fed_id ids become storage keys
        [InlineData("username=mcvsoffline&credential_type=game&password=mcvsoffline&for_username=x&for_credential_type=me")]
        [InlineData("username=mcvsoffline&credential_type=game&password=mcvsoffline&for_username=mcvsoffline&for_credential_type=game")]
        [InlineData("grant_type=refresh_token&refresh_token=x")]
        public void MalformedAuthorizeRequestsAreRejectedWith400(string form)
        {
            using var server = new AuthTestServer();
            Assert.Equal(400, server.Post("/authorize", form).StatusCode);
        }

        [Fact]
        public void TokensAndAccountsSurviveARestart()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string token = client.GetAccessToken(GameScopes.Client);
            server.Restart();

            var me = server.Get("/users/me?access_token=" + token);
            Assert.Equal(200, me.StatusCode);
            Assert.Equal(client.FedId, "fed_id:" + MiniJson.ParseObject(me.BodyText)["account"]);
            // Re-authorizing the same grant gives the same token back (tokens never expire, report 02 §3.3).
            var again = server.OpenClient("abc123");
            Assert.Equal(token, again.GetAccessToken(GameScopes.Client));
        }

        /// <summary>
        /// tokens.json lost: the client still holds its token, gets one 401, drops every token, re-authorizes once
        /// and retries (OP/Federation.cs Inspect). The account is the same because ids are derived from the credential.
        /// </summary>
        [Fact]
        public void LostTokensRecoverThroughOne401AndReauthorize()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string fedId = client.FedId;
            Assert.Equal(1, client.AuthorizeCount);

            server.Server.Dispose();
            File.Delete(Path.Combine(server.DataDir, TokenService.FileName));
            File.Delete(Path.Combine(server.DataDir, AccountStore.FileName));
            server.Restart();

            var response = client.AccountInfo();
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(2, client.AuthorizeCount);
            Assert.Equal(fedId, "fed_id:" + MiniJson.ParseObject(response.BodyText)["account"]);
        }

        [Fact]
        public void BadTokensGet401NeverForbidden()
        {
            using var server = new AuthTestServer();
            string unknown = server.Context.InstanceId + ".00000000000000000000000000000000";
            string foreign = "mcvs00000000.0123456789abcdef0123456789abcdef";
            var requests = new List<HttpRequest>();
            foreach (string token in new[] { null, "", "garbage", unknown, foreign })
            {
                string q = token == null ? "" : "?access_token=" + Uri.EscapeDataString(token);
                string f = token == null ? "" : "access_token=" + Uri.EscapeDataString(token);
                requests.Add(HttpRequest.Create("GET", "/users/me" + q));
                requests.Add(HttpRequest.Create("GET", "/encrypt_token" + (q.Length > 0 ? q + "&" : "?") + "nonce=n"));
                requests.Add(HttpRequest.Create("POST", "/games/mygame/alias", f));
                requests.Add(HttpRequest.Create("GET", "/games/mygame/alias" + q));
                requests.Add(HttpRequest.Create("POST", "/users/me/credentials", f + "&credential=facebook%3A1&if_exists=relink"));
                requests.Add(HttpRequest.Create("POST", "/users/me/transfer_code", f));
                requests.Add(HttpRequest.Create("POST", "/devices/mydevice", f + "&model=x"));
                if (!string.IsNullOrEmpty(token)) requests.Add(HttpRequest.Create("GET", "/verify" + q + "&nonce=LocalAnubisNonce&credential=system%3AAnubis"));
            }
            foreach (var request in requests)
            {
                var response = server.Handle(request);
                Assert.True(response.StatusCode == 401, request + " -> " + response.StatusCode + " " + response.BodyText);
                Assert.NotEqual("", response.BodyText);
                MiniJson.Parse(response.BodyText);
            }

            // The 401 log line says why, for device testers.
            Assert.True(server.Logs.Contains("(no access_token)"));
            Assert.True(server.Logs.Contains("token " + unknown + " is not known here"));
            Assert.True(server.Logs.Contains("token " + foreign + " was issued by 'mcvs00000000', this server is '" + server.Context.InstanceId + "'"));
        }

        [Fact]
        public void NoAuthEndpointEverAnswers403()
        {
            using var server = new AuthTestServer();
            var client = server.OpenClient("abc123");
            string token = client.GetAccessToken(GameScopes.Client);
            var targets = new[]
            {
                ("POST", "/authorize", "username=mcvsoffline&credential_type=game&password=bad&scope=auth"),
                ("POST", "/authorize", "username=p&credential_type=anonymous&password=&scope=auth&for_username=q"),
                ("GET", "/authorize?grant_type=transfer_code&transfer_code=NOPE&scope=auth", null),
                ("GET", "/users/facebook%3A404", null),
                ("GET", "/users/notacredential", null),
                ("GET", "/users/fed_id%3Aunknown/games/" + Uri.EscapeDataString(FedFederation.GameClientId), null),
                ("POST", "/users/me/credentials", "access_token=" + token + "&credential=game%3Amcvsoffline&if_exists=relink"),
                ("GET", "/games/mygame/alias/123", null),
                ("GET", "/verify", null),
            };
            server.Context.Settings.Override("auth.requireSuperPassword", true);
            foreach (var (method, target, body) in targets)
            {
                var response = server.Handle(HttpRequest.Create(method, target, body));
                Assert.NotEqual(403, response.StatusCode);
                Assert.True(response.StatusCode < 500, target + " -> " + response.StatusCode);
            }
        }
    }
}
