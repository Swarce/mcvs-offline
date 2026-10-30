using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>
    /// P5 for the patched assemblies (docs/design/pc-port.md §1.3, §12.4): the PC work must not change what the patcher makes of
    /// the Android APK. The real Android assemblies (MCVS_REFS) are patched for the default options with a FIXED stand-in server
    /// (<see cref="RealGamePatchTests.HookServerSource"/>, its MVID pinned to <see cref="FixedServerMvid"/>), so DerivedMvid no
    /// longer depends on the real server DLL, which WP1/WP2 change on purpose. The expected values were recorded on day 0
    /// (D0-d) by running this same helper against the main-dev 7f24f82 patcher, before any patch class changed:
    /// <list type="bullet">
    /// <item>the SHA-256 of every patched assembly (byte-identical output);</item>
    /// <item>independent of any MVID: a digest of every method's <see cref="IlPatch.Fingerprint"/>, and a digest of the
    /// AssemblyRef, TypeRef and MemberRef tables.</item>
    /// </list>
    /// Build 17 changes the Android output on purpose with one new default patch, 'inbox' (Game.dll): the main-dev values hold
    /// for the default options without it, and the full default set equals them except for Game.dll, whose values with 'inbox'
    /// are pinned in <see cref="WithInbox"/> (recorded at build 17 by the same helper).
    /// Skipped without MCVS_REFS, unless MCVS_REQUIRE_REAL_CLIENT=1. On a mismatch the test prints the current values.
    /// </summary>
    public class AndroidOutputUnchangedTests
    {
        /// <summary>The stand-in server's MVID for <see cref="ApkPatcher.DerivedMvid"/>.</summary>
        public static readonly Guid FixedServerMvid = new Guid("5c0ffee0-0d0d-4a11-8a55-000000000d0d");

        /// <summary>file → (whole-file SHA-256, method-fingerprint digest, reference-table digest), from the 7f24f82 patcher.</summary>
        private static readonly Dictionary<string, string[]> Expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Game.dll"] = new[] { "39c750a9df3e5699295f1b01d7b57909008a82dc3c7b932067fabe07ff122583", "bbc6ecd1a831ec66d40d12b827c6d22f2a5bacf1dd88b3f1611c3b3d7e12a2c0", "dabdfe2cda921657ef4d7e47c954ff9542594a791fb26622cae21b999bebd489" },
            ["Gameloft.Engine.dll"] = new[] { "cf8ec142a254356896806f103f153d95fe348f6f5a309f07be8656bb9da293a6", "2264b0ad5272d7bf998ffe90ff23dc4cb29223f0b328879ed11f8c3021823696", "29358c03195573113c0019fe7f7f583584a5d7d4596ca7ea7258402fad675c2d" },
            ["Gameloft.Util.dll"] = new[] { "42c5a155b11bf1eb65fc30b85b99772392d19a85ab4cc093031e88ef0c29ad1d", "342fb9bed176369a4e244b63c61bc981810b228a99a35c6b1fdb6d8ac452f432", "61cd1c1022b4fe5a8b228acc7bf7ad1d1c3822c9447787ab380cdd4c1cc9c78a" },
            ["NicePlaySDK.dll"] = new[] { "4442d3ef71ef5c566891b462c61c3aa8618b460fc536d6cd58661f6069ad313b", "3f99c5d454cbf68d790c4e494a76683bc2540e7382dbe993b0ef90a92a762a9c", "0bc929bd72c2f70b6ba87045b969951c234647c0cf35957fe11e1a089ba9f652" },
        };

        /// <summary>Game.dll patched for the full default set of build 17, 'inbox' included (the other files equal <see cref="Expected"/>).</summary>
        private static readonly Dictionary<string, string[]> WithInbox = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Game.dll"] = new[] { "6f68d497be4021997b7b855db73264d9ee693f3674d1f03aba0f460b4c660279", "6169bdc7012954bc1f993930be10b7cf8424e684bce45e87ab350be8fca2fd4d", "023c53c26c859c5d55e17f7c29b2e13aa5836546c01431b331a7df2522f69465" },
        };

        private readonly ITestOutputHelper output;

        public AndroidOutputUnchangedTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>P5: the default options without the build-17 'inbox' patch give exactly the main-dev 7f24f82 output.</summary>
        [Fact]
        public void TheDefaultAndroidOutputWithoutInboxIsTheMainDevOutput()
        {
            string refs = Refs();
            if (refs == null) return;
            AssertOutput(PatchDefault(refs, "inbox"), Expected);
        }

        /// <summary>The full default set: the main-dev output except Game.dll, which differs only by the 'inbox' patch (pinned values).</summary>
        [Fact]
        public void TheDefaultAndroidOutputIsTheMainDevOutputPlusTheInboxPatch()
        {
            string refs = Refs();
            if (refs == null) return;
            Assert.Contains("inbox", new PatcherOptions().Patches);
            Dictionary<string, byte[]> patched = PatchDefault(refs);
            var expected = new Dictionary<string, string[]>(Expected, StringComparer.Ordinal);
            foreach (var pair in WithInbox) expected[pair.Key] = pair.Value;
            AssertOutput(patched, expected);
            Assert.NotEqual(Expected["Game.dll"][1], WithInbox["Game.dll"][1]);
            using (ModuleDefinition game = ModuleDefinition.ReadModule(new MemoryStream(patched["Game.dll"]), new ReaderParameters { InMemory = true }))
                Assert.NotNull(game.GetType("Game." + InboxPatch.HelperType));
        }

        private string Refs()
        {
            string refs = TestEnvironment.GameAssemblies;
            if (refs != null && File.Exists(Path.Combine(refs, "Game.dll"))) return refs;
            Assert.True(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") != "1", "MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private void AssertOutput(Dictionary<string, byte[]> patched, Dictionary<string, string[]> expectedValues)
        {
            var problems = new List<string>();
            foreach (var pair in patched.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string[] actual = Describe(pair.Value);
                output.WriteLine("[\"" + pair.Key + "\"] = new[] { \"" + string.Join("\", \"", actual) + "\" },");
                string[] expected;
                if (!expectedValues.TryGetValue(pair.Key, out expected))
                {
                    problems.Add(pair.Key + ": patched now, but not in the main-dev output");
                    continue;
                }
                if (expected[1] != actual[1]) problems.Add(pair.Key + ": a patched method's IL differs (method fingerprints)");
                if (expected[2] != actual[2]) problems.Add(pair.Key + ": the AssemblyRef/TypeRef/MemberRef tables differ");
                if (expected[0] != actual[0]) problems.Add(pair.Key + ": the file differs (SHA-256)");
            }
            foreach (string missing in expectedValues.Keys.Where(k => !patched.ContainsKey(k))) problems.Add(missing + ": patched on main-dev, not now");
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        /// <summary>The default options' IL patches (less <paramref name="without"/>) applied as ApkPatcher.Run applies them, with the fixed stand-in server.</summary>
        internal static Dictionary<string, byte[]> PatchDefault(string refs, params string[] without)
        {
            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
            var ids = new PatcherOptions().Patches;
            foreach (string id in without) Assert.True(ids.Remove(id), id + " is not a default patch");
            List<IlPatch> patches = ApkPatcher.SelectPatches(ids);
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
                var reader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate };
                using (ModuleDefinition serverModule = ModuleDefinition.ReadModule(new MemoryStream(server)))
                {
                    serverModule.Mvid = FixedServerMvid;
                    var context = new PatchContext(serverModule, 51780);
                    foreach (var group in patches.GroupBy(p => p.AssemblyFile))
                    {
                        byte[] original = File.ReadAllBytes(Path.Combine(refs, group.Key));
                        result[group.Key] = ApkPatcher.PatchAssembly(original, group.ToList(), context, reader, FixedServerMvid, s => { });
                    }
                }
            }
            return result;
        }

        /// <summary>{ SHA-256 of the file, digest of every method's fingerprint, digest of the reference tables }.</summary>
        internal static string[] Describe(byte[] image)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(image), new ReaderParameters { InMemory = true }))
            {
                var methods = new List<string>();
                foreach (TypeDefinition type in AllTypes(module.Types))
                {
                    foreach (MethodDefinition method in type.Methods)
                    {
                        if (method.HasBody) methods.Add(method.FullName + " " + IlPatch.Fingerprint(method));
                    }
                }
                methods.Sort(StringComparer.Ordinal);
                var tables = new StringBuilder();
                foreach (AssemblyNameReference r in module.AssemblyReferences) tables.Append("asm ").Append(r.FullName).Append('\n');
                foreach (TypeReference t in module.GetTypeReferences()) tables.Append("type ").Append(t.Scope == null ? "" : t.Scope.Name).Append(' ').Append(t.FullName).Append('\n');
                foreach (MemberReference m in module.GetMemberReferences()) tables.Append("member ").Append(m.FullName).Append('\n');
                return new[] { Sha256(image), Sha256(Encoding.UTF8.GetBytes(string.Join("\n", methods))), Sha256(Encoding.UTF8.GetBytes(tables.ToString())) };
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types)
            {
                yield return type;
                foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
            }
        }

        private static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create()) return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }
}
