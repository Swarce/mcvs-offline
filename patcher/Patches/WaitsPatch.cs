using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'waits' patch of Gameloft.Engine.dll: the game's pumping wait helpers never sleep unbounded.
    /// <para>
    /// <c>TaskExtension.WaitAndWork(Task|ManualResetEventSlim, options)</c> is how engine code waits on a thread that must keep
    /// draining work while it waits: with <c>ExecuteContexts</c> it runs the <c>GameSynchronizationContext</c>s registered to
    /// the calling thread (every World's context, and GameApplication's, on the game thread), with <c>ExecuteNativeTasks</c> the
    /// native task queue, and between rounds it sleeps in <c>WaitHandle.WaitAny(signal, target)</c> until a
    /// <c>WorkItemAdded</c>/<c>NativeTaskAdded</c> wakes it. Work that arrives without that event (an item queued with
    /// <c>DelayedPost</c>, which only <c>ExecuteItems</c> moves into the queue, or a context registered to the thread after the
    /// wait began) is not drained until the target completes on its own; when the target needs that work, the thread sleeps
    /// for good. With the in-process TS/GS Worlds every World's work runs on the same game thread, so this patch closes that
    /// class of lost wake-ups: each <c>WaitAny(handles)</c> becomes <c>WaitAny(handles, 100)</c> and the loop simply
    /// runs another round (drain, check, wait) every 100 ms. Nothing else changes: the non-pumping paths
    /// (<c>task.Wait()</c>, <c>signal.Wait()</c>: no contexts and no native tasks asked for) keep their exact behaviour, so no
    /// work runs anywhere it did not run before.
    /// </para>
    /// Device test 2 showed no pumping wait on the game thread during the hang (the watchdog's heartbeat, posted to
    /// GameApplication.Context with WorkItemAdded, never ran), so this is hardening, not the fix for that hang ('physics' is).
    /// Both overloads are checked against the IL fingerprint of the verified 1.17.32 build first.
    /// </summary>
    public sealed class WaitsPatch : IlPatch
    {
        public const int SliceMs = 100;
        public const string TaskFingerprint = "7287eccfe8a0a2da0cf26353d5f19b30fe51ebc14c8e80502e99ad13a39c7cdd";
        public const string SignalFingerprint = "4c31f3938568f60b8bb253332f98642a1aece843a3a2d1c62d812582c30aaa71";

        public override string Id => "waits";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "TaskExtension.WaitAndWork: pumping waits re-check every " + SliceMs + " ms (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            TypeDefinition extension = RequireType(module, "Gameloft.Engine.TaskExtension");
            MethodDefinition onTask = RequireMethod(extension, "WaitAndWork",
                m => m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Threading.Tasks.Task",
                "static WaitAndWork(Task, WaitAndWorkOptions)");
            MethodDefinition onSignal = RequireMethod(extension, "WaitAndWork",
                m => m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Threading.ManualResetEventSlim",
                "static WaitAndWork(ManualResetEventSlim, WaitAndWorkOptions)");
            RequireFingerprint(onTask, TaskFingerprint, "WaitAndWork(Task, WaitAndWorkOptions)");
            RequireFingerprint(onSignal, SignalFingerprint, "WaitAndWork(ManualResetEventSlim, WaitAndWorkOptions)");
            Instruction taskWait = SingleWaitAny(onTask);
            Instruction signalWait = SingleWaitAny(onSignal);

            // The loop shapes the timeout result must fit (fingerprinted, checked again for the reader):
            //   Task:   WaitAny(handles); pop; ldarg.0; callvirt Task::get_IsCompleted(); brfalse <loop>   (258 = timeout: loop again)
            //   Signal: WaitAny(handles); ldc.i4.1; bne.un <loop>                                          (258 != 1: loop again)
            if (!(taskWait.Next.OpCode == OpCodes.Pop && taskWait.Next.Next.OpCode == OpCodes.Ldarg_0 &&
                  IsCall(taskWait.Next.Next.Next, "System.Threading.Tasks.Task", "get_IsCompleted")))
                throw new PatchException(onTask.FullName + ": WaitAny is not followed by the IsCompleted loop check");
            if (!(signalWait.Next.OpCode == OpCodes.Ldc_I4_1 && signalWait.Next.Next.OpCode == OpCodes.Bne_Un_S))
                throw new PatchException(onSignal.FullName + ": WaitAny is not followed by the 'index != 1' loop check");

            TypeSystem ts = module.TypeSystem;
            var waitHandle = new TypeReference("System.Threading", "WaitHandle", module, ts.CoreLibrary);
            MethodReference waitAnyTimed = CorlibMethod(module, waitHandle, "WaitAny", false, ts.Int32, new ArrayType(waitHandle), ts.Int32);

            foreach (var (method, wait) in new[] { (onTask, taskWait), (onSignal, signalWait) })
            {
                method.Body.SimplifyMacros();
                method.Body.GetILProcessor().InsertBefore(wait, Instruction.Create(OpCodes.Ldc_I4, SliceMs));
                wait.Operand = waitAnyTimed;
                method.Body.OptimizeMacros();
                context.Notes.Add(method.FullName + ": WaitHandle.WaitAny(handles) -> WaitAny(handles, " + SliceMs +
                                  ") (the pumping wait drains and re-checks every " + SliceMs + " ms instead of sleeping until a wake-up event)");
            }
        }

        private static Instruction SingleWaitAny(MethodDefinition method)
        {
            var calls = method.Body.Instructions.Where(i => IsCall(i, "System.Threading.WaitHandle", "WaitAny")).ToList();
            if (calls.Count != 1 || ((MethodReference)calls[0].Operand).Parameters.Count != 1 || calls[0].OpCode != OpCodes.Call)
                throw new PatchException(method.FullName + ": expected exactly one WaitHandle.WaitAny(WaitHandle[]) call:\n" + Dump(method));
            if (calls[0].Previous == null || calls[0].Previous.OpCode != OpCodes.Ldloc_3)
                throw new PatchException(method.FullName + ": WaitAny's argument is not the handles local (ldloc.3)");
            return calls[0];
        }
    }
}
