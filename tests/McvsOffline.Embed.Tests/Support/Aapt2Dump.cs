using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Xunit;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>The real aapt2 (Android build-tools) as an independent reader of the manifests the patcher writes.</summary>
    internal static class Aapt2Dump
    {
        /// <summary>A zip that holds the manifest (and resources.arsc when given), enough for aapt2's dump commands.</summary>
        public static string MinimalApk(byte[] manifest, byte[] resources = null)
        {
            string path = Path.Combine(TestEnvironment.NewTempDir("aapt2"), "m.apk");
            var entries = new List<ZipEntry> { ZipEntry.CreateDeflated(ManifestPatch.EntryName, manifest, null) };
            if (resources != null) entries.Add(ZipEntry.CreateStored("resources.arsc", resources, null));
            File.WriteAllBytes(path, ZipArchiveWriter.Write(entries));
            return path;
        }

        /// <summary><c>aapt2 dump xmltree --file AndroidManifest.xml</c>, without aapt2's "warn:" lines.</summary>
        public static string XmlTree(string apk)
        {
            return Run("dump", "xmltree", "--file", ManifestPatch.EntryName, apk);
        }

        public static string XmlTree(byte[] manifest)
        {
            return XmlTree(MinimalApk(manifest));
        }

        /// <summary><c>aapt2 dump xmltree --file <paramref name="entry"/></c> of a compiled res/ XML file (in a zip with a stub manifest).</summary>
        public static string XmlTree(byte[] document, string entry)
        {
            string path = Path.Combine(TestEnvironment.NewTempDir("aapt2"), "r.apk");
            byte[] manifest = AxmlWriter.Write(new AxmlWriter.Element("manifest", (null, "package", "app")));
            File.WriteAllBytes(path, ZipArchiveWriter.Write(new List<ZipEntry>
            {
                ZipEntry.CreateDeflated(ManifestPatch.EntryName, manifest, null), ZipEntry.CreateDeflated(entry, document, null),
            }));
            return Run("dump", "xmltree", "--file", entry, path);
        }

        /// <summary><c>aapt2 dump badging</c>, without aapt2's "warn:" lines (missing res/ files of a minimal APK).</summary>
        public static string Badging(string apk)
        {
            return Run("dump", "badging", apk);
        }

        /// <summary>The names of the badging's uses-permission lines, in order.</summary>
        public static List<string> UsesPermissions(string badging)
        {
            return Regex.Matches(badging, @"^uses-permission: name='([^']*)'", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        }

        /// <summary>
        /// An xmltree dump without the subtree of the element printed as "E: <paramref name="tag"/> (line=<paramref name="line"/>)":
        /// that line and every following line indented deeper. Exactly one such line must exist.
        /// </summary>
        public static string CutSubtree(string dump, string tag, int line)
        {
            List<string> lines = dump.Replace("\r", "").Split('\n').ToList();
            string header = "E: " + tag + " (line=" + line + ")";
            List<int> at = Enumerable.Range(0, lines.Count).Where(i => lines[i].Trim() == header).ToList();
            Assert.True(at.Count == 1, "expected one '" + header + "' in the dump, found " + at.Count);
            int indent = Indent(lines[at[0]]);
            int end = at[0] + 1;
            while (end < lines.Count && lines[end].Length > 0 && Indent(lines[end]) > indent) end++;
            lines.RemoveRange(at[0], end - at[0]);
            return string.Join("\n", lines);
        }

        /// <summary>
        /// An xmltree dump without its "N:" lines, every other line indented as if they had never been there. aapt2's reader
        /// attaches a namespace declaration to the next start tag (and ignores END_NAMESPACE), so an empty scope left by a
        /// removal shows up above the following element; the platform's parser skips namespace nodes altogether.
        /// </summary>
        public static string WithoutNamespaces(string dump)
        {
            var result = new List<string>();
            var open = new Stack<(int Indent, bool Namespace)>();
            foreach (string line in dump.Replace("\r", "").Split('\n'))
            {
                int indent = Indent(line);
                while (open.Count > 0 && open.Peek().Indent >= indent) open.Pop();
                bool ns = line.TrimStart().StartsWith("N: ", StringComparison.Ordinal);
                if (!ns) result.Add(new string(' ', Math.Max(0, indent - 2 * open.Count(o => o.Namespace))) + line.TrimStart());
                open.Push((indent, ns));
            }
            return string.Join("\n", result);
        }

        private static int Indent(string line)
        {
            return line.Length - line.TrimStart(' ').Length;
        }

        /// <summary>Runs aapt2 (which must succeed) and returns its output without "warn:" lines.</summary>
        internal static string Run(params string[] arguments)
        {
            int code = BuildTools.Run(TestEnvironment.Aapt2, arguments, null, out string text);
            Assert.True(code == 0, "aapt2 " + string.Join(" ", arguments) + " failed (exit " + code + "): " + text);
            return string.Join("\n", text.Replace("\r", "").Split('\n').Where(l => !l.StartsWith("warn: ", StringComparison.Ordinal)));
        }
    }
}
