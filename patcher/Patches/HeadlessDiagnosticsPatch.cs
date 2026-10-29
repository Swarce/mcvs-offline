using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>headless-diag</c> (docs/design/pc-port.md §8.5), Gameloft.Engine.dll: the exceptions the engine swallows at
    /// TS/GS World construction and at component creation are reported to <c>McvsOffline.PcHooks</c>, so a failure of the
    /// in-process server Worlds on the Windows engine is not silent (WR2 Q4). Both handlers keep swallowing:
    /// <list type="bullet">
    /// <item>M1 <c>ServerManager.&lt;_AddClientAsync&gt;d__20.MoveNext</c>: the inner <c>catch (Exception)</c> handler starts
    /// <c>IL_0319: pop; leave.s IL_031c</c>; the <c>pop</c> becomes <c>call McvsWorldDiagnostics.ServerWorldFailed(Exception)</c>
    /// (the handler start keeps pointing at it). The <c>OperationCanceledException</c> handler (IL_0316) is untouched.</item>
    /// <item>M4 <c>ComponentAssemblies.CreateInstance(GameObject, Type, bool, object[])</c>: the PC-only <c>catch (Exception)</c>
    /// around the constructor and <c>InitTemplate</c> (<c>IL_0061: pop; leave.s IL_0075</c>) becomes
    /// <c>ldarg.1; call McvsWorldDiagnostics.ComponentCreateFailed(Exception, Type)</c>; the method still returns what it
    /// returned (null, or the half-initialised component).</item>
    /// </list>
    /// <c>Gameloft.Engine.McvsWorldDiagnostics</c> (its presence is the witness, §3.9): each method is
    /// <c>try { [NoInlining] forwarder → PcHooks.&lt;same name&gt; } catch { }</c>, so a missing server DLL changes nothing.
    /// </summary>
    public sealed class HeadlessDiagnosticsPatch : IlPatch
    {
        /// <summary>The helper type in namespace Gameloft.Engine; its presence is the witness (§3.9).</summary>
        public const string HelperType = "McvsWorldDiagnostics";

        public const string HelperNamespace = "Gameloft.Engine";

        public const string HooksType = "McvsOffline.PcHooks";

        /// <summary>M1: <c>ServerManager.&lt;_AddClientAsync&gt;d__20.MoveNext()</c> (identical on Android).</summary>
        public const string PcAddClientFingerprint = "5b7fcb15925910abade2da0df9f30e223147118d2844e97fc47c7b1db191abc0";

        /// <summary>M4: <c>ComponentAssemblies.CreateInstance</c> on PC (Android has no catch there: 8f0ef7a2…).</summary>
        public const string PcCreateInstanceFingerprint = "e372ca4d0596bec209721344317fcb2244ab2e2d20887515875f39e6af652cd9";

        public override string Id => "headless-diag";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "server World and component creation failures are logged instead of swallowed (Gameloft.Engine.dll, PC)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            string addClientFingerprint = ForBuild(context, null, PcAddClientFingerprint);
            string createFingerprint = ForBuild(context, null, PcCreateInstanceFingerprint);
            PatchSites.RequireBuildCorlib(module, context, Id);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched (" + HelperNamespace + "." + HelperType + "): install from the game's own file");

            TypeDefinition manager = RequireType(module, "Gameloft.Engine.ServerManager");
            TypeDefinition machine = manager.NestedTypes.SingleOrDefault(t => t.Name == "<_AddClientAsync>d__20");
            if (machine == null) throw new PatchException(manager.FullName + ": the state machine <_AddClientAsync>d__20 is missing");
            MethodDefinition moveNext = RequireMethod(machine, "MoveNext", m => !m.IsStatic && !m.HasParameters, "MoveNext()");
            RequireFingerprint(moveNext, addClientFingerprint, "ServerManager._AddClientAsync (M1)");
            ExceptionHandler serverCatch = moveNext.Body.ExceptionHandlers.SingleOrDefault(h =>
                h.HandlerType == ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception" && h.HandlerStart.Offset == 0x319);
            if (serverCatch == null || serverCatch.HandlerStart.OpCode != OpCodes.Pop || serverCatch.HandlerStart.Next.OpCode != OpCodes.Leave_S ||
                ((Instruction)serverCatch.HandlerStart.Next.Operand).Offset != 0x31c ||
                !moveNext.Body.ExceptionHandlers.Any(h => h.CatchType != null && h.CatchType.FullName == "System.OperationCanceledException" &&
                                                         h.HandlerStart.Offset == 0x316))
                throw new PatchException(moveNext.FullName + ": the catch (Exception) at IL_0319 is not where the fingerprint says:\n" + Dump(moveNext));

            TypeDefinition assemblies = RequireType(module, "Gameloft.Engine.ComponentAssemblies");
            MethodDefinition create = RequireMethod(assemblies, "CreateInstance",
                m => m.IsStatic && m.Parameters.Count == 4 && m.Parameters[0].ParameterType.FullName == "Gameloft.Engine.GameObject" &&
                     m.Parameters[1].ParameterType.FullName == "System.Type" && m.Parameters[2].ParameterType.MetadataType == MetadataType.Boolean,
                "static CreateInstance(GameObject, Type, bool, object[])");
            RequireFingerprint(create, createFingerprint, "ComponentAssemblies.CreateInstance (M4)");
            ExceptionHandler componentCatch = create.Body.ExceptionHandlers.SingleOrDefault(h =>
                h.HandlerType == ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception");
            if (componentCatch == null || componentCatch.HandlerStart.Offset != 0x61 || componentCatch.HandlerStart.OpCode != OpCodes.Pop ||
                componentCatch.HandlerStart.Next.OpCode != OpCodes.Leave_S || ((Instruction)componentCatch.HandlerStart.Next.Operand).Offset != 0x75)
                throw new PatchException(create.FullName + ": the catch (Exception) at IL_0061 is not where the fingerprint says:\n" + Dump(create));

            TypeReference exception = PatchSites.CorlibType(module, "System", "Exception");
            TypeReference type = PatchSites.CorlibType(module, "System", "Type");
            MethodReference hookServer = ImportServerMethod(module, context, HooksType, "ServerWorldFailed", "System.Void", "System.Exception");
            MethodReference hookComponent = ImportServerMethod(module, context, HooksType, "ComponentCreateFailed", "System.Void", "System.Exception", "System.Type");

            // ---- the helper type
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            MethodDefinition forwardServer = PatchSites.Forwarder(helper, "ForwardServerWorldFailed", module.TypeSystem.Void, hookServer, exception);
            MethodDefinition forwardComponent = PatchSites.Forwarder(helper, "ForwardComponentCreateFailed", module.TypeSystem.Void, hookComponent, exception, type);
            MethodDefinition serverWorldFailed = PatchSites.GuardedVoid(helper, "ServerWorldFailed", forwardServer, exception);
            MethodDefinition componentCreateFailed = PatchSites.GuardedVoid(helper, "ComponentCreateFailed", forwardComponent, exception, type);

            // ---- change: M1, the handler's pop becomes the report (it consumes the exception as the pop did)
            serverCatch.HandlerStart.OpCode = OpCodes.Call;
            serverCatch.HandlerStart.Operand = serverWorldFailed;
            PatchSites.FixShortBranches(moveNext);
            context.Notes.Add(moveNext.FullName + ": catch (Exception) IL_0319 reports to " + HooksType + ".ServerWorldFailed (M1; still swallowed)");

            // ---- change: M4, pop -> ldarg.1 (the type), then the report (exception, type)
            Instruction start = componentCatch.HandlerStart;
            start.OpCode = OpCodes.Ldarg_1;
            start.Operand = null;
            create.Body.GetILProcessor().InsertAfter(start, Instruction.Create(OpCodes.Call, componentCreateFailed));
            PatchSites.FixShortBranches(create);
            context.Notes.Add(create.FullName + ": catch (Exception) IL_0061 reports to " + HooksType + ".ComponentCreateFailed(exception, type) " +
                              "(M4; still swallowed)");
        }
    }
}
