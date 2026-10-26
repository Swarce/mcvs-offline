using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Signing;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;
using ZipEntry = McvsOffline.Patcher.Zip.ZipEntry;

namespace McvsOffline.Embed.Tests.Patcher
{
    public class ZipTests
    {
        private static byte[] Data(int size, int seed)
        {
            var random = new Random(seed);
            var data = new byte[size];
            random.NextBytes(data);
            return data;
        }

        private static List<ZipEntry> SampleEntries()
        {
            return new List<ZipEntry>
            {
                ZipEntry.CreateDeflated("AndroidManifest.xml", Encoding.UTF8.GetBytes(new string('m', 999)), null),
                ZipEntry.CreateStored("res/raw/odd.bin", Data(7, 1), null),
                ZipEntry.CreateStored("assemblies/A.dll", Data(1001, 2), null),
                ZipEntry.CreateStored("lib/arm64-v8a/libstored.so", Data(333, 3), null),
                ZipEntry.CreateDeflated("lib/arm64-v8a/libaot-A.dll.so", Data(5000, 4), null),
                ZipEntry.CreateStored("assemblies/B.dll", Data(13, 5), null),
            };
        }

        /// <summary>Offset of an entry's data, read from its local header.</summary>
        internal static long DataOffset(byte[] zip, ZipEntry entry)
        {
            long h = entry.SourceOffset;
            int nameLength = zip[h + 26] | zip[h + 27] << 8;
            int extraLength = zip[h + 28] | zip[h + 29] << 8;
            return h + 30 + nameLength + extraLength;
        }

        [Fact]
        public void RoundTripKeepsRawDataAndAlignsStoredEntries()
        {
            List<ZipEntry> entries = SampleEntries();
            byte[] zip = ZipArchiveWriter.Write(entries);
            ZipArchiveReader.Archive back = ZipArchiveReader.Read(zip);

            Assert.Equal(entries.Select(e => e.Name), back.Entries.Select(e => e.Name));
            foreach (ZipEntry e in back.Entries)
            {
                ZipEntry original = entries.Single(x => x.Name == e.Name);
                Assert.Equal(original.Method, e.Method);
                Assert.Equal(original.Crc32, e.Crc32);
                Assert.Equal(original.RawData, e.RawData);
                Assert.Equal(original.GetData(), e.GetData());
                if (e.Method == ZipEntry.MethodStored)
                    Assert.Equal(0, DataOffset(zip, e) % ZipArchiveWriter.AlignmentFor(e));
            }
            Assert.Equal(0, DataOffset(zip, back.Find("lib/arm64-v8a/libstored.so")) % 4096);
        }

        [Fact]
        public void OutputIsReadableByTheBclZipReader()
        {
            List<ZipEntry> entries = SampleEntries();
            using (var archive = new ZipArchive(new MemoryStream(ZipArchiveWriter.Write(entries)), ZipArchiveMode.Read))
            {
                foreach (ZipEntry e in entries)
                {
                    using (Stream s = archive.GetEntry(e.Name).Open())
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        Assert.Equal(e.GetData(), ms.ToArray());
                    }
                }
            }
        }

        [Fact]
        public void OldPaddingIsReplacedButRealExtraRecordsAreKept()
        {
            byte[] zeros = new byte[3];
            Assert.Empty(ZipArchiveWriter.StripPadding(zeros));
            byte[] alignment = { 0x35, 0xD9, 0x02, 0x00, 0x04, 0x00 };
            Assert.Empty(ZipArchiveWriter.StripPadding(alignment));
            byte[] unix = { 0x75, 0x78, 0x02, 0x00, 0xAA, 0xBB };
            Assert.Equal(unix, ZipArchiveWriter.StripPadding(unix.Concat(alignment).ToArray()));
        }

