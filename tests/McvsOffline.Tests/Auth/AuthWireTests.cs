using System;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests.Auth
{
    /// <summary>
    /// The boot exchanges over a real socket, with the bytes Mono's HttpClient sends: POST with
    /// Expect: 100-continue, keep-alive reuse, the client's headers; and the framing of what comes back.
    /// </summary>
    public class AuthWireTests
    {
        [Fact]
        public void BootExchangesOverOneKeepAliveConnection()
        {
            using var dir = new TempDir();
            var options = new ServerOptions { DataDir = dir.Combine("data"), Port = 0, ConsoleLog = false, FileLog = false };
            using var server = McvsServer.Create(options);
            server.Start();
            string host = "127.0.0.1:" + server.Port;
            const string ua = "User-Agent: (2620:66102:1.17.32:android:googleplay; mcvs)\r\nCache-Control: NO-CACHE\r\n";

            using var client = new RawHttpClient(server.Port);

            client.Send("GET /config/2620:66102:1.17.32:android:googleplay/datacenters HTTP/1.1\r\nHost: " + host + "\r\n" + ua + "\r\n");
            var datacenters = client.ReadResponse();
            Assert.Equal(200, datacenters.Status);
            Assert.StartsWith("application/json", datacenters.Header("Content-Type"));

            client.Send("GET /locate/auth?client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay HTTP/1.1\r\nHost: " + host + "\r\n" + ua + "\r\n");
            var locate = client.ReadResponse();
            Assert.Equal(200, locate.Status);
            Assert.StartsWith("text/plain", locate.Header("Content-Type"));
            Assert.Equal("http://" + host + "/auth/", locate.BodyText); // no quotes, no newline, the real port
            Assert.Null(locate.Header("ETag"));

            string form = "username=abc&credential_type=anonymous&password=pw&device_id=dev&client_id=2620%3A66102%3A1.17.32%3Aandroid%3Agoogleplay&scope=auth%20lobby%20config";
            client.Send("POST /authorize HTTP/1.1\r\nHost: " + host + "\r\n" + ua
                        + "Content-Type: application/x-www-form-urlencoded; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(form)
                        + "\r\nExpect: 100-continue\r\n\r\n");
            Assert.Equal(100, client.ReadResponse().Status);
            client.Send(form);
            var authorize = client.ReadResponse();
            Assert.Equal(200, authorize.Status);
            var body = MiniJson.ParseObject(authorize.BodyText);
            string token = (string)body["access_token"];
            Assert.Equal("auth lobby config", body["scope"]);

            client.Send("GET /users/me?access_token=" + token + " HTTP/1.1\r\nHost: " + host + "\r\n" + ua + "\r\n");
            var me = client.ReadResponse();
            Assert.Equal(200, me.Status);
            Assert.Contains("\"anonymous:abc\"", me.BodyText);

            client.Send("GET /encrypt_token?nonce=LocalAnubisNonce&access_token=" + token + " HTTP/1.1\r\nHost: " + host + "\r\n" + ua + "\r\n");
            var encrypted = client.ReadResponse();
            Assert.Equal(200, encrypted.Status);
            Assert.StartsWith("text/plain", encrypted.Header("Content-Type"));
            Assert.Equal(token, encrypted.BodyText);

            client.Send("GET /users/me HTTP/1.1\r\nHost: " + host + "\r\n" + ua + "\r\n");
            var missing = client.ReadResponse();
            Assert.Equal(401, missing.Status);
            Assert.StartsWith("application/json", missing.Header("Content-Type"));
            Assert.Null(missing.Header("WWW-Authenticate"));
        }
    }
}
