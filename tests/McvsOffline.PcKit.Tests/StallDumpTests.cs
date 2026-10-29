using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Pc;
using Mono.Cecil;
using Xunit;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>
    /// WR2 F4: 'watch' follows bootstrap.log and takes one full dump per STALL of 60 s or more (fake dump writer), 'collect'
    /// lists the dumps without zipping them, and the MiniDumpWriteDump / OpenProcess imports have their Win32 shapes.
    /// </summary>
    public class StallDumpTests
    {
        // The host's STALL lines (bootstrap.log, build 14: "; pid N" at the end of the first line).
        private static string Stall(int ms, int pid)
        {
            return "2026-10-29T19:03:00.083Z WARN  [watchdog] STALL: no game-thread work item ran for " + ms +
                   " ms; frame clock (totalTime) unchanged for " + (ms - 1000) + " ms; suspendUpdateInBackground=false" + (pid == 0 ? "" : "; pid " + pid);
        }

        /// <summary>What the kit printed (INFO to stdout, WARN to stderr).</summary>
        private static string All(KitSandbox box)
        {
            return box.Out.ToString() + box.Err.ToString();
        }

        private const string RunsAgain = "2026-10-29T19:06:00.000Z INFO  [watchdog] the game thread runs again (after 4 stall reports)";

        private sealed class Dumps
        {
            public readonly List<Tuple<int, string>> Calls = new List<Tuple<int, string>>();
            public long Free = 100L * 1024 * 1024 * 1024;
            public bool Fail;

            public void Attach(KitEnvironment env)
            {
                env.WriteDump = (pid, path) =>
                {
                    Calls.Add(Tuple.Create(pid, path));
                    File.WriteAllBytes(path, new byte[1234]);
                    if (Fail) throw new System.ComponentModel.Win32Exception(5, "OpenProcess(" + pid + ") failed");
                    return 1234;
                };
                env.FreeBytes = folder => Free;
            }
        }

        [Fact]
        public void StallLinesParseWithAndWithoutThePid()
        {
            Assert.Equal(Tuple.Create(60594, 14492), StallDumps.ParseStall(Stall(60594, 14492)));
            Assert.Equal(Tuple.Create(180844, 0), StallDumps.ParseStall(Stall(180844, 0)));   // WR2's build 13 lines
            Assert.Null(StallDumps.ParseStall("[watchdog] game thread is tid 14492 ''"));
            Assert.Null(StallDumps.ParseStall(null));
        }

        [Fact]
        public void OneDumpPerStallOfSixtySecondsOrMore()
        {
            using (var box = new KitSandbox())
            {
                var dumps = new Dumps();
                dumps.Attach(box.Env);
                var log = new KitLog(box.Temp, box.Out, box.Err, box.Env.Now);
                var stalls = new StallDumps(box.Env, new string[0], log);
                var game = new List<int> { 14492 };

                stalls.Feed(Stall(5000, 14492), game);
                stalls.Feed(Stall(15000, 14492), game);
                Assert.Empty(dumps.Calls);
                stalls.Feed(Stall(60594, 14492), game);
                stalls.Feed(Stall(180844, 14492), game);   // the same stall: no second dump
                Tuple<int, string> first = Assert.Single(dumps.Calls);
                Assert.Equal(14492, first.Item1);
                string folder = Path.Combine(box.Env.LocalAppData, "McvsOffline", "dumps");
                Assert.Equal(folder, StallDumps.Folder(box.Env));
                string dump = Path.Combine(folder, "ModernCombatVersus-14492-20261102-180000-stall-60s.dmp");
                Assert.Equal(dump + ".partial", first.Item2);
                Assert.True(File.Exists(dump));
                Assert.False(File.Exists(dump + ".partial"));
                Assert.Contains("watch: dump written: " + dump + ", 1234 bytes", All(box));

                // The game thread ran again; the next stall gets its own dump.
                stalls.Feed(RunsAgain, game);
                stalls.Feed(Stall(5000, 14492), game);
                stalls.Feed(Stall(61000, 14492), game);
                Assert.Equal(2, dumps.Calls.Count);

                // A shorter lag without the recovery line is a new stall too; one without a pid (an older host) dumps the only game.
                box.Env.Now = () => new DateTime(2026, 11, 2, 18, 5, 0);
                stalls.Feed(Stall(60100, 0), game);
                Assert.Equal(3, dumps.Calls.Count);
                Assert.Equal(StallDumps.MaxPerWatch, stalls.Written);

                // The fourth: this watch has written its share.
                stalls.Feed("2026-10-29T19:10:00.000Z INFO  [embedded] MCVS Offline 0.1.0 embedded host starting (platform windows, pid 999)", game);
                stalls.Feed(Stall(65000, 14492), game);
                Assert.Equal(3, dumps.Calls.Count);
                Assert.Contains("this watch already wrote 3 dumps, no more", All(box));
            }
        }

        [Fact]
        public void NoDumpOfAnotherProcessOrWithoutRoom()
        {
            using (var box = new KitSandbox())
            {
                var dumps = new Dumps();
                dumps.Attach(box.Env);
                var log = new KitLog(box.Temp, box.Out, box.Err, box.Env.Now);
                var stalls = new StallDumps(box.Env, new string[0], log);

                stalls.Feed(Stall(60000, 777), new List<int> { 14492 });   // that pid is not a running game
                stalls.Feed(RunsAgain, null);
                stalls.Feed(Stall(60000, 0), new List<int> { 1, 2 });      // no pid and two games: which one?
                stalls.Feed(RunsAgain, null);
                stalls.Feed(Stall(60000, 14492), new List<int>());         // the game is gone
                Assert.Empty(dumps.Calls);
                Assert.Contains("pid 777 is not a running game process; no dump", All(box));
                Assert.Contains("no single game process to dump", All(box));

                stalls.Feed(RunsAgain, null);
                dumps.Free = 3L * 1024 * 1024 * 1024;
                stalls.Feed(Stall(60000, 14492), new List<int> { 14492 });
                Assert.Empty(dumps.Calls);
                Assert.Contains("only 3.0 GB free", All(box));

                // A failed dump leaves no partial file and says why.
                stalls.Feed(RunsAgain, null);
                dumps.Free = -1;   // unknown: try
                dumps.Fail = true;
                stalls.Feed(Stall(60000, 14492), new List<int> { 14492 });
                Assert.Single(dumps.Calls);
                Assert.Empty(Directory.GetFiles(StallDumps.Folder(box.Env)));
                Assert.Contains("the dump of pid 14492 failed: Win32Exception: OpenProcess(14492) failed", All(box));
                Assert.Equal(0, stalls.Written);

                // A folder that already holds MaxKept dumps gets no more.
                stalls.Feed(RunsAgain, null);
                dumps.Fail = false;
                for (int i = 0; i < StallDumps.MaxKept; i++) File.WriteAllText(Path.Combine(StallDumps.Folder(box.Env), "old-" + i + ".dmp"), "x");
                stalls.Feed(Stall(60000, 14492), new List<int> { 14492 });
                Assert.Single(dumps.Calls);
                Assert.Contains("dumps are already in", All(box));

                // A kit environment without a dump writer says so instead of failing.
                box.Env.WriteDump = null;
                Directory.Delete(StallDumps.Folder(box.Env), true);
                stalls.Feed(RunsAgain, null);
                stalls.Feed(Stall(60000, 14492), new List<int> { 14492 });
                Assert.Contains("this kit cannot write dumps", All(box));
            }
        }

        [Fact]
        public void PollFollowsBootstrapLogAcrossTheHostsRotation()
        {
            using (var box = new KitSandbox())
            {
                var dumps = new Dumps();
                dumps.Attach(box.Env);
                var log = new KitLog(box.Temp, box.Out, box.Err, box.Env.Now);
                List<string> paths = StallDumps.BootstrapLogs(box.Env, box.Game);
                Assert.Equal(Path.Combine(box.Game, "McvsOffline", "logs", "bootstrap.log"), paths[0]);
                Assert.Equal(Path.Combine(box.Temp, "McvsOffline", "logs", "bootstrap.log"), paths[1]);
                string bootstrap = paths[0];
                Directory.CreateDirectory(Path.GetDirectoryName(bootstrap));
                // WR2's run 15: a 60 s STALL that was in the log before the watch started is not news.
                File.WriteAllText(bootstrap, Stall(60594, 14492) + "\n");
                var stalls = new StallDumps(box.Env, paths, log);
                var game = new List<int> { 14492 };
                stalls.Poll(null);
                stalls.Poll(game);
                Assert.Empty(dumps.Calls);

                // A line written in two parts is read once it ends.
                File.AppendAllText(bootstrap, RunsAgain + "\n" + Stall(60100, 14492).Substring(0, 40));
                stalls.Poll(game);
                Assert.Empty(dumps.Calls);
                File.AppendAllText(bootstrap, Stall(60100, 14492).Substring(40) + "\r\n");
                stalls.Poll(game);
                Assert.Single(dumps.Calls);

                // The next start renames bootstrap.log to bootstrap.log.1 and begins a new file, here already longer than the
                // old one was when last read: its first bytes differ, so it is read from its start.
                File.Move(bootstrap, bootstrap + ".1");
                File.WriteAllText(bootstrap, "2026-10-29T19:20:00.000Z INFO  [embedded] MCVS Offline 0.1.0 embedded host starting (platform windows, pid 2000)\n" +
                                             string.Concat(Enumerable.Repeat("2026-10-29T19:20:00.100Z INFO  [boot] +0.1 s a line of the start\n", 20)) +
                                             Stall(60000, 2000) + "\n");
                Assert.True(new FileInfo(bootstrap).Length > new FileInfo(bootstrap + ".1").Length);
                stalls.Poll(new List<int> { 2000 });
                Assert.Equal(2, dumps.Calls.Count);
                Assert.Equal(2000, dumps.Calls[1].Item1);

                // The %TEMP% fallback log appears later: read from its start.
                string temp = paths[1];
                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                File.WriteAllText(temp, Stall(5000, 3000) + "\n" + Stall(60000, 3000) + "\n");
                stalls.Poll(new List<int> { 3000 });
                Assert.Equal(3, dumps.Calls.Count);
            }
        }

        [Fact]
        public void LogFollowerKeepsAMultiByteCharacterSplitBetweenReads()
        {
            using (var box = new KitSandbox())
            {
                string file = Path.Combine(box.Temp, "split.log");
                var follower = new LogFollower(file);
                Assert.Empty(follower.ReadNew());   // absent: read it from its start once it exists
                byte[] text = Encoding.UTF8.GetBytes("Referência\n");
                int cut = Array.IndexOf(text, (byte)0xC3) + 1;   // inside "ê"
                using (var f = new FileStream(file, FileMode.Create)) f.Write(text, 0, cut);
                Assert.Empty(follower.ReadNew());
                using (var f = new FileStream(file, FileMode.Append)) f.Write(text, cut, text.Length - cut);
                Assert.Equal(new[] { "Referência" }, follower.ReadNew().ToArray());
            }
        }

        [Fact]
        public void CollectListsTheDumpsButNeverZipsThem()
        {
            using (var box = new KitSandbox())
            {
                string stalls = StallDumps.Folder(box.Env);
                Directory.CreateDirectory(stalls);
                File.WriteAllBytes(Path.Combine(stalls, "ModernCombatVersus-14492-20261102-180000-stall-60s.dmp"), new byte[2048]);
                string wer = Path.Combine(box.Env.LocalAppData, "CrashDumps");
                Directory.CreateDirectory(wer);
                File.WriteAllBytes(Path.Combine(wer, "ModernCombatVersus.exe.9876.dmp"), new byte[4096]);
                File.WriteAllBytes(Path.Combine(wer, "notepad.exe.1.dmp"), new byte[16]);

                string zipPath = Collect.Run(box.Env, box.Game, null, null, null);
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
                    string listing = new StreamReader(zip.GetEntry("dumps.txt").Open()).ReadToEnd();
                    Assert.Contains(Path.Combine(stalls, "ModernCombatVersus-14492-20261102-180000-stall-60s.dmp") + "\t2048 bytes\t", listing);
                    Assert.Contains(Path.Combine(wer, "ModernCombatVersus.exe.9876.dmp") + "\t4096 bytes\t", listing);
                    Assert.DoesNotContain("notepad", listing);
                    string summary = new StreamReader(zip.GetEntry("summary.txt").Open()).ReadToEnd();
                    Assert.Contains("dumps (not in this zip, send them separately): 2", summary);
                }
            }
        }

        /// <summary>The kit's dump imports (checked like PcPInvokeTests): exact exports, last error, handle types, WINAPI.</summary>
        [Fact]
        public void TheDumpImportsHaveTheirWin32Shapes()
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(typeof(MiniDump).Assembly.Location, new ReaderParameters { InMemory = true }))
            {
                TypeDefinition type = module.GetType("McvsOffline.PcKit.MiniDump");
                Dictionary<string, MethodDefinition> imports = type.Methods.Where(m => m.IsPInvokeImpl).ToDictionary(m => m.PInvokeInfo.EntryPoint);
                Assert.Equal(new[] { "MiniDumpWriteDump", "OpenProcess" }, imports.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
                MethodDefinition dump = imports["MiniDumpWriteDump"];
                Assert.Equal("dbghelp", dump.PInvokeInfo.Module.Name);
                MethodDefinition open = imports["OpenProcess"];
                Assert.Equal("kernel32", open.PInvokeInfo.Module.Name);
                foreach (MethodDefinition m in imports.Values)
                {
                    Assert.True(m.PInvokeInfo.IsNoMangle, m.Name + ": ExactSpelling");
                    Assert.True(m.PInvokeInfo.SupportsLastError, m.Name + ": SetLastError");
                    Assert.True(m.PInvokeInfo.IsCallConvWinapi, m.Name + ": WINAPI");
                    Assert.DoesNotContain(m.Parameters, p => p.ParameterType.FullName == "System.String");
                }
                Assert.Equal("Microsoft.Win32.SafeHandles.SafeProcessHandle (System.UInt32, System.Boolean, System.Int32)",
                             open.ReturnType.FullName + " (" + string.Join(", ", open.Parameters.Select(p => p.ParameterType.FullName)) + ")");
                Assert.Equal("System.Boolean (Microsoft.Win32.SafeHandles.SafeProcessHandle, System.Int32, Microsoft.Win32.SafeHandles.SafeFileHandle, System.UInt32, System.IntPtr, System.IntPtr, System.IntPtr)",
                             dump.ReturnType.FullName + " (" + string.Join(", ", dump.Parameters.Select(p => p.ParameterType.FullName)) + ")");
                // BOOL returns and the inherit flag are 4-byte Win32 BOOLs: no U1 marshalling may be declared.
                Assert.Null(dump.MethodReturnType.MarshalInfo);
                Assert.Null(open.Parameters[1].MarshalInfo);
            }
            Assert.Equal(0x1826u, MiniDump.FullDumpType);   // full memory, handles, unloaded modules, memory info, thread info
            Assert.Equal(0x0450u, MiniDump.DumpAccess);
            if (!OperatingSystem.IsWindows()) Assert.Throws<PlatformNotSupportedException>(() => MiniDump.Write(1, Path.Combine(Path.GetTempPath(), "never.dmp")));
        }
    }
}
