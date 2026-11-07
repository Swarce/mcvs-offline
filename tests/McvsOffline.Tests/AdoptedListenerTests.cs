using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Tests.Admin;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>
    /// The port fallback's server side (docs/design/port-fallback.md): the embedded host binds the socket before args.txt is
    /// written and hands it over (ServerOptions.Listener); the server's port is that socket's from Create on.
    /// </summary>
    public class AdoptedListenerTests
    {
        [Fact]
        public void AConnectionQueuedBeforeStartIsAnswered()
        {
            Socket listening = HttpServer.Listen(IPAddress.Loopback, 0, null);
            int port = ((IPEndPoint)listening.LocalEndPoint).Port;
            using var server = new HttpServer(listening, r => HttpResponse.Json(new System.Collections.Generic.Dictionary<string, object> { ["ok"] = true }), Log.Null);
            Assert.Equal(0, server.Port);

            // The game may connect between the reservation and the accept thread: the backlog keeps it.
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            byte[] request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n");
            client.Send(request);

            server.Start();
            Assert.Equal(port, server.Port);
            client.ReceiveTimeout = 5000;
            var buffer = new byte[4096];
            int read = client.Receive(buffer);
            Assert.Matches(@"^HTTP/1\.[01] 200 ", Encoding.ASCII.GetString(buffer, 0, read));
        }

        [Fact]
        public void AnUnstartedServerClosesTheAdoptedSocket()
        {
            Socket listening = HttpServer.Listen(IPAddress.Loopback, 0, null);
            int port = ((IPEndPoint)listening.LocalEndPoint).Port;
            new HttpServer(listening, r => HttpResponse.Json(null), Log.Null).Dispose();
            // The port is free again: nothing leaks for the rest of the process.
            HttpServer.Listen(IPAddress.Loopback, port, null).Close();
        }

        [Fact]
        public void ListenThrowsAddressInUseAndLeavesNothingOpen()
        {
            using Socket holder = HttpServer.Listen(IPAddress.Loopback, 0, null);
            int port = ((IPEndPoint)holder.LocalEndPoint).Port;
            SocketException ex = Assert.Throws<SocketException>(() => HttpServer.Listen(IPAddress.Loopback, port, null));
            Assert.Equal(SocketError.AddressAlreadyInUse, ex.SocketErrorCode);
        }

        [Fact]
        public void TheServerUsesTheHandedOverSocketFromCreateOn()
        {
            using var dir = new TempDir();
            Socket listening = HttpServer.Listen(IPAddress.Loopback, 0, null);
            int bound = ((IPEndPoint)listening.LocalEndPoint).Port;
            var logs = new MemoryLogSink(LogLevel.Debug, 500);
            var options = new ServerOptions
            {
                DataDir = dir.Combine("data"),
                Port = 51780,                       // the configured port: busy in the real case, never bound here
                Listener = listening,
                PortNote = "settings port 51780 was busy at start",
                ConsoleLog = false,
                FileLog = false,
                RegisterServices = (r, c) =>
                {
                    ServiceRegistry.RegisterAll(r, c);
                    AdminService.Register(r, c, null);
                },
            };
            options.ExtraSinks.Add(logs);
            using McvsServer server = McvsServer.Create(options);

            // Before Start: every module saw the bound port (the admin log line used to name settings.port).
            Assert.Equal(bound, server.Context.Port);
            Assert.Equal(51780, server.Context.Settings.Port);
            Assert.Contains(logs.Snapshot(), e => e.Message.Contains("admin: settings page at http://127.0.0.1:" + bound + "/_mcvs/ui/"));
            Assert.Equal("http://127.0.0.1:" + bound + "/", server.Context.BaseUrl);

            server.Start();
            Assert.Equal(bound, server.Port);
            using (var http = new System.Net.Http.HttpClient())
            {
                string status = http.GetStringAsync("http://127.0.0.1:" + bound + "/_mcvs/status").Result;
                Assert.Equal("mcvs-offline", MiniJson.ParseObject(status)["server"]);
            }

            // The access gate wants this server's port in Host: the bound one.
            var client = new AdminTestClient(server, bound);
            client.OpenSession();
            var body = AdminTestClient.Json(client.Get("status"));
            Assert.Equal((long)bound, JsonPath.Get(body, "server.port"));
            Assert.Equal(51780L, JsonPath.Get(body, "server.configuredPort"));
            Assert.Equal("settings port 51780 was busy at start", JsonPath.Get(body, "server.portNote"));
        }

        [Fact]
        public void ADisposedServerThatNeverStartedFreesTheSocket()
        {
            using var dir = new TempDir();
            Socket listening = HttpServer.Listen(IPAddress.Loopback, 0, null);
            int bound = ((IPEndPoint)listening.LocalEndPoint).Port;
            McvsServer.Create(new ServerOptions { DataDir = dir.Combine("data"), Listener = listening, ConsoleLog = false, FileLog = false,
                                                  RegisterServices = (r, c) => { } }).Dispose();
            HttpServer.Listen(IPAddress.Loopback, bound, null).Close();
        }

        [Fact]
        public void WithoutAListenerTheStatusHasNoPortNote()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            client.OpenSession();
            var body = AdminTestClient.Json(client.Get("status"));
            Assert.Equal(51780L, JsonPath.Get(body, "server.configuredPort"));
            Assert.Null(JsonPath.Get(body, "server.portNote"));
        }
    }
}
