using Mono.Cecil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>headless-diag</c> (docs/design/pc-port.md §8.5), Gameloft.Engine.dll: the exceptions the engine swallows at
    /// TS/GS World construction (M1, <c>ServerManager._AddClientAsync</c>) and component creation (M4,
    /// <c>ComponentAssemblies.CreateInstance</c>) are reported to <c>McvsOffline.PcHooks</c>. Day-0 skeleton: refuses.
    /// </summary>
    public sealed class HeadlessDiagnosticsPatch : IlPatch
    {
        /// <summary>The helper type in namespace Gameloft.Engine; its presence is the witness (§3.9).</summary>
        public const string HelperType = "McvsWorldDiagnostics";

        public const string HelperNamespace = "Gameloft.Engine";

        public const string HooksType = "McvsOffline.PcHooks";

        public override string Id => "headless-diag";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "server World and component creation failures are logged instead of swallowed (Gameloft.Engine.dll, PC)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            throw new PatchException(Id + ": not implemented");
        }
    }
}
