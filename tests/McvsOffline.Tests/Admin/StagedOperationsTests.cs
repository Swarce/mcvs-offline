using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Server.Services.Storage;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// Staged operations applied at the next start, before the port opens (design §6.4): the import from another phone
    /// (switch), restores, relinks, undo; resumable steps, attempts, damaged payloads, a login that changed since staging.
    /// </summary>
    public class StagedOperationsTests
    {
        private const string OldInbox = "{\"messages\":[{\"id\":\"old\"}]}";
        private const string NewInbox = "{\"messages\":[{\"id\":\"from-the-old-phone\",\"type\":\"coupon\"}]}";

        /// <summary>
        /// The old phone (A): credential c1 plays account X (level 41) with an unclaimed inbox message. The new phone (B):
        /// credential c2 plays account Y (level 3). Returns the transfer text of X exported on A.
        /// </summary>
        private sealed class TwoPhones : IDisposable
        {
            public TwoPhones(bool xOnB = false)
            {
                A = new SavesServer(name: "phoneA");
                X = A.Player("c1oldphone").Id;
                A.SaveGame(X, 40, SavesServer.BigDump(seed: 7));
                A.SaveGame(X, 41, SavesServer.BigDump(seed: 7));   // revision 2
                A.Context.OpenStore("messages").WriteText(X + ".inbox", NewInbox);
                A.Play("c1oldphone");
                A.Session();
                Parts = A.ExportParts(partChars: 3900);
                XFile = A.ProfileFile(X);

                B = new SavesServer(dir: A.Dir, name: "phoneB");
                Y = B.Player("c2newphone").Id;
                B.SaveGame(Y, 3);
                if (xOnB)
                {
                    // B already has an older copy of X (say from an earlier import) and X's own inbox.
                    B.Context.Accounts.GetOrCreate("fed_id:" + X);
                    B.SaveGame(X, 10);
                    B.Context.OpenStore("messages").WriteText(X + ".inbox", OldInbox);
                }
                B.Play("c2newphone");
                B.Session();
            }

            public SavesServer A { get; }

            public SavesServer B { get; }

            public string X { get; }

            public string Y { get; }

            public List<string> Parts { get; }

            public string XFile { get; }

            /// <summary>Pastes the parts on B (two pastes) and stages the switch.</summary>
            public HttpResponse StageSwitch()
            {
                B.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = Parts[0] });
                HttpResponse done = B.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = string.Join("\n", Parts.Skip(1)) });
                Assert.True(done.StatusCode == 200, done.BodyText);
                Assert.Equal("switch", AdminTestClient.At(done, "plan"));
                Assert.Equal(true, AdminTestClient.At(done, "canStage"));
                HttpResponse staged = B.Client.Post("saves/import/stage", new Dictionary<string, object>
                {
                    ["uploadId"] = AdminTestClient.At(done, "uploadId"), ["confirm"] = "switch",
                });
                Assert.True(staged.StatusCode == 200, staged.BodyText);
                return staged;
            }

            public void Dispose()
            {
                B.Dispose();
                A.Dispose();
            }
        }

        private static string Root(LocalProfileStore store, string account) => ProfileJson.Serialize(store.ReadDocument(account).Root);

        [Fact]
        public void ASwitchImportFromAnotherPhoneIsAppliedBeforeTheServerStarts()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            string token = B.Context.Tokens.Issue(phones.Y, "anonymous:c2newphone", McvsConstants.ClientId, new[] { "auth", "storage" }, "dev").Token;
            HttpResponse staged = phones.StageSwitch();
            Assert.Equal(phones.X, AdminTestClient.At(staged, "staged.target"));
            Assert.Null(AdminTestClient.At(staged, "staged.stageBackup"));   // X has no file here yet
            Assert.Null(B.ProfileFile(phones.X));                            // nothing is written before the restart (I3)
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);

            B.Restart();
            // Before Start: the profile, the inbox and the login are already X's.
            LocalProfileStore store = B.Store;
            Assert.Equal(Root(phones.A.Store, phones.X), Root(store, phones.X));
            Assert.Equal(SavesServer.BigDump(seed: 7), B.Context.Features.Get<IProfileAccess>().ReadText(phones.X, "_mcvs.gameProfile"));
            Assert.True(store.ReadDocument(phones.X).Revision >= 3);
            Assert.Equal(NewInbox, B.Context.OpenStore("messages").ReadText(phones.X + ".inbox"));
            Assert.Equal(phones.X, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Null(B.Context.Tokens.Resolve(token));                     // the old grant of c2 (on Y) is gone
            Assert.NotNull(store.ReadDocument(phones.Y).Root["level"]);        // Y keeps its save

            Assert.False(B.PendingExists);
            Assert.Empty(Directory.GetFiles(B.StagedDir, "*.payload.json"));
            var history = B.History().Single();
            Assert.Equal("applied", history["result"]);
            Assert.Equal("import", history["kind"]);
            Assert.Equal("switch", history["plan"]);
            Assert.Equal(true, history["relinked"]);
            Assert.Equal(new object[] { "account", "profile", "messages", "relink", "probation" }, (List<object>)history["done"]);
            Assert.True(history.ContainsKey("durationMs"));
            Assert.Null(history["preBackupId"]);
            Assert.True(B.ProbationExists);
            Assert.Equal(1L, B.ProbationFile()["startsWithoutSave"]);
            Assert.True(B.Logs.Contains("applied to " + phones.X));
            Assert.True(B.Logs.Contains("credential anonymous:c2ne…hone now opens account " + phones.X + " (was " + phones.Y + ")"));

            // The page sees it and can undo it.
            B.Session();
            HttpResponse saves = B.Client.Get("saves");
            Assert.Equal(phones.X, AdminTestClient.At(saves, "current.account"));
            Assert.Equal("applied", AdminTestClient.At(saves, "last.result"));
            Assert.Equal(true, AdminTestClient.At(saves, "last.undoable"));
            Assert.Equal("anonymous:c2ne…hone", AdminTestClient.At(saves, "probation.relink.credential"));

            // The game's first save of X (the TS loads the profile and saves it at once) confirms the probation.
            B.SaveGame(phones.X, 41, SavesServer.BigDump(seed: 7));
            Assert.False(B.ProbationExists);
            Assert.Equal("confirmed", B.History().Single()["result"]);
        }

        [Fact]
        public void TheStageTimeBackupIsReusedWhenTheFileDidNotChange()
        {
            using var phones = new TwoPhones(xOnB: true);
            var B = phones.B;
            HttpResponse staged = phones.StageSwitch();
            string stageBackup = (string)AdminTestClient.At(staged, "staged.stageBackup.id");
            Assert.EndsWith("-preimport", stageBackup);
            string before = B.ProfileFile(phones.X);

            B.Restart();
            var history = B.History().Single();
            Assert.Equal("applied", history["result"]);
            Assert.Equal(stageBackup, history["preBackupId"]);
            Assert.Single(B.Store.Backups.List(phones.X), b => b.Pool == "system");
            Assert.Equal(before, B.Store.ReadBackupText(phones.X, stageBackup));
            // The inbox it replaced is kept.
            Assert.Equal(OldInbox, File.ReadAllText(Path.Combine(B.StagedDir, "history", (string)history["id"], "messages", "inbox.json")));
            Assert.Equal(NewInbox, B.Context.OpenStore("messages").ReadText(phones.X + ".inbox"));
        }

        [Fact]
        public void ASaveAfterStagingIsBackedUpAgainBeforeTheApply()
        {
            using var phones = new TwoPhones(xOnB: true);
            var B = phones.B;
            string stageBackup = (string)AdminTestClient.At(phones.StageSwitch(), "staged.stageBackup.id");
            B.Clock.Advance(TimeSpan.FromSeconds(5));
            B.SaveGame(phones.X, 11);   // the TS saved X after staging
            string latest = B.ProfileFile(phones.X);

            B.Restart();
            var history = B.History().Single();
            string pre = (string)history["preBackupId"];
            Assert.NotEqual(stageBackup, pre);
            Assert.EndsWith("-preimport", pre);
            Assert.Equal(latest, B.Store.ReadBackupText(phones.X, pre));
            Assert.Equal(2, B.Store.Backups.List(phones.X).Count(b => b.Reason == "preimport"));
        }

        [Fact]
        public void ACrashInTheMiddleResumesAtTheFailedStep()
        {
            using var phones = new TwoPhones(xOnB: true);
            var B = phones.B;
            phones.StageSwitch();
            // Step "messages" fails: the inbox's file name is taken by a folder.
            string inbox = B.Context.OpenStore("messages").PathFor(phones.X + ".inbox");
            File.Delete(inbox);
            Directory.CreateDirectory(inbox);

            B.Restart();
            Assert.True(B.PendingExists);
            var pending = B.Pending();
            Assert.Equal(new object[] { "account", "profile" }, (List<object>)pending["done"]);
            Assert.Equal(1L, pending["attempts"]);
            Assert.True(B.Logs.Contains("step messages failed"));
            Assert.Equal(Root(phones.A.Store, phones.X), Root(B.Store, phones.X));            // the profile is written...
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);       // ...the relink is not
            Assert.False(B.ProbationExists);
            int systemBackups = B.Store.Backups.List(phones.X).Count(b => b.Pool == "system");

            Directory.Delete(inbox);
            B.Restart();
            Assert.False(B.PendingExists);
            var history = B.History().Single();
            Assert.Equal("applied", history["result"]);
            Assert.Equal(2L, history["attempts"]);
            Assert.Equal(phones.X, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Equal(NewInbox, B.Context.OpenStore("messages").ReadText(phones.X + ".inbox"));
            Assert.Equal(systemBackups, B.Store.Backups.List(phones.X).Count(b => b.Pool == "system"));   // no second preimport
            Assert.True(B.ProbationExists);
        }

        [Fact]
        public void AResumeAfterTheRelinkStepStillPutsTheSwitchOnProbation()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            phones.StageSwitch();
            string blocker = Path.Combine(B.StagedDir, "probation.json");
            Directory.CreateDirectory(blocker);   // step probation fails once

            B.Restart();
            Assert.True(B.PendingExists);
            Assert.Equal(new object[] { "account", "profile", "messages", "relink" }, (List<object>)B.Pending()["done"]);
            Assert.Equal(phones.X, B.Context.Accounts.Find("anonymous:c2newphone").Id);

            Directory.Delete(blocker);
            B.Restart();
            var history = B.History().Single();
            Assert.Equal("applied", history["result"]);
            Assert.Equal(true, history["relinked"]);   // read back from the accounts, not assumed
            Assert.Equal(true, history["probation"]);
            Assert.Equal(phones.X, B.ProbationFile()["target"]);
        }

        [Fact]
        public void AfterThreeAttemptsItGivesUp()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            phones.StageSwitch();
            string inbox = B.Context.OpenStore("messages").PathFor(phones.X + ".inbox");
            Directory.CreateDirectory(inbox);
            for (int i = 1; i <= 3; i++)
            {
                B.Restart();
                Assert.True(B.PendingExists);
                Assert.Equal((long)i, B.Pending()["attempts"]);
            }
            B.Restart();
            Assert.False(B.PendingExists);
            var history = B.History().Single();
            Assert.Equal("failed", history["result"]);
            Assert.Contains("Gave up after 3 attempts", (string)history["message"]);
            Assert.Equal(new object[] { "account", "profile" }, (List<object>)history["done"]);
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.False(B.ProbationExists);
        }

        [Fact]
        public void ADamagedPayloadFailsWithoutWritingOrRelinking()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            phones.StageSwitch();
            string payload = Directory.GetFiles(B.StagedDir, "*.payload.json").Single();
            File.AppendAllText(payload, " ");   // one byte more: the payload no longer matches its CRC

            B.Restart();
            Assert.False(B.PendingExists);
            Assert.Empty(Directory.GetFiles(B.StagedDir, "*.payload.json"));
            var history = B.History().Single();
            Assert.Equal("failed", history["result"]);
            Assert.Contains("damaged", (string)history["message"]);
            Assert.Empty((List<object>)history["done"]);
            Assert.Null(B.ProfileFile(phones.X));
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.False(B.ProbationExists);
        }

        [Fact]
        public void ALoginThatMovedSinceStagingIsNotRelinked()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            phones.StageSwitch();
            string z = B.Player("zed").Id;
            B.Context.Accounts.LinkCredential(z, "anonymous:c2newphone");   // something else moved c2 meanwhile

            B.Restart();
            var history = B.History().Single();
            Assert.Equal("applied", history["result"]);
            Assert.Equal(false, history["relinked"]);
            Assert.Contains((List<object>)history["warnings"], w => ((string)w).Contains("changed since staging"));
            Assert.Equal(z, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Equal(Root(phones.A.Store, phones.X), Root(B.Store, phones.X));

            // The phone does not load X, so no save of X would ever confirm it: no probation, and nothing is reverted later.
            Assert.False(B.ProbationExists);
            Assert.Equal(false, history["probation"]);
            B.SaveGame(z, 1);
            B.Restart();
            B.Restart();
            Assert.Equal("applied", B.History().Single()["result"]);
            Assert.Equal(Root(phones.A.Store, phones.X), Root(B.Store, phones.X));
            Assert.Equal(z, B.Context.Accounts.Find("anonymous:c2newphone").Id);
        }

        [Fact]
        public void WithoutTheGamesSaveTwoMoreStartsRevertASwitchImport()
        {
            using var phones = new TwoPhones(xOnB: true);
            var B = phones.B;
            string xBefore = Root(B.Store, phones.X);
            phones.StageSwitch();
            B.Restart();
            Assert.Equal(phones.X, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Equal(41L, B.Store.ReadDocument(phones.X).Root["level"]);
            Assert.True(B.ProbationExists);
            Assert.Equal(phones.X, B.ProbationFile()["target"]);
            Assert.Equal(true, B.History().Single()["probation"]);
            string token = B.Context.Tokens.Issue(phones.X, "anonymous:c2newphone", McvsConstants.ClientId, new[] { "auth", "storage" }, "dev").Token;

            B.Restart();   // the game never saved X (say it cannot load it)
            B.Restart();
            Assert.False(B.ProbationExists);
            Assert.Equal(xBefore, Root(B.Store, phones.X));                                  // X's pre-backup restored
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);       // c2 back on Y
            Assert.Null(B.Context.Tokens.Resolve(token));
            Assert.Contains(B.Store.Backups.List(phones.X), b => b.Reason == "prerevert");
            Assert.Equal("reverted", B.History().Single()["result"]);
        }

        [Fact]
        public void AnApplyWithKeepZeroStillTakesItsPreimportBackup()
        {
            using var phones = new TwoPhones(xOnB: true);
            var B = phones.B;
            B.Context.Settings.Set("storage.backups.keep", 0L);
            B.Context.Settings.Save();
            phones.StageSwitch();
            B.Clock.Advance(TimeSpan.FromSeconds(5));
            B.SaveGame(phones.X, 11);   // the TS saved X after staging: the apply takes its own backup
            string latest = B.ProfileFile(phones.X);

            B.Restart();
            Assert.Equal(0L, B.Context.Settings.GetLong("storage.backups.keep", 20));   // this start runs with keep 0
            var history = B.History().Single();
            Assert.Equal("applied", history["result"]);
            Assert.Equal(2, B.Store.Backups.List(phones.X).Count(b => b.Reason == "preimport"));
            Assert.Equal(latest, B.Store.ReadBackupText(phones.X, (string)history["preBackupId"]));
            Assert.DoesNotContain(B.Store.Backups.List(phones.X), b => b.Pool == "auto");
        }

        [Fact]
        public void UndoOfASwitchGoesBackToThePreviousAccount()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            phones.StageSwitch();
            B.Restart();
            B.Play("c2newphone");
            B.Session();
            HttpResponse undo = B.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" });
            Assert.True(undo.StatusCode == 200, undo.BodyText);
            Assert.StartsWith("Go back to", (string)AdminTestClient.At(undo, "description"));
            Assert.Equal("undo", AdminTestClient.At(undo, "staged.kind"));
            Assert.Equal(phones.Y, AdminTestClient.At(undo, "staged.relink.to"));
            Assert.Null(AdminTestClient.At(undo, "staged.restoreBackupId"));   // X did not exist before: nothing to restore

            B.Restart();
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            var history = B.History();
            Assert.Equal("undo", history[0]["kind"]);
            Assert.Equal("applied", history[0]["result"]);
            Assert.Equal(history[1]["id"], history[0]["undoOf"]);
            Assert.Equal("superseded", history[1]["result"]);                  // the import's probation ended with it
            Assert.NotNull(B.ProfileFile(phones.X));                            // X stays on the phone
            // An undo takes the phone back to what it played: no probation, so it never undoes itself.
            Assert.False(B.ProbationExists);
            Assert.Equal(false, history[0]["probation"]);
            for (int i = 0; i < 3; i++)
            {
                B.SaveGame(phones.Y, 3);
                B.Restart();
            }
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Equal("applied", B.History()[0]["result"]);
            // An undo cannot be undone.
            B.Session();
            Assert.Equal(409, B.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" }).StatusCode);
            Assert.Equal(false, AdminTestClient.At(B.Client.Get("saves"), "last.undoable"));
        }

        [Fact]
        public void UndoOfAConfirmedSwitchImportRestoresXAndStays()
        {
            using var phones = new TwoPhones(xOnB: true);
            var B = phones.B;
            string xBefore = Root(B.Store, phones.X);   // level 10
            phones.StageSwitch();
            B.Restart();
            B.SaveGame(phones.X, 41, SavesServer.BigDump(seed: 7));   // the game loaded X: confirmed
            Assert.False(B.ProbationExists);
            Assert.Equal("confirmed", B.History().Single()["result"]);

            B.Session();
            HttpResponse undo = B.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" });
            Assert.True(undo.StatusCode == 200, undo.BodyText);
            Assert.Contains("goes back to how it was before the import", (string)AdminTestClient.At(undo, "description"));
            Assert.NotNull(AdminTestClient.At(undo, "staged.restoreBackupId"));
            Assert.NotNull(AdminTestClient.At(undo, "staged.payload"));   // the pre-import state, copied at staging

            B.Restart();
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Equal(xBefore, Root(B.Store, phones.X));
            Assert.False(B.ProbationExists);
            // The phone plays Y again and the game saves Y at every start: the undo stays.
            for (int i = 0; i < 3; i++)
            {
                B.SaveGame(phones.Y, 3);
                B.Restart();
            }
            Assert.Equal(phones.Y, B.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.Equal(xBefore, Root(B.Store, phones.X));
            Assert.Equal("applied", B.History()[0]["result"]);
            Assert.Equal("confirmed", B.History()[1]["result"]);
        }

        [Fact]
        public void UndoOfAReplaceRestoresTheSaveFromBefore()
        {
            using var s = new SavesServer();
            string a = s.Player("alice").Id;
            s.SaveGame(a, 20, SavesServer.BigDump(seed: 3));
            s.Play("alice");
            s.Session();
            List<string> parts = s.ExportParts(partChars: 60000);
            s.SaveGame(a, 25, SavesServer.BigDump(seed: 4));   // played on after the export
            string played = s.Store.ReadDocument(a).Root["level"].ToString();
            Assert.Equal("25", played);

            HttpResponse pasted = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = string.Concat(parts) });
            Assert.Equal(200, s.Client.Post("saves/import/stage", new Dictionary<string, object>
            {
                ["uploadId"] = AdminTestClient.At(pasted, "uploadId"), ["confirm"] = "replace", ["allowOlder"] = true,
            }).StatusCode);
            s.Restart();
            Assert.Equal(20L, s.Store.ReadDocument(a).Root["level"]);
            Assert.Equal(a, s.Context.Accounts.Find("anonymous:alice").Id);

            s.Session();
            HttpResponse undo = s.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" });
            Assert.True(undo.StatusCode == 200, undo.BodyText);
            Assert.StartsWith("Restore", (string)AdminTestClient.At(undo, "description"));
            s.Restart();
            Assert.Equal(25L, s.Store.ReadDocument(a).Root["level"]);
            Assert.Equal(SavesServer.BigDump(seed: 4), s.Context.Features.Get<IProfileAccess>().ReadText(a, "_mcvs.gameProfile"));
        }

        [Fact]
        public void ARestoreAndARelinkAreAppliedAtTheNextStart()
        {
            using var s = new SavesServer();
            string a = s.Player("alice").Id;
            string b = s.Player("bob").Id;
            s.SaveGame(a, 30);
            s.SaveGame(b, 6);
            s.Play("alice");
            s.Session();
            string backup = s.Store.CreateBackup(b, "manual").Id;
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            s.SaveGame(b, 7);
            HttpResponse restore = s.Client.Post("saves/" + b + "/restore", new Dictionary<string, object>
            {
                ["backup"] = backup, ["plan"] = "restore-other", ["confirm"] = "restore-other",
            });
            Assert.True(restore.StatusCode == 200, restore.BodyText);
            s.Restart();
            Assert.Equal(6L, s.Store.ReadDocument(b).Root["level"]);
            Assert.Equal(a, s.Context.Accounts.Find("anonymous:alice").Id);
            Assert.Equal("restore", s.History()[0]["kind"]);
            Assert.False(s.ProbationExists);   // the phone plays alice, not bob: no save of bob would confirm it
            Assert.Equal(false, s.History()[0]["probation"]);

            s.Session();
            Assert.Equal(200, s.Client.Post("saves/" + b + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }).StatusCode);
            s.Restart();
            Assert.Equal(b, s.Context.Accounts.Find("anonymous:alice").Id);
            Assert.Equal("relink", s.History()[0]["kind"]);
            Assert.Equal(true, s.History()[0]["relinked"]);
            Assert.Equal(new object[] { "relink", "probation" }, (List<object>)s.History()[0]["done"]);
            Assert.Equal(true, s.History()[0]["probation"]);
            Assert.Equal(b, s.ProbationFile()["target"]);
            Assert.Equal("applied", s.History()[1]["result"]);   // the restore was never on probation
        }

        [Fact]
        public void StagingARestoreOfTheOldestSystemBackupKeepsIt()
        {
            using var s = new SavesServer();
            string alice = s.Player("alice").Id;
            s.SaveGame(alice, 5);
            s.Play("alice");
            s.Session();
            string oldest = s.Store.CreateBackup(alice, "preimport").Id;   // "before my import", level 5
            for (int i = 0; i < 9; i++)
            {
                s.Clock.Advance(TimeSpan.FromSeconds(1));
                s.SaveGame(alice, 6 + i);
                s.Store.CreateBackup(alice, "presettings");
            }
            Assert.Equal(10, s.Store.Backups.List(alice).Count(b => b.Pool == "system"));
            var restore = new Dictionary<string, object> { ["backup"] = oldest, ["plan"] = "replace", ["confirm"] = "replace" };

            // Staging takes a prerestore backup; its pruning spares the backup being restored.
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            HttpResponse staged = s.Client.Post("saves/" + alice + "/restore", restore);
            Assert.True(staged.StatusCode == 200, staged.BodyText);
            Assert.NotNull(s.Store.Backups.Find(alice, oldest));
            string payload = Path.Combine(s.StagedDir, (string)AdminTestClient.At(staged, "staged.payload"));
            Assert.Equal(s.Store.ReadBackupText(alice, oldest), File.ReadAllText(payload));
            // Cancelled: the backup is still there, the copy is gone.
            Assert.Equal(true, AdminTestClient.At(s.Client.Delete("saves/staged"), "removed"));
            Assert.NotNull(s.Store.Backups.Find(alice, oldest));
            Assert.False(File.Exists(payload));

            s.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(200, s.Client.Post("saves/" + alice + "/restore", restore).StatusCode);
            // The pool rotates on before the restart (another system backup): the staged copy still applies.
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            s.Store.CreateBackup(alice, "presettings");
            Assert.Null(s.Store.Backups.Find(alice, oldest));

            s.Restart();
            var history = s.History()[0];
            Assert.True((string)history["result"] == "applied", (string)history["message"]);
            Assert.Equal(5L, s.Store.ReadDocument(alice).Root["level"]);
            Assert.Empty(Directory.GetFiles(s.StagedDir, "*.payload.json"));
        }

        [Fact]
        public void AnUndoWithAFullSystemPoolStillRestoresTheSaveFromBefore()
        {
            using var s = new SavesServer();
            string alice = s.Player("alice").Id;
            s.SaveGame(alice, 20);
            string backup = s.Store.CreateBackup(alice, "manual").Id;
            s.Clock.Advance(TimeSpan.FromSeconds(1));
            s.SaveGame(alice, 30);
            s.Play("alice");
            s.Session();
            Assert.Equal(200, s.Client.Post("saves/" + alice + "/restore", new Dictionary<string, object>
            {
                ["backup"] = backup, ["plan"] = "replace", ["confirm"] = "replace",
            }).StatusCode);
            s.Restart();
            s.SaveGame(alice, 20);   // confirmed
            string pre = (string)s.History()[0]["preBackupId"];   // the level-30 state, the oldest system backup
            for (int i = 0; i < 9; i++)
            {
                s.Clock.Advance(TimeSpan.FromSeconds(1));
                s.Store.CreateBackup(alice, "presettings");   // nine risky settings changes
            }
            s.Session();
            Assert.Equal(true, AdminTestClient.At(s.Client.Get("saves"), "last.undoable"));

            HttpResponse undo = s.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" });
            Assert.True(undo.StatusCode == 200, undo.BodyText);
            Assert.NotNull(s.Store.Backups.Find(alice, pre));   // the prerestore backup of the undo spared it
            Assert.NotNull(AdminTestClient.At(undo, "staged.payload"));
            s.Restart();
            var history = s.History()[0];
            Assert.True((string)history["result"] == "applied", (string)history["message"]);
            Assert.Equal("undo", history["kind"]);
            Assert.Equal(30L, s.Store.ReadDocument(alice).Root["level"]);
        }

        [Fact]
        public void TheApplyRunsWithoutTheAdminFramework()
        {
            using var phones = new TwoPhones();
            var B = phones.B;
            phones.StageSwitch();
            B.Server.Dispose();
            var options = new ServerOptions
            {
                DataDir = B.DataDir, Port = 0, ConsoleLog = false, FileLog = false, Clock = B.Clock,
                RegisterServices = (r, c) =>
                {
                    ServiceRegistry.RegisterAll(r, c);
                    SavesService.Register(r, c);   // no "admin" module: no routes, but the apply still happens
                },
            };
            using var server = McvsServer.Create(options);
            Assert.Equal(phones.X, server.Context.Accounts.Find("anonymous:c2newphone").Id);
            Assert.DoesNotContain(server.Router.Routes, r => r.Name.StartsWith("admin.saves", StringComparison.Ordinal));
            server.Dispose();
            B.Restart();   // the test kit's own server again (dispose cleanly)
        }

        [Fact]
        public void IdsAreOnlyThoseThisModuleMakes()
        {
            Assert.True(StagedOperation.IsId("20261026T101500123Z-import"));
            Assert.True(StagedOperation.IsId("20261026T101500123Z-undo-2"));
            Assert.False(StagedOperation.IsId("../../accounts"));
            Assert.False(StagedOperation.IsId("20261026T101500123Z-../x"));
            Assert.False(StagedOperation.IsId("20261026T101500123Z"));
            Assert.False(StagedOperation.IsId(null));
        }
    }
}
