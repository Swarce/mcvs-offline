// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.Runtime.InteropServices;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// Writes to logcat under the tag "McvsOffline" (<c>adb logcat -s McvsOffline</c>) through liblog, a public NDK
    /// library present on every device. The trimmed Mono.Android has no Android.Util.Log binding. If liblog cannot be
    /// called, the sink falls back to Console, which Xamarin forwards to logcat as "mono-stdout".
    /// </summary>
    internal sealed class LogcatSink : LogSink
    {
        public const string Tag = "McvsOffline";

        // logcat drops the end of very long lines (about 4 KB); long messages (full requests) are split.
        private const int ChunkChars = 3000;

        private volatile bool useConsole;

        public LogcatSink(LogLevel minLevel) : base(minLevel) { }

        public override void Write(LogEntry entry)
        {
            string text = "[" + (entry.Category ?? "-") + "] " + entry.Message;
            // The message is redacted by the logger; the exception text through the entry's redactor (design §5.4).
            if (entry.Exception != null) text += "\n" + entry.ExceptionText();
            int priority = Priority(entry.Level);
            if (text.Length <= ChunkChars)
            {
                WriteLine(priority, text);
                return;
            }
            for (int start = 0; start < text.Length; start += ChunkChars)
            {
                int length = text.Length - start < ChunkChars ? text.Length - start : ChunkChars;
                WriteLine(priority, text.Substring(start, length));
            }
        }

        private void WriteLine(int priority, string line)
        {
            if (!useConsole)
            {
                try
                {
                    NativeMethods.__android_log_write(priority, Tag, line);
                    return;
                }
                catch (Exception)
                {
                    useConsole = true;
                }
            }
            try
            {
                Console.WriteLine(Tag + " " + line);
            }
            catch (Exception)
            {
            }
        }

        private static int Priority(LogLevel level)
        {
            // android/log.h: VERBOSE 2, DEBUG 3, INFO 4, WARN 5, ERROR 6
            switch (level)
            {
                case LogLevel.Trace: return 2;
                case LogLevel.Debug: return 3;
                case LogLevel.Info: return 4;
                case LogLevel.Warn: return 5;
                default: return 6;
            }
        }

        private static class NativeMethods
        {
            [DllImport("liblog.so")]
            public static extern int __android_log_write(int priority, string tag, string text);
        }
    }
}
#endif
