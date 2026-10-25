using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'nativeonline' patch (device test 1, RUN 1): the native online SDK is only started when the player asks
    /// for it. <c>Gameloft.Engine.OnlinePlatformComponent.Init_private</c> is the single managed entry into
    /// <c>OnlinePlatformComponent_Init_private</c> in libnativeengine.so, which posts the native "GaiaInit" event. Its
    /// handler starts the native "Gaia Thread" (Gaia::Update over 18 service slots, each under a pthread mutex, with
    /// glwebtools/libcurl work against eve.gameloft.com, gameoptions, sink, ingameads and igp). With the internet on, the
    /// game thread stopped pumping its work queue right after this point on the S22 (RUN 1), while in airplane mode (DNS
    /// fails at once) boot went on. Offline play needs none of the native SDK: every Federation call the game makes is
    /// managed C# (report 09 §1), and the native singletons the per-frame calls read are created by MainLoop.Start, not
    /// by GaiaInit (IapStoreComponent_IsInitialized_private reads them safely without it).
    /// <code>
    /// protected static void Init_private(string datacenter, string datacenterCountry, string eveConfig)
    /// {
    ///     try { if (!McvsAllowNativeOnline(datacenter)) return; } catch { return; }    // added
    ///     OnlinePlatformComponent_Init_private(datacenter, datacenterCountry, eveConfig); // original
    /// }
    /// [MethodImpl(NoInlining)] private static bool McvsAllowNativeOnline(string datacenter)
    ///     =&gt; McvsOffline.GameHooks.AllowNativeOnlineInit(datacenter);
    /// </code>
    /// <c>GameHooks.AllowNativeOnlineInit</c> answers settings.json <c>embedded.nativeOnlineSdk</c> (default false) and
    /// logs the call as a boot milestone, so the player can turn the native SDK back on without re-patching. If
    /// McvsOffline.Server.dll cannot be loaded the helper throws inside the try and the native call is skipped.
    /// </summary>
    public sealed class NativeOnlinePatch : IlPatch
    {
        public const string TypeName = "Gameloft.Engine.OnlinePlatformComponent";
        public const string NativeName = "OnlinePlatformComponent_Init_private";
        public const string HelperName = "McvsAllowNativeOnline";
        public const string HooksType = "McvsOffline.GameHooks";

        public override string Id => "nativeonline";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "native online SDK (GaiaInit) only with settings embedded.nativeOnlineSdk";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            TypeDefinition type = RequireType(module, TypeName);
            MethodDefinition init = RequireMethod(type, "Init_private",
                m => m.IsStatic && m.IsFamily && m.Parameters.Count == 3 && m.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.String) &&
                     m.ReturnType.MetadataType == MetadataType.Void,
                "protected static void (string, string, string)");
            if (type.Methods.Any(m => m.Name == HelperName)) throw new PatchException("Gameloft.Engine.dll is already patched: run the patcher on your original APK");

            var code = init.Body.Instructions;
            bool shape = code.Count == 5 &&
                         code[0].OpCode == OpCodes.Ldarg_0 && code[1].OpCode == OpCodes.Ldarg_1 && code[2].OpCode == OpCodes.Ldarg_2 &&
                         IsCall(code[3], TypeName, NativeName) && code[3].OpCode == OpCodes.Call &&
                         ((MethodReference)code[3].Operand).Resolve() is MethodDefinition native && native.IsPInvokeImpl &&
                         code[4].OpCode == OpCodes.Ret && !init.Body.HasExceptionHandlers && !init.Body.HasVariables;
            if (!shape)
            {
                throw new PatchException(init.FullName + " does not match the verified 1.17.32 code " +
                                         "(ldarg.0; ldarg.1; ldarg.2; call " + NativeName + "(string, string, string); ret):\n" + Dump(init));
            }
            MethodReference allow = ImportServerMethod(module, context, HooksType, "AllowNativeOnlineInit", "System.Boolean", "System.String");

            var helper = new MethodDefinition(HelperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                                              module.TypeSystem.Boolean);
            helper.ImplAttributes |= MethodImplAttributes.NoInlining;
            helper.Parameters.Add(new ParameterDefinition("datacenter", ParameterAttributes.None, module.TypeSystem.String));
            ILProcessor h = helper.Body.GetILProcessor();
            h.Emit(OpCodes.Ldarg_0);
            h.Emit(OpCodes.Call, allow);
            h.Emit(OpCodes.Ret);
            type.Methods.Add(helper);

            // try { allowed = McvsAllowNativeOnline(datacenter); } catch (object) { goto ret; }  if (!allowed) goto ret;
            var allowed = new VariableDefinition(module.TypeSystem.Boolean);
            init.Body.Variables.Add(allowed);
            init.Body.InitLocals = true;
            ILProcessor il = init.Body.GetILProcessor();
            Instruction first = code[0];
            Instruction ret = code[4];
            Instruction tryStart = il.Create(OpCodes.Ldarg_0);
            Instruction handler = il.Create(OpCodes.Pop);
            Instruction check = il.Create(OpCodes.Ldloc, allowed);
            var sequence = new[]
            {
                tryStart,
                il.Create(OpCodes.Call, helper),
                il.Create(OpCodes.Stloc, allowed),
                il.Create(OpCodes.Leave, check),
                handler,
                il.Create(OpCodes.Leave, ret),
                check,
                il.Create(OpCodes.Brfalse, ret),
            };
            foreach (Instruction instruction in sequence) il.InsertBefore(first, instruction);
            init.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = handler,
                HandlerStart = handler,
                HandlerEnd = check,
                CatchType = module.TypeSystem.Object,
            });
            context.Notes.Add(init.FullName + ": the native GaiaInit call runs only when McvsOffline.GameHooks.AllowNativeOnlineInit says so " +
                              "(settings embedded.nativeOnlineSdk, default off)");
        }
    }
}
