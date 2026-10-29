using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Mandatory 'headless-engine' patch of Gameloft.Engine.dll, the engine half of <see cref="HeadlessGamePatch"/>:
    /// <list type="bullet">
    /// <item>H3, the safety net: <c>Component.Events.GetSafeAction(action, eventType)</c> is <c>ldarg.0; ret</c> in this
    /// release build (the server build's try/catch wrapper was stripped), so every component lifecycle event (Create, Start,
    /// PostStart, Update, Stop, Destroy, ...) of a TS/GS World could end the player's process. It now returns
    /// <c>new McvsHeadlessSafeAction(action, eventType).Invoke</c>, which calls the action directly for client Worlds (the
    /// client keeps its fail-fast behaviour) and, for <c>World.Headless</c> Worlds only, catches and reports the exception:
    /// <c>[McvsOffline] headless world: &lt;Type&gt;.&lt;Event&gt; threw, the rest of it is skipped: &lt;exception&gt;</c>
    /// on stdout (logcat mono-stdout, first 200) and to <c>McvsOffline.GameHooks.HeadlessComponentException</c>
    /// (bootstrap.log, boot trace). Every such line names a component that needs a precise fix.</item>
    /// <item>H5 <c>SoundGroupSnapshotComponent</c> gets <c>[KeepInHeadlessMode(false)]</c>: pure audio, it pushes and pops
    /// snapshots on the process-wide VoxManager.</item>
    /// <item>H6 <c>GameInputManagerComponent</c>: OnStart/OnUpdate return right after their base call in a headless World,
    /// so a server World does not poll the gamepad a second time per frame or register device callbacks. The component
    /// stays (cheats call <c>GetGameInputManager()</c> unguarded).</item>
    /// </list>
    /// </summary>
    public sealed class HeadlessEnginePatch : IlPatch
    {
        public const string SafeActionType = "McvsHeadlessSafeAction";
        public const string ReportPrefix = "[McvsOffline] headless world: ";

        public override string Id => "headless-engine";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "headless TS/GS Worlds: lifecycle exceptions reported instead of fatal, audio/input untouched (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            PatchSites.RequireBuildCorlib(module, context, Id);
            // ---- locate and verify
            TypeDefinition component = RequireType(module, "Gameloft.Engine.Component");
            TypeDefinition world = RequireType(module, "Gameloft.Engine.World");
            TypeDefinition eventType = RequireType(module, "Gameloft.Engine.ComponentEventType");
            if (!eventType.IsEnum) throw new PatchException(eventType.FullName + " is not an enum");
            TypeDefinition keep = RequireType(module, HeadlessGamePatch.KeepAttribute);
            MethodDefinition keepCtor = RequireMethod(keep, ".ctor", m => !m.IsStatic && m.Parameters.Count == 1 &&
                                                                    m.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean, "constructor (bool)");
            if (module.GetType("Gameloft.Engine." + SafeActionType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched: run the patcher on your original APK");

            MethodDefinition getWorld = RequireMethod(component, "get_World", m => !m.IsStatic && !m.HasParameters && m.ReturnType.FullName == world.FullName, "World getter");
            MethodDefinition getHeadless = RequireMethod(world, "get_Headless", m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Boolean, "bool getter");

            TypeDefinition events = component.NestedTypes.SingleOrDefault(t => t.Name == "Events");
            if (events == null) throw new PatchException(component.FullName + "/Events not found");
            MethodDefinition getSafe = RequireMethod(events, "GetSafeAction", m => m.IsStatic && m.Parameters.Count == 2 &&
                                                                               m.Parameters[1].ParameterType.FullName == eventType.FullName, "static GetSafeAction(Action<Component>, ComponentEventType)");
            var actionType = getSafe.Parameters[0].ParameterType as GenericInstanceType;
            if (actionType == null || actionType.ElementType.FullName != "System.Action`1" || actionType.GenericArguments[0].FullName != component.FullName ||
                getSafe.ReturnType.FullName != actionType.FullName)
                throw new PatchException(getSafe.FullName + " is not Action<Component> GetSafeAction(Action<Component>, ComponentEventType)");
            var gs = getSafe.Body.Instructions;
            if (!(gs.Count == 2 && gs[0].OpCode == OpCodes.Ldarg_0 && gs[1].OpCode == OpCodes.Ret))
                throw new PatchException(getSafe.FullName + " is not the verified 1.17.32 'ldarg.0; ret':\n" + Dump(getSafe));
            MethodDefinition invokeStart = RequireMethod(component, "InvokeStart", m => !m.IsStatic && !m.HasParameters, "InvokeStart()");
            MethodReference actionInvoke = invokeStart.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .SingleOrDefault(r => r.Name == "Invoke" && r.DeclaringType.FullName == actionType.FullName);
            MethodDefinition eventsCctor = events.Methods.SingleOrDefault(m => m.IsConstructor && m.IsStatic);
            MethodReference actionCtor = eventsCctor?.Body.Instructions.Where(i => i.OpCode == OpCodes.Newobj).Select(i => (MethodReference)i.Operand)
                .FirstOrDefault(r => r.DeclaringType.FullName == actionType.FullName && r.Parameters.Count == 2);
            if (actionInvoke == null || actionCtor == null)
                throw new PatchException("cannot find Action<Component>::Invoke (in Component.InvokeStart) and ::.ctor(object, native int) (in Component.Events..cctor)");
            int getSafeCallers = eventsCctor.Body.Instructions.Count(i => i.Operand is MethodReference r && r.Name == getSafe.Name && r.DeclaringType.FullName == events.FullName);
            if (getSafeCallers < 10)
                throw new PatchException(events.FullName + "..cctor calls GetSafeAction " + getSafeCallers + " times, expected one per lifecycle event");

            TypeDefinition snapshot = RequireType(module, "Gameloft.Engine.SoundGroupSnapshotComponent");
            HeadlessGamePatch.RequireNoKeepAttribute(snapshot);

            TypeDefinition input = RequireType(module, "Gameloft.Engine.GameInputManagerComponent");
            MethodDefinition inputStart = RequireMethod(input, "OnStart", m => !m.IsStatic && !m.HasParameters, "OnStart()");
            MethodDefinition inputUpdate = RequireMethod(input, "OnUpdate", m => !m.IsStatic && !m.HasParameters, "OnUpdate()");
            HeadlessGamePatch.RequireBaseCallFirst(inputStart, "Gameloft.Engine.Component");
            HeadlessGamePatch.RequireBaseCallFirst(inputUpdate, "Gameloft.Engine.Component");
            if (!IsCall(inputStart.Body.Instructions[2], "Gameloft.Engine.InputManager", "CheckConnectedDevices") ||
                !inputUpdate.Body.Instructions.Any(i => IsCall(i, "Gameloft.Engine.InputManager", "UpdateGamepadState")))
                throw new PatchException(input.FullName + ": OnStart/OnUpdate are not the verified 1.17.32 code:\n" + Dump(inputStart) + "\n" + Dump(inputUpdate));

            TypeSystem ts = module.TypeSystem;
            IMetadataScope corlib = ts.CoreLibrary;
            TypeReference exceptionType = new TypeReference("System", "Exception", module, corlib);
            TypeReference systemType = new TypeReference("System", "Type", module, corlib);
            MethodReference objectCtor = CorlibMethod(module, ts.Object, ".ctor", true, ts.Void);
            MethodReference getType = CorlibMethod(module, ts.Object, "GetType", true, systemType);
            MethodReference getFullName = CorlibMethod(module, systemType, "get_FullName", true, ts.String);
            MethodReference toString = CorlibMethod(module, ts.Object, "ToString", true, ts.String);
            MethodReference concat = CorlibMethod(module, ts.String, "Concat", false, ts.String, new ArrayType(ts.String));
            MethodReference writeLine = CorlibMethod(module, new TypeReference("System", "Console", module, corlib), "WriteLine", false, ts.Void, ts.String);
            MethodReference increment = CorlibMethod(module, new TypeReference("System.Threading", "Interlocked", module, corlib), "Increment", false, ts.Int32,
                                                     new ByReferenceType(ts.Int32));
            MethodReference hook = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "HeadlessComponentException", "System.Void",
                                                      "System.String", "System.String", "System.Exception");

            // ---- change: H5, H6
            HeadlessGamePatch.AddKeepAttribute(snapshot, keepCtor, false);
            context.Notes.Add(snapshot.FullName + ": + [KeepInHeadlessMode(false)] (server Worlds stay off the player's VoxManager)");
            HeadlessGamePatch.ReturnAfterBaseCallWhenHeadless(inputStart, getWorld, getHeadless);
            HeadlessGamePatch.ReturnAfterBaseCallWhenHeadless(inputUpdate, getWorld, getHeadless);
            context.Notes.Add(input.FullName + ": OnStart/OnUpdate return after the base call when World.Headless (no second gamepad poll per frame)");

            // ---- change: H3
            var safe = new TypeDefinition("Gameloft.Engine", SafeActionType,
                                          TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class, ts.Object);
            module.Types.Add(safe);
            var fAction = new FieldDefinition("action", FieldAttributes.Private | FieldAttributes.InitOnly, actionType);
            var fEvent = new FieldDefinition("eventType", FieldAttributes.Private | FieldAttributes.InitOnly, eventType);
            var fReported = new FieldDefinition("reported", FieldAttributes.Private | FieldAttributes.Static, ts.Int32);
            safe.Fields.Add(fAction);
            safe.Fields.Add(fEvent);
            safe.Fields.Add(fReported);

            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
                                                     MethodAttributes.RTSpecialName, ts.Void);
            ctor.Parameters.Add(new ParameterDefinition("action", ParameterAttributes.None, actionType));
            ctor.Parameters.Add(new ParameterDefinition("eventType", ParameterAttributes.None, eventType));
            {
                ILProcessor il = ctor.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, objectCtor);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, fAction);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Stfld, fEvent);
                il.Emit(OpCodes.Ret);
            }
            safe.Methods.Add(ctor);

            // [NoInlining] static void Forward(string type, string ev, Exception e) => McvsOffline.GameHooks.HeadlessComponentException(type, ev, e);
            // A separate method, so a missing McvsOffline.Server.dll fails when Forward is compiled, inside Report's try.
            var forward = new MethodDefinition("Forward", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            forward.ImplAttributes |= MethodImplAttributes.NoInlining;
            forward.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, ts.String));
            forward.Parameters.Add(new ParameterDefinition("eventName", ParameterAttributes.None, ts.String));
            forward.Parameters.Add(new ParameterDefinition("exception", ParameterAttributes.None, exceptionType));
            {
                ILProcessor il = forward.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, hook);
                il.Emit(OpCodes.Ret);
            }
            safe.Methods.Add(forward);

            // static void Report(ComponentEventType t, Component c, Exception e): never throws.
            //   if (Interlocked.Increment(ref reported) > 200) return;
            //   try { Console.WriteLine(ReportPrefix + c.GetType().FullName + "." + t + " threw, the rest of it is skipped: " + e); } catch { }
            //   try { Forward(c.GetType().FullName, t.ToString(), e); } catch { }
            var report = new MethodDefinition("Report", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            report.Parameters.Add(new ParameterDefinition("eventType", ParameterAttributes.None, eventType));
            report.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, component));
            report.Parameters.Add(new ParameterDefinition("exception", ParameterAttributes.None, exceptionType));
            {
                ILProcessor il = report.Body.GetILProcessor();
                Instruction ret = il.Create(OpCodes.Ret);
                Instruction secondTry = il.Create(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldsflda, fReported);
                il.Emit(OpCodes.Call, increment);
                il.Emit(OpCodes.Ldc_I4, 200);
                il.Emit(OpCodes.Bgt, ret);

                Instruction firstTry = il.Create(OpCodes.Ldc_I4_6);
                il.Append(firstTry);
                il.Emit(OpCodes.Newarr, ts.String);
                void Put(int index, Action push)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, index);
                    push();
                    il.Emit(OpCodes.Stelem_Ref);
                }
                Put(0, () => il.Emit(OpCodes.Ldstr, ReportPrefix));
                Put(1, () => { il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Callvirt, getType); il.Emit(OpCodes.Callvirt, getFullName); });
                Put(2, () => il.Emit(OpCodes.Ldstr, "."));
                Put(3, () => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Box, eventType); il.Emit(OpCodes.Callvirt, toString); });
                Put(4, () => il.Emit(OpCodes.Ldstr, " threw, the rest of it is skipped: "));
                Put(5, () => { il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Callvirt, toString); });
                il.Emit(OpCodes.Call, concat);
                il.Emit(OpCodes.Call, writeLine);
                il.Emit(OpCodes.Leave, secondTry);
                Instruction firstHandler = il.Create(OpCodes.Pop);
                il.Append(firstHandler);
                il.Emit(OpCodes.Leave, secondTry);

                il.Append(secondTry);
                il.Emit(OpCodes.Callvirt, getType);
                il.Emit(OpCodes.Callvirt, getFullName);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Box, eventType);
                il.Emit(OpCodes.Callvirt, toString);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, forward);
                il.Emit(OpCodes.Leave, ret);
                Instruction secondHandler = il.Create(OpCodes.Pop);
                il.Append(secondHandler);
                il.Emit(OpCodes.Leave, ret);
                il.Append(ret);

                report.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = firstTry, TryEnd = firstHandler, HandlerStart = firstHandler, HandlerEnd = secondTry, CatchType = ts.Object,
                });
                report.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = secondTry, TryEnd = secondHandler, HandlerStart = secondHandler, HandlerEnd = ret, CatchType = ts.Object,
                });
            }
            safe.Methods.Add(report);

            // public void Invoke(Component c)
            // {
            //     World w;
            //     if (c == null || (w = c.World) == null || !w.Headless) { action(c); return; }   // client Worlds: unchanged
            //     try { action(c); } catch (Exception e) { Report(eventType, c, e); }
            // }
            var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig, ts.Void);
            invoke.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, component));
            invoke.Body.Variables.Add(new VariableDefinition(exceptionType));
            invoke.Body.InitLocals = true;
            {
                ILProcessor il = invoke.Body.GetILProcessor();
                Instruction ret = il.Create(OpCodes.Ret);
                Instruction popDirect = il.Create(OpCodes.Pop);
                Instruction direct = il.Create(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Brfalse, direct);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, getWorld);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brfalse, popDirect);
                il.Emit(OpCodes.Callvirt, getHeadless);
                il.Emit(OpCodes.Brfalse, direct);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
                il.Emit(OpCodes.Ldfld, fAction);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, actionInvoke);
                il.Emit(OpCodes.Leave, ret);
                Instruction handler = il.Create(OpCodes.Stloc_0);
                il.Append(handler);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, fEvent);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Call, report);
                il.Emit(OpCodes.Leave, ret);
                il.Append(popDirect);
                il.Append(direct);
                il.Emit(OpCodes.Ldfld, fAction);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, actionInvoke);
                il.Append(ret);
                invoke.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = popDirect, CatchType = exceptionType,
                });
            }
            safe.Methods.Add(invoke);

            getSafe.Body.Instructions.Clear();
            {
                ILProcessor il = getSafe.Body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Ldftn, invoke);
                il.Emit(OpCodes.Newobj, actionCtor);
                il.Emit(OpCodes.Ret);
            }
            context.Notes.Add(getSafe.FullName + ": 'ldarg.0; ret' -> new " + SafeActionType + "(action, eventType).Invoke " +
                              "(headless Worlds: exceptions reported and the event's remainder skipped; client Worlds unchanged)");
        }
    }
}
