using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Auth
{
    /// <summary>
    /// Conformance: the REAL client library (Gameloft.OnlinePlatform.dll and its dependencies from the player's APK,
    /// MCVS_REFS, never committed) talks to the server over HTTP. The library targets mscorlib 4.0 and a
    /// retargetable Newtonsoft PCL, so it runs on net8 in its own load context; only the transport differs from the
    /// device (net8 SocketsHttpHandler instead of Mono's handler, covered by <see cref="AuthWireTests"/>).
    /// Skipped (with a note) when the assemblies are not available.
    /// </summary>
    public class RealClientConformanceTests
    {
        private const string ClientId = FedFederation.GameClientId;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private static readonly Lazy<Assembly> Library = new Lazy<Assembly>(LoadLibrary);

        private readonly ITestOutputHelper output;

        public RealClientConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public static string RefsDirectory =>
            Environment.GetEnvironmentVariable("MCVS_REFS") is string dir && dir.Length > 0 ? dir : "/home/user/mcvs-work/extracted/assemblies";

        [Fact]
        public void AnonymousGameServerOnBehalfAndLocalAnubisSessionsAgree()
        {
            if (!Available()) return;
            using var host = new Host();

            // Client: EN/AuthenticationComponent.cs OpenAnonymousSession.
            dynamic clientFed = host.Federation();
            var deviceArgs = new Dictionary<string, object>
            {
                ["firmware"] = "10", ["country"] = "US", ["carrier"] = "x", ["model"] = "Pixel 3", ["resolution"] = null, ["language"] = "en",
            };
            dynamic client = Await(clientFed.OpenSession(host.Credential("anonymous:realclient"), "anon-pass", "device-1",
                                                         deviceArgs, new List<string>(GameScopes.Client), CancellationToken.None));
            Assert.True((bool)client.IsOpen);
            string fedId = (string)client.FedId;
            Assert.Equal("fed_id:" + AccountStore.DeriveAccountId("anonymous:realclient"), fedId);
            Assert.Equal("None", Await(clientFed.GetEveStatus(CancellationToken.None)).ToString());

            // Game server own session (OpenGameServerSession).
            dynamic gs = Await(host.Federation().OpenSession(host.Credential("game:mcvsoffline"), "mcvsoffline", "device-1", null,
                                                             new List<string>(GameScopes.Server), CancellationToken.None));
            Assert.Equal("fed_id:" + AccountStore.DeriveAccountId("game:mcvsoffline"), (string)gs.FedId);

            // TS handshake: SessionForUser for the client's credential lands on the client's account (verify-C2 §1.5).
            dynamic ts = Await(host.Federation().OpenSessionForUser(host.Credential("game:mcvsoffline"), "mcvsoffline",
                                                                    host.Credential("anonymous:realclient"), "device-1", null,
                                                                    new List<string>(GameScopes.Server), CancellationToken.None));
            Assert.Equal(fedId, (string)ts.FedId);
            dynamic info = Await(ts.GetAccountInfo(CancellationToken.None));
            Assert.Equal("anonymous:realclient", (string)info.Credentials[0].ToString());

            // LocalAnubis: its own Federation forces HTTPS (EN/LocalAnubisManager.cs); explicit ports keep http.
            dynamic anubisFed = host.Federation(protocol: "HTTPS");
            dynamic anubis = Await(anubisFed.OpenSession(host.Credential("game:mcvsoffline"), "mcvsoffline", "default", null,
                                                         new List<string>(GameScopes.LocalAnubis), CancellationToken.None));
            Assert.True((bool)anubis.IsOpen);
            Assert.StartsWith("http://127.0.0.1:" + host.Server.Port, (string)Await(anubisFed.LocateService(host.Service("auth"), host.Protocol("HTTPS"), CancellationToken.None)));

            // Lobby hand-off: encrypt_token (client) -> verify (Controller) -> RoomUser.FromAccountInfo must not throw.
            string encrypted = Await(client.EncryptedAccessToken("LocalAnubisNonce", "lobby", CancellationToken.None));
            dynamic login = Await(anubis.Verify(encrypted, "LocalAnubisNonce", host.Credential("system:Anubis"), CancellationToken.None));
            Assert.Equal(fedId, "fed_id:" + (string)login["account"].ToString());
            dynamic roomUser = host.Type("Gameloft.OnlinePlatform.RoomUser").GetMethod("FromAccountInfo").Invoke(null, new object[] { login });
            Assert.NotNull(roomUser);

            // Finder ParseUser: verify of the raw lobby token.
            string lobbyToken = Await(client.GetAccessToken(new List<string> { "lobby" }, true, null, CancellationToken.None));
            dynamic finder = Await(anubis.Verify(lobbyToken, CancellationToken.None));
            Assert.Equal((string)login["account"].ToString(), (string)finder["account"].ToString());
            Assert.Equal(ClientId, (string)finder["client_id"].ToString());
        }

        [Fact]
        public void FirstRunProfileCallsSucceed()
        {
            if (!Available()) return;
            using var host = new Host();
            dynamic client = Await(host.Federation().OpenSession(host.Credential("anonymous:firstrun"), "pw", "device-9", null,
                                                                 new List<string>(GameScopes.Client), CancellationToken.None));
            dynamic ts = Await(host.Federation().OpenSessionForUser(host.Credential("game:mcvsoffline"), "mcvsoffline",
                                                                    host.Credential("anonymous:firstrun"), "device-9", null,
                                                                    new List<string>(GameScopes.Server), CancellationToken.None));

            // GM/GameProfileSection.cs CreateProfile: an array, empty (no account merge).
            dynamic accounts = Await(ts.GetAccountsForDeviceId("device-9", false, CancellationToken.None));
            Assert.Equal(0, (int)accounts.Count);
            Assert.False((bool)Await(ts.HasAccountsForDeviceId("device-9", true, CancellationToken.None)));

            // InitPlayerName: CreateAlias, then friend lookup.
            string alias = Await(ts.CreateAlias(CancellationToken.None));
            Assert.Matches("^[1-9][0-9]{8}$", alias);
            Assert.Equal(alias, (string)Await(client.CreateAlias(CancellationToken.None)));
            Assert.Equal((string)client.FedId, Await(client.LookupAlias(alias, CancellationToken.None)).ToString());

            // AgeGateChecker: every compliance compliant.
            dynamic compliances = Await(ts.AddCompliances(ts.FedId, "US", 15, CancellationToken.None));
            int count = 0;
            foreach (dynamic pair in (IEnumerable)compliances)
            {
                Assert.True((bool)pair.Value.Compliant);
                count++;
            }
            Assert.True(count > 0);

            // SocialComponent: users/{credential} answers 404 for an unknown account (caught by status code).
            var failure = Assert.ThrowsAny<Exception>(() => Await(client.GetAccountInfo(host.Credential("facebook:1"), CancellationToken.None, true)));
            Assert.Equal(404, StatusOf(failure));
            Assert.True((bool)Await(host.Federation().CheckIfUserExists(CancellationToken.None, host.Credential("anonymous:firstrun"))));
        }

        [Fact]
        public void TransferCodeRoundTrip()
        {
            if (!Available()) return;
            using var host = new Host();
            dynamic oldDevice = Await(host.Federation().OpenSession(host.Credential("anonymous:olddevice"), "pw", "d1", null,
                                                                    new List<string>(GameScopes.Client), CancellationToken.None));
            dynamic code = Await(oldDevice.GetTransferCode(CancellationToken.None));
            Assert.Matches("^[A-Z2-9]{10}$", (string)code.Code);
            Assert.True((DateTime)code.Expiration > DateTime.UtcNow);

            dynamic newDevice = Await(host.Federation().OpenSession(host.Credential("anonymous:newdevice"), "pw", "d2", null,
                                                                    new List<string>(GameScopes.Client), CancellationToken.None));
            string token = Await(newDevice.AuthorizeTransferCode((string)code.Code, CancellationToken.None));
            Assert.Equal(AccountStore.DeriveAccountId("anonymous:olddevice"), host.Server.Context.Tokens.Resolve(token).AccountId);
        }

        /// <summary>
        /// The server restarts without its tokens.json and accounts.json (a wiped data folder, a new LAN host): the
        /// client's cached token gets a 401, the client drops its tokens, re-authorizes once and retries
        /// (OP/Federation.cs Inspect); the account and its alias come back identical because both are derived.
        /// </summary>
        [Fact]
        public void LostTokensRecoverThroughTheClientsOwn401Handling()
        {
            if (!Available()) return;
            using var host = new Host();
            dynamic client = Await(host.Federation().OpenSession(host.Credential("anonymous:restart"), "pw", "d1", null,
                                                                 new List<string>(GameScopes.Client), CancellationToken.None));
            string alias = Await(client.CreateAlias(CancellationToken.None));

            host.RestartWithoutAuthState();

            Assert.Equal(alias, (string)Await(client.CreateAlias(CancellationToken.None)));
            Assert.Equal(1, host.Server.Context.Tokens.Count); // one new token, for the scope set "auth"
        }

        // ------------------------------------------------------------------------------------------ plumbing

        private bool Available()
        {
            if (File.Exists(Path.Combine(RefsDirectory, "Gameloft.OnlinePlatform.dll"))) return true;
            if (Conformance.GameClient.Required) throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but Gameloft.OnlinePlatform.dll is not in " + RefsDirectory);
            output.WriteLine("Gameloft.OnlinePlatform.dll not found in MCVS_REFS (" + RefsDirectory + "): skipped");
            return false;
        }

        private static dynamic Await(object task)
        {
            var t = (Task)task;
            Assert.True(t.Wait(Timeout), "the client request timed out");
            return t.GetType().GetProperty("Result")?.GetValue(t);
        }

        private static int StatusOf(Exception failure)
        {
            for (Exception e = failure; e != null; e = e.InnerException)
            {
                PropertyInfo responseProperty = e.GetType().GetProperty("Response");
                if (responseProperty?.GetValue(e) is System.Net.Http.HttpResponseMessage response) return (int)response.StatusCode;
            }
            return 0;
        }

        private static Assembly LoadLibrary()
        {
            var context = new GameLoadContext(RefsDirectory);
            return context.LoadFromAssemblyName(new AssemblyName("Gameloft.OnlinePlatform"));
        }

        /// <summary>A server on an ephemeral port plus factories for the client's own types.</summary>
        private sealed class Host : IDisposable
        {
            private readonly TempDir dir = new TempDir();

            public Host()
            {
                Start(0);
            }

            public McvsServer Server { get; private set; }

            /// <summary>Stops, deletes tokens.json and accounts.json, and starts again on the same port.</summary>
            public void RestartWithoutAuthState()
            {
                int port = Server.Port;
                Server.Dispose();
                File.Delete(dir.Combine("data", TokenService.FileName));
                File.Delete(dir.Combine("data", AccountStore.FileName));
                Start(port);
            }

            private void Start(int port)
            {
                Server = McvsServer.Create(new ServerOptions { DataDir = dir.Combine("data"), Port = port, ConsoleLog = false, FileLog = false });
                Server.Start();
            }

            public string Eve => "http://127.0.0.1:" + Server.Port + "/";

            public Type Type(string name) => Library.Value.GetType(name, true);

            public dynamic Protocol(string name) => Enum.Parse(Type("Gameloft.OnlinePlatform.Federation").GetNestedType("Protocol"), name);

            /// <summary>new Federation(clientId, eve, datacenter, 60000, 3, protocol), as OnlinePlatformComponent and LocalAnubisManager build it.</summary>
            public dynamic Federation(string datacenter = null, string protocol = "Unspecified") =>
                Activator.CreateInstance(Type("Gameloft.OnlinePlatform.Federation"), ClientId, Eve, datacenter, 60000, 3, Protocol(protocol));

            public dynamic Credential(string text) =>
                Type("Gameloft.OnlinePlatform.Credential").GetConstructor(new[] { typeof(string) }).Invoke(new object[] { text });

            public dynamic Service(string name) =>
                Type("Gameloft.OnlinePlatform.OnlineService").GetMethod("FromName").Invoke(null, new object[] { name });

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }
        }

        /// <summary>Loads Gameloft.* and their Newtonsoft copy from MCVS_REFS; the BCL comes from net8 (never Mono's copies).</summary>
        private sealed class GameLoadContext : AssemblyLoadContext
        {
            private readonly string directory;

            public GameLoadContext(string directory) : base("mcvs-game-client")
            {
                this.directory = directory;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal)
                    || simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
