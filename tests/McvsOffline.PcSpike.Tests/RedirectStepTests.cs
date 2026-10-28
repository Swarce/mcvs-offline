using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Mono.Cecil;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>
    /// 'redirect-test' / 'redirect-undo' (WP0 step 2) on a copy of a game folder: the config's single codeBase, the
    /// marker copy in McvsOffline\spike, no existing file modified, a foreign config backed up and restored.
    /// </summary>
    public class RedirectStepTests
    {
        private readonly ITestOutputHelper output;

        public RedirectStepTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void TheConfigHasOneCodeBaseUnderTheExeFolder()
        {
            string text = ExeConfig.Build("EngineBridge", new Version(1, 0, 0, 0), "McvsOffline/spike/EngineBridge.dll");
            output.WriteLine(text);
            XDocument doc = XDocument.Parse(text);
            XNamespace asm = "urn:schemas-microsoft-com:asm.v1";
            XElement dependent = doc.Root.Element("runtime").Element(asm + "assemblyBinding").Elements(asm + "dependentAssembly").Single();
            Assert.Equal("EngineBridge", (string)dependent.Element(asm + "assemblyIdentity").Attribute("name"));
            Assert.Equal("neutral", (string)dependent.Element(asm + "assemblyIdentity").Attribute("culture"));
            XElement codeBase = dependent.Elements(asm + "codeBase").Single();
            Assert.Equal("1.0.0.0", (string)codeBase.Attribute("version"));
            Assert.Equal("McvsOffline/spike/EngineBridge.dll", (string)codeBase.Attribute("href"));
            Assert.True(ExeConfig.IsOurs(text));
            Assert.False(ExeConfig.IsOurs("<configuration />"));
            Assert.StartsWith("<?xml", text);
        }

        [Theory]
        [InlineData("C:/Games/EngineBridge.dll")]
        [InlineData("/abs/EngineBridge.dll")]
        [InlineData("../EngineBridge.dll")]
        [InlineData("file://x/EngineBridge.dll")]
        public void AHrefOutsideTheExeFolderIsRejected(string href)
        {
            Assert.Throws<ArgumentException>(() => ExeConfig.Build("EngineBridge", new Version(1, 0), href));
        }

        private static Dictionary<string, string> Snapshot(string dir)
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                            .ToDictionary(f => Path.GetRelativePath(dir, f), KnownPcBuild.Sha256);
        }

        [Fact]
        public void InstallAndUndoTouchNoExistingFile()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                byte[] engine = Support.Engine();
                File.WriteAllBytes(Path.Combine(game, "EngineBridge.dll"), Support.EngineBridge(engine));
                File.WriteAllBytes(Path.Combine(game, "Gameloft.Engine.dll"), engine);
                File.WriteAllText(Path.Combine(game, "ModernCombatVersus.exe"), "stand-in");
                Dictionary<string, string> before = Snapshot(game);
                var log = new SpikeLog(data, echo: false);

                Assert.True(RedirectStep.Install(game, data, log));
                Dictionary<string, string> during = Snapshot(game);
                foreach (var file in before) Assert.Equal(file.Value, during[file.Key]);
                Assert.Equal(new[] { ExeConfig.FileName, Path.Combine("McvsOffline", "spike", "EngineBridge.dll") }.OrderBy(x => x),
                             during.Keys.Except(before.Keys).OrderBy(x => x));
                string config = File.ReadAllText(Path.Combine(game, ExeConfig.FileName));
                Assert.Contains("href=\"McvsOffline/spike/EngineBridge.dll\"", config);
                using (var copy = ModuleDefinition.ReadModule(RedirectStep.CopyPath(game)))
                {
                    var helper = copy.GetType(EngineBridgeMarkerPatch.TypeName).Methods.Single(m => m.Name == EngineBridgeMarkerPatch.HelperName);
                    Assert.Equal(RedirectStep.MarkerPath(data), helper.Body.Instructions[0].Operand);
                }
                Assert.StartsWith("NO MARKER", RedirectStep.DescribeMarker(data));

                // What the game would do when the CLR honours the config: our copy writes the marker.
                File.WriteAllText(RedirectStep.MarkerPath(data), EngineBridgeMarkerPatch.MarkerHeader + "\r\nlocation=X\\McvsOffline\\spike\\EngineBridge.dll\r\ncwd=X\r\n");
                Assert.StartsWith("MARKER WRITTEN", RedirectStep.DescribeMarker(data));
                Assert.Contains("location=X\\McvsOffline\\spike\\EngineBridge.dll; cwd=X", RedirectStep.DescribeMarker(data));

                Assert.True(RedirectStep.Undo(game, data, log));
                Dictionary<string, string> after = Snapshot(game);
                Assert.Equal(before, after);
                Assert.False(Directory.Exists(Path.Combine(game, "McvsOffline")));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void AForeignConfigIsBackedUpAndRestored()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                File.WriteAllBytes(Path.Combine(game, "EngineBridge.dll"), Support.EngineBridge(Support.Engine()));
                const string foreign = "<configuration><startup /></configuration>";
                File.WriteAllText(Path.Combine(game, ExeConfig.FileName), foreign);
                var log = new SpikeLog(data, echo: false);
                Assert.True(RedirectStep.Install(game, data, log));
                Assert.True(ExeConfig.IsOurs(File.ReadAllText(Path.Combine(game, ExeConfig.FileName))));
                Assert.Equal(foreign, File.ReadAllText(Path.Combine(data, "backup", ExeConfig.FileName)));
                Assert.True(RedirectStep.Undo(game, data, log));
                Assert.Equal(foreign, File.ReadAllText(Path.Combine(game, ExeConfig.FileName)));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void AnUnknownEngineBridgeIsRefusedAndNothingIsWritten()
        {
            string game = Support.NewTempDir("game");
            string data = Support.NewTempDir("data");
            try
            {
                byte[] engine = Support.Engine();
                string other = Support.EngineBridgeSource.Replace("NativeHostsDotNet);", "NativeHostsDotNet, null, true);");
                File.WriteAllBytes(Path.Combine(game, "EngineBridge.dll"), Support.Compile("EngineBridge", other, engine));
                var log = new SpikeLog(data, echo: false);
                Assert.False(RedirectStep.Install(game, data, log));
                Assert.False(File.Exists(Path.Combine(game, ExeConfig.FileName)));
                Assert.False(Directory.Exists(Path.Combine(game, "McvsOffline")));
                Assert.Contains("redirect-test refused", File.ReadAllText(log.FilePath));
            }
            finally
            {
                Support.DeleteDir(game);
                Support.DeleteDir(data);
            }
        }

        [Fact]
        public void TheRealSteamEngineBridgeGivesAVersion1CodeBase()
        {
            string pc = Support.PcInstall(output);
            if (pc == null) return;
            string data = Support.NewTempDir("data");
            try
            {
                Version version;
                string note;
                byte[] copy = RedirectStep.BuildMarkerCopy(Path.Combine(pc, "EngineBridge.dll"), RedirectStep.MarkerPath(data), out version, out note);
                output.WriteLine(note);
                Assert.Equal(new Version(1, 0, 0, 0), version);
                Assert.True(copy.Length > 0 && copy.Length < 16 * 1024);
            }
            finally
            {
                Support.DeleteDir(data);
            }
        }
    }
}
