using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>Probation of an applied operation and the automatic revert (design §6.5).</summary>
    public class ProbationTests
    {
        /// <summary>alice (the game's player) restored from level 30 to her level-20 backup; applied at the restart.</summary>
        private static SavesServer RestoredAlice(out string alice, out string bob)
        {
            var s = new SavesServer();
            alice = s.Player("alice").Id;
            bob = s.Player("bob").Id;
            s.SaveGame(bob, 6);
            s.SaveGame(alice, 20, SavesServer.BigDump(seed: 20));
            string backup = s.Store.CreateBackup(alice, "manual").Id;
            s.Clock.Advance(TimeSpan.FromMinutes(1));
            s.SaveGame(alice, 30, SavesServer.BigDump(seed: 30));
            s.Play("alice");
            s.Session();
            HttpResponse r = s.Client.Post("saves/" + alice + "/restore", new Dictionary<string, object>
            {
                ["backup"] = backup, ["plan"] = "replace", ["confirm"] = "replace",
            });
            Assert.True(r.StatusCode == 200, r.BodyText);
            s.Restart();
            Assert.Equal(20L, s.Store.ReadDocument(alice).Root["level"]);
            Assert.True(s.ProbationExists);
            return s;
        }

        [Fact]
        public void TheGamesFirstSaveOfTheTargetConfirms()
        {
            using var s = RestoredAlice(out string alice, out string bob);
            s.SaveGame(bob, 7);                                   // another account: not it
            Assert.True(s.ProbationExists);
            s.Store.Commit(alice, "x", "", "admin", doc => new WriteOutcome { Changed = true });   // not the game
            Assert.True(s.ProbationExists);
            s.SaveGame(alice, 20, SavesServer.BigDump(seed: 20));
            Assert.False(s.ProbationExists);
            Assert.Equal("confirmed", s.History()[0]["result"]);
            Assert.True(s.Logs.Contains("confirmed: the game saved " + alice));

            // Confirmed operations stay undoable; nothing reverts later.
            s.Restart();
            s.Restart();
            s.Restart();
            Assert.Equal(20L, s.Store.ReadDocument(alice).Root["level"]);
            s.Session();
            Assert.Equal(true, AdminTestClient.At(s.Client.Get("saves"), "last.undoable"));
        }

        [Fact]
        public void TwoMoreStartsWithoutASaveRevert()
        {
            using var s = RestoredAlice(out string alice, out _);
            string pre = (string)s.ProbationFile()["preBackupId"];
            Assert.NotNull(pre);
            s.Restart();
            Assert.Equal(2L, s.ProbationFile()["startsWithoutSave"]);
            Assert.Equal(20L, s.Store.ReadDocument(alice).Root["level"]);
            s.Restart();
            Assert.False(s.ProbationExists);
            Assert.Equal(30L, s.Store.ReadDocument(alice).Root["level"]);
            Assert.Equal(SavesServer.BigDump(seed: 30), s.Context.Features.Get<IProfileAccess>().ReadText(alice, "_mcvs.gameProfile"));
            Assert.Contains(s.Store.Backups.List(alice), b => b.Reason == "prerevert");
            var last = s.History()[0];
            Assert.Equal("reverted", last["result"]);
            Assert.Contains("did not load this save in 3 starts", (string)last["message"]);
            Assert.True(s.Logs.Contains("reverted: the game did not load this save in 3 starts"));

            s.Session();
            Assert.Equal("reverted", AdminTestClient.At(s.Client.Get("status"), "staged.last.result"));
            Assert.Equal(false, AdminTestClient.At(s.Client.Get("saves"), "last.undoable"));
        }

        [Fact]
        public void AStartWithinTenSecondsOfTheLastIsNotCounted()
        {
            using var s = RestoredAlice(out string alice, out _);
            s.Restart(TimeSpan.FromSeconds(5));   // the second process of one restart
            Assert.Equal(1L, s.ProbationFile()["startsWithoutSave"]);
            s.Restart();
            Assert.Equal(2L, s.ProbationFile()["startsWithoutSave"]);
            s.Restart(TimeSpan.FromSeconds(3));
            Assert.True(s.ProbationExists);
            s.Restart();
            Assert.False(s.ProbationExists);
            Assert.Equal(30L, s.Store.ReadDocument(alice).Root["level"]);
        }

        [Fact]
        public void ARelinkIsRevertedToo()
        {
            using var s = new SavesServer();
            string alice = s.Player("alice").Id;
            string bob = s.Player("bob").Id;
            s.SaveGame(alice, 30);
            s.SaveGame(bob, 6);
            s.Play("alice");
            s.Session();
            Assert.Equal(200, s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }).StatusCode);
            s.Restart();
            Assert.Equal(bob, s.Context.Accounts.Find("anonymous:alice").Id);
            Assert.Equal("anonymous:alice", ((Dictionary<string, object>)s.ProbationFile()["relink"])["credential"]);
            s.Restart();
            s.Restart();
            Assert.Equal(alice, s.Context.Accounts.Find("anonymous:alice").Id);
            Assert.Equal("reverted", s.History()[0]["result"]);
        }

        [Fact]
        public void APendingOperationIsAppliedAfterARevert()
        {
            using var s = RestoredAlice(out string alice, out string bob);
            s.Restart();   // start 2
            s.Session();
            Assert.Equal(200, s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }).StatusCode);
            s.Restart();   // start 3: revert first, then the relink
            var history = s.History();
            Assert.Equal("relink", history[0]["kind"]);
            Assert.Equal("applied", history[0]["result"]);
            Assert.Equal("reverted", history[1]["result"]);
            Assert.Equal(30L, s.Store.ReadDocument(alice).Root["level"]);
            Assert.Equal(bob, s.Context.Accounts.Find("anonymous:alice").Id);
            Assert.Equal(history[0]["id"], s.ProbationFile()["id"]);
        }

        [Fact]
        public void CountStartRules()
        {
            DateTime t = SavesServer.T0;
            var p = new Dictionary<string, object> { ["startsWithoutSave"] = 1L, ["lastStartAt"] = TimeFormat.Iso8601Millis(t) };
            Assert.False(Probation.CountStart(p, t.AddSeconds(9)));
            Assert.Equal(1L, p["startsWithoutSave"]);
            Assert.False(Probation.CountStart(p, t.AddSeconds(30)));
            Assert.Equal(2L, p["startsWithoutSave"]);
            Assert.True(Probation.CountStart(p, t.AddSeconds(60)));
            Assert.Equal(3L, p["startsWithoutSave"]);
            // A clock that went backwards counts as a start (no endless "double start").
            var q = new Dictionary<string, object> { ["startsWithoutSave"] = 1L, ["lastStartAt"] = TimeFormat.Iso8601Millis(t) };
            Probation.CountStart(q, t.AddMinutes(-5));
            Assert.Equal(2L, q["startsWithoutSave"]);
        }
    }
}
