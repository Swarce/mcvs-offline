using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;
using Mono.Cecil;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// logs/game.log (WR2 F5, F6): the lines and level filter of <see cref="GameLogFormat"/>, and GameLogBridge itself compiled
    /// against the player's real Gameloft.Logs (Android: MCVS_REFS, required with MCVS_REQUIRE_REAL_CLIENT=1; PC: MCVS_PC_REFS,
    /// required with MCVS_REQUIRE_PC=1) and run on net8 with the game's own logger, set up as each platform sets it up.
    /// </summary>
    public class GameLogTests
    {
        private const string TokenMessage = "[anonymous:76561198366698514] Get access token for scopes [auth, lobby, config] succeeded: "
                                            + "mcvs59915ef0.e681f53f4c247197a50af715d8337ccd";

        [Fact]
        public void TheFileKeepsFatalPlusTheConfiguredLevels()
        {
            bool[] keep = GameLogFormat.AcceptedLevels("error warning");
            Assert.Equal("Fatal Error Warning", GameLogFormat.Describe(keep));
            foreach (int level in new[] { 1, 2, 3 }) Assert.True(GameLogFormat.Keeps(keep, level));
            foreach (int level in new[] { 0, 4, 5, 6, 7, -1, 8, 99 }) Assert.False(GameLogFormat.Keeps(keep, level));

            Assert.Equal("Fatal", GameLogFormat.Describe(GameLogFormat.AcceptedLevels(null)));
            Assert.Equal("Fatal", GameLogFormat.Describe(GameLogFormat.AcceptedLevels("none bogus fatal")));
            Assert.Equal("Fatal Error Info Debug", GameLogFormat.Describe(GameLogFormat.AcceptedLevels("DEBUG,info;Error error")));
            // As in the game: "all" is the level All, not every level.
            Assert.Equal("Fatal All", GameLogFormat.Describe(GameLogFormat.AcceptedLevels("all")));
            // No filter (Android): everything the game logs.
            foreach (int level in new[] { 0, 1, 4, 5, 7, 99 }) Assert.True(GameLogFormat.Keeps(null, level));
        }

        [Fact]
        public void LinesCarryTheUtcTimeAndAreRedacted()
        {
            var local = new DateTime(2026, 10, 28, 13, 53, 25, 781, DateTimeKind.Local);
            string line = GameLogFormat.Line(local, "Debug", "Online", "{Session.cs}.GetAccessToken:384 | " + TokenMessage, null, 8192);
            Assert.Equal(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
                         + " Debug [Online] {Session.cs}.GetAccessToken:384 | [anonymous:7656…8514] Get access token for scopes "
                         + "[auth, lobby, config] succeeded: mcvs…<redacted>", line);
            Assert.Equal("2026-10-28T17:53:25.781Z", GameLogFormat.Stamp(new DateTime(2026, 10, 28, 17, 53, 25, 781, DateTimeKind.Utc)));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", GameLogFormat.Stamp(DateTime.Now));

            string cut = GameLogFormat.Line(DateTime.UtcNow, "Info", "X", new string('a', 300), new InvalidOperationException("boom"), 256);
            Assert.Contains(" Info [X] " + new string('a', 256) + "...[300 chars]\nSystem.InvalidOperationException: boom", cut);
        }

        public static IEnumerable<object[]> Builds()
        {
            yield return new object[] { "android" };
            yield return new object[] { "pc" };
        }

        /// <summary>The real Gameloft.Logs.dll of a build, or null when it is missing and not required.</summary>
        private static string GameloftLogs(string build)
        {
            if (build == "android")
            {
                string dir = TestEnvironment.GameAssemblies;
                string path = dir == null ? null : Path.Combine(dir, "Gameloft.Logs.dll");
                if (path != null && File.Exists(path)) return path;
                Assert.False(Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1", "MCVS_REQUIRE_REAL_CLIENT=1 but Gameloft.Logs.dll is missing (MCVS_REFS)");
                return null;
            }
            string pc = TestEnvironment.PcRefs;
            string pcPath = pc == null ? null : Path.Combine(pc, "Gameloft.Logs.dll");
            if (pcPath != null && File.Exists(pcPath)) return pcPath;
            Assert.False(TestEnvironment.RequirePc, "MCVS_REQUIRE_PC=1 but Gameloft.Logs.dll is missing (MCVS_PC_REFS)");
            return null;
        }

        [Theory]
        [MemberData(nameof(Builds))]
        public void LevelNamesAreTheGamesLogLevel(string build)
        {
            string path = GameloftLogs(build);
            if (path == null) return;
            using (ModuleDefinition module = ModuleDefinition.ReadModule(path))
            {
                TypeDefinition type = module.GetType("Gameloft.Logs", "LogLevel");
                Assert.NotNull(type);
                var names = type.Fields.Where(f => f.IsStatic && f.IsLiteral).OrderBy(f => Convert.ToInt32(f.Constant)).ToList();
                Assert.Equal(GameLogFormat.LevelNames, names.Select(f => f.Name));
                Assert.Equal(Enumerable.Range(0, GameLogFormat.LevelNames.Length), names.Select(f => Convert.ToInt32(f.Constant)));
            }
        }

        /// <summary>Calls the game's logger the way the game does once GameLogBridge is attached.</summary>
        private const string Driver = @"#define ENABLE_LOGGER
using System;
using Gameloft.Logs;

namespace McvsOffline.Embedded.Diagnostics
{
    public static class GameLogDriver
    {
        public static string Run(string path, bool fileKeepsLevels, string levels, string token)
        {
            // What the game does before anyone logs: on Win32NT Log's static constructor, with --game-log all on Android
            // GameApplication's constructor.
            Gameloft.Log.EnableAllLogCategoryFilter();
            var file = new McvsOffline.Server.Core.SafeLogFile(path, new McvsOffline.Server.Core.SafeLogFileOptions { Synchronous = true });
            string applied = GameLogBridge.Apply(levels, ""profile:debug"", file, 8192, fileKeepsLevels);
            Gameloft.Log.AddEntry(LogLevel.Debug, token, ""Online"");
            Gameloft.Log.AddEntry(LogLevel.Info, ""an info line"", ""Online"");
            Gameloft.Log.AddEntry(LogLevel.Warning, ""a warning line"", ""Online"");
            Gameloft.Log.AddEntry(LogLevel.Error, ""an error line"", ""Crm"", null, new InvalidOperationException(""boom""));
            Gameloft.Log.AddEntry(LogLevel.Fatal, ""a fatal line"", ""Engine"");
            Gameloft.Log.AddEntry(LogLevel.Server, ""a server line"", ""Online"");
            Gameloft.Log.AddEntry(LogLevel.Debug, ""a muted profile line"", ""Profile"");
            Gameloft.Log.AddEntry(LogLevel.Error, ""an error of token "" + token.Substring(token.LastIndexOf(' ') + 1), ""Online"");
            file.Dispose();
            return applied;
        }
    }
}
";

        [Theory]
        [MemberData(nameof(Builds))]
        public void TheBridgeOnTheRealLoggerKeepsWhatThePlatformAsksFor(string build)
        {
            string logs = GameloftLogs(build);
            if (logs == null) return;
            byte[] logsImage = File.ReadAllBytes(logs);
            byte[] bridge = FixtureCompiler.Compile("GameLogBridgeUnderTest",
                new[]
                {
                    "#define MCVS_GAMELOG\n" + FixtureCompiler.RepoSource("hosts", "embedded", "Diagnostics", "GameLogBridge.cs"),
                    FixtureCompiler.RepoSource("hosts", "embedded", "Diagnostics", "GameLogFormat.cs"),
                    Driver,
                },
                // This test assembly for GameFatalLines (public, compiled in from hosts/embedded), which the bridge hands Fatal entries to on PC.
                new[] { logsImage, File.ReadAllBytes(typeof(SafeLogFile).Assembly.Location), File.ReadAllBytes(typeof(GameFatalLines).Assembly.Location) });

            foreach (bool pc in new[] { true, false })
            {
                string dir = TestEnvironment.NewTempDir("gamelog");
                try
                {
                    string path = Path.Combine(dir, "game.log");
                    string applied;
                    using (var sandbox = new FixtureCompiler.Sandbox().With("Gameloft.Logs", logsImage).With("GameLogBridgeUnderTest", bridge))
                    {
                        MethodInfo run = sandbox.Get("GameLogBridgeUnderTest").GetType("McvsOffline.Embedded.Diagnostics.GameLogDriver").GetMethod("Run");
                        applied = (string)run.Invoke(null, new object[] { path, pc, "error warning", TokenMessage });
                    }
                    string text = File.ReadAllText(path);
                    string what = build + (pc ? " (PC filter)" : " (no filter)") + ":\n" + text;

                    Assert.Contains("Fatal Error Warning, muted profile:debug, copy to " + path, applied);
                    Assert.Equal(pc, applied.Contains("(Fatal Error Warning only; the game itself logs every level here)"));
                    Assert.Matches(@"(?m)^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z Warning \[Online\] \{[^}]*\}\.Run:\d+ \| a warning line$", text);
                    Assert.Matches(@"(?m)^\d{4}-\d{2}-\d{2}T[0-9:.]{12}Z Error \[Crm\] .*an error line\nSystem.InvalidOperationException: boom$", text);
                    Assert.Matches(@"(?m)^\d{4}-\d{2}-\d{2}T[0-9:.]{12}Z Fatal \[Engine\] .*a fatal line$", text);
                    Assert.Contains("an error of token mcvs…<redacted>", text);
                    Assert.DoesNotContain("e681f53f4c247197a50af715d8337ccd", text);
                    Assert.DoesNotContain("a muted profile line", text);
                    // PC: only Fatal and the configured levels; Android: everything the game logs (unchanged).
                    Assert.True(text.Contains("an info line") != pc, what);
                    Assert.True(text.Contains("a server line") != pc, what);
                    Assert.True(text.Contains("succeeded: mcvs…<redacted>") != pc, what);
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
            }
        }
    }
}
