using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Names;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Tests.Conformance;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// The in-match bot names (setting crm.botNames, <see cref="CrmBotNames"/>): rename cells for the AI table BotNames in
    /// the game object the GS fetches and installs before each match (configs/users/me/game), a new set of gamer tags every
    /// <see cref="CrmBotNames.WindowMinutes"/> minutes that keeps a share of the stock names of the player's own data pack.
    /// The player's own document carries none and does not change with the window.
    /// </summary>
    public class CrmBotNamesTests
    {
        private const string Mirror = "game._ServerConfig.default._OverrideTables";
        private static readonly DateTime Day1 = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(CrmBotNames.WindowMinutes);

        private readonly ITestOutputHelper output;

        public CrmBotNamesTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static readonly string[] Stock = { "Rocky", "Bruno", "Ace", "Viper", "J.D.", "Dr Smith", "Bruno", "rocky", "Kim&Co", "Zoë" };

        private static string AiDatabase(IEnumerable<object> names)
        {
            var rows = names.Select(n => (object)new List<object> { n }).ToList();
            var doc = new Dictionary<string, object>
            {
                ["BotNames"] = new Dictionary<string, object> { ["Fields"] = new List<object> { "Name" }, ["Rows"] = rows },
                ["Match1"] = new Dictionary<string, object> { ["Fields"] = new List<object> { "Name" }, ["Rows"] = new List<object>() },
            };
            return MiniJson.Serialize(doc);
        }

        /// <summary>A data pack (Android layout) with only the AI database.</summary>
        private static string FakePack(string dir, IEnumerable<object> names)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "MCVS.game"), "fake");
            string hashed = Path.Combine(dir, DataPack.HashedPath(CrmBotNames.LogicalPath).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(hashed));
            File.WriteAllText(hashed, AiDatabase(names), new UTF8Encoding(false));
            return dir;
        }

        private static Dictionary<string, object> Cells(Dictionary<string, object> tables)
        {
            return JsonValue.AsObject(JsonPath.Get(tables, "MCVS_AI.BotNames.Name@Name"));
        }

        /// <summary>200 made-up stock names (no number, no skeleton twice).</summary>
        private static readonly string[] ManyStock = Enumerable.Range(0, 200)
            .Select(i => "Stock" + (char)('a' + i / 26 % 26) + (char)('a' + i % 26) + (char)('a' + i / 676)).ToArray();

        [Fact]
        public void StockNamesAreTheNameColumnAsStringsOnce()
        {
            List<string> names = CrmBotNames.ParseStockNames(AiDatabase(new object[] { "Rocky", 1337L, "Rocky", "", null, "Ace" }), out HashSet<string> repeated);
            Assert.Equal(new[] { "Rocky", "Ace" }, names);
            Assert.Equal(new[] { "Rocky" }, repeated);
            Assert.Null(CrmBotNames.ParseStockNames("{\"Other\":{}}"));
            Assert.Null(CrmBotNames.ParseStockNames("not json"));
        }

        [Fact]
        public void EachWindowRenamesMostStockNamesWithDistinctGamerTagsThatAreNoStockName()
        {
            GamerTags tags = GamerTags.BuiltIn;
            var repeated = new HashSet<string> { "Bruno" };
            var renames = CrmBotNames.Renames(Stock, repeated, tags, Day1);
            var distinctStock = Stock.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            // Ordinal order, a subset of the stock names; a name the table has twice is always renamed.
            Assert.Equal(renames.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal), renames.Select(r => r.Key));
            Assert.All(renames, r => Assert.Contains(r.Key, distinctStock));
            Assert.Contains(renames, r => r.Key == "Bruno");
            var ours = renames.Select(r => r.Value).ToList();
            Assert.Equal(ours.Count, ours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(ours.Count, ours.Select(GamerTags.Skeleton).Distinct().Count());
            foreach (string name in ours)
            {
                Assert.DoesNotContain(name, Stock, StringComparer.OrdinalIgnoreCase);
                Assert.DoesNotContain(GamerTags.Skeleton(name), Stock.Select(GamerTags.Skeleton));
                Assert.Null(GamerTags.ShapeProblem(name));
                Assert.Matches("^[A-Za-z0-9_.-]{3,16}$", name);
                // CRMTableCell reads a value double.TryParse accepts as a number: the bot list would read null.
                Assert.False(double.TryParse(name.TrimStart('+', '-', '*'), System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out _), name);
            }
            // The same window gives the same names (both platforms, every request), the next window others.
            Assert.Equal(renames, CrmBotNames.Renames(Stock.Reverse().ToArray(), repeated, tags, Day1.AddMinutes(CrmBotNames.WindowMinutes - 1)));
            var next = CrmBotNames.Renames(Stock, repeated, tags, Day1 + Window);
            Assert.Empty(next.Select(r => r.Value).Intersect(ours, StringComparer.OrdinalIgnoreCase).Skip(1));
        }

        [Fact]
        public void APlayersDayMeetsTheStockNamesAndThousandsOfOursNotTheSame291()
        {
            // Up to build 28's first cut the whole day had one set: as many names as the table has rows, the stock ones gone.
            GamerTags tags = GamerTags.BuiltIn;
            var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keptEver = new HashSet<string>(StringComparer.Ordinal);
            int windows = 12;   // two hours of play
            for (int w = 0; w < windows; w++)
            {
                var renames = CrmBotNames.Renames(ManyStock, null, tags, Day1 + TimeSpan.FromTicks(Window.Ticks * w));
                int kept = ManyStock.Length - renames.Count;
                Assert.InRange(kept, ManyStock.Length * CrmBotNames.KeepStockPercent / 100 - 25, ManyStock.Length * CrmBotNames.KeepStockPercent / 100 + 25);
                var renamed = new HashSet<string>(renames.Select(r => r.Key));
                foreach (string stock in ManyStock) if (!renamed.Contains(stock)) keptEver.Add(stock);
                foreach (var r in renames) ours.Add(r.Value);
            }
            Assert.True(ours.Count >= windows * ManyStock.Length * 6 / 10, ours.Count + " of our names in " + windows + " windows");
            Assert.True(keptEver.Count >= ManyStock.Length * 9 / 10, keptEver.Count + " stock names came up");
        }

        [Fact]
        public void TheRenamesGoIntoTheGameObjectsMirrorOnlyAndAnOverlayCellWins()
        {
            var renames = CrmBotNames.Renames(Stock, new HashSet<string> { "Ace", "Bruno" }, GamerTags.BuiltIn, Day1);
            var options = new CrmOptions
            {
                Overlay = MiniJson.ParseObject("{\"game\":{\"_OverrideTables\":{\"MCVS_AI\":{\"BotNames\":{\"Name@Name\":{\"Ace\":\"OverlayAce\"}}}}}}"),
            };
            var root = CrmDocumentBuilder.Build(null, new CrmContent(), options, Log.Null);
            Assert.Empty(CrmDocumentValidator.Validate(root));
            var doc = new CrmDocument(root, false, null, Day1);
            string plain = doc.Json;
            var game = MiniJson.ParseObject(doc.GameJsonWithBotNames("w1", renames));
            var mirror = Cells(JsonValue.AsObject(JsonPath.Get(game, "_ServerConfig.default._OverrideTables")));
            Assert.Equal(renames.Count, mirror.Count);
            Assert.Equal("OverlayAce", mirror["Ace"]);
            Assert.Equal(renames.First(r => r.Key == "Bruno").Value, mirror["Bruno"]);
            // game._OverrideTables (the TS's per-player databases) gets only what the overlay put there.
            Assert.Equal(new[] { "Ace" }, Cells(JsonValue.AsObject(JsonPath.Get(game, "_OverrideTables"))).Keys);
            // The document itself (the player's configs/users/me) is untouched: only the overlay's cell in its mirror.
            Assert.Equal(plain, MiniJson.Serialize(doc.Root));
            Assert.Equal(new[] { "Ace" }, Cells(JsonValue.AsObject(JsonPath.Get(doc.Root, Mirror))).Keys);
            Assert.True(doc.TryGetSelectorJson("game", out string gameJson));
            Assert.False(Cells(JsonValue.AsObject(JsonPath.Get(MiniJson.ParseObject(gameJson), "_ServerConfig.default._OverrideTables"))).ContainsKey("Bruno"));
        }

        [Fact]
        public void TheServedGameObjectCarriesTheWindowsRenamesAndThePlayersDocumentNeverChanges()
        {
            using var dir = new TempDir();
            string pack = FakePack(dir.Combine("pack"), Stock);
            var clock = new ManualClock(Day1);
            var options = new McvsOffline.Server.ServerOptions
            {
                DataDir = dir.Combine("data"), DataPackDir = pack, Port = 51780, ConsoleLog = false, FileLog = false, Clock = clock,
            };
            using (var server = McvsOffline.Server.McvsServer.Create(options))
            {
                // The GS reads configs/users/me/game as the game object (report 04 §5.1).
                var game = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me/game"));
                var cells = Cells(JsonValue.AsObject(JsonPath.Get(game, "_ServerConfig.default._OverrideTables")));
                Assert.NotNull(cells);
                // Bruno is in the table twice: always renamed. The rest: about three in four.
                Assert.Contains("Bruno", cells.Keys);
                Assert.InRange(cells.Count, 1, Stock.Distinct(StringComparer.Ordinal).Count());
                Assert.Null(JsonPath.Get(game, "_OverrideTables.MCVS_AI"));
                var first = cells.ToDictionary(kv => kv.Key, kv => (string)kv.Value);
                Assert.Equal(CrmBotNames.Renames(Stock, new HashSet<string> { "Bruno" }, GamerTags.BuiltIn, Day1).ToDictionary(r => r.Key, r => r.Value), first);
                // The player's own document carries no bot names and stays the same text across windows: the TS never
                // reloads its per-player databases (CRMComponent.CrmConfig compares the text) because of them.
                string document = ContentTestSupport.Get(server, "/configs/users/me").BodyText;
                Assert.Null(JsonPath.Get(MiniJson.ParseObject(document), Mirror + ".MCVS_AI"));

                clock.Advance(Window);
                var next = Cells(JsonValue.AsObject(JsonPath.Get(
                    ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me/game")), "_ServerConfig.default._OverrideTables")));
                Assert.Empty(next.Values.Cast<string>().Intersect(first.Values, StringComparer.OrdinalIgnoreCase));
                Assert.Equal(document, ContentTestSupport.Get(server, "/configs/users/me").BodyText);

                server.Context.Settings.Override("crm.botNames", false);
                var off = ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me/game"));
                Assert.Null(JsonPath.Get(off, "_ServerConfig.default._OverrideTables.MCVS_AI"));
            }

            var logs = new MemoryLogSink();
            using (var server = ContentTestSupport.CreateServer(dir.Combine("nopack"), null, logs))
            {
                var response = ContentTestSupport.Get(server, "/configs/users/me/game");
                Assert.Equal(200, response.StatusCode);
                Assert.Null(JsonPath.Get(ContentTestSupport.JsonObject(response), "_ServerConfig.default._OverrideTables.MCVS_AI"));
                Assert.True(logs.Contains("the bots keep the game's names"));
            }
        }

        /// <summary>
        /// The player's real data pack and client: the served game object's mirror, as the GS installs it
        /// (CRMComponent.GetChanger on <c>_ServerConfig.default</c>, then Table.ApplyCRMTableChanges on the shipped BotNames
        /// table), leaves every row a string that <c>LoadBotNames</c> reads (<c>array[0] as string</c>), renames about three
        /// stock names in four (always the duplicated ones' first rows), and gives no two rows the same name.
        /// </summary>
        [Fact]
        public void TheRealClientRenamesMostRealBotNamesAndNoRowRepeats()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            string packDir = ContentTestSupport.RealDataPack;
            if (packDir == null)
            {
                output.WriteLine("no raw data pack (MCVS_DATAPACK): skipped");
                return;
            }
            using var dir = new TempDir();
            using var server = ContentTestSupport.CreateServer(dir.Combine("data"), packDir);
            // The GS parses configs/users/me/game as the game object; StandardCrm.Data reads a whole document.
            string documentText = "{\"game\":" + ContentTestSupport.Get(server, "/configs/users/me/game").BodyText + "}";
            var doc = MiniJson.ParseObject(documentText);
            var cells = Cells(JsonValue.AsObject(JsonPath.Get(doc, Mirror)));
            Assert.NotNull(cells);

            object stockTable = real.NewTable(CrmBotNames.LogicalPath, CrmBotNames.Table);
            List<object> stockColumn = Column(stockTable);
            var stockNames = stockColumn.Cast<string>().ToList();
            int distinctStock = stockNames.Distinct(StringComparer.Ordinal).Count();
            Assert.InRange(cells.Count, distinctStock * (100 - CrmBotNames.KeepStockPercent) / 100 - 30, distinctStock * (100 - CrmBotNames.KeepStockPercent) / 100 + 30);
            foreach (string twice in stockNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
                Assert.Contains(twice, cells.Keys);

            object data = Activator.CreateInstance(real.Type("Gameloft.OnlinePlatform", "Gameloft.OnlinePlatform.StandardCrm+Data"), documentText);
            object game = GameClient.Get(data, "RawGameParameters");
            object mirror = Index(Index(game, "_ServerConfig"), "default");
            object changer = real.Changer(mirror, CrmBotNames.LogicalPath);
            object tableChanger = real.TableChangers(changer)[CrmBotNames.Table];
            object table = real.NewTable(CrmBotNames.LogicalPath, CrmBotNames.Table);
            GameClient.Call(table, "ApplyCRMTableChanges", tableChanger);

            List<object> renamed = Column(table);
            Assert.Equal(stockColumn.Count, renamed.Count);
            Assert.All(renamed, n => Assert.IsType<string>(n));
            var ours = new HashSet<string>(cells.Values.Cast<string>(), StringComparer.Ordinal);
            int ourRows = renamed.Count(n => ours.Contains((string)n));
            int duplicates = stockNames.Count - distinctStock;
            Assert.Equal(cells.Count, ourRows);
            // The rows that keep a stock name are the kept ones and the second copies of the duplicated names: each name
            // is once in the list.
            Assert.Equal(renamed.Count, renamed.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count());
            output.WriteLine(stockNames.Count + " rows, " + ourRows + " renamed, " + (stockNames.Count - ourRows) + " stock rows kept ("
                             + duplicates + " second copies); e.g. " + string.Join(", ", renamed.Take(12)));
        }

        private static List<object> Column(object table)
        {
            var rows = (IEnumerable)GameClient.Get(table, "Rows");
            int column = RealTables.FieldIndex(table, CrmBotNames.Field);
            return rows.Cast<object[]>().Select(r => r[column]).ToList();
        }

        private static object Index(object jObject, string name)
        {
            PropertyInfo item = jObject.GetType().GetProperties()
                .Single(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(string));
            return item.GetValue(jObject, new object[] { name }) ?? throw new InvalidOperationException("no " + name);
        }
    }
}
