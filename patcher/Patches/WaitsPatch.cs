using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'waits' patch of Gameloft.Engine.dll: hardening with no observed trigger. The game's pumping wait helpers
    /// never sleep longer than 100 ms between rounds.
    /// <para>
    /// <c>TaskExtension.WaitAndWork(Task|ManualResetEventSlim, options)</c> waits while it keeps draining work: with
    /// <c>ExecuteContexts</c> it runs the <c>GameSynchronizationContext</c>s registered to the calling thread (a snapshot,
    /// <c>Currents</c>, taken once when the wait begins), with <c>ExecuteNativeTasks</c> the native task queue, and between
    /// rounds it sleeps in <c>WaitHandle.WaitAny(signal, target)</c>. <c>Post</c>, <c>Send</c> and <c>EnqueueDelayedItems</c>
    /// raise <c>WorkItemAdded</c> and the native side calls <c>NativeTaskAdded</c>, so ordinary work always wakes it. Two
    /// state changes in the shipped code raise no event, and the slices bound them to one round:
    /// <list type="bullet">
    /// <item>re-enabling a context: <c>World.OnLoadingScene</c>/<c>OnLoadedScene</c> set <c>Context.Enabled</c> false/true,
    /// and an item posted meanwhile woke the wait once while <c>ExecuteItems</c> returned at once. Only
    /// <c>World_Disposing</c> waits with contexts (options Default).</item>
    /// <item>the native wake-up callback: the <c>NativeTaskAdded</c> accessors install and remove it with an unlocked
    /// check-then-act, and every <c>WaitAndWork</c> with native tasks subscribes and unsubscribes, so two threads doing that
    /// at once can leave one wait without native wake-ups.</item>
    /// </list>
    /// Not covered: a context registered to the thread after the wait began (the loop drains the same snapshot every round);
    /// <c>DelayedPost</c>, which raises no event either, has no caller in the game. The call sites (MenuComponent: native
    /// tasks on the render thread; PhysicsComponent.WaitDeferred: native tasks and the task; World_Disposing: contexts and
    /// native tasks; SWFComponent: the non-pumping <c>task.Wait()</c> path) pump exactly what they pumped before, so no work
    /// runs anywhere or in an order it did not before; the cost is one extra wake-up per 100 ms of waiting.
    /// </para>
    /// Device test 2 showed no pumping wait on the game thread during the hang (the watchdog's heartbeat, posted to
    /// GameApplication.Context with WorkItemAdded, never ran), so this is not the fix for that hang.
    /// Both overloads are checked against the IL fingerprint of the verified 1.17.32 build first. PC (docs/design/pc-port.md §8.7):
    /// the same loops, only one <c>dup</c> per overload became <c>ldloc.0</c>/<c>ldloc.2</c> (offsets unchanged), so only the
    /// fingerprints differ; the timed <c>WaitAny</c> comes from the PC corlib (mscorlib 4, the BCL directory on Linux).
    /// </summary>
    public sealed class WaitsPatch : IlPatch
    {
        public const int SliceMs = 100;
        public const string TaskFingerprint = "7287eccfe8a0a2da0cf26353d5f19b30fe51ebc14c8e80502e99ad13a39c7cdd";
        public const string SignalFingerprint = "4c31f3938568f60b8bb253332f98642a1aece843a3a2d1c62d812582c30aaa71";
        public const string PcTaskFingerprint = "1b4aefd54d00ab434106c87b287e97a2b2487c048d31fdbd28ccd4d22ceb558d";
        public const string PcSignalFingerprint = "563529838ddb335c54b9db3c4d22be789b1ef1b49e546ee851dc0063d1ffbbcc";

        public override string Id => "waits";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "TaskExtension.WaitAndWork: pumping waits re-check every " + SliceMs + " ms (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            PatchSites.RequireBuildAssembly(module, context, Id);
            string taskFingerprint = ForBuild(context, TaskFingerprint, PcTaskFingerprint);
            string signalFingerprint = ForBuild(context, SignalFingerprint, PcSignalFingerprint);
            TypeDefinition extension = RequireType(module, "Gameloft.Engine.TaskExtension");
            MethodDefinition onTask = RequireMethod(extension, "WaitAndWork",
                m => m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Threading.Tasks.Task",
                "static WaitAndWork(Task, WaitAndWorkOptions)");
            MethodDefinition onSignal = RequireMethod(extension, "WaitAndWork",
                m => m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Threading.ManualResetEventSlim",
                "static WaitAndWork(ManualResetEventSlim, WaitAndWorkOptions)");
            RequireFingerprint(onTask, taskFingerprint, "WaitAndWork(Task, WaitAndWorkOptions)");
            RequireFingerprint(onSignal, signalFingerprint, "WaitAndWork(ManualResetEventSlim, WaitAndWorkOptions)");
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
