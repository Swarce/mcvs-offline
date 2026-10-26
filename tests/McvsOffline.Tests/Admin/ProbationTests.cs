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
        public void TheUndoOfARelinkIsNotOnProbationAndStays()
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
            s.SaveGame(bob, 6);   // the game loaded bob: confirmed
            Assert.False(s.ProbationExists);

            s.Session();
            HttpResponse undo = s.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" });
            Assert.True(undo.StatusCode == 200, undo.BodyText);
            s.Restart();
            Assert.Equal(alice, s.Context.Accounts.Find("anonymous:alice").Id);
            Assert.False(s.ProbationExists);   // back to the account the phone played: nothing to wait for

            // The game plays alice and saves alice at every start; the undo is never reverted.
            for (int i = 0; i < 3; i++)
            {
                s.SaveGame(alice, 30);
                s.Restart();
            }
            Assert.Equal(alice, s.Context.Accounts.Find("anonymous:alice").Id);
            var history = s.History();
            Assert.Equal("undo", history[0]["kind"]);
            Assert.Equal("applied", history[0]["result"]);
            Assert.Equal(false, history[0]["probation"]);
            Assert.Equal("confirmed", history[1]["result"]);
        }

        [Fact]
        public void ARestoreOfAnotherAccountIsNotOnProbation()
        {
            using var s = new SavesServer();
            string alice = s.Player("alice").Id;
            string bob = s.Player("bob").Id;
            s.SaveGame(alice, 30);
            s.SaveGame(bob, 6);
            s.Play("alice");
            s.Session();
            string backup = s.Store.CreateBackup(bob, "manual").Id;
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            s.SaveGame(bob, 7);
            HttpResponse restore = s.Client.Post("saves/" + bob + "/restore", new Dictionary<string, object>
            {
                ["backup"] = backup, ["plan"] = "restore-other", ["confirm"] = "restore-other",
            });
            Assert.True(restore.StatusCode == 200, restore.BodyText);
            s.Restart();
            Assert.Equal(6L, s.Store.ReadDocument(bob).Root["level"]);
            Assert.False(s.ProbationExists);

            // The game plays alice, never bob: the restore stays.
            for (int i = 0; i < 3; i++)
            {
                s.SaveGame(alice, 30);
                s.Restart();
            }
            Assert.Equal(6L, s.Store.ReadDocument(bob).Root["level"]);
            Assert.Equal("applied", s.History()[0]["result"]);
        }

        [Fact]
        public void ANewOperationSupersedesAnOpenProbation()
        {
            using var s = RestoredAlice(out string alice, out string bob);
            string restoreId = (string)s.ProbationFile()["id"];
            s.Session();
            Assert.Equal(200, s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }).StatusCode);
            s.Restart();
            var history = s.History();
            Assert.Equal("relink", history[0]["kind"]);
            Assert.Equal(restoreId, history[1]["id"]);
            Assert.Equal("superseded", history[1]["result"]);
            Assert.Equal(history[0]["id"], s.ProbationFile()["id"]);
            Assert.Equal(bob, s.ProbationFile()["target"]);
            Assert.Equal(20L, s.Store.ReadDocument(alice).Root["level"]);   // the superseded restore is not reverted
        }

        [Theory]
        [InlineData("import", "replace", false, false, true)]
        [InlineData("restore", "replace", false, false, true)]
        [InlineData("restore", "restore-other", false, false, false)]
        [InlineData("import", "switch", true, true, true)]
        [InlineData("import", "switch", true, false, false)]   // the login changed since staging: not relinked
        [InlineData("restore", "switch", true, true, true)]
        [InlineData("relink", "switch", true, true, true)]
        [InlineData("relink", "switch", true, false, false)]
        [InlineData("undo", "undo", true, true, false)]
        [InlineData("undo", "undo", false, false, false)]
        public void OnlyAnOperationThatChangesWhatThePhoneLoadsIsOnProbation(string kind, string plan, bool withRelink, bool relinked, bool expected)
        {
            const string target = "0123456789abcdef01234567", other = "fedcba9876543210fedcba98";
            var op = new StagedOperation { Kind = kind, Plan = plan, Target = target };
            if (withRelink)
            {
                op.Relink = kind == StagedOperation.KindUndo
                    ? new RelinkPlan { Credential = "anonymous:x", From = target, To = other }
                    : new RelinkPlan { Credential = "anonymous:x", From = other, To = target };
            }
            Assert.Equal(expected, StagedOperations.NeedsProbation(op, relinked));
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
