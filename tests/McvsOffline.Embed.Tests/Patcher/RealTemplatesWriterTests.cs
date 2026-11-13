using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using McvsOffline.Embed.Tests.Patcher.Pc;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;
using Reader = McvsOffline.Embed.Tests.Patcher.RealTemplatesPatchTests.Reader;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Build 32's 'templates' patch, second part: the template writer (<c>DefaultConverter.ConvertToTemplate</c>, behind
    /// <c>TObject.FromObject</c> of every profile save), <c>TNode.Copy</c> and <c>TNode.MergeWith</c> (<c>ValidateProfileVersion</c>'s
    /// merge of the default profile with the loaded one) without their per-child name scans, on the player's REAL
    /// Gameloft.Templates.dll of both builds, shipped vs patched vs patched without the server DLL:
    /// <list type="bullet">
    /// <item>a profile-save-shaped object graph (a GameObject-like <c>ITemplatable</c> root with component children, the 11,368-key
    /// unlock-all Inventory dictionary, lists, an int-keyed dictionary, enums, dates, nulls, a value declared as object) gives the
    /// shipped tree node for node and a byte-identical <c>ToString("j")</c>, faster;</item>
    /// <item>duplicate child names (before and after the 8th child, where the patched writer switches to its name set), a null
    /// name, attribute names and a converter that returns a node of another tree throw or build exactly as the shipped code;</item>
    /// <item><c>Copy</c> and <c>MergeWith</c> of the unlock-all profile and of 300 random trees (duplicate names, null values,
    /// arrays, null and empty names, nodes in two lists) give the shipped trees or the same exception.</item>
    /// </list>
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1 (Steam: MCVS_REQUIRE_PC=1).
    /// </summary>
    [Collection("GameHooks")]
    public class RealTemplatesWriterTests
    {
        private readonly ITestOutputHelper output;

        public RealTemplatesWriterTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Gameloft.Templates.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Gameloft.Templates.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static byte[] Server()
        {
            return FixtureCompiler.Compile("McvsOffline.Server", FixtureCompiler.RepoSource("hosts", "embedded", "GameDataHooks.cs"));
        }

        /// <summary>The three runs of one build: shipped, patched with the server, patched without it.</summary>
        private sealed class Build
        {
            public string Name, Folder;
            public Dictionary<string, byte[]> Patched;
            public byte[] Server, Shapes;

            public GameContext Shipped()
            {
                return new GameContext(Folder, new Dictionary<string, byte[]> { { "McvsFixture.Shapes.dll", Shapes } }, null);
            }

            public GameContext WithServer()
            {
                var images = new Dictionary<string, byte[]>(Patched) { { "McvsFixture.Shapes.dll", Shapes } };
                return new GameContext(Folder, images, Server);
            }

            public GameContext WithoutServer()
            {
                var images = new Dictionary<string, byte[]>(Patched) { { "McvsFixture.Shapes.dll", Shapes } };
                return new GameContext(Folder, images, null);
            }
        }

        private IEnumerable<Build> Builds()
        {
            string refs = Refs();
            if (refs == null) yield break;
            byte[] server = Server();
            yield return new Build
            {
                Name = "Android 1.17.32", Folder = refs, Server = server, Shapes = Shapes(refs),
                Patched = RealGamePatchTests.PatchReal(refs, server, null, new TemplatesPatch()),
            };
            string pc = RealPc.Folder(output);
            if (pc == null) yield break;
            byte[] pcServer = PcHookServer.Compile();
            yield return new Build
            {
                Name = "Steam 1.17.30", Folder = pc, Server = pcServer, Shapes = Shapes(pc),
                Patched = RealPc.Patch(pc, pcServer, null, BuildProfile.PcSteam6372049, new TemplatesPatch()),
            };
        }

        // ------------------------------------------------------------------------------------------------ the shapes

        /// <summary>
        /// Object graphs shaped like what <c>GameProfileSection.FromGameObject</c> converts (a GameObject with components, the
        /// Engine's <c>CrmStandardData</c> with its Inventory dictionary), compiled against the build's own Gameloft.Templates.dll.
        /// Shape, not the game's classes: the writer reads them through the same DefaultConverter / DictionaryConverter /
        /// ListConverter / ValueConverter code.
        /// </summary>
        private const string ShapesSource = @"
using System;
using System.Collections.Generic;
using System.Linq;
using Gameloft.Templates;

namespace McvsFixture
{
    public enum Kind { None, Rare, Epic }

    /// <summary>A GameObject-like node: named children (other nodes and components), some properties.</summary>
    public class Thing : ITemplatable
    {
        private readonly List<KeyValuePair<string, ITemplatable>> Kids = new List<KeyValuePair<string, ITemplatable>>();
        private Thing Up;
        public bool Enabled { get; set; }
        public bool CanSave { get; set; }
        public string Title { get; set; }
        TObject ITemplatable.DefaultTemplate { get { return null; } }
        TObject ITemplatable.Template { get; set; }
        public string TemplateLayer { get; set; }
        ITemplateConverter ITemplatable.TemplateConverter { get; set; }
        void ITemplatable.SetTemplateOverride(TName name, bool value) { }
        bool ITemplatable.GetTemplateOverride(TName name) { return false; }
        IEnumerable<TName> ITemplatable.GetTemplateOverrides() { return Enumerable.Empty<TName>(); }
        IEnumerable<string> ITemplatable.GetTemplateChildren() { return Kids.Select(k => k.Key); }
        ITemplatable ITemplatable.GetTemplateChild(string name) { foreach (var k in Kids) if (k.Key == name) return k.Value; return null; }
        void ITemplatable.SetTemplateChild(string name, ITemplatable child) { Kids.Add(new KeyValuePair<string, ITemplatable>(name, child)); }
        ITemplatable ITemplatable.GetTemplateParent() { return Up; }
        public Thing Add(string name, ITemplatable child) { Kids.Add(new KeyValuePair<string, ITemplatable>(name, child)); var t = child as Thing; if (t != null) t.Up = this; return this; }
    }

    public class CrmData
    {
        public Dictionary<string, int> Inventory { get; set; }
        public Dictionary<string, int> AdsInventory { get; set; }
        public bool FirstLaunch { get; set; }
    }

    public class Gear
    {
        public string TemplateId { get; set; }
        public int Level { get; set; }
        public Kind Rarity { get; set; }
        public Dictionary<string, float> Stats { get; set; }
    }

    public class Merc
    {
        public string Id { get; set; }
        public bool Locked { get; set; }
        public int Level { get; set; }
        public List<string> Camos { get; set; }
        public List<string> Accessories { get; set; }
        public int[] Mods { get; set; }
    }

    /// <summary>A component-like child with the profile's properties.</summary>
    public class ProfileLike : Thing
    {
        public int Version { get; set; }
        public string GameVersion { get; set; }
        public long Xp { get; set; }
        public double Ratio { get; set; }
        public DateTime LastSeen { get; set; }
        public string Nothing { get; set; }
        public object Boxed { get; set; }
        public Kind Tier { get; set; }
        public CrmData CrmStandardData { get; set; }
        public List<string> KillSignatureAvatars { get; set; }
        public List<Merc> Mercs { get; set; }
        public Dictionary<int, Gear> Gears { get; set; }
        public Dictionary<string, string> Flags { get; set; }
    }

    /// <summary>Many properties: an object of 8+ names from the default converter itself.</summary>
    public class Wide
    {
        public int A0 { get; set; } public int A1 { get; set; } public int A2 { get; set; } public int A3 { get; set; }
        public int A4 { get; set; } public int A5 { get; set; } public int A6 { get; set; } public int A7 { get; set; }
        public int A8 { get; set; } public int A9 { get; set; } public string B0 { get; set; } public Wide Next { get; set; }
    }

    public class Shared { public int Id { get; set; } }

    /// <summary>A converter that returns one node for every Shared: a node of another tree (no converter of the game does this).</summary>
    public class SharedConverter : ITemplateConverter
    {
        private static TNode node;
        public bool CanConvertToTemplate(object sourceInstance) { return sourceInstance is Shared; }
        public TNode ConvertToTemplate(object sourceInstance, TemplateContext context) { if (node == null) node = new TValue(1); return node; }
        public bool CanConvertFromTemplate(Type targetType, TNode sourceNode) { return false; }
        public object ConvertFromTemplate(Type targetType, TNode sourceNode, TemplateContext context) { return null; }
    }

    public class Holder
    {
        public Shared S0 { get; set; } public Shared S1 { get; set; } public Shared S2 { get; set; } public int N { get; set; }
    }

    public static class Make
    {
        public static Thing Profile(string[] keys)
        {
            var inv = new Dictionary<string, int>();
            for (int i = 0; i < keys.Length; i++) inv[keys[i]] = 1 + i % 3;
            var p = new ProfileLike
            {
                Version = 57, GameVersion = ""1.17.32"", Xp = 105200, Ratio = 0.25, LastSeen = new DateTime(2026, 11, 11, 18, 20, 1, DateTimeKind.Utc),
                Boxed = 42, Tier = Kind.Epic, Enabled = true, CanSave = true, Title = ""Pão"",
                CrmStandardData = new CrmData { Inventory = inv, AdsInventory = new Dictionary<string, int>(), FirstLaunch = false },
                KillSignatureAvatars = Enumerable.Range(1, 1512).Select(i => i.ToString()).ToList(),
                Mercs = Enumerable.Range(1, 21).Select(m => new Merc { Id = m.ToString(), Level = 30, Camos = Enumerable.Range(1, 210).Select(i => i.ToString()).ToList(),
                                                                       Accessories = Enumerable.Range(1, 248).Select(i => i.ToString()).ToList(), Mods = Enumerable.Range(m * 100, 22).ToArray() }).ToList(),
                Gears = Enumerable.Range(0, 400).ToDictionary(g => g, g => new Gear { TemplateId = ""COGScope_Ass_Rifle_Rar1"", Level = g % 10, Rarity = (Kind)(g % 3),
                                                                                       Stats = new Dictionary<string, float> { { ""Accuracy"", 1.5f }, { ""Range"", 2f } } }),
                Flags = Enumerable.Range(0, 40).ToDictionary(i => ""f"" + i, i => i % 5 == 0 ? null : ""v"" + i),
            };
            var root = new Thing { Enabled = true, CanSave = true, Title = ""Player"" };
            root.Add(""Profile"", p);
            root.Add(""League"", new Thing { Title = ""League"", TemplateLayer = ""default"" });
            var wideHolder = new Thing { Title = ""Wide"" };
            root.Add(""Wide"", wideHolder);
            for (int i = 0; i < 12; i++) root.Add(""Component"" + i, new Thing { Title = ""c"" + i, Enabled = i % 2 == 0 });
            return root;
        }

        /// <summary>A root with <paramref name=""children""/> kids named as given (duplicates allowed).</summary>
        public static Thing Named(string[] children)
        {
            var root = new Thing { Title = ""r"" };
            for (int i = 0; i < children.Length; i++) root.Add(children[i], new Thing { Title = ""k"" + i });
            return root;
        }

        public static Wide Chain(int depth)
        {
            Wide w = null;
            for (int i = 0; i < depth; i++) w = new Wide { A0 = i, A5 = -i, B0 = i % 2 == 0 ? null : ""b"" + i, Next = w };
            return w;
        }

        public static Holder Holder() { return new Holder { S0 = new Shared(), S1 = new Shared(), S2 = new Shared(), N = 3 }; }
    }
}
";

        private static byte[] Shapes(string folder)
        {
            byte[] templates = File.ReadAllBytes(Path.Combine(folder, "Gameloft.Templates.dll"));
            return FixtureCompiler.Compile("McvsFixture.Shapes", new[] { ShapesSource }, new[] { templates });
        }

        private static string[] Keys()
        {
            return RealTemplatesPatchTests.UnlockKeys().ToArray();
        }

        private static object Call(GameContext game, string method, params object[] args)
        {
            MethodInfo m = game.Get("McvsFixture.Shapes").GetType("McvsFixture.Make", true).GetMethod(method);
            return m.Invoke(null, args);
        }

        private static MethodInfo FromObjectMethod(GameContext game)
        {
            return game.Get("Gameloft.Templates").GetType(TemplatesPatch.ObjectType, true).GetMethod("FromObject", new[] { typeof(object) });
        }

        private static string Json(object tree)
        {
            return (string)tree.GetType().GetMethod("ToString", new[] { typeof(string) }).Invoke(tree, new object[] { "j" });
        }

        /// <summary>The tree and its text, or the exception.</summary>
        private static string Outcome(GameContext game, Func<object> make)
        {
            try
            {
                object tree = make();
                return new Reader(game).Describe(tree) + "\n---\n" + Json(tree);
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = ex.InnerException;
                while (inner is TargetInvocationException && inner.InnerException != null) inner = inner.InnerException;
                return "throws " + inner.GetType().FullName + ": " + inner.Message;
            }
        }

        private static double Best(Action action, int reps)
        {
            double best = double.MaxValue;
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        // ------------------------------------------------------------------------------------------------ the writer

        [Fact]
        public void TheProfileSaveWriterGivesTheShippedTreeAndTextFasterOnBothBuilds()
        {
            string[] keys = Keys();
            foreach (Build build in Builds())
            {
                string shippedTree, patchedTree, latchedTree, shippedText, patchedText, latchedText, status;
                double shippedMs, patchedMs, latchedMs, shippedJsonMs, patchedJsonMs;
                var lines = new List<string>();
                using (GameContext game = build.Shipped())
                {
                    object root = Call(game, "Profile", new object[] { keys });
                    MethodInfo from = FromObjectMethod(game);
                    shippedMs = Best(() => from.Invoke(null, new[] { root }), 3);
                    object tree = from.Invoke(null, new[] { root });
                    shippedTree = new Reader(game).Describe(tree);
                    shippedJsonMs = Best(() => Json(tree), 3);
                    shippedText = Json(tree);
                }
                using (GameContext game = build.WithServer())
                {
                    Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameDataHooks", true);
                    hooks.GetMethod("Configure").Invoke(null, new object[] { new Action<string>(l => { lock (lines) lines.Add(l); }) });
                    object root = Call(game, "Profile", new object[] { keys });
                    MethodInfo from = FromObjectMethod(game);
                    patchedMs = Best(() => from.Invoke(null, new[] { root }), 3);
                    object tree = from.Invoke(null, new[] { root });
                    patchedTree = new Reader(game).Describe(tree);
                    patchedJsonMs = Best(() => Json(tree), 3);
                    patchedText = Json(tree);
                    status = (string)hooks.GetMethod("Describe").Invoke(null, null);
                }
                using (GameContext game = build.WithoutServer())
                {
                    object root = Call(game, "Profile", new object[] { keys });
                    MethodInfo from = FromObjectMethod(game);
                    latchedMs = Best(() => from.Invoke(null, new[] { root }), 3);
                    object tree = from.Invoke(null, new[] { root });
                    latchedTree = new Reader(game).Describe(tree);
                    latchedText = Json(tree);
                }
                output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}: FromObject of the profile-save shape (Inventory {1} keys; tree {2} lines, text {3} chars), best of 3: shipped {4:F1} ms, patched {5:F1} ms ({6:F1}x), " +
                    "patched without the server {7:F1} ms; ToString(\"j\") shipped {8:F1} ms, patched {9:F1} ms",
                    build.Name, keys.Length, shippedTree.Count(c => c == '\n'), shippedText.Length, shippedMs, patchedMs, shippedMs / patchedMs, latchedMs,
                    shippedJsonMs, patchedJsonMs));
                output.WriteLine(build.Name + " status: " + status.Substring(status.IndexOf("; templates:", StringComparison.Ordinal)));
                foreach (string l in lines.Take(4)) output.WriteLine(build.Name + ": " + l);
                Assert.Equal(shippedTree, patchedTree);
                Assert.Equal(shippedTree, latchedTree);
                Assert.True(shippedText == patchedText, "ToString(\"j\") differs (patched)");
                Assert.True(shippedText == latchedText, "ToString(\"j\") differs (without the server)");
                Assert.Equal(Encoding.UTF8.GetBytes(shippedText), Encoding.UTF8.GetBytes(patchedText));
                Assert.DoesNotContain("WRONG PARENT", shippedTree);
                Assert.Contains("Inventory", shippedText);
                Assert.True(shippedTree.Count(c => c == '\n') > 20000, "a profile-sized tree");
                Assert.Contains("FromObject 4 (", status);
                Assert.Contains("ToString(j) 4 (", status);
                // The k²/2 Inventory scan is gone: at least 2x on any machine.
                Assert.True(patchedMs * 2 < shippedMs, "patched " + patchedMs + " ms vs shipped " + shippedMs + " ms");
                Assert.True(latchedMs * 2 < shippedMs, "patched without the server " + latchedMs + " ms vs shipped " + shippedMs + " ms");
            }
        }

        /// <summary>Child names that collide (the shipped writer throws "Conflict with name"), before and after the 8th child.</summary>
        private static IEnumerable<KeyValuePair<string, string[]>> NameCases()
        {
            string[] Seq(int n) => Enumerable.Range(0, n).Select(i => "c" + i).ToArray();
            yield return Case("3 distinct", Seq(3));
            yield return Case("7 distinct", Seq(7));
            yield return Case("8 distinct", Seq(8));
            yield return Case("40 distinct", Seq(40));
            yield return Case("duplicate at 2", new[] { "a", "b", "a" });
            yield return Case("duplicate at 7 (the 8th)", Seq(7).Concat(new[] { "c3" }).ToArray());
            yield return Case("duplicate at 8 (the 9th)", Seq(8).Concat(new[] { "c0" }).ToArray());
            yield return Case("duplicate at 30", Seq(30).Concat(new[] { "c29" }).ToArray());
            yield return Case("duplicate of the last", Seq(20).Concat(new[] { "c19", "c20" }).ToArray());
            yield return Case("odd names", Seq(10).Concat(new[] { "", "a b", "'q'", "x.y", "[1]", "@z", "Enabled", "Title" }).ToArray());
            yield return Case("an empty name twice", Seq(10).Concat(new[] { "", "" }).ToArray());
            yield return Case("names that differ only in case", Seq(10).Concat(new[] { "Abc", "abc", "ABC" }).ToArray());
        }

        private static KeyValuePair<string, string[]> Case(string name, string[] children)
        {
            return new KeyValuePair<string, string[]>(name, children);
        }

        [Fact]
        public void CollidingAndOddNamesThrowOrBuildExactlyAsShippedOnBothBuilds()
        {
            List<KeyValuePair<string, string[]>> cases = NameCases().ToList();
            foreach (Build build in Builds())
            {
                var runs = new List<List<string>>();
                foreach (Func<GameContext> make in new Func<GameContext>[] { build.Shipped, build.WithServer, build.WithoutServer })
                {
                    using (GameContext game = make())
                    {
                        MethodInfo from = FromObjectMethod(game);
                        var outcomes = cases.Select(c => Outcome(game, () => from.Invoke(null, new[] { Call(game, "Named", new object[] { c.Value }) }))).ToList();
                        outcomes.Add(Outcome(game, () => from.Invoke(null, new[] { Call(game, "Chain", 30) })));
                        runs.Add(outcomes);
                    }
                }
                int throws = runs[0].Count(o => o.StartsWith("throws ", StringComparison.Ordinal));
                output.WriteLine(build.Name + ": " + runs[0].Count + " objects, " + throws + " throw");
                foreach (string o in runs[0].Where(o => o.StartsWith("throws ", StringComparison.Ordinal)).Distinct()) output.WriteLine("  " + o);
                for (int i = 0; i < runs[0].Count; i++)
                {
                    string name = i < cases.Count ? cases[i].Key : "chain";
                    Assert.True(runs[0][i] == runs[1][i], name + "\nshipped:\n" + runs[0][i] + "\npatched:\n" + runs[1][i]);
                    Assert.True(runs[0][i] == runs[2][i], name + "\nshipped:\n" + runs[0][i] + "\nwithout the server:\n" + runs[2][i]);
                }
                Assert.InRange(throws, 6, 8);
                Assert.Contains(runs[0], o => o.Contains("Conflict with name"));
            }
        }

        [Fact]
        public void AConverterThatReturnsANodeOfAnotherTreeGetsTheShippedChecksOnBothBuilds()
        {
            foreach (Build build in Builds())
            {
                var outcomes = new List<string>();
                bool sharedSeen = false;
                foreach (Func<GameContext> make in new Func<GameContext>[] { build.Shipped, build.WithServer, build.WithoutServer })
                {
                    using (GameContext game = make())
                    {
                        Assembly templates = game.Get("Gameloft.Templates");
                        Type converter = game.Get("McvsFixture.Shapes").GetType("McvsFixture.SharedConverter", true);
                        templates.GetType("Gameloft.Templates.TemplateActivator", true).GetMethod("AddConverter")
                                 .Invoke(null, new[] { Activator.CreateInstance(converter) });
                        MethodInfo from = FromObjectMethod(game);
                        string first = Outcome(game, () => from.Invoke(null, new[] { Call(game, "Holder") }));
                        // after it, a profile-shaped object converts as shipped too (the fast path is off for good in this process)
                        string after = Outcome(game, () => from.Invoke(null, new[] { Call(game, "Named", new object[] { Enumerable.Range(0, 12).Select(i => "n" + i).Concat(new[] { "n4" }).ToArray() }) }));
                        outcomes.Add(first + "\n===\n" + after);
                        FieldInfo shared = templates.GetType(TemplatesPatch.NodeType, true).GetField("mcvsShared", BindingFlags.NonPublic | BindingFlags.Static);
                        if (shared != null && (bool)shared.GetValue(null)) sharedSeen = true;
                    }
                }
                output.WriteLine(build.Name + ": " + outcomes[0].Replace("\n", " | ").Substring(0, Math.Min(400, outcomes[0].Length)));
                Assert.Equal(outcomes[0], outcomes[1]);
                Assert.Equal(outcomes[0], outcomes[2]);
                Assert.Contains("throws Gameloft.Templates.TemplateException: Conflict with name", outcomes[0]);
                Assert.True(sharedSeen, "the patched writer saw the shared node");
            }
        }

        // ------------------------------------------------------------------------------------------------ Copy and MergeWith

        /// <summary>A small DefaultProfile-like template: what ValidateProfileVersion merges the loaded profile into.</summary>
        private static string DefaultProfile()
        {
            var sb = new StringBuilder("{\"@version\":\"0.1\",\"$default\":{\"Enabled\":true,\"Locked\":false,\"CanSave\":true,\"@type\":\"GameObject\",");
            sb.Append("\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"Version\":1,\"GameVersion\":null,\"Xp\":0,\"PlayerName\":null,");
            for (int i = 0; i < 60; i++) sb.Append("\"Prop").Append(i * 2).Append("\":").Append(i % 4 == 0 ? "null" : i.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"CrmStandardData\":{\"Inventory\":{\"currency_soft\":100,\"currency_hard\":null,\"Default_Item\":1},\"AdsInventory\":{},\"FirstLaunch\":true},");
            sb.Append("\"KillSignatureAvatars\":[\"1\",\"2\"],\"Mercs\":[{\"Id\":\"1\",\"Locked\":true}]},");
            sb.Append("\"/League\":{\"@type\":\"Game.LeagueComponent\",\"LeagueDivisionId\":1},\"/Gears\":{\"@type\":\"Game.GearsComponent\",\"Gears\":[]}}}");
            return sb.ToString();
        }

        [Fact]
        public void CopyAndTheProfileVersionMergeGiveTheShippedTreesFasterOnBothBuilds()
        {
            string profile = RealTemplatesPatchTests.UnlockAllProfile(RealTemplatesPatchTests.UnlockKeys());
            string defaults = DefaultProfile();
            foreach (Build build in Builds())
            {
                var trees = new List<string>();
                var copyMs = new List<double>();
                var mergeMs = new List<double>();
                foreach (Func<GameContext> make in new Func<GameContext>[] { build.Shipped, build.WithServer, build.WithoutServer })
                {
                    using (GameContext game = make())
                    {
                        var reader = new Reader(game);
                        object t = reader.Read(profile);
                        MethodInfo copy = t.GetType().GetMethod("Copy");
                        MethodInfo merge = t.GetType().GetMethod("MergeWith");
                        copyMs.Add(Best(() => copy.Invoke(t, null), 3));
                        object c = copy.Invoke(t, null);
                        mergeMs.Add(Best(() => merge.Invoke(reader.Read(defaults), new[] { t }), 3));
                        object d = reader.Read(defaults);
                        merge.Invoke(d, new[] { t });
                        // the merged profile again into a copy of itself (every name found: the merge-into-child path)
                        object again = copy.Invoke(d, null);
                        merge.Invoke(again, new[] { t });
                        trees.Add(reader.Describe(c) + "\n=== merged\n" + reader.Describe(d) + "\n=== again\n" + reader.Describe(again) + "\n=== source\n" + reader.Describe(t));
                    }
                }
                output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}: Copy of the unlock-all profile, best of 3: shipped {1:F1} ms, patched {2:F1} ms, without the server {3:F1} ms; " +
                    "DefaultProfile.MergeWith(profile) (with the read of the default): shipped {4:F1} ms, patched {5:F1} ms, without the server {6:F1} ms; {7} lines",
                    build.Name, copyMs[0], copyMs[1], copyMs[2], mergeMs[0], mergeMs[1], mergeMs[2], trees[0].Count(ch => ch == '\n')));
                Assert.True(trees[0] == trees[1], "patched Copy/MergeWith differ from shipped");
                Assert.True(trees[0] == trees[2], "patched without the server: Copy/MergeWith differ from shipped");
                Assert.DoesNotContain("WRONG PARENT", trees[0]);
                Assert.True(copyMs[1] * 2 < copyMs[0], "Copy patched " + copyMs[1] + " ms vs shipped " + copyMs[0] + " ms");
                Assert.True(mergeMs[1] * 2 < mergeMs[0], "MergeWith patched " + mergeMs[1] + " ms vs shipped " + mergeMs[0] + " ms");
            }
        }

        // ---- random trees, built node by node (duplicate names, null and empty names, nodes in two lists)

        /// <summary>Builds trees in one GameContext by reflection from a seed: the same seed, the same calls on every build.</summary>
        private sealed class Forest
        {
            private readonly ConstructorInfo newObject, newArray, newValue, newName;
            private readonly MethodInfo add, copy, merge, setNode;
            private readonly Type nameType;

            public Forest(GameContext game)
            {
                Assembly t = game.Get("Gameloft.Templates");
                nameType = t.GetType(TemplatesPatch.NameType, true);
                Type nodeType = t.GetType(TemplatesPatch.NodeType, true);
                Type kind = t.GetType("Gameloft.Templates.TNameType", true);
                newName = nameType.GetConstructor(new[] { typeof(string), kind });
                newObject = t.GetType(TemplatesPatch.ObjectType, true).GetConstructor(new[] { nameType });
                newArray = t.GetType(TemplatesPatch.ArrayType, true).GetConstructor(new[] { nameType });
                newValue = t.GetType(TemplatesPatch.ValueType, true).GetConstructor(new[] { nameType, typeof(object) });
                add = nodeType.GetMethod("Add", new[] { nodeType, typeof(bool) });
                copy = nodeType.GetMethod("Copy");
                merge = nodeType.GetMethod("MergeWith");
                setNode = nodeType.GetMethod("SetNode");
                Kind = kind;
            }

            public Type Kind { get; }

            private static readonly string[] Names = { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "a", "b", "", null };
            private static readonly int[] Types = { 2, 2, 2, 2, 2, 4, 8, 16, 1 };   // Property x5, Child, Attribute, Index, None

            private object Name(Random r, bool inArray)
            {
                string n = Names[r.Next(Names.Length)];
                if (n != null && r.Next(3) != 0) n = n + r.Next(60);
                int type = Types[r.Next(Types.Length)];
                if (inArray && type == 8) type = 16;   // no attribute in an array: TArray.Add names its elements by position
                if (n == null && type != 1) type = 4;  // a null name as a child name (the Steam build cannot intern a null property name)
                return newName.Invoke(new[] { n, Enum.ToObject(Kind, type) });
            }

            /// <summary>A random tree; <paramref name="pool"/> gets nodes to add a second time (a node in two lists).</summary>
            public object Tree(Random r, int depth, List<object> pool, bool wide, bool inArray = false)
            {
                int kind = depth > 3 ? 2 : r.Next(depth == 0 ? 1 : 3);
                object name = depth == 0 ? newName.Invoke(new[] { "default", Enum.ToObject(Kind, 32) }) : Name(r, inArray);
                if (kind == 2) return newValue.Invoke(new[] { name, r.Next(5) == 0 ? null : (object)r.Next(100) });
                object n = (kind == 0 ? newObject : newArray).Invoke(new[] { name });
                int count = wide ? r.Next(6, 40) : r.Next(0, depth == 0 ? 30 : 12);
                for (int i = 0; i < count; i++)
                {
                    object child = kind == 0 && pool.Count > 0 && r.Next(25) == 0 ? pool[r.Next(pool.Count)] : Tree(r, depth + 1, pool, wide && r.Next(3) == 0, kind == 1);
                    if (r.Next(10) == 0) pool.Add(child);
                    try
                    {
                        add.Invoke(n, new[] { child, (object)(r.Next(150) != 0) });   // now and then without the check: a duplicate name on purpose
                    }
                    catch (TargetInvocationException)
                    {
                    }
                }
                return n;
            }

            public object Obj(string name) { return newObject.Invoke(new[] { newName.Invoke(new object[] { name, Enum.ToObject(Kind, 2) }) }); }

            public object Arr(string name) { return newArray.Invoke(new[] { newName.Invoke(new object[] { name, Enum.ToObject(Kind, 2) }) }); }

            public object Val(string name, object value) { return newValue.Invoke(new[] { newName.Invoke(new object[] { name, Enum.ToObject(Kind, 2) }), value }); }

            public void Add(object parent, object child, bool check) { add.Invoke(parent, new[] { child, (object)check }); }

            public object Copy(object n) { return copy.Invoke(n, null); }

            public void Merge(object into, object from) { merge.Invoke(into, new[] { from }); }
        }

        private static string Describe(object root, GameContext game)
        {
            // cycles and nodes in two lists: number each node at its first visit
            Assembly t = game.Get("Gameloft.Templates");
            Type nodeType = t.GetType(TemplatesPatch.NodeType, true), valueType = t.GetType(TemplatesPatch.ValueType, true);
            PropertyInfo name = nodeType.GetProperty("Name"), parent = nodeType.GetProperty("Parent"), value = valueType.GetProperty("Value");
            PropertyInfo nameKind = name.PropertyType.GetProperty("Type"), valueKind = valueType.GetProperty("ValueType");
            PropertyInfo declaredBase = nodeType.GetProperty("DeclaredBase");
            FieldInfo annotations = nodeType.GetField("annotations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nameKind);
            Assert.NotNull(valueKind);
            Assert.NotNull(declaredBase);
            Assert.NotNull(annotations);
            MethodInfo nodes = nodeType.GetMethod("Nodes", Type.EmptyTypes);
            var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            var sb = new StringBuilder();
            void Walk(object n, int depth)
            {
                sb.Append(' ', depth);
                if (ids.TryGetValue(n, out int seen))
                {
                    sb.Append("#").Append(seen).Append('\n');
                    return;
                }
                int id = ids.Count;
                ids[n] = id;
                object p = parent.GetValue(n);
                object nodeName = name.GetValue(n);
                sb.Append('#').Append(id).Append(' ').Append(n.GetType().Name).Append(' ').Append(nodeName?.ToString() ?? "?")
                  .Append('/').Append(nodeName == null ? "?" : nameKind.GetValue(nodeName).ToString()).Append(' ')
                  .Append(p == null ? "-" : ids.TryGetValue(p, out int pid) ? "^" + pid : "^?");
                object b = declaredBase.GetValue(n);
                if (b != null) sb.Append(" base ").Append(ids.TryGetValue(b, out int bid) ? "#" + bid : name.GetValue(b)?.ToString() ?? "?");
                if (annotations.GetValue(n) is IDictionary<string, object> notes)
                    foreach (KeyValuePair<string, object> note in notes.OrderBy(x => x.Key, StringComparer.Ordinal))
                        sb.Append(" @").Append(note.Key).Append('=').Append(Convert.ToString(note.Value, CultureInfo.InvariantCulture));
                if (valueType.IsInstanceOfType(n))
                    sb.Append(" = ").Append(valueKind.GetValue(n)).Append(':').Append(Convert.ToString(value.GetValue(n), CultureInfo.InvariantCulture) ?? "null");
                sb.Append('\n');
                if (depth > 40) return;
                foreach (object c in ((IEnumerable)nodes.Invoke(n, null)).Cast<object>().ToList()) Walk(c, depth + 1);
            }
            Walk(root, 0);
            return sb.ToString();
        }

        private static string Try(Func<string> run)
        {
            try
            {
                return run();
            }
            catch (TargetInvocationException ex)
            {
                return "throws " + ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message;
            }
        }

        /// <summary>Hand-made trees that are not trees, and duplicate names, for the merge's checks and hand-backs.</summary>
        private static List<string> OddMerges(GameContext game)
        {
            var f = new Forest(game);
            var outcomes = new List<string>();
            object Wide(string prefix, int n, int from = 0)
            {
                object o = f.Obj("o");
                for (int i = from; i < from + n; i++) f.Add(o, f.Val(prefix + i, i % 3 == 0 ? null : (object)i), true);
                return o;
            }
            // 1. a node in two lists: X in the array "arr" and in this object (its parent is the object); merging an array into
            //    "arr" removes X from the object's list (TArray.MergeWith -> RemoveSelf), in the middle of the object's merge
            outcomes.Add(Try(() =>
            {
                object into = Wide("p", 10), arr = f.Arr("arr"), x = f.Val("x", 7);
                f.Add(arr, x, true);
                f.Add(into, arr, true);
                f.Add(into, x, true);
                object from = Wide("p", 6, 3);
                object src = f.Arr("arr");
                f.Add(src, f.Val("", 1), true);
                f.Add(src, f.Val("", 2), true);
                f.Add(from, src, true);
                for (int i = 0; i < 6; i++) f.Add(from, f.Val("q" + i, i), true);
                f.Merge(into, from);
                return Describe(into, game);
            }));
            // 2. duplicate names in the object merged into (the game's own loop)
            outcomes.Add(Try(() =>
            {
                object into = Wide("p", 10);
                f.Add(into, f.Val("p4", 99), false);
                f.Merge(into, Wide("p", 12));
                return Describe(into, game);
            }));
            // 3. duplicate names in the source
            outcomes.Add(Try(() =>
            {
                object into = Wide("p", 3), from = Wide("p", 10);
                f.Add(from, f.Val("p1", 5), false);
                f.Add(from, f.Val("p20", 6), false);
                f.Add(from, f.Val("p20", 7), false);
                f.Merge(into, from);
                return Describe(into, game);
            }));
            // 4. merging an object with itself
            outcomes.Add(Try(() =>
            {
                object o = Wide("p", 12);
                f.Merge(o, o);
                return Describe(o, game);
            }));
            // 5. a source that contains the object merged into (the source's child is the target)
            outcomes.Add(Try(() =>
            {
                object into = Wide("p", 9), from = Wide("p", 9, 4);
                f.Add(from, into, false);
                f.Merge(into, from);
                return Describe(into, game);
            }));
            // 6. an object that is its own child
            outcomes.Add(Try(() =>
            {
                object into = Wide("p", 9);
                f.Add(into, into, false);
                object from = Wide("p", 9, 2);
                object inner = Wide("p", 9, 5);
                f.Add(from, inner, false);
                f.Merge(into, from);
                return Describe(into, game);
            }));
            // 7. Copy of an object with a duplicate name after its 8th child, and of one with a null-named child
            outcomes.Add(Try(() =>
            {
                object o = Wide("p", 12);
                f.Add(o, f.Val("p10", 1), false);
                return Describe(f.Copy(o), game);
            }));
            outcomes.Add(Try(() =>
            {
                object o = Wide("p", 12);
                f.Add(o, f.Val("p1", 1), false);
                return Describe(f.Copy(o), game);
            }));
            return outcomes;
        }

        [Fact]
        public void TreesThatAreNotTreesMergeAndCopyExactlyAsShippedOnBothBuilds()
        {
            foreach (Build build in Builds())
            {
                var runs = new List<List<string>>();
                foreach (Func<GameContext> make in new Func<GameContext>[] { build.Shipped, build.WithServer, build.WithoutServer })
                    using (GameContext game = make())
                        runs.Add(OddMerges(game));
                for (int i = 0; i < runs[0].Count; i++)
                {
                    output.WriteLine(build.Name + " case " + (i + 1) + ": " + (runs[0][i].Length > 160 ? runs[0][i].Substring(0, 160).Replace("\n", " | ") + "..." : runs[0][i].Replace("\n", " | ")));
                    Assert.True(runs[0][i] == runs[1][i], "case " + (i + 1) + "\nshipped:\n" + runs[0][i] + "\npatched:\n" + runs[1][i]);
                    Assert.True(runs[0][i] == runs[2][i], "case " + (i + 1) + "\nshipped:\n" + runs[0][i] + "\nwithout the server:\n" + runs[2][i]);
                }
            }
        }

        [Fact]
        public void RandomTreesCopyAndMergeExactlyAsShippedOnBothBuilds()
        {
            const int count = 300;
            foreach (Build build in Builds())
            {
                var runs = new List<List<string>>();
                foreach (Func<GameContext> make in new Func<GameContext>[] { build.Shipped, build.WithServer, build.WithoutServer })
                {
                    using (GameContext game = make())
                    {
                        var forest = new Forest(game);
                        var outcomes = new List<string>();
                        for (int s = 0; s < count; s++)
                        {
                            var r = new Random(1000 + s);
                            var pool = new List<object>();
                            object a = forest.Tree(r, 0, pool, s % 2 == 0);
                            object b = forest.Tree(r, 0, pool, s % 3 == 0);
                            outcomes.Add(Try(() => Describe(forest.Copy(a), game)));
                            outcomes.Add(Try(() => { forest.Merge(a, b); return Describe(a, game) + "|" + Describe(b, game); }));
                            outcomes.Add(Try(() => { object c = forest.Copy(b); forest.Merge(c, a); return Describe(c, game); }));
                        }
                        runs.Add(outcomes);
                    }
                }
                int throws = runs[0].Count(o => o.StartsWith("throws ", StringComparison.Ordinal));
                output.WriteLine(build.Name + ": " + runs[0].Count + " Copy/MergeWith outcomes, " + throws + " throw");
                foreach (var g in runs[0].Where(o => o.StartsWith("throws ", StringComparison.Ordinal)).GroupBy(o => o.Length > 70 ? o.Substring(0, 70) : o).OrderByDescending(g => g.Count()).Take(8))
                    output.WriteLine("  " + g.Count() + " x " + g.Key);
                foreach (int i in Enumerable.Range(0, runs[0].Count).Where(i => runs[0][i].StartsWith("throws ", StringComparison.Ordinal)).Take(9))
                    output.WriteLine("  e.g. case " + i + " (" + (i % 3 == 0 ? "Copy" : "MergeWith") + "): " + runs[0][i]);
                for (int i = 0; i < runs[0].Count; i++)
                {
                    Assert.True(runs[0][i] == runs[1][i], "case " + i + "\nshipped:\n" + runs[0][i] + "\npatched:\n" + runs[1][i]);
                    Assert.True(runs[0][i] == runs[2][i], "case " + i + "\nshipped:\n" + runs[0][i] + "\nwithout the server:\n" + runs[2][i]);
                }
                Assert.True(throws > 0 && throws < runs[0].Count / 2, throws + " throw");
            }
        }
    }
}
