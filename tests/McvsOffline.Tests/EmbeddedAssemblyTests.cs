using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests
{
    /// <summary>
    /// Guards the embedded build (artifacts/bin/McvsOffline.Server.Embedded/*/McvsOffline.Server.dll): it must only
    /// reference assemblies that ship in the game's APK and expose McvsOffline.Bootstrap.Init().
    /// Built by scripts/build.sh before the tests run; set MCVS_REQUIRE_EMBEDDED=1 to fail when it is missing.
    /// </summary>
    public class EmbeddedAssemblyTests
    {
        private static readonly HashSet<string> AllowedReferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "mscorlib", "System", "System.Core", "Mono.Android", "Java.Interop",
        };

        private readonly ITestOutputHelper output;

        public EmbeddedAssemblyTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void EmbeddedDllReferencesOnlyTheGameBcl()
        {
            string dll = FindEmbeddedDll();
            if (dll == null)
            {
                Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_EMBEDDED") != "1",
                            "embedded DLL not found; build server/McvsOffline.Server.Embedded.csproj first");
                output.WriteLine("embedded DLL not built: skipped");
                return;
            }
            output.WriteLine(dll);
            using var stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            Assert.Equal("McvsOffline.Server", md.GetString(md.GetAssemblyDefinition().Name));

            var references = new List<string>();
            foreach (var handle in md.AssemblyReferences)
            {
                string name = md.GetString(md.GetAssemblyReference(handle).Name);
                references.Add(name);
                output.WriteLine("references " + name);
                Assert.Contains(name, AllowedReferences);
            }
            Assert.Contains("mscorlib", references);

            bool bootstrap = false;
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (md.GetString(type.Namespace) != "McvsOffline" || md.GetString(type.Name) != "Bootstrap") continue;
                foreach (var m in type.GetMethods())
                {
                    if (md.GetString(md.GetMethodDefinition(m).Name) == "Init") bootstrap = true;
                }
            }
            Assert.True(bootstrap, "McvsOffline.Bootstrap.Init() missing");
        }

        private static string FindEmbeddedDll()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "McvsOffline.sln"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) return null;
            string bin = Path.Combine(dir, "artifacts", "bin", "McvsOffline.Server.Embedded");
            if (!Directory.Exists(bin)) return null;
            string newest = null;
            foreach (string candidate in Directory.GetFiles(bin, "McvsOffline.Server.dll", SearchOption.AllDirectories))
            {
                if (newest == null || File.GetLastWriteTimeUtc(candidate) > File.GetLastWriteTimeUtc(newest)) newest = candidate;
            }
            return newest;
        }
    }
}
