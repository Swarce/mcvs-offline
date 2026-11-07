using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Optional 'finder' patch, verify-C6 P1: the LocalAnubis Finder's HttpListener prefix
    /// <c>http://localhost:51750/</c> becomes <c>http://*:51750/</c>.
    /// <code>
    /// Finder::.ctor  IL_0008 ldarg.3; IL_0009 ldstr "localhost"; IL_000e ldstr "http"; IL_0013 call HTTPServer::.ctor(handler, int, string, string)
    /// </code>
    /// On Android Mono resolves "localhost" for the listener to the first non-loopback IPv4 when the Finder starts,
    /// so a Wi-Fi change or airplane-mode toggle leaves it on a dead address and matchmaking fails until the game
    /// restarts (verify-C6 §2.2); "*" binds every interface and accepts any Host header. Required for LAN hosting
    /// (remote guests otherwise get "400 Invalid host").
    /// </summary>
    public sealed class FinderPatch : IlPatch
    {
        public const string TypeName = "Gameloft.Engine.LocalAnubis.Finder";

        public override string Id => "finder";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "LocalAnubis Finder listens on all interfaces (survives network changes; needed for LAN)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            ForBuild(context, Id, null);   // Android only: excluded on PC (docs/design/pc-port.md §8.8)
            PatchSites.RequireBuildAssembly(module, context, Id);
            TypeDefinition type = RequireType(module, TypeName);
            MethodDefinition ctor = RequireMethod(type, ".ctor", m => m.Parameters.Count == 3 && m.Parameters[2].ParameterType.MetadataType == MetadataType.Int32,
                                                  "constructor (Session, Lobby, int)");
            var code = ctor.Body.Instructions;
            var sites = Enumerable.Range(0, code.Count - 2)
                .Where(i => code[i].OpCode == OpCodes.Ldstr && (string)code[i].Operand == "localhost" &&
                            code[i + 1].OpCode == OpCodes.Ldstr && (string)code[i + 1].Operand == "http" &&
                            code[i + 2].OpCode == OpCodes.Call && code[i + 2].Operand is MethodReference m &&
                            m.Name == ".ctor" && m.DeclaringType.FullName == "Gameloft.Engine.HTTPServer" && m.Parameters.Count == 4)
                .ToList();
            if (sites.Count != 1)
            {
                throw new PatchException(ctor.FullName + ": expected one 'ldstr \"localhost\"; ldstr \"http\"; call HTTPServer::.ctor', found " +
                                         sites.Count + ":\n" + Dump(ctor));
            }
            code[sites[0]].Operand = "*";
            context.Notes.Add(ctor.FullName + ": HttpListener prefix host \"localhost\" -> \"*\"");
        }
    }
}
