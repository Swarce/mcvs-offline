using System;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;

namespace McvsOffline.Tests.Auth
{
    /// <summary>
    /// The composed server (core + every module, as the hosts run it) on a temporary data directory, driven
    /// in-process through <see cref="McvsServer.Handle"/>. Its base URL is http://127.0.0.1:51780/ (the default port),
    /// which is what eve and pandora hand out. <see cref="Restart"/> reopens the same data directory.
    /// </summary>
    public sealed class AuthTestServer : IDisposable
    {
        public const string Eve = "http://127.0.0.1:51780/";

        private readonly TempDir dir = new TempDir();
        private readonly Action<Router, ServerContext> register;
        private readonly IClock clock;

        public AuthTestServer(Action<Router, ServerContext> register = null, IClock clock = null)
        {
            this.register = register;
            this.clock = clock;
            Start();
        }

        public McvsServer Server { get; private set; }

        public ServerContext Context => Server.Context;

        public MemoryLogSink Logs { get; private set; }

        public string DataDir => dir.Combine("data");

        public HttpResponse Handle(HttpRequest request) => Server.Handle(request);

        public HttpResponse Get(string target) => Handle(HttpRequest.Create("GET", target));

        public HttpResponse Post(string target, string form) => Handle(HttpRequest.Create("POST", target, form));

        /// <summary>A client-side Federation pointed at this server (eve URL as passed by args.txt).</summary>
        public FedFederation Federation(string datacenter = null, string protocol = null) =>
            new FedFederation(Handle, Eve, datacenter, protocol);

        /// <summary>The player's own session: anonymous credential, client scopes, device args (EN/AuthenticationComponent.cs OpenAnonymousSession).</summary>
        public FedSession OpenClient(string username = "player1", string deviceId = "device-1", FedFederation federation = null)
        {
            var session = new FedSession(federation ?? Federation(), "anonymous:" + username, "anon-pass", deviceId)
            {
                DeviceArgs = GameScopes.DeviceArgs(),
            };
            session.Open(GameScopes.Client);
            return session;
        }

        /// <summary>The TS/GS own session (OpenGameServerSession): super credential, server scopes, no device args.</summary>
        public FedSession OpenGameServer(string deviceId = "device-1", FedFederation federation = null)
        {
            var session = new FedSession(federation ?? Federation(), Settings.DefaultSuperCredential, Settings.DefaultSuperPassword, deviceId);
            session.Open(GameScopes.Server);
            return session;
        }

        /// <summary>The TS handshake's per-player session (Connection.ProcessHandshake → OpenSessionForUser).</summary>
        public FedSessionForUser OpenForUser(string forUser, string deviceId = "device-1", FedFederation federation = null)
        {
            var session = new FedSessionForUser(federation ?? Federation(), Settings.DefaultSuperCredential, Settings.DefaultSuperPassword,
                                                forUser, deviceId);
            session.Open(GameScopes.Server);
            return session;
        }

        public void Restart()
        {
            Server.Dispose();
            Start();
        }

        public void Dispose()
        {
            Server.Dispose();
            dir.Dispose();
        }

        private void Start()
        {
            Logs = new MemoryLogSink();
            var options = new ServerOptions { DataDir = DataDir, ConsoleLog = false, FileLog = false, Clock = clock };
            options.ExtraSinks.Add(Logs);
            if (register != null) options.RegisterServices = register;
            Server = McvsServer.Create(options);
        }
    }
}
