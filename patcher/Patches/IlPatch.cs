using System;
using System.Collections.Generic;
using System.Linq;
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
        public PatchContext(ModuleDefinition server, int port)
        {
            Server = server;
            Port = port;
        }

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
