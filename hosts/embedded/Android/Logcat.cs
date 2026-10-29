// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_ANDROID), never into the net8 build.
#if MCVS_ANDROID
using System;
using System.Runtime.InteropServices;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// The one place that writes to logcat directly (tag "McvsOffline", liblog), usable without a Logger: the file sinks
    /// and the crash reporter report their own failures through it (<see cref="McvsOffline.Server.Core.SelfLog"/>), and the
    /// crash reporter writes its report here first, synchronously, on the dying thread.
    /// </summary>
    internal static class Logcat
    {
        public const string Tag = "McvsOffline";

        // logd drops the end of entries above about 4 KB.
        private const int ChunkChars = 3000;

        private static volatile bool broken;

        /// <summary>Android priority (3 debug, 4 info, 5 warn, 6 error, 7 fatal). Synchronous, never throws, splits long text.</summary>
        public static void Write(int priority, string text)
        {
            if (text == null) return;
            try
            {
                for (int start = 0; start < text.Length; start += ChunkChars)
                {
                    int length = text.Length - start < ChunkChars ? text.Length - start : ChunkChars;
                    string part = start == 0 && length == text.Length ? text : text.Substring(start, length);
                    if (!broken)
                    {
                        try
                        {
                            NativeMethods.__android_log_write(priority, Tag, part);
                            continue;
                        }
                        catch (Exception)
                        {
                            broken = true;
                        }
                    }
                    Console.WriteLine(Tag + " " + part);
                }
            }
            catch (Exception)
            {
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
