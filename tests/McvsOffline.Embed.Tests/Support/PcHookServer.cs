using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>
    /// The net452 stand-in for the PC McvsOffline.Server.dll (docs/design/pc-port.md §3.4): every hook of §3.2 with the exact
    /// signature the PC patches import, compiled against <see cref="TestEnvironment.Net452Refs"/> (mscorlib 4.0.0.0, so the
    /// patched PC assemblies keep one corlib, P3) as version 0.0.0.0. Each hook appends "Type.Method" to <c>McvsOffline.HookLog.Calls</c>
    /// and its arguments to <c>HookLog.Arguments</c>; the answers are public static fields. One owner (WP3); a §3.2 change goes
    /// through the integrator and updates it.
    /// </summary>
    public static class PcHookServer
    {
        public const string Source = @"
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

[assembly: System.Reflection.AssemblyVersion(""0.0.0.0"")]

namespace McvsOffline
{
    /// <summary>Every hook call, in order (thread-safe).</summary>
    public static class HookLog
    {
        public static readonly List<string> Calls = new List<string>();
        public static readonly List<object[]> Arguments = new List<object[]>();

        public static void Record(string hook, params object[] arguments)
        {
            lock (Calls)
            {
                Calls.Add(hook);
                Arguments.Add(arguments);
            }
        }

        public static void Clear()
        {
            lock (Calls)
            {
                Calls.Clear();
                Arguments.Clear();
            }
        }
    }

    public static class Bootstrap
    {
        public static bool Throws;

        public static void Init()
        {
            HookLog.Record(""Bootstrap.Init"");
            if (Throws) throw new InvalidOperationException(""Init failed"");
        }
    }

    public static class GameHooks
    {
        public static bool AllowNativeOnline;
        public static bool PhysicsMultithreadingAnswer;
        public static bool EnterPhysicsStepAnswer = true;

        public static void HeadlessComponentException(string component, string eventName, Exception exception)
        {
            HookLog.Record(""GameHooks.HeadlessComponentException"", component, eventName, exception);
        }

        public static bool PhysicsMultithreading(bool headless, bool requested)
        {
            HookLog.Record(""GameHooks.PhysicsMultithreading"", headless, requested);
            return PhysicsMultithreadingAnswer;
        }

        public static bool EnterPhysicsStep()
        {
            HookLog.Record(""GameHooks.EnterPhysicsStep"");
            return EnterPhysicsStepAnswer;
        }

        public static void ExitPhysicsStep()
        {
            HookLog.Record(""GameHooks.ExitPhysicsStep"");
        }

        public static bool AllowNativeOnlineInit(string datacenter)
        {
            HookLog.Record(""GameHooks.AllowNativeOnlineInit"", datacenter);
            return AllowNativeOnline;
        }

        public static void StoreItemException(object item, Exception exception)
        {
            HookLog.Record(""GameHooks.StoreItemException"", item, exception);
        }
    }

    public static class AdminHooks
    {
        public static bool OpenIfPageMarkerAnswer;

        public static IEnumerable<object> AccountMenuButtons(IEnumerable<object> buttons)
        {
            HookLog.Record(""AdminHooks.AccountMenuButtons"", buttons);
            return buttons;
        }

        public static void OpenPage(string trigger, object world)
        {
            HookLog.Record(""AdminHooks.OpenPage"", trigger, world);
        }

        public static bool OpenIfPageMarker(string url, object browserComponent)
        {
            HookLog.Record(""AdminHooks.OpenIfPageMarker"", url, browserComponent);
            return OpenIfPageMarkerAnswer;
        }
    }

    public static class GraphicsHooks
    {
        /// <summary>null: return null (the patched code keeps the text it read).</summary>
        public static string GameOptionsJsonAnswer;

        public static string GameOptionsJson(string json)
        {
            HookLog.Record(""GraphicsHooks.GameOptionsJson"", json);
            return GameOptionsJsonAnswer;
        }

        public static void GameOptionsApplied(object options)
        {
            HookLog.Record(""GraphicsHooks.GameOptionsApplied"", options);
        }
    }

    public static class PcHooks
    {
        /// <summary>null: a completed task.</summary>
        public static Task QuickLaunchAnswer;

        public static Task QuickLaunch(object component, object link, CancellationToken ct)
        {
            HookLog.Record(""PcHooks.QuickLaunch"", component, link, ct);
            return QuickLaunchAnswer ?? Task.FromResult<object>(null);
        }

        public static void DefaultAutomatchHost(object settings)
        {
            HookLog.Record(""PcHooks.DefaultAutomatchHost"", settings);
        }

        public static void ServerWorldFailed(Exception exception)
        {
            HookLog.Record(""PcHooks.ServerWorldFailed"", exception);
        }

        public static void ComponentCreateFailed(Exception exception, Type type)
        {
            HookLog.Record(""PcHooks.ComponentCreateFailed"", exception, type);
        }
    }
}
";

        private static readonly object Gate = new object();
        private static byte[] compiled;

        /// <summary>McvsOffline.Server 0.0.0.0 built from <see cref="Source"/> against the net452 reference assemblies (cached).</summary>
        public static byte[] Compile()
        {
            lock (Gate)
            {
                if (compiled != null) return compiled;
                string refs = TestEnvironment.Net452Refs;
                if (refs == null) throw new InvalidOperationException("the .NET Framework 4.5.2 reference assemblies are missing (MCVS_NET452_REFS)");
                var references = new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }
                    .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(refs, f)))
                    .ToList();
                var tree = CSharpSyntaxTree.ParseText(Source, new CSharpParseOptions(LanguageVersion.CSharp7_3));
                var compilation = CSharpCompilation.Create("McvsOffline.Server", new[] { tree }, references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));
                using (var stream = new MemoryStream())
                {
                    var result = compilation.Emit(stream);
                    if (!result.Success)
                    {
                        throw new InvalidOperationException("PcHookServer does not compile:\n" +
                                                            string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
                    }
                    compiled = stream.ToArray();
                    return compiled;
                }
            }
        }

        /// <summary>Writes <see cref="Compile"/>'s output as McvsOffline.Server.dll into a new temp folder (for the resolver) and returns the folder.</summary>
        public static string WriteToTempFolder()
        {
            string dir = TestEnvironment.NewTempDir("pc-hook-server");
            File.WriteAllBytes(Path.Combine(dir, "McvsOffline.Server.dll"), Compile());
            return dir;
        }
    }
}
