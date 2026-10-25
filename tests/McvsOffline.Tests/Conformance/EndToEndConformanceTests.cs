using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Services.Unlocks;
using McvsOffline.Tests.Auth;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Conformance
{
    /// <summary>
    /// End-to-end conformance: the player's REAL client libraries (see <see cref="GameClient"/>) boot against the
    /// complete server (every module, as ServiceRegistry wires it) over HTTP on an ephemeral port, in the order the game
    /// does it in Embedded mode, and every answer is consumed by the game's own parsing code.
    /// <para>
    /// Where the game would get a value from native code or from engine state that needs the native engine, the
    /// harness supplies the equivalent:
    /// <list type="bullet">
    /// <item>the anonymous username/password (AuthenticationComponent_GetAnonymousUsername/Password, Gaia): a
    /// device-derived name and a random password, as Gaia makes them;</item>
    /// <item>the profile selector "_mcvs" (GameApplication.GameProfileSelector reads MCVS.game through the engine):
    /// the value in the data pack (verify-C3 §1);</item>
    /// <item>GameProfileSection, SnapshotSection and MatchersSection, whose constructors only build that selector:
    /// created without their constructor, then ProfileSection's real constructor is run on them and the rest of
    /// their constructor is replayed (<see cref="GameClient.Construct"/>); every Load/Save/LoadSections/SaveSections
    /// call is the game's code;</item>
    /// <item>the game-profile dump (TObject.FromObject(playerGameObject).ToString("j")): the game's own
    /// DefaultProfile.tx from the data pack read and written back by the game's own Gameloft.Templates;</item>
    /// <item>CurrentMatchAccessor (its Selector also reads GameApplication): its two requests issued with the same
    /// StorageApi builders and Session.JsonRequest.</item>
    /// </list>
    /// </para>
    /// Every request the client makes is recorded from the server log; the test fails on any unknown endpoint, any
    /// 403, and any unexpected 4xx/5xx.
    /// </summary>
    public class EndToEndConformanceTests
    {
        private const string ClientId = FedFederation.GameClientId;
        private const string SuperCredential = "game:mcvsoffline";
        private const string SuperPassword = "mcvsoffline";
        private const string Selector = McvsConstants.GameProfileSelector; // "_mcvs", MCVS.game GameProfileSelector

        private readonly ITestOutputHelper output;

        public EndToEndConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public async Task BootSaveRestartAndReloadThroughTheRealClient()
        {
            GameClient game = GameClient.TryGet(output, "Gameloft.Engine", "Game", "Gameloft.Templates", "Gameloft.Util");
            if (game == null) return;
            var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
            using var host = new Host(game, output);

            // ---------------------------------------------------------------- 1. eve: datacenters + urls (OnlinePlatformComponent)
            dynamic clientFed = host.Federation();
            dynamic config = await GameClient.Await(clientFed.GetConfig(ct));
            Assert.Equal("None", config.Status.ToString()); // no "status": no update/maintenance popup (verify-C2 §1.2)
            Assert.Equal("mcvs", (string)clientFed.DatacenterName);
            foreach (string key in new[] { "pandora", "ecomm_api_root", "crm_iap", "etsv2", "marketing_site", "game_portal", "ads_agency" })
                Assert.True(((IDictionary<string, string>)config.Services).ContainsKey(key), "eve urls lacks " + key);
            host.Covered("eve config/{cid}/datacenters + datacenters/{dc}/urls -> Federation.GetConfig, Configuration (status None, 7 urls)");

            // ---------------------------------------------------------------- 2. pandora: locate every service the client knows
            Type service = game.Op("OnlineService");
            dynamic unspecified = game.Enum(game.Op("Federation+Protocol"), "Unspecified");
            var located = new List<string>();
            foreach (dynamic s in (IEnumerable)GameClient.Static(service, "FedServices"))
            {
                string url = await GameClient.Await(clientFed.LocateService(s, unspecified, ct));
                Assert.Equal(host.BaseUrl + "/" + (string)s.ToString(), url); // FormatUri trims the trailing '/'
                located.Add((string)s.ToString());
            }
            foreach (string name in new[] { "Ecommerce", "CrmIap", "Tracking", "Marketing", "GamePortal", "AdsAgency" })
            {
                string url = await GameClient.Await(clientFed.LocateService(GameClient.Static(service, name), unspecified, ct));
                Assert.StartsWith(host.BaseUrl, url);
                located.Add(name);
            }
            host.Covered("pandora locate/{svc} -> Federation.LocateService for " + located.Count + " services (" + string.Join(", ", located) + ")");

            // ---------------------------------------------------------------- 3. anonymous authorize (AuthenticationComponent.OpenAnonymousSession)
            string deviceId = "mcvs" + new string('7', 32);
            string anonymousUser = NativeAnonymousUsername(deviceId); // native Gaia supplies these two
            string anonymousPassword = "f3c1a9d2e8b74c55";
            var deviceArgs = new Dictionary<string, object>
            {
                ["firmware"] = "10", ["country"] = "US", ["carrier"] = "T-Mobile", ["model"] = "Pixel 3", ["resolution"] = "1080x2160", ["language"] = "en",
            };
            dynamic playerCredential = game.Credential("anonymous:" + anonymousUser);
            dynamic client = await GameClient.Await(clientFed.OpenSession(playerCredential, anonymousPassword, deviceId, deviceArgs,
                                                                          new List<string>(GameScopes.Client), ct));
            Assert.True((bool)client.IsOpen);
            string fedId = client.FedId.ToString();
            string accountId = AccountStore.DeriveAccountId("anonymous:" + anonymousUser);
            Assert.Equal("fed_id:" + accountId, fedId);
            Assert.Equal("None", (await GameClient.Await(clientFed.GetEveStatus(ct))).ToString());
            host.Covered("auth authorize (anonymous password grant, auto-created account, device info) -> Federation.OpenSession, Session.IsOpen");

            // ---------------------------------------------------------------- 4. users/me
            dynamic me = await GameClient.Await(client.GetAccountInfo(ct));
            Assert.Equal(fedId, me.FedId.ToString());
            Assert.Contains("anonymous:" + anonymousUser, ((IEnumerable)me.Credentials).Cast<object>().Select(c => c.ToString()));
            host.Covered("auth users/me -> Session.GetAccountInfo (AccountInfo.FedId, Credentials)");

            // ---------------------------------------------------------------- 5. the TS: game-server session + SessionForUser (EN/Connection.cs:1564)
            dynamic serverFed = host.Federation();
            dynamic gs = await GameClient.Await(serverFed.OpenSession(game.Credential(SuperCredential), SuperPassword, "mcvsGameServer", null,
                                                                      new List<string>(GameScopes.Server), ct));
            Assert.Equal("fed_id:" + AccountStore.DeriveAccountId(SuperCredential), gs.FedId.ToString());
            dynamic ts = await GameClient.Await(serverFed.OpenSessionForUser(game.Credential(SuperCredential), SuperPassword, playerCredential, deviceId, null,
                                                                             new List<string>(GameScopes.Server), ct));
            Assert.Equal(fedId, ts.FedId.ToString()); // on behalf: the subject is the player (verify-C2 §1.5)
            host.Covered("auth authorize (game server; for_username/for_credential_type on-behalf) -> OpenSession, OpenSessionForUser (FedId = player)");

            // ---------------------------------------------------------------- 6. first-run calls of the TS handshake
            Assert.Equal(0, (int)(await GameClient.Await(ts.GetAccountsForDeviceId(deviceId, ct))).Count);
            string alias = await GameClient.Await(ts.CreateAlias(ct));
            Assert.Matches("^[1-9][0-9]{8}$", alias);
            Assert.Equal(fedId, (await GameClient.Await(client.LookupAlias(alias, ct))).ToString());
            dynamic compliances = await GameClient.Await(ts.AddCompliances(ts.FedId, "US", 25, ct));
            foreach (dynamic pair in (IEnumerable)compliances) Assert.True((bool)pair.Value.Compliant);
            host.Covered("auth users?device_id= ([]), games/mygame/alias (create + lookup), users/{cred}/compliance -> Session.GetAccountsForDeviceId, CreateAlias, LookupAlias, AddCompliances");

            // ---------------------------------------------------------------- 7. CRM: GS server settings + the user's full document
            dynamic serverConfig = await GameClient.Await(GameClient.CallStatic(game.Op("CrmSection"), "GetCrmConfig", gs, "game", ct));
            dynamic serverDefault = serverConfig["_ServerConfig"]["default"];
            Assert.False((bool)serverDefault["IsDevelopment"]); // the dev menu would be on by default (verify-C5 (a))
            Assert.Equal(1, (int)serverDefault["Matchmaking"]["MinPlayers"]);
            Assert.Equal(0, (int)serverDefault["Matchmaking"]["MaxOpenTimeInSeconds"]);
            Assert.Equal("1", (string)serverConfig["_OverrideTables"]["MCVS_System"]["GameFeatures"]["Id@Enabled"]["RedeemCoupons"]);
            dynamic crm = await GameClient.Await(game.Service(ts, "CrmService").GetCRM().GetStandardConfiguration(ct));
            dynamic reader = crm.GetReader(); // StandardCrm.Data: runs every client parser of the document
            int storeItems = (int)reader.OfflineStore.Items.Count;
            Assert.True(storeItems > 1000, "offline_store items parsed by OfflineStoreData: " + storeItems);
            Assert.Equal(0, (int)reader.IapStore.Items.Count); // present, empty: IapStore.Items is read without a null check
            Assert.NotNull(reader.RawGameParameters);
            Assert.NotNull(reader.PointCutActions);
            Assert.NotNull(reader.FatigueGroups);
            dynamic iap = await GameClient.Await(GameClient.CallStatic(game.Op("CrmSection"), "GetCrmConfig", ts, "iap", ct));
            Assert.NotNull(iap["prices"]);
            host.Covered("crm configs/users/me/game (GS) -> CrmSection.GetCrmConfig: IsDevelopment=false, Matchmaking 1/0, RedeemCoupons=1");
            host.Covered("crm configs/users/me -> Crm.GetStandardConfiguration + StandardCrm.Data: OfflineStoreData (" + storeItems
                         + " items), IapStoreData, PointCutAction, fatigue groups, TLEs, ads, game parameters; configs/users/me/iap");

            // ---------------------------------------------------------------- 8. TS profile load: R1 game profile, R2 sections (ProfileManagerComponent.LoadPlayerProfile)
            var sections = new ProfileSections(game, ts);
            dynamic gameProfile = await GameClient.Await(GameClient.CallGeneric(sections.Profile, "GetGameProfile", new[] { sections.GameProfileType }, ct));
            Assert.True((bool)gameProfile.IsLoaded);
            Assert.Null(gameProfile.GetTemplate()); // new account: "{}" -> null template -> DefaultProfile (verify-C3 §3.1)
            await sections.LoadAll(ct);
            dynamic standard = sections.Standard;
            // Nothing is stored yet, so the server-maintained fields are absent; every game reader defaults them
            // (StandardProfile Safe* getters; PlayerProfileComponent.RetrieveInstallationDate falls back to UtcNow).
            Assert.Equal(1, ((DateTime)standard.Created).Year);
            host.Covered("storage R1 GET profiles/me/myprofile/_mcvs.gameProfile -> GameProfileSection.Load ({} = new profile, GetTemplate() null)");
            host.Covered("storage R2 GET profiles/me/myprofile?include_fields=... (nested) + R3 creates -> ProfileSection.LoadSections(matchers, snapshot, standard, inventory, _inventory)");

            // ---------------------------------------------------------------- 9. TS save: R4 game profile, R5 sections merge, R11 language (PlayerProfileComponent.DoSaveProfile)
            string dump = host.GameProfileDump(game);
            GameClient.Set(gameProfile, "ProfileString", dump);
            Assert.True((bool)gameProfile.IsModified);
            await GameClient.Await(gameProfile.Save(ct));
            Assert.False((bool)gameProfile.IsModified);
            standard.JsonProfile["level"] = Jv(game, 7);
            standard.JsonProfile["xp"] = Jv(game, 5120);
            standard.JsonProfile["country"] = Jv(game, "US");
            sections.Snapshot.JsonProfile["PlayerName"] = Jv(game, "agent-" + alias);
            sections.Snapshot.JsonProfile["PlayerLevel"] = Jv(game, 7);
            sections.Matchers.JsonProfile["LoginsPerDay"] = Jv(game, 1);
            await sections.SaveSections(ct, sections.Standard, sections.Snapshot, sections.Matchers);
            await GameClient.Await(game.Service(ts, "StorageService").SetProfileField("language", "\"en\"", ct));
            host.Covered("storage R4 POST _mcvs.gameProfile (" + dump.Length + " chars, game Templates dump) -> GameProfileSection.Save (200, no ETag)");

            // A later autosave of a realistic profile: real dumps are about 1.4 MB (verify-C3, STORAGE notes).
            string bigDump = host.Canonical(game, Inflate(dump, 1400 * 1024)); // as the game's writer would emit it
            GameClient.Set(gameProfile, "ProfileString", bigDump);
            await GameClient.Await(gameProfile.Save(ct));
            dump = bigDump;
            host.Covered("storage R4 again with a " + (bigDump.Length / 1024) + " KB dump (autosave of a full profile) -> GameProfileSection.Save");
            host.Covered("storage R5 POST profiles/me/myprofile operation=merge (standard + _mcvs.snapshot + _mcvs.matchers) -> ProfileSection.SaveSections; R11 language -> StorageService.SetProfileField");

            // ---------------------------------------------------------------- 10. GS: currentMatch R8 (empty first), R9 (CurrentMatchAccessor's requests)
            dynamic playerFedId = game.Credential(fedId);
            Type storageApi = game.Op("StorageApi");
            dynamic emptyMatch = await host.LoadCurrentMatch(game, gs, playerFedId, ct);
            Assert.Equal("Object", emptyMatch.Token.Type.ToString()); // never 404 (verify-C3 §3.4)
            Assert.Empty((IEnumerable)emptyMatch.Token);
            string match = "{\"CurrentServerType\":\"gs\",\"CurrentServerId\":\"room-1\",\"ReconnectAttemptsCount\":0,\"CurrentRoomUser\":\"" + fedId + "\"}";
            dynamic save = GameClient.CallStatic(storageApi, "SetProfileRequest", gs, playerFedId, "storage_admin", match, Selector + ".currentMatch",
                                                 null, GameClient.Static(game.Op("StorageApi+Operation"), "Set"), null);
            await GameClient.Await(GameClient.Call(gs, "JsonRequest", save, ct, "", "", 0));
            dynamic loadedMatch = await host.LoadCurrentMatch(game, gs, playerFedId, ct);
            Assert.Equal("gs", (string)loadedMatch.Token["CurrentServerType"]);
            host.Covered("storage R8/R9 GET/POST profiles/fed_id%3A<id>/myprofile/_mcvs.currentMatch with the game server's own token (scope storage_admin) -> Session.JsonRequest");

            // ---------------------------------------------------------------- 11. lobby hand-off: encrypt_token -> verify (LocalAnubis Controller/Finder)
            dynamic anubisFed = host.Federation(protocol: "HTTPS"); // LocalAnubisManager forces HTTPS; explicit ports keep http
            dynamic anubis = await GameClient.Await(anubisFed.OpenSession(game.Credential(SuperCredential), SuperPassword, "default", null,
                                                                          new List<string>(GameScopes.LocalAnubis), ct));
            string encrypted = await GameClient.Await(client.EncryptedAccessToken("LocalAnubisNonce", "lobby", ct));
            dynamic login = await GameClient.Await(anubis.Verify(encrypted, "LocalAnubisNonce", game.Credential("system:Anubis"), ct));
            Assert.Equal(accountId, login["account"].ToString());
            dynamic roomUser = game.Op("RoomUser").GetMethod("FromAccountInfo").Invoke(null, new object[] { login });
            Assert.Equal(fedId, roomUser.FedId.ToString());
            host.Covered("auth encrypt_token + verify -> Session.EncryptedAccessToken, Session.Verify, RoomUser.FromAccountInfo (LocalAnubis HTTPS-forced Federation)");

            // ---------------------------------------------------------------- 12. matchmaker lobbyconfigs -> the TS/GS World's arguments (Controller.DoConnectGame)
            foreach (string serverType in new[] { "ts", "gs" })
            {
                Type gameConfigType = game.Type("Gameloft.Engine", "Gameloft.Engine.LocalAnubis.GameConfig");
                dynamic gameConfig = await GameClient.Await(GameClient.CallStatic(gameConfigType, "FindGame", anubis, serverType, ct));
                Assert.Equal(serverType, (string)gameConfig.ServerType);
                Assert.NotNull(GameClient.Call(gameConfig, "GetAutomacher", "pvpdeathmatch"));
                object controller = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    game.Type("Gameloft.Engine", "Gameloft.Engine.LocalAnubis.Controller"));
                string configArgs = GameClient.Call(controller, "GetConfigArgs", gameConfig);
                Type argumentsType = game.Type("Gameloft.Engine", "Gameloft.Engine.GameApplication+Arguments");
                dynamic args = GameClient.CallStatic(argumentsType, "FromString", configArgs);
                Assert.Equal(serverType, (string)args.ServerType);
                Assert.Equal(host.BaseUrl + "/", (string)args.EnvironmentSwitcherUrl);
                Assert.Equal(SuperCredential, (string)args.SuperCredential);
                Assert.Equal(SuperPassword, (string)args.SuperPassword);
                Assert.Equal("mcvs", (string)args.DataCenter);
                if (serverType == "ts") Assert.Equal("Levels/Menus.level", (string)args.Level);
                else Assert.Null((string)args.Level);
                host.Covered("matchmaker lobbyconfigs/{cid}/" + serverType + " -> LocalAnubis GameConfig.FindGame, Controller.GetConfigArgs, GameApplication.Arguments.FromString (ServerType "
                             + serverType + ", EnvironmentSwitcherUrl, SuperCredential/Password, DataCenter" + (serverType == "ts" ? ", Level" : "") + ")");
            }

            // ---------------------------------------------------------------- 13. coupon redeem (PlayerProfileComponent.RedeemCouponRemote -> PushCouponDataToInbox)
            game.RegisterGameAssemblies();
            byte[] coupon = await GameClient.Await(game.Service(ts, "AssetsService").RedeemCoupon("UNLOCKMERCS", ct));
            dynamic rewards = GameClient.CallStatic(game.Type("Gameloft.Util", "Gameloft.Util.Json"), "LoadArray", Encoding.UTF8.GetString(coupon));
            Type rewardItem = game.Type("Game", "Game.RewardItem");
            object source = Activator.CreateInstance(rewardItem.GetNestedType("TrackingSource"));
            var parsedTypes = new List<string>();
            foreach (dynamic reward in (IEnumerable)rewards)
            {
                object item = GameClient.CallStatic(game.Type("Game", "Game.RewardHelper"), "CreateRewardItem", (string)reward["type"], (int)reward["quantity"], source);
                Assert.NotNull(item);
                // Explicit merc ids only (the reward displays look every UnlockMerc up by its id; "ALL" crashed them).
                string subtype = (string)rewardItem.GetProperty("Subtype").GetValue(item);
                string subtype2 = (string)rewardItem.GetProperty("Subtype2").GetValue(item);
                Assert.True(int.TryParse(subtype, out int mercId) && mercId > 0, (string)reward["type"]);
                if (item.GetType().Name == "ModTierRewardItem") Assert.Equal("4", subtype2);
                else Assert.Equal("UnlockMercRewardItem", item.GetType().Name);
                parsedTypes.Add(item.GetType().Name + "_" + subtype);
            }
            Assert.Equal(McvsOffline.Tests.Content.AssetsTests.MercGroup.Length, parsedTypes.Count);
            Assert.Equal(20, parsedTypes.Count(t => t.StartsWith("UnlockMercRewardItem_", StringComparison.Ordinal)));
            host.Covered("assets coupons/{cid}/{code} -> AssetsService.RedeemCoupon, Json.LoadArray, RewardHelper.CreateRewardItem (UNLOCKMERCS: "
                         + "20 UnlockMercRewardItem with numeric merc ids, 21 ModTierRewardItem tier 4)");
            // The opt-in codes: every reward parses to its reward class with numeric ids (claim and display look them up).
            var expectedClass = new Dictionary<string, string>
            {
                ["UnlockMerc"] = "UnlockMercRewardItem", ["WeaponCamo"] = "WeaponCamoRewardItem",
                ["MercAccessory"] = "MercAccessoryRewardItem", ["KillSignature"] = "KillSignatureRewardItem",
            };
            foreach (string code in new[] { "UNLOCKPAID", "UNLOCKUNRELEASED" })
            {
                byte[] data = await GameClient.Await(game.Service(ts, "AssetsService").RedeemCoupon(code, ct));
                int n = 0;
                foreach (dynamic reward in (IEnumerable)GameClient.CallStatic(game.Type("Gameloft.Util", "Gameloft.Util.Json"), "LoadArray", Encoding.UTF8.GetString(data)))
                {
                    string type = (string)reward["type"];
                    object item = GameClient.CallStatic(game.Type("Game", "Game.RewardHelper"), "CreateRewardItem", type, (int)reward["quantity"], source);
                    Assert.NotNull(item);
                    Assert.Equal(expectedClass[type.Split('_')[0]], item.GetType().Name);
                    string last = (string)rewardItem.GetProperty(type.StartsWith("UnlockMerc_", StringComparison.Ordinal) ? "Subtype" : "Subtype2").GetValue(item);
                    Assert.True(int.TryParse(last, out _), type);
                    n++;
                }
                Assert.True(n > 0, code);
                host.Covered("assets coupons/{cid}/" + code + " -> " + n + " rewards, each parsed by RewardHelper.CreateRewardItem to its class with numeric ids");
            }

            // ---------------------------------------------------------------- 14. next app start: a new server instance on the same data, a new client
            host.Restart();
            dynamic fed2 = host.Federation();
            dynamic client2 = await GameClient.Await(fed2.OpenSession(playerCredential, anonymousPassword, deviceId, deviceArgs, new List<string>(GameScopes.Client), ct));
            Assert.Equal(fedId, client2.FedId.ToString());
            dynamic ts2 = await GameClient.Await(host.Federation().OpenSessionForUser(game.Credential(SuperCredential), SuperPassword, playerCredential, deviceId, null,
                                                                                    new List<string>(GameScopes.Server), ct));
            var reloaded = new ProfileSections(game, ts2);
            dynamic gameProfile2 = await GameClient.Await(GameClient.CallGeneric(reloaded.Profile, "GetGameProfile", new[] { reloaded.GameProfileType }, ct));
            Assert.Equal(dump, (string)GameClient.Get(gameProfile2, "ProfileString")); // byte-exact (verify-C3 §7.6)
            dynamic template = gameProfile2.GetTemplate(); // the TS's own TemplateReader over what storage returned
            Assert.NotNull(template);
            Assert.Equal(dump, (string)template.ToString("j"));
            await reloaded.LoadAll(ct);
            Assert.Equal(7, (int)reloaded.Standard.Level);
            Assert.Equal(5120, (int)reloaded.Standard.Xp);
            Assert.Equal("en", (string)reloaded.Standard.Language);
            Assert.Equal("mcvs", (string)reloaded.Standard.Datacenter); // server-maintained, read-only fields
            Assert.Equal("anonymous:" + anonymousUser, reloaded.Standard.ProfileCredential.ToString());
            Assert.Equal(DateTime.UtcNow.Year, ((DateTime)reloaded.Standard.Created).Year);
            Assert.Equal("agent-" + alias, (string)reloaded.Snapshot.JsonProfile["PlayerName"]);
            Assert.Equal(1, (int)reloaded.Matchers.JsonProfile["LoginsPerDay"]);
            Assert.Equal(alias, (string)await GameClient.Await(ts2.CreateAlias(ct)));
            host.Covered("restart: new server process on the same data + new client: same account, same alias; R1 returns the dump byte-exact; Gameloft.Templates reads it back identically; R2 sections persisted");

            // ---------------------------------------------------------------- 15. another server reads the player's profile read-only (LoadPlayerProfileReadOnly)
            dynamic gs2 = await GameClient.Await(host.Federation().OpenSession(game.Credential(SuperCredential), SuperPassword, "mcvsGameServer", null,
                                                                             new List<string>(GameScopes.Server), ct));
            dynamic readOnly = GameClient.CallStatic(game.Op("SessionForUser"), "GetReadOnlySessionForUser", gs2, playerFedId);
            var other = new ProfileSections(game, readOnly, playerFedId);
            await other.LoadGameAndStandard(ct);
            string seen = GameClient.Get(other.Game, "ProfileString");
            Assert.True(JTokenEquals(game, dump, seen), "read-only view differs from the saved dump");
            Assert.NotNull(other.Game.GetTemplate());
            Assert.Equal(7, (int)other.Standard.Level);
            host.Covered("super credential read-only session for another user (GetReadOnlySessionForUser, profiles/fed_id%3A<id>/myprofile?include_fields=_mcvs.gameProfile,...) -> LoadSections(game, standard), GetTemplate");

            host.AssertCleanRequestLog();
            host.WriteCoverage();
        }

        /// <summary>
        /// settings.unlockAll end to end: after the TS saves the game profile the server fills the inventory ledger;
        /// the TS's own InventorySection reads it, CheckInventory's integer cast holds for every entry, the TS's
        /// AlertListener receives the set_profile alerts, and every key is a reward the game's RewardItem.Parse knows.
        /// </summary>
        [Fact]
        public async Task UnlockAllLedgerIsReadableByTheRealClient()
        {
            GameClient game = GameClient.TryGet(output, "Gameloft.Engine", "Game", "Gameloft.Templates", "Gameloft.Util");
            if (game == null) return;
            var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
            using var host = new Host(game, output, settings =>
            {
                settings.Override("unlockAll", true);
                settings.Override("unlocks", new Dictionary<string, object>
                {
                    ["batchSize"] = 4000L, ["batchDelaySeconds"] = 0L, ["startDelaySeconds"] = 0L, ["streamWaitSeconds"] = 30L,
                });
            });

            dynamic fed = host.Federation();
            dynamic player = game.Credential("anonymous:unlockconformance");
            await GameClient.Await(fed.OpenSession(player, "pw", "dev-unlock", null, new List<string>(GameScopes.Client), ct));
            dynamic ts = await GameClient.Await(host.Federation().OpenSessionForUser(game.Credential(SuperCredential), SuperPassword, player, "dev-unlock", null,
                                                                                   new List<string>(GameScopes.Server), ct));

            // The TS's user AlertsComponent listens with AlertType.All on the player's session (EN/AlertsComponent.cs).
            var alerts = new AlertRecorder();
            dynamic listener = game.Service(ts, "AlertService").GetAlert();
            alerts.Attach(listener);
            listener.StartListening();
            await Task.Delay(300, ct);
            // The granter waits for exactly this before its first batch: a stream of the player that takes set_profile.
            string fedId = "fed_id:" + AccountStore.DeriveAccountId("anonymous:unlockconformance");
            Assert.Equal(1, McvsOffline.Server.Services.Alerts.AlertHub.For(host.Server.Context).ListenerCount(fedId, "set_profile"));

            var sections = new ProfileSections(game, ts);
            dynamic gameProfile = await GameClient.Await(GameClient.CallGeneric(sections.Profile, "GetGameProfile", new[] { sections.GameProfileType }, ct));
            await sections.LoadAll(ct);
            GameClient.Set(gameProfile, "ProfileString", host.GameProfileDump(game));
            await GameClient.Await(gameProfile.Save(ct));

            UnlockGranter granter = host.Server.Context.Features.Get<UnlockGranter>();
            int expected = granter.AllKeys().Count;
            string account = AccountStore.DeriveAccountId("anonymous:unlockconformance");
            for (int i = 0; i < 200 && !(granter.Status().TryGetValue(account, out object s) && ((string)s).StartsWith("done", StringComparison.Ordinal)); i++)
                await Task.Delay(50, ct);

            int mercs = McvsOffline.Tests.Content.AssetsTests.MercGroup.Length;
            int expectedAlerts = 1 + (expected - mercs + 3999) / 4000;
            Assert.True(alerts.WaitFor(expectedAlerts, 10000), "set_profile alerts received by the real AlertListener: " + alerts.Count + "/" + expectedAlerts);
            Assert.All(alerts.Items, a => Assert.Equal("set_profile", ((dynamic)a).Type.ToString()));

            // A fresh load, as IapStoreComponent.CheckInventory does it (EN/IapStoreComponent.cs:1243-1306).
            game.RegisterGameAssemblies();
            var again = new ProfileSections(game, ts);
            await again.LoadAll(ct);
            dynamic inventory = again.Inventory.JsonProfile;
            Type jvalue = game.Type("Newtonsoft.Json", "Newtonsoft.Json.Linq.JValue");
            Type rewardHelper = game.Type("Game", "Game.RewardHelper");
            object source = Activator.CreateInstance(game.Type("Game", "Game.RewardItem").GetNestedType("TrackingSource"));
            var keys = new List<string>();
            var unknown = new List<string>();
            foreach (dynamic property in (IEnumerable)inventory) // JObject children are JProperty
            {
                string key = property.Name;
                object value = property.Value;
                Assert.IsType(jvalue, value);
                int count = (int)(long)jvalue.GetProperty("Value").GetValue(value); // (int)(long)((JValue)v).Value
                Assert.Equal(1, count);
                keys.Add(key);
                if (GameClient.CallStatic(rewardHelper, "CreateRewardItem", key, count, source) == null) unknown.Add(key);
            }
            Assert.Equal(expected, keys.Count);
            Assert.Equal(McvsOffline.Tests.Content.AssetsTests.MercGroup, keys.Take(mercs));
            Assert.StartsWith("MercSkin_", keys[keys.Count - 1]);
            Assert.True(unknown.Count == 0, "reward ids RewardItem.Parse does not know: " + string.Join(", ", unknown.Take(20)));
            host.Covered("unlockAll: " + keys.Count + " ledger keys read by IapStoreComponent.InventorySection (LoadSections), all JValue integers, the merc unlocks (UnlockMerc_<id>, ModTier_<id>_4) first, MercSkin_* last; "
                         + alerts.Count + " set_profile alerts on the real AlertListener; every key parsed by RewardItem.Parse");

            await GameClient.Await(listener.StopListening(ct));
            host.AssertCleanRequestLog();
            host.WriteCoverage();
        }

        // ------------------------------------------------------------------------------------------ helpers

        /// <summary>A Gaia-style anonymous username: derived from the device, URL-safe.</summary>
        private static string NativeAnonymousUsername(string deviceId)
        {
            return "a" + McvsOffline.Server.Core.Accounts.AccountStore.DeriveAccountId("device:" + deviceId).Substring(0, 23);
        }

        /// <summary>The dump with a large component-like property added under "$default" (plain ints and strings).</summary>
        private static string Inflate(string dump, int targetChars)
        {
            int at = dump.IndexOf("\"$default\"", StringComparison.Ordinal);
            at = dump.IndexOf('{', at) + 1;
            var sb = new StringBuilder("\"/McvsLoadTest\":{\"Items\":[");
            for (int i = 0; sb.Length < targetChars; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"Id\":\"WeaponCamo_").Append(i % 21 + 1).Append('_').Append(i).Append("\",\"Count\":").Append(i * 7 % 1000)
                  .Append(",\"Seen\":").Append(i % 2 == 0 ? "true" : "false").Append(",\"Name\":\"caf\u00e9 ").Append(i).Append("\"}");
            }
            sb.Append("]},");
            return dump.Insert(at, sb.ToString());
        }

        private static dynamic Jv(GameClient game, object value) =>
            GameClient.CallStatic(game.Type("Newtonsoft.Json", "Newtonsoft.Json.Linq.JToken"), "FromObject", value);

        private static bool JTokenEquals(GameClient game, string a, string b)
        {
            Type jtoken = game.Type("Newtonsoft.Json", "Newtonsoft.Json.Linq.JToken");
            object ta = GameClient.CallStatic(jtoken, "Parse", a);
            object tb = GameClient.CallStatic(jtoken, "Parse", b);
            return (bool)GameClient.CallStatic(jtoken, "DeepEquals", ta, tb);
        }

        /// <summary>The profile sections the TS loads, built as <see cref="EndToEndConformanceTests"/> explains.</summary>
        private sealed class ProfileSections
        {
            private readonly GameClient game;
            private readonly Type sectionType;

            public ProfileSections(GameClient game, object session, object credential = null)
            {
                this.game = game;
                sectionType = game.Op("ProfileSection");
                GameProfileType = game.Type("Game", "Game.GameProfileSection");
                dynamic storage = game.Service(session, "StorageService");
                Profile = credential == null ? storage.GetProfile() : GameClient.Call(storage, "GetProfile", credential);

                // GameProfileSection(Profile): base(profile, "_mcvs.gameProfile") + its two field initializers.
                Game = GameClient.Construct(GameProfileType, sectionType, (object)Profile, Selector + ".gameProfile");
                GameClient.Set(Game, "unsupportedLinkFrom", new List<string> { "72571", "72147", "61134", "66102", "66354" });
                GameClient.Set(Game, "unsupportedLinkTo", new List<string> { "72571", "72147" });
                // SnapshotSection / MatchersSection(Profile): base(profile, "_mcvs.snapshot"/"_mcvs.matchers"); ProfileString = "{}".
                Snapshot = GameClient.Construct(game.Type("Gameloft.Engine", "Gameloft.Engine.SnapshotSection"), sectionType, (object)Profile, Selector + ".snapshot");
                GameClient.Set(Snapshot, "ProfileString", "{}");
                Matchers = GameClient.Construct(game.Type("Game", "Game.MatchersSection"), sectionType, (object)Profile, Selector + ".matchers");
                GameClient.Set(Matchers, "ProfileString", "{}");

                // Profile.GetProfileSection<T>(name) would construct them itself; it returns cached sections first.
                var cache = (IDictionary)GameClient.Get(Profile, "sections");
                cache["game"] = Game;
                cache["snapshot"] = Snapshot;
                cache["matcher"] = Matchers;
            }

            public dynamic Profile { get; }

            public Type GameProfileType { get; }

            public dynamic Game { get; }

            public dynamic Snapshot { get; }

            public dynamic Matchers { get; }

            public dynamic Standard { get; private set; }

            public dynamic Inventory { get; private set; }

            public dynamic AdsInventory { get; private set; }

            /// <summary>ProfileManagerComponent.LoadPlayerProfile's LoadSections (real constructors for the other three).</summary>
            public async Task LoadAll(CancellationToken ct)
            {
                Standard = Section("standard", game.Op("StandardProfile"));
                Inventory = Section("inventory", game.Type("Gameloft.Engine", "Gameloft.Engine.IapStoreComponent+InventorySection"));
                AdsInventory = Section("adsInventory", game.Type("Gameloft.Engine", "Gameloft.Engine.AdsComponent+AdsInventorySection"));
                await Load(ct, Matchers, Snapshot, Standard, Inventory, AdsInventory);
            }

            /// <summary>ProfileManagerComponent.LoadPlayerProfileReadOnly's LoadSections.</summary>
            public async Task LoadGameAndStandard(CancellationToken ct)
            {
                Standard = Section("standard", game.Op("StandardProfile"));
                await Load(ct, Game, Standard);
            }

            public Task SaveSections(CancellationToken ct, params object[] sections) =>
                GameClient.Await(GameClient.CallStatic(sectionType, "SaveSections", (object)game.List(sectionType, sections), ct, "", "", 0));

            private Task Load(CancellationToken ct, params object[] sections) =>
                GameClient.Await(GameClient.CallStatic(sectionType, "LoadSections", (object)game.List(sectionType, sections), ct, "", "", 0));

            private dynamic Section(string name, Type type) => GameClient.CallGeneric(Profile, "GetProfileSection", new[] { type }, name);
        }

        /// <summary>The full server on an ephemeral port, its request log and the coverage notes.</summary>
        private sealed class Host : IDisposable
        {
            private static readonly Regex RequestLine = new Regex(@"^#(\d+) (\S+) (\S+) -> (\d+) \[([^\]]*)\] (\S+)(?: B)? ([0-9.]+) ms");

            private readonly TempDir dir = new TempDir();
            private readonly GameClient game;
            private readonly ITestOutputHelper output;
            private readonly Action<Settings> configure;
            private readonly MemoryLogSink logs = new MemoryLogSink(LogLevel.Info, 100000);
            private readonly List<string> coverage = new List<string>();
            private int port;

            public Host(GameClient game, ITestOutputHelper output, Action<Settings> configure = null)
            {
                this.game = game;
                this.output = output;
                this.configure = configure;
                Start();
            }

            public McvsServer Server { get; private set; }

            /// <summary>"http://127.0.0.1:port" (what FormatUri makes of our URLs: no trailing slash).</summary>
            public string BaseUrl => "http://127.0.0.1:" + Server.Port;

            public static string DataPackDir
            {
                get
                {
                    string dir = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
                    if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data";
                    return Directory.Exists(dir) ? dir : null;
                }
            }

            public dynamic Federation(string protocol = "Unspecified") =>
                Activator.CreateInstance(game.Op("Federation"), ClientId, BaseUrl + "/", null, 60000, 3,
                                         (object)game.Enum(game.Op("Federation+Protocol"), protocol));

            public void Restart()
            {
                Server.Dispose();
                Start();
            }

            public void Covered(string flow)
            {
                coverage.Add(flow);
                output.WriteLine("OK  " + flow);
            }

            /// <summary>
            /// What GameProfileSection.FromGameObject saves: TObject.FromObject(player).ToString("j"). Without the
            /// engine we serialize the game's own DefaultProfile.tx (from the player's data pack) with the same
            /// Gameloft.Templates writer; without a data pack, a hand-written dump of the same shape.
            /// </summary>
            public string GameProfileDump(GameClient game)
            {
                string tx = null;
                if (DataPackDir != null && new DataPack(DataPackDir).TryReadText("DefaultProfile.tx", out string text)) tx = text;
                if (tx == null)
                {
                    output.WriteLine("no data pack: using the synthetic dump from the storage tests");
                    return McvsOffline.Tests.Storage.GameData.GameProfileDump;
                }
                return Canonical(game, tx);
            }

            /// <summary>A template text read and written back by the game's Gameloft.Templates ("j" format).</summary>
            public string Canonical(GameClient game, string templateText)
            {
                Type reader = game.Type("Gameloft.Templates", "Gameloft.Templates.TemplateReader");
                dynamic templateReader = GameClient.CallStatic(reader, "FromStream", new MemoryStream(Encoding.UTF8.GetBytes(templateText)), "DefaultProfile.tx", null);
                dynamic template = templateReader.ReadDefaultTemplate();
                return template.ToString("j");
            }

            public async Task<dynamic> LoadCurrentMatch(GameClient game, object gs, object fedId, CancellationToken ct)
            {
                dynamic request = GameClient.CallStatic(game.Op("StorageApi"), "GetProfileRequest", gs, fedId, "storage_admin", Selector + ".currentMatch", new List<string>());
                return await GameClient.Await(GameClient.Call(gs, "JsonRequest", request, ct, "", "", 0));
            }

            /// <summary>No unknown endpoint, no 403, no 5xx; 4xx only where the protocol expects them.</summary>
            public void AssertCleanRequestLog()
            {
                var requests = new List<string>();
                var problems = new List<string>();
                var timed = new List<KeyValuePair<double, string>>();
                foreach (LogEntry e in logs.Snapshot())
                {
                    if (e.Category != "http") continue;
                    Match m = RequestLine.Match(e.Message ?? "");
                    if (!m.Success) continue;
                    int status = int.Parse(m.Groups[4].Value);
                    string route = m.Groups[5].Value;
                    string line = m.Groups[2].Value + " " + Path(m.Groups[3].Value) + " -> " + status + " [" + route + "]";
                    requests.Add(line);
                    timed.Add(new KeyValuePair<double, string>(double.Parse(m.Groups[7].Value, System.Globalization.CultureInfo.InvariantCulture),
                                                               line + " (" + m.Groups[6].Value + " B)"));
                    if (route == "unknown" || status == 403 || status >= 500 || status >= 400) problems.Add(line);
                }
                output.WriteLine("");
                output.WriteLine(requests.Count + " requests made by the real client:");
                foreach (var group in requests.GroupBy(r => r).OrderBy(g => g.Key)) output.WriteLine("  " + group.Count() + "x " + group.Key);
                output.WriteLine("slowest (server-side time):");
                foreach (var t in timed.OrderByDescending(t => t.Key).Take(5))
                    output.WriteLine("  " + t.Key.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms  " + t.Value);
                Assert.True(problems.Count == 0, "unexpected answers:\n" + string.Join("\n", problems));
                Assert.DoesNotContain(logs.Snapshot(), e => e.Message != null && e.Message.Contains("UNKNOWN ENDPOINT"));
            }

            public void WriteCoverage()
            {
                output.WriteLine("");
                output.WriteLine("flows that ran through the real client code: " + coverage.Count);
            }

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }

            private void Start()
            {
                var options = new ServerOptions
                {
                    DataDir = dir.Combine("data"), Port = port, ConsoleLog = false, FileLog = false, DataPackDir = DataPackDir,
                };
                options.ExtraSinks.Add(logs);
                Server = McvsServer.Create(options);
                configure?.Invoke(Server.Context.Settings);
                Server.Start();
                port = Server.Port; // the restart keeps the port, like the device
            }

            /// <summary>The path with ids and tokens folded, for a readable request summary.</summary>
            private static string Path(string target)
            {
                string p = target;
                int q = p.IndexOf('?');
                string query = q >= 0 ? p.Substring(q + 1) : null;
                if (q >= 0) p = p.Substring(0, q);
                p = Regex.Replace(p, "fed_id(%3A|:)[0-9a-f]+", "fed_id:<id>", RegexOptions.IgnoreCase);
                p = Regex.Replace(p, "2620:66102:1\\.17\\.32:android:googleplay", "{cid}");
                p = Regex.Replace(p, "/[1-9][0-9]{8}$", "/<alias>");
                if (query != null && query.Contains("include_fields")) p += "?include_fields=...";
                else if (query != null && query.Contains("device_id")) p += "?device_id=...";
                return p;
            }
        }

        /// <summary>Records the Alert arguments of AlertListener.AlertReceived.</summary>
        private sealed class AlertRecorder
        {
            private readonly List<object> items = new List<object>();

            public int Count { get { lock (items) return items.Count; } }

            public List<object> Items { get { lock (items) return new List<object>(items); } }

            public void Attach(object listener)
            {
                EventInfo e = listener.GetType().GetEvent("AlertReceived");
                e.AddEventHandler(listener, Delegate.CreateDelegate(e.EventHandlerType, this, GetType().GetMethod(nameof(OnEvent))));
            }

            public void OnEvent(object sender, object args)
            {
                lock (items)
                {
                    items.Add(args);
                    Monitor.PulseAll(items);
                }
            }

            public bool WaitFor(int count, int timeoutMs)
            {
                int deadline = Environment.TickCount + timeoutMs;
                lock (items)
                {
                    while (items.Count < count)
                    {
                        int left = deadline - Environment.TickCount;
                        if (left <= 0) return false;
                        Monitor.Wait(items, left);
                    }
                    return true;
                }
            }
        }
    }
}
