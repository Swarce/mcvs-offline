using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xunit;

namespace McvsOffline.PcSpike.Tests
{
    /// <summary>
    /// How 'start' and 'telemetry' stop (Ctrl+C, the window's close button, shutdown), the one console reader shared by the
    /// notes, the menu and 'collect', the menu's game-folder prompt, and 'collect' reading a CSV that is still being written.
    /// </summary>
    public class ConsoleAndStopTests
    {
        [Fact]
        public void ClosingTheWindowIsSighupOnWindowsAndIsRegistered()
        {
            // .NET maps CTRL_CLOSE_EVENT to SIGHUP and CTRL_SHUTDOWN_EVENT to SIGTERM (dotnet/runtime#55333).
            Assert.Contains(PosixSignal.SIGHUP, StopSignal.CloseSignals);
            Assert.Contains(PosixSignal.SIGTERM, StopSignal.CloseSignals);
            Assert.True(StopSignal.DefaultCloseGrace < TimeSpan.FromSeconds(5), "Windows ends the process 5 s after CTRL_CLOSE_EVENT");
        }

        [Fact]
        public void TheCloseHandlerHoldsUntilTheCommandHasStopped()
        {
            var stop = new StopSignal(false, TimeSpan.FromSeconds(30));
            var context = new PosixSignalContext(PosixSignal.SIGHUP);
            var handler = new Thread(() => stop.OnSignal(context));
            handler.Start();
            Assert.True(stop.Wait(10000));
            Assert.True(stop.IsRequested);
            Assert.True(stop.Closing);
            Assert.True(context.Cancel);
            Assert.False(handler.Join(300), "the handler returned before the command disposed its server and logs");
            stop.Dispose();
            Assert.True(handler.Join(10000));
        }

        [Fact]
        public void TheCloseHandlerGivesUpAfterTheGrace()
        {
            var stop = new StopSignal(false, TimeSpan.FromMilliseconds(100));
            var handler = new Thread(() => stop.OnSignal(new PosixSignalContext(PosixSignal.SIGTERM)));
            handler.Start();
            Assert.True(handler.Join(10000));
            Assert.True(stop.Closing);
            stop.Dispose();
        }

