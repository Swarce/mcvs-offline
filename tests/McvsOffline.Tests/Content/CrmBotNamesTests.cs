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
    /// the CRM mirror the GS installs before each match, a new set of gamer tags each UTC day, from the stock names of the
    /// player's own data pack.
    /// </summary>
    public class CrmBotNamesTests
    {
        private const string Mirror = "game._ServerConfig.default._OverrideTables";
        private static readonly DateTime Day1 = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

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

        [Fact]
        public void StockNamesAreTheNameColumnAsStringsOnce()
        {
            List<string> names = CrmBotNames.ParseStockNames(AiDatabase(new object[] { "Rocky", 1337L, "Rocky", "", null, "Ace" }));
            Assert.Equal(new[] { "Rocky", "Ace" }, names);
            Assert.Null(CrmBotNames.ParseStockNames("{\"Other\":{}}"));
            Assert.Null(CrmBotNames.ParseStockNames("not json"));
        }

        [Fact]
        public void EachStockNameGetsADistinctGamerTagThatIsNoStockNameAndANewSetEachDay()
        {
            GamerTags tags = GamerTags.BuiltIn;
            var renames = CrmBotNames.Renames(Stock, tags, Day1);
            var distinctStock = Stock.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(distinctStock, renames.Select(r => r.Key));
            var ours = renames.Select(r => r.Value).ToList();
            Assert.Equal(ours.Count, ours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (string name in ours)
            {
                Assert.DoesNotContain(name, Stock, StringComparer.OrdinalIgnoreCase);
                Assert.Null(GamerTags.ShapeProblem(name));
                Assert.Matches("^[A-Za-z0-9_.-]{3,16}$", name);
                // CRMTableCell reads a value double.TryParse accepts as a number: the bot list would read null.
                Assert.False(double.TryParse(name.TrimStart('+', '-', '*'), System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out _), name);
            }
            // The same day gives the same names (both platforms, every request), the next day others.
            Assert.Equal(renames, CrmBotNames.Renames(Stock.Reverse().ToArray(), tags, Day1.AddHours(13)));
            var next = CrmBotNames.Renames(Stock, tags, Day1.AddDays(1));
            Assert.True(next.Zip(renames, (a, b) => a.Value != b.Value).Count(d => d) >= renames.Count - 1);
        }

        [Fact]
        public void TheRenamesGoIntoTheMirrorOnlyAndAnOverlayCellWins()
        {
            var content = new CrmContent { BotNames = CrmBotNames.Renames(Stock, GamerTags.BuiltIn, Day1) };
            var options = new CrmOptions
            {
                Overlay = MiniJson.ParseObject("{\"game\":{\"_OverrideTables\":{\"MCVS_AI\":{\"BotNames\":{\"Name@Name\":{\"Ace\":\"OverlayAce\"}}}}}}"),
            };
            var doc = CrmDocumentBuilder.Build(null, content, options, Log.Null);
            Assert.Empty(CrmDocumentValidator.Validate(doc));
            var mirror = Cells(JsonValue.AsObject(JsonPath.Get(doc, Mirror)));
            Assert.Equal(content.BotNames.Count, mirror.Count);
            Assert.Equal("OverlayAce", mirror["Ace"]);
            Assert.Equal(content.BotNames.First(r => r.Key == "Bruno").Value, mirror["Bruno"]);
            // game._OverrideTables (the TS's per-player databases) gets only what the overlay put there.
            Assert.Equal(new[] { "Ace" }, Cells(JsonValue.AsObject(JsonPath.Get(doc, "game._OverrideTables"))).Keys);

            var off = CrmDocumentBuilder.Build(null, new CrmContent(), new CrmOptions(), Log.Null);
            Assert.Null(Cells(JsonValue.AsObject(JsonPath.Get(off, Mirror))));
        }

        [Fact]
        public void TheServedGameObjectCarriesTodaysRenamesUnlessSwitchedOffOrWithoutADataPack()
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
                Assert.Equal(Stock.Distinct(StringComparer.Ordinal).Count(), cells.Count);
                Assert.Null(JsonPath.Get(game, "_OverrideTables.MCVS_AI"));
                var first = cells.ToDictionary(kv => kv.Key, kv => (string)kv.Value);

                clock.Advance(TimeSpan.FromDays(1));
                var tomorrow = Cells(JsonValue.AsObject(JsonPath.Get(
                    ContentTestSupport.JsonObject(ContentTestSupport.Get(server, "/configs/users/me/game")), "_ServerConfig.default._OverrideTables")));
                Assert.Equal(first.Keys, tomorrow.Keys);
                Assert.NotEqual(first.Values, tomorrow.Values.Cast<string>());

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
        /// table), leaves every row a string that <c>LoadBotNames</c> reads (<c>array[0] as string</c>), renames every stock
        /// name but the second copy of the two duplicated ones, and gives no two rows the same name.
        /// </summary>
        [Fact]
        public void TheRealClientRenamesEveryRealBotName()
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
            string documentText = ContentTestSupport.Get(server, "/configs/users/me").BodyText;
            var doc = MiniJson.ParseObject(documentText);
            var cells = Cells(JsonValue.AsObject(JsonPath.Get(doc, Mirror)));
            Assert.NotNull(cells);

            object stockTable = real.NewTable(CrmBotNames.LogicalPath, CrmBotNames.Table);
            List<object> stockColumn = Column(stockTable);
            var stockNames = stockColumn.Cast<string>().ToList();
            Assert.Equal(stockNames.Distinct(StringComparer.Ordinal).Count(), cells.Count);

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
            int duplicates = stockNames.Count - stockNames.Distinct(StringComparer.Ordinal).Count();
            Assert.Equal(cells.Count, ourRows);
            Assert.Equal(stockNames.Count - duplicates, ourRows);
            // The rows that keep a stock name are the second copies of the duplicated names: each name is once in the list.
            Assert.Equal(renamed.Count, renamed.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count());
            output.WriteLine(stockNames.Count + " rows, " + ourRows + " renamed, " + duplicates + " duplicated stock rows kept; e.g. "
                             + string.Join(", ", renamed.Take(12)));
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
