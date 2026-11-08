// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Embedded
{
    /// <summary>What answered on a busy port (<see cref="PortProbe.Probe"/>).</summary>
    public enum PortHolderKind
    {
        /// <summary>The connection was refused: the holder is bound but not listening (or it went away meanwhile).</summary>
        Refused,

        /// <summary>The holder accepted and closed (or reset) the connection without answering: e.g. an adb reverse with no PC server behind it.</summary>
        Closed,

        /// <summary>The holder accepted the connection (or let it wait) and never answered: e.g. a frozen earlier copy of the game.</summary>
        Silent,

        /// <summary>The holder answered, but not as MCVS Encore.</summary>
        Foreign,

        /// <summary>A working MCVS Encore server answered GET /_mcvs/status.</summary>
        Mcvs,
    }

    /// <summary>The result of <see cref="PortProbe.Probe"/>.</summary>
    public sealed class PortHolder
    {
        public PortHolder(PortHolderKind kind, string detail)
        {
            Kind = kind;
            Detail = detail ?? "";
        }

        public PortHolderKind Kind { get; private set; }

        /// <summary>What was seen, in a few words ("it accepted the connection but sent nothing in 500 ms").</summary>
        public string Detail { get; private set; }

        /// <summary>MCVS Encore only: the server's version, data directory and instance id (null when it did not say).</summary>
        public string Version { get; internal set; }

        public string DataDir { get; internal set; }

        public string Instance { get; internal set; }

        public int ElapsedMs { get; internal set; }

        public override string ToString()
        {
            return Kind + ": " + Detail;
        }
    }

    /// <summary>
    /// Asks whoever holds 127.0.0.1:&lt;port&gt; what it is, with a hard time budget (docs/design/port-fallback.md): a raw socket
    /// connect (at most <c>connectMs</c>; a frozen process with a full backlog never completes it), then
    /// <c>GET /_mcvs/status HTTP/1.0</c> with the Host header the access gate wants, and at most <c>readMs</c> (in total) and
    /// 16 KiB of answer. No TcpClient, no HttpClient, no async: the game's trimmed System.dll and C# 7.3. Runs on the thread that
    /// starts the game (the Android UI thread, the PC boot thread), so it is only ever called for a port that is busy. Never
    /// throws.
    /// </summary>
    public static class PortProbe
    {
        public const int DefaultConnectMs = 250;
        public const int DefaultReadMs = 500;
        public const int MaxAnswerBytes = 16 * 1024;

        /// <summary>The value of "server" in an MCVS Encore /_mcvs/status answer (CoreRoutes).</summary>
        public const string ServerName = "mcvs-offline";

        public static PortHolder Probe(int port, int connectMs, int readMs)
        {
            int started = Environment.TickCount;
            PortHolder holder;
            try
            {
                holder = ProbeCore(port, connectMs < 1 ? 1 : connectMs, readMs < 1 ? 1 : readMs);
            }
            catch (Exception ex)
            {
                holder = new PortHolder(PortHolderKind.Silent, "the probe failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
            holder.ElapsedMs = Environment.TickCount - started;
            return holder;
        }

        private static PortHolder ProbeCore(int port, int connectMs, int readMs)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IAsyncResult connecting = socket.BeginConnect(new IPEndPoint(IPAddress.Loopback, port), null, null);
                if (!connecting.AsyncWaitHandle.WaitOne(connectMs, false))
                    return new PortHolder(PortHolderKind.Silent, "it did not even accept a connection within " + connectMs + " ms");
                try
                {
                    socket.EndConnect(connecting);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                        return new PortHolder(PortHolderKind.Refused, "it refused the connection (bound but not listening, or just gone)");
                    return new PortHolder(PortHolderKind.Closed, "the connection failed (" + ex.SocketErrorCode + ")");
                }

                int deadline = Environment.TickCount + readMs;
                socket.SendTimeout = readMs;
                string host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                byte[] request = Ascii("GET /_mcvs/status HTTP/1.0\r\nHost: " + host + "\r\nAccept: application/json\r\n" +
                                       "User-Agent: mcvs-offline-port-probe\r\nConnection: close\r\n\r\n");
                try
                {
                    socket.Send(request, 0, request.Length, SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    return new PortHolder(PortHolderKind.Closed, "it accepted the connection and dropped it (" + ex.SocketErrorCode + ")");
                }

                var answer = new List<byte>();
                var buffer = new byte[4096];
                bool closed = false;
                string failure = null;
                while (answer.Count < MaxAnswerBytes)
                {
                    int left = deadline - Environment.TickCount;
                    if (left <= 0) break;
                    socket.ReceiveTimeout = left;
                    int read;
                    try
                    {
                        read = socket.Receive(buffer, 0, Math.Min(buffer.Length, MaxAnswerBytes - answer.Count), SocketFlags.None);
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode != SocketError.TimedOut && ex.SocketErrorCode != SocketError.WouldBlock) failure = ex.SocketErrorCode.ToString();
                        break;
                    }
                    if (read <= 0)
                    {
                        closed = true;
                        break;
                    }
                    for (int i = 0; i < read; i++) answer.Add(buffer[i]);
                }
                if (answer.Count == 0)
                {
                    if (failure != null) return new PortHolder(PortHolderKind.Closed, "it accepted the connection and reset it (" + failure + ")");
                    if (closed) return new PortHolder(PortHolderKind.Closed, "it accepted the connection and closed it without answering");
                    return new PortHolder(PortHolderKind.Silent, "it accepted the connection but sent nothing in " + readMs + " ms");
                }
                return Classify(Encoding.UTF8.GetString(answer.ToArray()));
            }
            finally
            {
                try { socket.Close(); } catch (Exception) { }
            }
        }

        /// <summary>An answer (status line, headers, body as far as it came) → Mcvs or Foreign.</summary>
        internal static PortHolder Classify(string answer)
        {
            if (!answer.StartsWith("HTTP/", StringComparison.Ordinal))
                return new PortHolder(PortHolderKind.Foreign, "it answered, but not HTTP");
            int lineEnd = answer.IndexOf('\n');
            string status = (lineEnd < 0 ? answer : answer.Substring(0, lineEnd)).Trim();
            if (status.Length > 40) status = status.Substring(0, 40);
            int body = answer.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            object parsed;
            if (body >= 0 && MiniJson.TryParse(answer.Substring(body + 4), out parsed))
            {
                var json = parsed as Dictionary<string, object>;
                object server;
                if (json != null && json.TryGetValue("server", out server) && string.Equals(server as string, ServerName, StringComparison.Ordinal))
                {
                    var holder = new PortHolder(PortHolderKind.Mcvs, "MCVS Encore " + (Text(json, "version") ?? "?") + " answers (data directory " +
                                                                     (Text(json, "dataDir") ?? "?") + ")");
                    holder.Version = Text(json, "version");
                    holder.DataDir = Text(json, "dataDir");
                    holder.Instance = Text(json, "instance");
                    return holder;
                }
            }
            return new PortHolder(PortHolderKind.Foreign, "it answered '" + status + "', not as MCVS Encore");
        }

        private static string Text(Dictionary<string, object> json, string key)
        {
            object value;
            return json.TryGetValue(key, out value) ? value as string : null;
        }

        private static byte[] Ascii(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)(text[i] < 128 ? text[i] : '?');
            return bytes;
        }
    }
}
