using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// An out-of-process minidump of the running game (WR2 F4; <see cref="StallDumps"/>): dbghelp's MiniDumpWriteDump with full
    /// memory, handles, unloaded modules, the memory map and thread info. The kit is 64-bit and the game a 32-bit (WOW64)
    /// process: the dump holds both views (WinDbg: <c>.effmach x86</c> or <c>!wow64exts.sw</c> for the game's own stacks).
    /// The process is opened with the rights MiniDumpWriteDump needs and no more. Windows only; throws on failure.
    /// </summary>
    public static class MiniDump
    {
        /// <summary>MiniDumpWithFullMemory | WithHandleData | WithUnloadedModules | WithFullMemoryInfo | WithThreadInfo.</summary>
        public const uint FullDumpType = 0x00000002 | 0x00000004 | 0x00000020 | 0x00000800 | 0x00001000;

        /// <summary>PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_DUP_HANDLE (the handle data).</summary>
        public const uint DumpAccess = 0x0400 | 0x0010 | 0x0040;

        [DllImport("kernel32", EntryPoint = "OpenProcess", ExactSpelling = true, SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("dbghelp", EntryPoint = "MiniDumpWriteDump", ExactSpelling = true, SetLastError = true)]
        private static extern bool MiniDumpWriteDump(SafeProcessHandle process, int processId, SafeFileHandle file, uint dumpType,
                                                     IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

        /// <summary>Writes the dump of <paramref name="pid"/> to <paramref name="path"/> (created new); returns its size in bytes.</summary>
        public static long Write(int pid, string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("a minidump needs Windows");
            using (SafeProcessHandle process = OpenProcess(DumpAccess, false, pid))
            {
                if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(" + pid + ") failed");
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    if (!MiniDumpWriteDump(process, pid, file.SafeFileHandle, FullDumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();   // an HRESULT for MiniDumpWriteDump
                        throw new Win32Exception(error, "MiniDumpWriteDump failed (0x" + error.ToString("X8") + ")");
                    }
                    file.Flush(true);
                    return file.Length;
                }
            }
        }

        /// <summary>Free bytes on the drive of <paramref name="folder"/>, or -1 when unknown.</summary>
        public static long FreeBytes(string folder)
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return -1;
            }
        }
    }
}
