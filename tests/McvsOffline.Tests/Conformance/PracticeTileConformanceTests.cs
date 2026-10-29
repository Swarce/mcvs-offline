using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Tests.Content;
using McvsOffline.Tests.Golden;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Conformance
{
    /// <summary>
    /// <c>crm.practiceTile</c> against the REAL client code: the served CRM documents (static, per player, and the GS mirror
    /// <c>_ServerConfig.default</c>) parsed by the game's <c>StandardCrm.Data</c>, turned into changers by
    /// <c>CRMComponent.GetChanger</c>, applied by <c>Database.Table.ApplyCRMTableChanges</c> to the real MCVS_Meta
    /// <c>MainMenuElements</c> (<see cref="RealTables"/>) and read back by the game's own typed table
    /// (<c>Game.MenuElementsCollection</c>, the <c>MenuElement</c> rows <c>MainMenuComponent.GetMenuElements</c> lists).
    /// The menu's own filter and order are those of GM/MainMenuComponent.cs: <c>OrderBy(OrderId)</c>, then only rows whose
    /// <c>Displayed</c> is true (<c>IsMenuItemDisplayed</c>; none of these rows is BUNDLES). The Flash main menu has six
    /// slots, <c>mc_promo0..5</c> (MenuMainV2.updateMenu fills slot i with items[i]). Skipped without MCVS_REFS and the
    /// data pack, mandatory with MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class PracticeTileConformanceTests
    {
        private static readonly DateTime Day = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>The five tiles the shipped data shows, in menu order (OrderId 1-5).</summary>
        private static readonly string[] ShippedTiles = { "QUICKPLAY", "CONTRACTS", "MONTLYCARD", "TLE", "OFFERS" };

        private const int FlashSlots = 6;

        private readonly ITestOutputHelper output;

        public PracticeTileConformanceTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Off (the default): the menu the game builds from the served trees is the shipped five tiles and SOLO stays hidden.
        /// On: SOLO ("PRACTICE", LINK_SOLO, no league lock) is shown as the sixth and last tile, in the last Flash slot, and
        /// the five shipped tiles keep their slots; the applied cells are what the game parsed (SET "true", SET 6.0).
        /// </summary>
        [Fact]
        public void TheGameShowsThePracticeTileLastOnlyWhenTheSettingIsOn()
        {
            RealTables real = RealTables.TryGet(output);
            if (real == null) return;
            Assert.True(real.HasTable(CrmDocumentBuilder.PracticeTileTable));

            // The shipped table as the game reads it, without any changer.
            List<object> shipped = Elements(real, real.NewTable(CrmDocumentBuilder.PracticeTileTable));
            object solo = shipped.Single(e => (string)GameClient.Get(e, "Id") == CrmDocumentBuilder.PracticeTileRow);
            Assert.Equal("SOLO", (string)GameClient.Get(solo, "Type"));
            Assert.Equal("LINK_SOLO", (string)GameClient.Get(solo, "Action"));
            Assert.Equal("menu.BotBattleTxt", (string)GameClient.Get(solo, "LocId"));
            Assert.False((bool)GameClient.Get(solo, "Displayed"));
            Assert.Equal(0, (int)GameClient.Get(solo, "OrderId"));
            Assert.Equal(ShippedTiles, Menu(shipped));

            foreach (bool on in new[] { false, true })
            {
                using (var run = new GoldenRun(Day))
                {
                    if (on) run.Set("crm.practiceTile", true);
                    string player = run.NewPlayer("tile");
                    var trees = new List<(string Name, object Params)>();
                    foreach (var served in new[] { ("static", (string)null), ("per-player", player) })
                    {
                        HttpOk(run.Get("/configs/users/me", served.Item2), out string text);
                        object game = GameOf(real, text);
                        trees.Add((served.Item1, game));
                        trees.Add((served.Item1 + " mirror", MirrorOf(game)));
                    }
                    foreach (var tree in trees)
                    {
                        string name = (on ? "on " : "off ") + tree.Name;
                        Dictionary<string, object> changers = real.TableChangers(real.Changer(tree.Params));
                        object tableChanger;
                        bool has = changers.TryGetValue(CrmDocumentBuilder.PracticeTileTable, out tableChanger);
                        Assert.True(has == on, name + ": MainMenuElements changer " + (has ? "present" : "absent"));
                        object table = has ? real.Applied(tableChanger) : real.NewTable(CrmDocumentBuilder.PracticeTileTable);
                        List<object> elements = Elements(real, table);
                        string[] menu = Menu(elements);
                        output.WriteLine(name + ": " + string.Join(", ", menu));
                        Assert.True(menu.Length <= FlashSlots, name + ": " + menu.Length + " tiles for " + FlashSlots + " slots");
                        if (!on)
                        {
                            Assert.Equal(ShippedTiles, menu);
                            continue;
                        }
                        Assert.Equal(ShippedTiles.Concat(new[] { "SOLO" }).ToArray(), menu);
                        var cells = Cells(tableChanger);
                        Assert.Equal(new[] { "Id@Displayed", "Id@OrderId" }, cells.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
                        Assert.Equal(((object)"true", "SET"), cells["Id@Displayed"][CrmDocumentBuilder.PracticeTileRow]);
                        Assert.Equal(((object)6.0, "SET"), cells["Id@OrderId"][CrmDocumentBuilder.PracticeTileRow]);
                        object shown = elements.Single(e => (string)GameClient.Get(e, "Id") == CrmDocumentBuilder.PracticeTileRow);
                        Assert.True((bool)GameClient.Get(shown, "Displayed"));
                        Assert.Equal((int)CrmDocumentBuilder.PracticeTileOrderId, (int)GameClient.Get(shown, "OrderId"));
                        Assert.Equal("LINK_SOLO", (string)GameClient.Get(shown, "Action"));
                        Assert.Equal("menu.BotBattleTxt", (string)GameClient.Get(shown, "LocId"));
                        Assert.Equal(0, (int)GameClient.Get(shown, "UnlockLeague"));   // MenuElement.IsLocked: never league-locked
                        Assert.True(real.Exists((string)GameClient.Get(shown, "Image")), name + ": no image " + GameClient.Get(shown, "Image"));
                    }
                }
            }
        }

        /// <summary>
        /// The client half on each client (Android and the Steam build): the served document parses through that client's
        /// <c>StandardCrm.Data</c> and <c>CRMComponent.GetChanger</c> into the two SET cells, in both trees, and off gives no
        /// MainMenuElements changer. With the Steam install's data pack present, its MainMenuElements is the Android one
        /// (row 13 hidden SOLO/LINK_SOLO, the same five shown tiles and OrderIds), so the Android apply above holds there.
        /// </summary>
        [Theory]
        [MemberData(nameof(TestFolders.RealClientFolders), MemberType = typeof(TestFolders))]
        public void EachClientParsesTheTwoCells(string platform, string folder)
        {
            GameClient game = GameClient.TryGetFrom(output, platform, folder, "Gameloft.Engine", "Gameloft.Util");
            if (game == null) return;
            Type crmData = game.Op("StandardCrm+Data");
            MethodInfo getChanger = game.Type("Gameloft.Engine", "Gameloft.Engine.CRMComponent").GetMethod("GetChanger", BindingFlags.Public | BindingFlags.Static);
            foreach (bool on in new[] { false, true })
            {
                using (var run = new GoldenRun(Day))
                {
                    if (on) run.Set("crm.practiceTile", true);
                    HttpOk(run.Get("/configs/users/me", null), out string text);
                    object gameParams = GameClient.Get(Activator.CreateInstance(crmData, text), "RawGameParameters");
                    Assert.NotNull(gameParams);
                    foreach (object tree in new[] { gameParams, MirrorOf(gameParams) })
                    {
                        object changer = getChanger.Invoke(null, new[] { tree, RealTables.MetaPath });
                        object tableChanger = ((IEnumerable)GameClient.Get(changer, "TableChangers")).Cast<object>()
                            .SingleOrDefault(c => (string)GameClient.Get(c, "TableName") == CrmDocumentBuilder.PracticeTileTable);
                        Assert.True((tableChanger != null) == on, platform + ": MainMenuElements changer with the setting " + (on ? "on" : "off"));
                        if (!on) continue;
                        var cells = Cells(tableChanger);
                        Assert.Equal(2, cells.Count);
                        Assert.Equal(((object)"true", "SET"), cells["Id@Displayed"][CrmDocumentBuilder.PracticeTileRow]);
                        Assert.Equal(((object)6.0, "SET"), cells["Id@OrderId"][CrmDocumentBuilder.PracticeTileRow]);
                    }
                }
            }

            if (platform != TestFolders.Pc) return;
            var pack = new DataPack(folder);
            if (!pack.IsValid || !pack.TryReadText(RealTables.MetaPath, out string pcMeta))
            {
                Assert.True(!TestFolders.RequirePc, "MCVS_REQUIRE_PC=1 but " + folder + " has no readable " + RealTables.MetaPath);
                output.WriteLine("no PC " + RealTables.MetaPath + ": the data comparison is skipped");
                return;
            }
            string androidMeta = AndroidMeta();
            if (androidMeta == null)
            {
                Assert.False(GameClient.Required, "MCVS_REQUIRE_REAL_CLIENT=1 but no Android data pack for " + RealTables.MetaPath);
                return;
            }
            string pcRows = MiniJson.Serialize(JsonPath.Get(MiniJson.ParseObject(pcMeta), "MainMenuElements"));
            string androidRows = MiniJson.Serialize(JsonPath.Get(MiniJson.ParseObject(androidMeta), "MainMenuElements"));
            Assert.Equal(androidRows, pcRows);
            output.WriteLine("PC MainMenuElements = Android (" + pcRows.Length + " chars)");
        }

        // ------------------------------------------------------------------------------------------ helpers

        /// <summary>The <c>MenuElement</c>s of a <c>Game.MenuElementsCollection</c> filled from <paramref name="table"/>.</summary>
        private static List<object> Elements(RealTables real, object table)
        {
            object collection = real.Fill("Game", "Game.MenuElementsCollection", table);
            return ((IEnumerable)collection).Cast<object>().ToList();
        }

        /// <summary>What GetMenuElements lists, by Type: stable OrderBy(OrderId), then Displayed only.</summary>
        private static string[] Menu(List<object> elements)
        {
            return elements.OrderBy(e => (int)GameClient.Get(e, "OrderId"))
                           .Where(e => (bool)GameClient.Get(e, "Displayed"))
                           .Select(e => (string)GameClient.Get(e, "Type"))
                           .ToArray();
        }

        /// <summary>The parsed changer's cells: "Index@Field" → key → (CellNewValue, Operator).</summary>
        private static Dictionary<string, Dictionary<string, (object, string)>> Cells(object tableChanger)
        {
            var result = new Dictionary<string, Dictionary<string, (object, string)>>(StringComparer.Ordinal);
            foreach (object fieldChanger in (IEnumerable)GameClient.Get(tableChanger, "FieldChangers"))
            {
                var cells = new Dictionary<string, (object, string)>(StringComparer.Ordinal);
                foreach (object cell in (IEnumerable)GameClient.Get(fieldChanger, "TableCells"))
                    cells[(string)GameClient.Get(cell, "CellRefValue")] = (GameClient.Get(cell, "CellNewValue"), GameClient.Get(cell, "Operator").ToString());
                result[(string)GameClient.Get(fieldChanger, "IndexField") + "@" + (string)GameClient.Get(fieldChanger, "FieldToChange")] = cells;
            }
            return result;
        }

        private static void HttpOk(McvsOffline.Server.Core.Http.HttpResponse response, out string body)
        {
            Assert.Equal(200, response.StatusCode);
            body = response.BodyText;
        }

        private static object GameOf(RealTables real, string documentText)
        {
            object data = Activator.CreateInstance(real.Type("Gameloft.OnlinePlatform", "Gameloft.OnlinePlatform.StandardCrm+Data"), documentText);
            return GameClient.Get(data, "RawGameParameters") ?? throw new InvalidOperationException("the document has no game parameters");
        }

        /// <summary><c>game._ServerConfig.default</c>: the parameters server Worlds install (its <c>_OverrideTables</c> is the mirror).</summary>
        private static object MirrorOf(object gameParams)
        {
            object serverConfig = Index(gameParams, "_ServerConfig") ?? throw new InvalidOperationException("no _ServerConfig");
            return Index(serverConfig, "default") ?? throw new InvalidOperationException("no _ServerConfig.default");
        }

        private static object Index(object jObject, string name)
        {
            PropertyInfo item = jObject.GetType().GetProperties()
                                       .First(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetIndexParameters()[0].ParameterType == typeof(string));
            return item.GetValue(jObject, new object[] { name });
        }

        private static string AndroidMeta()
        {
            string pack = ContentTestSupport.RealDataPack;
            if (pack != null)
            {
                var dataPack = new DataPack(pack);
                return dataPack.TryReadText(RealTables.MetaPath, out string text) ? text : null;
            }
            string decoded = ContentTestSupport.DecodedDataPack;
            string path = decoded == null ? null : Path.Combine(decoded, RealTables.MetaPath);
            return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
