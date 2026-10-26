using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>TokenService.LastDirectAnonymous (design §6.6) and RevokeCredential (§6.4 step 7).</summary>
    public class TokenServiceAdminTests
    {
        private const string Client = "2620:66102:1.17.32:android:googleplay";
        private static readonly string[] Scopes = { "auth", "storage" };

        private static TokenService Create(TempDir dir) => new TokenService(dir.Combine("tokens.json"), "mcvs1234abcd", null, null);

        [Fact]
        public void OnlyDirectAnonymousGrantsCount()
        {
            using var dir = new TempDir();
            var tokens = Create(dir);
            Assert.Null(tokens.LastDirectAnonymous);

            tokens.Issue("aaaaaaaaaaaaaaaaaaaaaaaa", "anonymous:player1", Client, Scopes, "device-1");
            TokenInfo last = tokens.LastDirectAnonymous;
            Assert.Equal("anonymous:player1", last.Credential);
            Assert.Equal(Client, last.ClientId);
            Assert.Equal("device-1", last.DeviceId);
            Assert.Null(last.Actor);

            // The game server's on-behalf token for another player, its own game: grant, a fed_id credential: ignored.
            tokens.Issue("bbbbbbbbbbbbbbbbbbbbbbbb", "anonymous:player2", Client, Scopes, "device-2", "game:mcvsoffline");
            tokens.Issue("cccccccccccccccccccccccc", "game:mcvsoffline", Client, Scopes, "device-3");
            tokens.Issue("dddddddddddddddddddddddd", "fed_id:dddddddddddddddddddddddd", Client, Scopes);
            Assert.Equal("anonymous:player1", tokens.LastDirectAnonymous.Credential);

            // A copy: changing it changes nothing.
            last.Credential = "anonymous:changed";
            Assert.Equal("anonymous:player1", tokens.LastDirectAnonymous.Credential);
        }

        [Fact]
        public void AReissuedGrantBecomesTheLastOneWithItsNewDevice()
        {
            using var dir = new TempDir();
            var tokens = Create(dir);
            TokenInfo first = tokens.Issue("aaaaaaaaaaaaaaaaaaaaaaaa", "anonymous:player1", Client, Scopes, "device-1");
            tokens.Issue("bbbbbbbbbbbbbbbbbbbbbbbb", "anonymous:player2", "other:client", Scopes, "device-2");
            Assert.Equal("anonymous:player2", tokens.LastDirectAnonymous.Credential);

            TokenInfo again = tokens.Issue("aaaaaaaaaaaaaaaaaaaaaaaa", "anonymous:player1", Client, new[] { "storage", "auth" }, "device-9");
            Assert.Equal(first.Token, again.Token);
            TokenInfo last = tokens.LastDirectAnonymous;
            Assert.Equal("anonymous:player1", last.Credential);
            Assert.Equal("device-9", last.DeviceId);
            Assert.Equal(first.Token, last.Token);
        }

        [Fact]
        public void RevokeCredentialRemovesEveryGrantOfThatCredentialOnly()
        {
            using var dir = new TempDir();
            var tokens = Create(dir);
            TokenInfo own = tokens.Issue("aaaaaaaaaaaaaaaaaaaaaaaa", "anonymous:moved", Client, Scopes, "device-1");
            TokenInfo onBehalf = tokens.Issue("aaaaaaaaaaaaaaaaaaaaaaaa", "anonymous:moved", Client, new[] { "auth" }, null, "game:mcvsoffline");
            TokenInfo stays = tokens.Issue("bbbbbbbbbbbbbbbbbbbbbbbb", "anonymous:other", Client, Scopes, "device-2");
            TokenInfo prefix = tokens.Issue("cccccccccccccccccccccccc", "anonymous:moved2", Client, Scopes, "device-3");

            Assert.Equal(2, tokens.RevokeCredential("anonymous:moved"));
            Assert.Null(tokens.Resolve(own.Token));
            Assert.Null(tokens.Resolve(onBehalf.Token));
            Assert.NotNull(tokens.Resolve(stays.Token));
            Assert.NotNull(tokens.Resolve(prefix.Token));
            Assert.Equal(0, tokens.RevokeCredential("anonymous:moved"));
            Assert.Equal(0, tokens.RevokeCredential(null));

            // Persisted: a restart does not bring them back; a new authorize gets a new token.
            var reloaded = Create(dir);
            Assert.Null(reloaded.Resolve(own.Token));
            Assert.NotNull(reloaded.Resolve(stays.Token));
            Assert.NotEqual(own.Token, reloaded.Issue("dddddddddddddddddddddddd", "anonymous:moved", Client, Scopes, "device-1").Token);
        }
    }
}
