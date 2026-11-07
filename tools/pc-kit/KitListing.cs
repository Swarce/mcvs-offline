using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Pc;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// 'status --listing' (docs/design/pc-port.md §9.2, WR2 steps 1 and 16): the game folder's top level with size, time and
    /// SHA-256 (folders: file count and total size) into <c>listing-&lt;stamp&gt;.txt</c>, and, when an earlier listing exists,
    /// <c>listing-diff.txt</c> against the first one, each difference classified as ours, the game's own or other. Listings
    /// are looked for in both kit folders (<c>McvsOffline\kit\</c> and <c>%TEMP%\McvsOffline\kit\</c>): the one made before
    /// the install lies in the temp folder, because McvsOffline\ did not exist yet.
    /// </summary>
    public static class KitListing
    {
        public const string Prefix = "listing-";
        public const string DiffName = "listing-diff.txt";

        /// <summary>Writes the listing (and the diff); returns the lines to print.</summary>
        public static List<string> Write(string game, string kitFolder, IEnumerable<string> searchFolders, DateTime now)
        {
            var lines = new List<string>();
            Directory.CreateDirectory(kitFolder);
            List<GameFolderEntry> entries = GameFolder.Listing(game);
            string file = Path.Combine(kitFolder, Prefix + KitServer.Stamp(now) + ".txt");
            File.WriteAllText(file, GameFolder.FormatListing(game, entries), new UTF8Encoding(false));
            lines.Add("listing: " + file + " (" + entries.Count + " entries)");

            string first = Listings(searchFolders.Concat(new[] { kitFolder })).FirstOrDefault(f => !string.Equals(f, file, StringComparison.Ordinal));
            if (first == null)
            {
                lines.Add("no earlier listing: this one is the reference for the next 'status --listing'");
                return lines;
            }
            List<string> diff = GameFolder.DiffListings(GameFolder.ParseListing(File.ReadAllText(first)), entries);
            var text = new StringBuilder();
            text.Append("# differences of ").Append(Path.GetFileName(file)).Append(" against the first listing ").Append(first).Append('\n');
            text.Append("# what\tentry\twhose\n");
            foreach (string d in diff) text.Append(d).Append('\n');
            if (diff.Count == 0) text.Append("# none\n");
            string diffFile = Path.Combine(kitFolder, DiffName);
            File.WriteAllText(diffFile, text.ToString(), new UTF8Encoding(false));
            lines.Add("differences against " + first + ": " + (diff.Count == 0 ? "none" : diff.Count.ToString()) + " (" + diffFile + ")");
            foreach (string d in diff) lines.Add("  " + d.Replace('\t', ' '));
            return lines;
        }

        /// <summary>Every listing-*.txt in the folders (the diff excluded), oldest first by name (the name holds the time).</summary>
        public static List<string> Listings(IEnumerable<string> folders)
        {
            var files = new List<string>();
            foreach (string folder in folders.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.Ordinal))
            {
                if (!Directory.Exists(folder)) continue;
                files.AddRange(Directory.GetFiles(folder, Prefix + "*.txt").Where(f => !string.Equals(Path.GetFileName(f), DiffName, StringComparison.Ordinal)));
            }
            return files.OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ThenBy(f => f, StringComparer.Ordinal).ToList();
        }
    }
}
