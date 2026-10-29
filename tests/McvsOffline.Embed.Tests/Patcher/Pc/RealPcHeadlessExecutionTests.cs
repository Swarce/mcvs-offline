using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// The PC <c>headless</c> sites (docs/design/pc-port.md §8.4, §12.4) executed on net8 with the uninitialised-World technique of
    /// RealGamePatchTests: a <c>RuntimeHelpers.GetUninitializedObject</c> World with <c>&lt;Headless&gt;k__BackingField</c> set, a
    /// GameObject whose <c>world</c> field holds it, the component on that GameObject. Headless reaches the inserted return or
    /// jump without touching client-only code; a client World enters the original body (observed as what that body does on an
    /// uninitialised object). Where code that runs before the guard needs a native World, the test says so and keeps the
    /// structural asserts (the operands of the inserted and retargeted branches on the real patched method).
    /// </summary>
    public class RealPcHeadlessExecutionTests
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly BuildProfile Pc = BuildProfile.PcSteam6372049;

        private readonly ITestOutputHelper output;

        public RealPcHeadlessExecutionTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>An uninitialised World (headless as given), a GameObject in it, a component of <paramref name="type"/> on it.</summary>
        private sealed class Scene
        {
            public Scene(Assembly engine, Type type)
            {
                World = engine.GetType("Gameloft.Engine.World", true);
                Type gameObject = engine.GetType("Gameloft.Engine.GameObject", true);
                Type component = engine.GetType("Gameloft.Engine.Component", true);
                WorldObject = RuntimeHelpers.GetUninitializedObject(World);
                object go = RuntimeHelpers.GetUninitializedObject(gameObject);
                gameObject.GetField("world", All).SetValue(go, WorldObject);
                Component = RuntimeHelpers.GetUninitializedObject(type);
                component.GetFields(All).Single(f => f.FieldType == gameObject).SetValue(Component, go);

                // World.Synchronization (GetGlobalComponent) answers null instead of failing on the missing cache: IsServer() is false.
                FieldInfo cacheField = World.GetField("globalComponents", All);
                Type funcType = typeof(Func<,>).MakeGenericType(typeof(Type), component);
                Delegate factory = Delegate.CreateDelegate(funcType, typeof(Scene).GetMethod(nameof(NoComponent), All).MakeGenericMethod(component));
                cacheField.SetValue(WorldObject, Activator.CreateInstance(cacheField.FieldType, factory));
            }

            public Type World { get; }

            public object WorldObject { get; }

            public object Component { get; }

            public bool Headless
            {
                set { World.GetField("<Headless>k__BackingField", All).SetValue(WorldObject, value); }
            }

            /// <summary>Runs <paramref name="method"/> on the component; the exception it threw, or null.</summary>
            public Exception Run(string method)
            {
                MethodInfo m = Component.GetType().GetMethod(method, All | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                try
                {
                    m.Invoke(Component, null);
                    return null;
                }
                catch (TargetInvocationException ex)
                {
                    return ex.InnerException;
                }
            }

            private static T NoComponent<T>(Type type) where T : class
            {
                return null;
            }
        }

        private (Dictionary<string, byte[]> Patched, byte[] Server) PatchHeadless(string pc)
        {
            byte[] server = PcHookServer.Compile();
            return (RealPc.Patch(pc, server, null, Pc, new HeadlessGamePatch()), server);
        }

        [Fact]
        public void H1InputSettingsIsDroppedFromHeadlessWorldsAndH7HelpersAreNullSafe()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var (patched, server) = PatchHeadless(pc);
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Assembly engine = game.Get("Gameloft.Engine");
                Assembly gameDll = game.Get("Game");
                Type keep = engine.GetType("Gameloft.Engine.KeepInHeadlessModeAttribute", true);
                object attribute = gameDll.GetType("Game.InputSettingsComponent", true).GetCustomAttributes(false).Single(keep.IsInstanceOfType);
                Assert.Equal(false, keep.GetProperty("Keep").GetValue(attribute));
                foreach (string type in new[] { "Game.FlameZoneComponent", "Game.BacktrackTimeDistortionExplosion" })
                    gameDll.GetType(type, true).GetMethod("McvsTriggerEnd", All).Invoke(null, new object[] { null });
            }
        }

        [Fact]
        public void H5SoundUpdateReturnsInHeadlessWorldsOnly()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var (patched, server) = PatchHeadless(pc);
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                var scene = new Scene(game.Get("Gameloft.Engine"), game.Get("Game").GetType("Game.SoundManagerGameComponent", true));
                FieldInfo active = scene.Component.GetType().GetField("loadingSnapShotActive", All);
                active.SetValue(scene.Component, true);
                scene.Headless = true;
                Assert.Null(scene.Run("OnUpdate"));
                Assert.Equal(true, active.GetValue(scene.Component));   // returned before VoxManager.PopGroupSnapshot
                scene.Headless = false;
                Exception client = scene.Run("OnUpdate");
                output.WriteLine("client World: " + client);
                Assert.True(client != null || !(bool)active.GetValue(scene.Component), "a client World runs the original body (VoxManager, native)");
            }
        }

        [Fact]
        public void H11FocusLostReturnsAtOnceInHeadlessWorlds()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var (patched, server) = PatchHeadless(pc);
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                var scene = new Scene(game.Get("Gameloft.Engine"), game.Get("Game").GetType("Game.GameplayComponent", true));
                scene.Headless = true;
                Assert.Null(scene.Run("OnAppFocusLost"));
                scene.Headless = false;
                Exception client = scene.Run("OnAppFocusLost");   // HideScoreBoard, then InputManager.IsCursorVisible (nativeengine)
                output.WriteLine("client World: " + client?.GetType().Name);
                Assert.True(client is DllNotFoundException || client is EntryPointNotFoundException, "the original body ran: " + client);
            }
        }

        [Fact]
        public void M3HeadlessWorldsCallTheBaseOnStopAndClientWorldsKeepTheStockCall()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var (patched, server) = PatchHeadless(pc);
            using (var game = new RealGamePatchTests.GameContext(pc, patched, server))
            {
                Assembly gameDll = game.Get("Game");
                Type steam = gameDll.GetType("Game.SteamAchievementsComponent", true);
                Type achievements = gameDll.GetType("Game.BaseAchievementsComponent", true);
                FieldInfo mercs = achievements.GetField("mercsComponent", All), gameplay = achievements.GetField("gameplayComponent", All);

                var scene = new Scene(game.Get("Gameloft.Engine"), steam);
                object mercsSentinel = RuntimeHelpers.GetUninitializedObject(mercs.FieldType);
                object gameplaySentinel = RuntimeHelpers.GetUninitializedObject(gameplay.FieldType);
                mercs.SetValue(scene.Component, mercsSentinel);
                gameplay.SetValue(scene.Component, gameplaySentinel);
                scene.Headless = true;
                Exception headless = scene.Run("OnStop");
                output.WriteLine("headless World: " + (headless == null ? "returned" : headless.ToString()));
                Assert.Null(headless);   // base OnStop: Component.OnStop, the fields nulled, IsServer() false; then the stock tail
                Assert.Null(mercs.GetValue(scene.Component));
                Assert.Null(gameplay.GetValue(scene.Component));

                mercs.SetValue(scene.Component, mercsSentinel);
                gameplay.SetValue(scene.Component, gameplaySentinel);
                scene.Headless = false;
                Exception client = scene.Run("OnStop");   // the stock base.OnStart() on an uninitialised component
                output.WriteLine("client World: " + (client == null ? "returned" : client.GetType().Name + ": " + client.Message));
                if (client != null)
                {
                    Assert.Same(mercsSentinel, mercs.GetValue(scene.Component));       // not nulled: base.OnStart() threw first
                    Assert.Same(gameplaySentinel, gameplay.GetValue(scene.Component));
                }
            }
        }

        /// <summary>
        /// H8, H9, H10, M2 and H13 run code before their guard that needs a native World, so they are checked structurally on the
        /// real patched methods (the reason per site below); WR2 covers their behaviour.
        /// </summary>
        [Fact]
        public void TheGuardsThatCannotRunHereTargetWhatTheDesignSays()
        {
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            var (patched, _) = PatchHeadless(pc);
            ReaderParameters reader = RealPc.Reader(pc, PcHookServer.WriteToTempFolder());
            using (reader.AssemblyResolver)
            using (ModuleDefinition original = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(Path.Combine(pc, "Game.dll"))), reader))
            using (ModuleDefinition game = ModuleDefinition.ReadModule(new MemoryStream(patched["Game.dll"]), reader))
            {
                TestEnvironment.AssertPcCorlib(game);

                // H8: GameManager.GetPhysicalResolution (a nativegame P/Invoke) runs at IL_0014, before the guard.
                MethodDefinition prefs = RealPc.Method(game, "Game.GamePreferencesComponent", "LoadPreferencesWindows", 0);
                Instruction h8 = AssertGuard(prefs, OriginalAt(original, "Game.GamePreferencesComponent", "LoadPreferencesWindows", 0x25), true);
                AssertTarget(h8, original, "Game.GamePreferencesComponent", "LoadPreferencesWindows", 0x2b, prefs);

                // H9: block A runs base.OnStart() (the World's meta-game) before the guard.
                MethodDefinition steamStart = RealPc.Method(game, "Game.SteamAchievementsComponent", "OnStart", 0);
                Instruction h9 = AssertGuard(steamStart, OriginalAt(original, "Game.SteamAchievementsComponent", "OnStart", 0x38), true);
                Assert.Equal(OpCodes.Ret, ((Instruction)h9.Operand).OpCode);
                Assert.Same(steamStart.Body.Instructions.Last(), h9.Operand);
                Instruction guardStart = h9.Previous.Previous.Previous;
                Assert.Equal(new[] { guardStart, guardStart }, steamStart.Body.Instructions.Where(i => i.Operand == guardStart).Select(i => (Instruction)i.Operand));
                Assert.Equal(2, steamStart.Body.Instructions.Count(i => i.Operand == guardStart && (i.OpCode == OpCodes.Brfalse_S || i.OpCode == OpCodes.Brfalse)));
                AssertStockTail(original, "Game.SteamAchievementsComponent", "OnStart", 0x38, h9.Next);

                // M3: the operands resolve to the real base type's OnStop (headless) and OnStart (client).
                MethodDefinition steamStop = RealPc.Method(game, "Game.SteamAchievementsComponent", "OnStop", 0);
                var stop = steamStop.Body.Instructions;
                Assert.Equal(OpCodes.Brfalse, stop[3].OpCode);
                Assert.Equal("System.Void Game.BaseAchievementsComponent::OnStop()", stop[5].Operand.ToString());
                Assert.Equal(OpCodes.Br, stop[6].OpCode);
                Assert.Same(stop[7], stop[3].Operand);
                Assert.Equal("System.Void Game.BaseAchievementsComponent::OnStart()", stop[8].Operand.ToString());
                Assert.Same(stop[9], stop[6].Operand);
                Assert.NotNull(((MethodReference)stop[5].Operand).Resolve());

                // H10: base.OnCreate (ClientComponent: World.IsServer, GameApplication.Instance) runs before the guard.
                MethodDefinition create = RealPc.Method(game, "Game.GameClientComponent", "OnCreate", 0);
                Instruction h10 = AssertGuard(create, OriginalAt(original, "Game.GameClientComponent", "OnCreate", 0x79), true);
                Assert.Same(create.Body.Instructions.Last(), h10.Operand);
                AssertStockTail(original, "Game.GameClientComponent", "OnCreate", 0x79, h10.Next);

                // M2: CheckGameplayInitialized (before the guard) needs the World's SynchronizationComponent and, to fall through
                // to IL_0025, a running server match or a client link. Both entry branches now target the guard.
                MethodDefinition update = RealPc.Method(game, "Game.GameplayComponent", "OnUpdate", 0);
                Instruction m2 = AssertGuard(update, OriginalAt(original, "Game.GameplayComponent", "OnUpdate", 0x57), true);
                Instruction m2Start = m2.Previous.Previous.Previous;
                Assert.Equal(2, update.Body.Instructions.Count(i => i.Operand == m2Start));
                Assert.Equal(OpCodes.Ldarg_0, ((Instruction)m2.Operand).OpCode);
                Assert.Equal("m_isShowRoundResult", ((FieldReference)((Instruction)m2.Operand).Next.Operand).Name);
                Assert.Equal(3, update.Body.Instructions.Count(i => i.Operand is MethodReference r && r.Name == "ShowCursor"));
                AssertStockTail(original, "Game.GameplayComponent", "OnUpdate", 0x57, m2.Next);

                // H11: also executed above; the prologue jumps to the ret.
                MethodDefinition focus = RealPc.Method(game, "Game.GameplayComponent", "OnAppFocusLost", 0);
                Assert.Same(focus.Body.Instructions.Last(), focus.Body.Instructions[3].Operand);

                // H13: GameMatch.OnStart runs before the site; the null check skips to IL_0033 (the original ldarg.0 after the dispatch).
                MethodDefinition tutorial = RealPc.Method(game, "Game.TutorialComponent", "OnStart", 0);
                Instruction getUi = tutorial.Body.Instructions.Single(i => i.Operand is MethodReference r && r.Name == "GetUI");
                Assert.Equal(new[] { OpCodes.Dup, OpCodes.Brtrue, OpCodes.Pop, OpCodes.Br }, new[] { getUi.Next, getUi.Next.Next, getUi.Next.Next.Next, getUi.Next.Next.Next.Next }.Select(i => i.OpCode));
                Assert.Same(getUi.Next.Next.Next.Next.Next, getUi.Next.Next.Operand);
                Assert.Equal("HIDE_AT_START_HUD", ((Instruction)getUi.Next.Next.Operand).Operand);
                var skip = (Instruction)getUi.Next.Next.Next.Next.Operand;
                Assert.Equal(OpCodes.Ldarg_0, skip.OpCode);
                Assert.Equal("DispatchEvent", ((MethodReference)skip.Previous.Operand).Name);
            }
        }

        /// <summary>The instruction of the original body at <paramref name="offset"/> (as text, for comparison with the patched body).</summary>
        private static string OriginalAt(ModuleDefinition original, string type, string method, int offset)
        {
            return RealPc.Method(original, type, method, 0).Body.Instructions.Single(i => i.Offset == offset).ToString().Substring(9);
        }

        /// <summary>
        /// The <c>ldarg.0; call get_World; callvirt get_Headless; brtrue|brfalse</c> guard right before the instruction that was
        /// <paramref name="stockText"/> (without its label); returns the branch.
        /// </summary>
        private static Instruction AssertGuard(MethodDefinition method, string stockText, bool whenHeadless)
        {
            Instruction branch = method.Body.Instructions.Single(i => i.Previous != null && i.Previous.Operand is MethodReference r && r.Name == "get_Headless" &&
                                                                     i.Next != null && i.Next.ToString().Substring(9) == stockText);
            Assert.Equal(whenHeadless ? Code.Brtrue : Code.Brfalse, branch.OpCode.Code == Code.Brtrue_S ? Code.Brtrue : branch.OpCode.Code == Code.Brfalse_S ? Code.Brfalse : branch.OpCode.Code);
            Assert.Equal(OpCodes.Callvirt, branch.Previous.OpCode);
            Assert.Equal("Gameloft.Engine.World", ((MethodReference)branch.Previous.Operand).DeclaringType.FullName);
            Assert.Equal("get_World", ((MethodReference)branch.Previous.Previous.Operand).Name);
            Assert.Equal(OpCodes.Ldarg_0, branch.Previous.Previous.Previous.OpCode);
            return branch;
        }

        private static void AssertTarget(Instruction branch, ModuleDefinition original, string type, string method, int offset, MethodDefinition patched)
        {
            Assert.Equal(OriginalAt(original, type, method, offset), ((Instruction)branch.Operand).ToString().Substring(9));
            Assert.Contains((Instruction)branch.Operand, patched.Body.Instructions);
        }

        /// <summary>From <paramref name="offset"/> on, the patched body is the original one (opcodes and operands).</summary>
        private static void AssertStockTail(ModuleDefinition original, string type, string method, int offset, Instruction first)
        {
            var stock = RealPc.Method(original, type, method, 0).Body.Instructions.SkipWhile(i => i.Offset != offset).ToList();
            var now = new List<Instruction>();
            for (Instruction i = first; i != null; i = i.Next) now.Add(i);
            Assert.Equal(stock.Select(Describe), now.Select(Describe));
        }

        /// <summary>Opcode (a short branch the insertion widened counts as the same branch) and operand.</summary>
        private static string Describe(Instruction i)
        {
            string name = i.OpCode.OperandType == OperandType.ShortInlineBrTarget ? i.OpCode.Name.Replace(".s", "") : i.OpCode.Name;
            if (i.Operand is Instruction target) return name + " -> " + target.OpCode.Name + " " + (target.Operand == null || target.Operand is Instruction ? "" : target.Operand.ToString());
            return name + " " + (i.Operand == null ? "" : i.Operand.ToString());
        }
    }
}
