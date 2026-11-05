// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Embedded
{
    /// <summary>How this start got its server port (<see cref="PortReservation.Reserve"/>).</summary>
    public enum PortOutcome
    {
        /// <summary>The configured port is bound (the normal case).</summary>
        Configured,

        /// <summary>The configured port is busy and its holder is not MCVS Offline: another port is bound for this start.</summary>
        Fallback,

        /// <summary>A working MCVS Offline server answers on the configured port (a standalone through adb reverse...): the game uses it.</summary>
        Deferred,

        /// <summary>Nothing could be bound (or the fallback is off and the port is busy): the server thread retries the configured port.</summary>
        Failed,
    }

    /// <summary>
    /// The port fallback (docs/design/port-fallback.md): reserves the server's listening socket synchronously, before args.txt
    /// is written, so every place that points the game at the server can use the port actually bound.
    /// <list type="number">
    /// <item>Bind the configured port (normal case: one bind, well under a millisecond).</item>
    /// <item>Busy (EADDRINUSE): ask the holder what it is (<see cref="PortProbe"/>, at most about 0.75 s). A working MCVS Offline
    /// server: use it (<see cref="PortOutcome.Deferred"/>, never two servers on one port or, for another copy of the game, on one
    /// data folder). Anything else: try the configured port <see cref="Rebinds"/> more times (a dying process lets it go).</item>
    /// <item>Then, unless the fallback is off: the next <see cref="FallbackRange"/> ports in order (deterministic, so a LAN guest's
    /// line is predictable), then any free port (port 0). Windows' excluded port ranges (AccessDenied) skip the probe and the
    /// neighbours, which sit in the same reserved block, and go straight to port 0.</item>
    /// </list>
    /// The fallback is for this start only: settings.json keeps its port, and the next start tries it first again.
    /// </summary>
    public sealed class PortReservation
    {
        /// <summary>Ports tried after the configured one, in order (51781..51789 for 51780).</summary>
        public const int FallbackRange = 9;

        /// <summary>Further binds of the configured port after the probe, and the pause before each.</summary>
        public const int Rebinds = 2;

        public const int RebindDelayMs = 150;

        private PortReservation()
        {
        }

        public PortOutcome Outcome { get; private set; }

        /// <summary>The configured (preferred) port.</summary>
        public int ConfiguredPort { get; private set; }

        /// <summary>The port the game must use for the offline server this start (the bound one, or the configured one).</summary>
        public int Port { get; private set; }

        /// <summary>The bound, listening socket (Configured, Fallback), or null; the server adopts it (ServerOptions.Listener).</summary>
        public Socket Listener { get; private set; }

        /// <summary>What held the configured port, or null when it was free (or Windows reserves it).</summary>
        public PortHolder Holder { get; private set; }

        /// <summary>The first bind error on the configured port, or null when it was free.</summary>
        public SocketException BindError { get; private set; }

        /// <summary>Why the configured port could not be used, in a few words (the status page, the log line), or null.</summary>
        public string Reason { get; private set; }

        /// <summary>
        /// Reserves the server port. <paramref name="listen"/> binds and listens (HttpServer.Listen; tests inject failures),
        /// <paramref name="probe"/> asks a holder (PortProbe.Probe). Never throws for socket errors.
        /// </summary>
        public static PortReservation Reserve(IPAddress address, int configured, bool allowFallback,
                                              Func<IPAddress, int, Socket> listen, Func<int, PortHolder> probe)
        {
            if (address == null) throw new ArgumentNullException("address");
            listen = listen ?? ((a, p) => HttpServer.Listen(a, p, null));
            probe = probe ?? (p => PortProbe.Probe(p, PortProbe.DefaultConnectMs, PortProbe.DefaultReadMs));
            var r = new PortReservation { ConfiguredPort = configured, Port = configured };

            Socket socket = TryListen(listen, address, configured, r);
            if (socket != null) return r.Bound(PortOutcome.Configured, socket);

            bool reserved = r.BindError != null && r.BindError.SocketErrorCode == SocketError.AccessDenied;
            if (reserved)
            {
                r.Reason = "port " + Num(configured) + " cannot be used (" + r.BindError.SocketErrorCode + ": Windows reserves it for Hyper-V/WSL, " +
                           "an excluded port range, or another program has it exclusively)";
            }
            else
            {
                PortHolder holder = probe(configured) ?? new PortHolder(PortHolderKind.Silent, "unknown");
                r.Holder = holder;
                if (holder.Kind == PortHolderKind.Mcvs)
                {
                    r.Outcome = PortOutcome.Deferred;
                    r.Reason = "port " + Num(configured) + " is held by a working MCVS Offline server (" + holder.Detail + ")";
                    return r;
                }
                r.Reason = "port " + Num(configured) + " is held by another program that does not answer as MCVS Offline: " + holder.Detail;
                for (int i = 0; i < Rebinds; i++)
                {
                    Thread.Sleep(RebindDelayMs);
                    socket = TryListen(listen, address, configured, null);
                    if (socket != null)
                    {
                        r.Reason = null;
                        r.Holder = null;
                        return r.Bound(PortOutcome.Configured, socket);
                    }
                }
            }

            if (!allowFallback)
            {
                r.Outcome = PortOutcome.Failed;
                return r;
            }
            if (!reserved)
            {
                for (int p = configured + 1; p <= configured + FallbackRange && p <= 65535; p++)
                {
                    SocketException error;
                    socket = TryListen(listen, address, p, out error);
                    if (socket != null) return r.Bound(PortOutcome.Fallback, socket);
                    if (error != null && error.SocketErrorCode == SocketError.AccessDenied) break;
                }
            }
            SocketException last;
            socket = TryListen(listen, address, 0, out last);
            if (socket != null) return r.Bound(PortOutcome.Fallback, socket);
            r.Outcome = PortOutcome.Failed;
            return r;
        }

        /// <summary>Closes the socket if nobody adopted it (a failed server creation).</summary>
        public void Release()
        {
            Socket s = Listener;
            Listener = null;
            if (s == null) return;
            try { s.Close(); } catch (Exception) { }
        }

        /// <summary>The server adopted the socket: it closes it from now on.</summary>
        internal void Adopted()
        {
            Listener = null;
        }

        private PortReservation Bound(PortOutcome outcome, Socket socket)
        {
            Outcome = outcome;
            Listener = socket;
            var local = socket.LocalEndPoint as IPEndPoint;
            Port = local == null ? ConfiguredPort : local.Port;
            return this;
        }

        private static Socket TryListen(Func<IPAddress, int, Socket> listen, IPAddress address, int port, PortReservation first)
        {
            SocketException error;
            Socket socket = TryListen(listen, address, port, out error);
            if (socket == null && first != null) first.BindError = error ?? new SocketException((int)SocketError.SocketError);
            return socket;
        }

        private static Socket TryListen(Func<IPAddress, int, Socket> listen, IPAddress address, int port, out SocketException error)
        {
            error = null;
            try
            {
                return listen(address, port);
            }
            catch (SocketException ex)
            {
                error = ex;
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Num(int port)
        {
            return port.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The LocalAnubis Finder's port (docs/design/port-fallback.md): the game's own HTTP listener for QuickLaunch (51750 unless
    /// -LocalAnubisFinderPort says otherwise). A frozen earlier copy of the game holds it as well as the server port, and the
    /// game's LocalAnubisManager.Start fails when it is busy (the main menu never loads). Mono's listener binds it without
    /// SO_REUSEADDR, on every interface with the 'finder' patch (PC: http.sys, localhost), so the check binds 0.0.0.0 the same
    /// way, closes at once, and picks the next free one of <see cref="PortReservation.FallbackRange"/> ports, else any free port.
    /// The Finder only binds a few seconds later, so this is a best effort; it costs one bind when the port is free.
    /// </summary>
    public static class FinderPortCheck
    {
        /// <summary>True when 0.0.0.0:<paramref name="port"/> can be bound right now (the socket is closed again).</summary>
        public static bool IsFree(int port)
        {
            int ignored;
            return TryBind(port, out ignored);
        }

        /// <summary>
        /// <paramref name="preferred"/> when it is free, else the first free port after it (up to FallbackRange), else an
        /// ephemeral one; <paramref name="preferred"/> when nothing can be bound at all. <paramref name="isFree"/>: tests.
        /// </summary>
        public static int Choose(int preferred, Func<int, bool> isFree)
        {
            isFree = isFree ?? IsFree;
            if (Safe(isFree, preferred)) return preferred;
            for (int p = preferred + 1; p <= preferred + PortReservation.FallbackRange && p <= 65535; p++)
            {
                if (Safe(isFree, p)) return p;
            }
            int any;
            return TryBind(0, out any) && any > 0 ? any : preferred;
        }

        private static bool Safe(Func<int, bool> isFree, int port)
        {
            try
            {
                return isFree(port);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryBind(int port, out int bound)
        {
            bound = 0;
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                var local = socket.LocalEndPoint as IPEndPoint;
                bound = local == null ? port : local.Port;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); } catch (Exception) { }
                }
            }
        }
    }
}
