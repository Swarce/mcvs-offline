using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Matchmaker;
using Xunit;

namespace McvsOffline.Tests.Content
{
    /// <summary>matchmaker lobbyconfigs: the cmd_args the in-process TS/GS Worlds are built with.</summary>
    public class MatchmakerTests
    {
        /// <summary>
        /// The client's own parsing, replicated: LA/Controller.cs GetConfigArgs (split on " -" and "-", first space of
        /// each piece becomes a newline) followed by EN/ApplicationArguments.cs Parse (newline-separated, "-Key" then a
        /// value unless the field is a bool flag).
        /// </summary>
        private static Dictionary<string, string> ParseLikeTheGame(string cmdArgs, params string[] boolFlags)
        {
            string[] source = cmdArgs.Split(new[] { " -", "-" }, StringSplitOptions.RemoveEmptyEntries);
            var regex = new Regex(" ");
            string configArgs = string.Join("\n", source.Select(s => "-" + regex.Replace(s, "\n", 1)));
            string[] array = configArgs.Trim('\n').Split('\n');
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < array.Length; i++)
            {
                if (!array[i].StartsWith("-")) continue;
                string key = array[i].Substring(1);
                if (boolFlags.Contains(key)) { result[key] = "true"; continue; }
                i++;
                Assert.True(i < array.Length, "Need to set a value for " + key);
                result[key] = array[i];
            }
            return result;
        }

        private static Dictionary<string, object> Lobby(McvsServerHandle h, string type, string host = "127.0.0.1:51780")
        {
            var response = ContentTestSupport.Get(h.Server, "/lobbyconfigs/" + ContentTestSupport.ClientId + "/" + type + "?access_token=x", host);
            Assert.Equal(200, response.StatusCode);
            return ContentTestSupport.JsonObject(response);
        }

        [Fact]
        public void TsConfigLoadsTheMenusLevelWithOurFederation()
        {
            using var h = new McvsServerHandle();
            var cfg = Lobby(h, "ts");
            Assert.Equal("ts", JsonPath.Get(cfg, "game.server_type"));
            Assert.Equal(0L, JsonPath.Get(cfg, "game.room_launch_timeout"));
            string cmd = (string)JsonPath.Get(cfg, "game.cmd_args");
            Assert.Equal("-Level Levels/Menus.level -ServerType ts -EnvironmentSwitcherUrl http://127.0.0.1:51780/ -DataCenter mcvs "
                         + "-SuperCredential game:mcvsoffline -SuperPassword mcvsoffline", cmd);

            var args = ParseLikeTheGame(cmd);
            Assert.Equal("Levels/Menus.level", args["Level"]);
            Assert.Equal("ts", args["ServerType"]);
            Assert.Equal("http://127.0.0.1:51780/", args["EnvironmentSwitcherUrl"]);
            Assert.Equal("mcvs", args["DataCenter"]);
            Assert.Equal("game:mcvsoffline", args["SuperCredential"]);
            Assert.Equal("mcvsoffline", args["SuperPassword"]);
        }

        [Fact]
        public void GsConfigHasNoLevelAndRoomForEightPlayers()
        {
            using var h = new McvsServerHandle();
            var cfg = Lobby(h, "GS");
            Assert.Equal("gs", JsonPath.Get(cfg, "game.server_type"));
            Assert.Equal(8L, JsonPath.Get(cfg, "game.room_capacity"));
            Assert.Equal(false, JsonPath.Get(cfg, "game.midgame_join"));
            string cmd = (string)JsonPath.Get(cfg, "game.cmd_args");
            Assert.DoesNotContain("-Level", cmd);
            var args = ParseLikeTheGame(cmd);
            Assert.Equal("gs", args["ServerType"]);
            Assert.Equal("game:mcvsoffline", args["SuperCredential"]);

            // GameConfig.GetAutomacher(First by name) must find the template's automatcher.
            var names = new List<string>();
            foreach (var kv in (Dictionary<string, object>)cfg["automatchers"]) names.Add((string)JsonPath.Get(kv.Value, "name"));
            Assert.Contains("pvpdeathmatch", names);
            Assert.Contains("dev", names);
        }

