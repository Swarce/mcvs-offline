using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Embedded;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Embedded.Windows;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// WR2 F3 (docs/design/pc-port.md §6.4): our top-level exception filter over the exe's non-chaining one (run through its
    /// function pointer with a fake kernel32), the game's Fatal lines as "probably fatal" reports, and the pid in the host's lines.
    /// In the GameHooks collection: CrashReporter's directory, context and flush are process-wide.
    /// </summary>
    [Collection("GameHooks")]
    public class PcCrashCaptureTests
    {
        /// <summary>kernel32 for the filter: files on disk, a fixed module and thread, the installed filter recorded.</summary>
        private sealed class FakeNative : ICrashFilterNative
        {
            public IntPtr Previous;
            public IntPtr Installed;
            public string ModuleName = @"C:\Steam\steamapps\common\Modern Combat Versus\Gameloft.Engine.Native.dll";
            public IntPtr ModuleBase = new IntPtr(0x10000000);
            public readonly Dictionary<long, FileStream> Open = new Dictionary<long, FileStream>();
            public readonly List<string> Created = new List<string>();
            public readonly List<string> Refused = new List<string>();
            public int Flushes;
            private long next = 100;

            public IntPtr SetFilter(IntPtr filter)
            {
                IntPtr old = Installed == IntPtr.Zero ? Previous : Installed;
                Installed = filter;
                return old;
            }

            public uint CurrentThreadId()
            {
                return 7712;
            }

            public int ModuleOf(IntPtr address, char[] buffer, out IntPtr moduleBase)
            {
                moduleBase = IntPtr.Zero;
                long a = address.ToInt64();
                if (a < ModuleBase.ToInt64() || a >= ModuleBase.ToInt64() + 0x100000) return 0;
                moduleBase = ModuleBase;
                ModuleName.CopyTo(0, buffer, 0, ModuleName.Length);
                return ModuleName.Length;
            }

            public IntPtr Create(char[] path)
            {
                int end = Array.IndexOf(path, '\0');
                string file = new string(path, 0, end);
                try
                {
                    var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    long handle = next++;
                    Open[handle] = stream;
                    Created.Add(file);
                    return new IntPtr(handle);
                }
                catch (IOException)
                {
                    Refused.Add(file);
                    return IntPtr.Zero;
                }
            }

            public bool Write(IntPtr file, byte[] data, int count)
            {
                Open[file.ToInt64()].Write(data, 0, count);
                return true;
            }

            public void Flush(IntPtr file)
            {
                Open[file.ToInt64()].Flush();
                Flushes++;
            }

            public void Close(IntPtr file)
            {
                Open[file.ToInt64()].Dispose();
                Open.Remove(file.ToInt64());
            }
        }

        /// <summary>An EXCEPTION_POINTERS with one EXCEPTION_RECORD in unmanaged memory (this process's pointer size).</summary>
        private sealed class ExceptionPointers : IDisposable
        {
            private readonly IntPtr record;

            public ExceptionPointers(uint code, uint flags, long address, params long[] parameters)
            {
                int ptr = IntPtr.Size;
                int information = ptr == 8 ? 32 : 20;
                record = Marshal.AllocHGlobal(information + 15 * ptr);
                for (int i = 0; i < information + 15 * ptr; i++) Marshal.WriteByte(record, i, 0);
                Marshal.WriteInt32(record, 0, unchecked((int)code));
                Marshal.WriteInt32(record, 4, unchecked((int)flags));
                Marshal.WriteIntPtr(record, 8 + ptr, new IntPtr(address));
                Marshal.WriteInt32(record, 8 + 2 * ptr, parameters.Length);
                for (int i = 0; i < parameters.Length; i++) Marshal.WriteIntPtr(record, information + i * ptr, new IntPtr(parameters[i]));
                Pointer = Marshal.AllocHGlobal(2 * ptr);
                Marshal.WriteIntPtr(Pointer, 0, record);
                Marshal.WriteIntPtr(Pointer, ptr, IntPtr.Zero);
            }

            public IntPtr Pointer { get; }

            public void Dispose()
            {
                Marshal.FreeHGlobal(record);
                Marshal.FreeHGlobal(Pointer);
            }
        }

        private static string Hex(long value)
        {
            return value.ToString("X" + (2 * IntPtr.Size));
        }

        private static readonly DateTime CrashTime = new DateTime(2026, 10, 29, 18, 22, 25, 123, DateTimeKind.Utc);

        [Fact]
        public void TheFilterWritesItsReportThenReturnsWhatTheGamesFilterReturns()
        {
            string dir = TestEnvironment.NewTempDir("native-crash");
            var native = new FakeNative();
            var gameCalls = new List<IntPtr>();
            // The exe's filter (0x406e10): writes core.dmp, returns EXCEPTION_CONTINUE_SEARCH (here 1 to tell it from ours).
            NativeCrashFilter.TopLevelFilter game = p =>
            {
                gameCalls.Add(p);
                return 1;
            };
            GCHandle keep = GCHandle.Alloc(game);
            native.Previous = Marshal.GetFunctionPointerForDelegate(game);
            int flushes = 0;
            NativeCrashFilter.ResetForTests();
            CrashReporter.ResetForTests();
            try
            {
                string hooks = NativeCrashFilter.Install(native, 4321, null);
                Assert.Equal("native filter ok (previous filter 0x" + native.Previous.ToInt64().ToString("X8") + ")", hooks);
                Assert.Equal(NativeCrashFilter.FilterPointer, native.Installed);
                Assert.Equal(native.Previous, NativeCrashFilter.PreviousFilter);
                Assert.Equal("native filter ok (already installed)", NativeCrashFilter.Install(native, 4321, null));

                NativeCrashFilter.Clock = () => CrashTime;
                CrashReporter.SetCrashDirectory(dir);
                CrashReporter.LastGameFatal = "14:22:24.900 Fatal [] {ASValue.cs}.SetAsDictionary | probe";
                CrashReporter.Context = () => "boot: 88 requests, last #88 GET /inventory\nwatchdog: game thread tid 7712";
                CrashReporter.FlushLogs = budget => flushes++;

                // An access violation writing 0x40 inside the engine's native DLL, through the installed function pointer (WINAPI).
                var installed = (NativeCrashFilter.TopLevelFilter)Marshal.GetDelegateForFunctionPointer(native.Installed, typeof(NativeCrashFilter.TopLevelFilter));
                using (var av = new ExceptionPointers(0xC0000005, 0, 0x10001234, 1, 0x40))
                {
                    Assert.Equal(1, installed(av.Pointer));
                    Assert.Equal(new[] { av.Pointer }, gameCalls.ToArray());
                }

                string file = Path.Combine(dir, "crash-20261029-182225-123-native.txt");
                Assert.Equal(new[] { file }, native.Created.ToArray());
                Assert.Empty(native.Open);   // closed
                Assert.Equal(file, NativeCrashFilter.LastReport);
                string text = File.ReadAllText(file);
                Assert.StartsWith("FATAL native unhandled exception (top-level filter; the game's own filter runs next and writes core.dmp", text);
                Assert.Contains("time 2026-10-29T18:22:25.123Z, pid 4321, thread tid 7712\n", text);
                Assert.Contains("code 0xC0000005 (access violation), flags 0x0\n", text);
                Assert.Contains("address 0x" + Hex(0x10001234) + " in " + native.ModuleName + " +0x1234 (base 0x" + Hex(0x10000000) + ")\n", text);
                Assert.Contains("writing 0x" + Hex(0x40) + "\n", text);
                Assert.Contains("parameters 0x1 0x40\n", text);
                Assert.Contains("game's last Fatal line: 14:22:24.900 Fatal [] {ASValue.cs}.SetAsDictionary | probe\n", text);
                Assert.Contains("--- context ---\nboot: 88 requests, last #88 GET /inventory\nwatchdog: game thread tid 7712\n", text);
                Assert.True(text.IndexOf("game's last Fatal line", StringComparison.Ordinal) < text.IndexOf("--- context ---", StringComparison.Ordinal));
                Assert.Equal(2, native.Flushes);   // the allocation-free part reaches the disk before the context is built
                Assert.Equal(1, flushes);          // then the queued log lines

                // A second crashing thread: no second report, the game's filter still runs.
                using (var again = new ExceptionPointers(NativeCrashFilter.ClrException, 1, 0x7FFE0000))
                {
                    Assert.Equal(1, installed(again.Pointer));
                    Assert.Equal(2, gameCalls.Count);
                }
                Assert.Single(native.Created);
            }
            finally
            {
                NativeCrashFilter.ResetForTests();
                CrashReporter.Context = null;
                CrashReporter.FlushLogs = null;
                CrashReporter.SetCrashDirectory(null);
                CrashReporter.ResetForTests();
                keep.Free();
            }
            Assert.Equal(native.Previous, native.Installed);   // the reset put the game's filter back
        }

        [Fact]
        public void AFilterThatCallsBackIntoOursAndNoPreviousFilterBothEndInContinueSearch()
        {
            string dir = TestEnvironment.NewTempDir("native-crash-reentry");
            var native = new FakeNative();
            int inner = -1;
            NativeCrashFilter.TopLevelFilter chaining = p =>
            {
                inner = NativeCrashFilter.Filter(p);   // a filter that chains to the one it replaced (ours)
                return 1;
            };
            GCHandle keep = GCHandle.Alloc(chaining);
            native.Previous = Marshal.GetFunctionPointerForDelegate(chaining);
            NativeCrashFilter.ResetForTests();
            CrashReporter.ResetForTests();
            try
            {
                NativeCrashFilter.Install(native, 1, null);
                NativeCrashFilter.Clock = () => CrashTime;
                CrashReporter.SetCrashDirectory(dir);
                using (var e = new ExceptionPointers(0xC00000FD, 1, 0x20000000))
                {
                    Assert.Equal(1, NativeCrashFilter.Filter(e.Pointer));
                    Assert.Equal(NativeCrashFilter.ContinueSearch, inner);
                }
                string text = File.ReadAllText(Assert.Single(native.Created));
                Assert.Contains("code 0xC00000FD (stack overflow), flags 0x1 (noncontinuable)\n", text);
                Assert.Contains("address 0x" + Hex(0x20000000) + " (in no loaded module)\n", text);
                Assert.Contains("game's last Fatal line: (none)\n", text);
                Assert.DoesNotContain("parameters", text);
            }
            finally
            {
                NativeCrashFilter.ResetForTests();
                CrashReporter.SetCrashDirectory(null);
                CrashReporter.ResetForTests();
                keep.Free();
            }

            // No previous filter (none installed before ours): EXCEPTION_CONTINUE_SEARCH; before the host knows the game folder
            // the report goes to the fallback folder (%TEMP%\McvsOffline\logs).
            string fallback = TestEnvironment.NewTempDir("native-crash-fallback");
            var alone = new FakeNative();
            try
            {
                CrashReporter.Context = null;   // EmbeddedHost.Start in other tests leaves its own
                CrashReporter.SetCrashDirectory(null);
                Assert.Equal("native filter ok (previous filter none)", NativeCrashFilter.Install(alone, 2, fallback));
                NativeCrashFilter.Clock = () => CrashTime;
                using (var e = new ExceptionPointers(NativeCrashFilter.ClrException, 1, 0x7FFE0000, 0x80131500))
                {
                    Assert.Equal(NativeCrashFilter.ContinueSearch, NativeCrashFilter.Filter(e.Pointer));
                }
                string file = Assert.Single(alone.Created);
                Assert.Equal(fallback, Path.GetDirectoryName(file));
                string text = File.ReadAllText(file);
                Assert.Contains("(managed exception (CLR))", text);
                Assert.Contains("a managed exception reached native code", text);
                Assert.DoesNotContain("--- context ---", text);
            }
            finally
            {
                NativeCrashFilter.ResetForTests();
            }
        }

        [Fact]
        public void TheReportIsFormattedWithoutAllocating()
        {
            var native = new FakeNative();
            CrashReporter.ResetForTests();
            CrashReporter.LastGameFatal = "14:06:38.389 Fatal [] MenuComponent.OnInvokeDelegate : An exception has occured. Ação";
            try
            {
                using (var av = new ExceptionPointers(0xC0000005, 0, 0x10004000, 0, 0x10))
                {
                    NativeCrashFilter.Format(av.Pointer, CrashTime, native);   // JIT and statics first
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    NativeCrashFilter.Format(av.Pointer, CrashTime, native);
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.Equal(0, allocated);
                    string text = NativeCrashFilter.FormattedText();
                    Assert.Contains("reading 0x" + Hex(0x10) + "\n", text);
                    Assert.Contains("+0x4000", text);
                    Assert.Contains("occured. Ação\n", text);   // UTF-8
                }
                NativeCrashFilter.Format(IntPtr.Zero, CrashTime, native);
                Assert.Contains("no exception record\n", NativeCrashFilter.FormattedText());
            }
            finally
            {
                CrashReporter.ResetForTests();
            }
            Assert.Equal("unknown", NativeCrashFilter.CodeName(0x12345678));
            Assert.Equal("C++ exception", NativeCrashFilter.CodeName(0xE06D7363));
        }

        [Fact]
        public void OnlyWindowsInstallsTheFilterFromThePlatform()
        {
            // Off Windows (the tests) the platform's hook is a no-op; nothing is added to the hooks line.
            string before = CrashReporter.Hooks;
            new WindowsPlatform().InstallCrashHooks();
            WindowsPlatform.InstallNativeCrashFilter();
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(before, CrashReporter.Hooks);
                Assert.Equal(IntPtr.Zero, NativeCrashFilter.FilterPointer);
            }
        }

        // ------------------------------------------------------------------ the game's Fatal lines

        [Fact]
        public void TheGamesRethrowLinesAreCrashCandidates()
        {
            Assert.Equal(GameFatalLines.MenuSource, GameFatalLines.CrashCandidate("OnInvokeDelegate", "MenuComponent.OnInvokeDelegate : An exception has occured."));
            Assert.Equal(GameFatalLines.MenuSource, GameFatalLines.CrashCandidate(null, "MenuComponent.OnInvokeDelegate : An exception has occured."));
            Assert.Equal(GameFatalLines.TrackSource, GameFatalLines.CrashCandidate("TrackUnhandledException", "UnhandledException"));
            Assert.Equal(GameFatalLines.TrackSource, GameFatalLines.CrashCandidate("", "UnhandledException"));
            Assert.Null(GameFatalLines.CrashCandidate("OnStart", "Failed to load the store"));
            Assert.Null(GameFatalLines.CrashCandidate(null, null));
        }

        /// <summary>
        /// WR2 crashes 1 and 2 (game.log:2525, 2547): OnInvokeDelegate logs MenuInvokationException(inner TargetInvocationException),
        /// then TrackUnhandledException logs that inner exception: one report per incident; a later incident gets its own.
        /// </summary>
        [Fact]
        public void OneProbablyFatalReportPerIncidentWithTheManagedStack()
        {
            var reports = new List<Tuple<string, object, string>>();
            var lines = new GameFatalLines((s, e, d) => reports.Add(Tuple.Create(s, e, d)), () => "   at Gameloft.Engine.MenuComponent.OnInvokeDelegate(Int32 funcID, IntPtr argument)");
            CrashReporter.ResetForTests();
            try
            {
                var nre = new NullReferenceException("Object reference not set to an instance of an object.");
                var target = new System.Reflection.TargetInvocationException(nre);
                var menu = new InvalidOperationException("Failed to invoke 'ToggleInboxMessages'", target);

                lines.Fatal("Update", "Something else is fatal", null, "14:06:30.000 Fatal [] Something else is fatal");
                Assert.Empty(reports);
                Assert.Equal("14:06:30.000 Fatal [] Something else is fatal", CrashReporter.LastGameFatal);

                lines.Fatal("OnInvokeDelegate", "MenuComponent.OnInvokeDelegate : An exception has occured.", menu,
                            "14:06:38.389 Fatal [] MenuComponent.OnInvokeDelegate : An exception has occured.\nMenuInvokationException: Failed to invoke 'ToggleInboxMessages'");
                lines.Fatal("TrackUnhandledException", "UnhandledException", target, "14:06:38.414 Fatal [] UnhandledException\nTargetInvocationException");
                Tuple<string, object, string> first = Assert.Single(reports);
                Assert.Equal(GameFatalLines.MenuSource, first.Item1);
                Assert.Same(menu, first.Item2);
                Assert.Contains("--- the game's Fatal line ---\n14:06:38.389 Fatal [] MenuComponent.OnInvokeDelegate", first.Item3);
                Assert.Contains("--- managed stack of the logging thread ---\n   at Gameloft.Engine.MenuComponent.OnInvokeDelegate", first.Item3);
                Assert.Equal("14:06:38.414 Fatal [] UnhandledException | TargetInvocationException", CrashReporter.LastGameFatal);

                // UpdateWorlds: TrackUnhandledException alone, a new exception: reported.
                var update = new InvalidOperationException("UpdateWorlds");
                lines.Fatal("TrackUnhandledException", "UnhandledException", update, "14:08:50.987 Fatal [] UnhandledException");
                Assert.Equal(2, reports.Count);
                Assert.Equal(GameFatalLines.TrackSource, reports[1].Item1);

                string longLine = new string('x', 5000);
                lines.Fatal(null, "x", null, longLine);
                Assert.Equal(GameFatalLines.LastFatalChars + 3, CrashReporter.LastGameFatal.Length);
                Assert.Null(GameFatalLines.Short(""));
                Assert.True(GameFatalLines.InChain(menu, nre));
                Assert.False(GameFatalLines.InChain(nre, menu));

                // A report sink that throws never reaches the game's logger.
                new GameFatalLines((s, e, d) => throw new IOException("disk full")).Fatal("OnInvokeDelegate", "MenuComponent.OnInvokeDelegate", new Exception(), "l");
            }
            finally
            {
                CrashReporter.ResetForTests();
            }
        }

        [Fact]
        public void ProbablyFatalReportsAreExceptionFilesWithTheirOwnBudget()
        {
            string dir = TestEnvironment.NewTempDir("probably-fatal");
            CrashReporter.Install();
            CrashReporter.ResetForTests();
            int flushes = 0;
            CrashReporter.SetCrashDirectory(dir);
            CrashReporter.FlushLogs = budget => flushes++;
            CrashReporter.Context = () => "boot: 12 requests";
            HostProcess.Id = 4321;
            try
            {
                var target = new System.Reflection.TargetInvocationException(new NullReferenceException("inbox"));
                var lines = new GameFatalLines(CrashReporter.ReportProbablyFatal, () => "   at Game.InboxComponent.NativeToggleMessages()");
                lines.Fatal("TrackUnhandledException", "UnhandledException", target, "14:06:38.414 Fatal [] UnhandledException");
                string file = Assert.Single(Directory.GetFiles(dir, "exception-*.txt"));
                string text = File.ReadAllText(file);
                Assert.StartsWith("PROBABLY FATAL exception via GameApplication.TrackUnhandledException (the game logged it as Fatal and rethrows it into native code", text);
                Assert.Contains(", pid 4321\n", text);
                Assert.Contains("System.NullReferenceException: inbox", text);
                Assert.Contains("--- managed stack of the logging thread ---\n   at Game.InboxComponent.NativeToggleMessages()", text);
                Assert.Contains("--- context ---\nboot: 12 requests", text);
                Assert.Equal(1, flushes);
                Assert.Empty(Directory.GetFiles(dir, "crash-*.txt"));

                // The same object as a crash later (an AppDomain handler after all): the FATAL report still goes through.
                CrashReporter.Report("AppDomain.UnhandledException", target, true);
                Assert.Single(Directory.GetFiles(dir, "crash-*.txt"));
                // ... and not the other way round.
                CrashReporter.ReportProbablyFatal(GameFatalLines.TrackSource, target, null);
                Assert.Single(Directory.GetFiles(dir, "exception-*.txt"));

                for (int i = 0; i < CrashReporter.ProbablyFatalBudget + 2; i++)
                    CrashReporter.ReportProbablyFatal(GameFatalLines.MenuSource, new Exception("menu " + i), null);
                Assert.Equal(CrashReporter.ProbablyFatalBudget, Directory.GetFiles(dir, "exception-*.txt").Count(f => File.ReadAllText(f).StartsWith("PROBABLY FATAL", StringComparison.Ordinal)));
                // The fatal budget is untouched by them.
                CrashReporter.Report("AppDomain.UnhandledException", new Exception("crash after"), true);
                Assert.Equal(2, Directory.GetFiles(dir, "crash-*.txt").Length);
            }
            finally
            {
                HostProcess.Id = 0;
                CrashReporter.Context = null;
                CrashReporter.FlushLogs = null;
                CrashReporter.SetCrashDirectory(null);
                CrashReporter.ResetForTests();
            }
        }

        // ------------------------------------------------------------------ the pid in the host's lines

        [Fact]
        public void ThePidIsInTheStartLineOnlyWhenTheHostSetsIt()
        {
            Assert.Equal("", HostProcess.Suffix(", "));
            HostProcess.Id = 4321;
            try
            {
                Assert.Equal(", pid 4321", HostProcess.Suffix(", "));
                Assert.Equal("; pid 4321", HostProcess.Suffix("; "));
                string root = TestEnvironment.NewTempDir("pid-line");
                EmbeddedHost host = EmbeddedHost.Start(new DirectoryPlatform(root, false),
                                                       new EmbeddedHostOptions { Port = TestEnvironment.FreePort(), Watchdog = false, StatusIntervalMs = 0 });
                try
                {
                    Assert.True(host.WaitForServer(30000));
                    Assert.True(host.FlushLogs(10000));
                }
                finally
                {
                    host.Server?.Dispose();
                    host.Logger.Dispose();
                }
                Assert.Contains("embedded host starting (platform directory, pid 4321)",
                                File.ReadAllText(Path.Combine(root, EmbeddedPaths.DataFolderName, "logs", "bootstrap.log")));
            }
            finally
            {
                HostProcess.Id = 0;
            }
        }
    }
}
