using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Server;
using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The pieces of the port fallback (docs/design/port-fallback.md): the probe of a busy port's holder, the reservation's
    /// order, and the hooks the patched game reads. The host end to end: EmbeddedHostTests.
    /// </summary>
    [Collection("GameHooks")]
    public class PortFallbackTests
    {
        // ------------------------------------------------------------------ fake holders

        /// <summary>Listens and never accepts: the kernel completes connections into the backlog and nobody answers (a frozen process).</summary>
        internal sealed class SilentHolder : IDisposable
        {
            private readonly TcpListener listener;

            public SilentHolder(IPAddress address = null, int port = 0)
            {
                listener = new TcpListener(address ?? IPAddress.Loopback, port);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            public int Port { get; }

            public void Dispose() => listener.Stop();
        }

        /// <summary>Accepts and resets every connection at once (an adb reverse with nothing behind it refuses the same way).</summary>
        internal sealed class ResettingHolder : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread thread;
            private volatile bool stopped;

            public ResettingHolder(string answer = null)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                thread = new Thread(() =>
                {
                    while (!stopped)
                    {
                        try
                        {
                            using Socket s = listener.AcceptSocket();
                            if (answer != null)
                            {
                                s.ReceiveTimeout = 2000;
                                var buffer = new byte[4096];
                                s.Receive(buffer);
                                s.Send(Encoding.ASCII.GetBytes(answer));
                                s.Shutdown(SocketShutdown.Both);
                            }
                            else
                            {
                                s.LingerState = new LingerOption(true, 0);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                }) { IsBackground = true };
                thread.Start();
            }

            public int Port { get; }

            public void Dispose()
            {
                stopped = true;
                listener.Stop();
            }
        }

        // ------------------------------------------------------------------ probe

        [Fact]
        public void ASilentHolderIsSilentWithinTheBudget()
        {
            using var holder = new SilentHolder();
            var watch = Stopwatch.StartNew();
            PortHolder result = PortProbe.Probe(holder.Port, PortProbe.DefaultConnectMs, PortProbe.DefaultReadMs);
            watch.Stop();
            Assert.Equal(PortHolderKind.Silent, result.Kind);
            Assert.Contains("accepted the connection but sent nothing in 500 ms", result.Detail);
            Assert.InRange(watch.ElapsedMilliseconds, 400, 1500);
        }

        [Fact]
        public void AResettingHolderIsClosed()
        {
            using var holder = new ResettingHolder();
            PortHolder result = PortProbe.Probe(holder.Port, 250, 500);
            Assert.Equal(PortHolderKind.Closed, result.Kind);
            Assert.InRange(result.ElapsedMs, 0, 1000);
        }

        [Fact]
        public void NothingListeningIsRefused()
        {
            int port = TestEnvironment.FreePort();
            PortHolder result = PortProbe.Probe(port, 250, 500);
            Assert.Equal(PortHolderKind.Refused, result.Kind);
        }

        [Fact]
        public void AnotherHttpServerIsForeign()
        {
            using var holder = new ResettingHolder("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            PortHolder result = PortProbe.Probe(holder.Port, 250, 1000);
            Assert.Equal(PortHolderKind.Foreign, result.Kind);
            Assert.Contains("HTTP/1.1 404 Not Found", result.Detail);
        }

        [Fact]
        public void AWorkingMcvsServerIsRecognised()
        {
            string data = TestEnvironment.NewTempDir("probe-mcvs");
            using McvsServer server = McvsServer.Create(new ServerOptions { DataDir = data, Port = 0, ConsoleLog = false, FileLog = false, RegisterServices = (r, c) => { } });
            server.Start();
            PortHolder result = PortProbe.Probe(server.Port, 250, 2000);
            Assert.Equal(PortHolderKind.Mcvs, result.Kind);
            Assert.Equal(server.Context.DataDir, result.DataDir);
            Assert.Equal(server.Context.InstanceId, result.Instance);
        }

        [Fact]
        public void ClassifyNeedsTheMcvsServerName()
        {
            Assert.Equal(PortHolderKind.Mcvs, PortProbe.Classify("HTTP/1.1 200 OK\r\n\r\n{\"server\": \"mcvs-offline\", \"version\": \"0.26\"}").Kind);
            Assert.Equal(PortHolderKind.Foreign, PortProbe.Classify("HTTP/1.1 200 OK\r\n\r\n{\"server\": \"nginx\"}").Kind);
            Assert.Equal(PortHolderKind.Foreign, PortProbe.Classify("HTTP/1.1 200 OK\r\n\r\n{\"server\": \"mcvs-off").Kind);
            Assert.Equal(PortHolderKind.Foreign, PortProbe.Classify("SSH-2.0-OpenSSH_9.6\r\n").Kind);
        }

        // ------------------------------------------------------------------ reservation

        [Fact]
        public void AFreePortIsBoundOnceWithoutAProbe()
        {
            int port = TestEnvironment.FreePort();
            int probes = 0;
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, port, true, null, p => { probes++; return null; });
            try
            {
                Assert.Equal(PortOutcome.Configured, r.Outcome);
                Assert.Equal(port, r.Port);
                Assert.NotNull(r.Listener);
                Assert.Null(r.Reason);
                Assert.Equal(0, probes);
            }
            finally
            {
                r.Release();
            }
        }

        [Fact]
        public void ASilentHolderMovesTheServerToTheNextFreePort()
        {
            using var holder = new SilentHolder();
            var watch = Stopwatch.StartNew();
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, holder.Port, true, null, null);
            watch.Stop();
            try
            {
                Assert.Equal(PortOutcome.Fallback, r.Outcome);
                Assert.NotEqual(holder.Port, r.Port);
                Assert.Equal(PortHolderKind.Silent, r.Holder.Kind);
                Assert.Equal(SocketError.AddressAlreadyInUse, r.BindError.SocketErrorCode);
                Assert.Contains("port " + holder.Port + " is held by another program that does not answer as MCVS Offline", r.Reason);
                Assert.InRange(watch.ElapsedMilliseconds, 0, 2500);
            }
            finally
            {
                r.Release();
            }
        }

        [Fact]
        public void TheFallbackOrderIsTheNeighboursThenAnyPort()
        {
            var tried = new List<int>();
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, 51780, true, (a, p) =>
            {
                tried.Add(p);
                if (p != 0 && p != 51784) throw new SocketException((int)SocketError.AddressAlreadyInUse);
                return HttpServer.Listen(a, 0, null);
            }, p => new PortHolder(PortHolderKind.Closed, "closed"));
            r.Release();
            Assert.Equal(PortOutcome.Fallback, r.Outcome);
            Assert.Equal(new[] { 51780, 51780, 51780, 51781, 51782, 51783, 51784 }, tried);

            tried.Clear();
            r = PortReservation.Reserve(IPAddress.Loopback, 51780, true, (a, p) =>
            {
                tried.Add(p);
                if (p != 0) throw new SocketException((int)SocketError.AddressAlreadyInUse);
                return HttpServer.Listen(a, 0, null);
            }, p => new PortHolder(PortHolderKind.Silent, "silent"));
            r.Release();
            Assert.Equal(PortOutcome.Fallback, r.Outcome);
            Assert.Equal(new[] { 51780, 51780, 51780, 51781, 51782, 51783, 51784, 51785, 51786, 51787, 51788, 51789, 0 }, tried);
        }

        [Fact]
        public void AWindowsReservedRangeGoesStraightToAnyPortWithoutAProbe()
        {
            var tried = new List<int>();
            int probes = 0;
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, 51780, true, (a, p) =>
            {
                tried.Add(p);
                if (p != 0) throw new SocketException((int)SocketError.AccessDenied);
                return HttpServer.Listen(a, 0, null);
            }, p => { probes++; return null; });
            r.Release();
            Assert.Equal(PortOutcome.Fallback, r.Outcome);
            Assert.Equal(new[] { 51780, 0 }, tried);
            Assert.Equal(0, probes);
            Assert.Contains("Windows reserves it", r.Reason);
        }

        [Fact]
        public void AWorkingMcvsServerIsUsedAndNothingIsBound()
        {
            var tried = new List<int>();
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, 51780, true, (a, p) =>
            {
                tried.Add(p);
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }, p => new PortHolder(PortHolderKind.Mcvs, "MCVS Offline 0.26 answers"));
            Assert.Equal(PortOutcome.Deferred, r.Outcome);
            Assert.Equal(51780, r.Port);
            Assert.Null(r.Listener);
            Assert.Equal(new[] { 51780 }, tried);
        }

        [Fact]
        public void APinnedPortNeverFallsBack()
        {
            var tried = new List<int>();
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, 51780, false, (a, p) =>
            {
                tried.Add(p);
                throw new SocketException((int)SocketError.AddressAlreadyInUse);
            }, p => new PortHolder(PortHolderKind.Silent, "silent"));
            Assert.Equal(PortOutcome.Failed, r.Outcome);
            Assert.Equal(51780, r.Port);
            Assert.Null(r.Listener);
            Assert.Equal(new[] { 51780, 51780, 51780 }, tried);
        }

        [Fact]
        public void AHolderThatGoesAwayDuringTheProbeLeavesTheConfiguredPort()
        {
            int calls = 0;
            PortReservation r = PortReservation.Reserve(IPAddress.Loopback, 51780, true, (a, p) =>
            {
                if (calls++ == 0) throw new SocketException((int)SocketError.AddressAlreadyInUse);
                return HttpServer.Listen(a, 0, null);
            }, p => new PortHolder(PortHolderKind.Refused, "refused"));
            r.Release();
            Assert.Equal(PortOutcome.Configured, r.Outcome);
            Assert.Null(r.Reason);
        }

        // ------------------------------------------------------------------ finder

        [Fact]
        public void ABusyFinderPortMovesToTheNextFreeOne()
        {
            using var holder = new SilentHolder(IPAddress.Any);
            Assert.False(FinderPortCheck.IsFree(holder.Port));
            int chosen = FinderPortCheck.Choose(holder.Port, null);
            Assert.NotEqual(holder.Port, chosen);
            Assert.True(FinderPortCheck.IsFree(chosen));

            Assert.Equal(51752, FinderPortCheck.Choose(51750, p => p == 51752));
            Assert.Equal(51750, FinderPortCheck.Choose(51750, p => true));
            int any = FinderPortCheck.Choose(51750, p => false);
            Assert.True(any > 0 && any != 51750, "ephemeral " + any);
        }

        // ------------------------------------------------------------------ hooks

        [Fact]
        public void TheHooksReturnTheBakedValuesUntilTheHostDecides()
        {
            try
            {
                PortHooks.Configure(0, 0);
                Assert.Equal("http://127.0.0.1:51780/location/?ip=", PortHooks.GeolocalizationUrl("http://127.0.0.1:51780/location/?ip="));
                Assert.Equal(51750, PortHooks.LocalAnubisFinderPort(51750));

                PortHooks.Configure(51783, 51751);
                Assert.Equal("http://127.0.0.1:51783/", PortHooks.ServerBase);
                Assert.Equal("http://127.0.0.1:51783/location/?ip=", PortHooks.GeolocalizationUrl("http://127.0.0.1:51780/location/?ip="));
                Assert.Equal(51751, PortHooks.LocalAnubisFinderPort(51750));
                // Only our own loopback URL is rewritten.
                Assert.Equal("http://ip.ecomapis.gameloft.org/location/?ip=", PortHooks.GeolocalizationUrl("http://ip.ecomapis.gameloft.org/location/?ip="));
                Assert.Equal("http://127.0.0.1:51780/other/", PortHooks.GeolocalizationUrl("http://127.0.0.1:51780/other/"));
                Assert.Equal("http://127.0.0.1:x/location/", PortHooks.GeolocalizationUrl("http://127.0.0.1:x/location/"));
                Assert.Null(PortHooks.GeolocalizationUrl(null));

                PortHooks.Configure(70000, -1);
                Assert.Null(PortHooks.ServerBase);
                Assert.Equal(51750, PortHooks.LocalAnubisFinderPort(51750));
            }
            finally
            {
                PortHooks.Configure(0, 0);
            }
        }
    }
}
