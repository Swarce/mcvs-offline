using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// <see cref="GameDataHooks"/> (build 30, patches 'crmtables' and 'jsonnorm') on look-alikes of the game's types (same public
    /// property names as Gameloft.Engine's CRMDatabaseChanger, CRMTableChanger, CRMTableFieldChanger, CRMTableCell and Database).
    /// RealHitchPatchTests runs the same code against the real game assemblies.
    /// </summary>
    [Collection("GameHooks")]
    public class GameDataHooksTests
    {
        public enum CellValueOperator { SET, ADD, MUL }

        public sealed class Cell
        {
            public Cell(string key, object value, CellValueOperator op = CellValueOperator.SET)
            {
                CellRefValue = key;
                CellNewValue = value;
                Operator = op;
            }

            public object CellNewValue { get; private set; }
            public string CellRefValue { get; private set; }
            public CellValueOperator Operator { get; private set; }
        }

        public sealed class Field
        {
            public string FieldToChange { get; set; }
            public string IndexField { get; set; }
            public List<Cell> TableCells { get; set; } = new List<Cell>();
        }

        public sealed class TableChanger
        {
            public string TableName { get; set; }
            public List<Field> FieldChangers { get; set; } = new List<Field>();
        }

        public sealed class Changer
        {
            public List<TableChanger> TableChangers { get; private set; } = new List<TableChanger>();
        }

        public sealed class Database
        {
            public string File { get; set; } = "Design/MCVS_Meta.xlsx";
        }

        /// <summary>Two tables: Weapons (Id@Damage: +5, *2, 12; Id@Name) and Bots (Name@Name).</summary>
        private static Changer Make(double rifle = 5, string name = "Rifle X", CellValueOperator op = CellValueOperator.ADD, bool swap = false)
        {
            var changer = new Changer();
            var weapons = new TableChanger { TableName = "Weapons" };
            var damage = new Field { FieldToChange = "Damage", IndexField = "Id" };
            var cells = new List<Cell> { new Cell("rifle", rifle, op), new Cell("pistol", 2.0, CellValueOperator.MUL), new Cell("smg", 12.0) };
            if (swap) cells.Reverse();
            damage.TableCells.AddRange(cells);
            var names = new Field { FieldToChange = "Name", IndexField = "Id" };
            names.TableCells.Add(new Cell("rifle", name));
            weapons.FieldChangers.Add(damage);
            weapons.FieldChangers.Add(names);
            var bots = new TableChanger { TableName = "Bots" };
            var botNames = new Field { FieldToChange = "Name", IndexField = "Name" };
            botNames.TableCells.Add(new Cell("Bruno", "Flank Sinatra"));
            bots.FieldChangers.Add(botNames);
            changer.TableChangers.Add(weapons);
            changer.TableChangers.Add(bots);
            return changer;
        }

        [Fact]
        public void OnlyTheSameCellsInTheSameOrderAreTheSame()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                var db = new Database();
                Assert.True(GameDataHooks.SameCrmChanges(db, Make(), Make()));
                Assert.Contains("crm tables: Design/MCVS_Meta.xlsx got the same CRM cells again (2 tables, 5 cells): kept, no reload from disk (compared in ",
                                lines.Single());
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), Make(rifle: 6)));                  // another value
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), Make(op: CellValueOperator.SET)));  // another operator, same value
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), Make(name: "Rifle Y")));
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), Make(swap: true)));                 // order matters (later cells win)
                Changer more = Make();
                more.TableChangers.Add(new TableChanger { TableName = "Extra" });
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), more));
                Changer numberAsText = Make();
                numberAsText.TableChangers[0].FieldChangers[0].TableCells[2] = new Cell("smg", "12");   // "12" is not 12.0
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), numberAsText));
                Assert.Contains(lines, l => l.Contains("got other CRM cells (Weapons rifle): the game reloads it"));
                Assert.Contains(lines, l => l.Contains("got other CRM cells (tables 2 -> 3)"));

                // The cases the game's own setter decides: no reload, the in-place first apply, or no file.
                Changer c = Make();
                Assert.False(GameDataHooks.SameCrmChanges(db, c, c));
                Assert.False(GameDataHooks.SameCrmChanges(db, null, Make()));
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), null));
                Assert.False(GameDataHooks.SameCrmChanges(db, new Changer(), new Changer()));
                Assert.False(GameDataHooks.SameCrmChanges(db, Make(), new Changer()));
                Assert.False(GameDataHooks.SameCrmChanges(new Database { File = "" }, Make(), Make()));
                Assert.False(GameDataHooks.SameCrmChanges(new object(), Make(), Make()));      // no File property: a failure is "no"
                Assert.False(GameDataHooks.SameCrmChanges(db, new object(), new object()));   // nor TableChangers

                Assert.Equal(1, GameDataHooks.CrmReloadsSkipped);
                Assert.Equal(6, GameDataHooks.CrmReloadsKept);
                Assert.StartsWith("data hooks: crm tables 1 reloads skipped (same cells), 6 reloaded (other cells), compare total ", GameDataHooks.Describe());
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }

        [Fact]
        public void TheCrmLinesStopAtTheLimitAndTheCountersGoOn()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                for (int i = 0; i < GameDataHooks.CrmLineLimit + 10; i++) Assert.True(GameDataHooks.SameCrmChanges(new Database(), Make(), Make()));
                Assert.Equal(GameDataHooks.CrmLineLimit, lines.Count);
                Assert.Equal(GameDataHooks.CrmLineLimit + 10, GameDataHooks.CrmReloadsSkipped);
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }

        private static string Big(char c, int extra = 0)
        {
            return "{\"k\":\"" + new string(c, GameDataHooks.NormalizeLogMinChars + extra) + "\"}";
        }

        [Fact]
        public void LargeResultsAreKeptForTheSameCharactersOnly()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                string a = Big('a'), normA = "normalized a";
                Assert.Null(GameDataHooks.CachedNormalizedJson(a));
                Assert.Same(normA, GameDataHooks.RememberNormalizedJson(normA, a));
                Assert.Same(normA, GameDataHooks.CachedNormalizedJson(a));
                Assert.Same(normA, GameDataHooks.CachedNormalizedJson(new string(a.ToCharArray())));   // equal text, another string
                Assert.Null(GameDataHooks.CachedNormalizedJson(Big('b')));                             // same length, other text
                Assert.Null(GameDataHooks.CachedNormalizedJson(Big('a', 1)));
                Assert.Single(lines);
                Assert.StartsWith("json normalize: " + a.Length + " chars in ", lines[0]);
                Assert.EndsWith(" ms (0 large ones from the cache since the last line)", lines[0]);

                // Small inputs are never kept (and the result passes through).
                Assert.Null(GameDataHooks.CachedNormalizedJson("{\"b\":1}"));
                Assert.Equal("x", GameDataHooks.RememberNormalizedJson("x", "{\"b\":1}"));
                Assert.Null(GameDataHooks.CachedNormalizedJson("{\"b\":1}"));
                Assert.Null(GameDataHooks.CachedNormalizedJson(null));
                Assert.Null(GameDataHooks.RememberNormalizedJson(null, a));

                // Least recently used goes first: a is used again, so b (the oldest after it) is dropped by the fifth.
                string[] inputs = { Big('b'), Big('c'), Big('d') };
                foreach (string s in inputs) GameDataHooks.RememberNormalizedJson("n" + s[6], s);
                Assert.Same(normA, GameDataHooks.CachedNormalizedJson(a));
                GameDataHooks.RememberNormalizedJson("ne", Big('e'));
                Assert.Null(GameDataHooks.CachedNormalizedJson(Big('b')));
                Assert.Equal("nc", GameDataHooks.CachedNormalizedJson(Big('c')));
                Assert.Same(normA, GameDataHooks.CachedNormalizedJson(a));
                Assert.Equal(5, GameDataHooks.NormalizeMisses);
                Assert.Equal(5, GameDataHooks.NormalizeHits);
                Assert.Contains("json normalize 5 large ones computed (", GameDataHooks.Describe());
                Assert.Contains(", 5 from the cache; templates: ", GameDataHooks.Describe());
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }

        // ------------------------------------------------------------------------------------------------ crmtables: Database.Load

        /// <summary>Look-alike of Gameloft.Engine.Database.Table: public fields Fields and Rows, properties Database and Name.</summary>
        public sealed class Table
        {
            public string[] Fields;
            public object[][] Rows;
            public object Database { get; set; }
            public string Name { get; set; }
        }

        /// <summary>Look-alike of a Database for Load: its Tables dictionary.</summary>
        public sealed class FileDatabase
        {
            public Dictionary<string, Table> Tables { get; } = new Dictionary<string, Table>();
        }

        private const string MetaFile = "Design/MCVS_Meta.xlsx";

        /// <summary>The game's Load with the patch around it: Restore, else "parse" (two tables, new arrays), then Parsed.</summary>
        private static bool Load(FileDatabase db, string path = MetaFile, object oddCell = null)
        {
            if (GameDataHooks.RestoreDatabaseFile(db, path)) return true;
            foreach (string name in new[] { "Weapons", "Bots" })
            {
                Table t;
                if (!db.Tables.TryGetValue(name, out t))
                {
                    t = db.Tables[name] = new Table();
                    t.Database = db;
                    t.Name = name;
                }
                t.Fields = name == "Weapons" ? new[] { "Id", "Damage" } : new[] { "Name" };
                t.Rows = name == "Weapons"
                    ? new[] { new object[] { "rifle", 10L }, new object[] { "pistol", oddCell ?? 4.5 } }
                    : new[] { new object[] { "Ace" }, new object[] { "Bruno" }, new object[] { "Kim" } };
            }
            GameDataHooks.DatabaseFileParsed(db, path);
            return false;
        }

        [Fact]
        public void AFileIsKeptAtItsSecondParseByANewDatabaseAndRestoredAsCopies()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                var human = new FileDatabase();
                Assert.False(Load(human));                       // parse 1 by a new database: not kept
                Assert.False(Load(human));                       // a reload of an existing database: not kept either
                Assert.Equal(0, GameDataHooks.FilesKept);
                var bot1 = new FileDatabase();
                Assert.False(Load(bot1));                        // parse 2 by a new database: kept
                Assert.Equal(1, GameDataHooks.FilesKept);
                Assert.Contains("crm tables: Design/MCVS_Meta.xlsx read from disk and parsed in ", lines.Last());
                Assert.EndsWith(" (a new database); its rows are kept in memory for the next loads (2 tables, 5 rows, 7 cells)", lines.Last());

                bot1.Tables["Weapons"].Rows[0][1] = 15.0;        // a CRM cell applied after the parse: not in the kept copy
                var bot2 = new FileDatabase();
                Assert.True(Load(bot2));                         // restored: new Table objects, the dictionary in the file's order
                Assert.Equal(new[] { "Weapons", "Bots" }, bot2.Tables.Keys);
                Assert.Same(bot2, bot2.Tables["Bots"].Database);
                Assert.Equal("Bots", bot2.Tables["Bots"].Name);
                Assert.Equal(10L, bot2.Tables["Weapons"].Rows[0][1]);
                Assert.Equal("Kim", bot2.Tables["Bots"].Rows[2][0]);
                Assert.StartsWith("crm tables: Design/MCVS_Meta.xlsx restored from memory (2 tables, 5 rows) in ", lines.Last());
                Assert.EndsWith(", no read from disk", lines.Last());

                // A reload of an existing database keeps its Table objects and gets new arrays: nothing is shared.
                Table weapons = human.Tables["Weapons"];
                human.Tables["Weapons"].Rows[1][1] = "changed";
                Assert.True(Load(human));
                Assert.Same(weapons, human.Tables["Weapons"]);
                Assert.Equal(4.5, human.Tables["Weapons"].Rows[1][1]);
                Assert.NotSame(human.Tables["Weapons"].Rows, bot2.Tables["Weapons"].Rows);
                Assert.NotSame(human.Tables["Weapons"].Rows[0], bot2.Tables["Weapons"].Rows[0]);
                Assert.NotSame(human.Tables["Weapons"].Fields, bot2.Tables["Weapons"].Fields);
                human.Tables["Weapons"].Fields[0] = "X";
                var bot3 = new FileDatabase();
                Assert.True(Load(bot3));
                Assert.Equal("Id", bot3.Tables["Weapons"].Fields[0]);
                Assert.Equal(1, GameDataHooks.FilesKept);

                // Another file has its own count.
                Assert.False(Load(new FileDatabase(), "Design/MCVS_AI.xlsx"));
                Assert.Equal(3, GameDataHooks.FilesRestored);
                Assert.Equal(4, GameDataHooks.FilesParsed);
                string status = GameDataHooks.Describe();
                Assert.Contains("; database files 4 parsed (", status);
                Assert.Contains(" 3 restored from memory (", status);
                Assert.Contains(" 1 files kept; json normalize ", status);

                // Game_DataChanged: nothing kept is used again, and the counts start over.
                GameDataHooks.ForgetDatabaseFiles();
                Assert.Equal(0, GameDataHooks.FilesKept);
                Assert.False(Load(new FileDatabase()));
                Assert.False(Load(new FileDatabase()));
                Assert.True(Load(new FileDatabase()));
                Assert.Contains(" 1 files kept (1 forgotten: data changed); json normalize ", GameDataHooks.Describe());
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }

        [Fact]
        public void AFileWithACellThatIsNotAPlainValueIsNeverKept()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                var odd = new List<int> { 1 };                   // a JSON object or array cell (JToken) is mutable
                Assert.False(Load(new FileDatabase(), MetaFile, odd));
                Assert.False(Load(new FileDatabase(), MetaFile, odd));
                Assert.EndsWith("; not kept in memory (a cell is not a plain value)", lines.Last());
                Assert.False(Load(new FileDatabase(), MetaFile, odd));
                Assert.Equal(0, GameDataHooks.FilesKept);
                Assert.True(GameDataHooks.IsPlainValue(null));
                Assert.True(GameDataHooks.IsPlainValue("x"));
                Assert.True(GameDataHooks.IsPlainValue(3L));
                Assert.True(GameDataHooks.IsPlainValue(2.5));
                Assert.True(GameDataHooks.IsPlainValue(true));
                Assert.True(GameDataHooks.IsPlainValue(DateTime.UtcNow));
                Assert.False(GameDataHooks.IsPlainValue(new object[0]));
                Assert.False(GameDataHooks.IsPlainValue(new object()));
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }

        [Fact]
        public void TheLoadHooksSayNoAndKeepNothingOnInputTheyDoNotKnow()
        {
            GameDataHooks.Reset();
            try
            {
                Assert.False(GameDataHooks.RestoreDatabaseFile(null, MetaFile));
                Assert.False(GameDataHooks.RestoreDatabaseFile(new FileDatabase(), null));
                Assert.False(GameDataHooks.RestoreDatabaseFile(new FileDatabase(), ""));
                Assert.False(GameDataHooks.RestoreDatabaseFile(new object(), MetaFile));   // no Tables: a failure is "no"
                GameDataHooks.DatabaseFileParsed(new object(), MetaFile);                   // without a Restore call first: ignored
                GameDataHooks.DatabaseFileParsed(null, null);
                Assert.Equal(0, GameDataHooks.FilesParsed);
                // A parse that fails (the file is missing) leaves nothing behind for the next Load.
                Assert.False(GameDataHooks.RestoreDatabaseFile(new FileDatabase(), MetaFile));
                Assert.False(Load(new FileDatabase()));
                Assert.Equal(1, GameDataHooks.FilesParsed);
            }
            finally
            {
                GameDataHooks.Reset();
            }
        }

        /// <summary>
        /// Build 32 review: a template read inside another on the same thread (a base template not cached yet, read by a nested
        /// ReadDefaultTemplate) has its own line; the outer read's total, nodes part and the status counters leave its time out,
        /// and the outer line says how much. A read after them (not inside) leaves nothing out.
        /// </summary>
        [Fact]
        public void ATemplateReadInsideAnotherIsCountedOnce()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                long second = System.Diagnostics.Stopwatch.Frequency;
                long now = GameDataHooks.TemplateClock();
                long outerStarted = now - second, outerRooted = now - second * 9 / 10;
                // The base template: started 0.4 s ago (inside the outer read, after its JSON), ends now.
                GameDataHooks.TemplateRead("Base.tx", now - second * 4 / 10, now - second * 35 / 100, 0, 0);
                GameDataHooks.TemplateRead("DefaultProfile.tx", outerStarted, outerRooted, 3, 11368);
                Assert.Equal(2, lines.Count);
                Assert.StartsWith("templates: Base.tx read in ", lines[0]);
                Assert.DoesNotContain("not counting", lines[0]);
                Match outer = Regex.Match(lines[1], @"^templates: DefaultProfile\.tx read in ([0-9.]+) ms \(JSON ([0-9.]+) ms, nodes ([0-9.]+) ms; not counting ([0-9.]+) ms in 1 template read inside it, counted on their own\); 3 objects");
                Assert.True(outer.Success, lines[1]);
                double total = Number(outer, 1), json = Number(outer, 2), nodes = Number(outer, 3), inner = Number(outer, 4);
                Assert.InRange(inner, 395, 700);
                Assert.InRange(total, 595, 800);   // about 1 s minus the base's 0.4 s
                Assert.InRange(json, 99, 101);
                Assert.InRange(nodes, total - json - 1, total - json + 1);
                // The status total is the two own times (about 1 s), not 1.4 s.
                Match status = Regex.Match(GameDataHooks.Describe(), @"; templates: 2 read \(([0-9.]+) ms, longest ([0-9.]+) ms");
                Assert.True(status.Success, GameDataHooks.Describe());
                Assert.InRange(Number(status, 1), 995, 1300);
                Assert.InRange(Number(status, 2), total - 1, total + 1);

                // A three-level chain in the order the reads end: C inside B inside A.
                lines.Clear();
                now = GameDataHooks.TemplateClock();
                GameDataHooks.TemplateRead("C.tx", now - second / 10, 0, 0, 0);
                GameDataHooks.TemplateRead("B.tx", now - second * 3 / 10, 0, 0, 0);
                GameDataHooks.TemplateRead("A.tx", now - second * 6 / 10, 0, 0, 0);
                Match b = Regex.Match(lines[1], @"^templates: B\.tx read in ([0-9.]+) ms \(not counting ([0-9.]+) ms in 1 template read inside it");
                Match a = Regex.Match(lines[2], @"^templates: A\.tx read in ([0-9.]+) ms \(not counting ([0-9.]+) ms in 1 template read inside it");
                Assert.True(b.Success && a.Success, string.Join("\n", lines));
                Assert.InRange(Number(b, 1), 195, 300);   // B's own 0.2 s
                Assert.InRange(Number(a, 1), 295, 400);   // A's own 0.3 s: B (0.3 s, C included) is taken out once
                Assert.InRange(Number(a, 2), 295, 400);

                // A read that starts after them nested nothing.
                lines.Clear();
                long later = GameDataHooks.TemplateClock();
                GameDataHooks.TemplateRead("After.tx", later - second / 5, 0, 0, 0);
                Assert.DoesNotContain("not counting", Assert.Single(lines));
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }

        private static double Number(Match m, int group)
        {
            return double.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Build 32, patch 'templates': timing only; a slow or large read gets a line, every read counts, lines are capped.</summary>
        [Fact]
        public void TemplateReadsAreCountedAndTheSlowOrLargeOnesLogged()
        {
            GameDataHooks.Reset();
            var lines = new List<string>();
            GameDataHooks.Configure(lines.Add);
            try
            {
                Assert.Contains("; templates: none read yet (or no 'templates' patch)", GameDataHooks.Describe());
                long now = GameDataHooks.TemplateClock();
                long second = System.Diagnostics.Stopwatch.Frequency;
                // In the order they started (reads on one thread follow each other or nest; none of these nests in another).
                GameDataHooks.TemplateRead("Slow.tx", now - second, 0, 1, 9);                               // slow: logged, no JSON split
                GameDataHooks.TemplateRead("DefaultProfile.tx", now - second / 2, now - second / 4, 23, 11368);
                GameDataHooks.TemplateRead("Small.tx", now, now, 0, 0);                                     // fast and small: counted only
                GameDataHooks.TemplateRead("Never.tx", 0, 0, 5, 50000);                                     // no clock (hooks were missing): ignored
                Assert.Equal(3, GameDataHooks.TemplateReads);
                Assert.Equal(24, GameDataHooks.TemplateObjectsIndexed);
                Assert.Equal(11368, GameDataHooks.TemplateLargestObject);
                Assert.Equal(2, lines.Count);
                Assert.StartsWith("templates: Slow.tx read in ", lines[0]);
                Assert.DoesNotContain("JSON", lines[0]);
                Assert.DoesNotContain("(", lines[0].Substring(0, lines[0].IndexOf("; ", StringComparison.Ordinal)));
                Assert.StartsWith("templates: DefaultProfile.tx read in ", lines[1]);
                Assert.Contains(" (JSON ", lines[1]);
                Assert.DoesNotContain("not counting", lines[1]);
                Assert.EndsWith("; 23 objects of 8+ names indexed, the largest 11368 names (patch 'templates')", lines[1]);

                GameDataHooks.TemplateFromObject(new List<int>(), now - second / 5);
                GameDataHooks.TemplateFromObject(null, now);                                                // fast: counted only
                Assert.StartsWith("templates: TObject.FromObject(List`1) took ", lines[2]);
                GameDataHooks.TemplateToJson(new string('x', 611274), now - second / 4);
                GameDataHooks.TemplateToJson("{}", now);                                                    // fast: counted only
                GameDataHooks.TemplateToJson(null, 0);                                                      // no clock: ignored
                Assert.StartsWith("templates: TObject.ToString(\"j\") wrote 611274 chars in ", lines[3]);
                Assert.EndsWith(" (the template writer's text: a profile save stores it)", lines[3]);
                string status = GameDataHooks.Describe();
                Assert.Contains("); ToString(j) 2 (1 of 100 ms or more, ", status);
                Assert.Contains("; templates: 3 read (", status);
                Assert.Contains("2 slow or large ones", status);
                Assert.Contains("24 objects indexed (largest 11368 names); FromObject 2 (1 of 100 ms or more, ", status);

                for (int i = 0; i < GameDataHooks.TemplateLineLimit + 5; i++)
                {
                    long at = GameDataHooks.TemplateClock();
                    GameDataHooks.TemplateRead("Big.tx", at, at, 1, 5000);
                }
                Assert.Equal(GameDataHooks.TemplateLineLimit, lines.Count);
                Assert.Equal(3 + GameDataHooks.TemplateLineLimit + 5, GameDataHooks.TemplateReads);
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }
    }
}
