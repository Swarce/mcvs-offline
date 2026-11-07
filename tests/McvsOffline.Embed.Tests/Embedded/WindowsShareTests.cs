using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Windows;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// WindowsShare (docs/design/pc-port.md §6.3, C15): shares become files in McvsOffline\exports and Explorer opens on them; names
    /// are made Windows-safe, and a name that exists ignoring case gets a " (2)" suffix (NTFS folds case, §4.6).
    /// </summary>
    public class WindowsShareTests
    {
        private sealed class Env
        {
            public readonly string Exports = Path.Combine(TestEnvironment.NewTempDir("winshare"), "McvsOffline", "exports");
            public readonly List<string> Started = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public bool ExplorerFails;

            public WindowsShare Share()
            {
                return new WindowsShare(() => Exports, (program, args) =>
                {
                    if (ExplorerFails) throw new System.ComponentModel.Win32Exception(2);
                    Started.Add(program + " " + args);
                }, Warnings.Add);
            }
        }

        [Fact]
        public void AFileIsCopiedToExportsAndShownInExplorer()
        {
            var env = new Env();
            string source = Path.Combine(TestEnvironment.NewTempDir("winshare-src"), "x.json");
            File.WriteAllText(source, "{\"save\":1}");
            AdminShareResult result = env.Share().ShareFile(source, "mcvs-save-1.json", "MCVS Offline save");
            Assert.True(result.Ok);
            Assert.Equal("explorer", result.Method);
            Assert.Equal("McvsOffline\\exports\\mcvs-save-1.json", result.PublicPath);
            string target = Path.Combine(env.Exports, "mcvs-save-1.json");
            Assert.Equal("{\"save\":1}", File.ReadAllText(target));
            Assert.Equal(new[] { "explorer.exe /select,\"" + target + "\"" }, env.Started);
        }

        [Fact]
        public void SaveWithoutASubjectOnlyCopies()
        {
            // The page's "Save" (IAdminHost.ShareFile with a null subject): the file lands in exports, Explorer stays closed.
            var env = new Env();
            string source = Path.Combine(TestEnvironment.NewTempDir("winshare-src"), "x.zip");
            File.WriteAllText(source, "zip");
            AdminShareResult result = env.Share().ShareFile(source, "mcvs-logs-1.zip", null);
            Assert.True(result.Ok);
            Assert.Equal("file", result.Method);
            Assert.Equal("McvsOffline\\exports\\mcvs-logs-1.zip", result.PublicPath);
            Assert.Equal("zip", File.ReadAllText(Path.Combine(env.Exports, "mcvs-logs-1.zip")));
            Assert.Empty(env.Started);
            Assert.Empty(env.Warnings);
        }

        [Fact]
        public void ANameThatExistsIgnoringCaseGetsASuffix()
        {
            var env = new Env();
            Directory.CreateDirectory(env.Exports);
            File.WriteAllText(Path.Combine(env.Exports, "MCVS-Save.json"), "old");
            File.WriteAllText(Path.Combine(env.Exports, "mcvs-save (2).JSON"), "old");
            string source = Path.Combine(TestEnvironment.NewTempDir("winshare-src"), "x.json");
            File.WriteAllText(source, "new");
            AdminShareResult result = env.Share().ShareFile(source, "mcvs-save.json", "MCVS Offline save");
            Assert.Equal("McvsOffline\\exports\\mcvs-save (3).json", result.PublicPath);
            Assert.Equal("old", File.ReadAllText(Path.Combine(env.Exports, "MCVS-Save.json")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(env.Exports, "mcvs-save (3).json")));
        }

        [Fact]
        public void ATextBecomesASanitisedTxtFile()
        {
            var env = new Env();
            AdminShareResult result = env.Share().ShareText("MCVS Offline: logs <today>?", "line 1\nline 2");
            Assert.True(result.Ok);
            Assert.Equal("McvsOffline\\exports\\MCVS Offline_ logs _today__.txt", result.PublicPath);
            byte[] bytes = File.ReadAllBytes(Path.Combine(env.Exports, "MCVS Offline_ logs _today__.txt"));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, new[] { bytes[0], bytes[1], bytes[2] });
            Assert.Equal("line 1\nline 2", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
            Assert.Equal("McvsOffline\\exports\\MCVS Offline_ logs _today__ (2).txt", env.Share().ShareText("MCVS Offline: logs <today>?", "again").PublicPath);
        }

        [Fact]
        public void ExplorerThatDoesNotStartLeavesTheFile()
        {
            var env = new Env { ExplorerFails = true };
            AdminShareResult result = env.Share().ShareText("notes", "x");
            Assert.True(result.Ok);
            Assert.Equal("file", result.Method);
            Assert.True(File.Exists(Path.Combine(env.Exports, "notes.txt")));
            Assert.Single(env.Warnings);
            Assert.Contains("Explorer did not start", env.Warnings[0]);
        }

        [Fact]
        public void FailuresAnswerNone()
        {
            var env = new Env();
            Assert.Equal("none", env.Share().ShareFile(Path.Combine(env.Exports, "missing.json"), "a.json", "s").Method);
            Assert.Equal("none", new WindowsShare(() => null, null, null).ShareText("s", "t").Method);
            Assert.Empty(env.Started);
        }

        [Theory]
        [InlineData("CON", "_CON.txt")]
        [InlineData("nul", "_nul.txt")]
        [InlineData("com1.log", "_com1.log.txt")]
        [InlineData("LPT9", "_LPT9.txt")]
        [InlineData("COM0", "COM0.txt")]
        [InlineData("console", "console.txt")]
        [InlineData("name. . ", "name.txt")]
        [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j.txt")]
        [InlineData("", "MCVS Offline.txt")]
        [InlineData("...", "MCVS Offline.txt")]
        [InlineData("report.TXT", "report.txt")]
        [InlineData("tab\there", "tab_here.txt")]
        public void TextNamesAreWindowsSafe(string subject, string expected)
        {
            Assert.Equal(expected, WindowsShare.Sanitize(subject, ".txt"));
        }

        [Fact]
        public void FileNamesKeepTheirExtensionAndLongStemsAreCut()
        {
            Assert.Equal("mcvs-logs-20261029.zip", WindowsShare.Sanitize("mcvs-logs-20261029.zip", ""));
            Assert.Equal("_aux.json", WindowsShare.Sanitize("aux.json", ""));
            string stem = WindowsShare.Sanitize(new string('x', 300), ".txt");
            Assert.Equal(new string('x', 100) + ".txt", stem);
            Assert.True(WindowsShare.IsReservedName("Con.txt"));
            Assert.False(WindowsShare.IsReservedName("Conan"));
        }
    }
}
