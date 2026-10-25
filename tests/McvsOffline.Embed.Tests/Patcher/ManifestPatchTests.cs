using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;
using ZipEntry = McvsOffline.Patcher.Zip.ZipEntry;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The manifest's vendor-wide signature permission is renamed into the package's namespace, so the re-signed APK
    /// installs next to other Gameloft games (INSTALL_FAILED_DUPLICATE_PERMISSION otherwise).
    /// </summary>
    public class ManifestPatchTests
    {
        private const string Package = "com.gameloft.android.ANMP.GloftMVHM";
        private const string Vendor = "glshare.permission.ACCESS_SHARED_DATA";
        private static readonly string Scoped = Package + ".mcvs." + Vendor;

        private readonly ITestOutputHelper output;

        public ManifestPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BinaryXmlRoundTripsByteForByte(bool utf8)
        {
            byte[] manifest = AxmlWriter.Write(AxmlWriter.GameLikeManifest(), utf8);
            Assert.Equal(manifest, BinaryXml.Parse(manifest).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheVendorPermissionIsRenamedEverywhereAndNothingElseChanges(bool utf8)
        {
            byte[] manifest = AxmlWriter.Write(AxmlWriter.GameLikeManifest(), utf8);
            var notes = new List<string>();
            byte[] patched = ManifestPatch.Apply(manifest, notes);
            Assert.NotNull(patched);
            Assert.Contains(notes, n => n.Contains(Vendor) && n.Contains(Scoped) && n.Contains("<provider android:permission>"));

            List<BinaryXml.Attribute> before = BinaryXml.Parse(manifest).StringAttributes();
            List<BinaryXml.Attribute> after = BinaryXml.Parse(patched).StringAttributes();
            Assert.Equal(before.Count, after.Count);
            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].Element, after[i].Element);
                Assert.Equal(before[i].Name, after[i].Name);
                Assert.Equal(before[i].Value == Vendor ? Scoped : before[i].Value, after[i].Value);
            }
            Assert.Equal(3, after.Count(a => a.Value == Scoped)); // <permission>, <uses-permission>, <provider android:permission>
            Assert.Equal(new[] { Package + ".permission.C2D_MESSAGE", Scoped }, ManifestPatch.DeclaredPermissions(after).Select(a => a.Value));

            // Applying again finds nothing left to rename.
            Assert.Null(ManifestPatch.Apply(patched, null));
        }

        [Fact]
        public void AManifestWithOnlyPackageScopedPermissionsIsLeftAlone()
        {
            byte[] manifest = AxmlWriter.Write(AxmlWriter.GameLikeManifest(vendor: Package + ".permission.SHARED"));
            Assert.Null(ManifestPatch.Apply(manifest, new List<string>()));
        }

        [Fact]
        public void RefusesWhenThePermissionTextAlsoNamesSomethingElse()
        {
            AxmlWriter.Element root = AxmlWriter.GameLikeManifest();
            root.Children.Last().Add(new AxmlWriter.Element("meta-data", (AxmlWriter.AndroidNs, "name", "x"), (AxmlWriter.AndroidNs, "value", Vendor)));
            var ex = Assert.Throws<InvalidDataException>(() => ManifestPatch.Apply(AxmlWriter.Write(root), null));
            Assert.Contains("meta-data", ex.Message);
        }

        [Fact]
        public void RejectsATextManifest()
        {
            Assert.Throws<InvalidDataException>(() => ManifestPatch.Apply(Encoding.UTF8.GetBytes("<manifest/>"), null));
        }

        [Fact]
        public void Aapt2ReadsThePatchedSyntheticManifest()
        {
            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): skipped");
                return;
            }
            byte[] patched = ManifestPatch.Apply(AxmlWriter.Write(AxmlWriter.GameLikeManifest()), null);
            string dump = XmlTree(patched);
            Assert.Equal(new[] { Package + ".permission.C2D_MESSAGE", Scoped }, DeclaredPermissionsIn(dump));
            Assert.DoesNotContain(Vendor + "\"", dump.Replace(Scoped, ""));
        }

        /// <summary>
        /// The player's real 1.17.32 manifest: byte-exact round trip, and after the patch aapt2's dump is the original dump
        /// with exactly the three glshare uses renamed; every declared permission is in the package's namespace.
        /// </summary>
        [Fact]
        public void TheRealManifestOnlyChangesTheVendorPermission()
        {
            string apk = TestEnvironment.GameApk;
            if (apk == null)
            {
                output.WriteLine("original APK (MCVS_APK) not available: skipped");
                return;
            }
            byte[] manifest = ZipArchiveReader.Read(apk).Find(ManifestPatch.EntryName).GetData();
            Assert.Equal(manifest, BinaryXml.Parse(manifest).ToArray());
            var notes = new List<string>();
            byte[] patched = ManifestPatch.Apply(manifest, notes);
            Assert.NotNull(patched);
            Assert.Single(notes);
            output.WriteLine(notes[0]);
            Assert.Contains("<permission android:name>, <uses-permission android:name>, <provider android:permission>", notes[0]);

            if (TestEnvironment.Aapt2 == null)
            {
                output.WriteLine("aapt2 not found (set MCVS_BUILD_TOOLS): dump comparison skipped");
                return;
            }
            string original = XmlTree(manifest), result = XmlTree(patched);
            Assert.Equal(6, Regex.Matches(original, Regex.Escape("\"" + Vendor + "\"")).Count); // value + Raw, three times
            Assert.Equal(original.Replace("\"" + Vendor + "\"", "\"" + Scoped + "\""), result);
            Assert.All(DeclaredPermissionsIn(result), p => Assert.StartsWith(Package + ".", p));
        }

        /// <summary><c>aapt2 dump xmltree</c> of a manifest, through a zip that holds only it.</summary>
        private string XmlTree(byte[] manifest)
        {
            string dir = TestEnvironment.NewTempDir("aapt2");
            string zip = Path.Combine(dir, "m.apk");
            File.WriteAllBytes(zip, ZipArchiveWriter.Write(new List<ZipEntry> { ZipEntry.CreateDeflated(ManifestPatch.EntryName, manifest, null) }));
            int code = BuildTools.Run(TestEnvironment.Aapt2, new[] { "dump", "xmltree", "--file", ManifestPatch.EntryName, zip }, null, out string text);
            Assert.True(code == 0, "aapt2 dump xmltree failed: " + text);
            return text;
        }

        /// <summary>The android:name of every &lt;permission&gt; element in an aapt2 xmltree dump.</summary>
        internal static List<string> DeclaredPermissionsIn(string dump)
        {
            var names = new List<string>();
            string[] lines = dump.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "E: permission" && !lines[i].Trim().StartsWith("E: permission (", StringComparison.Ordinal)) continue;
                for (int j = i + 1; j < lines.Length && lines[j].TrimStart().StartsWith("A: ", StringComparison.Ordinal); j++)
                {
                    Match m = Regex.Match(lines[j], @"android:name\(0x01010003\)=""([^""]*)""");
                    if (m.Success) names.Add(m.Groups[1].Value);
                }
            }
            return names;
        }
    }
}
