using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Optional 'bottile' patch (verify-C4 §6): the hidden main-menu tile SOLO ("PRACTICE", MCVS_Meta MainMenuElements
    /// row 13, Displayed=false; Flash LINK_SOLO -> PopupSolo_Bot_Match -> Native.StartMatchWithBots) runs
    /// <code>
    /// private void NativeStartMatchWithBots()
    /// {
    ///     applyUniqueTask.RunTask(ct => StartAutoMatchAsync(CancellationToken, MatchConnectionType.OfflineMatchWithBots), ...);
    /// }
    /// // lambda &lt;NativeStartMatchWithBots&gt;b__354_0:
    /// //   IL_0000 ldarg.0; IL_0001 ldarg.0; IL_0002 call Component::get_CancellationToken()
    /// //   IL_0007 ldc.i4.2   (OfflineMatchWithBots)
    /// //   IL_0008 call GameClientComponent::StartAutoMatchAsync(CancellationToken, MatchConnectionType); IL_000d ret
    /// </code>
    /// Free bot matches pay only the first BotMatchesInfo row. With <c>ldc.i4.1</c> (OnlineAndTutorial) the tile
    /// behaves like "Battle": onboarding before PvP activation, then bot-filled PvP on the in-process GS with full
    /// progression. The lambda is found through the <c>ldftn</c> in NativeStartMatchWithBots (not by its generated
    /// name), and every instruction and the enum values are checked before the one byte (0x18 -> 0x17) changes.
    /// Dormant with the shipped data: no visible UI reaches StartMatchWithBots unless the CRM shows the tile (server
    /// setting crm.practiceTile); the full-reward bot match is Battle > Classic multiplayer, which needs no patch. The
    /// developer cheat PlayAgainstBots uses another, unpatched lambda (StartAutoMatchSameAsPlayerLevel).
    /// </summary>
    public sealed class BotTilePatch : IlPatch
    {
        public const string TypeName = "Game.GameClientComponent";
        public const string EnumName = "Game.GameClientComponent/MatchConnectionType";

        public override string Id => "bottile";

        public override string AssemblyFile => "Game.dll";

        public override string Description => "PRACTICE tile (hidden by default) -> bot-filled PvP on the in-process GS (full progression)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            PatchSites.RequireBuildAssembly(module, context, Id);
            TypeDefinition type = RequireType(module, TypeName);
            CheckEnum(type);

            MethodDefinition native = RequireMethod(type, "NativeStartMatchWithBots", m => !m.IsStatic && !m.HasParameters,
                                                    "instance method without parameters");
            var lambdas = native.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldftn)
                .Select(i => i.Operand as MethodReference)
                .Where(r => r != null && r.DeclaringType.FullName == TypeName && r.Name.StartsWith("<NativeStartMatchWithBots>"))
                .ToList();
            if (lambdas.Count != 1)
                throw new PatchException(native.FullName + ": expected one ldftn of its lambda, found " + lambdas.Count + ":\n" + Dump(native));
            MethodDefinition lambda = lambdas[0].Resolve();
            if (lambda == null || !lambda.HasBody) throw new PatchException("cannot resolve " + lambdas[0].FullName);

            var code = lambda.Body.Instructions;
            bool shape = code.Count == 6 &&
                         code[0].OpCode == OpCodes.Ldarg_0 &&
                         code[1].OpCode == OpCodes.Ldarg_0 &&
                         IsCall(code[2], "Gameloft.Engine.Component", "get_CancellationToken") &&
                         code[3].OpCode == OpCodes.Ldc_I4_2 && code[3].Offset == 7 &&
                         IsCall(code[4], TypeName, "StartAutoMatchAsync") &&
                         ((MethodReference)code[4].Operand).Parameters.Count == 2 &&
                         ((MethodReference)code[4].Operand).Parameters[1].ParameterType.FullName == EnumName &&
                         code[5].OpCode == OpCodes.Ret;
            if (!shape)
            {
                throw new PatchException(lambda.FullName + " does not match the verified 1.17.32 code " +
                                         "(ldarg.0; ldarg.0; call get_CancellationToken; IL_0007 ldc.i4.2; call StartAutoMatchAsync; ret):\n" +
                                         Dump(lambda));
            }
            code[3].OpCode = OpCodes.Ldc_I4_1;
            context.Notes.Add(lambda.FullName + ": IL_0007 ldc.i4.2 (OfflineMatchWithBots) -> ldc.i4.1 (OnlineAndTutorial)");
        }

        private static void CheckEnum(TypeDefinition type)
        {
            TypeDefinition e = type.NestedTypes.SingleOrDefault(t => t.Name == "MatchConnectionType" && t.IsEnum);
            if (e == null) throw new PatchException(EnumName + " not found");
            object Value(string name) => e.Fields.SingleOrDefault(f => f.Name == name && f.HasConstant)?.Constant;
            if (!Equals(Value("OnlineAndTutorial"), 1) || !Equals(Value("OfflineMatchWithBots"), 2))
                throw new PatchException(EnumName + " values differ from the verified build (OnlineAndTutorial = 1, OfflineMatchWithBots = 2)");
        }
    }
}
