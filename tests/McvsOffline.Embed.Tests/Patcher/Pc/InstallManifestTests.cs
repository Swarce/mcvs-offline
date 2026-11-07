using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Pc;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Embed.Tests.Patcher.Pc
{
    /// <summary><c>install.json</c> format 1 (docs/design/pc-port.md §3.10, §7.5): round trip, key order, MiniJson reads it.</summary>
    public class InstallManifestTests
    {
        public static InstallManifest Sample()
        {
            var m = new InstallManifest
            {
                InstallerName = "McvsOffline.PcKit", InstallerVersion = "0.1.0", ServerBuild = "13", WrittenUtc = "2026-11-02T18:00:00Z",
                BuildId = "pc-steam-6372049", GameVersion = "1.17.30", ClientId = "2620:70425:1.17.30:steam:steam",
                ExeSha256 = "884f74611cb0daccbe889b054bda16d6ba96d5da4911e1bcd38d38d675231852",
                Port = 51780, Bcl = "C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319",
            };
            m.Originals.Add(new InstallOriginal { File = "EngineBridge.dll", Sha256 = "ce26", Mvid = new Guid("b5913b9a-f9dc-42d6-bd3a-435a8280e8f1") });
            m.Originals.Add(new InstallOriginal { File = "Game.dll", Sha256 = "7459", Mvid = new Guid("577aad49-7a49-41ba-86f1-ff84bbbb33d9") });
            m.Written.Add(new InstallWritten
            {
                Path = "McvsOffline/bin/EngineBridge.dll", Sha256 = "aa", Mvid = Guid.Parse("11111111-2222-4333-8444-555555555555"),
                Version = new Version(1, 0, 0, 0), Patches = new List<string> { "hook-pc" },
            });
            m.Written.Add(new InstallWritten
            {
                Path = "McvsOffline/bin/McvsOffline.Server.dll", Sha256 = "bb", Mvid = Guid.Parse("66666666-2222-4333-8444-555555555555"),
                Version = new Version(0, 0, 0, 0), ServerVersion = "0.1.0-dev", GameLog = true,
            });
            m.Written.Add(new InstallWritten { Path = "ModernCombatVersus.exe.config", Sha256 = "cc" });
            m.Patches.AddRange(new[] { "hook-pc", "headless" });
            m.Notes.Add("a note with \"quotes\", a back\\slash and ü");
            return m;
        }

        [Fact]
        public void RoundTrips()
        {
            InstallManifest m = Sample();
            InstallManifest back = InstallManifest.Parse(m.ToJson());
            Assert.Equal(m.ToJson(), back.ToJson());
            Assert.Equal(51780, back.Port);
            Assert.Equal("0.1.0-dev", back.FindWritten("McvsOffline/bin/McvsOffline.Server.dll").ServerVersion);
            Assert.True(back.FindWritten("McvsOffline/bin/McvsOffline.Server.dll").GameLog);
            Assert.Null(back.FindWritten("ModernCombatVersus.exe.config").Mvid);
            Assert.Equal(new[] { "hook-pc" }, back.FindWritten("McvsOffline/bin/EngineBridge.dll").Patches);
            Assert.Equal(new Guid("577aad49-7a49-41ba-86f1-ff84bbbb33d9"), back.FindOriginal("Game.dll").Mvid);
            Assert.Equal("a note with \"quotes\", a back\\slash and ü", back.Notes.Single());
        }

        [Fact]
        public void KeysAreInTheDesignsOrderAndTheBytesHaveNoBom()
        {
            string json = Sample().ToJson();
            string[] keys = { "\"format\"", "\"kind\"", "\"installer\"", "\"build\"", "\"originals\"", "\"written\"", "\"options\"", "\"notes\"" };
            int last = -1;
            foreach (string key in keys)
            {
                int at = json.IndexOf(key, StringComparison.Ordinal);
                Assert.True(at > last, key + " out of order");
                last = at;
            }
            Assert.True(json.IndexOf("\"name\"", StringComparison.Ordinal) < json.IndexOf("\"writtenUtc\"", StringComparison.Ordinal));
            Assert.True(json.IndexOf("\"port\"", StringComparison.Ordinal) < json.IndexOf("\"bcl\"", StringComparison.Ordinal));
            byte[] bytes = Sample().ToBytes();
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal(json, Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public void MiniJsonReadsItWithTheDayZeroKeys()
        {
            Dictionary<string, object> root = MiniJson.ParseObject(Sample().ToJson());
            Assert.Equal(new[] { "format", "kind", "installer", "build", "originals", "written", "options", "notes" }, root.Keys.ToArray());
            Assert.Equal(1L, root["format"]);
            Assert.Equal("mcvs-pc-install", root["kind"]);
            var originals = (Dictionary<string, object>)root["originals"];
            Assert.Equal("ce26", ((Dictionary<string, object>)originals["EngineBridge.dll"])["sha256"]);
            var written = (Dictionary<string, object>)root["written"];
            Assert.Equal("cc", ((Dictionary<string, object>)written["ModernCombatVersus.exe.config"])["sha256"]);
            Assert.Equal(51780L, ((Dictionary<string, object>)root["options"])["port"]);
            var server = (Dictionary<string, object>)written["McvsOffline/bin/McvsOffline.Server.dll"];
            Assert.Equal(true, server["gameLog"]);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1]")]
        [InlineData("{\"kind\":\"mcvs-pc-install\"}")]
        [InlineData("{\"format\":2,\"kind\":\"mcvs-pc-install\"}")]
        [InlineData("{\"format\":1,\"kind\":\"something else\"}")]
        public void OtherFormatsAreRefused(string json)
        {
            Assert.Throws<FormatException>(() => InstallManifest.Parse(json));
        }

        [Fact]
        public void AnotherFormatSaysSo()
        {
            Assert.Equal("unknown install.json format", Assert.Throws<FormatException>(() => InstallManifest.Parse("{\"format\":2}")).Message);
        }
    }
}
