using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Admin.Saves;
using McvsOffline.Server.Services.Storage;
using McvsOffline.Tests.Storage;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>The saves routes through McvsServer.Handle (design §4.6): every route, plan and error code.</summary>
    public class SavesApiTests
    {
        private static Dictionary<string, object> J(HttpResponse r) => AdminTestClient.Json(r);

        private static object At(HttpResponse r, string path) => AdminTestClient.At(r, path);

        private static void Status(int expected, HttpResponse r) => Assert.True(r.StatusCode == expected, r.StatusCode + " " + r.BodyText);

        private static void Error(int status, string code, HttpResponse r)
        {
            Status(status, r);
            Assert.Equal(code, J(r)["error"]);
        }

        /// <summary>A server whose game plays "alice" (level 37), with "bob" (level 5) on the same phone.</summary>
        private static (SavesServer S, string Alice, string Bob) Phone()
        {
            var s = new SavesServer();
            string alice = s.Player("alice").Id;
            string bob = s.Player("bob").Id;
            s.SaveGame(alice, 37, SavesServer.BigDump());
            s.SaveGame(bob, 5);
            s.Play("alice");
            s.Session();
            return (s, alice, bob);
        }

        [Fact]
        public void EveryRouteNeedsASession()
        {
            using var s = new SavesServer();
            string a = s.Player("alice").Id;
            var routes = new (string Method, string Path)[]
            {
                ("GET", "saves"), ("GET", "saves/import/files"), ("POST", "saves/import/paste"), ("POST", "saves/import/validate"),
                ("POST", "saves/import/stage"), ("GET", "saves/staged"), ("DELETE", "saves/staged"), ("POST", "saves/staged/undo"),
                ("GET", "saves/" + a + "/backups"), ("POST", "saves/" + a + "/backups"), ("DELETE", "saves/" + a + "/backups/x"),
                ("POST", "saves/" + a + "/restore"), ("POST", "saves/" + a + "/relink"), ("POST", "saves/" + a + "/export"),
                ("GET", "saves/" + a + "/export"), ("GET", "logs"), ("GET", "logs/server.log"), ("POST", "logs/zip"), ("POST", "logs/share"),
                ("GET", "logs/bundle"), ("POST", "logs/share-text"),
            };
            foreach (var (method, path) in routes)
            {
                HttpResponse r = s.Client.Send(method, path, method == "GET" ? null : "{}", method == "GET" ? null : "application/json");
                Assert.True(r.StatusCode == 401, method + " " + path + " answered " + r.StatusCode);
            }
            foreach (string name in new[] { "admin.saves.list", "admin.saves.backups", "admin.saves.backup", "admin.saves.backupDelete",
                                            "admin.saves.restore", "admin.saves.relink", "admin.saves.export", "admin.saves.download",
                                            "admin.saves.importFiles", "admin.saves.paste", "admin.saves.validate", "admin.saves.stage",
                                            "admin.saves.staged", "admin.saves.unstage", "admin.saves.undo", "admin.logs.list",
                                            "admin.logs.tail", "admin.logs.zip", "admin.logs.share", "admin.logs.download", "admin.logs.shareText" })
                Assert.Contains(s.Server.Router.Routes, r => r.Name == name);
        }

        [Fact]
        public void ListShowsTheCurrentPlayerFirstAndMasksEveryCredential()
        {
            var (s, alice, bob) = Phone();
            using (s)
            {
                s.Player("carol");   // an account without a save is not listed
                HttpResponse r = s.Client.Get("saves");
                Status(200, r);
                Assert.Equal(alice, At(r, "current.account"));
                Assert.Equal("game", At(r, "current.source"));
                Assert.Equal("anonymous:alic…", At(r, "current.credential"));
                var accounts = (List<object>)J(r)["accounts"];
                Assert.Equal(2, accounts.Count);
                var first = (Dictionary<string, object>)accounts[0];
                Assert.Equal(alice, first["account"]);
                Assert.Equal(true, first["isCurrent"]);
                Assert.Equal(true, first["hasGameProfile"]);
                Assert.Equal(37L, first["level"]);
                Assert.Equal(1L, first["revision"]);
                Assert.Equal(bob, ((Dictionary<string, object>)accounts[1])["account"]);
                Assert.DoesNotContain("anonymous:alice", r.BodyText);
                Assert.DoesNotContain("anonymous:bob\"", r.BodyText);
                Assert.Null(J(r)["staged"]);
                Assert.Null(J(r)["probation"]);
                Assert.Null(J(r)["last"]);
            }
        }

        [Fact]
        public void BackupsAreListedCreatedAndOnlyManualOnesDeleted()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                HttpResponse made = s.Client.Post("saves/current/backups", new Dictionary<string, object> { ["note"] = "before tuning" });
                Status(200, made);
                Assert.Equal("manual", At(made, "backup.pool"));
                Assert.Equal(true, At(made, "backup.deletable"));
                Assert.Equal(37L, At(made, "backup.level"));
                Assert.NotNull(At(made, "asOf"));
                string id = (string)At(made, "backup.id");
                Assert.Matches("^\\d{8}T\\d{9}Z-r1-manual$", id);

                s.Clock.Advance(TimeSpan.FromSeconds(1));
                BackupInfo system = s.Store.CreateBackup(alice, "presettings");
                HttpResponse list = s.Client.Get("saves/" + alice + "/backups");
                Status(200, list);
                var backups = ((List<object>)J(list)["backups"]).Cast<Dictionary<string, object>>().ToList();
                Assert.Equal(new[] { system.Id, id }, backups.Select(b => b["id"]));
                Assert.Equal(new object[] { "system", "manual" }, backups.Select(b => b["pool"]));
                Assert.Equal(10L, At(list, "limits.manual"));
                Assert.Equal(20L, At(list, "limits.auto"));

                Error(400, "not_deletable", s.Client.Delete("saves/" + alice + "/backups/" + system.Id));
                Error(404, "not_found", s.Client.Delete("saves/" + alice + "/backups/20261026T100000000Z-r1-nothere"));
                Error(404, "not_found", s.Client.Delete("saves/" + alice + "/backups/..%2F..%2Faccounts"));
                Status(200, s.Client.Delete("saves/" + alice + "/backups/" + id));
                Error(404, "not_found", s.Client.Delete("saves/" + alice + "/backups/" + id));

                Error(400, "invalid", s.Client.Post("saves/current/backups", new Dictionary<string, object> { ["note"] = "<script>" }));
                Error(400, "invalid", s.Client.Post("saves/current/backups", new Dictionary<string, object> { ["note"] = 5L }));
            }
        }

        [Theory]
        [InlineData("0123456789ABCDEF01234567")]   // upper case: one file, two accounts on Android's shared storage
        [InlineData("0123456789abcdef0123456")]
        [InlineData("fedcba9876543210fedcba98")]   // well formed, not an account here
        [InlineData("..")]
        [InlineData("%2e%2e%2faccounts")]
        [InlineData("currentx")]
        public void AccountIdentifiersAreStrict(string account)
        {
            var (s, _, _) = Phone();
            using (s)
            {
                Error(404, "not_found", s.Client.Get("saves/" + account + "/backups"));
                Error(404, "not_found", s.Client.Post("saves/" + account + "/backups", "{}"));
                Error(404, "not_found", s.Client.Get("saves/" + account + "/export"));
            }
        }

        [Fact]
        public void AnEleventhManualBackupIsManualFullAndAnAccountWithoutSaveIsNotFound()
        {
            var (s, _, _) = Phone();
            using (s)
            {
                for (int i = 0; i < 10; i++)
                {
                    Status(200, s.Client.Post("saves/current/backups", "{}"));
                    s.Clock.Advance(TimeSpan.FromSeconds(1));
                }
                Error(409, "manual_full", s.Client.Post("saves/current/backups", "{}"));
                string carol = s.Player("carol").Id;
                Error(404, "not_found", s.Client.Post("saves/" + carol + "/backups", "{}"));
            }
        }

        [Fact]
        public void ReimportingTheOwnExportStagesAReplaceAndCanBeCancelled()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                List<string> parts = s.ExportParts();
                Assert.True(parts.Count >= 2, "need several parts, got " + parts.Count);

                HttpResponse first = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = "Alice: here\n" + parts[0] + "\nbye" });
                Status(200, first);
                Assert.Equal(false, J(first)["complete"]);
                Assert.Equal(new object[] { 1L }, (List<object>)J(first)["have"]);
                Assert.Contains("part 1 of " + parts.Count, (string)J(first)["message"]);

                HttpResponse rest = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = string.Join("\r\n", parts.Skip(1).Reverse()) });
                Status(200, rest);
                Assert.Equal(true, J(rest)["complete"]);
                Assert.Equal("replace", J(rest)["plan"]);
                Assert.Equal(true, J(rest)["canStage"]);
                Assert.Empty((List<object>)J(rest)["errors"]);
                Assert.Empty((List<object>)J(rest)["warnings"]);
                Assert.Equal(alice, At(rest, "bundle.account"));
                Assert.Equal(37L, At(rest, "bundle.level"));
                Assert.Equal(true, At(rest, "bundle.hasGameProfile"));
                Assert.Equal(true, At(rest, "local.isCurrent"));
                Assert.Equal(alice, At(rest, "current.account"));
                string upload = (string)J(rest)["uploadId"];
                Assert.Matches("^u-[0-9a-f]{16}$", upload);

                Error(400, "invalid", s.Client.Post("saves/import/stage", new Dictionary<string, object> { ["uploadId"] = upload, ["confirm"] = "switch" }));
                HttpResponse staged = s.Client.Post("saves/import/stage", new Dictionary<string, object> { ["uploadId"] = upload, ["confirm"] = "replace" });
                Status(200, staged);
                Assert.Equal("import", At(staged, "staged.kind"));
                Assert.Equal("replace", At(staged, "staged.plan"));
                Assert.Null(At(staged, "staged.relink"));
                Assert.NotNull(At(staged, "staged.stageBackup.id"));
                // The upload is used up; a second stage of it is not found.
                Error(404, "not_found", s.Client.Post("saves/import/stage", new Dictionary<string, object> { ["uploadId"] = upload, ["confirm"] = "replace" }));

                Assert.Equal("import", At(s.Client.Get("saves"), "staged.kind"));
                Assert.Equal("import", At(s.Client.Get("status"), "staged.pending.kind"));
                Assert.Equal("import", At(s.Client.Get("saves/staged"), "pending.kind"));
                Assert.True(s.PendingExists);
                Assert.Contains(s.Store.Backups.List(alice), b => b.Reason == "preimport");

                HttpResponse cancel = s.Client.Delete("saves/staged");
                Status(200, cancel);
                Assert.Equal(true, J(cancel)["removed"]);
                Assert.False(s.PendingExists);
                Assert.Empty(Directory.GetFiles(s.StagedDir, "*.payload.json"));
                Assert.Null(J(s.Client.Get("saves"))["staged"]);
                Assert.Null(At(s.Client.Get("status"), "staged.pending"));
            }
        }

        [Fact]
        public void ASecondOperationNeedsReplace()
        {
            var (s, alice, bob) = Phone();
            using (s)
            {
                string backup = s.Store.CreateBackup(bob, "manual").Id;
                var restore = new Dictionary<string, object> { ["backup"] = backup, ["plan"] = "restore-other", ["confirm"] = "restore-other" };
                Status(200, s.Client.Post("saves/" + bob + "/restore", restore));
                HttpResponse exists = s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" });
                Error(409, "staged_exists", exists);
                Assert.Equal("restore", At(exists, "details.pending.kind"));
                HttpResponse replaced = s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch", ["replace"] = true });
                Status(200, replaced);
                Assert.Equal("relink", At(replaced, "staged.kind"));
                Assert.Equal("anonymous:alic…", At(replaced, "staged.relink.credential"));
                Assert.Equal(alice, At(replaced, "staged.relink.from"));
                Assert.Equal(bob, At(replaced, "staged.relink.to"));
                Assert.DoesNotContain("anonymous:alice", s.Client.Get("saves/staged").BodyText);
                // The pending manifest on disk keeps the full credential (needed at the next start).
                Assert.Equal("anonymous:alice", JsonPath.Get(s.Pending(), "relink.credential"));
            }
        }

        [Fact]
        public void ANewerLocalSaveNeedsAllowOlder()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                List<string> parts = s.ExportParts(partChars: 60000);
                s.SaveGame(alice, 38);   // the game played on: revision 2 > 1
                HttpResponse r = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = string.Concat(parts) });
                Status(200, r);
                Assert.Equal(true, J(r)["canStage"]);
                Assert.Contains((List<object>)J(r)["warnings"], w => ((string)w).Contains("newer save") && ((string)w).Contains("2 > 1"));
                var stage = new Dictionary<string, object> { ["uploadId"] = J(r)["uploadId"], ["confirm"] = "replace" };
                Error(409, "newer_save", s.Client.Post("saves/import/stage", stage));
                stage["allowOlder"] = true;
                Status(200, s.Client.Post("saves/import/stage", stage));
            }
        }

        [Fact]
        public void ASwitchNeedsTheGamesCredential()
        {
            using var other = new SavesServer();
            string x = other.Player("xavier").Id;
            other.SaveGame(x, 41);
            other.Play("xavier");
            other.Session();
            List<string> parts = other.ExportParts(partChars: 60000);

            // This phone knows no credential: the only save is its player (source "single").
            using var s = new SavesServer(withHost: false);
            string y = s.Player("yvonne").Id;
            s.SaveGame(y, 3);
            s.Session();
            Assert.Equal("single", At(s.Client.Get("saves"), "current.source"));
            HttpResponse r = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = string.Concat(parts) });
            Status(200, r);
            Assert.Equal("switch", J(r)["plan"]);
            Assert.Equal(false, J(r)["canStage"]);
            Assert.Null(J(r)["uploadId"]);
            Assert.Contains("current_player_unknown", (List<object>)J(r)["errorCodes"]);
            Assert.Contains(SavesApi.CurrentUnknown, (List<object>)J(r)["errors"]);

            // A backup of another account cannot be switched to either.
            string z = s.Player("zoe").Id;
            s.SaveGame(z, 9);
            string backup = s.Store.CreateBackup(z, "manual").Id;
            Error(409, "current_player_unknown", s.Client.Post("saves/" + z + "/restore",
                new Dictionary<string, object> { ["backup"] = backup, ["plan"] = "switch", ["confirm"] = "switch" }));
            Error(409, "current_player_unknown", s.Client.Post("saves/" + z + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }));
        }

        [Fact]
        public void RestoreChecksThePlanTheConfirmationAndTheBackup()
        {
            var (s, alice, bob) = Phone();
            using (s)
            {
                string mine = s.Store.CreateBackup(alice, "manual").Id;
                s.Clock.Advance(TimeSpan.FromSeconds(1));   // another id
                string bobs = s.Store.CreateBackup(bob, "manual").Id;
                Error(400, "invalid", s.Client.Post("saves/" + alice + "/restore", new Dictionary<string, object> { ["backup"] = mine, ["plan"] = "switch", ["confirm"] = "switch" }));
                Error(400, "invalid", s.Client.Post("saves/" + alice + "/restore", new Dictionary<string, object> { ["backup"] = mine, ["plan"] = "replace", ["confirm"] = "restore-other" }));
                Error(400, "invalid", s.Client.Post("saves/" + bob + "/restore", new Dictionary<string, object> { ["backup"] = bobs, ["plan"] = "replace", ["confirm"] = "replace" }));
                Error(404, "not_found", s.Client.Post("saves/" + bob + "/restore", new Dictionary<string, object> { ["backup"] = mine, ["plan"] = "restore-other", ["confirm"] = "restore-other" }));
                Error(404, "not_found", s.Client.Post("saves/" + bob + "/restore", new Dictionary<string, object> { ["backup"] = "../x", ["plan"] = "restore-other", ["confirm"] = "restore-other" }));

                HttpResponse ok = s.Client.Post("saves/" + alice + "/restore", new Dictionary<string, object> { ["backup"] = mine, ["plan"] = "replace", ["confirm"] = "replace" });
                Status(200, ok);
                Assert.Equal(mine, At(ok, "staged.restoreBackupId"));
                Assert.Equal(37L, At(ok, "staged.summary.level"));
                Status(200, s.Client.Post("saves/" + bob + "/restore", new Dictionary<string, object> { ["backup"] = bobs, ["plan"] = "switch", ["confirm"] = "switch", ["replace"] = true }));
                Assert.Equal(bob, JsonPath.Get(s.Pending(), "relink.to"));
            }
        }

        [Fact]
        public void ABackupWithoutGameProgressCannotBeRestored()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                s.Store.Commit(alice, "wipe", "", "http", doc =>
                {
                    doc.Root.Remove("_mcvs");
                    return new WriteOutcome { Changed = true };
                });
                string empty = s.Store.CreateBackup(alice, "manual").Id;   // a copy of the wiped state
                HttpResponse r = s.Client.Post("saves/" + alice + "/restore", new Dictionary<string, object> { ["backup"] = empty, ["plan"] = "replace", ["confirm"] = "replace" });
                Error(400, "invalid", r);
                Assert.Contains(SaveBundle.ErrorNoProgress, r.BodyText);
            }
        }

        [Fact]
        public void RelinkNeedsAnotherAccountWithGameProgress()
        {
            var (s, alice, bob) = Phone();
            using (s)
            {
                Error(400, "invalid", s.Client.Post("saves/" + alice + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }));
                Error(400, "invalid", s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "yes" }));
                string carol = s.Player("carol").Id;
                Error(409, "no_game_profile", s.Client.Post("saves/" + carol + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }));
                HttpResponse ok = s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" });
                Status(200, ok);
                Assert.Equal(5L, At(ok, "staged.summary.level"));
                Assert.Null(At(ok, "staged.stageBackup"));   // a relink writes no profile
            }
        }

        [Fact]
        public void UndoBeforeAnyAppliedOperationIsNothingToUndo()
        {
            var (s, _, _) = Phone();
            using (s)
            {
                Error(409, "nothing_to_undo", s.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" }));
                Error(400, "invalid", s.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "yes" }));
            }
        }

        [Fact]
        public void PasteLimitsAndMistakes()
        {
            var (s, _, _) = Phone();
            using (s)
            {
                HttpResponse tooLong = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = new string('A', SavesApi.MaxPasteChars + 1) });
                Error(413, "too_large", tooLong);
                Error(400, "invalid", s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = "just a chat message" }));
                Error(400, "invalid", s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = 12L }));
                Error(400, "bad_request", s.Client.Post("saves/import/paste", "{}"));

                TransferExport bomb = SaveTransfer.Encode(new byte[SaveTransfer.MaxInflatedBytes + 1], 60000, "0b0b0b0b", true);
                Error(413, "too_large", s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = string.Concat(bomb.Parts) }));

                HttpResponse damaged = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = "{\"format\": \"mcvs-offline/save-bundle\", \"version\": 1}" });
                Status(200, damaged);
                Assert.Equal(false, J(damaged)["canStage"]);
                Assert.Contains(SaveBundle.ErrorAccount, (List<object>)J(damaged)["errors"]);
            }
        }

        [Fact]
        public void PastesAreRateLimited()
        {
            var (s, _, _) = Phone();
            using (s)
            {
                for (int i = 0; i < SavesApi.PastesPerMinute; i++) Status(400, s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = "x" }));
                HttpResponse limited = s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = "x" });
                Error(429, "rate_limited", limited);
                Assert.NotNull(At(limited, "details.retryAfterSeconds"));
            }
        }

        [Fact]
        public void ExportToTheShareSheetWritesTheFileFirst()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                s.Context.Accounts.Update(alice, a => a.Data["alias"] = "123456789");
                HttpResponse r = s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "share" });
                Status(200, r);
                string file = (string)J(r)["file"];
                Assert.Matches("^mcvs-save-123456789-\\d{8}-\\d{4}\\.txt$", file);
                Assert.Equal(true, At(r, "share.ok"));
                Assert.Equal("sheet", At(r, "share.method"));
                Assert.Null(At(r, "transfer.texts"));
                string[] call = Assert.Single(s.Host.SharedFiles);
                Assert.Equal(new[] { "text/plain", file, "MCVS Offline save" }, call.Skip(1).Take(3));
                Assert.Contains("123456789", call[4]);
                Assert.True(File.Exists(call[0]));
                Assert.Equal(Path.Combine(s.DataDir, "admin", "exports", file), call[0]);
                string text = File.ReadAllText(call[0]);
                Assert.StartsWith("MCVS Offline save of 123456789 (level 37, revision 1)", text);
                Assert.Equal((long)Encoding.UTF8.GetByteCount(text), J(r)["size"]);
                var parts = SaveTransfer.Extract(text, null);
                SaveBundle bundle = SaveBundle.Parse(Encoding.UTF8.GetString(SaveTransfer.Decode(parts)), SavesServer.T0);
                Assert.True(bundle.IsValid);
                Assert.Equal(s.ProfileFile(alice), bundle.ProfileText);

                HttpResponse json = s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "downloads", ["format"] = "json" });
                Status(200, json);
                Assert.EndsWith(".json", (string)J(json)["file"]);
                string[] second = s.Host.SharedFiles[1];
                Assert.Equal("application/json", second[1]);
                Assert.Null(second[3]);   // downloads: copy only, no share sheet

                s.Host.Info.CanSaveToDownloads = false;
                Error(501, "not_supported", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "downloads" }));
                Error(400, "invalid", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["partChars"] = 5000L }));
                Error(400, "invalid", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "text", ["format"] = "json" }));
                Error(400, "invalid", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "mail" }));
            }
        }

        [Fact]
        public void ExportsAreRateLimitedAndTheNewestFiveKept()
        {
            var (s, _, _) = Phone();
            using (s)
            {
                for (int i = 0; i < 6; i++) Status(200, s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "share" }));
                Error(429, "rate_limited", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "share" }));
                s.Clock.Advance(TimeSpan.FromMinutes(2));
                Status(200, s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "share" }));
                // Six of the exports fell in the same minute (one name); the seventh has its own.
                Assert.InRange(Directory.GetFiles(Path.Combine(s.DataDir, "admin", "exports")).Length, 1, SavesApi.ExportsKept);
            }
        }

        [Fact]
        public void TheNewExportIsNeverPrunedBeforeItIsShared()
        {
            var (s, alice, bob) = Phone();
            using (s)
            {
                // Export names start with the alias: bob's sorts before alice's five.
                s.Context.Accounts.Update(alice, a => a.Data["alias"] = "900000000");
                s.Context.Accounts.Update(bob, a => a.Data["alias"] = "100000000");
                string folder = Path.Combine(s.DataDir, "admin", "exports");
                for (int i = 0; i < SavesApi.ExportsKept; i++)
                {
                    Status(200, s.Client.Post("saves/" + alice + "/export", new Dictionary<string, object> { ["target"] = "share" }));
                    File.SetLastWriteTimeUtc(s.Host.SharedFiles.Last()[0], DateTime.UtcNow.AddMinutes(i - 10));   // written in this order
                    s.Clock.Advance(TimeSpan.FromMinutes(1));
                }
                string oldest = Path.GetFileName(s.Host.SharedFiles[0][0]);
                s.Clock.Advance(TimeSpan.FromMinutes(1));
                HttpResponse r = s.Client.Post("saves/" + bob + "/export", new Dictionary<string, object> { ["target"] = "share" });
                Status(200, r);
                string[] shared = s.Host.SharedFiles.Last();
                Assert.Equal(Path.Combine(folder, (string)J(r)["file"]), shared[0]);
                Assert.StartsWith("mcvs-save-100000000-", (string)J(r)["file"]);
                Assert.True(File.Exists(shared[0]));
                string[] kept = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
                Assert.Equal(SavesApi.ExportsKept, kept.Length);
                Assert.DoesNotContain(oldest, kept);   // the oldest by time went, not the newest by name
            }
        }

        [Fact]
        public void AnExportThatNeedsMoreThan999PartsIsTooLarge()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                // ~2 MB of random printable characters: even deflated, more than 999 Discord-sized parts.
                var rng = new Random(5);
                var blob = new StringBuilder(2000000);
                while (blob.Length < 2000000)
                {
                    char c = (char)rng.Next(0x21, 0x7f);
                    if (c != '"' && c != '\\') blob.Append(c);
                }
                s.SaveGame(alice, 37, "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"Blob\":\""
                                      + blob + "\"}}}");
                HttpResponse r = s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "text", ["partChars"] = 1900L });
                Error(413, "too_large", r);
                Assert.Equal((long)SaveTransfer.MaxParts, At(r, "details.limit"));
                Assert.Equal("parts", At(r, "details.unit"));
                Assert.Contains("Choose larger parts", (string)J(r)["message"]);
                Error(413, "too_large", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "share", ["partChars"] = 1900L }));
                // Larger parts carry it.
                HttpResponse ok = s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "text", ["partChars"] = 3900L });
                Status(200, ok);
                Assert.InRange((long)At(ok, "transfer.parts"), 2L, (long)SaveTransfer.MaxParts);
            }
        }

        [Fact]
        public void WhileTheGameSavesOrRestartsNoProfileIsRead()
        {
            var fast = new ExitTimings { ResponseDelayMs = 10, GameLoopTimeoutMs = 1000, SaveTimeoutMs = 300, SaveQuietMs = 50, FlushTimeoutMs = 100 };
            using var s = new SavesServer(exitTimings: fast);
            string alice = s.Player("alice").Id;
            string bob = s.Player("bob").Id;
            s.SaveGame(alice, 37);
            s.SaveGame(bob, 5);
            s.Play("alice");
            s.Session();
            string backup = s.Store.CreateBackup(alice, "manual").Id;
            var api = s.Context.Features.Get<AdminApi>();

            // "Save the game now": the sequence waits for the game's save.
            var gate = new System.Threading.ManualResetEventSlim(false);
            s.Host.OnRequestGameSave = () => gate.Wait(5000);
            try
            {
                Status(202, s.Client.Post("actions/save-now", new Dictionary<string, object> { ["screen"] = "saves" }));
                Assert.Equal("saving", api.ExitState);
                Error(409, "busy", s.Client.Get("saves/" + alice + "/export?format=json"));
                Error(409, "busy", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "text" }));
                Error(409, "busy", s.Client.Post("saves/current/backups", new Dictionary<string, object>()));
                Error(409, "busy", s.Client.Post("saves/current/restore", new Dictionary<string, object> { ["backup"] = backup, ["plan"] = "replace", ["confirm"] = "replace" }));
                Error(409, "busy", s.Client.Post("saves/" + bob + "/relink", new Dictionary<string, object> { ["confirm"] = "switch" }));
                Error(409, "busy", s.Client.Post("saves/import/paste", new Dictionary<string, object> { ["text"] = "MCVS-SAVE:" }));
                Error(409, "busy", s.Client.Post("saves/import/stage", new Dictionary<string, object> { ["uploadId"] = "u-0123456789abcdef", ["confirm"] = "replace" }));
                Error(409, "busy", s.Client.Post("saves/staged/undo", new Dictionary<string, object> { ["confirm"] = "undo" }));
                Status(200, s.Client.Get("saves"));                        // listed without reading a profile
                Status(200, s.Client.Get("saves/" + alice + "/backups"));
            }
            finally
            {
                gate.Set();
            }
            for (int i = 0; i < 500 && api.ExitState != "idle"; i++) System.Threading.Thread.Sleep(10);
            Assert.Equal("idle", api.ExitState);
            Status(200, s.Client.Get("saves/" + alice + "/export?format=json"));

            // A restart ends with the account's lock held until the process ends: a download answers at once.
            Status(202, s.Client.Post("actions/restart", new Dictionary<string, object> { ["confirm"] = true, ["restartWithoutSave"] = true }));
            for (int i = 0; i < 500 && !s.Host.Calls.Any(c => c.StartsWith("Exit:", StringComparison.Ordinal)); i++) System.Threading.Thread.Sleep(10);
            Assert.Equal(1, s.Store.Locks.QueueLength(alice));
            Error(409, "restarting", s.Client.Get("saves/" + alice + "/export?format=transfer"));
            Error(409, "restarting", s.Client.Post("saves/current/export", new Dictionary<string, object> { ["target"] = "text" }));
        }

        [Fact]
        public void WithoutAHostExportsAreDownloadsOnly()
        {
            using var s = new SavesServer(withHost: false);
            string a = s.Player("alice").Id;
            s.SaveGame(a, 12);
            s.Session();
            Error(501, "not_supported", s.Client.Post("saves/" + a + "/export", new Dictionary<string, object> { ["target"] = "share" }));
            Status(200, s.Client.Post("saves/" + a + "/export", new Dictionary<string, object> { ["target"] = "text" }));

            HttpResponse download = s.Client.Get("saves/" + a + "/export?format=transfer");
            Status(200, download);
            Assert.StartsWith("text/plain", download.ContentType);
            Assert.Matches("^attachment; filename=\"mcvs-save-" + a + "-\\d{8}-\\d{4}\\.txt\"$", download.Headers.Get("Content-Disposition"));
            Assert.Equal("no-store", download.Headers.Get("Cache-Control"));
            Assert.Contains("MCVS-SAVE:1:", download.BodyText);

            HttpResponse json = s.Client.Get("saves/" + a + "/export?format=json");
            Status(200, json);
            Assert.StartsWith("application/json", json.ContentType);
            Assert.Equal(SaveBundle.Format, J(json)["format"]);
            Error(400, "invalid", s.Client.Get("saves/" + a + "/export?format=zip"));
        }

        [Fact]
        public void ImportFilesListsOnlyPlainSaveFilesAndValidatesThem()
        {
            using var other = new SavesServer();
            string x = other.Player("xavier").Id;
            other.SaveGame(x, 41);
            other.Play("xavier");
            other.Session();
            string transfer = other.Client.Get("saves/current/export?format=transfer").BodyText;

            var (s, _, _) = Phone();
            using (s)
            {
                HttpResponse empty = s.Client.Get("saves/import/files");
                Status(200, empty);
                string folder = (string)J(empty)["directory"];
                Assert.Equal(Path.Combine(s.DataDir, "import"), folder);
                Assert.Equal(SavesApi.WritableBy, J(empty)["writableBy"]);

                File.WriteAllText(Path.Combine(folder, "old phone.txt"), transfer);
                File.WriteAllText(Path.Combine(folder, "raw.json"), other.ProfileFile(x));
                File.WriteAllText(Path.Combine(folder, "notes.md"), "x");
                File.WriteAllText(Path.Combine(folder, "bad#name.txt"), transfer);
                Directory.CreateDirectory(Path.Combine(folder, "folder.txt"));
                string outside = s.Dir.Combine("outside.txt");
                File.WriteAllText(outside, transfer);
                File.CreateSymbolicLink(Path.Combine(folder, "link.txt"), outside);
                using (var big = new FileStream(Path.Combine(folder, "big.txt"), FileMode.Create)) big.SetLength(SavesApi.MaxImportFileBytes + 1);

                HttpResponse list = s.Client.Get("saves/import/files");
                var names = ((List<object>)J(list)["files"]).Cast<Dictionary<string, object>>().Select(f => (string)f["name"]).OrderBy(n => n).ToList();
                Assert.Equal(new[] { "old phone.txt", "raw.json" }, names);

                HttpResponse checkedFile = s.Client.Post("saves/import/validate", new Dictionary<string, object> { ["file"] = "old phone.txt" });
                Status(200, checkedFile);
                Assert.Equal(true, J(checkedFile)["complete"]);
                Assert.Equal("switch", J(checkedFile)["plan"]);
                Assert.Equal(true, J(checkedFile)["canStage"]);
                Assert.Equal(x, At(checkedFile, "bundle.account"));
                Assert.Equal(41L, At(checkedFile, "bundle.level"));
                Assert.Equal(false, At(checkedFile, "local.exists"));

                HttpResponse raw = s.Client.Post("saves/import/validate", new Dictionary<string, object> { ["file"] = "raw.json" });
                Status(200, raw);
                Assert.Equal(true, J(raw)["canStage"]);
                Assert.Equal(0L, At(raw, "bundle.messages"));

                foreach (string bad in new[] { "link.txt", "big.txt", "bad#name.txt", "folder.txt", "../accounts.json", "../import/raw.json", "RAW.JSON", "" })
                    Error(404, "not_found", s.Client.Post("saves/import/validate", new Dictionary<string, object> { ["file"] = bad }));

                HttpResponse staged = s.Client.Post("saves/import/stage", new Dictionary<string, object> { ["uploadId"] = J(checkedFile)["uploadId"], ["confirm"] = "switch" });
                Status(200, staged);
                Assert.Equal("file", At(staged, "staged.source.type"));
                Assert.Equal("old phone.txt", At(staged, "staged.source.name"));
            }
        }

        [Fact]
        public void APartialImportFileReportsTheMissingParts()
        {
            var (s, _, _) = Phone();
            using (s)
            {
                List<string> parts = s.ExportParts();
                string folder = Path.Combine(s.DataDir, "import");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "half.txt"), parts[0]);
                HttpResponse r = s.Client.Post("saves/import/validate", new Dictionary<string, object> { ["file"] = "half.txt" });
                Status(200, r);
                Assert.Equal(false, J(r)["complete"]);
                Assert.Equal(false, J(r)["canStage"]);
                Assert.Contains("part 1 of", (string)J(r)["message"]);
            }
        }

        [Fact]
        public void RiskySettingsAreBackedUpThroughTheHook()
        {
            var (s, alice, _) = Phone();
            using (s)
            {
                Assert.NotNull(s.Context.Features.Get<AdminApi>().CreateBackup);
                HttpResponse r = s.Client.Post("settings", new Dictionary<string, object> { ["changes"] = new Dictionary<string, object> { ["devMenu"] = true } });
                Status(200, r);
                string id = (string)At(r, "backup.id");
                Assert.EndsWith("-presettings", id);
                Assert.Equal("system", s.Store.Backups.List(alice).Single(b => b.Id == id).Pool);
            }
        }
    }
}
