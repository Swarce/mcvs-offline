using System.Collections.Generic;
using System.IO;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Server.Services.Auth;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// NTFS folds case (docs/design/pc-port.md §4.6): at every site where text from a request names a file, two names that
    /// differ only in case are one name on every platform, so Android and PC behave alike; Windows' reserved names are
    /// refused everywhere. The sites keyed by our own ids (exports: alias digits or a lower-case account id; uploads
    /// "u-" + lower hex; import sets lower hex; DocumentStore keys: account ids, our clan ids, the game's own leaderboard
    /// names) cannot collide and are only checked for that shape here.
    /// </summary>
    public class CaseCollisionTests
    {
        [Fact]
        public void FileInFindsTheFileWhateverTheCaseOfTheRequest()
        {
            using var dir = new TempDir();
            string file = dir.Combine("mcvs-logs-20261029-1200.zip");
            File.WriteAllText(file, "zip");
            Assert.Equal(file, AdminFiles.FileIn(dir.Path, "mcvs-logs-20261029-1200.zip"));
            Assert.Equal(file, AdminFiles.FileIn(dir.Path, "MCVS-LOGS-20261029-1200.ZIP"));
            Assert.Equal(file, AdminFiles.FileIn(dir.Path, "Mcvs-Logs-20261029-1200.zip"));
            Assert.Null(AdminFiles.FileIn(dir.Path, "mcvs-logs-20261029-1201.zip"));
        }

        [Fact]
        public void TwoFilesThatDifferOnlyInCaseAreOneName()
        {
            // Possible on a case-sensitive disk only (Android, Linux): the exact name wins, a third spelling finds neither.
            using var dir = new TempDir();
            string lower = dir.Combine("save.txt");
            string upper = dir.Combine("SAVE.txt");
            File.WriteAllText(lower, "a");
            File.WriteAllText(upper, "b");
            if (!File.Exists(lower) || File.ReadAllText(lower) != "a") return; // a case-insensitive disk: nothing to check
            Assert.Equal(lower, AdminFiles.FileIn(dir.Path, "save.txt"));
            Assert.Equal(upper, AdminFiles.FileIn(dir.Path, "SAVE.txt"));
            Assert.Null(AdminFiles.FileIn(dir.Path, "Save.txt"));

            var files = new List<FileInfo> { new FileInfo(lower), new FileInfo(upper) };
            Assert.Equal(lower, AdminFiles.FindByName(files, "save.txt").FullName);
            Assert.Null(AdminFiles.FindByName(files, "Save.TXT"));
        }

        [Fact]
        public void FindByNameFoldsCase()
        {
            using var dir = new TempDir();
            string path = dir.Combine("My Save.txt");
            File.WriteAllText(path, "x");
            var files = new List<FileInfo> { new FileInfo(path), new FileInfo(dir.Combine("other.txt")) };
            Assert.Equal(path, AdminFiles.FindByName(files, "My Save.txt").FullName);
            Assert.Equal(path, AdminFiles.FindByName(files, "my save.TXT").FullName);
            Assert.Null(AdminFiles.FindByName(files, "my save"));
            Assert.Null(AdminFiles.FindByName(files, null));
        }

        [Theory]
        [InlineData("CON", true)]
        [InlineData("con", true)]
        [InlineData("Nul.txt", true)]
        [InlineData("aux.tar.gz", true)]
        [InlineData("PRN", true)]
        [InlineData("com1", true)]
        [InlineData("COM9.log", true)]
        [InlineData("lpt5.zip", true)]
        [InlineData("save.", true)]
        [InlineData("save ", true)]
        [InlineData("CON .txt", true)]
        [InlineData("COM0", false)]
        [InlineData("COM10", false)]
        [InlineData("console.txt", false)]
        [InlineData("nul_save.txt", false)]
        [InlineData("mcvs-save-123456789-20261029-1200.txt", false)]
        [InlineData(".hidden", false)]
        public void WindowsReservedNamesAreRefusedEverywhere(string name, bool reserved)
        {
            Assert.Equal(reserved, AdminFiles.IsWindowsReservedName(name));
            using var dir = new TempDir();
            if (reserved) Assert.Null(AdminFiles.FileIn(dir.Path, name));
        }

        [Fact]
        public void OurOwnIdsHaveOneCaseOnly()
        {
            // Export names: "mcvs-save-" + SafeName(alias or account id) + stamp. Aliases are digits, account ids lower hex.
            Assert.True(AliasRegistry.IsWellFormed("123456789"));
            Assert.False(AliasRegistry.IsWellFormed("1234567AB"));
            // Import sets and upload ids are lower-case hex by validation.
            Assert.True(AdminFiles.IsLowerHex("0a1b2c3d", 8));
            Assert.False(AdminFiles.IsLowerHex("0A1B2C3D", 8));
            Assert.True(ImportUploads.IsUploadId("u-0123456789abcdef"));
            Assert.False(ImportUploads.IsUploadId("u-0123456789ABCDEF"));
            // DocumentStore keeps the letters of a key: its keys are ids we or the game chose, never typed text.
            Assert.Equal("fed_5fid_3aabc", DocumentStore.EncodeKey("fed_id:abc"));
        }
    }
}