        [Fact]
        public void ARealSighupRequestsTheStopInsteadOfEndingTheProcess()
        {
            if (!OperatingSystem.IsLinux()) return;
            using (var stop = new StopSignal())
            {
                Assert.Equal(0, kill(Environment.ProcessId, 1 /* SIGHUP */));
                Assert.True(stop.Wait(10000));
                Assert.True(stop.Closing);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [Fact]
        public void AStoppedNotesReaderLeavesTheNextLineToTheMenu()
        {
            var input = new QueueReader();
            var lines = new ConsoleLines(input);
            var notes = new List<string>();
            var noted = new ManualResetEventSlim(false);
            var stop = new CancellationTokenSource();
            var thread = new Thread(() =>
            {
                string line;
                while (lines.TryReadLine(stop.Token, out line))
                {
                    lock (notes) notes.Add(line);
                    noted.Set();
                }
            });
            thread.Start();
            input.Push("match start");
            Assert.True(noted.Wait(10000));
            stop.Cancel();
            Assert.True(thread.Join(10000));

            input.Push("2");
            Assert.Equal("2", lines.ReadLine());
            Assert.Equal(new[] { "match start" }, notes);

            input.Push("stale");
            Thread.Sleep(200);
            lines.DiscardPending();
            input.Push("6");
            Assert.Equal("6", lines.ReadLine());
            input.End();
            Assert.Null(lines.ReadLine());
        }

        [Fact]
        public void AnInterruptedReadIsNotTheEndOfAnInteractiveConsole()
        {
            // On Windows, Ctrl+C makes a pending Console.ReadLine return null; the menu must still read the next choice.
            var interactive = new ConsoleLines(new ScriptedReader(null, "1"), false);
            Assert.Equal("1", interactive.ReadLine());
            var redirected = new ConsoleLines(new ScriptedReader(null, "1"));
            Assert.Null(redirected.ReadLine());
        }

        [Fact]
        public void TheMenuAsksForTheGameFolderAndTakesAPastedPathOrTheExe()
        {
            string root = Support.NewTempDir("menu");
            try
            {
                string game = Path.Combine(root, "Modern Combat Versus");
                Directory.CreateDirectory(game);
                File.WriteAllBytes(Path.Combine(game, SteamLocator.ExeName), new byte[] { 0x4d, 0x5a });
                var options = SpikeOptions.Parse(new string[0]);
                string before = options.GameDir;

                var output = new StringWriter();
                Assert.False(Program.AskGameFolder(options, "no Steam", new ConsoleLines(new StringReader("\n")), output));
                Assert.Equal(before, options.GameDir);
                Assert.Contains("The game was not found: no Steam.", output.ToString());

                output = new StringWriter();
                string typed = Path.Combine(root, "elsewhere") + "\n\"" + Path.Combine(game, SteamLocator.ExeName) + "\"\n";
                Assert.True(Program.AskGameFolder(options, null, new ConsoleLines(new StringReader(typed)), output));
                Assert.Equal(game, options.GameDir);
                Assert.Contains("no " + SteamLocator.ExeName + " in " + Path.Combine(root, "elsewhere"), output.ToString());

                Assert.False(Program.AskGameFolder(options, null, new ConsoleLines(new StringReader("")), new StringWriter()));
                Assert.Equal(game, options.GameDir);
            }
            finally
            {
                Support.DeleteDir(root);
            }
        }

        [Fact]
        public void PeaksReadsACsvTheSamplerStillHasOpen()
        {
            string dir = Support.NewTempDir("peaks");
            try
            {
                string csv = Path.Combine(dir, "telemetry-20261028-201500.csv");
                // As TelemetrySampler.Run: new StreamWriter(path, false, ...) = FileAccess.Write, FileShare.Read, kept open.
                using (var writer = new StreamWriter(csv, false, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    writer.WriteLine(TelemetryCsv.Header);
                    writer.WriteLine("2026-10-28 20:15:35,17,4242,1600.0,1700.0,,,,,1,1,");
                    double privateMb, virtualMb;
                    int rows;
                    Assert.True(TelemetryCsv.Peaks(csv, out privateMb, out virtualMb, out rows));
                    Assert.Equal(1, rows);
                    Assert.Equal(1700.0, virtualMb);
                }
            }
            finally
            {
                Support.DeleteDir(dir);
            }
        }

        [Fact]
        public void AnUnreadableCsvIsReportedNotThrown()
        {
            string dir = Support.NewTempDir("peaks-bad");
            try
            {
                double privateMb, virtualMb;
                int rows;
                Assert.False(TelemetryCsv.Peaks(dir, out privateMb, out virtualMb, out rows));
                Assert.Equal(-1, rows);
                Assert.False(TelemetryCsv.Peaks(Path.Combine(dir, "missing.csv"), out privateMb, out virtualMb, out rows));
                Assert.Equal(-1, rows);
            }
            finally
            {
                Support.DeleteDir(dir);
            }
        }

        /// <summary>Returns the given lines (null too) in order, then blocks like a console nobody types into.</summary>
        private sealed class ScriptedReader : TextReader
        {
            private readonly Queue<string> lines;

            public ScriptedReader(params string[] lines)
            {
                this.lines = new Queue<string>(lines);
            }

            public override string ReadLine()
            {
                lock (lines)
                {
                    if (lines.Count > 0) return lines.Dequeue();
                }
                Thread.Sleep(Timeout.Infinite);
                return null;
            }
        }

        /// <summary>A console stand-in: ReadLine blocks until a line is pushed; null after <see cref="End"/>.</summary>
        private sealed class QueueReader : TextReader
        {
            private readonly BlockingCollection<string> queue = new BlockingCollection<string>();

            public void Push(string line) => queue.Add(line);

            public void End() => queue.CompleteAdding();

            public override string ReadLine()
            {
                string line;
                return queue.TryTake(out line, Timeout.Infinite) ? line : null;
            }
        }
    }
}
