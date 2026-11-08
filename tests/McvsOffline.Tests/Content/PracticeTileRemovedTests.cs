using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Tests.Admin;
using Xunit;

namespace McvsOffline.Tests.Content
{
    /// <summary>
    /// Build 21 removed the setting <c>crm.practiceTile</c> (the hidden PRACTICE tile of the main menu, never visible even
    /// when on) and its patch <c>bottile</c>. A settings.json of an earlier build that still has the key must keep loading
    /// as it is (no repair, no fallback, every other key kept on disk), the key is no longer a setting of the page, and the
    /// CRM document is build 20's, whatever the old key says: build 20 wrote nothing for the default (off).
    /// </summary>
    public class PracticeTileRemovedTests
    {
        /// <summary>
        /// The CRM documents of build 20 (10ea093) with the default settings, produced by this exact setup
        /// (<see cref="Server"/>: every module plus admin, port 51780, clock <see cref="CrmNewsfeedTests.Wednesday"/>, a fresh
        /// data folder whose settings.json has only <see cref="OtherKeys"/>, no data pack or the sample preconfiguration, no
        /// token): SHA-256 of the UTF-8 body and its length. If generated content or another CRM layer changes on purpose,
        /// rerun this test: the failure message gives the new values.
        /// </summary>
        private static readonly Dictionary<string, KeyValuePair<string, int>> Build20 = new Dictionary<string, KeyValuePair<string, int>>
        {
            ["nopre /configs/users/me"] = Pair("972dd7661992faa963e1a341fc3c6c806554196afd07e1e599f8f2bfd62f3949", 403511),
            ["nopre /configs/users/me/game"] = Pair("e858e7b62289f535bf3781e245c2763ebbdd2162ae479743581f3c5b1d57e18b", 2426),
            ["pre /configs/users/me"] = Pair("c87042394c342a8c9d31a0ec17bbc7995bbf4885987305b757b84594085eb1cd", 403702),
            ["pre /configs/users/me/game"] = Pair("47fd2d8359ba4dde9ced85d360c5ee30e2397b2fed474c1e7c5b2c21a717b2b8", 2548),
        };

        /// <summary>Keys of a player's settings.json that do not change the CRM document (kept on disk next to the old key).</summary>
        private const string OtherKeys = "\"geo\": {\"country\": \"FR\"}, \"storage\": {\"backups\": {\"keep\": 7}}, \"myOwnNote\": \"keep me\"";

        private static KeyValuePair<string, int> Pair(string sha256, int length) => new KeyValuePair<string, int>(sha256, length);

        private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        /// <summary>A settings.json with <see cref="OtherKeys"/> and, unless null, <c>"crm": {"practiceTile": tile}</c>.</summary>
        private static string SettingsFile(string tile)
        {
            return "{" + OtherKeys + (tile == null ? "" : ", \"crm\": {\"practiceTile\": " + tile + "}") + "}";
        }

        private static McvsServer Server(TempDir dir, string settingsJson, bool preconfiguration, MemoryLogSink logs = null)
        {
            string data = dir.Combine("data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, Settings.FileName), settingsJson, new UTF8Encoding(false));
            string pack = preconfiguration ? ContentTestSupport.CreateFakeDataPack(dir.Combine("pack"), ContentTestSupport.SamplePreconfiguration()) : null;
            return AdminTestClient.CreateServer(data, logs, clock: new ManualClock(CrmNewsfeedTests.Wednesday), configure: o => o.DataPackDir = pack);
        }

        /// <summary>
        /// The served documents are build 20's for the default settings, and a settings.json that still says
        /// <c>crm.practiceTile: true</c> (build 20: two SET cells on MainMenuElements row 13) or false gives the same bytes.
        /// </summary>
        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "false")]
        [InlineData(false, "true")]
        [InlineData(true, null)]
        [InlineData(true, "false")]
        [InlineData(true, "true")]
        public void TheDocumentIsBuild20sWhateverTheOldKeySays(bool preconfiguration, string tile)
        {
            string variant = preconfiguration ? "pre" : "nopre";
            using var dir = new TempDir();
            using var server = Server(dir, SettingsFile(tile), preconfiguration);
            Assert.False(server.Context.Settings.IsFallback);
            foreach (string route in new[] { "/configs/users/me", "/configs/users/me/game" })
            {
                string text = ContentTestSupport.Get(server, route).BodyText;
                string name = variant + " " + route;
                KeyValuePair<string, int> expected = Build20[name];
                string actual = Sha256(text);
                Assert.True(expected.Key == actual && expected.Value == text.Length,
                            name + " (crm.practiceTile " + (tile ?? "absent") + ") differs from build 20's: now sha256 " + actual + ", " + text.Length + " chars");
                Assert.DoesNotContain("MainMenuElements", text);
            }
        }

        /// <summary>
        /// An earlier build's settings.json with <c>crm.practiceTile</c> loads as it is: no fallback, no load error, no repair
        /// panel on the page; the key and every other key stay in the file, also after the page saves another setting; the
        /// page no longer lists it or accepts a change to it (it is not a setting any more).
        /// </summary>
        [Fact]
        public void AnOldSettingsFileWithTheKeyLoadsAndKeepsItsKeys()
        {
            using var dir = new TempDir();
            var logs = new MemoryLogSink();
            using var server = Server(dir, SettingsFile("true"), false, logs);
            Settings settings = server.Context.Settings;
            Assert.False(settings.IsFallback);
            Assert.Null(settings.LoadError);
            Assert.Equal(true, settings.Get("crm.practiceTile"));
            Assert.Equal("FR", settings.Get("geo.country"));

            var client = new AdminTestClient(server);
            client.OpenSession();
            var body = AdminTestClient.Json(client.Get("settings"));
            Assert.Equal(false, body["fallback"]);
            Assert.Null(body["error"]);
            var keys = ((List<object>)body["groups"]).Cast<Dictionary<string, object>>()
                .SelectMany(g => ((List<object>)g["keys"]).Cast<Dictionary<string, object>>())
                .Select(k => (string)k["key"]).ToList();
            Assert.Contains("instantBots", keys);
            Assert.DoesNotContain("crm.practiceTile", keys);
            Assert.Null(SettingsSchema.Find("crm.practiceTile"));

            // Not editable: the page refuses it and writes nothing.
            string before = File.ReadAllText(settings.FilePath);
            var refused = client.Post("settings", new Dictionary<string, object> { ["changes"] = new Dictionary<string, object> { ["crm.practiceTile"] = false } });
            Assert.Equal(400, refused.StatusCode);
            Assert.Equal(before, File.ReadAllText(settings.FilePath));

            // Saving another setting keeps the old key and the player's own keys in the file.
            var saved = client.Post("settings", new Dictionary<string, object> { ["changes"] = new Dictionary<string, object> { ["instantBots"] = false } });
            Assert.Equal(200, saved.StatusCode);
            Dictionary<string, object> disk = MiniJson.ParseObject(File.ReadAllText(settings.FilePath));
            Assert.Equal(true, JsonPath.Get(disk, "crm.practiceTile"));
            Assert.Equal("FR", JsonPath.Get(disk, "geo.country"));
            Assert.Equal(7L, JsonPath.Get(disk, "storage.backups.keep"));
            Assert.Equal("keep me", disk["myOwnNote"]);
            Assert.Equal(false, disk["instantBots"]);
            Assert.False(logs.Contains("practiceTile"), "a log line names the old key");
        }
    }
}
