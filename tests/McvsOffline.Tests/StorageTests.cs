using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>Atomic files, keyed locks and the per-module document store.</summary>
    public class StorageTests
    {
        [Fact]
        public void AtomicWriteReplacesContentAndLeavesNoTempFiles()
        {
            using var dir = new TempDir();
            string path = dir.Combine("sub", "doc.json");
            AtomicFile.WriteAllText(path, "{\"v\":1}");
            AtomicFile.WriteAllText(path, "{\"v\":2}");
            Assert.Equal("{\"v\":2}", AtomicFile.ReadAllTextOrNull(path));
            Assert.Single(Directory.GetFiles(dir.Combine("sub")));
            Assert.Null(AtomicFile.ReadAllTextOrNull(dir.Combine("missing.json")));
        }

        [Fact]
        public void LeftoverTempFilesAreCleanedUp()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.Combine("doc.json.123" + AtomicFile.TempSuffix), "partial");
            File.WriteAllText(dir.Combine("doc.json"), "{}");
            Assert.Equal(1, AtomicFile.DeleteLeftovers(dir.Path));
            Assert.Single(Directory.GetFiles(dir.Path));
        }

        [Fact]
        public void KeysEncodeToSafeReversibleFileNames()
        {
            foreach (string key in new[] { "fed_id:9f2c0d1e7a6b4c3d2e1f0a9b", "a/b\\c", "..", ".hidden", "café", "x_y" })
            {
                string encoded = DocumentStore.EncodeKey(key);
                Assert.DoesNotContain("/", encoded);
                Assert.DoesNotContain(":", encoded);
                Assert.False(encoded.StartsWith("."));
                Assert.Equal(key, DocumentStore.DecodeKey(encoded));
            }
            Assert.Equal("9f2c0d1e", DocumentStore.EncodeKey("9f2c0d1e"));
        }

        [Fact]
        public void DocumentStoreReadWriteUpdateDelete()
        {
            using var dir = new TempDir();
            var store = new DocumentStore(dir.Combine("profiles"));
            Assert.Null(store.Read("acc1"));
            store.Write("acc1", MiniJson.ParseObject("{\"a\":1}"));
            Assert.Equal(1L, store.Read("acc1")["a"]);
            int result = store.Update("acc1", doc => { doc["b"] = 2L; return 42; });
            Assert.Equal(42, result);
            Assert.Equal("{\"a\":1,\"b\":2}", store.ReadText("acc1"));
            store.Update("acc2", doc => doc["new"] = true);
            Assert.Equal(new List<string> { "acc1", "acc2" }, store.Keys());
            Assert.True(store.Delete("acc1"));
            Assert.False(store.Exists("acc1"));
            Assert.False(store.Delete("acc1"));
        }

        [Fact]
        public void FailedUpdateWritesNothing()
        {
            using var dir = new TempDir();
            var store = new DocumentStore(dir.Path);
            store.Write("k", MiniJson.ParseObject("{\"a\":1}"));
            Assert.Throws<InvalidOperationException>(() => store.Update("k", doc => { doc["a"] = 2L; throw new InvalidOperationException(); }));
            Assert.Equal("{\"a\":1}", store.ReadText("k"));
        }

        [Fact]
        public void CorruptDocumentsAreReportedNotTreatedAsMissing()
        {
            // Reporting stored data as absent makes the game reset the profile (verify-C3 §3.1).
            using var dir = new TempDir();
            var store = new DocumentStore(dir.Path);
            File.WriteAllText(store.PathFor("acc"), "{\"truncated\":");
            Assert.Throws<DocumentStoreException>(() => store.Read("acc"));
            Assert.Throws<DocumentStoreException>(() => store.Update("acc", d => { }));
            Assert.Equal("{\"truncated\":", File.ReadAllText(store.PathFor("acc")));
        }

        [Fact]
        public void ConcurrentUpdatesToOneKeyAreSerialized()
        {
            using var dir = new TempDir();
            var store = new DocumentStore(dir.Path);
            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 25; i++)
                    store.Update("counter", doc => doc["n"] = JsonValue.GetLong(doc, "n") + 1);
            });
            Assert.Equal(200L, store.Read("counter")["n"]);
        }

        [Fact]
        public void KeyedLockExcludesSameKeyOnlyAndCleansUp()
        {
            var locks = new KeyedLock();
            var holding = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                using (locks.Acquire("a"))
                {
                    holding.Set();
                    release.Wait();
                }
            });
            holder.Start();
            holding.Wait();

            Assert.True(RunsWithin(() => { using (locks.Acquire("b")) { } }, 2000, out _));

            Assert.False(RunsWithin(() => { using (locks.Acquire("a")) { } }, 200, out var sameKey));
            release.Set();
            Assert.True(sameKey.Join(2000));
            holder.Join();
            Assert.Equal(0, locks.ActiveKeys);

            using (locks.Acquire("r"))
            using (locks.Acquire("r")) // re-entrant
            {
                Assert.Equal(1, locks.ActiveKeys);
            }
            Assert.Equal(0, locks.ActiveKeys);
        }

        private static bool RunsWithin(Action action, int timeoutMs, out Thread thread)
        {
            thread = new Thread(() => action());
            thread.Start();
            return thread.Join(timeoutMs);
        }

        [Fact]
        public void FileSystemRenameReplacesTheTarget()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.Combine("a"), "new");
            File.WriteAllText(dir.Combine("b"), "old");
            FileSystem.Rename(dir.Combine("a"), dir.Combine("b"));
            Assert.Equal("new", File.ReadAllText(dir.Combine("b")));
            Assert.False(File.Exists(dir.Combine("a")));
        }
    }
}
