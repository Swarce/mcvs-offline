using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Mandatory 'physics' patch of Gameloft.Engine.dll (device test 2: the game thread slept in a futex for good during the
    /// TS handshake, "connecting" forever).
    /// <para>
    /// The cause: GameOptions_android.json turns on multithreaded Havok (<c>multithreadedPhysics</c>) and deferred client physics
    /// for the whole process, and every multithreaded hkpWorld steps through ONE process-wide hkCpuThreadPool and hkJobQueue
    /// (libnativeengine.so 0x37fab4 creates them once; <c>PhysicsComponent_UpdateStep_private</c> passes them to
    /// <c>hkpWorld::stepMultithreaded</c>). The pool supports one stepping thread at a time: <c>processJobQueue</c> and
    /// <c>waitForCompletion</c> take no lock and share a single "processing" flag, worker callback and completion semaphore.
    /// Once the in-process TS World plays its level, the client World's deferred step runs on a loading-pool thread while the
    /// TS World steps on the game thread, through that same pool, and a thread then waits in <c>hkSemaphore::acquire</c>
    /// (pthread_cond_wait) for a completion that never comes. Gameloft never ran this mix: dedicated servers ran one World per
    /// Linux process and skip GameOptions (single-threaded Havok, no deferral), and the client has one World.
    /// </para>
    /// <list type="bullet">
    /// <item>P2: the <c>PhysicsComponent</c> constructor asks <c>McvsPhysics.UseMultithreading(this)</c> instead of reading
    /// <c>PhysicsMultithreaded</c>: headless (TS/GS) Worlds get a single-threaded hkpWorld (stepDeltaTime, never the pool),
    /// as on Gameloft's servers; the client keeps multithreaded, deferred physics. <c>GameHooks.PhysicsMultithreading</c>
    /// makes the final choice (settings <c>embedded.physics.serverMultithreaded</c> / <c>clientMultithreaded</c>) and logs it;
    /// without McvsOffline.Server.dll the IL itself picks "multithreaded only for client Worlds".</item>
    /// <item>P1: <c>PhysicsComponent.UpdateSimulationSteps</c> (the only caller of <c>UpdateStep_private</c>) runs inside
    /// <c>McvsPhysics.EnterStep()</c> / <c>ExitStep()</c>, a process-wide gate (<c>GameHooks.EnterPhysicsStep</c>, settings
    /// <c>embedded.physics.stepGate</c>), so no two Worlds ever step Havok at the same time. The wait is bounded
    /// (<c>embedded.physics.stepGateTimeoutMs</c>, default 2000 ms; then the step goes ahead without the gate and a warning
    /// is logged), so the gate itself can never hang the game. <c>WaitDeferred</c> is always called outside it.</item>
    /// </list>
    /// Both sites are checked against the IL fingerprint of the verified 1.17.32 build first.
    /// </summary>
    public sealed class PhysicsPatch : IlPatch
    {
        public const string HelperType = "McvsPhysics";
        public const string CtorFingerprint = "3e87166b9ec439a74f70a8b7a0082b3251897087c10ca84dc3f7a468faa05468";
        public const string StepsFingerprint = "0e91c200f47598c43fa8be9907f7ddbfc5e019b5681c8edd380b528787ca5afa";

        public override string Id => "physics";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "Havok: single-threaded physics for TS/GS Worlds, one World steps at a time (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            TypeDefinition physics = RequireType(module, "Gameloft.Engine.PhysicsComponent");
            TypeDefinition component = RequireType(module, "Gameloft.Engine.Component");
            TypeDefinition world = RequireType(module, "Gameloft.Engine.World");
            if (module.GetType("Gameloft.Engine." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched: run the patcher on your original APK");

            MethodDefinition ctor = RequireMethod(physics, ".ctor", m => !m.IsStatic && !m.HasParameters, "constructor ()");
            RequireFingerprint(ctor, CtorFingerprint, "PhysicsComponent()");
            MethodDefinition steps = RequireMethod(physics, "UpdateSimulationSteps",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32 &&
                     m.Parameters[1].ParameterType.MetadataType == MetadataType.Single && m.ReturnType.MetadataType == MetadataType.Void,
                "void (int, float)");
            RequireFingerprint(steps, StepsFingerprint, "UpdateSimulationSteps(int, float)");

            MethodDefinition getMultithreaded = RequireMethod(physics, "get_PhysicsMultithreaded",
                m => m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Boolean, "static bool getter");
            MethodDefinition getWorld = RequireMethod(component, "get_World", m => !m.IsStatic && !m.HasParameters && m.ReturnType.FullName == world.FullName, "World getter");
            MethodDefinition getHeadless = RequireMethod(world, "get_Headless", m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Boolean, "bool getter");

            // The constructor's site, as fingerprinted: IL_0018 ldarg.0; IL_0019 call get_PhysicsMultithreaded(); IL_001e callvirt ConstructNative(bool)
            var code = ctor.Body.Instructions;
            int site = Enumerable.Range(0, code.Count).Single(i => IsCall(code[i], physics.FullName, "get_PhysicsMultithreaded"));
            Instruction read = code[site];
            if (!(site >= 1 && code[site - 1].OpCode == OpCodes.Ldarg_0 && read.OpCode == OpCodes.Call &&
                  code[site + 1].OpCode == OpCodes.Callvirt && IsCall(code[site + 1], physics.FullName, "ConstructNative")))
                throw new PatchException(ctor.FullName + ": the ConstructNative(PhysicsMultithreaded) site is not where the fingerprint says");
            if (code.Any(i => i.Operand == read) || ctor.Body.ExceptionHandlers.Count != 0)
                throw new PatchException(ctor.FullName + ": unexpected branch or handler at the ConstructNative site");

            TypeSystem ts = module.TypeSystem;
            MethodReference hookMultithreading = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "PhysicsMultithreading", "System.Boolean",
                                                                    "System.Boolean", "System.Boolean");
            MethodReference hookEnter = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "EnterPhysicsStep", "System.Boolean");
            MethodReference hookExit = ImportServerMethod(module, context, NativeOnlinePatch.HooksType, "ExitPhysicsStep", "System.Void");

            // ---- the helper type
            var helper = new TypeDefinition("Gameloft.Engine", HelperType,
                                            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit |
                                            TypeAttributes.Class, ts.Object);
            module.Types.Add(helper);

            // [NoInlining] static bool ForwardMultithreading(bool headless, bool requested) => GameHooks.PhysicsMultithreading(headless, requested);
            // [NoInlining] static bool ForwardEnter() => GameHooks.EnterPhysicsStep();
            // [NoInlining] static void ForwardExit() => GameHooks.ExitPhysicsStep();
            // Separate methods, so a missing McvsOffline.Server.dll fails when they are compiled, inside their caller's try.
            MethodDefinition forwardMt = Forwarder(helper, "ForwardMultithreading", ts.Boolean, hookMultithreading, ts.Boolean, ts.Boolean);
            forwardMt.Parameters[0].Name = "headless";
            forwardMt.Parameters[1].Name = "requested";
            MethodDefinition forwardEnter = Forwarder(helper, "ForwardEnter", ts.Boolean, hookEnter);
            MethodDefinition forwardExit = Forwarder(helper, "ForwardExit", ts.Void, hookExit);

            // internal static bool UseMultithreading(PhysicsComponent component)
            // {
            //     bool requested = PhysicsComponent.PhysicsMultithreaded, headless = false;
            //     try { World w; if (component != null && (w = component.World) != null) headless = w.Headless; } catch { }
            //     bool use = requested && !headless;                              // without McvsOffline.Server.dll
            //     try { use = ForwardMultithreading(headless, requested); } catch { }
            //     return use;
            // }
            var useMt = new MethodDefinition("UseMultithreading", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
            useMt.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, physics));
            var vRequested = new VariableDefinition(ts.Boolean);
            var vHeadless = new VariableDefinition(ts.Boolean);
            var vUse = new VariableDefinition(ts.Boolean);
            var vWorld = new VariableDefinition(world);
            foreach (var v in new[] { vRequested, vHeadless, vUse, vWorld }) useMt.Body.Variables.Add(v);
            useMt.Body.InitLocals = true;
            {
                ILProcessor il = useMt.Body.GetILProcessor();
                Instruction afterRead = il.Create(OpCodes.Ldloc, vRequested);
                Instruction leaveFirst = il.Create(OpCodes.Leave, afterRead);
                Instruction notRequested = il.Create(OpCodes.Ldc_I4_0);
                Instruction storeUse = il.Create(OpCodes.Stloc, vUse);
                Instruction result = il.Create(OpCodes.Ldloc, vUse);

                il.Emit(OpCodes.Call, getMultithreaded);
                il.Emit(OpCodes.Stloc, vRequested);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, vHeadless);
                Instruction firstTry = il.Create(OpCodes.Ldarg_0);
                il.Append(firstTry);
                il.Emit(OpCodes.Brfalse, leaveFirst);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, getWorld);
                il.Emit(OpCodes.Stloc, vWorld);
                il.Emit(OpCodes.Ldloc, vWorld);
                il.Emit(OpCodes.Brfalse, leaveFirst);
                il.Emit(OpCodes.Ldloc, vWorld);
                il.Emit(OpCodes.Callvirt, getHeadless);
                il.Emit(OpCodes.Stloc, vHeadless);
                il.Append(leaveFirst);
                Instruction firstHandler = il.Create(OpCodes.Pop);
                il.Append(firstHandler);
                il.Emit(OpCodes.Leave, afterRead);

                il.Append(afterRead);                    // ldloc requested
                il.Emit(OpCodes.Brfalse, notRequested);
                il.Emit(OpCodes.Ldloc, vHeadless);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Br, storeUse);
                il.Append(notRequested);
                il.Append(storeUse);

                Instruction secondTry = il.Create(OpCodes.Ldloc, vHeadless);
                il.Append(secondTry);
                il.Emit(OpCodes.Ldloc, vRequested);
                il.Emit(OpCodes.Call, forwardMt);
                il.Emit(OpCodes.Stloc, vUse);
                il.Emit(OpCodes.Leave, result);
                Instruction secondHandler = il.Create(OpCodes.Pop);
                il.Append(secondHandler);
                il.Emit(OpCodes.Leave, result);
                il.Append(result);
                il.Emit(OpCodes.Ret);

                useMt.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = firstTry, TryEnd = firstHandler, HandlerStart = firstHandler, HandlerEnd = afterRead, CatchType = ts.Object,
                });
                useMt.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = secondTry, TryEnd = secondHandler, HandlerStart = secondHandler, HandlerEnd = result, CatchType = ts.Object,
                });
                useMt.Body.OptimizeMacros();
            }
            helper.Methods.Add(useMt);

            // internal static bool EnterStep() { bool taken = false; try { taken = ForwardEnter(); } catch { } return taken; }
            var enter = new MethodDefinition("EnterStep", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Boolean);
            var vTaken = new VariableDefinition(ts.Boolean);
            enter.Body.Variables.Add(vTaken);
            enter.Body.InitLocals = true;
            {
                ILProcessor il = enter.Body.GetILProcessor();
                Instruction result = il.Create(OpCodes.Ldloc, vTaken);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, vTaken);
                Instruction tryStart = il.Create(OpCodes.Call, forwardEnter);
                il.Append(tryStart);
                il.Emit(OpCodes.Stloc, vTaken);
                il.Emit(OpCodes.Leave, result);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Leave, result);
                il.Append(result);
                il.Emit(OpCodes.Ret);
                enter.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = result, CatchType = ts.Object,
                });
                enter.Body.OptimizeMacros();
            }
            helper.Methods.Add(enter);

            // internal static void ExitStep() { try { ForwardExit(); } catch { } }
            var exit = new MethodDefinition("ExitStep", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            {
                ILProcessor il = exit.Body.GetILProcessor();
                Instruction ret = il.Create(OpCodes.Ret);
                Instruction tryStart = il.Create(OpCodes.Call, forwardExit);
                il.Append(tryStart);
                il.Emit(OpCodes.Leave, ret);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Leave, ret);
                il.Append(ret);
                exit.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = ret, CatchType = ts.Object,
                });
                exit.Body.OptimizeMacros();
            }
            helper.Methods.Add(exit);

            // ---- change: P2, the constructor passes UseMultithreading(this) to ConstructNative
            {
                ILProcessor il = ctor.Body.GetILProcessor();
                il.InsertBefore(read, il.Create(OpCodes.Ldarg_0));
                read.Operand = useMt;
            }
            context.Notes.Add(ctor.FullName + ": ConstructNative(PhysicsMultithreaded) -> ConstructNative(" + HelperType + ".UseMultithreading(this)) " +
                              "(headless TS/GS Worlds: single-threaded Havok, as on Gameloft's servers; settings embedded.physics)");

            // ---- change: P1, UpdateSimulationSteps runs its loop inside the process-wide step gate
            //   bool taken = McvsPhysics.EnterStep();
            //   try { <original loop> } finally { if (taken) McvsPhysics.ExitStep(); }
            {
                MethodBody body = steps.Body;
                body.SimplifyMacros();
                var gate = new VariableDefinition(ts.Boolean);
                body.Variables.Add(gate);
                body.InitLocals = true;
                ILProcessor il = body.GetILProcessor();
                Instruction first = body.Instructions[0];
                Instruction oldRet = body.Instructions[body.Instructions.Count - 1];
                if (oldRet.OpCode != OpCodes.Ret || body.Instructions.Count(i => i.OpCode == OpCodes.Ret) != 1)
                    throw new PatchException(steps.FullName + ": expected a single ret at the end");
                Instruction end = il.Create(OpCodes.Ret);
                Instruction finallyStart = il.Create(OpCodes.Ldloc, gate);
                Instruction endFinally = il.Create(OpCodes.Endfinally);
                il.InsertBefore(first, il.Create(OpCodes.Call, enter));
                il.InsertBefore(first, il.Create(OpCodes.Stloc, gate));
                oldRet.OpCode = OpCodes.Leave;
                oldRet.Operand = end;
                il.Append(finallyStart);
                il.Emit(OpCodes.Brfalse, endFinally);
                il.Emit(OpCodes.Call, exit);
                il.Append(endFinally);
                il.Append(end);
                body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                {
                    TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = end,
                });
                body.OptimizeMacros();
            }
            context.Notes.Add(steps.FullName + ": the Havok steps run inside " + HelperType + ".EnterStep()/ExitStep(), a process-wide gate " +
                              "(no two Worlds step at the same time; bounded wait; settings embedded.physics.stepGate)");
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
