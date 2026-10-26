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
                Server.Context.Settings.Override("unlocks", new Dictionary<string, object>
                {
                    ["batchSize"] = batchSize, ["batchDelaySeconds"] = 0L, ["startDelaySeconds"] = 0L, ["streamWaitSeconds"] = 30L,
                });
                Server.Context.Events.Subscribe(AlertHub.PushTopic, payload => { lock (Alerts) Alerts.Add((Dictionary<string, object>)payload); });
            }

            public McvsServer Server { get; }

            public List<Dictionary<string, object>> Alerts { get; } = new List<Dictionary<string, object>>();

            public UnlockGranter Granter => Server.Context.Features.Get<UnlockGranter>();

            public IProfileAccess Profiles => Server.Context.Features.Get<IProfileAccess>();

            /// <summary>
            /// The TS loading a player and saving the game profile, with its for_username token: LoadSections creates
            /// the sections that are absent (R3, verify-C3 §2.3), then R4.
            /// </summary>
            public string SaveGameProfile(string user)
            {
                Token(user, out string accountId);
                foreach (string section in new[] { "_mcvs.matchers", "_mcvs.snapshot", "inventory", "_inventory" })
                    if (Profiles.Read(accountId, section) == null) Post(user, section, "{}");
                return Post(user, "_mcvs.gameProfile", GameData.GameProfileDump);
            }

            /// <summary>
            /// A selector Set by the TS for a player (<c>POST profiles/me/myprofile/&lt;selector&gt;
            /// visibility=restricted&amp;object=...</c>): R3 with "{}", R4 with the dump (verify-C3 §2.3).
            /// </summary>
            public string Post(string user, string selector, string objectText)
            {
                string token = Token(user, out string accountId);
                string form = UrlEncoding.EncodeForm(new[]
                {
                    new KeyValuePair<string, string>("access_token", token),
                    new KeyValuePair<string, string>("visibility", "restricted"),
                    new KeyValuePair<string, string>("object", objectText),
                });
                Assert.Equal(200, Server.Handle(HttpRequest.Create("POST", "/profiles/me/myprofile/" + selector, form)).StatusCode);
                return accountId;
            }

            /// <summary>A GET by the TS for a player (R1 with a selector, R2 with "" and include_fields); returns the body.</summary>
            public string Get(string user, string selector, string query = "")
            {
                string token = Token(user, out _);
                var response = Server.Handle(HttpRequest.Create("GET", "/profiles/me/myprofile" + (selector.Length > 0 ? "/" + selector : "")
                                                                       + "?access_token=" + Uri.EscapeDataString(token) + query));
                Assert.Equal(200, response.StatusCode);
                return response.BodyText;
            }

            private string Token(string user, out string accountId)
            {
                Account account = Server.Context.Accounts.GetOrCreate("anonymous:" + user, "d");
                accountId = account.Id;
                return Server.Context.Tokens.Issue(account.Id, "anonymous:" + user, McvsConstants.ClientId,
                                                   StorageHarness.ServerScopes, "d", "game:mcvsoffline").Token;
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
            Assert.DoesNotContain("KillSignature_Avatar_1513", keys); // the unreleased set is coupon-only (UNLOCKUNRELEASED)
            Assert.Equal(McvsOffline.Tests.Content.AssetsTests.MercGroup, keys.Take(McvsOffline.Tests.Content.AssetsTests.MercGroup.Length));
            Assert.StartsWith("MercSkin_", keys[keys.Count - 1]);

            string account = h.SaveGameProfile("unlock1");
            h.WaitForStatus(account, "done (");

            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            Assert.Equal(keys, inventory.Keys.ToList()); // insertion order kept: the diff claims in JSON order
            Assert.All(inventory.Values, v => Assert.Equal(1L, v)); // JSON integers (the TS casts (int)(long))

            // The merc unlocks alone first, then batches of 4000; one set_profile alert per batch, to the player.
            int expectedAlerts = 1 + (keys.Count - McvsOffline.Tests.Content.AssetsTests.MercGroup.Length + 3999) / 4000;
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

        /// <summary>
        /// A brand-new player's first TS load (ProfileManagerComponent.LoadPlayerProfile): R1 and R2 read {}, then
        /// LoadSections creates each absent section with an R3 Set of "{}" (OP/ProfileSection.cs:232-239, 326-333),
        /// inventory included, and only then does the TS save the game profile (R4). Nothing may be granted before R4:
        /// an R3 inventory landing after a grant would erase the merc unlocks and every merc-dependent key claimed
        /// after it would become an alternate reward (verify-C5 (c)).
        /// </summary>
        [Fact]
        public void NothingIsGrantedDuringTheFirstLoadAndTheLedgerStartsWithTheMercUnlockAfterR4()
        {
            using var h = new Harness(unlockAll: true, batchSize: 4000);
            Assert.Equal("{}", h.Get("fresh", "_mcvs.gameProfile"));                       // R1
            Assert.Equal("{}", h.Get("fresh", "", "&include_fields=_mcvs.matchers,_mcvs.snapshot,inventory,_inventory")); // R2

            string account = h.Post("fresh", "_mcvs.matchers", "{}");                       // R3, the first write ever
            Thread.Sleep(300);
            Assert.Null(h.Profiles.Read(account, "inventory"));
            h.Post("fresh", "_mcvs.snapshot", "{}");                                        // R3
            h.Post("fresh", "inventory", "{}");                                             // R3: Set, replaces the section
            h.Post("fresh", "_inventory", "{}");                                            // R3
            Thread.Sleep(300);
            Assert.Empty((Dictionary<string, object>)h.Profiles.Read(account, "inventory"));
            Assert.Empty(h.Alerts);
            Assert.DoesNotContain(account, h.Granter.Status().Where(kv => ((string)kv.Value).StartsWith("done", StringComparison.Ordinal)).Select(kv => kv.Key));

            h.SaveGameProfile("fresh");                                                     // R4
            h.WaitForStatus(account, "done (");
            List<string> keys = h.Granter.AllKeys();
            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            Assert.Equal(McvsOffline.Tests.Content.AssetsTests.MercGroup, inventory.Keys.Take(McvsOffline.Tests.Content.AssetsTests.MercGroup.Length));
            Assert.Equal(keys, inventory.Keys.ToList());

            // The one-way backup holds the game profile the TS just saved, not an empty document.
            LocalProfileStore store = h.Server.Context.Features.Get<LocalProfileStore>();
            BackupInfo backup = store.Backups.List(account).Single(b => b.Reason == "unlockall");
            Assert.Contains("gameProfile", store.Backups.Read(account, backup.Id));
        }

        /// <summary>
        /// The ledger loses the merc unlock between two batches (a late R3 Set from another load, a hand edit): the
        /// next batch puts the merc unlocks back first, alone, and the lost keys are written again.
        /// </summary>
        [Fact]
        public void ALedgerResetBetweenBatchesPutsTheMercUnlockBackFirst()
        {
            using var h = new Harness(unlockAll: false, batchSize: 4000);
            string account = h.SaveGameProfile("reset1");
            h.Server.Context.Settings.Override("unlockAll", true); // granted below, on this thread only (no worker)
            bool wiped = false;
            h.Server.Context.Events.Subscribe(AlertHub.PushTopic, payload =>
            {
                // Runs on the granter's thread right after a batch was written: reset the ledger once, after batch 2.
                if (!wiped && h.Alerts.Count == 2)
                {
                    wiped = true;
                    h.Post("reset1", "inventory", "{}");
                }
            });
            h.Granter.Grant(account);
            Assert.True(wiped);

            List<string> keys = h.Granter.AllKeys();
            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            Assert.Equal(McvsOffline.Tests.Content.AssetsTests.MercGroup, inventory.Keys.Take(McvsOffline.Tests.Content.AssetsTests.MercGroup.Length));
            Assert.Equal(new HashSet<string>(keys), new HashSet<string>(inventory.Keys));

            // Alerts: merc unlock, batch 1 (wiped after it), then the merc unlock again alone, then every batch again.
            int batches = (keys.Count - McvsOffline.Tests.Content.AssetsTests.MercGroup.Length + 3999) / 4000;
            Assert.Equal(2 + 1 + batches, h.Alerts.Count);
            Assert.Equal(0, h.Granter.Grant(account)); // complete
        }

        /// <summary>
        /// The first batch waits until the TS's AlertsComponent listens for the player (a stream that takes set_profile;
        /// the TS's per-connection "connection"-only listener does not count), so the first alert is not lost.
        /// </summary>
        [Fact]
        public void TheFirstBatchWaitsForThePlayersAlertStream()
        {
            using var h = new Harness(unlockAll: true, batchSize: 4000);
            AlertHub hub = AlertHub.For(h.Server.Context);
            string account = h.SaveGameProfile("stream1");
            h.WaitForStatus(account, "waiting: for the player's alert stream");
            Assert.Empty((Dictionary<string, object>)h.Profiles.Read(account, "inventory"));

            string fedId = McvsConstants.FedIdPrefix + account;
            using (hub.Subscribe(new[] { fedId }, new[] { "connection" }, null))
            {
                Thread.Sleep(600);
                Assert.Empty((Dictionary<string, object>)h.Profiles.Read(account, "inventory"));
                using (AlertHub.Subscription all = hub.Subscribe(new[] { fedId }, new string[0], null))
                {
                    h.WaitForStatus(account, "done (");
                    var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
                    Assert.Equal(McvsOffline.Tests.Content.AssetsTests.MercGroup, inventory.Keys.Take(McvsOffline.Tests.Content.AssetsTests.MercGroup.Length));
                    Assert.Contains("set_profile", string.Join("\n", all.Take(1000, () => false)));
                }
            }
        }

        [Fact]
        public void OnlyTheGameProfileSectionSelectorsTrigger()
        {
            Assert.True(UnlockGranter.IsTriggerSelector("_mcvs.gameProfile"));
            Assert.True(UnlockGranter.IsTriggerSelector("_mcvs.matchers"));
            Assert.True(UnlockGranter.IsTriggerSelector("_mcvs.snapshot"));
            Assert.False(UnlockGranter.IsTriggerSelector("_mcvs"));            // R7
            Assert.False(UnlockGranter.IsTriggerSelector("_mcvs.currentMatch")); // R9
            Assert.False(UnlockGranter.IsTriggerSelector("_mcvs.gameProfile.x"));
            Assert.False(UnlockGranter.IsTriggerSelector("inventory"));
            Assert.False(UnlockGranter.IsTriggerSelector(""));                 // R5 root merge
            Assert.False(UnlockGranter.IsTriggerSelector(null));
        }

        /// <summary>
        /// A ledger granted by build 3 holds UnlockMerc_ALL_MAX (every merc and every mod, already claimed): the per-merc
        /// keys that replaced it are not written, or the game would pay 40 random gear for every merc the player owns.
        /// </summary>
        [Fact]
        public void ALegacyAllMercsKeyCoversTheMercGroup()
        {
            using var h = new Harness(unlockAll: false, batchSize: 20000);
            string account = h.SaveGameProfile("legacy1");
            h.Profiles.Merge(account, "inventory", new Dictionary<string, object> { [UnlockGranter.LegacyMercsKey] = 1L });
            h.Server.Context.Settings.Override("unlockAll", true);
            h.Granter.Grant(account);

            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            foreach (string key in McvsOffline.Tests.Content.AssetsTests.MercGroup) Assert.False(inventory.ContainsKey(key), key);
            Assert.Equal(1L, inventory[UnlockGranter.LegacyMercsKey]);
            Assert.Equal(1L, inventory["MercSkin_1_2"]);
            Assert.Equal(h.Granter.AllKeys().Count - McvsOffline.Tests.Content.AssetsTests.MercGroup.Length + 1, inventory.Count);
            Assert.Equal(0, h.Granter.Grant(account)); // complete
        }

        [Fact]
        public void ExistingLedgerEntriesAreKeptAndOnlyMissingKeysAreWritten()
        {
            using var h = new Harness(unlockAll: true);
            string account = h.SaveGameProfile("unlock2");
            h.WaitForStatus(account, "done (");
            h.Profiles.Merge(account, "inventory", new Dictionary<string, object> { ["UnlockMerc_2"] = 3L, ["VersusCoins"] = 500L });
            h.Profiles.Remove(account, "inventory.MercSkin_1_2");

            // A direct grant only fills the gap; other ledger keys (real IAP grants) are untouched.
            Assert.Equal(1, h.Granter.Grant(account));
            var inventory = (Dictionary<string, object>)h.Profiles.Read(account, "inventory");
            Assert.Equal(3L, inventory["UnlockMerc_2"]);
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
            Assert.Empty((Dictionary<string, object>)h.Profiles.Read(account, "inventory")); // as the R3 creation left it
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

            // Writes under /_mcvs need the CSRF header (design §5.2 rule 4): an HTML form cannot add it.
            Assert.Equal(403, h.Server.Handle(HttpRequest.Create("POST", "/_mcvs/unlocks/grant", "account=nobody")).StatusCode);
            Assert.Equal(404, h.Server.Handle(Grant("account=nobody")).StatusCode);
            string account = h.SaveGameProfile("unlock4");
            h.WaitForStatus(account, "done (");
            var again = h.Server.Handle(Grant("account=fed_id:" + account));
            Assert.Equal(false, MiniJson.ParseObject(again.BodyText)["queued"]); // already done this run
        }

        private static HttpRequest Grant(string form)
        {
            var request = HttpRequest.Create("POST", "/_mcvs/unlocks/grant", form);
            request.Headers.Add("X-MCVS", "1");
            return request;
        }
    }
}
