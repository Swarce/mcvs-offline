using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Assets;
using Xunit;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets the dynamic test assembly derive from Game's internal GearRewardItem (honoured by the .NET runtime).</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// What the counting rewards of <see cref="InboxScene"/> do when the game builds their reward object
    /// (<c>RewardItem.GetObject</c> → the overridden <c>GetRewardInfo</c>): count the build, then answer by label ("throw…" throws,
    /// "null…" returns null, anything else a small dictionary {id, type}).
    /// </summary>
    public static class InboxRewardCounter
    {
        private static readonly Dictionary<object, string> labels = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

        public static int Builds;

        public static void Reset()
        {
            Builds = 0;
            lock (labels) labels.Clear();
        }

        public static void Label(object reward, string label)
        {
            lock (labels) labels[reward] = label;
        }

        public static Dictionary<string, object> Info(object reward)
        {
            Builds++;
            string label;
            lock (labels) labels.TryGetValue(reward, out label);
            if (label != null && label.StartsWith("throw", StringComparison.Ordinal)) throw new InvalidOperationException("reward " + label + " throws");
            if (label != null && label.StartsWith("null", StringComparison.Ordinal)) return null;
            string name = reward.GetType().Name;
            return new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = label, ["type"] = name.Substring(0, name.Length - "RewardItem".Length) };
        }
    }

    /// <summary>
    /// The real Game.dll (patched or not) in its own load context with fake inbox state: an uninitialized InboxComponent and
    /// PlayerProfileComponent, inbox messages of real <c>RewardHelper.CreateRewardItem</c> rewards or of counting rewards (a
    /// <c>RewardItem</c> subclass "CountingRewardItem" and a <c>GearRewardItem</c> subclass, both emitted, whose
    /// <c>GetRewardInfo</c> goes to <see cref="InboxRewardCounter"/>). Shared by the Android and the PC inbox tests.
    /// </summary>
    internal sealed class InboxScene : IDisposable
    {
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Type inboxType, messageType, rewardListType, predicateType;
        private readonly MethodInfo generate, refresh, createReward;
        private readonly FieldInfo active, last;
        private readonly object trackingSource;

        public InboxScene(string directory, Dictionary<string, byte[]> images, byte[] server)
        {
            Context = new GameContext(directory, images, server);
            IsPatched = images.ContainsKey("Game.dll");
            Game = Context.Get("Game");
            Assembly engine = Context.Get("Gameloft.Engine");
            inboxType = Game.GetType(InboxPatch.InboxType, true);
            messageType = Game.GetType("Game.InboxMessage", true);
            Type rewardType = Game.GetType(InboxPatch.RewardType, true);
            Type profileType = Game.GetType("Game.PlayerProfileComponent", true);
            rewardListType = typeof(List<>).MakeGenericType(rewardType);
            predicateType = typeof(Func<,>).MakeGenericType(rewardType, typeof(bool));
            Inbox = RuntimeHelpers.GetUninitializedObject(inboxType);
            Profile = RuntimeHelpers.GetUninitializedObject(profileType);
            generate = inboxType.GetMethod("GenerateRewardsObjects", Any);
            refresh = inboxType.GetMethod("RefreshUI", Any);
            active = inboxType.GetField("refreshUIActive", Any);
            last = inboxType.GetField("lastRefreshUIDone", Any);
            createReward = Game.GetType("Game.RewardHelper", true).GetMethods(Any)
                               .Single(m => m.Name == "CreateRewardItem" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(string));
            trackingSource = Activator.CreateInstance(rewardType.GetNestedType("TrackingSource", Any));
            Counting = EmitCounting(Game, rewardType, profileType, "Mcvs", "CountingRewardItem");
            Gear = EmitCounting(Game, Game.GetType(InboxPatch.GearRewardType, true), profileType, "Mcvs", "GearRewardItem");
            // RewardItem.Parse finds the reward classes through TypeUtil, which searches the registered game assemblies.
            var registered = (IList)engine.GetType("Gameloft.Engine.ComponentAssemblies", true).GetField("gameAssemblies", Any).GetValue(null);
            if (!registered.Contains(Game)) registered.Add(Game);
        }

        public GameContext Context { get; }

        public Assembly Game { get; }

        public bool IsPatched { get; }

        public object Inbox { get; }

        public object Profile { get; }

        /// <summary>RewardItem subclass "CountingRewardItem" (its Type is "Counting").</summary>
        public Type Counting { get; }

        /// <summary>GearRewardItem subclass named GearRewardItem (its Type is "Gear", as the real ones).</summary>
        public Type Gear { get; }

        public object NewReward(Type type, string label)
        {
            object reward = RuntimeHelpers.GetUninitializedObject(type);
            InboxRewardCounter.Label(reward, label);
            return reward;
        }

        /// <summary>The game's own reward for a payload type such as "WeaponCamo_3_12" (null when the game knows no such class).</summary>
        public object RealReward(string type, int quantity)
        {
            return createReward.Invoke(null, new[] { type, (object)quantity, trackingSource });
        }

        public object Message(IEnumerable<object> rewards, bool nullList = false)
        {
            object message = Activator.CreateInstance(messageType, "coupon-test");
            PropertyInfo list = messageType.GetProperty("Rewards", Any);
            if (nullList)
            {
                list.SetValue(message, null);
                return message;
            }
            var typed = (IList)Activator.CreateInstance(rewardListType);
            foreach (object r in rewards) typed.Add(r);
            list.SetValue(message, typed);
            return message;
        }

        /// <summary>A Func&lt;RewardItem, bool&gt; of the game's types that calls <paramref name="test"/>.</summary>
        public Delegate Predicate(Func<object, bool> test)
        {
            var holder = new PredicateHolder(test);
            MethodInfo invoke = typeof(PredicateHolder).GetMethod(nameof(PredicateHolder.Invoke)).MakeGenericMethod(predicateType.GetGenericArguments()[0]);
            return Delegate.CreateDelegate(predicateType, holder, invoke);
        }

        /// <summary>InboxComponent.GenerateRewardsObjects; an exception it throws is rethrown as itself.</summary>
        public List<Dictionary<string, object>> Generate(object message, Delegate predicate = null)
        {
            try
            {
                return (List<Dictionary<string, object>>)generate.Invoke(Inbox, new[] { message, Profile, predicate });
            }
            catch (TargetInvocationException e)
            {
                ExceptionDispatchInfoThrow(e.InnerException);
                throw;
            }
        }

        /// <summary>"id,id,…" of a built list (the counting rewards' labels).</summary>
        public static string Ids(IEnumerable<Dictionary<string, object>> list)
        {
            return string.Join(",", list.Select(d => (string)d["id"]));
        }

        /// <summary>
        /// One InboxComponent.RefreshUI call with the inbox open and <c>lastRefreshUIDone</c> = <paramref name="lastDone"/>: true when it
        /// started a rebuild (the shipped code writes <c>lastRefreshUIDone = DateTime.Now</c> first; the fake component throws later).
        /// </summary>
        public bool Refresh(DateTime lastDone)
        {
            active.SetValue(Inbox, true);
            last.SetValue(Inbox, lastDone);
            try
            {
                refresh.Invoke(Inbox, null);
            }
            catch (TargetInvocationException)
            {
            }
            return (DateTime)last.GetValue(Inbox) != lastDone;
        }

        /// <summary>RefreshUI calls one after the other (the inbox's constant frames): how many started a rebuild.</summary>
        public int RefreshFrames(DateTime lastDone, int frames)
        {
            active.SetValue(Inbox, true);
            last.SetValue(Inbox, lastDone);
            int rebuilds = 0;
            for (int i = 0; i < frames; i++)
            {
                DateTime before = (DateTime)last.GetValue(Inbox);
                try
                {
                    refresh.Invoke(Inbox, null);
                }
                catch (TargetInvocationException)
                {
                }
                if ((DateTime)last.GetValue(Inbox) != before) rebuilds++;
            }
            return rebuilds;
        }

        /// <summary>The helper the patch adds (Game.McvsInbox), or null in the shipped Game.dll.</summary>
        public Type Helper => Game.GetType("Game." + InboxPatch.HelperType, false);

        /// <summary>
        /// The reward list of the server's own coupon <paramref name="code"/> (server/Content coupons.json + unlocks.json, exactly what
        /// <c>GET /coupons/…</c> answers): UNLOCKALL is the 11,368 rewards of the measured payload.
        /// </summary>
        public static List<string> CouponPayload(string code)
        {
            string content = Path.Combine(TestEnvironment.RepoRoot, "server", "Content");
            var catalog = new CouponCatalog(MiniJson.ParseObject(File.ReadAllText(Path.Combine(content, CouponCatalog.CouponsFile))),
                                            MiniJson.ParseObject(File.ReadAllText(Path.Combine(content, CouponCatalog.UnlocksFile))), null, null);
            List<object> payload = catalog.Resolve(code, out _);
            Assert.NotNull(payload);
            return payload.Select(r => (string)((Dictionary<string, object>)r)["type"]).ToList();
        }

        public void Dispose()
        {
            Context.Dispose();
        }

        private static void ExceptionDispatchInfoThrow(Exception e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        }

        private static Type EmitCounting(Assembly game, Type baseType, Type profileType, string ns, string name)
        {
            using (AssemblyLoadContext.EnterContextualReflection(game))
            {
                AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("McvsInboxRewards" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
                ab.SetCustomAttribute(new CustomAttributeBuilder(typeof(IgnoresAccessChecksToAttribute).GetConstructor(new[] { typeof(string) }), new object[] { "Game" }));
                ModuleBuilder mb = ab.DefineDynamicModule("Rewards");
                TypeBuilder tb = mb.DefineType(ns + "." + name, TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class, baseType);
                MethodInfo info = baseType.GetMethod("GetRewardInfo", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { profileType }, null);
                Assert.NotNull(info);
                MethodBuilder m = tb.DefineMethod("GetRewardInfo", MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig, info.ReturnType, new[] { profileType });
                ILGenerator il = m.GetILGenerator();
                il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                il.Emit(System.Reflection.Emit.OpCodes.Call, typeof(InboxRewardCounter).GetMethod(nameof(InboxRewardCounter.Info)));
                il.Emit(System.Reflection.Emit.OpCodes.Ret);
                tb.DefineDefaultConstructor(MethodAttributes.Public);
                return tb.CreateType();
            }
        }

        private sealed class PredicateHolder
        {
            private readonly Func<object, bool> test;

            public PredicateHolder(Func<object, bool> test)
            {
                this.test = test;
            }

            public bool Invoke<T>(T reward)
            {
                return test(reward);
            }
        }
    }
}
