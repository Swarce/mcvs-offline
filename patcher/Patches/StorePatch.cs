using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'store' patch of Game.dll (device test 3: opening the shop crashed at once).
    /// <list type="bullet">
    /// <item>S1 <c>KillSignatureRewardItem.GetAvailableId(profile)</c> builds its candidate list only for the "Avatar" and
    /// "Background" subtypes; for "Sound" the list stays null and <c>list.Count</c> (IL_0084) throws. Every caller of
    /// <c>IsAvailable</c> (<c>GetAvailableId(profile) != null</c>) therefore throws for a sound: the unified store filter, the
    /// reward popups and the lottery (<c>RewardItem.GetRewardInfo</c> puts <c>IsAvailable</c> in every reward object). The
    /// official store never listed sounds; ours does. The "other subtype" branch (IL_0046 <c>br.s IL_0084</c>) now goes to
    /// <code>
    /// if (Subtype != "Sound" || IsRandom || profile.KillSignatureSounds.Contains(Subtype2)) return null;
    /// return Subtype2;
    /// </code>
    /// (the same ownership test <c>OnPrepareToClaim</c> makes for a sound), and a null <c>Subtype2</c> returns null before
    /// <c>Subtype2.StartsWith("Rarity")</c>. Avatars and backgrounds run exactly the shipped code.</item>
    /// <item>S2 the safety net: <c>GameUnifiedStoreComponent.SelectItems</c> keeps an item when
    /// <c>x.Content.FirstOrDefault()?.RewardPrototype.IsAvailable(profile) ?? false</c> (the lambda in its closure class). Its body
    /// now runs in <c>try { ... } catch (Exception e) { McvsStoreSafety.Report(x, e); return false; }</c>: an item whose check
    /// throws is left out of the shop and reported (<c>[McvsOffline] store item &lt;id&gt; threw &lt;exception&gt;</c> on stdout and in
    /// bootstrap.log, through <c>McvsOffline.GameHooks.StoreItemException</c>) instead of ending the process.</item>
    /// </list>
    /// Both methods are checked against the IL fingerprint of the verified 1.17.32 build first.
    /// </summary>
    public sealed class StorePatch : IlPatch
    {
        public const string RewardType = "Game.KillSignatureRewardItem";
        public const string StoreType = "Game.GameUnifiedStoreComponent";
        public const string SafetyType = "McvsStoreSafety";
        public const string GetAvailableIdFingerprint = "02933672268448f87bdfc3a3c43d0013be430a9e5782922913741d5a92a145d8";
        public const string FilterFingerprint = "dbd08a1c0cbdb037e81a316ec15e0fbd8f68543258adc574c0234c30b98aa307";

        public override string Id => "store";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "shop: kill-signature sounds no longer throw in IsAvailable; an item whose availability check throws is skipped (Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            TypeDefinition reward = RequireType(module, RewardType);
            TypeDefinition store = RequireType(module, StoreType);
            if (module.GetType("Game." + SafetyType) != null)
                throw new PatchException("Game.dll is already patched: run the patcher on your original APK");
            MethodDefinition getId = RequireMethod(reward, "GetAvailableId",
                m => !m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "Game.PlayerProfileComponent" &&
                     m.ReturnType.MetadataType == MetadataType.String, "instance string GetAvailableId(PlayerProfileComponent)");
            RequireFingerprint(getId, GetAvailableIdFingerprint, "KillSignatureRewardItem.GetAvailableId(PlayerProfileComponent)");
            MethodDefinition isRandom = RequireMethod(reward, "get_IsRandom", m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Boolean,
                                                      "bool getter");

            // The references S1 needs, taken from the verified code itself: get_Subtype (IL_0026), get_Subtype2 (IL_0011),
            // String.op_Equality (IL_0032), and from OnPrepareToClaim's sound case: get_KillSignatureSounds and List<string>.Contains.
            var code = getId.Body.Instructions;
            Instruction At(int offset) => code.Single(i => i.Offset == offset);
            Instruction otherBranch = At(0x46);
            Instruction returnNull = At(0xac);
            var getSubtype2 = (MethodReference)At(0x11).Operand;
            var getSubtype = (MethodReference)At(0x26).Operand;
            var stringEquals = (MethodReference)At(0x32).Operand;
            if (otherBranch.OpCode != OpCodes.Br_S || ((Instruction)otherBranch.Operand).Offset != 0x84 || returnNull.OpCode != OpCodes.Ldnull ||
                getSubtype2.Name != "get_Subtype2" || getSubtype.Name != "get_Subtype" || stringEquals.Name != "op_Equality")
                throw new PatchException(getId.FullName + ": the verified sites moved:\n" + Dump(getId));
            MethodDefinition prepare = RequireMethod(reward, "OnPrepareToClaim", m => !m.IsStatic && m.Parameters.Count == 1, "OnPrepareToClaim(PlayerProfileComponent)");
            var pc = prepare.Body.Instructions;
            var soundSites = Enumerable.Range(0, pc.Count - 3)
                .Where(i => IsCall(pc[i], "Game.PlayerProfileComponent", "get_KillSignatureSounds") && pc[i + 1].OpCode == OpCodes.Ldarg_0 &&
                            IsCall(pc[i + 2], "Game.RewardItem", "get_Subtype2") && pc[i + 3].Operand is MethodReference c && c.Name == "Contains" &&
                            c.DeclaringType.FullName == "System.Collections.Generic.List`1<System.String>")
                .ToList();
            if (soundSites.Count != 1)
                throw new PatchException(prepare.FullName + ": expected one 'profile.KillSignatureSounds.Contains(Subtype2)', found " + soundSites.Count + ":\n" + Dump(prepare));
            var getSounds = (MethodReference)pc[soundSites[0]].Operand;
            var contains = (MethodReference)pc[soundSites[0] + 3].Operand;

            MethodDefinition filter = FindFilterLambda(store);
            RequireFingerprint(filter, FilterFingerprint, "GameUnifiedStoreComponent.SelectItems availability filter");
            var filterRets = filter.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
            if (filterRets.Count != 2) throw new PatchException(filter.FullName + ": expected two ret:\n" + Dump(filter));

            TypeSystem ts = module.TypeSystem;
            var exceptionType = new TypeReference("System", "Exception", module, ts.CoreLibrary);
            var interlocked = new TypeReference("System.Threading", "Interlocked", module, ts.CoreLibrary);
            MethodReference increment = CorlibMethod(module, interlocked, "Increment", false, ts.Int32, new ByReferenceType(ts.Int32));
            MethodReference hook = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "StoreItemException", "System.Void",
                                                      "System.Object", "System.Exception");

            // ---- change: S1
            getId.Body.SimplifyMacros();
            ILProcessor il = getId.Body.GetILProcessor();
            Instruction first = code[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, getSubtype2));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, returnNull));
            Instruction sound = il.Create(OpCodes.Ldarg_0);
            il.Append(sound);
            il.Emit(OpCodes.Call, getSubtype);
            il.Emit(OpCodes.Ldstr, "Sound");
            il.Emit(OpCodes.Call, stringEquals);
            il.Emit(OpCodes.Brfalse, returnNull);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, isRandom);
            il.Emit(OpCodes.Brtrue, returnNull);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, getSounds);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getSubtype2);
            il.Emit(OpCodes.Callvirt, contains);
            il.Emit(OpCodes.Brtrue, returnNull);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getSubtype2);
            il.Emit(OpCodes.Ret);
            otherBranch.Operand = sound;
            getId.Body.OptimizeMacros();
            context.Notes.Add(getId.FullName + ": null Subtype2 -> null; subtype \"Sound\" (list stayed null, IL_0084 threw) -> Subtype2 unless random " +
                              "or already owned (KillSignatureSounds), else null; other subtypes -> null");

            // ---- change: S2
            TypeDefinition safety = AddSafetyType(module, hook, increment, exceptionType);
            MethodDefinition report = safety.Methods.Single(m => m.Name == "Report");
            filter.Body.SimplifyMacros();
            var result = new VariableDefinition(ts.Boolean);
            var caught = new VariableDefinition(exceptionType);
            filter.Body.Variables.Add(result);
            filter.Body.Variables.Add(caught);
            filter.Body.InitLocals = true;
            il = filter.Body.GetILProcessor();
            Instruction end = il.Create(OpCodes.Ldloc, result);
            Instruction handler = il.Create(OpCodes.Stloc, caught);
            Instruction tryStart = filter.Body.Instructions[0];
            foreach (Instruction ret in filterRets)
            {
                ret.OpCode = OpCodes.Stloc;
                ret.Operand = result;
                il.InsertAfter(ret, il.Create(OpCodes.Leave, end));
            }
            il.Append(handler);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloc, caught);
            il.Emit(OpCodes.Call, report);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Leave, end);
            il.Append(end);
            il.Emit(OpCodes.Ret);
            filter.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = end, CatchType = exceptionType,
            });
            filter.Body.OptimizeMacros();
            context.Notes.Add(filter.FullName + ": body in try/catch (Exception) -> Game." + SafetyType + ".Report(item, e); false " +
                              "(an item whose availability check throws is left out of the shop and reported)");
        }

        /// <summary>
        /// The SelectItems lambda <c>x =&gt; x.Content.FirstOrDefault()?.RewardPrototype.IsAvailable(profile) ?? false</c>: the one
        /// <c>&lt;SelectItems&gt;b__*</c> method of the store's nested closure classes that calls <c>IRewardItem.IsAvailable</c>
        /// (found by what it does, not by its generated name).
        /// </summary>
        private static MethodDefinition FindFilterLambda(TypeDefinition store)
        {
            List<MethodDefinition> found = store.NestedTypes
                .SelectMany(t => t.Methods)
                .Where(m => m.Name.StartsWith("<SelectItems>b__") && m.HasBody && !m.IsStatic && m.Parameters.Count == 1 &&
                            m.ReturnType.MetadataType == MetadataType.Boolean &&
                            m.Body.Instructions.Any(i => IsCall(i, "Game.IRewardItem", "IsAvailable")))
                .ToList();
            if (found.Count != 1)
                throw new PatchException(store.FullName + ": expected one SelectItems lambda calling IRewardItem.IsAvailable, found " + found.Count);
            if (found[0].Parameters[0].ParameterType.FullName != "Game.UnifiedStoreComponent/UnifiedShopItem")
                throw new PatchException(found[0].FullName + " does not take a UnifiedShopItem");
            return found[0];
        }

        /// <summary>
        /// <code>
        /// static class McvsStoreSafety
        /// {
        ///     static int reported;
        ///     static void Report(object item, Exception e) { if (Interlocked.Increment(ref reported) &gt; 200) return; try { Forward(item, e); } catch { } }
        ///     [NoInlining] static void Forward(object item, Exception e) =&gt; McvsOffline.GameHooks.StoreItemException(item, e);
        /// }
        /// </code>
        /// Forward is separate so a missing McvsOffline.Server.dll fails when Forward is compiled, inside Report's try.
        /// </summary>
        private static TypeDefinition AddSafetyType(ModuleDefinition module, MethodReference hook, MethodReference increment, TypeReference exceptionType)
        {
            TypeSystem ts = module.TypeSystem;
            var safety = new TypeDefinition("Game", SafetyType, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed |
                                                                TypeAttributes.BeforeFieldInit | TypeAttributes.Class, ts.Object);
            module.Types.Add(safety);
            var reported = new FieldDefinition("reported", FieldAttributes.Private | FieldAttributes.Static, ts.Int32);
            safety.Fields.Add(reported);

            var forward = new MethodDefinition("Forward", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            forward.ImplAttributes |= MethodImplAttributes.NoInlining;
            forward.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, ts.Object));
            forward.Parameters.Add(new ParameterDefinition("exception", ParameterAttributes.None, exceptionType));
            {
                ILProcessor il = forward.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, hook);
                il.Emit(OpCodes.Ret);
            }
            safety.Methods.Add(forward);

            var report = new MethodDefinition("Report", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            report.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, ts.Object));
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
            safety.Methods.Add(report);
            return safety;
        }
    }
}
