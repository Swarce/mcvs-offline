using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Tests.Misc
{
    /// <summary>A player as the Federation sees them: account, its own client token and the TS's on-behalf token.</summary>
    public sealed class TestPlayer
    {
        public string Credential;
        public string AccountId;
        public string FedId => McvsConstants.FedIdPrefix + AccountId;

        /// <summary>The client's own token (anonymous credential).</summary>
        public string Token;

        /// <summary>The token a game server got for this player (for_username grant).</summary>
        public string OnBehalfToken;
    }

    /// <summary>
    /// The full server (core + every module, as the hosts run it) in a temporary data directory. Requests go through
    /// <see cref="McvsServer.Handle"/> in-process; <see cref="Start"/> also serves HTTP for the streaming tests.
    /// Tokens are issued directly through the core TokenService, so these tests do not depend on the AUTH module.
    /// </summary>
    public sealed class MiscServer : IDisposable
    {
        public const string SuperCredential = "game:mcvsoffline";

        private readonly bool ownsDir;

        /// <param name="dir">An existing data directory to reuse (restart tests); the caller disposes it.</param>
        public MiscServer(ManualClock clock = null, TempDir dir = null)
        {
            ownsDir = dir == null;
            Dir = dir ?? new TempDir();
            Logs = new MemoryLogSink();
            Clock = clock;
            var options = new ServerOptions
            {
                DataDir = Dir.Combine("data"),
                Port = 0,
                ConsoleLog = false,
                FileLog = false,
                Clock = clock,
            };
            options.ExtraSinks.Add(Logs);
            Server = McvsServer.Create(options);
            GameServer = NewAccountToken(SuperCredential, null);
        }

        public TempDir Dir { get; }

        public MemoryLogSink Logs { get; }

        public ManualClock Clock { get; }

        public McvsServer Server { get; }

        public ServerContext Context => Server.Context;

        /// <summary>The game server's own session (super credential).</summary>
        public TestPlayer GameServer { get; }

        public string BaseUrl => "http://127.0.0.1:" + Server.Port + "/";

        public void Start() => Server.Start();

        public void Set(string setting, object value) => Context.Settings.Override(setting, value);

        public TestPlayer NewPlayer(string username)
        {
            TestPlayer player = NewAccountToken("anonymous:" + username, null);
            player.OnBehalfToken = Context.Tokens.Issue(player.AccountId, player.Credential, McvsConstants.ClientId,
                new[] { "auth", "storage", "message", "message_secured", "alert", "leaderboard", "social", "chat", "social_group" },
                "device", SuperCredential).Token;
            return player;
        }

        private TestPlayer NewAccountToken(string credential, string actor)
        {
            Account account = Context.Accounts.GetOrCreate(credential, "device");
            var player = new TestPlayer { Credential = credential, AccountId = account.Id };
            player.Token = Context.Tokens.Issue(account.Id, credential, McvsConstants.ClientId, new[] { "auth", "lobby", "config" }, "device", actor).Token;
            return player;
        }

        /// <summary>A GET with the token in the query (report 02 §1.4).</summary>
        public HttpResponse Get(string target, string token = null)
        {
            if (token != null) target += (target.Contains("?") ? "&" : "?") + "access_token=" + Uri.EscapeDataString(token);
            return Server.Handle(HttpRequest.Create("GET", target));
        }

        /// <summary>A form POST with the token in the body; <paramref name="form"/> is name, value, name, value...</summary>
        public HttpResponse Post(string target, string token, params string[] form)
        {
            return Server.Handle(HttpRequest.Create("POST", target, Form(token, form)));
        }

        /// <summary>A DELETE as the client sends it: token in the query and every parameter in the body.</summary>
        public HttpResponse Delete(string target, string token, params string[] form)
        {
            string t = token == null ? target : target + (target.Contains("?") ? "&" : "?") + "access_token=" + Uri.EscapeDataString(token);
            return Server.Handle(HttpRequest.Create("DELETE", t, Form(token, form)));
        }

        public static string Form(string token, params string[] form)
        {
            var parts = new List<string>();
            for (int i = 0; i + 1 < form.Length; i += 2)
                parts.Add(Uri.EscapeDataString(form[i]) + "=" + Uri.EscapeDataString(form[i + 1] ?? ""));
            if (token != null) parts.Add("access_token=" + Uri.EscapeDataString(token));
            return string.Join("&", parts);
        }

        public static Dictionary<string, object> Obj(HttpResponse response) => MiniJson.ParseObject(response.BodyText);

        public static List<object> Arr(HttpResponse response) => MiniJson.ParseArray(response.BodyText);

        public void Dispose()
        {
            Server.Dispose();
            if (ownsDir) Dir.Dispose();
        }
    }

    /// <summary>
    /// Reads a streamed (chunked) HTTP response line by line as it arrives, the way the client's StreamReader does
    /// for the alert and chat streams.
    /// </summary>
    public sealed class StreamingResponse : IDisposable
    {
        private readonly TcpClient client;
        private readonly Stream stream;
        private readonly StringBuilder pending = new StringBuilder();
        private readonly byte[] buffer = new byte[8192];
        private bool ended;

        public StreamingResponse(int port, string target)
        {
            client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            stream = client.GetStream();
            byte[] request = Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            stream.Write(request, 0, request.Length);
            ReadHead();
        }

        public int Status { get; private set; }

        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True once the server sent the last chunk.</summary>
        public bool Ended => ended;

        /// <summary>The next body line (without "\n"), or null when the stream ended or nothing came within the timeout.</summary>
        public string ReadLine(int timeoutMs = 5000)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                string text = pending.ToString();
                int nl = text.IndexOf('\n');
                if (nl >= 0)
                {
                    pending.Remove(0, nl + 1);
                    return text.Substring(0, nl);
                }
                if (ended) return null;
                int left = deadline - Environment.TickCount;
                if (left <= 0) return null;
                if (!ReadChunk(left)) return null;
            }
        }

        /// <summary>The next line that is not blank and not an SSE comment.</summary>
        public string ReadDataLine(int timeoutMs = 5000)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (true)
            {
                int left = deadline - Environment.TickCount;
                if (left <= 0) return null;
                string line = ReadLine(left);
                if (line == null) return null;
                if (line.Trim().Length == 0 || line.StartsWith(":", StringComparison.Ordinal)) continue;
                return line;
            }
        }

        public void Dispose() => client.Dispose();

        private void ReadHead()
        {
            client.ReceiveTimeout = 10000;
            string head = ReadRawLine();
            Status = int.Parse(head.Split(' ')[1], CultureInfo.InvariantCulture);
            string line;
            while ((line = ReadRawLine()).Length > 0)
            {
                int colon = line.IndexOf(':');
                Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
        }

        private bool ReadChunk(int timeoutMs)
        {
            client.ReceiveTimeout = Math.Max(1, timeoutMs);
            try
            {
                string sizeLine = ReadRawLine();
                if (sizeLine == null) { ended = true; return false; }
                int size = int.Parse(sizeLine.Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    ReadRawLine();
                    ended = true;
                    return false;
                }
                byte[] data = ReadExact(size);
                ReadRawLine();
                pending.Append(Encoding.UTF8.GetString(data));
                return true;
            }
            catch (IOException)
            {
                return false; // timeout
            }
        }

        private string ReadRawLine()
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = ReadByte();
                if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
            }
        }

        private byte[] ReadExact(int count)
        {
            var result = new byte[count];
            for (int i = 0; i < count; i++)
            {
                int b = ReadByte();
                if (b < 0) throw new IOException("stream closed inside a chunk");
                result[i] = (byte)b;
            }
            return result;
        }

        private int bufferStart;
        private int bufferEnd;

        private int ReadByte()
        {
            if (bufferStart == bufferEnd)
            {
                bufferStart = 0;
                bufferEnd = stream.Read(buffer, 0, buffer.Length);
                if (bufferEnd <= 0)
                {
                    bufferEnd = 0;
                    return -1;
                }
            }
            return buffer[bufferStart++];
        }
    }
}
