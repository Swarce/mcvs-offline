using System;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>
    /// The installer's write, rename and delete seam (docs/design/pc-port.md §7.1, §7.6): the real one is System.IO
    /// (<see cref="PcFileOps"/>); tests inject failures after any step and Windows semantics (a file open for writing cannot be
    /// deleted). Every member throws IOException / UnauthorizedAccessException on failure, as System.IO does.
    /// </summary>
    public interface IPcFileOps
    {
        /// <summary>Writes the whole file (creating its folder).</summary>
        void WriteAllBytes(string path, byte[] bytes);

        /// <summary>Renames source to destination, replacing an existing destination.</summary>
        void Rename(string source, string destination);

        /// <summary>Deletes a file; a missing file is no error.</summary>
        void DeleteFile(string path);

        /// <summary>Deletes a folder and everything in it; a missing folder is no error.</summary>
        void DeleteDirectory(string path);
    }

    /// <summary><see cref="IPcFileOps"/> over System.IO.</summary>
    public sealed class PcFileOps : IPcFileOps
    {
        /// <summary>Calls of the move at most (the first and nine retries).</summary>
        public const int RenameAttempts = 10;

        /// <summary>Pause between two attempts, in milliseconds (at most 0.9 s in all).</summary>
        public const int RenameDelayMs = 100;

        private const int ErrorSharingViolation = 32;
        private const int ErrorLockViolation = 33;

        private readonly Action<string, string> move;
        private readonly Action<int> sleep;
        private readonly bool windows;

        public PcFileOps()
            : this(null, null, OperatingSystem.IsWindows())
        {
        }

        /// <summary>
        /// Tests: <paramref name="move"/> instead of <c>File.Move(source, destination, true)</c>, <paramref name="sleep"/> instead
        /// of Thread.Sleep, and whether an access denial counts as transient (<paramref name="windows"/>; the default is the OS).
        /// </summary>
        public PcFileOps(Action<string, string> move, Action<int> sleep, bool windows)
        {
            this.move = move ?? ((s, d) => System.IO.File.Move(s, d, true));
            this.sleep = sleep ?? System.Threading.Thread.Sleep;
            this.windows = windows;
        }

        public void WriteAllBytes(string path, byte[] bytes)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(path, bytes);
        }

        /// <summary>
        /// The .mcvs-tmp → target move. A target held open for a moment (a virus scanner reading a freshly written DLL, the
        /// Explorer preview) fails with a sharing or lock violation, or on Windows an access denial: retried, at most
        /// <see cref="RenameAttempts"/> calls <see cref="RenameDelayMs"/> ms apart (as the server's FileSystem.Retry does for its
        /// own renames), then the last exception propagates unchanged. Any other failure propagates at once.
        /// </summary>
        public void Rename(string source, string destination)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    move(source, destination);
                    return;
                }
                catch (Exception ex) when (attempt < RenameAttempts && IsTransient(ex, windows))
                {
                    sleep(RenameDelayMs);
                }
            }
        }

        /// <summary>
        /// A failure a retry can cure: an IOException for ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION (HResult
        /// 0x80070020 / 0x80070021), or, when <paramref name="windows"/>, an UnauthorizedAccessException (ERROR_ACCESS_DENIED,
        /// what a move reports for a target a scanner holds; elsewhere it is a permission problem a retry does not cure).
        /// </summary>
        public static bool IsTransient(Exception ex, bool windows)
        {
            if (ex is UnauthorizedAccessException) return windows;
            if (!(ex is System.IO.IOException)) return false;
            return ex.HResult == unchecked((int)0x80070000) + ErrorSharingViolation || ex.HResult == unchecked((int)0x80070000) + ErrorLockViolation;
        }

        public void DeleteFile(string path)
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }

        public void DeleteDirectory(string path)
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
        }
    }
}
