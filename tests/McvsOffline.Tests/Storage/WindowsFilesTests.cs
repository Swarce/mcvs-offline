using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using McvsOffline.Server.Core.Storage;
using Xunit;

namespace McvsOffline.Tests.Storage
{
    /// <summary>
    /// The PC build's rename (docs/design/pc-port.md §4.6): MoveFileExW behind an injected native call, retried on a
    /// sharing violation (32) or an access denial (5), five attempts 20 ms apart, then an IOException naming the error.
    /// </summary>
    public class WindowsFilesTests
    {
        /// <summary>A scripted native rename: returns the codes in order (the last one repeats) and records each call.</summary>
        private sealed class ScriptedMove
        {
            private readonly int[] codes;

            public ScriptedMove(params int[] codes)
            {
                this.codes = codes;
            }

            public readonly List<string> Calls = new List<string>();

            public int Invoke(string source, string destination)
            {
                Calls.Add(source + " -> " + destination);
                return codes[Math.Min(Calls.Count - 1, codes.Length - 1)];
            }
        }

        [Fact]
        public void SuccessAtOnceIsOneCall()
        {
            var move = new ScriptedMove(0);
            WindowsFiles.Rename("a.tmp", "a.json", move.Invoke);
            Assert.Equal(new[] { "a.tmp -> a.json" }, move.Calls);
        }

        [Fact]
        public void ASharingViolationIsRetried()
        {
            var move = new ScriptedMove(WindowsFiles.ErrorSharingViolation, 0);
            WindowsFiles.Rename("a.tmp", "a.json", move.Invoke);
            Assert.Equal(2, move.Calls.Count);
        }

        [Fact]
        public void AccessDeniedFiveTimesFailsWithTheWin32Error()
        {
            var move = new ScriptedMove(WindowsFiles.ErrorAccessDenied);
            var watch = Stopwatch.StartNew();
            IOException failure = Assert.Throws<IOException>(() => WindowsFiles.Rename("a.tmp", "a.json", move.Invoke));
            watch.Stop();
            Assert.Equal(WindowsFiles.RenameAttempts, move.Calls.Count);
            Assert.Equal(5, move.Calls.Count);
            Assert.Contains("Win32 error 5 (access denied)", failure.Message);
            Assert.Contains("after 5 attempts", failure.Message);
            Assert.Contains("a.tmp", failure.Message);
            Assert.Contains("a.json", failure.Message);
            // Four pauses of 20 ms between the five attempts.
            Assert.True(watch.ElapsedMilliseconds >= 4 * WindowsFiles.RenameDelayMs - 5, watch.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void TransientErrorsThatClearOnTheLastAttemptSucceed()
        {
            var move = new ScriptedMove(32, 5, 32, 5, 0);
            WindowsFiles.Rename("a.tmp", "a.json", move.Invoke);
            Assert.Equal(5, move.Calls.Count);
        }

        [Theory]
        [InlineData(2, "file not found")]
        [InlineData(112, "disk full")]
        [InlineData(1234, null)]
        public void AnyOtherErrorFailsAtOnce(int error, string name)
        {
            var move = new ScriptedMove(error, 0);
            IOException failure = Assert.Throws<IOException>(() => WindowsFiles.Rename("a.tmp", "a.json", move.Invoke));
            Assert.Single(move.Calls);
            Assert.Contains("Win32 error " + error + (name == null ? "" : " (" + name + ")"), failure.Message);
            Assert.DoesNotContain("attempts", failure.Message);
        }

        [Theory]
        [InlineData(5, true)]
        [InlineData(32, true)]
        [InlineData(0, false)]
        [InlineData(2, false)]
        [InlineData(33, false)] // ERROR_LOCK_VIOLATION: not a rename's transient case
        public void OnlySharingViolationAndAccessDeniedAreTransient(int error, bool transient)
        {
            Assert.Equal(transient, WindowsFiles.IsTransient(error));
        }

        [Fact]
        public void TheNativeCallIsRequired()
        {
            Assert.Throws<ArgumentNullException>(() => WindowsFiles.Rename("a", "b", null));
        }
    }

    /// <summary><see cref="FileSystem.Retry"/>: attempts, the pause between them, and what a failure looks like.</summary>
    public class FileSystemRetryTests
    {
        [Fact]
        public void StopsAtTheFirstSuccess()
        {
            int calls = 0;
            Assert.Equal(0, FileSystem.Retry(() => ++calls < 3 ? 7 : 0, 5, 0));
            Assert.Equal(3, calls);
        }

        [Fact]
        public void ReturnsTheLastTransientErrorWhenTheAttemptsRunOut()
        {
            int calls = 0;
            Assert.Equal(13, FileSystem.Retry(() => 10 + ++calls, 3, 0));
            Assert.Equal(3, calls);
        }

        [Fact]
        public void SleepsBetweenAttemptsButNotBeforeTheFirstOrAfterTheLast()
        {
            var times = new List<long>();
            var watch = Stopwatch.StartNew();
            FileSystem.Retry(() =>
            {
                times.Add(watch.ElapsedMilliseconds);
                return 1;
            }, 3, 30);
            long total = watch.ElapsedMilliseconds;
            Assert.Equal(3, times.Count);
            Assert.True(times[0] < 25, "first call at " + times[0] + " ms");
            Assert.True(times[1] - times[0] >= 25 && times[2] - times[1] >= 25, string.Join(", ", times));
            Assert.True(total - times[2] < 25, "no pause after the last attempt: " + (total - times[2]) + " ms");
        }

        [Fact]
        public void AnExceptionPropagatesAtOnceUnchanged()
        {
            int calls = 0;
            var thrown = new IOException("permanent");
            IOException caught = Assert.Throws<IOException>(() => FileSystem.Retry(() =>
            {
                calls++;
                if (calls == 2) throw thrown;
                return 1;
            }, 5, 0));
            Assert.Same(thrown, caught);
            Assert.Equal(2, calls);
        }

        [Fact]
        public void AtLeastOneAttempt()
        {
            int calls = 0;
            Assert.Equal(4, FileSystem.Retry(() => { calls++; return 4; }, 0, 0));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void TheNet8RenameStillReplacesTheTarget()
        {
            using var dir = new TempDir();
            string source = dir.Combine("a.tmp");
            string target = dir.Combine("a.json");
            File.WriteAllText(source, "new");
            File.WriteAllText(target, "old");
            FileSystem.Rename(source, target);
            Assert.False(File.Exists(source));
            Assert.Equal("new", File.ReadAllText(target));
        }
    }
}
