using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Patcher.Core;
using Mono.Cecil;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>One public static hook of McvsOffline.Server.dll a patch imports (docs/design/pc-port.md §3.2).</summary>
    public sealed class ServerHook
    {
        public ServerHook(string type, string method, string returnType, params string[] parameters)
        {
            Type = type;
            Method = method;
            ReturnType = returnType;
            Parameters = parameters;
        }

        public string Type { get; }

        public string Method { get; }

        public string ReturnType { get; }

        public string[] Parameters { get; }

        public override string ToString()
        {
            return "public static " + ReturnType + " " + Type + "::" + Method + "(" + string.Join(", ", Parameters) + ")";
        }
    }

    /// <summary>
    /// What the PC patches need from the server DLL they are installed with: every hook of §3.2 with the exact signature
    /// <c>IlPatch.ImportServerMethod</c> looks up, the PC reference rules (<see cref="ServerAssemblyCheck"/>), and the
    /// Gameloft.Logs reference of the game-log bridge (a DLL built without MCVS_GAMELOG lacks it). The kit's publish target runs
    /// <see cref="Check"/> on the exact file it embeds (§9.1); <c>PublishCheckTests</c> runs it in the tests.
    /// </summary>
    public static class ServerHookContract
    {
        public static readonly IReadOnlyList<ServerHook> Hooks = new[]
        {
            new ServerHook("McvsOffline.Bootstrap", "Init", "System.Void"),
            new ServerHook("McvsOffline.GameHooks", "HeadlessComponentException", "System.Void", "System.String", "System.String", "System.Exception"),
            new ServerHook("McvsOffline.GameHooks", "PhysicsMultithreading", "System.Boolean", "System.Boolean", "System.Boolean"),
            new ServerHook("McvsOffline.GameHooks", "EnterPhysicsStep", "System.Boolean"),
            new ServerHook("McvsOffline.GameHooks", "ExitPhysicsStep", "System.Void"),
            new ServerHook("McvsOffline.GameHooks", "AllowNativeOnlineInit", "System.Boolean", "System.String"),
            new ServerHook("McvsOffline.GameHooks", "StoreItemException", "System.Void", "System.Object", "System.Exception"),
            new ServerHook("McvsOffline.GameHooks", "InboxRewardException", "System.Void", "System.Object", "System.Exception"),
            new ServerHook("McvsOffline.AdminHooks", "AccountMenuButtons", "System.Collections.Generic.IEnumerable`1<System.Object>",
                           "System.Collections.Generic.IEnumerable`1<System.Object>"),
            new ServerHook("McvsOffline.AdminHooks", "OpenPage", "System.Void", "System.String", "System.Object"),
            new ServerHook("McvsOffline.AdminHooks", "OpenIfPageMarker", "System.Boolean", "System.String", "System.Object"),
            new ServerHook("McvsOffline.GraphicsHooks", "GameOptionsJson", "System.String", "System.String"),
            new ServerHook("McvsOffline.GraphicsHooks", "GameOptionsApplied", "System.Void", "System.Object"),
            new ServerHook("McvsOffline.PcHooks", "QuickLaunch", "System.Threading.Tasks.Task", "System.Object", "System.Object", "System.Threading.CancellationToken"),
            new ServerHook("McvsOffline.PcHooks", "DefaultAutomatchHost", "System.Void", "System.Object"),
            new ServerHook("McvsOffline.PcHooks", "ServerWorldFailed", "System.Void", "System.Exception"),
            new ServerHook("McvsOffline.PcHooks", "ComponentCreateFailed", "System.Void", "System.Exception", "System.Type"),
        };

        /// <summary>Every problem of <paramref name="dll"/> for the PC kit; empty when it may be published.</summary>
        public static List<string> Check(byte[] dll, bool requireGameLog = true)
        {
            var problems = new List<string>();
            try
            {
                ServerAssemblyCheck.Check(dll, "McvsOffline.Server.dll", BuildProfile.PcSteam6372049);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is BadImageFormatException)
            {
                problems.Add(ex.Message);
                return problems;
            }
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(dll), new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
            {
                if (requireGameLog && !module.AssemblyReferences.Any(r => r.Name == "Gameloft.Logs"))
                    problems.Add("McvsOffline.Server.dll does not reference Gameloft.Logs: it was built without MCVS_GAMELOG (MCVS_PC_REFS lacks Gameloft.Logs.dll)");
                if (module.Assembly.Name.Version != new Version(0, 0, 0, 0))
                    problems.Add("McvsOffline.Server.dll is version " + module.Assembly.Name.Version + "; the patched assemblies reference 0.0.0.0");
                foreach (ServerHook hook in Hooks)
                {
                    TypeDefinition type = module.GetType(hook.Type);
                    bool found = type != null && type.Methods.Any(m =>
                        m.Name == hook.Method && m.IsStatic && m.IsPublic && type.IsPublic && m.ReturnType.FullName == hook.ReturnType &&
                        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(hook.Parameters));
                    if (!found) problems.Add("missing hook " + hook);
                }
            }
            return problems;
        }
    }

    /// <summary>The test assembly's entry point, for the kit's publish target: <c>check-server &lt;dll&gt;</c>.</summary>
    public static class PublishCheck
    {
        public static int Main(string[] args)
        {
            if (args.Length != 2 || args[0] != "check-server")
            {
                Console.Error.WriteLine("usage: McvsOffline.PcKit.Tests check-server <McvsOffline.Server.dll>   (the tests run with dotnet test)");
                return 2;
            }
            byte[] dll;
            try
            {
                dll = File.ReadAllBytes(args[1]);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
            List<string> problems = ServerHookContract.Check(dll);
            foreach (string reference in ServerAssemblyCheck.References(dll)) Console.WriteLine("  references " + reference);
            if (problems.Count == 0)
            {
                Console.WriteLine("check-server: " + args[1] + " has every PC hook (" + ServerHookContract.Hooks.Count + "), Gameloft.Logs and only allowed references");
                return 0;
            }
            foreach (string p in problems) Console.Error.WriteLine("error: " + p);
            return 1;
        }
    }
}
