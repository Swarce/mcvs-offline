using McvsOffline.Embed.Tests.Support;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Look-alikes of the 1.17.32 code each patch targets: same type/member names and the same source shapes, so
    /// Roslyn emits the instruction patterns the locators verify (compare with the decompiled game code cited in each
    /// patch class).
    /// </summary>
    public static class Fixtures
    {
        public const string NicePlaySdk = @"
namespace Gameloft.NicePlaySDK
{
    public static class NicePlaySDKDLL
    {
        public static int OriginalBodyRuns;
        public static bool Enabled => false;
        public static void OnInitialize()
        {
            OriginalBodyRuns++;
            if (Enabled) { OriginalBodyRuns += 100; }
        }
    }
}";

        /// <summary>A stand-in McvsOffline.Server: the real LanSupport plus a recording Bootstrap.</summary>
        public static byte[] Server(bool bootstrapThrows = false)
        {
            string bootstrap = @"
namespace McvsOffline
{
    public static class Bootstrap
    {
        public static int Calls;
        public static void Init()
        {
            Calls++;
            " + (bootstrapThrows ? "throw new System.InvalidOperationException(\"server broken\");" : "") + @"
        }
    }
}";
            return FixtureCompiler.Compile("McvsOffline.Server",
                bootstrap,
                FixtureCompiler.RepoSource("hosts", "embedded", "LanSupport.cs"),
                FixtureCompiler.RepoSource("server", "Core", "Num.cs"));
        }

        public const string GameloftUtil = @"
namespace Gameloft.Util
{
    public class Geolocalization
    {
        public string GeolocalizationFile => ""Config/EdgeList.txt"";
        public string GeolocalizationUrl => ""http://ip.ecomapis.gameloft.org/location/?ip="";
        public string EdgeListUrl => ""https://it.gameloft.org/dump/dump.edgelist"";
    }
}";

        public const string Game = @"
using System;
using System.Threading;
using System.Threading.Tasks;
namespace Gameloft.Engine
{
    public class Component
    {
        public CancellationToken CancellationToken => CancellationToken.None;
    }
}
namespace Game
{
    public class GameClientComponent : Gameloft.Engine.Component
    {
        public enum MatchConnectionType { Undefined, OnlineAndTutorial, OfflineMatchWithBots }
        public MatchConnectionType Requested;
        private void RunTask(Func<CancellationToken, Task> task, CancellationToken ct) { task(ct).Wait(); }
        private void NativeStartMatchWithBots()
        {
            RunTask((CancellationToken ct) => StartAutoMatchAsync(base.CancellationToken, MatchConnectionType.OfflineMatchWithBots), base.CancellationToken);
        }
        private void StartAutoMatchSameAsPlayerLevel()
        {
            RunTask((CancellationToken ct) => StartAutoMatchAsync(base.CancellationToken, MatchConnectionType.OfflineMatchWithBots), base.CancellationToken);
        }
        private Task StartAutoMatchAsync(CancellationToken ct, MatchConnectionType matchConnection)
        {
            Requested = matchConnection;
            return Task.CompletedTask;
        }
        public string BotTile() { NativeStartMatchWithBots(); return Requested.ToString(); }
        public string SameLevel() { StartAutoMatchSameAsPlayerLevel(); return Requested.ToString(); }
    }
}";

        public const string GameloftEngine = @"
using System.Net;
using System.Threading;
using System.Threading.Tasks;
namespace Gameloft.OnlinePlatform { public class Session { } }
namespace Gameloft.Engine
{
    public class HTTPServerMessageHandler { }
    public class HTTPServer
    {
        public string Prefix;
        public HTTPServer(HTTPServerMessageHandler messageHandler, int portNumber, string prefix = ""localhost"", string scheme = ""http"")
        {
            Prefix = scheme + ""://"" + prefix + "":"" + portNumber + ""/"";
        }
    }
    public class GameApplication
    {
        public class Arguments
        {
            public string RemoteIp;
            public string GSRemoteIp;
            public string LocalAnubisHost;
        }
        public static GameApplication Instance { get; set; }
        public Arguments Args { get; set; }
    }
    public static class LocalAnubisManager
    {
        public static int Starts;
        public static IPAddress Host
        {
            get
            {
                if (!string.IsNullOrEmpty(GameApplication.Instance.Args.LocalAnubisHost))
                    return IPAddress.Parse(GameApplication.Instance.Args.LocalAnubisHost);
                return IPAddress.Loopback;
            }
        }
        public static string PublicHost => GetPublicHost();
        public static string GetPublicHost()
        {
            if (IPAddress.IsLoopback(Host)) return ""localhost"";
            return ""100.64.0.7"";
        }
        public static void Start() { Start(CancellationToken.None).Wait(); }
        public static async Task Start(CancellationToken ct)
        {
            await Task.Yield();
            Starts++;
        }
    }
    public class ClientSettings
    {
        public int GetTeamRoomInfoRecurringDelay { get; set; } = 500;
        public string LocalAnubisFinderHost { get; set; } = LocalAnubisManager.PublicHost;
        public int LocalAnubisFinderPort { get; set; } = 51750;
    }
}
namespace Gameloft.Engine.LocalAnubis
{
    using Gameloft.OnlinePlatform;
    public class Lobby { }
    public class FinderHTTPMessageHandler : HTTPServerMessageHandler
    {
        public FinderHTTPMessageHandler(Session session, Lobby lobby) { }
    }
    public class Finder : HTTPServer
    {
        public int PublicPort { get; set; }
        public Finder(Session session, Lobby lobby, int port = 0)
            : base(new FinderHTTPMessageHandler(session, lobby), port, ""localhost"", ""http"")
        {
            PublicPort = port;
        }
    }
}";
    }
}
