using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// Minimal HTTP/1.1 server on a raw listening socket, one background thread per connection.
    /// <para>
    /// Why not HttpListener: Mono's HttpListener rejects requests whose Host header does not match a "localhost"
    /// prefix (verify-C6 §2.2), which breaks LAN guests. Why raw Socket instead of TcpListener: the game's trimmed
    /// System.dll has no TcpListener.AcceptTcpClient/Server. Threads (not async): the shipped mscorlib cannot
    /// compile async/await (ARCHITECTURE §2.1), and long-lived streams (alerts) hold a connection anyway.
    /// </para>
    /// Supports keep-alive and pipelining, Content-Length and chunked request bodies, Expect: 100-continue,
    /// streamed (chunked) responses and HEAD. A handler exception becomes a 500; nothing escapes a thread
    /// (an unhandled exception on a thread would kill the game process in the embedded build).
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private readonly IPAddress address;
        private readonly int requestedPort;
        private readonly Func<HttpRequest, HttpResponse> handler;
        private readonly Log log;
        private readonly HttpServerLimits limits;
        private readonly ManualResetEvent stopSignal = new ManualResetEvent(false);
        private readonly object gate = new object();
        private readonly HashSet<Connection> connections = new HashSet<Connection>();

        private Socket listener;
        private Thread acceptThread;
        private ManualResetEvent acceptThreadDone;
        private long nextRequestId;
        private long nextConnectionId;
        private volatile bool stopping;

        public HttpServer(IPAddress address, int port, Func<HttpRequest, HttpResponse> handler, Log log, HttpServerLimits limits = null)
        {
            if (address == null) throw new ArgumentNullException("address");
            if (handler == null) throw new ArgumentNullException("handler");
            this.address = address;
            requestedPort = port;
            this.handler = handler;
            this.log = log;
            this.limits = limits ?? new HttpServerLimits();
        }

        /// <summary>The bound port (resolves port 0 to the actual ephemeral port after <see cref="Start"/>).</summary>
        public int Port { get; private set; }

        public IPEndPoint LocalEndPoint { get; private set; }

        public bool IsRunning { get { return listener != null && !stopping; } }

        /// <summary>Signalled when <see cref="Stop"/> begins; streaming handlers watch it.</summary>
        public WaitHandle StopSignal { get { return stopSignal; } }

        public int ActiveConnections
        {
            get { lock (gate) return connections.Count; }
        }

        /// <summary>Binds and starts accepting. Throws SocketException if the port is in use.</summary>
        public void Start()
        {
            lock (gate)
            {
                if (listener != null) throw new InvalidOperationException("server already started");
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.Equals(IPAddress.IPv6Any))
                        socket.DualMode = true;
#if MCVS_EMBEDDED
                    // SO_REUSEADDR alone, through libc: a restarted game can bind again while its previous connections
                    // sit in TIME_WAIT (up to 60 s on Linux) instead of failing to start the server. Not
                    // SetSocketOption(ReuseAddress): Mono also sets SO_REUSEPORT then, and a second listener of the same
                    // UID could share the port (see SocketReuse).
                    string reuse = SocketReuse.TrySetReuseAddressOnly(socket);
                    if (reuse != null) Warn("SO_REUSEADDR not set (" + reuse + "); right after a restart the port may stay busy for up to a minute", null);
#endif
                    // net8: deliberately not set. .NET on Unix already enables SO_REUSEADDR for TCP before bind, and its
                    // ReuseAddress option additionally sets SO_REUSEPORT, which lets a second host bind the same port
                    // and silently take half of the connections (MISC request). HttpServerTests pins the net8 behaviour,
                    // SocketReuseTests the libc call the embedded build makes. On Windows the flag would allow two
                    // servers on one port as well.
                    socket.Bind(new IPEndPoint(address, requestedPort));
                    socket.Listen(128);
                }
                catch
                {
                    socket.Close();
                    throw;
                }
                listener = socket;
                LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;
                Port = LocalEndPoint.Port;
                stopping = false;
                stopSignal.Reset();
                acceptThreadDone = new ManualResetEvent(false);
                acceptThread = new Thread(AcceptLoop);
                acceptThread.IsBackground = true;
                acceptThread.Name = "mcvs-http-accept";
                acceptThread.Start();
            }
            Info("listening on " + LocalEndPoint);
        }

        /// <summary>Stops accepting, closes every connection and waits (bounded) for the threads to finish.</summary>
        public void Stop(int timeoutMs = 5000)
        {
            Socket l;
            List<Connection> open;
            lock (gate)
            {
                if (listener == null || stopping) return;
                stopping = true;
                stopSignal.Set();
                l = listener;
                open = new List<Connection>(connections);
            }
            try { l.Close(); } catch (Exception) { }
            foreach (var c in open) c.Abort();

            int deadline = Environment.TickCount + timeoutMs;
            if (acceptThreadDone != null) acceptThreadDone.WaitOne(Num.Max(0, timeoutMs));
            foreach (var c in open)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0) break;
                c.Done.WaitOne(left);
            }
            lock (gate)
            {
                listener = null;
            }
            Info("stopped");
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            try
            {
                while (!stopping)
                {
                    Socket client;
                    try
                    {
                        // Poll first so the loop notices Stop() even where Close() does not interrupt Accept().
                        if (!listener.Poll(250 * 1000, SelectMode.SelectRead)) continue;
                        client = listener.Accept();
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        if (stopping) break;
                        Warn("accept failed: " + ex.Message, null);
                        Thread.Sleep(50);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        if (stopping) break;
                        Warn("accept failed", ex);
                        Thread.Sleep(50);
                        continue;
                    }
                    StartConnection(client);
                }
            }
            catch (Exception ex)
            {
                Warn("accept loop terminated", ex);
            }
            finally
            {
                acceptThreadDone.Set();
            }
        }

        private void StartConnection(Socket socket)
        {
            Connection connection;
            lock (gate)
            {
                if (stopping || connections.Count >= limits.MaxConnections)
                {
                    RejectBusy(socket);
                    return;
                }
                connection = new Connection(socket, Interlocked.Increment(ref nextConnectionId));
                connections.Add(connection);
            }
            try
            {
                var thread = new Thread(() => Serve(connection));
                thread.IsBackground = true;
                thread.Name = "mcvs-http-" + connection.Id;
                thread.Start();
            }
            catch (Exception ex)
            {
                Warn("could not start a connection thread", ex);
                lock (gate) connections.Remove(connection);
                connection.Abort();
                connection.Done.Set();
            }
        }

        private void RejectBusy(Socket socket)
        {
            try
            {
                using (var stream = new NetworkStream(socket, true))
                {
                    stream.WriteTimeout = 1000;
                    HttpResponseWriter.WriteError(stream, HttpStatus.ServiceUnavailable, "server busy");
                }
            }
            catch (Exception)
            {
                try { socket.Close(); } catch (Exception) { }
            }
        }

        private void Serve(Connection connection)
        {
            Socket socket = connection.Socket;
            try
            {
                socket.NoDelay = true;
                IPEndPoint remote = socket.RemoteEndPoint as IPEndPoint;
                IPEndPoint local = socket.LocalEndPoint as IPEndPoint;
                using (var stream = new NetworkStream(socket, true))
                {
                    stream.ReadTimeout = limits.IdleTimeoutMs;
                    stream.WriteTimeout = limits.WriteTimeoutMs;
                    var reader = new HttpConnectionReader(stream);
                    while (!stopping)
                    {
                        HttpRequest request;
                        try
                        {
                            request = HttpRequestParser.Read(reader, stream, limits, remote, local);
                        }
                        catch (HttpProtocolException ex)
                        {
                            Warn("bad request from " + remote + ": " + ex.Message + " -> " + ex.Status, null);
                            HttpResponseWriter.WriteError(stream, ex.Status, ex.Message);
                            break;
                        }
                        if (request == null) break; // client closed the connection

                        request.Id = Interlocked.Increment(ref nextRequestId);
                        HttpResponse response;
                        try
                        {
                            response = handler(request) ?? HttpResponse.Empty(HttpStatus.NoContent);
                        }
                        catch (Exception ex)
                        {
                            Error("handler failed for " + request, ex);
                            response = HttpResponse.Error(HttpStatus.InternalServerError, "internal error");
                        }

                        bool keepAlive = request.KeepAlive && !response.CloseConnection && !stopping;
                        if (!HttpResponseWriter.Write(stream, request, response, keepAlive, stopSignal)) break;
                    }
                }
            }
            catch (IOException)
            {
                // Idle timeout, reset or client gone: normal end of a connection.
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Warn("connection " + connection.Id + " failed", ex);
            }
            finally
            {
                connection.Abort();
                lock (gate) connections.Remove(connection);
                connection.Done.Set();
            }
        }

        private void Info(string message)
        {
            if (log != null) log.Info(message);
        }

        private void Warn(string message, Exception ex)
        {
            if (log != null) log.Warn(message, ex);
        }

        private void Error(string message, Exception ex)
        {
            if (log != null) log.Error(message, ex);
        }

        private sealed class Connection
        {
            public readonly Socket Socket;
            public readonly long Id;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            private int aborted;

            public Connection(Socket socket, long id)
            {
                Socket = socket;
                Id = id;
            }

            public void Abort()
            {
                if (Interlocked.Exchange(ref aborted, 1) != 0) return;
                try { Socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
                try { Socket.Close(); } catch (Exception) { }
            }
        }
    }
}
