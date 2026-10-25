using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using Xunit;

namespace McvsOffline.Tests
{
    public class AccountsTests
    {
        [Theory]
        [InlineData("anonymous:abc", "anonymous", "abc")]
        [InlineData("Anonymous:AbC", "anonymous", "AbC")]
        [InlineData("game:mcvsoffline", "game", "mcvsoffline")]
        [InlineData("gamecenter:G:123:bundle", "gamecenter", "G:123:bundle")]
        [InlineData("fed_id:9f2c", "fed_id", "9f2c")]
        public void CredentialsParse(string text, string type, string username)
        {
            Assert.True(Credential.TryParse(text, out var c));
            Assert.Equal(type, c.Type);
            Assert.Equal(username, c.Username);
            Assert.True(c.IsValid);
        }

        [Theory]
        [InlineData("")]
        [InlineData("nocolon")]
        [InlineData(":user")]
        [InlineData("type:")]
        public void InvalidCredentialsAreRejected(string text)
        {
            Assert.False(Credential.TryParse(text, out _));
        }

        [Fact]
        public void MeIsSpecial()
        {
            Assert.True(Credential.TryParse("me", out var me));
            Assert.True(me.IsMe);
            Assert.Equal("me", me.ToString());
        }

        [Fact]
        public void AccountsAreCreatedOnceWithDeterministicIds()
        {
            using var dir = new TempDir();
            var store = new AccountStore(dir.Combine("accounts.json"), null, null);
            var a = store.GetOrCreate("anonymous:player1", "device-1");
            var again = store.GetOrCreate("ANONYMOUS:player1", "device-2");
            var b = store.GetOrCreate("anonymous:player2");

            Assert.Equal(a.Id, again.Id);
            Assert.NotEqual(a.Id, b.Id);
            Assert.Equal(24, a.Id.Length);
            Assert.DoesNotContain(":", a.Id);
            Assert.Equal(AccountStore.DeriveAccountId("anonymous:player1"), a.Id);
            Assert.Equal("fed_id:" + a.Id, a.FedId);
            Assert.Equal(new List<string> { "device-1", "device-2" }, store.FindById(a.Id).DeviceIds);
            Assert.Equal(2, store.Count);
        }

        [Fact]
        public void AccountsPersistAcrossInstances()
        {
            using var dir = new TempDir();
            string path = dir.Combine("accounts.json");
            var first = new AccountStore(path, null, null);
            var a = first.GetOrCreate("anonymous:p");
            first.Update(a.Id, acc => acc.Data["alias"] = "123456789");

            var second = new AccountStore(path, null, null);
            var loaded = second.Find("anonymous:p");
            Assert.Equal(a.Id, loaded.Id);
            Assert.Equal("123456789", loaded.Data["alias"]);
            Assert.Equal(a.Id, second.Find("fed_id:" + a.Id).Id);
            Assert.Null(second.Find("anonymous:unknown"));
            Assert.Null(second.Find("garbage"));
        }

        [Fact]
        public void ReturnedAccountsAreSnapshots()
        {
            using var dir = new TempDir();
            var store = new AccountStore(dir.Combine("accounts.json"), null, null);
            var a = store.GetOrCreate("anonymous:p");
            a.Credentials.Add("anonymous:intruder");
            Assert.Null(store.Find("anonymous:intruder"));
        }

        [Fact]
        public void LinkingMovesACredentialBetweenAccounts()
        {
            using var dir = new TempDir();
            var store = new AccountStore(dir.Combine("accounts.json"), null, null);
            var a = store.GetOrCreate("anonymous:a");
            var b = store.GetOrCreate("anonymous:b");
            Assert.True(store.LinkCredential(a.Id, "anonymous:b"));
            Assert.Equal(a.Id, store.Find("anonymous:b").Id);
            Assert.DoesNotContain("anonymous:b", store.FindById(b.Id).Credentials);
            Assert.False(store.LinkCredential("nope", "anonymous:c"));
        }

        [Fact]
        public void DevicesCanBeLookedUp()
        {
            using var dir = new TempDir();
            var store = new AccountStore(dir.Combine("accounts.json"), null, null);
            store.GetOrCreate("anonymous:a", "dev");
            store.GetOrCreate("anonymous:b", "other");
            Assert.Single(store.FindByDevice("dev"));
            Assert.Empty(store.FindByDevice("none"));
        }

