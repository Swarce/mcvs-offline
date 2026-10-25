using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>LocalProfileStore: durability, crash safety, backups, recovery, ordering; plus the module seams.</summary>
    public class ProfileStoreTests
    {
        private const string Me = "profiles/me/myprofile";

        [Fact]
        public void WritesAreOnDiskBeforeTheAnswerAndSurviveRestarts()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            Assert.Null(h.StoredFile(account));
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));

            string file = h.StoredFile(account);
            Assert.NotNull(file);
            Assert.Contains("\"format\": \"mcvs-offline/seshat-profile\"", file);
            Assert.Contains(GameData.GameProfileDump, file);
            Assert.Contains("\"revision\": 1", file);
            Assert.Empty(Directory.GetFiles(h.Store.ProfilesDirectory, "*" + AtomicFile.TempSuffix));

            h.Restart();
            Assert.Equal(GameData.GameProfileDump, h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
        }

        [Fact]
        public void InterruptedWritesLeaveTheLastCompleteVersion()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            string complete = h.StoredFile(account);

            // A crash after writing part of the temp file, before the rename: the temp file is garbage and is
            // never read; the next start removes it.
            string temp = h.Store.PathFor(account) + ".1a2b3" + AtomicFile.TempSuffix;
            File.WriteAllText(temp, complete.Substring(0, complete.Length / 2));
            Assert.Equal("1", h.Get(Me + "/level", token).BodyText);
            h.Restart();
            Assert.False(File.Exists(temp));
            Assert.Equal("1", h.Get(Me + "/level", token).BodyText);
            Assert.Equal(complete, h.StoredFile(account));
        }

        [Fact]
        public void UnreadableFileIsRestoredFromTheNewestBackup()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.backups.intervalMinutes", 0L));
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            h.Post(Me + "/level", token, ("object", "2")); // backs up the level-1 file
            File.WriteAllText(h.Store.PathFor(account), "{\"format\":\"mcvs-offline/seshat-pro"); // torn by a disk fault

            var read = h.Get(Me + "/level", token);
            Assert.Equal(200, read.StatusCode);
            Assert.Equal("1", read.BodyText); // never "missing": that would reset the profile
            Assert.Single(Directory.GetFiles(h.Store.ProfilesDirectory, "*.corrupt-*"));
            Assert.True(h.Logs.Contains("restored from backup"));
        }

        [Fact]
        public void UnreadableFileWithoutBackupAnswers503AndIsLeftAlone()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            Directory.CreateDirectory(h.Store.ProfilesDirectory);
            File.WriteAllText(h.Store.PathFor(account), "{ not json");

            Assert.Equal(503, h.Get(Me + "/_mcvs.gameProfile", token).StatusCode);
            Assert.Equal(503, h.Get(Me, token, ("include_fields", "level")).StatusCode);
            Assert.Equal(503, h.Post(Me + "/level", token, ("object", "1")).StatusCode);
            Assert.Equal("{ not json", File.ReadAllText(h.Store.PathFor(account)));
            Assert.True(h.Logs.Contains("exists but cannot be read"));

            // Once fixed by hand, everything works again.
            File.WriteAllText(h.Store.PathFor(account), "{\"level\":4}");
            Assert.Equal("4", h.Get(Me + "/level", token).BodyText);
        }

        [Fact]
        public void HandEditsAreSeenDespiteTheCache()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            Assert.Equal("1", h.Get(Me + "/level", token).BodyText); // cached now

            string path = h.Store.PathFor(account);
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"level\": 1", "\"level\": 8"));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); // same length: only the time differs
            Assert.Equal("8", h.Get(Me + "/level", token).BodyText);

            File.Delete(path);
            Assert.Equal("{}", h.Get(Me, token).BodyText);
        }

        [Fact]
        public void CachedCopiesAreIndependent()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me, token, ("operation", "merge"), ("object", "{\"a\":{\"b\":1}}"));
            var first = h.Store.ReadDocument(account);
            ((Dictionary<string, object>)first.Root["a"])["b"] = 99L; // a caller scribbling on its copy
            first.Root["zzz"] = true;
            Assert.Equal("{\"b\":1}", h.Get(Me + "/a", token).BodyText);
            Assert.False(h.Store.ReadDocument(account).Root.ContainsKey("zzz"));
        }

        [Fact]
        public void WorksWithTheCacheDisabled()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.cache", false));
            string token = h.PlayerToken("p");
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));
            h.Post(Me, token, ("operation", "merge"), ("object", GameData.SectionsMerge));
            Assert.Equal(GameData.GameProfileDump, h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Equal(7L, StorageHarness.Json(h.Get(Me, token, ("include_fields", "level")))["level"]);
        }

        [Fact]
        public void BackupsRotateByIntervalAndAreGuardedBeforeDestructiveWrites()
        {
            using var h = new StorageHarness(configure: s =>
            {
                s.Set("storage.backups.intervalMinutes", 30L);
                s.Set("storage.backups.keep", 3L);
            });
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            List<BackupInfo> Backups() => h.Store.Backups.List(account);

            h.Post(Me + "/level", token, ("object", "1"));          // first write: nothing to back up
            Assert.Empty(Backups());
            h.Post(Me + "/level", token, ("object", "2"));          // due (no backup yet)
            Assert.Single(Backups());
            h.Clock.Advance(TimeSpan.FromMinutes(10));
            h.Post(Me + "/level", token, ("object", "3"));          // not due
            Assert.Single(Backups());
            h.Clock.Advance(TimeSpan.FromMinutes(25));
            h.Post(Me + "/level", token, ("object", "4"));          // due again
            Assert.Equal(2, Backups().Count);

            h.Post(Me + "/_mcvs.gameProfile", token, ("object", "{\"x\":1}"));
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            h.Post(Me + "/_mcvs.gameProfile", token, ("object", "{}"));   // clears the dump: guarded
            Assert.Equal("wipe", Backups()[0].Reason);
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            h.Post(Me + "/delete", token);
            Assert.Equal("delete", Backups()[0].Reason);
            Assert.Equal(3, Backups().Count); // pruned to "keep"

            // Every backup is a complete profile file.
            foreach (var b in Backups()) ProfileFile.Parse(File.ReadAllText(b.Path), account, null);
        }

        [Fact]
        public void BackupsCanBeDisabled()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.backups.keep", 0L));
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            h.Post(Me + "/level", token, ("object", "1"));
            h.Post(Me + "/level", token, ("object", "2"));
            h.Post(Me + "/delete", token);
            Assert.Empty(h.Store.Backups.List(account));
        }

        [Fact]
        public void ConcurrentWritersLoseNothingAndReadTheirOwnWrites()
        {
            using var h = new StorageHarness(configure: s => s.Set("storage.backups.keep", 0L));
            string token = h.PlayerToken("p");
            const int threads = 8, perThread = 25;
            var failures = new List<string>();
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    string value = "{\"t\":" + t + ",\"i\":" + i + "}";
                    var w = h.Post(Me + "/w" + t + "_" + i, token, ("object", value));
                    var c = h.Post(Me + "/counter", token, ("operation", "increment"), ("object", "1"));
                    var r = h.Get(Me + "/w" + t + "_" + i, token);
                    if (w.StatusCode != 200 || c.StatusCode != 200 || r.BodyText != value)
                        lock (failures) failures.Add(t + "/" + i + ": " + r.BodyText);
                }
            })).ToList();
            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());

            Assert.Empty(failures);
            Assert.Equal((threads * perThread).ToString(), h.Get(Me + "/counter", token).BodyText);
            var doc = StorageHarness.Json(h.Get(Me, token));
            Assert.Equal(threads * perThread + 6, doc.Count); // + counter + 5 read-only fields
            Assert.Equal(0, h.Store.Locks.ActiveKeys);
        }

        [Fact]
        public void AccountsAreIndependent()
        {
            using var h = new StorageHarness();
            string a = h.PlayerToken("a"), b = h.PlayerToken("b");
            h.Post(Me + "/level", a, ("object", "1"));
            h.Post(Me + "/level", b, ("object", "2"));
            Assert.Equal("1", h.Get(Me + "/level", a).BodyText);
            Assert.Equal("2", h.Get(Me + "/level", b).BodyText);
            Assert.Equal(2, h.Store.AccountIds().Count);
        }

        [Fact]
        public void FairLockServesWaitersInArrivalOrderAndIsReentrant()
        {
            var locks = new FairKeyedLock();
            var order = new List<int>();
            var holder = locks.Acquire("k");
            using (locks.Acquire("k")) { } // re-entrant on the same thread
            Assert.Equal(1, locks.QueueLength("k"));

            var threads = new List<Thread>();
            for (int i = 0; i < 6; i++)
            {
                int n = i;
                var t = new Thread(() =>
                {
                    using (locks.Acquire("k")) lock (order) order.Add(n);
                });
                t.Start();
                threads.Add(t);
                // Wait until this thread holds its ticket, so arrival order is known.
                SpinWait.SpinUntil(() => locks.QueueLength("k") == n + 2, 5000);
                Assert.Equal(n + 2, locks.QueueLength("k"));
            }
            holder.Dispose();
            threads.ForEach(t => t.Join());
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, order);
            Assert.Equal(0, locks.ActiveKeys);
        }

        [Fact]
        public void InterruptedWaiterDoesNotBlockTheQueue()
        {
            var locks = new FairKeyedLock();
            var holder = locks.Acquire("k");
            var interrupted = new Thread(() =>
            {
                try { using (locks.Acquire("k")) { } }
                catch (ThreadInterruptedException) { }
            });
            interrupted.Start();
            SpinWait.SpinUntil(() => locks.QueueLength("k") == 2, 5000);
            bool later = false;
            var next = new Thread(() => { using (locks.Acquire("k")) later = true; });
            next.Start();
            SpinWait.SpinUntil(() => locks.QueueLength("k") == 3, 5000);
            interrupted.Interrupt();
            interrupted.Join();
            holder.Dispose();
            Assert.True(next.Join(5000));
            Assert.True(later);
        }

        // ------------------------------------------------------------------ seams for other modules

        [Fact]
        public void InventoryGrantChannelCountsUpAndNotifies()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            var events = new List<Dictionary<string, object>>();
            h.Context.Events.Subscribe(ProfileAccess.LedgerTopic, p => events.Add((Dictionary<string, object>)p));
            var changes = new List<object>();
            h.Context.Events.Subscribe(LocalProfileStore.ChangedTopic, p => changes.Add(p));

            h.Post(Me + "/inventory", token, ("visibility", "restricted"), ("object", "{}")); // R3 creates the section
            Assert.Equal(1, h.Access.AddToInventory(account, "UnlockMerc_ALL_MAX", 1));
            Assert.Equal(3, h.Access.AddToInventory(account, "VersusCoins", 3));
            Assert.Equal(4, h.Access.AddToInventory(account, "VersusCoins", 1));
            Assert.Equal(0, h.Access.AddToLedger(account, "_inventory", "ad.reward", -5)); // clamped, key with a dot

            // The TS reads every entry as (int)(long)JValue.Value: integers only.
            Assert.Equal("{\"UnlockMerc_ALL_MAX\":1,\"VersusCoins\":4}", h.Get(Me + "/inventory", token).BodyText);
            Assert.Equal("{\"ad.reward\":0}", h.Get(Me + "/_inventory", token).BodyText);
            Assert.Equal(4, events.Count);
            Assert.Equal("VersusCoins", events[2]["key"]);
            Assert.Equal(4L, events[2]["count"]);
            Assert.Equal(5, changes.Count);
        }

        [Fact]
        public void ProfileAccessReadsAndWritesLikeTheGame()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("p");
            string account = h.Player("p").Id;
            Assert.False(h.Access.HasProfile(account));
            Assert.Null(h.Access.Read(account, "level"));

            h.Post(Me + "/_mcvs.gameProfile", token, ("object", GameData.GameProfileDump));
            Assert.True(h.Access.HasProfile(account));
            Assert.Equal(GameData.GameProfileDump, h.Access.ReadText(account, "_mcvs.gameProfile"));
            Assert.Equal(57L, h.Access.Read(account, "_mcvs.gameProfile.$default./Profile.Version"));
            var plain = (Dictionary<string, object>)h.Access.Read(account, "");
            Assert.Equal("anonymous:p", plain["credential"]);

            h.Access.Set(account, "settings.unlockAll", true);
            h.Access.Merge(account, "", MiniJson.ParseObject("{\"settings\":{\"x\":1},\"credential\":\"ignored\"}"));
            Assert.Equal("{\"unlockAll\":true,\"x\":1}", h.Get(Me + "/settings", token).BodyText);
            Assert.True(h.Access.Remove(account, "settings.x"));
            Assert.False(h.Access.Remove(account, "settings.x"));
            Assert.Equal(GameData.GameProfileDump, h.Get(Me + "/_mcvs.gameProfile", token).BodyText);
            Assert.Throws<ArgumentException>(() => h.Access.Set(account, "", 1L));
        }

        [Fact]
        public void LocatorRoutesAccountsToAnotherStore()
        {
            using var h = new StorageHarness();
            string token = h.PlayerToken("host");
            var guest = h.Player("guest");
            var remote = new RecordingStore();
            h.Context.Features.Set<IProfileStoreLocator>(new GuestLocator(guest.Id, remote));

            var read = h.Get("profiles/" + guest.FedId + "/myprofile/_mcvs.gameProfile", token);
            Assert.Equal(200, read.StatusCode);
            Assert.Equal("{\"remote\":true}", read.BodyText);
            Assert.Equal(200, h.Post("profiles/fed_id%3A" + guest.Id + "/myprofile", token, ("operation", "merge"), ("object", "{\"a\":1}")).StatusCode);
            Assert.Equal(new[] { "get _mcvs.gameProfile", "write merge " }, remote.Calls);
            Assert.Null(h.StoredFile(guest.Id)); // nothing landed locally

            // Other accounts stay local.
            Assert.Equal(200, h.Post(Me + "/level", token, ("object", "1")).StatusCode);
            Assert.NotNull(h.StoredFile(h.Player("host").Id));
        }

        private sealed class GuestLocator : IProfileStoreLocator
        {
            private readonly string guest;
            private readonly IProfileStore store;

            public GuestLocator(string guest, IProfileStore store)
            {
                this.guest = guest;
                this.store = store;
            }

            public IProfileStore Locate(StorageTarget target) => target.AccountId == guest ? store : null;
        }

        private sealed class RecordingStore : IProfileStore
        {
            public List<string> Calls { get; } = new List<string>();

            public string Name => "recording";

            public StorageResult Get(StorageTarget target, string selector, IList<string> includeFields)
            {
                Calls.Add("get " + selector);
                return StorageResult.Ok("{\"remote\":true}");
            }

            public StorageResult Write(StorageTarget target, StorageWrite write)
            {
                Calls.Add("write " + write.EffectiveOperation + " " + write.Selector);
                return StorageResult.OkEmpty();
            }

            public StorageResult DeleteProfile(StorageTarget target) => StorageResult.OkEmpty();
            public StorageResult ListBackups(StorageTarget target, DateTime? fromUtc, DateTime? toUtc) => StorageResult.Ok("[]");
            public StorageResult GetBackup(StorageTarget target, string backupId) => StorageResult.OkEmpty();
            public StorageResult GetVisibility(StorageTarget target) => StorageResult.OkEmpty();
            public StorageResult SetVisibility(StorageTarget target, string selector, string visibility) => StorageResult.OkEmpty();
        }
    }
}
