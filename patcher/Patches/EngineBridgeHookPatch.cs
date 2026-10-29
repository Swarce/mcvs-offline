using Mono.Cecil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>hook-pc</c> (replaces Android's <c>hook</c>; docs/design/pc-port.md §8.2): <c>EngineBridge.Class1.Start()</c>
    /// first runs <c>try { McvsOfflineInit(); } catch { }</c>, a <c>[NoInlining]</c> helper calling
    /// <c>McvsOffline.Bootstrap.Init()</c>, then the stock <c>MainLoop.Start(0, null, false)</c>. Day-0 skeleton: refuses.
    /// </summary>
    public sealed class EngineBridgeHookPatch : IlPatch
    {
        public const string TypeName = "EngineBridge.Class1";

        /// <summary>The witness (§3.9): the private static helper, named as Android's (<see cref="BootstrapHookPatch.HelperName"/>).</summary>
        public const string HelperName = BootstrapHookPatch.HelperName;

        public override string Id => "hook-pc";

        public override string AssemblyFile => "EngineBridge.dll";

        public override string Description => "EngineBridge.Class1.Start calls McvsOffline.Bootstrap.Init (starts the offline server)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            throw new PatchException(Id + ": not implemented");
        }
    }
}
