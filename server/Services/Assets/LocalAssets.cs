using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Server.Core;

namespace McvsOffline.Server.Services.Assets
{
    /// <summary>
    /// Iris assets provided locally: files under <c>&lt;data&gt;/assets/</c> named like the asset
    /// (<c>assets/tech_en.json</c>, <c>assets/GameOptions</c>, an archive made with tools/cdn-mirror/iris.py...). Gameloft's
    /// asset service is gone, and every use of an asset falls back to local data or a default image when it is
    /// missing (report 04 §11), so an empty folder is the normal case.
    /// </summary>
    public sealed class LocalAssets
    {
        private readonly string root;

        public LocalAssets(string directory)
        {
            root = Path.GetFullPath(directory);
        }

        public string Directory { get { return root; } }

        /// <summary>Full path of an existing asset, or null (also for names that try to leave the folder).</summary>
        public string Locate(string name)
        {
            if (!IsSafeName(name)) return null;
            string path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : null;
        }

        /// <summary>The listing <c>GET assets/{cid}</c> returns: <c>[{name, hash (sha256 hex), size}]</c>.</summary>
        public List<object> List()
        {
            var result = new List<object>();
            if (!System.IO.Directory.Exists(root)) return result;
            var files = new List<string>();
            Collect(root, files, 0);
            files.Sort(StringComparer.Ordinal);
            foreach (string file in files)
            {
                string name = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                if (!IsSafeName(name)) continue;
                result.Add(Metadata(name, file));
            }
            return result;
        }

        /// <summary><c>{name, hash, size}</c> of an asset file.</summary>
        public static Dictionary<string, object> Metadata(string name, string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var meta = new Dictionary<string, object>(StringComparer.Ordinal);
            meta["name"] = name;
            meta["hash"] = Sha256Hex(data);
            meta["size"] = (long)data.Length;
            return meta;
        }

        public static string Sha256Hex(byte[] data)
        {
#if MCVS_ANDROID
            // A direct constructor call: on Mono SHA256.Create() goes through a CryptoConfig name lookup, and the
            // trimmed BCL only keeps what is referenced statically. Not on PC: SHA256Managed throws under the FIPS
            // policy on .NET Framework, SHA256.Create() picks the CNG/CSP implementation.
            using (var sha = new SHA256Managed())
#else
            using (var sha = SHA256.Create())
#endif
            {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string ContentTypeFor(string name)
        {
            string ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".zip": return "application/zip";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        /// <summary>Relative names only: no "..", no rooted paths, no backslashes or control characters.</summary>
        public static bool IsSafeName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 512) return false;
            if (name.StartsWith("/", StringComparison.Ordinal) || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0) return false;
            foreach (string part in name.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..") return false;
            }
            foreach (char c in name)
            {
                if (c < 0x20) return false;
            }
            return true;
        }

        private static void Collect(string dir, List<string> files, int depth)
        {
            if (depth > 8) return;
            files.AddRange(System.IO.Directory.GetFiles(dir));
            foreach (string sub in System.IO.Directory.GetDirectories(dir)) Collect(sub, files, depth + 1);
        }
    }

    /// <summary>A parsed single "bytes=" range (RFC 7233), as Iris clients send for partial downloads.</summary>
    public struct ByteRange
    {
        public long Start;
        public long End; // inclusive

        /// <summary>
        /// Parses <paramref name="header"/> against a body of <paramref name="length"/> bytes. Returns false for an
        /// absent or unsupported header (send the whole body); sets <paramref name="satisfiable"/> to false when the
        /// header is valid but outside the body (416).
        /// </summary>
        public static bool TryParse(string header, long length, out ByteRange range, out bool satisfiable)
        {
            range = new ByteRange();
            satisfiable = true;
            if (string.IsNullOrEmpty(header)) return false;
            string h = header.Trim();
            if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            string spec = h.Substring(6).Trim();
            if (spec.IndexOf(',') >= 0) return false; // multi-range: answer the whole body
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;
            string a = spec.Substring(0, dash).Trim();
            string b = spec.Substring(dash + 1).Trim();
            long start, end;
            if (a.Length == 0)
            {
                long suffix;
                if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out suffix)) return false;
                if (suffix == 0 || length == 0) { satisfiable = false; return true; }
                start = Num.Max(0, length - suffix);
                end = length - 1;
            }
            else
            {
                if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
                if (b.Length == 0) end = length - 1;
                else if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out end)) return false;
                if (end < start) return false;
                if (start >= length) { satisfiable = false; return true; }
                end = Num.Min(end, length - 1);
            }
            range.Start = start;
            range.End = end;
            return true;
        }
    }
}
