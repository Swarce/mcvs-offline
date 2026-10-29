using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Admin;
using McvsOffline.Embedded.Android;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>The embedded host's IAdminHost (design §8.2): capabilities, the watchdog lease, problems, game-thread work, wiring.</summary>
    [Collection("GameHooks")]
    public class AdminHostTests
    {
        [Fact]
        public void WithoutAnAndroidPlatformEveryCapabilityIsOffAndSharesAnswerNone()
        {
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("adminhost-none")))
            {
                var host = new AdminHost(server.Context, null, new GameReflection(), null) { GameRoot = "/x" };
                AdminHostInfo info = host.Describe();
                Assert.Equal("directory", info.Platform);
                Assert.Equal(0, info.SdkInt);
                Assert.Equal("none", info.RestartVia);
                Assert.Equal("unknown", info.BrowserStatus);
                Assert.Equal(-1, info.FreeBytes);
                Assert.Empty(info.MissingJni);
                Assert.False(info.CanOpenPage || info.CanShareFile || info.CanSaveToDownloads || info.CanShareText || info.CanCopy ||
                             info.CanToast || info.CanSave || info.CanExit);
                Assert.False(info.RelaunchUnconfirmed);
                Assert.Null(info.Notifications);
                Assert.StartsWith("graphics: graphics.profile", info.Graphics);
                Assert.StartsWith("hd textures:", info.HdTextures);

                string file = Path.Combine(TestEnvironment.NewTempDir("adminhost-file"), "mcvs-logs-x.zip");
                File.WriteAllText(file, "zip");
                AdminShareResult shared = host.ShareFile(file, "application/zip", "mcvs-logs-x.zip", "MCVS Offline logs", null);
                Assert.False(shared.Ok);
                Assert.Equal("none", shared.Method);
                Assert.Equal("none", host.ShareText("s", "t").Method);
                Assert.False(host.CopyToClipboard("l", "t"));
                Assert.False(host.CloseBrowser());
                Assert.False(host.WaitForGameLoop(10));
                Assert.False(host.RequestGameSave());
                Assert.False(host.Exit("user", true));
                host.ShowToast("nothing happens");
                host.PageAlive();
                Assert.Empty(host.RecentProblems(20));
            }
        }

        [Fact]
        public void CapabilitiesFollowTheJniTableAndTheAndroidVersion()
        {
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("adminhost-caps")))
            {
                var platform = new RecordingAdminPlatform { Sdk = 34, Free = 123456789 };
                var host = new AdminHost(server.Context, platform, new GameReflection(), null) { GameRoot = "/storage/emulated/0/x" };
                AdminHostInfo info = host.Describe();
                Assert.Equal("android", info.Platform);
                Assert.Equal(34, info.SdkInt);
                Assert.Equal("game", info.RestartVia);
                Assert.Equal(123456789, info.FreeBytes);
                Assert.Equal("/storage/emulated/0/Download/MCVS-Offline", info.PublicLogsDir);
                Assert.True(info.CanOpenPage && info.CanShareFile && info.CanSaveToDownloads && info.CanShareText && info.CanCopy && info.CanToast && info.CanExit);
                Assert.False(info.CanSave);   // no game: GameReflection unbound
                Assert.True(info.RelaunchUnconfirmed);   // Android 13+: the restart alarm may bring nothing back (target-sdk.md §4.6, not observed yet)
                host.NotificationStatus = () => "allowed";
                Assert.Equal("allowed", host.Describe().Notifications);
                host.NotificationStatus = () => throw new InvalidOperationException("jni");
                Assert.Null(host.Describe().Notifications);
                host.NotificationStatus = null;

                platform.Missing.Add(AndroidJni.ClipboardSetPrimary.Key);
                info = host.Describe();
                Assert.False(info.CanCopy);
                Assert.True(info.CanShareFile);
                Assert.Equal(new[] { "android/content/ClipboardManager.setPrimaryClip(Landroid/content/ClipData;)V" }, info.MissingJni);

                platform.Missing.Clear();
                platform.Missing.Add(AndroidJni.DownloadsUri.Key);
                info = host.Describe();
                Assert.False(info.CanShareFile);
                Assert.False(info.CanSaveToDownloads);

                platform.Missing.Clear();
                platform.Sdk = 28;   // Android 9: file:// shares, no Downloads
                info = host.Describe();
                Assert.False(info.RelaunchUnconfirmed);
                Assert.True(info.CanShareFile);
                Assert.False(info.CanSaveToDownloads);
                platform.Missing.Add(AndroidJni.StrictSetVmPolicy.Key);
                Assert.False(host.Describe().CanShareFile);
                platform.Sdk = 23;   // no StrictMode relaxation needed before Android 7
                Assert.True(host.Describe().CanShareFile);

                platform.Missing.Clear();
                platform.Sdk = 34;
                platform.Missing.Add(AndroidJni.MainActivityRestart.Key);
                Assert.True(host.Describe().CanExit);    // the host's restart remains
                platform.Missing.Add(AndroidJni.KillProcess.Key);
                Assert.False(host.Describe().CanExit);
                platform.Missing.Clear();
                server.Context.Settings.Set("admin.restartVia", "none");
                Assert.Equal("none", host.Describe().RestartVia);
                Assert.False(host.Describe().CanExit);
                server.Context.Settings.Set("admin.restartVia", "nonsense");
                Assert.Equal("game", host.Describe().RestartVia);
            }
        }

        [Fact]
        public void PageAliveSharesAndCloseDriveTheWatchdogLease()
        {
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("adminhost-lease")))
            {
                var watchdog = new GameThreadWatchdog(null, null, null);   // not started: only its lease is used
                var platform = new RecordingAdminPlatform { Sdk = 34, BrowserOpen = true };
                var host = new AdminHost(server.Context, platform, new GameReflection(), null) { Watchdog = () => watchdog };
                string reason;
                Assert.False(watchdog.IsPaused(out reason));

                host.PageAlive();
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.PageLeaseReason, reason);
                Assert.Contains("paused: MCVS Offline page open", watchdog.Describe());

                string file = Path.Combine(TestEnvironment.NewTempDir("adminhost-share"), "mcvs-save-a.txt");
                File.WriteAllText(file, "MCVS-SAVE");
                AdminShareResult shared = host.ShareFile(file, "text/plain", "mcvs-save-a.txt", "MCVS Offline save", "text");
                Assert.True(shared.Ok);
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.ShareLeaseReason, reason);   // the share target may keep the game in the background for minutes
                Assert.Contains("ShareFile:text/plain:mcvs-save-a.txt:MCVS Offline save", platform.Calls);

                host.PageAlive();   // the player came back: 45 s again
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.PageLeaseReason, reason);

                platform.ShareResult = new AdminShareResult { Ok = true, Method = "downloads", PublicPath = "Download/MCVS-Offline/mcvs-save-a.txt" };
                host.ShareFile(file, "text/plain", "mcvs-save-a.txt", "MCVS Offline save", null);   // "Save to Downloads": no chooser
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.PageLeaseReason, reason);

                Assert.True(host.CloseBrowser());
                Assert.False(watchdog.IsPaused(out reason));
                Assert.Contains("QuitInGameBrowser", platform.Calls);

                // Unsafe names and missing files never reach the platform.
                platform.Calls.Clear();
                Assert.Equal("none", host.ShareFile(file, "text/plain", "../evil.txt", "s", null).Method);
                Assert.Equal("none", host.ShareFile(file + ".missing", "text/plain", "a.txt", "s", null).Method);
                Assert.Empty(platform.Calls);
                Assert.False(host.CopyToClipboard("l", new string('x', AdminHost.MaxClipboardChars + 1)));
                Assert.True(host.CopyToClipboard("l", new string('x', AdminHost.MaxClipboardChars)));
            }
        }

        /// <summary>
        /// Review of WP5: a text share the UI thread started but had not finished within the wait may still open its sheet, so the
        /// share lease stays; one that never runs (withdrawn) or failed goes back to the page's lease.
        /// </summary>
        [Fact]
        public void ATextShareStillOpeningKeepsTheShareLease()
        {
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("adminhost-sharetext")))
            {
                var watchdog = new GameThreadWatchdog(null, null, null);
                var platform = new RecordingAdminPlatform { Sdk = 34 };
                var host = new AdminHost(server.Context, platform, new GameReflection(), null) { Watchdog = () => watchdog };
                string reason;

                platform.TextResult = AndroidShare.TextShareResult(UiCall.Pending);
                AdminShareResult result = host.ShareText("MCVS Offline report", "text");
                Assert.False(result.Ok);
                Assert.Equal("text", result.Method);
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.ShareLeaseReason, reason);

                platform.TextResult = AndroidShare.TextShareResult(UiCall.Withdrawn);
                Assert.Equal("none", host.ShareText("MCVS Offline report", "text").Method);
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.PageLeaseReason, reason);

                platform.TextResult = AndroidShare.TextShareResult(UiCall.Done);
                Assert.True(host.ShareText("MCVS Offline report", "text").Ok);
                Assert.True(watchdog.IsPaused(out reason));
                Assert.Equal(AdminHost.ShareLeaseReason, reason);
            }
        }

        /// <summary>
        /// Review of WP5: the problem sink (WARN and up) is the page's "recent problems"; the watchdog's pause and resume around every
        /// page open, close and share, and its tid line, are INFO, while stall snapshots stay WARN.
        /// </summary>
        [Fact]
        public void TheWatchdogsPauseAndResumeAreNotProblems()
        {
            string root = TestEnvironment.NewTempDir("adminhost-watchdog-lines");
            AdminHooks.Host = null;
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions
            {
                Port = TestEnvironment.FreePort(),
                Watchdog = true,
                StatusIntervalMs = 0,
            });
            try
            {
                Assert.True(host.WaitForServer(30000));
                GameThreadWatchdog watchdog = host.Watchdog;
                Assert.NotNull(watchdog);
                Assert.NotNull(watchdog.Notice);
                watchdog.Notice("[watchdog] game thread is tid 4242 'UnityMain'");
                watchdog.Notice("[watchdog] paused: " + AdminHost.PageLeaseReason);
                watchdog.Notice("[watchdog] resumed");
                List<string> problems = host.AdminHost.RecentProblems(100);
                Assert.DoesNotContain(problems, p => p.Contains("paused: ") || p.Contains("resumed") || p.Contains("tid 4242"));
                Assert.True(host.FlushLogs(10000));
                string log = File.ReadAllText(host.Paths.BootstrapLog);
                Assert.Contains("INFO  [watchdog] paused: MCVS Offline page open", log);
                Assert.Contains("INFO  [watchdog] resumed", log);
                Assert.Contains("INFO  [watchdog] game thread is tid 4242 'UnityMain'", log);
                Assert.Equal("x", EmbeddedHost.WithoutTag("[watchdog] x", "[watchdog] "));
                Assert.Equal("[boot] x", EmbeddedHost.WithoutTag("[boot] x", "[watchdog] "));
            }
            finally
            {
                host.Server?.Dispose();
                AdminHooks.Host = null;
            }
        }

        [Fact]
        public void WaitForGameLoopAndRequestGameSaveRunOnTheGameThread()
        {
            var log = new MemoryLogSink();
            using (McvsServer server = AdminFixtures.CreateServer(TestEnvironment.NewTempDir("adminhost-save"), log))
            {
                FakeGame game = FakeGame.Install();
                try
                {
                    var host = new AdminHost(server.Context, new RecordingAdminPlatform(), game.Reflection, null);
                    Assert.True(game.Reflection.CanSave);
                    Assert.False(host.WaitForGameLoop(100));   // nobody drains the queue: no frame
                    Assert.True(game.App.Context.Pending > 0);
                    game.App.Context.Drain();

                    using (var pump = game.Pump())
                    {
                        Assert.True(host.WaitForGameLoop(5000));
                    }

                    var other = new FakeProfile { FedId = new FakeCredential("fed_id:other") };
                    var mine = new FakeProfile { FedId = new FakeCredential("fed_id:me") };
                    game.World.Components.Add(other);
                    game.World.Components.Add(mine);
                    Assert.True(host.RequestGameSave());
                    Assert.Equal(0, mine.Saves);   // posted, not run
                    game.App.Context.Drain();
                    Assert.Equal(1, mine.Saves);
                    Assert.Equal(0, other.Saves);
                    Assert.Equal(typeof(FakeProfile), game.World.LastEnumerated);
                    Assert.True(log.Contains("admin: asked the game to save the profile (ForceSaveProfile)"));

                    // A faulted save is logged, never thrown into the game thread.
                    mine.Fault = new InvalidOperationException("probe: the TS is gone");
                    Assert.True(host.RequestGameSave());
                    game.App.Context.Drain();
                    Assert.True(SpinWait.SpinUntil(() => log.Contains("the game's ForceSaveProfile failed"), 5000));

                    // Without our own profile: the first one.
                    game.World.Components.Remove(mine);
                    Assert.True(host.RequestGameSave());
                    game.App.Context.Drain();
                    Assert.Equal(1, other.Saves);

                    // A server World is never asked.
                    game.World.IsServer = true;
                    Assert.True(host.RequestGameSave());
                    game.App.Context.Drain();
                    Assert.Equal(1, other.Saves);
                    Assert.True(log.Contains("admin: no client World to save from"));
                }
                finally
                {
                    FakeGame.Uninstall();
                }
            }
        }

        [Fact]
        public void TheMediaStoreLedgerKeepsTheNewestFivePerKind()
        {
            string data = TestEnvironment.NewTempDir("ledger");
            var ledger = new MediaStoreLedger(data);
            var deleted = new List<string>();
            var gone = new HashSet<string>();
            var failing = new HashSet<string>();
            Func<string, int> delete = uri =>
            {
                if (failing.Contains(uri)) return -1;
                deleted.Add(uri);
                return gone.Contains(uri) ? 0 : 1;
            };
            DateTime t = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
            for (int i = 1; i <= 5; i++) Assert.Empty(ledger.Record("content://media/external/downloads/" + i, "mcvs-logs-" + i + ".zip", t.AddMinutes(i), delete));
            for (int i = 1; i <= 3; i++) ledger.Record("content://media/external/downloads/s" + i, "mcvs-save-p-" + i + ".txt", t.AddMinutes(10 + i), delete);
            Assert.Empty(deleted);

            // A sixth zip: the oldest zip goes; the saves are another kind.
            Assert.Equal(new[] { "content://media/external/downloads/1" }, ledger.Record("content://media/external/downloads/6", "mcvs-logs-6.zip", t.AddMinutes(20), delete));
            Assert.Equal(new[] { "content://media/external/downloads/1" }, deleted);

            // A row the player already deleted (delete finds nothing) is dropped as well; a failing delete stays for next time.
            gone.Add("content://media/external/downloads/2");
            failing.Add("content://media/external/downloads/3");
            ledger.Record("content://media/external/downloads/7", "mcvs-logs-7.zip", t.AddMinutes(21), delete);
            ledger.Record("content://media/external/downloads/8", "mcvs-logs-8.zip", t.AddMinutes(22), delete);
            List<string> logs = ledger.Rows().Where(r => r.Kind == "logs").Select(r => r.Uri).ToList();
            Assert.Equal(new[] { "3", "4", "5", "6", "7", "8" }.Select(n => "content://media/external/downloads/" + n), logs);
            Assert.Equal(3, ledger.Rows().Count(r => r.Kind == "save"));
            Assert.Equal("mcvs-logs-8.zip", ledger.Rows().Last().Name);
            Assert.Equal("2026-10-26T10:22:00.000Z", ledger.Rows().Last().Time);
            Assert.True(File.Exists(Path.Combine(data, "admin", "mediastore.json")));

            failing.Clear();
            ledger.Record("content://media/external/downloads/9", "mcvs-logs-9.zip", t.AddMinutes(23), delete);
            Assert.Equal(new[] { "5", "6", "7", "8", "9" }.Select(n => "content://media/external/downloads/" + n),
                         ledger.Rows().Where(r => r.Kind == "logs").Select(r => r.Uri));

            Assert.Equal("logs", MediaStoreLedger.KindOf("mcvs-logs-20261026-101500.zip"));
            Assert.Equal("save", MediaStoreLedger.KindOf("mcvs-save-Player-20261026-1015.txt"));
            Assert.Equal("other", MediaStoreLedger.KindOf("x.txt"));

            // A damaged ledger starts over instead of failing the share.
            File.WriteAllText(Path.Combine(data, "admin", "mediastore.json"), "{ broken");
            Assert.Empty(ledger.Record("content://media/external/downloads/10", "mcvs-logs-10.zip", t, delete));
            Assert.Single(ledger.Rows());
        }

        [Fact]
        public void TheEmbeddedHostRegistersItsAdminHostAndRedactsItsLogger()
        {
            string root = TestEnvironment.NewTempDir("adminhost-wiring");
            int port = TestEnvironment.FreePort();
            string ticket = "0123456789abcdef0123456789abcdef";
            AdminHooks.Host = null;
            EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false), new EmbeddedHostOptions
            {
                Port = port,
                Watchdog = false,
                StatusIntervalMs = 0,
                RegisterServices = ServiceRegistry.RegisterAll,   // every module, the admin modules included
            });
            try
            {
                Assert.True(host.WaitForServer(30000));
                Assert.NotNull(host.Server);
                Assert.Same(host.AdminHost, host.Server.Context.Features.Get<IAdminHost>());
                Assert.Same(host.AdminHost, AdminHooks.Host);   // the hooks work once the port is open
                Assert.Equal((Func<string, string>)McvsOffline.Server.Core.Http.LogRedaction.Redact, host.Logger.Redactor);

                host.Log.Warn("probe: the browser opened http://127.0.0.1:" + port + "/_mcvs/ui/#t=" + ticket);
                host.Log.Info("probe: an info line is not a problem");
                List<string> problems = host.AdminHost.RecentProblems(20);
                Assert.Contains(problems, p => p.Contains("probe: the browser opened") && p.Contains("#t=<redacted>"));
                Assert.DoesNotContain(problems, p => p.Contains(ticket) || p.Contains("an info line"));
                Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z WARN  \[embedded\] probe: the browser", problems.First(p => p.Contains("probe: the browser")));

                AdminHostInfo info = host.Server.Context.Features.Get<IAdminHost>().Describe();
                Assert.Equal("directory", info.Platform);
                Assert.Equal(host.DeviceId, info.DeviceId);
                Assert.Equal(host.Paths.LogsDir, info.LogsDir);
                Assert.Equal(host.Paths.GameRoot, info.GameRoot);
                Assert.True(host.FlushLogs(10000));
                Assert.DoesNotContain(ticket, File.ReadAllText(host.Paths.BootstrapLog));
            }
            finally
            {
                host.Server?.Dispose();
                AdminHooks.Host = null;
            }
        }
    }

    // ---------------------------------------------------------------------- fixtures shared by the WP5 tests

    internal static class AdminFixtures
    {
        public const int Port = 51780;

        /// <summary>The composed server (not listening) on port 51780; <paramref name="admin"/> false leaves the admin modules out.</summary>
        public static McvsServer CreateServer(string dataDir, MemoryLogSink logs = null, bool admin = true)
        {
            var options = new ServerOptions
            {
                DataDir = dataDir,
                Port = Port,
                ConsoleLog = false,
                FileLog = false,
                RegisterServices = (r, c) => ServiceRegistry.RegisterAll(r, c, module => admin || !module.StartsWith("admin", StringComparison.Ordinal)),
            };
            if (logs != null) options.ExtraSinks.Add(logs);
            return McvsServer.Create(options);
        }
    }

    /// <summary>An IAdminPlatform that records every call in order and answers with configurable results.</summary>
    internal sealed class RecordingAdminPlatform : IAdminPlatform
    {
        private readonly object gate = new object();

        public RecordingAdminPlatform()
        {
            Sdk = 34;
            Free = -1;
            ExitViaGameResult = true;
            ExitViaHostResult = true;
            JniResult = true;
            ShareResult = new AdminShareResult { Ok = true, Method = "sheet", PublicPath = "Download/MCVS-Offline/x", Message = "ok" };
            TextResult = new AdminShareResult { Ok = true, Method = "text" };
            Missing = new List<string>();
            Calls = new SynchronizedList();
        }

        public int Sdk { get; set; }
        public long Free { get; set; }
        public volatile bool BrowserOpen;
        public bool ExitViaGameResult { get; set; }
        public bool ExitViaHostResult { get; set; }
        public bool JniResult { get; set; }
        public AdminShareResult ShareResult { get; set; }
        public AdminShareResult TextResult { get; set; }

        /// <summary>When set, ExitViaGame's result (after the call is recorded): e.g. the restart posted to a UI thread through UiCall.</summary>
        public Func<bool, bool> ExitViaGameHook { get; set; }

        public List<string> Missing { get; set; }
        public SynchronizedList Calls { get; private set; }

        /// <summary>Urls and post data passed to the JNI opens.</summary>
        public List<string> Opened { get; } = new List<string>();

        public int SdkInt { get { return Sdk; } }

        public bool OpenInGameBrowserJni(string url)
        {
            Calls.Add("OpenInGameBrowserJni");
            lock (gate) Opened.Add(url);
            return JniResult;
        }

        public bool OpenInGameBrowserWithPostData(string url, string postData)
        {
            Calls.Add("OpenInGameBrowserWithPostData");
            lock (gate) Opened.Add(url + "|" + postData);
            return JniResult;
        }

        public bool IsInGameBrowserOpen()
        {
            Calls.Add("IsInGameBrowserOpen");
            return BrowserOpen;
        }

        public bool QuitInGameBrowser()
        {
            Calls.Add("QuitInGameBrowser");
            bool was = BrowserOpen;
            BrowserOpen = false;
            return was;
        }

        public AdminShareResult ShareFile(string path, string mimeType, string displayName, string subject, string text)
        {
            Calls.Add("ShareFile:" + mimeType + ":" + displayName + ":" + subject);
            return ShareResult;
        }

        public AdminShareResult ShareText(string subject, string text)
        {
            Calls.Add("ShareText:" + subject);
            return TextResult;
        }

        public bool CopyToClipboard(string label, string text)
        {
            Calls.Add("CopyToClipboard:" + label);
            return true;
        }

        public void ShowToast(string text)
        {
            Calls.Add("ShowToast:" + text);
        }

        public bool ExitViaGame(bool relaunch)
        {
            Calls.Add("ExitViaGame:" + relaunch);
            Func<bool, bool> hook = ExitViaGameHook;
            return hook != null ? hook(relaunch) : ExitViaGameResult;
        }

        public bool ExitViaHost(bool relaunch)
        {
            Calls.Add("ExitViaHost:" + relaunch);
            return ExitViaHostResult;
        }

        public string PublicDownloadsDir()
        {
            return "/storage/emulated/0/Download/MCVS-Offline";
        }

        public long FreeBytes(string path)
        {
            return Free;
        }

        public List<string> MissingJniMembers()
        {
            return new List<string>(Missing);
        }

        /// <summary>Android's rules over <see cref="Sdk"/> and <see cref="Missing"/> (the matrix these tests check).</summary>
        public AdminPlatformCapabilities Capabilities()
        {
            return McvsOffline.Embedded.Android.AndroidCapabilities.From(Sdk, new List<string>(Missing));
        }
    }

    /// <summary>A list safe to add to from several threads (the exit fallback runs on its own).</summary>
    internal sealed class SynchronizedList : IEnumerable<string>
    {
        private readonly List<string> items = new List<string>();

        public void Add(string item)
        {
            lock (items) items.Add(item);
        }

        public void Clear()
        {
            lock (items) items.Clear();
        }

        public List<string> Snapshot()
        {
            lock (items) return new List<string>(items);
        }

        public bool Contains(string item)
        {
            lock (items) return items.Contains(item);
        }

        public int Count
        {
            get { lock (items) return items.Count; }
        }

        public IEnumerator<string> GetEnumerator()
        {
            return Snapshot().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    // ---------------------------------------------------------------------- look-alikes of the game's members (by name)

    /// <summary>Gameloft.Engine.GameApplication: static Instance, World, Context.</summary>
    internal sealed class FakeGameApplication
    {
        public static FakeGameApplication Instance { get; set; }

        public FakeWorld World { get; set; }

        public FakeGameContext Context { get; } = new FakeGameContext();
    }

    /// <summary>The GameSynchronizationContext: the "game thread" is whoever calls <see cref="Drain"/>.</summary>
    internal sealed class FakeGameContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>> items = new ConcurrentQueue<KeyValuePair<SendOrPostCallback, object>>();

        public int Pending { get { return items.Count; } }

        public override void Post(SendOrPostCallback d, object state)
        {
            items.Enqueue(new KeyValuePair<SendOrPostCallback, object>(d, state));
        }

        public void Drain()
        {
            while (items.TryDequeue(out var item)) item.Key(item.Value);
        }
    }

    /// <summary>Gameloft.Engine.ComponentGroupOptions.</summary>
    internal enum FakeGroupOptions
    {
        None = 0,
        IncludeInactives = 1,
    }

    /// <summary>Gameloft.Engine.World: Browser, Authentication, IsServer, EnumerateActiveComponents(Type, options).</summary>
    internal sealed class FakeWorld
    {
        public FakeBrowser Browser { get; set; }

        public FakeAuthentication Authentication { get; set; }

        public bool? IsServer { get; set; }

        public List<object> Components { get; } = new List<object>();

        public Type LastEnumerated { get; private set; }

        public IEnumerable<object> EnumerateActiveComponents(Type type, FakeGroupOptions options)
        {
            LastEnumerated = type;
            return Components.Where(type.IsInstanceOfType).ToList();
        }
    }

    /// <summary>Gameloft.Engine.BrowserComponent: World, IsInGameBrowserInitialized, OpenUrl(string, bool).</summary>
    internal sealed class FakeBrowser
    {
        public FakeWorld World { get; set; }

        public bool IsInGameBrowserInitialized { get; set; }

        public List<string> Opened { get; } = new List<string>();

        public void OpenUrl(string url, bool ingame)
        {
            Opened.Add(url + "|" + ingame);
        }
    }

    /// <summary>A BrowserComponent whose World getter throws.</summary>
    internal sealed class ThrowingBrowser
    {
        public FakeWorld World
        {
            get { throw new InvalidOperationException("probe: World is disposed"); }
        }
    }

    /// <summary>Gameloft.OnlinePlatform.Credential: a struct with an implicit conversion to string.</summary>
    internal struct FakeCredential
    {
        private readonly string text;

        public FakeCredential(string text)
        {
            this.text = text;
        }

        public static implicit operator string(FakeCredential credential)
        {
            return credential.text;
        }
    }

    /// <summary>Gameloft.Engine.AuthenticationComponent: AnonymousCredential, FedId.</summary>
    internal sealed class FakeAuthentication
    {
        public FakeCredential AnonymousCredential { get; set; }

        public FakeCredential FedId { get; set; }
    }

    /// <summary>Game.PlayerProfileComponent: FedId, ForceSaveProfile(CancellationToken).</summary>
    internal sealed class FakeProfile
    {
        public FakeCredential FedId { get; set; }

        public int Saves;

        public Exception Fault;

        public Task ForceSaveProfile(CancellationToken ct)
        {
            Saves++;
            return Fault == null ? Task.CompletedTask : Task.FromException(Fault);
        }
    }

    /// <summary>A FakeGameApplication instance with a client World, a browser and the game's credentials; GameReflection bound to it.</summary>
    internal sealed class FakeGame
    {
        public FakeGameApplication App { get; private set; }

        public FakeWorld World { get; private set; }

        public FakeBrowser Browser { get; private set; }

        public GameReflection Reflection { get; private set; }

        public static FakeGame Install()
        {
            var world = new FakeWorld
            {
                IsServer = false,
                Authentication = new FakeAuthentication { AnonymousCredential = new FakeCredential("anonymous:a1b2c3d4e5f6"), FedId = new FakeCredential("fed_id:me") },
            };
            var browser = new FakeBrowser { World = world, IsInGameBrowserInitialized = true };
            world.Browser = browser;
            var app = new FakeGameApplication { World = world };
            FakeGameApplication.Instance = app;
            var reflection = new GameReflection();
            Assert.True(reflection.Bind(typeof(FakeGameApplication), typeof(FakeProfile)));
            return new FakeGame { App = app, World = world, Browser = browser, Reflection = reflection };
        }

        public static void Uninstall()
        {
            FakeGameApplication.Instance = null;
        }

        /// <summary>A "game thread" draining the queue until disposed.</summary>
        public IDisposable Pump()
        {
            return new Pumper(App.Context);
        }

        private sealed class Pumper : IDisposable
        {
            private readonly Thread thread;
            private volatile bool stop;

            public Pumper(FakeGameContext context)
            {
                thread = new Thread(() =>
                {
                    while (!stop)
                    {
                        context.Drain();
                        Thread.Sleep(5);
                    }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            public void Dispose()
            {
                stop = true;
                thread.Join(5000);
            }
        }
    }
}
