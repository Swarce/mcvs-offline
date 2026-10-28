using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embedded.Hd;
using McvsOffline.Embedded.Install;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The HD texture installer (hosts/embedded/Hd/HdInstaller.cs, docs/design/hd-textures.md): a pack's files replace the
    /// originals in Data/, and the installer's index, its saved copy and the journal list them, so the installer's own checks
    /// (every listed file with its size, every split piece in the journal; the CRCs our background check reads) accept them
    /// without SKIP_VALIDATION. Each test runs the host's order: HdInstaller.Prepare, InstallerData.Prepare, HdInstaller.Apply.
    /// </summary>
    [Collection("HdInstaller")]
    public class HdInstallerTests
    {
        private const int Split = PackIndex.SplitSize;
        private readonly ITestOutputHelper output;

        public HdInstallerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // ------------------------------------------------------------------ fixture

        /// <summary>A small installed data pack: whole files, a split file, two "lightmaps" of one level.</summary>
        private static readonly (string path, int size)[] PackFiles =
        {
            ("Data/MCVS.game", 790),
            ("Data/1/0018344.f", 4893),
            ("Data/8/dbd2f1.f", 43301),
            ("Data/a/4309cd.f", 2 * Split + 12345),
            ("Data/b/5aa.f", 3000),
            ("Data/b/5ab.f", 5000),
        };

        /// <summary>The HD pack: a whole file that grows, one that becomes split, a split file with more pieces, a two-file group.</summary>
        private static readonly (string path, int size, string group)[] HdFiles =
        {
            ("Data/8/dbd2f1.f", 160000, null),
            ("Data/1/0018344.f", 2 * Split + 777, null),
            ("Data/a/4309cd.f", 4 * Split + 99, null),
            ("Data/b/5aa.f", 12000, "lightmaps/Port"),
            ("Data/b/5ab.f", 20000, "lightmaps/Port"),
        };

        private sealed class Env
        {
            public string Root;
            public string Internal;
            public InstallerDataLocations Loc;
            public Logger Logger = new Logger();
            public MemoryLogSink Lines = new MemoryLogSink(LogLevel.Debug, 4000);
            public Dictionary<string, object> Prefs = new Dictionary<string, object>();
            public string Pack;

            public Env(string name)
            {
                string dir = TestEnvironment.NewTempDir(name);
                Root = Path.Combine(dir, "external", "files");
                Internal = Path.Combine(dir, "internal", "files");
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(Internal);
                Loc = new InstallerDataLocations(Internal, Root);
                Logger.AddSink(Lines);
            }

            public string Text { get { return string.Join("\n", Lines.Snapshot().Select(e => e.Message)); } }

            public string P(string rel) { return Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)); }

            public byte[] Read(string rel) { return File.ReadAllBytes(P(rel)); }
        }

        private sealed class MapPrefs : IInstallerPrefs
        {
            private readonly Dictionary<string, object> map;

            public MapPrefs(Dictionary<string, object> map) { this.map = map; }

            public string GetString(string key) => map.TryGetValue(key, out object v) ? v as string : null;

            public int GetInt(string key, int defaultValue) => map.TryGetValue(key, out object v) && v is int i ? i : defaultValue;

            public bool SetString(string key, string value) { map[key] = value; return true; }

            public bool SetInt(string key, int value) { map[key] = value; return true; }
        }

        private static byte[] Bytes(int size, int seed)
        {
            var data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = (byte)(seed * 31 + i * 7 + (i >> 11));
            return data;
        }

        private static void Write(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        /// <summary>The data pack installed by the game's installer: files, its index (Gameloft's form) with gl_ver, and the journal.</summary>
        private static Env Installed(string name, bool withPack = true)
        {
            var env = new Env(name);
            for (int i = 0; i < PackFiles.Length; i++) Write(env.P(PackFiles[i].path), Bytes(PackFiles[i].size, i + 1));
            byte[] index = GameloftIndex(env.Root, PackFiles.Select(f => f.path));
            File.WriteAllBytes(env.Loc.PackInfo, index);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, InstallerData.ApkVersionCode);
            var journal = PackIndex.Parse(index).Entries.Where(e => e.Piece > 0).Select(e => e.JournalKey + "\n");
            File.WriteAllText(env.Loc.Journal, string.Concat(journal));
            if (withPack) env.Pack = WritePack(env, HdFiles, null);
            return env;
        }

        private static long PristineTotal => PackFiles.Sum(f => (long)f.size);

        private static InstallerDataOptions DataOptions(bool rebuild = true)
        {
            return new InstallerDataOptions
            {
                RebuildIndex = rebuild, VerifyRestoredData = false,
                KnownPacks = new[] { new InstallerData.KnownPack("ETC", PackFiles.Length, PristineTotal) },
            };
        }

        private static byte[] HdBytes(string path)
        {
            int i = Array.FindIndex(HdFiles, f => f.path == path);
            return Bytes(HdFiles[i].size, 100 + i);
        }

        private static byte[] Original(string path)
        {
            int i = Array.FindIndex(PackFiles, f => f.path == path);
            return Bytes(PackFiles[i].size, i + 1);
        }

        /// <summary>Writes McvsOffline/hd/&lt;name&gt; with the given files (originals as installed, HD bytes from <see cref="HdBytes"/>).</summary>
        private static string WritePack((string path, int size, string group)[] files, Env env, string fileName = "mcvs-hd-test.zip",
                                        Action<Dictionary<string, object>> tweak = null, Func<string, byte[]> original = null)
        {
            var manifestFiles = new List<object>();
            var zip = new TestZip();
            foreach (var f in files)
            {
                byte[] orig = (original ?? Original)(f.path);
                byte[] hd = HdBytes(f.path);
                var entry = new Dictionary<string, object>
                {
                    ["path"] = f.path,
                    ["original"] = new Dictionary<string, object> { ["size"] = (long)orig.Length, ["crc32"] = Crc32.Update(0, orig, 0, orig.Length).ToString("x8") },
                    ["hd"] = new Dictionary<string, object> { ["size"] = (long)hd.Length, ["crc32"] = Crc32.Update(0, hd, 0, hd.Length).ToString("x8") },
                };
                if (f.group != null) entry["group"] = f.group;
                manifestFiles.Add(entry);
            }
            var manifest = new Dictionary<string, object>
            {
                ["format"] = "mcvs-hd", ["version"] = 1L, ["id"] = "test-1", ["name"] = "Test HD pack", ["pack"] = "ETC", ["gameVersion"] = "1.17.32",
                ["files"] = manifestFiles,
            };
            tweak?.Invoke(manifest);
            zip.Add(HdPack.ManifestEntry, Encoding.UTF8.GetBytes(McvsOffline.Server.Core.Json.MiniJson.Serialize(manifest, true)));
            foreach (var f in files) zip.Add(f.path, HdBytes(f.path));
            string path = Path.Combine(env.Root, "McvsOffline", "hd", fileName);
            Write(path, zip.ToArray());
            return path;
        }

        private static string WritePack(Env env, (string path, int size, string group)[] files, Action<Dictionary<string, object>> tweak)
        {
            return WritePack(files, env, "mcvs-hd-test.zip", tweak);
        }

        /// <summary>One start of the game as the host runs it (EmbeddedHost.PrepareInstallerData).</summary>
        private static (InstallerDataResult data, HdResult hd) Boot(Env env, bool enabled = true, bool rebuild = true, int budgetMs = 60000,
                                                                     Action<string> crash = null)
        {
            Log log = env.Logger.For("hd");
            var hdOptions = new HdInstallerOptions { Enabled = enabled, BudgetMs = budgetMs, Checkpoint = crash };
            InstallerDataOptions options = DataOptions(rebuild);
            options.HdOriginalSizes = HdInstaller.Prepare(env.Loc, hdOptions, log).OriginalSizes;
            InstallerDataResult data = InstallerData.Prepare(env.Loc, new MapPrefs(env.Prefs), options, env.Logger.For("install"), 1700000000);
            HdResult hd = HdInstaller.Apply(env.Loc, hdOptions, log);
            return (data, hd);
        }

        /// <summary>
        /// What the game's installer checks at a start (DownloadComponent with its index): gl_ver, every entry's file with its size,
        /// every piece of a split file in the journal; plus the CRC32 of every entry (what our background check reads, and what the
        /// installer's own CRC pass would read).
        /// </summary>
        private static PackIndex AssertInstallerAccepts(Env env)
        {
            Assert.Equal(InstallerData.ApkVersionCode, File.ReadAllText(env.Loc.GlVer));
            PackIndex index = PackIndex.Parse(File.ReadAllBytes(env.Loc.PackInfo));
            DataCheck check = InstallerData.Check(index, env.Root);
            Assert.True(check.Complete, check.Describe());
            var journal = new HashSet<string>(File.ReadAllText(env.Loc.Journal).Split('\n').Where(l => l.Length > 0));
            foreach (PackEntry e in index.Entries.Where(e => e.Piece > 0)) Assert.Contains(e.JournalKey, journal);
            foreach (PackEntry e in index.Entries)
            {
                if (index.IsRebuilt) Assert.Equal(0, e.Crc);
                else
                {
                    byte[] all = File.ReadAllBytes(Path.Combine(env.Root, e.RelativePath));
                    int start = e.Piece == 0 ? 0 : (e.Piece - 1) * Split;
                    Assert.Equal((uint)e.Crc, Crc32.Update(0, all, start, e.Size));
                }
            }
            // The background CRC check finds nothing to delete.
            Assert.False(File.Exists(env.Loc.DamagedList));
            return index;
        }

        private static void AssertHdInstalled(Env env, IEnumerable<string> paths = null)
        {
            foreach (string path in paths ?? HdFiles.Select(f => f.path))
            {
                Assert.Equal(HdBytes(path), env.Read(path));
                Assert.Equal(Original(path), File.ReadAllBytes(HdInstaller.BackupPath(env.Loc, path)));
            }
        }

        private static void AssertOriginals(Env env)
        {
            foreach (var f in PackFiles) Assert.Equal(Original(f.path), env.Read(f.path));
        }

        private static void AssertNoTemps(Env env)
        {
            Assert.Empty(Directory.GetFiles(env.Root, "*" + HdInstaller.TempSuffix, SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(env.Root, "*.mcvs-tmp", SearchOption.AllDirectories));
        }

        private sealed class Crash : Exception
        {
            public Crash(string point) : base("simulated crash at " + point) { }
        }

        // ------------------------------------------------------------------ install, second start, removal

        [Fact]
        public void APackIsInstalledAndTheInstallersChecksAcceptItWithoutSkipValidation()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-install");
            byte[] pristineIndex = File.ReadAllBytes(env.Loc.PackInfo);

            var (data, hd) = Boot(env);
            output.WriteLine(data.Summary);
            output.WriteLine(hd.Summary);
            output.WriteLine(env.Text);
            Assert.Equal("installer data: Gameloft index in place (6 files, " + PackIndex.Parse(pristineIndex).Entries.Count + " entries)", data.Summary);
            Assert.StartsWith("hd textures: Test HD pack, 5 files installed (", hd.Summary);
            Assert.Contains("5 written now", hd.Summary);
            Assert.True(hd.IndexChanged);
            Assert.True(hd.Installed);
            Assert.True(HdInstaller.Installed);
            Assert.Equal(hd.Summary, HdInstaller.Status);
            Assert.False(File.Exists(Path.Combine(env.Root, "qaTestingConfigs.txt")));

            AssertHdInstalled(env);
            PackIndex index = AssertInstallerAccepts(env);
            Assert.False(index.IsRebuilt);
            // Only the five files changed in the index; every other entry is byte-for-byte Gameloft's.
            PackIndex before = PackIndex.Parse(pristineIndex);
            var hdPaths = new HashSet<string>(HdFiles.Select(f => f.path));
            Assert.Equal(before.Entries.Where(e => !hdPaths.Contains(e.RelativePath)).Select(Key),
                         index.Entries.Where(e => !hdPaths.Contains(e.RelativePath)).Select(Key));
            Assert.Equal(new[] { 1, 2, 3 }, index.EntriesOf("Data/1/0018344.f").Select(e => e.Piece));   // whole -> 3 pieces
            Assert.Equal(5, index.EntriesOf("Data/a/4309cd.f").Count);                                   // 3 -> 5 pieces
            Assert.Equal(4L * Split + 99, index.EntriesOf("Data/a/4309cd.f")[0].TotalSize);
            // The saved copy follows, so a reinstall gives the installer an index that matches the HD files.
            Assert.Equal(File.ReadAllBytes(env.Loc.PackInfo), File.ReadAllBytes(env.Loc.SavedIndex));
            AssertNoTemps(env);

            // Second start: nothing to do, nothing written.
            byte[] indexAfter = File.ReadAllBytes(env.Loc.PackInfo);
            string journalAfter = File.ReadAllText(env.Loc.Journal);
            DateTime stamp = File.GetLastWriteTimeUtc(env.P("Data/8/dbd2f1.f"));
            var (data2, hd2) = Boot(env);
            output.WriteLine(hd2.Summary);
            Assert.StartsWith("hd textures: Test HD pack, 5 files installed (", hd2.Summary);
            Assert.DoesNotContain("written now", hd2.Summary);
            Assert.False(hd2.IndexChanged);
            Assert.Equal(indexAfter, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(journalAfter, File.ReadAllText(env.Loc.Journal));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(env.P("Data/8/dbd2f1.f")));
            Assert.StartsWith("installer data: Gameloft index in place (6 files", data2.Summary);
        }

        private static string Key(PackEntry e)
        {
            return e.Folder + "|" + e.Name + "|" + e.Size + "|" + e.Crc + "|" + e.Offset + "|" + e.ZipSize + "|" + e.ZipName + "|" + e.TotalSize;
        }

        [Fact]
        public void TurningItOffRestoresByteIdenticalOriginalsAndTheIndex()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-remove");
            Boot(env, rebuild: false);   // gives the saved copy its first version
            HdInstaller.Apply(env.Loc, new HdInstallerOptions { Enabled = false }, env.Logger.For("hd"));
            // The pristine state, as the installer and InstallerData leave it.
            byte[] pristineIndex = File.ReadAllBytes(env.Loc.PackInfo);
            byte[] pristineSaved = File.ReadAllBytes(env.Loc.SavedIndex);
            var pristineJournal = new HashSet<string>(File.ReadAllLines(env.Loc.Journal));

            var (_, on) = Boot(env);
            Assert.True(on.Installed);
            AssertHdInstalled(env);

            var (_, off) = Boot(env, enabled: false);
            output.WriteLine(off.Summary);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back", off.Summary);
            Assert.False(off.Installed);
            Assert.False(HdInstaller.Installed);
            AssertOriginals(env);
            Assert.Equal(pristineIndex, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(pristineSaved, File.ReadAllBytes(env.Loc.SavedIndex));
            Assert.Equal(pristineJournal, new HashSet<string>(File.ReadAllLines(env.Loc.Journal)));
            Assert.False(File.Exists(Path.Combine(HdInstaller.HdDir(env.Loc), HdInstaller.StateFile)));
            Assert.False(Directory.Exists(Path.Combine(HdInstaller.HdDir(env.Loc), HdInstaller.BackupFolder)));
            Assert.True(File.Exists(env.Pack));   // the pack stays for later
            AssertInstallerAccepts(env);
            AssertNoTemps(env);

            // Off again: nothing to do. On again: installed again from the pack.
            Assert.Equal("hd textures: off (settings hd.enabled false)", Boot(env, enabled: false).hd.Summary);
            Assert.True(Boot(env).hd.Installed);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
        }

        [Fact]
        public void APackWithFewerFilesGivesBackTheOnesItNoLongerHas()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-repack");
            Boot(env);
            File.Delete(env.Pack);
            WritePack(env, HdFiles.Take(2).ToArray(), m => m["id"] = "test-2");
            var (_, hd) = Boot(env);
            output.WriteLine(hd.Summary);
            Assert.Contains("2 files installed", hd.Summary);
            Assert.Contains("3 originals put back", hd.Summary);
            AssertHdInstalled(env, HdFiles.Take(2).Select(f => f.path));
            foreach (var f in HdFiles.Skip(2)) Assert.Equal(Original(f.path), env.Read(f.path));
            AssertInstallerAccepts(env);
        }

        [Fact]
        public void WithoutThePackFileTheInstalledFilesStayAndALostOneGetsItsOriginalBack()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-nopack");
            Boot(env);
            File.Delete(env.Pack);
            File.Delete(env.P("Data/8/dbd2f1.f"));
            var (_, hd) = Boot(env);
            output.WriteLine(hd.Summary);
            Assert.Contains("4 files installed", hd.Summary);
            Assert.Contains("the pack file is no longer in McvsOffline/hd", hd.Summary);
            Assert.Equal(Original("Data/8/dbd2f1.f"), env.Read("Data/8/dbd2f1.f"));
            AssertInstallerAccepts(env);
        }

        // ------------------------------------------------------------------ crashes

        public static IEnumerable<object[]> CrashPoints()
        {
            yield return new object[] { "recorded" };
            yield return new object[] { "written:Data/8/dbd2f1.f" };
            yield return new object[] { "written:Data/1/0018344.f" };
            yield return new object[] { "written:Data/b/5aa.f" };   // half of the lightmap group
            yield return new object[] { "commit" };
        }

        [Theory]
        [MemberData(nameof(CrashPoints))]
        public void AStartCutShortWhileInstallingIsCompletedByTheNextOne(string point)
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-crash");
            var (_, crashed) = Boot(env, crash: p => { if (p == point) throw new Crash(p); });
            output.WriteLine(crashed.Summary);
            Assert.StartsWith("hd textures: failed (Crash: simulated crash at " + point, crashed.Summary);
            // Leave a temporary file behind as a process killed in the middle of a copy would.
            File.WriteAllBytes(HdInstaller.TempFor(env.P("Data/a/4309cd.f")), new byte[123]);

            // What the game's installer would see if it ran now is not our concern: our step always runs first. The next start
            // brings the index in line before InstallerData looks at it, then finishes the pack.
            var (data, hd) = Boot(env);
            output.WriteLine(data.Summary);
            output.WriteLine(hd.Summary);
            Assert.StartsWith("hd textures: Test HD pack, 5 files installed", hd.Summary);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
            AssertNoTemps(env);
            Assert.Equal(File.ReadAllBytes(env.Loc.PackInfo), File.ReadAllBytes(env.Loc.SavedIndex));
        }

        [Fact]
        public void AStartCutShortWhileRemovingIsCompletedByTheNextOne()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-crash-remove");
            Boot(env);
            var (_, crashed) = Boot(env, enabled: false, crash: p => { if (p == "restored:Data/1/0018344.f") throw new Crash(p); });
            Assert.StartsWith("hd textures: failed", crashed.Summary);
            var (_, off) = Boot(env, enabled: false);
            output.WriteLine(off.Summary);
            Assert.False(off.Installed);
            AssertOriginals(env);
            AssertInstallerAccepts(env);
            AssertNoTemps(env);
        }

        [Fact]
        public void TheBudgetSpreadsTheInstallOverStartsAndEveryStartIsConsistent()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-budget");
            int starts = 0;
            HdResult hd;
            do
            {
                hd = Boot(env, budgetMs: -1).hd;
                starts++;
                output.WriteLine(hd.Summary);
                AssertInstallerAccepts(env);
                if (starts == 1) Assert.Contains("files at the next start", hd.Summary);
            }
            while (hd.Summary.Contains("at the next start") && starts < 10);
            Assert.Equal(4, starts);   // one group per start: three single files and the lightmap pair
            AssertHdInstalled(env);
        }

        // ------------------------------------------------------------------ the index reset by the game or a reinstall

        [Fact]
        public void AReinstallGivesTheInstallerTheSavedIndexWithTheHdFiles()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-reinstall");
            Boot(env);
            // Uninstall + install: Android wipes internal storage (pack.info, prefs/gl_ver).
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            var (data, hd) = Boot(env);
            output.WriteLine(data.Summary);
            output.WriteLine(hd.Summary);
            Assert.StartsWith("installer data: index restored from the saved index (6 files", data.Summary);
            Assert.Equal("ETC", env.Prefs[InstallerData.DataTypeKey]);   // the variant is recognised by the original sizes
            Assert.DoesNotContain("written now", hd.Summary);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
        }

        [Fact]
        public void AGlVerMismatchIsAResetToo()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-glver");
            Boot(env);
            File.WriteAllText(env.Loc.GlVer, "1173100");
            var (data, hd) = Boot(env);
            output.WriteLine(data.Summary);
            Assert.StartsWith("installer data: index restored from the saved index", data.Summary);
            Assert.True(hd.Installed);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
        }

        [Fact]
        public void WithNoIndexAnywhereTheDataIsStillRecognisedAndRebuiltWithTheHdSizes()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-rebuild");
            Boot(env);
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            File.Delete(env.Loc.SavedIndex);
            var (data, hd) = Boot(env);
            output.WriteLine(data.Summary);
            output.WriteLine(hd.Summary);
            Assert.StartsWith("installer data: index rebuilt from the complete ETC data", data.Summary);
            Assert.True(hd.Installed);
            PackIndex index = AssertInstallerAccepts(env);
            Assert.True(index.IsRebuilt);
            AssertHdInstalled(env);

            // Removal on a rebuilt index gives the rebuilt index of the originals.
            Boot(env, enabled: false);
            AssertOriginals(env);
            index = AssertInstallerAccepts(env);
            var expected = PackIndex.Rebuild(PackFiles.Select(f => new PackIndex.FileInfoEntry(f.path, f.size)).ToList());
            Assert.Equal(expected.Raw, index.Raw);
        }

        [Fact]
        public void TheGamesOwnIndexOverAnInstalledPackIsMadeToMatchBeforeTheInstallerRuns()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-cdnindex");
            byte[] pristine = File.ReadAllBytes(env.Loc.PackInfo);
            Boot(env);
            // The installer re-synced with the CDN (Gameloft's index again), deleted one HD file and fetched its original.
            File.WriteAllBytes(env.Loc.PackInfo, pristine);
            File.WriteAllBytes(env.P("Data/8/dbd2f1.f"), Original("Data/8/dbd2f1.f"));
            var (_, hd) = Boot(env);
            output.WriteLine(env.Text);
            Assert.Contains("the installer's index listed 4 of the HD pack's files with other sizes", env.Text);
            Assert.Contains("5 files installed", hd.Summary);
            Assert.Contains("1 written now", hd.Summary);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
        }

        [Fact]
        public void WithoutAUsableIndexTheOriginalsGoBackAndThePackReturnsWithTheIndex()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-noindex");
            Boot(env);
            byte[] saved = File.ReadAllBytes(env.Loc.SavedIndex);
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            File.Delete(env.Loc.SavedIndex);
            var (data, hd) = Boot(env, rebuild: false);
            output.WriteLine(data.Summary);
            output.WriteLine(hd.Summary);
            Assert.Equal("installer data: left to the game's installer (no usable index)", data.Summary);
            Assert.StartsWith("hd textures: not installed now: the game's installer has no usable index", hd.Summary);
            Assert.False(hd.Installed);
            AssertOriginals(env);   // what Gameloft's CDN index expects, so the installer downloads nothing

            // The player copies the saved index back (it lists the HD sizes: our state brings it in line with the originals first).
            Write(env.Loc.SavedIndex, saved);
            var (data2, hd2) = Boot(env, rebuild: false);
            output.WriteLine(data2.Summary);
            output.WriteLine(hd2.Summary);
            Assert.StartsWith("installer data: index restored from the saved index", data2.Summary);
            Assert.True(hd2.Installed);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
        }

        // ------------------------------------------------------------------ packs that do not fit

        [Fact]
        public void APackMadeFromOtherOriginalsChangesNothing()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-wrongorig", withPack: false);
            byte[] index = File.ReadAllBytes(env.Loc.PackInfo);
            WritePack(HdFiles, env, original: p => Bytes(Original(p).Length, 77));
            var (_, hd) = Boot(env);
            output.WriteLine(hd.Summary);
            Assert.False(hd.Installed);
            Assert.Contains("none installed", hd.Summary);
            Assert.Contains("5 skipped", hd.Summary);
            Assert.Contains("is not the original the pack was made from", hd.Summary);
            AssertOriginals(env);
            Assert.Equal(index, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.False(File.Exists(Path.Combine(HdInstaller.HdDir(env.Loc), HdInstaller.StateFile)));
            AssertNoTemps(env);
        }

        [Fact]
        public void ALightmapGroupIsInstalledWhollyOrNotAtAll()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-group", withPack: false);
            WritePack(HdFiles, env, original: p => p == "Data/b/5ab.f" ? Bytes(5000, 9) : Original(p));
            var (_, hd) = Boot(env);
            output.WriteLine(hd.Summary);
            Assert.Contains("3 files installed", hd.Summary);
            Assert.Contains("2 skipped", hd.Summary);
            Assert.Contains("its group 'lightmaps/Port' of 2 files is skipped", hd.Summary);
            Assert.Equal(Original("Data/b/5aa.f"), env.Read("Data/b/5aa.f"));
            Assert.Equal(Original("Data/b/5ab.f"), env.Read("Data/b/5ab.f"));
            AssertInstallerAccepts(env);
        }

        [Theory]
        [InlineData("format", "other", "format is not 'mcvs-hd'")]
        [InlineData("version", 2L, "version 2")]
        [InlineData("files", null, "lists no files")]
        public void ABrokenManifestIsReportedAndNothingChanges(string key, object value, string reason)
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-badmanifest", withPack: false);
            WritePack(env, HdFiles, m => m[key] = value);
            var (_, hd) = Boot(env);
            output.WriteLine(hd.Summary);
            Assert.StartsWith("hd textures: none installed; mcvs-hd-test.zip cannot be used: ", hd.Summary);
            Assert.Contains(reason, hd.Summary);
            AssertOriginals(env);
        }

        [Theory]
        [InlineData("../Data/8/dbd2f1.f")]
        [InlineData("Data/../8/dbd2f1.f")]
        [InlineData("Data/8/x/dbd2f1.f")]
        [InlineData("Data\\8\\dbd2f1.f")]
        [InlineData("Data/8/.dbd2f1.f")]
        [InlineData("McvsOffline/settings.json")]
        public void OnlyPlainDataPathsAreAccepted(string path)
        {
            Assert.False(HdPack.IsDataPath(path));
            Assert.True(HdPack.IsDataPath("Data/8/dbd2f1.f"));
        }

        [Fact]
        public void NoPackAndNothingInstalledIsQuiet()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-none", withPack: false);
            byte[] index = File.ReadAllBytes(env.Loc.PackInfo);
            var (_, hd) = Boot(env);
            Assert.Equal("hd textures: none (no McvsOffline/hd/mcvs-hd-*.zip)", hd.Summary);
            Assert.False(hd.IndexChanged);
            Assert.Equal(index, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.DoesNotContain("WARN", string.Join("\n", env.Lines.Snapshot().Where(e => e.Level >= LogLevel.Warn).Select(e => e.Message)));
        }

        [Fact]
        public void ManageInstallerFalseLeavesEverythingAlone()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-unmanaged");
            byte[] index = File.ReadAllBytes(env.Loc.PackInfo);
            HdResult hd = HdInstaller.Apply(env.Loc, new HdInstallerOptions { ManageInstaller = false }, env.Logger.For("hd"));
            Assert.Equal("hd textures: not managed (install.manageInstaller false)", hd.Summary);
            AssertOriginals(env);
            Assert.Equal(index, File.ReadAllBytes(env.Loc.PackInfo));
        }

        // ------------------------------------------------------------------ index writing

        [Fact]
        public void EntriesForFollowGameloftsLayout()
        {
            Assert.Equal(new[] { 100 }, PackIndex.PieceSizes(100));
            Assert.Equal(new[] { Split }, PackIndex.PieceSizes(Split));
            Assert.Equal(new[] { Split, 1 }, PackIndex.PieceSizes(Split + 1));
            Assert.Equal(new[] { 2 * Split }, PackIndex.PieceSizes(2L * Split));   // an exact multiple stays whole (goodSize)
            List<PackEntry> e = PackIndex.EntriesFor("Data/1/0084423.f", Split + 5, new uint[] { 7, 8 }, false, null);
            Assert.Equal(new[] { "0084423.f.split_0001", "0084423.f.split_0002" }, e.Select(x => x.Name));
            Assert.All(e, x => Assert.Equal(".\\\\Data\\1", x.Folder));
            Assert.Equal(new long[] { 7, 8 }, e.Select(x => x.Crc));
            Assert.Equal(".\\\\Data\\10084423.f.split_0002", e[1].JournalKey);
            PackIndex written = PackIndex.Write(e);
            Assert.Equal(Split + 5L, written.LogicalBytes);
        }

        [Fact]
        public void TheRealIndexIsWrittenBackByteForByteAndARealFileCanBeSwappedAndRestored()
        {
            string data = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
            if (string.IsNullOrEmpty(data)) data = "/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data";
            string indexPath = Directory.Exists(data) ? Path.GetFullPath(Path.Combine(data, "..", "..", "pack.info")) : null;
            if (indexPath == null || !File.Exists(indexPath))
            {
                output.WriteLine("no real data pack with its pack.info (MCVS_DATAPACK): skipped");
                return;
            }
            byte[] raw = File.ReadAllBytes(indexPath);
            PackIndex real = PackIndex.Parse(raw);
            Assert.Equal(raw, real.Replace(null).Raw);
            Assert.Equal(raw, real.Replace(new Dictionary<string, List<PackEntry>> { ["Data/8/dbd2f1.f"] = real.EntriesOf("Data/8/dbd2f1.f") }).Raw);

            // A game root with the real index and two real files: the probe texture (ETC1, 43,301 bytes) and a split file.
            HdInstaller.ResetForTests();
            var env = new Env("hd-real");
            string[] paths = { "Data/8/dbd2f1.f", "Data/1/0084423.f" };
            foreach (string p in paths) Write(env.P(p), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(data), p)));
            File.WriteAllBytes(env.Loc.PackInfo, raw);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, InstallerData.ApkVersionCode);
            File.WriteAllText(env.Loc.Journal, string.Concat(real.Entries.Where(e => e.Piece > 0).Select(e => e.JournalKey + "\n")));
            var hdFiles = new (string, int, string)[] { (paths[0], 180000, null), (paths[1], 3 * Split + 4321, null) };
            var zip = new TestZip();
            var list = new List<object>();
            for (int i = 0; i < paths.Length; i++)
            {
                byte[] orig = env.Read(paths[i]);
                byte[] hd = Bytes(hdFiles[i].Item2, 50 + i);
                zip.Add(paths[i], hd);
                list.Add(new Dictionary<string, object>
                {
                    ["path"] = paths[i],
                    ["original"] = new Dictionary<string, object> { ["size"] = (long)orig.Length, ["crc32"] = Crc32.Update(0, orig, 0, orig.Length).ToString("x8") },
                    ["hd"] = new Dictionary<string, object> { ["size"] = (long)hd.Length, ["crc32"] = Crc32.Update(0, hd, 0, hd.Length).ToString("x8") },
                });
            }
            var manifest = new Dictionary<string, object> { ["format"] = "mcvs-hd", ["version"] = 1L, ["name"] = "real", ["pack"] = "ETC", ["files"] = list };
            zip.Add(HdPack.ManifestEntry, Encoding.UTF8.GetBytes(McvsOffline.Server.Core.Json.MiniJson.Serialize(manifest)));
            Write(Path.Combine(env.Root, "McvsOffline", "hd", "mcvs-hd-real.zip"), zip.ToArray());
            byte[] journal = File.ReadAllBytes(env.Loc.Journal);

            // The same pack labelled for the ASTC data pack: refused on this (ETC) index, nothing changes.
            manifest["pack"] = "ASTC";
            var astc = new TestZip();
            astc.Add(HdPack.ManifestEntry, Encoding.UTF8.GetBytes(McvsOffline.Server.Core.Json.MiniJson.Serialize(manifest)));
            for (int i = 0; i < paths.Length; i++) astc.Add(paths[i], Bytes(hdFiles[i].Item2, 50 + i));
            string packPath = Path.Combine(env.Root, "McvsOffline", "hd", "mcvs-hd-real.zip");
            byte[] etcPack = File.ReadAllBytes(packPath);
            File.WriteAllBytes(packPath, astc.ToArray());
            HdResult refused = HdInstaller.Apply(env.Loc, new HdInstallerOptions(), env.Logger.For("hd"));
            output.WriteLine(refused.Summary);
            Assert.Contains("was made for the ASTC data pack, this phone has the ETC pack", refused.Summary);
            Assert.False(refused.Installed);
            Assert.Equal(raw, File.ReadAllBytes(env.Loc.PackInfo));
            File.WriteAllBytes(packPath, etcPack);

            var hdOptions = new HdInstallerOptions();
            HdResult result = HdInstaller.Apply(env.Loc, hdOptions, env.Logger.For("hd"));
            output.WriteLine(result.Summary);
            Assert.True(result.Installed);
            PackIndex patched = PackIndex.Parse(File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(real.Entries.Count + 2, patched.Entries.Count);   // the split file: 2 pieces -> 4; the probe texture stays whole
            Assert.Equal("ETC", InstallerData.VariantOf(patched.LogicalFiles,
                InstallerData.PackBytes(patched, paths.ToDictionary(p => p, p => (long)File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(data), p)).Length))));
            foreach (string p in paths)
            {
                long len = new FileInfo(env.P(p)).Length;
                Assert.All(patched.EntriesOf(p), e => Assert.True(e.Piece == 0 ? e.Size == len : InstallerData.GoodSize(len, e.Piece, e.Size)));
            }

            HdResult off = HdInstaller.Apply(env.Loc, new HdInstallerOptions { Enabled = false }, env.Logger.For("hd"));
            output.WriteLine(off.Summary);
            Assert.Equal(raw, File.ReadAllBytes(env.Loc.PackInfo));   // Gameloft's index, byte for byte (offsets and ZIP sizes included)
            Assert.Equal(new HashSet<string>(Encoding.UTF8.GetString(journal).Split('\n')), new HashSet<string>(File.ReadAllText(env.Loc.Journal).Split('\n')));
            foreach (string p in paths) Assert.Equal(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(data), p)), env.Read(p));
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>An index in Gameloft's own form (real CRCs, increasing offsets, non-zero ZIP sizes) for the files under root.</summary>
        private static byte[] GameloftIndex(string root, IEnumerable<string> relPaths)
        {
            var entries = new List<PackEntry>();
            long offset = 0;
            foreach (string rel in relPaths.OrderBy(p => p.Substring(0, p.LastIndexOf('/')), StringComparer.Ordinal).ThenBy(p => p, StringComparer.Ordinal))
            {
                byte[] data = File.ReadAllBytes(Path.Combine(root, rel));
                int[] sizes = PackIndex.PieceSizes(data.Length);
                var crcs = new uint[sizes.Length];
                var like = new List<PackEntry>();
                for (int i = 0, start = 0; i < sizes.Length; start += sizes[i], i++)
                {
                    crcs[i] = Crc32.Update(0, data, start, sizes[i]);
                    // PackEntry's setters are internal to the embedded code compiled into this test assembly.
                    like.Add(new PackEntry { Offset = offset, ZipSize = sizes[i] / 2 + 40 });
                    offset += sizes[i] / 2 + 40;
                }
                entries.AddRange(PackIndex.EntriesFor(rel, data.Length, crcs, false, like));
            }
            return PackIndex.Write(entries).Raw;
        }

        /// <summary>A zip with stored entries (what tools/hd-textures writes).</summary>
        private sealed class TestZip
        {
            private readonly MemoryStream body = new MemoryStream();
            private readonly MemoryStream central = new MemoryStream();
            private int count;

            public void Add(string name, byte[] data)
            {
                byte[] n = Encoding.UTF8.GetBytes(name);
                uint crc = Crc32.Update(0, data, 0, data.Length);
                uint offset = (uint)body.Position;
                U32(body, 0x04034b50); U16(body, 10); U16(body, 0); U16(body, 0); U16(body, 0); U16(body, 0x21);
                U32(body, crc); U32(body, (uint)data.Length); U32(body, (uint)data.Length); U16(body, (ushort)n.Length); U16(body, 0);
                body.Write(n, 0, n.Length);
                body.Write(data, 0, data.Length);
                U32(central, 0x02014b50); U16(central, 20); U16(central, 10); U16(central, 0); U16(central, 0); U16(central, 0); U16(central, 0x21);
                U32(central, crc); U32(central, (uint)data.Length); U32(central, (uint)data.Length); U16(central, (ushort)n.Length);
                U16(central, 0); U16(central, 0); U16(central, 0); U16(central, 0); U32(central, 0); U32(central, offset);
                central.Write(n, 0, n.Length);
                count++;
            }

            public byte[] ToArray()
            {
                var all = new MemoryStream();
                body.Position = 0;
                body.CopyTo(all);
                uint cdOffset = (uint)all.Position;
                central.Position = 0;
                central.CopyTo(all);
                U32(all, 0x06054b50); U16(all, 0); U16(all, 0); U16(all, (ushort)count); U16(all, (ushort)count);
                U32(all, (uint)central.Length); U32(all, cdOffset); U16(all, 0);
                return all.ToArray();
            }

            private static void U16(Stream s, ushort v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

            private static void U32(Stream s, uint v) { U16(s, (ushort)v); U16(s, (ushort)(v >> 16)); }
        }
    }

    /// <summary>The installer's status is process-wide: its tests do not run in parallel with each other.</summary>
    [CollectionDefinition("HdInstaller", DisableParallelization = true)]
    public class HdInstallerCollection
    {
    }
}
