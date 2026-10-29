using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Optional 'lan' patch, verify-C6 P2 + P3 (P1 is <see cref="FinderPatch"/>, which 'lan' implies). Experimental
    /// (roadmap M3): it changes nothing for single-device play, where the advertised host stays "localhost".
    /// <para><b>P2</b>, host: the advertised LocalAnubis host is computed once when LocalAnubis starts and cached, instead of
    /// on every access from the live (and temporarily swapped) <c>GameApplication.Args</c> (verify-C6 §2.6):</para>
    /// <code>
    /// public static Task Start(CancellationToken ct)
    /// {
    ///     try { LanSupport.ResetPublicHost();
    ///           LanSupport.SetPublicHost(GameApplication.Instance.Args.GSRemoteIp, GetPublicHost()); }
    ///     catch { LanSupport.ResetPublicHost(); }
    ///     ...original
    /// }
    /// public static string GetPublicHost()
    /// {
    ///     string cached = LanSupport.GetCachedPublicHost(); if (cached != null) return cached;
    ///     ...original
    /// }
    /// </code>
    /// <para><b>P3</b>, guest: <c>ClientSettings</c> initialises <c>LocalAnubisFinderHost</c> with
    /// <c>LanSupport.ChooseFinderHost(GameApplication.Instance.Args.RemoteIp, LocalAnubisManager.PublicHost)</c>, so
    /// <c>-RemoteIp &lt;host&gt;</c> (declared by the game but read nowhere) points a guest at the host's Finder.</para>
    /// The helpers live in McvsOffline.Server.dll (<c>McvsOffline.LanSupport</c>), which therefore becomes a dependency of
    /// Gameloft.Engine.dll; the patcher always installs it.
    /// </summary>
    public sealed class LanPatch : IlPatch
    {
        public const string ManagerType = "Gameloft.Engine.LocalAnubisManager";
        public const string ApplicationType = "Gameloft.Engine.GameApplication";
        public const string ArgumentsType = "Gameloft.Engine.GameApplication/Arguments";
        public const string ClientSettingsType = "Gameloft.Engine.ClientSettings";
        public const string SupportType = "McvsOffline.LanSupport";

        public override string Id => "lan";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "LAN hosting: cached advertised host (P2) and -RemoteIp for guests (P3); experimental";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            ForBuild(context, Id, null);   // Android only: excluded on PC (docs/design/pc-port.md §8.8)
            PatchSites.RequireBuildCorlib(module, context, Id);
            // ---- locate and verify everything first
            TypeDefinition manager = RequireType(module, ManagerType);
            TypeDefinition application = RequireType(module, ApplicationType);
            TypeDefinition arguments = application.NestedTypes.SingleOrDefault(t => t.Name == "Arguments");
            if (arguments == null) throw new PatchException(ArgumentsType + " not found");
            FieldDefinition remoteIp = RequireField(arguments, "RemoteIp", "System.String");
            FieldDefinition gsRemoteIp = RequireField(arguments, "GSRemoteIp", "System.String");
            MethodDefinition getInstance = RequireMethod(application, "get_Instance", m => m.IsStatic && !m.HasParameters, "static getter");
            MethodDefinition getArgs = RequireMethod(application, "get_Args", m => !m.IsStatic && !m.HasParameters, "instance getter");
            if (getInstance.ReturnType.FullName != ApplicationType || getArgs.ReturnType.FullName != ArgumentsType)
                throw new PatchException("GameApplication.Instance/Args have unexpected types");

            MethodDefinition getPublicHost = RequireMethod(manager, "GetPublicHost",
                m => m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.String, "static string method");
            MethodDefinition publicHostGetter = RequireMethod(manager, "get_PublicHost",
                m => m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.String, "static string getter");
            MethodDefinition start = RequireMethod(manager, "Start",
                m => m.IsStatic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Threading.CancellationToken",
                "static Start(CancellationToken)");

            var hostCode = getPublicHost.Body.Instructions;
            if (hostCode.Count < 4 || !IsCall(hostCode[0], ManagerType, "get_Host") || !IsCall(hostCode[1], "System.Net.IPAddress", "IsLoopback") ||
                hostCode[3].OpCode != OpCodes.Ldstr || (string)hostCode[3].Operand != "localhost")
            {
                throw new PatchException(getPublicHost.FullName + " does not start like the verified 1.17.32 code " +
                                         "(call get_Host; call IPAddress::IsLoopback; brfalse; ldstr \"localhost\"):\n" + Dump(getPublicHost));
            }
            if (publicHostGetter.Body.Instructions.Count != 2 || !IsCall(publicHostGetter.Body.Instructions[0], ManagerType, "GetPublicHost"))
                throw new PatchException(publicHostGetter.FullName + " no longer just returns GetPublicHost():\n" + Dump(publicHostGetter));

            TypeDefinition clientSettings = RequireType(module, ClientSettingsType);
            MethodDefinition settingsCtor = RequireMethod(clientSettings, ".ctor", m => !m.IsStatic && !m.HasParameters, "parameterless constructor");
            var ctorCode = settingsCtor.Body.Instructions;
            var finderSites = Enumerable.Range(0, ctorCode.Count - 1)
                .Where(i => IsCall(ctorCode[i], ManagerType, "get_PublicHost") &&
                            ctorCode[i + 1].OpCode == OpCodes.Stfld && ctorCode[i + 1].Operand is FieldReference f &&
                            f.Name == "<LocalAnubisFinderHost>k__BackingField")
                .ToList();
            if (finderSites.Count != 1)
            {
                throw new PatchException(settingsCtor.FullName + ": expected one 'call get_PublicHost; stfld <LocalAnubisFinderHost>k__BackingField', found " +
                                         finderSites.Count);
            }

            MethodReference getCached = ImportServerMethod(module, context, SupportType, "GetCachedPublicHost", "System.String");
            MethodReference reset = ImportServerMethod(module, context, SupportType, "ResetPublicHost", "System.Void");
            MethodReference set = ImportServerMethod(module, context, SupportType, "SetPublicHost", "System.Void", "System.String", "System.String");
            MethodReference choose = ImportServerMethod(module, context, SupportType, "ChooseFinderHost", "System.String", "System.String", "System.String");

            // ---- P2a: GetPublicHost returns the cached value when there is one
            ILProcessor il = getPublicHost.Body.GetILProcessor();
            Instruction first = hostCode[0];
            Instruction popNull = il.Create(OpCodes.Pop);
            il.InsertBefore(first, il.Create(OpCodes.Call, getCached));
            il.InsertBefore(first, il.Create(OpCodes.Dup));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, popNull));
            il.InsertBefore(first, il.Create(OpCodes.Ret));
            il.InsertBefore(first, popNull);

            // ---- P2b: Start(ct) computes and caches it with the original arguments
            ILProcessor s = start.Body.GetILProcessor();
            PrependGuarded(start, new[]
            {
                s.Create(OpCodes.Call, reset),
                s.Create(OpCodes.Call, getInstance),
                s.Create(OpCodes.Callvirt, getArgs),
                s.Create(OpCodes.Ldfld, gsRemoteIp),
                s.Create(OpCodes.Call, getPublicHost),
                s.Create(OpCodes.Call, set),
            }, new[] { s.Create(OpCodes.Call, reset) });

            // ---- P3: ClientSettings.LocalAnubisFinderHost honours -RemoteIp
            ILProcessor c = settingsCtor.Body.GetILProcessor();
            Instruction call = ctorCode[finderSites[0]];
            c.InsertBefore(call, c.Create(OpCodes.Call, getInstance));
            c.InsertBefore(call, c.Create(OpCodes.Callvirt, getArgs));
            c.InsertBefore(call, c.Create(OpCodes.Ldfld, remoteIp));
            c.InsertAfter(call, c.Create(OpCodes.Call, choose));

            context.Notes.Add(getPublicHost.FullName + ": returns McvsOffline.LanSupport.GetCachedPublicHost() when set (P2)");
            context.Notes.Add(start.FullName + ": caches the advertised host at LocalAnubis start (P2)");
            context.Notes.Add(settingsCtor.FullName + ": LocalAnubisFinderHost = ChooseFinderHost(Args.RemoteIp, PublicHost) (P3)");
        }
    }
}