        [Fact]
        public void CorruptAccountFileIsSetAsideAndIdsStayStable()
        {
            using var dir = new TempDir();
            string path = dir.Combine("accounts.json");
            string id = new AccountStore(path, null, null).GetOrCreate("anonymous:p").Id;
            File.WriteAllText(path, "{not json");
            var store = new AccountStore(path, null, null);
            Assert.Equal(0, store.Count);
            Assert.Equal(id, store.GetOrCreate("anonymous:p").Id); // same profile is found again
            Assert.Single(Directory.GetFiles(dir.Path, "accounts.json.corrupt-*"));
        }

        [Fact]
        public void TokensAreSelfDescribingAndResolvable()
        {
            using var dir = new TempDir();
            var tokens = new TokenService(dir.Combine("tokens.json"), "mcvsdeadbeef", null, null);
            var t = tokens.Issue("acc1", "anonymous:p", McvsConstants.ClientId, new[] { "auth", "lobby", "config" }, "dev1");
            Assert.StartsWith("mcvsdeadbeef.", t.Token);
            Assert.Equal(13 + 32, t.Token.Length);
            Assert.DoesNotContain("~", t.Token);
            Assert.Equal("mcvsdeadbeef", TokenService.GetIssuer(t.Token));
            Assert.True(tokens.IsLocal(t.Token));
            Assert.False(tokens.IsLocal("mcvsother.abc"));
            Assert.Equal("auth lobby config", t.ScopeString);

            var resolved = tokens.Resolve(t.Token);
            Assert.Equal("acc1", resolved.AccountId);
            Assert.Equal("anonymous:p", resolved.Credential);
            Assert.Equal("fed_id:acc1", resolved.FedId);
            Assert.False(resolved.IsOnBehalf);
            Assert.Null(tokens.Resolve("mcvsdeadbeef.unknown"));
            Assert.Null(tokens.Resolve(""));
        }

        [Fact]
        public void SameGrantReusesTheTokenAndEchoesTheRequestedScopeOrder()
        {
            using var dir = new TempDir();
            var tokens = new TokenService(dir.Combine("tokens.json"), "mcvs1", null, null);
            var a = tokens.Issue("acc", "anonymous:p", "cid", new[] { "auth", "lobby" });
            var b = tokens.Issue("acc", "anonymous:p", "cid", new[] { "lobby", "auth" });
            var c = tokens.Issue("acc", "anonymous:p", "cid", new[] { "auth" });
            var onBehalf = tokens.Issue("acc", "anonymous:p", "cid", new[] { "auth", "lobby" }, actor: "game:mcvsoffline");
            Assert.Equal(a.Token, b.Token);
            Assert.Equal("lobby auth", b.ScopeString);
            Assert.NotEqual(a.Token, c.Token);
            Assert.NotEqual(a.Token, onBehalf.Token);
            Assert.True(tokens.Resolve(onBehalf.Token).IsOnBehalf);
            Assert.Equal(3, tokens.Count);
        }

        [Fact]
        public void TokensPersistAndOldestAreEvicted()
        {
            using var dir = new TempDir();
            string path = dir.Combine("tokens.json");
            var tokens = new TokenService(path, "mcvs1", null, null, capacity: 16);
            var first = tokens.Issue("acc", "anonymous:p", "cid", new[] { "s0" });
            for (int i = 1; i < 20; i++) tokens.Issue("acc", "anonymous:p", "cid", new[] { "s" + i });
            Assert.Equal(16, tokens.Count);
            Assert.Null(tokens.Resolve(first.Token));

            var reloaded = new TokenService(path, "mcvs1", null, null, capacity: 16);
            Assert.Equal(16, reloaded.Count);
            var last = reloaded.Issue("acc", "anonymous:p", "cid", new[] { "s19" });
            Assert.Equal(tokens.Issue("acc", "anonymous:p", "cid", new[] { "s19" }).Token, last.Token);
        }

        [Fact]
        public void ScopeParsingAcceptsSpacesCommasAndPlus()
        {
            Assert.Equal(new List<string> { "auth", "lobby", "config" }, TokenService.ParseScopes("auth lobby+config,auth"));
            Assert.Empty(TokenService.ParseScopes(null));
        }
    }
}
