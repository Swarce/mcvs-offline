using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Alerts;
using McvsOffline.Server.Services.Storage;
using McvsOffline.Server.Services.Unlocks;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Unlocks
{
    /// <summary>settings.unlockAll through the inventory ledger (verify-C5 (c)).</summary>
    public class UnlockGranterTests
    {
        private sealed class Harness : IDisposable
        {
            private readonly TempDir dir = new TempDir();

            public Harness(bool unlockAll, long batchSize = 5000)
            {
                var options = new ServerOptions
                {
                    DataDir = dir.Combine("data"), Port = 0, ConsoleLog = false, FileLog = false,
                    RegisterServices = (router, context) =>
                    {
                        StorageService.Register(router, context);
                        UnlocksService.Register(router, context);
                    },
                };
                Server = McvsServer.Create(options);
                Server.Context.Settings.Override("unlockAll", unlockAll);
                Server.Context.Settings.Override("unlocks", new Dictionary<string, object> { ["batchSize"] = batchSize, ["batchDelaySeconds"] = 0L });
                Server.Context.Events.Subscribe(AlertHub.PushTopic, payload => { lock (Alerts) Alerts.Add((Dictionary<string, object>)payload); });
            }

            public McvsServer Server { get; }

            public List<Dictionary<string, object>> Alerts { get; } = new List<Dictionary<string, object>>();

            public UnlockGranter Granter => Server.Context.Features.Get<UnlockGranter>();

            public IProfileAccess Profiles => Server.Context.Features.Get<IProfileAccess>();

            /// <summary>The TS saving the game profile for a player (R4), with its for_username token.</summary>
            public string SaveGameProfile(string user)
            {
                Account account = Server.Context.Accounts.GetOrCreate("anonymous:" + user, "d");
                string token = Server.Context.Tokens.Issue(account.Id, "anonymous:" + user, McvsConstants.ClientId,
                                                           StorageHarness.ServerScopes, "d", "game:mcvsoffline").Token;
                string form = UrlEncoding.EncodeForm(new[]
                {
                    new KeyValuePair<string, string>("access_token", token),
                    new KeyValuePair<string, string>("visibility", "restricted"),
                    new KeyValuePair<string, string>("object", GameData.GameProfileDump),
                });
                Assert.Equal(200, Server.Handle(HttpRequest.Create("POST", "/profiles/me/myprofile/_mcvs.gameProfile", form)).StatusCode);
                return account.Id;
            }

            public Dictionary<string, object> WaitForStatus(string account, string prefix)
            {
                for (int i = 0; i < 200; i++)
                {
                    Dictionary<string, object> status = Granter.Status();
                    if (status.TryGetValue(account, out object s) && ((string)s).StartsWith(prefix, StringComparison.Ordinal)) return status;
                    Thread.Sleep(50);
                }
                throw new TimeoutException("unlock status of " + account + ": " + MiniJson.Serialize(Granter.Status()));
            }

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }
        }

        [Fact]
        public void AGameProfileSaveGrantsEveryUnlockInOrderWithSetProfileAlerts()
        {
            using var h = new Harness(unlockAll: true, batchSize: 4000);
            List<string> keys = h.Granter.AllKeys();
            Assert.True(keys.Count > 11000, "about 11.3k unlock keys expected, got " + keys.Count);
            Assert.Equal("UnlockMerc_ALL_MAX", keys[0]);
            Assert.StartsWith("MercSkin_", keys[keys.Count - 1]);

            string account = h.SaveGameProfile("unlock1");
            h.WaitForStatus(account, "done (");

            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            Assert.Equal(keys, inventory.Keys.ToList()); // insertion order kept: the diff claims in JSON order
            Assert.All(inventory.Values, v => Assert.Equal(1L, v)); // JSON integers (the TS casts (int)(long))

            // UnlockMerc_ALL_MAX alone first, then batches of 4000; one set_profile alert per batch, to the player.
            int expectedAlerts = 1 + (keys.Count - 1 + 3999) / 4000;
            Assert.Equal(expectedAlerts, h.Alerts.Count);
            Assert.All(h.Alerts, a =>
            {
                Assert.Equal(account, a["to"]);
                Assert.Equal("set_profile", ((Dictionary<string, object>)a["alert"])["type"]);
            });

            // The one-way change was preceded by a backup of the profile as the game left it.
            LocalProfileStore store = h.Server.Context.Features.Get<LocalProfileStore>();
            Assert.Contains(store.Backups.List(account), b => b.Reason == "unlockall");

            // Later saves do not grant again.
            h.Alerts.Clear();
            h.SaveGameProfile("unlock1");
            Thread.Sleep(200);
            Assert.Empty(h.Alerts);
        }

        [Fact]
        public void ExistingLedgerEntriesAreKeptAndOnlyMissingKeysAreWritten()
        {
            using var h = new Harness(unlockAll: true);
            string account = h.SaveGameProfile("unlock2");
            h.WaitForStatus(account, "done (");
            h.Profiles.Merge(account, "inventory", new Dictionary<string, object> { ["UnlockMerc_ALL_MAX"] = 3L, ["VersusCoins"] = 500L });
            h.Profiles.Remove(account, "inventory.MercSkin_1_2");

            // A direct grant only fills the gap; other ledger keys (real IAP grants) are untouched.
            Assert.Equal(1, h.Granter.Grant(account));
            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            Assert.Equal(3L, inventory["UnlockMerc_ALL_MAX"]);
            Assert.Equal(500L, inventory["VersusCoins"]);
            Assert.Equal(1L, inventory["MercSkin_1_2"]);
            Assert.Equal(0, h.Granter.Grant(account));
        }

        [Fact]
        public void NothingHappensWhenUnlockAllIsOffOrForOtherWrites()
        {
            using var h = new Harness(unlockAll: false);
            string account = h.SaveGameProfile("unlock3");
            Thread.Sleep(200);
            Assert.Null(h.Profiles.Read(account, "inventory"));
            Assert.Empty(h.Granter.Status());

            // On, but the write is not a game-profile save by the game (an in-process API write): still nothing.
            h.Server.Context.Settings.Override("unlockAll", true);
            h.Profiles.Set(account, "language", "en");
            Thread.Sleep(200);
            Assert.Empty(h.Granter.Status());
        }

        [Fact]
        public void DiagnosticsRoutesAreLoopbackOnly()
        {
            using var h = new Harness(unlockAll: true);
            var status = h.Server.Handle(HttpRequest.Create("GET", "/_mcvs/unlocks"));
            Assert.Equal(200, status.StatusCode);
            Dictionary<string, object> body = MiniJson.ParseObject(status.BodyText);
            Assert.Equal(true, body["enabled"]);

            var remote = HttpRequest.Create("GET", "/_mcvs/unlocks");
            remote.RemoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.30"), 5000);
            Assert.Equal(404, h.Server.Handle(remote).StatusCode);

            Assert.Equal(404, h.Server.Handle(HttpRequest.Create("POST", "/_mcvs/unlocks/grant", "account=nobody")).StatusCode);
            string account = h.SaveGameProfile("unlock4");
            h.WaitForStatus(account, "done (");
            var again = h.Server.Handle(HttpRequest.Create("POST", "/_mcvs/unlocks/grant", "account=fed_id:" + account));
            Assert.Equal(false, MiniJson.ParseObject(again.BodyText)["queued"]); // already done this run
        }
    }
}
