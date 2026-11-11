using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
    /// The build-30 hitch patches on the player's REAL assemblies (the owner's S22 log of one bot match), each executed on net8
    /// next to the shipped code on identical input, with the real <c>McvsOffline.GameDataHooks</c> (hosts/embedded/GameDataHooks.cs)
    /// as the server:
    /// <list type="bullet">
    /// <item>'crmtables' (Gameloft.Engine <c>Database.Changer</c>): a <c>Database</c> fed the same sequence of changers (the same
    /// cells again as a new object, other cells, back, no cells, null, ADD and MUL cells among them) has the same tables and raises
    /// <c>TablesLoaded</c> as often after every step as the shipped one, and reads its file only when the cells changed. The
    /// engine's file read (<c>AssetStream.ReadAllText</c>, native GlfStream) is replaced in BOTH copies by a counted
    /// <c>File.ReadAllText</c> of a test database.</item>
    /// <item>'jsonnorm' (Gameloft.Util <c>Json.NormalizeJsonString</c>): two profile saves of an unlock-all-size profile give the
    /// shipped results string for string, and the game's normalization runs 3 times instead of 8.</item>
    /// </list>
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1 (Steam: MCVS_REQUIRE_PC=1).
    /// </summary>
    [Collection("GameHooks")]
    public class RealHitchPatchTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ITestOutputHelper output;

        public RealHitchPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Gameloft.Engine.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Gameloft.Engine.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        /// <summary>The real data hooks as McvsOffline.Server (the file is System-only on purpose).</summary>
        private static byte[] Server()
        {
            return FixtureCompiler.Compile("McvsOffline.Server", FixtureCompiler.RepoSource("hosts", "embedded", "GameDataHooks.cs"));
        }

        private static MethodDefinition Setter(ModuleDefinition module)
        {
            return module.GetType(CrmTablesPatch.TypeName).Methods.Single(m => m.Name == "set_Changer");
        }

        private static MethodDefinition Normalize(ModuleDefinition module)
        {
            return module.GetType(JsonNormPatch.TypeName).Methods.Single(m => m.Name == "NormalizeJsonString");
        }

        [Fact]
        public void TheSitesHaveTheVerifiedFingerprintsOnBothBuilds()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var engine = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Engine.dll")))
            using (var util = ModuleDefinition.ReadModule(Path.Combine(refs, "Gameloft.Util.dll")))
            {
                output.WriteLine("Android set_Changer " + IlPatch.Fingerprint(Setter(engine)) + ", NormalizeJsonString " + IlPatch.Fingerprint(Normalize(util)));
                Assert.Equal(CrmTablesPatch.SetterFingerprint, IlPatch.Fingerprint(Setter(engine)));
                Assert.Equal(JsonNormPatch.NormalizeFingerprint, IlPatch.Fingerprint(Normalize(util)));
            }
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            ReaderParameters reader = RealPc.Reader(pc, null);
            using (reader.AssemblyResolver)
            using (var engine = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Gameloft.Engine.dll"))), reader))
            using (var util = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Gameloft.Util.dll"))), reader))
            {
                output.WriteLine("Steam set_Changer " + IlPatch.Fingerprint(Setter(engine)) + ", NormalizeJsonString " + IlPatch.Fingerprint(Normalize(util)));
                Assert.Equal(CrmTablesPatch.PcSetterFingerprint, IlPatch.Fingerprint(Setter(engine)));
                Assert.Equal(JsonNormPatch.PcNormalizeFingerprint, IlPatch.Fingerprint(Normalize(util)));
            }
        }

        [Fact]
        public void BothPatchesApplyToTheSteamBuild()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var notes = new List<string>();
            Dictionary<string, byte[]> steam = RealPc.Patch(pc, PcHookServer.Compile(), notes, BuildProfile.PcSteam6372049, new CrmTablesPatch(), new JsonNormPatch());
            Assert.Contains("Gameloft.Engine.dll", steam.Keys);
            Assert.Contains("Gameloft.Util.dll", steam.Keys);
            Assert.Contains(notes, n => n.Contains("set_Changer"));
            Assert.Contains(notes, n => n.Contains("NormalizeJsonString"));
        }

        // ------------------------------------------------------------------------------------------------ crmtables

        /// <summary>The test database: two tables, numbers (long and double), strings, a null cell, a duplicated key.</summary>
        private const string BaseDatabase = @"{
  ""Weapons"": { ""Fields"": [""Id"", ""Damage"", ""Name"", ""Note""],
                 ""Rows"": [[""rifle"", 10, ""Rifle"", null], [""pistol"", 4.5, ""Pistol"", ""x""], [""smg"", 7, ""SMG"", null], [""rifle"", 99, ""Dup"", null]] },
  ""Bots"":    { ""Fields"": [""Name""], ""Rows"": [[""Ace""], [""Bruno""], [""Kim""]] }
}";

        private const string Cells1 = @"{ ""Weapons"": { ""Id@Damage"": { ""rifle"": ""+5"", ""pistol"": ""*2"", ""smg"": ""12"" }, ""Id@Note"": { ""smg"": ""hot"" } },
  ""Bots"": { ""Name@Name"": { ""Bruno"": ""Flank Sinatra"" } }, ""Missing"": { ""Id@X"": { ""a"": 1 } } }";

        /// <summary>Cells1 with one value changed.</summary>
        private const string Cells2 = @"{ ""Weapons"": { ""Id@Damage"": { ""rifle"": ""+6"", ""pistol"": ""*2"", ""smg"": ""12"" }, ""Id@Note"": { ""smg"": ""hot"" } },
  ""Bots"": { ""Name@Name"": { ""Bruno"": ""Flank Sinatra"" } }, ""Missing"": { ""Id@X"": { ""a"": 1 } } }";

        /// <summary>One step: the changer JSON to assign (null: assign null).</summary>
        private static readonly string[] Steps =
        {
            Cells1,   // null -> cells: applied in place
            Cells1,   // the same cells, a new object: the shipped setter reloads, the patched one keeps
            Cells1,
            Cells2,   // other cells: both reload
            Cells2,
            Cells1,
            "{}",     // no cells: back to the file's rows (both reload)
            "{}",     // no cells to no cells: nothing
            Cells1,
            null,     // null: the file's rows
            Cells1,   // null -> cells again: in place
            Cells1,
        };

        private sealed class EngineRun
        {
            public List<string> Snapshots = new List<string>();
            public List<int> Events = new List<int>();
            public int Reads;
        }

        [Fact]
        public void TheSameCellsKeepTheTablesAndRaiseTablesLoadedLikeAReload()
        {
            string refs = Refs();
            if (refs == null) return;
            string dir = TestEnvironment.NewTempDir("crmtables");
            string file = Path.Combine(dir, "MCVS_Test.xlsx");
            File.WriteAllText(file, BaseDatabase);

            byte[] server = Server();
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, notes, new CrmTablesPatch());
            foreach (string n in notes) output.WriteLine(n);
            byte[] shipped = File.ReadAllBytes(Path.Combine(refs, "Gameloft.Engine.dll"));

            EngineRun original = Run(refs, Instrument(refs, shipped), null, file, null);
            var lines = new List<string>();
            EngineRun withHooks = Run(refs, Instrument(refs, patched["Gameloft.Engine.dll"]), server, file, lines);
            EngineRun noServer = Run(refs, Instrument(refs, patched["Gameloft.Engine.dll"]), null, file, null);
            foreach (string l in lines) output.WriteLine(l);

            for (int i = 0; i < Steps.Length; i++)
            {
                Assert.True(original.Snapshots[i] == withHooks.Snapshots[i], "step " + i + ":\n" + original.Snapshots[i] + "\nvs\n" + withHooks.Snapshots[i]);
                Assert.Equal(original.Events[i], withHooks.Events[i]);
                Assert.Equal(original.Snapshots[i], noServer.Snapshots[i]);
                Assert.Equal(original.Events[i], noServer.Events[i]);
            }
            // The ADD cell was applied once, the MUL cell once, a SET cell and a renamed bot are there (step 1 = same cells again).
            Assert.Contains("rifle|Double:15|Rifle|null|", original.Snapshots[1]);
            Assert.Contains("pistol|Double:9|Pistol|x|", original.Snapshots[1]);
            Assert.Contains("smg|Double:12|SMG|hot|", original.Snapshots[1]);
            Assert.Contains("rifle|Int64:99|Dup|null|", original.Snapshots[1]);
            Assert.Contains("Flank Sinatra|", original.Snapshots[1]);
            Assert.Contains("rifle|Int64:10|Rifle|null|", original.Snapshots[9]);   // null: the file's rows
            output.WriteLine("file reads: shipped " + original.Reads + ", patched " + withHooks.Reads + ", patched without the server " + noServer.Reads);
            // Shipped: the first load + 9 reloads (steps 1-6, 8, 9, 11); patched: the four "same cells again" steps (1, 2, 4, 11)
            // keep the tables. Without McvsOffline.Server.dll the patched setter is the game's.
            Assert.Equal(1 + 9, original.Reads);
            Assert.Equal(original.Reads - 4, withHooks.Reads);
            Assert.Equal(original.Reads, noServer.Reads);
            Assert.Equal(4, lines.Count(l => l.StartsWith("crm tables: " + file + " got the same CRM cells again (3 tables, 6 cells): kept, no reload from disk",
                                                          StringComparison.Ordinal)));
            Assert.Equal(2, lines.Count(l => l.Contains("got other CRM cells")));
            Assert.Equal(6, lines.Count);
        }

        /// <summary>
        /// Runs <see cref="Steps"/> on a fresh Database of <paramref name="engine"/> (a GameContext of its own) and records, after
        /// each step, every table's fields and rows (type and value of every cell) and the TablesLoaded count.
        /// </summary>
        private EngineRun Run(string refs, byte[] engine, byte[] server, string file, List<string> lines)
        {
            var run = new EngineRun();
            using (var game = new GameContext(refs, new Dictionary<string, byte[]> { ["Gameloft.Engine.dll"] = engine }, server))
            {
                Assembly gameEngine = game.Get("Gameloft.Engine");
                if (server != null)
                {
                    Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameDataHooks", true);
                    hooks.GetMethod("Configure").Invoke(null, new object[] { new Action<string>(l => { lock (lines) lines.Add(l); }) });
                }
                Type databaseType = gameEngine.GetType("Gameloft.Engine.Database", true);
                Type tableType = gameEngine.GetType("Gameloft.Engine.Database+Table", true);
                Type changerType = gameEngine.GetType("Gameloft.Engine.CRMDatabaseChanger", true);
                Type jobject = game.Get("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JObject", true);
                MethodInfo parse = jobject.GetMethod("Parse", new[] { typeof(string) });
                ConstructorInfo newChanger = changerType.GetConstructor(new[] { jobject });
                Type assetStream = gameEngine.GetType("Gameloft.Engine.AssetStream", true);

                object database = RuntimeHelpers.GetUninitializedObject(databaseType);
                databaseType.GetProperty("Tables", All).SetValue(database, Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tableType)));
                databaseType.GetField("file", All).SetValue(database, file);
                int events = 0;
                databaseType.GetEvent("TablesLoaded").AddEventHandler(database, new EventHandler((s, e) => events++));
                databaseType.GetMethod("Load", All).Invoke(database, new object[] { file });
                PropertyInfo changer = databaseType.GetProperty("Changer");
                foreach (string step in Steps)
                {
                    events = 0;
                    object value = step == null ? null : newChanger.Invoke(new[] { parse.Invoke(null, new object[] { step }) });
                    changer.SetValue(database, value);
                    run.Events.Add(events);
                    run.Snapshots.Add(Snapshot((IDictionary)databaseType.GetProperty("Tables", All).GetValue(database), tableType));
                }
                run.Reads = (int)assetStream.GetField(ReadsField, All).GetValue(null);
            }
            return run;
        }

        private static string Snapshot(IDictionary tables, Type tableType)
        {
            var sb = new StringBuilder();
            foreach (string name in tables.Keys.Cast<string>().OrderBy(n => n, StringComparer.Ordinal))
            {
                object table = tables[name];
                sb.Append('[').Append(name).Append("] ").Append(string.Join(",", (string[])tableType.GetField("Fields").GetValue(table))).Append('\n');
                foreach (object[] row in (object[][])tableType.GetField("Rows").GetValue(table))
                {
                    foreach (object cell in row)
                        sb.Append(cell == null ? "null" : cell.GetType().Name + ":" + Convert.ToString(cell, CultureInfo.InvariantCulture)).Append('|');
                    sb.Append('\n');
                }
            }
            return sb.ToString().Replace("String:", "").Replace("|\n", "|\n");
        }

        private const string ReadsField = "McvsTestReads";

        /// <summary>
        /// Test instrumentation of a Gameloft.Engine image: <c>AssetStream.ReadAllText(path, needsBuildOnWin)</c> (the native
        /// GlfStream) becomes <c>McvsTestReads++; return File.ReadAllText(path);</c>.
        /// </summary>
        private static byte[] Instrument(string refs, byte[] image)
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                using (var module = ModuleDefinition.ReadModule(new MemoryStream(image), new ReaderParameters { AssemblyResolver = resolver, InMemory = true }))
                {
                    TypeDefinition asset = module.GetType("Gameloft.Engine.AssetStream");
                    MethodDefinition read = asset.Methods.Single(m => m.Name == "ReadAllText" && m.Parameters.Count == 2);
                    var reads = new FieldDefinition(ReadsField, Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
                    asset.Fields.Add(reads);
                    var fileType = new TypeReference("System.IO", "File", module, module.TypeSystem.CoreLibrary);
                    var readAll = new MethodReference("ReadAllText", module.TypeSystem.String, fileType);
                    readAll.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                    read.Body = new Mono.Cecil.Cil.MethodBody(read);
                    ILProcessor il = read.Body.GetILProcessor();
                    il.Emit(OpCodes.Ldsfld, reads);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stsfld, reads);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, module.ImportReference(readAll));
                    il.Emit(OpCodes.Ret);
                    var stream = new MemoryStream();
                    module.Write(stream);
                    return stream.ToArray();
                }
            }
        }

        [Fact]
        public void TheCrmTablesIlIsTheVerifiedChange()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), null, new CrmTablesPatch());
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Engine.dll"])))
            {
                var code = Setter(module).Body.Instructions;
                Assert.Equal(OpCodes.Ldarg_0, code[0].OpCode);
                Assert.True(code[4].OpCode == OpCodes.Call && ((MethodReference)code[4].Operand).FullName ==
                            "System.Boolean Gameloft.Engine.McvsCrmTables::Same(System.Object,System.Object,System.Object)");
                Assert.Contains(code, i => i.OpCode == OpCodes.Ldfld && ((FieldReference)i.Operand).Name == "TablesLoaded");
                Assert.Contains(code, i => i.OpCode == OpCodes.Callvirt && ((MethodReference)i.Operand).FullName ==
                                           "System.Void System.EventHandler::Invoke(System.Object,System.EventArgs)");
                // The game's setter follows unchanged, still with its Load(File) and OnTableLoaded calls.
                Assert.Contains(code, i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "Load");
                TypeDefinition helper = module.GetType("Gameloft.Engine.McvsCrmTables");
                Assert.NotNull(helper.Fields.Single(f => f.Name == "hooksMissing"));
                MethodDefinition forward = helper.Methods.Single(m => m.Name == "Forward");
                Assert.Contains(forward.Body.Instructions, i => i.OpCode == OpCodes.Call &&
                    ((MethodReference)i.Operand).FullName == "System.Boolean McvsOffline.GameDataHooks::SameCrmChanges(System.Object,System.Object,System.Object)");
            }
            // A second run on the patched file is refused before anything changes.
            string dir = TestEnvironment.NewTempDir("crmtables-twice");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(dir, "Gameloft.Engine.dll"), patched["Gameloft.Engine.dll"]);
            Assert.Contains("already patched", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, Server(), null, new CrmTablesPatch())).Message);
        }

        // ------------------------------------------------------------------------------------------------ jsonnorm

        /// <summary>An unlock-all-like profile: ~600 KB, keys out of order, nested objects and arrays, numbers, non-ASCII.</summary>
        private static string Profile(int seed, int bump)
        {
            var random = new Random(seed);
            var sb = new StringBuilder("{");
            sb.Append("\"xp\":").Append(1000 + bump).Append(',');
            sb.Append("\"inventory\":{");
            for (int i = 0; i < 11368; i++)
            {
                if (i > 0) sb.Append(',');
                int id = random.Next(1000000);
                sb.Append("\"item_").Append(id.ToString("x6")).Append('_').Append(i).Append("\":{\"c\":").Append(random.Next(5))
                  .Append(",\"new\":").Append(random.Next(2) == 0 ? "false" : "true").Append('}');
            }
            sb.Append("},\"name\":\"Pão de Queijo\",\"loadouts\":[{\"z\":1,\"a\":[3,2,1]},{\"b\":{\"y\":2.5,\"x\":null}}],\"agent\":\"merc").Append(seed).Append("\"}");
            return sb.ToString();
        }

        [Fact]
        public void TwoProfileSavesGiveTheShippedResultsWithThreeNormalizationsInsteadOfEight()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, notes, new JsonNormPatch());
            foreach (string n in notes) output.WriteLine(n);

            string saved = Profile(1, 0);              // what the last save sent
            string current = Profile(1, 1);            // the match changed the XP
            string next = Profile(1, 2);               // the next match
            Assert.InRange(saved.Length, 400000, 900000);
            // IsModified twice per save: (saved, current) and again; after the save the saved text is the one sent.
            string[] calls = { saved, current, saved, current, current, next, current, next };
            string[] others = { "", null, "{\"b\":1,\"a\":2}", "[3,1]", "null", "{\"b\":1,\"a\":2}" };

            var lines = new List<string>();
            List<string> shippedResults, patchedResults, latchedResults;
            string broken = saved.Substring(0, saved.Length - 10);
            string shippedError;
            using (var game = new GameContext(refs, new Dictionary<string, byte[]>(), null))
            {
                shippedResults = NormalizeAll(game, calls.Concat(others));
                shippedError = Error(game, broken);
            }
            using (var game = new GameContext(refs, patched, server))
            {
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameDataHooks", true);
                hooks.GetMethod("Configure").Invoke(null, new object[] { new Action<string>(l => { lock (lines) lines.Add(l); }) });
                patchedResults = NormalizeAll(game, calls.Concat(others));
                Assert.Equal(3, hooks.GetProperty("NormalizeMisses").GetValue(null));
                Assert.Equal(5, hooks.GetProperty("NormalizeHits").GetValue(null));

                // Not JSON: the game's exception, every time (nothing is kept for it).
                Assert.NotNull(shippedError);
                Assert.Equal(shippedError, Error(game, broken));
                Assert.Equal(shippedError, Error(game, broken));
                Assert.Equal(3, hooks.GetProperty("NormalizeMisses").GetValue(null));
            }
            using (var game = new GameContext(refs, patched, null))
                latchedResults = NormalizeAll(game, calls.Concat(others));
            foreach (string l in lines) output.WriteLine(l);

            Assert.Equal(shippedResults, patchedResults);
            Assert.Equal(shippedResults, latchedResults);
            Assert.NotEqual(shippedResults[0], shippedResults[1]);     // IsModified: true
            Assert.Equal(shippedResults[1], shippedResults[4]);
            Assert.Equal(3, lines.Count(l => l.StartsWith("json normalize: ", StringComparison.Ordinal)));
            Assert.Contains(lines, l => l.Contains(" chars in ") && l.Contains("(3 large ones from the cache since the last line)"));
        }

        /// <summary>The exception type and message NormalizeJsonString throws for <paramref name="input"/>, or null.</summary>
        private static string Error(GameContext game, string input)
        {
            try
            {
                NormalizeAll(game, new[] { input });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message;
            }
        }

        private static List<string> NormalizeAll(GameContext game, IEnumerable<string> inputs)
        {
            MethodInfo normalize = game.Get("Gameloft.Util").GetType(JsonNormPatch.TypeName, true).GetMethod("NormalizeJsonString");
            return inputs.Select(s => (string)normalize.Invoke(null, new object[] { s })).ToList();
        }

        [Fact]
        public void TheJsonNormIlIsTheVerifiedChange()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, Server(), null, new JsonNormPatch());
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Gameloft.Util.dll"])))
            {
                var code = Normalize(module).Body.Instructions;
                Assert.Equal("System.String Gameloft.Util.McvsJsonNorm::Cached(System.String)", ((MethodReference)code[1].Operand).FullName);
                Assert.Equal(OpCodes.Ret, code[code.Count - 1].OpCode);
                Assert.Equal("System.String Gameloft.Util.McvsJsonNorm::Remember(System.String,System.String)", ((MethodReference)code[code.Count - 2].Operand).FullName);
                Assert.Equal(OpCodes.Ldarg_0, code[code.Count - 3].OpCode);
                Assert.Equal("ToCompactString", ((MethodReference)code[code.Count - 4].Operand).Name);
                Assert.Contains(module.AssemblyReferences, r => r.Name == "McvsOffline.Server");
            }
        }

        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string refs = Refs();
            if (refs == null) return;
            RealAdminPagePatchTests.AssertIlVerifyClean(output, refs, "Gameloft.Engine.dll", new IlPatch[] { new CrmTablesPatch() },
                                                        new[] { @"\]Gameloft\.Engine\.Database\.set_Changer$" }, 1,
                                                        "Database::set_Changer", "McvsCrmTables::Same", "McvsCrmTables::Forward");
            RealAdminPagePatchTests.AssertIlVerifyClean(output, refs, "Gameloft.Util.dll", new IlPatch[] { new JsonNormPatch() },
                                                        new[] { @"\]Gameloft\.Util\.Json\.NormalizeJsonString$" }, 1,
                                                        "Json::NormalizeJsonString", "McvsJsonNorm::Cached", "McvsJsonNorm::Remember");
        }
    }
}
