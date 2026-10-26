using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Tests.Admin;
using McvsOffline.Tests.Auth;
using McvsOffline.Tests.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The hidden redeem code that opens the settings page (design docs/design/settings-page.md §9.5, WP7): the admin
    /// module with a fake host over <see cref="McvsServer.Handle"/>, and the game's own coupon request from the real
    /// client over HTTP.
    /// </summary>
    public class RedeemPageCodeTests
    {
        private const string Player = "anonymous:redeemer0123456789abcdef";
        private static readonly Regex PageUrl = new Regex("^http://127\\.0\\.0\\.1:51780/_mcvs/ui/#t=[0-9a-f]{32}$");

        private readonly ITestOutputHelper output;

        public RedeemPageCodeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>A server with the admin module, a fake host whose game credential is the player's, and a fake clock.</summary>
        private sealed class Rig : IDisposable
        {
            private readonly TempDir dir = new TempDir();

            public Rig(bool withHost = true)
            {
                Host = new FakeAdminHost();
                Clock = new ManualClock(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Utc));
                Logs = new MemoryLogSink();
                Server = AdminTestClient.CreateServer(dir.Combine("data"), Logs, (r, c) =>
                {
                    if (withHost) c.Features.Set<IAdminHost>(Host);
                }, Clock);
                Account account = Server.Context.Accounts.GetOrCreate(Player, "fake-device");
                AccountId = account.Id;
                Host.Info.GameCredential = Player; // the current player, as the host reads it on the game thread
                // The menu server's token for the player (for_username grant), as MiscTestSupport makes it.
                OnBehalfToken = Server.Context.Tokens.Issue(account.Id, Player, McvsConstants.ClientId, GameScopes.Server, "fake-device",
                                                            "game:mcvsoffline").Token;
            }

            public FakeAdminHost Host { get; }

            public ManualClock Clock { get; }

            public MemoryLogSink Logs { get; }

            public McvsServer Server { get; }

            public string AccountId { get; }

            public string OnBehalfToken { get; }

            public HttpResponse Redeem(string code, string token = null, IPEndPoint remote = null)
            {
                string target = "/coupons/" + ContentTestSupport.ClientId + "/" + code
                                + (token == null ? "" : "?access_token=" + Uri.EscapeDataString(token));
                var headers = new HttpHeaders();
                headers.Add("Host", "127.0.0.1:51780");
                var request = new HttpRequest("GET", target, headers, null);
                request.RemoteEndPoint = remote ?? new IPEndPoint(IPAddress.Loopback, 40001);
                return Server.Handle(request);
            }

            public int Opened => Host.Calls.Count(c => c == "OpenPage:redeem");

            public bool Logged(string code, string outcome) =>
                Logs.Contains("coupon '" + code + "': opens the MCVS Offline page (" + outcome + ")");

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }
        }

        private static void AssertDoesNotExist(HttpResponse response)
        {
            // 404 is what the game maps to CouponDoesNotExist; it claims nothing and adds no inbox message.
            Assert.Equal(404, response.StatusCode);
            Assert.Contains("coupon does not exist", response.BodyText);
        }

        [Fact]
        public void TheGamesRequestOpensThePageAndAnswersDoesNotExist()
        {
            using var rig = new Rig();
            HttpResponse response = rig.Redeem("mcvsoffline"); // no token: what the game sends (see the real-client test)
            AssertDoesNotExist(response);
            Assert.Equal(rig.Redeem("NOSUCHCODE").BodyText, response.BodyText); // the same answer as an unknown code
            Assert.Equal(new[] { "OpenPage:redeem" }, rig.Host.Calls);
            Assert.Matches(PageUrl, Assert.Single(rig.Host.OpenedUrls)); // a fresh one-time ticket, only to the host
            Assert.True(rig.Logged("mcvsoffline", "requested"));
            Assert.DoesNotContain(rig.Logs.Snapshot(), e => e.Message.Contains("#t="));
        }

        [Fact]
        public void TheCurrentPlayersTokenOpensItAlsoWithCouponsTurnedOff()
        {
            using var rig = new Rig();
            rig.Server.Context.Settings.Override("assets.couponsEnabled", false);
            AssertDoesNotExist(rig.Redeem(" McvsOffline ", rig.OnBehalfToken));
            Assert.Equal(1, rig.Opened);
            AssertDoesNotExist(rig.Redeem("UNLOCKMERCS", rig.OnBehalfToken)); // coupons stay off
        }

        [Fact]
        public void ASecondRequestWithin30SecondsIsIgnored()
        {
            using var rig = new Rig();
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
            rig.Clock.Advance(TimeSpan.FromSeconds(29));
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
            Assert.Equal(1, rig.Opened);
            Assert.True(rig.Logged("MCVSOFFLINE", "ignored: opened less than 30 s ago"));
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
            Assert.Equal(2, rig.Opened);
        }

        [Fact]
        public void OtherCallersAreIgnoredWithTheReason()
        {
            using var rig = new Rig();
            Account other = rig.Server.Context.Accounts.GetOrCreate("anonymous:someoneelse0123456789", "other-device");
            string otherToken = rig.Server.Context.Tokens.Issue(other.Id, "anonymous:someoneelse0123456789", McvsConstants.ClientId,
                                                                GameScopes.Server, "other-device", "game:mcvsoffline").Token;

            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE", otherToken));
            Assert.True(rig.Logged("MCVSOFFLINE", "ignored: the access token is another account's"));
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE", "not-a-token"));
            Assert.True(rig.Logged("MCVSOFFLINE", "ignored: unknown access token"));
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE", rig.OnBehalfToken, new IPEndPoint(IPAddress.Parse("192.168.1.5"), 40001)));
            Assert.True(rig.Logged("MCVSOFFLINE", "ignored: not from this phone"));
            rig.Host.Info.BrowserOpen = true;
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE", rig.OnBehalfToken));
            Assert.True(rig.Logged("MCVSOFFLINE", "ignored: the in-game browser is already open"));
            Assert.Equal(0, rig.Opened);

            // None of them used up the 30 s: the player's own request still works at once.
            rig.Host.Info.BrowserOpen = false;
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE", rig.OnBehalfToken, new IPEndPoint(IPAddress.Parse("::ffff:127.0.0.1"), 40001)));
            Assert.Equal(1, rig.Opened);
        }

        [Fact]
        public void NoHostOrATurnedOffPageOpensNothing()
        {
            using (var rig = new Rig(withHost: false))
            {
                AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
                Assert.True(rig.Logged("MCVSOFFLINE", "ignored: no in-game browser here"));
            }
            using (var rig = new Rig())
            {
                rig.Server.Context.Settings.Override("admin.enabled", false);
                AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
                Assert.True(rig.Logged("MCVSOFFLINE", "ignored: the settings page is turned off (admin.enabled)"));
                Assert.Equal(0, rig.Opened);
            }
        }

        [Fact]
        public void AnEmptyCodeTurnsItOffAndOtherCodesAreUnchanged()
        {
            using var rig = new Rig();
            rig.Server.Context.Settings.Override("admin.redeemCode", "");
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
            Assert.Equal(0, rig.Opened);
            Assert.True(rig.Logs.Contains("coupon 'MCVSOFFLINE' does not exist"));
            Assert.False(rig.Logs.Contains("opens the MCVS Offline page"));

            HttpResponse coupon = rig.Redeem("UNLOCKMERCS");
            Assert.Equal(200, coupon.StatusCode);
            Assert.StartsWith("[{\"type\":", coupon.BodyText);
            Assert.Equal(0, rig.Opened);
        }

        [Fact]
        public void TheCodeIsTheSetting()
        {
            using var rig = new Rig();
            rig.Server.Context.Settings.Set("admin.redeemCode", "OPENPAGE1"); // as the page writes it (live)
            AssertDoesNotExist(rig.Redeem("MCVSOFFLINE"));
            Assert.Equal(0, rig.Opened);
            AssertDoesNotExist(rig.Redeem("openpage1"));
            Assert.Equal(1, rig.Opened);
            Assert.True(rig.Logged("openpage1", "requested"));
        }

        /// <summary>
        /// The game's own redemption, through the real Gameloft.OnlinePlatform over HTTP: the menu server's on-behalf
        /// session (SessionForUser, as the TS gets it) calls AssetsService.RedeemCoupon. The request carries no access
        /// token (AssetsApi.GetRedeemCouponRequest has no scopes), the client gets the 404 it maps to "code does not
        /// exist", and the page opens once with a fresh ticket.
        /// </summary>
        [Fact]
        public async Task TheRealClientsRedemptionOpensThePage()
        {
            GameClient game = GameClient.TryGet(output, "Gameloft.Util");
            if (game == null) return;
            var ct = new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token;
            using var dir = new TempDir();
            var host = new FakeAdminHost();
            var logs = new MemoryLogSink(LogLevel.Debug, 100000);
            using var server = AdminTestClient.CreateServer(dir.Combine("data"), logs, (r, c) => c.Features.Set<IAdminHost>(host),
                                                            configure: o => o.Port = 0);
            server.Start();
            string baseUrl = "http://127.0.0.1:" + server.Port;
            dynamic Federation() => Activator.CreateInstance(game.Op("Federation"), FedFederation.GameClientId, baseUrl + "/", null, 60000, 3,
                                                             (object)game.Enum(game.Op("Federation+Protocol"), "Unspecified"));

            // The player's own login creates the account; the host reports that credential as the game's.
            string user = "mcvs" + new string('5', 32);
            dynamic credential = game.Credential("anonymous:" + user);
            dynamic fed = Federation();
            await GameClient.Await(fed.GetConfig(ct));
            dynamic client = await GameClient.Await(fed.OpenSession(credential, "f3c1a9d2e8b74c55", "mcvs" + new string('7', 32), null,
                                                                    new List<string>(GameScopes.Client), ct));
            Assert.True((bool)client.IsOpen);
            host.Info.GameCredential = "anonymous:" + user;

            // The menu server: game-server credential, session for the player (EN/Connection.cs SessionForUser).
            dynamic ts = await GameClient.Await(Federation().OpenSessionForUser(game.Credential("game:mcvsoffline"), "mcvsoffline", credential,
                                                                               "mcvs" + new string('7', 32), null,
                                                                               new List<string>(GameScopes.Server), ct));
            Exception failure = null;
            try
            {
                await GameClient.Await(game.Service(ts, "AssetsService").RedeemCoupon("MCVSOFFLINE", ct));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            Assert.NotNull(failure);
            Assert.Equal(404, GameClient.StatusOf(failure)); // AssetsComponent.RedeemCoupon: CouponError.NotFound, nothing claimed

            Assert.Equal(new[] { "OpenPage:redeem" }, host.Calls);
            Assert.Matches(new Regex("^http://127\\.0\\.0\\.1:" + server.Port + "/_mcvs/ui/#t=[0-9a-f]{32}$"), Assert.Single(host.OpenedUrls));
            Assert.True(logs.Contains("coupon 'MCVSOFFLINE': opens the MCVS Offline page (requested)"));
            List<string> requests = logs.Snapshot().Where(e => e.Category == "http" && e.Message != null && e.Message.Contains("/coupons/"))
                                        .Select(e => e.Message).ToList();
            Assert.NotEmpty(requests);
            foreach (string line in requests)
            {
                output.WriteLine(line.Split('\n')[0]);
                Assert.DoesNotContain("access_token", line);
            }
        }
    }
}
