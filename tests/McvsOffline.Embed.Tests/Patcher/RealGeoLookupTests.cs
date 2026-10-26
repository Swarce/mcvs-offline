using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using McvsOffline.Server;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The 'geo' patch applied to the player's REAL Gameloft.Util.dll (MCVS_REFS) in memory, then the game's own
    /// <c>Geolocalization.GetRegion</c> (called during the TS handshake) run against the complete server: the patched
    /// URL reaches MISC's location route and the game parses <c>["country_info"]["iso2"]</c>. Skipped without the
    /// assembly unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    public class RealGeoLookupTests
    {
        private readonly ITestOutputHelper output;

        public RealGeoLookupTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public async Task PatchedGeolocalizationAsksTheServerAndParsesItsAnswer()
        {
            string refs = Environment.GetEnvironmentVariable("MCVS_REFS");
            if (string.IsNullOrEmpty(refs)) refs = "/home/user/mcvs-work/extracted/assemblies";
            string util = Path.Combine(refs, "Gameloft.Util.dll");
            if (!File.Exists(util))
            {
                if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                    throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but " + util + " is missing");
                output.WriteLine("Gameloft.Util.dll not found in MCVS_REFS (" + refs + "): skipped");
                return;
            }

            string data = TestEnvironment.NewTempDir("geo");
            try
            {
                using var server = McvsServer.Create(new ServerOptions { DataDir = data, Port = 0, ConsoleLog = false, FileLog = false });
                server.Start();

                byte[] patched;
                var resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(refs);
                using (ModuleDefinition module = ModuleDefinition.ReadModule(util, new ReaderParameters { AssemblyResolver = resolver }))
                {
                    var context = new PatchContext(null, server.Port);
                    new GeoPatch().Apply(module, context);
                    output.WriteLine(string.Join("\n", context.Notes));
                    using var stream = new MemoryStream();
                    module.Write(stream);
                    patched = stream.ToArray();
                }

                var loader = new PatchedContext(refs, patched);
                Type geo = loader.LoadFromAssemblyName(new AssemblyName("Gameloft.Util")).GetType("Gameloft.Util.Geolocalization", true);
                object instance = Activator.CreateInstance(geo);
                Assert.Equal("http://127.0.0.1:" + server.Port + "/location/?ip=", geo.GetProperty("GeolocalizationUrl").GetValue(instance));

                var lookup = (Task<string>)geo.GetMethod("GetRegion").Invoke(instance, new object[] { IPAddress.Parse("192.168.1.30"), CancellationToken.None });
                Assert.Same(lookup, await Task.WhenAny(lookup, Task.Delay(10000)));
                Assert.Equal("US", await lookup);
                GC.KeepAlive(loader);   // a finalized collectible context starts unloading (see RealArgumentsParserTests)
            }
            finally
            {
                try { Directory.Delete(data, true); } catch (IOException) { }
            }
        }

        /// <summary>The game's assemblies from the folder, with the patched Gameloft.Util; the BCL from net8.</summary>
        private sealed class PatchedContext : AssemblyLoadContext
        {
            private readonly string directory;
            private readonly byte[] patchedUtil;

            public PatchedContext(string directory, byte[] patchedUtil) : base("mcvs-geo-conformance", isCollectible: true)
            {
                this.directory = directory;
                this.patchedUtil = patchedUtil;
            }

            protected override Assembly Load(AssemblyName name)
            {
                string simple = name.Name;
                if (simple == "Gameloft.Util") return LoadFromStream(new MemoryStream(patchedUtil));
                if (simple == "mscorlib" || simple == "netstandard" || simple == "System" || simple.StartsWith("System.", StringComparison.Ordinal)
                    || simple.StartsWith("Microsoft.", StringComparison.Ordinal))
                    return null;
                string path = Path.Combine(directory, simple + ".dll");
                return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
            }
        }
    }
}
