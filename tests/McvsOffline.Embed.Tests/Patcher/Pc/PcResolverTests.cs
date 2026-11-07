using System;
using System.IO;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Pc;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// <see cref="PcAssemblyResolver"/> and the test support built on it (docs/design/pc-port.md §7.3, C12): only the given
    /// directories, the BCL first; never .NET 8's facades.
    /// </summary>
    public class PcResolverTests
    {
        private readonly ITestOutputHelper output;

        public PcResolverTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void MscorlibResolvesIntoTheBclDirectoryAndNothingElseIsSearched()
        {
            string bcl = TestEnvironment.Net452Refs;
            Assert.True(bcl != null, "the net452 reference assemblies are part of the test setup (NuGet)");
            string server = PcHookServer.WriteToTempFolder();
            using (var resolver = new PcAssemblyResolver(bcl, null, server))
            {
                AssemblyDefinition corlib = resolver.Resolve(AssemblyNameReference.Parse("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));
                Assert.Equal(Path.Combine(bcl, "mscorlib.dll"), corlib.MainModule.FileName);
                Assert.Same(corlib, resolver.Resolve(AssemblyNameReference.Parse("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")));
                // Only in the .NET 8 shared framework (and net452's Facades/, which is not searched): never found.
                Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(AssemblyNameReference.Parse("System.Runtime, Version=4.0.0.0")));
                Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(AssemblyNameReference.Parse("NoSuchAssembly")));
                Assert.Equal(Path.Combine(server, "McvsOffline.Server.dll"), resolver.Find("McvsOffline.Server"));
            }
            Assert.Throws<ArgumentException>(() => new PcAssemblyResolver(server, null, null));   // no mscorlib.dll there
        }

        [Fact]
        public void TheBclComesFirstThenTheGameThenTheServerFolder()
        {
            string bcl = TestEnvironment.Net452Refs;
            Assert.True(bcl != null, "the net452 reference assemblies are part of the test setup (NuGet)");
            string game = TestEnvironment.NewTempDir("resolver-game");
            string server = TestEnvironment.NewTempDir("resolver-server");
            // The same name in all three places: the BCL wins; a name only in the game and the server folder: the game wins.
            File.WriteAllBytes(Path.Combine(game, "System.dll"), PcHookServer.Compile());
            File.WriteAllBytes(Path.Combine(game, "Shared.dll"), PcHookServer.Compile());
            File.WriteAllBytes(Path.Combine(server, "Shared.dll"), PcHookServer.Compile());
            File.WriteAllBytes(Path.Combine(server, "OnlyServer.exe"), PcHookServer.Compile());
            using (var resolver = new PcAssemblyResolver(bcl, game, server))
            {
                Assert.Equal(new[] { bcl, Path.GetFullPath(game), Path.GetFullPath(server) }, resolver.Directories);
                Assert.Equal(Path.Combine(bcl, "System.dll"), resolver.Find("System"));
                Assert.Equal(Path.Combine(game, "Shared.dll"), resolver.Find("Shared"));
                Assert.Equal(Path.Combine(server, "OnlyServer.exe"), resolver.Find("OnlyServer"));
                Assert.Null(resolver.Find("../System"));
                Assert.Null(resolver.Find("Nowhere"));
                AssemblyResolutionException missing = Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(AssemblyNameReference.Parse("Nowhere")));
                Assert.Equal("Nowhere", missing.AssemblyReference.Name);
            }
            Assert.Throws<ArgumentException>(() => new PcAssemblyResolver(null, game, server));
        }

        [Fact]
        public void ThePcHookServerHasOneCorlibAndAssertPcCorlibCatchesTheDefaultResolver()
        {
            byte[] image = PcHookServer.Compile();
            ReaderParameters reader = TestEnvironment.PcReader(null, null);
            using (reader.AssemblyResolver)
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(image), reader))
            {
                Assert.Equal("McvsOffline.Server", module.Assembly.Name.Name);
                Assert.Equal(new Version(0, 0, 0, 0), module.Assembly.Name.Version);
                TestEnvironment.AssertPcCorlib(module);
                Assert.NotNull(module.GetType("McvsOffline.PcHooks"));
            }
            // Framework-dependent Cecil finds mscorlib 4.0.0.0 among .NET 8's trusted platform assemblies: the assert must say no.
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(image)))
            {
                Exception failure = Record.Exception(() => TestEnvironment.AssertPcCorlib(module));
                output.WriteLine(failure == null ? "(no failure)" : failure.Message);
                Assert.NotNull(failure);
            }
        }
    }
}
