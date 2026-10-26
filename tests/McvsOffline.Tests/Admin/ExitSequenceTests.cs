using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The save-then-exit sequence (design §4.5, §8.8) with a recording host and a fake clock.</summary>
    public class ExitSequenceTests
    {
        private const string Account = "0123456789abcdef01234567";

        private static readonly ExitTimings Fast = new ExitTimings
        {
            ResponseDelayMs = 0, GameLoopTimeoutMs = 8000, SaveTimeoutMs = 400, SaveQuietMs = 50, FlushTimeoutMs = 2000,
        };

        private sealed class Rig
        {
            public readonly FakeAdminHost Host = new FakeAdminHost();
            public readonly ManualClock Clock = new ManualClock(new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc));
            public readonly GameSaveTracker Saves;
            public readonly ExitSequence Exit;
            public readonly MemoryLogSink Logs = new MemoryLogSink();
            public bool ReopenResult = true;
            public bool LockReleased;

            public Rig(string account = Account)
            {
                Saves = new GameSaveTracker(null, Clock);
                var logger = new Logger();
                logger.AddSink(Logs);
                Exit = new ExitSequence(
                    () => Host,
                    Saves,
                    () => new CurrentPlayer { AccountId = account, Source = "game" },
                    a =>
                    {
                        Host.Record("Lock:" + a);
                        return new Releaser(() =>
                        {
                            LockReleased = true;
                            Host.Record("Unlock:" + a);
                        });
                    },
                    ms =>
                    {
                        Host.Record("Flush:" + ms);
                        return true;
                    },
                    (screen, saved) =>
                    {
                        Host.Record("Reopen:" + (screen ?? "-") + ":" + saved);
                        return ReopenResult;
                    },
                    Clock, logger.For("admin"), Fast);
            }

            /// <summary>The game saves the account (an "http" storage event) when asked to.</summary>
            public void GameSavesOnRequest(string account = Account, string source = "http")
            {
                Host.OnRequestGameSave = () => Saves.OnProfileChanged(Change(account, source));
            }

            public void Run(ExitRequest request)
            {
                Assert.True(Exit.TryBegin(request, out string refusal), refusal);
                Exit.Run(request);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly Action action;
            public Releaser(Action action) => this.action = action;
            public void Dispose() => action();
        }

        private static Dictionary<string, object> Change(string account, string source, bool deleted = false) =>
            new Dictionary<string, object> { ["account"] = account, ["source"] = source, ["deleted"] = deleted, ["selector"] = "_mcvs.gameProfile" };

        [Fact]
        public void SaveNowClosesSavesAndReopens()
        {
            var rig = new Rig();
            rig.GameSavesOnRequest();
            rig.Run(new ExitRequest { Kind = ExitRequest.SaveNow, Screen = "saves" });

            Assert.Equal(new[]
            {
                "ShowToast:" + ExitSequence.ToastSaving, "CloseBrowser", "WaitForGameLoop:8000", "RequestGameSave", "Reopen:saves:True",
            }, rig.Host.Calls);
            Assert.Equal(ExitSequence.Idle, rig.Exit.State);
            Assert.Equal("saved", rig.Exit.Last["result"]);
            Assert.Equal("save-now", rig.Exit.Last["kind"]);
            Assert.True(rig.Logs.Contains("admin: exit save-now: close the browser"));
        }

        [Fact]
        public void SaveNowWithoutASaveReopensWithNoSave()
        {
            var rig = new Rig();
            var watch = Stopwatch.StartNew();
            rig.Run(new ExitRequest { Kind = ExitRequest.SaveNow, Screen = "saves" });
            Assert.True(watch.ElapsedMilliseconds >= 350, "waited for the save");
            Assert.Equal("Reopen:saves:False", rig.Host.Calls[rig.Host.Calls.Count - 1]);
            Assert.Equal("nosave", rig.Exit.Last["result"]);
            Assert.Equal(ExitSequence.Idle, rig.Exit.State);
        }

        [Fact]
        public void RestartSavesLocksFlushesAndExits()
        {
            var rig = new Rig();
            rig.GameSavesOnRequest();
            rig.Run(new ExitRequest { Kind = ExitRequest.Restart, Reason = "settings" });

            Assert.Equal(new[]
            {
                "ShowToast:" + ExitSequence.ToastRestarting, "CloseBrowser", "WaitForGameLoop:8000", "RequestGameSave",
                "Lock:" + Account, "Flush:2000", "Exit:settings:True",
            }, rig.Host.Calls);
            Assert.Equal("exiting", rig.Exit.Last["result"]);
            // The process is ending: the lock stays held and the module stays "restarting".
            Assert.False(rig.LockReleased);
            Assert.Equal(ExitSequence.Restarting, rig.Exit.State);
            Assert.False(rig.Exit.TryBegin(new ExitRequest { Kind = ExitRequest.SaveNow }, out string refusal));
            Assert.Equal("restarting", refusal);
        }

        [Fact]
        public void NoSaveMeansNoRestartWithoutConsent()
        {
            var rig = new Rig();
            rig.Run(new ExitRequest { Kind = ExitRequest.Restart, Reason = "user" });

            Assert.DoesNotContain(rig.Host.Calls, c => c.StartsWith("Exit:") || c.StartsWith("Lock:"));
            Assert.Equal("ShowToast:" + ExitSequence.ToastNotSaved, rig.Host.Calls[rig.Host.Calls.Count - 1]);
            Assert.Equal("cancelled", rig.Exit.Last["result"]);
            Assert.Equal("The game did not save, so it was not restarted.", rig.Exit.Last["message"]);
            Assert.Equal(ExitSequence.Idle, rig.Exit.State);
        }

        [Fact]
        public void ConsentRestartsWithoutASave()
        {
            var rig = new Rig();
            rig.Run(new ExitRequest { Kind = ExitRequest.Restart, Reason = "user", RestartWithoutSave = true });
            Assert.Contains("Exit:user:True", rig.Host.Calls);
            Assert.Equal("exiting", rig.Exit.Last["result"]);
        }

        [Fact]
        public void AFailedExitReleasesTheLockAndClearsTheFlag()
        {
            var rig = new Rig();
            rig.GameSavesOnRequest();
            rig.Host.ExitResult = false;
            rig.Run(new ExitRequest { Kind = ExitRequest.Restart, Reason = "import" });

            List<string> calls = rig.Host.Calls;
            Assert.Equal("Exit:import:True", calls[calls.Count - 3]);
            Assert.Equal("ShowToast:" + ExitSequence.ToastFailed, calls[calls.Count - 2]);
            Assert.Equal("Unlock:" + Account, calls[calls.Count - 1]);
            Assert.True(rig.LockReleased);
            Assert.Equal("failed", rig.Exit.Last["result"]);
            Assert.Equal(ExitSequence.Idle, rig.Exit.State);
            Assert.True(rig.Exit.TryBegin(new ExitRequest { Kind = ExitRequest.SaveNow }, out _));
        }

        [Fact]
        public void OneSequenceAtATime()
        {
            var rig = new Rig();
            Assert.True(rig.Exit.TryBegin(new ExitRequest { Kind = ExitRequest.SaveNow }, out _));
            Assert.Equal(ExitSequence.Saving, rig.Exit.State);
            Assert.False(rig.Exit.TryBegin(new ExitRequest { Kind = ExitRequest.SaveNow }, out string busy));
            Assert.Equal("busy", busy);
            Assert.False(rig.Exit.TryBegin(new ExitRequest { Kind = ExitRequest.Restart }, out _));
            Assert.Equal(Account, rig.Exit.Player.AccountId);
        }

        [Fact]
        public void AnAdminSaveOrAnotherAccountIsNotTheGamesSave()
        {
            var rig = new Rig();
            rig.Host.OnRequestGameSave = () =>
            {
                rig.Saves.OnProfileChanged(Change(Account, "admin"));
                rig.Saves.OnProfileChanged(Change("ffffffffffffffffffffffff", "http"));
                rig.Saves.OnProfileChanged(Change(Account, "http", deleted: true));
            };
            rig.Run(new ExitRequest { Kind = ExitRequest.Restart, Reason = "user" });
            Assert.Equal("cancelled", rig.Exit.Last["result"]);
        }

        [Fact]
        public void WithoutAHostNothingHappens()
        {
            var exit = new ExitSequence(() => null, null, () => null, null, null, null, null, null, Fast);
            Assert.True(exit.TryBegin(new ExitRequest { Kind = ExitRequest.Restart }, out _));
            exit.Run(new ExitRequest { Kind = ExitRequest.Restart });
            Assert.Equal("failed", exit.Last["result"]);
            Assert.Equal(ExitSequence.Idle, exit.State);
        }

        // ------------------------------------------------------------------ GameSaveTracker

        [Fact]
        public void TheTrackerKeepsOnlyTheGamesSaves()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc));
            var hub = new EventHub(null);
            var tracker = new GameSaveTracker(hub, clock);
            Assert.Null(tracker.LastGameSave(Account));

            hub.Publish("storage.profile.changed", Change(Account, "admin"));
            Assert.Null(tracker.LastGameSave(Account));
            hub.Publish("storage.profile.changed", Change(Account, "http"));
            Assert.Equal(clock.UtcNow, tracker.LastGameSave(Account));
            clock.Advance(TimeSpan.FromMinutes(1));
            hub.Publish("storage.profile.changed", Change(Account, "http", deleted: true));
            Assert.Equal(clock.UtcNow.AddMinutes(-1), tracker.LastGameSave(Account));
        }

        [Fact]
        public void WaitForSaveHonoursSinceAndTheQuietPeriod()
        {
            var clock = new ManualClock(new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc));
            var tracker = new GameSaveTracker(null, clock);

            // A save before "since" does not count.
            tracker.OnProfileChanged(Change(Account, "http"));
            clock.Advance(TimeSpan.FromSeconds(5));
            DateTime since = clock.UtcNow;
            Assert.False(tracker.WaitForSave(Account, since, 100, 10));

            // Saves that keep coming delay the answer until they stop for the quiet period.
            var saver = new Thread(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    Thread.Sleep(40);
                    tracker.OnProfileChanged(Change(Account, "http"));
                }
            });
            var watch = Stopwatch.StartNew();
            saver.Start();
            Assert.True(tracker.WaitForSave(Account, since, 5000, 150));
            Assert.True(watch.ElapsedMilliseconds >= 300, "returned after the last save plus the quiet period: " + watch.ElapsedMilliseconds);
            saver.Join();

            // Unknown account: false at the timeout.
            Assert.False(tracker.WaitForSave("eeeeeeeeeeeeeeeeeeeeeeee", since, 50, 10));
            Assert.False(tracker.WaitForSave(null, since, 50, 10));
        }
    }
}
