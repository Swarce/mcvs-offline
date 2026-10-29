using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using McvsOffline.Patcher.Pc;
using McvsOffline.Server.Core.Content;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The <c>gameload</c> patch (WR2 F1) on the real Steam files: which redirected assembly the engine loads by bare name (only
    /// Game), what the stock and the patched <c>ComponentAssemblies.GetAssembly</c> ask the loader for (executed on net8: the
    /// load context records the requested name), and the refusal when Game.dll does not have the identity the patch bakes in.
    /// Fingerprint, exact IL, second application and ILVerify are RealPcPatchTests'. Skipped without MCVS_PC_REFS, a failure under
    /// MCVS_REQUIRE_PC=1.
    /// </summary>
    public class RealPcGameLoadTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealPcGameLoadTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>The simple names of everything the PC install redirects (the four patched files and the server).</summary>
        private static List<string> Redirected()
        {
            return GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }).Select(Path.GetFileNameWithoutExtension).ToList();
        }

        /// <summary>
        /// Config/Assemblies.mjson of the Steam data pack names the assemblies ComponentAssemblies loads by bare name (gameDlls and
        /// managedDlls; engineDlls are editor-only and never loaded): of the redirected ones that is Game alone, which is exactly
        /// the patch's table.
        /// </summary>
        [Fact]
        public void TheEngineLoadsOnlyGameOfTheRedirectedAssembliesByBareName()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            string mjson = Path.Combine(pc, "Data", DataPack.DecimalPath("Config/Assemblies.mjson"));
            Assert.True(File.Exists(mjson), mjson + " (Config/Assemblies.mjson) is missing");
            var loaded = new List<string>();
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(mjson)))
            {
                foreach (string list in new[] { "gameDlls", "managedDlls" })
                {
                    JsonElement array;
                    if (!doc.RootElement.TryGetProperty(list, out array)) continue;
                    foreach (JsonElement entry in array.EnumerateArray())
                    {
                        string dll = entry.GetProperty("dllName").GetString();
                        loaded.Add(dll.EndsWith(".dll", StringComparison.Ordinal) ? dll.Substring(0, dll.Length - 4) : dll);
                    }
                }
            }
            output.WriteLine("Assemblies.mjson loads: " + string.Join(", ", loaded));
            Assert.Equal(new[] { "Game", "NicePlaySDK" }, loaded.OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(GameAssemblyLoadPatch.QualifiedNames.Select(n => n.Key), loaded.Intersect(Redirected()));
        }

        /// <summary>
        /// Why the four other redirected assemblies did come from McvsOffline\bin in WR2: each is bound through a full static
        /// reference (EngineBridge → Gameloft.Engine → Gameloft.Util, with their file versions), while nothing in the game folder
        /// references Game statically; and ComponentAssemblies.GetAssembly is the only Assembly.Load by name in the game's own
        /// assemblies (Gameloft.Sequences loads raw bytes). The one other bind by name is Newtonsoft.Json's
        /// DefaultSerializationBinder (LoadWithPartialName for a "$type" assembly name): a partial bind that the config's
        /// qualifyAssembly entries turn into a full one, so it too lands on our copies.
        /// </summary>
        [Fact]
        public void TheOtherRedirectedAssembliesAreStaticReferencesAndGetAssemblyIsTheOnlyLoadByName()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var versions = new Dictionary<string, Version>(StringComparer.Ordinal);
            foreach (string file in RealPc.PatchedFiles) versions[Path.GetFileNameWithoutExtension(file)] = GameFolder.ReadAssemblyVersion(File.ReadAllBytes(Path.Combine(pc, file)));
            var loads = new List<string>();
            var referencesToGame = new List<string>();
            foreach (string path in RealPc.ManagedAssemblies(pc))
            {
                using (ModuleDefinition module = ModuleDefinition.ReadModule(path))
                {
                    foreach (AssemblyNameReference r in module.AssemblyReferences)
                    {
                        if (r.Name == "Game") referencesToGame.Add(module.Name);
                        Version expected;
                        if (versions.TryGetValue(r.Name, out expected)) Assert.True(expected == r.Version, module.Name + " references " + r.FullName + ", the file is " + expected);
                    }
                    foreach (MethodDefinition m in RealPc.AllTypes(module.Types).SelectMany(t => t.Methods).Where(m => m.HasBody))
                    {
                        foreach (Instruction i in m.Body.Instructions)
                        {
                            var callee = i.Operand as MethodReference;
                            if (callee == null || (i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt)) continue;
                            bool byName = callee.DeclaringType.FullName == "System.Reflection.Assembly" &&
                                          (callee.Name == "Load" || callee.Name == "LoadWithPartialName") &&
                                          callee.Parameters.Count >= 1 && callee.Parameters[0].ParameterType.FullName != "System.Byte[]";
                            byName |= callee.DeclaringType.FullName == "System.AppDomain" && callee.Name == "Load" &&
                                      callee.Parameters[0].ParameterType.FullName != "System.Byte[]";
                            if (byName) loads.Add(module.Name + " " + m.FullName + ": " + callee.FullName);
                        }
                    }
                }
            }
            foreach (string l in loads) output.WriteLine(l);
            Assert.Empty(referencesToGame);
            Assert.Equal(new[]
            {
                "Gameloft.Engine.dll System.Reflection.Assembly Gameloft.Engine.ComponentAssemblies::GetAssembly(System.String,System.String): " +
                "System.Reflection.Assembly System.Reflection.Assembly::Load(System.Reflection.AssemblyName)",
                "Newtonsoft.Json.dll System.Type Newtonsoft.Json.Serialization.DefaultSerializationBinder::GetTypeFromTypeNameKey(" +
                "Newtonsoft.Json.Serialization.DefaultSerializationBinder/TypeNameKey): System.Reflection.Assembly System.Reflection.Assembly::LoadWithPartialName(System.String)",
            }, loads);
            using (ModuleDefinition bridge = ModuleDefinition.ReadModule(Path.Combine(pc, "EngineBridge.dll")))
                Assert.Contains(bridge.AssemblyReferences, r => r.Name == "Gameloft.Engine");
            using (ModuleDefinition engine = ModuleDefinition.ReadModule(Path.Combine(pc, "Gameloft.Engine.dll")))
                Assert.Contains(engine.AssemblyReferences, r => r.Name == "Gameloft.Util");
        }

        /// <summary>
        /// Executed: the stock GetAssembly asks the loader for the bare name "Game" (no version: the WR2 bind), the patched one for
        /// "Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null" and gets Game; a name outside the table (NicePlaySDK, absent
        /// on PC) still goes bare and still yields null; the cache key stays the bare name.
        /// </summary>
        [Fact]
        public void ThePatchedEngineAsksTheLoaderForGamesFullName()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();

            AssemblyName stock = Requested(pc, new Dictionary<string, byte[]>(), server, "Game", out Assembly stockGame, out _);
            output.WriteLine("stock: " + stock.FullName);
            Assert.Equal("Game", stock.Name);
            Assert.Null(stock.Version);   // a partial name: no application-config policy on .NET Framework
            Assert.NotNull(stockGame);

            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new GameAssemblyLoadPatch());
            AssemblyName full = Requested(pc, patched, server, "Game", out Assembly game, out IDictionary<string, Assembly> cache);
            output.WriteLine("patched: " + full.FullName);
            Assert.Equal("Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null", full.FullName);
            Assert.Equal(new Version(0, 0, 0, 0), full.Version);
            Assert.Equal("Game", game.GetName().Name);
            Assert.Same(game, cache["Game"]);

            AssemblyName other = Requested(pc, patched, server, "NicePlaySDK", out Assembly none, out cache);
            Assert.Equal("NicePlaySDK", other.Name);
            Assert.Null(other.Version);
            Assert.Null(none);
            Assert.True(cache.ContainsKey("NicePlaySDK"));
        }

        /// <summary>The helper itself: the table's names qualified, anything else (null included) returned as it came.</summary>
        [Fact]
        public void QualifyMapsTheTableAndPassesEverythingElse()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] server = PcHookServer.Compile();
            Dictionary<string, byte[]> patched = RealPc.Patch(pc, server, null, Pc, new GameAssemblyLoadPatch());
            using (var context = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                MethodInfo qualify = context.Get("Gameloft.Engine").GetType(GameAssemblyLoadPatch.HelperNamespace + "." + GameAssemblyLoadPatch.HelperType, true)
                                            .GetMethod(GameAssemblyLoadPatch.QualifyMethod, All);
                foreach (KeyValuePair<string, string> name in GameAssemblyLoadPatch.QualifiedNames)
                    Assert.Equal(name.Value, qualify.Invoke(null, new object[] { name.Key }));
                Assert.Equal("game", qualify.Invoke(null, new object[] { "game" }));   // ordinal, as Assemblies.mjson spells it
                Assert.Equal("Gameloft.Engine", qualify.Invoke(null, new object[] { "Gameloft.Engine" }));
                Assert.Null(qualify.Invoke(null, new object[] { null }));
            }
        }

        /// <summary>A Game.dll with another identity is refused before the engine changes (the table would point the load at nothing).</summary>
        [Fact]
        public void AGameDllWithAnotherIdentityIsRefusedBeforeAnyChange()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] otherGame;
            ReaderParameters original = RealPc.Reader(pc, null);
            using (original.AssemblyResolver)
            using (ModuleDefinition game = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Game.dll"))), original))
            {
                game.Assembly.Name.Version = new Version(1, 2, 3, 4);
                var stream = new MemoryStream();
                game.Write(stream);
                otherGame = stream.ToArray();
            }
            string copy = RealPc.CopyWith(pc, new Dictionary<string, byte[]> { ["Game.dll"] = otherGame });
            string serverDir = PcHookServer.WriteToTempFolder();
            try
            {
                byte[] server = PcHookServer.Compile();
                ReaderParameters reader = RealPc.Reader(copy, serverDir);
                using (reader.AssemblyResolver)
                using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server), reader))
                using (ModuleDefinition engine = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(copy, "Gameloft.Engine.dll"))), reader))
                {
                    string before = RealPc.MethodsDigest(engine);
                    int types = engine.Types.Count;
                    var context = new PatchContext(serverModule, 51780, Pc);
                    PatchException refused = Assert.Throws<PatchException>(() => new GameAssemblyLoadPatch().Apply(engine, context));
                    output.WriteLine(refused.Message);
                    Assert.Equal("gameload: Game.dll is \"Game, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null\", expected \"" +
                                 GameAssemblyLoadPatch.QualifiedNames[0].Value + "\"", refused.Message);
                    Assert.Equal(before, RealPc.MethodsDigest(engine));
                    Assert.Equal(types, engine.Types.Count);
                    Assert.Empty(context.Notes);
                }
            }
            finally
            {
                Directory.Delete(copy, true);
                Directory.Delete(serverDir, true);
            }
        }

        /// <summary>
        /// Runs ComponentAssemblies.GetAssembly(name + ".dll", name) of the engine in <paramref name="patched"/> (else the stock
        /// one) in a fresh load context, and returns the one name the engine asked the loader for.
        /// </summary>
        private static AssemblyName Requested(string pc, Dictionary<string, byte[]> patched, byte[] server, string name, out Assembly result,
                                              out IDictionary<string, Assembly> cache)
        {
            using (var context = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Type assemblies = context.Get("Gameloft.Engine").GetType("Gameloft.Engine.ComponentAssemblies", true);
                MethodInfo getAssembly = assemblies.GetMethod("GetAssembly", All, null, new[] { typeof(string), typeof(string) }, null);
                lock (context.Requests) context.Requests.Clear();
                result = (Assembly)getAssembly.Invoke(null, new object[] { name + ".dll", name });
                cache = (IDictionary<string, Assembly>)assemblies.GetField("cachedAssemblies", All).GetValue(null);
                List<AssemblyName> asked;
                lock (context.Requests) asked = context.Requests.Where(r => r.Name == name).ToList();
                return Assert.Single(asked);
            }
        }
    }
}
