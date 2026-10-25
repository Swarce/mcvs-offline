using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher;
using McvsOffline.Patcher.Zip;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>--game-log: the res/raw/args.txt rewrite and what the game's Java side makes of it.</summary>
    public class RawArgsTests
    {
        /// <summary>AndroidUtilsPlugin.getArguments(): UseDlc when a line equals "-UseDlc"; the filter is " " + every line containing "-LogCategoryFilter".</summary>
        private static void JavaGetArguments(byte[] content, out bool useDlc, out string filter)
        {
            useDlc = false;
            filter = "";
            using (var reader = new StringReader(Encoding.UTF8.GetString(content)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line == "-UseDlc") useDlc = true;
                    else if (line.Contains("-LogCategoryFilter")) filter += " " + line;
                }
            }
        }

        private static ZipArchiveReader.Archive Apk(string rawArgs)
        {
            var entries = new List<ZipEntry> { ZipEntry.CreateStored("resources.arsc", new byte[16], null) };
            if (rawArgs != null) entries.Add(ZipEntry.CreateStored(KnownBuild.RawArgsEntry, Encoding.ASCII.GetBytes(rawArgs), null));
            return ZipArchiveReader.Read(ZipArchiveWriter.Write(entries));
        }

        [Fact]
        public void AllLevelsRemovesTheFilterLineAndKeepsUseDlcOff()
        {
            JavaGetArguments(Encoding.ASCII.GetBytes(KnownBuild.RawArgsOriginal), out bool dlc, out string filter);
            Assert.False(dlc);
            Assert.Equal(" -LogCategoryFilter Fatal", filter);   // non-empty: GameApplication keeps the Fatal-only default

            var report = new PatchReport();
            byte[] replaced = ApkPatcher.RawArgsFor(Apk(KnownBuild.RawArgsOriginal), new PatcherOptions(), report);
            Assert.NotNull(replaced);
            JavaGetArguments(replaced, out dlc, out filter);
            Assert.False(dlc);
            Assert.Equal("", filter);   // IsNullOrEmpty: GameApplication..ctor IL_015c EnableAllLogCategoryFilter()
            Assert.Single(report.Notes);
            Assert.Equal(0x38799ea7u, Crc32.Compute(Encoding.ASCII.GetBytes(KnownBuild.RawArgsOriginal)));
        }

        [Fact]
        public void FatalKeepsTheEntryAndUnknownContentIsRefused()
        {
            Assert.Null(ApkPatcher.RawArgsFor(Apk(KnownBuild.RawArgsOriginal), new PatcherOptions { GameLog = GameLogMode.Fatal }, new PatchReport()));
            Assert.Throws<InvalidDataException>(() => ApkPatcher.RawArgsFor(Apk("-LogCategoryFilter Error"), new PatcherOptions(), new PatchReport()));
            Assert.Throws<InvalidDataException>(() => ApkPatcher.RawArgsFor(Apk(null), new PatcherOptions(), new PatchReport()));
            var forced = new PatchReport();
            Assert.Null(ApkPatcher.RawArgsFor(Apk("-UseDlc"), new PatcherOptions { Force = true }, forced));
            Assert.Single(forced.Warnings);
        }

        [Fact]
        public void TheRealApkShipsTheVerifiedEntry()
        {
            string apk = TestEnvironment.GameApk;
            if (apk == null) return;
            ZipEntry e = ZipArchiveReader.Read(apk).Find(KnownBuild.RawArgsEntry);
            Assert.NotNull(e);
            Assert.Equal(ZipEntry.MethodStored, e.Method);
            Assert.Equal(KnownBuild.RawArgsOriginalCrc, e.Crc32);
            Assert.Equal(KnownBuild.RawArgsOriginal, Encoding.ASCII.GetString(e.GetData()));
        }
    }
}
