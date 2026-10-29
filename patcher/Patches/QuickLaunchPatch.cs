using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// PC patch <c>quicklaunch</c> (docs/design/pc-port.md §8.3), Gameloft.Engine.dll. The Steam engine lost the client plumbing
    /// that routes an Embedded link to the game's own LocalAnubis (Lobby, Controller, Finder): nothing starts it, the client's
    /// quick launch goes to the online matchmaker (our Federation answers 404 by design: the error popup), and every room would
    /// advertise port 0. Three sites, each checked against the fingerprint of build 6372049 first:
    /// <list type="bullet">
    /// <item>P1 <c>LocalAnubis.BaseServer.get_Port()</c> (<c>return 0;</c>) gets Android's body back, verbatim:
    /// <c>return (Listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0</c> as the compiler wrote it (castclass); the result is
    /// checked to have Android's fingerprint.</item>
    /// <item>P2 <c>ClientComponent.QuickLaunch(ClientLink, CancellationToken)</c> returns
    /// <c>McvsQuickLaunch.QuickLaunch(this, link, ct)</c>: <c>McvsOffline.PcHooks.QuickLaunch</c> starts LocalAnubis and asks its
    /// Finder for the room; when the hook cannot be called the stock path runs (<c>link.QuickLaunch(ct)</c>, which is what the
    /// stock async body awaits).</item>
    /// <item>P3 <c>MatchmakingComponent.AddAutomatchHostSettings</c> first calls <c>McvsQuickLaunch.DefaultAutomatchHost(settings)</c>
    /// (<c>PcHooks.DefaultAutomatchHost</c>: the local Finder as the automatch host when nothing else is set); the stock body still
    /// overwrites Host/Port when the request carries both.</item>
    /// </list>
    /// <code>
    /// internal static abstract sealed class McvsQuickLaunch      // the witness (§3.9)
    /// {
    ///     internal static Task QuickLaunch(ClientComponent c, ClientLink l, CancellationToken ct)
    ///     { Task t; try { t = CallQuickLaunch(c, l, ct); } catch { t = l.QuickLaunch(ct); } return t; }
    ///     [NoInlining] static Task CallQuickLaunch(...) =&gt; McvsOffline.PcHooks.QuickLaunch(c, l, ct);
    ///     internal static void DefaultAutomatchHost(object settings) { try { CallDefault(settings); } catch { } }   // AutomatchSettings is protected
    ///     [NoInlining] static void CallDefault(...) =&gt; McvsOffline.PcHooks.DefaultAutomatchHost(s);
    /// }
    /// </code>
    /// </summary>
    public sealed class QuickLaunchPatch : IlPatch
    {
        /// <summary>The helper type in namespace Gameloft.Engine (internal, abstract sealed); its presence is the witness (§3.9).</summary>
        public const string HelperType = "McvsQuickLaunch";

        public const string HelperNamespace = "Gameloft.Engine";

        public const string HooksType = "McvsOffline.PcHooks";

        public const string ServerType = "Gameloft.Engine.LocalAnubis.BaseServer";
        public const string ClientType = "Gameloft.Engine.ClientComponent";
        public const string LinkType = "Gameloft.Engine.ClientLink";
        public const string MatchmakingType = "Gameloft.Engine.MatchmakingComponent";

        /// <summary>P1 on PC: <c>ldc.i4.0; ret</c>.</summary>
        public const string PcPortFingerprint = "20ed09f81f4535a206c3139d229bad3202bf726b4c4e769190598ea519553f85";

        /// <summary>P1 on Android 1.17.32, and P1 on PC after the patch (16 instructions).</summary>
        public const string AndroidPortFingerprint = "c180c05cbf2de69be2b562e5e296a47e2f1213e32f42ea62421f57f34948da80";

        /// <summary>P2: the async kick-off of <c>&lt;QuickLaunch&gt;d__124</c> (22 instructions, 2 locals; identical on Android).</summary>
        public const string PcQuickLaunchFingerprint = "cd9c1ab7b3985ad4a508d0c72fa0789f71ca9ef3aaba94b3a9a1ff5bba85a1e2";

        /// <summary>P3: host/port from the request, set only when both are present (26 instructions, 3 locals; identical on Android).</summary>
        public const string PcAutomatchHostFingerprint = "47da5614b4727db6e26447e562bbf5d0b113fa493d2f547707f81b217030df65";

        public override string Id => "quicklaunch";

        public override string AssemblyFile => "Gameloft.Engine.dll";

        public override string Description => "quick launch through the game's own LocalAnubis (Lobby, Controller, Finder) instead of the online matchmaker (Gameloft.Engine.dll, PC)";

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            // ---- locate and verify
            string portFingerprint = ForBuild(context, null, PcPortFingerprint);
            string quickLaunchFingerprint = ForBuild(context, null, PcQuickLaunchFingerprint);
            string automatchFingerprint = ForBuild(context, null, PcAutomatchHostFingerprint);
            PatchSites.RequireBuildAssembly(module, context, Id);
            if (module.GetType(HelperNamespace + "." + HelperType) != null)
                throw new PatchException("Gameloft.Engine.dll is already patched (" + HelperNamespace + "." + HelperType + "): install from the game's own file");

            TypeDefinition server = RequireType(module, ServerType);
            MethodDefinition getPort = RequireMethod(server, "get_Port",
                m => !m.IsStatic && !m.HasParameters && m.ReturnType.MetadataType == MetadataType.Int32, "instance int get_Port()");
            RequireFingerprint(getPort, portFingerprint, "BaseServer.Port (P1)");
            MethodDefinition getListener = RequireMethod(server, "get_Listener",
                m => !m.IsStatic && !m.HasParameters && m.ReturnType.FullName == "System.Net.Sockets.TcpListener", "instance TcpListener get_Listener()");

            TypeDefinition client = RequireType(module, ClientType);
            TypeDefinition link = RequireType(module, LinkType);
            MethodDefinition quickLaunch = RequireMethod(client, "QuickLaunch",
                m => !m.IsStatic && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == LinkType &&
                     m.Parameters[1].ParameterType.FullName == "System.Threading.CancellationToken" &&
                     m.ReturnType.FullName == "System.Threading.Tasks.Task",
                "instance Task QuickLaunch(ClientLink, CancellationToken)");
            RequireFingerprint(quickLaunch, quickLaunchFingerprint, "ClientComponent.QuickLaunch (P2)");
            MethodDefinition linkQuickLaunch = RequireMethod(link, "QuickLaunch",
                m => !m.IsStatic && m.IsPublic && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.Threading.CancellationToken" &&
                     m.ReturnType.FullName == "System.Threading.Tasks.Task",
                "public instance Task QuickLaunch(CancellationToken)");

            TypeDefinition matchmaking = RequireType(module, MatchmakingType);
            TypeDefinition settings = matchmaking.NestedTypes.SingleOrDefault(t => t.Name == "AutomatchSettings");
            if (settings == null) throw new PatchException(MatchmakingType + ": nested type AutomatchSettings not found");
            MethodDefinition automatchHost = RequireMethod(matchmaking, "AddAutomatchHostSettings",
                m => !m.IsStatic && m.Parameters.Count == 4 && m.Parameters[0].ParameterType.FullName == settings.FullName &&
                     m.ReturnType.FullName == "System.Threading.Tasks.Task",
                "instance Task AddAutomatchHostSettings(AutomatchSettings, Session, JObject, CancellationToken)");
            RequireFingerprint(automatchHost, automatchFingerprint, "MatchmakingComponent.AddAutomatchHostSettings (P3)");
            Instruction automatchFirst = automatchHost.Body.Instructions[0];
            if (PatchSites.BranchesTo(automatchHost.Body, automatchFirst).Count != 0)
                throw new PatchException(automatchHost.FullName + ": IL_0000 is a branch target:\n" + Dump(automatchHost));

            TypeSystem ts = module.TypeSystem;
            TypeReference endPoint = SystemType(module, "System.Net", "EndPoint");
            TypeReference ipEndPoint = SystemType(module, "System.Net", "IPEndPoint");
            MethodReference localEndpoint = SystemMethod(module, "System.Net.Sockets.TcpListener", "get_LocalEndpoint", true, endPoint);
            MethodReference endpointPort = SystemMethod(module, "System.Net.IPEndPoint", "get_Port", true, ts.Int32);
            TypeReference cancellationToken = PatchSites.CorlibType(module, "System.Threading", "CancellationToken");
            TypeReference task = PatchSites.CorlibType(module, "System.Threading.Tasks", "Task");
            MethodReference hookQuickLaunch = ImportServerMethod(module, context, HooksType, "QuickLaunch", "System.Threading.Tasks.Task",
                                                                 "System.Object", "System.Object", "System.Threading.CancellationToken");
            MethodReference hookDefault = ImportServerMethod(module, context, HooksType, "DefaultAutomatchHost", "System.Void", "System.Object");

            // ---- change: P1, Android's body (opcodes as the Android compiler wrote them: call, castclass, short branches)
            {
                MethodBody body = getPort.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                ILProcessor il = body.GetILProcessor();
                Instruction getEndpoint = il.Create(OpCodes.Call, localEndpoint);
                Instruction cast = il.Create(OpCodes.Castclass, ipEndPoint);
                Instruction readPort = il.Create(OpCodes.Call, endpointPort);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, getListener);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue_S, getEndpoint);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Br_S, cast);
                il.Append(getEndpoint);
                il.Append(cast);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue_S, readPort);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ret);
                il.Append(readPort);
                il.Emit(OpCodes.Ret);
                PatchSites.ComputeOffsets(body);
                string restored = Fingerprint(getPort);
                if (restored != AndroidPortFingerprint)
                    throw new PatchException(getPort.FullName + ": the restored body is not Android's (fingerprint " + restored + "):\n" + Dump(getPort));
            }
            context.Notes.Add(getPort.FullName + ": Android's body (the listener's port, 0 without a listener) instead of 0 (P1)");

            // ---- the helper type
            TypeDefinition helper = PatchSites.AddHelperType(module, HelperNamespace, HelperType);
            MethodDefinition callQuickLaunch = PatchSites.Forwarder(helper, "CallQuickLaunch", task, hookQuickLaunch, client, link, cancellationToken);
            // The settings travel as object: AutomatchSettings is a protected nested type, which an unrelated type's signature
            // cannot name verifiably (ILVerify: "method is not visible"); the hook takes object anyway.
            MethodDefinition callDefault = PatchSites.Forwarder(helper, "CallDefault", ts.Void, hookDefault, ts.Object);

            // internal static Task QuickLaunch(ClientComponent c, ClientLink l, CancellationToken ct)
            // { Task t; try { t = CallQuickLaunch(c, l, ct); } catch { t = l.QuickLaunch(ct); } return t; }
            var helperQuickLaunch = new MethodDefinition("QuickLaunch", MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig, task);
            helperQuickLaunch.Parameters.Add(new ParameterDefinition("component", ParameterAttributes.None, client));
            helperQuickLaunch.Parameters.Add(new ParameterDefinition("link", ParameterAttributes.None, link));
            helperQuickLaunch.Parameters.Add(new ParameterDefinition("ct", ParameterAttributes.None, cancellationToken));
            var result = new VariableDefinition(task);
            helperQuickLaunch.Body.Variables.Add(result);
            helperQuickLaunch.Body.InitLocals = true;
            {
                ILProcessor il = helperQuickLaunch.Body.GetILProcessor();
                Instruction end = il.Create(OpCodes.Ldloc_0);
                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                il.Append(tryStart);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, callQuickLaunch);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Leave_S, end);
                Instruction handler = il.Create(OpCodes.Pop);
                il.Append(handler);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Callvirt, linkQuickLaunch);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Leave_S, end);
                il.Append(end);
                il.Emit(OpCodes.Ret);
                helperQuickLaunch.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = end, CatchType = ts.Object,
                });
            }
            helper.Methods.Add(helperQuickLaunch);
            MethodDefinition helperDefault = PatchSites.GuardedVoid(helper, "DefaultAutomatchHost", callDefault, ts.Object);
            helperDefault.Parameters[0].Name = "settings";

            // ---- change: P2, the kick-off becomes one call (no locals; the state-machine type stays, unused)
            {
                MethodBody body = quickLaunch.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                body.InitLocals = false;
                ILProcessor il = body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, helperQuickLaunch);
                il.Emit(OpCodes.Ret);
            }
            context.Notes.Add(quickLaunch.FullName + ": returns " + HooksType + ".QuickLaunch(this, link, ct) (LocalAnubis and its Finder for an " +
                              "Embedded link; link.QuickLaunch(ct) when the hook cannot be called) (P2)");

            // ---- change: P3, the prologue (IL_0000 is no branch target)
            {
                ILProcessor il = automatchHost.Body.GetILProcessor();
                il.InsertBefore(automatchFirst, il.Create(OpCodes.Ldarg_1));
                il.InsertBefore(automatchFirst, il.Create(OpCodes.Call, helperDefault));
            }
            context.Notes.Add(automatchHost.FullName + ": " + HooksType + ".DefaultAutomatchHost(settings) first (the local Finder as the automatch " +
                              "host when the settings name none); the request's host/port still win (P3)");
        }

        /// <summary>A type of the System assembly the module already references.</summary>
        private static TypeReference SystemType(ModuleDefinition module, string ns, string name)
        {
            AssemblyNameReference system = module.AssemblyReferences.FirstOrDefault(r => r.Name == "System");
            if (system == null) throw new PatchException(module.Name + " has no reference to System (needed for " + ns + "." + name + ")");
            return module.ImportReference(new TypeReference(ns, name, module, system));
        }
    }
}
