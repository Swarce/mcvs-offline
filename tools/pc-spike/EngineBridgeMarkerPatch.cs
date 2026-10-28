using System;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The WP0 step 2 marker edit (pc-port.md §5) of the Steam build's <c>EngineBridge.dll</c> (4 KB, net451): it only
    /// proves that the CLR loaded our copy through <c>ModernCombatVersus.exe.config</c>. The verified body of
    /// <c>EngineBridge.Class1.Start</c> (Steam build 6372049, 1.17.30) is
    /// <code>
    /// IL_0000: ldc.i4.0                       // MainLoop.ModeType.NativeHostsDotNet
    /// IL_0001: ldnull                         // args
    /// IL_0002: ldc.i4.0                       // isEditor
    /// IL_0003: call int32 Gameloft.Engine.MainLoop::Start(ModeType, string[], bool)
    /// IL_0008: pop
    /// IL_0009: ret
    /// </code>
    /// and becomes
    /// <code>
    /// public void Start()
    /// {
    ///     try { McvsSpikeMarker(); } catch { }                         // added
    ///     MainLoop.Start(MainLoop.ModeType.NativeHostsDotNet);         // original body, unchanged
    /// }
    /// [MethodImpl(NoInlining)] private static void McvsSpikeMarker()
    /// {
    ///     File.WriteAllText("&lt;marker path&gt;", "McvsOffline pc-spike marker\r\nlocation=" + Assembly.GetExecutingAssembly().Location +
    ///                       "\r\ncwd=" + Environment.CurrentDirectory + "\r\ncommandline=" + Environment.CommandLine + "\r\n");
    /// }
    /// </code>
    /// Nothing else changes. The marker records which file the CLR loaded, and the current directory and command line at the
    /// moment <c>Class1.Start</c> runs, i.e. before native <c>MainLoop_Start</c> reads args.txt (unknown 4). Every member
    /// it calls is a .NET Framework 2.0+ mscorlib member, referenced through the module's own <c>mscorlib 4.0.0.0</c> (the
    /// tests resolve them against the .NET Framework 4.5 reference assemblies); the guard keeps the game starting even if
    /// the write fails. The same site is where WP3's <c>EngineBridgeHookPatch</c> will call <c>McvsOffline.Bootstrap.Init</c>.
    /// </summary>
    public sealed class EngineBridgeMarkerPatch : IlPatch
    {
        public const string TypeName = "EngineBridge.Class1";
        public const string MethodName = "Start";
        public const string HelperName = "McvsSpikeMarker";

        /// <summary>The first line of the marker file.</summary>
        public const string MarkerHeader = "McvsOffline pc-spike marker";

        /// <summary><see cref="IlPatch.Fingerprint"/> of the verified <c>Class1.Start</c> (EngineBridge.dll sha256 ce2655ee…7a595).</summary>
        public const string StartFingerprint = "52063de24e52f57559dfaeb3485615933fe07e89665b7e7e4346d88a90857ac9";

        private readonly string markerPath;

        public EngineBridgeMarkerPatch(string markerPath)
        {
            if (string.IsNullOrEmpty(markerPath) || !Path.IsPathFullyQualified(markerPath))
                throw new ArgumentException("the marker path must be absolute: " + markerPath, nameof(markerPath));
            this.markerPath = markerPath;
        }

        public override string Id => "spike-marker";

        public override string AssemblyFile => "EngineBridge.dll";

        public override string Description => "EngineBridge.Class1.Start writes a marker file, then starts the game as before (WP0 redirect test)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            TypeDefinition type = RequireType(module, TypeName);
            MethodDefinition start = RequireMethod(type, MethodName,
                m => !m.IsStatic && m.IsPublic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void,
                "public instance void method without parameters");
            if (type.Methods.Any(m => m.Name == HelperName))
                throw new PatchException("EngineBridge.dll already has " + HelperName + ": use the game's own EngineBridge.dll, not our copy");
            string actual = Fingerprint(start);
            if (actual != StartFingerprint)
            {
                throw new PatchException(start.FullName + " is not the verified Steam 1.17.30 code (IL fingerprint " + actual + ", expected " +
                                         StartFingerprint + "):\n" + Dump(start));
            }

            var corlib = module.TypeSystem.CoreLibrary as AssemblyNameReference;
            if (corlib == null || corlib.Name != "mscorlib" || corlib.Version == null || corlib.Version.Major != 4)
                throw new PatchException("EngineBridge.dll does not reference mscorlib 4.x (" + module.TypeSystem.CoreLibrary + "): not the Steam build");

            TypeReference stringType = module.TypeSystem.String;
            TypeReference assemblyType = new TypeReference("System.Reflection", "Assembly", module, corlib);
            TypeReference environmentType = new TypeReference("System", "Environment", module, corlib);
            TypeReference fileType = new TypeReference("System.IO", "File", module, corlib);

            MethodReference getExecutingAssembly = Method(module, assemblyType, "GetExecutingAssembly", false, assemblyType);
            MethodReference getLocation = Method(module, assemblyType, "get_Location", true, stringType);
            MethodReference getCurrentDirectory = Method(module, environmentType, "get_CurrentDirectory", false, stringType);
            MethodReference getCommandLine = Method(module, environmentType, "get_CommandLine", false, stringType);
            MethodReference concat4 = Method(module, module.TypeSystem.String, "Concat", false, stringType, stringType, stringType, stringType, stringType);
            MethodReference writeAllText = Method(module, fileType, "WriteAllText", false, module.TypeSystem.Void, stringType, stringType);

            var helper = new MethodDefinition(HelperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                                              module.TypeSystem.Void);
            helper.ImplAttributes |= MethodImplAttributes.NoInlining;
            ILProcessor h = helper.Body.GetILProcessor();
            h.Emit(OpCodes.Ldstr, markerPath);
            h.Emit(OpCodes.Ldstr, MarkerHeader + "\r\nlocation=");
            h.Emit(OpCodes.Call, getExecutingAssembly);
            h.Emit(OpCodes.Callvirt, getLocation);
            h.Emit(OpCodes.Ldstr, "\r\ncwd=");
            h.Emit(OpCodes.Call, getCurrentDirectory);
            h.Emit(OpCodes.Call, concat4);
            h.Emit(OpCodes.Ldstr, "\r\ncommandline=");
            h.Emit(OpCodes.Call, getCommandLine);
            h.Emit(OpCodes.Ldstr, "\r\n");
            h.Emit(OpCodes.Call, concat4);
            h.Emit(OpCodes.Call, writeAllText);
            h.Emit(OpCodes.Ret);
            type.Methods.Add(helper);

            ILProcessor il = start.Body.GetILProcessor();
            PrependGuarded(start, new[] { il.Create(OpCodes.Call, helper) }, null);
            context.Notes.Add(TypeName + "::" + MethodName + ": try { " + HelperName + "(); } catch { } before the original body; the marker goes to " + markerPath);
        }

        /// <summary>A reference to an mscorlib member through the module's own mscorlib scope (no resolution: the owner's PC
        /// has no reference assemblies, and the tests check every member against the .NET Framework 4.5 ones).</summary>
        private static MethodReference Method(ModuleDefinition module, TypeReference declaringType, string name, bool hasThis,
                                              TypeReference returnType, params TypeReference[] parameters)
        {
            var reference = new MethodReference(name, returnType, declaringType) { HasThis = hasThis };
            foreach (TypeReference p in parameters) reference.Parameters.Add(new ParameterDefinition(p));
            return module.ImportReference(reference);
        }
    }
}
