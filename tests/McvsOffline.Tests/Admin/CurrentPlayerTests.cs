using System;
using System.Collections.Generic;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>Who "the current player" is (design §6.6): game, session, device, single, none.</summary>
    public class CurrentPlayerTests
    {
        private const string AndroidClient = "2620:66102:1.17.32:android:googleplay";

        private sealed class Rig : IDisposable
        {
            private readonly TempDir dir = new TempDir();

            public Rig()
            {
                Server = AdminTestClient.CreateServer(dir.Combine("data"));
                Host = new FakeAdminHost();
                Host.Info.DeviceId = "device-1";
                Server.Context.Features.Set<IAdminHost>(Host);
            }

            public McvsServer Server { get; }
            public FakeAdminHost Host { get; }
            public AdminApi Api => Server.Context.Features.Get<AdminApi>();
            public CurrentPlayer Current => Api.CurrentPlayer();

            /// <summary>The player's own login, as the game's client sends it (OP/Session.cs GetAuthorizeRequest).</summary>
            public string Authorize(string username, string deviceId, string clientId = AndroidClient)
            {
                Assert.Equal(200, Server.Handle(GameLogin(username, deviceId, clientId)).StatusCode);
                return Server.Context.Accounts.Find("anonymous:" + username).Id;
            }

            public void SaveGameProfile(string account)
            {
                var access = Server.Context.Features.Get<IProfileAccess>();
                access.Set(account, "_mcvs.gameProfile", new Dictionary<string, object> { ["@version"] = "0.1" });
            }

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }
        }

        /// <summary>POST authorize with the form and the User-Agent of the game's client, nothing else.</summary>
        internal static HttpRequest GameLogin(string username, string deviceId, string clientId = AndroidClient)
        {
            HttpRequest request = HttpRequest.Create("POST", "/authorize",
                "credential_type=anonymous&username=" + username + "&password=x"
                + (deviceId == null ? "" : "&device_id=" + deviceId)
                + "&client_id=" + Uri.EscapeDataString(clientId) + "&scope=auth%20storage", "application/x-www-form-urlencoded");
            request.Headers.Add("Cache-Control", "NO-CACHE");
            request.Headers.Add("User-Agent", "(" + clientId + "; mcvs)");
            return request;
        }

        /// <summary>
        /// What a web page on the phone (or the PC of the standalone host) can send to 127.0.0.1 without reading the
        /// answer: authorize takes any anonymous name without a password and the Federation Host rule lets 127.0.0.1
        /// through. None of it may make its account "the current player": the page would then back up, unlock and relink
        /// the wrong account (design §6.6).
        /// </summary>
        private static List<Func<string, HttpRequest>> WebPageLogins()
        {
            const string chrome = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36";
            const string old = "Mozilla/5.0 (Linux; Android 7.0; SM-G930F Build/NRD90M; wv) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/64.0 Mobile Safari/537.36";
            string Query(string user) => "client_id=x:android:x&username=" + user + "&credential_type=anonymous&scope=storage";
            HttpRequest With(HttpRequest request, params string[] headers)
            {
                for (int i = 0; i < headers.Length; i += 2) request.Headers.Add(headers[i], headers[i + 1]);
                return request;
            }
            return new List<Func<string, HttpRequest>>
            {
                // <img src> in a current browser: GET with Fetch Metadata, Origin and Referer.
                user => With(HttpRequest.Create("GET", "/authorize?" + Query(user)), "User-Agent", chrome, "Origin", "https://evil.example",
                             "Referer", "https://evil.example/", "Sec-Fetch-Site", "cross-site", "Sec-Fetch-Mode", "no-cors", "Sec-Fetch-Dest", "image"),
                // An old WebView's <img referrerpolicy="no-referrer">, even with a device id: the method and the User-Agent tell.
                user => With(HttpRequest.Create("GET", "/authorize?" + Query(user) + "&device_id=device-1"), "User-Agent", old),
                // Its <form method=post> (no Origin before Chromium 76 / Firefox 70 with no-referrer).
                user => With(HttpRequest.Create("POST", "/authorize", Query(user) + "&device_id=device-1", "application/x-www-form-urlencoded"),
                             "User-Agent", old),
                // A current browser's form POST: Origin and Sec-Fetch-*, whatever else the form claims.
                user => With(HttpRequest.Create("POST", "/authorize", "credential_type=anonymous&username=" + user + "&device_id=device-1&client_id="
                                                                     + Uri.EscapeDataString(AndroidClient) + "&scope=storage",
                                                "application/x-www-form-urlencoded"),
                             "User-Agent", "(" + AndroidClient + "; mcvs)", "Origin", "null", "Sec-Fetch-Site", "cross-site"),
                // The game's form and User-Agent, but no device id (the game always sends one).
                user => GameLogin(user, null),
                // A LAN guest's own login: the game's request, from another device.
                user =>
                {
                    HttpRequest guest = GameLogin(user, "device-1");
                    guest.RemoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("192.168.1.23"), 40001);
                    return guest;
                },
            };
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AWebPageCannotBecomeTheCurrentPlayer(bool withHost)
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var host = new FakeAdminHost();
            host.Info.DeviceId = "device-1";
            host.Info.GameCredential = null; // not read yet: the next sources decide
            if (withHost) server.Context.Features.Set<IAdminHost>(host);
            var api = server.Context.Features.Get<AdminApi>();

            Assert.Equal(200, server.Handle(GameLogin("realplayer0123456789", "device-1")).StatusCode);
            string player = server.Context.Accounts.Find("anonymous:realplayer0123456789").Id;
            Assert.Equal("session", api.CurrentPlayer().Source);
            Assert.Equal(player, api.CurrentPlayer().AccountId);

            List<Func<string, HttpRequest>> logins = WebPageLogins();
            for (int i = 0; i < logins.Count; i++)
            {
                // Every one is a valid login of its own account, just not the player's.
                string name = "attacker00000000000" + i;
                Assert.Equal(200, server.Handle(logins[i](name)).StatusCode);
                Assert.NotNull(server.Context.Accounts.Find("anonymous:" + name));
                CurrentPlayer current = api.CurrentPlayer();
                Assert.True(current.Source == "session" && current.AccountId == player, "login " + i + " made " + current.AccountId + " (" + current.Source + ") current");
                Assert.Equal("anonymous:realplayer0123456789", current.Credential);
            }

            // The game's next login still counts.
            Assert.Equal(200, server.Handle(GameLogin("realplayer0123456789", "device-1")).StatusCode);
            Assert.Equal(player, api.CurrentPlayer().AccountId);
        }

        [Fact]
        public void TheGamesOwnCredentialWins()
        {
            using var rig = new Rig();
            string other = rig.Authorize("someoneElse", "device-1");
            string mine = rig.Server.Context.Accounts.GetOrCreate("anonymous:player1").Id;
            rig.Host.Info.GameCredential = "anonymous:player1";

            CurrentPlayer current = rig.Current;
            Assert.Equal("game", current.Source);
            Assert.Equal(mine, current.AccountId);
            Assert.Equal("anonymous:player1", current.Credential);
            Assert.NotEqual(other, mine);

            // A credential the server never saw: the next rule decides.
            rig.Host.Info.GameCredential = "anonymous:neverAuthorized";
            Assert.Equal("session", rig.Current.Source);
        }

        [Fact]
        public void TheLastLoginCountsOnlyFromTheGamesClientOnThisDevice()
        {
            using var rig = new Rig();
            string account = rig.Authorize("player1", "device-1");
            CurrentPlayer current = rig.Current;
            Assert.Equal("session", current.Source);
            Assert.Equal(account, current.AccountId);
            Assert.Equal("anonymous:player1", current.Credential);

            // Another app logs in with its own anonymous name from a foreign device id: not the player.
            rig.Authorize("intruder", "device-9");
            Assert.NotEqual("session", rig.Current.Source);
            Assert.NotEqual(rig.Server.Context.Accounts.Find("anonymous:intruder").Id, rig.Current.AccountId);
        }

        /// <summary>The Steam build's own login counts like Android's (docs/design/pc-port.md §4.4).</summary>
        [Fact]
        public void TheSteamClientsLoginIsTheSession()
        {
            using var rig = new Rig();
            string account = rig.Authorize("76561198000000001", "device-1", McvsConstants.ClientIdSteam);
            CurrentPlayer current = rig.Current;
            Assert.Equal("session", current.Source);
            Assert.Equal(account, current.AccountId);
            Assert.Equal("anonymous:76561198000000001", current.Credential);
        }

        [Theory]
        [InlineData("2620:70425:1.17.30:ios:appstore")]   // a platform we do not serve
        [InlineData("2620:70425:1.17.30:Steam:steam")]    // platforms compare exactly
        [InlineData("steam")]                              // not a client id at all
        public void AnUnknownPlatformIsRefused(string clientId)
        {
            Assert.False(McvsConstants.IsGameClientId(clientId));
            using var rig = new Rig();
            rig.Authorize("tool", "device-1", clientId);
            Assert.NotEqual("session", rig.Current.Source);
        }

        [Fact]
        public void BothKnownClientIdsAreGameClientIds()
        {
            Assert.True(McvsConstants.IsGameClientId(McvsConstants.ClientId));
            Assert.True(McvsConstants.IsGameClientId(McvsConstants.ClientIdSteam));
            Assert.True(McvsConstants.IsGameClientId("2620:66102:1.17.40:android:googleplay")); // any version
            Assert.False(McvsConstants.IsGameClientId(null));
        }

        [Fact]
        public void ANonGameClientIsIgnored()
        {
            using var rig = new Rig();
            rig.Authorize("tool", "device-1", "1234:5678:1.0:windows:steam");
            CurrentPlayer current = rig.Current;
            Assert.NotEqual("session", current.Source);
            // ...but the account it created is the only one of this device.
            Assert.Equal("device", current.Source);
            Assert.Null(current.Credential);
        }

        [Fact]
        public void TheSessionFollowsARelink()
        {
            using var rig = new Rig();
            rig.Authorize("player1", "device-1");
            string imported = rig.Server.Context.Accounts.GetOrCreate("fed_id:fedcba9876543210fedcba98").Id;
            Assert.True(rig.Server.Context.Accounts.LinkCredential(imported, "anonymous:player1"));
            Assert.Equal(imported, rig.Current.AccountId);
            Assert.Equal("session", rig.Current.Source);
        }

        [Fact]
        public void TheDevicesOnlyAccountThenTheOnlyGameProfile()
        {
            using var rig = new Rig();
            Assert.Null(rig.Current.Source);
            Assert.Null(rig.Current.AccountId);

            string a = rig.Server.Context.Accounts.GetOrCreate("anonymous:a", "device-1").Id;
            Assert.Equal("device", rig.Current.Source);
            Assert.Equal(a, rig.Current.AccountId);
            string b = rig.Server.Context.Accounts.GetOrCreate("anonymous:b", "device-1").Id;
            Assert.Null(rig.Current.Source); // two accounts on this device and no profile

            rig.SaveGameProfile(b);
            Assert.Equal("single", rig.Current.Source);
            Assert.Equal(b, rig.Current.AccountId);
            rig.SaveGameProfile(a);
            Assert.Null(rig.Current.Source);
        }

        [Fact]
        public void WithoutAHostOnlyTheLastLoginAndTheProfilesCount()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var api = server.Context.Features.Get<AdminApi>();
            Assert.Null(api.CurrentPlayer().Source);
            server.Handle(GameLogin("pc", "d"));
            Assert.Equal("session", api.CurrentPlayer().Source);
        }
    }
}
