using Mono.Cecil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>quicklaunch</c> (docs/design/pc-port.md §8.3), Gameloft.Engine.dll: P1 <c>BaseServer.get_Port</c> gets
    /// Android's body back, P2 <c>ClientComponent.QuickLaunch</c> goes through <c>McvsOffline.PcHooks.QuickLaunch</c> (stock path on
    /// any failure), P3 <c>MatchmakingComponent.AddAutomatchHostSettings</c> asks <c>PcHooks.DefaultAutomatchHost</c> first.
    /// Day-0 skeleton: refuses.
    /// </summary>
    public sealed class QuickLaunchPatch : IlPatch
    {
        /// <summary>The helper type in namespace Gameloft.Engine (internal, abstract sealed); its presence is the witness (§3.9).</summary>
        public const string HelperType = "McvsQuickLaunch";

        public const string HelperNamespace = "Gameloft.Engine";

        public const string HooksType = "McvsOffline.PcHooks";

        public override string Id => "quicklaunch";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "quick launch through the game's own LocalAnubis (Lobby, Controller, Finder) instead of the online matchmaker (Gameloft.Engine.dll, PC)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            throw new PatchException(Id + ": not implemented");
        }
    }
}
