using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Optional 'forum' patch of Game.dll (not default): the Settings "Forum" button opens the offline settings page
    /// (docs/design/settings-page.md §9.4). Settings <c>btn_forum</c> calls <c>Native.GoToForum</c> →
    /// <c>UIComponent.GoToForum()</c>, whose body <c>base.World.Browser.ShowForum()</c> is dead offline. The body becomes
    /// <code>
    /// public void GoToForum() { McvsOffline.AdminHooks.OpenPage("forum", base.World); }
    /// </code>
    /// (the hook catches everything itself). The button keeps its "Forum" label and icon. Checked against the IL fingerprint
    /// of the verified 1.17.32 build first; a second run fails it.
    /// </summary>
    public sealed class ForumPatch : IlPatch
    {
        public const string UiType = "Game.UIComponent";
        public const string ForumFingerprint = "0b1e47e654f8198a7fef5095296c31c61693a5ecbe56eace9aee742789a93e8e";
        public const string HooksType = AdminPagePatch.HooksType;

        /// <summary>The trigger name the hook is called with (AdminTriggers.Forum).</summary>
        public const string Trigger = "forum";

        public override string Id => "forum";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "Settings > Forum opens the offline settings page (Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            PatchSites.RequireBuildCorlib(module, context, Id);
            // ---- locate and verify
            TypeDefinition ui = RequireType(module, UiType);
            MethodDefinition forum = RequireMethod(ui, "GoToForum",
                m => !m.IsStatic && m.IsPublic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Void,
                "public instance void GoToForum()");
            RequireFingerprint(forum, ForumFingerprint, "UIComponent.GoToForum");
            var code = forum.Body.Instructions;
            if (code[1].OpCode != OpCodes.Call || !IsCall(code[1], "Gameloft.Engine.Component", "get_World"))
                throw new PatchException(forum.FullName + ": the verified sites moved:\n" + Dump(forum));
            var getWorld = (MethodReference)code[1].Operand;

            MethodReference openPage = ImportServerMethod(module, context, HooksType, "OpenPage", "System.Void", "System.String", "System.Object");

            // ---- change: the whole body
            code.Clear();
            ILProcessor il = forum.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, Trigger);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getWorld);
            il.Emit(OpCodes.Call, openPage);
            il.Emit(OpCodes.Ret);
            context.Notes.Add(forum.FullName + ": body -> " + HooksType + ".OpenPage(\"" + Trigger + "\", World) (was World.Browser.ShowForum(), " +
                              "dead offline)");
        }
    }
}
