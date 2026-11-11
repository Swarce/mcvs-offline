using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>
    /// Build 32: the profile lock (<c>$default./Profile.ProfileOwnerRoomId</c>) an earlier start left in the stored game profile
    /// is answered as null to this start's reads (<see cref="StaleProfileOwner"/>); a lock this start's game wrote, an absent
    /// lock and anything unexpected are answered as stored.
    /// </summary>
    public class StaleProfileOwnerTests
    {
        private const string Path = "profiles/me/myprofile/_mcvs.gameProfile";
        private const string Old = "LocalAnubis-Room-18478500-6c40-4424-9b53-e40b7e14c747";
        private const string Current = "LocalAnubis-Room-b5a779e1-670c-46ae-a9e1-5a55bd1949cf";

        /// <summary>A dump in the game's shape with the lock as the Templates writer writes it ("…" or null).</summary>
        internal static string Dump(string owner, string playerName = "agent-123456")
        {
            return "{\"@version\":\"0.1\",\"$default\":{\"Enabled\":true,\"Assets@type\":\"AssetsComponent\"," +
                   "\"/League\":{\"@type\":\"LeagueComponent\",\"LeagueDivisionId\":40}," +
                   "\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"Version\":57,\"PlayerName\":\"" + playerName + "\"," +
                   "\"ProfileOwnerRoomId\":" + (owner == null ? "null" : "\"" + owner + "\"") + "," +
                   "\"CrmStandardData\":{\"Inventory\":{\"ProfileOwnerRoomId\":1,\"a\":2}},\"Xp\":105200}," +
                   "\"/ProfilePropertyTable\":{\"@type\":\"ProfilePropertyTableComponent\"}}}";
        }

        private static string Owner(string dump)
        {
            int start, length;
            string owner;
            Assert.True(StaleProfileOwner.TryFindOwner(dump, out start, out length, out owner), dump);
            return owner;
        }

        // ------------------------------------------------------------------ the pure parts

        [Fact]
        public void FindsTheLockInTheGamesShape()
        {
            string dump = Dump(Old);
            int start, length;
            string owner;
            Assert.True(StaleProfileOwner.TryFindOwner(dump, out start, out length, out owner));
            Assert.Equal(Old, owner);
            Assert.Equal("\"" + Old + "\"", dump.Substring(start, length));

            Assert.True(StaleProfileOwner.TryFindOwner(Dump(null), out start, out length, out owner));
            Assert.Null(owner);
            Assert.Equal("null", Dump(null).Substring(start, length));
        }

        [Fact]
        public void ReleasingGivesExactlyTheTextOfAReleasedProfile()
        {
            string dump = Dump(Old);
            int start, length;
            string owner;
            Assert.True(StaleProfileOwner.TryFindOwner(dump, out start, out length, out owner));
            // What SaveAndReleaseProfile stores: the same profile with ProfileOwnerRoomId null (build-31 log: 51 chars shorter).
            Assert.Equal(Dump(null), StaleProfileOwner.Release(dump, start, length));
            Assert.Equal(51, dump.Length - Dump(null).Length);
        }

        [Fact]
        public void PrettyPrintedEscapedAndBomTextsAreFound()
        {
            string pretty = "﻿ {\n  \"@version\" : \"0.1\" ,\n  \"$default\" : {\n    \"/Profile\" : {\n      \"Version\" : 57 ,\n" +
                            "      \"ProfileOwnerRoomId\" : \"Local\\u0041nubis-Room-1\\/2\"\n    }\n  }\n}\n";
            Assert.Equal("LocalAnubis-Room-1/2", Owner(pretty));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("null")]
        [InlineData("not json")]
        [InlineData("{\"$default\":{}}")]
        [InlineData("{\"$default\":{\"/Profile\":{}}}")]                                                    // no lock
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":5}}}")]                              // not a string
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":{\"a\":1}}}}")]
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\",\"ProfileOwnerRoomId\":\"b\"}}}")]   // twice
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\",\"profileownerroomid\":\"b\"}}}")]   // other case
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\",\"'ProfileOwnerRoomId'\":\"b\"}}}")] // quoted
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\",\".ProfileOwnerRoomId\":\"b\"}}}")]  // leading dot
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\",\"ProfileOwnerRoomId@type\":\"b\"}}}")]
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\"},\"/Profile.ProfileOwnerRoomId\":\"b\"}}")]
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\"},\"/Profile\":{}}}")]          // /Profile twice
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\"}},\"$default\":{}}")]          // $default twice
        [InlineData("{\"$default\":{\"/Profile\":{\"@type\":\"Game.LeagueComponent\",\"ProfileOwnerRoomId\":\"a\"}}}")]
        [InlineData("{\"$default\":{\"/Profile\":{\"@type\":5,\"ProfileOwnerRoomId\":\"a\"}}}")]
        [InlineData("{\"$default\":{\"/Profile\":[\"ProfileOwnerRoomId\"]}}")]
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\"}")]                           // cut off
        [InlineData("{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"a\\q\"}}}")]                        // bad escape
        [InlineData("{\"$default\":{\"/Profile\":{\"Inventory\":{\"ProfileOwnerRoomId\":\"a\"}}}}")]           // only deeper
        public void AnythingElseIsNotALock(string dump)
        {
            int start, length;
            string owner;
            Assert.False(StaleProfileOwner.TryFindOwner(dump, out start, out length, out owner));
        }

        [Fact]
        public void OnlyTheOwnerHeldBeforeTheFirstRequestIsCleared()
        {
            var owners = new StaleProfileOwner();
            string cleared;
            bool first;
            // The first read captures the stale owner and answers it released.
            Assert.Equal(Dump(null), owners.OnRead("acc", Dump(Old), true, out cleared, out first));
            Assert.Equal(Old, cleared);
            Assert.True(first);
            Assert.Equal(Old, owners.Inherited("acc"));
            // Read again (the ownership loop's reloads, if the game asked): released again, reported once.
            Assert.Equal(Dump(null), owners.OnRead("acc", Dump(Old), true, out cleared, out first));
            Assert.Equal(Old, cleared);
            Assert.False(first);
            // The game took the lock (TakeProfileLock): its own room is answered as stored, so are a release and an absent dump.
            Assert.Equal(Dump(Current), owners.OnRead("acc", Dump(Current), true, out cleared, out first));
            Assert.Null(cleared);
            Assert.Equal(Dump(null), owners.OnRead("acc", Dump(null), true, out cleared, out first));
            Assert.Null(cleared);
            Assert.Equal("{}", owners.OnRead("acc", "{}", true, out cleared, out first));
            Assert.Null(cleared);
            // An old save put back later (a restore of a backup) holds the old owner again: still not a room of this process.
            Assert.Equal(Dump(null), owners.OnRead("acc", Dump(Old), true, out cleared, out first));
            Assert.Equal(Old, cleared);
        }

        [Fact]
        public void AWriteFirstCapturesTheOwnerBeforeIt()
        {
            var owners = new StaleProfileOwner();
            string cleared;
            bool first;
            // The game's first request is a write: the owner stored before it is the earlier start's.
            owners.BeforeWrite("acc", () => Dump(Old));
            Assert.True(owners.IsCaptured("acc"));
            owners.BeforeWrite("acc", () => { throw new InvalidOperationException("not read again"); });
            Assert.Equal(Dump(null), owners.OnRead("acc", Dump(Old), true, out cleared, out first));
            // The game's first write takes the lock with its own room: never cleared.
            var fresh = new StaleProfileOwner();
            fresh.BeforeWrite("acc", () => Dump(null));
            Assert.Equal(Dump(Current), fresh.OnRead("acc", Dump(Current), true, out cleared, out first));
            Assert.Null(cleared);
            // Nothing stored yet, or unreadable: nothing inherited.
            var none = new StaleProfileOwner();
            none.BeforeWrite("acc", () => null);
            none.BeforeWrite("other", () => { throw new InvalidOperationException("unreadable"); });
            Assert.Equal(Dump(Old), none.OnRead("acc", Dump(Old), true, out cleared, out first));
            Assert.Equal(Dump(Old), none.OnRead("other", Dump(Old), true, out cleared, out first));
            Assert.Null(cleared);
        }

        [Fact]
        public void AnUnreadableFirstDumpInheritsNothing()
        {
            var owners = new StaleProfileOwner();
            string cleared;
            bool first;
            string twice = "{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"" + Old + "\",\"ProfileOwnerRoomId\":\"" + Old + "\"}}}";
            Assert.Equal(twice, owners.OnRead("acc", twice, true, out cleared, out first));
            Assert.True(owners.IsCaptured("acc"));
            Assert.Null(owners.Inherited("acc"));
            Assert.Equal(Dump(Old), owners.OnRead("acc", Dump(Old), true, out cleared, out first));
            Assert.Null(cleared);
        }

        [Fact]
        public void WithTheSettingOffTheOwnerIsCapturedButNotCleared()
        {
            var owners = new StaleProfileOwner();
            string cleared;
            bool first;
            Assert.Equal(Dump(Old), owners.OnRead("acc", Dump(Old), false, out cleared, out first));
            Assert.Null(cleared);
            Assert.Equal(Old, owners.Inherited("acc"));
            Assert.Equal(Dump(null), owners.OnRead("acc", Dump(Old), true, out cleared, out first));
            Assert.Equal(Old, cleared);
        }

        [Fact]
        public void AccountsAreSeparate()
        {
            var owners = new StaleProfileOwner();
            string cleared;
            bool first;
            Assert.Equal(Dump(null), owners.OnRead("a", Dump(Old), true, out cleared, out first));
            // b's first read held nothing: a's old owner in b's dump later is b's own (written in this start).
            Assert.Equal(Dump(null), owners.OnRead("b", Dump(null), true, out cleared, out first));
            Assert.Equal(Dump(Old), owners.OnRead("b", Dump(Old), true, out cleared, out first));
            Assert.Null(cleared);
            // c's earlier start left Current: released for c only.
            Assert.Equal(Dump(null), owners.OnRead("c", Dump(Current), true, out cleared, out first));
            Assert.Equal(Dump(Old), owners.OnRead("c", Dump(Old), true, out cleared, out first));
            Assert.Equal(Dump(Current), owners.OnRead("a", Dump(Current), true, out cleared, out first));
        }

        // ------------------------------------------------------------------ over the storage endpoints

        private static string Get(StorageHarness h, string token)
        {
            HttpResponse r = h.Get(Path, token);
            Assert.Equal(200, r.StatusCode);
            return r.BodyText;
        }

        private static void Save(StorageHarness h, string token, string dump)
        {
            Assert.Equal(200, h.Post(Path, token, ("visibility", "restricted"), ("object", dump)).StatusCode);
        }

        private static int Lines(StorageHarness h, string text)
        {
            int n = 0;
            foreach (LogEntry e in h.Logs.Snapshot())
            {
                if (e.Message.Contains(text)) n++;
            }
            return n;
        }

        private const string Line = "profile: cleared a room owner left by an earlier start: " + Old;

        [Fact]
        public void TheNextStartGetsTheLockReleasedAndTheGamesOwnLocksStay()
        {
            using var h = new StorageHarness(inProcessClient: true);
            string token = h.PlayerToken("p");
            // Start 1: TakeProfileLock saved Old, then the app was killed (no SaveAndReleaseProfile).
            Save(h, token, Dump(Old));
            Assert.Equal(Dump(Old), Get(h, token));
            h.Restart();
            token = h.PlayerToken("p");
            string stored = h.StoredFile(h.Player("p").Id);

            // Start 2, the TS's LoadPlayerProfile: released, one Info line, the file unchanged.
            Assert.Equal(Dump(null), Get(h, token));
            Assert.Equal(1, Lines(h, Line));
            Assert.Contains(h.Logs.Snapshot(), e => e.Level == LogLevel.Info && e.Message.Contains(Line));
            Assert.Equal(stored, h.StoredFile(h.Player("p").Id));
            // A second read before the game saves: released again, no second Info line.
            Assert.Equal(Dump(null), Get(h, token));
            Assert.Single(h.Logs.Snapshot().FindAll(e => e.Level == LogLevel.Info && e.Message.Contains(Line)));
            Assert.Equal(2, Lines(h, Line));

            // TakeProfileLock with this start's room, the Play sequence (TS release, GS load and save, TS lock again).
            Save(h, token, Dump(Current));
            Assert.Equal(Dump(Current), Get(h, token));
            Save(h, token, Dump(null));
            Assert.Equal(Dump(null), Get(h, token));
            Save(h, token, Dump(null, "after the match"));
            Save(h, token, Dump(Current, "after the match"));
            Assert.Equal(Dump(Current, "after the match"), Get(h, token));

            // Start 3: this time Current is the earlier start's.
            h.Restart();
            token = h.PlayerToken("p");
            Assert.Equal(Dump(null, "after the match"), Get(h, token));
            Assert.Equal(1, Lines(h, "profile: cleared a room owner left by an earlier start: " + Current));
        }

        [Fact]
        public void AnyWriteBeforeTheFirstReadCapturesTheStoredOwner()
        {
            using var h = new StorageHarness(inProcessClient: true);
            string token = h.PlayerToken("p");
            Save(h, token, Dump(Old));
            h.Restart();
            token = h.PlayerToken("p");
            // Start 2's first request writes another section (R5 matchers), then the dump is read.
            Assert.Equal(200, h.Post("profiles/me/myprofile/_mcvs.matchers", token, ("visibility", "restricted"), ("object", "{\"LoginsPerDay\":1}")).StatusCode);
            Assert.Equal(Dump(null), Get(h, token));

            // A start whose first request is the lock save itself: its room is never taken for an old one.
            h.Restart();
            token = h.PlayerToken("p");
            Save(h, token, Dump(Current));
            Assert.Equal(Dump(Current), Get(h, token));
            Assert.Equal(0, Lines(h, "earlier start: " + Current));
        }

        [Fact]
        public void TheGameServersWriteToTheAccountAlsoCapturesFirst()
        {
            using var h = new StorageHarness(inProcessClient: true);
            string token = h.PlayerToken("p");
            Save(h, token, Dump(Old));
            h.Restart();
            string account = h.Player("p").Id;
            // currentMatch written with the game server's own token on the fed_id path before the player's first read.
            Assert.Equal(200, h.Post("profiles/fed_id%3A" + account + "/myprofile/_mcvs.currentMatch", h.GameServerToken(),
                                     ("object", "{}")).StatusCode);
            Assert.Equal(Dump(null), Get(h, h.PlayerToken("p")));
        }

        [Fact]
        public void TheStandaloneServerAndTheSettingOffServeTheLockAsStored()
        {
            using (var h = new StorageHarness())
            {
                string token = h.PlayerToken("p");
                Save(h, token, Dump(Old));
                h.Restart();
                Assert.Equal(Dump(Old), Get(h, h.PlayerToken("p")));
                Assert.Equal(0, Lines(h, "earlier start"));
            }
            using (var h = new StorageHarness(configure: s => s.Override("storage.clearStaleProfileOwner", false), inProcessClient: true))
            {
                string token = h.PlayerToken("p");
                Save(h, token, Dump(Old));
                h.Restart();
                token = h.PlayerToken("p");
                Assert.Equal(Dump(Old), Get(h, token));
                // Turned on in the same start: the owner was captured at the first read, so it is still known as the old one.
                h.Context.Settings.Override("storage.clearStaleProfileOwner", true);
                Assert.Equal(Dump(null), Get(h, token));
            }
        }

        [Fact]
        public void OtherReadsAndUnexpectedDumpsAreAnsweredAsStored()
        {
            using var h = new StorageHarness(inProcessClient: true);
            string token = h.PlayerToken("p");
            Save(h, token, Dump(Old));
            h.Restart();
            token = h.PlayerToken("p");
            // A deeper selector and the whole document are not the game's profile load: unchanged.
            Assert.Equal("\"" + Old + "\"", h.Get("profiles/me/myprofile/_mcvs.gameProfile.$default./Profile.ProfileOwnerRoomId", token).BodyText);
            Assert.Contains(Old, h.Get("profiles/me/myprofile", token).BodyText);
            // A dump that is not in the expected shape (the lock twice).
            using var h2 = new StorageHarness(inProcessClient: true);
            string t2 = h2.PlayerToken("q");
            string twice = "{\"$default\":{\"/Profile\":{\"ProfileOwnerRoomId\":\"" + Old + "\",\"ProfileOwnerRoomId\":\"" + Old + "\"}}}";
            Save(h2, t2, twice);
            h2.Restart();
            Assert.Equal(twice, Get(h2, h2.PlayerToken("q")));
            // Non-JSON text stored opaque.
            using var h3 = new StorageHarness(inProcessClient: true);
            string t3 = h3.PlayerToken("r");
            Save(h3, t3, "not json " + Old);
            h3.Restart();
            Assert.Equal("not json " + Old, Get(h3, h3.PlayerToken("r")));
        }
    }
}