        [Fact]
        public void Crc32MatchesTheBclImplementation()
        {
            byte[] data = Data(4096, 7);
            uint expected;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
                using (Stream s = archive.CreateEntry("x", CompressionLevel.NoCompression).Open())
                    s.Write(data, 0, data.Length);
                ms.Position = 0;
                expected = ZipArchiveReader.Read(ms.ToArray()).Entries[0].Crc32;
            }
            Assert.Equal(expected, Crc32.Compute(data));
            Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        }
    }

    public class SigningAndPipelineTests
    {
        private readonly ITestOutputHelper output;

        public SigningAndPipelineTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static BuildTools Tools()
        {
            try
            {
                return BuildTools.Locate(TestEnvironment.BuildToolsDir);
            }
            catch (ArgumentException)
            {
                return BuildTools.Locate(null);
            }
        }

        /// <summary>
        /// A small APK-shaped zip with the look-alike NicePlaySDK and the entry kinds the patcher handles; with
        /// <paramref name="debloatLike"/> its manifest has every debloat target and it holds the Crashlytics/Fabric files.
        /// </summary>
        private static byte[] SyntheticApk(bool debloatLike = false, string withoutEntry = null)
        {
            AxmlWriter.Element manifest = debloatLike ? ManifestDebloatTests.DebloatLikeManifest() : AxmlWriter.GameLikeManifest();
            var entries = new List<ZipEntry>
            {
                ZipEntry.CreateDeflated("AndroidManifest.xml", AxmlWriter.Write(manifest), null),
                ZipEntry.CreateDeflated("META-INF/MANIFEST.MF", Encoding.ASCII.GetBytes("Manifest-Version: 1.0\r\n\r\n"), null),
                ZipEntry.CreateDeflated("META-INF/GAMELOFT.SF", Encoding.ASCII.GetBytes("old"), null),
                ZipEntry.CreateDeflated("META-INF/GAMELOFT.RSA", new byte[] { 1, 2, 3 }, null),
                ZipEntry.CreateDeflated("META-INF/maven/x/pom.xml", Encoding.ASCII.GetBytes("<pom/>"), null),
                ZipEntry.CreateStored("assemblies/Other.dll", FixtureCompiler.Compile("Other", "public class Other {}"), null),
                ZipEntry.CreateStored("assemblies/NicePlaySDK.dll", FixtureCompiler.Compile("NicePlaySDK", Fixtures.NicePlaySdk), null),
                ZipEntry.CreateDeflated("lib/arm64-v8a/libmonodroid.so", new byte[2000], null),
                ZipEntry.CreateDeflated("lib/arm64-v8a/libaot-NicePlaySDK.dll.so", new byte[3000], null),
                ZipEntry.CreateDeflated("lib/armeabi-v7a/libaot-NicePlaySDK.dll.so", new byte[3001], null),
                ZipEntry.CreateDeflated("lib/arm64-v8a/libaot-Other.dll.so", new byte[3002], null),
                ZipEntry.CreateStored("res/raw/odd.bin", new byte[] { 9, 9, 9 }, null),
                ZipEntry.CreateStored("res/raw/args.txt", Encoding.ASCII.GetBytes(KnownBuild.RawArgsOriginal), null),
                ZipEntry.CreateStored("resources.arsc", new byte[517], null),
            };
            if (debloatLike)
            {
                foreach (string name in ApkPatcher.DebloatDroppedEntries.Keys)
                {
                    byte[] data = Encoding.ASCII.GetBytes("# " + name + "\n");
                    entries.Add(name.EndsWith(".so", StringComparison.Ordinal) ? ZipEntry.CreateDeflated(name, new byte[1500], null) : ZipEntry.CreateDeflated(name, data, null));
                }
                entries.Add(ZipEntry.CreateDeflated("fabric/README.txt", Encoding.ASCII.GetBytes("not a properties file"), null));   // not a debloat kind
            }
            entries.RemoveAll(e => e.Name == withoutEntry);
            return ZipArchiveWriter.Write(entries);
        }

        private PatchReport RunSynthetic(string dir, SignerChoice signer, out string outApk, SortedSet<string> abis = null, bool allAbis = false,
                                         string patches = "none", byte[] apkBytes = null)
        {
            string apk = Path.Combine(dir, "in.apk");
            File.WriteAllBytes(apk, apkBytes ?? SyntheticApk());
            string server = Path.Combine(dir, "McvsOffline.Server.dll");
            File.WriteAllBytes(server, Fixtures.Server());
            outApk = Path.Combine(dir, "out", "offline.apk");
            var options = new PatcherOptions
            {
                InputApk = apk,
                ServerDll = server,
                OutputApk = outApk,
                Patches = PatcherOptions.ParsePatchList(patches),
                Keystore = Path.Combine(dir, "key.p12"),
                BuildToolsDir = Tools().ApkSigner != null ? Path.GetDirectoryName(Tools().ApkSigner) : null,
                Signer = signer,
                Abis = allAbis ? null : abis ?? new SortedSet<string>(new[] { PatcherOptions.Arm64 }, StringComparer.Ordinal),
                Force = true,
                SkipServerReferenceCheck = true,
                TestOnlyHookIsMandatory = true,
                TestOnlyDebloatWithoutIl = true,
            };
            return ApkPatcher.Run(options, s => output.WriteLine(s));
        }

        [Fact]
        public void AbiSelectionKeepsAllOnRequestAndRefusesAnAbiTheApkLacks()
        {
            string dir = TestEnvironment.NewTempDir("abis");
            PatchReport all = RunSynthetic(dir, SignerChoice.BuiltIn, out string outApk, allAbis: true);
            var names = ZipArchiveReader.Read(File.ReadAllBytes(outApk)).Entries.Select(e => e.Name).ToList();
            Assert.DoesNotContain("lib/armeabi-v7a/libaot-NicePlaySDK.dll.so", names);   // still an AOT image of a patched assembly
            Assert.DoesNotContain(all.Removed, r => r.Contains("ABI not kept"));
            Assert.Contains("native libraries: every ABI kept (arm64-v8a, armeabi-v7a)", all.Notes);

            // No lib/x86/libmonodroid.so in the APK: keeping only x86 would give an APK that cannot start.
            var ex = Assert.Throws<InvalidDataException>(() => RunSynthetic(TestEnvironment.NewTempDir("abis-x86"), SignerChoice.BuiltIn, out _,
                                                                            new SortedSet<string>(new[] { "x86" }, StringComparer.Ordinal)));
            Assert.Contains("lib/x86/libmonodroid.so", ex.Message);
        }

        [Fact]
        public void AbiListParsing()
        {
            Assert.Equal(new[] { "arm64-v8a" }, PatcherOptions.ParseAbiList("arm64-v8a"));
            Assert.Equal(new[] { "arm64-v8a", "armeabi-v7a" }, PatcherOptions.ParseAbiList(" armeabi-v7a, ARM64-V8A "));
            Assert.Null(PatcherOptions.ParseAbiList("all"));
            Assert.Throws<ArgumentException>(() => PatcherOptions.ParseAbiList("arm65"));
            Assert.Throws<ArgumentException>(() => PatcherOptions.ParseAbiList(""));
            Assert.Null(new PatcherOptions().Abis);
            Assert.Equal("armeabi-v7a", ApkPatcher.AbiOf("lib/armeabi-v7a/libmonodroid.so"));
            Assert.Null(ApkPatcher.AbiOf("assemblies/Game.dll"));
            Assert.Null(ApkPatcher.AbiOf("lib/odd"));
        }

        [Fact]
        public void PipelineAddsServerStripsAotAndKeepsEverythingElse()
        {
            string dir = TestEnvironment.NewTempDir("pipeline");
            PatchReport report = RunSynthetic(dir, SignerChoice.BuiltIn, out string outApk);

            ZipArchiveReader.Archive input = ZipArchiveReader.Read(Path.Combine(dir, "in.apk"));
            byte[] bytes = File.ReadAllBytes(outApk);
            ZipArchiveReader.Archive result = ZipArchiveReader.Read(bytes);
            var names = result.Entries.Select(e => e.Name).ToList();

            Assert.True(report.KeystoreCreated);
            Assert.True(result.HasSigningBlock);
            Assert.Contains("assemblies/McvsOffline.Server.dll", names);
            Assert.Equal(names.IndexOf("assemblies/NicePlaySDK.dll") + 1, names.IndexOf("assemblies/McvsOffline.Server.dll"));
            Assert.DoesNotContain("lib/arm64-v8a/libaot-NicePlaySDK.dll.so", names);
            Assert.DoesNotContain("lib/armeabi-v7a/libaot-NicePlaySDK.dll.so", names);
            // 64-bit only by default: nothing of lib/armeabi-v7a survives, and it is reported as dropped by ABI.
            Assert.DoesNotContain(names, n => n.StartsWith("lib/armeabi-v7a/", StringComparison.Ordinal));
            Assert.Contains("lib/armeabi-v7a/libaot-NicePlaySDK.dll.so (native library of an ABI not kept, --abi)", report.Removed);
            Assert.Contains(report.Notes, n => n.StartsWith("native libraries: kept arm64-v8a; dropped armeabi-v7a", StringComparison.Ordinal));
            Assert.DoesNotContain("META-INF/GAMELOFT.SF", names);
            Assert.Contains("META-INF/CERT.SF", names);
            // The manifest's vendor-wide permission was renamed (ManifestPatchTests); it stays DEFLATED.
            Assert.Contains("AndroidManifest.xml", report.Modified);
            Assert.Equal(ZipEntry.MethodDeflated, result.Find("AndroidManifest.xml").Method);
            Assert.Equal(ManifestPatch.Apply(input.Find("AndroidManifest.xml").GetData(), null), result.Find("AndroidManifest.xml").GetData());
            Assert.Contains(report.Notes, n => n.Contains("glshare.permission.ACCESS_SHARED_DATA renamed"));
            foreach (string kept in new[] { "assemblies/Other.dll", "lib/arm64-v8a/libmonodroid.so", "lib/arm64-v8a/libaot-Other.dll.so",
                                             "res/raw/odd.bin", "resources.arsc", "META-INF/maven/x/pom.xml" })
            {
                ZipEntry a = input.Find(kept), b = result.Find(kept);
                Assert.Equal(a.Method, b.Method);
                Assert.Equal(a.RawData, b.RawData);
            }
            foreach (ZipEntry e in result.Entries.Where(e => e.Method == ZipEntry.MethodStored))
                Assert.Equal(0, ZipTests.DataOffset(bytes, e) % 4);
            Assert.NotEqual(input.Find("assemblies/NicePlaySDK.dll").RawData, result.Find("assemblies/NicePlaySDK.dll").RawData);
            // --game-log all (the default): res/raw/args.txt keeps its name and STORED method, without -LogCategoryFilter.
            Assert.Contains("res/raw/args.txt", report.Modified);
            ZipEntry rawArgs = result.Find("res/raw/args.txt");
            Assert.Equal(ZipEntry.MethodStored, rawArgs.Method);
            Assert.Equal(KnownBuild.RawArgsAllLevels, Encoding.ASCII.GetString(rawArgs.GetData()));
            Assert.Contains(report.Notes, n => n.StartsWith("res/raw/args.txt: ", StringComparison.Ordinal));
            Assert.Equal(ZipEntry.MethodStored, result.Find("assemblies/NicePlaySDK.dll").Method);
        }

        [Fact]
        public void DebloatRemovesTheManifestTargetsAndTheCrashlyticsFiles()
        {
            string dir = TestEnvironment.NewTempDir("debloat");
            PatchReport report = RunSynthetic(dir, SignerChoice.BuiltIn, out string outApk, allAbis: true, patches: "debloat",
                                              apkBytes: SyntheticApk(debloatLike: true));
            ZipArchiveReader.Archive input = ZipArchiveReader.Read(Path.Combine(dir, "in.apk"));
            ZipArchiveReader.Archive result = ZipArchiveReader.Read(File.ReadAllBytes(outApk));

            // The files: gone from every ABI and reported one by one (like the ABI drops) plus one note; anything else stays.
            foreach (string name in ApkPatcher.DebloatDroppedEntries.Keys)
            {
                Assert.Null(result.Find(name));
                Assert.Contains(name + " (" + ApkPatcher.DebloatDroppedEntries[name] + "; debloat)", report.Removed);
            }
            Assert.Equal(input.Find("fabric/README.txt").RawData, result.Find("fabric/README.txt").RawData);
            Assert.Contains("debloat: dropped 9 Crashlytics/Fabric files nothing loads offline (2 lib/<abi>/libcrashlytics.so, 7 Fabric/Crashlytics .properties)", report.Notes);

            // The manifest: the permission rename plus the debloat, still DEFLATED, one note per removed element.
            Assert.Contains("AndroidManifest.xml", report.Modified);
            Assert.Equal(ZipEntry.MethodDeflated, result.Find("AndroidManifest.xml").Method);
            Assert.Equal(ManifestDebloat.Apply(ManifestPatch.Apply(input.Find("AndroidManifest.xml").GetData(), null), null), result.Find("AndroidManifest.xml").GetData());
            Assert.Equal(ManifestDebloat.Targets.Count, report.Notes.Count(n => n.StartsWith("manifest: removed <", StringComparison.Ordinal)));
            var removed = new HashSet<string>(report.Removed.Select(r => r.Split(' ')[0]));
            Assert.All(input.Entries.Where(e => !removed.Contains(e.Name)), e => Assert.NotNull(result.Find(e.Name)));

            BuildTools tools = Tools();
            if (tools.ApkSigner == null)
            {
                output.WriteLine("apksigner not found (set MCVS_BUILD_TOOLS): signature not verified");
                return;
            }
            Assert.Contains("Verified using v2 scheme (APK Signature Scheme v2): true", ExternalApkSigner.Verify(tools.ApkSigner, outApk));
        }

        [Fact]
        public void DebloatWritesNothingForAnApkItWasNotVerifiedOn()
        {
            // Not the 1.17.32 manifest (the files are all there): refused, nothing written.
            string dir = TestEnvironment.NewTempDir("debloat-manifest");
            byte[] plainManifest = SyntheticApk(debloatLike: true);
            var entries = ZipArchiveReader.Read(plainManifest).Entries.Select(e => e.Name == "AndroidManifest.xml"
                ? ZipEntry.CreateDeflated(e.Name, AxmlWriter.Write(AxmlWriter.GameLikeManifest()), null) : e).ToList();
            var ex = Assert.Throws<InvalidDataException>(() => RunSynthetic(dir, SignerChoice.BuiltIn, out string outApk, patches: "debloat",
                                                                            apkBytes: ZipArchiveWriter.Write(entries)));
            Assert.Contains("is not the verified 1.17.32 manifest", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "out", "offline.apk")));

            // A Crashlytics/Fabric file missing, or one more of those kinds: another build, refused.
            string missing = TestEnvironment.NewTempDir("debloat-missing");
            ex = Assert.Throws<InvalidDataException>(() => RunSynthetic(missing, SignerChoice.BuiltIn, out _, patches: "debloat",
                                                                        apkBytes: SyntheticApk(debloatLike: true, withoutEntry: "fabric/com.crashlytics.sdk.android.beta.properties")));
            Assert.Contains("not the verified 1.17.32 set, nothing was written: missing fabric/com.crashlytics.sdk.android.beta.properties", ex.Message);
            string extra = TestEnvironment.NewTempDir("debloat-extra");
            var more = ZipArchiveReader.Read(SyntheticApk(debloatLike: true)).Entries.ToList();
            more.Add(ZipEntry.CreateDeflated("lib/x86/libcrashlytics.so", new byte[10], null));
            ex = Assert.Throws<InvalidDataException>(() => RunSynthetic(extra, SignerChoice.BuiltIn, out _, patches: "debloat", apkBytes: ZipArchiveWriter.Write(more)));
            Assert.Contains("unexpected lib/x86/libcrashlytics.so", ex.Message);

            // Without debloat the same APKs patch fine and keep those files.
            PatchReport plain = RunSynthetic(TestEnvironment.NewTempDir("debloat-off"), SignerChoice.BuiltIn, out string kept, allAbis: true,
                                             apkBytes: SyntheticApk(debloatLike: true));
            Assert.All(ApkPatcher.DebloatDroppedEntries.Keys, name => Assert.NotNull(ZipArchiveReader.Read(File.ReadAllBytes(kept)).Find(name)));
            Assert.DoesNotContain(plain.Notes, n => n.StartsWith("manifest: removed", StringComparison.Ordinal));
        }

        [Fact]
        public void DebloatEntryKinds()
        {
            Assert.True(ApkPatcher.IsDebloatEntry("lib/arm64-v8a/libcrashlytics.so"));
            Assert.True(ApkPatcher.IsDebloatEntry("lib/x86/libcrashlytics.so"));
            Assert.True(ApkPatcher.IsDebloatEntry("fabric/io.fabric.sdk.android.fabric.properties"));
            Assert.True(ApkPatcher.IsDebloatEntry("assets/crashlytics-build.properties"));
            Assert.False(ApkPatcher.IsDebloatEntry("lib/arm64-v8a/sub/libcrashlytics.so"));
            Assert.False(ApkPatcher.IsDebloatEntry("lib/libcrashlytics.so"));
            Assert.False(ApkPatcher.IsDebloatEntry("fabric/sub/x.properties"));
            Assert.False(ApkPatcher.IsDebloatEntry("fabric/README.txt"));
            Assert.False(ApkPatcher.IsDebloatEntry("assets/other.properties"));
            Assert.False(ApkPatcher.IsDebloatEntry("lib/arm64-v8a/libaot-BindCrashlytics.dll.so"));
            Assert.Equal(9, ApkPatcher.DebloatDroppedEntries.Count);
            Assert.All(ApkPatcher.DebloatDroppedEntries.Keys, k => Assert.True(ApkPatcher.IsDebloatEntry(k), k));
        }

        [Fact]
        public void BuiltInSignatureVerifiesWithApksigner()
        {
            BuildTools tools = Tools();
            if (tools.ApkSigner == null)
            {
                output.WriteLine("apksigner not found (set MCVS_BUILD_TOOLS): skipped");
                return;
            }
            string dir = TestEnvironment.NewTempDir("builtin");
            RunSynthetic(dir, SignerChoice.BuiltIn, out string outApk);
            string text = ExternalApkSigner.Verify(tools.ApkSigner, outApk);
            output.WriteLine(text);
            Assert.Contains("Verified using v1 scheme (JAR signing): true", text);
            Assert.Contains("Verified using v2 scheme (APK Signature Scheme v2): true", text);
        }

        [Fact]
        public void ApksignerSignsWithTheGeneratedKey()
        {
            BuildTools tools = Tools();
            if (tools.ApkSigner == null)
            {
                output.WriteLine("apksigner not found (set MCVS_BUILD_TOOLS): skipped");
                return;
            }
            string dir = TestEnvironment.NewTempDir("apksigner");
            PatchReport report = RunSynthetic(dir, SignerChoice.ApkSigner, out string outApk);
            Assert.StartsWith("apksigner", report.Signer);
            string text = ExternalApkSigner.Verify(tools.ApkSigner, outApk);
            Assert.Contains("Verified using v3 scheme (APK Signature Scheme v3): true", text);

            // The same key signs again (updates must keep the signer).
            PatchReport second = RunSynthetic(dir, SignerChoice.BuiltIn, out _);
            Assert.False(second.KeystoreCreated);
        }

        [Fact]
        public void RefusesAnAlreadyPatchedApkAndANet8Server()
        {
            string dir = TestEnvironment.NewTempDir("refuse");
            RunSynthetic(dir, SignerChoice.BuiltIn, out string outApk);
            var again = new PatcherOptions
            {
                InputApk = outApk, ServerDll = Path.Combine(dir, "McvsOffline.Server.dll"), OutputApk = Path.Combine(dir, "x.apk"),
                Force = true, SkipServerReferenceCheck = true,
            };
            Assert.Contains("already contains", Assert.Throws<InvalidDataException>(() => ApkPatcher.Run(again, null)).Message);

            var net8Server = new PatcherOptions
            {
                InputApk = Path.Combine(dir, "in.apk"), ServerDll = Path.Combine(dir, "McvsOffline.Server.dll"), OutputApk = Path.Combine(dir, "y.apk"),
                Force = true,
            };
            Assert.Contains("not the embedded build", Assert.Throws<InvalidDataException>(() => ApkPatcher.Run(net8Server, null)).Message);
        }

        [Fact]
        public void RefusesAnUnknownBuildWithoutForce()
        {
            string dir = TestEnvironment.NewTempDir("unknown");
            File.WriteAllBytes(Path.Combine(dir, "in.apk"), SyntheticApk());
            var options = new PatcherOptions
            {
                InputApk = Path.Combine(dir, "in.apk"), ServerDll = Path.Combine(dir, "s.dll"), OutputApk = Path.Combine(dir, "o.apk"),
            };
            Assert.Contains("not the Modern Combat Versus", Assert.Throws<InvalidDataException>(() => ApkPatcher.Run(options, null)).Message);
        }

        [Fact]
        public void DerivedMvidsDependOnInputs()
        {
            Guid a = Guid.NewGuid(), s = Guid.NewGuid();
            Assert.Equal(ApkPatcher.DerivedMvid(a, new[] { "hook" }, s), ApkPatcher.DerivedMvid(a, new[] { "hook" }, s));
            Assert.NotEqual(ApkPatcher.DerivedMvid(a, new[] { "hook" }, s), ApkPatcher.DerivedMvid(a, new[] { "hook", "geo" }, s));
            Assert.NotEqual(ApkPatcher.DerivedMvid(a, new[] { "hook" }, s), ApkPatcher.DerivedMvid(a, new[] { "hook" }, Guid.NewGuid()));
        }

        [Fact]
        public void CommandLineParsing()
        {
            PatcherOptions o = McvsOffline.Patcher.Program.Parse(new[]
            {
                "--apk", "a.apk", "--server", "s.dll", "--out", "o.apk", "--patch", "geo,lan", "--port", "52000",
                "--ks-pass", "pass:secret", "--signer", "builtin", "--strip-aot", "all",
            });
            Assert.Equal(new[] { "finder", "geo", "lan" }, o.Patches);
            Assert.Equal(52000, o.Port);
            Assert.Equal("secret", o.KeystorePassword);
            Assert.Equal(SignerChoice.BuiltIn, o.Signer);
            Assert.Equal(AotStripMode.All, o.StripAot);
            PatcherOptions defaults = McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s", "--out", "o" });
            Assert.Equal(new[] { "bottile", "debloat", "finder", "geo", "nativeonline", "signatures", "store", "waits" }, defaults.Patches);
            Assert.Equal(GameLogMode.All, defaults.GameLog);
            Assert.Equal(GameLogMode.Fatal, McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s", "--out", "o", "--game-log", "fatal" }).GameLog);
            Assert.Throws<ArgumentException>(() => McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s", "--out", "o", "--game-log", "debug" }));
            Assert.Null(McvsOffline.Patcher.Program.Parse(new[] { "--help" }));
            Assert.Throws<ArgumentException>(() => McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s" }));
            Assert.Throws<ArgumentException>(() => McvsOffline.Patcher.Program.Parse(new[] { "--apk", "a", "--server", "s", "--out", "o", "--port", "0" }));
        }
    }
}
