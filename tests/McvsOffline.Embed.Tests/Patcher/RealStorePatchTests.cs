using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The device-test-3 'store' patch applied to the player's REAL Game.dll (MCVS_REFS) and executed on net8: kill-signature
    /// sounds no longer throw in IsAvailable (the shop crashed on open), and the shop filter skips an item whose check throws.
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealStorePatchTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ITestOutputHelper output;

        public RealStorePatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Game.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static byte[] Server()
        {
            return FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
        }

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            {
                TypeDefinition reward = module.GetType(StorePatch.RewardType);
                Assert.Equal(StorePatch.GetAvailableIdFingerprint, IlPatch.Fingerprint(reward.Methods.Single(m => m.Name == "GetAvailableId")));
                TypeDefinition closure = module.GetType(StorePatch.StoreType).NestedTypes.Single(t => t.Name == "<>c__DisplayClass25_0");
                Assert.Equal(StorePatch.FilterFingerprint, IlPatch.Fingerprint(closure.Methods.Single(m => m.Name == "<SelectItems>b__8")));
            }
        }

        [Fact]
        public void SoundsAreAvailableUntilOwnedAndNothingThrows()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            var notes = new List<string>();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, notes, new StorePatch());
            foreach (string n in notes) output.WriteLine(n);
            Assert.Equal(2, notes.Count);
            using (var game = new GameContext(refs, patched, server))
            {
                Assembly g = game.Get("Game");
                Type rewardType = g.GetType(StorePatch.RewardType, true);
                Type profileType = g.GetType("Game.PlayerProfileComponent", true);
                object profile = RuntimeHelpers.GetUninitializedObject(profileType);
                var owned = new List<string> { "1" };
                profileType.GetProperty("KillSignatureSounds", Any).SetValue(profile, owned);
                MethodInfo isAvailable = rewardType.GetMethod("IsAvailable", Any, null, new[] { profileType }, null);

                bool Available(string subtype, string subtype2)
                {
                    object item = RuntimeHelpers.GetUninitializedObject(rewardType);
                    rewardType.GetProperty("Subtype", Any).SetValue(item, subtype);
                    rewardType.GetProperty("Subtype2", Any).SetValue(item, subtype2);
                    return (bool)isAvailable.Invoke(item, new[] { profile });
                }

                // KillSignature_Sound_2 is sold while the player does not own sound 2 (IL_0084 threw before).
                Assert.True(Available("Sound", "2"));
                Assert.False(Available("Sound", "1"));
                owned.Add("2");
                Assert.False(Available("Sound", "2"));
                // A random sound has no candidate list: unavailable rather than a crash; so is an unknown subtype or no id.
                Assert.False(Available("Sound", "Random"));
                Assert.False(Available("Sound", "Rarity03"));
                Assert.False(Available("Emote", "2"));
                Assert.False(Available("Sound", null));
                Assert.False(Available("Avatar", null));
            }
        }

        [Fact]
        public void TheShopFilterSkipsAnItemWhoseCheckThrows()
        {
            string refs = Refs();
            if (refs == null) return;
            byte[] server = Server();
            Dictionary<string, byte[]> patched = RealGamePatchTests.PatchReal(refs, server, null, new StorePatch());
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(patched["Game.dll"])))
            {
                MethodDefinition filter = module.GetType(StorePatch.StoreType).NestedTypes.Single(t => t.Name == "<>c__DisplayClass25_0")
                    .Methods.Single(m => m.Name == "<SelectItems>b__8");
                ExceptionHandler h = Assert.Single(filter.Body.ExceptionHandlers);
                Assert.Equal(ExceptionHandlerType.Catch, h.HandlerType);
                Assert.Equal("System.Exception", h.CatchType.FullName);
                Assert.Equal(filter.Body.Instructions[0], h.TryStart);
                Assert.Single(filter.Body.Instructions, i => i.OpCode == OpCodes.Ret);
                Assert.Single(filter.Body.Instructions, i => i.Operand is MethodReference r && r.Name == "IsAvailable");
            }
            using (var game = new GameContext(refs, patched, server))
            {
                Assembly g = game.Get("Game");
                Type hooks = game.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
                hooks.GetField("StoreCalls").SetValue(null, 0);
                Type closure = g.GetType(StorePatch.StoreType + "+<>c__DisplayClass25_0", true);
                MethodInfo lambda = closure.GetMethod("<SelectItems>b__8", Any);
                object instance = RuntimeHelpers.GetUninitializedObject(closure);
                // x == null: x.Content throws NullReferenceException inside the lambda -> caught, reported, item left out.
                Assert.False((bool)lambda.Invoke(instance, new object[] { null }));
                Assert.Equal(1, (int)hooks.GetField("StoreCalls").GetValue(null));
                Assert.IsType<NullReferenceException>(hooks.GetField("LastStoreException").GetValue(null));
                Assert.Null(hooks.GetField("LastStoreItem").GetValue(null));
            }
        }
    }
}
