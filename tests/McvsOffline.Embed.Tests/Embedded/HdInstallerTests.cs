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
    /// The HD texture installer (hosts/embedded/Hd/HdInstaller.cs, docs/design/hd-install.md): a pack's files replace the
    /// originals in Data/, and the installer's index, its saved copy and the journal list them, so the installer's own checks
    /// (every listed file with its size, every split piece in the journal; the CRCs our background check reads) accept them
    /// without SKIP_VALIDATION. Each test runs the host's order: HdInstaller.Prepare, InstallerData.Prepare, HdInstaller.Apply.
    /// </summary>
    [Collection("HdInstaller")]
    public class HdInstallerTests : IDisposable
    {
        private const int Split = PackIndex.SplitSize;
        private readonly ITestOutputHelper output;

        public HdInstallerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>Each test leaves 10-20 MB of files; they go when it ends (the collection runs one test at a time).</summary>
        private static readonly List<string> Created = new List<string>();

        public void Dispose()
        {
            foreach (string dir in Created)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                }
            }
            Created.Clear();
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
            ("Data/1/0018344.f", Split + 777, null),
            ("Data/a/4309cd.f", 3 * Split + 99, null),
            ("Data/b/5aa.f", 12000, "lightmaps:Port"),
            ("Data/b/5ab.f", 20000, "lightmaps:Port"),
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

            /// <summary>Writes the installer tried inside McvsOffline/hd/ (every <see cref="Boot"/> treats it as read only and checks this stays empty).</summary>
            public List<string> DropWrites = new List<string>();

            public Env(string name)
            {
                string dir = TestEnvironment.NewTempDir(name);
                Created.Add(dir);
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

        /// <summary>
        /// Writes McvsOffline/hd/&lt;name&gt; with the given files (originals as installed, HD bytes from <see cref="HdBytes"/>), in the
        /// shape tools/hd-textures writes (docs/design/hd-textures.md §2-§3): aligned stored entries, the manifest last.
        /// </summary>
        private static string WritePack((string path, int size, string group)[] files, Env env, string fileName = "mcvs-hd-test.zip",
                                        Action<Dictionary<string, object>> tweak = null, Func<string, byte[]> original = null)
        {
            var list = new List<(string, byte[], byte[], string)>();
            foreach (var f in files) list.Add((f.path, (original ?? Original)(f.path), HdBytes(f.path), f.group));
            string path = Path.Combine(env.Root, "McvsOffline", "hd", fileName);
            Write(path, ConverterPack(list, tweak));
            return path;
        }

        /// <summary>A pack as tools/hd-textures' PackBuilder writes it, with every manifest field of docs/design/hd-textures.md §3.</summary>
        internal static byte[] ConverterPack(IEnumerable<(string path, byte[] original, byte[] replacement, string group)> files,
                                             Action<Dictionary<string, object>> tweak = null)
        {
            var manifestFiles = new List<object>();
            var zip = new TestZip(align: true);
            long originalBytes = 0, replacementBytes = 0;
            foreach (var f in files)
            {
                string[] parts = f.path.Split('/');
                manifestFiles.Add(new Dictionary<string, object>
                {
                    ["entry"] = f.path,
                    ["hash"] = (parts[1] + parts[2].Substring(0, parts[2].Length - 2)).PadLeft(8, '0'),
                    ["logicalPath"] = null,
                    ["category"] = f.group == null ? "surfaces" : "lightmaps",
                    ["levels"] = new List<object> { "Port" },
                    ["group"] = f.group,
                    ["layout"] = "ktx",
                    ["original"] = new Dictionary<string, object>
                    {
                        ["size"] = (long)f.original.Length, ["crc32"] = Crc32.Update(0, f.original, 0, f.original.Length).ToString("x8"),
                        ["width"] = 256L, ["height"] = 256L, ["mipLevels"] = 9L,
                    },
                    ["replacement"] = new Dictionary<string, object>
                    {
                        ["size"] = (long)f.replacement.Length, ["crc32"] = Crc32.Update(0, f.replacement, 0, f.replacement.Length).ToString("x8"),
                        ["width"] = 512L, ["height"] = 512L, ["mipLevels"] = 10L,
                    },
                    ["psnr"] = 31.5,
                    ["originalPsnr"] = 27.25,
                    ["alphaPsnr"] = null,
                });
                originalBytes += f.original.Length;
                replacementBytes += f.replacement.Length;
                zip.Add(f.path, f.replacement);
            }
            var manifest = new Dictionary<string, object>
            {
                ["format"] = 1L, ["kind"] = "hd-textures", ["tool"] = "McvsOffline.HdTextures 1", ["created"] = "2026-10-28T20:15:00Z",
                ["gameVersion"] = "1.17.32", ["pack"] = "ETC", ["scope"] = "default",
                ["categories"] = new List<object> { "surfaces", "lightmaps", "characters", "equipment" },
                ["maps"] = new List<object> { "Port" },
                ["files"] = manifestFiles,
                ["totals"] = new Dictionary<string, object> { ["files"] = (long)manifestFiles.Count, ["originalBytes"] = originalBytes, ["replacementBytes"] = replacementBytes },
            };
            tweak?.Invoke(manifest);
            zip.Add(HdPack.ManifestEntry, Encoding.UTF8.GetBytes(McvsOffline.Server.Core.Json.MiniJson.Serialize(manifest, true)));
            return zip.ToArray();
        }

        private static string WritePack(Env env, (string path, int size, string group)[] files, Action<Dictionary<string, object>> tweak)
        {
            return WritePack(files, env, "mcvs-hd-test.zip", tweak);
        }

        /// <summary>
        /// One start of the game as the host runs it (EmbeddedHost.PrepareInstallerData). McvsOffline/hd/ is read only, as on a
        /// phone where a file manager or adb made it (another uid owns it): any write the installer tries there fails and is
        /// recorded, and the start fails the test when there was one. <paramref name="deny"/> denies more paths the same way;
        /// <paramref name="fake"/> false runs with the real file system only (the chmod test).
        /// </summary>
        private static (InstallerDataResult data, HdResult hd) Boot(Env env, bool enabled = true, bool rebuild = true, int budgetMs = 60000,
                                                                     Action<string> crash = null, Func<string, bool> deny = null, bool fake = true)
        {
            Log log = env.Logger.For("hd");
            var hdOptions = new HdInstallerOptions { Enabled = enabled, BudgetMs = budgetMs, Checkpoint = crash };
            if (fake) hdOptions.WriteDenied = ReadOnlyDrop(env, deny);
            InstallerDataOptions options = DataOptions(rebuild);
            options.HdOriginalSizes = HdInstaller.Prepare(env.Loc, hdOptions, log).OriginalSizes;
            InstallerDataResult data = InstallerData.Prepare(env.Loc, new MapPrefs(env.Prefs), options, env.Logger.For("install"), 1700000000);
            HdResult hd = HdInstaller.Apply(env.Loc, hdOptions, log);
            Assert.Empty(env.DropWrites);
            return (data, hd);
        }

        /// <summary>The fake platform's answer to "may this path be written": not inside McvsOffline/hd/ (recorded), nor where <paramref name="deny"/> says.</summary>
        private static Func<string, bool> ReadOnlyDrop(Env env, Func<string, bool> deny = null)
        {
            string drop = HdInstaller.HdDir(env.Loc);
            return path =>
            {
                if (Under(path, drop))
                {
                    env.DropWrites.Add(path);
                    return true;
                }
                return deny != null && deny(path);
            };
        }

        private static bool Under(string path, string dir)
        {
            string full = Path.GetFullPath(path), root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            return full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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

        /// <summary>The HD files in Data/, and their originals in the backup folder (McvsOffline/hd-install unless <paramref name="workDir"/>).</summary>
        private static void AssertHdInstalled(Env env, IEnumerable<string> paths = null, string workDir = null)
        {
            foreach (string path in paths ?? HdFiles.Select(f => f.path))
            {
                Assert.Equal(HdBytes(path), env.Read(path));
                string backup = workDir == null ? HdInstaller.BackupPath(env.Loc, path)
                    : Path.Combine(workDir, HdInstaller.BackupFolder, path.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(Original(path), File.ReadAllBytes(backup));
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
            Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed (", hd.Summary);
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
            Assert.Equal(new[] { 1, 2 }, index.EntriesOf("Data/1/0018344.f").Select(e => e.Piece));   // whole -> 2 pieces
            Assert.Equal(4, index.EntriesOf("Data/a/4309cd.f").Count);                                // 3 -> 4 pieces
            Assert.Equal(3L * Split + 99, index.EntriesOf("Data/a/4309cd.f")[0].TotalSize);
            // The saved copy follows, so a reinstall gives the installer an index that matches the HD files.
            Assert.Equal(File.ReadAllBytes(env.Loc.PackInfo), File.ReadAllBytes(env.Loc.SavedIndex));
            AssertNoTemps(env);

            // Second start: nothing to do, nothing written.
            byte[] indexAfter = File.ReadAllBytes(env.Loc.PackInfo);
            string journalAfter = File.ReadAllText(env.Loc.Journal);
            DateTime stamp = File.GetLastWriteTimeUtc(env.P("Data/8/dbd2f1.f"));
            var (data2, hd2) = Boot(env);
            output.WriteLine(hd2.Summary);
            Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed (", hd2.Summary);
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
            Assert.False(File.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.StateFile)));
            Assert.False(Directory.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.BackupFolder)));
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
            WritePack(env, HdFiles.Take(2).ToArray(), m => m["created"] = "2026-10-29T08:00:00Z");
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

        // ------------------------------------------------------------------ where the installer writes

        /// <summary>Every file under <paramref name="dir"/> with a hash of its bytes (empty when the folder is missing).</summary>
        private static Dictionary<string, string> Tree(string dir)
        {
            if (!Directory.Exists(dir)) return new Dictionary<string, string>();
            return Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(dir, f),
                f => Directory.Exists(f) ? "dir" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        }

        private static string Fallback(Env env)
        {
            return Path.Combine(env.Internal, "McvsOffline", HdPlaces.WorkFolderName);
        }

        /// <summary>The pristine index and journal as the installer and InstallerData leave them (one round trip gives the saved copy its first version).</summary>
        private (byte[] index, HashSet<string> journal) Pristine(Env env)
        {
            Boot(env, rebuild: false);
            HdInstaller.Apply(env.Loc, new HdInstallerOptions { Enabled = false, WriteDenied = ReadOnlyDrop(env) }, env.Logger.For("hd"));
            Assert.Empty(env.DropWrites);
            return (File.ReadAllBytes(env.Loc.PackInfo), new HashSet<string>(File.ReadAllLines(env.Loc.Journal)));
        }

        /// <summary>
        /// The build 11 device test (Galaxy S22, Android 16): McvsOffline/hd/ made with adb belongs to the shell uid, so creating
        /// hd/backup/Data/9 failed and nothing was installed. The installer now only reads the drop folder.
        /// </summary>
        [Fact]
        public void ADropFolderMadeByAnotherToolIsOnlyReadAndThePackInstalls()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-dropfolder");
            string drop = HdInstaller.HdDir(env.Loc);
            Dictionary<string, string> before = Tree(drop);

            var (_, hd) = Boot(env);   // a write inside McvsOffline/hd/ fails as on the phone; Boot checks none was tried
            output.WriteLine(hd.Summary);
            Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed (", hd.Summary);
            Assert.DoesNotContain("failed", hd.Summary);
            Assert.Equal(before, Tree(drop));
            Assert.Equal(new[] { "mcvs-hd-test.zip" }, Directory.GetFileSystemEntries(drop).Select(Path.GetFileName));
            Assert.Equal(Path.Combine(env.Root, "McvsOffline", "hd-install"), HdInstaller.WorkDir(env.Loc));
            Assert.True(File.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.StateFile)));
            Assert.Contains("hd: installer files in McvsOffline/hd-install", env.Text);
            Assert.Empty(Directory.GetFiles(env.Root, HdPlaces.ProbeName, SearchOption.AllDirectories));
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);

            var (_, off) = Boot(env, enabled: false);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back", off.Summary);
            AssertOriginals(env);
            AssertInstallerAccepts(env);
            Assert.Equal(before, Tree(drop));
        }

        /// <summary>The same with the folder's mode on disk (denies nothing when the tests run as root; the fake above denies).</summary>
        [Fact]
        public void AReadOnlyDropFolderOnDiskIsNeverWritten()
        {
            if (OperatingSystem.IsWindows()) return;
            HdInstaller.ResetForTests();
            Env env = Installed("hd-chmod");
            string drop = HdInstaller.HdDir(env.Loc);
            Dictionary<string, string> before = Tree(drop);
            const UnixFileMode readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                          UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(drop, readOnly);
            File.SetUnixFileMode(env.Pack, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            try
            {
                if (Environment.UserName == "root") output.WriteLine("running as root: the mode denies nothing here");
                var (_, hd) = Boot(env, fake: false);
                output.WriteLine(hd.Summary);
                Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed (", hd.Summary);
                AssertHdInstalled(env);
                AssertInstallerAccepts(env);
                Assert.Equal(before, Tree(drop));
                Assert.Equal(readOnly, File.GetUnixFileMode(drop));

                var (_, off) = Boot(env, enabled: false, fake: false);
                Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back", off.Summary);
                AssertOriginals(env);
                Assert.Equal(before, Tree(drop));
            }
            finally
            {
                File.SetUnixFileMode(drop, readOnly | UnixFileMode.UserWrite);
            }
        }

        [Fact]
        public void WhenHdInstallCannotBeWrittenTheAppsInternalStorageIsUsed()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-fallback");
            var (pristineIndex, pristineJournal) = Pristine(env);
            string primary = HdInstaller.WorkDir(env.Loc), fallback = Fallback(env);
            Directory.Delete(primary, true);
            // McvsOffline/hd-install made by another tool too: the app's internal storage takes the state and the backups.
            Func<string, bool> denyPrimary = p => Under(p, primary);

            var (_, on) = Boot(env, deny: denyPrimary);
            output.WriteLine(on.Summary);
            Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed (", on.Summary);
            Assert.Contains("5 written now", on.Summary);
            Assert.EndsWith("; backups in the app's internal storage (McvsOffline/hd-install: permission denied)", on.Summary);
            Assert.Contains("hd: installer files in the app's internal storage " + fallback + " (McvsOffline/hd-install: permission denied)", env.Text);
            AssertHdInstalled(env, workDir: fallback);
            Assert.True(File.Exists(Path.Combine(fallback, HdInstaller.StateFile)));
            Assert.False(Directory.Exists(primary));
            AssertInstallerAccepts(env);

            // McvsOffline/hd-install can be written again: the folder that holds installed.json stays in use.
            var (_, again) = Boot(env);
            output.WriteLine(again.Summary);
            Assert.Matches(@"^hd textures: mcvs-hd-test\.zip, 5 files installed \([0-9.]+ MiB\); backups in the app's internal storage$", again.Summary);
            Assert.False(File.Exists(Path.Combine(primary, HdInstaller.StateFile)));

            var (_, off) = Boot(env, enabled: false);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back", off.Summary);
            AssertOriginals(env);
            Assert.Equal(pristineIndex, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(pristineJournal, new HashSet<string>(File.ReadAllLines(env.Loc.Journal)));
            Assert.False(File.Exists(Path.Combine(fallback, HdInstaller.StateFile)));
            Assert.False(Directory.Exists(Path.Combine(fallback, HdInstaller.BackupFolder)));

            // With nothing installed the next install goes to McvsOffline/hd-install again.
            Assert.True(Boot(env).hd.Installed);
            AssertHdInstalled(env);
        }

        [Fact]
        public void WhenNoFolderCanBeWrittenNothingChangesAndStatusSaysWhy()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-nowrite");
            byte[] index = File.ReadAllBytes(env.Loc.PackInfo);
            string journal = File.ReadAllText(env.Loc.Journal);
            string primary = HdInstaller.WorkDir(env.Loc), fallback = Fallback(env);

            var (_, hd) = Boot(env, deny: p => Under(p, primary) || Under(p, fallback));
            Assert.Equal("hd textures: none installed; cannot write McvsOffline/hd-install: permission denied " +
                         "(nor the app's internal storage: permission denied); nothing changed", hd.Summary);
            Assert.Equal(hd.Summary, HdInstaller.Status);
            Assert.False(hd.Installed);
            Assert.False(hd.IndexChanged);
            AssertOriginals(env);
            Assert.Equal(index, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(journal, File.ReadAllText(env.Loc.Journal));
            Assert.False(Directory.Exists(primary));
            Assert.False(Directory.Exists(fallback));
            Assert.Contains("hd: cannot write McvsOffline/hd-install: permission denied", env.Text);
            AssertNoTemps(env);
        }

        [Fact]
        public void WhenDataCannotBeWrittenNothingInItChanges()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-nodata");
            byte[] index = File.ReadAllBytes(env.Loc.PackInfo);
            string data8 = env.P("Data/8");
            Func<string, bool> denyData8 = p => Under(p, data8);

            var (_, hd) = Boot(env, deny: denyData8);
            Assert.Equal("hd textures: none installed; cannot write Data/8: permission denied; nothing changed", hd.Summary);
            Assert.False(hd.Installed);
            AssertOriginals(env);
            Assert.Equal(index, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.False(Directory.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.BackupFolder)));
            Assert.False(File.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.StateFile)));

            // Installed, then Data/8 cannot be written: removing changes nothing either, and the files stay consistent.
            Assert.True(Boot(env).hd.Installed);
            var (_, off) = Boot(env, enabled: false, deny: denyData8);
            Assert.Matches(@"^hd textures: mcvs-hd-test\.zip, 5 files installed \([0-9.]+ MiB\); nothing changed now: cannot write Data/8: permission denied$", off.Summary);
            Assert.True(off.Installed);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back", Boot(env, enabled: false).hd.Summary);
            AssertOriginals(env);
        }

        /// <summary>
        /// Data/ cannot be written and InstallerData has just given the installer an index with the original sizes (a reinstall
        /// with Gameloft's index in files/pack.info): nothing in Data/ changes, but the index and the journal are still made to
        /// match the HD files, or the installer would delete and download them.
        /// </summary>
        [Fact]
        public void WhenDataCannotBeWrittenTheIndexStillMatchesTheHdFiles()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-nodata-index", withPack: false);
            byte[] pristine = File.ReadAllBytes(env.Loc.PackInfo);
            var one = HdFiles.Take(1).ToArray();
            WritePack(env, one, null);
            Assert.True(Boot(env).hd.Installed);
            // Uninstall + install: internal storage is wiped, no saved copy of ours, Gameloft's index next to Data/ (files/pack.info).
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            File.Delete(env.Loc.SavedIndex);
            File.WriteAllBytes(env.Loc.PlayerIndex, pristine);

            var (data, hd) = Boot(env, deny: p => Under(p, env.P("Data/8")));
            output.WriteLine(data.Summary);
            output.WriteLine(hd.Summary);
            Assert.Equal("installer data: index restored from the files/pack.info, 1 entries to download", data.Summary);
            Assert.Matches(@"^hd textures: mcvs-hd-test\.zip, 1 file installed \([0-9.]+ MiB\); nothing changed now: cannot write Data/8: permission denied$", hd.Summary);
            Assert.True(hd.Installed);
            Assert.True(hd.IndexChanged);
            AssertHdInstalled(env, one.Select(f => f.path));
            AssertInstallerAccepts(env);
        }

        /// <summary>
        /// McvsOffline/hd-install holds a state, cannot be written at one start (the twin in internal storage takes the state over
        /// and installs more), then can be written again: the newer state in internal storage stays in use, removal puts every
        /// original back, and the twin's stale state and backups are cleaned up rather than offered for deletion.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AStaleStateInHdInstallNeverHidesTheNewerOneInInternalStorage(bool onAgainFirst)
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-twin", withPack: false);
            var (pristineIndex, pristineJournal) = Pristine(env);
            string primary = HdInstaller.WorkDir(env.Loc), fallback = Fallback(env);
            string small = WritePack(env, HdFiles.Take(2).ToArray(), null);
            var (_, a) = Boot(env);
            output.WriteLine(a.Summary);
            Assert.Contains("2 files installed", a.Summary);
            AssertHdInstalled(env, HdFiles.Take(2).Select(f => f.path));

            File.Delete(small);
            WritePack(env, HdFiles, m => m["created"] = "2026-10-29T08:00:00Z");
            var (_, b) = Boot(env, deny: p => Under(p, primary));
            output.WriteLine(b.Summary);
            Assert.Contains("5 files installed", b.Summary);
            Assert.Contains("3 written now", b.Summary);
            Assert.EndsWith("; backups in the app's internal storage (McvsOffline/hd-install: permission denied)", b.Summary);
            Assert.Contains("hd: took over McvsOffline/hd-install/installed.json (2 files) into " + fallback, env.Text);
            Assert.True(File.Exists(Path.Combine(primary, HdInstaller.StateFile)));   // the stale copy McvsOffline/hd-install keeps

            if (onAgainFirst)
            {
                var (_, again) = Boot(env);
                output.WriteLine(again.Summary);
                Assert.Matches(@"^hd textures: mcvs-hd-test\.zip, 5 files installed \([0-9.]+ MiB\); backups in the app's internal storage$", again.Summary);
                AssertInstallerAccepts(env);
            }

            var (_, off) = Boot(env, enabled: false);
            output.WriteLine(off.Summary);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back", off.Summary);
            Assert.DoesNotContain("is ignored", env.Text);
            Assert.DoesNotContain("may be deleted", env.Text);
            AssertOriginals(env);
            Assert.Equal(pristineIndex, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(pristineJournal, new HashSet<string>(File.ReadAllLines(env.Loc.Journal)));
            // Nothing refers to them any more: the twin's stale state and its backups are gone, as are ours.
            Assert.False(File.Exists(Path.Combine(primary, HdInstaller.StateFile)));
            Assert.False(Directory.Exists(Path.Combine(primary, HdInstaller.BackupFolder)));
            Assert.False(File.Exists(Path.Combine(fallback, HdInstaller.StateFile)));
            Assert.False(Directory.Exists(Path.Combine(fallback, HdInstaller.BackupFolder)));

            // A later start does not bring anything back; on again, McvsOffline/hd-install is used.
            Assert.Equal("hd textures: off (settings hd.enabled false)", Boot(env, enabled: false).hd.Summary);
            AssertOriginals(env);
            Assert.True(Boot(env).hd.Installed);
            AssertHdInstalled(env);
            AssertInstallerAccepts(env);
        }

        /// <summary>
        /// Both folders hold an installed.json that neither took over from the other (here: build 11's in the drop folder beside
        /// ours): the records ours lacks are added to ours, so removal puts back the originals only the old state knew.
        /// </summary>
        [Fact]
        public void TwoUnrelatedStatesAreMergedAndRemovalPutsEveryOriginalBack()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-merge", withPack: false);
            string drop = HdInstaller.HdDir(env.Loc), primary = HdInstaller.WorkDir(env.Loc);
            string first = WritePack(env, HdFiles.Take(2).ToArray(), null);
            Assert.True(Boot(env).hd.Installed);
            // Build 11 had kept this state and its backups in the drop folder.
            string keep = Path.Combine(env.Root, "..", "kept");
            Directory.Move(primary, keep);
            File.Delete(first);
            WritePack(env, HdFiles.Skip(2).ToArray(), m => m["created"] = "2026-10-29T08:00:00Z");
            var (_, b) = Boot(env);
            output.WriteLine(b.Summary);
            Assert.Contains("3 files installed", b.Summary);
            File.Move(Path.Combine(keep, HdInstaller.StateFile), Path.Combine(drop, HdInstaller.StateFile));
            Directory.Move(Path.Combine(keep, HdInstaller.BackupFolder), Path.Combine(drop, HdInstaller.BackupFolder));
            Dictionary<string, string> before = Tree(drop);

            var (_, off) = Boot(env, enabled: false);
            output.WriteLine(off.Summary);
            Assert.Contains("hd: McvsOffline/hd/installed.json of an earlier start: 2 of its 2 files added to McvsOffline/hd-install/installed.json", env.Text);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back; McvsOffline/hd/backup (and its installed.json) " +
                         "of an earlier build is no longer used and may be deleted", off.Summary);
            AssertOriginals(env);
            AssertInstallerAccepts(env);
            Assert.Equal(before, Tree(drop));
            Assert.False(File.Exists(Path.Combine(primary, HdInstaller.StateFile)));
        }

        /// <summary>
        /// Build 11 kept installed.json and backup/ in McvsOffline/hd/ (where the app had made that folder itself): the state is
        /// taken over once, the old backups are used where they are and never written, and removal puts the originals back.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheStateOfBuild11IsTakenOverAndRemovalUsesItsBackups(bool hdInstallWritable)
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-migrate");
            var (pristineIndex, pristineJournal) = Pristine(env);
            Assert.True(Boot(env).hd.Installed);
            string drop = HdInstaller.HdDir(env.Loc), primary = HdInstaller.WorkDir(env.Loc);
            File.Move(Path.Combine(primary, HdInstaller.StateFile), Path.Combine(drop, HdInstaller.StateFile));
            Directory.Move(Path.Combine(primary, HdInstaller.BackupFolder), Path.Combine(drop, HdInstaller.BackupFolder));
            Directory.Delete(primary, true);
            Dictionary<string, string> before = Tree(drop);
            string work = hdInstallWritable ? primary : Fallback(env);
            Func<string, bool> deny = hdInstallWritable ? (Func<string, bool>)null : p => Under(p, primary);

            var (_, hd) = Boot(env, deny: deny);
            output.WriteLine(hd.Summary);
            Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed (", hd.Summary);
            Assert.DoesNotContain("written now", hd.Summary);
            Assert.Contains("hd: took over McvsOffline/hd/installed.json (5 files) into " + (hdInstallWritable ? "McvsOffline/hd-install" : work), env.Text);
            Assert.True(File.Exists(Path.Combine(work, HdInstaller.StateFile)));
            Assert.True(File.Exists(Path.Combine(work, HdPlaces.ImportedFile)));
            Assert.False(Directory.Exists(Path.Combine(work, HdInstaller.BackupFolder)));   // the old backups stay where they are
            Assert.Equal(before, Tree(drop));
            AssertInstallerAccepts(env);

            var (_, off) = Boot(env, enabled: false, deny: deny);
            output.WriteLine(off.Summary);
            Assert.Equal("hd textures: off (settings hd.enabled false); 5 originals put back; McvsOffline/hd/backup (and its installed.json) " +
                         "of an earlier build is no longer used and may be deleted", off.Summary);
            AssertOriginals(env);
            Assert.Equal(pristineIndex, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal(pristineJournal, new HashSet<string>(File.ReadAllLines(env.Loc.Journal)));
            Assert.False(File.Exists(Path.Combine(work, HdInstaller.StateFile)));
            Assert.Equal(before, Tree(drop));

            // Taken over once: the old installed.json is not taken again.
            var (_, still) = Boot(env, enabled: false, deny: deny);
            Assert.StartsWith("hd textures: off (settings hd.enabled false); McvsOffline/hd/backup", still.Summary);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(env.Text, "took over"));
            AssertOriginals(env);

            // On again: the originals are backed up into our own folder; the drop folder is still only read.
            var (_, on) = Boot(env, deny: deny);
            output.WriteLine(on.Summary);
            Assert.Contains("5 written now", on.Summary);
            AssertHdInstalled(env, workDir: work);
            AssertInstallerAccepts(env);
            Assert.Equal(before, Tree(drop));
        }

        /// <summary>
        /// An old installed.json that cannot be read is not taken over and nothing changes (Data/ may hold HD files whose
        /// originals only it knows); once it can be read again it is taken over.
        /// </summary>
        [Fact]
        public void AnOldStateThatCannotBeReadChangesNothingUntilItCanBe()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-migrate-bad");
            Assert.True(Boot(env).hd.Installed);
            string drop = HdInstaller.HdDir(env.Loc), primary = HdInstaller.WorkDir(env.Loc);
            string old = Path.Combine(drop, HdInstaller.StateFile);
            File.Move(Path.Combine(primary, HdInstaller.StateFile), old);
            Directory.Move(Path.Combine(primary, HdInstaller.BackupFolder), Path.Combine(drop, HdInstaller.BackupFolder));
            Directory.Delete(primary, true);
            byte[] good = File.ReadAllBytes(old);
            File.WriteAllText(old, "{ not json");
            byte[] index = File.ReadAllBytes(env.Loc.PackInfo);

            var (_, hd) = Boot(env, enabled: false);
            output.WriteLine(hd.Summary);
            Assert.Equal("hd textures: McvsOffline/hd/installed.json of an earlier build cannot be read; nothing changed", hd.Summary);
            Assert.Contains("it is not taken over", env.Text);
            AssertHdInstalled(env, workDir: drop);
            Assert.Equal(index, File.ReadAllBytes(env.Loc.PackInfo));
            AssertInstallerAccepts(env);
            Assert.False(File.Exists(Path.Combine(primary, HdInstaller.StateFile)));

            File.WriteAllBytes(old, good);
            var (_, off) = Boot(env, enabled: false);
            output.WriteLine(off.Summary);
            Assert.StartsWith("hd textures: off (settings hd.enabled false); 5 originals put back", off.Summary);
            AssertOriginals(env);
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
            Assert.StartsWith("hd textures: mcvs-hd-test.zip, 5 files installed", hd.Summary);
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

        [Fact]
        public void TheBudgetSpreadsRemovalOverStartsAndEveryStartIsConsistent()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-budget-remove");
            Boot(env, rebuild: false);
            HdInstaller.Apply(env.Loc, new HdInstallerOptions { Enabled = false }, env.Logger.For("hd"));
            byte[] pristineIndex = File.ReadAllBytes(env.Loc.PackInfo);
            Assert.True(Boot(env).hd.Installed);
            int starts = 0;
            HdResult hd;
            do
            {
                hd = Boot(env, enabled: false, budgetMs: -1).hd;
                starts++;
                output.WriteLine(hd.Summary);
                AssertInstallerAccepts(env);
                // A level's lightmaps are given back together: never one HD file and one original.
                Assert.Equal(env.Read("Data/b/5aa.f").Length == 3000, env.Read("Data/b/5ab.f").Length == 5000);
                if (starts == 1)
                {
                    Assert.Equal("hd textures: mcvs-hd-test.zip, 4 files installed (8.0 MiB); 1 original put back; 4 originals at the next start", hd.Summary);
                    Assert.True(hd.Installed);
                }
            }
            while (hd.Summary.Contains("at the next start") && starts < 10);
            Assert.Equal(4, starts);   // one group per start: three single files and the lightmap pair
            Assert.Equal("hd textures: off (settings hd.enabled false); 2 originals put back", hd.Summary);
            AssertOriginals(env);
            Assert.Equal(pristineIndex, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.False(File.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.StateFile)));
            AssertNoTemps(env);
        }

        [Fact]
        public void AStartThatRanOutOfTimeRemovingInstallsNothingNew()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-budget-repack", withPack: false);
            WritePack(HdFiles.Take(3).ToArray(), env);
            Boot(env);
            AssertHdInstalled(env, HdFiles.Take(3).Select(f => f.path));
            // The next pack drops the first three and brings the lightmap pair: the removals come first, one group per start.
            File.Delete(Directory.GetFiles(HdInstaller.HdDir(env.Loc), "mcvs-hd-*.zip").Single());
            WritePack(HdFiles.Skip(3).ToArray(), env);
            HdResult first = Boot(env, budgetMs: -1).hd;
            output.WriteLine(first.Summary);
            Assert.Contains("1 original put back; 2 originals at the next start; 2 files at the next start", first.Summary);
            Assert.Equal(Original("Data/b/5aa.f"), env.Read("Data/b/5aa.f"));
            AssertInstallerAccepts(env);
            HdResult hd;
            int starts = 1;
            do
            {
                hd = Boot(env, budgetMs: -1).hd;
                starts++;
                output.WriteLine(hd.Summary);
                AssertInstallerAccepts(env);
            }
            while (hd.Summary.Contains("at the next start") && starts < 10);
            Assert.Equal(4, starts);   // the second removal, the third, then the lightmap pair (the budget counts both)
            AssertHdInstalled(env, HdFiles.Skip(3).Select(f => f.path));
            foreach (var f in HdFiles.Take(3)) Assert.Equal(Original(f.path), env.Read(f.path));
        }

        [Fact]
        public void WithoutAUsableIndexTheOriginalsGoBackWithinTheBudget()
        {
            HdInstaller.ResetForTests();
            Env env = Installed("hd-noindex-budget");
            Boot(env);
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            File.Delete(env.Loc.SavedIndex);
            var (_, hd) = Boot(env, rebuild: false, budgetMs: -1);
            output.WriteLine(hd.Summary);
            Assert.Contains("1 original is back, 4 more follow at the next start and the pack is installed again", hd.Summary);
            int starts = 1;
            while (hd.Summary.Contains("at the next start") && starts < 10)
            {
                hd = Boot(env, rebuild: false, budgetMs: -1).hd;
                starts++;
                output.WriteLine(hd.Summary);
            }
            Assert.Equal(4, starts);
            Assert.Contains("the originals are back", hd.Summary);
            AssertOriginals(env);
        }

        // ------------------------------------------------------------------ the converter's pack

        [Fact]
        public void APackInTheConvertersShapeIsRead()
        {
            Env env = new Env("hd-converter-shape");
            byte[] a = Bytes(3000, 1), b = Bytes(5000, 2), c = Bytes(700, 3);
            string path = Path.Combine(env.Root, "mcvs-hd-default.zip");
            Write(path, ConverterPack(new (string, byte[], byte[], string)[]
            {
                ("Data/1/9921a.f", c, Bytes(2800, 4), null),
                ("Data/b/5aa.f", a, Bytes(12000, 5), "lightmaps:Port"),
                ("Data/b/5ab.f", b, Bytes(20000, 6), "lightmaps:Port"),
            }));
            HdPack pack = HdPack.Open(path);
            Assert.Equal("ETC", pack.Variant);
            Assert.Equal("mcvs-hd-default.zip", pack.Name);
            Assert.Matches("^[0-9a-f]{8}$", pack.Id);
            Assert.Equal(new[] { "Data/1/9921a.f", "Data/b/5aa.f", "Data/b/5ab.f" }, pack.Files.Select(f => f.Path));
            Assert.Equal(new[] { "Data/1/9921a.f", "lightmaps:Port", "lightmaps:Port" }, pack.Files.Select(f => f.Group));   // null: the file alone
            Assert.Equal(new long[] { 700, 3000, 5000 }, pack.Files.Select(f => f.OriginalSize));
            Assert.Equal(Crc32.Update(0, a, 0, a.Length), pack.Files[1].OriginalCrc);
            Assert.Equal(new long[] { 2800, 12000, 20000 }, pack.Files.Select(f => f.HdSize));
            Assert.All(pack.Files, f => Assert.Equal(f.HdSize, f.Entry.Size));
            Assert.All(pack.Files, f => Assert.True(f.Entry.Crc32 == f.HdCrc));

            // The manifest of the installer's first draft (format "mcvs-hd", path/hd) is not the contract.
            Write(path, ConverterPack(new (string, byte[], byte[], string)[] { ("Data/b/5aa.f", a, Bytes(12000, 5), null) }, m =>
            {
                m["format"] = "mcvs-hd";
                m["version"] = 1L;
                m.Remove("kind");
            }));
            Assert.Contains("kind is not 'hd-textures'", Assert.Throws<HdZipException>(() => HdPack.Open(path)).Message);
            Write(path, ConverterPack(new (string, byte[], byte[], string)[] { ("Data/b/5aa.f", a, Bytes(12000, 5), null) }, m =>
            {
                var f = (Dictionary<string, object>)((List<object>)m["files"])[0];
                f["hd"] = f["replacement"];
                f.Remove("replacement");
            }));
            Assert.Contains("Data/b/5aa.f has no 'replacement' object", Assert.Throws<HdZipException>(() => HdPack.Open(path)).Message);
        }

