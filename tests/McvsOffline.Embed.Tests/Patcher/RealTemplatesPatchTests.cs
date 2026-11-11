using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McvsOffline.Embed.Tests.Patcher.Pc;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Build 32's 'templates' patch (Gameloft.Templates <c>JsonTemplateReader</c>) on the player's REAL assemblies of both builds,
    /// executed on net8 next to the shipped code on identical input, with the real <c>McvsOffline.GameDataHooks</c> as the server
    /// and without any server (the <c>hooksMissing</c> latch):
    /// <list type="bullet">
    /// <item>an unlock-all-shaped game profile (the 11,368 ledger keys of server/Content/unlocks.json in
    /// <c>CrmStandardData.Inventory</c>, the kill-signature lists, 21 mercs, ~611 KB) gives the shipped template node for node
    /// (type, name and name type, value and value type, order, parent links, annotations, declared bases), in a fraction of the
    /// time;</item>
    /// <item>hand-written edge cases (names that are one TName: <c>a</c>, <c>.a</c>, <c>'a'</c>; path merges <c>a.b</c> + <c>a.c</c>;
    /// <c>@value</c> objects; a local <c>@base</c>; a null value merged with an object; attributes; index, child and quoted names;
    /// empty and odd names) and 400 random documents give the same tree, or the same exception type and message;</item>
    /// <item>two reads (two bots) share no node, and changing one tree leaves the other as it was;</item>
    /// <item><c>TObject.FromObject</c> is unchanged and reports its time.</item>
    /// </list>
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1 (Steam: MCVS_REQUIRE_PC=1).
    /// </summary>
    [Collection("GameHooks")]
    public class RealTemplatesPatchTests
    {
        private readonly ITestOutputHelper output;

        public RealTemplatesPatchTests(ITestOutputHelper output)
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

        /// <summary>The real data hooks as McvsOffline.Server (the file is System-only on purpose).</summary>
        private static byte[] Server()
        {
            return FixtureCompiler.Compile("McvsOffline.Server", FixtureCompiler.RepoSource("hosts", "embedded", "GameDataHooks.cs"));
        }

        private static MethodDefinition Method(ModuleDefinition module, string type, string name, Func<MethodDefinition, bool> filter = null)
        {
            return module.GetType(type).Methods.Single(m => m.Name == name && (filter == null || filter(m)));
        }

        private static string Fingerprints(ModuleDefinition t)
        {
            return "ReadObject " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "ReadObject", m => m.Parameters.Count == 2)) +
                   ", ReadArray " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "ReadArray", m => m.Parameters.Count == 2)) +
                   ", ReadDefaultTemplate " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "ReadDefaultTemplate")) +
                   ", LoadRoot " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "LoadRoot")) +
                   ", FromObject " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ObjectType, "FromObject")) +
                   ", relied on " + TemplatesPatch.Combined(TemplatesPatch.ReliedOn(t)) +
                   ", ConvertToTemplate " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ConverterType, "ConvertToTemplate")) +
                   ", Copy " + IlPatch.Fingerprint(Method(t, TemplatesPatch.NodeType, "Copy")) +
                   ", ToString " + IlPatch.Fingerprint(Method(t, TemplatesPatch.ObjectType, "ToString", m => m.Parameters.Count == 1)) +
                   ", trees relied on " + TemplatesPatch.Combined(TemplatesPatch.TreesReliedOn(t));
        }

        private static void AssertFingerprints(ModuleDefinition t, bool pc)
        {
            Assert.Equal(pc ? TemplatesPatch.PcReadObjectFingerprint : TemplatesPatch.ReadObjectFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "ReadObject", m => m.Parameters.Count == 2)));
            Assert.Equal(pc ? TemplatesPatch.PcReadArrayFingerprint : TemplatesPatch.ReadArrayFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "ReadArray", m => m.Parameters.Count == 2)));
            Assert.Equal(pc ? TemplatesPatch.PcReadDefaultTemplateFingerprint : TemplatesPatch.ReadDefaultTemplateFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "ReadDefaultTemplate")));
            Assert.Equal(pc ? TemplatesPatch.PcLoadRootFingerprint : TemplatesPatch.LoadRootFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ReaderType, "LoadRoot")));
            Assert.Equal(pc ? TemplatesPatch.PcFromObjectFingerprint : TemplatesPatch.FromObjectFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ObjectType, "FromObject")));
            Assert.Equal(pc ? TemplatesPatch.PcReliedOnFingerprint : TemplatesPatch.ReliedOnFingerprint, TemplatesPatch.Combined(TemplatesPatch.ReliedOn(t)));
            Assert.Equal(pc ? TemplatesPatch.PcConvertToTemplateFingerprint : TemplatesPatch.ConvertToTemplateFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ConverterType, "ConvertToTemplate")));
            Assert.Equal(pc ? TemplatesPatch.PcCopyFingerprint : TemplatesPatch.CopyFingerprint, IlPatch.Fingerprint(Method(t, TemplatesPatch.NodeType, "Copy")));
            Assert.Equal(pc ? TemplatesPatch.PcToStringFingerprint : TemplatesPatch.ToStringFingerprint,
                         IlPatch.Fingerprint(Method(t, TemplatesPatch.ObjectType, "ToString", m => m.Parameters.Count == 1)));
            Assert.Equal(pc ? TemplatesPatch.PcTreesReliedOnFingerprint : TemplatesPatch.TreesReliedOnFingerprint, TemplatesPatch.Combined(TemplatesPatch.TreesReliedOn(t)));
        }

        [Fact]
        public void TheSitesHaveTheVerifiedFingerprintsOnBothBuilds()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                using (var t = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Templates.dll"), new ReaderParameters { AssemblyResolver = resolver }))
                {
                    output.WriteLine("Android " + Fingerprints(t));
                    AssertFingerprints(t, false);
                }
            }
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            ReaderParameters reader = RealPc.Reader(pc, null);
            using (reader.AssemblyResolver)
            using (var t = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Gameloft.Templates.dll"))), reader))
            {
                output.WriteLine("Steam " + Fingerprints(t));
                AssertFingerprints(t, true);
            }
        }

        [Fact]
        public void ThePatchAppliesToTheSteamBuild()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var notes = new List<string>();
            Dictionary<string, byte[]> steam = RealPc.Patch(pc, PcHookServer.Compile(), notes, BuildProfile.PcSteam6372049, new TemplatesPatch());
            Assert.Equal(new[] { "Gameloft.Templates.dll" }, steam.Keys);
            Assert.Contains(notes, n => n.Contains("ReadObject") && n.Contains("dictionary"));
            Assert.Contains(notes, n => n.Contains("ReadDefaultTemplate"));
        }

        [Fact]
        public void ThePatchedIlIsTheVerifiedChange()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), null, new TemplatesPatch());
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Templates.dll"])))
            {
                MethodDefinition readObject = Method(module, TemplatesPatch.ReaderType, "ReadObject", m => m.Parameters.Count == 2);
                Assert.Single(readObject.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "McvsBind");
                Assert.Single(readObject.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "McvsIndexFor");
                Assert.DoesNotContain(readObject.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "BindNodes");
                MethodDefinition readArray = Method(module, TemplatesPatch.ReaderType, "ReadArray", m => m.Parameters.Count == 2);
                Instruction add = readArray.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "Add");
                Assert.Equal(OpCodes.Ldc_I4_0, add.Previous.OpCode);
                MethodDefinition bind = Method(module, TemplatesPatch.ReaderType, "McvsBind");
                Assert.Single(bind.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "BindNodes");
                Assert.Single(bind.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "TryGetValue");
                Assert.NotNull(module.GetType(TemplatesPatch.HelperNamespace + "." + TemplatesPatch.HelperType));
                // the writer, Copy and MergeWith
                MethodDefinition convert = Method(module, TemplatesPatch.ConverterType, "ConvertToTemplate");
                Assert.Single(convert.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "McvsConvertAdd");
                Assert.Single(convert.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "McvsConvertBegin");
                Assert.DoesNotContain(convert.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "Add" && m.DeclaringType.Name == "TNode");
                MethodDefinition copy = Method(module, TemplatesPatch.NodeType, "Copy");
                Assert.Single(copy.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "McvsCopyAdd");
                Assert.DoesNotContain(copy.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "Add");
                MethodDefinition merge = Method(module, TemplatesPatch.NodeType, "MergeWith");
                Assert.Single(merge.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "McvsMergeFrom");
                Assert.Single(merge.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "Node");   // the game's loop is still there
                MethodDefinition text = Method(module, TemplatesPatch.ObjectType, "ToString", m => m.Parameters.Count == 1);
                Assert.Single(text.Body.Instructions, i => i.Operand is MethodReference m && m.Name == "JsonDone");
                Assert.NotNull(module.GetType(TemplatesPatch.NodeType).Fields.SingleOrDefault(f => f.Name == "mcvsShared" && f.IsStatic));
                Assert.Contains(module.AssemblyReferences, r => r.Name == "McvsOffline.Server");
            }
        }

        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string refs = Refs();
            if (refs == null) return;
            RealAdminPagePatchTests.AssertIlVerifyClean(output, refs, "Gameloft.Templates.dll", new IlPatch[] { new TemplatesPatch() },
                new[] { @"\]Gameloft\.Templates\.JsonTemplateReader\.(ReadObject|ReadArray|ReadDefaultTemplate|LoadRoot)$", @"\]Gameloft\.Templates\.TObject\.(FromObject|ToString)$",
                        @"\]Gameloft\.Templates\.TNode\.(Copy|MergeWith)$", @"\]Gameloft\.Templates\.DefaultConverter\.ConvertToTemplate$" }, 10,
                "JsonTemplateReader::ReadObject", "JsonTemplateReader::ReadArray", "JsonTemplateReader::ReadDefaultTemplate", "JsonTemplateReader::LoadRoot",
                "JsonTemplateReader::McvsBind", "JsonTemplateReader::McvsIndexFor", "TObject::FromObject", "TObject::ToString", "McvsTemplates::Clock", "McvsTemplates::Read",
                "McvsTemplates::FromObjectDone", "McvsTemplates::ForwardClock", "McvsTemplates::ForwardRead", "McvsTemplates::ForwardFromObject",
                "McvsTemplates::JsonDone", "McvsTemplates::ForwardJson", "TNode::Copy", "TNode::MergeWith", "TNode::McvsAddNew", "TNode::McvsConvertBegin",
                "TNode::McvsConvertAdd", "TNode::McvsCopyAdd", "TNode::McvsMergeFrom", "DefaultConverter::ConvertToTemplate");
        }

        // ------------------------------------------------------------------------------------------------ the profile

        /// <summary>The unlock-all ledger keys in grant order: server/Content/unlocks.json's groups in its "order" list, expanded.</summary>
        internal static List<string> UnlockKeys()
        {
            using (JsonDocument doc = JsonDocument.Parse(FixtureCompiler.RepoSource("server", "Content", "unlocks.json")))
            {
                var groups = doc.RootElement.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("id").GetString(), g => g);
                var keys = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement id in doc.RootElement.GetProperty("order").EnumerateArray())
                {
                    foreach (JsonElement item in groups[id.GetString()].GetProperty("items").EnumerateArray())
                        foreach (string key in Expand(item.GetString()))
                            if (seen.Add(key)) keys.Add(key);
                }
                return keys;
            }
        }

        private static IEnumerable<string> Expand(string pattern)
        {
            Match m = Regex.Match(pattern, @"\{([^}]*)\}");
            if (!m.Success)
            {
                yield return pattern;
                yield break;
            }
            foreach (string part in m.Groups[1].Value.Split(','))
            {
                IEnumerable<int> range;
                int dash = part.IndexOf('-');
                if (dash > 0)
                {
                    int a = int.Parse(part.Substring(0, dash), CultureInfo.InvariantCulture), b = int.Parse(part.Substring(dash + 1), CultureInfo.InvariantCulture);
                    range = Enumerable.Range(a, b - a + 1);
                }
                else
                {
                    range = new[] { int.Parse(part, CultureInfo.InvariantCulture) };
                }
                foreach (int i in range)
                    foreach (string s in Expand(pattern.Substring(0, m.Index) + i.ToString(CultureInfo.InvariantCulture) + pattern.Substring(m.Index + m.Length)))
                        yield return s;
            }
        }

        /// <summary>
        /// An unlock-all-shaped game-profile dump in the writer's compact form: $default &gt; /Profile &gt; CrmStandardData.Inventory
        /// with every ledger key, the kill-signature lists, 21 mercs with their camo and accessory lists, gears up to ~611 KB.
        /// Shape, not real data.
        /// </summary>
        internal static string UnlockAllProfile(IList<string> keys, int target = 611350)
        {
            var sb = new StringBuilder(target + 1000);
            sb.Append("{\"@version\":\"0.1\",\"$default\":{\"Enabled\":true,\"Locked\":false,\"CanSave\":true,\"@type\":\"GameObject\",");
            sb.Append("\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"Version\":57,\"GameVersion\":\"1.17.32\",\"Xp\":105200,\"PlayerName\":\"Pão\",");
            for (int i = 0; i < 120; i++) sb.Append("\"Prop").Append(i).Append("\":").Append(i % 3 == 0 ? (i * 0.25).ToString("R", CultureInfo.InvariantCulture) : (i * 7).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"CrmStandardData\":{\"Inventory\":{");
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(keys[i]).Append("\":").Append(1 + i % 3);
            }
            sb.Append("},\"AdsInventory\":{},\"CRMLimits\":{},\"FirstLaunch\":false}");
            List("KillSignatureAvatars", 1, 1512, sb);
            List("KillSignatureNewAvatars", 2, 1512, sb);
            List("KillSignatureBackgrounds", 1, 35, sb);
            List("KillSignatureSounds", 1, 5, sb);
            sb.Append(",\"Mercs\":[");
            for (int m = 1; m <= 21; m++)
            {
                if (m > 1) sb.Append(',');
                sb.Append("{\"Id\":\"").Append(m).Append("\",\"Locked\":false,\"Level\":30,\"Xp\":0,\"Displayed\":true");
                List("Skins", 1, 4, sb);
                List("Camos", 1, 210, sb);
                List("NewCamos", 2, 210, sb);
                List("Accessories", 1, 248, sb);
                sb.Append(",\"Mods\":[");
                for (int d = 0; d < 22; d++)
                {
                    if (d > 0) sb.Append(',');
                    sb.Append("{\"Id\":\"").Append(m * 100 + d).Append("\",\"Locked\":false}");
                }
                sb.Append("],\"GearSlots\":[{\"Unlocked\":true,\"GearId\":null},{\"Unlocked\":false,\"GearId\":\"g1\"}]}");
            }
            sb.Append("]},\"/League\":{\"@type\":\"Game.LeagueComponent\",\"LeagueDivisionId\":40,\"ClaimedRankUpRewards\":[1,2,3]}");
            sb.Append(",\"/Gears\":{\"@type\":\"Game.GearsComponent\",\"Gears\":[");
            for (int g = 0; sb.Length < target - 200; g++)
            {
                if (g > 0) sb.Append(',');
                sb.Append("{\"Id\":\"").Append(g).Append("\",\"TemplateId\":\"COGScope_Ass_Rifle_Rar1\",\"Level\":").Append(g % 10)
                  .Append(",\"Rarity\":2,\"SlotId\":\"0001\",\"New\":false,\"Stats\":{\"Accuracy\":1.5,\"Range\":2}}");
            }
            sb.Append("]}}}");
            return sb.ToString();
        }

        private static void List(string name, int from, int to, StringBuilder sb)
        {
            sb.Append(",\"").Append(name).Append("\":[");
            for (int i = from; i <= to; i++)
            {
                if (i > from) sb.Append(',');
                sb.Append('"').Append(i).Append('"');
            }
            sb.Append(']');
        }

        // ------------------------------------------------------------------------------------------------ edge cases

        private static string Pad(int n, string prefix = "p")
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(",\"").Append(prefix).Append(i).Append("\":").Append(i);
            return sb.ToString();
        }

        /// <summary>Documents for the edge cases; most objects have 8+ properties, so the patched reader indexes them.</summary>
        private static IEnumerable<KeyValuePair<string, string>> EdgeCases()
        {
            string p = Pad(9);
            yield return Case("one TName three ways", "{\"$default\":{\"a\":{\"x\":1},\".a\":{\"y\":2},\"'a'\":{\"z\":3}" + p + "}}");
            yield return Case("path merges", "{\"$default\":{\"a.b\":1,\"a.c\":2,\"a\":{\"d\":3},\"a.b\":4" + p + ",\"q.r.s\":5,\"q.r.t\":[1,2],\"q\":{\"r\":{\"u\":null}}}}");
            yield return Case("value objects", "{\"$default\":{\"v\":{\"@value\":3,\"x\":1,\"y\":{\"z\":2}},\"w\":{\"@value\":\"s\"}" + p + ",\"v.k\":7}}");
            yield return Case("local base", "{\"$base\":{\"a\":1,\"b\":{\"c\":2}" + p + "},\"$default\":{\"@base\":\"$base\",\"a\":3" + p + ",\"b\":{\"d\":4}}}");
            yield return Case("null merged with an object", "{\"$default\":{\"o\":{\"k\":null,\"m\":1" + p + "},\"o.k.z\":1,\"o.m\":2" + p.Replace("\"p", "\"r") + "}}");
            yield return Case("value merged with a value", "{\"$default\":{\"a\":1,\"a\":2" + p + "}}");
            yield return Case("value merged with an object", "{\"$default\":{\"a\":1,\"a.b\":2" + p + "}}");
            yield return Case("object merged with a value", "{\"$default\":{\"a\":{\"b\":1},\"a\":2" + p + "}}");
            yield return Case("array merged with an array", "{\"$default\":{\"a\":[1,2,3],\"a\":[4]" + p + "}}");
            yield return Case("array merged with an object", "{\"$default\":{\"a\":[1,{\"b\":2}],\"a\":{\"c\":1}" + p + "}}");
            yield return Case("attributes", "{\"$default\":{\"@type\":\"GameObject\",\"@layer\":\"x\",\"@type\":\"Other\",\"@sealed\":true" + p + ",\"@q\":{\"a\":1},\"@q\":{\"b\":2}}}");
            yield return Case("qualified names", "{\"$default\":{\"/c\":{\"a\":1},\"c\":2,\"/c\":{\"b\":2},\"'a.b'\":6,\"a.b\":7,\"'it''s'\":8,\"/'x y'\":{\"z\":1}" + p + "}}");
            yield return Case("an index name", "{\"$default\":{\"[3]\":4" + p + "}}");
            yield return Case("a quoted index name", "{\"$default\":{\"['x y']\":5,\"['x y']\":6" + p + "}}");
            yield return Case("empty and odd names", "{\"$default\":{\"\":1,\"''\":2" + p + "}}");
            yield return Case("a dot alone", "{\"$default\":{\".\":1" + p + "}}");
            yield return Case("a trailing dot", "{\"$default\":{\"a.\":1" + p + "}}");
            yield return Case("two dots", "{\"$default\":{\"a..b\":1" + p + "}}");
            yield return Case("an at sign alone", "{\"$default\":{\"@\":1" + p + "}}");
            yield return Case("an open bracket", "{\"$default\":{\"[\":1" + p + "}}");
            yield return Case("a quote", "{\"$default\":{\"'\":1" + p + "}}");
            yield return Case("a template name inside", "{\"$default\":{\"$t\":{\"a\":1}" + p + "}}");
            yield return Case("a type name", "{\"$default\":{\":T\":{\"a\":1}" + p + "}}");
            yield return Case("arrays", "{\"$default\":{\"e\":[],\"n\":[[1,2],[3,[4,5]],{\"a\":1}],\"v\":[{\"@value\":1,\"x\":2}],\"o\":{}" + p + "}}");
            yield return Case("big array of objects", "{\"$default\":{\"g\":[" + string.Join(",", Enumerable.Range(0, 300).Select(i => "{\"id\":" + i + Pad(8) + "}")) + "]}}");
            yield return Case("no default", "{\"$other\":{\"a\":1" + p + "}}");
            yield return Case("not an object", "{\"$default\":[1,2]}");
            yield return Case("bad version", "{\"@version\":\"9.9\",\"$default\":{}}");
            yield return Case("unknown directive", "{\"x\":1,\"$default\":{}}");
            yield return Case("not JSON", "{\"$default\":{\"a\":1" + p);
            yield return Case("small objects only", "{\"$default\":{\"a\":1,\"a\":2,\"b.c\":3,\"b\":{\"d\":4}}}");
            yield return Case("seven then eight", "{\"$default\":{" + Pad(7).Substring(1) + ",\"p0\":{\"x\":1}},\"$e\":{" + Pad(8).Substring(1) + ",\"p0\":{\"x\":1}}}");
        }

        private static KeyValuePair<string, string> Case(string name, string json)
        {
            return new KeyValuePair<string, string>(name, json);
        }

        /// <summary>
        /// Random documents: property names drawn from a pool of plain, quoted, dotted, attribute, index and child names that
        /// mostly name the same TName several times. Each simple name has one kind (object, value or array), so most documents
        /// are valid and their objects merge; a few (a value against an object through a path) throw.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, string>> RandomCases(int count, int seed)
        {
            var random = new Random(seed);
            for (int n = 0; n < count; n++)
            {
                var sb = new StringBuilder("{\"$default\":");
                Obj(sb, random, 0);
                sb.Append('}');
                yield return Case("random " + n, sb.ToString());
            }
        }

        private static readonly string[] ObjectNames = { "a", ".a", "'a'", "b", "/c", "c", "o", "o.p", "a.b", "'b'", "p", "o.p.q", "q" };
        private static readonly string[] ValueNames = { "v", "w", "@x", "'v'", "a.v", "o.p.v", "@y", "x", "b.w", "y", "z" };
        private static readonly string[] ArrayNames = { "l", "m", "a.l", "'l'" };

        private static void Obj(StringBuilder sb, Random random, int depth)
        {
            int count = random.Next(depth == 0 ? 8 : 0, depth == 0 ? 48 : (depth > 2 ? 6 : 20));
            sb.Append('{');
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                int kind = random.Next(depth > 3 ? 2 : 3);
                string[] pool = kind == 0 ? ValueNames : kind == 1 ? ArrayNames : ObjectNames;
                sb.Append('"').Append(pool[random.Next(pool.Length)]).Append("\":");
                if (kind == 0) Scalar(sb, random);
                else if (kind == 1) Array(sb, random, depth + 1);
                else Obj(sb, random, depth + 1);
            }
            sb.Append('}');
        }

        private static void Array(StringBuilder sb, Random random, int depth)
        {
            sb.Append('[');
            int items = random.Next(6);
            int kind = random.Next(3);
            for (int i = 0; i < items; i++)
            {
                if (i > 0) sb.Append(',');
                if (kind == 0 || depth > 3) Scalar(sb, random);
                else if (kind == 1) Obj(sb, random, depth + 1);
                else Array(sb, random, depth + 1);
            }
            sb.Append(']');
        }

        private static void Scalar(StringBuilder sb, Random random)
        {
            switch (random.Next(7))
            {
                case 0: sb.Append(random.Next(100)); break;
                case 1: sb.Append("\"s").Append(random.Next(10)).Append('"'); break;
                case 2: sb.Append("null"); break;
                case 3: sb.Append(random.Next(2) == 0 ? "true" : "false"); break;
                case 4: sb.Append((random.Next(1000) / 8.0).ToString("R", CultureInfo.InvariantCulture)); break;
                case 5: sb.Append("{\"@value\":").Append(random.Next(9)).Append(",\"k\":").Append(random.Next(3)).Append('}'); break;
                default: sb.Append(random.Next(1000000)).Append(".5"); break;
            }
        }

        // ------------------------------------------------------------------------------------------------ the game's reader, by reflection

        internal sealed class Reader
        {
            private readonly MethodInfo fromStream, readDefault;
            private readonly Type node, value, array;
            private readonly PropertyInfo name, parent, declaredBase, path, nameName, nameType, valueValue, valueType;
            private readonly MethodInfo nodes;
            private readonly FieldInfo annotations;

            public Reader(GameContext game)
            {
                Assembly templates = game.Get("Gameloft.Templates");
                Type reader = templates.GetType("Gameloft.Templates.TemplateReader", true);
                fromStream = reader.GetMethods().Single(m => m.Name == "FromStream" && m.GetParameters().Length == 3 &&
                                                             m.GetParameters()[0].ParameterType == typeof(Stream));
                readDefault = reader.GetMethod("ReadDefaultTemplate");
                node = templates.GetType(TemplatesPatch.NodeType, true);
                value = templates.GetType(TemplatesPatch.ValueType, true);
                array = templates.GetType(TemplatesPatch.ArrayType, true);
                name = node.GetProperty("Name");
                parent = node.GetProperty("Parent");
                declaredBase = node.GetProperty("DeclaredBase");
                path = node.GetProperty("Path");
                nodes = node.GetMethod("Nodes", Type.EmptyTypes);
                annotations = node.GetField("annotations", BindingFlags.Instance | BindingFlags.NonPublic);
                Type tname = templates.GetType(TemplatesPatch.NameType, true);
                nameName = tname.GetProperty("Name");
                nameType = tname.GetProperty("Type");
                valueValue = value.GetProperty("Value");
                valueType = value.GetProperty("ValueType");
            }

            public Assembly Templates { get { return node.Assembly; } }

            /// <summary>What GameProfileSection.GetTemplate does: FromStream(UTF-8 bytes, "DefaultProfile.tx").ReadDefaultTemplate().</summary>
            public object Read(string json)
            {
                object r = fromStream.Invoke(null, new object[] { new MemoryStream(Encoding.UTF8.GetBytes(json)), "DefaultProfile.tx", null });
                return readDefault.Invoke(r, null);
            }

            /// <summary>The tree as text, or the exception's type and message.</summary>
            public string Outcome(string json)
            {
                try
                {
                    return Describe(Read(json));
                }
                catch (TargetInvocationException ex)
                {
                    Exception inner = ex.InnerException;
                    return "throws " + inner.GetType().FullName + ": " + inner.Message;
                }
            }

            public string Describe(object root)
            {
                if (root == null) return "null";
                var sb = new StringBuilder();
                Describe(root, null, 0, sb);
                return sb.ToString();
            }

            private void Describe(object n, object expectedParent, int depth, StringBuilder sb)
            {
                object tname = name.GetValue(n);
                sb.Append(' ', depth).Append(n.GetType().Name).Append(' ').Append(nameType.GetValue(tname)).Append(':')
                  .Append((string)nameName.GetValue(tname) ?? "<null>");
                if (value.IsInstanceOfType(n))
                {
                    object v = valueValue.GetValue(n);
                    sb.Append(" = ").Append(valueType.GetValue(n)).Append(' ').Append(v == null ? "null" : v.GetType().Name + " " + Convert.ToString(v, CultureInfo.InvariantCulture));
                }
                if (!ReferenceEquals(parent.GetValue(n), expectedParent)) sb.Append(" WRONG PARENT");
                object b = declaredBase.GetValue(n);
                if (b != null) sb.Append(" base ").Append(path.GetValue(b));
                var ann = annotations.GetValue(n) as IDictionary;
                if (ann != null)
                {
                    var entries = new List<DictionaryEntry>();
                    IDictionaryEnumerator en = ann.GetEnumerator();
                    while (en.MoveNext()) entries.Add(en.Entry);
                    foreach (DictionaryEntry e in entries.OrderBy(e => (string)e.Key, StringComparer.Ordinal))
                        sb.Append(" [").Append(e.Key).Append('=').Append(e.Value).Append(']');
                }
                sb.Append('\n');
                foreach (object child in (IEnumerable)nodes.Invoke(n, null)) Describe(child, n, depth + 1, sb);
            }

            /// <summary>Every node of the tree, by reference.</summary>
            public HashSet<object> Nodes(object root)
            {
                var all = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var stack = new Stack<object>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    object n = stack.Pop();
                    all.Add(n);
                    foreach (object child in (IEnumerable)nodes.Invoke(n, null)) stack.Push(child);
                }
                return all;
            }

            public int Count(object n)
            {
                return (int)node.GetProperty("Count").GetValue(n);
            }

            public object Child(object n, int index)
            {
                return ((IEnumerable)nodes.Invoke(n, null)).Cast<object>().ElementAt(index);
            }
        }

        // ------------------------------------------------------------------------------------------------ equivalence

        [Fact]
        public void TheUnlockAllProfileGivesTheShippedTreeFasterOnBothBuilds()
        {
            string refs = Refs();
            if (refs == null) return;
            List<string> keys = UnlockKeys();
            output.WriteLine(keys.Count + " unlock keys from server/Content/unlocks.json");
            Assert.InRange(keys.Count, 11000, 12000);
            string profile = UnlockAllProfile(keys);
            output.WriteLine("profile: " + profile.Length + " chars");
            byte[] server = Server();
            var notes = new List<string>();
            Run(refs, RealGamePatchTests.PatchReal(refs, server, notes, new TemplatesPatch()), server, profile, keys.Count, "Android 1.17.32");
            foreach (string n in notes) output.WriteLine(n);
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] pcServer = PcHookServer.Compile();
            Run(pc, RealPc.Patch(pc, pcServer, null, BuildProfile.PcSteam6372049, new TemplatesPatch()), pcServer, profile, keys.Count, "Steam 1.17.30");
        }

        private void Run(string folder, Dictionary<string, byte[]> patched, byte[] server, string profile, int inventoryKeys, string build)
        {
            string shippedTree, patchedTree, latchedTree;
            double shippedMs, patchedMs, latchedMs;
            var lines = new List<string>();
            using (var game = new GameContext(folder, new Dictionary<string, byte[]>(), null))
            {
                var reader = new Reader(game);
                shippedMs = Best(() => reader.Read(profile), 3);
                shippedTree = reader.Describe(reader.Read(profile));
            }
            using (var game = new GameContext(folder, patched, server))
            {
                var reader = new Reader(game);
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameDataHooks", true);
                hooks.GetMethod("Configure").Invoke(null, new object[] { new Action<string>(l => { lock (lines) lines.Add(l); }) });
                patchedMs = Best(() => reader.Read(profile), 3);
                object bot1 = reader.Read(profile), bot2 = reader.Read(profile);
                patchedTree = reader.Describe(bot1);
                Assert.Equal(patchedTree, reader.Describe(bot2));
                // Two bots: no node in common, and a change to one leaves the other as it was.
                HashSet<object> a = reader.Nodes(bot1), b = reader.Nodes(bot2);
                Assert.True(a.Count > 40000, a.Count + " nodes");
                Assert.False(a.Overlaps(b), "two reads share a node");
                object first = reader.Child(bot1, 0);
                first.GetType().GetMethod("RemoveSelf").Invoke(first, null);
                Assert.NotEqual(patchedTree, reader.Describe(bot1));
                Assert.Equal(patchedTree, reader.Describe(bot2));
                Assert.Equal(5, (int)hooks.GetProperty("TemplateReads").GetValue(null));
                Assert.Equal(inventoryKeys, (int)hooks.GetProperty("TemplateLargestObject").GetValue(null));
                string status = (string)hooks.GetMethod("Describe").Invoke(null, null);
                output.WriteLine(build + " status: " + status.Substring(status.IndexOf("; templates:", StringComparison.Ordinal)));
                Assert.Contains("; templates: 5 read (", status);
            }
            using (var game = new GameContext(folder, patched, null))
            {
                var reader = new Reader(game);
                latchedMs = Best(() => reader.Read(profile), 3);
                latchedTree = reader.Describe(reader.Read(profile));
            }
            foreach (string l in lines.Take(3)) output.WriteLine(build + ": " + l);
            output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}: GetTemplate of the {1}-char unlock-all profile, best of 3: shipped {2:F1} ms, patched {3:F1} ms ({4:F1}x), patched without the server {5:F1} ms; tree {6} lines",
                build, profile.Length, shippedMs, patchedMs, shippedMs / patchedMs, latchedMs, shippedTree.Count(c => c == '\n')));
            Assert.Equal(shippedTree, patchedTree);
            Assert.Equal(shippedTree, latchedTree);
            Assert.DoesNotContain("WRONG PARENT", shippedTree);
            Assert.Contains(lines, l => l.StartsWith("templates: DefaultProfile.tx read in ", StringComparison.Ordinal) &&
                                        l.Contains("the largest " + inventoryKeys + " names"));
            // The k² term is gone: at least 4x on any machine (about 20x on the development box).
            Assert.True(patchedMs * 4 < shippedMs, "patched " + patchedMs + " ms vs shipped " + shippedMs + " ms");
            Assert.True(latchedMs * 4 < shippedMs, "patched without the server " + latchedMs + " ms vs shipped " + shippedMs + " ms");
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

        [Fact]
        public void EdgeCasesAndRandomDocumentsGiveTheShippedTreesOrExceptionsOnBothBuilds()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            List<KeyValuePair<string, string>> cases = EdgeCases().Concat(RandomCases(400, 32)).ToList();
            Compare(refs, RealGamePatchTests.PatchReal(refs, server, null, new TemplatesPatch()), server, cases, "Android");
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] pcServer = PcHookServer.Compile();
            Compare(pc, RealPc.Patch(pc, pcServer, null, BuildProfile.PcSteam6372049, new TemplatesPatch()), pcServer, cases, "Steam");
        }

        private void Compare(string folder, Dictionary<string, byte[]> patched, byte[] server, List<KeyValuePair<string, string>> cases, string build)
        {
            List<string> shipped, withServer, latched;
            using (var game = new GameContext(folder, new Dictionary<string, byte[]>(), null))
                shipped = cases.Select(c => new Reader(game).Outcome(c.Value)).ToList();
            int indexed;
            using (var game = new GameContext(folder, patched, server))
            {
                var reader = new Reader(game);
                withServer = cases.Select(c => reader.Outcome(c.Value)).ToList();
                indexed = (int)game.Get("McvsOffline.Server").GetType("McvsOffline.GameDataHooks", true).GetProperty("TemplateObjectsIndexed").GetValue(null);
            }
            using (var game = new GameContext(folder, patched, null))
            {
                var reader = new Reader(game);
                latched = cases.Select(c => reader.Outcome(c.Value)).ToList();
            }
            int throws = shipped.Count(s => s.StartsWith("throws ", StringComparison.Ordinal));
            output.WriteLine(build + ": " + cases.Count + " documents, " + throws + " of them throw; " + indexed + " objects indexed by the patched reader");
            foreach (var group in shipped.Where(s => s.StartsWith("throws ", StringComparison.Ordinal)).GroupBy(s => s.Length > 140 ? s.Substring(0, 140) : s).OrderByDescending(g => g.Count()).Take(12))
                output.WriteLine("  " + group.Count() + " x " + group.Key);
            for (int i = 0; i < cases.Count; i++)
            {
                Assert.True(shipped[i] == withServer[i], cases[i].Key + ": " + cases[i].Value + "\nshipped:\n" + shipped[i] + "\npatched:\n" + withServer[i]);
                Assert.True(shipped[i] == latched[i], cases[i].Key + ": " + cases[i].Value + "\nshipped:\n" + shipped[i] + "\nwithout the server:\n" + latched[i]);
                Assert.DoesNotContain("WRONG PARENT", shipped[i]);
            }
            Assert.True(indexed > 300, indexed + " objects indexed");
            Assert.True(throws >= 10 && throws <= cases.Count / 3, throws + " of " + cases.Count + " throw");
        }

        [Fact]
        public void FromObjectIsUnchangedAndReportsItsTime()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new TemplatesPatch());
            List<string> keys = UnlockKeys();
            var inventory = new Dictionary<string, int>();
            for (int i = 0; i < keys.Count; i++) inventory[keys[i]] = 1 + i % 3;
            string shipped, withServer, latched, status;
            using (var game = new GameContext(refs, new Dictionary<string, byte[]>(), null))
                shipped = FromObject(game, inventory);
            using (var game = new GameContext(refs, patched, server))
            {
                withServer = FromObject(game, inventory);
                status = (string)game.Get("McvsOffline.Server").GetType("McvsOffline.GameDataHooks", true).GetMethod("Describe").Invoke(null, null);
            }
            using (var game = new GameContext(refs, patched, null))
                latched = FromObject(game, inventory);
            output.WriteLine(status.Substring(status.IndexOf("; templates:", StringComparison.Ordinal)));
            Assert.Equal(shipped, withServer);
            Assert.Equal(shipped, latched);
            Assert.Contains("FromObject 1 (", status);
        }

        private static string FromObject(GameContext game, object instance)
        {
            var reader = new Reader(game);
            MethodInfo fromObject = reader.Templates.GetType(TemplatesPatch.ObjectType, true).GetMethod("FromObject", new[] { typeof(object) });
            return reader.Describe(fromObject.Invoke(null, new[] { instance }));
        }
    }
}
