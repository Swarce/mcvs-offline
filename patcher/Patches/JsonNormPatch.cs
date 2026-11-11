using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'jsonnorm' patch of Gameloft.Util.dll (build 30; the owner's S22 log: every save of the ~611 KB unlock-all
    /// gameProfile stopped the game thread for 3 to 5 s, on the results screen, when Play is pressed and back in the menu).
    /// <para>
    /// <c>ProfileSection.IsModified</c> is <c>Json.NormalizeJsonString(SavedProfileString) != Json.NormalizeJsonString(ProfileString)</c>
    /// and a save evaluates it twice (<c>PlayerProfileComponent.DoSaveProfile</c>, then <c>ProfileSection.SaveImplementation</c>):
    /// four normalizations of the whole profile text per save (each a Newtonsoft parse, a recursive sort of every object's
    /// properties, a re-serialization), on the game thread. <c>NormalizeJsonString</c> is a pure function of its string, so:
    /// <code>
    /// public static string NormalizeJsonString(string json)
    /// {
    ///     string cached = McvsJsonNorm.Cached(json);                 // added: same characters as a kept large input
    ///     if (cached != null) return cached;
    ///     if (string.IsNullOrEmpty(json)) return null;
    ///     return McvsJsonNorm.Remember(NormalizeToken(Load(json)).ToCompactString(), json);   // added: keep large results
    /// }
    /// </code>
    /// <c>McvsOffline.GameDataHooks</c> keeps the last <c>NormalizeCacheSlots</c> results of inputs of at least
    /// <c>NormalizeCacheMinChars</c> characters and answers only an input with exactly the same characters (ordinal), with the
    /// very string the game's code returned for it. After a save the saved text is the text that save normalized, so the next
    /// save computes one normalization instead of four, and the second IsModified of a save none. Exceptions (a text that is not
    /// JSON) are the game's: nothing is kept for them. Without McvsOffline.Server.dll both helpers return at once (a
    /// <c>hooksMissing</c> latch) and the method is the game's.
    /// </para>
    /// The method is checked against the IL fingerprint of the verified build first.
    /// </summary>
    public sealed class JsonNormPatch : IlPatch
    {
        public const string TypeName = "Gameloft.Util.Json";
        public const string HelperNamespace = "Gameloft.Util";
        public const string HelperType = "McvsJsonNorm";
        public const string HooksType = "McvsOffline.GameDataHooks";
        public const string NormalizeFingerprint = "1c1bc78cc5d2a2eaec3877275e17bab8b52caf49e834a5fd36fc3a79920cba07";
        public const string PcNormalizeFingerprint = NormalizeFingerprint;

        public override string Id => "jsonnorm";

        public override string AssemblyFile => "Gameloft.Util.dll";

        public override string Description => "profile saves: a large JSON text normalized before is not normalized again (Json.NormalizeJsonString; Gameloft.Util.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            PatchSites.RequireBuildAssembly(module, context, Id);
            string fingerprint = ForBuild(context, NormalizeFingerprint, PcNormalizeFingerprint);
            TypeDefinition json = RequireType(module, TypeName);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Gameloft.Util.dll is already patched: run the patcher on your original game files");
            MethodDefinition normalize = RequireMethod(json, "NormalizeJsonString",
                m => m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                     m.ReturnType.MetadataType == MetadataType.String,
                "static string NormalizeJsonString(string)");
            RequireFingerprint(normalize, fingerprint, "Json.NormalizeJsonString");
            // The verified body: IsNullOrEmpty -> ldnull; ret, else Load, NormalizeToken, ToCompactString; ret (the last ret).
            var code = normalize.Body.Instructions;
            Instruction last = code[code.Count - 1];
            if (last.OpCode != OpCodes.Ret || !(last.Previous.OpCode == OpCodes.Call && last.Previous.Operand is MethodReference m0 &&
                                                m0.Name == "ToCompactString") || normalize.Body.HasExceptionHandlers)
                throw new PatchException(normalize.FullName + ": the result site (call ToCompactString; ret) is not where the fingerprint says:\n" + Dump(normalize));
            if (code.Count(i => i.OpCode == OpCodes.Ret) != 2 || code.Any(i => i.Operand == last))
                throw new PatchException(normalize.FullName + ": unexpected returns or a branch to the last ret");

            // ---- references
            TypeSystem ts = module.TypeSystem;
            MethodReference hookCached = ImportServerMethod(module, context, HooksType, "CachedNormalizedJson", "System.String", "System.String");
            MethodReference hookRemember = ImportServerMethod(module, context, HooksType, "RememberNormalizedJson", "System.String",
                                                              "System.String", "System.String");

            // ---- the helper type
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            var hooksMissing = new FieldDefinition("hooksMissing", FieldAttributes.Private | FieldAttributes.Static, ts.Boolean);
            helper.Fields.Add(hooksMissing);
            MethodDefinition forwardCached = PatchSites.Forwarder(helper, "ForwardCached", ts.String, hookCached, ts.String);
            MethodDefinition forwardRemember = PatchSites.Forwarder(helper, "ForwardRemember", ts.String, hookRemember, ts.String, ts.String);
            // internal static string Cached(string json)   { string r = null;  if (!hooksMissing) try { r = ForwardCached(json); } catch { hooksMissing = true; } return r; }
            // internal static string Remember(string n, string json) { string r = n; if (!hooksMissing) try { r = ForwardRemember(n, json); } catch { hooksMissing = true; } return r; }
            MethodDefinition cached = Guarded(helper, "Cached", hooksMissing, forwardCached, false, ts.String);
            MethodDefinition remember = Guarded(helper, "Remember", hooksMissing, forwardRemember, true, ts.String, ts.String);

            // ---- change
            {
                MethodBody body = normalize.Body;
                ILProcessor il = body.GetILProcessor();
                Instruction first = code[0];
                Instruction compute = il.Create(OpCodes.Pop);
                foreach (Instruction i in new[]
                         {
                             il.Create(OpCodes.Ldarg_0),
                             il.Create(OpCodes.Call, cached),
                             il.Create(OpCodes.Dup),
                             il.Create(OpCodes.Brfalse, compute),
                             il.Create(OpCodes.Ret),
                             compute,
                         })
                    il.InsertBefore(first, i);
                il.InsertBefore(last, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(last, il.Create(OpCodes.Call, remember));
                PatchSites.FixShortBranches(normalize);
            }
            context.Notes.Add(normalize.FullName + ": the result for a large input with the same characters as one normalized before comes from " +
                              HooksType + " (4 normalizations of the whole profile per save before, 1 after)");
        }

        /// <summary>
        /// <code>
        /// internal static string name(args) { string r = passThrough ? arg0 : null; if (!hooksMissing) { try { r = forward(args); } catch { hooksMissing = true; } } return r; }
        /// </code>
        /// </summary>
        private static MethodDefinition Guarded(TypeDefinition owner, string name, FieldDefinition hooksMissing, MethodDefinition forward, bool passThrough,
                                                params TypeReference[] parameters)
        {
            TypeSystem ts = owner.Module.TypeSystem;
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.String);
            for (int i = 0; i < parameters.Length; i++) method.Parameters.Add(new ParameterDefinition("a" + i, ParameterAttributes.None, parameters[i]));
            method.Body.Variables.Add(new VariableDefinition(ts.String));
            method.Body.InitLocals = true;
            ILProcessor il = method.Body.GetILProcessor();
            Instruction result = il.Create(OpCodes.Ldloc_0);
            il.Emit(passThrough ? OpCodes.Ldarg_0 : OpCodes.Ldnull);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldsfld, hooksMissing);
            il.Emit(OpCodes.Brtrue_S, result);
            Instruction tryStart = PatchSites.LoadArgument(0);
            il.Append(tryStart);
            for (int i = 1; i < parameters.Length; i++) il.Append(PatchSites.LoadArgument(i));
            il.Emit(OpCodes.Call, forward);
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Leave_S, result);
            Instruction handler = il.Create(OpCodes.Pop);
            il.Append(handler);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Stsfld, hooksMissing);
            il.Emit(OpCodes.Leave_S, result);
            il.Append(result);
            il.Emit(OpCodes.Ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = ts.Object,
            });
            owner.Methods.Add(method);
            return method;
        }
    }
}
