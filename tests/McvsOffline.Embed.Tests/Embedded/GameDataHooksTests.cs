using System;
using System.Collections.Generic;
using System.Linq;
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
                Assert.EndsWith(", 5 from the cache", GameDataHooks.Describe());
            }
            finally
            {
                GameDataHooks.Configure(null);
                GameDataHooks.Reset();
            }
        }
    }
}
