using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Art;
using McvsOffline.Patcher.Resources;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The resources.arsc reader/writer and the two table edits of the art patch (app-icon-and-launch-screen.md §5.2, §5.4,
    /// WP-2/WP-4) on a synthetic table shaped like 1.17.32's; aapt2, when found, reads the results independently.
    /// </summary>
    public class ArscTableTests
    {
        private readonly ITestOutputHelper output;

        public ArscTableTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static ArtSettings.SplashSettings Splash(bool night = true)
        {
            return new ArtSettings.SplashSettings
            {
                Background = 0x10181F,
                BackgroundNight = night ? 0x000000 : (uint?)null,
                IconBackground = 0x334455,
                IconPreferred = true,
            };
        }

        /// <summary>The package's children, raw, for comparing before and after an edit.</summary>
        private static List<byte[]> Children(byte[] table)
        {
            return ArscTable.Parse(table).Package(0x7f).Children.Select(c => c.Bytes).ToList();
        }

        [Fact]
        public void ParseAndWriteGiveTheSameBytes()
        {
            foreach (bool utf8 in new[] { true, false })
            {
                byte[] table = SyntheticArsc.Build(utf8);
                ArscTable parsed = ArscTable.Parse(table);
                Assert.Equal(table, parsed.ToArray());
                ArscPackage package = parsed.Package(0x7f);
                Assert.Equal("com.gameloft.android.ANMP.GloftMVHM", package.Name);
                Assert.Equal("style", package.TypeName(0x0b));
                Assert.Equal("mipmap", package.TypeName(3));
                Assert.Equal(new[] { "()", "night-v8", "v11", "v21" }, package.Types(0x0b).Select(t => ArscConfig.Describe(t.Config)));
                Assert.Equal(new[] { "()", "ldpi-v4", "mdpi-v4", "hdpi-v4", "xhdpi-v4", "xxhdpi-v4", "xxxhdpi-v4", "anydpi-v26" },
                             package.Types(3).Select(t => ArscConfig.Describe(t.Config)));
                Assert.Equal("res/mipmap/ic_launcher_foreground1.png", parsed.Strings.Get((int)package.Types(3)[0].Entry(4).Value.Data));
            }
            Assert.Throws<InvalidDataException>(() => ArscTable.Parse(new byte[] { 2, 0, 12, 0, 13, 0, 0, 0, 0, 0, 0, 0 }));
        }

        [Fact]
        public void StringPoolAppendKeepsEveryIndexAndBothEncodings()
        {
            foreach (bool utf8 in new[] { true, false })
            {
                byte[] bytes = SyntheticArsc.Pool(new[] { "a", "", "résumé" }, utf8);
                StringPool pool = StringPool.Parse(bytes);
                Assert.Equal(bytes, pool.ToArray());
                string longText = new string('x', 200);   // two-byte lengths
                Assert.Equal(3, pool.Append("res/new.png"));
                Assert.Equal(4, pool.Append(longText));
                StringPool back = StringPool.Parse(pool.ToArray());
                Assert.Equal(new[] { "a", "", "résumé", "res/new.png", longText }, Enumerable.Range(0, back.Count).Select(back.Get));
                Assert.Equal(0, back.ToArray().Length % 4);
                Assert.Equal(2, back.IndexOf("résumé"));
            }
        }

        [Fact]
        public void SplashInsertsTwoChunksAfterTheStyleTypeAndChangesNothingElse()
        {
            byte[] before = SyntheticArsc.Build();
            ArscTable table = ArscTable.Parse(before);
            string note = SplashTheme.Apply(table, Splash());
            output.WriteLine(note);
            byte[] after = table.ToArray();
            Assert.Equal(before.Length + 2 * (84 + 4 * 0x180 + 16 + 3 * 12), after.Length);

            List<byte[]> a = Children(before), b = Children(after);
            ArscPackage package = ArscTable.Parse(after).Package(0x7f);
            int spec = package.TypeSpecIndex(0x0b);
            int last = package.TypeIndices(0x0b).Last();
            Assert.Equal(a.Count + 2, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                int j = i <= last - 2 ? i : i + 2;   // the two new chunks are the last two of the style run
                if (i == spec) continue;
                Assert.True(a[i].AsSpan().SequenceEqual(b[j]), "chunk " + i + " changed");
            }
            Assert.Equal(ArscTable.TypeSpecType, ArscTable.U16(b[last + 1], 0));   // dimen's typeSpec still follows the style run

            // typeSpec: only entry 0x17e's flags change (+uiMode, the night configuration).
            var oldSpec = new ArscTypeSpec(a[spec]);
            var newSpec = package.TypeSpec(0x0b);
            for (int e = 0; e < oldSpec.EntryCount; e++)
                Assert.Equal(e == SplashTheme.ThemeEntry ? 0x1400u : oldSpec.Flags(e), newSpec.Flags(e));

            List<ArscType> styles = package.Types(0x0b);
            Assert.Equal(new[] { "()", "night-v8", "v11", "v21", "v31", "night-v31" }, styles.Select(t => ArscConfig.Describe(t.Config)));
            ArscEntry light = styles[4].Entry(SplashTheme.ThemeEntry), dark = styles[5].Entry(SplashTheme.ThemeEntry);
            Assert.Single(styles[4].Entries());
            Assert.Equal(0x01030242u, light.Parent);
            Assert.Equal(SplashTheme.ThemeName, package.KeyStrings.Get((int)light.Key));
            Assert.Equal(new (uint, byte, uint)[] { (0x0101062c, 0x1c, 0xFF10181F), (0x01010630, 0x1c, 0xFF334455), (0x01010668, 0x10, 1) }, light.Items);
            Assert.Equal(new (uint, byte, uint)[] { (0x0101062c, 0x1c, 0xFF000000), (0x01010630, 0x1c, 0xFF334455), (0x01010668, 0x10, 1) }, dark.Items);
            Assert.Contains("+v31 (windowSplashScreenBackground #10181F", note);

            // A second run refuses: the theme already has a v31 configuration.
            var again = Assert.Throws<InvalidDataException>(() => SplashTheme.Apply(ArscTable.Parse(after), Splash()));
            Assert.Contains("already has the configuration v31", again.Message);
        }

        [Fact]
        public void SplashWithoutNightColoursAddsOnlyV31()
        {
            ArscTable table = ArscTable.Parse(SyntheticArsc.Build());
            var splash = Splash(night: false);
            splash.IconBackground = null;
            splash.IconPreferred = false;
            splash.WindowBackground = true;
            SplashTheme.Apply(table, splash);
            ArscPackage package = ArscTable.Parse(table.ToArray()).Package(0x7f);
            ArscType v31 = package.Types(0x0b).Last();
            Assert.Equal("v31", ArscConfig.Describe(v31.Config));
            Assert.Equal(new (uint, byte, uint)[] { (0x01010054, 0x1c, 0xFF10181F), (0x0101062c, 0x1c, 0xFF10181F) }, v31.Entry(SplashTheme.ThemeEntry).Items);
            Assert.Equal(0x400u, package.TypeSpec(0x0b).Flags(SplashTheme.ThemeEntry));
        }

        [Fact]
        public void SplashRefusesATableWithoutTheTheme()
        {
            byte[] table = SyntheticArsc.Build();
            ArscTable parsed = ArscTable.Parse(table);
            ArscPackage package = parsed.Package(0x7f);
            // Drop entry 0x17e from every style configuration.
            foreach (int i in package.TypeIndices(0x0b))
            {
                var type = new ArscType(package.Children[i].Bytes);
                SortedDictionary<int, byte[]> entries = type.Entries();
                entries.Remove(SplashTheme.ThemeEntry);
                package.Children[i] = new ArscChunk(ArscType.Build(type.Id, type.Config, type.EntryCount, entries));
            }
            var ex = Assert.Throws<InvalidDataException>(() => SplashTheme.Apply(ArscTable.Parse(parsed.ToArray()), Splash()));
            Assert.Contains("has no configuration", ex.Message);
        }

        [Fact]
        public void MonochromeGivesTheSpareSlotSixDensities()
        {
            byte[] before = SyntheticArsc.Build();
            ArscTable table = ArscTable.Parse(before);
            int strings = table.Strings.Count;
            output.WriteLine(MonochromeIcon.Apply(table));
            ArscTable after = ArscTable.Parse(table.ToArray());
            Assert.Equal(strings + 6, after.Strings.Count);
            ArscPackage package = after.Package(0x7f);
            foreach (ArscType type in package.Types(3))
            {
                string config = ArscConfig.Describe(type.Config);
                ArscEntry spare = type.Entry(MonochromeIcon.SpareEntry);
                if (config == "()" || config == "anydpi-v26")
                {
                    Assert.Equal(config == "()", spare != null);
                    continue;
                }
                IconDensity d = IconDensity.All.Single(x => config == x.Name + "-v4");
                Assert.Equal(MonochromeIcon.DensityFile(d), after.Strings.Get((int)spare.Value.Data));
                Assert.Equal(MonochromeIcon.SpareName, package.KeyStrings.Get((int)spare.Key));
                // The other entries keep their values.
                ArscType old = ArscTable.Parse(before).Package(0x7f).Types(3).Single(t => ArscConfig.Describe(t.Config) == config);
                foreach (var kv in old.Entries()) Assert.Equal(kv.Value, type.EntryBytes(kv.Key));
            }
            Assert.Equal(0x100u, package.TypeSpec(3).Flags(MonochromeIcon.SpareEntry));
            Assert.Throws<InvalidDataException>(() => MonochromeIcon.Apply(after));   // a second run refuses
        }

        [Fact]
        public void TheAdaptiveIconWriterMatchesTheShippedLayout()
        {
            byte[] two = MonochromeIcon.AdaptiveIcon(false);
            Assert.Equal(MonochromeIcon.AdaptiveIconBytes, two.Length);
            Assert.Equal(MonochromeIcon.AdaptiveIconCrc, McvsOffline.Patcher.Zip.Crc32.Compute(two));
            byte[] three = MonochromeIcon.AdaptiveIcon(true);
            Assert.Equal(552 + 24 + 4 + 56 + 24, three.Length);   // one more pool string (+offset) and one element pair
        }

        [Fact]
        public void Aapt2ReadsTheEditedSyntheticTable()
        {
            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found: skipped");
                return;
            }
            byte[] manifest = AxmlWriter.Write(AxmlWriter.GameLikeManifest());
            string before = ArtAapt2.Resources(Aapt2Dump.MinimalApk(manifest, SyntheticArsc.Build()));
            ArscTable table = ArscTable.Parse(SyntheticArsc.Build());
            SplashTheme.Apply(table, Splash());
            MonochromeIcon.Apply(table);
            string after = ArtAapt2.Resources(Aapt2Dump.MinimalApk(manifest, table.ToArray()));
            output.WriteLine(after);
            List<string> added = ArtAapt2.AddedLines(before, after);
            var expected = new List<string>();
            foreach (IconDensity d in IconDensity.All) expected.Add("(" + d.Name + "-v4) (file) " + MonochromeIcon.DensityFile(d) + " type=PNG");
            expected.AddRange(new[]
            {
                "(v31) (style) size=3 parent=0x01030242",
                "0x0101062c=#ff10181f", "0x01010630=#ff334455", "0x01010668=1",
                "(night-v31) (style) size=3 parent=0x01030242",
                "0x0101062c=#ff000000", "0x01010630=#ff334455", "0x01010668=1",
            });
            Assert.Equal(expected, added);
        }
    }
}
