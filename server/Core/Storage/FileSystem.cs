using System;
using System.IO;
#if MCVS_EMBEDDED
using System.Runtime.InteropServices;
#endif

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>
    /// The two file operations durable storage needs and the game's trimmed mscorlib lacks: an atomic
    /// replace-by-rename and fsync. The shipped mscorlib has no File.Move, File.Replace, FileInfo.MoveTo or
    /// FileStream.Flush(bool), so the embedded build calls libc directly (Android is Linux; the descriptor of a
    /// Mono FileStream on Unix is its SafeFileHandle value). The net8 build uses the BCL.
    /// </summary>
    public static class FileSystem
    {
        /// <summary>Renames <paramref name="source"/> to <paramref name="destination"/>, atomically replacing it.</summary>
        public static void Rename(string source, string destination)
        {
#if MCVS_EMBEDDED
            if (NativeMethods.rename(source, destination) != 0)
            {
                int errno = Marshal.GetLastWin32Error();
                throw new IOException("rename(" + source + ", " + destination + ") failed, errno " + errno);
            }
#else
            File.Move(source, destination, true);
#endif
        }

        /// <summary>Flushes <paramref name="stream"/> and asks the OS to commit it to storage (best effort).</summary>
        public static void FlushToDisk(FileStream stream)
        {
#if MCVS_EMBEDDED
            stream.Flush();
            try
            {
                int fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                NativeMethods.fsync(fd);
            }
            catch (Exception)
            {
                // fsync is an extra safety net; a failure here must not fail the write itself.
            }
#else
            stream.Flush(true);
#endif
        }

#if MCVS_EMBEDDED
        private static class NativeMethods
        {
            [DllImport("libc", SetLastError = true)]
            public static extern int rename(string oldpath, string newpath);

            [DllImport("libc", SetLastError = true)]
            public static extern int fsync(int fd);
        }
#endif
    }
}
