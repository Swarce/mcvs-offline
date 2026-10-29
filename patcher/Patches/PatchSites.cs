using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Patcher.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// What several patches share for the per-build site tables (docs/design/pc-port.md §8): the build check every patch runs
    /// first, the <c>if (World.Headless) goto …</c> insertions with their branch retargeting, short-branch repair, and the
    /// helper types whose presence is a patch's witness (§3.9).
    /// </summary>
    internal static class PatchSites
    {
        /// <summary>
        /// Refuses a module that is the shipped original of another build (its MVID is one of that build's
        /// <see cref="BuildProfile.PatchedOriginals"/> or <see cref="BuildProfile.UnchangedWitnesses"/>), so a patch whose sites are
        /// identical on both builds still refuses the other build's file before changing anything (§8.8). The corlib reference
        /// cannot tell the builds apart: the Android game assemblies reference mscorlib 4.0.0.0 (b77a5c561934e089) as the PC ones
        /// do (2.0.5.0 is the version of the APK's own BCL, which the embedded server is compiled against). Any other module
        /// (this build's original, a copy the tests changed, a look-alike) is judged by the patch's own checks.
        /// </summary>
        internal static void RequireBuildAssembly(ModuleDefinition module, PatchContext context, string patchId)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            if (context == null) throw new ArgumentNullException(nameof(context));
            foreach (BuildProfile other in new[] { BuildProfile.Android1_17_32, BuildProfile.PcSteam6372049 })
            {
                if (other == context.Build) continue;
                foreach (var original in other.PatchedOriginals.Concat(other.UnchangedWitnesses))
                {
                    if (original.Value != module.Mvid) continue;
                    throw new PatchException(patchId + ": " + module.Name + " is the " + original.Key + " of build " + other.Id + " (MVID " + module.Mvid +
                                             "), not an assembly of build " + context.Build.Id);
                }
            }
        }

        /// <summary><c>ldarg.0; call Component::get_World(); callvirt World::get_Headless(); brtrue|brfalse target</c> (long form).</summary>
        internal static Instruction[] HeadlessBranch(MethodReference getWorld, MethodReference getHeadless, bool whenHeadless, Instruction target)
        {
            return new[]
            {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, getWorld), Instruction.Create(OpCodes.Callvirt, getHeadless),
                Instruction.Create(whenHeadless ? OpCodes.Brtrue : OpCodes.Brfalse, target),
            };
        }

        /// <summary>
        /// Inserts <paramref name="sequence"/> before <paramref name="at"/> and makes every branch (and switch entry) that targeted
        /// <paramref name="at"/> target the first inserted instruction, which Cecil's InsertBefore does not do. Refuses an insertion
        /// point that starts or ends a protected region.
        /// </summary>
        internal static void InsertBeforeRetargeted(MethodDefinition method, Instruction at, IList<Instruction> sequence)
        {
            MethodBody body = method.Body;
            if (body.ExceptionHandlers.Any(h => h.TryStart == at || h.TryEnd == at || h.HandlerStart == at || h.HandlerEnd == at || h.FilterStart == at))
                throw new PatchException(method.FullName + ": IL_" + at.Offset.ToString("x4") + " is an exception-handler boundary, not an insertion point");
            List<Instruction> branches = BranchesTo(body, at);
            ILProcessor il = body.GetILProcessor();
            foreach (Instruction i in sequence) il.InsertBefore(at, i);
            Instruction first = sequence[0];
            foreach (Instruction branch in branches)
            {
                if (branch.Operand is Instruction[] targets)
                {
                    for (int k = 0; k < targets.Length; k++) if (targets[k] == at) targets[k] = first;
                }
                else
                {
                    branch.Operand = first;
                }
            }
            FixShortBranches(method);
        }

        /// <summary>Every instruction whose operand (or one of whose switch targets) is <paramref name="target"/>.</summary>
        internal static List<Instruction> BranchesTo(MethodBody body, Instruction target)
        {
            return body.Instructions.Where(i => i.Operand == target || (i.Operand is Instruction[] targets && targets.Contains(target))).ToList();
        }

        /// <summary>Sets every instruction's offset as the writer will (the fingerprint of an edited body depends on it).</summary>
        internal static void ComputeOffsets(MethodBody body)
        {
            int offset = 0;
            foreach (Instruction i in body.Instructions)
            {
                i.Offset = offset;
                offset += i.GetSize();
            }
        }

        /// <summary>
        /// Turns every short branch whose target an insertion moved out of the signed-byte range into its long form (Cecil writes
        /// a short branch without checking it), until the layout is stable. Branches that still fit keep their encoding.
        /// </summary>
        internal static void FixShortBranches(MethodDefinition method)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                ComputeOffsets(method.Body);
                foreach (Instruction i in method.Body.Instructions)
                {
                    if (i.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
                    int delta = ((Instruction)i.Operand).Offset - (i.Offset + i.GetSize());
                    if (delta >= sbyte.MinValue && delta <= sbyte.MaxValue) continue;
                    i.OpCode = LongForm(i.OpCode);
                    changed = true;
                }
            }
        }

        private static OpCode LongForm(OpCode shortForm)
        {
            switch (shortForm.Code)
            {
                case Code.Br_S: return OpCodes.Br;
                case Code.Brfalse_S: return OpCodes.Brfalse;
                case Code.Brtrue_S: return OpCodes.Brtrue;
                case Code.Beq_S: return OpCodes.Beq;
                case Code.Bge_S: return OpCodes.Bge;
                case Code.Bgt_S: return OpCodes.Bgt;
                case Code.Ble_S: return OpCodes.Ble;
                case Code.Blt_S: return OpCodes.Blt;
                case Code.Bne_Un_S: return OpCodes.Bne_Un;
                case Code.Bge_Un_S: return OpCodes.Bge_Un;
                case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
                case Code.Ble_Un_S: return OpCodes.Ble_Un;
                case Code.Blt_Un_S: return OpCodes.Blt_Un;
                case Code.Leave_S: return OpCodes.Leave;
                default: throw new InvalidOperationException("no long form for " + shortForm);
            }
        }

        /// <summary>The instruction at <paramref name="offset"/> of the body as read, or a <see cref="PatchException"/>.</summary>
        internal static Instruction At(MethodDefinition method, int offset)
        {
            Instruction i = method.Body.Instructions.FirstOrDefault(x => x.Offset == offset);
            if (i == null) throw new PatchException(method.FullName + ": no instruction at IL_" + offset.ToString("x4"));
            return i;
        }

        /// <summary>A new <c>internal static abstract sealed</c> helper type in <paramref name="ns"/>, added to the module.</summary>
        internal static TypeDefinition AddHelperType(ModuleDefinition module, string ns, string name)
        {
            var helper = new TypeDefinition(ns, name,
                                            TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit |
                                            TypeAttributes.Class, module.TypeSystem.Object);
            module.Types.Add(helper);
            return helper;
        }

        /// <summary><c>[NoInlining] private static R name(params) =&gt; target(params);</c> (the call that binds McvsOffline.Server.dll).</summary>
        internal static MethodDefinition Forwarder(TypeDefinition owner, string name, TypeReference returnType, MethodReference target, params TypeReference[] parameters)
        {
            var method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, returnType);
            method.ImplAttributes |= MethodImplAttributes.NoInlining;
            for (int i = 0; i < parameters.Length; i++) method.Parameters.Add(new ParameterDefinition("a" + i, ParameterAttributes.None, parameters[i]));
            ILProcessor il = method.Body.GetILProcessor();
            foreach (ParameterDefinition p in method.Parameters) il.Append(LoadArgument(p.Index));
            il.Emit(OpCodes.Call, target);
            il.Emit(OpCodes.Ret);
            owner.Methods.Add(method);
            return method;
        }

        /// <summary><c>internal static void name(params) { try { forwarder(params); } catch { } }</c>.</summary>
        internal static MethodDefinition GuardedVoid(TypeDefinition owner, string name, MethodReference forwarder, params TypeReference[] parameters)
        {
            ModuleDefinition module = owner.Module;
            var method = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            for (int i = 0; i < parameters.Length; i++) method.Parameters.Add(new ParameterDefinition("a" + i, ParameterAttributes.None, parameters[i]));
            ILProcessor il = method.Body.GetILProcessor();
            Instruction ret = il.Create(OpCodes.Ret);
            Instruction call = il.Create(OpCodes.Call, forwarder);
            for (int i = 0; i < parameters.Length; i++) il.Append(LoadArgument(i));
            il.Append(call);
            Instruction tryStart = method.Body.Instructions[0];
            il.Emit(OpCodes.Leave_S, ret);
            Instruction handler = il.Create(OpCodes.Pop);
            il.Append(handler);
            il.Emit(OpCodes.Leave_S, ret);
            il.Append(ret);
            method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = ret, CatchType = module.TypeSystem.Object,
            });
            owner.Methods.Add(method);
            return method;
        }

        internal static Instruction LoadArgument(int index)
        {
            switch (index)
            {
                case 0: return Instruction.Create(OpCodes.Ldarg_0);
                case 1: return Instruction.Create(OpCodes.Ldarg_1);
                case 2: return Instruction.Create(OpCodes.Ldarg_2);
                case 3: return Instruction.Create(OpCodes.Ldarg_3);
                default: throw new ArgumentOutOfRangeException(nameof(index), "helpers take at most four arguments");
            }
        }

        /// <summary>
        /// A type of the module's own corlib (mscorlib), as a reference in <paramref name="module"/>, resolved so that a value type
        /// (CancellationToken) is encoded as one; a type the corlib does not have is a <see cref="PatchException"/>.
        /// </summary>
        internal static TypeReference CorlibType(ModuleDefinition module, string ns, string name)
        {
            var reference = new TypeReference(ns, name, module, module.TypeSystem.CoreLibrary);
            TypeDefinition resolved;
            try
            {
                resolved = reference.Resolve();
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("cannot resolve the game's mscorlib for " + ns + "." + name + ": " + ex.Message);
            }
            if (resolved == null) throw new PatchException("the game's mscorlib has no " + ns + "." + name);
            reference.IsValueType = resolved.IsValueType;
            return module.ImportReference(reference);
        }
    }
}
