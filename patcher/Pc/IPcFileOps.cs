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
        public void WriteAllBytes(string path, byte[] bytes)
        {
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllBytes(path, bytes);
        }

        public void Rename(string source, string destination)
        {
            System.IO.File.Move(source, destination, true);
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
