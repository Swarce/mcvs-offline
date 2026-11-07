using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>
    /// File access of the admin modules (design §4.2 "Identifiers", §5): every path is built from a fixed folder under
    /// the data directory plus a name the caller validated, is confined to that folder, never goes through a symbolic
    /// link, and every read from a folder a person can write to (import/, logs/) has a size limit.
    /// <para>
    /// Only APIs of the game's trimmed BCL: File.Copy, File.Move and File.WriteAllBytes are not used.
    /// </para>
    /// </summary>
    internal static class AdminFiles
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// &lt;data&gt;/&lt;relative&gt; ("admin/uploads", "import"...), created when missing. Throws IOException when the
        /// folder or one of its parents below the data directory is a symbolic link (a planted link could point anywhere).
        /// </summary>
        public static string Folder(ServerContext context, string relative)
        {
            string root = Path.GetFullPath(context.DataDir);
            string current = root;
            foreach (string part in relative.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..") throw new ArgumentException("bad folder '" + relative + "'", "relative");
                current = Path.Combine(current, part);
                if (IsLink(current)) throw new IOException("refusing to use " + current + ": it is a symbolic link");
                if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            }
            return current;
        }

        /// <summary>
        /// True when the path exists and is a symbolic link (or another reparse point). On Unix the attributes come from
        /// lstat, so a link has ReparsePoint (the trimmed BCL has no File.GetAttributes: FileSystemInfo.Attributes it is).
        /// </summary>
        public static bool IsLink(string path)
        {
            try
            {
                if (File.Exists(path)) return (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
                if (Directory.Exists(path)) return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
                return false;
            }
            catch (Exception)
            {
                return true; // cannot tell: treat as unsafe
            }
        }

        /// <summary>A regular file (no folder, no link).</summary>
        public static bool IsPlainFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                return (new FileInfo(path).Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary><paramref name="path"/> is directly inside <paramref name="folder"/> (after normalization).</summary>
        public static bool IsDirectlyInside(string folder, string path)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path));
            return parent != null && string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar),
                                                   Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal);
        }

        /// <summary>
        /// How the readers open files: <c>FileShare.ReadWrite | FileShare.Delete</c>, so the logger can keep writing and,
        /// on Windows, rotate (rename) a log while the page reads its tail (docs/design/pc-port.md §4.6).
        /// </summary>
        public const FileShare ReaderShare = FileShare.ReadWrite | FileShare.Delete;

        /// <summary>
        /// The plain file <paramref name="name"/> directly inside <paramref name="folder"/>, or null when the name is not a
        /// plain file name (also a Windows reserved name, <see cref="IsWindowsReservedName"/>), the file does not exist,
        /// is a link or a folder, or resolves outside the folder. Names are compared ignoring case on every platform, as
        /// NTFS does (docs/design/pc-port.md §4.6): without an exact match, the one file whose name equals
        /// <paramref name="name"/> ignoring case is the file (several such files on a case-sensitive disk: none is).
        /// </summary>
        public static string FileIn(string folder, string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOf('\0') >= 0 || IsWindowsReservedName(name))
                return null;
            string path = Path.Combine(folder, name);
            if (!IsDirectlyInside(folder, path)) return null;
            if (IsPlainFile(path)) return path;
            if (File.Exists(path) || Directory.Exists(path)) return null; // a link or a folder under the exact name
            string match = null;
            foreach (FileInfo f in PlainFiles(folder))
            {
                if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (match != null) return null;
                match = f.FullName;
            }
            return match != null && IsDirectlyInside(folder, match) ? match : null;
        }

        /// <summary>
        /// The file of <paramref name="files"/> named <paramref name="name"/>: the exact name, else the only one whose name
        /// equals it ignoring case (NTFS folds case, so on PC both spellings are one file; docs/design/pc-port.md §4.6).
        /// Null when there is none, or several that differ only in case and none matches exactly.
        /// </summary>
        public static FileInfo FindByName(IEnumerable<FileInfo> files, string name)
        {
            if (files == null || string.IsNullOrEmpty(name)) return null;
            FileInfo folded = null;
            int foldedCount = 0;
            foreach (FileInfo f in files)
            {
                if (string.Equals(f.Name, name, StringComparison.Ordinal)) return f;
                if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                folded = f;
                foldedCount++;
            }
            return foldedCount == 1 ? folded : null;
        }

        /// <summary>
        /// True for a name Windows cannot use for a file in any folder: a DOS device name (<c>CON</c>, <c>PRN</c>,
        /// <c>AUX</c>, <c>NUL</c>, <c>COM1</c>-<c>COM9</c>, <c>LPT1</c>-<c>LPT9</c>, any case, with or without an
        /// extension: <c>nul.txt</c> is the device too), or a name ending in '.' or a space (Windows strips them, so
        /// the file would get another name). Refused on every platform, so a name that works on Android works on PC.
        /// </summary>
        public static bool IsWindowsReservedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            char last = name[name.Length - 1];
            if (last == '.' || last == ' ') return true;
            int dot = name.IndexOf('.');
            string stem = (dot < 0 ? name : name.Substring(0, dot)).TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                   && stem[3] >= '1' && stem[3] <= '9';
        }

        /// <summary>The plain files of a folder (no links, no folders), or an empty list.</summary>
        public static List<FileInfo> PlainFiles(string folder)
        {
            var result = new List<FileInfo>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || IsLink(folder)) return result;
            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception)
            {
                return result;
            }
            foreach (string file in files)
            {
                if (!IsPlainFile(file)) continue;
                try
                {
                    result.Add(new FileInfo(file));
                }
                catch (Exception)
                {
                    // vanished or unreadable: not listed
                }
            }
            return result;
        }

        /// <summary>The whole file when it has at most <paramref name="maxBytes"/> bytes, else null (also when it grows while read).</summary>
        public static byte[] ReadAtMost(string path, long maxBytes)
        {
            long length = new FileInfo(path).Length;
            if (length > maxBytes) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ReaderShare))
            {
                var buffer = new byte[(int)length];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < buffer.Length)
                {
                    var shorter = new byte[read];
                    Buffer.BlockCopy(buffer, 0, shorter, 0, read);
                    return shorter;
                }
                if (stream.ReadByte() >= 0) return null; // it grew past what was checked
                return buffer;
            }
        }

        /// <summary>
        /// The last <paramref name="maxBytes"/> bytes of a file that may be written to at the same time (<see cref="ReaderShare"/>).
        /// <paramref name="size"/> is the file's length when it was opened.
        /// </summary>
        public static byte[] ReadTail(string path, long maxBytes, out long size)
        {
            size = new FileInfo(path).Length;
            long start = size > maxBytes ? size - maxBytes : 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ReaderShare))
            {
                if (start > 0) stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[(int)(size - start)];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == buffer.Length) return buffer;
                var shorter = new byte[read];
                Buffer.BlockCopy(buffer, 0, shorter, 0, read);
                return shorter;
            }
        }

        /// <summary>The first <paramref name="maxBytes"/> bytes of a file (or all of a smaller one).</summary>
        public static byte[] ReadHead(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ReaderShare))
            {
                var buffer = new byte[maxBytes];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                var result = new byte[read];
                Buffer.BlockCopy(buffer, 0, result, 0, read);
                return result;
            }
        }

        /// <summary>UTF-8 text (invalid bytes become U+FFFD), without a BOM.</summary>
        public static string Utf8(byte[] bytes, int offset, int count)
        {
            string text = Encoding.UTF8.GetString(bytes, offset, count);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            return text;
        }

        public static byte[] Utf8Bytes(string text)
        {
            return Utf8NoBom.GetBytes(text ?? "");
        }

        /// <summary>Creates a new file with exactly these bytes; false (nothing written) when it already exists.</summary>
        public static bool TryCreateNew(string path, byte[] data)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                if (File.Exists(path)) return false;
                throw;
            }
            using (stream)
            {
                stream.Write(data, 0, data.Length);
                McvsOffline.Server.Core.Storage.FileSystem.FlushToDisk(stream);
            }
            return true;
        }

        /// <summary>
        /// Deletes the files of <paramref name="folder"/> that <paramref name="match"/> selects beyond the newest
        /// <paramref name="keep"/>, newest by the time they were written (then by name), never <paramref name="spare"/> (the
        /// file just written: names need not sort by time, e.g. an export's starts with the alias). The spared file counts
        /// as one of the kept.
        /// </summary>
        public static void KeepNewest(string folder, Func<string, bool> match, int keep, string spare = null)
        {
            var files = new List<KeyValuePair<string, DateTime>>();
            foreach (FileInfo f in PlainFiles(folder))
            {
                if (!match(f.Name)) continue;
                DateTime written;
                try
                {
                    written = f.LastWriteTimeUtc;
                }
                catch (Exception)
                {
                    written = DateTime.MinValue;   // unreadable: oldest
                }
                files.Add(new KeyValuePair<string, DateTime>(f.Name, written));
            }
            files.Sort((a, b) =>
            {
                bool aSpare = a.Key == spare, bSpare = b.Key == spare;
                if (aSpare != bSpare) return aSpare ? -1 : 1;
                int byTime = b.Value.CompareTo(a.Value);
                return byTime != 0 ? byTime : string.CompareOrdinal(b.Key, a.Key);
            });
            for (int i = keep < 0 ? 0 : keep; i < files.Count; i++)
            {
                if (files[i].Key == spare) continue;
                DeleteQuietly(Path.Combine(folder, files[i].Key));
            }
        }

        public static void DeleteQuietly(string path)
        {
            try
            {
                if (IsPlainFile(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // best effort: a leftover only costs space and is pruned later
            }
        }

        /// <summary>
        /// Deletes a folder this module created (its plain files, then the folder itself; nothing is followed through a
        /// link, sub-folders are left alone).
        /// </summary>
        public static void DeleteFolderQuietly(string folder)
        {
            try
            {
                if (!Directory.Exists(folder) || IsLink(folder)) return;
                foreach (FileInfo f in PlainFiles(folder)) DeleteQuietly(f.FullName);
                Directory.Delete(folder, false);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        /// <summary>Only [A-Za-z0-9_-] of a name for files and Content-Disposition (at most 40 characters).</summary>
        public static string SafeName(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? "")
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-') sb.Append(c);
                if (sb.Length >= 40) break;
            }
            return sb.Length == 0 ? "player" : sb.ToString();
        }

        /// <summary>True when every character of <paramref name="s"/> is one of [0-9a-f] and it has <paramref name="length"/> of them.</summary>
        public static bool IsLowerHex(string s, int length)
        {
            if (s == null || s.Length != length) return false;
            foreach (char c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
