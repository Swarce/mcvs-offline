using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using McvsOffline.Server;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>Save now, restart, CRM reload, unlock now and the clipboard over McvsServer.Handle (design §4.5).</summary>
    public class ActionsApiTests
    {
        private static readonly ExitTimings Fast = new ExitTimings
        {
            ResponseDelayMs = 10, GameLoopTimeoutMs = 1000, SaveTimeoutMs = 500, SaveQuietMs = 50, FlushTimeoutMs = 100,
        };

        private sealed class Rig : IDisposable
        {
            private readonly TempDir dir = new TempDir();

            public Rig(bool withHost = true)
            {
                Server = AdminTestClient.CreateServer(dir.Combine("data"), exitTimings: Fast);
                Client = new AdminTestClient(Server);
                if (withHost)
                {
                    Host = new FakeAdminHost();
                    Host.Info.GameCredential = "anonymous:player1";
                    Server.Context.Features.Set<IAdminHost>(Host);
                    Account = Server.Context.Accounts.GetOrCreate("anonymous:player1").Id;
                }
                Client.OpenSession();
            }

            public McvsServer Server { get; }
            public AdminTestClient Client { get; }
            public FakeAdminHost Host { get; }
            public string Account { get; }

            /// <summary>The game saves when asked (the TS's dump POST arrives as an "http" storage change).</summary>
            public void GameSavesOnRequest()
            {
                Host.OnRequestGameSave = () => Server.Context.Events.Publish(LocalProfileStore.ChangedTopic, new Dictionary<string, object>
                {
                    ["account"] = Account, ["source"] = "http", ["deleted"] = false, ["selector"] = "_mcvs.gameProfile",
                });
            }

            /// <summary>Waits until the sequence has a result (status exit.last).</summary>
            public Dictionary<string, object> WaitForExitResult()
            {
                var api = Server.Context.Features.Get<AdminApi>();
                for (int i = 0; i < 300; i++)
                {
                    var last = api.Exit.Last;
                    if (last != null && (api.ExitState == "idle" || (string)last["result"] == "exiting")) return last;
                    Thread.Sleep(10);
                }
                throw new TimeoutException("the exit sequence did not finish");
            }

            public void Dispose()
            {
                Server.Dispose();
                dir.Dispose();
            }
        }

        [Fact]
        public void WithoutAHostThereIsNothingToRestartOrSave()
        {
            using var rig = new Rig(withHost: false);
            var restart = rig.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true, ["reason"] = "user" });
            Assert.Equal(501, restart.StatusCode);
            Assert.Equal("not_supported", AdminTestClient.At(restart, "error"));
            Assert.Equal(501, rig.Client.Post("actions/save-now", new Dictionary<string, object> { ["screen"] = "saves" }).StatusCode);
            Assert.Equal(501, rig.Client.Post("clipboard", new Dictionary<string, object> { ["text"] = "x" }).StatusCode);
        }

        [Fact]
        public void RestartSavesThenExits()
        {
            using var rig = new Rig();
            rig.GameSavesOnRequest();
            Assert.Equal(400, rig.Client.Post("actions/restart", new Dictionary<string, object> { ["reason"] = "user" }).StatusCode);

            var response = rig.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true, ["reason"] = "settings" });
            Assert.Equal(202, response.StatusCode);
            Assert.Equal(true, AdminTestClient.At(response, "restarting"));

            var last = rig.WaitForExitResult();
            Assert.Equal("exiting", last["result"]);
            Assert.Equal(new[]
            {
                "ShowToast:" + ExitSequence.ToastRestarting, "CloseBrowser", "WaitForGameLoop:1000", "RequestGameSave", "Exit:settings:True",
            }, rig.Host.Calls);

            // The process is ending: writes are refused; status answers without touching the profile.
            var write = rig.Client.Post("settings", new Dictionary<string, object> { ["changes"] = new Dictionary<string, object>() });
            Assert.Equal(409, write.StatusCode);
            Assert.Equal("restarting", AdminTestClient.At(write, "error"));
            Assert.Equal(409, rig.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true }).StatusCode);
            var status = AdminTestClient.Json(rig.Client.Get("status"));
            Assert.Equal("restarting", JsonPath.Get(status, "exit.state"));
            Assert.Equal(rig.Account, JsonPath.Get(status, "player.account"));
            Assert.False(((Dictionary<string, object>)status["player"]).ContainsKey("level"));

            // The profile lock is held: a save of that account would wait (checked without blocking).
            var store = rig.Server.Context.Features.Get<LocalProfileStore>();
            Assert.Equal(1, store.Locks.QueueLength(rig.Account));
        }

        [Fact]
        public void RestartWithoutASaveNeedsConsent()
        {
            using var rig = new Rig();
            Assert.Equal(202, rig.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true }).StatusCode);
            var last = rig.WaitForExitResult();
            Assert.Equal("cancelled", last["result"]);
            Assert.DoesNotContain(rig.Host.Calls, c => c.StartsWith("Exit:"));
            Assert.Equal("idle", AdminTestClient.At(rig.Client.Get("status"), "exit.state"));
            Assert.Equal("cancelled", AdminTestClient.At(rig.Client.Get("status"), "exit.last.result"));

            Assert.Equal(202, rig.Client.Post("actions/restart", new Dictionary<string, object>
            {
                ["confirm"] = true, ["restartWithoutSave"] = true, ["reason"] = "import",
            }).StatusCode);
            Assert.Equal("exiting", rig.WaitForExitResult()["result"]);
            Assert.Contains("Exit:import:True", rig.Host.Calls);
        }

        [Fact]
        public void AHostThatCannotExitAnswers501()
        {
            using var rig = new Rig();
            rig.Host.Info.CanExit = false;
            Assert.Equal(501, rig.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true }).StatusCode);
            rig.Host.Info.CanSave = false;
            Assert.Equal(501, rig.Client.Post("actions/save-now", "{}").StatusCode);
        }

        [Fact]
        public void SaveNowReopensThePageOnItsScreen()
        {
            using var rig = new Rig();
            rig.GameSavesOnRequest();
            var response = rig.Client.Post("actions/save-now", new Dictionary<string, object> { ["screen"] = "saves" });
            Assert.Equal(202, response.StatusCode);
            Assert.Equal(true, AdminTestClient.At(response, "accepted"));
            Assert.Equal("saved", rig.WaitForExitResult()["result"]);
            string url = rig.Host.OpenedUrls.Single();
            Assert.Matches(@"^http://127\.0\.0\.1:51780/_mcvs/ui/#t=[0-9a-f]{32}&s=saves$", url);
            Assert.Equal("OpenPage:resume", rig.Host.Calls.Last());

            // The new ticket opens a session (the page after "Save the game now").
            var fresh = new AdminTestClient(rig.Server);
            Assert.Equal(200, fresh.Post("session", new Dictionary<string, object> { ["ticket"] = AdminTestClient.TicketOf(url) }).StatusCode);
            Assert.NotNull(AdminTestClient.At(fresh.Get("status"), "player.lastGameSave"));

            // No save this time: the page says so.
            rig.Host.OnRequestGameSave = null;
            Assert.Equal(202, rig.Client.Post("actions/save-now", new Dictionary<string, object> { ["screen"] = "saves" }).StatusCode);
            Assert.Equal("nosave", rig.WaitForExitResult()["result"]);
            Assert.EndsWith("&s=saves&r=nosave", rig.Host.OpenedUrls.Last());
        }

        [Fact]
        public void OneSequenceAtATime()
        {
            using var rig = new Rig();
            var gate = new ManualResetEventSlim(false);
            rig.Host.OnRequestGameSave = () => gate.Wait(5000);
            Assert.Equal(202, rig.Client.Post("actions/save-now", "{}").StatusCode);
            var busy = rig.Client.Post("actions/save-now", "{}");
            Assert.Equal(409, busy.StatusCode);
            Assert.Equal("busy", AdminTestClient.At(busy, "error"));
            var restart = rig.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true });
            Assert.Equal(409, restart.StatusCode);
            Assert.Equal("restarting", AdminTestClient.At(restart, "error"));
            gate.Set();
            Assert.Equal("nosave", rig.WaitForExitResult()["result"]);
        }

        [Fact]
        public void CrmReloadAndUnlockNow()
        {
            using var rig = new Rig();
            Assert.Equal(true, AdminTestClient.At(rig.Client.Post("actions/crm-reload", "{}"), "ok"));

            var off = rig.Client.Post("actions/unlock-now", "{}");
            Assert.Equal(409, off.StatusCode);
            Assert.Equal("invalid", AdminTestClient.At(off, "error"));

            rig.Server.Context.Settings.Override("unlockAll", true);
            var queued = rig.Client.Post("actions/unlock-now", "{}");
            Assert.Equal(200, queued.StatusCode);
            Assert.Equal(true, AdminTestClient.At(queued, "queued"));

            rig.Host.Info.GameCredential = null;
            rig.Host.Info.DeviceId = "nobody";
            var unknown = rig.Client.Post("actions/unlock-now", "{}");
            Assert.Equal(409, unknown.StatusCode);
            Assert.Equal("current_player_unknown", AdminTestClient.At(unknown, "error"));
        }

        [Fact]
        public void TheClipboardGoesThroughTheHost()
        {
            using var rig = new Rig();
            var ok = rig.Client.Post("clipboard", new Dictionary<string, object> { ["label"] = "MCVS-SAVE part 1", ["text"] = "MCVS-SAVE:1:..." });
            Assert.Equal(200, ok.StatusCode);
            Assert.Equal("MCVS-SAVE:1:...", rig.Host.Clipboard.Single());
            Assert.Contains("CopyToClipboard:MCVS-SAVE part 1", rig.Host.Calls);

            var tooLong = rig.Client.Post("clipboard", new Dictionary<string, object> { ["text"] = new string('x', 128001) });
            Assert.Equal(413, tooLong.StatusCode);
            Assert.Equal(128000L, AdminTestClient.At(tooLong, "details.limit"));
            Assert.Equal(200, rig.Client.Post("clipboard", new Dictionary<string, object> { ["text"] = new string('x', 128000) }).StatusCode);

            rig.Host.CopyResult = false;
            Assert.Equal(501, rig.Client.Post("clipboard", new Dictionary<string, object> { ["text"] = "x" }).StatusCode);
            rig.Host.CopyResult = true;
            rig.Host.Info.CanCopy = false;
            Assert.Equal(501, rig.Client.Post("clipboard", new Dictionary<string, object> { ["text"] = "x" }).StatusCode);
            Assert.Equal(400, rig.Client.Post("clipboard", "{}").StatusCode);
        }
    }
}
