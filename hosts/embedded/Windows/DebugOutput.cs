#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Windows
{
    /// <summary>
    /// The Windows counterpart of logcat (docs/design/pc-port.md §6.3): <c>OutputDebugStringW</c>, which DebugView and the
    /// spike's <c>DebugOutputCapture</c> read. A GUI-subsystem process has no console. Usable without a Logger: the file sinks
    /// and the crash reporter report their own failures through it (<see cref="SelfLog"/>).
    /// </summary>
    public static class DebugOutput
    {
        public const string Tag = "McvsOffline";

        // Debuggers read OutputDebugString through a 4 KB shared buffer; longer strings are cut.
        internal const int ChunkChars = 2000;

        private static volatile bool broken;

        /// <summary>Where the text goes (tests replace it; never null).</summary>
        internal static Action<string> Writer = Win32Writer;

        /// <summary>SelfLog's writer: priority as in <see cref="SelfLog"/> (3 debug .. 7 fatal). Synchronous, never throws.</summary>
        public static void Write(int priority, string text)
        {
            WriteText(PriorityName(priority) + " " + text);
        }

        /// <summary>One "McvsOffline &lt;text&gt;" line, split into chunks the debugger takes whole. Never throws.</summary>
        public static void WriteText(string text)
        {
            if (text == null) return;
            try
            {
                for (int start = 0; start < text.Length; start += ChunkChars)
                {
                    int length = text.Length - start < ChunkChars ? text.Length - start : ChunkChars;
                    string part = start == 0 && length == text.Length ? text : text.Substring(start, length);
                    Writer(Tag + " " + part + "\n");
                }
            }
            catch (Exception)
            {
            }
        }

        private static void Win32Writer(string text)
        {
            if (broken) return;
            try
            {
                Win32.OutputDebugStringW(text);
            }
            catch (Exception)
            {
                // Not on Windows (the tests) or kernel32 refused: nothing else to write to.
                broken = true;
            }
        }

        internal static string PriorityName(int priority)
        {
            switch (priority)
            {
                case SelfLog.Debug: return "D";
                case SelfLog.Info: return "I";
                case SelfLog.Warn: return "W";
                case SelfLog.Error: return "E";
                case SelfLog.Fatal: return "F";
                default: return "V";
            }
        }
    }

    /// <summary>The system log sink of the PC host: "[category] message" lines through <see cref="DebugOutput"/>.</summary>
    public sealed class DebugOutputSink : LogSink
    {
        public DebugOutputSink(LogLevel minLevel) : base(minLevel) { }

        public override void Write(LogEntry entry)
        {
            if (entry == null) return;
            string text = "[" + (entry.Category ?? "-") + "] " + entry.Message;
            // The message is redacted by the logger; the exception text through the entry's redactor (design §5.4).
            if (entry.Exception != null) text += "\n" + entry.ExceptionText();
            DebugOutput.WriteText(text);
        }
    }
}
#endif
