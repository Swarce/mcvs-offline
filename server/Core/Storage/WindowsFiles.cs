#if MCVS_WINDOWS || !MCVS_EMBEDDED
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>
    /// The Windows side of <see cref="FileSystem"/> for the PC build (docs/design/pc-port.md §4.6). Compiled into the PC
    /// DLL and the net8 build (so the tests run it with an injected native call), never into the Android DLL.
    /// </summary>
    public static class WindowsFiles
    {
        /// <summary>ERROR_ACCESS_DENIED: on a rename, typically a reader or a virus scanner holding the target.</summary>
        public const int ErrorAccessDenied = 5;

        /// <summary>ERROR_SHARING_VIOLATION: another process has the file open without FILE_SHARE_DELETE.</summary>
        public const int ErrorSharingViolation = 32;

        /// <summary>Calls of the native rename at most (the first and four retries).</summary>
        public const int RenameAttempts = 5;

        /// <summary>Pause between two attempts, in milliseconds.</summary>
        public const int RenameDelayMs = 20;

        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;

        /// <summary>
        /// Renames <paramref name="source"/> to <paramref name="destination"/>, replacing it when it exists:
        /// <c>MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)</c> (atomic on one NTFS volume, flushed
        /// before it returns). A sharing violation or an access denial (an open reader, an antivirus scan) is retried
        /// <see cref="RenameAttempts"/> times <see cref="RenameDelayMs"/> ms apart; then, or at once for any other error,
        /// an <see cref="IOException"/> naming the Win32 error.
        /// </summary>
        public static void Rename(string source, string destination)
        {
            Rename(source, destination, NativeMove);
        }

        /// <summary>
        /// <see cref="Rename(string, string)"/> over <paramref name="move"/>, which returns 0 on success or the Win32 error
        /// (tests inject it).
        /// </summary>
        public static void Rename(string source, string destination, Func<string, string, int> move)
        {
            if (move == null) throw new ArgumentNullException("move");
            int error = FileSystem.Retry(() =>
            {
                int code = move(source, destination);
                if (code == 0 || IsTransient(code)) return code;
                throw Failure(source, destination, code, 1);
            }, RenameAttempts, RenameDelayMs);
            if (error != 0) throw Failure(source, destination, error, RenameAttempts);
        }

        /// <summary>The errors a retry can cure: <see cref="ErrorSharingViolation"/> and <see cref="ErrorAccessDenied"/>.</summary>
        public static bool IsTransient(int win32Error)
        {
            return win32Error == ErrorSharingViolation || win32Error == ErrorAccessDenied;
        }

        /// <summary>A short name for the Win32 errors a rename meets ("sharing violation"), else null.</summary>
        public static string Describe(int win32Error)
        {
            switch (win32Error)
            {
                case 2: return "file not found";
                case 3: return "path not found";
                case ErrorAccessDenied: return "access denied";
                case ErrorSharingViolation: return "sharing violation";
                case 17: return "not the same device";
                case 112: return "disk full";
                case 183: return "already exists";
                default: return null;
            }
        }

        private static IOException Failure(string source, string destination, int win32Error, int attempts)
        {
            string name = Describe(win32Error);
            return new IOException("MoveFileExW(" + source + ", " + destination + ") failed: Win32 error " + win32Error
                                   + (name == null ? "" : " (" + name + ")")
                                   + (attempts > 1 ? " after " + attempts + " attempts" : ""));
        }

        private static int NativeMove(string source, string destination)
        {
            return NativeMethods.MoveFileExW(source, destination, MoveFileReplaceExisting | MoveFileWriteThrough)
                ? 0
                : Marshal.GetLastWin32Error();
        }

        private static class NativeMethods
        {
            [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);
        }
    }
}
#endif
