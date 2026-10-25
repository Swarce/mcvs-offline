using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace McvsOffline.Server.Core.Storage
{
    /// <summary>
    /// Crash-safe whole-file writes: the data goes to a temporary file in the same directory, is flushed to disk,
    /// then renamed over the target. Readers therefore see either the old or the new content, never a partial file.
    /// That matters: a half-written profile reads as "missing", and the game answers a missing profile by resetting
    /// it (verify-C3 §3.1, §7.1). Callers that read-modify-write must also hold a <see cref="KeyedLock"/>.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>Suffix of temporary files; leftovers after a crash can be deleted with <see cref="DeleteLeftovers"/>.</summary>
        public const string TempSuffix = ".mcvs-tmp";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static long counter;

        public static void WriteAllBytes(string path, byte[] data)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (data == null) throw new ArgumentNullException("data");
            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = full + "." + Environment.TickCount.ToString("x", CultureInfo.InvariantCulture)
                          + Interlocked.Increment(ref counter).ToString(CultureInfo.InvariantCulture) + TempSuffix;
            try
            {
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    FileSystem.FlushToDisk(fs);
                }
                FileSystem.Rename(temp, full);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>Writes text as UTF-8 without BOM.</summary>
        public static void WriteAllText(string path, string text)
        {
            WriteAllBytes(path, Utf8NoBom.GetBytes(text ?? ""));
        }

        /// <summary>Reads a UTF-8 text file; returns null when it does not exist.</summary>
        public static string ReadAllTextOrNull(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            string text = Encoding.UTF8.GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text;
        }

        /// <summary>Deletes temporary files left in <paramref name="directory"/> by an interrupted write.</summary>
        public static int DeleteLeftovers(string directory)
        {
            if (!Directory.Exists(directory)) return 0;
            int deleted = 0;
            foreach (string file in Directory.GetFiles(directory, "*" + TempSuffix))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception)
                {
                }
            }
            return deleted;
        }
    }
}
