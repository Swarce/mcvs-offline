#if MCVS_WINDOWS || !MCVS_EMBEDDED
using System.IO;

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>
    /// The Windows side of <see cref="FileSystem"/> for the PC build (docs/design/pc-port.md §4.6). Compiled into the PC
    /// DLL and the net8 build (so the tests run it), never into the Android DLL.
    /// </summary>
    public static class WindowsFiles
    {
        /// <summary>
        /// Renames <paramref name="source"/> to <paramref name="destination"/>, replacing it when it exists.
        /// Day-0 stand-in (the net452 trial's): <c>File.Replace</c> when the target exists, else <c>File.Move</c>.
        /// WP1 replaces it with <c>MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)</c> and the retry on
        /// sharing violations (§4.6).
        /// </summary>
        public static void Rename(string source, string destination)
        {
            if (File.Exists(destination)) File.Replace(source, destination, null, true);
            else File.Move(source, destination);
        }
    }
}
#endif
