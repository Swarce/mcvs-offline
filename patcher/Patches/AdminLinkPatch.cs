using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'adminlink' patch of Gameloft.Engine.dll: the "MCVS Offline" inbox message opens the offline settings page
    /// with a fresh one-time ticket (docs/design/settings-page.md §9.3). The CRM <c>newsfeeds</c> item links to a fixed
    /// marker URL (<c>http://127.0.0.1:&lt;port&gt;/_mcvs/ui/#news</c>) that carries no secret; Flash <c>Inbox.gotoLink</c> opens
    /// it through <c>Native.OpenUrl</c> → <c>UIComponent.OpenUrl</c> or <c>MenuComponent.NativeOpenUrl</c> →
    /// <c>BrowserComponent.OpenUrl(url, ingame)</c>. Every caller funnels through that engine method (both registrations of
    /// the Flash native, <c>MenuManager.Hyperlink</c>, the dev-menu browser cheats and the host's own page opener), so it is
    /// hooked there rather than in <c>Game.UIComponent.OpenUrl</c>:
    /// <code>
    /// public void OpenUrl(string url, bool ingame = true)
    /// {
    ///     if (McvsOffline.AdminHooks.OpenIfPageMarker(url, this)) return;   // added
    ///     ...                                                               // original body, unchanged
    /// }
    /// </code>
    /// The hook compares the URL with the marker (ordinal, no allocation) and for it opens the page exactly as the Settings ›
    /// Account button does (trigger "news"), also when the native browser was never initialised offline; it returns false for
    /// every other URL (the host's own URL carries a ticket, so it passes through) and catches everything itself. The prefix
    /// sits outside the body's only try block (the youtube.com one). Without this patch the marker URL loads as it is: the
    /// page's view-only mode. Checked against the IL fingerprint of the verified 1.17.32 build first; a second run fails it.
    /// </summary>
    public sealed class AdminLinkPatch : IlPatch
    {
        public const string BrowserType = "Gameloft.Engine.BrowserComponent";
        public const string OpenUrlFingerprint = "7585cdc7e72ffbe58c2a11dbc768aa94cfad044bea81a0330aaee6291db456cb";

        /// <summary>PC <c>OpenUrl</c> (86 instructions: no Facebook hit, the survey branch; it opens the Steam overlay after login).</summary>
        public const string PcOpenUrlFingerprint = "7ff1c8c2e34c2619e262ce8c4c1ffced5134409db7106559d8eb5e4ed032ea29";
        public const string HooksType = AdminPagePatch.HooksType;

        public override string Id => "adminlink";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "the 'MCVS Offline' inbox message opens the offline settings page with a fresh one-time ticket (Gameloft.Engine.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            PatchSites.RequireBuildCorlib(module, context, Id);
            string fingerprint = ForBuild(context, OpenUrlFingerprint, PcOpenUrlFingerprint);
            TypeDefinition browser = RequireType(module, BrowserType);
            MethodDefinition openUrl = RequireMethod(browser, "OpenUrl",
                m => !m.IsStatic && m.IsPublic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                     m.Parameters[1].ParameterType.MetadataType == MetadataType.Boolean && m.ReturnType.MetadataType == MetadataType.Void,
                "public instance void OpenUrl(string, bool)");
            RequireFingerprint(openUrl, fingerprint, "BrowserComponent.OpenUrl(string, bool)");
            var code = openUrl.Body.Instructions;
            if (code[0].OpCode != OpCodes.Ldarg_0 || code[1].OpCode != OpCodes.Call || !IsCall(code[1], "Gameloft.Engine.Component", "get_World"))
                throw new PatchException(openUrl.FullName + ": the verified sites moved:\n" + Dump(openUrl));

            MethodReference hook = ImportServerMethod(module, context, HooksType, "OpenIfPageMarker", "System.Boolean", "System.String", "System.Object");

            // ---- change: L1 (brfalse.s jumps over the one-byte ret; the shipped body's own short branches keep their distances)
            ILProcessor il = openUrl.Body.GetILProcessor();
            Instruction first = code[0];
            var prefix = new[]
            {
                il.Create(OpCodes.Ldarg_1),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Call, hook),
                il.Create(OpCodes.Brfalse_S, first),
                il.Create(OpCodes.Ret),
            };
            foreach (Instruction instruction in prefix) il.InsertBefore(first, instruction);
            context.Notes.Add(openUrl.FullName + ": " + HooksType + ".OpenIfPageMarker(url, this) first; the inbox marker URL opens the " +
                              "settings page and returns, every other URL runs the original body");
        }
    }
}
