using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'inbox' patch of Game.dll (build 17: "the game freezes when I go to the inbox, only after redeeming specific
    /// codes"). A redeem code becomes ONE inbox message with every reward of the code (UNLOCKALL 11,368, UNLOCKPAID 5,644, the
    /// small codes 6-150), and opening the inbox builds the reward object of every reward of every unexpired message on the game
    /// thread (<c>GetMessagesObject</c> → <c>GetObject</c> → <c>GenerateRewardsObjects</c> → <c>RewardItem.GetObject</c>, which
    /// for camos and accessories builds a whole merc or scans the accessory table), then deep-copies all of it into Flash and
    /// draws one card per reward; while the inbox is open the C# part runs again on every constant frame of every 10th second.
    /// This patch bounds that cost whatever the profile holds, so it also rescues a profile that already holds such a message:
    /// <list type="bullet">
    /// <item>I1 <c>InboxComponent.GenerateRewardsObjects(message, profile, predicate)</c> now calls
    /// <c>McvsInbox.Rewards</c>: the display list (predicate null) builds at most <see cref="Shown"/> rewards, in order, plus
    /// every Gear reward (Inbox.as onClaimMsg looks for <c>type == "Gear"</c> to warn that the gear inventory is full), so the
    /// rewards past the cap are never built at all; a reward whose <c>GetObject</c> throws uses a slot, is reported
    /// (<c>McvsOffline.GameHooks.InboxRewardException</c>) and is left out instead of ending the process. A non-null predicate
    /// (the claim's skin list) gets every matching reward, each in the same try/catch.</item>
    /// <item>I2 <c>InboxComponent.GetObject(message, profile)</c> adds <c>"RewardsTotal"</c>: the message's reward count (Flash
    /// reads only the keys it knows).</item>
    /// <item>I3 the claim popup: <c>ClaimRewardsForMessage</c> (its state machine) sends Flash
    /// <c>rewardsBufferList.Concat(claimed.Select(GetObject)).ToList()</c>; that <c>ToList</c> is now <c>McvsInbox.Popup</c>, which
    /// takes the first <see cref="Shown"/> (so the lazy Select builds only those). The claim itself
    /// (<c>ClaimRewardsForMessageRequest</c>, which claims every reward of the message and removes it) is not touched.</item>
    /// <item>I4 <c>InboxComponent.RefreshUI()</c> (every constant frame, 0.05 s) rebuilt the list on every frame of a second
    /// whose number ends in 0, up to 20 times per 10 s; its <c>lastRefreshUIDone</c> was written and never read. It now also
    /// needs <c>McvsInbox.RefreshDue(lastRefreshUIDone)</c>: at least <see cref="RefreshSeconds"/> s since the last refresh (or a
    /// clock that went back). The field starts at default(DateTime), so the first refresh still runs: one per 10 s window.</item>
    /// <item>I5 PC only: <c>KillSignatureRewardItem.IsRandom</c> calls <c>Subtype2.ToLower()</c> without the null check the
    /// Android build has (a custom coupon "KillSignature_Avatar" without id); a null Subtype2 now returns false, as on
    /// Android. The Android getter is checked against its fingerprint and left as it is.</item>
    /// </list>
    /// Every method is checked against the IL fingerprint of the verified build (Android 1.17.32, PC 1.17.30) first.
    /// </summary>
    public sealed class InboxPatch : IlPatch
    {
        public const string InboxType = "Game.InboxComponent";
        public const string RewardType = "Game.RewardItem";
        public const string GearRewardType = "Game.GearRewardItem";
        public const string KillSignatureType = "Game.KillSignatureRewardItem";
        public const string HelperType = "McvsInbox";

        /// <summary>The same body on both builds.</summary>
        public const string GenerateRewardsFingerprint = "32a0a0939e296e30fa244cd36e410f4533af60c6e1487938d450401931df051b";
        public const string GetObjectFingerprint = "1615c0ce640e5b79dbd1182675833a6f320390d800cc522f73cc0ef013c98efd";

        /// <summary>Android and PC differ only in the compiler's anonymous-type numbers.</summary>
        public const string RefreshFingerprint = "02686d810924609dd89b88788f8006628c2aee24e1fa9b0dd5afc3146ce4d6fe";
        public const string PcRefreshFingerprint = "9b953c2ee0fd52261960981e4eac1ba56eaaf10cb8a7a2e3b059e766a6eab98a";
        public const string ClaimFingerprint = "0633e7af9253b4cfd934d3600df2a0005c254f6935f01e16b4f2944cc347ade0";
        public const string PcClaimFingerprint = "fffba4a9cc935f44ba241364223741f076e5d98937d0bb6bc03a5a37a12c4e1e";

        /// <summary>Android's getter already has the null check; PC's does not (I5).</summary>
        public const string IsRandomFingerprint = "210d26ff2d1d5a4d0958779b8fc8648e031ec0e9185384c125ece077a851260b";
        public const string PcIsRandomFingerprint = "ff1724db3dea0139e3a7bf836acc1ae78804b0de28fb63506a36c5f21180bd03";

        /// <summary>Rewards an inbox message shows (Gear rewards come on top) and the claim popup lists.</summary>
        public const int Shown = 30;

        /// <summary>Minimum time between two inbox refreshes (the game asks every 10 s).</summary>
        public const int RefreshSeconds = 9;

        /// <summary>The key I2 adds to every message object.</summary>
        public const string TotalKey = "RewardsTotal";

        public override string Id => "inbox";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "inbox: a message shows at most " + Shown + " rewards (the claim still gets them all), one refresh per 10 s, " +
                                              "a reward that throws is skipped (Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            PatchSites.RequireBuildAssembly(module, context, Id);
            // ---- locate and verify
            TypeDefinition inbox = RequireType(module, InboxType);
            TypeDefinition reward = RequireType(module, RewardType);
            TypeDefinition gear = RequireType(module, GearRewardType);
            TypeDefinition signature = RequireType(module, KillSignatureType);
            if (module.GetType("Game." + HelperType) != null)
                throw new PatchException("Game.dll is already patched: run the patcher on your original game files");

            MethodDefinition generate = RequireMethod(inbox, "GenerateRewardsObjects", m => !m.IsStatic && m.Parameters.Count == 3,
                                                      "instance List<Dictionary<string, object>> GenerateRewardsObjects(InboxMessage, PlayerProfileComponent, Func<RewardItem, bool>)");
            RequireFingerprint(generate, GenerateRewardsFingerprint, "InboxComponent.GenerateRewardsObjects");
            MethodDefinition getObject = RequireMethod(inbox, "GetObject", m => !m.IsStatic && m.Parameters.Count == 2,
                                                       "instance Dictionary<string, object> GetObject(InboxMessage, PlayerProfileComponent)");
            RequireFingerprint(getObject, GetObjectFingerprint, "InboxComponent.GetObject");
            MethodDefinition refresh = RequireMethod(inbox, "RefreshUI", m => !m.IsStatic && !m.HasParameters, "instance void RefreshUI()");
            RequireFingerprint(refresh, ForBuild(context, RefreshFingerprint, PcRefreshFingerprint), "InboxComponent.RefreshUI");
            MethodDefinition claim = StateMachine(inbox, "ClaimRewardsForMessage");
            RequireFingerprint(claim, ForBuild(context, ClaimFingerprint, PcClaimFingerprint), "InboxComponent.ClaimRewardsForMessage (state machine)");
            MethodDefinition isRandom = RequireMethod(signature, "get_IsRandom", m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Boolean,
                                                      "bool getter");
            RequireFingerprint(isRandom, ForBuild(context, IsRandomFingerprint, PcIsRandomFingerprint), "KillSignatureRewardItem.IsRandom");

            // I1 references from the verified body: InboxMessage.get_Rewards (IL_000e), new List<Dictionary<string, object>>() (IL_005d).
            var gen = generate.Body.Instructions;
            Instruction At(Mono.Collections.Generic.Collection<Instruction> code, int offset) => code.Single(i => i.Offset == offset);
            var getRewards = (MethodReference)At(gen, 0x0e).Operand;
            var newList = (MethodReference)At(gen, 0x5d).Operand;
            if (getRewards.Name != "get_Rewards" || newList.Name != ".ctor" || !(getRewards.ReturnType is GenericInstanceType) ||
                !(generate.ReturnType is GenericInstanceType) || !(generate.Parameters[2].ParameterType is GenericInstanceType))
                throw new PatchException(generate.FullName + ": the verified sites moved:\n" + Dump(generate));
            MethodDefinition rewardGetObject = RequireMethod(reward, "GetObject",
                m => !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Game.PlayerProfileComponent",
                "instance Dictionary<string, object> GetObject(PlayerProfileComponent)");

            // I2: the one ret, after the last Dictionary<string, object>.Add of the collection initializer (the dictionary is on the stack).
            var obj = getObject.Body.Instructions;
            var objRets = obj.Where(i => i.OpCode == OpCodes.Ret).ToList();
            var add = objRets.Count == 1 ? objRets[0].Previous?.Operand as MethodReference : null;
            if (add == null || add.Name != "Add" || add.DeclaringType.FullName != "System.Collections.Generic.Dictionary`2<System.String,System.Object>")
                throw new PatchException(getObject.FullName + ": expected one ret after Dictionary<string, object>.Add:\n" + Dump(getObject));

            // I3: the one ToList<Dictionary<string, object>> right after Concat (the popup's list).
            var popupSites = claim.Body.Instructions.Where(i => IsCall(i, "System.Linq.Enumerable", "ToList") && i.Previous != null &&
                                                                IsCall(i.Previous, "System.Linq.Enumerable", "Concat")).ToList();
            if (popupSites.Count != 1 || claim.Body.Instructions.Count(i => IsCall(i, "System.Linq.Enumerable", "ToList")) != 1)
                throw new PatchException(claim.FullName + ": expected one Concat(...).ToList():\n" + Dump(claim));
            Instruction popupSite = popupSites[0];

            // I4 (verified offsets): IL_0018 brtrue.s IL_0055 (Second % 10 != 0 -> ret), IL_001a ldarg.0, IL_0020 stfld lastRefreshUIDone.
            var rf = refresh.Body.Instructions;
            Instruction skip = At(rf, 0x18), afterSkip = At(rf, 0x1a), store = At(rf, 0x20), getNow = At(rf, 0x1b);
            FieldDefinition last = RequireField(inbox, "lastRefreshUIDone", "System.DateTime");
            if (skip.OpCode != OpCodes.Brtrue_S || ((Instruction)skip.Operand).OpCode != OpCodes.Ret || ((Instruction)skip.Operand).Next != null ||
                afterSkip.OpCode != OpCodes.Ldarg_0 || store.OpCode != OpCodes.Stfld || ((FieldReference)store.Operand).Resolve() != last ||
                !IsCall(getNow, "System.DateTime", "get_Now"))
                throw new PatchException(refresh.FullName + ": the verified sites moved:\n" + Dump(refresh));

            // I5 (PC): IL_0001 call get_Subtype2.
            var getSubtype2 = (MethodReference)isRandom.Body.Instructions[1].Operand;
            if (getSubtype2.Name != "get_Subtype2") throw new PatchException(isRandom.FullName + ": the verified sites moved:\n" + Dump(isRandom));

            // ---- references, each checked against the game's own assemblies
            TypeSystem ts = module.TypeSystem;
            var listOfReward = (GenericInstanceType)getRewards.ReturnType;
            var listOfDict = (GenericInstanceType)generate.ReturnType;
            var predicateType = (GenericInstanceType)generate.Parameters[2].ParameterType;
            var dictType = listOfDict.GenericArguments[0];
            // Members of the generic instances are declared on fresh copies whose open type has its generic parameters (get_Item
            // returns !0, Invoke takes !0 and returns !1); the copies have the same full names as the shipped references.
            GenericInstanceType rewardsOwner = Fresh(module, listOfReward), dictsOwner = Fresh(module, listOfDict), predicateOwner = Fresh(module, predicateType);
            MethodReference rewardCount = Checked(new MethodReference("get_Count", ts.Int32, rewardsOwner) { HasThis = true });
            var rewardAt = new MethodReference("get_Item", rewardsOwner.ElementType.GenericParameters[0], rewardsOwner) { HasThis = true };
            rewardAt.Parameters.Add(new ParameterDefinition(ts.Int32));
            rewardAt = Checked(rewardAt);
            var invoke = new MethodReference("Invoke", predicateOwner.ElementType.GenericParameters[1], predicateOwner) { HasThis = true };
            invoke.Parameters.Add(new ParameterDefinition(predicateOwner.ElementType.GenericParameters[0]));
            invoke = Checked(invoke);
            var listAdd = new MethodReference("Add", ts.Void, dictsOwner) { HasThis = true };
            listAdd.Parameters.Add(new ParameterDefinition(dictsOwner.ElementType.GenericParameters[0]));
            listAdd = Checked(listAdd);
            MethodReference listCount = Checked(new MethodReference("get_Count", ts.Int32, dictsOwner) { HasThis = true });

            var enumerableOpen = new TypeReference("System.Collections.Generic", "IEnumerable`1", module, ts.CoreLibrary);
            enumerableOpen.GenericParameters.Add(new GenericParameter("T", enumerableOpen));
            var enumerableOfDict = new GenericInstanceType(enumerableOpen);
            enumerableOfDict.GenericArguments.Add(dictType);
            var enumerable = new TypeReference("System.Collections", "IEnumerable", module, ts.CoreLibrary);
            var enumerator = new TypeReference("System.Collections", "IEnumerator", module, ts.CoreLibrary);
            var disposable = new TypeReference("System", "IDisposable", module, ts.CoreLibrary);
            MethodReference getEnumerator = CorlibMethod(module, enumerable, "GetEnumerator", true, enumerator);
            MethodReference moveNext = CorlibMethod(module, enumerator, "MoveNext", true, ts.Boolean);
            MethodReference current = CorlibMethod(module, enumerator, "get_Current", true, ts.Object);
            MethodReference dispose = CorlibMethod(module, disposable, "Dispose", true, ts.Void);

            var dateTime = new TypeReference("System", "DateTime", module, ts.CoreLibrary) { IsValueType = true };
            var timeSpan = new TypeReference("System", "TimeSpan", module, ts.CoreLibrary) { IsValueType = true };
            MethodReference subtract = CorlibMethod(module, dateTime, "op_Subtraction", false, timeSpan, dateTime, dateTime);
            MethodReference ticks = CorlibMethod(module, timeSpan, "get_Ticks", true, ts.Int64);
            var now = (MethodReference)getNow.Operand;

            var exceptionType = new TypeReference("System", "Exception", module, ts.CoreLibrary);
            var interlocked = new TypeReference("System.Threading", "Interlocked", module, ts.CoreLibrary);
            MethodReference increment = CorlibMethod(module, interlocked, "Increment", false, ts.Int32, new ByReferenceType(ts.Int32));
            MethodReference hook = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "InboxRewardException", "System.Void",
                                                      "System.Object", "System.Exception");

            // ---- the helper type
            var helper = new TypeDefinition("Game", HelperType, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                                                                TypeAttributes.BeforeFieldInit | TypeAttributes.Class, ts.Object);
            module.Types.Add(helper);
            MethodDefinition report = AddReport(helper, hook, increment, exceptionType);
            MethodDefinition rewards = AddRewards(helper, generate, listOfReward, listOfDict, getRewards, rewardCount, rewardAt, invoke, gear,
                                                  rewardGetObject, newList, listAdd, exceptionType, report);
            MethodDefinition popup = AddPopup(helper, enumerableOfDict, listOfDict, dictType, newList, listAdd, listCount, getEnumerator, moveNext,
                                              current, disposable, dispose, exceptionType, report);
            MethodDefinition total = AddTotal(helper, generate.Parameters[0].ParameterType, getRewards, rewardCount);
            MethodDefinition due = AddRefreshDue(helper, dateTime, timeSpan, now, subtract, ticks);

            // ---- change: I1
            {
                MethodBody body = generate.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                body.InitLocals = false;
                ILProcessor il = body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, rewards);
                il.Emit(OpCodes.Ret);
            }
            context.Notes.Add(generate.FullName + ": Game." + HelperType + ".Rewards: the display list (predicate null) builds at most " + Shown +
                              " rewards plus every Gear reward; a reward whose GetObject throws is reported and skipped");

            // ---- change: I2
            {
                getObject.Body.SimplifyMacros();
                ILProcessor il = getObject.Body.GetILProcessor();
                Instruction ret = objRets[0];
                il.InsertBefore(ret, il.Create(OpCodes.Dup));
                il.InsertBefore(ret, il.Create(OpCodes.Ldstr, TotalKey));
                il.InsertBefore(ret, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(ret, il.Create(OpCodes.Call, total));
                il.InsertBefore(ret, il.Create(OpCodes.Box, ts.Int32));
                il.InsertBefore(ret, il.Create(OpCodes.Callvirt, add));
                getObject.Body.OptimizeMacros();
            }
            context.Notes.Add(getObject.FullName + ": adds \"" + TotalKey + "\" (the message's reward count)");

            // ---- change: I3
            popupSite.Operand = popup;
            context.Notes.Add(claim.FullName + ": the claim popup's Concat(...).ToList() -> Game." + HelperType + ".Popup (the first " + Shown +
                              " rewards; ClaimRewardsForMessageRequest still claims every reward)");

            // ---- change: I4
            {
                refresh.Body.SimplifyMacros();
                ILProcessor il = refresh.Body.GetILProcessor();
                var exit = (Instruction)skip.Operand;
                il.InsertBefore(afterSkip, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(afterSkip, il.Create(OpCodes.Ldfld, last));
                il.InsertBefore(afterSkip, il.Create(OpCodes.Call, due));
                il.InsertBefore(afterSkip, il.Create(OpCodes.Brfalse, exit));
                refresh.Body.OptimizeMacros();
            }
            context.Notes.Add(refresh.FullName + ": also needs " + RefreshSeconds + " s since lastRefreshUIDone (one refresh per 10 s window, " +
                              "every constant frame of the second before)");

            // ---- change: I5 (PC)
            if (context.Build.IsPc)
            {
                isRandom.Body.SimplifyMacros();
                ILProcessor il = isRandom.Body.GetILProcessor();
                Instruction first = isRandom.Body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, getSubtype2));
                il.InsertBefore(first, il.Create(OpCodes.Brtrue, first));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                isRandom.Body.OptimizeMacros();
                context.Notes.Add(isRandom.FullName + ": a null Subtype2 -> false (Subtype2.ToLower() threw; the Android build checks it)");
            }

            MethodReference Checked(MethodReference reference)
            {
                MethodDefinition resolved;
                try
                {
                    resolved = reference.Resolve();
                }
                catch (AssemblyResolutionException ex)
                {
                    throw new PatchException("cannot resolve the game's assemblies for " + reference.FullName + ": " + ex.Message);
                }
                if (resolved == null) throw new PatchException("the game's assemblies have no " + reference.FullName);
                return module.ImportReference(reference);
            }
        }

        /// <summary>A copy of <paramref name="type"/> on a new open type that declares its generic parameters (for member references).</summary>
        private static GenericInstanceType Fresh(ModuleDefinition module, GenericInstanceType type)
        {
            TypeReference element = type.ElementType;
            var open = new TypeReference(element.Namespace, element.Name, module, element.Scope, element.IsValueType);
            for (int n = 0; n < type.GenericArguments.Count; n++) open.GenericParameters.Add(new GenericParameter("T" + n, open));
            var fresh = new GenericInstanceType(open);
            foreach (TypeReference argument in type.GenericArguments) fresh.GenericArguments.Add(argument);
            return fresh;
        }

        /// <summary>The MoveNext of the async method <paramref name="name"/> (found through its AsyncStateMachine attribute).</summary>
        private static MethodDefinition StateMachine(TypeDefinition type, string name)
        {
            MethodDefinition method = RequireMethod(type, name, m => m.CustomAttributes.Any(a => a.AttributeType.Name == "AsyncStateMachineAttribute"),
                                                    "async method");
            CustomAttribute attribute = method.CustomAttributes.Single(a => a.AttributeType.Name == "AsyncStateMachineAttribute");
            var machine = (attribute.ConstructorArguments[0].Value as TypeReference)?.Resolve();
            if (machine == null || machine.DeclaringType != type) throw new PatchException(method.FullName + ": its state machine type was not found");
            return RequireMethod(machine, "MoveNext", m => !m.IsStatic && !m.HasParameters, "MoveNext()");
        }

        /// <summary>
        /// <code>
        /// static List&lt;Dictionary&lt;string, object&gt;&gt; Rewards(InboxMessage message, PlayerProfileComponent profile, Func&lt;RewardItem, bool&gt; predicate)
        /// {
        ///     var result = new List&lt;Dictionary&lt;string, object&gt;&gt;();
        ///     List&lt;RewardItem&gt; rewards = message.Rewards;
        ///     if (rewards == null) return result;
        ///     int tried = 0, count = rewards.Count;
        ///     for (int i = 0; i &lt; count; i++)
        ///     {
        ///         RewardItem r = rewards[i];
        ///         if (r == null) continue;
        ///         if (predicate != null) { if (!predicate(r)) continue; }
        ///         else if (!(r is GearRewardItem)) { if (tried &gt;= Shown) continue; tried++; }
        ///         Dictionary&lt;string, object&gt; o = null;
        ///         try { o = r.GetObject(profile); } catch (Exception e) { Report(r, e); }
        ///         if (o != null) result.Add(o);
        ///     }
        ///     return result;
        /// }
        /// </code>
        /// The shipped <c>Where(predicate).SelectNonNull(r =&gt; r.GetObject(profile)).ToList()</c> in order, with the cap and the
        /// catch; a null reward is skipped (the shipped code threw a NullReferenceException for it).
        /// </summary>
        private static MethodDefinition AddRewards(TypeDefinition helper, MethodDefinition generate, TypeReference listOfReward, TypeReference listOfDict,
                                                   MethodReference getRewards, MethodReference rewardCount, MethodReference rewardAt, MethodReference invoke,
                                                   TypeDefinition gear, MethodReference getObject, MethodReference newList, MethodReference listAdd,
                                                   TypeReference exceptionType, MethodDefinition report)
        {
            ModuleDefinition module = helper.Module;
            TypeSystem ts = module.TypeSystem;
            var method = new MethodDefinition("Rewards", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, listOfDict);
            method.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, generate.Parameters[0].ParameterType));
            method.Parameters.Add(new ParameterDefinition("profile", ParameterAttributes.None, generate.Parameters[1].ParameterType));
            method.Parameters.Add(new ParameterDefinition("predicate", ParameterAttributes.None, generate.Parameters[2].ParameterType));
            var result = new VariableDefinition(listOfDict);
            var list = new VariableDefinition(listOfReward);
            var i = new VariableDefinition(ts.Int32);
            var tried = new VariableDefinition(ts.Int32);
            var count = new VariableDefinition(ts.Int32);
            var item = new VariableDefinition(getObject.DeclaringType);
            var built = new VariableDefinition(getObject.ReturnType);
            var caught = new VariableDefinition(exceptionType);
            foreach (VariableDefinition v in new[] { result, list, i, tried, count, item, built, caught }) method.Body.Variables.Add(v);
            method.Body.InitLocals = true;

            ILProcessor il = method.Body.GetILProcessor();
            Instruction end = il.Create(OpCodes.Ldloc, result);
            Instruction cond = il.Create(OpCodes.Ldloc, i);
            Instruction next = il.Create(OpCodes.Ldloc, i);
            Instruction loop = il.Create(OpCodes.Ldloc, list);
            Instruction display = il.Create(OpCodes.Ldloc, item);
            Instruction build = il.Create(OpCodes.Ldnull);
            Instruction tryStart = il.Create(OpCodes.Ldloc, item);
            Instruction handler = il.Create(OpCodes.Stloc, caught);
            Instruction after = il.Create(OpCodes.Ldloc, built);

            il.Emit(OpCodes.Newobj, newList);
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, getRewards);
            il.Emit(OpCodes.Stloc, list);
            il.Emit(OpCodes.Ldloc, list);
            il.Emit(OpCodes.Brfalse, end);
            il.Emit(OpCodes.Ldloc, list);
            il.Emit(OpCodes.Callvirt, rewardCount);
            il.Emit(OpCodes.Stloc, count);
            il.Emit(OpCodes.Br, cond);

            il.Append(loop);
            il.Emit(OpCodes.Ldloc, i);
            il.Emit(OpCodes.Callvirt, rewardAt);
            il.Emit(OpCodes.Stloc, item);
            il.Emit(OpCodes.Ldloc, item);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Brfalse, display);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldloc, item);
            il.Emit(OpCodes.Callvirt, invoke);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Br, build);

            il.Append(display);
            il.Emit(OpCodes.Isinst, gear);
            il.Emit(OpCodes.Brtrue, build);
            il.Emit(OpCodes.Ldloc, tried);
            il.Emit(OpCodes.Ldc_I4, Shown);
            il.Emit(OpCodes.Bge, next);
            il.Emit(OpCodes.Ldloc, tried);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, tried);

            il.Append(build);
            il.Emit(OpCodes.Stloc, built);
            il.Append(tryStart);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, getObject);
            il.Emit(OpCodes.Stloc, built);
            il.Emit(OpCodes.Leave, after);
            il.Append(handler);
            il.Emit(OpCodes.Ldloc, item);
            il.Emit(OpCodes.Ldloc, caught);
            il.Emit(OpCodes.Call, report);
            il.Emit(OpCodes.Leave, after);
            il.Append(after);
            il.Emit(OpCodes.Brfalse, next);
            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ldloc, built);
            il.Emit(OpCodes.Callvirt, listAdd);

            il.Append(next);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, i);
            il.Append(cond);
            il.Emit(OpCodes.Ldloc, count);
            il.Emit(OpCodes.Blt, loop);
            il.Append(end);
            il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = after, CatchType = exceptionType,
            });
            method.Body.OptimizeMacros();
            helper.Methods.Add(method);
            return method;
        }

        /// <summary>
        /// <code>
        /// static List&lt;Dictionary&lt;string, object&gt;&gt; Popup(IEnumerable&lt;Dictionary&lt;string, object&gt;&gt; rewards)
        /// {
        ///     var result = new List&lt;Dictionary&lt;string, object&gt;&gt;();
        ///     try
        ///     {
        ///         IEnumerator e = ((IEnumerable)rewards).GetEnumerator();
        ///         while (result.Count &lt; Shown &amp;&amp; e.MoveNext()) result.Add((Dictionary&lt;string, object&gt;)e.Current);
        ///         (e as IDisposable)?.Dispose();
        ///     }
        ///     catch (Exception x) { Report(null, x); }
        ///     return result;
        /// }
        /// </code>
        /// The lazy <c>Select(GetObject)</c> behind it builds only the rewards taken. A throw keeps what was built so far (the
        /// shipped ToList threw out of the claim, and Flash never got its answer).
        /// </summary>
        private static MethodDefinition AddPopup(TypeDefinition helper, TypeReference enumerableOfDict, TypeReference listOfDict, TypeReference dictType,
                                                 MethodReference newList, MethodReference listAdd, MethodReference listCount, MethodReference getEnumerator,
                                                 MethodReference moveNext, MethodReference current, TypeReference disposable, MethodReference dispose,
                                                 TypeReference exceptionType, MethodDefinition report)
        {
            var method = new MethodDefinition("Popup", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, listOfDict);
            method.Parameters.Add(new ParameterDefinition("rewards", ParameterAttributes.None, enumerableOfDict));
            var result = new VariableDefinition(listOfDict);
            var e = new VariableDefinition(getEnumerator.ReturnType);
            var caught = new VariableDefinition(exceptionType);
            method.Body.Variables.Add(result);
            method.Body.Variables.Add(e);
            method.Body.Variables.Add(caught);
            method.Body.InitLocals = true;

            ILProcessor il = method.Body.GetILProcessor();
            Instruction end = il.Create(OpCodes.Ldloc, result);
            Instruction loop = il.Create(OpCodes.Ldloc, result);
            Instruction cond = il.Create(OpCodes.Ldloc, result);
            Instruction done = il.Create(OpCodes.Ldloc, e);
            Instruction leave = il.Create(OpCodes.Leave, end);
            Instruction handler = il.Create(OpCodes.Stloc, caught);

            il.Emit(OpCodes.Newobj, newList);
            il.Emit(OpCodes.Stloc, result);
            Instruction tryStart = il.Create(OpCodes.Ldarg_0);
            il.Append(tryStart);
            il.Emit(OpCodes.Callvirt, getEnumerator);
            il.Emit(OpCodes.Stloc, e);
            il.Emit(OpCodes.Br, cond);
            il.Append(loop);
            il.Emit(OpCodes.Ldloc, e);
            il.Emit(OpCodes.Callvirt, current);
            il.Emit(OpCodes.Castclass, dictType);
            il.Emit(OpCodes.Callvirt, listAdd);
            il.Append(cond);
            il.Emit(OpCodes.Callvirt, listCount);
            il.Emit(OpCodes.Ldc_I4, Shown);
            il.Emit(OpCodes.Bge, done);
            il.Emit(OpCodes.Ldloc, e);
            il.Emit(OpCodes.Callvirt, moveNext);
            il.Emit(OpCodes.Brtrue, loop);
            il.Append(done);
            il.Emit(OpCodes.Isinst, disposable);
            il.Emit(OpCodes.Dup);
            Instruction noDispose = il.Create(OpCodes.Pop);
            il.Emit(OpCodes.Brfalse, noDispose);
            il.Emit(OpCodes.Callvirt, dispose);
            il.Emit(OpCodes.Br, leave);
            il.Append(noDispose);
            il.Append(leave);
            il.Append(handler);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldloc, caught);
            il.Emit(OpCodes.Call, report);
            il.Emit(OpCodes.Leave, end);
            il.Append(end);
            il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = end, CatchType = exceptionType,
            });
            method.Body.OptimizeMacros();
            helper.Methods.Add(method);
            return method;
        }

        /// <summary><c>static int Total(InboxMessage message) =&gt; message.Rewards?.Count ?? 0;</c></summary>
        private static MethodDefinition AddTotal(TypeDefinition helper, TypeReference messageType, MethodReference getRewards, MethodReference rewardCount)
        {
            TypeSystem ts = helper.Module.TypeSystem;
            var method = new MethodDefinition("Total", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Int32);
            method.Parameters.Add(new ParameterDefinition("message", ParameterAttributes.None, messageType));
            ILProcessor il = method.Body.GetILProcessor();
            Instruction have = il.Create(OpCodes.Callvirt, rewardCount);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, getRewards);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brtrue, have);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            il.Append(have);
            il.Emit(OpCodes.Ret);
            method.Body.OptimizeMacros();
            helper.Methods.Add(method);
            return method;
        }

        /// <summary>
        /// <code>
        /// static bool RefreshDue(DateTime last)
        /// {
        ///     long ticks = (DateTime.Now - last).Ticks;
        ///     return ticks &lt; 0 || ticks &gt;= RefreshSeconds * TimeSpan.TicksPerSecond;
        /// }
        /// </code>
        /// </summary>
        private static MethodDefinition AddRefreshDue(TypeDefinition helper, TypeReference dateTime, TypeReference timeSpan, MethodReference now,
                                                      MethodReference subtract, MethodReference ticks)
        {
            TypeSystem ts = helper.Module.TypeSystem;
            var method = new MethodDefinition("RefreshDue", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
            method.Parameters.Add(new ParameterDefinition("last", ParameterAttributes.None, dateTime));
            var span = new VariableDefinition(timeSpan);
            var value = new VariableDefinition(ts.Int64);
            method.Body.Variables.Add(span);
            method.Body.Variables.Add(value);
            method.Body.InitLocals = true;
            ILProcessor il = method.Body.GetILProcessor();
            Instruction yes = il.Create(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Call, now);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, subtract);
            il.Emit(OpCodes.Stloc, span);
            il.Emit(OpCodes.Ldloca, span);
            il.Emit(OpCodes.Call, ticks);
            il.Emit(OpCodes.Stloc, value);
            il.Emit(OpCodes.Ldloc, value);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Conv_I8);
            il.Emit(OpCodes.Blt, yes);
            il.Emit(OpCodes.Ldloc, value);
            il.Emit(OpCodes.Ldc_I8, RefreshSeconds * 10000000L);
            il.Emit(OpCodes.Clt);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Ret);
            il.Append(yes);
            il.Emit(OpCodes.Ret);
            method.Body.OptimizeMacros();
            helper.Methods.Add(method);
            return method;
        }

        /// <summary>
        /// <code>
        /// static int reported;
        /// static void Report(object reward, Exception e) { if (Interlocked.Increment(ref reported) &gt; 200) return; try { Forward(reward, e); } catch { } }
        /// [NoInlining] static void Forward(object reward, Exception e) =&gt; McvsOffline.GameHooks.InboxRewardException(reward, e);
        /// </code>
        /// As <c>McvsStoreSafety</c> (StorePatch): Forward is separate so a missing McvsOffline.Server.dll fails when Forward is
        /// compiled, inside Report's try.
        /// </summary>
        private static MethodDefinition AddReport(TypeDefinition helper, MethodReference hook, MethodReference increment, TypeReference exceptionType)
        {
            TypeSystem ts = helper.Module.TypeSystem;
            var reported = new FieldDefinition("reported", FieldAttributes.Private | FieldAttributes.Static, ts.Int32);
            helper.Fields.Add(reported);

            var forward = new MethodDefinition("Forward", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            forward.ImplAttributes |= MethodImplAttributes.NoInlining;
            forward.Parameters.Add(new ParameterDefinition("reward", ParameterAttributes.None, ts.Object));
            forward.Parameters.Add(new ParameterDefinition("exception", ParameterAttributes.None, exceptionType));
            {
                ILProcessor il = forward.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, hook);
                il.Emit(OpCodes.Ret);
            }
            helper.Methods.Add(forward);

            var report = new MethodDefinition("Report", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            report.Parameters.Add(new ParameterDefinition("reward", ParameterAttributes.None, ts.Object));
            report.Parameters.Add(new ParameterDefinition("exception", ParameterAttributes.None, exceptionType));
            {
                ILProcessor il = report.Body.GetILProcessor();
                Instruction ret = il.Create(OpCodes.Ret);
                il.Emit(OpCodes.Ldsflda, reported);
                il.Emit(OpCodes.Call, increment);
                il.Emit(OpCodes.Ldc_I4, 200);
                il.Emit(OpCodes.Bgt, ret);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, forward);
                il.Emit(OpCodes.Leave, ret);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Leave, ret);
                il.Append(ret);
                report.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = ret, CatchType = ts.Object,
                });
            }
            helper.Methods.Add(report);
            return report;
        }
    }
}
