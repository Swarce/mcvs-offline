using System;
using System.Collections.Generic;
using McvsOffline.Embedded.Windows;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>The relaunch helper's exact command line (docs/design/pc-port.md §6.6, C18).</summary>
    public class RelaunchHelperTests
    {
        [Fact]
        public void TheHelperWaitsForThePidThenStartsTheGameTheSteamWay()
        {
            Assert.Equal("-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Wait-Process -Id 4242 -Timeout 60 -ErrorAction SilentlyContinue; " +
                         "Start-Sleep -Seconds 2; Start-Process 'steam://rungameid/723780'\"",
                         WindowsExit.HelperArguments(4242));
        }

        [Fact]
        public void TheHelperStartsOncePerProcessAsHiddenPowerShell()
        {
            var started = new List<string>();
            var exit = new WindowsExit { ProcessId = () => 17, StartProcess = (program, args) => started.Add(program + " " + args) };
            exit.StartHelper();
            exit.StartHelper();
            Assert.Equal(new[] { "powershell.exe " + WindowsExit.HelperArguments(17) }, started);
        }
    }

    /// <summary>
    /// WindowsExit's two paths with injected actions (docs/design/pc-port.md §6.6): the game's quit is posted to the game thread
    /// after the helper; the host's path runs helper, flush, kill in that order.
    /// </summary>
    public class WindowsExitTests
    {
        private sealed class Env
        {
            public readonly List<string> Calls = new List<string>();
            public readonly List<Action> Posted = new List<Action>();
            public bool PostWorks = true;
            public readonly WindowsExit Exit;

            public Env()
            {
                Exit = new WindowsExit
                {
                    ProcessId = () => 99,
                    StartProcess = (program, args) => Calls.Add("helper"),
                    Kill = () => Calls.Add("kill"),
                    ExitApp = () => Calls.Add("ExitAppAndroid"),
                    FlushLogsNow = ms => Calls.Add("flush:" + ms),
                    Post = action =>
                    {
                        Calls.Add("post");
                        if (!PostWorks) return false;
                        Posted.Add(action);
                        return true;
                    },
                    Warn = (text, ex) => Calls.Add("warn:" + text),
                };
            }
        }

        [Fact]
        public void TheGamesQuitIsPostedAfterTheHelper()
        {
            var env = new Env();
            Assert.True(env.Exit.ViaGame(true));
            Assert.Equal(new[] { "helper", "post" }, env.Calls);
            env.Posted[0]();   // the game thread runs it
            Assert.Equal(new[] { "helper", "post", "ExitAppAndroid" }, env.Calls);
        }

        [Fact]
        public void WithoutRelaunchNoHelper()
        {
            var env = new Env();
            Assert.True(env.Exit.ViaGame(false));
            Assert.Equal(new[] { "post" }, env.Calls);
            Assert.True(env.Exit.ViaHost(false));
            Assert.Equal(new[] { "post", "flush:2000", "kill" }, env.Calls);
        }

        [Fact]
        public void TheHostsPathIsHelperFlushKillAndTheHelperStartsOnce()
        {
            var env = new Env();
            Assert.True(env.Exit.ViaHost(true));
            Assert.Equal(new[] { "helper", "flush:2000", "kill" }, env.Calls);

            var both = new Env();
            both.Exit.ViaGame(true);      // AdminHost's 3 s fallback then takes the host's path
            both.Exit.ViaHost(true);
            Assert.Equal(new[] { "helper", "post", "flush:2000", "kill" }, both.Calls);
        }

        [Fact]
        public void NoGameThreadMeansTheGamesQuitIsNotAvailable()
        {
            var env = new Env { PostWorks = false };
            Assert.False(env.Exit.ViaGame(false));
            Assert.Contains(env.Calls, c => c.StartsWith("warn:admin: the game thread is not known", StringComparison.Ordinal));
        }

        [Fact]
        public void AHelperThatDoesNotStartIsAWarningNotAFailure()
        {
            var env = new Env();
            env.Exit.StartProcess = (program, args) => { throw new System.ComponentModel.Win32Exception(2); };
            Assert.True(env.Exit.ViaHost(true));
            Assert.Contains(env.Calls, c => c.StartsWith("warn:admin: the relaunch helper did not start", StringComparison.Ordinal));
            Assert.Equal("kill", env.Calls[env.Calls.Count - 1]);
        }

        [Fact]
        public void TheRealExitAppAndroidIsMissingOffTheGame()
        {
            // Off the game, MenuManager does not resolve: the posted call throws (GameReflection.Post logs it), nothing else.
            var exit = new WindowsExit();
            Assert.Throws<MissingMethodException>(() => exit.ExitApp());
        }
    }
}
