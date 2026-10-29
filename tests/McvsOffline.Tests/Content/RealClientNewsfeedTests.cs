using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Tests.Admin;
using McvsOffline.Tests.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The inbox item through the player's REAL client code (see <see cref="GameClient"/>; skipped without MCVS_REFS,
    /// mandatory with MCVS_REQUIRE_REAL_CLIENT=1): the served document through <c>StandardCrm.Data</c> (every client
    /// parser of the document, OP/StandardCrm.cs), our <c>newsfeeds</c> token through <c>NewsFeedData</c> with the game's
    /// own Newtonsoft, the item through Game.dll's <c>new InboxMessage(NewsFeedItem, DateTime)</c> and InboxComponent's
    /// id mapping, and the link through the admin portal's marker check (what patch <c>adminlink</c> calls).
    /// </summary>
    public class RealClientNewsfeedTests
    {
        private readonly ITestOutputHelper output;

        public RealClientNewsfeedTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static McvsServer Server(TempDir dir) =>
            AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(CrmNewsfeedTests.Wednesday));

        private static string Document(McvsServer server) => ContentTestSupport.Get(server, "/configs/users/me").BodyText;

        private static List<object> Items(dynamic crm) => ((IEnumerable)crm.NewsFeed.Items).Cast<object>().ToList();

        private static DateTime Monday => TimeFormat.FromUnixSeconds(CrmNewsfeedTests.MondayEpoch);

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void TheWholeDocumentParsesWithOurItem(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Util");
            if (game == null) return;
            using var dir = new TempDir();
            using var server = Server(dir);

            dynamic crm = Activator.CreateInstance(game.Op("StandardCrm+Data"), Document(server)); // runs every parser
            dynamic item = Assert.Single(Items(crm));
            Assert.Equal(CrmNewsfeed.NewsId, (string)item.Id);
            Assert.Equal(CrmNewsfeed.Title, (string)item.Title);
            Assert.Equal(CrmNewsfeed.Body, (string)item.Body);
            Assert.Equal(CrmNewsfeedTests.Marker, (string)item.RedirectLink);
            Assert.Equal("", (string)item.AssetName);
            Assert.Equal("", (string)item.YouTubeLink);
            Assert.Equal(Monday, (DateTime)item.CreationDate); // Convert.ToInt64 + DateTimeUtil.FromEpoch
            // end_date = week start + 37 days (pc-port.md §4.3): the expiry of the inbox message instead of now + 30 days.
            Assert.Equal(Monday.AddDays(CrmNewsfeed.EndDateDays), ((DateTime)item.EndDate).ToUniversalTime());

            // The link 'adminlink' recognises (and the only kind Flash Inbox.gotoLink passes on: it starts with "http").
            var portal = new AdminTestClient(server).Portal;
            Assert.True(portal.IsPageMarker((string)item.RedirectLink));
            Assert.StartsWith("http://127.0.0.1:", (string)item.RedirectLink);
            Assert.DoesNotContain("t=", (string)item.RedirectLink);
        }

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void NewsFeedDataReadsOurTokenWithTheGamesNewtonsoft(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Util");
            if (game == null) return;
            using var dir = new TempDir();
            using var server = Server(dir);
            string newsfeeds = MiniJson.Serialize(MiniJson.ParseObject(Document(server))["newsfeeds"]);
            Assert.Equal("[" + CrmNewsfeedTests.ItemJson + "]", newsfeeds);

            Type jtoken = game.Type("Newtonsoft.Json", "Newtonsoft.Json.Linq.JToken");
            object token = GameClient.CallStatic(jtoken, "Parse", newsfeeds);
            dynamic data = Activator.CreateInstance(game.Op("NewsFeedData"), token);
            dynamic item = Assert.Single(((IEnumerable)data.Items).Cast<object>());
            Assert.Equal(CrmNewsfeed.NewsId, (string)item.Id);
            Assert.Equal(CrmNewsfeedTests.Marker, (string)item.RedirectLink);
            Assert.Equal(Monday, (DateTime)item.CreationDate);
        }

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void TheInboxMessageIsFromMcvsAndLinksToTheMarker(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Engine", "Game", "Gameloft.Util");
            if (game == null) return;
            using var dir = new TempDir();
            using var server = Server(dir);
            dynamic crm = Activator.CreateInstance(game.Op("StandardCrm+Data"), Document(server));
            object item = Assert.Single(Items(crm));

            // InboxComponent.UpdateNewsFeedItem: kept while CreationDate + ExpirationDays is not in the past, then
            // new InboxMessage(nf, now + ExpirationDays) when the inbox does not have it yet.
            Type component = game.Type("Game", "Game.InboxComponent");
            int days = GameClient.Static(component, "ExpirationDays");
            Assert.Equal(30, days);
            DateTime created = (DateTime)((dynamic)item).CreationDate;
            Assert.False(created.AddDays(days) < CrmNewsfeedTests.Wednesday);
            Assert.False(created.AddDays(days) < CrmNewsfeedTests.Wednesday.AddDays(5)); // the last second of the week too

            dynamic message = Activator.CreateInstance(game.Type("Game", "Game.InboxMessage"), item, CrmNewsfeedTests.Wednesday.AddDays(days));
            Assert.Equal("newsfeed_mcvsoffline_FromMCVS", (string)message.Id);
            Assert.Equal("MCVS", (string)message.From);
            Assert.Equal(CrmNewsfeedTests.Marker, (string)message.RedirectLink);
            Assert.Equal(CrmNewsfeed.Title, (string)message.Title);
            Assert.Equal(CrmNewsfeed.Body, (string)message.Message);
            Assert.Equal("NewsFeed", message.Type.ToString());
            Assert.Equal("", (string)message.AssetName);
            Assert.False((bool)message.IsClaimable); // no rewards: nothing to claim
            Assert.False((bool)message.Pinned);
            Assert.Equal(Monday, (DateTime)message.CreationDate);

            // RemoveExpiredNewsFeed maps the inbox id back to the news id: our message stays while we list it.
            object inbox = RuntimeHelpers.GetUninitializedObject(component);
            Assert.Equal(CrmNewsfeed.NewsId, (string)GameClient.Call(inbox, "ToNewsFeedIdFromInboxId", (string)message.Id));
            Assert.Equal((string)message.Id, (string)GameClient.Call(inbox, "ToInboxIdFromNewsFeedId", CrmNewsfeed.NewsId));
        }

        /// <summary>
        /// PC only (docs/design/pc-port.md §4.3): the Steam client keeps every item in the static
        /// <c>NewsFeedData.Newsfeeds</c> (keyed by <c>asset</c>, first item wins) and gives an inbox message whose creation
        /// date differs from its item's that item's <c>EndDate</c> as expiry. Our item is the one entry for asset "", and its
        /// EndDate is our end_date, 37 days after the week start, in every week: the message no longer expires at the
        /// Monday rollover.
        /// </summary>
        [Fact]
        public void PcNewsfeedDictionaryHoldsOurItemWithItsEndDate()
        {
            // A private load context: StandardCrm.Data of the other PC tests (running in parallel) adds to the shared one's
            // static Newsfeeds dictionary.
            GameClient game = GameClient.TryGetPrivate(output, TestFolders.Pc, TestFolders.PcRefs, "Gameloft.Util");
            if (game == null) return;
            Type newsFeedData = game.Op("NewsFeedData");
            var newsfeeds = (IDictionary)GameClient.Static(newsFeedData, "Newsfeeds");
            Assert.NotNull(newsfeeds); // the PC member (Android's NewsFeedData has none)
            Type jtoken = game.Type("Newtonsoft.Json", "Newtonsoft.Json.Linq.JToken");

            foreach (DateTime now in new[] { CrmNewsfeedTests.Wednesday, CrmNewsfeedTests.Wednesday.AddDays(7) })
            {
                using var dir = new TempDir();
                using var server = AdminTestClient.CreateServer(dir.Combine("data"), clock: new ManualClock(now));
                string list = MiniJson.Serialize(MiniJson.ParseObject(Document(server))["newsfeeds"]);
                newsfeeds.Clear(); // a new game process: the static dictionary starts empty
                Activator.CreateInstance(newsFeedData, GameClient.CallStatic(jtoken, "Parse", list));
                Assert.Single(newsfeeds);
                dynamic item = newsfeeds[""];
                Assert.Equal(CrmNewsfeed.NewsId, (string)item.Id);
                DateTime monday = CrmNewsfeed.WeekStart(now);
                Assert.Equal(monday, ((DateTime)item.CreationDate).ToUniversalTime());
                DateTime end = ((DateTime)item.EndDate).ToUniversalTime();
                Assert.Equal(monday.AddDays(CrmNewsfeed.EndDateDays), end);
                Assert.True(end > now.AddDays(30), "the message must outlive the client's own 30 days");
            }
        }

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void TurnedOffTheEmptyListReachesTheInboxCleanup(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Util");
            if (game == null) return;
            using var dir = new TempDir();
            using var server = Server(dir);
            server.Context.Settings.Override("crm.newsfeedLink", false);
            dynamic crm = Activator.CreateInstance(game.Op("StandardCrm+Data"), Document(server));
            // OnCrmDataSet returns early when NewsFeed (or its Items) is null; with [] it runs RemoveExpiredNewsFeed,
            // which deletes our message because its id is no longer listed.
            Assert.NotNull(crm.NewsFeed);
            Assert.Empty(Items(crm));
        }

        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void TheSanitizerKeepsABrokenOverlayFromBreakingTheClient(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Util");
            if (game == null) return;
            Type data = game.Op("StandardCrm+Data");
            using var dir = new TempDir();
            using var server = Server(dir);
            var raw = MiniJson.ParseObject(Document(server));

            // What the client does with a newsfeeds that is not an array: the whole document fails.
            raw["newsfeeds"] = MiniJson.ParseObject("{\"oops\":true}");
            var thrown = Assert.ThrowsAny<Exception>(() => Activator.CreateInstance(data, MiniJson.Serialize(raw)));
            output.WriteLine("unsanitized: " + (thrown is TargetInvocationException ? thrown.InnerException : thrown).GetType().Name);
            // And with an entry whose timestamp has a fraction: Convert.ToInt64 fails, the item looks 2000 years old.
            raw["newsfeeds"] = MiniJson.ParseArray("[{\"news_id\":42,\"title\":\"x\",\"timestamp\":1792972800.5}]");
            dynamic unsanitized = Activator.CreateInstance(data, MiniJson.Serialize(raw));
            Assert.Equal(default(DateTime), (DateTime)((dynamic)Assert.Single(Items(unsanitized))).CreationDate);

            // Through crm.overlay the server sanitizes both: the document parses, our item is there and the entry is usable.
            server.Context.Settings.Override("crm.overlay", MiniJson.ParseObject("{\"newsfeeds\":{\"oops\":true}}"));
            dynamic crm = Activator.CreateInstance(data, Document(server));
            Assert.Equal(CrmNewsfeed.NewsId, (string)((dynamic)Assert.Single(Items(crm))).Id);

            server.Context.Settings.Override("crm.overlay", MiniJson.ParseObject("{\"newsfeeds\":[{\"news_id\":42,\"title\":\"x\",\"timestamp\":1792972800.5}]}"));
            crm = Activator.CreateInstance(data, Document(server));
            List<object> items = Items(crm);
            Assert.Equal(new[] { "42", CrmNewsfeed.NewsId }, items.Select(i => (string)((dynamic)i).Id).ToArray());
            Assert.Equal(Monday, (DateTime)((dynamic)items[0]).CreationDate);
        }
    }
}
