using System;
using System.Linq;
using McvsOffline.Patcher.Core;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>hook-pc</c> (replaces Android's <c>hook</c>; docs/design/pc-port.md §8.2). On Steam the native exe's first
    /// render callback calls <c>Wrapper.Start</c>, which calls <c>EngineBridge.Class1.Start()</c>; the stock body only starts the
    /// engine (<c>MainLoop.Start(NativeHostsDotNet, null, false)</c>, which reads args.txt). It becomes:
    /// <code>
    /// public void Start()
    /// {
    ///     McvsOfflineHookTrace(null);                         // added: the marker line in %TEMP%\McvsOffline\hook.txt
    ///     try { McvsOfflineInit(); }                          // added: the server, settings, args.txt before the engine reads it
    ///     catch (object e) { McvsOfflineHookTrace(e); }      // added: the exception text, appended to the same file
    ///     MainLoop.Start(ModeType.NativeHostsDotNet, null, false);   // original body, unchanged
    /// }
    /// [MethodImpl(NoInlining)] private static void McvsOfflineInit() { McvsOffline.Bootstrap.Init(); }
    /// [MethodImpl(NoInlining)] private static void McvsOfflineHookTrace(object e) { try { McvsOfflineHookWrite(e); } catch { } }
    /// [MethodImpl(NoInlining)] private static void McvsOfflineHookWrite(object e)
    /// {
    ///     string pid; try { pid = McvsOfflineGetCurrentProcessId().ToString(); } catch { pid = "?"; }
    ///     DateTime now = DateTime.UtcNow;
    ///     string what = e == null
    ///         ? string.Concat(new[] { "start pid ", pid, " exe ", Environment.GetCommandLineArgs()[0], " dll ", Assembly.GetExecutingAssembly().Location })
    ///         : string.Concat("McvsOfflineInit failed (pid ", pid, "): ", e.ToString());
    ///     string dir = Path.Combine(Path.GetTempPath(), "McvsOffline");
    ///     Directory.CreateDirectory(dir);
    ///     File.AppendAllText(Path.Combine(dir, "hook.txt"), string.Concat(now.ToString(MarkerTimeFormat, CultureInfo.InvariantCulture), " hook-pc ", what, "\r\n"));
    /// }
    /// [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")] private static extern uint McvsOfflineGetCurrentProcessId();
    /// </code>
    /// The marker uses mscorlib (and kernel32) only, so it is written even when McvsOffline.Server.dll cannot be loaded: a start
    /// line says our patched EngineBridge ran, and from which file; a "failed" line says why the server did not start; no
    /// line since the install, while the game did run, says Windows ran the stock EngineBridge (status reads it, §7.6).
    /// <c>McvsOfflineHookTrace</c> catches everything, including a member the JIT cannot bind in <c>McvsOfflineHookWrite</c>
    /// (that call is inside its try), so the marker can never stop the game. The non-inlined <c>McvsOfflineInit</c> keeps a
    /// missing or unloadable McvsOffline.Server.dll inside the try (the bind fails when the helper is compiled, at the call).
    /// Checked first: both methods of <c>Class1</c> against the fingerprints of build 6372049, no helper yet, and mscorlib 4 with
    /// token b77a5c561934e089 as the corlib (the Steam build; the Android server DLL would add mscorlib 2.0.5.0); after the edit,
    /// the new <c>Start</c> against <see cref="PcPatchedStartFingerprint"/>.
    /// </summary>
    public sealed class EngineBridgeHookPatch : IlPatch
    {
        public const string TypeName = "EngineBridge.Class1";

        /// <summary>The witness (§3.9): the private static helper, named as Android's (<see cref="BootstrapHookPatch.HelperName"/>).</summary>
        public const string HelperName = BootstrapHookPatch.HelperName;

        /// <summary><c>ldc.i4.0; ldnull; ldc.i4.0; call int32 MainLoop::Start(ModeType, string[], bool); pop; ret</c>.</summary>
        public const string PcStartFingerprint = "52063de24e52f57559dfaeb3485615933fe07e89665b7e7e4346d88a90857ac9";

        /// <summary><c>ldarg.0; call Object::.ctor(); ret</c>.</summary>
        public const string PcCtorFingerprint = "d5269750068c46d568d90f6889356b64b3ec93374d0b0175199fad928189ef8c";

        /// <summary>
        /// The patched <c>Start</c> (the self-check after the edit): <c>ldnull; call McvsOfflineHookTrace(object); try { call
        /// McvsOfflineInit(); leave } catch (object) { call McvsOfflineHookTrace(object); leave }</c>, then the six original
        /// instructions.
        /// </summary>
        public const string PcPatchedStartFingerprint = "136b34177fbd13488b65aec7855308c372ed52db218155737e848bea8a7d75dc";

        /// <summary>The marker's guard (<c>try { McvsOfflineHookWrite(e); } catch { }</c>); §3.9's witness stays <see cref="HelperName"/>.</summary>
        public const string TraceName = "McvsOfflineHookTrace";

        /// <summary>The marker's writer.</summary>
        public const string WriteName = "McvsOfflineHookWrite";

        /// <summary>The kernel32 <c>GetCurrentProcessId</c> import (mscorlib 4 has no process id of its own).</summary>
        public const string PidName = "McvsOfflineGetCurrentProcessId";

        /// <summary>The marker file is <c>%TEMP%\McvsOffline\hook.txt</c> (the folder of the host's fallback logs).</summary>
        public const string MarkerFolder = "McvsOffline";

        public const string MarkerFile = "hook.txt";

        /// <summary>The UTC time at the start of every marker line ("2026-10-30T18:05:00.123Z").</summary>
        public const string MarkerTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        /// <summary>After the time, a start line: " hook-pc start pid &lt;pid&gt; exe &lt;exe&gt; dll &lt;EngineBridge.dll loaded&gt;".</summary>
        public const string MarkerStart = " hook-pc start pid ";

        /// <summary>After the time, a failure line: " hook-pc McvsOfflineInit failed (pid &lt;pid&gt;): &lt;exception&gt;".</summary>
        public const string MarkerFailed = " hook-pc McvsOfflineInit failed (pid ";

        private const string MarkerTag = " hook-pc ";

        public override string Id => "hook-pc";

        public override string AssemblyFile => "EngineBridge.dll";

        public override string Description => "EngineBridge.Class1.Start calls McvsOffline.Bootstrap.Init (starts the offline server)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            string startFingerprint = ForBuild(context, null, PcStartFingerprint);
            string ctorFingerprint = ForBuild(context, null, PcCtorFingerprint);
            PatchSites.RequireBuildAssembly(module, context, Id);
            TypeDefinition type = RequireType(module, TypeName);
            MethodDefinition start = RequireMethod(type, "Start",
                m => !m.IsStatic && m.IsPublic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void,
                "public instance void method without parameters");
            MethodDefinition ctor = RequireMethod(type, ".ctor", m => !m.IsStatic && !m.HasParameters, "parameterless constructor");
            if (type.Methods.Any(m => m.Name == HelperName || m.Name == TraceName) ||
                start.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "McvsOffline.Bootstrap"))
            {
                throw new PatchException("EngineBridge.dll is already patched: install from the game's own EngineBridge.dll, not our copy");
            }
            RequireFingerprint(start, startFingerprint, "EngineBridge.Class1.Start()");
            RequireFingerprint(ctor, ctorFingerprint, "EngineBridge.Class1()");
            var corlib = module.TypeSystem.CoreLibrary as AssemblyNameReference;
            if (corlib == null || corlib.Name != "mscorlib" || corlib.Version == null || corlib.Version.Major != 4 ||
                TokenHex(corlib.PublicKeyToken) != BuildProfile.PcSteam6372049.CorlibPublicKeyToken)
                throw new PatchException("EngineBridge.dll does not reference mscorlib 4.x (" + module.TypeSystem.CoreLibrary + "): not the Steam build");
            MethodReference init = ImportServerMethod(module, context, "McvsOffline.Bootstrap", "Init", "System.Void");
            MarkerMembers marker = MarkerMembers.Of(module);

            // ---- change: the helpers
            var helper = new MethodDefinition(HelperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                                              module.TypeSystem.Void);
            helper.ImplAttributes |= MethodImplAttributes.NoInlining;
            ILProcessor h = helper.Body.GetILProcessor();
            h.Emit(OpCodes.Call, init);
            h.Emit(OpCodes.Ret);
            type.Methods.Add(helper);
            MethodDefinition pid = AddPid(module, type);
            MethodDefinition write = AddWrite(module, type, marker, pid);
            MethodDefinition trace = AddTrace(module, type, write);

            // ---- change: Start
            ILProcessor il = start.Body.GetILProcessor();
            Instruction first = start.Body.Instructions[0];
            Instruction callInit = il.Create(OpCodes.Call, helper);
            Instruction traceError = il.Create(OpCodes.Call, trace);
            foreach (Instruction i in new[]
            {
                il.Create(OpCodes.Ldnull), il.Create(OpCodes.Call, trace),
                callInit, il.Create(OpCodes.Leave, first),
                traceError, il.Create(OpCodes.Leave, first),
            })
            {
                il.InsertBefore(first, i);
            }
            start.Body.ExceptionHandlers.Insert(0, new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = callInit, TryEnd = traceError, HandlerStart = traceError, HandlerEnd = first, CatchType = module.TypeSystem.Object,
            });
            PatchSites.ComputeOffsets(start.Body);
            string patched = Fingerprint(start);
            if (patched != PcPatchedStartFingerprint)
                throw new PatchException(start.FullName + ": the patched body is not the designed one (fingerprint " + patched + "):\n" + Dump(start));
            context.Notes.Add(TypeName + "::Start: the %TEMP%\\" + MarkerFolder + "\\" + MarkerFile + " marker, then try { McvsOffline.Bootstrap.Init(); } " +
                              "catch { the exception into the marker } before MainLoop.Start (the original body)");
        }

        /// <summary>The mscorlib members the marker calls, each resolved in the game's mscorlib (a missing one refuses the patch).</summary>
        private sealed class MarkerMembers
        {
            public TypeReference DateTime;
            public MethodReference GetTempPath, Combine, CreateDirectory, AppendAllText, UtcNow, DateToString, Invariant, CommandLineArgs,
                                   ExecutingAssembly, Location, Concat4, ConcatArray, ObjectToString;

            public static MarkerMembers Of(ModuleDefinition module)
            {
                TypeSystem ts = module.TypeSystem;
                TypeReference path = PatchSites.CorlibType(module, "System.IO", "Path");
                TypeReference directory = PatchSites.CorlibType(module, "System.IO", "Directory");
                TypeReference directoryInfo = PatchSites.CorlibType(module, "System.IO", "DirectoryInfo");
                TypeReference file = PatchSites.CorlibType(module, "System.IO", "File");
                TypeReference date = PatchSites.CorlibType(module, "System", "DateTime");
                TypeReference culture = PatchSites.CorlibType(module, "System.Globalization", "CultureInfo");
                TypeReference provider = PatchSites.CorlibType(module, "System", "IFormatProvider");
                TypeReference environment = PatchSites.CorlibType(module, "System", "Environment");
                TypeReference assembly = PatchSites.CorlibType(module, "System.Reflection", "Assembly");
                var strings = new ArrayType(ts.String);
                return new MarkerMembers
                {
                    DateTime = date,
                    GetTempPath = CorlibMethod(module, path, "GetTempPath", false, ts.String),
                    Combine = CorlibMethod(module, path, "Combine", false, ts.String, ts.String, ts.String),
                    CreateDirectory = CorlibMethod(module, directory, "CreateDirectory", false, directoryInfo, ts.String),
                    AppendAllText = CorlibMethod(module, file, "AppendAllText", false, ts.Void, ts.String, ts.String),
                    UtcNow = CorlibMethod(module, date, "get_UtcNow", false, date),
                    DateToString = CorlibMethod(module, date, "ToString", true, ts.String, ts.String, provider),
                    Invariant = CorlibMethod(module, culture, "get_InvariantCulture", false, culture),
                    CommandLineArgs = CorlibMethod(module, environment, "GetCommandLineArgs", false, strings),
                    ExecutingAssembly = CorlibMethod(module, assembly, "GetExecutingAssembly", false, assembly),
                    Location = CorlibMethod(module, assembly, "get_Location", true, ts.String),
                    Concat4 = CorlibMethod(module, ts.String, "Concat", false, ts.String, ts.String, ts.String, ts.String, ts.String),
                    ConcatArray = CorlibMethod(module, ts.String, "Concat", false, ts.String, strings),
                    ObjectToString = CorlibMethod(module, ts.Object, "ToString", true, ts.String),
                };
            }
        }

        /// <summary><c>[DllImport("kernel32.dll", EntryPoint = "GetCurrentProcessId")] private static extern uint McvsOfflineGetCurrentProcessId()</c>.</summary>
        private static MethodDefinition AddPid(ModuleDefinition module, TypeDefinition type)
        {
            ModuleReference kernel32 = module.ModuleReferences.FirstOrDefault(m => string.Equals(m.Name, "kernel32.dll", StringComparison.OrdinalIgnoreCase));
            if (kernel32 == null)
            {
                kernel32 = new ModuleReference("kernel32.dll");
                module.ModuleReferences.Add(kernel32);
            }
            var pid = new MethodDefinition(PidName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.PInvokeImpl,
                                           module.TypeSystem.UInt32);
            pid.ImplAttributes |= MethodImplAttributes.PreserveSig;
            pid.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvWinapi | PInvokeAttributes.NoMangle, "GetCurrentProcessId", kernel32);
            type.Methods.Add(pid);
            return pid;
        }

        /// <summary><c>McvsOfflineHookWrite(object error)</c>: one marker line (the class comment has its C#).</summary>
        private static MethodDefinition AddWrite(ModuleDefinition module, TypeDefinition type, MarkerMembers m, MethodDefinition pid)
        {
            TypeSystem ts = module.TypeSystem;
            var write = new MethodDefinition(WriteName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            write.ImplAttributes |= MethodImplAttributes.NoInlining;
            write.Parameters.Add(new ParameterDefinition("error", ParameterAttributes.None, ts.Object));
            MethodBody body = write.Body;
            body.InitLocals = true;
            var pidText = new VariableDefinition(ts.String);
            var now = new VariableDefinition(m.DateTime);
            var what = new VariableDefinition(ts.String);
            body.Variables.Add(pidText);
            body.Variables.Add(now);
            body.Variables.Add(what);
            ILProcessor il = body.GetILProcessor();

            // string pid; try { pid = GetCurrentProcessId().ToString(); } catch { pid = "?"; }
            Instruction afterPid = il.Create(OpCodes.Call, m.UtcNow);
            Instruction pidTry = il.Create(OpCodes.Call, pid);
            Instruction pidCatch = il.Create(OpCodes.Pop);
            il.Append(pidTry);
            il.Emit(OpCodes.Box, ts.UInt32);
            il.Emit(OpCodes.Callvirt, m.ObjectToString);
            il.Emit(OpCodes.Stloc, pidText);
            il.Emit(OpCodes.Leave, afterPid);
            il.Append(pidCatch);
            il.Emit(OpCodes.Ldstr, "?");
            il.Emit(OpCodes.Stloc, pidText);
            il.Emit(OpCodes.Leave, afterPid);
            body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = pidTry, TryEnd = pidCatch, HandlerStart = pidCatch, HandlerEnd = afterPid, CatchType = ts.Object,
            });

            // DateTime now = DateTime.UtcNow; string what = error == null ? the start text : the failure text;
            il.Append(afterPid);
            il.Emit(OpCodes.Stloc, now);
            Instruction failed = il.Create(OpCodes.Ldstr, MarkerFailed.Substring(MarkerTag.Length));
            Instruction writeLine = il.Create(OpCodes.Call, m.GetTempPath);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Brtrue, failed);
            il.Emit(OpCodes.Ldc_I4_6);
            il.Emit(OpCodes.Newarr, ts.String);
            Element(il, 0, i => i.Emit(OpCodes.Ldstr, MarkerStart.Substring(MarkerTag.Length)));
            Element(il, 1, i => i.Emit(OpCodes.Ldloc, pidText));
            Element(il, 2, i => i.Emit(OpCodes.Ldstr, " exe "));
            Element(il, 3, i =>
            {
                i.Emit(OpCodes.Call, m.CommandLineArgs);
                i.Emit(OpCodes.Ldc_I4_0);
                i.Emit(OpCodes.Ldelem_Ref);
            });
            Element(il, 4, i => i.Emit(OpCodes.Ldstr, " dll "));
            Element(il, 5, i =>
            {
                i.Emit(OpCodes.Call, m.ExecutingAssembly);
                i.Emit(OpCodes.Callvirt, m.Location);
            });
            il.Emit(OpCodes.Call, m.ConcatArray);
            il.Emit(OpCodes.Stloc, what);
            il.Emit(OpCodes.Br, writeLine);
            il.Append(failed);
            il.Emit(OpCodes.Ldloc, pidText);
            il.Emit(OpCodes.Ldstr, "): ");
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, m.ObjectToString);
            il.Emit(OpCodes.Call, m.Concat4);
            il.Emit(OpCodes.Stloc, what);

            // string dir = Path.Combine(Path.GetTempPath(), "McvsOffline"); Directory.CreateDirectory(dir);
            // File.AppendAllText(Path.Combine(dir, "hook.txt"), now.ToString(format, invariant) + " hook-pc " + what + "\r\n");
            il.Append(writeLine);
            il.Emit(OpCodes.Ldstr, MarkerFolder);
            il.Emit(OpCodes.Call, m.Combine);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Call, m.CreateDirectory);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldstr, MarkerFile);
            il.Emit(OpCodes.Call, m.Combine);
            il.Emit(OpCodes.Ldloca, now);
            il.Emit(OpCodes.Ldstr, MarkerTimeFormat);
            il.Emit(OpCodes.Call, m.Invariant);
            il.Emit(OpCodes.Call, m.DateToString);
            il.Emit(OpCodes.Ldstr, MarkerTag);
            il.Emit(OpCodes.Ldloc, what);
            il.Emit(OpCodes.Ldstr, "\r\n");
            il.Emit(OpCodes.Call, m.Concat4);
            il.Emit(OpCodes.Call, m.AppendAllText);
            il.Emit(OpCodes.Ret);
            type.Methods.Add(write);
            return write;
        }

        /// <summary><c>dup; ldc.i4 index; &lt;value&gt;; stelem.ref</c> (one element of a new string[]).</summary>
        private static void Element(ILProcessor il, int index, Action<ILProcessor> value)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, index);
            value(il);
            il.Emit(OpCodes.Stelem_Ref);
        }

        /// <summary><c>McvsOfflineHookTrace(object error) { try { McvsOfflineHookWrite(error); } catch { } }</c>.</summary>
        private static MethodDefinition AddTrace(ModuleDefinition module, TypeDefinition type, MethodDefinition write)
        {
            TypeSystem ts = module.TypeSystem;
            var trace = new MethodDefinition(TraceName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, ts.Void);
            trace.ImplAttributes |= MethodImplAttributes.NoInlining;
            trace.Parameters.Add(new ParameterDefinition("error", ParameterAttributes.None, ts.Object));
            ILProcessor il = trace.Body.GetILProcessor();
            Instruction ret = il.Create(OpCodes.Ret);
            Instruction tryStart = il.Create(OpCodes.Ldarg_0);
            Instruction handler = il.Create(OpCodes.Pop);
            il.Append(tryStart);
            il.Emit(OpCodes.Call, write);
            il.Emit(OpCodes.Leave, ret);
            il.Append(handler);
            il.Emit(OpCodes.Leave, ret);
            il.Append(ret);
            trace.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = ret, CatchType = ts.Object,
            });
            type.Methods.Add(trace);
            return trace;
        }

        /// <summary>The token as lower-case hex ("b77a5c561934e089"); "" when there is none.</summary>
        private static string TokenHex(byte[] token)
        {
            return token == null ? "" : string.Concat(token.Select(b => b.ToString("x2")));
        }
    }
}
