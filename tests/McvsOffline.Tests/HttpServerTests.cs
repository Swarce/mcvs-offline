using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>The HTTP/1.1 server over real sockets: framing, Expect: 100-continue, keep-alive, errors, streams.</summary>
    public class HttpServerTests
    {
        private static HttpResponse Echo(HttpRequest request)
        {
            var o = new Dictionary<string, object>
            {
                ["method"] = request.Method,
                ["path"] = request.Path,
                ["raw"] = request.RawTarget,
                ["bodyLength"] = (long)request.Body.Length,
                ["a"] = request.Param("a"),
                ["b"] = request.Param("b"),
                ["body"] = request.IsFormBody ? null : request.BodyText,
            };
            return HttpResponse.Json(o);
        }

        [Fact]
        public void GetReturnsJsonWithContentLengthAndDate()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            client.Send("GET /x/y?a=1 HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            var r = client.ReadResponse();
            Assert.Equal(200, r.Status);
            Assert.Equal("application/json; charset=utf-8", r.Header("Content-Type"));
            Assert.Equal(r.Body.Length.ToString(), r.Header("Content-Length"));
            Assert.NotNull(r.Header("Date"));
            Assert.Null(r.Header("ETag"));
            Assert.Contains("\"path\":\"/x/y\"", r.BodyText);
            Assert.Contains("\"a\":\"1\"", r.BodyText);
        }

        [Fact]
        public void ASecondServerCannotBindTheSamePort()
        {
            // With SO_REUSEPORT (what .NET's ReuseAddress sets on Linux) the second bind would succeed and the kernel
            // would split connections between two hosts (MISC request).
            using var first = new TestHttpServer(Echo);
            var second = new HttpServer(System.Net.IPAddress.Loopback, first.Port, Echo, null);
            Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => second.Start());
            Assert.False(second.IsRunning);
        }

        [Fact]
        public void ARestartBindsAgainWhileOldConnectionsAreInTimeWait()
        {
            var server = new TestHttpServer(Echo);
            int port = server.Port;
            for (int i = 0; i < 3; i++)
            {
                // "Connection: close": the server closes first, so its side of each connection enters TIME_WAIT.
                using var client = server.Connect();
                client.Send("GET /x HTTP/1.1\r\nHost: h\r\nConnection: close\r\n\r\n");
                Assert.Equal(200, client.ReadResponse().Status);
            }
            server.Dispose();

            var again = new HttpServer(System.Net.IPAddress.Loopback, port, Echo, null);
            again.Start();
            try
            {
                Assert.Equal(port, again.Port);
            }
            finally
            {
                again.Stop();
            }
        }

        [Fact]
        public void PostFormBodyWithContentLength()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            string body = "a=hello%20world&b=fed_id%3Aabc";
            client.Send("POST /authorize HTTP/1.1\r\nHost: h\r\nContent-Type: application/x-www-form-urlencoded; charset=utf-8\r\n" +
                        "Content-Length: " + body.Length + "\r\n\r\n" + body);
            var r = client.ReadResponse();
            Assert.Equal(200, r.Status);
            Assert.Contains("\"a\":\"hello world\"", r.BodyText);
            Assert.Contains("\"b\":\"fed_id:abc\"", r.BodyText);
        }

        [Fact]
        public void ChunkedRequestBodyWithExtensionsAndTrailers()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            client.Send("POST /p HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\nContent-Type: text/plain\r\n\r\n" +
                        "5;ext=1\r\nhello\r\n" +
                        "6\r\n world\r\n" +
                        "0\r\nX-Trailer: yes\r\n\r\n");
            var r = client.ReadResponse();
            Assert.Equal(200, r.Status);
            Assert.Contains("\"body\":\"hello world\"", r.BodyText);
            Assert.Contains("\"bodyLength\":11", r.BodyText);
        }

        [Fact]
        public void ExpectContinueGetsInterim100BeforeTheBodyIsSent()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            string body = "a=1&b=2";
            client.Send("POST /p HTTP/1.1\r\nHost: h\r\nExpect: 100-continue\r\nContent-Type: application/x-www-form-urlencoded\r\n" +
                        "Content-Length: " + body.Length + "\r\n\r\n");
            var interim = client.ReadResponse();
            Assert.Equal(100, interim.Status);
            client.Send(body);
            var r = client.ReadResponse();
            Assert.Equal(200, r.Status);
            Assert.Contains("\"b\":\"2\"", r.BodyText);
        }

        [Fact]
        public void KeepAliveServesSeveralRequestsThenHonoursConnectionClose()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            for (int i = 0; i < 3; i++)
            {
                client.Send("GET /n" + i + " HTTP/1.1\r\nHost: h\r\n\r\n");
                var r = client.ReadResponse();
                Assert.Equal(200, r.Status);
                Assert.Contains("/n" + i, r.BodyText);
                Assert.Null(r.Header("Connection"));
            }
            client.Send("GET /last HTTP/1.1\r\nHost: h\r\nConnection: close\r\n\r\n");
            var last = client.ReadResponse();
            Assert.Equal("close", last.Header("Connection"));
            Assert.True(client.IsClosedByServer());
        }

        [Fact]
        public void PipelinedRequestsAreAnsweredInOrder()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            client.Send("GET /one HTTP/1.1\r\nHost: h\r\n\r\nPOST /two HTTP/1.1\r\nHost: h\r\nContent-Length: 3\r\nContent-Type: text/plain\r\n\r\nabcGET /three HTTP/1.1\r\nHost: h\r\n\r\n");
            Assert.Contains("/one", client.ReadResponse().BodyText);
            var two = client.ReadResponse();
            Assert.Contains("/two", two.BodyText);
            Assert.Contains("\"body\":\"abc\"", two.BodyText);
            Assert.Contains("/three", client.ReadResponse().BodyText);
        }

        [Fact]
        public void Http10ClosesUnlessKeepAliveIsRequested()
        {
            using var server = new TestHttpServer(Echo);
            using (var client = server.Connect())
            {
                client.Send("GET /a HTTP/1.0\r\n\r\n");
                var r = client.ReadResponse();
                Assert.StartsWith("HTTP/1.0 200", r.StatusLine);
                Assert.True(client.IsClosedByServer());
            }
            using (var client = server.Connect())
            {
                client.Send("GET /a HTTP/1.0\r\nConnection: keep-alive\r\n\r\n");
                var r = client.ReadResponse();
                Assert.Equal("keep-alive", r.Header("Connection"));
                client.Send("GET /b HTTP/1.0\r\nConnection: keep-alive\r\n\r\n");
                Assert.Contains("/b", client.ReadResponse().BodyText);
            }
        }

        [Fact]
        public void MalformedRequestGets400AndTheConnectionIsClosed()
        {
            using var server = new TestHttpServer(Echo);
            using var client = server.Connect();
            client.Send("THIS IS NOT HTTP\r\n\r\n");
            var r = client.ReadResponse();
            Assert.Equal(400, r.Status);
            Assert.Equal("close", r.Header("Connection"));
            Assert.True(client.IsClosedByServer());

            using var client2 = server.Connect();
            client2.Send("GET /x HTTP/1.1\r\nBad Header Line\r\n\r\n");
            Assert.Equal(400, client2.ReadResponse().Status);

            using var client3 = server.Connect();
            client3.Send("GET /x FTP/1.0\r\n\r\n");
            Assert.Equal(505, client3.ReadResponse().Status);
        }

        [Fact]
        public void OversizedBodyGets413()
        {
            using var server = new TestHttpServer(Echo, new HttpServerLimits { MaxBodyBytes = 1000 });
            using var client = server.Connect();
            client.Send("POST /x HTTP/1.1\r\nHost: h\r\nContent-Length: 5000\r\n\r\n");
            Assert.Equal(413, client.ReadResponse().Status);
        }

        [Fact]
        public void HandlerExceptionBecomes500AndTheServerKeepsRunning()
        {
            using var server = new TestHttpServer(req => req.Path == "/boom" ? throw new InvalidOperationException("boom") : HttpResponse.Text("ok"));
            using var client = server.Connect();
            client.Send("GET /boom HTTP/1.1\r\nHost: h\r\n\r\n");
            Assert.Equal(500, client.ReadResponse().Status);
            client.Send("GET /fine HTTP/1.1\r\nHost: h\r\n\r\n");
            Assert.Equal("ok", client.ReadResponse().BodyText);
            Assert.True(server.Logs.Contains("boom"));
        }

        [Fact]
        public void HeadSendsHeadersWithoutBody()
        {
            using var server = new TestHttpServer(_ => HttpResponse.Text("hello"));
            using var client = server.Connect();
            client.Send("HEAD /x HTTP/1.1\r\nHost: h\r\n\r\nGET /x HTTP/1.1\r\nHost: h\r\n\r\n");
            var head = client.ReadResponse(headRequest: true);
            Assert.Equal("5", head.Header("Content-Length"));
            Assert.Equal("hello", client.ReadResponse().BodyText); // the next response starts right after the headers
        }

        [Fact]
        public void StreamedResponseIsChunkedAndFlushedPerWrite()
        {
            using var server = new TestHttpServer(_ => HttpResponse.Stream(s =>
            {
                s.Write("data: {\"n\":1}\n");
                s.Write("data: {\"n\":2}\n");
                s.Write("data: {\"n\":3}\n");
            }, "text/event-stream"));
            using var client = server.Connect();
            client.Send("GET /alerts/me HTTP/1.1\r\nHost: h\r\n\r\n");
            var r = client.ReadResponse();
            Assert.True(r.WasChunked);
            Assert.Equal("data: {\"n\":1}\ndata: {\"n\":2}\ndata: {\"n\":3}\n", r.BodyText);
            // The connection stays usable after the stream ends.
            client.Send("GET /again HTTP/1.1\r\nHost: h\r\n\r\n");
            Assert.Equal(200, client.ReadResponse().Status);
        }

        [Fact]
        public void StopEndsLongRunningStreamsAndIdleConnections()
        {
            var started = new ManualResetEventSlim();
            var server = new TestHttpServer(_ => HttpResponse.Stream(s =>
            {
                started.Set();
                while (!s.WaitForStop(50)) { }
            }, "text/event-stream"));
            using var streaming = server.Connect();
            using var idle = server.Connect();
            streaming.Send("GET /alerts/me HTTP/1.1\r\nHost: h\r\n\r\n");
            Assert.True(started.Wait(5000));
            server.Dispose();
            Assert.True(idle.IsClosedByServer());
            Assert.Equal(0, server.Server.ActiveConnections);
        }

        [Fact]
        public async Task DotNetHttpClientWithExpectContinueAndLargeFormBody()
        {
            using var server = new TestHttpServer(Echo);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.ExpectContinue = true;
            string big = new string('x', 3 * 1024 * 1024);
            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("a", "1"), new KeyValuePair<string, string>("b", big) });
            var response = await http.PostAsync("http://127.0.0.1:" + server.Port + "/profiles/me/myprofile/_mcvs.gameProfile", content);
            Assert.Equal(200, (int)response.StatusCode);
            string text = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"a\":\"1\"", text);
            Assert.Contains("\"bodyLength\":" + (big.Length + "a=1&b=".Length), text);

            // Keep-alive reuse by the client.
            var again = await http.GetAsync("http://127.0.0.1:" + server.Port + "//config/x/");
            Assert.Contains("\"path\":\"/config/x\"", await again.Content.ReadAsStringAsync());
        }

        [Fact]
        public void ConnectionLimitAnswers503()
        {
            var release = new ManualResetEventSlim();
            using var server = new TestHttpServer(_ => HttpResponse.Stream(s => { while (!release.IsSet && !s.WaitForStop(20)) { } }, "text/plain"),
                                                  new HttpServerLimits { MaxConnections = 1 });
            using var first = server.Connect();
            first.Send("GET /hold HTTP/1.1\r\nHost: h\r\n\r\n");
            SpinWait.SpinUntil(() => server.Server.ActiveConnections == 1, 5000);
            using var second = server.Connect();
            Assert.Equal(503, second.ReadResponse().Status);
            release.Set();
        }
    }
}
