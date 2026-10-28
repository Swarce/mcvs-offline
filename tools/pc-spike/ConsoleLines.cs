using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The console's input lines, read by one thread for the whole program and handed to whoever asks: the notes of 'start'
    /// and 'telemetry', the menu and the questions of 'collect'. With separate Console.ReadLine calls, the notes thread
    /// of a stopped 'start' would still be waiting in ReadLine and swallow the next menu choice.
    /// <para>
    /// On an interactive Windows console, ReadLine returns null when Ctrl+C interrupts it; that is not the end of the
    /// input, so only redirected input ends at null (<see cref="Stdin"/>).
    /// </para>
    /// </summary>
    public sealed class ConsoleLines
    {
        private static readonly Lazy<ConsoleLines> stdin =
            new Lazy<ConsoleLines>(() => new ConsoleLines(Console.In, Console.IsInputRedirected));

        private readonly TextReader reader;
        private readonly bool endAtNull;
        private readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        private readonly object gate = new object();
        private bool started;

        public ConsoleLines(TextReader reader, bool endAtNull = true)
        {
            this.reader = reader;
            this.endAtNull = endAtNull;
        }

        public static ConsoleLines Stdin => stdin.Value;

        /// <summary>The next line; null at the end of the input.</summary>
        public string ReadLine()
        {
            string line;
            return TryReadLine(CancellationToken.None, out line) ? line : null;
        }

        /// <summary>The next line; false at the end of the input or once <paramref name="token"/> is cancelled.</summary>
        public bool TryReadLine(CancellationToken token, out string line)
        {
            Start();
            try
            {
                return lines.TryTake(out line, Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                line = null;
                return false;
            }
        }

        /// <summary>Drops lines typed while nobody was asking (between the end of 'start' and the menu).</summary>
        public void DiscardPending()
        {
            string ignored;
            while (lines.TryTake(out ignored))
            {
            }
        }

        private void Start()
        {
            lock (gate)
            {
                if (started) return;
                started = true;
            }
            new Thread(Pump) { IsBackground = true, Name = "console-input" }.Start();
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    string line = reader.ReadLine();
                    if (line != null)
                    {
                        lines.Add(line);
                        continue;
                    }
                    if (endAtNull) break;
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
            finally
            {
                lines.CompleteAdding();
            }
        }
    }
}
