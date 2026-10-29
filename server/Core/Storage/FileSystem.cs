using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>
    /// The file operations durable storage needs and the game's trimmed mscorlib lacks: an atomic replace-by-rename,
    /// fsync of a file, and fsync of a directory. The shipped mscorlib has no File.Move, File.Replace,
    /// FileInfo.MoveTo or FileStream.Flush(bool), so the Android build calls libc directly (Android is Linux; the
    /// descriptor of a Mono FileStream on Unix is its SafeFileHandle value). The net8 build uses the BCL where it can.
    /// The PC build (MCVS_WINDOWS, .NET Framework) renames through <see cref="WindowsFiles"/> and has no libc import at
    /// all (docs/design/pc-port.md §3.1, §4.6): every write would fail on Windows otherwise.
    /// </summary>
    public static class FileSystem
    {
#if MCVS_ANDROID || !MCVS_EMBEDDED
        private const int O_RDONLY = 0;

        private static volatile bool directoryFlushUnavailable;
#endif

        /// <summary>
        /// False once a directory fsync failed to bind (no libc, not Unix); <see cref="FlushDirectory"/> is then a no-op.
        /// Always false in the PC DLL.
        /// </summary>
        public static bool DirectoryFlushAvailable
        {
#if MCVS_ANDROID || !MCVS_EMBEDDED
            get { return IsUnix && !directoryFlushUnavailable; }
#else
            get { return false; }
#endif
        }

        /// <summary>Renames <paramref name="source"/> to <paramref name="destination"/>, atomically replacing it.</summary>
        public static void Rename(string source, string destination)
        {
#if MCVS_ANDROID
            if (NativeMethods.rename(source, destination) != 0)
            {
                int errno = Marshal.GetLastWin32Error();
                throw new IOException("rename(" + source + ", " + destination + ") failed, errno " + errno);
            }
#elif MCVS_WINDOWS
            WindowsFiles.Rename(source, destination);
#else
            File.Move(source, destination, true);
#endif
        }

        /// <summary>
        /// Calls <paramref name="attempt"/> until it returns 0 (done), at most <paramref name="attempts"/> times, sleeping
        /// <paramref name="delayMs"/> ms between two calls. A non-zero result is a transient error worth another try (e.g. a
        /// Win32 sharing violation, <see cref="WindowsFiles"/>); a permanent failure is an exception from
        /// <paramref name="attempt"/>, which propagates at once, unchanged. Returns 0, or the last transient error when the
        /// attempts ran out.
        /// </summary>
        public static int Retry(Func<int> attempt, int attempts, int delayMs)
        {
            return Retry(attempt, attempts, delayMs, ThreadSleep);
        }

        private static readonly Action<int> ThreadSleep = Thread.Sleep;

        /// <summary><see cref="Retry(Func{int}, int, int)"/> with the pause made by <paramref name="sleep"/> (tests record it).</summary>
        internal static int Retry(Func<int> attempt, int attempts, int delayMs, Action<int> sleep)
        {
            if (attempt == null) throw new ArgumentNullException("attempt");
            int result = 0;
            for (int i = 0; i < attempts || i == 0; i++)
            {
                if (i > 0 && delayMs > 0) sleep(delayMs);
                result = attempt();
                if (result == 0) return 0;
            }
            return result;
        }

        /// <summary>Flushes <paramref name="stream"/> and asks the OS to commit it to storage (best effort).</summary>
        public static void FlushToDisk(FileStream stream)
        {
#if MCVS_ANDROID
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

        /// <summary>
        /// Commits the directory entries of <paramref name="directory"/> to storage (Unix, best effort). After
        /// <see cref="AtomicFile"/> renames a temp file over a profile, the new name is only durable once the directory
        /// itself is synced: a power loss right after a save could otherwise bring back the old file (or, for a new
        /// file, no file, which the game would read as "no profile" and reset).
        /// </summary>
        public static void FlushDirectory(string directory)
        {
#if MCVS_ANDROID || !MCVS_EMBEDDED
            if (string.IsNullOrEmpty(directory) || !DirectoryFlushAvailable) return;
            try
            {
                // O_RDONLY without O_DIRECTORY: Linux opens directories read-only, and O_DIRECTORY's value differs by
                // architecture (x86 vs ARM).
                int fd = NativeMethods.open(directory, O_RDONLY);
                if (fd < 0) return;
                try
                {
                    NativeMethods.fsync(fd);
                }
                finally
                {
                    NativeMethods.close(fd);
                }
            }
            catch (Exception)
            {
                // DllNotFoundException / EntryPointNotFoundException: no usable libc. Never fail a write over it.
                directoryFlushUnavailable = true;
            }
#else
            // PC: no directory fsync on Windows (NTFS journals the rename itself), and no libc import in the PC DLL.
#endif
        }

#if MCVS_ANDROID || !MCVS_EMBEDDED
        private static bool IsUnix
        {
            get
            {
                PlatformID platform = Environment.OSVersion.Platform;
                return platform == PlatformID.Unix || platform == PlatformID.MacOSX;
            }
        }
#endif

        private static class NativeMethods
        {
#if MCVS_ANDROID
            [DllImport("libc", SetLastError = true)]
            public static extern int rename(string oldpath, string newpath);
#endif

#if MCVS_ANDROID || !MCVS_EMBEDDED
            // The Android DLL and the standalone Linux server keep the directory fsync; the PC DLL has no libc ModuleRef.
            [DllImport("libc", SetLastError = true)]
            public static extern int fsync(int fd);

            [DllImport("libc", SetLastError = true)]
            public static extern int open(string path, int flags);

            [DllImport("libc", SetLastError = true)]
            public static extern int close(int fd);
#endif
        }
    }
}
