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

        /// <summary>The configured port is busy and its holder is not MCVS Encore: another port is bound for this start.</summary>
        Fallback,

        /// <summary>A working MCVS Encore server answers on the configured port (a standalone through adb reverse...): the game uses it.</summary>
        Deferred,

        /// <summary>Nothing could be bound (or the fallback is off and the port is busy): the server thread retries the configured port.</summary>
        Failed,
    }

    /// <summary>
    /// The port fallback (docs/design/port-fallback.md): reserves the server's listening socket synchronously, before args.txt
    /// is written, so every place that points the game at the server can use the port actually bound.
    /// <list type="number">
    /// <item>Bind the configured port (normal case: one bind, well under a millisecond).</item>
    /// <item>Busy (EADDRINUSE): ask the holder what it is (<see cref="PortProbe"/>, at most about 0.75 s). A working MCVS Encore
    /// server: use it (<see cref="PortOutcome.Deferred"/>, never two servers on one port or, for another copy of the game, on one
    /// data folder). A silent holder while <see cref="StartMarker"/> says a start of this game on the same folder claimed the
    /// port moments ago: probe again for up to <see cref="StartingWaitMs"/> (a second copy started at the same moment).
    /// Anything else: try the configured port <see cref="Rebinds"/> more times (a dying process lets it go).</item>
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
        /// How long a silent holder is given to start answering when a start of this game on the same data folder claimed the
        /// port moments ago (<see cref="StartMarker"/>), and the pause between the probes meanwhile.
        /// </summary>
        public const int StartingWaitMs = 2000;

        public const int StartingPollMs = 100;

        /// <summary>
        /// Reserves the server port. <paramref name="listen"/> binds and listens (HttpServer.Listen; tests inject failures),
        /// <paramref name="probe"/> asks a holder (PortProbe.Probe). Never throws for socket errors.
        /// </summary>
        public static PortReservation Reserve(IPAddress address, int configured, bool allowFallback,
                                              Func<IPAddress, int, Socket> listen, Func<int, PortHolder> probe)
        {
            Func<IPAddress, int, Action<string>, Socket> bind = null;
            if (listen != null) bind = (a, p, w) => listen(a, p);
            return Reserve(address, configured, allowFallback, bind, probe, null, null);
        }

        /// <summary>
        /// Reserves the server port. <paramref name="listen"/> binds and listens with a warning callback (HttpServer.Listen: the
        /// Android build's "SO_REUSEADDR not set" line; tests inject failures), <paramref name="probe"/> asks a holder
        /// (PortProbe.Probe), <paramref name="warn"/> gets that warning once (the bootstrap log; may be null), and
        /// <paramref name="startingHere"/> says whether a start of this game on the same data folder claimed the port moments ago
        /// and its server is not up yet (<see cref="StartMarker.IsStarting"/>; may be null): a silent holder is then given
        /// <see cref="StartingWaitMs"/> to answer before the fallback. Never throws for socket errors.
        /// </summary>
        public static PortReservation Reserve(IPAddress address, int configured, bool allowFallback,
                                              Func<IPAddress, int, Action<string>, Socket> listen, Func<int, PortHolder> probe,
                                              Action<string> warn, Func<int, bool> startingHere)
        {
            if (address == null) throw new ArgumentNullException("address");
            Func<IPAddress, int, Action<string>, Socket> bindAndListen = listen ?? ((a, p, w) => HttpServer.Listen(a, p, w));
            // The reason is the same for every socket: logged once, not once per port tried.
            bool warned = false;
            Action<string> warnOnce = message =>
            {
                if (warned || warn == null) return;
                warned = true;
                try { warn(message); } catch (Exception) { }
            };
            Func<IPAddress, int, Socket> listenOne = (a, p) => bindAndListen(a, p, warnOnce);
            probe = probe ?? (p => PortProbe.Probe(p, PortProbe.DefaultConnectMs, PortProbe.DefaultReadMs));
            var r = new PortReservation { ConfiguredPort = configured, Port = configured };
            return r.Run(address, allowFallback, listenOne, probe, startingHere);
        }

        private PortReservation Run(IPAddress address, bool allowFallback, Func<IPAddress, int, Socket> listen,
                                    Func<int, PortHolder> probe, Func<int, bool> startingHere)
        {
            int configured = ConfiguredPort;
            PortReservation r = this;

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
                string waited = null;
                if (holder.Kind == PortHolderKind.Silent && IsStartingHere(startingHere, configured))
                {
                    // Another copy of the game on this data folder reserved the port moments ago and is still building its server
                    // (it binds in Prepare and answers only once McvsServer.Create and Start have run): give it time to answer
                    // rather than starting a second server on the same folder.
                    int started = Environment.TickCount;
                    holder = WaitForStartingServer(probe, configured, holder);
                    waited = Num(Environment.TickCount - started);
                }
                r.Holder = holder;
                if (holder.Kind == PortHolderKind.Mcvs)
                {
                    r.Outcome = PortOutcome.Deferred;
                    r.Reason = "port " + Num(configured) + " is held by a working MCVS Encore server (" + holder.Detail + ")" +
                               (waited == null ? "" : ", which was still starting (it answered after " + waited + " ms)");
                    return r;
                }
                r.Reason = "port " + Num(configured) + " is held by another program that does not answer as MCVS Encore: " + holder.Detail +
                           (waited == null ? "" : " (a start of this game on the same data folder claimed the port moments ago; waited " + waited +
                                                  " ms for its server to answer)");
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

        /// <summary>
        /// The deferred server stopped and the configured port could be bound (<paramref name="socket"/>): this start's own server
        /// takes it over. Port and args.txt stay the configured ones, so the game's next requests reach the new server.
        /// </summary>
        internal void TakeOver(Socket socket)
        {
            if (socket == null) throw new ArgumentNullException("socket");
            Holder = null;
            Reason = null;
            Bound(PortOutcome.Configured, socket);
        }

        private static bool IsStartingHere(Func<int, bool> startingHere, int port)
        {
            if (startingHere == null) return false;
            try
            {
                return startingHere(port);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Probes again until the holder answers as MCVS Encore, stops being silent, or <see cref="StartingWaitMs"/> is over.</summary>
        private static PortHolder WaitForStartingServer(Func<int, PortHolder> probe, int port, PortHolder first)
        {
            int started = Environment.TickCount;
            PortHolder last = first;
            while (Environment.TickCount - started < StartingWaitMs)
            {
                Thread.Sleep(StartingPollMs);
                PortHolder next = probe(port) ?? new PortHolder(PortHolderKind.Silent, "unknown");
                last = next;
                if (next.Kind != PortHolderKind.Silent) break;
            }
            return last;
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
    /// logs/server-start.txt (docs/design/port-fallback.md §2): which port this game's latest start reserved for its server, and
    /// whether that server is up yet. A second copy of the game started at the same moment on the same data folder finds the port
    /// busy while the first copy is still building its server (it binds in Prepare, before McvsServer.Create and Start, and
    /// answers nothing until then); the marker tells it to wait for that server instead of falling back and running a second
    /// server on the same folder. A frozen copy wrote it long ago or already wrote "running", so it costs nothing then.
    /// Three lines (<c>port=51780</c>, <c>state=starting</c>, <c>utcTicks=...</c>). Best effort: never throws.
    /// </summary>
    public static class StartMarker
    {
        public const string FileName = "server-start.txt";

        /// <summary>A "starting" marker older than this is stale (a start that crashed or froze before its server was up).</summary>
        public const int StartingWindowMs = 10000;

        public const string Starting = "starting";
        public const string Running = "running";
        public const string Stopped = "stopped";

        public static void Write(string path, int port, string state, DateTime utcNow)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                string text = "port=" + port.ToString(CultureInfo.InvariantCulture) + "\nstate=" + state + "\nutcTicks=" +
                              utcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\n";
                System.IO.File.WriteAllText(path, text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>True when the marker says a start reserved <paramref name="port"/> less than StartingWindowMs ago and its server is not up yet.</summary>
        public static bool IsStarting(string path, int port, DateTime utcNow)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return false;
                int markedPort = -1;
                string state = null;
                long ticks = -1;
                foreach (string raw in System.IO.File.ReadAllText(path).Split('\n'))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq);
                    string value = line.Substring(eq + 1);
                    if (key == "port") int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out markedPort);
                    else if (key == "state") state = value;
                    else if (key == "utcTicks") long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
                }
                if (markedPort != port || state != Starting || ticks <= 0) return false;
                double ageMs = (utcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalMilliseconds;
                return ageMs >= -1000 && ageMs < StartingWindowMs;
            }
            catch (Exception)
            {
                return false;
            }
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
