using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// The one mandatory patch (verify-C7 §5, §7): <c>Gameloft.NicePlaySDK.NicePlaySDKDLL.OnInitialize</c> is invoked
    /// reflectively by <c>XamarinMainActivity.OnCreate</c> before <c>MainLoop.Start</c>, the Java GameInstaller and native
    /// init, and its stock body only logs (<c>Enabled</c> is false). It becomes:
    /// <code>
    /// public static void OnInitialize()
    /// {
    ///     try { McvsOfflineInit(); } catch { }          // added
    ///     Log.Info("Initializing 9sPlay DLL handler"); ... // original body, unchanged
    /// }
    /// [MethodImpl(NoInlining)] private static void McvsOfflineInit() { McvsOffline.Bootstrap.Init(); }
    /// </code>
    /// OnCreate does not guard that reflective call, so a throw would crash the game; the try/catch plus the
    /// non-inlined helper make even a missing or unloadable McvsOffline.Server.dll harmless (the JIT failure surfaces
    /// when the helper is called, inside the try).
    /// </summary>
    public sealed class BootstrapHookPatch : IlPatch
    {
        public const string TypeName = "Gameloft.NicePlaySDK.NicePlaySDKDLL";
        public const string HelperName = "McvsOfflineInit";

        public override string Id => "hook";

        public override string AssemblyFile => "NicePlaySDK.dll";

        public override string Description => "NicePlaySDKDLL.OnInitialize calls McvsOffline.Bootstrap.Init (starts the offline server)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            ForBuild(context, Id, null);   // Android only: excluded on PC (docs/design/pc-port.md §8.8)
            PatchSites.RequireBuildCorlib(module, context, Id);
            TypeDefinition type = RequireType(module, TypeName);
            MethodDefinition onInitialize = RequireMethod(type, "OnInitialize",
                m => m.IsStatic && m.IsPublic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void,
                "public static void method without parameters");
            if (type.Methods.Any(m => m.Name == HelperName) ||
                onInitialize.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "McvsOffline.Bootstrap"))
            {
                throw new PatchException("NicePlaySDK.dll is already patched: run the patcher on your original APK");
            }
            MethodReference init = ImportServerMethod(module, context, "McvsOffline.Bootstrap", "Init", "System.Void");

            var helper = new MethodDefinition(HelperName, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                                              module.TypeSystem.Void);
            helper.ImplAttributes |= MethodImplAttributes.NoInlining;
            ILProcessor h = helper.Body.GetILProcessor();
            h.Emit(OpCodes.Call, init);
            h.Emit(OpCodes.Ret);
            type.Methods.Add(helper);

            ILProcessor il = onInitialize.Body.GetILProcessor();
            PrependGuarded(onInitialize, new[] { il.Create(OpCodes.Call, helper) }, null);
            context.Notes.Add(TypeName + "::OnInitialize: try { McvsOffline.Bootstrap.Init(); } catch { } before the original body");
        }
    }
}
