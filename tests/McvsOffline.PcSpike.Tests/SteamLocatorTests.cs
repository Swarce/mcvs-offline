using System;
using System.IO;
using Xunit;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>The KeyValues parser and the Steam library search (pc-port.md §3.5), on folders built in a temp directory.</summary>
    public class SteamLocatorTests
    {
        private const string CurrentLibraryFolders = @"""libraryfolders""
{
	""0""
	{
		""path""		""C:\\Program Files (x86)\\Steam""
		""label""		""""
		""contentid""		""4718734567439223051""
		""totalsize""		""0""
		""apps""
		{
			""228980""		""436826013""
		}
	}
	""1""
	{
		""path""		""D:\\SteamLibrary""
		""apps""
		{
			""723780""		""3923048036""
		}
	}
}
";

        private const string OldLibraryFolders = @"""LibraryFolders""
{
	""TimeNextStatsReport""		""1610000000""
	""ContentStatsID""		""-123""
	""1""		""E:\\Games\\Steam""
	""2""		""F:\\More Games""
}
";

        private const string AppManifest = @"""AppState""
{
	""appid""		""723780""
	""Universe""		""1""
	""name""		""Modern Combat Versus""
	""StateFlags""		""4""
	""installdir""		""Modern Combat Versus""
	""buildid""		""6372049""
}
";

        [Fact]
        public void ParsesTheCurrentLibraryFoldersLayout()
        {
            Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLocator.ParseLibraryFolders(CurrentLibraryFolders));
        }

        [Fact]
        public void ParsesThePre2021LibraryFoldersLayout()
        {
            Assert.Equal(new[] { @"E:\Games\Steam", @"F:\More Games" }, SteamLocator.ParseLibraryFolders(OldLibraryFolders));
        }

        [Fact]
        public void ParsesEscapesCommentsBareTokensAndConditions()
        {
            VdfNode root = Vdf.Parse("\uFEFF// comment\n\"a\" { \"q\" \"say \\\"hi\\\"\\n\" bare value [$WIN32] \"n\" { } // tail\n }");
            VdfNode a = root["A"];
            Assert.True(a.IsBlock);
            Assert.Equal("say \"hi\"\n", a.Get("q"));
            Assert.Equal("value", a.Get("bare"));
            Assert.True(a["n"].IsBlock);
            Assert.Empty(a["n"].Children);
            Assert.Null(a.Get("missing"));
        }

        [Theory]
        [InlineData("\"a\" { \"b\" \"c\"")]
        [InlineData("\"a\" }")]
        [InlineData("\"a\" \"unterminated")]
        [InlineData("\"a\"")]
        public void MalformedTextThrowsFormatException(string text)
        {
            Assert.Throws<FormatException>(() => Vdf.Parse(text));
        }

        [Fact]
        public void FindsTheGameThroughASecondLibraryAndItsAppManifest()
        {
            string root = Support.NewTempDir("steam");
            try
            {
                string steam = Path.Combine(root, "Steam");
                string library = Path.Combine(root, "SteamLibrary");
                Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
                File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                                  "\"libraryfolders\" { \"0\" { \"path\" \"" + Escape(steam) + "\" } \"1\" { \"path\" \"" + Escape(library) + "\" } }");
                string game = Path.Combine(library, "steamapps", "common", "Modern Combat Versus");
                Directory.CreateDirectory(game);
                File.WriteAllText(Path.Combine(game, SteamLocator.ExeName), "");
                File.WriteAllText(Path.Combine(library, "steamapps", SteamLocator.ManifestName), AppManifest);

                string problem;
                GameLocation found = SteamLocator.FindInSteamRoots(new[] { steam }, out problem);
                Assert.Null(problem);
                Assert.Equal(Path.GetFullPath(game), Path.GetFullPath(found.Directory));
                Assert.Contains(SteamLocator.ManifestName, found.How);
                Assert.Equal(Path.Combine(found.Directory, SteamLocator.ExeName), found.Exe);
            }
            finally
            {
                Support.DeleteDir(root);
            }
        }

        [Fact]
        public void WithoutAManifestAnyCommonFolderWithTheExeIsFound()
        {
            string root = Support.NewTempDir("steam");
            try
            {
                string game = Path.Combine(root, "steamapps", "common", "MCVS");
                Directory.CreateDirectory(game);
                Directory.CreateDirectory(Path.Combine(root, "steamapps", "common", "Other Game"));
                File.WriteAllText(Path.Combine(game, SteamLocator.ExeName), "");
                string problem;
                GameLocation found = SteamLocator.FindInSteamRoots(new[] { root }, out problem);
                Assert.Equal(Path.GetFullPath(game), found.Directory);
                Assert.StartsWith("found in ", found.How);
            }
            finally
            {
                Support.DeleteDir(root);
            }
        }

        [Fact]
        public void NothingFoundNamesTheLibrariesItSearched()
        {
            string root = Support.NewTempDir("steam");
            try
            {
                string problem;
                Assert.Null(SteamLocator.FindInSteamRoots(new[] { root, root, null }, out problem));
                Assert.Contains("was not found in the Steam libraries " + Path.GetFullPath(root), problem);
                Assert.Null(SteamLocator.FindInSteamRoots(new string[0], out problem));
                Assert.Contains("Steam was not found", problem);
            }
            finally
            {
                Support.DeleteDir(root);
            }
        }

        [Fact]
        public void AnExplicitFolderOrExePathWins()
        {
            string game = Support.NewTempDir("game");
            try
            {
                File.WriteAllText(Path.Combine(game, SteamLocator.ExeName), "");
                string problem;
                Assert.Equal(game, SteamLocator.Locate(game, out problem).Directory);
                Assert.Equal(game, SteamLocator.Locate("\"" + Path.Combine(game, SteamLocator.ExeName) + "\"", out problem).Directory);
                Assert.Null(SteamLocator.Locate(Path.Combine(game, "nope"), out problem));
                Assert.Contains("no " + SteamLocator.ExeName, problem);
            }
            finally
            {
                Support.DeleteDir(game);
            }
        }

        private static string Escape(string path) => path.Replace("\\", "\\\\");
    }
}
