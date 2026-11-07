using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
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
    /// The build-17 'inbox' patch applied to the player's REAL Game.dll (MCVS_REFS) and executed on net8 next to the unpatched
    /// Game.dll (two load contexts, <see cref="InboxScene"/>): the display list of a message is capped at 30 rewards plus every
    /// Gear reward and is otherwise the shipped list, the rewards past the cap are never built (counted through a RewardItem
    /// subclass), the UNLOCKALL message of the server's own coupon catalog costs at most 30 builds, the claim popup takes the
    /// first 30, the claim itself is untouched, RefreshUI rebuilds once per 10 s window, and ILVerify finds nothing new.
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealInboxPatchTests
    {
        private const BindingFlags Any = InboxScene.Any;

        /// <summary>The UNLOCKALL payload (inbox-freeze report, OUT/UNLOCKALL.json): one message of 11,368 rewards.</summary>
        internal const int UnlockAllRewards = 11368;

        /// <summary>The patched GenerateRewardsObjects on the UNLOCKALL message stays under this (the report's timing guard).</summary>
        internal const double UnlockAllBudgetMs = 50;

        private readonly ITestOutputHelper output;

        public RealInboxPatchTests(ITestOutputHelper output)
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

        private static readonly object PatchGate = new object();
        private static byte[] server;
        private static Dictionary<string, byte[]> patched;
        private static List<string> patchNotes;

        /// <summary>The stand-in server and Game.dll patched with 'inbox' alone (patched once for the class).</summary>
        private static Dictionary<string, byte[]> Patched(string refs, out byte[] serverImage)
        {
            lock (PatchGate)
            {
                if (patched == null)
                {
                    server = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
                    var notes = new List<string>();
                    patched = RealGamePatchTests.PatchReal(refs, server, notes, new InboxPatch());
                    patchNotes = notes;
                }
                serverImage = server;
                return patched;
            }
        }

        // ------------------------------------------------------------------ IL

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            {
                AssertShippedFingerprints(module, false);
            }
        }

        /// <summary>The five verified bodies of <paramref name="module"/> (Android or PC fingerprints).</summary>
        internal static void AssertShippedFingerprints(ModuleDefinition module, bool pc)
        {
            TypeDefinition inbox = module.GetType(InboxPatch.InboxType);
            Assert.Equal(InboxPatch.GenerateRewardsFingerprint, IlPatch.Fingerprint(inbox.Methods.Single(m => m.Name == "GenerateRewardsObjects")));
            Assert.Equal(InboxPatch.GetObjectFingerprint, IlPatch.Fingerprint(inbox.Methods.Single(m => m.Name == "GetObject")));
            Assert.Equal(pc ? InboxPatch.PcRefreshFingerprint : InboxPatch.RefreshFingerprint, IlPatch.Fingerprint(inbox.Methods.Single(m => m.Name == "RefreshUI")));
            Assert.Equal(pc ? InboxPatch.PcClaimFingerprint : InboxPatch.ClaimFingerprint, IlPatch.Fingerprint(ClaimMoveNext(module)));
            Assert.Equal(pc ? InboxPatch.PcIsRandomFingerprint : InboxPatch.IsRandomFingerprint,
                         IlPatch.Fingerprint(module.GetType(InboxPatch.KillSignatureType).Methods.Single(m => m.Name == "get_IsRandom")));
        }

        internal static MethodDefinition ClaimMoveNext(ModuleDefinition module, string method = "ClaimRewardsForMessage")
        {
            return module.GetType(InboxPatch.InboxType).NestedTypes.Single(t => t.Name.StartsWith("<" + method + ">d__", StringComparison.Ordinal))
                         .Methods.Single(m => m.Name == "MoveNext");
        }

        [Fact]
        public void ThePatchedIlHasTheExpectedShapeJitsAndIsNotPatchedTwice()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            foreach (string n in patchNotes) output.WriteLine(n);
            Assert.Equal(4, patchNotes.Count);   // no I5 on Android: its IsRandom already checks for null

            using (var original = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(images["Game.dll"])))
            {
                AssertPatchedShape(original, module, false);
            }
            using (var game = new GameContext(refs, images, serverImage))
            {
                JitPatched(game.Get("Game"), output);
            }

            // Patched once: the helper type is there (checked before the fingerprints, which no longer match either).
            string dir = TestEnvironment.NewTempDir("inbox-repatch");
            try
            {
                foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
                File.WriteAllBytes(Path.Combine(dir, "Game.dll"), images["Game.dll"]);
                Assert.Contains("already patched", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new InboxPatch())).Message);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>What the patch changed, and that the claim itself (ClaimRewardsForMessageRequest) is the shipped code.</summary>
        internal static void AssertPatchedShape(ModuleDefinition original, ModuleDefinition module, bool pc)
        {
            TypeDefinition inbox = module.GetType(InboxPatch.InboxType);
            TypeDefinition helper = module.GetType("Game." + InboxPatch.HelperType);
            Assert.NotNull(helper);
            Assert.True(helper.IsAbstract && helper.IsSealed && helper.IsNotPublic);
            Assert.Equal(new[] { "Forward", "Popup", "RefreshDue", "Report", "Rewards", "Total" }, helper.Methods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));

            // I1: the whole body is the call.
            MethodDefinition generate = inbox.Methods.Single(m => m.Name == "GenerateRewardsObjects");
            Assert.Equal(new[] { "ldarg.1", "ldarg.2", "ldarg.3", "call", "ret" }, generate.Body.Instructions.Select(i => i.OpCode.Name));
            Assert.Equal("Rewards", ((MethodReference)generate.Body.Instructions[3].Operand).Name);
            MethodDefinition rewards = helper.Methods.Single(m => m.Name == "Rewards");
            Assert.Contains(rewards.Body.Instructions, i => (i.OpCode == OpCodes.Ldc_I4_S || i.OpCode == OpCodes.Ldc_I4) && Convert.ToInt32(i.Operand) == InboxPatch.Shown);
            Assert.Contains(rewards.Body.Instructions, i => i.OpCode == OpCodes.Isinst && ((TypeReference)i.Operand).FullName == InboxPatch.GearRewardType);
            ExceptionHandler h = Assert.Single(rewards.Body.ExceptionHandlers);
            Assert.Equal("System.Exception", h.CatchType.FullName);
            Assert.True(IsCall(h.TryStart.Next.Next, "GetObject"), h.TryStart.Next.Next.ToString());

            // I2: dup; ldstr "RewardsTotal"; ldarg.1; call Total; box int; callvirt Add; ret.
            MethodDefinition getObject = inbox.Methods.Single(m => m.Name == "GetObject");
            var tail = getObject.Body.Instructions.Reverse().Take(7).Reverse().ToList();
            Assert.Equal(new[] { "dup", "ldstr", "ldarg.1", "call", "box", "callvirt", "ret" }, tail.Select(i => i.OpCode.Name));
            Assert.Equal(InboxPatch.TotalKey, tail[1].Operand);
            Assert.Equal("Total", ((MethodReference)tail[3].Operand).Name);
            Assert.Single(getObject.Body.Instructions, i => i.OpCode == OpCodes.Ret);

            // I3: the popup's ToList is Popup; the claim request (every reward claimed, the message removed) is the shipped IL.
            MethodDefinition claim = ClaimMoveNext(module);
            Assert.DoesNotContain(claim.Body.Instructions, i => IsCall(i, "ToList"));
            Instruction popup = Assert.Single(claim.Body.Instructions, i => IsCall(i, "Popup"));
            Assert.True(IsCall(popup.Previous, "Concat"));
            Assert.Equal(IlPatch.Fingerprint(ClaimMoveNext(original, "ClaimRewardsForMessageRequest")),
                         IlPatch.Fingerprint(ClaimMoveNext(module, "ClaimRewardsForMessageRequest")));
            Assert.Contains(claim.Body.Instructions, i => IsCall(i, "ClaimRewardsForMessageRequest"));

            // I4: ldarg.0; ldfld lastRefreshUIDone; call RefreshDue; brfalse <ret> right after the Second % 10 test.
            MethodDefinition refresh = inbox.Methods.Single(m => m.Name == "RefreshUI");
            Instruction due = Assert.Single(refresh.Body.Instructions, i => IsCall(i, "RefreshDue"));
            Assert.Equal(OpCodes.Ldfld, due.Previous.OpCode);
            Assert.Equal("lastRefreshUIDone", ((FieldReference)due.Previous.Operand).Name);
            Assert.True(due.Next.OpCode.Code == Code.Brfalse_S || due.Next.OpCode.Code == Code.Brfalse);
            Assert.Same(refresh.Body.Instructions.Last(), due.Next.Operand);
            Assert.Equal(OpCodes.Rem, due.Previous.Previous.Previous.Previous.OpCode);
            Instruction stamp = Assert.Single(refresh.Body.Instructions, i => i.OpCode == OpCodes.Stfld);
            Assert.True(stamp.Offset > due.Offset);

            // I5: PC only.
            MethodDefinition isRandom = module.GetType(InboxPatch.KillSignatureType).Methods.Single(m => m.Name == "get_IsRandom");
            if (pc)
            {
                Assert.Equal(new[] { "ldarg.0", "call", "brtrue.s", "ldc.i4.0", "ret" }, isRandom.Body.Instructions.Take(5).Select(i => i.OpCode.Name));
            }
            else
            {
                Assert.Equal(InboxPatch.IsRandomFingerprint, IlPatch.Fingerprint(isRandom));
            }
        }

        /// <summary>Every changed and added method compiles (InvalidProgramException on bad IL).</summary>
        internal static void JitPatched(Assembly game, ITestOutputHelper output)
        {
            Type inbox = game.GetType(InboxPatch.InboxType, true);
            var methods = new List<MethodBase>
            {
                inbox.GetMethod("GenerateRewardsObjects", Any), inbox.GetMethod("GetObject", Any), inbox.GetMethod("RefreshUI", Any),
                game.GetType(InboxPatch.KillSignatureType, true).GetMethod("get_IsRandom", Any),
            };
            methods.Add(inbox.GetNestedTypes(Any).Single(t => t.Name.StartsWith("<ClaimRewardsForMessage>d__", StringComparison.Ordinal)).GetMethod("MoveNext", Any));
            methods.AddRange(game.GetType("Game." + InboxPatch.HelperType, true).GetMethods(Any | BindingFlags.DeclaredOnly));
            foreach (MethodBase m in methods)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
                output.WriteLine("JIT ok: " + m.DeclaringType + "." + m.Name);
            }
        }

        private static bool IsCall(Instruction i, string name)
        {
            return i != null && (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference m && m.Name == name;
        }

        // ------------------------------------------------------------------ I1 differential

        [Fact]
        public void TheDisplayListIsCappedKeepsGearAndIsOtherwiseTheShippedList()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            using (var original = new InboxScene(refs, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new InboxScene(refs, images, serverImage))
            {
                AssertDisplayDifferential(original, fixedScene, output);
            }
        }

        /// <summary>The I1 cases on one unpatched and one patched scene (the PC tests run it on the Steam Game.dll).</summary>
        internal static void AssertDisplayDifferential(InboxScene original, InboxScene fixedScene, ITestOutputHelper output)
        {
            Assert.False(original.IsPatched);
            Assert.True(fixedScene.IsPatched);
            // (counting rewards, gear positions) -> what each scene builds, and how many GetRewardInfo calls.
            foreach (var (count, gears) in new[]
                     {
                         (0, new int[0]), (1, new int[0]), (29, new int[0]), (30, new int[0]), (31, new int[0]), (45, new[] { 0, 31, 44 }),
                         (100, Enumerable.Range(40, 35).ToArray()), (UnlockAllRewards, new[] { 3, 5000, UnlockAllRewards - 1 }),
                     })
            {
                var labels = Enumerable.Range(0, count).Select(i => (gears.Contains(i) ? "gear" : "r") + i).ToList();
                string Run(InboxScene s, out int builds)
                {
                    InboxRewardCounter.Reset();
                    var rewards = labels.Select(l => s.NewReward(l.StartsWith("gear", StringComparison.Ordinal) ? s.Gear : s.Counting, l)).ToList();
                    string ids = InboxScene.Ids(s.Generate(s.Message(rewards)));
                    builds = InboxRewardCounter.Builds;
                    return ids;
                }
                string before = Run(original, out int originalBuilds);
                string after = Run(fixedScene, out int patchedBuilds);
                Assert.Equal(string.Join(",", labels), before);
                // The first 30 rewards that are not Gear, and every Gear reward, in the message's order.
                int taken = 0;
                var expected = labels.Where(l => l.StartsWith("gear", StringComparison.Ordinal) || taken++ < InboxPatch.Shown).ToList();
                Assert.Equal(string.Join(",", expected), after);
                Assert.Equal(count, originalBuilds);
                Assert.Equal(expected.Count, patchedBuilds);
                output.WriteLine(count + " rewards (" + gears.Length + " Gear): shipped " + originalBuilds + " builds, patched " + patchedBuilds);
            }

            // No reward list, and a reward whose GetObject answers null (both skip it; it uses one of the 30).
            foreach (InboxScene s in new[] { original, fixedScene }) Assert.Empty(s.Generate(s.Message(null, nullList: true)));
            foreach (InboxScene s in new[] { original, fixedScene })
            {
                InboxRewardCounter.Reset();
                var rewards = Enumerable.Range(0, 40).Select(i => s.NewReward(s.Counting, (i == 2 ? "null" : "r") + i)).ToList();
                string ids = InboxScene.Ids(s.Generate(s.Message(rewards)));
                Assert.Equal(string.Join(",", Enumerable.Range(0, s.IsPatched ? 30 : 40).Where(i => i != 2).Select(i => "r" + i)), ids);
            }

            // A predicate (the claim's skin list) gets every matching reward, uncapped, in both.
            foreach (InboxScene s in new[] { original, fixedScene })
            {
                InboxRewardCounter.Reset();
                var rewards = Enumerable.Range(0, 200).Select(i => s.NewReward(i % 2 == 0 ? s.Gear : s.Counting, "x" + i)).ToList();
                Delegate onlyGear = s.Predicate(r => r.GetType() == s.Gear);
                string ids = InboxScene.Ids(s.Generate(s.Message(rewards), onlyGear));
                Assert.Equal(string.Join(",", Enumerable.Range(0, 100).Select(i => "x" + (2 * i))), ids);
                Assert.Equal(100, InboxRewardCounter.Builds);
            }

            // A reward that throws: the shipped call throws it (into native code, the process ends); the patched one leaves that
            // reward out, reports it and builds the rest (the throw uses one of the 30).
            Type hooks = fixedScene.Context.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
            FieldInfo inboxCalls = hooks.GetField("InboxCalls");
            if (inboxCalls != null) inboxCalls.SetValue(null, 0);
            var thrown = Assert.Throws<InvalidOperationException>(() =>
            {
                InboxRewardCounter.Reset();
                original.Generate(original.Message(Enumerable.Range(0, 10).Select(i => original.NewReward(original.Counting, (i == 4 ? "throw" : "r") + i)).ToList()));
            });
            Assert.Equal("reward throw4 throws", thrown.Message);
            InboxRewardCounter.Reset();
            var withThrow = Enumerable.Range(0, 40).Select(i => fixedScene.NewReward(fixedScene.Counting, (i == 4 ? "throw" : "r") + i)).ToList();
            Assert.Equal(string.Join(",", Enumerable.Range(0, 30).Where(i => i != 4).Select(i => "r" + i)), InboxScene.Ids(fixedScene.Generate(fixedScene.Message(withThrow))));
            if (inboxCalls != null)
            {
                Assert.Equal(1, (int)inboxCalls.GetValue(null));
                Assert.Same(withThrow[4], hooks.GetField("LastInboxReward").GetValue(null));
                Assert.IsType<InvalidOperationException>(hooks.GetField("LastInboxException").GetValue(null));
            }
            // A null reward: the shipped code throws a NullReferenceException; the patched one skips it (no slot used).
            Assert.Throws<NullReferenceException>(() => original.Generate(original.Message(new object[] { original.NewReward(original.Counting, "a"), null })));
            Assert.Equal("a,b", InboxScene.Ids(fixedScene.Generate(fixedScene.Message(new object[] { fixedScene.NewReward(fixedScene.Counting, "a"), null, fixedScene.NewReward(fixedScene.Counting, "b") }))));
        }

        // ------------------------------------------------------------------ the UNLOCKALL message

        [Fact]
        public void TheUnlockAllMessageCostsAtMost30BuildsAndIsFast()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            using (var original = new InboxScene(refs, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new InboxScene(refs, images, serverImage))
            {
                AssertUnlockAll(original, fixedScene, output);
            }
        }

        /// <summary>
        /// The server's UNLOCKALL payload as the game parses it (RewardHelper.CreateRewardItem, 11,368 rewards of the real classes,
        /// none of them Gear) and a counting message of the same size: the patched list builds 30 of them, under
        /// <see cref="UnlockAllBudgetMs"/> each time; the shipped one builds all 11,368.
        /// </summary>
        internal static void AssertUnlockAll(InboxScene original, InboxScene fixedScene, ITestOutputHelper output)
        {
            List<string> payload = InboxScene.CouponPayload("UNLOCKALL");
            Assert.Equal(UnlockAllRewards, payload.Count);
            var real = payload.Select(t => fixedScene.RealReward(t, 1)).ToList();
            Assert.DoesNotContain(null, real);
            Type gear = fixedScene.Game.GetType(InboxPatch.GearRewardType, true);
            Assert.DoesNotContain(real, r => gear.IsInstanceOfType(r));
            output.WriteLine("UNLOCKALL: " + string.Join(", ", real.GroupBy(r => r.GetType().Name).Select(g => g.Count() + " " + g.Key)));

            // On this fake profile every real GetObject throws (no World): the patched list tries exactly 30 and reports them; the
            // shipped one ends at the first.
            object realMessage = fixedScene.Message(real);
            Type hooks = fixedScene.Context.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
            FieldInfo inboxCalls = hooks.GetField("InboxCalls");
            if (inboxCalls != null) inboxCalls.SetValue(null, 0);
            List<Dictionary<string, object>> shown = fixedScene.Generate(realMessage);
            if (inboxCalls != null) Assert.Equal(InboxPatch.Shown, shown.Count + (int)inboxCalls.GetValue(null));
            Assert.ThrowsAny<Exception>(() => original.Generate(original.Message(payload.Select(t => original.RealReward(t, 1)).ToList())));
            double realMs = Best(() => fixedScene.Generate(realMessage));
            output.WriteLine("patched GenerateRewardsObjects, real UNLOCKALL message: " + realMs.ToString("0.000") + " ms (" + shown.Count + " built)");
            Assert.True(realMs < UnlockAllBudgetMs, realMs + " ms");

            double Counting(InboxScene s, out int builds, out int listed)
            {
                InboxRewardCounter.Reset();
                object message = s.Message(payload.Select((t, i) => s.NewReward(s.Counting, t)).ToList());
                InboxRewardCounter.Builds = 0;
                listed = s.Generate(message).Count;
                builds = InboxRewardCounter.Builds;
                return Best(() => s.Generate(message));
            }
            double shippedMs = Counting(original, out int shippedBuilds, out int shippedListed);
            double patchedMs = Counting(fixedScene, out int patchedBuilds, out int patchedListed);
            output.WriteLine("counting UNLOCKALL message: shipped " + shippedBuilds + " builds, " + shippedMs.ToString("0.000") + " ms; patched " + patchedBuilds +
                             " builds, " + patchedMs.ToString("0.000") + " ms");
            Assert.Equal(UnlockAllRewards, shippedBuilds);
            Assert.Equal(UnlockAllRewards, shippedListed);
            Assert.Equal(InboxPatch.Shown, patchedBuilds);
            Assert.Equal(InboxPatch.Shown, patchedListed);
            Assert.True(patchedMs < UnlockAllBudgetMs, patchedMs + " ms");
        }

        /// <summary>The best of five timed runs after one warm-up (milliseconds).</summary>
        private static double Best(Action run)
        {
            run();
            double best = double.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                run();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        // ------------------------------------------------------------------ I2, I3

        [Fact]
        public void TheClaimPopupListsTheFirst30AndBuildsOnlyThose()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            using (var scene = new InboxScene(refs, images, serverImage))
            {
                AssertPopupAndTotal(scene);
            }
        }

        internal static void AssertPopupAndTotal(InboxScene scene)
        {
            Type helper = scene.Helper;
            MethodInfo popup = helper.GetMethod("Popup", Any);
            MethodInfo total = helper.GetMethod("Total", Any);

            // ClaimRewardsForMessage: rewardsBufferList (the skins, built first) .Concat(claimed.Where(!skin).Select(GetObject)).
            int built = 0;
            var skins = Enumerable.Range(0, 3).Select(i => new Dictionary<string, object> { ["id"] = "skin" + i }).ToList();
            IEnumerable<Dictionary<string, object>> claimed = Enumerable.Range(0, UnlockAllRewards).Select(i =>
            {
                built++;
                return new Dictionary<string, object> { ["id"] = "c" + i };
            });
            var list = (List<Dictionary<string, object>>)popup.Invoke(null, new object[] { skins.Concat(claimed) });
            Assert.Equal(InboxPatch.Shown, list.Count);
            Assert.Equal(new[] { "skin0", "skin1", "skin2" }.Concat(Enumerable.Range(0, 27).Select(i => "c" + i)), list.Select(d => (string)d["id"]));
            Assert.Equal(27, built);
            // Fewer than 30: all of them, nulls kept as ToList keeps them.
            list = (List<Dictionary<string, object>>)popup.Invoke(null, new object[] { new List<Dictionary<string, object>> { skins[0], null, skins[1] } });
            Assert.Equal(new[] { skins[0], null, skins[1] }, list);
            // A throwing reward keeps what was built so far and is reported (the shipped ToList threw out of the claim).
            Type hooks = scene.Context.Get("McvsOffline.Server").GetType("McvsOffline.GameHooks", true);
            FieldInfo inboxCalls = hooks.GetField("InboxCalls");
            if (inboxCalls != null) inboxCalls.SetValue(null, 0);
            IEnumerable<Dictionary<string, object>> throwing = Enumerable.Range(0, 50).Select(i =>
            {
                if (i == 7) throw new InvalidOperationException("seven");
                return new Dictionary<string, object> { ["id"] = "t" + i };
            });
            list = (List<Dictionary<string, object>>)popup.Invoke(null, new object[] { throwing });
            Assert.Equal(Enumerable.Range(0, 7).Select(i => "t" + i), list.Select(d => (string)d["id"]));
            if (inboxCalls != null) Assert.Equal(1, (int)inboxCalls.GetValue(null));

            // I2's count: every reward of the message, and 0 without a list.
            InboxRewardCounter.Reset();
            object message = scene.Message(Enumerable.Range(0, 1234).Select(i => scene.NewReward(scene.Counting, "r" + i)).ToList());
            Assert.Equal(1234, (int)total.Invoke(null, new[] { message }));
            Assert.Equal(0, (int)total.Invoke(null, new[] { scene.Message(null, nullList: true) }));
            Assert.Equal(0, InboxRewardCounter.Builds);
        }

        // ------------------------------------------------------------------ I4

        [Fact]
        public void RefreshUiRebuildsOncePer10SecondWindow()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            using (var original = new InboxScene(refs, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new InboxScene(refs, images, serverImage))
            {
                AssertRefreshLatch(original, fixedScene, output);
            }
        }

        /// <summary>
        /// Inside a second whose number ends in 0 (waited for, at most 10 s), 20 constant frames: the shipped RefreshUI rebuilds on
        /// every one, the patched one not at all when the last refresh was just now, once when it was 10 s ago, and once for the
        /// first refresh (lastRefreshUIDone = default). RefreshDue on its own: 9 s is the bound, a clock that went back is due.
        /// </summary>
        internal static void AssertRefreshLatch(InboxScene original, InboxScene fixedScene, ITestOutputHelper output)
        {
            MethodInfo due = fixedScene.Helper.GetMethod("RefreshDue", Any);
            bool Due(DateTime last) => (bool)due.Invoke(null, new object[] { last });
            Assert.True(Due(default(DateTime)));
            Assert.False(Due(DateTime.Now));
            Assert.False(Due(DateTime.Now.AddSeconds(-8.5)));
            Assert.True(Due(DateTime.Now.AddSeconds(-InboxPatch.RefreshSeconds - 0.1)));
            Assert.True(Due(DateTime.Now.AddHours(1)));   // the clock went back: refresh rather than wait an hour

            // Outside a :x0 second neither rebuilds (the shipped test, untouched).
            for (int attempt = 0; attempt < 3; attempt++)
            {
                WaitForWindow();
                DateTime start = DateTime.Now;
                int shipped = original.RefreshFrames(DateTime.Now, 20);
                int recent = fixedScene.RefreshFrames(DateTime.Now, 20);
                int stale = fixedScene.RefreshFrames(DateTime.Now.AddSeconds(-10), 20);
                int first = fixedScene.RefreshFrames(default(DateTime), 20);
                if (DateTime.Now.Second != start.Second)
                {
                    output.WriteLine("left the :x0 second during the frames, again");
                    continue;
                }
                output.WriteLine("20 frames in a :x0 second: shipped " + shipped + " rebuilds; patched " + recent + " (refreshed just now), " + stale +
                                 " (10 s ago), " + first + " (first refresh)");
                Assert.Equal(20, shipped);
                Assert.Equal(0, recent);
                Assert.Equal(1, stale);
                Assert.Equal(1, first);
                return;
            }
            Assert.Fail("never stayed inside one :x0 second for the 80 frames");
        }

        /// <summary>Sleeps until the start of the next second whose number ends in 0 (it may already be one: then at once).</summary>
        private static void WaitForWindow()
        {
            while (true)
            {
                DateTime now = DateTime.Now;
                if (now.Second % 10 == 0 && now.Millisecond < 500) return;
                int ms = (10 - now.Second % 10) * 1000 - now.Millisecond + 20;
                Thread.Sleep(Math.Max(1, Math.Min(ms, 10000)));
            }
        }

        // ------------------------------------------------------------------ ILVerify

        [Fact]
        public void IlVerifyAcceptsThePatchedMethods()
        {
            string refs = Refs();
            if (refs == null) return;
            RealAdminPagePatchTests.AssertIlVerifyClean(output, refs, "Game.dll", new IlPatch[] { new InboxPatch() },
                                                        new[] { @"\]Game\.InboxComponent\.(GenerateRewardsObjects|GetObject|RefreshUI)$" }, 3,
                                                        "InboxComponent::GenerateRewardsObjects", "InboxComponent::GetObject", "InboxComponent::RefreshUI",
                                                        "McvsInbox::Rewards", "McvsInbox::Popup", "McvsInbox::Total", "McvsInbox::RefreshDue",
                                                        "McvsInbox::Report", "McvsInbox::Forward");
        }
    }
}