        [Fact]
        public void LanModeLetsHumansShareRoomsAndKeepsCmdArgsSafe()
        {
            using var h = new McvsServerHandle(lan: true);
            var cfg = Lobby(h, "gs", host: "my-host-pc:51780");
            Assert.Equal(true, JsonPath.Get(cfg, "game.midgame_join"));
            Assert.Equal(true, JsonPath.Get(cfg, "automatchers.a1.midgame_join"));
            // A host name with '-' cannot travel in cmd_args (the Controller splits on '-'): loopback instead.
            Assert.Contains("-EnvironmentSwitcherUrl http://127.0.0.1:51780/", (string)JsonPath.Get(cfg, "game.cmd_args"));
            Assert.True(h.Logs.Contains("contains '-' or spaces"));

            cfg = Lobby(h, "gs", host: "192.168.1.20:51780");
            Assert.Contains("-EnvironmentSwitcherUrl http://192.168.1.20:51780/", (string)JsonPath.Get(cfg, "game.cmd_args"));
        }

        [Fact]
        public void ExtraArgumentsAreAppendedOnlyWhenSafe()
        {
            using var h = new McvsServerHandle();
            h.Server.Context.Settings.Override("matchmaker", MiniJson.ParseObject(
                @"{""tsArgs"":""-AllowP2P -LocalAnubisHost 0.0.0.0"",""gsArgs"":""-GSRemoteIp my-host"",""passDataCenter"":false,""automatchers"":[""arena""]}"));
            var ts = Lobby(h, "ts");
            string cmd = (string)JsonPath.Get(ts, "game.cmd_args");
            Assert.EndsWith("-SuperPassword mcvsoffline -AllowP2P -LocalAnubisHost 0.0.0.0", cmd);
            Assert.DoesNotContain("-DataCenter", cmd);
            var args = ParseLikeTheGame(cmd, "AllowP2P");
            Assert.Equal("true", args["AllowP2P"]);
            Assert.Equal("0.0.0.0", args["LocalAnubisHost"]);
            Assert.Equal("arena", JsonPath.Get(ts, "automatchers.a1.name"));

            var gs = Lobby(h, "gs");
            Assert.EndsWith("-SuperPassword mcvsoffline", (string)JsonPath.Get(gs, "game.cmd_args")); // rejected: '-' in value
            Assert.True(h.Logs.Contains("matchmaker.gsArgs"));
        }

        [Theory]
        [InlineData("-AllowP2P", "-AllowP2P")]
        [InlineData("  -A 1   -B\t2 ", "-A 1 -B 2")]
        [InlineData("value -A", "")]
        [InlineData("-A x-y", "")]
        [InlineData("-A- 1", "")]
        [InlineData("", "")]
        public void ExtraArgumentValidation(string input, string expected)
        {
            Assert.Equal(expected, LobbyConfigBuilder.CheckExtraArgs(input, "ts", Log.Null));
        }

        [Fact]
        public void UnexpectedServerTypesAndRoomCalls()
        {
            using var h = new McvsServerHandle();
            var other = Lobby(h, "private");
            Assert.Equal("private", JsonPath.Get(other, "game.server_type"));
            Assert.DoesNotContain("-Level", (string)JsonPath.Get(other, "game.cmd_args"));
            Assert.Equal(404, ContentTestSupport.Get(h.Server, "/lobbyconfigs/" + ContentTestSupport.ClientId + "/a.b").StatusCode);

            // In Embedded mode rooms are LocalAnubis' business; if they reach us something is misconfigured.
            Assert.Equal("[]", ContentTestSupport.Get(h.Server, "/rooms/" + ContentTestSupport.ClientId + "?server_type=gs").BodyText);
            Assert.Equal(404, ContentTestSupport.Post(h.Server, "/rooms/" + ContentTestSupport.ClientId + "/quick_launch", "server_type=ts").StatusCode);
            Assert.Equal(404, ContentTestSupport.Post(h.Server, "/rooms/" + ContentTestSupport.ClientId + "/automatch/pvpdeathmatch", "server_type=gs").StatusCode);
            Assert.True(h.Logs.Contains("Embedded"));
        }
    }
}
