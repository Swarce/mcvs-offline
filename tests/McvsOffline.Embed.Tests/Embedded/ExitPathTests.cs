using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Android;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// IAdminHost.Exit on the embedded host (design §8.8): the restart marker, the log flush, the path chosen by admin.restartVia,
    /// the browser closed first, the host fallback when the game's restart leaves the process alive; with a recording platform.
    /// </summary>
    [Collection("GameHooks")]
    public class ExitPathTests
    {
        [Fact]
        public void TheGamesRestartRunsAfterTheMarkerAndTheFlushAndFallsBackToTheHost()
        {
            using (var env = new ExitEnvironment("exit-game"))
            {
                env.Host.ExitFallbackMs = 400;
                Assert.True(env.Host.Exit("settings", true));
                // Right away: the browser check and the game's own restart (MainActivity.d.a(true)); the flush came first.
                Assert.Equal(new[] { "Flush:2000", "IsInGameBrowserOpen", "ExitViaGame:True" }, env.Calls.Snapshot().Take(3));
                Assert.DoesNotContain("ExitViaHost:True", env.Calls.Snapshot());
                string marker = File.ReadAllText(Path.Combine(env.LogsDir, AdminHost.RestartMarkerFile));
                Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z settings\n$", marker);
                Assert.True(env.Logs.Contains("admin: exit (settings, relaunch yes, via game)"));

                // The process "survives" the game's restart: the host's restart after ExitFallbackMs.
                Assert.True(SpinWait.SpinUntil(() => env.Calls.Contains("ExitViaHost:True"), 5000));
                Assert.Equal(new[] { "Flush:2000", "IsInGameBrowserOpen", "ExitViaGame:True", "Sleep:400", "Flush:2000", "ExitViaHost:True" },
                             env.Calls.Snapshot());
                Assert.True(env.Logs.Contains("still alive 400 ms after the game's restart"));

                // One exit per process.
                Assert.True(env.Host.Exit("user", true));
                Assert.Equal(6, env.Calls.Count);
            }
        }

        [Fact]
        public void ABrowserStillShownIsClosedBeforeTheGamesRestart()
        {
            using (var env = new ExitEnvironment("exit-browser"))
            {
                env.Host.ExitFallbackMs = 60000;
                env.Platform.BrowserOpen = true;
                Assert.True(env.Host.Exit("import", false));
                // MainActivity must be resumed for finish() to reach onPause → c(): close the browser, wait 500 ms.
                Assert.Equal(new[] { "Flush:2000", "IsInGameBrowserOpen", "QuitInGameBrowser", "Sleep:500", "ExitViaGame:False" },
                             env.Calls.Snapshot().Take(5));
            }
        }

        [Fact]
        public void WithoutTheGamesRestartTheHostsRestartRunsAtOnce()
        {
            using (var env = new ExitEnvironment("exit-nogame"))
            {
                env.Platform.ExitViaGameResult = false;   // MainActivity.d null, or the members are missing
                Assert.True(env.Host.Exit("user", true));
                Assert.Equal(new[] { "Flush:2000", "IsInGameBrowserOpen", "ExitViaGame:True", "ExitViaHost:True" }, env.Calls.Snapshot());
            }
            using (var env = new ExitEnvironment("exit-host"))
            {
                env.Server.Context.Settings.Set("admin.restartVia", "host");
                Assert.True(env.Host.Exit("user", true));
                Assert.Equal(new[] { "Flush:2000", "ExitViaHost:True" }, env.Calls.Snapshot());
            }
        }

        /// <summary>
        /// Review of WP5: the UI thread does not take the game's restart within the wait. It is withdrawn before the host's restart
        /// starts, so when the UI thread frees up only one restart has run (not two alarms, finish and finishAndRemoveTask).
        /// </summary>
        [Fact]
        public void AGameRestartTheUiThreadDidNotStartIsWithdrawnBeforeTheHostsRestart()
        {
            using (var env = new ExitEnvironment("exit-ui-busy"))
            {
                var ui = new UiQueue();   // a busy UI thread: nothing runs until Drain
                env.Platform.ExitViaGameHook = relaunch => PostGameRestart(ui, env.Calls, relaunch, 200);
                Assert.True(env.Host.Exit("settings", true));
                Assert.Equal(new[] { "Flush:2000", "IsInGameBrowserOpen", "ExitViaGame:True", "ExitViaHost:True" }, env.Calls.Snapshot());
                Assert.True(env.Logs.Contains("the game's restart is not available; using the host's restart"));
                ui.Drain();
                Assert.DoesNotContain("MainActivity.a:True", env.Calls.Snapshot());
            }
        }

        /// <summary>
        /// The UI thread started the game's restart but it had not returned within the wait: it is underway, so the host's restart
        /// does not start next to it; only the fallback (the process still alive ExitFallbackMs later) runs it.
        /// </summary>
        [Fact]
        public void AGameRestartAlreadyRunningIsNotDoubledByTheHostsRestart()
        {
            using (var env = new ExitEnvironment("exit-ui-slow"))
            using (var ui = new PumpedUiThread())
            using (var inside = new ManualResetEventSlim(false))
            using (var release = new ManualResetEventSlim(false))
            {
                env.Host.ExitFallbackMs = 400;
                env.Platform.ExitViaGameHook = relaunch =>
                {
                    var call = new UiCall(() =>
                    {
                        inside.Set();
                        release.Wait(10000);   // MainActivity.a still running on the UI thread
                        env.Calls.Add("MainActivity.a:" + relaunch);
                    }, null);
                    ui.Post(call.Run);
                    Assert.True(inside.Wait(5000));   // the UI thread has taken it
                    return UiCall.InEffect(call.Wait(100));
                };
                Assert.True(env.Host.Exit("settings", true));
                List<string> calls = env.Calls.Snapshot();
                Assert.Equal(new[] { "Flush:2000", "IsInGameBrowserOpen", "ExitViaGame:True" }, calls.Take(3));
                Assert.DoesNotContain("ExitViaHost:True", calls.Take(4));
                release.Set();
                Assert.True(SpinWait.SpinUntil(() => env.Calls.Contains("ExitViaHost:True"), 5000));
                calls = env.Calls.Snapshot();
                Assert.Contains("MainActivity.a:True", calls);
                Assert.True(calls.IndexOf("ExitViaHost:True") > calls.IndexOf("Sleep:400"));   // the fallback, not at once
                Assert.Equal(1, calls.Count(c => c == "ExitViaHost:True"));
            }
        }

        /// <summary>What AndroidExit.ViaGame does: MainActivity.d.a(relaunch) posted to the UI thread, waited for, withdrawn late.</summary>
        private static bool PostGameRestart(UiQueue ui, SynchronizedList calls, bool relaunch, int waitMs)
        {
            var call = new UiCall(() => calls.Add("MainActivity.a:" + relaunch), null);
            ui.Post(call.Run);
            return UiCall.InEffect(call.Wait(waitMs));
        }

        [Fact]
        public void RestartViaNoneAndNoPlatformDoNothing()
        {
            using (var env = new ExitEnvironment("exit-none"))
            {
                env.Server.Context.Settings.Set("admin.restartVia", "none");
                Assert.False(env.Host.Exit("settings", true));
                Assert.Empty(env.Calls.Snapshot());
                Assert.False(File.Exists(Path.Combine(env.LogsDir, AdminHost.RestartMarkerFile)));
            }
            using (var env = new ExitEnvironment("exit-noplatform", platform: false))
            {
                Assert.False(env.Host.Exit("settings", true));
                Assert.Empty(env.Calls.Snapshot());
                Assert.False(File.Exists(Path.Combine(env.LogsDir, AdminHost.RestartMarkerFile)));
            }
        }

        [Fact]
        public void AFailedExitRemovesTheMarkerAndCanBeRetried()
        {
            using (var env = new ExitEnvironment("exit-failed"))
            {
                env.Server.Context.Settings.Set("admin.restartVia", "host");
                env.Platform.ExitViaHostResult = false;
                Assert.False(env.Host.Exit("settings", true));
                Assert.False(File.Exists(Path.Combine(env.LogsDir, AdminHost.RestartMarkerFile)));
                env.Platform.ExitViaHostResult = true;
                Assert.True(env.Host.Exit("settings", true));   // ExitSequence released its lock: the player may try again
                Assert.Equal(2, env.Calls.Snapshot().Count(c => c == "ExitViaHost:True"));
            }
        }

        [Fact]
        public void TheNextStartReadsAndDeletesTheMarker()
        {
            string logs = TestEnvironment.NewTempDir("exit-marker");
            Assert.Null(AdminHost.TakeRestartMarker(logs));
            File.WriteAllText(Path.Combine(logs, AdminHost.RestartMarkerFile), "2026-10-26T10:15:00.123Z import\n");
            Assert.Equal("import", AdminHost.TakeRestartMarker(logs));
            Assert.False(File.Exists(Path.Combine(logs, AdminHost.RestartMarkerFile)));
            File.WriteAllText(Path.Combine(logs, AdminHost.RestartMarkerFile), "garbage");
            Assert.Equal("unknown", AdminHost.TakeRestartMarker(logs));
            File.WriteAllText(Path.Combine(logs, AdminHost.RestartMarkerFile), "2026-10-26T10:15:00.123Z ../../Evil Reason!\n");
            Assert.Equal("evilreason", AdminHost.TakeRestartMarker(logs));
            Assert.Equal("unknown", AdminHost.SafeReason(null));
        }

        [Fact]
        public void TheEmbeddedHostLogsARequestedRestartAtTheNextBoot()
        {
            string root = TestEnvironment.NewTempDir("exit-boot");
            string logs = Path.Combine(root, "McvsOffline", "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, AdminHost.RestartMarkerFile), "2026-10-26T10:15:00.123Z settings\n");
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                   new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.True(host.FlushLogs(10000));
                Assert.Contains("previous exit: restart requested from the settings page (settings)", File.ReadAllText(host.Paths.BootstrapLog));
                Assert.False(File.Exists(Path.Combine(logs, AdminHost.RestartMarkerFile)));
                Assert.Contains(host.Boot.Milestones(), m => m.Contains("previous exit: restart requested from the settings page (settings)"));
            }
            finally
            {
                host.Server?.Dispose();
                AdminHooks.Host = null;
            }
        }

        /// <summary>An AdminHost over a recording platform; the flush and the waits are recorded into the same list.</summary>
        private sealed class ExitEnvironment : IDisposable
        {
            public ExitEnvironment(string name, bool platform = true)
            {
                Logs = new MemoryLogSink();
                Server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir(name), Logs);
                LogsDir = Path.Combine(Server.Context.DataDir, "logs");
                Platform = new RecordingAdminPlatform();
                Calls = Platform.Calls;
                Host = new AdminHost(Server.Context, platform ? Platform : null, new GameReflection(), null)
                {
                    LogsDir = LogsDir,
                    FlushLogs = ms =>
                    {
                        Calls.Add("Flush:" + ms);
                        return true;
                    },
                    Sleep = ms =>
                    {
                        Calls.Add("Sleep:" + ms);
                        Thread.Sleep(Math.Min(ms, 1000));
                    },
                };
            }

            public MemoryLogSink Logs { get; }
            public McvsServer Server { get; }
            public string LogsDir { get; }
            public RecordingAdminPlatform Platform { get; }
            public SynchronizedList Calls { get; }
            public AdminHost Host { get; }

            public void Dispose()
            {
                Server.Dispose();
            }
        }
    }
}
