using System;
using System.Collections.Generic;
using McvsOffline.Server;
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

            /// <summary>The player's own login, as the game's client sends it.</summary>
            public string Authorize(string username, string deviceId, string clientId = AndroidClient)
            {
                var response = Server.Handle(HttpRequest.Create("POST", "/authorize",
                    "credential_type=anonymous&username=" + username + "&password=x&device_id=" + deviceId
                    + "&client_id=" + Uri.EscapeDataString(clientId) + "&scope=auth%20storage"));
                Assert.Equal(200, response.StatusCode);
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
            server.Handle(HttpRequest.Create("POST", "/authorize",
                "credential_type=anonymous&username=pc&password=x&device_id=d&client_id=" + Uri.EscapeDataString(AndroidClient) + "&scope=auth"));
            Assert.Equal("session", api.CurrentPlayer().Source);
        }
    }
}
