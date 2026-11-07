using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch 'fpslimit' of Game.dll (build 27, default on PC; Android has neither site): settings.json
    /// <c>graphics.pcFpsLimit</c> can replace the frame rate limit the Steam game gives its WCP pacer, up to "unlimited".
    /// <para>
    /// The Steam game sets the limit at exactly two places, both in <c>Game.GamePreferencesComponent</c>:
    /// <code>
    /// LoadPreferencesWindows():          GameApplication.Instance.WCPFramerateLimit = McvsFrameRate.Limit(num);   // IL_0977 ldloc.0
    /// OnPreferencesChangedWindows("FPS"): GameApplication.Instance.WCPFramerateLimit = McvsFrameRate.Limit(FPS);   // IL_0179 call get_FPS
    /// </code>
    /// (the start value from LocalStorage "FPS", and the FPS LIMIT row of Settings › Performance: 30, 60, 90 or 120). The setter is a
    /// P/Invoke to WCPToolkit's render manager, which stores <c>max(1, value)</c> and spins each frame until 1e6/limit µs minus
    /// 100 µs have passed (docs/design/pc-port.md §8.11). A value of 0 or -1 (Android's "no limit") would give 1 FPS and NaN would
    /// hang the pacer, so "unlimited" must be a finite limit above any screen: the hook answers 1000.
    /// </para>
    /// <para>
    /// <c>Game.McvsFrameRate.Limit</c> asks <c>McvsOffline.GraphicsHooks.FrameRateLimit(float)</c> through a [NoInlining] forwarder
    /// inside a try/catch; without McvsOffline.Server.dll (or when the hook throws) the game's value goes on unchanged, and a
    /// <c>hooksMissing</c> latch keeps that to one exception. With the setting at its default "game" the hook returns the game's
    /// value bit for bit, so nothing changes unless the player picks another limit. Both methods are checked against their IL
    /// fingerprints of Steam build 6372049 first; a second run fails.
    /// </para>
    /// </summary>
    public sealed class FpsLimitPatch : IlPatch
    {
        public const string TypeName = "Game.GamePreferencesComponent";
        public const string HelperNamespace = "Game";
        public const string HelperType = "McvsFrameRate";
        public const string HooksType = GraphicsPatch.HooksType;

        /// <summary><c>GamePreferencesComponent.LoadPreferencesWindows()</c> of Steam 6372049, as shipped (patch 'headless' checks it too).</summary>
        public const string PcLoadFingerprint = HeadlessGamePatch.PcLoadPreferencesFingerprint;

        /// <summary>
        /// The same method after patch 'headless' (mandatory on PC, earlier in the plan) put its H8 guard before <c>instance = this</c>
        /// (offsets as the writer lays them out): the install sees this one.
        /// </summary>
        public const string PcLoadAfterHeadlessFingerprint = "30be04a39d229c6db386a32ed4ae0cc89e468467157ef0f240418d50679f286f";

        /// <summary><c>GamePreferencesComponent.OnPreferencesChangedWindows(object, PropertyInfo)</c> of Steam 6372049.</summary>
        public const string PcChangedFingerprint = "236ce209771c3525be2fdffc92a8697f13871b1b3b655b0504287992470a087e";

        /// <summary>The native setter both sites call.</summary>
        public const string SetterOwner = "Gameloft.Engine.GameApplication";
        public const string Setter = "set_WCPFramerateLimit";

        public override string Id => "fpslimit";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "frame rate: settings graphics.pcFpsLimit can replace the in-game FPS LIMIT (30 to 240, or unlimited) (Game.dll, PC)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            string loadFingerprint = ForBuild(context, null, PcLoadFingerprint);
            string loadAfterHeadless = ForBuild(context, null, PcLoadAfterHeadlessFingerprint);
            string changedFingerprint = ForBuild(context, null, PcChangedFingerprint);
            PatchSites.RequireBuildAssembly(module, context, Id);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Game.dll is already patched (" + HelperNamespace + "." + HelperType + "): install from the game's own file");
            TypeDefinition preferences = RequireType(module, TypeName);
            MethodDefinition load = RequireMethod(preferences, "LoadPreferencesWindows",
                m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void, "void LoadPreferencesWindows()");
            MethodDefinition changed = RequireMethod(preferences, "OnPreferencesChangedWindows",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[1].ParameterType.FullName == "System.Reflection.PropertyInfo" &&
                     m.ReturnType.MetadataType == MetadataType.Void,
                "void OnPreferencesChangedWindows(object, PropertyInfo)");
            // Offsets as the writer lays them out: an earlier patch of this run leaves them stale in memory.
            PatchSites.ComputeOffsets(load.Body);
            if (Fingerprint(load) != loadAfterHeadless)
                RequireFingerprint(load, loadFingerprint, "GamePreferencesComponent.LoadPreferencesWindows(), as shipped or after patch 'headless'");
            PatchSites.ComputeOffsets(changed.Body);
            RequireFingerprint(changed, changedFingerprint, "GamePreferencesComponent.OnPreferencesChangedWindows()");

            // Exactly two setter calls in the whole module, one in each method, each right after its value:
            //   LoadPreferencesWindows      IL_0972 call get_Instance; IL_0977 ldloc.0;      IL_0978 callvirt set_WCPFramerateLimit (as shipped)
            //   OnPreferencesChangedWindows IL_0173 call get_Instance; IL_0178 ldarg.0; IL_0179 call get_FPS; IL_017e callvirt set_WCPFramerateLimit
            var all = new List<KeyValuePair<MethodDefinition, Instruction>>();
            foreach (TypeDefinition type in AllTypes(module))
            {
                foreach (MethodDefinition method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (Instruction i in method.Body.Instructions.Where(i => IsCall(i, SetterOwner, Setter)))
                        all.Add(new KeyValuePair<MethodDefinition, Instruction>(method, i));
                }
            }
            if (all.Count != 2 || all.Count(kv => kv.Key == load) != 1 || all.Count(kv => kv.Key == changed) != 1)
                throw new PatchException("Game.dll: expected one call to " + SetterOwner + "::" + Setter + " in each of LoadPreferencesWindows and " +
                                         "OnPreferencesChangedWindows and none elsewhere, found " + all.Count + ": " +
                                         string.Join(", ", all.Select(kv => kv.Key.FullName)));
            Instruction loadSite = RequireSite(load, all.Single(kv => kv.Key == load).Value,
                                               i => i.OpCode == OpCodes.Ldloc_0 && load.Body.Variables[0].VariableType.MetadataType == MetadataType.Single &&
                                                    IsCall(i.Previous, SetterOwner, "get_Instance"));
            Instruction changedSite = RequireSite(changed, all.Single(kv => kv.Key == changed).Value,
                                                  i => i.OpCode == OpCodes.Call && IsCall(i, TypeName, "get_FPS") &&
                                                       ((MethodReference)i.Operand).ReturnType.MetadataType == MetadataType.Single &&
                                                       i.Previous.OpCode == OpCodes.Ldarg_0 && IsCall(i.Previous.Previous, SetterOwner, "get_Instance"));

            TypeSystem ts = module.TypeSystem;
            MethodReference hook = ImportServerMethod(module, context, HooksType, "FrameRateLimit", "System.Single", "System.Single");

            // ---- the helper type
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            // private static bool hooksMissing;   (set once a call to McvsOffline.Server.dll failed: the game's value from then on)
            var hooksMissing = new FieldDefinition("hooksMissing", FieldAttributes.Private | FieldAttributes.Static, ts.Boolean);
            helper.Fields.Add(hooksMissing);
            // [NoInlining] private static float Forward(float a0) => GraphicsHooks.FrameRateLimit(a0);
            MethodDefinition forward = PatchSites.Forwarder(helper, "Forward", ts.Single, hook, ts.Single);

            // internal static float Limit(float game)
            // {
            //     float r = game;                                                  // without McvsOffline.Server.dll
            //     if (!hooksMissing) { try { r = Forward(game); } catch { hooksMissing = true; } }
            //     return r;
            // }
            var limit = new MethodDefinition("Limit", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Single);
            limit.Parameters.Add(new ParameterDefinition("game", ParameterAttributes.None, ts.Single));
            var r = new VariableDefinition(ts.Single);
            limit.Body.Variables.Add(r);
            limit.Body.InitLocals = true;
            {
                ILProcessor il = limit.Body.GetILProcessor();
                Instruction result = il.Create(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldsfld, hooksMissing);
                il.Emit(OpCodes.Brtrue_S, result);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
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
                limit.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = ts.Object,
                });
            }
            helper.Methods.Add(limit);

            // ---- change: the value goes through McvsFrameRate.Limit right before each setter call
            foreach (KeyValuePair<MethodDefinition, Instruction> site in new[]
                     {
                         new KeyValuePair<MethodDefinition, Instruction>(load, loadSite),
                         new KeyValuePair<MethodDefinition, Instruction>(changed, changedSite),
                     })
            {
                site.Key.Body.GetILProcessor().InsertAfter(site.Value, Instruction.Create(OpCodes.Call, limit));
                PatchSites.FixShortBranches(site.Key);
            }
            context.Notes.Add(load.FullName + ": the start value of the frame rate limit goes through " + HelperNamespace + "." + HelperType +
                              ".Limit before set_WCPFramerateLimit (settings graphics.pcFpsLimit; 'game' keeps the in-game value)");
            context.Notes.Add(changed.FullName + ": the FPS LIMIT row's value goes through " + HelperNamespace + "." + HelperType +
                              ".Limit before set_WCPFramerateLimit");
        }

        /// <summary>
        /// The instruction that pushes the setter's value (<paramref name="setter"/>'s previous one), checked to be what the fingerprint
        /// says: the value matching <paramref name="value"/>, and no branch into the setter (the inserted call goes between the two).
        /// </summary>
        private static Instruction RequireSite(MethodDefinition method, Instruction setter, System.Func<Instruction, bool> value)
        {
            Instruction before = setter.Previous;
            if (setter.OpCode != OpCodes.Callvirt || before == null || before.Previous == null || !value(before) ||
                ((MethodReference)setter.Operand).Parameters.Count != 1 ||
                ((MethodReference)setter.Operand).Parameters[0].ParameterType.MetadataType != MetadataType.Single)
                throw new PatchException(method.FullName + ": the set_WCPFramerateLimit site is not where the fingerprint says:\n" + Dump(method));
            if (PatchSites.BranchesTo(method.Body, setter).Count != 0 ||
                method.Body.ExceptionHandlers.Any(h => h.TryStart == setter || h.HandlerStart == setter || h.TryEnd == setter || h.HandlerEnd == setter))
                throw new PatchException(method.FullName + ": unexpected branch or handler at the set_WCPFramerateLimit site");
            return before;
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            var stack = new Stack<TypeDefinition>(module.Types);
            while (stack.Count > 0)
            {
                TypeDefinition t = stack.Pop();
                yield return t;
                foreach (TypeDefinition nested in t.NestedTypes) stack.Push(nested);
            }
        }
    }
}