#if MCVS_HD_CONVERTER
        /// <summary>
        /// The two sides together: tools/hd-textures' PackBuilder converts synthetic PC textures for a synthetic ETC Data/ folder (the
        /// converter's own fixtures), and the installer puts the pack it wrote into a game root with Gameloft's index, then removes it.
        /// Compiled when the converter is in the tree (MCVS_HD_CONVERTER, set by the project file).
        /// </summary>
        [Fact]
        public void APackWrittenByTheConverterIsInstalledAndRemoved()
        {
            HdInstaller.ResetForTests();
            var env = new Env("hd-converter");
            string pc = Path.Combine(env.Root, "..", "pc", "Data");
            Directory.CreateDirectory(pc);
            Directory.CreateDirectory(Path.Combine(env.Root, "Data"));
            File.WriteAllText(env.P("Data/MCVS.game"), "{}");
            var index = new McvsOffline.Tools.HdTextures.HdIndex();
            index.Levels.Add(new McvsOffline.Tools.HdTextures.HdLevel { Name = "Testmap", Alias = "Map42" });
            var logical = new[]
            {
                ("3d/Environment/01_WorkTextures/TEST_Concrete_bc.tga", McvsOffline.Tools.HdTextures.HdIndex.Surfaces, false),
                ("3d/Characters/Male/Test/TP/Textures/Test_body_bc.tga", McvsOffline.Tools.HdTextures.HdIndex.Characters, true),
                ("Baked/Levels/Testmap/Testmap.Lightmap_0.tga", McvsOffline.Tools.HdTextures.HdIndex.Lightmaps, true),
                ("Baked/Levels/Testmap/Testmap.Lightmap_0.lowend.tga", McvsOffline.Tools.HdTextures.HdIndex.Lightmaps, false),
            };
            int seed = 1;
            foreach (var (name, category, split) in logical)
            {
                const int size = 32;
                string hash = McvsOffline.Server.Core.Content.Crc32.Compute(Encoding.UTF8.GetBytes(name)).ToString("x8");
                var pcImage = McvsOffline.Embed.Tests.HdTextures.HdFixtures.Pattern(size, size, seed++, !split);
                int levels = (int)Math.Log2(size / 2) + 1;
                byte[] android = split
                    ? McvsOffline.Embed.Tests.HdTextures.HdFixtures.AndroidSplitFile(pcImage.HalfSize(), levels)
                    : McvsOffline.Embed.Tests.HdTextures.HdFixtures.AndroidKtxFile(pcImage.HalfSize(), levels);
                byte[] dds = McvsOffline.Tools.HdTextures.GcbfWriter.Encode(McvsOffline.Embed.Tests.HdTextures.HdFixtures.Dds(pcImage, split));
                var e = new McvsOffline.Tools.HdTextures.HdIndexEntry
                {
                    Hash = hash, Category = category, Levels = category == McvsOffline.Tools.HdTextures.HdIndex.Characters ? new string[0] : new[] { "Testmap" },
                    Layout = split ? "split" : "ktx",
                    AndroidSize = android.Length, AndroidWidth = size / 2, AndroidHeight = size / 2, AndroidMips = levels,
                    PcSize = dds.Length, PcWidth = size, PcHeight = size, PcMips = levels + 1,
                };
                Write(env.P("Data/" + e.AndroidPath), android);
                File.WriteAllBytes(Path.Combine(pc, e.PcPath), dds);
                index.Entries.Add(e);
            }
            string[] dataFiles = Directory.GetFiles(Path.Combine(env.Root, "Data"), "*", SearchOption.AllDirectories)
                .Select(f => "Data/" + Path.GetRelativePath(Path.Combine(env.Root, "Data"), f).Replace(Path.DirectorySeparatorChar, '/')).ToArray();
            var originals = dataFiles.ToDictionary(f => f, f => env.Read(f));
            byte[] gameloft = GameloftIndex(env.Root, dataFiles);
            File.WriteAllBytes(env.Loc.PackInfo, gameloft);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, InstallerData.ApkVersionCode);
            File.WriteAllText(env.Loc.Journal, string.Concat(PackIndex.Parse(gameloft).Entries.Where(x => x.Piece > 0).Select(x => x.JournalKey + "\n")));

            string packPath = Path.Combine(HdInstaller.HdDir(env.Loc), "mcvs-hd-default.zip");
            var log = new StringWriter();
            var summary = McvsOffline.Tools.HdTextures.PackBuilder.Build(new McvsOffline.Tools.HdTextures.BuildOptions
            {
                PcData = pc, AndroidData = Path.Combine(env.Root, "Data"), Categories = McvsOffline.Tools.HdTextures.HdIndex.DefaultCategories,
                Output = packPath, Threads = 2, Index = index,
            }, log);
            output.WriteLine(log.ToString());
            Assert.Equal(4, summary.Files);

            HdPack pack = HdPack.Open(packPath);
            Assert.Equal("ETC", pack.Variant);
            Assert.Equal(4, pack.Files.Count);
            Assert.Equal(2, pack.Files.Count(f => f.Group == "lightmaps:Testmap"));
            Assert.All(pack.Files.Where(f => f.Group != "lightmaps:Testmap"), f => Assert.Equal(f.Path, f.Group));

            var known = new[] { new InstallerData.KnownPack("ETC", dataFiles.Length, originals.Values.Sum(b => (long)b.Length)) };
            var hdOptions = new HdInstallerOptions();
            var dataOptions = new InstallerDataOptions { RebuildIndex = false, VerifyRestoredData = false, KnownPacks = known };
            dataOptions.HdOriginalSizes = HdInstaller.Prepare(env.Loc, hdOptions, env.Logger.For("hd")).OriginalSizes;
            InstallerData.Prepare(env.Loc, new MapPrefs(env.Prefs), dataOptions, env.Logger.For("install"), 1700000000);
            HdResult on = HdInstaller.Apply(env.Loc, hdOptions, env.Logger.For("hd"));
            output.WriteLine(on.Summary);
            Assert.StartsWith("hd textures: mcvs-hd-default.zip, 4 files installed", on.Summary);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(packPath))
            {
                foreach (HdPackFile f in pack.Files)
                {
                    using (var ms = new MemoryStream())
                    {
                        using (Stream entry = zip.GetEntry(f.Path).Open()) entry.CopyTo(ms);
                        Assert.Equal(ms.ToArray(), env.Read(f.Path));
                    }
                }
            }
            PackIndex patched = PackIndex.Parse(File.ReadAllBytes(env.Loc.PackInfo));
            Assert.True(InstallerData.Check(patched, env.Root).Complete);
            foreach (PackEntry x in patched.Entries.Where(x => x.Piece == 0))
            {
                byte[] all = File.ReadAllBytes(Path.Combine(env.Root, x.RelativePath));
                Assert.Equal((uint)x.Crc, Crc32.Update(0, all, 0, all.Length));
            }

            HdResult off = HdInstaller.Apply(env.Loc, new HdInstallerOptions { Enabled = false }, env.Logger.For("hd"));
            output.WriteLine(off.Summary);
            Assert.Equal("hd textures: off (settings hd.enabled false); 4 originals put back", off.Summary);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.PackInfo));
            foreach (var kv in originals) Assert.Equal(kv.Value, env.Read(kv.Key));
        }
