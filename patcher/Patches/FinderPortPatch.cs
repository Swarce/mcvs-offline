using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Mandatory 'finderport' patch of Gameloft.Engine.dll (Android; build 26, docs/design/port-fallback.md). The game's own
    /// LocalAnubis Finder listens on <c>Args.LocalAnubisFinderPort ?? 51750</c>, but the client side of it,
    /// <c>ClientSettings.LocalAnubisFinderPort</c> (QuickLaunch, FindRooms, locate), is initialised with a hard-coded 51750 and
    /// never read from the arguments. When 51750 is busy at start (a frozen earlier copy of the game holds it), the host moves
    /// the Finder with <c>-LocalAnubisFinderPort</c>; the client must follow it:
    /// <code>
    /// ClientSettings::.ctor  IL_00f7 ldc.i4 51750
    ///                               call int32 McvsOffline.PortHooks::LocalAnubisFinderPort(int32)   // added
    ///                        IL_00fc stfld int32 ClientSettings::'&lt;LocalAnubisFinderPort&gt;k__BackingField'
    /// </code>
    /// The hook returns 51750 unless the host chose another port, so nothing changes when the port is free. Mandatory, so the
    /// host can rely on it whenever it moves the Finder. Located by its exact pattern (one site; the 'lan' patch edits the
    /// neighbouring LocalAnubisFinderHost site of the same constructor and is applied after this one). Not on PC: the Steam
    /// build's ClientSettings has no such property, and PcHooks already use the Finder's PublicPort (docs/design/pc-port.md).
    /// </summary>
    public sealed class FinderPortPatch : IlPatch
    {
        public const string ClientSettingsType = "Gameloft.Engine.ClientSettings";
        public const string BackingField = "<LocalAnubisFinderPort>k__BackingField";
        public const int DefaultFinderPort = 51750;

        public override string Id => "finderport";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "the client follows the LocalAnubis Finder port the host chose (-LocalAnubisFinderPort; port fallback)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            ForBuild(context, Id, null);   // Android only
            PatchSites.RequireBuildAssembly(module, context, Id);
            TypeDefinition settings = RequireType(module, ClientSettingsType);
            RequireField(settings, BackingField, "System.Int32");
            MethodDefinition ctor = RequireMethod(settings, ".ctor", m => !m.IsStatic && !m.HasParameters, "parameterless constructor");
            var code = ctor.Body.Instructions;
            var sites = Enumerable.Range(0, code.Count - 1)
                .Where(i => code[i].OpCode == OpCodes.Ldc_I4 && code[i].Operand is int value && value == DefaultFinderPort &&
                            code[i + 1].OpCode == OpCodes.Stfld && code[i + 1].Operand is FieldReference f && f.Name == BackingField &&
                            f.DeclaringType.FullName == ClientSettingsType)
                .ToList();
            if (sites.Count != 1)
            {
                throw new PatchException(ctor.FullName + ": expected one 'ldc.i4 51750; stfld " + BackingField + "', found " + sites.Count + ":\n" + Dump(ctor));
            }

            MethodReference hook = ImportServerMethod(module, context, GeoPatch.HooksType, "LocalAnubisFinderPort", "System.Int32", "System.Int32");
            ILProcessor il = ctor.Body.GetILProcessor();
            il.InsertAfter(code[sites[0]], il.Create(OpCodes.Call, hook));
            context.Notes.Add(ctor.FullName + ": LocalAnubisFinderPort = " + GeoPatch.HooksType + ".LocalAnubisFinderPort(51750) (the port the host chose)");
        }
    }
}
