using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Default 'adminpage' patch of Game.dll: an "MCVS Offline" button in Settings › Account that opens the offline settings
    /// page (docs/design/settings-page.md §9.2). Flash <c>MenuAccountSettings</c> builds the Account submenu from
    /// <c>Native.GetAccountMenuButtonList()</c>, handles <c>customAction == "showRedeem"</c> itself and forwards every other
    /// <c>customAction</c> to <c>Native.ProcessAccountMenuButtonAction(customAction)</c>; both natives are
    /// <c>PlayerProfileComponent</c> methods (<c>UI.OverrideNativeFunction</c>), so they run on the game thread and no Flash
    /// change is needed.
    /// <list type="bullet">
    /// <item>A1 <c>GetAccountMenuButtonList()</c> returns <c>McvsOffline.AdminHooks.AccountMenuButtons(list)</c>: the hook appends
    /// <c>{customAction: "mcvs_offline", isClickable, label, icon}</c> after "Recover progress" (unchanged list when the page is
    /// disabled).</item>
    /// <item>A2 <c>ProcessAccountMenuButtonAction(action)</c> starts with
    /// <code>
    /// if (action == "mcvs_offline") { McvsOffline.AdminHooks.OpenPage("account", base.World); return; }
    /// </code>
    /// and every other action runs the shipped body.</item>
    /// </list>
    /// Both hooks catch everything themselves, so no exception handler is added. Both methods are checked against the IL
    /// fingerprint of the verified 1.17.32 build first; a second run on a patched Game.dll fails that check.
    /// </summary>
    public sealed class AdminPagePatch : IlPatch
    {
        public const string ProfileType = "Game.PlayerProfileComponent";
        public const string ButtonListFingerprint = "5522a99141e39edccee31473340f733dbeaa8885316247f3349cd74d5238d5df";
        public const string ButtonActionFingerprint = "6bb9a563929381d3a3e60c77378b758aa63604a1ee6933551b752ac67564c053";
        public const string HooksType = "McvsOffline.AdminHooks";

        /// <summary>The button's customAction (McvsOffline.AdminHooks.ButtonAction).</summary>
        public const string ButtonAction = "mcvs_offline";

        /// <summary>The trigger name the hook is called with (AdminTriggers.Account).</summary>
        public const string Trigger = "account";

        internal const string ButtonList = "System.Collections.Generic.IEnumerable`1<System.Object>";

        public override string Id => "adminpage";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "Settings > Account: an 'MCVS Offline' button that opens the offline settings page (Game.dll)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            TypeDefinition profile = RequireType(module, ProfileType);
            MethodDefinition list = RequireMethod(profile, "GetAccountMenuButtonList",
                m => !m.IsStatic && m.IsPrivate && !m.HasParameters && m.ReturnType.FullName == ButtonList,
                "private instance IEnumerable<object> GetAccountMenuButtonList()");
            RequireFingerprint(list, ButtonListFingerprint, "PlayerProfileComponent.GetAccountMenuButtonList");
            MethodDefinition action = RequireMethod(profile, "ProcessAccountMenuButtonAction",
                m => !m.IsStatic && m.IsPrivate && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
                     m.ReturnType.MetadataType == MetadataType.Void,
                "private instance void ProcessAccountMenuButtonAction(string)");
            RequireFingerprint(action, ButtonActionFingerprint, "PlayerProfileComponent.ProcessAccountMenuButtonAction");

            // A1 site: the single ret (IL_0150) right after ldloc.0 (IL_014f), and no branch to the ret itself (every path
            // that returns the list goes through the ldloc.0 the hook call follows).
            var listCode = list.Body.Instructions;
            Instruction ret = listCode[listCode.Count - 1];
            Instruction load = listCode[listCode.Count - 2];
            if (listCode.Count(i => i.OpCode == OpCodes.Ret) != 1 || ret.OpCode != OpCodes.Ret || ret.Offset != 0x150 ||
                load.OpCode != OpCodes.Ldloc_0 || load.Offset != 0x14f ||
                listCode.Any(i => i.Operand == ret || (i.Operand is Instruction[] targets && targets.Contains(ret))))
                throw new PatchException(list.FullName + ": the verified sites moved:\n" + Dump(list));

            // A2 references, taken from the verified code itself: IL_0001 base.World, IL_0018 string ==.
            var actionCode = action.Body.Instructions;
            Instruction At(int offset) => actionCode.Single(i => i.Offset == offset);
            Instruction worldCall = At(0x01), equalsCall = At(0x18);
            if (worldCall.OpCode != OpCodes.Call || !IsCall(worldCall, "Gameloft.Engine.Component", "get_World") ||
                equalsCall.OpCode != OpCodes.Call || !IsCall(equalsCall, "System.String", "op_Equality"))
                throw new PatchException(action.FullName + ": the verified sites moved:\n" + Dump(action));
            var getWorld = (MethodReference)worldCall.Operand;
            var stringEquals = (MethodReference)equalsCall.Operand;

            MethodReference buttons = ImportServerMethod(module, context, HooksType, "AccountMenuButtons", ButtonList, ButtonList);
            MethodReference openPage = ImportServerMethod(module, context, HooksType, "OpenPage", "System.Void", "System.String", "System.Object");

            // ---- change: A1
            ILProcessor il = list.Body.GetILProcessor();
            il.InsertBefore(ret, il.Create(OpCodes.Call, buttons));
            context.Notes.Add(list.FullName + ": returns " + HooksType + ".AccountMenuButtons(list) (the 'MCVS Offline' button after " +
                              "\"Recover progress\")");

            // ---- change: A2 (brfalse.s jumps 17 bytes; the shipped body's own short branches keep their distances)
            il = action.Body.GetILProcessor();
            Instruction first = actionCode[0];
            var prefix = new[]
            {
                il.Create(OpCodes.Ldarg_1),
                il.Create(OpCodes.Ldstr, ButtonAction),
                il.Create(OpCodes.Call, stringEquals),
                il.Create(OpCodes.Brfalse_S, first),
                il.Create(OpCodes.Ldstr, Trigger),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Call, getWorld),
                il.Create(OpCodes.Call, openPage),
                il.Create(OpCodes.Ret),
            };
            foreach (Instruction instruction in prefix) il.InsertBefore(first, instruction);
            context.Notes.Add(action.FullName + ": action \"" + ButtonAction + "\" -> " + HooksType + ".OpenPage(\"" + Trigger + "\", World) and " +
                              "return; every other action runs the original body");
        }
    }
}
