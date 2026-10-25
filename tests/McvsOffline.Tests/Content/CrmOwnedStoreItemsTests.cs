using System;
using System.Collections.Generic;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using Xunit;

namespace McvsOffline.Tests.Content
{
    /// <summary>The owned kill signatures left out of a player's offline_store (CrmOwnedStoreItems).</summary>
    public class CrmOwnedStoreItemsTests
    {
        private static readonly string[] ServerScopes = { "auth", "storage", "storage_ro", "storage_restricted", "storage_admin", "config" };

        [Fact]
        public void ScanReadsTheSignatureListsOfTheDump()
        {
            string dump = "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\","
                          + "\"KillSignatureAvatars\" : [ \"1\", \"22\",5 ],\"KillSignatureBackgrounds\":[],"
                          + "\"KillSignatureSounds\":[\"2\"],\"Name\":\"KillSignatureAvatars\","
                          + "\"Note\":\"\\\"KillSignatureBackgrounds\\\":[\\\"9\\\"]\"}}}";
            var set = CrmOwnedStoreItems.Scan(dump, out string problem);
            Assert.Null(problem);
            Assert.Equal(new[] { "KillSignature_Avatar_1", "KillSignature_Avatar_22", "KillSignature_Avatar_5" }, Sorted(set.Names));
            Assert.Equal(3, set.Avatars);
            Assert.Equal(0, set.Backgrounds);

            var none = CrmOwnedStoreItems.Scan("{\"$default\":{\"/Profile\":{\"Version\":57}}}", out problem);
            Assert.Null(problem);
            Assert.Empty(none.Names);
        }

        [Theory]
        [InlineData("{\"a\":{\"KillSignatureAvatars\":[\"1\"]},\"b\":{\"KillSignatureAvatars\":[\"2\"]}}", "appears more than once")]
        [InlineData("{\"KillSignatureAvatars\":[{\"@value\":\"1\"}]}", "not an array of ids")]
        [InlineData("{\"KillSignatureBackgrounds\":{}}", "not an array of ids")]
        [InlineData("{\"KillSignatureAvatars\":[\"1\\u0031\"]}", "not an array of ids")]
        [InlineData("{\"KillSignatureAvatars\":[\"1\",]}", "not an array of ids")]
        [InlineData("{\"KillSignatureAvatars\":[\"1\"", "not an array of ids")]
        public void ScanRefusesAnythingUnexpected(string dump, string reason)
        {
            Assert.Null(CrmOwnedStoreItems.Scan(dump, out string problem));
            Assert.Contains(reason, problem);
        }

        [Fact]
        public void JsonWithoutKeepsEverythingElse()
        {
            var root = MiniJson.ParseObject("{\"game\":{\"x\":1},\"offline_store\":{\"prices\":[{\"item\":\"A\"},{\"group\":\"G\",\"bundle\":[]},"
                                            + "{\"item\":\"B\"}],\"promotion\":null},\"iap\":{\"prices\":[]}}");
            var doc = new CrmDocument(root, false, null, DateTime.UtcNow);

            Assert.Equal("{\"game\":{\"x\":1},\"offline_store\":{\"prices\":[{\"group\":\"G\",\"bundle\":[]},{\"item\":\"B\"}],\"promotion\":null},"
                         + "\"iap\":{\"prices\":[]}}", doc.JsonWithout(new HashSet<string> { "A", "G" }, out int omitted));
            Assert.Equal(1, omitted);
            Assert.Same(doc.Json, doc.JsonWithout(new HashSet<string> { "C" }, out omitted));
            Assert.Equal(0, omitted);
            Assert.Equal(3, ((List<object>)JsonPath.Get(doc.Root, "offline_store.prices")).Count); // the cached tree is untouched
        }

