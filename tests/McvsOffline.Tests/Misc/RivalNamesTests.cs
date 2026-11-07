using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Names;
using McvsOffline.Server.Services.Leaderboards;
using McvsOffline.Server.Services.Social.LiveOps;
using McvsOffline.Tests.Content;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Misc
{
    /// <summary>
    /// The names and emblems of the generated leaderboard rivals (docs/LIVEOPS.md "Rival names and emblems"): gamer tags
    /// from names.json, a pure function of the rival's id, unique on a board, within the client's limits and the blocklist,
    /// and the emblem fields the client's rows read (GM/LeaderboardsComponent.cs GetEntriesObject).
    /// </summary>
    public class RivalNamesTests
    {
        private const string Board = "/leaderboards/desc/Leaderboard_Season42_Div3";
        private static readonly Regex Shape = new Regex("^[A-Za-z0-9_.-]{3,16}$");

        private readonly ITestOutputHelper output;

        public RivalNamesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static List<Dictionary<string, object>> Rows(MiscServer s, TestPlayer p, string board = Board)
        {
            var top = MiscServer.Obj(s.Get(board + "?offset=0&limit=100", p.OnBehalfToken));
            return ((List<object>)top["data"]).Cast<Dictionary<string, object>>().ToList();
        }

        private static void PostScore(MiscServer s, TestPlayer p, int score, string name, string board = Board)
        {
            Assert.Equal(200, s.Post(board + "/me", p.OnBehalfToken, "score", score.ToString(CultureInfo.InvariantCulture), "display_name", name,
                                     "_level", "12", "_fed_id", p.FedId, "_killSignatureEmblem", "57", "_killSignatureBg", "12").StatusCode);
        }

        /// <summary>Words a gamer tag must never contain, read the way a player would (case, separators, digits as letters).</summary>
        private static readonly string[] Forbidden = { "fuck", "shit", "nazi", "hitler", "ninja", "batman", "trump", "isis", "porn", "jesus" };

        private static string Readable(string name)
        {
            var map = new Dictionary<char, char> { ['0'] = 'o', ['1'] = 'i', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['7'] = 't', ['8'] = 'b' };
            return new string(name.ToLowerInvariant().Where(c => c != '_' && c != '.' && c != '-').Select(c => map.TryGetValue(c, out char m) ? m : c).ToArray());
        }

        [Fact]
        public void EveryRivalRowHasAUniqueGamerTagAndItsOwnEmblem()
        {
            using var s = new MiscServer();
            TestPlayer p = s.NewPlayer("rows");
            PostScore(s, p, 150, "RealPlayer");
            List<Dictionary<string, object>> rows = Rows(s, p);
            Assert.Equal(100, rows.Count);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GamerTags tags = GamerTags.BuiltIn;
            var emblems = new HashSet<string>();
            foreach (var row in rows)
            {
                string name = (string)row["display_name"];
                Assert.True(names.Add(name), "two rows are named " + name);
                if ((string)row["credential"] == p.FedId) continue;
                Assert.Equal(true, row["_mcvs_bot"]);
                Assert.Matches(Shape, name);
                Assert.Null(GamerTags.ShapeProblem(name));
                // The client reads both ids as strings and falls back to the viewer's own emblem without them.
                string avatar = Assert.IsType<string>(row["_killSignatureEmblem"]);
                string background = Assert.IsType<string>(row["_killSignatureBg"]);
                Assert.Contains(int.Parse(avatar, CultureInfo.InvariantCulture), tags.Avatars);
                Assert.InRange(int.Parse(background, CultureInfo.InvariantCulture), 1, 35);
                Assert.IsType<string>(row["_level"]);
                Assert.False(row.ContainsKey("_avatar"));
                Assert.False(row.ContainsKey("_leagueId"));
                Assert.False(row.ContainsKey("_clanId"));
                emblems.Add(avatar);
            }
            Assert.True(emblems.Count >= 40, "only " + emblems.Count + " different emblems among 99 rivals");
            output.WriteLine(string.Join(", ", rows.Take(40).Select(r => r["display_name"])));
        }

        [Fact]
        public void ARivalKeepsItsNameAndEmblemWhenTheNumberOfRealPlayersChanges()
        {
            // The rival in slot k has the same id whatever the count; up to build 27 its name followed the score draws.
            var ten = VirtualPlayers.Generate("desc/board", 10, 400);
            var forty = VirtualPlayers.Generate("desc/board", 40, 400);
            var byCredential = forty.ToDictionary(e => (string)e["credential"]);
            foreach (var e in ten)
            {
                var same = byCredential[(string)e["credential"]];
                Assert.Equal(e["display_name"], same["display_name"]);
                Assert.Equal(e["_killSignatureEmblem"], same["_killSignatureEmblem"]);
                Assert.Equal(e["_killSignatureBg"], same["_killSignatureBg"]);
            }
            // ...and across calls (a new session) and servers (the other platform): a pure function of the board and slot.
            Assert.Equal(MiniJson.Serialize(forty), MiniJson.Serialize(VirtualPlayers.Generate("desc/board", 40, 400)));
        }

        [Fact]
        public void ARealPlayersNameIsNeverARivalsAndTheRivalsBeforeKeepTheirs()
        {
            using var s = new MiscServer();
            TestPlayer alice = s.NewPlayer("alice");
            PostScore(s, alice, 150, "Alice");
            List<Dictionary<string, object>> before = Rows(s, alice);
            var rivals = before.Where(r => r.ContainsKey("_mcvs_bot")).ToList();
            // A second player takes (in another case) the name a rival has now.
            string taken = (string)rivals[30]["display_name"];
            TestPlayer bob = s.NewPlayer("bob");
            PostScore(s, bob, 140, taken.ToUpperInvariant());
            List<Dictionary<string, object>> after = Rows(s, alice);
            Assert.Equal(after.Count, after.Select(r => (string)r["display_name"]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Single(after, r => string.Equals((string)r["display_name"], taken, StringComparison.OrdinalIgnoreCase));

            // A third takes a rival's name with a number after it: no rival keeps a name that reads the same (Frost27 / frost-27).
            string plain = (string)rivals.Skip(40).First(r => !((string)r["display_name"]).Any(char.IsDigit))["display_name"];
            TestPlayer carol = s.NewPlayer("carol");
            PostScore(s, carol, 130, plain.ToLowerInvariant() + "77");
            List<Dictionary<string, object>> third = Rows(s, alice);
            Assert.Single(third, r => GamerTags.Skeleton((string)r["display_name"]) == GamerTags.Skeleton(plain));
            Assert.Equal(third.Count, third.Select(r => GamerTags.Skeleton((string)r["display_name"])).Distinct().Count());
            // Rivals are named in slot order: the slots before the one that lost its name keep theirs.
            var names = after.Where(r => r.ContainsKey("_mcvs_bot")).ToDictionary(r => (string)r["credential"], r => (string)r["display_name"]);
            int kept = rivals.Count(r => names.TryGetValue((string)r["credential"], out string n) && n == (string)r["display_name"]);
            Assert.True(kept >= 30, kept + " rivals kept their names");
        }

        [Fact]
        public void SeasonRivalsGetGamerTagsAndEmblemsAndKeepTheirPacesAndLevels()
        {
            DateTime mid = new DateTime(2026, 10, 21, 9, 30, 0, DateTimeKind.Utc);
            using var s = new MiscServer(new McvsOffline.Server.Core.ManualClock(mid));
            var settings = new McvsOffline.Server.Core.Settings();
            LiveOpsWindow season = LiveOpsSchedule.SeasonAt(LiveOpsSettings.From(settings, null), mid);
            Assert.True(LiveOpsService.For(s.Context).TryGetSeasonBoard(LiveOpsSchedule.BoardName(season, 3), out SeasonBoardInfo info));
            var rivals = SeasonRivals.Generate("desc/mcvs_division_s1_d01_Leaderboard", info, 99, 100, 20, mid);
            Assert.Equal(99, rivals.Count);
            Assert.Equal(99, rivals.Select(r => (string)r["display_name"]).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var r in rivals)
            {
                Assert.Matches(Shape, (string)r["display_name"]);
                Assert.Contains(int.Parse((string)r["_killSignatureEmblem"], CultureInfo.InvariantCulture), GamerTags.BuiltIn.Avatars);
                Assert.IsType<string>(r["_killSignatureBg"]);
            }
            // The state the paces and levels come from still advances by two draws where the callsign was (build 27).
            double[] finals = SeasonRivals.FinalScores("desc/mcvs_division_s1_d01_Leaderboard", info, 99, 100, 20);
            double x = info.Season.Progress(mid);
            ulong state = LiveOpsHash.Fnv64("mcvs-offline/season-rival/v1|desc/mcvs_division_s1_d01_Leaderboard");
            for (int i = 0; i < 99; i++)
            {
                double pace = 0.8 + 0.45 * LiveOpsHash.Unit(ref state);
                LiveOpsHash.Next(ref state);
                LiveOpsHash.Next(ref state);
                int level = (int)Math.Max(1, Math.Min(50, 20 + 4 - (i * 9 / 99) - LiveOpsHash.Below(ref state, 5)));
                Assert.Equal((long)Math.Round(finals[i] * Math.Pow(x, pace)), rivals[i]["score"]);
                Assert.Equal(level.ToString(CultureInfo.InvariantCulture), rivals[i]["_level"]);
            }
        }

        [Fact]
        public void NamesAreDeterministicVariedAndWithinTheLimitsAndTheBlocklist()
        {
            GamerTags tags = GamerTags.BuiltIn;
            Assert.Equal("built-in names.json", tags.Source);
            Assert.InRange(tags.MaxLength, 3, GamerTags.ClientMaxLength);
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var styles = new HashSet<string>();
            int refused = 0;
            const int n = 20000;
            for (int i = 0; i < n; i++)
            {
                string id = "test-" + i.ToString(CultureInfo.InvariantCulture);
                string first = tags.Name(RivalIdentity.NameScope, id, 0);
                Assert.Equal(first, tags.Name(RivalIdentity.NameScope, id, 0));
                if (first == null) refused++;
                string name = tags.UniqueName(RivalIdentity.NameScope, id, null);
                Assert.Matches(Shape, name);
                Assert.Null(GamerTags.ShapeProblem(name));
                Assert.DoesNotContain('<', name);
                string readable = Readable(name);
                foreach (string word in Forbidden) Assert.DoesNotContain(word, readable);
                Assert.Null(tags.BlockedReason(new[] { name }));
                distinct.Add(name);
                if (name.Contains('_')) styles.Add("_");
                if (name.Any(char.IsDigit)) styles.Add("digits");
                if (name.StartsWith("agent-", StringComparison.Ordinal)) styles.Add("default");
                if (name == name.ToLowerInvariant()) styles.Add("lower");
            }
            // Across 200 boards' worth of names a few single-word tags repeat (a handle alone, "The" + a noun); on one board
            // UniqueName never repeats one.
            Assert.True(distinct.Count >= n * 92 / 100, distinct.Count + " distinct names of " + n);
            Assert.Equal(new[] { "_", "default", "digits", "lower" }, styles.OrderBy(x => x));
            Assert.True(refused < n / 10, refused + " first draws refused");
            output.WriteLine(string.Join(", ", Enumerable.Range(0, 60).Select(i => tags.UniqueName(RivalIdentity.NameScope, "sample-" + i, null))));
        }

        [Fact]
        public void NoBoardShowsTwoRivalsThatReadAsTheSamePlayer()
        {
            // Build 28's first cut: about one 99-row board in 16 had a pair like Frost27 / frost-27, MagneticNova /
            // magnetic_nova or blindside_563 / blindside_386.
            for (int b = 0; b < 300; b++)
            {
                var rivals = VirtualPlayers.Generate("desc/board-" + b.ToString(CultureInfo.InvariantCulture), 99, 400);
                var skeletons = new Dictionary<string, string>();
                foreach (var r in rivals)
                {
                    string name = (string)r["display_name"];
                    string skeleton = GamerTags.Skeleton(name);
                    Assert.False(skeletons.TryGetValue(skeleton, out string other), "board " + b + ": " + name + " beside " + other);
                    skeletons[skeleton] = name;
                }
            }
        }

        [Fact]
        public void ASkeletonIsTheNameAsAPlayerTellsTwoNamesApart()
        {
            Assert.Equal("frost", GamerTags.Skeleton("Frost27"));
            Assert.Equal("frost", GamerTags.Skeleton("frost-27"));
            Assert.Equal(GamerTags.Skeleton("blindside_563"), GamerTags.Skeleton("blindside_386"));
            Assert.Equal(GamerTags.Skeleton("MagneticNova"), GamerTags.Skeleton("magnetic_nova"));
            Assert.Equal(GamerTags.Skeleton("DarkStag"), GamerTags.Skeleton("dark.stag"));
            Assert.Equal("tacticalleopard", GamerTags.Skeleton("7ac7icalLeop4rd"));
            Assert.Equal("bravokr", GamerTags.Skeleton("Brav0_KR"));
            Assert.Equal("lionca", GamerTags.Skeleton("L10n_CA"));
            Assert.NotEqual(GamerTags.Skeleton("agent-123456789"), GamerTags.Skeleton("agent-987654321"));
            Assert.NotEqual(GamerTags.Skeleton("FrostGG"), GamerTags.Skeleton("Frost27"));
            var taken = RivalIdentity.Taken(new[] { "Frost27" });
            Assert.False(GamerTags.Reserve(taken, "frost-99"));
            Assert.False(GamerTags.Reserve(taken, "FROST27"));
            Assert.True(GamerTags.Reserve(taken, "FrostByte"));
            Assert.False(GamerTags.Reserve(taken, "frostbyte"));
        }

        [Fact]
        public void NoNameDrawsOneWordTwiceOrPutsACriminalNextToACountry()
        {
            GamerTags tags = GamerTags.BuiltIn;
            var doubled = new HashSet<string>(new[] { "adj", "noun", "handle", "short", "foreign", "mascot" }
                .SelectMany(l => tags.Part(l)).Select(w => w.ToLowerInvariant() + w.ToLowerInvariant()));
            // The adj and noun lists share no word (ShadowShadow came from both); nounNoun draws again on the same noun.
            Assert.Empty(tags.Part("adj").Intersect(tags.Part("noun"), StringComparer.OrdinalIgnoreCase));
            string[] rough = { "bandit", "outlaw", "pirate", "raider", "coyote", "jackal", "gunner", "commando", "sniper", "tsunami" };
            Assert.Empty(tags.Part("mascot").Select(w => w.ToLowerInvariant()).Intersect(rough));
            var country = new Regex("^(.+)_([A-Z][A-Z0-9]|[0-9][A-Z])$");
            int countries = 0;
            for (int i = 0; i < 100000; i++)
            {
                string name = tags.Name(RivalIdentity.NameScope, "pair-" + i.ToString(CultureInfo.InvariantCulture), 0);
                if (name == null) continue;
                Assert.DoesNotContain(Readable(name), doubled);
                Match m = country.Match(name);
                if (!m.Success) continue;
                countries++;
                Assert.DoesNotContain(Readable(m.Groups[1].Value), rough);
                Assert.Matches("^[A-Z]{2}$", m.Groups[2].Value);   // leet never touches the code (no L30pard_R0)
            }
            Assert.True(countries > 1000, countries + " country tags");
        }

        [Fact]
        public void TheShapeRuleRefusesWhatTheClientCannotShowOrReadsAsANumber()
        {
            foreach (string bad in new[] { "1337", "-42", "+7", "*3", "1e5", "NaN", "Infinity", "_Fox", "Fox_", "Fo__x", "Fo.-x", "A<b>c", "Fox&Co",
                                           "Fox Co", "x", "ThisNameIsWayTooLong", "Zoë", "12" })
                Assert.NotNull(GamerTags.ShapeProblem(bad));
            foreach (string good in new[] { "SilentFox", "silent_fox", "Raven1998", "xXRavenXx", "agent-123456789", "Kn1ght_FR", "fox.ace", "ab" })
                Assert.Null(GamerTags.ShapeProblem(good));
        }

        [Fact]
        public void TheBlocklistReadsLeetCaseSeparatorsJoinsAndNumbers()
        {
            GamerTags tags = GamerTags.BuiltIn;
            Assert.NotNull(tags.BlockedReason(new[] { "Sh1tStorm" }));      // digits read as letters
            Assert.NotNull(tags.BlockedReason(new[] { "N_A_Z_I" }));         // separators ignored
            Assert.NotNull(tags.BlockedReason(new[] { "Wolf", "455" }));     // 455 is a part that reads 'ass'
            Assert.NotNull(tags.BlockedReason(new[] { "Atlas", "Sniper" })); // 'ass' across the join
            Assert.NotNull(tags.BlockedReason(new[] { "Fox", "88" }));
            Assert.NotNull(tags.BlockedReason(new[] { "Ranger", "14" }));
            Assert.NotNull(tags.BlockedReason(new[] { "Ninja", "07" }));     // a streamer's name
            // Brands, streamers, other games' characters and slang that two harmless words make.
            foreach (string[] pieces in new[]
                     {
                         new[] { "Sniper", "Wolf" }, new[] { "Iron", "Wolf" }, new[] { "1r0n", "W0lf" }, new[] { "Blue", "Falcon" },
                         new[] { "Echo", "Fox" }, new[] { "Spartan", "117" }, new[] { "Blue", "Yeti" }, new[] { "blu3_", "y3t1" },
                         new[] { "Red", "Rocket" }, new[] { "Iron", "Golem" }, new[] { "Ghost", "Gaming" }, new[] { "Nova", "Gaming" },
                         new[] { "Steel", "Panther" }, new[] { "Night", "Ranger" }, new[] { "The", "Red", "Guardian" },
                         new[] { "Hollow", "Knight" }, new[] { "Soldier", "76" }, new[] { "Cpt", "Falcon" }, new[] { "Stellar", "Blade" },
                     })
                Assert.NotNull(tags.BlockedReason(pieces));
            Assert.Null(tags.BlockedReason(new[] { "Silent", "Fox" }));
            Assert.Null(tags.BlockedReason(new[] { "Ranger", "1448" }));
            Assert.Null(tags.BlockedReason(new[] { "Glass", "Cannon" }));    // 'ass' inside one harmless part
        }

        [Fact]
        public void EmblemsComeFromThePlainProfileAvatarsAndSomeKeepTheStartingOne()
        {
            GamerTags tags = GamerTags.BuiltIn;
            // KillsignatureAvatars rows in profile/avatars/ with Unlockable=true (1.17.32): never Kayac (443-1514, 382-384,
            // 499), season placement emblems (253-321), agents (322-442) or medals.
            Assert.Equal(222, tags.Avatars.Length);
            Assert.All(tags.Avatars, a => Assert.True((a >= 1 && a <= 6) || (a >= 9 && a <= 224), a + " is not a plain profile avatar"));
            Assert.Equal(Enumerable.Range(1, 35), tags.Backgrounds);
            int defaults = 0;
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < 5000; i++)
            {
                tags.Emblem(RivalIdentity.EmblemScope, "e" + i, out int avatar, out int background);
                tags.Emblem(RivalIdentity.EmblemScope, "e" + i, out int again, out int againBackground);
                Assert.Equal(avatar, again);
                Assert.Equal(background, againBackground);
                if (avatar == 1 && background == 1) defaults++;
                counts[avatar] = counts.TryGetValue(avatar, out int c) ? c + 1 : 1;
            }
            Assert.InRange(defaults, 5000 * tags.DefaultShare / 100 - 150, 5000 * tags.DefaultShare / 100 + 150);
            Assert.True(counts.Count > 200, counts.Count + " different emblems");
        }

        [Fact]
        public void AnOverrideFileIsUsedAndABrokenOneIsRefusedForTheBuiltInFile()
        {
            using var dir = new TempDir();
            string contentDir = dir.Combine("content");
            Directory.CreateDirectory(contentDir);
            var logs = new McvsOffline.Server.Core.MemoryLogSink();
            var logger = new McvsOffline.Server.Core.Logger();
            logger.AddSink(logs);
            var provider = new ContentProvider(null, contentDir, logger.For("content"));

            Assert.Same(GamerTags.BuiltIn, GamerTags.For(provider, logger.For("test")));

            // A valid override with one style and a few words: every name comes from it (5 x 6 word pairs: names on a board
            // also differ without their numbers).
            var doc = MiniJson.ParseObject(File.ReadAllText(Path.Combine(ContentTestSupport.RepoRoot, "server", "Content", "names.json")));
            var parts = (Dictionary<string, object>)doc["parts"];
            parts["adj"] = new List<object> { "Plaid", "Suede", "Velvet", "Denim", "Linen" };
            parts["noun"] = new List<object> { "Walrus", "Narwhal", "Puffin", "Lemur", "Gecko", "Heron" };
            doc["styles"] = new List<object> { MiniJson.ParseObject("{\"id\":\"only\",\"pattern\":\"{adj}{noun}{d2}\",\"weight\":1}") };
            File.WriteAllText(Path.Combine(contentDir, GamerTags.FileName), MiniJson.Serialize(doc));
            GamerTags custom = GamerTags.For(provider, logger.For("test"));
            Assert.Equal(GamerTags.FileName, custom.Source);
            var rivals = VirtualPlayers.Generate("desc/custom", 20, 400, custom, null);
            Assert.All(rivals, r => Assert.Matches("^(Plaid|Suede|Velvet|Denim|Linen)(Walrus|Narwhal|Puffin|Lemur|Gecko|Heron)[0-9]{2}$", (string)r["display_name"]));

            // Emblem ids outside the client's tables: refused, logged, the built-in file used.
            doc["emblems"] = MiniJson.ParseObject("{\"avatars\":\"0-3\",\"backgrounds\":\"1-35\"}");
            File.WriteAllText(Path.Combine(contentDir, GamerTags.FileName), MiniJson.Serialize(doc));
            Assert.Same(GamerTags.BuiltIn, GamerTags.For(provider, logger.For("test")));
            Assert.True(logs.Contains("names.json refused"));

            // A part the blocklist refuses: the same.
            doc["emblems"] = MiniJson.ParseObject("{\"avatars\":\"1-6\",\"backgrounds\":\"1-35\"}");
            parts["noun"] = new List<object> { "Walrus", "Nazi" };
            File.WriteAllText(Path.Combine(contentDir, GamerTags.FileName), MiniJson.Serialize(doc));
            Assert.Same(GamerTags.BuiltIn, GamerTags.For(provider, logger.For("test")));
        }

        /// <summary>
        /// Every emblem a rival may show resolves in the player's own MCVS_Meta (Android pack, and the Steam pack when it is
        /// there) to a plain profile avatar whose image is in the pack: PlayerProfileComponent.GetSignatureObject finds the
        /// row by its string id, and an unknown id would show a blank badge.
        /// </summary>
        [Fact]
        public void EveryPooledEmblemIsAPlainProfileAvatarOfTheRealDataPacks()
        {
            var packs = new List<string>();
            if (ContentTestSupport.RealDataPack != null) packs.Add(ContentTestSupport.RealDataPack);
            string pc = Path.Combine(Environment.GetEnvironmentVariable("MCVS_PC_REFS") ?? TestFolders.DefaultPcRefs, "Data");
            if (File.Exists(Path.Combine(pc, "MCVS.game"))) packs.Add(pc);
            if (packs.Count == 0)
            {
                Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") != "1", "MCVS_REQUIRE_REAL_CLIENT=1 but no data pack");
                output.WriteLine("no data pack: skipped");
                return;
            }
            foreach (string dir in packs)
            {
                var pack = new DataPack(dir);
                Assert.True(pack.TryReadText("Design/MCVS_Meta.xlsx", out string text));
                var table = (Dictionary<string, object>)MiniJson.ParseObject(text)["KillsignatureAvatars"];
                var fields = ((List<object>)table["Fields"]).Cast<string>().ToList();
                var rows = ((List<object>)table["Rows"]).Cast<List<object>>()
                    .ToDictionary(r => JsonValue.AsString(r[fields.IndexOf("ID")]), r => r);
                foreach (int id in GamerTags.BuiltIn.Avatars)
                {
                    Assert.True(rows.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out List<object> row), id + " has no row in " + dir);
                    string asset = (string)row[fields.IndexOf("Asset")];
                    Assert.StartsWith("Menus/assets/png/profile/avatars/avatar", asset);
                    Assert.Equal("true", JsonValue.AsString(row[fields.IndexOf("Unlockable")]).ToLowerInvariant());
                    Assert.True(pack.Contains(asset), asset + " is not in " + dir);
                }
                var backgrounds = (Dictionary<string, object>)MiniJson.ParseObject(text)["KillsignatureBackgrounds"];
                var bgIds = ((List<object>)backgrounds["Rows"]).Cast<List<object>>().Select(r => JsonValue.AsString(r[0])).ToHashSet();
                Assert.All(GamerTags.BuiltIn.Backgrounds, b => Assert.Contains(b.ToString(CultureInfo.InvariantCulture), bgIds));
                output.WriteLine(dir + ": " + GamerTags.BuiltIn.Avatars.Length + " emblems and " + GamerTags.BuiltIn.Backgrounds.Length + " backgrounds resolve");
            }
        }
    }
}
