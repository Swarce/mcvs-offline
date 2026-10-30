using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>gameload</c> (docs/design/pc-port.md §8.9, WR2 F1), Gameloft.Engine.dll. The engine loads the game's own
    /// assemblies (<c>Config/Assemblies.mjson</c>: <c>Game</c>, and <c>NicePlaySDK</c>, which the PC build does not have) in
    /// <c>ComponentAssemblies.GetAssembly(string fullPath, string name)</c> with <c>Assembly.Load(new AssemblyName(name))</c>, a
    /// bare name. A partial-name load gets no application-config policy: the <c>&lt;codeBase&gt;</c> of
    /// <c>ModernCombatVersus.exe.config</c> is not consulted and the stock <c>Game.dll</c> next to the exe is loaded (WR2: 15 of 15
    /// starts). Every other redirected assembly is bound by a full static reference (EngineBridge from the exe,
    /// Gameloft.Engine from EngineBridge, Gameloft.Util from Gameloft.Engine, McvsOffline.Server from the patched copies) and
    /// came from McvsOffline\bin. One site, checked against the fingerprint of build 6372049 first:
    /// <code>
    /// IL_0027: ldarg.1                       // name
    ///          call string McvsGameLoad::Qualify(string)    // inserted: "Game" -> "Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"
    /// IL_0028: newobj AssemblyName::.ctor(string)
    /// IL_002d: call Assembly::Load(AssemblyName)
    /// </code>
    /// A full display name is a full reference, which should get the codeBase (see below). The cache key
    /// (<c>cachedAssemblies[name]</c>) and the <c>Loading</c>/<c>Loaded</c> events keep the bare name. <c>Gameloft.Engine.McvsGameLoad</c> (internal static, no server
    /// dependency; its presence is the witness, §3.9):
    /// <code>
    /// internal static string Qualify(string name) { if (name == "Game") return "Game, Version=0.0.0.0, …"; return name; }
    /// </code>
    /// The table is <see cref="QualifiedNames"/>: the redirected assemblies that Assemblies.mjson names. Each full name is
    /// checked against the identity of the file in the game folder before anything changes (the patched copy keeps it). This
    /// patch alone is meant to carry the fix: the full name carries <c>PublicKeyToken=null</c>, so fusion should take it as a
    /// full reference (not partial) and apply the config's codeBase, as for the static references. Expected, not proven: the
    /// reasoning rests on the SSCLI sources (the display-name parser and IsPartial), and the managed AssemblyName reaches
    /// fusion through the VM's AssemblySpec conversion, which those sources do not show; gameload has never run with a config
    /// in effect (WR2 was build 13 without it; build 14's config was most likely ignored). Unconfirmed on Windows until WR3
    /// step 4 shows Game loaded from McvsOffline\bin (§8.9). Builds 14 and 15 also wrote <c>&lt;qualifyAssembly&gt;</c>
    /// entries into the config; those most likely made .NET ignore the whole file (SSCLI evidence; WR3 confirms; pc-port.md
    /// §7.4), and the config no longer has them.
    /// </summary>
    public sealed class GameAssemblyLoadPatch : IlPatch
    {
        /// <summary>The helper type in namespace Gameloft.Engine (internal, abstract sealed); its presence is the witness (§3.9).</summary>
        public const string HelperType = "McvsGameLoad";

        public const string HelperNamespace = "Gameloft.Engine";

        public const string QualifyMethod = "Qualify";

        public const string AssembliesType = "Gameloft.Engine.ComponentAssemblies";

        /// <summary><c>ComponentAssemblies.GetAssembly(string, string)</c> on PC (45 instructions, 1 local, one catch (object)).</summary>
        public const string PcGetAssemblyFingerprint = "b915f891ed04967504e8f835388ebdf926ff5b2ecdc97717b754dc5a4a14ca91";

        /// <summary>The offset of the <c>ldarg.1</c> that feeds <c>new AssemblyName(name)</c> (the first instruction of the try).</summary>
        public const int NameLoadOffset = 0x27;

        /// <summary>
        /// Bare name → full display name, for every redirected assembly the engine loads by bare name (build 6372049:
        /// Assemblies.mjson lists <c>Game</c> in gameDlls and <c>NicePlaySDK</c> in managedDlls; only Game is ours). The version
        /// is the file's (Game.dll 0.0.0.0); assemblies of the game are not strong-named.
        /// </summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> QualifiedNames = new[]
        {
            new KeyValuePair<string, string>("Game", "Game, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"),
        };

        public override string Id => "gameload";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "the engine loads Game by its full name, so the .exe.config redirect to McvsOffline\\bin applies (Gameloft.Engine.dll, PC)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            string fingerprint = ForBuild(context, null, PcGetAssemblyFingerprint);
            PatchSites.RequireBuildAssembly(module, context, Id);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched (" + HelperNamespace + "." + HelperType + "): install from the game's own file");

            TypeDefinition assemblies = RequireType(module, AssembliesType);
            MethodDefinition getAssembly = RequireMethod(assemblies, "GetAssembly",
                m => m.IsStatic && m.Parameters.Count == 2 && m.Parameters.All(p => p.ParameterType.MetadataType == MetadataType.String) &&
                     m.ReturnType.FullName == "System.Reflection.Assembly",
                "static Assembly GetAssembly(string, string)");
            RequireFingerprint(getAssembly, fingerprint, "ComponentAssemblies.GetAssembly");
            Instruction nameLoad = PatchSites.At(getAssembly, NameLoadOffset);
            Instruction construct = nameLoad.Next;
            if (nameLoad.OpCode != OpCodes.Ldarg_1 || construct == null || construct.OpCode != OpCodes.Newobj ||
                ((MethodReference)construct.Operand).FullName != "System.Void System.Reflection.AssemblyName::.ctor(System.String)" ||
                construct.Next == null || !IsCall(construct.Next, "System.Reflection.Assembly", "Load"))
                throw new PatchException(getAssembly.FullName + ": IL_0027 is not ldarg.1; newobj AssemblyName(string); call Assembly.Load:\n" + Dump(getAssembly));
            foreach (KeyValuePair<string, string> name in QualifiedNames) RequireIdentity(module, name.Key, name.Value);

            MethodReference equality = CorlibMethod(module, module.TypeSystem.String, "op_Equality", false, module.TypeSystem.Boolean,
                                                    module.TypeSystem.String, module.TypeSystem.String);

            // ---- the helper type: internal static string Qualify(string name)
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            var qualify = new MethodDefinition(QualifyMethod, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                                               module.TypeSystem.String);
            qualify.Parameters.Add(new ParameterDefinition("name", ParameterAttributes.None, module.TypeSystem.String));
            {
                ILProcessor il = qualify.Body.GetILProcessor();
                Instruction asIs = il.Create(OpCodes.Ldarg_0);
                Instruction next = null;
                for (int i = 0; i < QualifiedNames.Count; i++)
                {
                    next = i + 1 < QualifiedNames.Count ? il.Create(OpCodes.Ldarg_0) : asIs;
                    if (i == 0) il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldstr, QualifiedNames[i].Key);
                    il.Emit(OpCodes.Call, equality);
                    il.Emit(OpCodes.Brfalse_S, next);
                    il.Emit(OpCodes.Ldstr, QualifiedNames[i].Value);
                    il.Emit(OpCodes.Ret);
                    if (next != asIs) il.Append(next);
                }
                il.Append(asIs);
                il.Emit(OpCodes.Ret);
            }
            helper.Methods.Add(qualify);

            // ---- change: the bare name passes through Qualify before new AssemblyName(...) (inside the try, no branch targets it)
            getAssembly.Body.GetILProcessor().InsertAfter(nameLoad, Instruction.Create(OpCodes.Call, qualify));
            PatchSites.FixShortBranches(getAssembly);
            context.Notes.Add(getAssembly.FullName + ": Assembly.Load(new AssemblyName(" + HelperType + "." + QualifyMethod + "(name))): " +
                              string.Join(", ", QualifiedNames.Select(n => n.Key + " -> \"" + n.Value + "\"")) +
                              " (a full reference, so the .exe.config codeBase should apply: expected, WR3 step 4 confirms)");
        }

        /// <summary>
        /// The game folder's <paramref name="name"/>.dll (through the module's resolver: the PC installer's folders) has exactly the
        /// identity <paramref name="fullName"/>; else <see cref="PatchException"/> before anything changes.
        /// </summary>
        private static void RequireIdentity(ModuleDefinition module, string name, string fullName)
        {
            AssemblyDefinition resolved;
            try
            {
                resolved = module.AssemblyResolver.Resolve(AssemblyNameReference.Parse(fullName));
            }
            catch (AssemblyResolutionException ex)
            {
                throw new PatchException("gameload: cannot read " + name + ".dll to check its identity (" + ex.Message + ")");
            }
            if (resolved == null) throw new PatchException("gameload: " + name + ".dll was not found to check its identity");
            if (resolved.Name.FullName != fullName)
                throw new PatchException("gameload: " + name + ".dll is \"" + resolved.Name.FullName + "\", expected \"" + fullName + "\"");
        }
    }
}
