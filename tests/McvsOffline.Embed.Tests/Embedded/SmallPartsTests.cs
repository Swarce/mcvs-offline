using System.IO;
using System.Linq;
using System.Net;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    public class DeviceIdentityTests
    {
        [Fact]
        public void GeneratedOncePersistedAndReused()
        {
            string dir = TestEnvironment.NewTempDir("devid");
            string first = DeviceIdentity.LoadOrCreate(dir, null, null);
            string second = DeviceIdentity.LoadOrCreate(dir, "mcvsother", null);

            Assert.True(DeviceIdentity.IsValid(first));
            Assert.StartsWith("mcvs", first);
            Assert.Equal(first, second);
            Assert.Equal(first + "\n", File.ReadAllText(Path.Combine(dir, DeviceIdentity.FileName)));
        }

        [Fact]
        public void LostFileAdoptsTheValueFromArgsTxt()
        {
            string dir = TestEnvironment.NewTempDir("devid");
            Assert.Equal("mcvsfromargs", DeviceIdentity.LoadOrCreate(dir, "mcvsfromargs", null));
            Assert.Equal("mcvsfromargs", DeviceIdentity.LoadOrCreate(dir, null, null));
        }

        [Fact]
        public void InvalidStoredValueIsReplaced()
        {
            string dir = TestEnvironment.NewTempDir("devid");
            File.WriteAllText(Path.Combine(dir, DeviceIdentity.FileName), "has-dash:and colon");
            string id = DeviceIdentity.LoadOrCreate(dir, "bad value", null);
            Assert.True(DeviceIdentity.IsValid(id));
            Assert.NotEqual("has-dash:and colon", id);
        }

        [Theory]
        [InlineData("mcvs0123", true)]
        [InlineData("abc", false)]
        [InlineData("with-dash", false)]
        [InlineData("with:colon", false)]
        [InlineData("with space", false)]
        public void Validation(string id, bool valid)
        {
            Assert.Equal(valid, DeviceIdentity.IsValid(id));
        }
    }

    public class ManagedArgsTests
    {
        [Fact]
        public void DefaultsMatchTheArchitecture()
        {
            var args = ManagedArgs.Build(new Settings(), "mcvsid", 51780, null);
            Assert.Equal(new[]
            {
                "-ServerLocation Embedded", "-AnubisCommand QuickLaunch", "-EnvironmentSwitcherUrl http://127.0.0.1:51780/",
                "-SuperCredential game:mcvsoffline", "-SuperPassword mcvsoffline", "-DeviceId mcvsid",
            }, args.Select(a => a.ToString()));
        }

        [Fact]
        public void LanModeAddsLocalAnubisHost()
        {
            var settings = new Settings();
            settings.Set("lanMode", true);
            Assert.Contains(ManagedArgs.Build(settings, "mcvsid", 51780, null), a => a.ToString() == "-LocalAnubisHost 0.0.0.0");
        }

        [Theory]
        [InlineData("game:mcvsoffline", true)]
        [InlineData("game:other_name", true)]
        [InlineData("nocolon", false)]
        [InlineData("a:b:c", false)]
        [InlineData("game:with-dash", false)]
        [InlineData(":x", false)]
        [InlineData("game:", false)]
        public void SuperCredentialValidation(string credential, bool valid)
        {
            Assert.Equal(valid, ManagedArgs.IsValidSuperCredential(credential));
        }
    }

    public class LanSupportTests
    {
        private static readonly IPAddress[] Interfaces =
        {
            IPAddress.Parse("100.64.3.4"), IPAddress.Parse("10.20.30.40"), IPAddress.Parse("192.168.1.23"),
        };

        [Fact]
        public void LoopbackSetupKeepsLocalhost()
        {
            Assert.Equal("localhost", LanSupport.SelectPublicHost("1.2.3.4", "5.6.7.8", "localhost", Interfaces));
        }

        [Fact]
        public void ConfiguredThenGsRemoteIpThenPrivateLanAddressThenDetected()
        {
            Assert.Equal("192.168.50.1", LanSupport.SelectPublicHost(" 192.168.50.1 ", "10.0.0.9", "100.64.3.4", Interfaces));
            Assert.Equal("10.0.0.9", LanSupport.SelectPublicHost("", "10.0.0.9", "100.64.3.4", Interfaces));
            Assert.Equal("192.168.1.23", LanSupport.SelectPublicHost(null, null, "100.64.3.4", Interfaces));
            Assert.Equal("10.20.30.40", LanSupport.SelectPublicHost(null, null, "100.64.3.4", Interfaces.Take(2).ToList()));
            Assert.Equal("100.64.3.4", LanSupport.SelectPublicHost(null, null, "100.64.3.4", new IPAddress[0]));
        }

        [Fact]
        public void FinderHostHonoursRemoteIp()
        {
            Assert.Equal("192.168.1.2", LanSupport.ChooseFinderHost(" 192.168.1.2 ", "localhost"));
            Assert.Equal("localhost", LanSupport.ChooseFinderHost(null, "localhost"));
            Assert.Equal("localhost", LanSupport.ChooseFinderHost("  ", "localhost"));
        }

        [Fact]
        public void CacheIsSetAndReset()
        {
            LanSupport.ResetPublicHost();
            Assert.Null(LanSupport.GetCachedPublicHost());
            LanSupport.SetPublicHost(null, "localhost");
            Assert.Equal("localhost", LanSupport.GetCachedPublicHost());
            LanSupport.ResetPublicHost();
            Assert.Null(LanSupport.GetCachedPublicHost());
        }
    }
}
