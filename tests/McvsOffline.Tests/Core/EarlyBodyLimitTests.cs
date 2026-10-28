using System;
using System.IO;
using System.Net;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>
    /// The header-time body limit (design §5.2 rule 5): under /_mcvs/ a request without a valid session can never make
    /// the server read more than 1 KiB of body; the check happens after the headers, before the body and before
    /// "100 Continue".
    /// </summary>
    public class EarlyBodyLimitTests
    {
        private static readonly Func<string, string, HttpHeaders, long> Limit = (method, target, headers) =>
            LocalAccessGate.BodyLimit(method, target, headers, cookie => cookie == "valid-session",
                                      (m, p) => p == "/_mcvs/api/v1/settings" ? 64 * 1024 : 0);

        private static HttpRequest Parse(string head, byte[] body, MemoryStream output = null)
        {
            var input = new MemoryStream();
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            input.Write(headBytes, 0, headBytes.Length);
            if (body != null) input.Write(body, 0, body.Length);
            input.Position = 0;
            return HttpRequestParser.Read(new HttpConnectionReader(input), output ?? new MemoryStream(), new HttpServerLimits(),
                                          new IPEndPoint(IPAddress.Loopback, 40000), null, Limit);
        }

        [Fact]
        public void ADeclaredBodyAboveTheLimitIsRefusedBeforeItIsRead()
        {
            // The stream holds the headers only: reading the body would fail with an IOException instead.
            var output = new MemoryStream();
            var ex = Assert.Throws<RequestBodyLimitException>(() =>
                Parse("POST /_mcvs/api/v1/settings?note=secret HTTP/1.1\r\nHost: 127.0.0.1:51780\r\nContent-Type: application/json\r\n"
                      + "Expect: 100-continue\r\nContent-Length: 2048\r\n\r\n", null, output));
            Assert.Equal(1024, ex.Limit);
            Assert.Equal(0, output.Length); // no "100 Continue"
            Assert.Contains("/_mcvs/api/v1/settings", ex.Message);
            Assert.DoesNotContain("secret", ex.Message); // logged without the query

            // Without the limit the parser would wait for the body (here: the stream ends).
            Assert.Throws<IOException>(() => HttpRequestParser.Read(
                new HttpConnectionReader(new MemoryStream(Encoding.ASCII.GetBytes(
                    "POST /_mcvs/api/v1/settings HTTP/1.1\r\nHost: x\r\nContent-Length: 2048\r\n\r\n"))),
                new MemoryStream(), new HttpServerLimits(), null, null));
        }

        [Fact]
        public void AValidSessionGetsTheRouteLimit()
        {
            const string head = "POST /_mcvs/api/v1/settings HTTP/1.1\r\nHost: 127.0.0.1:51780\r\nCookie: mcvs_s=valid-session\r\n"
                                + "Content-Type: application/json\r\nContent-Length: ";
            var body = new byte[2048];
            HttpRequest request = Parse(head + body.Length + "\r\n\r\n", body);
            Assert.Equal(2048, request.Body.Length);

            var max = new byte[64 * 1024];
            Assert.Equal(max.Length, Parse(head + max.Length + "\r\n\r\n", max).Body.Length);
            Assert.Throws<RequestBodyLimitException>(() => Parse(head + (max.Length + 1) + "\r\n\r\n", null));

            // Another cookie value, or an unknown admin route: 1 KiB again.
            Assert.Throws<RequestBodyLimitException>(() => Parse(head.Replace("valid-session", "other") + body.Length + "\r\n\r\n", null));
            Assert.Throws<RequestBodyLimitException>(() =>
                Parse(head.Replace("/settings", "/whatever") + body.Length + "\r\n\r\n", null));
        }

        [Fact]
        public void ChunkedBodiesStopAtTheLimit()
        {
            string chunk = new string('a', 600);
            string size = chunk.Length.ToString("x");
            string chunked = size + "\r\n" + chunk + "\r\n" + size + "\r\n" + chunk + "\r\n0\r\n\r\n";
            const string head = "POST /_mcvs/api/v1/settings HTTP/1.1\r\nHost: 127.0.0.1:51780\r\nTransfer-Encoding: chunked\r\n";
            var ex = Assert.Throws<RequestBodyLimitException>(() => Parse(head + "\r\n" + chunked, null));
            Assert.Equal(1024, ex.Limit);

            HttpRequest withSession = Parse(head + "Cookie: mcvs_s=valid-session\r\n\r\n" + chunked, null);
            Assert.Equal(1200, withSession.Body.Length);
        }

        [Fact]
        public void FederationRoutesKeepTheGlobalLimit()
        {
            var body = new byte[256 * 1024];
            HttpRequest request = Parse("POST /profiles/me/myprofile HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: " + body.Length + "\r\n\r\n", body);
            Assert.Equal(body.Length, request.Body.Length);
        }

        [Fact]
        public void OverASocketTheAnswerIs413WithoutReadingTheBody()
        {
            using var dir = new TempDir();
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = 0, ConsoleLog = false, FileLog = false };
            using var server = McvsServer.Create(options);
            server.Context.IsValidAdminSession = cookie => cookie == "valid-session";
            server.Context.AdminRouteBodyLimit = (m, p) => p == "/_mcvs/api/v1/whatever" ? 64 * 1024 : 0;
            server.Start();
            string host = "127.0.0.1:" + server.Port;

            using (var client = new RawHttpClient(server.Port))
            {
                // Headers only, as a client waiting for "100 Continue": the answer comes at once, and it is not a 100.
                client.Send("POST /_mcvs/api/v1/whatever HTTP/1.1\r\nHost: " + host + "\r\nX-MCVS: 1\r\nContent-Type: application/json\r\n"
                            + "Expect: 100-continue\r\nContent-Length: 2048\r\n\r\n");
                RawResponse response = client.ReadResponse();
                Assert.Equal(413, response.Status);
                var error = MiniJson.ParseObject(response.BodyText);
                Assert.Equal("too_large", error["error"]);
                Assert.Equal(1024L, JsonPath.Get(error, "details.limit"));
                Assert.Equal("close", response.Header("Connection"));
                Assert.True(client.IsClosedByServer());
            }

            using (var client = new RawHttpClient(server.Port))
            {
                // A client that sends everything at once still reads the 413 (the server drains before closing).
                client.Send("POST /_mcvs/api/v1/whatever HTTP/1.1\r\nHost: " + host + "\r\nX-MCVS: 1\r\nContent-Type: application/json\r\n"
                            + "Content-Length: 4096\r\n\r\n" + new string('x', 4096));
                Assert.Equal(413, client.ReadResponse().Status);
            }

            using (var client = new RawHttpClient(server.Port))
            {
                // With a valid session the route's limit applies: the request reaches the router.
                client.Send("POST /_mcvs/api/v1/whatever HTTP/1.1\r\nHost: " + host + "\r\nX-MCVS: 1\r\nCookie: mcvs_s=valid-session\r\n"
                            + "Content-Type: application/json\r\nContent-Length: 4096\r\n\r\n" + new string('x', 4096));
                Assert.NotEqual(413, client.ReadResponse().Status);
            }
        }
    }
}
