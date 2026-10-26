using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Admin.Logs;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Server.Services.Storage;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// A composed server with the admin, admin-logs and admin-saves modules over a data directory that survives
    /// <see cref="Restart"/> (the next start of the app), a manual clock, a fake host whose game credential decides the
    /// current player (§6.6 source "game"), and a session. Shared by the WP3 tests.
    /// </summary>
    public sealed class SavesServer : IDisposable
    {
        public static readonly DateTime T0 = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

        private readonly bool ownsDir;

        public SavesServer(TempDir dir = null, string name = "data", ManualClock clock = null, bool withHost = true,
                           Action<ServerOptions> configure = null)
        {
            ownsDir = dir == null;
            Dir = dir ?? new TempDir();
            DataDir = Dir.Combine(name);
            Clock = clock ?? new ManualClock(T0);
            Logs = new MemoryLogSink();
            WithHost = withHost;
            Configure = configure;
            Host = new FakeAdminHost();
            Start();
        }

        public TempDir Dir { get; }

        public string DataDir { get; }

        public ManualClock Clock { get; }

        public MemoryLogSink Logs { get; private set; }

        public bool WithHost { get; }

        public Action<ServerOptions> Configure { get; }

        public FakeAdminHost Host { get; }

        public McvsServer Server { get; private set; }

        public AdminTestClient Client { get; private set; }

        public ServerContext Context => Server.Context;

        public LocalProfileStore Store => Context.Features.Get<LocalProfileStore>();

        public string StagedDir => Path.Combine(DataDir, "admin", "staged");

        /// <summary>The next start of the app: a new server over the same data directory (staged operations apply here).</summary>
        public void Restart(TimeSpan? after = null)
        {
            Server.Dispose();
            Clock.Advance(after ?? TimeSpan.FromMinutes(1));
            Start();
        }

        private void Start()
        {
            Logs = new MemoryLogSink();
            Server = AdminTestClient.CreateServer(DataDir, Logs, (r, c) =>
            {
                LogsService.Register(r, c);
                SavesService.Register(r, c);
            }, Clock, Configure);
            if (WithHost) Context.Features.Set<IAdminHost>(Host);
            Client = new AdminTestClient(Server);
        }

        public AdminTestClient Session()
        {
            Client.OpenSession();
            return Client;
        }

        /// <summary>An account made the way the game's login makes one.</summary>
        public Account Player(string user) => Context.Accounts.GetOrCreate("anonymous:" + user, "dev-" + user);

        /// <summary>The game plays this credential (the host read it on the game thread).</summary>
        public void Play(string user) => Host.Info.GameCredential = "anonymous:" + user;

        /// <summary>A game save (source http) with a level and a game profile dump.</summary>
        public void SaveGame(string accountId, long level, string dump = GameData.GameProfileDump)
        {
            Store.Commit(accountId, "set", "", "http", doc =>
            {
                doc.Root["level"] = level;
                doc.Root["_mcvs"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["gameProfile"] = VerbatimJson.FromValidJson(dump) };
                return new WriteOutcome { Changed = true };
            });
        }

        public string ProfileFile(string accountId) => File.Exists(Store.PathFor(accountId)) ? File.ReadAllText(Store.PathFor(accountId)) : null;

        public List<Dictionary<string, object>> History()
        {
            string dir = Path.Combine(StagedDir, "history");
            if (!Directory.Exists(dir)) return new List<Dictionary<string, object>>();
            return Directory.GetFiles(dir, "*.json").OrderByDescending(f => f, StringComparer.Ordinal)
                            .Select(f => MiniJson.ParseObject(File.ReadAllText(f))).ToList();
        }

        public bool PendingExists => File.Exists(Path.Combine(StagedDir, "pending.json"));

        public bool ProbationExists => File.Exists(Path.Combine(StagedDir, "probation.json"));

        public Dictionary<string, object> Pending() => MiniJson.ParseObject(File.ReadAllText(Path.Combine(StagedDir, "pending.json")));

        public Dictionary<string, object> ProbationFile() => MiniJson.ParseObject(File.ReadAllText(Path.Combine(StagedDir, "probation.json")));

        /// <summary>Exports the account as transfer text parts through the API (Discord-sized by default).</summary>
        public List<string> ExportParts(string account = "current", int partChars = 1900)
        {
            HttpResponse response = Client.Post("saves/" + account + "/export",
                                                new Dictionary<string, object> { ["target"] = "text", ["partChars"] = (long)partChars });
            Assert.True(response.StatusCode == 200, response.BodyText);
            return ((List<object>)AdminTestClient.At(response, "transfer.texts")).Cast<string>().ToList();
        }

        public void Dispose()
        {
            Server.Dispose();
            if (ownsDir) Dir.Dispose();
        }
    }
}
