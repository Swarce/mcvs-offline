using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// The settings page end to end over a real socket (design §10.9): servers composed by ServiceRegistry alone (the admin
    /// modules included), listening on a free loopback port, spoken to with raw HTTP/1.1 the way the game's WebView and the
    /// game itself send it.
    /// </summary>
    public class AdminEndToEndTests
    {
        private const string ClientId = "2620:66102:1.17.32:android:googleplay";

        /// <summary>A started server (every module of ServiceRegistry) on its own data directory, with a cookie jar for the page.</summary>
        private sealed class Phone : IDisposable
        {
            public Phone(string dataDir)
            {
                DataDir = dataDir;
                Server = McvsServer.Create(new ServerOptions { DataDir = dataDir, Port = 0, ConsoleLog = false, FileLog = false });
                Server.Start();
            }

            public string DataDir { get; }

            public McvsServer Server { get; }

            public int Port => Server.Port;

            public string Host => "127.0.0.1:" + Port;

            public string Origin => "http://" + Host;

            /// <summary>The page's session cookie (mcvs_s), taken from Set-Cookie.</summary>
            public string Cookie { get; set; }

            /// <summary>One request on its own connection. <paramref name="host"/> null: this server's 127.0.0.1:&lt;port&gt;; "" sends none.</summary>
            public RawResponse Send(string method, string target, string body = null, string contentType = null, string host = null,
                                    string extraHeaders = "")
            {
                var head = new StringBuilder(method + " " + target + " HTTP/1.1\r\n");
                string h = host ?? Host;
                if (h.Length > 0) head.Append("Host: ").Append(h).Append("\r\n");
                head.Append(extraHeaders);
                if (Cookie != null) head.Append("Cookie: mcvs_s=").Append(Cookie).Append("\r\n");
                byte[] bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
                if (body != null) head.Append("Content-Type: ").Append(contentType).Append("\r\nContent-Length: ").Append(bytes.Length).Append("\r\n");
                head.Append("Connection: close\r\n\r\n");
                using (var client = new RawHttpClient(Port))
                {
                    byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
                    client.Send(headBytes.Concat(bytes).ToArray());
                    RawResponse response = client.ReadResponse();
                    string setCookie = response.Header("Set-Cookie");
                    if (setCookie != null && setCookie.StartsWith("mcvs_s=", StringComparison.Ordinal))
                    {
                        string value = setCookie.Substring(7, setCookie.IndexOf(';') - 7);
                        Cookie = value.Length == 0 ? null : value;
                    }
                    return response;
                }
            }

            /// <summary>What the page's XHR sends: JSON, X-MCVS: 1, the same-origin Origin, the cookie.</summary>
            public RawResponse Api(string method, string path, object body = null)
            {
                if (method == "GET") return Send("GET", "/_mcvs/api/v1/" + path);
                return Send(method, "/_mcvs/api/v1/" + path, MiniJson.Serialize(body ?? new Dictionary<string, object>()), "application/json; charset=utf-8",
                            null, "X-MCVS: 1\r\nOrigin: " + Origin + "\r\n");
            }

            /// <summary>A fresh one-time ticket, as the host's page opener mints one.</summary>
            public string MintTicket()
            {
                string url = Server.Context.Features.Get<IAdminPortal>().MintPageUrl(AdminTriggers.Test, null);
                Assert.StartsWith("http://127.0.0.1:" + Port + "/_mcvs/ui/#t=", url);
                return url.Substring(url.IndexOf("#t=", StringComparison.Ordinal) + 3);
            }

            /// <summary>The page's boot: the ticket for a session cookie.</summary>
            public void OpenSession()
            {
                RawResponse session = Api("POST", "session", new Dictionary<string, object> { ["ticket"] = MintTicket() });
                Assert.True(session.Status == 200, session.BodyText);
                Assert.NotNull(Cookie);
            }

            /// <summary>The game's login (auth/authorize with its client id): the access token.</summary>
            public string Authorize(string user)
            {
                string form = "username=" + user + "&credential_type=anonymous&password=pw&device_id=dev-" + user
                              + "&client_id=" + UrlEncoding.Encode(ClientId) + "&scope=auth%20storage%20config";
                RawResponse authorize = Send("POST", "/auth/authorize", form, "application/x-www-form-urlencoded; charset=utf-8", null,
                                             "User-Agent: (" + ClientId + "; mcvs)\r\n");
                Assert.True(authorize.Status == 200, authorize.BodyText);
                return (string)MiniJson.ParseObject(authorize.BodyText)["access_token"];
            }

            /// <summary>The account the token opens (users/me).</summary>
            public string AccountOf(string token)
            {
                RawResponse me = Send("GET", "/auth/users/me?access_token=" + token);
                Assert.True(me.Status == 200, me.BodyText);
                return (string)MiniJson.ParseObject(me.BodyText)["account"];
            }

            /// <summary>The TS's dump POST: a save with source http.</summary>
            public void SaveGame(string token, string playerName)
            {
                string dump = GameData.GameProfileDump.Replace("agent-123456", playerName);
                string form = "visibility=restricted&object=" + UrlEncoding.Encode(dump) + "&access_token=" + token;
                RawResponse saved = Send("POST", "/profiles/me/myprofile/_mcvs.gameProfile", form, "application/x-www-form-urlencoded; charset=utf-8");
                Assert.True(saved.Status == 200, saved.BodyText);
            }

            public void Dispose() => Server.Dispose();
        }

        private static Dictionary<string, object> Json(RawResponse response) => MiniJson.ParseObject(response.BodyText);

        private static object At(RawResponse response, string path) => JsonPath.Get(Json(response), path);

        [Fact]
        public void TicketCookieAndASettingsWriteThatTheCrmDocumentReflects()
        {
            using var dir = new TempDir();
            using var phone = new Phone(dir.Combine("data"));

            RawResponse page = phone.Send("GET", "/_mcvs/ui/");
            Assert.Equal(200, page.Status);
            Assert.StartsWith("text/html", page.Header("Content-Type"));
            Assert.Contains("default-src 'none'", page.Header("Content-Security-Policy"));

            // No session yet; the one-time ticket buys one, exactly once.
            Assert.Equal(401, phone.Api("GET", "status").Status);
            string ticket = phone.MintTicket();
            RawResponse session = phone.Api("POST", "session", new Dictionary<string, object> { ["ticket"] = ticket });
            Assert.Equal(200, session.Status);
            Assert.Matches("^mcvs_s=[0-9a-f]{64}; Path=/_mcvs/; HttpOnly; SameSite=Strict$", session.Header("Set-Cookie"));
            string cookie = phone.Cookie;
            phone.Cookie = null;
            Assert.Equal(401, phone.Api("POST", "session", new Dictionary<string, object> { ["ticket"] = ticket }).Status);
            phone.Cookie = cookie;
            RawResponse status = phone.Api("GET", "status");
            Assert.Equal(200, status.Status);
            Assert.Equal((long)phone.Port, At(status, "server.port"));

            // The inbox item is in the CRM document players get, pointing at the page marker.
            string crm = "/configs/users/me";
            var item = (Dictionary<string, object>)Assert.Single((List<object>)Json(phone.Send("GET", crm))["newsfeeds"]);
            Assert.Equal("http://127.0.0.1:" + phone.Port + "/_mcvs/ui/#news", item["redirect"]);

            // The page turns it off: settings.json on disk, and the next CRM document has "newsfeeds": [].
            RawResponse settings = phone.Api("GET", "settings");
            Assert.Equal(200, settings.Status);
            RawResponse saved = phone.Api("POST", "settings", new Dictionary<string, object>
            {
                ["changes"] = new Dictionary<string, object> { ["crm.newsfeedLink"] = false, ["economy.versusCoinsMultiplier"] = 2.0 },
                ["fingerprint"] = At(settings, "fingerprint"),
            });
            Assert.True(saved.Status == 200, saved.BodyText);
            var file = MiniJson.ParseObject(File.ReadAllText(Path.Combine(phone.DataDir, Settings.FileName)));
            Assert.Equal(false, JsonPath.Get(file, "crm.newsfeedLink"));
            Assert.Empty((List<object>)Json(phone.Send("GET", crm))["newsfeeds"]);

            // Closing the session expires the cookie.
            Assert.Equal(200, phone.Api("DELETE", "session").Status);
            Assert.Null(phone.Cookie);
            phone.Cookie = cookie;
            Assert.Equal(401, phone.Api("GET", "status").Status);
        }

        [Fact]
        public void TheLegacyMcvsRoutesRejectAForeignHost()
        {
            using var dir = new TempDir();
            using var phone = new Phone(dir.Combine("data"));
            foreach (string path in new[] { "/_mcvs/status", "/_mcvs/routes", "/_mcvs/crm", "/_mcvs/liveops", "/_mcvs/unlocks", "/_mcvs/ui/", "/_mcvs/api/v1/ping" })
            {
                Assert.Equal(200, phone.Send("GET", path).Status);
                Assert.Equal(200, phone.Send("GET", path, host: "localhost:" + phone.Port).Status);
                // DNS rebinding: a page on evil.com resolved to 127.0.0.1 sends its own Host.
                Assert.Equal(404, phone.Send("GET", path, host: "evil.com:" + phone.Port).Status);
                Assert.Equal(404, phone.Send("GET", path, host: "evil.com").Status);
                Assert.Equal(404, phone.Send("GET", path, host: "127.0.0.1:" + (phone.Port == 65535 ? 1 : phone.Port + 1)).Status);
            }
            // And the writes need X-MCVS: 1 on top of the Host.
            Assert.Equal(403, phone.Send("POST", "/_mcvs/crm/reload", "{}", "application/json").Status);
            Assert.Equal(404, phone.Send("POST", "/_mcvs/crm/reload", "{}", "application/json", "evil.com:" + phone.Port, "X-MCVS: 1\r\n").Status);
            Assert.Equal(200, phone.Send("POST", "/_mcvs/crm/reload", "{}", "application/json", null, "X-MCVS: 1\r\n").Status);
        }

        [Fact]
        public void AFederationRouteRejectsAForeignHostAndAcceptsTheLoopbackOne()
        {
            using var dir = new TempDir();
            using var phone = new Phone(dir.Combine("data"));
            const string config = "/config/" + ClientId + "/datacenters";
            Assert.Equal(200, phone.Send("GET", config).Status);                                  // 127.0.0.1:<port>, as the game sends it
            Assert.Equal(200, phone.Send("GET", "/configs/users/me").Status);
            Assert.Equal(200, phone.Send("GET", config, host: "localhost:1234").Status);          // any port on a loopback name
            Assert.Equal(200, phone.Send("GET", config, host: "").Status);                        // no Host (HTTP/1.0 style)
            Assert.Equal(404, phone.Send("GET", config, host: "evil.com").Status);
            Assert.Equal(404, phone.Send("GET", "/configs/users/me", host: "evil.com:" + phone.Port).Status);
        }

        [Fact]
        public void AnUnauthenticatedTwoKiBPostIsRefusedBeforeItsBodyIsSent()
        {
            using var dir = new TempDir();
            using var phone = new Phone(dir.Combine("data"));
            string head = "POST /_mcvs/api/v1/settings HTTP/1.1\r\nHost: " + phone.Host + "\r\nOrigin: " + phone.Origin + "\r\nX-MCVS: 1\r\n"
                          + "Content-Type: application/json\r\nExpect: 100-continue\r\nContent-Length: 2048\r\n";
            using (var client = new RawHttpClient(phone.Port))
            {
                // Headers only, waiting for "100 Continue" as Mono's HttpClient does: the first answer is the 413.
                client.Send(head + "\r\n");
                RawResponse response = client.ReadResponse();
                Assert.Equal(413, response.Status);
                Assert.Equal("too_large", Json(response)["error"]);
                Assert.Equal(1024L, At(response, "details.limit"));
                Assert.True(client.IsClosedByServer());
            }

            // With a session the route's own limit (64 KiB) applies: the body is read and reaches the settings route.
            phone.OpenSession();
            string body = "{\"changes\":{},\"pad\":\"" + new string('x', 2000) + "\"}";
            using (var client = new RawHttpClient(phone.Port))
            {
                client.Send(head.Replace("Content-Length: 2048", "Content-Length: " + body.Length) + "Cookie: mcvs_s=" + phone.Cookie + "\r\n\r\n");
                RawResponse first = client.ReadResponse();
                Assert.Equal(100, first.Status);
                client.Send(body);
                RawResponse answer = client.ReadResponse();
                Assert.NotEqual(413, answer.Status);
                Assert.NotEqual(401, answer.Status);
            }
        }

        /// <summary>
        /// A move to a new phone: export on phone A, paste and stage on phone B, then B's next start applies the import before
        /// the port opens. The game's login on B (its credential C2) then opens the imported account X, and the TS's first
        /// save of X ends the probation.
        /// </summary>
        [Fact]
        public void AStagedImportIsAppliedAtTheNextStartAndTheGamesSaveEndsItsProbation()
        {
            using var dir = new TempDir();
            string x, transfer;
            using (var a = new Phone(dir.Combine("phoneA")))
            {
                string tokenA = a.Authorize("c1oldphone");
                x = a.AccountOf(tokenA);
                a.SaveGame(tokenA, "oldphone-agent");
                a.OpenSession();
                RawResponse export = a.Api("POST", "saves/" + x + "/export", new Dictionary<string, object>
                {
                    ["target"] = "text", ["format"] = "transfer", ["partChars"] = 3900L,
                });
                Assert.True(export.Status == 200, export.BodyText);
                transfer = string.Join("\n\nchat noise\n\n", ((List<object>)At(export, "transfer.texts")).Cast<string>());
            }

            string dataB = dir.Combine("phoneB");
            string y;
            using (var b = new Phone(dataB))
            {
                string tokenB = b.Authorize("c2newphone");
                y = b.AccountOf(tokenB);
                Assert.NotEqual(x, y);
                b.SaveGame(tokenB, "newphone-agent");
                b.OpenSession();
                Assert.Equal("session", At(b.Api("GET", "saves"), "current.source"));
                RawResponse pasted = b.Api("POST", "saves/import/paste", new Dictionary<string, object> { ["text"] = transfer });
                Assert.True(pasted.Status == 200, pasted.BodyText);
                Assert.Equal(true, At(pasted, "complete"));
                Assert.Equal("switch", At(pasted, "plan"));
                Assert.Equal(true, At(pasted, "canStage"));
                RawResponse staged = b.Api("POST", "saves/import/stage", new Dictionary<string, object>
                {
                    ["uploadId"] = At(pasted, "uploadId"), ["confirm"] = "switch",
                });
                Assert.True(staged.Status == 200, staged.BodyText);
                Assert.Equal(x, At(staged, "staged.target"));
                Assert.Equal(y, b.AccountOf(tokenB));   // nothing changes before the restart
            }

            string probationFile = Path.Combine(dataB, "admin", "staged", Probation.FileName);
            using (var b = new Phone(dataB))
            {
                Assert.False(File.Exists(Path.Combine(dataB, "admin", "staged", "pending.json")));
                Assert.True(File.Exists(probationFile));

                // The game logs in with its own credential and lands on the imported account.
                string token = b.Authorize("c2newphone");
                Assert.Equal(x, b.AccountOf(token));
                RawResponse profile = b.Send("GET", "/profiles/me/myprofile/_mcvs.gameProfile?access_token=" + token);
                Assert.True(profile.Status == 200, profile.BodyText);
                Assert.Contains("oldphone-agent", profile.BodyText);

                // The TS's first save of X (source http; the game posts a dump only when it changed) confirms the operation.
                b.SaveGame(token, "oldphone-agent-played");
                Assert.False(File.Exists(probationFile));
                b.OpenSession();
                RawResponse staged = b.Api("GET", "saves/staged");
                Assert.Equal(200, staged.Status);
                Assert.Null(At(staged, "probation"));
                Assert.Null(At(staged, "pending"));
                var last = (Dictionary<string, object>)((List<object>)At(staged, "history"))[0];
                Assert.Equal("import", last["kind"]);
                Assert.Equal("confirmed", last["result"]);
            }
        }
    }
}
