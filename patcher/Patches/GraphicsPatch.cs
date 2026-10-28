using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'graphics' patch of Gameloft.Engine.dll (docs/research/pc-textures.md WP0): settings.json <c>graphics.profile</c>
    /// (auto, mid, high, ultra) chooses which of Gameloft's own GPU blocks in GameOptions_android.json applies, and the effective
    /// options are logged. The game resolves its profile natively from that JSON in
    /// <c>GameOptionPerformanceProfileComponent.OnCreate</c> (once per process; restart to change it):
    /// <code>
    /// protected override void OnCreate()
    /// {
    ///     base.OnCreate();
    ///     if (PlatformInfo.CurrentPlatform == PlatformType.Linux) return;
    ///     if (!GameOptionManager.IsGameOptionInitialized())
    ///     {
    ///         string text = "";
    ///         if (string.IsNullOrEmpty(GameOptionsDefaultJson) || !GlfStream.Exists(GameOptionsDefaultJson)) return;
    ///         text = McvsGraphics.GameOptionsJson(AssetStream.ReadAllText(GameOptionsDefaultJson));   // added call
    ///         string clientId = GetComponent&lt;GameDefinitionComponent&gt;()?.ClientID;
    ///         GameOptionManager.InitializeGameOption(text, clientId);
    ///     }
    ///     ApplyGameOptions();
    ///     McvsGraphics.OptionsApplied(this);                                                         // added
    /// }
    /// </code>
    /// The helper type <c>Gameloft.Engine.McvsGraphics</c> forwards to <c>McvsOffline.GraphicsHooks.GameOptionsJson(string)</c> and
    /// <c>GameOptionsApplied(object)</c> (with <c>m_options</c>) of the embedded server, each call in a try/catch around a
    /// [NoInlining] forwarder, so a missing or failing McvsOffline.Server.dll leaves the JSON text as read (today's behaviour)
    /// and the boot goes on. What the rewrite changes (the GPU selection, the chosen block's priority steps, the world options it
    /// keeps at the MidGPU values) is documented in hosts/embedded/Graphics/GameOptionsProfile.cs; with <c>auto</c> (the
    /// default) the text is passed through unchanged. Checked against the IL fingerprint of the verified 1.17.32 build first;
    /// a second run fails it.
    /// </summary>
    public sealed class GraphicsPatch : IlPatch
    {
        public const string TypeName = "Gameloft.Engine.GameOptionPerformanceProfileComponent";
        public const string HelperType = "McvsGraphics";
        public const string HooksType = "McvsOffline.GraphicsHooks";
        public const string OnCreateFingerprint = "ca2930ef38ded244fe404f825ac75f7ad39e12b4210980ec790eb23423694e19";

        public override string Id => "graphics";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "graphics profile: settings graphics.profile picks Gameloft's own GPU block in GameOptions_android.json, effective options logged (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            TypeDefinition component = RequireType(module, TypeName);
            if (module.GetType("Gameloft.Engine." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched: run the patcher on your original APK");
            MethodDefinition onCreate = RequireMethod(component, "OnCreate",
                m => !m.IsStatic && !m.HasParameters && m.IsVirtual && m.ReturnType.MetadataType == MetadataType.Void, "protected override void OnCreate()");
            RequireFingerprint(onCreate, OnCreateFingerprint, "GameOptionPerformanceProfileComponent.OnCreate()");
            FieldDefinition options = RequireField(component, "m_options",
                "System.Collections.Generic.Dictionary`2<System.String,System.Object>");

            var code = onCreate.Body.Instructions;
            int read = Enumerable.Range(0, code.Count).Single(i => IsCall(code[i], "Gameloft.Engine.AssetStream", "ReadAllText"));
            int apply = Enumerable.Range(0, code.Count).Single(i => IsCall(code[i], TypeName, "ApplyGameOptions"));
            Instruction last = code[code.Count - 1];
            if (!(code[read].OpCode == OpCodes.Call && code[read + 1].OpCode == OpCodes.Stloc_0 &&
                  ((MethodReference)code[read].Operand).ReturnType.MetadataType == MetadataType.String &&
                  apply == code.Count - 2 && last.OpCode == OpCodes.Ret && code[apply - 1].OpCode == OpCodes.Ldarg_0 &&
                  code.Count(i => IsCall(i, "Gameloft.Engine.GameOptionManager", "InitializeGameOption")) == 1))
                throw new PatchException(onCreate.FullName + ": the ReadAllText / ApplyGameOptions sites are not where the fingerprint says:\n" + Dump(onCreate));

            TypeSystem ts = module.TypeSystem;
            MethodReference hookJson = ImportServerMethod(module, context, HooksType, "GameOptionsJson", "System.String", "System.String");
            MethodReference hookApplied = ImportServerMethod(module, context, HooksType, "GameOptionsApplied", "System.Void", "System.Object");

            // ---- the helper type
            var helper = new TypeDefinition("Gameloft.Engine", HelperType,
                                            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit |
                                            TypeAttributes.Class, ts.Object);
            module.Types.Add(helper);

            // [NoInlining] static string ForwardJson(string json) => GraphicsHooks.GameOptionsJson(json);
            // [NoInlining] static void ForwardApplied(object options) => GraphicsHooks.GameOptionsApplied(options);
            MethodDefinition forwardJson = Forwarder(helper, "ForwardJson", ts.String, hookJson, ts.String);
            MethodDefinition forwardApplied = Forwarder(helper, "ForwardApplied", ts.Void, hookApplied, ts.Object);

            // internal static string GameOptionsJson(string json)
            // {
            //     string result = json;
            //     try { string t = ForwardJson(json); if (t != null) result = t; } catch { }
            //     return result;
            // }
            var gameOptionsJson = new MethodDefinition("GameOptionsJson", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.String);
            gameOptionsJson.Parameters.Add(new ParameterDefinition("json", ParameterAttributes.None, ts.String));
            var vResult = new VariableDefinition(ts.String);
            var vText = new VariableDefinition(ts.String);
            gameOptionsJson.Body.Variables.Add(vResult);
            gameOptionsJson.Body.Variables.Add(vText);
            gameOptionsJson.Body.InitLocals = true;
            {
                ILProcessor il = gameOptionsJson.Body.GetILProcessor();
                Instruction result = il.Create(OpCodes.Ldloc, vResult);
                Instruction leaveTry = il.Create(OpCodes.Leave, result);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stloc, vResult);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
                il.Emit(OpCodes.Call, forwardJson);
                il.Emit(OpCodes.Stloc, vText);
                il.Emit(OpCodes.Ldloc, vText);
                il.Emit(OpCodes.Brfalse, leaveTry);
                il.Emit(OpCodes.Ldloc, vText);
                il.Emit(OpCodes.Stloc, vResult);
                il.Append(leaveTry);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Leave, result);
                il.Append(result);
                il.Emit(OpCodes.Ret);
                gameOptionsJson.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = ts.Object,
                });
                gameOptionsJson.Body.OptimizeMacros();
            }
            helper.Methods.Add(gameOptionsJson);

            // internal static void OptionsApplied(GameOptionPerformanceProfileComponent component)
            // {
            //     try { ForwardApplied(component == null ? null : component.m_options); } catch { }
            // }
            var optionsApplied = new MethodDefinition("OptionsApplied", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            optionsApplied.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, component));
            {
                ILProcessor il = optionsApplied.Body.GetILProcessor();
                Instruction ret = il.Create(OpCodes.Ret);
                Instruction call = il.Create(OpCodes.Call, forwardApplied);
                Instruction isNull = il.Create(OpCodes.Ldnull);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
                il.Emit(OpCodes.Brfalse, isNull);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, options);
                il.Emit(OpCodes.Br, call);
                il.Append(isNull);
                il.Append(call);
                il.Emit(OpCodes.Leave, ret);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Leave, ret);
                il.Append(ret);
                optionsApplied.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = ret, CatchType = ts.Object,
                });
                optionsApplied.Body.OptimizeMacros();
            }
            helper.Methods.Add(optionsApplied);

            // ---- change: the text read from GameOptions_android.json goes through McvsGraphics.GameOptionsJson (string -> string,
            // the stack shape stays the same) and McvsGraphics.OptionsApplied(this) runs after ApplyGameOptions. Five bytes each:
            // the body's short branches (brtrue.s IL_0060, the farthest) stay in range.
            ILProcessor body = onCreate.Body.GetILProcessor();
            body.InsertAfter(code[read], body.Create(OpCodes.Call, gameOptionsJson));
            body.InsertBefore(last, body.Create(OpCodes.Ldarg_0));
            body.InsertBefore(last, body.Create(OpCodes.Call, optionsApplied));
            context.Notes.Add(onCreate.FullName + ": GameOptions_android.json goes through " + HooksType + ".GameOptionsJson before " +
                              "InitializeGameOption (settings graphics.profile: auto, mid, high, ultra), and " + HooksType +
                              ".GameOptionsApplied logs the effective options");
        }

        private static MethodDefinition Forwarder(TypeDefinition owner, string name, TypeReference returnType, MethodReference target, params TypeReference[] parameters)
        {
            var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, returnType);
            method.ImplAttributes |= MethodImplAttributes.NoInlining;
            for (int i = 0; i < parameters.Length; i++) method.Parameters.Add(new ParameterDefinition("a" + i, ParameterAttributes.None, parameters[i]));
            ILProcessor il = method.Body.GetILProcessor();
            for (int i = 0; i < parameters.Length; i++) il.Emit(OpCodes.Ldarg, method.Parameters[i]);
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            method.Body.OptimizeMacros();
            owner.Methods.Add(method);
            return method;
        }
    }
}
