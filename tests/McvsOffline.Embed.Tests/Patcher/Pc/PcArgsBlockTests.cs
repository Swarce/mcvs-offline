using System.Collections.Generic;
using System.Text;
using McvsOffline.Embedded;
using McvsOffline.Patcher.Pc;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary>The uninstall side of the args.txt block (docs/design/pc-port.md §3.4): the markers are ArgsFile's, and Remove undoes Merge.</summary>
    public class PcArgsBlockTests
    {
        private static readonly List<ArgEntry> Managed = new List<ArgEntry>
        {
            new ArgEntry("ServerLocation", "Embedded"), new ArgEntry("DeviceId", "mcvs0123456789abcdef0123456789abcdef"),
        };

        [Fact]
        public void TheMarkersAreArgsFiles()
        {
            Assert.Equal(ArgsFile.BeginMarker, PcArgsBlock.BeginMarker);
            Assert.Equal(ArgsFile.EndMarker, PcArgsBlock.EndMarker);
            Assert.Equal(ArgsFile.KnownManagedKeys, PcArgsBlock.KnownManagedKeys);
        }

        [Theory]
        [InlineData("")]
        [InlineData("-Foo bar\n")]
        [InlineData("-Foo bar\r\n-Baz 1\r\n")]
        [InlineData("-Foo bar\n\n-Baz 1\n")]
        public void RemoveGivesBackThePlayersFile(string players)
        {
            byte[] original = Encoding.ASCII.GetBytes(players);
            byte[] merged = ArgsFile.Merge(original, Managed).Content;
            Assert.True(PcArgsBlock.HasBlock(merged));
            bool onlyBlock;
            byte[] removed = PcArgsBlock.Remove(merged, out onlyBlock);
            Assert.Equal(players, Encoding.ASCII.GetString(removed));
            Assert.Equal(players.Trim().Length == 0, onlyBlock);
            Assert.False(PcArgsBlock.HasBlock(removed));
        }

        [Fact]
        public void ABlockWithoutItsEndMarkerIsRemovedWithEveryManagedLine()
        {
            // WR2 F10: the host writes -DataCenter; with the end marker deleted by hand, uninstall left it as a player line.
            var settings = new McvsOffline.Server.Core.Settings();
            settings.Set("lanMode", true);
            string written = Encoding.ASCII.GetString(ArgsFile.Merge(null, ManagedArgs.Build(settings, "mcvs0123456789abcdef", 51780, null)).Content);
            Assert.Contains("-DataCenter mcvs\n", written);
            Assert.Contains("-LocalAnubisHost 0.0.0.0\n", written);
            string withoutEnd = "-Foo bar\n" + written.Replace(PcArgsBlock.EndMarker + "\n", "") + "-Baz 1\n";
            Assert.DoesNotContain(PcArgsBlock.EndMarker, withoutEnd);

            bool onlyBlock;
            byte[] removed = PcArgsBlock.Remove(Encoding.ASCII.GetBytes(withoutEnd), out onlyBlock);
            Assert.Equal("-Foo bar\n-Baz 1\n", Encoding.ASCII.GetString(removed));
            Assert.False(onlyBlock);
            Assert.Equal("", Encoding.ASCII.GetString(PcArgsBlock.Remove(Encoding.ASCII.GetBytes(written.Replace(PcArgsBlock.EndMarker + "\n", "")), out onlyBlock)));
            Assert.True(onlyBlock);
        }

        [Fact]
        public void AFileWrittenFromNothingLeavesOnlyTheBlock()
        {
            bool onlyBlock;
            Assert.Empty(PcArgsBlock.Remove(ArgsFile.Merge(null, Managed).Content, out onlyBlock));
            Assert.True(onlyBlock);
            Assert.False(PcArgsBlock.HasBlock(null));
            Assert.Empty(PcArgsBlock.Remove(null, out onlyBlock));
            Assert.True(onlyBlock);
        }
    }
}
