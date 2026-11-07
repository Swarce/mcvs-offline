using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    public class ArgsFileTests
    {
        private static List<ArgEntry> Managed(int port = 51780, string deviceId = "mcvs0123456789abcdef")
        {
            return new List<ArgEntry>
            {
                new ArgEntry("ServerLocation", "Embedded"),
                new ArgEntry("AnubisCommand", "QuickLaunch"),
                new ArgEntry("EnvironmentSwitcherUrl", "http://127.0.0.1:" + port + "/"),
                new ArgEntry("SuperCredential", "game:mcvsoffline"),
                new ArgEntry("SuperPassword", "mcvsoffline"),
                new ArgEntry("DeviceId", deviceId),
            };
        }

        private static byte[] Bytes(string text) => text.Select(c => (byte)c).ToArray();

        private static string Text(byte[] bytes) => new string(bytes.Select(b => (char)b).ToArray());

        [Fact]
        public void NewFileGivesTheGameEveryManagedArgument()
        {
            ArgsMergeResult result = ArgsFile.Merge(null, Managed());

            Assert.True(result.Changed);
            var game = GameArgsSimulator.Parse(result.Content);
            Assert.Equal("Embedded", game["ServerLocation"]);
            Assert.Equal("QuickLaunch", game["AnubisCommand"]);
            Assert.Equal("http://127.0.0.1:51780/", game["EnvironmentSwitcherUrl"]);
            Assert.Equal("game:mcvsoffline", game["SuperCredential"]);
            Assert.Equal("mcvsoffline", game["SuperPassword"]);
            Assert.Equal("mcvs0123456789abcdef", game["DeviceId"]);
            Assert.Equal(6, game.Count);
        }

        [Fact]
        public void WrittenBlockUsesOnlySpacesAndLineFeedsAndNoBom()
        {
            byte[] content = ArgsFile.Merge(null, Managed()).Content;

            Assert.DoesNotContain((byte)'\t', content);
            Assert.DoesNotContain((byte)'\r', content);
            Assert.NotEqual(0xEF, content[0]);
            Assert.All(content, b => Assert.True(b == '\n' || (b >= 0x20 && b < 0x7F)));
            string text = Text(content);
            Assert.StartsWith(ArgsFile.BeginMarker + " ", text);
            Assert.EndsWith(ArgsFile.EndMarker + "\n", text);
            // The hint is the value of an ignored argument: none of its words may start a new argument.
            Assert.DoesNotContain(ArgsFile.Tokenize(ArgsFile.BlockHint), t => t.StartsWith("-"));
        }

        [Fact]
        public void MergeIsIdempotent()
        {
            byte[] user = Bytes("-LogCategoryFilter Fatal\r\n-Offline\n");
            byte[] first = ArgsFile.Merge(user, Managed()).Content;
            ArgsMergeResult second = ArgsFile.Merge(first, Managed());

            Assert.False(second.Changed);
            Assert.Equal(first, second.Content);
        }

        [Fact]
        public void PlayerLinesArePreservedVerbatimAroundTheBlock()
        {
            string before = "-LogCategoryFilter Fatal\n\xFF\xFE odd bytes kept\n";
            string after = "-DataCenter local\n";
            string old = before + ArgsFile.BeginMarker + " old hint\n-ServerLocation Embedded\n" + ArgsFile.EndMarker + "\n" + after;

            string merged = Text(ArgsFile.Merge(Bytes(old), Managed()).Content);

            Assert.StartsWith(before + ArgsFile.BeginMarker, merged);
            Assert.EndsWith(ArgsFile.EndMarker + "\n" + after, merged);
            Assert.Contains("-DeviceId mcvs0123456789abcdef\n", merged);
        }

        [Fact]
        public void BlockIsUpdatedInPlaceWhenSettingsChange()
        {
            byte[] first = ArgsFile.Merge(Bytes("-Offline\n"), Managed(51780)).Content;
            ArgsMergeResult second = ArgsFile.Merge(first, Managed(52000));

            Assert.True(second.Changed);
            string text = Text(second.Content);
            Assert.StartsWith("-Offline\n" + ArgsFile.BeginMarker, text);
            Assert.Contains("http://127.0.0.1:52000/", text);
            Assert.DoesNotContain("51780", text);
            Assert.Single(AllIndexes(text, ArgsFile.BeginMarker));
        }

        [Fact]
        public void PlayerSettingAManagedKeyWinsCaseInsensitively()
        {
            byte[] user = Bytes("-environmentswitcherurl http://192.168.1.20:51780/\n-ServerLocation Remote\n-RemoteIp 192.168.1.20\n");
            ArgsMergeResult result = ArgsFile.Merge(user, Managed());

            Assert.Equal(new[] { "ServerLocation", "EnvironmentSwitcherUrl" }, result.OverriddenKeys);
            Assert.DoesNotContain(result.Written, e => e.Key == "ServerLocation" || e.Key == "EnvironmentSwitcherUrl");
            var game = GameArgsSimulator.Parse(result.Content);
            Assert.Equal("Remote", game["ServerLocation"]);
            Assert.Equal("http://192.168.1.20:51780/", game["EnvironmentSwitcherUrl"]);
            Assert.Equal("192.168.1.20", game["RemoteIp"]);
            Assert.Equal("QuickLaunch", game["AnubisCommand"]);
        }

        [Fact]
        public void ByteOrderMarkIsRemovedSoTheFirstArgumentCounts()
        {
            byte[] user = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("-LogCategoryFilter Fatal\n")).ToArray();
            Assert.False(GameArgsSimulator.Parse(user).ContainsKey("LogCategoryFilter"));   // what the game did before

            ArgsMergeResult result = ArgsFile.Merge(user, Managed());

            Assert.Contains(result.Warnings, w => w.Contains("byte order mark"));
            Assert.Equal((byte)'-', result.Content[0]);
            Assert.Equal("Fatal", GameArgsSimulator.Parse(result.Content)["LogCategoryFilter"]);
        }

        [Fact]
        public void TabsInPlayerLinesAreReportedAndLeftAlone()
        {
            ArgsMergeResult result = ArgsFile.Merge(Bytes("-Level\tLevels/Menus.level\n"), Managed());

            Assert.Contains(result.Warnings, w => w.Contains("tab"));
            Assert.StartsWith("-Level\tLevels/Menus.level\n", Text(result.Content));
        }

        [Fact]
        public void CrLfFilesGetACrLfBlock()
        {
            string text = Text(ArgsFile.Merge(Bytes("-Offline\r\n-LogCategoryFilter Fatal\r\n"), Managed()).Content);

            Assert.Contains(ArgsFile.EndMarker + "\r\n", text);
            Assert.DoesNotContain("\n-", text.Replace("\r\n-", ""));
            Assert.Equal("Embedded", GameArgsSimulator.Parse(Bytes(text))["ServerLocation"]);
        }

        [Fact]
        public void LastPlayerLineWithoutNewlineIsTerminatedBeforeTheBlock()
        {
            ArgsMergeResult result = ArgsFile.Merge(Bytes("-LogCategoryFilter Fatal"), Managed());

            Assert.StartsWith("-LogCategoryFilter Fatal\n" + ArgsFile.BeginMarker, Text(result.Content));
            var game = GameArgsSimulator.Parse(result.Content);
            Assert.Equal("Fatal", game["LogCategoryFilter"]);
            Assert.Equal("Embedded", game["ServerLocation"]);
        }

        [Fact]
        public void DuplicateBlocksAndStrayEndMarkersAreCleanedUp()
        {
            string block = ArgsFile.BeginMarker + "\n-ServerLocation Online\n" + ArgsFile.EndMarker + "\n";
            string old = block + "-Offline\n" + block + ArgsFile.EndMarker + "\n";

            ArgsMergeResult result = ArgsFile.Merge(Bytes(old), Managed());

            string text = Text(result.Content);
            Assert.Single(AllIndexes(text, ArgsFile.BeginMarker));
            Assert.Single(AllIndexes(text, ArgsFile.EndMarker));
            Assert.EndsWith("-Offline\n", text);
            Assert.Equal("Embedded", GameArgsSimulator.Parse(result.Content)["ServerLocation"]);
        }

        [Fact]
        public void MissingEndMarkerRecoversOnlyManagedLines()
        {
            string old = "-Offline\n" + ArgsFile.BeginMarker + " hint\n-ServerLocation Embedded\n-DeviceId mcvsold\n-Level mylevel\n";

            ArgsMergeResult result = ArgsFile.Merge(Bytes(old), Managed());

            string text = Text(result.Content);
            Assert.Contains(result.Warnings, w => w.Contains("without"));
            Assert.DoesNotContain("mcvsold", text);
            Assert.Contains("-Level mylevel\n", text);
            Assert.Equal("mylevel", GameArgsSimulator.Parse(result.Content)["Level"]);
        }

        [Fact]
        public void MissingEndMarkerRecoversEveryLineManagedArgsWrites()
        {
            // WR2 F10: -DataCenter (written since build 5) was not a known key, so a block whose end marker the player
            // deleted kept "-DataCenter mcvs" as a player line: it overrode settings.dataCenter and survived an uninstall.
            var settings = new Settings();
            settings.Set("lanMode", true);
            List<ArgEntry> written = ManagedArgs.Build(settings, "mcvsold", 51780, null);
            foreach (ArgEntry entry in written) Assert.Contains(entry.Key, ArgsFile.KnownManagedKeys);

            var block = new StringBuilder(ArgsFile.BeginMarker + " hint\n");
            foreach (ArgEntry entry in written) block.Append(entry).Append('\n');
            string old = "-Offline\n" + block + "-Level mylevel\n";   // the end marker deleted by hand

            ArgsMergeResult result = ArgsFile.Merge(Bytes(old), Managed());
            string text = Text(result.Content);
            Assert.Contains(result.Warnings, w => w.Contains("without"));
            Assert.DoesNotContain("-DataCenter", text);
            Assert.DoesNotContain("-LocalAnubisHost", text);
            Assert.DoesNotContain("mcvsold", text);
            Assert.StartsWith("-Offline\n", text);
            Assert.Contains("-Level mylevel\n", text);
            Assert.False(GameArgsSimulator.Parse(result.Content).ContainsKey("DataCenter"));
        }

        [Fact]
        public void ReadManagedValueFindsTheDeviceId()
        {
            byte[] content = ArgsFile.Merge(Bytes("-DeviceId playerid\n"), Managed(deviceId: "mcvsabc")).Content;
            Assert.Null(ArgsFile.ReadManagedValue(content, "DeviceId"));   // the player's own value is outside the block

            content = ArgsFile.Merge(null, Managed(deviceId: "mcvsabc")).Content;
            Assert.Equal("mcvsabc", ArgsFile.ReadManagedValue(content, "DeviceId"));
            Assert.Null(ArgsFile.ReadManagedValue(null, "DeviceId"));
        }

        [Theory]
        [InlineData("Key", "", "empty")]
        [InlineData("Key", "two words", "printable ASCII")]
        [InlineData("Key", "tab\there", "printable ASCII")]
        [InlineData("Key", "-dash", "must not start with '-'")]
        [InlineData("Key", "café", "printable ASCII")]
        [InlineData("Bad Key", "x", "letters")]
        public void InvalidEntriesAreRejected(string key, string value, string reason)
        {
            string error = ArgsFile.ValidateEntry(new ArgEntry(key, value));
            Assert.NotNull(error);
            Assert.Contains(reason, error);
            Assert.Throws<ArgumentException>(() => ArgsFile.Merge(null, new[] { new ArgEntry(key, value) }));
        }

        [Fact]
        public void TokenizerSplitsOnSpaceCrLfOnly()
        {
            Assert.Equal(new[] { "-A", "b\tc", "d" }, ArgsFile.Tokenize("  -A b\tc\r\n\nd "));
        }

        private static List<int> AllIndexes(string text, string value)
        {
            var result = new List<int>();
            for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal)) result.Add(i);
            return result;
        }
    }
}
