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
    /// from names.json (the curated list first since build 29, the generator once it is used up), a pure function of the
    /// rival's id, unique on a board, within the client's limits and the blocklist, and the emblem fields the client's rows
    /// read (GM/LeaderboardsComponent.cs GetEntriesObject).
    /// </summary>
    public class RivalNamesTests
    {
        private const string Board = "/leaderboards/desc/Leaderboard_Season42_Div3";
        /// <summary>The build-28 shape every generated name keeps.</summary>
        private static readonly Regex Shape = new Regex("^[A-Za-z0-9_.-]{3,16}$");

        /// <summary>The built-in names.json without its curated list: the generator alone, as a board past the list uses it.</summary>
        private static GamerTags GeneratorOnly()
        {
            var doc = MiniJson.ParseObject(File.ReadAllText(Path.Combine(ContentTestSupport.RepoRoot, "server", "Content", "names.json")));
            Assert.True(doc.Remove("curated"));
            GamerTags tags = GamerTags.Parse(MiniJson.Serialize(doc), out string problem);
            Assert.True(tags != null, problem);
            Assert.Empty(tags.Curated);
            return tags;
        }

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
        private static readonly string[] Forbidden = { "fuck", "shit", "nazi", "hitler", "trump", "porn", "jesus" };

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
                // A board of 100 is far from using up the curated list: every rival has a curated name (build 29).
                Assert.True(tags.IsCurated(name), name + " is not a curated name");
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
                Assert.True(GamerTags.BuiltIn.IsCurated((string)r["display_name"]), (string)r["display_name"]);
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
        public void GeneratedNamesAreDeterministicVariedAndWithinTheLimitsAndTheBlocklist()
        {
            Assert.Equal("built-in names.json", GamerTags.BuiltIn.Source);
            GamerTags tags = GeneratorOnly();
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
                Assert.Null(GamerTags.GeneratedShapeProblem(name));
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
            // Across 200 boards' worth of names a few single-word tags repeat (a handle alone, "The" + a noun; fewer numbers
            // since build 29); on one board UniqueName never repeats one.
            Assert.True(distinct.Count >= n * 85 / 100, distinct.Count + " distinct names of " + n);
            Assert.Equal(new[] { "_", "default", "digits", "lower" }, styles.OrderBy(x => x));
            Assert.True(refused < n / 10, refused + " first draws refused");
            output.WriteLine(string.Join(", ", Enumerable.Range(0, 60).Select(i => tags.UniqueName(RivalIdentity.NameScope, "sample-" + i, null))));
        }

        /// <summary>
        /// The owner's verdict on build 28: many names read like an automatically picked console name. The templates that
        /// made them are gone from the generator (Adjective+Noun+digits, xXNameXx, x_name_x, ...Plays/...Gaming/...Wins,
        /// Word_NN, word-NN) and names with digits are rare; and the generator only names a board past the curated list.
        /// </summary>
        [Fact]
        public void TheGeneratorHasNoGenericTemplatesLeft()
        {
            GamerTags tags = GeneratorOnly();
            Assert.DoesNotContain("adjNoun_d2", tags.StyleIds);
            Assert.DoesNotContain("xXnounXx", tags.StyleIds);
            Assert.DoesNotContain("x_noun_x", tags.StyleIds);
            Assert.DoesNotContain("short_d2", tags.StyleIds);
            Assert.DoesNotContain("noun-d2", tags.StyleIds);
            Assert.Empty(tags.Part("suffix").Intersect(new[] { "Plays", "Gaming", "Wins" }));
            var adjNounDigits = new Regex("^(" + string.Join("|", tags.Part("adj")) + ")(" + string.Join("|", tags.Part("noun")) + ")[0-9]{2}$");
            int digits = 0;
            const int n = 5000;
            for (int i = 0; i < n; i++)
            {
                string name = tags.UniqueName(RivalIdentity.NameScope, "generic-" + i.ToString(CultureInfo.InvariantCulture), null);
                // Only leetspeak ends like that (LunarBullet as LunarBull37): its digits read back as letters.
                if (adjNounDigits.IsMatch(name)) Assert.True(Readable(name).All(char.IsLetter), name);
                Assert.DoesNotMatch("^xX.*Xx$", name);
                Assert.DoesNotMatch("^x_.*_x$", name);
                Assert.DoesNotMatch("(Plays|Gaming|Wins)$", name);
                Assert.DoesNotMatch("^[A-Za-z]+[_-][0-9]{2}$", name);
                if (Regex.IsMatch(name, "[0-9]{2,}") && !Readable(name).All(char.IsLetter)) digits++;   // a number, not leetspeak
            }
            Assert.True(digits < n / 5, digits + " of " + n + " generated names have a number");
        }

        [Fact]
        public void ANormalBoardTakesEveryNameFromTheCuratedList()
        {
            GamerTags tags = GamerTags.BuiltIn;
            Assert.True(tags.Curated.Length >= 1500, tags.Curated.Length + " curated names");
            // A sample of 50 rivals of one board: all curated, all different.
            var sample = VirtualPlayers.Generate("desc/sample-board", 50, 400);
            var names = sample.Select(r => (string)r["display_name"]).ToList();
            Assert.All(names, name => Assert.True(tags.IsCurated(name), name + " is not curated"));
            Assert.Equal(50, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            output.WriteLine(string.Join(" | ", names));
            // 300 boards of 99: never a generated name, and the boards differ (each rival's own order through the list).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int b = 0; b < 300; b++)
                foreach (var r in VirtualPlayers.Generate("desc/board-" + b.ToString(CultureInfo.InvariantCulture), 99, 400))
                {
                    Assert.True(tags.IsCurated((string)r["display_name"]), (string)r["display_name"]);
                    seen.Add((string)r["display_name"]);
                }
            Assert.True(seen.Count > tags.Curated.Length * 9 / 10, seen.Count + " different curated names on 300 boards");
        }

        [Fact]
        public void ACuratedNameIsAPureFunctionOfTheIdAndWhatIsTaken()
        {
            GamerTags tags = GamerTags.BuiltIn;
            for (int i = 0; i < 2000; i++)
            {
                string id = "pure-" + i.ToString(CultureInfo.InvariantCulture);
                string first = tags.UniqueName(RivalIdentity.NameScope, id, null);
                Assert.True(tags.IsCurated(first));
                Assert.Equal(first, tags.UniqueName(RivalIdentity.NameScope, id, null));
                Assert.Equal(first, tags.CuratedName(RivalIdentity.NameScope, id, RivalIdentity.Taken(null)));
                // Taken by a real player (in another case): the rival moves to another curated name, always the same one.
                var taken = RivalIdentity.Taken(new[] { first.ToUpperInvariant() });
                string moved = tags.UniqueName(RivalIdentity.NameScope, id, taken);
                Assert.NotEqual(first, moved, StringComparer.OrdinalIgnoreCase);
                Assert.True(tags.IsCurated(moved));
                Assert.Equal(moved, tags.UniqueName(RivalIdentity.NameScope, id, RivalIdentity.Taken(new[] { first.ToUpperInvariant() })));
            }
            // The same board on another server (the other platform, a new session) shows the same names.
            Assert.Equal(MiniJson.Serialize(VirtualPlayers.Generate("desc/pure", 99, 400)), MiniJson.Serialize(VirtualPlayers.Generate("desc/pure", 99, 400)));
        }

        [Fact]
        public void TheGeneratorOnlyNamesTheRivalsPastTheCuratedList()
        {
            GamerTags tags = GamerTags.BuiltIn;
            int curated = tags.Curated.Length;
            var real = new[] { tags.Curated[0], "Some Real Player" };
            var rivals = VirtualPlayers.Generate("desc/huge", curated + 40, 400, tags, real);
            var names = rivals.Select(r => (string)r["display_name"]).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(names.Count, names.Select(GamerTags.Skeleton).Distinct().Count());
            // Every curated name but the real player's is used before the first generated one.
            int firstGenerated = names.FindIndex(n => !tags.IsCurated(n));
            Assert.Equal(curated - 1, firstGenerated);
            Assert.Equal(curated - 1, names.Count(tags.IsCurated));
            Assert.DoesNotContain(tags.Curated[0], names, StringComparer.OrdinalIgnoreCase);
            Assert.All(names.Skip(firstGenerated), n => Assert.Null(GamerTags.GeneratedShapeProblem(n)));
        }

        [Fact]
        public void EveryCuratedNameFitsTheClientTheBlocklistAndNoTwoReadAlike()
        {
            GamerTags tags = GamerTags.BuiltIn;
            var skeletons = new Dictionary<string, string>();
            foreach (string name in tags.Curated)
            {
                Assert.Null(GamerTags.ShapeProblem(name));
                Assert.Null(tags.CuratedBlockedReason(name));
                Assert.InRange(name.Length, GamerTags.ClientMinLength, GamerTags.ClientMaxLength);
                Assert.DoesNotContain(name, ch => "<>&:{}/\\\"`".IndexOf(ch) >= 0);
                Assert.All(name, ch => Assert.True(ch < 0x100, name + " has a character outside Latin-1"));
                Assert.False(double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out _), name);
                string skeleton = GamerTags.Skeleton(name);
                if (skeleton.Length == 0) continue;
                Assert.False(skeletons.TryGetValue(skeleton, out string other), name + " reads like " + other);
                skeletons[skeleton] = name;
            }
            // Spaces, signs and Latin-1 letters are used: names a player would really type.
            Assert.Contains(tags.Curated, n => n.Contains(' '));
            Assert.Contains(tags.Curated, n => n.Contains('\''));
            Assert.Contains(tags.Curated, n => n.Any(ch => ch >= '\u00C0'));
            Assert.Contains(tags.Curated, n => n.StartsWith("[", StringComparison.Ordinal));
        }

        [Fact]
        public void NoTwoCuratedNamesAreOneLetterApartOrTheSameJokeTwice()
        {
            // Build 29's first list held Sheesh / Sheeesh / Sheeeesh, Scooby Snack / Scooby Snacks, Happy Accident /
            // Happy Accidents and more: two of them in one bot window or board read as one name copied. Skeletons of 5
            // letters or more one letter apart are only kept when names_tuning.json curatedNear lists them as two jokes.
            GamerTags tags = GamerTags.BuiltIn;
            var tuning = MiniJson.ParseObject(File.ReadAllText(Path.Combine(ContentTestSupport.RepoRoot, "tools", "content-gen", "names_tuning.json")));
            var allowed = new HashSet<string>();
            foreach (object pair in (List<object>)tuning["curatedNear"])
            {
                var two = ((List<object>)pair).Cast<string>().OrderBy(n => n, StringComparer.Ordinal).ToArray();
                allowed.Add(two[0] + "|" + two[1]);
            }
            Assert.NotEmpty(allowed);
            var near = new List<string>();
            string[] curated = tags.Curated;
            string[] skeletons = curated.Select(GamerTags.Skeleton).ToArray();
            for (int i = 0; i < curated.Length; i++)
            {
                if (skeletons[i].Length < 5) continue;
                for (int j = i + 1; j < curated.Length; j++)
                {
                    if (skeletons[j].Length < 5 || !OneLetterApart(skeletons[i], skeletons[j])) continue;
                    var two = new[] { curated[i], curated[j] }.OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    if (!allowed.Contains(two[0] + "|" + two[1])) near.Add(curated[i] + " / " + curated[j]);
                }
            }
            Assert.Empty(near);
            // The review's list: a Thai obscenity, blocked words inside one word, innuendo, politics, people better known
            // for a misconduct case, and one joke written twice.
            foreach (string gone in new[] { "Kuy", "Kusoge", "Sextou", "Shiitake Happens", "Gherkin Jerkin", "Nagibator228", "Down Bad 0-7",
                                            "Krispy Kremlin", "Elon Musgo", "Ich Bin Ein Bot", "Neuland", "DrDisrespect", "Dr Disrespawn",
                                            "The Two Time", "Snipe Tyson", "Conor McGrenade", "xQc", "Clix", "Ali-A", "SurprisedPikachu",
                                            "ZoroIsLost", "UnoReverseCard", "RGBForFPS", "RGB Gives FPS", "Press F to Pay", "F to Pay Respect",
                                            "F in Chat", "Arrow to Knee", "CakeWasALie", "Half Life 3", "HalfLife3Soon", "I Am Inevitable",
                                            "NarutoRunner", "HighNoon", "Nanomachines", "Is Mayo An Inst", "No This Is", "Peace Was Never",
                                            "Hey Vsauce", "Ight Imma Head", "CJ Follow Train", "skrt", "skskskk" })
                Assert.DoesNotContain(gone, curated);
        }

        /// <summary>True when <paramref name="a"/> and <paramref name="b"/> differ by one letter changed, added or left out.</summary>
        private static bool OneLetterApart(string a, string b)
        {
            if (a == b || Math.Abs(a.Length - b.Length) > 1) return false;
            if (a.Length > b.Length) { string t = a; a = b; b = t; }
            int i = 0;
            while (i < a.Length && a[i] == b[i]) i++;
            return a.Length == b.Length
                ? string.CompareOrdinal(a, i + 1, b, i + 1, a.Length) == 0
                : string.CompareOrdinal(a, i, b, i + 1, b.Length) == 0;
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
            // Build 29: spaces, signs and accents are left out too.
            Assert.Equal(GamerTags.Skeleton("Pika Pew"), GamerTags.Skeleton("PikaPew"));
            Assert.Equal(GamerTags.Skeleton("Mr. Saturn"), GamerTags.Skeleton("mr_saturn"));
            Assert.Equal("cremebrulee", GamerTags.Skeleton("Crème Brûlée"));
            Assert.Equal(GamerTags.Skeleton("[TACO] Tuesday"), GamerTags.Skeleton("Taco Tuesday"));
            Assert.Equal(GamerTags.Skeleton("Ñoño"), GamerTags.Skeleton("nono"));
            Assert.NotEqual(GamerTags.Skeleton("Pika Pew"), GamerTags.Skeleton("Pika Pow"));
            // A real player's name in another script keeps its letters: two such players are still told apart.
            Assert.Equal("дима", GamerTags.Skeleton("Дима_99"));
            Assert.NotEqual(GamerTags.Skeleton("Дима"), GamerTags.Skeleton("Саша"));
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
            // What the client cannot take or show: markup (FormatName), ':' (the bot's credential dev:<name>), braces (the
            // localization formatter), a CRM cell operator first, a number, no letter, a space at an end, the length.
            foreach (string bad in new[] { "1337", "-42", "+7", "*3", "1e5", "NaN", "nan", "Infinity", " 12.5 ", "A<b>c", "Fox&Co", "dev:Fox",
                                           "Fox:Co", "{0}", "{x_1}Fox", "Fox/Co", "Fox\\Co", "Say \"hi\"", "-Fox", "+Fox", "*Fox", " Fox", "Fox ",
                                           "Fox  Co", "x", "ThisNameIsWayTooLong", "Fox\tCo", "Fox\u00A0Co", "Fox\u00D7", "Fox\u0100", "Fox\u4E00", "12" })
                Assert.NotNull(GamerTags.ShapeProblem(bad));
            // What players type: spaces, apostrophes, brackets and the other signs, Latin-1 letters, 2 characters.
            foreach (string good in new[] { "SilentFox", "silent_fox", "Raven1998", "xXRavenXx", "agent-123456789", "Kn1ght_FR", "fox.ace", "ab",
                                            "Fox Co", "Zoë", "_Fox", "Fox_", "Fo__x", "It'sa Me, Ammo", "[LAG] Pingu", "Snake? Snaaake!", "Q*bert",
                                            "Top% Rattata", "dont @ me", "Black^", "~SPICY~", "(DAD)PaidForIt", "|404|Aim", "o7", "Ñu",
                                            "...Whatever", "Miracle-", "Alt+F4" })
                Assert.Null(GamerTags.ShapeProblem(good));
            // Generated names keep the build-28 shape on top.
            foreach (string bad in new[] { "Fox Co", "Zoë", "_Fox", "Fox_", "Fo__x", "Fo.-x", "o7", "Q*bert" })
                Assert.NotNull(GamerTags.GeneratedShapeProblem(bad));
            foreach (string good in new[] { "SilentFox", "silent_fox", "xXRavenXx", "agent-123456789", "fox.ace" })
                Assert.Null(GamerTags.GeneratedShapeProblem(good));
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
            Assert.NotNull(tags.BlockedReason(new[] { "Iron", "Cross" }));   // a hate symbol two harmless words make
            Assert.NotNull(tags.BlockedReason(new[] { "Red", "Rocket" }));   // slang
            // Build 29, the owner's rule: brands, streamers and other games' characters are allowed.
            foreach (string[] pieces in new[]
                     {
                         new[] { "Ninja", "07" }, new[] { "Sniper", "Wolf" }, new[] { "Iron", "Wolf" }, new[] { "Blue", "Falcon" },
                         new[] { "Echo", "Fox" }, new[] { "Spartan", "117" }, new[] { "Blue", "Yeti" }, new[] { "Hollow", "Knight" },
                         new[] { "Soldier", "76" }, new[] { "Cpt", "Falcon" }, new[] { "Batman" }, new[] { "Shroud" },
                     })
                Assert.Null(tags.BlockedReason(pieces));
            Assert.Null(tags.BlockedReason(new[] { "Silent", "Fox" }));
            Assert.Null(tags.BlockedReason(new[] { "Ranger", "1448" }));
            Assert.Null(tags.BlockedReason(new[] { "Glass", "Cannon" }));    // 'ass' inside one harmless part
        }

        [Fact]
        public void TheCuratedBlocklistStillRefusesSlursHateSexAndTragediesButNotBrandsOrStreamers()
        {
            GamerTags tags = GamerTags.BuiltIn;
            // Slurs, profanity, sex, hate, politics, drugs and tragedies, also in leet, with accents, in a clan tag or a
            // camel-case word.
            foreach (string bad in new[] { "Ur A Retard", "f4gg0t lol", "N1GG4 PLS", "Sh1t Happens", "xX_Nazi_Xx", "[KKK] Clan", "Big D1ck Energy",
                                           "Porn Star", "Sexy Sniper", "Heil Me", "HitlerDidNothin", "Trump 2024", "Opium Den", "Weed Lord",
                                           "Suicide Squad", "KYS noob", "Jesus Saves", "Fox 88", "Ranger1488", "ISIS Fan", "Ass Hat",
                                           "WhitePower", "Kill Your Self", "Iron Cross", "Nazì", "Gay Frog", "Sex", "Fuçk",
                                           "Kuy", "KUY Gang", "Khuy" })
                Assert.True(tags.CuratedBlockedReason(bad) != null, bad + " passed the blocklist");
            // The owner's rule: brands, other games' characters, streamers; and words that only hold a blocked word across
            // a space or a case change.
            foreach (string good in new[] { "Shroud's Mouse", "Ninja", "Pokimane", "xQcOW", "Master Chief", "Doritos Dew", "PewDiePie",
                                            "Sodapoppin", "Pika Pew", "Spooky Scary", "Obi Wan Kenoodle", "WaitItsAllOhio", "Copium",
                                            "Knife Crisis", "Glass Cannon", "Prophet Margin", "Cobra 11", "Agent 47" })
                Assert.True(tags.CuratedBlockedReason(good) == null, good + ": " + tags.CuratedBlockedReason(good));
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

            // A valid override without a curated list, with one style and a few words: every name comes from it (5 x 6 word
            // pairs: names on a board also differ without their numbers).
            var doc = MiniJson.ParseObject(File.ReadAllText(Path.Combine(ContentTestSupport.RepoRoot, "server", "Content", "names.json")));
            Assert.True(doc.Remove("curated"));
            var parts = (Dictionary<string, object>)doc["parts"];
            parts["adj"] = new List<object> { "Plaid", "Suede", "Velvet", "Denim", "Linen" };
            parts["noun"] = new List<object> { "Walrus", "Narwhal", "Puffin", "Lemur", "Gecko", "Heron" };
            doc["styles"] = new List<object> { MiniJson.ParseObject("{\"id\":\"only\",\"pattern\":\"{adj}{noun}{d2}\",\"weight\":1}") };
            File.WriteAllText(Path.Combine(contentDir, GamerTags.FileName), MiniJson.Serialize(doc));
            GamerTags custom = GamerTags.For(provider, logger.For("test"));
            Assert.Equal(GamerTags.FileName, custom.Source);
            var rivals = VirtualPlayers.Generate("desc/custom", 20, 400, custom, null);
            Assert.All(rivals, r => Assert.Matches("^(Plaid|Suede|Velvet|Denim|Linen)(Walrus|Narwhal|Puffin|Lemur|Gecko|Heron)[0-9]{2}$", (string)r["display_name"]));

            // With three curated names: they go first, the generator names the rest.
            doc["curated"] = new List<object> { "Señor Walrus", "Lil' Puffin", "[GG] Heron" };
            File.WriteAllText(Path.Combine(contentDir, GamerTags.FileName), MiniJson.Serialize(doc));
            GamerTags three = GamerTags.For(provider, logger.For("test"));
            Assert.Equal(3, three.Curated.Length);
            var named = VirtualPlayers.Generate("desc/custom", 20, 400, three, null).Select(r => (string)r["display_name"]).ToList();
            Assert.Equal(new[] { "Señor Walrus", "Lil' Puffin", "[GG] Heron" }.OrderBy(x => x), named.Take(3).OrderBy(x => x));
            Assert.All(named.Skip(3), n => Assert.Matches("^(Plaid|Suede|Velvet|Denim|Linen)(Walrus|Narwhal|Puffin|Lemur|Gecko|Heron)[0-9]{2}$", n));

            // A curated name the client cannot take, one the blocklist refuses, or two that read alike: refused.
            foreach (string[] bad in new[] { new[] { "dev:Walrus" }, new[] { "{0} Walrus" }, new[] { "Nazi Walrus" }, new[] { "Pika Pew", "pikapew" } })
            {
                doc["curated"] = bad.Cast<object>().ToList();
                File.WriteAllText(Path.Combine(contentDir, GamerTags.FileName), MiniJson.Serialize(doc));
                Assert.Same(GamerTags.BuiltIn, GamerTags.For(provider, logger.For("test")));
            }
            doc.Remove("curated");

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
