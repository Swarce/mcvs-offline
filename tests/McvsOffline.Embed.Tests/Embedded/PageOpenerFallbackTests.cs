using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>An admin platform that opens the page itself (Windows' IPagePlatform), recording every open.</summary>
    internal sealed class PagePlatform : IAdminPlatform, IPagePlatform
    {
        public readonly List<string> Opens = new List<string>();
        public bool OverlayWorks = true;
        public bool SystemWorks = true;
        public int JniCalls;

        public string[] OpenModes { get { return new[] { "overlay", "system" }; } }

        public string Open(string url, string mode)
        {
            Opens.Add(mode);
            if (mode == "overlay") return OverlayWorks ? "overlay" : null;
            if (mode == "system") return SystemWorks ? "system" : null;
            if (OverlayWorks) return "overlay";
            return SystemWorks ? "system" : null;
        }

        public int SdkInt { get { return 0; } }
        public bool OpenInGameBrowserJni(string url) { JniCalls++; return false; }
        public bool OpenInGameBrowserWithPostData(string url, string postData) { JniCalls++; return false; }
        public bool IsInGameBrowserOpen() { return false; }
        public bool QuitInGameBrowser() { return false; }
        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text) { return AdminShareResult.None("x"); }
        public AdminShareResult ShareText(string subject, string text) { return AdminShareResult.None("x"); }
        public bool CopyToClipboard(string label, string text) { return false; }
        public void ShowToast(string text) { }
        public bool ExitViaGame(bool relaunch) { return false; }
        public bool ExitViaHost(bool relaunch) { return false; }
        public string PublicDownloadsDir() { return null; }
        public long FreeBytes(string path) { return -1; }
        public List<string> MissingJniMembers() { return new List<string>(); }
        public AdminPlatformCapabilities Capabilities() { return new AdminPlatformCapabilities { OpenPage = true, OpenInBrowser = true }; }
    }

    /// <summary>
    /// PageOpener with a platform that opens the page itself (docs/design/pc-port.md §6.5, §12.2 WindowsPageOpenerTests): auto,
    /// overlay, system and game by admin.openVia; jni/post are auto with one WARN; the lease and the log line.
    /// </summary>
    public class WindowsPageOpenerTests
    {
        internal sealed class Env : IDisposable
        {
            public readonly MemoryLogSink Logs = new MemoryLogSink(LogLevel.Debug, 500);
            public readonly McvsServer Server;
            public readonly PagePlatform Platform = new PagePlatform();
            public readonly GameThreadWatchdog Watchdog = new GameThreadWatchdog(null, null, null);
            public readonly AdminHost Host;
            public int Now = 1000;

            public Env()
            {
                Server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("pageopener"), Logs);
                Host = new AdminHost(Server.Context, Platform, new GameReflection(), null) { PlatformName = "windows", Watchdog = () => Watchdog };
                Host.Opener.Clock = () => Now;
            }

            public bool Open(string trigger = "account", object world = null)
            {
                return Host.Opener.OpenWithWorld(world, "http://127.0.0.1:51780/_mcvs/ui/#t=0123456789abcdef", trigger);
            }

            public string LastOpenLine()
            {
                return Logs.Snapshot().Select(e => e.Message).LastOrDefault(m => m.StartsWith("admin: page opened", StringComparison.Ordinal));
            }

            public int Warnings(string text)
            {
                return Logs.Snapshot().Count(e => e.Level == LogLevel.Warn && e.Message.Contains(text));
            }

            public void Dispose()
            {
                Server.Dispose();
            }
        }

        [Fact]
        public void AutoGoesToThePlatformAndTheLineNamesTheRoute()
        {
            using (var env = new Env())
            {
                Assert.True(env.Open());
                Assert.Equal(new[] { "auto" }, env.Platform.Opens);
                Assert.Equal("admin: page opened (trigger account, via overlay)", env.LastOpenLine());
                string reason;
                Assert.True(env.Watchdog.IsPaused(out reason));   // the lease is taken first
                env.Platform.OverlayWorks = false;
                Assert.True(env.Open("news"));
                Assert.Equal("admin: page opened (trigger news, via system)", env.LastOpenLine());
                Assert.Equal(0, env.Platform.JniCalls);
                Assert.DoesNotContain(env.Logs.Snapshot(), e => e.Message.Contains("0123456789abcdef"));
            }
        }

        [Fact]
        public void OverlayAndSystemArePassedThrough()
        {
            using (var env = new Env())
            {
                env.Server.Context.Settings.Set("admin.openVia", "overlay");
                Assert.True(env.Open());
                env.Server.Context.Settings.Set("admin.openVia", "System");
                Assert.True(env.Open());
                Assert.Equal(new[] { "overlay", "system" }, env.Platform.Opens);
                Assert.Equal("admin: page opened (trigger account, via system)", env.LastOpenLine());
            }
        }

        [Fact]
        public void AFailedOpenReleasesTheLease()
        {
            using (var env = new Env())
            {
                env.Platform.OverlayWorks = false;
                env.Platform.SystemWorks = false;
                Assert.False(env.Open());
                string reason;
                Assert.False(env.Watchdog.IsPaused(out reason));
                Assert.Equal(1, env.Warnings("admin: the page could not be opened (trigger account, openVia auto)"));
            }
        }

        [Fact]
        public void GameUsesTheGamesOwnBrowser()
        {
            using (var env = new Env())
            {
                env.Server.Context.Settings.Set("admin.openVia", "game");
                var world = new FakeWorld();
                world.Browser = new FakeBrowser { World = world, IsInGameBrowserInitialized = true };
                Assert.True(env.Open("account", world));
                Assert.Empty(env.Platform.Opens);
                Assert.Single(world.Browser.Opened);
                Assert.Equal("admin: page opened (trigger account, via game)", env.LastOpenLine());
                Assert.False(env.Open("account", null));   // no World: game cannot open
            }
        }

        [Fact]
        public void JniAndPostAreAutoWithOneWarningOnPc()
        {
            using (var env = new Env())
            {
                env.Server.Context.Settings.Set("admin.openVia", "jni");
                Assert.Equal("auto", env.Host.OpenVia);
                Assert.True(env.Open());
                Assert.True(env.Open());
                Assert.Equal(new[] { "auto", "auto" }, env.Platform.Opens);
                Assert.Equal(1, env.Warnings("admin: settings admin.openVia 'jni' is not one of auto, game, overlay, system on this platform; using auto"));
                env.Server.Context.Settings.Set("admin.openVia", "post");
                Assert.Equal("auto", env.Host.OpenVia);
                Assert.Equal(1, env.Warnings("'post'"));
                Assert.Equal(0, env.Platform.JniCalls);
            }
        }

        [Fact]
        public void OverlayAndSystemAreAutoWithOneWarningOnAndroid()
        {
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("pageopener-android"), new MemoryLogSink()))
            {
                var logs = new MemoryLogSink(LogLevel.Debug, 100);
                var logger = new Logger();
                logger.AddSink(logs);
                var platform = new RecordingAdminPlatform();
                var host = new AdminHost(server.Context, platform, new GameReflection(), logger.For("admin"));
                server.Context.Settings.Set("admin.openVia", "overlay");
                Assert.Equal("auto", host.OpenVia);
                Assert.Equal("auto", host.OpenVia);
                Assert.Single(logs.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.Contains("'overlay' is not one of auto, game, jni, post"));
                server.Context.Settings.Set("admin.openVia", "post");
                Assert.Equal("post", host.OpenVia);
                Assert.True(host.Opener.OpenWithWorld(null, "http://127.0.0.1:51780/_mcvs/ui/#t=abc", "account"));
                Assert.Contains("OpenInGameBrowserWithPostData", platform.Calls);   // Android's switch is unchanged
            }
        }
    }

    /// <summary>
    /// The no-page fallback (docs/design/pc-port.md §6.5) with an injected clock: an auto open after an overlay open that got no
    /// PageAlive within 20 s takes the system browser and says so; a PageAlive in time keeps auto; the trigger browser always
    /// takes the system browser; Android's opener is not affected.
    /// </summary>
    public class PageOpenerFallbackTests
    {
        [Fact]
        public void NoPageAliveWithin20sAfterAnOverlayOpenSendsTheNextAutoOpenToTheSystemBrowser()
        {
            using (var env = new WindowsPageOpenerTests.Env())
            {
                Assert.True(env.Open());                      // overlay
                env.Now += PageOpener.NoPageMs - 1;
                Assert.True(env.Open());                      // not 20 s yet: still auto
                Assert.Equal(new[] { "auto", "auto" }, env.Platform.Opens);
                env.Now += PageOpener.NoPageMs;
                Assert.True(env.Open("resume"));
                Assert.Equal("system", env.Platform.Opens[2]);
                Assert.Equal("admin: page opened (trigger resume, via system, overlay showed no page last time)", env.LastOpenLine());
                Assert.True(env.Open());                      // the last open went to the system browser: auto again
                Assert.Equal("auto", env.Platform.Opens[3]);
            }
        }

        [Fact]
        public void APageAliveInTimeKeepsAuto()
        {
            using (var env = new WindowsPageOpenerTests.Env())
            {
                Assert.True(env.Open());
                env.Now += 3000;
                env.Host.PageAlive();                          // the page loaded with its session
                env.Now += 60000;
                Assert.True(env.Open());
                Assert.Equal(new[] { "auto", "auto" }, env.Platform.Opens);
                Assert.Equal("admin: page opened (trigger account, via overlay)", env.LastOpenLine());
            }
        }

        [Fact]
        public void TheBrowserTriggerAlwaysTakesTheSystemBrowser()
        {
            using (var env = new WindowsPageOpenerTests.Env())
            {
                Assert.True(env.Open("browser"));
                env.Server.Context.Settings.Set("admin.openVia", "overlay");
                Assert.True(env.Open("browser"));
                env.Server.Context.Settings.Set("admin.openVia", "game");
                Assert.True(env.Open("browser"));
                Assert.Equal(new[] { "system", "system", "system" }, env.Platform.Opens);
                Assert.Equal("admin: page opened (trigger browser, via system)", env.LastOpenLine());
            }
        }

        [Fact]
        public void AnOverlayOnlySettingIsNotOverridden()
        {
            using (var env = new WindowsPageOpenerTests.Env())
            {
                env.Server.Context.Settings.Set("admin.openVia", "overlay");
                Assert.True(env.Open());
                env.Now += 3 * PageOpener.NoPageMs;
                Assert.True(env.Open());
                Assert.Equal(new[] { "overlay", "overlay" }, env.Platform.Opens);   // the player asked for the overlay only
            }
        }

        [Fact]
        public void AndroidIsNotAffected()
        {
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("pageopener-fallback-android")))
            {
                var platform = new RecordingAdminPlatform();
                var host = new AdminHost(server.Context, platform, new GameReflection(), null);
                int now = 0;
                host.Opener.Clock = () => now;
                Assert.True(host.Opener.OpenWithWorld(null, "http://127.0.0.1:51780/_mcvs/ui/#t=abc", "account"));
                now += 10 * PageOpener.NoPageMs;
                Assert.True(host.Opener.OpenWithWorld(null, "http://127.0.0.1:51780/_mcvs/ui/#t=def", "account"));
                Assert.Equal(new[] { "http://127.0.0.1:51780/_mcvs/ui/#t=abc", "http://127.0.0.1:51780/_mcvs/ui/#t=def" }, platform.Opened);
            }
        }
    }
}
