using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>Storage over real sockets with the full service registry, as the game reaches it.</summary>
    public class StorageHttpTests
    {
        private static McvsServer Start(TempDir dir)
        {
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = 0, ConsoleLog = false, FileLog = false };
            var server = McvsServer.Create(options);
            server.Start();
            return server;
        }

        private static string Token(McvsServer server, string user)
        {
            var account = server.Context.Accounts.GetOrCreate("anonymous:" + user);
            return server.Context.Tokens.Issue(account.Id, "anonymous:" + user, McvsConstants.ClientId,
                                               StorageHarness.ServerScopes, null, StorageHarness.SuperCredential).Token;
        }

        /// <summary>A dump of about 1.5 MB, larger than any real one, URL-encoded into the form like the client does.</summary>
        private static string BigDump()
        {
            var sb = new StringBuilder("{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"Version\":57,\"Items\":[");
            for (int i = 0; i < 20000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"Id\":\"item.").Append(i).Append("\",\"Ratio\":0.").Append(i % 97).Append("0,\"Name\":\"é/").Append(i).Append("\"}");
            }
            sb.Append("]}}}");
            return sb.ToString();
        }

        [Fact]
        public void LargeDumpWithExpectContinueAndChunkedBodiesRoundTrips()
        {
            using var dir = new TempDir();
            using var server = Start(dir);
            string token = Token(server, "p");
            string dump = BigDump();
            string form = "visibility=restricted&object=" + UrlEncoding.Encode(dump) + "&access_token=" + token;
            byte[] body = Encoding.UTF8.GetBytes(form);

            using (var client = new RawHttpClient(server.Port))
            {
                // Mono's HttpWebRequest sends Expect: 100-continue on POST (verify-C3 §7.8).
                client.Send("POST /profiles/me/myprofile/_mcvs.gameProfile HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port
                            + "\r\nCache-Control: NO-CACHE\r\nContent-Type: application/x-www-form-urlencoded; charset=utf-8\r\n"
                            + "Expect: 100-continue\r\nContent-Length: " + body.Length + "\r\n\r\n");
                Assert.Equal(100, client.ReadResponse().Status);
                client.Send(body);
                var saved = client.ReadResponse();
                Assert.Equal(200, saved.Status);
                Assert.Equal("{}", saved.BodyText);
                Assert.Null(saved.Header("ETag"));

                // Same connection (keep-alive): a chunked merge, then the reads.
                string merge = "operation=merge&object=" + UrlEncoding.Encode(GameData.SectionsMerge) + "&access_token=" + token;
                var chunked = new StringBuilder();
                chunked.Append("POST /profiles/me/myprofile HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\nContent-Type: application/x-www-form-urlencoded\r\nTransfer-Encoding: chunked\r\n\r\n");
                for (int i = 0; i < merge.Length; i += 100)
                {
                    string piece = merge.Substring(i, System.Math.Min(100, merge.Length - i));
                    chunked.Append(piece.Length.ToString("x")).Append("\r\n").Append(piece).Append("\r\n");
                }
                chunked.Append("0\r\n\r\n");
                client.Send(chunked.ToString());
                Assert.Equal(200, client.ReadResponse().Status);

                client.Send("GET /profiles/me/myprofile/_mcvs.gameProfile?access_token=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n\r\n");
                var reread = client.ReadResponse();
                Assert.Equal(200, reread.Status);
                Assert.Equal(dump, reread.BodyText);
                Assert.Null(reread.Header("ETag"));

                client.Send("GET /profiles/me/myprofile?include_fields=" + UrlEncoding.Encode(GameData.SectionsIncludeFields)
                            + "&access_token=" + token + " HTTP/1.1\r\nHost: 127.0.0.1:" + server.Port + "\r\n\r\n");
                var sections = MiniJson.ParseObject(client.ReadResponse().BodyText);
                Assert.Equal(1000L, JsonPath.Get(sections, "_mcvs.snapshot.MatchMakingRating"));
            }
        }

        [Fact]
        public async Task ParallelClientsForSeveralAccountsKeepEveryWrite()
        {
            using var dir = new TempDir();
            using var server = Start(dir);
            string baseUrl = "http://127.0.0.1:" + server.Port + "/";
            var tokens = Enumerable.Range(0, 4).Select(i => Token(server, "u" + i)).ToArray();
            using var http = new HttpClient();

            async Task Worker(int player, int worker)
            {
                for (int i = 0; i < 10; i++)
                {
                    var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["operation"] = "merge",
                        ["object"] = "{\"w" + worker + "\":{\"n\":" + i + "},\"_mcvs\":{\"snapshot\":{\"p" + worker + "\":" + i + "}}}",
                        ["access_token"] = tokens[player],
                    });
                    var response = await http.PostAsync(baseUrl + "profiles/me/myprofile", content);
                    Assert.Equal(200, (int)response.StatusCode);
                }
            }

            var all = new List<Task>();
            for (int p = 0; p < tokens.Length; p++)
            {
                for (int w = 0; w < 4; w++)
                {
                    int player = p, worker = w; // capture copies, not the loop variables
                    all.Add(Task.Run(() => Worker(player, worker)));
                }
            }
            await Task.WhenAll(all);

            foreach (string token in tokens)
            {
                var doc = MiniJson.ParseObject(await http.GetStringAsync(baseUrl + "profiles/me/myprofile?access_token=" + token));
                for (int w = 0; w < 4; w++)
                {
                    Assert.Equal(9L, JsonPath.Get(doc, "w" + w + ".n"));
                    Assert.Equal(9L, JsonPath.Get(doc, "_mcvs.snapshot.p" + w));
                }
            }
            Assert.Equal(4, server.Context.Features.Get<LocalProfileStore>().AccountIds().Count);
        }
    }
}
