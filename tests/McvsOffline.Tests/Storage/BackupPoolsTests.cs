using System;
using System.IO;
using System.Linq;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>The four backup pools (design §6.3): auto, event, system, manual — capped and pruned separately.</summary>
    public class BackupPoolsTests
    {
        private const string Me = "profiles/me/myprofile";
        private const string Account = "0123456789abcdef01234567";
        private static readonly DateTime T0 = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("auto", "auto")]
        [InlineData("manual", "manual")]
        [InlineData("preimport", "system")]
        [InlineData("prerestore", "system")]
        [InlineData("presettings", "system")]
        [InlineData("prerevert", "system")]
        [InlineData("wipe", "event")]
        [InlineData("delete", "event")]
        [InlineData("replace", "event")]
        [InlineData("unlockall", "event")]
        [InlineData("backup", "event")]
        [InlineData("somethingnew", "event")]
        public void PoolOfToken(string token, string pool)
        {
            Assert.Equal(pool, ProfileBackups.PoolOf(token));
        }

        [Fact]
        public void IdsUseTheSafeTokenAndTheirPoolIsDetected()
        {
            using var dir = new TempDir();
            var backups = new ProfileBackups(dir.Path);
            BackupInfo info = backups.Create(Account, "{}", 12, "pre-import", T0, 20);
            Assert.Equal("20261026T100000000Z-r12-preimport", info.Id);
            Assert.Equal("preimport", info.Reason);
            Assert.Equal("system", info.Pool);
            Assert.False(info.Deletable);

            // Same millisecond: a numbered id, still in the same pool.
            BackupInfo again = backups.Create(Account, "{}", 12, "preimport", T0, 20);
            Assert.Equal("20261026T100000000Z-r12-preimport-2", again.Id);
            Assert.Equal("system", again.Pool);

            BackupInfo mine = backups.Create(Account, "{}", 12, "manual", T0, 20);
            Assert.Equal("manual", mine.Pool);
            Assert.True(mine.Deletable);
            Assert.Equal(new[] { "system", "system", "manual" }, backups.List(Account).Select(b => b.Pool)); // by id, newest first
        }

        [Fact]
        public void EveryPoolIsCappedSeparately()
        {
            using var dir = new TempDir();
            var backups = new ProfileBackups(dir.Path);
            DateTime t = T0;
            for (int i = 0; i < 25; i++) backups.Create(Account, "{}", i, "auto", t = t.AddMinutes(1), 20);
            for (int i = 0; i < 25; i++) backups.Create(Account, "{}", i, i % 2 == 0 ? "wipe" : "unlockall", t = t.AddMinutes(1), 20);
            for (int i = 0; i < 12; i++) backups.Create(Account, "{}", i, "preimport", t = t.AddMinutes(1), 20);
            for (int i = 0; i < 10; i++) backups.Create(Account, "{}", i, "manual", t = t.AddMinutes(1), 20);

            var all = backups.List(Account);
            Assert.Equal(20, all.Count(b => b.Pool == "auto"));
            Assert.Equal(20, all.Count(b => b.Pool == "event"));
            Assert.Equal(10, all.Count(b => b.Pool == "system"));
            Assert.Equal(10, all.Count(b => b.Pool == "manual"));
            // The newest of each pool survive.
            Assert.Equal(24, all.Where(b => b.Pool == "auto").Max(b => b.Revision));
            Assert.Equal(5, all.Where(b => b.Pool == "auto").Min(b => b.Revision));
            Assert.Equal(2, all.Where(b => b.Pool == "system").Min(b => b.Revision));
        }

        [Fact]
        public void AnEleventhManualBackupIsRefusedAndNothingIsWritten()
        {
            using var dir = new TempDir();
            var backups = new ProfileBackups(dir.Path);
            for (int i = 0; i < 10; i++) backups.Create(Account, "{}", i, "manual", T0.AddMinutes(i), 20);
            int files = Directory.GetFiles(backups.DirectoryFor(Account)).Length;

            var ex = Assert.Throws<ManualBackupLimitException>(() => backups.Create(Account, "{}", 99, "manual", T0.AddHours(1), 20));
            Assert.Equal(10, ex.Limit);
            Assert.Equal(files, Directory.GetFiles(backups.DirectoryFor(Account)).Length);
            Assert.DoesNotContain(backups.List(Account), b => b.Revision == 99);

            // Other pools are unaffected by a full manual pool, and never evict a manual backup.
            for (int i = 0; i < 30; i++) backups.Create(Account, "{}", 100 + i, "wipe", T0.AddHours(2).AddMinutes(i), 20);
            Assert.Equal(10, backups.List(Account).Count(b => b.Pool == "manual"));
        }

        [Fact]
        public void AutoWithKeepZeroIsNeverPrunedAndLoweringKeepPrunesAtTheNextAutomaticBackup()
        {
            using var dir = new TempDir();
            var backups = new ProfileBackups(dir.Path);
            for (int i = 0; i < 5; i++) backups.Create(Account, "{}", i, "auto", T0.AddMinutes(i), 5);
            backups.Create(Account, "{}", 9, "preimport", T0.AddMinutes(10), 0);   // keep 0: the auto pool is left alone
            Assert.Equal(5, backups.List(Account).Count(b => b.Pool == "auto"));

            backups.Create(Account, "{}", 10, "auto", T0.AddMinutes(20), 2);
            Assert.Equal(new long[] { 10, 4 }, backups.List(Account).Where(b => b.Pool == "auto").Select(b => b.Revision));
        }

        [Fact]
        public void IntervalCountsOnlyAutomaticBackups()
        {
            using var dir = new TempDir();
            var backups = new ProfileBackups(dir.Path);
            Assert.True(backups.IsDue(Account, T0, 30));
            backups.Create(Account, "{}", 1, "manual", T0, 20);
            Assert.True(backups.IsDue(Account, T0.AddMinutes(1), 30));
            backups.Create(Account, "{}", 2, "auto", T0.AddMinutes(1), 20);
            Assert.False(backups.IsDue(Account, T0.AddMinutes(30), 30));
            Assert.True(backups.IsDue(Account, T0.AddMinutes(31), 30));
            // A fresh instance reads the newest automatic backup from disk.
            var reopened = new ProfileBackups(dir.Path);
            Assert.False(reopened.IsDue(Account, T0.AddMinutes(30), 30));
        }

        // ------------------------------------------------------------------ LocalProfileStore

        [Fact]
        public void KeepZeroAndCreateBackupKeepsTheExistingAutomaticBackups()
        {
            using var h = new StorageHarness(configure: s =>
            {
                s.Set("storage.backups.keep", 3L);
                s.Set("storage.backups.intervalMinutes", 5L);
            });
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            for (int i = 0; i < 4; i++)
            {
                h.Post(Me + "/level", token, ("object", (i + 1).ToString()));
                h.Clock.Advance(TimeSpan.FromMinutes(10));
            }
            Assert.Equal(3, h.Store.Backups.List(account).Count(b => b.Pool == "auto"));

            h.Context.Settings.Set("storage.backups.keep", 0L);
            BackupInfo before = h.Store.CreateBackup(account, "presettings");
            Assert.Equal("system", before.Pool);
            h.Clock.Advance(TimeSpan.FromMinutes(10));
            h.Post(Me + "/level", token, ("object", "9"));          // keep 0: no new automatic backup
            Assert.Equal(3, h.Store.Backups.List(account).Count(b => b.Pool == "auto"));
            Assert.Equal(4, h.Store.Backups.List(account).Count);
        }

        [Fact]
        public void APruneSparesTheBackupBeingRestored()
        {
            using var dir = new TempDir();
            var backups = new ProfileBackups(dir.Path);
            DateTime t = T0;
            string oldest = backups.Create(Account, "{}", 0, "preimport", t, 20).Id;
            for (int i = 1; i < ProfileBackups.SystemCap; i++) backups.Create(Account, "{}", i, "presettings", t = t.AddMinutes(1), 20);
            backups.Create(Account, "{}", 99, "prerestore", t = t.AddMinutes(1), 20, spare: oldest);
            Assert.NotNull(backups.Find(Account, oldest));
            Assert.Equal(ProfileBackups.SystemCap + 1, backups.List(Account).Count(b => b.Pool == "system"));   // the spared one is extra

            backups.Create(Account, "{}", 100, "presettings", t.AddMinutes(1), 20);   // the next prune is the usual one
            Assert.Null(backups.Find(Account, oldest));
            Assert.Equal(ProfileBackups.SystemCap, backups.List(Account).Count(b => b.Pool == "system"));
        }

        [Fact]
        public void ACommitWithASystemReasonIsBackedUpEvenWithKeepZero()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.backups.keep", 0L));
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            h.Store.Commit(account, "admin.import", "", "admin", doc =>
            {
                doc.Root["level"] = 41L;
                return new WriteOutcome { Changed = true, BackupReason = "preimport" };
            });
            BackupInfo backup = Assert.Single(h.Store.Backups.List(account));
            Assert.Equal("preimport", backup.Reason);
            Assert.Equal("system", backup.Pool);
            Assert.Equal(1, h.Store.BackupLevel(backup));
        }

        [Fact]
        public void CreateBackupCopiesTheCurrentFileAndRefusesOtherReasons()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            Assert.Null(h.Store.CreateBackup(account, "manual"));   // nothing stored yet

            h.Post(Me + "/level", token, ("object", "37"));
            BackupInfo info = h.Store.CreateBackup(account, "manual");
            Assert.Equal("manual", info.Pool);
            Assert.True(info.Deletable);
            Assert.Equal(h.StoredFile(account), h.Store.ReadBackupText(account, info.Id));
            Assert.Equal(37, h.Store.BackupLevel(info));
            Assert.Equal(1, info.Revision);

            Assert.Throws<ArgumentException>(() => h.Store.CreateBackup(account, "auto"));
            Assert.Throws<ArgumentException>(() => h.Store.CreateBackup(account, "wipe"));
            Assert.Null(h.Store.ReadBackupText(account, "../../accounts"));
            Assert.Null(h.Store.ReadBackupText(account, "20261026T100000000Z-r1-nothere"));
        }

        [Fact]
        public void AnEleventhManualBackupThroughTheStoreIsRefused()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            for (int i = 0; i < 10; i++)
            {
                h.Store.CreateBackup(account, "manual");
                h.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.Throws<ManualBackupLimitException>(() => h.Store.CreateBackup(account, "manual"));
            Assert.Equal(10, h.Store.Backups.List(account).Count(b => b.Pool == "manual"));
        }

        [Fact]
        public void OnlyManualBackupsCanBeDeleted()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.backups.intervalMinutes", 0L));
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            h.Post(Me + "/level", token, ("object", "2"));           // auto
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            BackupInfo system = h.Store.CreateBackup(account, "preimport");
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            BackupInfo manual = h.Store.CreateBackup(account, "manual");
            BackupInfo auto = h.Store.Backups.List(account).Single(b => b.Pool == "auto");

            Assert.False(h.Store.DeleteBackup(account, auto.Id));
            Assert.False(h.Store.DeleteBackup(account, system.Id));
            Assert.False(h.Store.DeleteBackup(account, "nothere"));
            Assert.False(h.Store.DeleteBackup(account, "../profiles"));
            Assert.True(h.Store.DeleteBackup(account, manual.Id));
            Assert.False(h.Store.DeleteBackup(account, manual.Id));
            Assert.Equal(new[] { "system", "auto" }, h.Store.Backups.List(account).Select(b => b.Pool));
        }
    }
}