        [Fact]
        public void OwnedSignaturesLeaveTheStoreAtTheNextGameProfileRead()
        {
            using var dir = new TempDir();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"));
            string player = Token(server, "p1");
            string other = Token(server, "p2");

            // No game profile yet (a new player): the whole store.
            string full = Crm(server, player);
            Assert.Contains("\"KillSignature_Avatar_1\"", full);
            Assert.Equal(full, Crm(server, null));

            // The TS saves (R4), reloads at the next login or return to the menu, then does its forced refresh.
            Save(server, player, new[] { "1", "2" }, new[] { "3" });
            ReadGameProfile(server, player);
            string owned = Crm(server, player);
            Assert.DoesNotContain("\"KillSignature_Avatar_1\"", owned);
            Assert.DoesNotContain("\"KillSignature_Avatar_2\"", owned);
            Assert.DoesNotContain("\"KillSignature_Background_3\"", owned);
            Assert.Contains("\"KillSignature_Avatar_3\"", owned);
            Assert.Contains("\"KillSignature_Background_1\"", owned);
            Assert.Contains("\"KillSignature_Sound_2\"", owned);   // sounds stay: the screen lists owned ones from the store
            var doc = MiniJson.ParseObject(owned);
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            Assert.Equal(((List<object>)JsonPath.Get(MiniJson.ParseObject(full), "offline_store.prices")).Count - 3,
                         ((List<object>)JsonPath.Get(doc, "offline_store.prices")).Count);

            // DoSaveProfile's refresh follows a save, not a read: the same text, so the TS does not apply it again.
            Save(server, player, new[] { "1", "2", "3" }, new[] { "3" });
            Assert.Equal(owned, Crm(server, player));

            // The next reload takes the new set.
            ReadGameProfile(server, player);
            Assert.DoesNotContain("\"KillSignature_Avatar_3\"", Crm(server, player));

            // Other players, explicit user paths and selectors are not filtered.
            Assert.Equal(full, Crm(server, other));
            var game = ContentTestSupport.Get(server, "/configs/users/me/game?access_token=" + Uri.EscapeDataString(player));
            Assert.Equal(200, game.StatusCode);

            // crm.omitOwnedSignatures=false serves the whole store again.
            server.Context.Settings.Override("crm", MiniJson.ParseObject("{\"omitOwnedSignatures\":false}"));
            Assert.Contains("\"KillSignature_Avatar_1\"", Crm(server, player));
        }

        [Fact]
        public void AnUnreadableDumpServesTheWholeStore()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"), logs: logs);
            string player = Token(server, "p1");
            string full = Crm(server, null);

            var save = ContentTestSupport.Post(server, "/profiles/me/myprofile/_mcvs.gameProfile", UrlEncoding.EncodeForm(new[]
            {
                new KeyValuePair<string, string>("object", "{\"a\":{\"KillSignatureAvatars\":[\"1\"]},\"b\":{\"KillSignatureAvatars\":[\"2\"]}}"),
                new KeyValuePair<string, string>("access_token", player),
            }));
            Assert.Equal(200, save.StatusCode);
            ReadGameProfile(server, player);
            Assert.Equal(full, Crm(server, player));
            Assert.True(logs.Contains("appears more than once); serving the whole store"));
        }

        private static string Token(McvsServer server, string user)
        {
            var account = server.Context.Accounts.GetOrCreate("anonymous:" + user, "device-" + user);
            return server.Context.Tokens.Issue(account.Id, "anonymous:" + user, McvsConstants.ClientId, ServerScopes,
                                               "device-" + user, "game:mcvsoffline").Token;
        }

        private static string Crm(McvsServer server, string token)
        {
            var response = ContentTestSupport.Get(server, "/configs/users/me" + (token == null ? "" : "?access_token=" + Uri.EscapeDataString(token)));
            Assert.Equal(200, response.StatusCode);
            return response.BodyText;
        }

        private static void Save(McvsServer server, string token, string[] avatars, string[] backgrounds)
        {
            string dump = "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"KillSignatureAvatars\":"
                          + MiniJson.Serialize(new List<object>(avatars)) + ",\"KillSignatureBackgrounds\":"
                          + MiniJson.Serialize(new List<object>(backgrounds)) + "}}}";
            var response = ContentTestSupport.Post(server, "/profiles/me/myprofile/_mcvs.gameProfile", UrlEncoding.EncodeForm(new[]
            {
                new KeyValuePair<string, string>("visibility", "restricted"),
                new KeyValuePair<string, string>("object", dump),
                new KeyValuePair<string, string>("access_token", token),
            }));
            Assert.Equal(200, response.StatusCode);
        }

        private static void ReadGameProfile(McvsServer server, string token)
        {
            var response = ContentTestSupport.Get(server, "/profiles/me/myprofile/_mcvs.gameProfile?access_token=" + Uri.EscapeDataString(token));
            Assert.Equal(200, response.StatusCode);
        }

        private static string[] Sorted(IEnumerable<string> names)
        {
            var list = new List<string>(names);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }
    }
}