#endif

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
            Assert.False(File.Exists(Path.Combine(HdInstaller.WorkDir(env.Loc), HdInstaller.StateFile)));
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
            Assert.Contains("its group 'lightmaps:Port' of 2 files is skipped", hd.Summary);
            Assert.Equal(Original("Data/b/5aa.f"), env.Read("Data/b/5aa.f"));
            Assert.Equal(Original("Data/b/5ab.f"), env.Read("Data/b/5ab.f"));
            AssertInstallerAccepts(env);
        }

        [Theory]
        [InlineData("format", 2L, "format 2 (this build reads format 1)")]
        [InlineData("format", "mcvs-hd", "format is not a number")]
        [InlineData("kind", "other", "kind is not 'hd-textures'")]
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
            var packFiles = new List<(string, byte[], byte[], string)>();
            for (int i = 0; i < paths.Length; i++) packFiles.Add((paths[i], env.Read(paths[i]), Bytes(hdFiles[i].Item2, 50 + i), null));
            string packPath = Path.Combine(env.Root, "McvsOffline", "hd", "mcvs-hd-real.zip");
            byte[] etcPack = ConverterPack(packFiles);
            Write(packPath, etcPack);
            byte[] journal = File.ReadAllBytes(env.Loc.Journal);

            // The same pack labelled for the ASTC data pack: refused on this (ETC) index, nothing changes.
            File.WriteAllBytes(packPath, ConverterPack(packFiles, m => m["pack"] = "ASTC"));
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

        /// <summary>
        /// A zip with stored entries; <c>align</c>: entry data on a 4-byte boundary through zipalign's extra field 0xD935 in the local
        /// header, as tools/hd-textures' StoredZipWriter writes the pack (docs/design/hd-textures.md §2).
        /// </summary>
        private sealed class TestZip
        {
            private readonly MemoryStream body = new MemoryStream();
            private readonly MemoryStream central = new MemoryStream();
            private readonly bool align;
            private int count;

            public TestZip(bool align = false)
            {
                this.align = align;
            }

            public void Add(string name, byte[] data)
            {
                byte[] n = Encoding.UTF8.GetBytes(name);
                uint crc = Crc32.Update(0, data, 0, data.Length);
                uint offset = (uint)body.Position;
                int pad = align ? 6 + (int)((4 - (body.Position + 30 + n.Length + 6) % 4) % 4) : 0;
                U32(body, 0x04034b50); U16(body, 10); U16(body, 0); U16(body, 0); U16(body, 0); U16(body, 0x21);
                U32(body, crc); U32(body, (uint)data.Length); U32(body, (uint)data.Length); U16(body, (ushort)n.Length); U16(body, (ushort)pad);
                body.Write(n, 0, n.Length);
                if (align)
                {
                    U16(body, 0xD935); U16(body, (ushort)(pad - 4)); U16(body, 4);
                    for (int i = 0; i < pad - 6; i++) body.WriteByte(0);
                    Assert.Equal(0, body.Position % 4);
                }
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
