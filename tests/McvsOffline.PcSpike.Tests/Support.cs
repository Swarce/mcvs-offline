using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>The Steam install the research used (read only; never committed), temp folders and Roslyn fixtures.</summary>
    public static class Support
    {
        /// <summary>The Steam game folder (MCVS_PC_INSTALL, default /home/user/mcvs-work/pc/install), or null after logging a skip.
        /// MCVS_REQUIRE_PC=1 turns a missing install into a failure.</summary>
        public static string PcInstall(ITestOutputHelper output)
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_PC_INSTALL");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/pc/install";
            if (File.Exists(Path.Combine(dir, "ModernCombatVersus.exe")) && File.Exists(Path.Combine(dir, "EngineBridge.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_PC=1 but " + dir + " has no Steam install of the game");
            output.WriteLine("the Steam install is not available (MCVS_PC_INSTALL=" + dir + "): skipped");
            return null;
        }

        public static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mcvs-pcspike-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static void DeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>The .NET Framework 4.5 reference assemblies (Microsoft.NETFramework.ReferenceAssemblies.net45).</summary>
        public static string Net45ReferenceAssemblies
        {
            get
            {
                string dir = typeof(Support).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                                .Single(a => a.Key == "Net45ReferenceAssemblies").Value;
                if (!File.Exists(Path.Combine(dir, "mscorlib.dll"))) throw new InvalidOperationException("no mscorlib.dll in " + dir);
                return dir;
            }
        }

        /// <summary>
        /// The Steam EngineBridge as source: <c>Class1.Start</c> compiles (Release) to the same six instructions as the
        /// shipped net451 DLL, so the patch and the redirect step can be exercised without the install.
        /// </summary>
        public const string EngineBridgeSource = @"
using Gameloft.Engine;
namespace EngineBridge
{
    public sealed class Class1
    {
        public void Start()
        {
            MainLoop.Start(MainLoop.ModeType.NativeHostsDotNet);
        }
    }
}";

        /// <summary>A Gameloft.Engine (version 0.0.0.0, as on PC) whose MainLoop.Start records its arguments instead of starting the engine.</summary>
        public const string EngineSource = @"
namespace Gameloft.Engine
{
    public static class MainLoop
    {
        public enum ModeType { NativeHostsDotNet = 0, DotNetHostsNative = 1, FormsHostsNative = 2, NotStarted = -1 }
        public static int Calls;
        public static ModeType LastMode = ModeType.NotStarted;
        public static string[] LastArgs = new string[0];
        public static bool LastIsEditor = true;
        public static int Start(ModeType mode, string[] args = null, bool isEditor = false)
        {
            Calls++;
            LastMode = mode;
            LastArgs = args;
            LastIsEditor = isEditor;
            return 0;
        }
    }
}";

        /// <summary>Like the game's DLLs: built against .NET Framework 4.5 (mscorlib 4.0.0.0), which net8 loads through its facades.</summary>
        private static readonly Lazy<List<MetadataReference>> Framework = new Lazy<List<MetadataReference>>(() =>
            new[] { "mscorlib.dll", "System.dll", "System.Core.dll" }
                .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(Net45ReferenceAssemblies, f)))
                .ToList());

        public static byte[] Compile(string assemblyName, string source, params byte[][] references)
        {
            var refs = new List<MetadataReference>(Framework.Value);
            refs.AddRange(references.Select(r => (MetadataReference)MetadataReference.CreateFromImage(r)));
            var compilation = CSharpCompilation.Create(assemblyName,
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp7_3)) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var stream = new MemoryStream())
            {
                var result = compilation.Emit(stream);
                if (!result.Success)
                {
                    throw new InvalidOperationException(assemblyName + " does not compile:\n" +
                                                        string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
                }
                return stream.ToArray();
            }
        }

        public static byte[] Engine() => Compile("Gameloft.Engine", EngineSource);

        public static byte[] EngineBridge(byte[] engine) => Compile("EngineBridge", EngineBridgeSource, engine);

        /// <summary>Loads assemblies from bytes into an isolated, collectible context.</summary>
        public sealed class Sandbox : AssemblyLoadContext, IDisposable
        {
            private readonly Dictionary<string, byte[]> images = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            public Sandbox() : base("pcspike-" + Guid.NewGuid().ToString("N"), true) { }

            public Sandbox With(string name, byte[] image)
            {
                images[name] = image;
                return this;
            }

            public Assembly Get(string name) => LoadFromAssemblyName(new AssemblyName(name));

            protected override Assembly Load(AssemblyName name)
            {
                byte[] image;
                return images.TryGetValue(name.Name, out image) ? LoadFromStream(new MemoryStream(image)) : null;
            }

            public void Dispose() => Unload();
        }
    }
}
