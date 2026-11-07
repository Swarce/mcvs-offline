using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The Java installer's state that Bootstrap prepares (docs/INSTALL-OFFLINE.md). The rules mirrored here come from the
    /// installer's own code (GameInstaller.onCreate/n(), DownloadComponent.a(h, ...), goodSize, PackFileReader): an index in
    /// getFilesDir()/pack.info plus gl_ver = 1173200 plus every file at its size (split pieces also in the journal) means
    /// "start the game", without network.
    /// </summary>
    public class InstallerDataTests
    {
        private const int Split = PackIndex.SplitSize;
        private readonly ITestOutputHelper output;

        public InstallerDataTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private sealed class Prefs : IInstallerPrefs
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>();

            public string GetString(string key) { return Values.TryGetValue(key, out object v) ? v as string : null; }

            public int GetInt(string key, int defaultValue) { return Values.TryGetValue(key, out object v) && v is int i ? i : defaultValue; }

            public bool SetString(string key, string value) { Values[key] = value; return true; }

            public bool SetInt(string key, int value) { Values[key] = value; return true; }
        }

        private sealed class Env
        {
            public string Root;
            public string Internal;
            public InstallerDataLocations Loc;
            public Prefs Prefs = new Prefs();
            public Logger Logger = new Logger();
            public MemoryLogSink Lines = new MemoryLogSink(LogLevel.Debug, 1000);
            public Log Log;

            public Env(string name)
            {
                string dir = TestEnvironment.NewTempDir(name);
                Root = Path.Combine(dir, "external", "files");
                Internal = Path.Combine(dir, "internal", "files");
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(Internal);
                Loc = new InstallerDataLocations(Internal, Root);
                Logger.AddSink(Lines);
                Log = Logger.For("install");
            }

            public string Text { get { return string.Join("\n", Lines.Snapshot().Select(e => e.Message)); } }

            public void File(string rel, int size, byte seed)
            {
                string path = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var data = new byte[size];
                for (int i = 0; i < size; i++) data[i] = (byte)(seed + i * 7 + (i >> 11));
                System.IO.File.WriteAllBytes(path, data);
            }

            public string Path_(string rel) { return Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)); }
        }

        /// <summary>A small data pack: two buckets, Config, a split file of three pieces.</summary>
        private static readonly string[] Files = { "Data/MCVS.game", "Data/Config/EngineConfig.json", "Data/1/0018344.f", "Data/a/4309cd.f" };
        private static readonly int[] Sizes = { 790, 207, 4893, 2 * Split + 12345 };

        private static Env NewPack(string name)
        {
            var env = new Env(name);
            for (int i = 0; i < Files.Length; i++) env.File(Files[i], Sizes[i], (byte)(i + 1));
            return env;
        }

        private static InstallerDataOptions Options(bool rebuild = true)
        {
            long total = Sizes.Sum(s => (long)s);
            return new InstallerDataOptions { RebuildIndex = rebuild, KnownPacks = new[] { new InstallerData.KnownPack("ETC", Files.Length, total) } };
        }

        /// <summary>An index in Gameloft's own form (real CRCs, ZIP sizes and offsets) for the files under root.</summary>
        private static byte[] GameloftIndex(string root, IEnumerable<string> relPaths)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriterBE(ms);
            long offset = 0;
            foreach (var group in relPaths.GroupBy(p => p.Substring(0, p.LastIndexOf('/'))).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var entries = new List<(string name, int size, uint crc, long total)>();
                foreach (string rel in group.OrderBy(p => p, StringComparer.Ordinal))
                {
                    byte[] data = File.ReadAllBytes(Path.Combine(root, rel));
                    string name = rel.Substring(rel.LastIndexOf('/') + 1);
                    if (data.Length > Split)
                    {
                        int pieces = (data.Length + Split - 1) / Split;
                        for (int p = 1; p <= pieces; p++)
                        {
                            int start = (p - 1) * Split, len = Math.Min(Split, data.Length - start);
                            entries.Add((name + ".split_" + p.ToString("D4"), len, Crc32.Update(0, data, start, len), data.Length));
                        }
                    }
                    else
                    {
                        entries.Add((name, data.Length, Crc32.Update(0, data, 0, data.Length), data.Length));
                    }
                }
                w.Utf("version: 101");
                w.Int(2048);
                w.Utf(PackIndex.FolderFor(group.Key));
                w.Int(entries.Count);
                foreach (var e in entries)
                {
                    w.Long(offset);
                    w.Int(e.size);
                    w.Long(e.crc);
                    w.Utf(e.name);
                    if (e.name.EndsWith(".split_0001", StringComparison.Ordinal)) w.Long(e.total);
                    int zsize = e.size / 2 + 40;
                    w.Int(zsize);
                    w.Utf(e.name + ".zip");
                    offset += zsize;
                }
            }
            return ms.ToArray();
        }

        private sealed class BinaryWriterBE
        {
            private readonly Stream s;

            public BinaryWriterBE(Stream s) { this.s = s; }

            public void Int(int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

            public void Long(long v) { Int((int)(v >> 32)); Int((int)v); }

            public void Utf(string v) { byte[] b = Encoding.UTF8.GetBytes(v); s.WriteByte((byte)(b.Length >> 8)); s.WriteByte((byte)b.Length); s.Write(b, 0, b.Length); }
        }

        [Fact]
        public void Crc32MatchesJavaUtilZip()
        {
            Assert.Equal(0xCBF43926u, Crc32.Update(0, Encoding.ASCII.GetBytes("123456789"), 0, 9));
            byte[] two = Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, Crc32.Update(Crc32.Update(0, two, 0, 4), two, 4, 5));
        }

        [Fact]
        public void RebuiltIndexHasGameloftsLayoutAndPassesTheInstallersChecks()
        {
            Env env = NewPack("rebuild");
            long bytes;
            List<PackIndex.FileInfoEntry> files = InstallerData.ScanData(env.Root, out bytes);
            Assert.Equal(Files.Length, files.Count);
            PackIndex index = PackIndex.Rebuild(files);
            Assert.True(index.IsRebuilt);
            Assert.Equal(Files.Length, index.LogicalFiles);
            Assert.Equal(bytes, index.LogicalBytes);
            Assert.Equal(6, index.Entries.Count);   // 3 whole files + 3 pieces

            var pieces = index.Entries.Where(e => e.Piece > 0).ToList();
            Assert.Equal(new[] { "4309cd.f.split_0001", "4309cd.f.split_0002", "4309cd.f.split_0003" }, pieces.Select(e => e.Name));
            Assert.All(pieces, e => Assert.Equal(".\\\\Data\\a", e.Folder));
            Assert.Equal(new[] { Split, Split, 12345 }, pieces.Select(e => e.Size));
            Assert.All(pieces, e => Assert.Equal(2L * Split + 12345, e.TotalSize));
            Assert.Equal("Data/a/4309cd.f", pieces[0].RelativePath);
            Assert.Equal(".\\\\Data\\a4309cd.f.split_0001", pieces[0].JournalKey);   // installer/Utils: folder + name
            PackEntry game = index.Entries.Single(e => e.Name == "MCVS.game");
            Assert.Equal(".\\\\Data", game.Folder);
            Assert.Equal("Data/MCVS.game", game.RelativePath);
            Assert.All(index.Entries, e => Assert.Equal(0L, e.Crc));   // CRC 0: the installer skips its CRC check

            DataCheck check = InstallerData.Check(index, env.Root);
            Assert.True(check.Complete, check.Describe());
            Assert.Equal(3, check.GoodPieces.Count);

            // Round trip through the parser gives the same bytes back.
            Assert.Equal(index.Raw, PackIndex.Parse(index.Raw).Raw);
        }

        [Fact]
        public void GoodSizeIsTheInstallersRule()
        {
            // A complete 2 MiB + 100 bytes file: piece 1 needs >= 2 MiB, piece 2 must end the file with exactly 100 bytes.
            Assert.True(InstallerData.GoodSize(Split + 100, 1, Split));
            Assert.True(InstallerData.GoodSize(Split + 100, 2, 100));
            Assert.False(InstallerData.GoodSize(Split + 99, 2, 100));
            Assert.False(InstallerData.GoodSize(Split, 2, 100));
            // Its quirk: a last piece of exactly 2 MiB never passes (length % 2 MiB is 0), which is why Rebuild keeps such a file whole.
            Assert.False(InstallerData.GoodSize(2L * Split, 2, Split));
            PackIndex index = PackIndex.Rebuild(new[] { new PackIndex.FileInfoEntry("Data/1/x.f", 2L * Split) });
            Assert.Single(index.Entries);
            Assert.Equal(2 * Split, index.Entries[0].Size);
        }

        [Fact]
        public void TheStubTheInstallerWritesAfterAFailedCdnRequestIsRejected()
        {
            // DownloadComponent.m() when the redirect is not an .amz (for example a 429 page of 277 bytes): a one-entry
            // "index" naming the d.cdn.php URL, written with a layout its own reader cannot read back.
            string a = "d.cdn.php?model=SM-S901E&device=r0q&product=2620&version=1.17.32&compression=ASTC&portal=google_market";
            var ms = new MemoryStream();
            var w = new BinaryWriterBE(ms);
            w.Utf(".\\");
            w.Int(1);
            w.Int(0);
            w.Int(277);
            w.Long(0);
            w.Utf(a);
            w.Int(277);
            w.Utf(a + ".zip");
            Assert.Null(PackIndex.TryParse(ms.ToArray(), out string error));
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Null(PackIndex.TryParse(new byte[0], out error));
        }

        [Theory]
        [InlineData(".\\\\..\\x", "a.f")]
        [InlineData(".\\\\Other", "a.f")]
        [InlineData(".\\\\Data\\1", "../a.f")]
        [InlineData(".\\\\Data\\1", "lib.so")]
        [InlineData(".\\", "a.f")]
        public void UnsafeOrForeignEntriesAreRefused(string folder, string name)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriterBE(ms);
            w.Utf("version: 101");
            w.Int(2048);
            w.Utf(folder);
            w.Int(1);
            w.Long(0);
            w.Int(10);
            w.Long(1234);
            w.Utf(name);
            w.Int(50);
            w.Utf(name + ".zip");
            Assert.Null(PackIndex.TryParse(ms.ToArray(), out string error));
            output.WriteLine(error);
        }

        [Fact]
        public void FreshInstallWithACompleteDataFolderNeedsNoNetwork()
        {
            Env env = NewPack("fresh");
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1792945862);
            output.WriteLine(env.Text);
            Assert.Contains("index rebuilt from the complete ETC data", result.Summary);
            Assert.Null(result.IndexToVerify);   // nothing to check a rebuilt index against

            PackIndex written = PackIndex.Parse(File.ReadAllBytes(env.Loc.PackInfo));
            Assert.True(written.IsRebuilt);
            Assert.Equal("1173200", File.ReadAllText(env.Loc.GlVer));   // exact: the installer compares with compareTo
            Assert.Equal(new[] { ".\\\\Data\\a4309cd.f.split_0001", ".\\\\Data\\a4309cd.f.split_0002", ".\\\\Data\\a4309cd.f.split_0003" },
                         File.ReadAllText(env.Loc.Journal).Split('\n', StringSplitOptions.RemoveEmptyEntries));
            Assert.Equal("1792945862/", File.ReadAllText(env.Loc.InsTime));
            Assert.Equal("ETC", env.Prefs.GetString("DATA_TYPE"));
            Assert.Equal(11732, env.Prefs.GetInt("CurrentVersion", 0));
            Assert.False(File.Exists(env.Loc.SavedIndex));   // rebuilt indexes are never saved as if they were Gameloft's

            // Next start: still matches, nothing rewritten but InsTime.
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1792950000);
            Assert.Contains("rebuilt index still matches", result.Summary);
            Assert.Equal("1792950000/", File.ReadAllText(env.Loc.InsTime));

            // A file goes missing: the rebuilt index is withdrawn, so the installer checks online instead of starting with holes.
            File.Delete(env.Path_("Data/1/0018344.f"));
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1792950100);
            output.WriteLine(env.Text);
            Assert.Contains("rebuilt index removed", result.Summary);
            Assert.False(File.Exists(env.Loc.PackInfo));
        }

        [Fact]
        public void AnIncompleteDataFolderIsLeftToTheInstaller()
        {
            Env env = NewPack("incomplete");
            File.Delete(env.Path_("Data/MCVS.game"));
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1);
            Assert.Contains("left to the game's installer", result.Summary);
            Assert.False(File.Exists(env.Loc.PackInfo));
            Assert.False(File.Exists(env.Loc.GlVer));
            Assert.Contains("not the complete 1.17.32 data pack", env.Text);

            // install.rebuildIndex false: never rebuilt, even when complete.
            Env complete = NewPack("norebuild");
            result = InstallerData.Prepare(complete.Loc, complete.Prefs, Options(rebuild: false), complete.Log, 1);
            Assert.False(File.Exists(complete.Loc.PackInfo));
        }

        [Fact]
        public void NoDataFolderLeavesEverythingAlone()
        {
            var env = new Env("nodata");
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1);
            Assert.Contains("no data yet", result.Summary);
            Assert.Empty(Directory.GetFiles(env.Internal, "*", SearchOption.AllDirectories));
            Assert.Empty(env.Prefs.Values);
        }

        [Fact]
        public void TheInstallersIndexIsSavedAndGivenBackAfterAReinstall()
        {
            Env env = NewPack("reinstall");
            byte[] gameloft = GameloftIndex(env.Root, Files);
            // The installer downloaded everything: its index, gl_ver and journal are in place.
            File.WriteAllBytes(env.Loc.PackInfo, gameloft);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, "1173200");
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1000);
            Assert.Contains("Gameloft index in place", result.Summary);
            Assert.Null(result.IndexToVerify);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.SavedIndex));
            Assert.Contains("Keep it in your backup", File.ReadAllText(env.Loc.SavedIndexNote));
            Assert.False(File.Exists(env.Loc.Journal));   // the installer's own journal is never written from sizes
            Assert.Equal("1000/", File.ReadAllText(env.Loc.InsTime));

            // Reinstall: internal storage and the journal are gone, files/ (with McvsOffline/install) restored from the backup.
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            env.Prefs.Values.Clear();
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 2000);
            output.WriteLine(env.Text);
            Assert.Contains("index restored from the saved index", result.Summary);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal("1173200", File.ReadAllText(env.Loc.GlVer));
            Assert.Equal(3, File.ReadAllText(env.Loc.Journal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Equal(11732, env.Prefs.GetInt("CurrentVersion", 0));
            Assert.NotNull(result.IndexToVerify);

            // The background CRC check: clean data passes.
            Assert.Equal(0, InstallerData.VerifyContent(env.Loc, result.IndexToVerify, env.Log, null, 0));

            // A hole in piece 2 of the split file (sizes still right): found, listed, and removed at the next start.
            using (var fs = new FileStream(env.Path_("Data/a/4309cd.f"), FileMode.Open, FileAccess.Write))
            {
                fs.Position = Split + 10;
                fs.Write(new byte[64], 0, 64);
            }
            Assert.Equal(1, InstallerData.VerifyContent(env.Loc, result.IndexToVerify, env.Log, null, 0));
            Assert.Equal("Data/a/4309cd.f", File.ReadAllText(env.Loc.DamagedList).Trim());
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 3000);
            output.WriteLine(env.Text);
            Assert.False(File.Exists(env.Path_("Data/a/4309cd.f")));
            Assert.False(File.Exists(env.Loc.DamagedList));
            Assert.Equal("", File.ReadAllText(env.Loc.Journal).Trim());   // its pieces left the journal: the installer downloads them
            Assert.Contains("Gameloft index in place", result.Summary);
        }

        /// <summary>Restores files/ with the saved Gameloft index (internal storage empty, as after a reinstall).</summary>
        private static (Env env, byte[] index) RestoredBackup(string name)
        {
            Env env = NewPack(name);
            byte[] gameloft = GameloftIndex(env.Root, Files);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.SavedIndex));
            File.WriteAllBytes(env.Loc.SavedIndex, gameloft);
            return (env, gameloft);
        }

        private static void ZeroPiece2(Env env)
        {
            using (var fs = new FileStream(env.Path_("Data/a/4309cd.f"), FileMode.Open, FileAccess.Write))
            {
                fs.Position = Split;
                fs.Write(new byte[Split], 0, Split);   // pre-allocated, never written: the size is still right
            }
        }

        [Fact]
        public void APartialRestoreWaitsForTheInstallerAndAFileItFinishedIsNotDeleted()
        {
            Env env = RestoredBackup("partial").env;
            byte[] good = File.ReadAllBytes(env.Path_("Data/1/0018344.f"));
            File.Delete(env.Path_("Data/1/0018344.f"));
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1000);
            output.WriteLine(result.Summary);
            Assert.Contains("1 entries to download", result.Summary);
            Assert.NotNull(result.IndexToVerify);
            Assert.True(result.WaitForInstaller);   // the CRC check starts only after the installer has handed over
            Assert.True(File.Exists(env.Loc.VerifyPending));

            // Even if it ran early: a missing file is the installer's business, never listed as damaged.
            Assert.Equal(0, InstallerData.VerifyContent(env.Loc, result.IndexToVerify, env.Log, null, 0));
            Assert.False(File.Exists(env.Loc.DamagedList));
            Assert.Contains("1 files missing or of another size were left to the installer", env.Text);
            Assert.False(File.Exists(env.Loc.VerifyPending));   // every entry was read: done

            // A file listed as damaged that the installer finished (or the player replaced) since is read again and kept.
            File.WriteAllBytes(env.Path_("Data/1/0018344.f"), good);
            File.WriteAllText(env.Loc.DamagedList, "Data/1/0018344.f\n");
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 2000);
            output.WriteLine(env.Text);
            Assert.True(File.Exists(env.Path_("Data/1/0018344.f")));
            Assert.Equal(good, File.ReadAllBytes(env.Path_("Data/1/0018344.f")));
            Assert.False(File.Exists(env.Loc.DamagedList));
            Assert.Contains("0 still damaged and deleted (0 journal lines), 1 good again", env.Text);
            Assert.Equal("installer data: Gameloft index in place (ETC, 4 files, 6 entries)", result.Summary);
            Assert.Null(result.IndexToVerify);

            // A complete restore has nothing to download: the check need not wait for the installer.
            Env full = RestoredBackup("partial-full").env;
            result = InstallerData.Prepare(full.Loc, full.Prefs, Options(), full.Log, 1000);
            Assert.NotNull(result.IndexToVerify);
            Assert.False(result.WaitForInstaller);
        }

        [Fact]
        public void TheDamagedListIsReadAgainAndOnlyFilesThatStillFailAreDeleted()
        {
            (Env env, byte[] gameloft) = RestoredBackup("recheck");
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1000);
            Assert.Equal(0, InstallerData.VerifyContent(env.Loc, result.IndexToVerify, env.Log, null, 0));   // clean
            ZeroPiece2(env);
            env.File("Data/2/extra.f", 100, 9);   // in no index
            File.Delete(env.Path_("Data/Config/EngineConfig.json"));
            File.WriteAllText(env.Loc.DamagedList, string.Join("\n", "Data/a/4309cd.f", "Data/MCVS.game", "Data/Config/EngineConfig.json",
                                                                "Data/2/extra.f", "Data/../settings.json", "Data/a/4309cd.f") + "\n");
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 2000);
            output.WriteLine(env.Text);
            Assert.False(File.Exists(env.Path_("Data/a/4309cd.f")));   // still damaged: deleted, its pieces left the journal
            Assert.Equal("", File.ReadAllText(env.Loc.Journal).Trim());
            Assert.True(File.Exists(env.Path_("Data/MCVS.game")));     // good: kept
            Assert.True(File.Exists(env.Path_("Data/2/extra.f")));     // not in the index: left alone
            Assert.False(File.Exists(env.Loc.DamagedList));
            Assert.Contains("1 still damaged and deleted (3 journal lines), 1 good again, 1 missing or of another size (left to the installer), " +
                            "1 not in the index (left alone)", env.Text);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.PackInfo));

            // Without the installer's index the saved copy is the reference; without any index nothing is deleted.
            env.File("Data/a/4309cd.f", 2 * Split + 12345, 4);
            ZeroPiece2(env);
            File.Delete(env.Loc.PackInfo);
            File.Delete(env.Loc.SavedIndex);
            File.WriteAllText(env.Loc.DamagedList, "Data/a/4309cd.f\n");
            InstallerData.Prepare(env.Loc, env.Prefs, Options(rebuild: false), env.Log, 3000);
            Assert.True(File.Exists(env.Path_("Data/a/4309cd.f")));
            Assert.Contains("1 not in any index (none found) (left alone)", env.Text);
        }

        [Fact]
        public void ACheckThatDidNotGetThroughIsScheduledAgainAndResumes()
        {
            (Env env, byte[] gameloft) = RestoredBackup("resume");
            ZeroPiece2(env);
            InstallerDataResult first = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1000);
            Assert.Contains("index restored from the saved index (ETC, 4 files, 6 entries)", first.Summary);
            Assert.NotNull(first.IndexToVerify);
            Assert.Equal(3, File.ReadAllText(env.Loc.Journal).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);   // sizes only
            Assert.Equal("index " + InstallerData.IndexHash(first.IndexToVerify) + "\nnext 0\n", File.ReadAllText(env.Loc.VerifyPending));

            // The session ends before the check started: the next start schedules it again.
            InstallerDataResult second = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 2000);
            output.WriteLine(second.Summary);
            Assert.Equal("installer data: Gameloft index in place (ETC, 4 files, 6 entries), CRC check pending", second.Summary);
            Assert.NotNull(second.IndexToVerify);
            Assert.False(second.WaitForInstaller);
            // ... unless the check is switched off.
            var off = Options();
            off.VerifyRestoredData = false;
            Assert.Null(InstallerData.Prepare(env.Loc, env.Prefs, off, env.Log, 2000).IndexToVerify);

            // Stopped part-way: the position is kept, and the next run resumes there (what was read before is not read again).
            IList<PackEntry> entries = second.IndexToVerify.Entries;
            int splitAt = Enumerable.Range(0, entries.Count).First(i => entries[i].RelativePath == "Data/a/4309cd.f");
            int calls = 0;
            Assert.Equal(-1, InstallerData.VerifyContent(env.Loc, second.IndexToVerify, env.Log, () => calls++ >= splitAt, 0));
            Assert.Contains("next " + splitAt, File.ReadAllText(env.Loc.VerifyPending));
            Assert.False(File.Exists(env.Loc.DamagedList));
            byte[] game = File.ReadAllBytes(env.Path_("Data/MCVS.game"));
            game[3] ^= 0xFF;   // damaged after it was read: not found by the resumed run
            File.WriteAllBytes(env.Path_("Data/MCVS.game"), game);
            InstallerDataResult third = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 3000);
            Assert.Contains("CRC check pending", third.Summary);
            Assert.Equal(1, InstallerData.VerifyContent(env.Loc, third.IndexToVerify, env.Log, null, 0));
            Assert.Contains("resumes at entry " + splitAt + " of 6", env.Text);
            Assert.Equal("Data/a/4309cd.f", File.ReadAllText(env.Loc.DamagedList).Trim());
            Assert.False(File.Exists(env.Loc.VerifyPending));

            // The next start deletes the still-damaged file; the check is done, so nothing more is scheduled.
            InstallerDataResult fourth = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 4000);
            Assert.False(File.Exists(env.Path_("Data/a/4309cd.f")));
            Assert.Equal("installer data: Gameloft index in place (ETC, 4 files, 6 entries)", fourth.Summary);
            Assert.Null(fourth.IndexToVerify);

            // A saved position belongs to one index: for another one the check starts from the beginning.
            File.WriteAllText(env.Loc.VerifyPending, "index 00000000-1\nnext 5\n");
            Assert.Equal(1, InstallerData.VerifyContent(env.Loc, PackIndex.Parse(gameloft), env.Log, null, 0));
            Assert.Contains("Data/MCVS.game", File.ReadAllText(env.Loc.DamagedList));
        }

        [Fact]
        public void AnIndexFromAMirrorNextToDataIsUsedAndAnotherVariantIsNot()
        {
            Env env = NewPack("mirror");
            byte[] gameloft = GameloftIndex(env.Root, Files);
            File.WriteAllBytes(env.Loc.PlayerIndex, gameloft);
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1);
            Assert.Contains("index restored from the files/pack.info", result.Summary);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.SavedIndex));   // kept for later reinstalls

            // An index of other data (every size differs, like the other texture variant) is refused; the complete folder is
            // then rebuilt from instead.
            Env other = NewPack("variant");
            Env source = new Env("variantsrc");
            for (int i = 0; i < Files.Length; i++) source.File(Files[i], Sizes[i] + 3, 9);
            File.WriteAllBytes(other.Loc.PlayerIndex, GameloftIndex(source.Root, Files));
            result = InstallerData.Prepare(other.Loc, other.Prefs, Options(), other.Log, 1);
            output.WriteLine(other.Text);
            Assert.Contains("does not match the data", other.Text);
            Assert.Contains("index rebuilt", result.Summary);
        }

        [Fact]
        public void AGlVerMismatchOrAnUnreadableIndexMeansNoIndex()
        {
            Env env = NewPack("glver");
            File.WriteAllBytes(env.Loc.PackInfo, GameloftIndex(env.Root, Files));
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, "1173100");   // another APK version: onCreate would delete pack.info
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1);
            Assert.Contains("gl_ver is '1173100'", env.Text);
            Assert.Contains("index rebuilt", result.Summary);
            Assert.Equal("1173200", File.ReadAllText(env.Loc.GlVer));

            Env stub = NewPack("stub");
            File.WriteAllBytes(stub.Loc.PackInfo, new byte[] { 0, 2, (byte)'.', (byte)'\\', 0, 0, 0, 1, 0, 0 });
            result = InstallerData.Prepare(stub.Loc, stub.Prefs, Options(), stub.Log, 1);
            Assert.Contains("is unusable", stub.Text);
            Assert.True(PackIndex.Parse(File.ReadAllBytes(stub.Loc.PackInfo)).IsRebuilt);
        }

        /// <summary>
        /// A data pack with replaced files (the ASTC HD pack of tools/hd-textures astc-pack): its own index and replaced.tsv are
        /// pushed to McvsOffline/install/ over a phone whose installer still has the index of the old data. The saved index
        /// replaces it, the pack is recognised by Gameloft's sizes in replaced.tsv, and later starts, a reinstall and a lost
        /// index all keep it.
        /// </summary>
        [Fact]
        public void ADataPackWithReplacedFilesIsRecognisedAndItsIndexReplacesTheOldOne()
        {
            Env env = NewPack("replaced");
            long gameloftBytes = Sizes.Sum(s => (long)s);
            var options = new Func<InstallerDataOptions>(() => new InstallerDataOptions
            {
                KnownPacks = new[] { new InstallerData.KnownPack("ASTC", Files.Length, gameloftBytes), new InstallerData.KnownPack("ETC", Files.Length, gameloftBytes + 99) },
            });
            byte[] original = File.ReadAllBytes(env.Path_("Data/1/0018344.f"));
            PackIndex gameloft = PackIndex.Parse(GameloftIndex(env.Root, Files));

            // The old data the installer's index describes (other sizes, as the ETC pack's).
            Env old = new Env("replaced-old");
            for (int i = 0; i < Files.Length; i++) old.File(Files[i], Sizes[i] + 3, 9);
            File.WriteAllBytes(env.Loc.PackInfo, GameloftIndex(old.Root, Files));
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, "1173200");
            env.Prefs.SetString(InstallerData.DataTypeKey, "ETC");

            // The pushed pack: Data/1/0018344.f replaced by a file that grew past 2 MiB, the index for it, replaced.tsv.
            env.File("Data/1/0018344.f", 2 * Split + 99, 77);
            byte[] hd = File.ReadAllBytes(env.Path_("Data/1/0018344.f"));
            var crcs = new List<uint> { Crc32.Update(0, hd, 0, Split), Crc32.Update(0, hd, Split, Split), Crc32.Update(0, hd, 2 * Split, 99) };
            PackIndex shipped = gameloft.Replace(new Dictionary<string, List<PackEntry>>
            {
                ["Data/1/0018344.f"] = PackIndex.EntriesFor("Data/1/0018344.f", hd.Length, crcs, false, gameloft.EntriesOf("Data/1/0018344.f")),
            });
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.SavedIndex));
            File.WriteAllBytes(env.Loc.SavedIndex, shipped.Raw);
            string list = "# test\nmcvs-replaced\t1\tASTC\t1.17.32\nData/1/0018344.f\t" + hd.Length + "\t" + Crc32.Update(0, hd, 0, hd.Length).ToString("x8") + "\t" +
                          original.Length + "\t" + Crc32.Update(0, original, 0, original.Length).ToString("x8") + "\n";
            File.WriteAllText(env.Loc.ReplacedList, list);
            // A damaged list of the old data's background check: it says nothing about the new data.
            File.WriteAllText(env.Loc.DamagedList, "Data/a/4309cd.f\nData/1/0018344.f\n");

            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, options(), env.Log, 10);
            output.WriteLine(env.Text);
            Assert.False(File.Exists(env.Loc.DamagedList));
            Assert.True(File.Exists(env.Path_("Data/a/4309cd.f")));
            Assert.Equal(hd, File.ReadAllBytes(env.Path_("Data/1/0018344.f")));
            string described = "ASTC with 1 replaced files, 4 files, " + shipped.Entries.Count + " entries";
            Assert.Equal("installer data: index switched to the saved index (" + described + ")", result.Summary);
            Assert.Equal(shipped.Raw, File.ReadAllBytes(env.Loc.PackInfo));
            Assert.Equal("ASTC", env.Prefs.GetString(InstallerData.DataTypeKey));
            Assert.Contains(".\\\\Data\\1" + "0018344.f.split_0003", File.ReadAllText(env.Loc.Journal));
            Assert.Equal(0, InstallerData.VerifyContent(env.Loc, result.IndexToVerify, env.Log, null, 0));

            // Next start: in place, still the ASTC pack.
            result = InstallerData.Prepare(env.Loc, env.Prefs, options(), env.Log, 20);
            Assert.Equal("installer data: Gameloft index in place (" + described + ")", result.Summary);

            // A reinstall (internal storage wiped): restored from the saved copy.
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            env.Prefs.Values.Clear();
            result = InstallerData.Prepare(env.Loc, env.Prefs, options(), env.Log, 30);
            Assert.Equal("installer data: index restored from the saved index (" + described + ")", result.Summary);

            // No index anywhere: the files are still the complete ASTC pack by Gameloft's sizes, and get a rebuilt index.
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            File.Delete(env.Loc.SavedIndex);
            result = InstallerData.Prepare(env.Loc, env.Prefs, options(), env.Log, 40);
            Assert.StartsWith("installer data: index rebuilt from the complete ASTC data (ASTC with 1 replaced files", result.Summary);

            // replaced.tsv only counts a file with the listed size: another size is not taken for the pack.
            Directory.Delete(env.Internal, true);
            Directory.CreateDirectory(env.Internal);
            env.File("Data/1/0018344.f", 2 * Split + 98, 77);
            result = InstallerData.Prepare(env.Loc, env.Prefs, options(), env.Log, 50);
            Assert.Contains("not the complete 1.17.32 data pack", env.Text);
            Assert.False(File.Exists(env.Loc.PackInfo));
        }

        /// <summary>A saved index that does not match the data either leaves the installer's index alone.</summary>
        [Fact]
        public void ASavedIndexIsOnlyTakenWhenTheDataMatchesIt()
        {
            Env env = NewPack("switch-no");
            byte[] gameloft = GameloftIndex(env.Root, Files);
            File.WriteAllBytes(env.Loc.PackInfo, gameloft);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.GlVer));
            File.WriteAllText(env.Loc.GlVer, "1173200");
            Env other = new Env("switch-no-other");
            for (int i = 0; i < Files.Length; i++) other.File(Files[i], Sizes[i] + 5, 3);
            Directory.CreateDirectory(Path.GetDirectoryName(env.Loc.SavedIndex));
            File.WriteAllBytes(env.Loc.SavedIndex, GameloftIndex(other.Root, Files));
            // The installer's index matches the data: it stays, and the stale saved copy is replaced by it.
            InstallerDataResult result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 1);
            Assert.Contains("Gameloft index in place", result.Summary);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.SavedIndex));

            // Neither matches (the data changed again): the installer's index is kept and does its own work.
            File.WriteAllBytes(env.Loc.SavedIndex, GameloftIndex(other.Root, Files));
            env.File("Data/MCVS.game", 12, 1);
            result = InstallerData.Prepare(env.Loc, env.Prefs, Options(), env.Log, 2);
            output.WriteLine(env.Text);
            Assert.Contains("matches neither the installer's index", env.Text);
            Assert.Contains("Gameloft index in place", result.Summary);
            Assert.Equal(gameloft, File.ReadAllBytes(env.Loc.PackInfo));
        }

        [Fact]
        public void ManageInstallerFalseTouchesNothing()
        {
            Env env = NewPack("off");
            var options = Options();
            options.Manage = false;
            InstallerData.Prepare(env.Loc, env.Prefs, options, env.Log, 1);
            Assert.Empty(Directory.GetFiles(env.Internal, "*", SearchOption.AllDirectories));
            Assert.False(File.Exists(env.Loc.InsTime));
        }

        // ------------------------------------------------------------------ the player's real data pack (when available)

        private static string RealDataDir
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
                if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data";
                return Directory.Exists(dir) ? dir : null;
            }
        }

        /// <summary>Both live 1.17.32 indexes (local copies outside the repository, when present) parse and are recognised.</summary>
        [Fact]
        public void BothLiveIndexesParseAndAreRecognised()
        {
            string dir = Environment.GetEnvironmentVariable("MCVS_CDN_MANIFESTS");
            if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/tools/cdn-mirror/manifests/1.17.32";
            int seen = 0;
            foreach (string variant in new[] { "ASTC", "ETC" })
            {
                string path = Path.Combine(dir, variant, "pack.info");
                if (!File.Exists(path)) continue;
                PackIndex index = PackIndex.Parse(File.ReadAllBytes(path));
                Assert.False(index.IsRebuilt);
                Assert.Equal(variant, InstallerData.VariantOf(index.LogicalFiles, index.LogicalBytes));
                Assert.Equal(variant == "ASTC" ? 16212 : 16218, index.Entries.Count);
                seen++;
            }
            output.WriteLine(seen + " live indexes checked");
        }

        [Fact]
        public void RealIndexParsesAndRebuildingTheRealDataGivesTheSameFolderAndNameKeys()
        {
            string data = RealDataDir;
            string indexPath = data == null ? null : Path.GetFullPath(Path.Combine(data, "..", "..", "pack.info"));
            if (indexPath == null || !File.Exists(indexPath))
            {
                output.WriteLine("no real data pack with its pack.info (MCVS_DATAPACK): skipped");
                return;
            }
            string root = Path.GetDirectoryName(data);
            PackIndex real = PackIndex.Parse(File.ReadAllBytes(indexPath));
            Assert.False(real.IsRebuilt);
            string variant = InstallerData.VariantOf(real.LogicalFiles, real.LogicalBytes);
            output.WriteLine(variant + ": " + real.LogicalFiles + " files, " + real.LogicalBytes + " bytes, " + real.Entries.Count + " entries");
            Assert.NotNull(variant);

            DataCheck check = InstallerData.Check(real, root);
            Assert.True(check.Complete, check.Describe());
            Assert.Equal(real.Entries.Count(e => e.Piece > 0), check.GoodPieces.Count);

            long bytes;
            List<PackIndex.FileInfoEntry> files = InstallerData.ScanData(root, out bytes);
            Assert.Equal(variant, InstallerData.VariantOf(files.Count, bytes));
            PackIndex rebuilt = PackIndex.Rebuild(files);
            // GameInstaller's PackFile.equals is folder + name: the rebuilt index names exactly the entries of Gameloft's, with the same sizes.
            var realKeys = real.Entries.ToDictionary(e => e.Folder + "|" + e.Name, e => (e.Size, e.TotalSize));
            var rebuiltKeys = rebuilt.Entries.ToDictionary(e => e.Folder + "|" + e.Name, e => (e.Size, e.TotalSize));
            Assert.Equal(realKeys.Count, rebuiltKeys.Count);
            foreach (var kv in realKeys) Assert.Equal(kv.Value, rebuiltKeys[kv.Key]);

            // A few real CRCs: a whole file and every piece of the first split file.
            foreach (PackEntry e in real.Entries.Where(e => e.Piece == 0).Take(3).Concat(real.Entries.Where(e => e.Piece > 0).TakeWhile((e, i) => i == 0 || e.Piece > 1)))
            {
                byte[] all = File.ReadAllBytes(Path.Combine(root, e.RelativePath));
                int start = e.Piece == 0 ? 0 : (e.Piece - 1) * Split;
                Assert.Equal((uint)e.Crc, Crc32.Update(0, all, start, e.Size));
            }
        }
    }

    /// <summary>The host prepares the installer's state synchronously in Start, before the Java GameInstaller can run.</summary>
    [Collection("GameHooks")]
    public class InstallerDataHostTests
    {
        private sealed class InstallerPlatform : McvsOffline.Embedded.IEmbeddedPlatform, McvsOffline.Embedded.IInstallerPlatform
        {
            private readonly string root, internalDir;

            public InstallerPlatform(string root, string internalDir)
            {
                this.root = root;
                this.internalDir = internalDir;
            }

            public bool Monitored;
            public readonly Dictionary<string, object> Prefs = new Dictionary<string, object>();

            public string Name => "installer-test";

            public string EntryPoint => "test host";

            public string ArgsReader => "nobody";

            public string PortInUseHint => "";

            public string ResolveGameRoot(Log log) => root;

            public LogSink CreateSystemLogSink(LogLevel minLevel) => null;

            public string InternalFilesDir() => internalDir;

            public IInstallerPrefs InstallerPrefs() => new MapPrefs(Prefs);

            public void MonitorInstaller(Log log, BootTrace boot) { Monitored = true; }

            public int InstallerHandedOver() => 1;

            private sealed class MapPrefs : IInstallerPrefs
            {
                private readonly Dictionary<string, object> map;

                public MapPrefs(Dictionary<string, object> map) { this.map = map; }

                public string GetString(string key) => map.TryGetValue(key, out object v) ? v as string : null;

                public int GetInt(string key, int defaultValue) => map.TryGetValue(key, out object v) && v is int i ? i : defaultValue;

                public bool SetString(string key, string value) { map[key] = value; return true; }

                public bool SetInt(string key, int value) { map[key] = value; return true; }
            }
        }

        [Fact]
        public void StartGivesTheInstallerItsSavedIndexBack()
        {
            string dir = TestEnvironment.NewTempDir("installerhost");
            string root = Path.Combine(dir, "files"), internalDir = Path.Combine(dir, "internal");
            Directory.CreateDirectory(internalDir);
            var loc = new InstallerDataLocations(internalDir, root);
            Directory.CreateDirectory(Path.Combine(root, "Data", "1"));
            File.WriteAllBytes(Path.Combine(root, "Data", "MCVS.game"), new byte[790]);
            File.WriteAllBytes(Path.Combine(root, "Data", "1", "0018344.f"), new byte[979]);
            // A saved Gameloft-style index (non-zero ZIP sizes; CRC of zeros as stored).
            PackIndex rebuilt = PackIndex.Rebuild(new[] { new PackIndex.FileInfoEntry("Data/MCVS.game", 790), new PackIndex.FileInfoEntry("Data/1/0018344.f", 979) });
            byte[] raw = (byte[])rebuilt.Raw.Clone();
            // Turn the rebuilt index into a "Gameloft" one: patch each entry's zsize (the int before the trailing zname) to 99.
            raw = MakeZipSizesNonZero(raw);
            Assert.False(PackIndex.Parse(raw).IsRebuilt);
            Directory.CreateDirectory(Path.GetDirectoryName(loc.SavedIndex));
            File.WriteAllBytes(loc.SavedIndex, raw);

            // No background CRC check in a test (it would start 90 s later).
            Directory.CreateDirectory(Path.Combine(root, "McvsOffline"));
            File.WriteAllText(Path.Combine(root, "McvsOffline", "settings.json"), "{\"install\": {\"verifyRestoredData\": false}}");
            var platform = new InstallerPlatform(root, internalDir);
            McvsOffline.Embedded.EmbeddedHost host = McvsOffline.Embedded.EmbeddedHost.Start(platform,
                new McvsOffline.Embedded.EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                // Synchronous: done when Start returns (the installer starts after OnCreate).
                Assert.Equal(raw, File.ReadAllBytes(loc.PackInfo));
                Assert.Equal("1173200", File.ReadAllText(loc.GlVer));
                Assert.Equal(11732, platform.Prefs["CurrentVersion"]);
                Assert.True(platform.Monitored);
                Assert.Contains(host.Boot.Milestones(), m => m.Contains("installer data: index restored from the saved index"));
                string settings = File.ReadAllText(Path.Combine(root, "McvsOffline", "settings.json"));
                Assert.Contains("\"manageInstaller\": true", settings);
                Assert.Contains("\"rebuildIndex\": true", settings);
                Assert.True(host.WaitForServer(30000));
            }
            finally
            {
                host.Server?.Dispose();
            }
        }

        /// <summary>Sets every entry's ZIP size to 99 in an index written by PackIndex.Rebuild (layout known: see its writer).</summary>
        private static byte[] MakeZipSizesNonZero(byte[] raw)
        {
            int pos = 0;
            Func<int> i32 = () => { int v = (raw[pos] << 24) | (raw[pos + 1] << 16) | (raw[pos + 2] << 8) | raw[pos + 3]; pos += 4; return v; };
            Func<string> utf = () => { int n = (raw[pos] << 8) | raw[pos + 1]; string v = Encoding.ASCII.GetString(raw, pos + 2, n); pos += 2 + n; return v; };
            var copy = (byte[])raw.Clone();
            while (pos < raw.Length)
            {
                utf(); i32(); utf();
                int count = i32();
                for (int k = 0; k < count; k++)
                {
                    pos += 8; i32(); pos += 8;
                    string name = utf();
                    if (name.EndsWith(".split_0001", StringComparison.Ordinal)) pos += 8;
                    copy[pos + 3] = 99;
                    pos += 4;
                    utf();
                }
            }
            return copy;
        }
    }
}
