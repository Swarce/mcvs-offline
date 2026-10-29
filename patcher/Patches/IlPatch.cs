using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Patcher.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>A patch site did not look like the verified 1.17.32 code: nothing is changed and the patcher stops.</summary>
    public sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    /// <summary>What every patch can use.</summary>
    public sealed class PatchContext
    {
        /// <summary>A context for the Android build (<see cref="BuildProfile.Android1_17_32"/>), as every caller before the PC port.</summary>
        public PatchContext(ModuleDefinition server, int port) : this(server, port, BuildProfile.Android1_17_32)
        {
        }

        public PatchContext(ModuleDefinition server, int port, BuildProfile build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            Server = server;
            Port = port;
            Build = build;
        }

        /// <summary>The game build the patched assemblies come from; patches pick their site tables by it (<see cref="IlPatch.ForBuild"/>).</summary>
        public BuildProfile Build { get; }

        /// <summary>McvsOffline.Server.dll (the embedded build) as read by Cecil.</summary>
        public ModuleDefinition Server { get; }

        /// <summary>Port of the embedded Federation server (default 51780), used in URLs baked into the game.</summary>
        public int Port { get; }

        /// <summary>Human-readable notes for the report, one per change.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// One IL patch of one game assembly. <see cref="Apply"/> first locates and verifies every site against the
    /// exact instruction pattern of the verified build and throws <see cref="PatchException"/> before changing
    /// anything if one does not match; only then it edits the module.
    /// </summary>
    public abstract class IlPatch
    {
        /// <summary>Command-line id (--patch).</summary>
        public abstract string Id { get; }

        /// <summary>File name in assemblies/, e.g. "NicePlaySDK.dll".</summary>
        public abstract string AssemblyFile { get; }

        public abstract string Description { get; }

        public abstract void Apply(ModuleDefinition module, PatchContext context);

        // ----------------------------------------------------------------- helpers for locators

        /// <summary>
        /// The value for <c>ctx.Build</c>: <paramref name="android"/> or <paramref name="pc"/> (a fingerprint, a site table, an
        /// offset). A null value means this patch has no table for that build: <see cref="PatchException"/>
        /// "&lt;id&gt; is not supported on build &lt;id&gt;", before anything changes.
        /// </summary>
        protected string ForBuild(PatchContext ctx, string android, string pc)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            string value = ctx.Build.IsPc ? pc : android;
            if (value == null) throw new PatchException(Id + " is not supported on build " + ctx.Build.Id);
            return value;
        }

        protected static TypeDefinition RequireType(ModuleDefinition module, string fullName)
        {
            TypeDefinition type = module.GetType(fullName);
            if (type == null) throw new PatchException(module.Name + ": type " + fullName + " not found");
            return type;
        }

        protected static MethodDefinition RequireMethod(TypeDefinition type, string name, Func<MethodDefinition, bool> filter, string what)
        {
            var matches = type.Methods.Where(m => m.Name == name && filter(m)).ToList();
            if (matches.Count != 1)
                throw new PatchException(type.FullName + ": expected exactly one " + what + " named " + name + ", found " + matches.Count);
            if (!matches[0].HasBody) throw new PatchException(matches[0].FullName + " has no IL body");
            return matches[0];
        }

        protected static FieldDefinition RequireField(TypeDefinition type, string name, string fieldType)
        {
            FieldDefinition field = type.Fields.SingleOrDefault(f => f.Name == name);
            if (field == null) throw new PatchException(type.FullName + ": field " + name + " not found");
            if (field.FieldType.FullName != fieldType)
                throw new PatchException(type.FullName + "::" + name + " is " + field.FieldType.FullName + ", expected " + fieldType);
            return field;
        }

        /// <summary>The public static method of McvsOffline.Server.dll a patch calls, checked against its expected signature.</summary>
        protected static MethodReference ImportServerMethod(ModuleDefinition target, PatchContext context, string typeName,
                                                             string methodName, string returnType, params string[] parameterTypes)
        {
            TypeDefinition type = context.Server.GetType(typeName);
            if (type == null)
                throw new PatchException("McvsOffline.Server.dll has no type " + typeName + " (is it the embedded build of this version?)");
            MethodDefinition method = type.Methods.SingleOrDefault(m =>
                m.Name == methodName && m.IsStatic && m.IsPublic && m.ReturnType.FullName == returnType &&
                m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameterTypes));
            if (method == null)
            {
                throw new PatchException("McvsOffline.Server.dll has no public static " + returnType + " " + typeName + "::" + methodName +
                                         "(" + string.Join(", ", parameterTypes) + ")");
            }
            return target.ImportReference(method);
        }

        protected static bool IsCall(Instruction i, string declaringType, string name)
        {
            if (i.OpCode != OpCodes.Call && i.OpCode != OpCodes.Callvirt) return false;
            return i.Operand is MethodReference m && m.Name == name && m.DeclaringType.FullName == declaringType;
        }

        /// <summary>"IL_0007: ldc.i4.2" style rendering of a body for error messages.</summary>
        protected static string Dump(MethodDefinition method)
        {
            return string.Join("\n", method.Body.Instructions.Select(i => "    " + i));
        }

        /// <summary>
        /// SHA-256 (hex) of a method body exactly as read: local types, every instruction with its offset and operand
        /// ("IL_0019: call System.Boolean Gameloft.Engine.PhysicsComponent::get_PhysicsMultithreaded()"), every exception
        /// handler. Two bodies with the same fingerprint are the same IL, so a patch that checks it changes only the code
        /// that was verified by hand.
        /// </summary>
        public static string Fingerprint(MethodDefinition method)
        {
            var text = new System.Text.StringBuilder();
            text.Append("locals:");
            foreach (VariableDefinition v in method.Body.Variables) text.Append(' ').Append(v.VariableType.FullName);
            text.Append('\n');
            foreach (Instruction i in method.Body.Instructions) text.Append(i).Append('\n');
            foreach (ExceptionHandler h in method.Body.ExceptionHandlers)
            {
                text.Append("eh ").Append(h.HandlerType).Append(' ').Append(Label(h.TryStart)).Append('-').Append(Label(h.TryEnd))
                    .Append(" -> ").Append(Label(h.HandlerStart)).Append('-').Append(Label(h.HandlerEnd))
                    .Append(' ').Append(h.CatchType == null ? "" : h.CatchType.FullName).Append('\n');
            }
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
                return string.Concat(hash.Select(b => b.ToString("x2")));
            }
        }

        private static string Label(Instruction i)
        {
            return i == null ? "end" : "IL_" + i.Offset.ToString("x4");
        }

        /// <summary>Throws unless <paramref name="method"/> is exactly the verified 1.17.32 body.</summary>
        protected static void RequireFingerprint(MethodDefinition method, string expected, string what)
        {
            string actual = Fingerprint(method);
            if (actual != expected)
            {
                throw new PatchException(method.FullName + " is not the verified 1.17.32 code (" + what + "; IL fingerprint " + actual +
                                         ", expected " + expected + "):\n" + Dump(method));
            }
        }

        /// <summary>A method of the game's own (trimmed) mscorlib, checked to exist there.</summary>
        protected static MethodReference CorlibMethod(ModuleDefinition module, TypeReference declaringType, string name, bool hasThis,
                                                      TypeReference returnType, params TypeReference[] parameters)
        {
            var reference = new MethodReference(name, returnType, declaringType) { HasThis = hasThis };
            foreach (TypeReference p in parameters) reference.Parameters.Add(new ParameterDefinition(p));
            MethodDefinition resolved;
            try
            {
                resolved = reference.Resolve();
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("cannot resolve the game's mscorlib for " + reference.FullName + ": " + ex.Message);
            }
            if (resolved == null) throw new PatchException("the game's mscorlib has no " + reference.FullName + " (trimmed)");
            return module.ImportReference(reference);
        }

        /// <summary>
        /// Like <see cref="CorlibMethod"/>, for a member of the <c>System</c> assembly the module already references (PC:
        /// <c>TcpListener</c>, <c>IPEndPoint</c>, resolved through the BCL directory). Resolve() proves it exists; a module
        /// without a System reference, or a member that does not resolve, is a <see cref="PatchException"/>.
        /// </summary>
        protected static MethodReference SystemMethod(ModuleDefinition module, string typeFullName, string name, bool instance,
                                                      TypeReference returnType, params TypeReference[] parameters)
        {
            AssemblyNameReference system = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System");
            if (system == null) throw new PatchException(module.Name + " has no reference to System (needed for " + typeFullName + "::" + name + ")");
            int dot = typeFullName.LastIndexOf('.');
            string ns = dot < 0 ? "" : typeFullName.Substring(0, dot);
            string typeName = dot < 0 ? typeFullName : typeFullName.Substring(dot + 1);
            var declaringType = new TypeReference(ns, typeName, module, system);
            var reference = new MethodReference(name, returnType, declaringType) { HasThis = instance };
            foreach (TypeReference p in parameters) reference.Parameters.Add(new ParameterDefinition(p));
            MethodDefinition resolved;
            try
            {
                resolved = reference.Resolve();
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("cannot resolve System for " + reference.FullName + ": " + ex.Message);
            }
            if (resolved == null) throw new PatchException("System " + system.Version + " has no " + reference.FullName);
            return module.ImportReference(reference);
        }

        /// <summary>Wraps <paramref name="instructions"/> in try { ... } catch (object) { pop; [onCatch] } at the method start.</summary>
        protected static void PrependGuarded(MethodDefinition method, IList<Instruction> instructions, IList<Instruction> onCatch)
        {
            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            var tryStart = instructions[0];
            var leaveTry = il.Create(OpCodes.Leave, first);
            var handlerStart = il.Create(OpCodes.Pop);
            var leaveHandler = il.Create(OpCodes.Leave, first);

            var sequence = new List<Instruction>(instructions) { leaveTry, handlerStart };
            if (onCatch != null) sequence.AddRange(onCatch);
            sequence.Add(leaveHandler);
            foreach (Instruction instruction in sequence) il.InsertBefore(first, instruction);

            var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart,
                TryEnd = handlerStart,
                HandlerStart = handlerStart,
                HandlerEnd = first,
                CatchType = method.Module.TypeSystem.Object,
            };
            method.Body.ExceptionHandlers.Insert(0, handler);
        }
    }
}
