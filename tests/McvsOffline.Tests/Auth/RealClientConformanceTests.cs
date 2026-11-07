using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core;
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
    /// Skipped (with a note) when the assemblies are not available. Every test runs for each folder of
    /// <see cref="TestFolders.RealClientFolders"/>: the APK's (MCVS_REFS) and the Steam install's (MCVS_PC_REFS, whose
    /// client sends the Steam client id; docs/design/pc-port.md §4.7).
    /// </summary>
    public class RealClientConformanceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>One load context per folder.</summary>
        private static readonly ConcurrentDictionary<string, Lazy<Assembly>> Libraries =
            new ConcurrentDictionary<string, Lazy<Assembly>>(StringComparer.Ordinal);

        private readonly ITestOutputHelper output;

        public RealClientConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public static string RefsDirectory => TestFolders.AndroidRefs;

        /// <summary>The client id the platform's client sends (the Federation constructor's first argument).</summary>
        private static string ClientIdOf(string platform) =>
            platform == TestFolders.Pc ? McvsConstants.ClientIdSteam : FedFederation.GameClientId;

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void AnonymousGameServerOnBehalfAndLocalAnubisSessionsAgree(string platform, string folder)
        {
            if (!Available(platform, folder)) return;
            using var host = new Host(folder, ClientIdOf(platform));

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
            Assert.Equal(ClientIdOf(platform), (string)finder["client_id"].ToString());
        }

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void FirstRunProfileCallsSucceed(string platform, string folder)
        {
            if (!Available(platform, folder)) return;
            using var host = new Host(folder, ClientIdOf(platform));
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

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void TransferCodeRoundTrip(string platform, string folder)
        {
            if (!Available(platform, folder)) return;
            using var host = new Host(folder, ClientIdOf(platform));
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
        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void LostTokensRecoverThroughTheClientsOwn401Handling(string platform, string folder)
        {
            if (!Available(platform, folder)) return;
            using var host = new Host(folder, ClientIdOf(platform));
            dynamic client = Await(host.Federation().OpenSession(host.Credential("anonymous:restart"), "pw", "d1", null,
                                                                 new List<string>(GameScopes.Client), CancellationToken.None));
            string alias = Await(client.CreateAlias(CancellationToken.None));

            host.RestartWithoutAuthState();

            Assert.Equal(alias, (string)Await(client.CreateAlias(CancellationToken.None)));
            Assert.Equal(1, host.Server.Context.Tokens.Count); // one new token, for the scope set "auth"
        }

        // ------------------------------------------------------------------------------------------ plumbing

        private bool Available(string platform, string folder)
        {
            if (File.Exists(Path.Combine(folder, "Gameloft.OnlinePlatform.dll"))) return true;
            if (TestFolders.Required(platform))
                throw new InvalidOperationException("the " + platform + " client is required but Gameloft.OnlinePlatform.dll is not in " + folder);
            output.WriteLine("Gameloft.OnlinePlatform.dll not found in " + TestFolders.VariableOf(platform) + " (" + folder + "): skipped");
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

        /// <summary>Gameloft.OnlinePlatform from <paramref name="folder"/>, loaded once per folder.</summary>
        private static Assembly LoadLibrary(string folder)
        {
            return Libraries.GetOrAdd(Path.GetFullPath(folder), f => new Lazy<Assembly>(() =>
                new GameLoadContext(f).LoadFromAssemblyName(new AssemblyName("Gameloft.OnlinePlatform")))).Value;
        }

        /// <summary>A server on an ephemeral port plus factories for the client's own types.</summary>
        private sealed class Host : IDisposable
        {
            private readonly TempDir dir = new TempDir();
            private readonly Assembly library;
            private readonly string clientId;

            public Host(string folder, string clientId)
            {
                library = LoadLibrary(folder);
                this.clientId = clientId;
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

            public Type Type(string name) => library.GetType(name, true);

            public dynamic Protocol(string name) => Enum.Parse(Type("Gameloft.OnlinePlatform.Federation").GetNestedType("Protocol"), name);

            /// <summary>new Federation(clientId, eve, datacenter, 60000, 3, protocol), as OnlinePlatformComponent and LocalAnubisManager build it.</summary>
            public dynamic Federation(string datacenter = null, string protocol = "Unspecified") =>
                Activator.CreateInstance(Type("Gameloft.OnlinePlatform.Federation"), clientId, Eve, datacenter, 60000, 3, Protocol(protocol));

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

        /// <summary>Loads Gameloft.* and their Newtonsoft copy from the folder (MCVS_REFS or MCVS_PC_REFS); the BCL comes from net8 (never Mono's copies).</summary>
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
