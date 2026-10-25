using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>
    /// The storage requests of the game's progression path (verify-C3 §2.3, R1-R12) with the answers the game
    /// needs (§7 checklist): exact statuses, bodies, nesting, byte-exact game profile, no ETag.
    /// </summary>
    public class StorageProtocolTests
    {
        private const string Me = "profiles/me/myprofile";

        [Fact]
        public void NewPlayerLoadSaveAndReloadFollowsTheGameSequence()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("player1");
            string fedId = h.Player("player1").FedId;
            string gs = h.GameServerToken();

            // R1: no game profile yet -> 200 {} (new profile without the R7 reset path).
            var r1 = h.Get(Me + "/_mcvs.gameProfile", token);
            Assert.Equal(200, r1.StatusCode);
            Assert.Equal("{}", r1.BodyText);

            // R2: sections load of a new account -> 200 {} (an object, never 404).
            var r2 = h.Get(Me, token, ("include_fields", GameData.SectionsIncludeFields));
            Assert.Equal(200, r2.StatusCode);
            Assert.Equal("{}", r2.BodyText);

            // R3: each absent selector section is created with {} and must answer exactly 200.
            foreach (string section in new[] { "_mcvs.matchers", "_mcvs.snapshot", "inventory", "_inventory" })
            {
                var r3 = h.Post(Me + "/" + section, token, ("visibility", "restricted"), ("object", "{}"));
                Assert.Equal(200, r3.StatusCode);
                Assert.Equal("{}", r3.BodyText);
                Assert.Null(r3.Headers.Get("ETag"));
            }

            // R8: currentMatch through the fed id path with the game server's own token -> 200 {} (never 404).
            var r8 = h.Get("profiles/" + UrlEncoding.Encode(fedId) + "/myprofile/_mcvs.currentMatch", gs);
            Assert.Equal(200, r8.StatusCode);
            Assert.Equal("{}", r8.BodyText);

            // R4: the full dump, Set on _mcvs.gameProfile -> exactly 200, no ETag.
            var r4 = h.Post(Me + "/_mcvs.gameProfile", token, ("visibility", "restricted"), ("object", GameData.GameProfileDump));
            Assert.Equal(200, r4.StatusCode);
            Assert.Null(r4.Headers.Get("ETag"));

            // R5: one root merge with standard fields and a partial _mcvs; must keep gameProfile.
            var r5 = h.Post(Me, token, ("operation", "merge"), ("object", GameData.SectionsMerge));
            Assert.Equal(200, r5.StatusCode);

            // R11: language field (a JSON string).
            Assert.Equal(200, h.Post(Me + "/language", token, ("object", "\"en\"")).StatusCode);

            // R9: the GS registers the room in currentMatch.
            string match = "{\"CurrentServerType\":\"gs\",\"CurrentServerId\":\"room-1\",\"ReconnectAttemptsCount\":0}";
            var r9 = h.Post("profiles/" + UrlEncoding.Encode(fedId) + "/myprofile/_mcvs.currentMatch", gs, ("object", match));
            Assert.Equal(200, r9.StatusCode);
            Assert.Equal("{}", r9.BodyText);

            // Reload (TS resume): R1 returns the dump byte-for-byte.
            var reload = h.Get(Me + "/_mcvs.gameProfile", token);
            Assert.Equal(200, reload.StatusCode);
            Assert.Equal(GameData.GameProfileDump, reload.BodyText);

            // R2 again: nested sections (SelectToken("_mcvs.snapshot") must find them), standard fields, read-only fields.
            var sections = StorageHarness.Json(h.Get(Me, token, ("include_fields", GameData.SectionsIncludeFields)));
            Assert.False(sections.ContainsKey("_mcvs.snapshot"));
            var mcvs = (Dictionary<string, object>)sections["_mcvs"];
            Assert.Equal(new[] { "matchers", "snapshot" }, new List<string>(mcvs.Keys));
            Assert.Equal(1000L, JsonPath.Get(mcvs, "snapshot.MatchMakingRating"));
            Assert.Equal(1L, JsonPath.Get(mcvs, "matchers.LoginsPerDay"));
            Assert.Equal(7L, sections["level"]);
            Assert.Equal(3600.0, sections["total_playtime"]);
            Assert.Equal("en", sections["language"]);
            Assert.Null(sections["birthdate"]);
            Assert.Equal("anonymous:player1", sections["credential"]);
            Assert.Equal("2026-10-25T10:00:00Z", sections["created"]);
            Assert.Equal("mcvs", sections["datacenter"]);
            Assert.Equal(new Dictionary<string, object>(), sections["inventory"]);
            Assert.False(sections.ContainsKey("gameProfile"));
            Assert.False(mcvs.ContainsKey("gameProfile"));

            // R8 again: the stored match, exactly as written.
            Assert.Equal(match, h.Get("profiles/" + fedId + "/myprofile/_mcvs.currentMatch", gs).BodyText);

            // After an app restart everything is still there.
            h.Restart();
            Assert.Equal(GameData.GameProfileDump, h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Equal("\"en\"", h.Get(Me + "/language", token).BodyText);
        }

        [Fact]
        public void IncludeFieldsReturnsNestedObjectsAndOmitsMissingPaths()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me, token, ("operation", "merge"), ("object", "{\"a\":{\"b\":{\"c\":1,\"d\":2},\"e\":3},\"x\":[1,2]}"));

            var projected = h.Get(Me, token, ("include_fields", "a.b.c,a.e,missing,a.nope.deeper,x"));
            Assert.Equal("{\"a\":{\"b\":{\"c\":1},\"e\":3},\"x\":[1,2]}", projected.BodyText);

            // Parent and child in either order: the parent wins.
            Assert.Equal("{\"a\":{\"b\":{\"c\":1,\"d\":2},\"e\":3}}", h.Get(Me, token, ("include_fields", "a.b.c,a")).BodyText);
            Assert.Equal("{\"a\":{\"b\":{\"c\":1,\"d\":2},\"e\":3}}", h.Get(Me, token, ("include_fields", "a,a.b.c")).BodyText);

            // include_fields on a selector GET is relative to the selected object.
            Assert.Equal("{\"b\":{\"d\":2}}", h.Get(Me + "/a", token, ("include_fields", "b.d")).BodyText);

            // Without include_fields the whole document comes back (read-only fields first).
            var whole = StorageHarness.Json(h.Get(Me, token));
            Assert.Equal(new[] { "credential", "created", "modified", "datacenter", "last_session", "a", "x" }, new List<string>(whole.Keys));
        }

        [Fact]
        public void ProjectionEmbedsTheGameProfileVerbatim()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string gs = h.GameServerToken();
            string fedId = h.Player("p").FedId;
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));

            // Read-only profile view of another player (GM/ProfileManagerComponent.cs:997-1027), game-server token.
            var other = h.Get("profiles/" + UrlEncoding.Encode(fedId) + "/myprofile", gs, ("include_fields", "_mcvs.gameProfile,credential"));
            Assert.Equal(200, other.StatusCode);
            Assert.Contains("\"_mcvs\":{\"gameProfile\":" + GameData.GameProfileDump + "}", other.BodyText);
            Assert.Contains("\"credential\":\"anonymous:p\"", other.BodyText);

            // Paths into the dump work too (the dump is parsed only for that).
            Assert.Equal("57", h.Get(Me + "/_mcvs.gameProfile.$default./Profile.Version", token).BodyText);
        }

        [Fact]
        public void MergeKeepsGameProfileAndCurrentMatch()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));
            h.Post(Me + "/_mcvs.currentMatch", token, ("object", "{\"CurrentServerType\":\"gs\"}"));
            h.Post(Me + "/_mcvs.snapshot", token, ("object", "{\"PlayerLevel\":1,\"Trophies\":5}"));

            Assert.Equal(200, h.Post(Me, token, ("operation", "merge"), ("object", "{\"_mcvs\":{\"snapshot\":{\"PlayerLevel\":2}}}")).StatusCode);

            Assert.Equal(GameData.GameProfileDump, h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Equal("{\"CurrentServerType\":\"gs\"}", h.Get(Me + "/_mcvs.currentMatch", token).BodyText);
            Assert.Equal("{\"PlayerLevel\":2,\"Trophies\":5}", h.Get(Me + "/_mcvs.snapshot", token).BodyText);
        }

        [Fact]
        public void MissingGameProfileAndCurrentMatchAreEmptyObjectsEvenInNotFoundMode()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.absentField", "404"));
            string token = h.PlayerToken("p");
            h.Post(Me + "/level", token, ("object", "3"));

            Assert.Equal("{}", h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Equal("{}", h.Get(Me + "/_mcvs.currentMatch", token).BodyText);
            Assert.Equal("{}", h.Get(Me + "/inventory", token).BodyText);
            Assert.Equal("{}", h.Get(Me + "/_mcvs.snapshot", token).BodyText);
            Assert.Equal(404, h.Get(Me + "/current_client_id", token).StatusCode);
            Assert.Equal(404, h.Get(Me + "/_mcvs", token).StatusCode); // IsNewAccount probe: 404 = new account

            using var lenient = new StorageHarness();
            string t2 = lenient.PlayerToken("p");
            var absent = lenient.Get(Me + "/current_client_id", t2);
            Assert.Equal(200, absent.StatusCode);
            Assert.Equal("{}", absent.BodyText);
        }

        [Fact]
        public void EmptyObjectDeletesAndNullIsNotStoredForSections()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me + "/language", token, ("object", "\"fr\""));
            h.Post(Me + "/inventory", token, ("object", "{\"VersusCoins\":5}"));

            // StorageService.DeleteProfileField sends object= (empty).
            Assert.Equal(200, h.Post(Me + "/language", token, ("object", "")).StatusCode);
            Assert.Equal("{}", h.Get(Me + "/language", token).BodyText);

            // JSON null on a section would read back as "null" and make SetProfile("null") throw: removed instead.
            Assert.Equal(200, h.Post(Me + "/inventory", token, ("object", "null")).StatusCode);
            var doc = StorageHarness.Json(h.Get(Me, token));
            Assert.False(doc.ContainsKey("inventory"));

            // ...also when it arrives in a merge.
            h.Post(Me + "/_mcvs.matchers", token, ("object", "{\"a\":1}"));
            h.Post(Me, token, ("operation", "merge"), ("object", "{\"_mcvs\":{\"matchers\":null},\"clan\":null}"));
            doc = StorageHarness.Json(h.Get(Me, token));
            Assert.False(((Dictionary<string, object>)doc["_mcvs"]).ContainsKey("matchers"));
            Assert.True(doc.ContainsKey("clan")); // null is stored for ordinary fields (harmless, verify-C3 §7.4)
            Assert.Null(doc["clan"]);
        }

        [Fact]
        public void R7ResetReplacesMcvsAndKeepsABackup()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));
            h.Post(Me + "/_mcvs.snapshot", token, ("object", "{\"PlayerLevel\":9}"));

            // GameProfileSection.CreateProfile: Set on all of _mcvs (only reached after a 404/403, which we never send).
            Assert.Equal(200, h.Post(Me + "/_mcvs", token, ("visibility", "restricted"), ("object", "{\"gameProfile\":null}")).StatusCode);
            Assert.Equal("null", h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Equal("{\"gameProfile\":null}", h.Get(Me + "/_mcvs", token).BodyText);

            var backups = h.Store.Backups.List(account);
            Assert.Contains(backups, b => b.Reason == "wipe");
            Assert.True(h.Logs.Contains("clears the stored game profile"));
        }

        [Fact]
        public void FedIdPathsAcceptEncodedPlainAndDoublyEncodedForms()
        {
            using var h = new StorageHarness();
            string gs = h.GameServerToken();
            string id = h.Player("p").Id;
            Assert.Equal(200, h.Post("profiles/fed_id%3A" + id + "/myprofile/_mcvs.currentMatch", gs, ("object", "{\"n\":1}")).StatusCode);
            Assert.Equal("{\"n\":1}", h.Get("profiles/fed_id:" + id + "/myprofile/_mcvs.currentMatch", gs).BodyText);
            Assert.Equal("{\"n\":1}", h.Get("profiles/fed_id%3a" + id + "/myprofile/_mcvs.currentMatch", gs).BodyText);
            Assert.Equal("{\"n\":1}", h.Get("profiles/fed_id%253A" + id + "/myprofile/_mcvs.currentMatch", gs).BodyText);
            Assert.Equal("{\"n\":1}", h.Get("//profiles//FED_ID%3A" + id + "/myprofile/_mcvs.currentMatch/", gs).BodyText);

            // "me" with the player's for_username token is the same document.
            Assert.Equal("{\"n\":1}", h.Get(Me + "/_mcvs.currentMatch", h.PlayerToken("p")).BodyText);
            // So is the anonymous credential.
            Assert.Equal("{\"n\":1}", h.Get("profiles/anonymous%3Ap/myprofile/_mcvs.currentMatch", gs).BodyText);
        }

        [Fact]
        public void UnknownCredentialsReadAsEmptyAndWritesCreateTheAccount()
        {
            using var h = new StorageHarness();
            string gs = h.GameServerToken();
            Assert.Equal("{}", h.Get("profiles/anonymous%3Anobody/myprofile", gs).BodyText);
            Assert.Null(h.Context.Accounts.Find("anonymous:nobody"));

            Assert.Equal(200, h.Post("profiles/anonymous%3Anobody/myprofile/level", gs, ("object", "4")).StatusCode);
            var account = h.Context.Accounts.Find("anonymous:nobody");
            Assert.NotNull(account);
            Assert.Equal("4", h.Get("profiles/fed_id%3A" + account.Id + "/myprofile/level", gs).BodyText);

            Assert.Equal(400, h.Get("profiles/notacredential/myprofile", gs).StatusCode);
        }

        [Fact]
        public void AuthenticationFailuresAre401NeverForbidden()
        {
            using var h = new StorageHarness();
            Assert.Equal(401, h.Get(Me, null).StatusCode);
            Assert.Equal(401, h.Get(Me, "mcvs00000000.0123456789abcdef0123456789abcdef").StatusCode);
            Assert.Equal(401, h.Post(Me + "/_mcvs.gameProfile", "garbage", ("object", "{}")).StatusCode);
            Assert.Equal(401, h.Get("profiles", "nope", ("credentials", "fed_id:x")).StatusCode);
            Assert.True(h.Logs.Contains("401 for GET /profiles/me/myprofile: no access_token"));

            // A bearer header also works (not used by the game, harmless).
            string token = h.PlayerToken("p");
            var request = HttpRequest.Create("GET", "/" + Me);
            request.Headers.Add("Authorization", "Bearer " + token);
            Assert.Equal(200, h.Send(request).StatusCode);
        }

        [Fact]
        public void IfMatchIsIgnoredAndNoETagIsEverSent()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            for (int i = 0; i < 3; i++)
            {
                var request = HttpRequest.Create("POST", "/" + Me + "/_mcvs.gameProfile",
                    "object=" + UrlEncoding.Encode("{\"v\":" + i + "}") + "&visibility=restricted&access_token=" + token);
                request.Headers.Add("If-Match", "\"stale-" + i + "\"");
                var response = h.Send(request);
                Assert.Equal(200, response.StatusCode); // never 412 (verify-C3 §3.5)
                Assert.Null(response.Headers.Get("ETag"));
                Assert.Null(h.Get(Me + "/_mcvs.gameProfile", token).Headers.Get("ETag"));
            }
            Assert.Equal("{\"v\":2}", h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
        }

        [Fact]
        public void BatchProfilesReturnOneNestedEntryPerCredentialInOrder()
        {
            using var h = new StorageHarness();
            string gs = h.GameServerToken();
            var a = h.Player("a");
            var b = h.Player("b");
            h.Post("profiles/" + a.FedId + "/myprofile/_mcvs.snapshot", gs, ("object", "{\"MatchMakingRating\":1100,\"x\":1}"));
            h.Post("profiles/" + b.FedId + "/myprofile/_mcvs.snapshot", gs, ("object", "{\"MatchMakingRating\":900}"));

            var batch = h.Get("profiles", gs, ("credentials", b.FedId + "," + a.FedId + ",fed_id:unknown"),
                              ("include_fields", "_mcvs.snapshot.MatchMakingRating"));
            Assert.Equal(200, batch.StatusCode);
            Assert.Equal("[{\"_mcvs\":{\"snapshot\":{\"MatchMakingRating\":900}}},{\"_mcvs\":{\"snapshot\":{\"MatchMakingRating\":1100}}},{}]",
                         batch.BodyText);
        }

        [Fact]
        public void DeleteProfileRemovesTheDocumentAndKeepsABackup()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));

            var delete = h.Post(Me + "/delete", token);
            Assert.Equal(200, delete.StatusCode);
            Assert.Equal("{}", delete.BodyText);
            Assert.Equal("{}", h.Get(Me, token).BodyText);
            Assert.Equal("{}", h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Null(h.StoredFile(account));
            Assert.Contains(h.Store.Backups.List(account), x => x.Reason == "delete");

            // Deleting again, or an account that never saved, is still 200.
            Assert.Equal(200, h.Post(Me + "/delete", token).StatusCode);
            Assert.Equal(200, h.Send(HttpRequest.Create("DELETE", "/" + Me + "?access_token=" + token)).StatusCode);
        }

        [Fact]
        public void HttpDeleteOnASelectorRemovesIt()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me, token, ("operation", "merge"), ("object", "{\"a\":{\"b\":1,\"c\":2}}"));
            Assert.Equal(200, h.Send(HttpRequest.Create("DELETE", "/" + Me + "/a.b?access_token=" + token)).StatusCode);
            Assert.Equal("{\"c\":2}", h.Get(Me + "/a", token).BodyText);
            // operation=delete through POST does the same.
            Assert.Equal(200, h.Post(Me + "/a.c", token, ("operation", "delete")).StatusCode);
            Assert.Equal("{}", h.Get(Me + "/a", token).BodyText);
        }

        [Fact]
        public void BackupsEndpointListsAndServesBackups()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.backups.intervalMinutes", 0L));
            string token = h.PlayerToken("p");
            string fedId = h.Player("p").FedId;
            h.Post(Me + "/level", token, ("object", "1"));
            h.Clock.Advance(System.TimeSpan.FromMinutes(1));
            h.Post(Me + "/level", token, ("object", "2"));

            var list = MiniJson.ParseArray(h.Get("/profiles/" + UrlEncoding.Encode(fedId) + "/myprofile/backups", token,
                ("start_date", "2026-08-26T10:00:00Z"), ("end_date", "2026-10-25T10:05:00Z")).BodyText);
            Assert.Single(list);
            var entry = (Dictionary<string, object>)list[0];
            Assert.Equal("2026-10-25T10:01:00Z", entry["date"]);
            Assert.Equal(1L, entry["level"]);
            foreach (string key in new[] { "id", "size", "totalSpend", "index" }) Assert.True(entry.ContainsKey(key), key);

            var backup = StorageHarness.Json(h.Get("/profiles/" + fedId + "/myprofile/backups/" + entry["id"], token));
            Assert.Equal(1L, backup["level"]);

            // Out-of-range dates filter; unknown ids answer {} (GetValidUpd10Profile reads it inside try/catch).
            Assert.Equal("[]", h.Get("/profiles/" + fedId + "/myprofile/backups", token, ("start_date", "2027-01-01T00:00:00Z")).BodyText);
            Assert.Equal("{}", h.Get("/profiles/" + fedId + "/myprofile/backups/nothing-here", token).BodyText);
            Assert.Equal("{}", h.Get("/profiles/" + fedId + "/myprofile/backups/..%2F..%2Fsecrets", token).BodyText);
        }

        [Fact]
        public void VisibilityIsRecordedAndServed()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me, token, ("operation", "merge"), ("visibility", "{\"*\":\"restricted\"}"), ("object", "{\"level\":1}"));
            h.Post(Me + "/_mcvs.gameProfile", token, ("visibility", "restricted"), ("object", "{}"));
            Assert.Equal(200, h.Post("/profiles/me/myprofile/visibility/_mcvs.snapshot", token, ("visibility", "public")).StatusCode);
            Assert.Equal("{\"*\":\"restricted\",\"_mcvs.gameProfile\":\"restricted\",\"_mcvs.snapshot\":\"public\"}",
                         h.Get("/profiles/me/myprofile/visibility", token).BodyText);
        }

        [Fact]
        public void UnknownOperationsAreLoggedInFullAndChangeNothing()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me + "/level", token, ("object", "1"));
            var response = h.Post(Me + "/level", token, ("operation", "frobnicate"), ("object", "5"));
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("{}", response.BodyText);
            Assert.Equal("1", h.Get(Me + "/level", token).BodyText);
            Assert.True(h.Logs.Contains("unknown storage operation 'frobnicate'"));
            Assert.True(h.Logs.Contains("operation = frobnicate"));
        }

        [Fact]
        public void OtherOperationsHaveTheirObviousMeaning()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me + "/stats.kills", token, ("operation", "increment"), ("object", "2"));
            h.Post(Me + "/stats", token, ("operation", "increment"), ("object", "{\"kills\":3,\"deaths\":1}"));
            h.Post(Me + "/tags", token, ("operation", "append"), ("object", "[\"a\"]"));
            h.Post(Me + "/tags", token, ("operation", "add"), ("object", "[\"a\",\"b\"]"));
            h.Post(Me, token, ("operation", "batch_set"), ("object", "{\"x.y\":1,\"z\":true}"));
            h.Post(Me, token, ("operation", "batch_delete"), ("object", "[\"z\"]"));
            Assert.Equal("{\"kills\":5,\"deaths\":1}", h.Get(Me + "/stats", token).BodyText);
            Assert.Equal("[\"a\",\"b\"]", h.Get(Me + "/tags", token).BodyText);
            Assert.Equal("{\"y\":1}", h.Get(Me + "/x", token).BodyText);
            Assert.Equal("{}", h.Get(Me + "/z", token).BodyText);
        }

        [Fact]
        public void ReadOnlyFieldsAreServerMaintained()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me, token, ("operation", "merge"), ("object", "{\"credential\":\"anonymous:evil\",\"created\":\"1999-01-01T00:00:00Z\",\"xp\":1}"));
            h.Clock.Advance(System.TimeSpan.FromHours(1));
            h.Post(Me + "/created", token, ("object", "\"2000-01-01T00:00:00Z\""));
            h.Post(Me + "/xp", token, ("object", "2"));
            var doc = StorageHarness.Json(h.Get(Me, token));
            Assert.Equal("anonymous:p", doc["credential"]);
            Assert.Equal("2026-10-25T10:00:00Z", doc["created"]);
            Assert.Equal("2026-10-25T11:00:00Z", doc["modified"]);
            Assert.Equal("2026-10-25T11:00:00Z", doc["last_session"]);
            Assert.Equal(2L, doc["xp"]);
        }

        [Fact]
        public void NonJsonObjectIsStoredAsOpaqueText()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            Assert.Equal(200, h.Post(Me + "/note", token, ("object", "not json {")).StatusCode);
            Assert.Equal("not json {", h.Get(Me + "/note", token).BodyText);
            Assert.Equal("not json {", StorageHarness.Json(h.Get(Me, token))["note"]); // a JSON string inside documents
            h.Restart();
            Assert.Equal("not json {", h.Get(Me + "/note", token).BodyText);
        }

        [Fact]
        public void PercentEncodingOfFormValuesRoundTrips()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            // Uri.EscapeDataString encoding: spaces %20, '+' %2B, '&' %26, '=' %3D, non-ASCII as UTF-8.
            string value = "{\"s\":\"a b+c&d=e%f é 漢 \\\"q\\\"\"}";
            Assert.Equal(200, h.Post(Me + "/text", token, ("object", value)).StatusCode);
            Assert.Equal(value, h.Get(Me + "/text", token).BodyText);
            // A literal '+' sent unescaped is a space, as in every form decoder.
            var raw = HttpRequest.Create("POST", "/" + Me + "/plus", "object=%22a+b%22&access_token=" + token);
            Assert.Equal(200, h.Send(raw).StatusCode);
            Assert.Equal("\"a b\"", h.Get(Me + "/plus", token).BodyText);
        }

        [Fact]
        public void HeadAndJsonBodiesAreAccepted()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            var json = HttpRequest.Create("POST", "/" + Me + "/level",
                "{\"object\":\"7\",\"access_token\":\"" + token + "\"}", "application/json");
            Assert.Equal(200, h.Send(json).StatusCode);
            Assert.Equal("7", h.Get(Me + "/level", token).BodyText);
            Assert.Equal(200, h.Send(HttpRequest.Create("HEAD", "/" + Me + "/level?access_token=" + token)).StatusCode);
        }

        [Fact]
        public void DataObjectsCanBeStoredReadSearchedAndDeleted()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            Assert.Equal(200, h.Post("data/me/blob.one", token, ("data", "eJzLSM3JyQcABiwCFQ=="), ("category", "profile"),
                                     ("visibility", "private"), ("meta_data", "{\"description\":\"d\"}")).StatusCode);
            var get = h.Get("data/me/blob.one", token);
            Assert.Equal(200, get.StatusCode);
            Assert.Equal("eJzLSM3JyQcABiwCFQ==", get.BodyText);
            var search = MiniJson.ParseArray(h.Get("data", token, ("category", "profile")).BodyText);
            Assert.Single(search);
            Assert.False(((Dictionary<string, object>)search[0]).ContainsKey("data"));
            Assert.Equal(200, h.Post("data/me/blob.one/delete", token).StatusCode);
            Assert.Equal(404, h.Get("data/me/blob.one", token).StatusCode);
        }

        [Fact]
        public void UnusedSearchesAnswerEmptyArrays()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            Assert.Equal("[]", h.Get("profiles/location/me", token, ("distance", "10"), ("limit", "5")).BodyText);
            Assert.Equal("[]", h.Get("/profiles/matchers/default/matches", token, ("limit", "5")).BodyText);
        }

        [Fact]
        public void DiagnosticsListProfilesForThisDevice()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));
            var list = StorageHarness.Json(h.Get("_mcvs/storage", null));
            var profiles = (List<object>)list["profiles"];
            var first = (Dictionary<string, object>)profiles[0];
            Assert.Equal(account, first["account"]);
            Assert.Equal(true, first["hasGameProfile"]);
            Assert.Contains(GameData.GameProfileDump, h.Get("_mcvs/storage/profiles/fed_id:" + account, null).BodyText.Replace("\n", ""));
            Assert.Equal(h.StoredFile(account), h.Get("_mcvs/storage/profiles/" + account, null, ("raw", "1")).BodyText);
        }

        [Fact]
        public void EveryRequestIsLoggedWithItsRoute()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            h.Post(Me + "/_mcvs.gameProfile", token, ("visibility", "restricted"), ("object", "{\"a\":1}"));
            Assert.True(h.Logs.Contains("[storage.field.write]"));
            Assert.True(h.Logs.Contains("visibility = restricted"));
        }
    }
}
