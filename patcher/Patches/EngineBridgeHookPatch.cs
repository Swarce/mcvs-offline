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
    ///     try { McvsOfflineInit(); } catch { }               // added: the server, settings, args.txt before the engine reads it
    ///     MainLoop.Start(ModeType.NativeHostsDotNet, null, false);   // original body, unchanged
    /// }
    /// [MethodImpl(NoInlining)] private static void McvsOfflineInit() { McvsOffline.Bootstrap.Init(); }
    /// </code>
    /// The non-inlined helper keeps a missing or unloadable McvsOffline.Server.dll inside the try (the bind fails when the
    /// helper is compiled, at the call). Checked first: both methods of <c>Class1</c> against the fingerprints of build 6372049,
    /// no helper yet, and mscorlib 4 with token b77a5c561934e089 as the corlib (the Steam build; the Android server DLL would add
    /// mscorlib 2.0.5.0).
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
            if (type.Methods.Any(m => m.Name == HelperName) ||
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

            // ---- change
            var helper = new MethodDefinition(HelperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                                              module.TypeSystem.Void);
            helper.ImplAttributes |= MethodImplAttributes.NoInlining;
            ILProcessor h = helper.Body.GetILProcessor();
            h.Emit(OpCodes.Call, init);
            h.Emit(OpCodes.Ret);
            type.Methods.Add(helper);

            ILProcessor il = start.Body.GetILProcessor();
            PrependGuarded(start, new[] { il.Create(OpCodes.Call, helper) }, null);
            context.Notes.Add(TypeName + "::Start: try { McvsOffline.Bootstrap.Init(); } catch { } before MainLoop.Start (the original body)");
        }

        /// <summary>The token as lower-case hex ("b77a5c561934e089"); "" when there is none.</summary>
        private static string TokenHex(byte[] token)
        {
            return token == null ? "" : string.Concat(token.Select(b => b.ToString("x2")));
        }
    }
}
