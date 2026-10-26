using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'signatures' patch of Game.dll (device test 6: the kill-signature screen froze for good after unlock-all; the
    /// watchdog's managed dump had the game thread in <c>List&lt;string&gt;.Contains</c> under
    /// <c>GetSignatureAssetsList</c> ← <c>GetSignatureAvatarsList</c> ← <c>GameOfflineStore.GetKillSignItems</c>).
    /// <list type="bullet">
    /// <item>G1 <c>PlayerProfileComponent.GetSignatureAssetsList(tableName, ownedIds, newIds)</c> sets <c>locked</c> and
    /// <c>isNew</c> of every row of the table with <c>ownedIds.Contains(item.id)</c> and <c>newIds.Contains(item.id)</c>: list
    /// scans, so one call costs rows × (owned + new) string compares, ~4.6 million for 1,518 avatars after unlock-all. Every
    /// signature lookup goes through it (the shop's availability check per kill-signature item, GetSignatureObject for
    /// matchmaking, the HUD and leaderboards, level-up rewards). A list of more than <see cref="SetThreshold"/> ids is now copied
    /// once per call into a <c>HashSet&lt;string&gt;</c> (default comparer, like <c>List.Contains</c>); a shorter one is used as it
    /// is; both are read through <c>ICollection&lt;string&gt;.Contains</c>. Same flags for every row, and a null list still throws
    /// a NullReferenceException at its first use.</item>
    /// <item>G2 <c>GameOfflineStore.GetKillSignItems(subtype, profile)</c> (the signature screen) calls
    /// <c>profile.GetSignatureAvatarsList()</c> (<c>...BackgroundsList()</c> for backgrounds) once per owned item to find its
    /// image: 1,512 rebuilt lists after unlock-all. Each of the two sites now keeps the list of its first call in a new local
    /// for the rest of that call of GetKillSignItems. Nothing in the loop changes the profile's lists or the table, so every
    /// later call would have returned the same rows with the same flags.</item>
    /// </list>
    /// Both methods are checked against the IL fingerprint of the verified 1.17.32 build first.
    /// </summary>
    public sealed class SignaturesPatch : IlPatch
    {
        public const string ProfileType = "Game.PlayerProfileComponent";
        public const string StoreType = "Game.GameOfflineStore";
        public const string AssetsListFingerprint = "3d3edcc78e31a448c549d9fa98ac114eb7a75070d69d0d17ccc5d56b7586eb63";
        public const string KillSignItemsFingerprint = "b4d15713ed2727489059029fccad25270b28d4af7c1b699eb7f40f16dcdc513a";

        /// <summary>Lists up to this many ids keep the shipped scan (it is faster than hashing for a handful of ids).</summary>
        public const int SetThreshold = 16;

        public override string Id => "signatures";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "kill signatures: owned/new id lookups in sets, one avatar list per signature screen refresh (Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            TypeDefinition profile = RequireType(module, ProfileType);
            TypeDefinition store = RequireType(module, StoreType);
            MethodDefinition assets = RequireMethod(profile, "GetSignatureAssetsList",
                m => !m.IsStatic && m.Parameters.Count == 3 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                     m.Parameters[1].ParameterType.FullName == ListOfString && m.Parameters[2].ParameterType.FullName == ListOfString,
                "instance List<SignatureItemData> GetSignatureAssetsList(string, List<string>, List<string>)");
            RequireFingerprint(assets, AssetsListFingerprint, "PlayerProfileComponent.GetSignatureAssetsList");
            MethodDefinition items = RequireMethod(store, "GetKillSignItems",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                     m.Parameters[1].ParameterType.FullName == ProfileType,
                "instance Dictionary<string, object> GetKillSignItems(string, PlayerProfileComponent)");
            RequireFingerprint(items, KillSignItemsFingerprint, "GameOfflineStore.GetKillSignItems");

            // G1 sites (verified offsets): IL_0023 ldarg.2 / IL_002a List<string>.Contains (owned), IL_0038 ldarg.3 / IL_003f (new).
            var code = assets.Body.Instructions;
            Instruction At(int offset) => code.Single(i => i.Offset == offset);
            Instruction ownedLoad = At(0x23), ownedContains = At(0x2a), newLoad = At(0x38), newContains = At(0x3f);
            if (ownedLoad.OpCode != OpCodes.Ldarg_2 || newLoad.OpCode != OpCodes.Ldarg_3 ||
                !IsCall(ownedContains, ListOfString, "Contains") || !IsCall(newContains, ListOfString, "Contains"))
                throw new PatchException(assets.FullName + ": the verified sites moved:\n" + Dump(assets));

            // G2 sites: the one GetSignatureAvatarsList() and the one GetSignatureBackgroundsList() call of the method.
            Instruction avatarsCall = SingleCall(items, "GetSignatureAvatarsList");
            Instruction backgroundsCall = SingleCall(items, "GetSignatureBackgroundsList");

            // ---- references, each checked against the game's own BCL
            TypeSystem ts = module.TypeSystem;
            var listOfString = (GenericInstanceType)((MethodReference)ownedContains.Operand).DeclaringType;
            MethodReference listCount = Checked(module, new MethodReference("get_Count", ts.Int32, listOfString) { HasThis = true });

            var collectionOpen = new TypeReference("System.Collections.Generic", "ICollection`1", module, ts.CoreLibrary);
            collectionOpen.GenericParameters.Add(new GenericParameter("T", collectionOpen));
            var collectionOfString = new GenericInstanceType(collectionOpen);
            collectionOfString.GenericArguments.Add(ts.String);
            var setContains = new MethodReference("Contains", ts.Boolean, collectionOfString) { HasThis = true };
            setContains.Parameters.Add(new ParameterDefinition(collectionOpen.GenericParameters[0]));
            setContains = Checked(module, setContains);

            AssemblyNameReference systemCore = module.AssemblyReferences.SingleOrDefault(a => a.Name == "System.Core");
            if (systemCore == null) throw new PatchException(module.Name + " does not reference System.Core");
            var setOpen = new TypeReference("System.Collections.Generic", "HashSet`1", module, systemCore);
            setOpen.GenericParameters.Add(new GenericParameter("T", setOpen));
            var setOfString = new GenericInstanceType(setOpen);
            setOfString.GenericArguments.Add(ts.String);
            var enumerableOpen = new TypeReference("System.Collections.Generic", "IEnumerable`1", module, ts.CoreLibrary);
            enumerableOpen.GenericParameters.Add(new GenericParameter("T", enumerableOpen));
            var enumerableOfT = new GenericInstanceType(enumerableOpen);
            enumerableOfT.GenericArguments.Add(setOpen.GenericParameters[0]);
            var setCtor = new MethodReference(".ctor", ts.Void, setOfString) { HasThis = true };
            setCtor.Parameters.Add(new ParameterDefinition(enumerableOfT));
            setCtor = Checked(module, setCtor);

            // ---- change: G1
            assets.Body.SimplifyMacros();
            ILProcessor il = assets.Body.GetILProcessor();
            var owned = new VariableDefinition(module.ImportReference(collectionOfString));
            var fresh = new VariableDefinition(module.ImportReference(collectionOfString));
            assets.Body.Variables.Add(owned);
            assets.Body.Variables.Add(fresh);
            assets.Body.InitLocals = true;
            Instruction first = code[0];
            foreach (Instruction i in Lookup(il, assets.Parameters[1], owned, listCount, setCtor)) il.InsertBefore(first, i);
            foreach (Instruction i in Lookup(il, assets.Parameters[2], fresh, listCount, setCtor)) il.InsertBefore(first, i);
            ownedLoad.OpCode = OpCodes.Ldloc;
            ownedLoad.Operand = owned;
            newLoad.OpCode = OpCodes.Ldloc;
            newLoad.Operand = fresh;
            ownedContains.Operand = setContains;
            newContains.Operand = setContains;
            assets.Body.OptimizeMacros();
            context.Notes.Add(assets.FullName + ": ownedIds/newIds of more than " + SetThreshold + " ids are looked up in a HashSet<string> built once " +
                              "per call (List.Contains per table row before: rows x ids compares)");

            // ---- change: G2
            items.Body.SimplifyMacros();
            il = items.Body.GetILProcessor();
            foreach (Instruction call in new[] { avatarsCall, backgroundsCall })
            {
                var getList = (MethodReference)call.Operand;
                var cache = new VariableDefinition(getList.ReturnType);
                items.Body.Variables.Add(cache);
                // Stack at the site: ..., profile. Becomes:
                //   ldloc cache; brtrue HAVE; call getList; stloc cache; br LOAD; HAVE: pop; LOAD: ldloc cache
                // The site instruction itself becomes the first one, so the null-check branch that targets it stays valid.
                Instruction next = call.Next;
                Instruction have = il.Create(OpCodes.Pop);
                Instruction load = il.Create(OpCodes.Ldloc, cache);
                call.OpCode = OpCodes.Ldloc;
                call.Operand = cache;
                il.InsertBefore(next, il.Create(OpCodes.Brtrue, have));
                il.InsertBefore(next, il.Create(OpCodes.Call, getList));
                il.InsertBefore(next, il.Create(OpCodes.Stloc, cache));
                il.InsertBefore(next, il.Create(OpCodes.Br, load));
                il.InsertBefore(next, have);
                il.InsertBefore(next, load);
            }
            items.Body.InitLocals = true;
            items.Body.OptimizeMacros();
            context.Notes.Add(items.FullName + ": GetSignatureAvatarsList()/GetSignatureBackgroundsList() run once per call and are reused for every " +
                              "owned item (once per owned item before)");
        }

        private const string ListOfString = "System.Collections.Generic.List`1<System.String>";

        /// <summary>
        /// <code>
        /// local = ids;
        /// if (ids != null &amp;&amp; ids.Count &gt; SetThreshold) local = new HashSet&lt;string&gt;(ids);
        /// </code>
        /// </summary>
        private static Instruction[] Lookup(ILProcessor il, ParameterDefinition ids, VariableDefinition local, MethodReference count, MethodReference setCtor)
        {
            Instruction done = il.Create(OpCodes.Nop);
            return new[]
            {
                il.Create(OpCodes.Ldarg, ids),
                il.Create(OpCodes.Stloc, local),
                il.Create(OpCodes.Ldarg, ids),
                il.Create(OpCodes.Brfalse, done),
                il.Create(OpCodes.Ldarg, ids),
                il.Create(OpCodes.Callvirt, count),
                il.Create(OpCodes.Ldc_I4, SetThreshold),
                il.Create(OpCodes.Ble, done),
                il.Create(OpCodes.Ldarg, ids),
                il.Create(OpCodes.Newobj, setCtor),
                il.Create(OpCodes.Stloc, local),
                done,
            };
        }

        private static Instruction SingleCall(MethodDefinition method, string name)
        {
            var calls = method.Body.Instructions.Where(i => IsCall(i, ProfileType, name) && i.OpCode == OpCodes.Call &&
                                                            !((MethodReference)i.Operand).HasParameters).ToList();
            if (calls.Count != 1)
                throw new PatchException(method.FullName + ": expected one call of " + name + "(), found " + calls.Count + ":\n" + Dump(method));
            return calls[0];
        }

        /// <summary>Imports <paramref name="reference"/> after checking that the game's assemblies define it.</summary>
        private static MethodReference Checked(ModuleDefinition module, MethodReference reference)
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
}
