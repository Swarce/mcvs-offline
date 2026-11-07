using System;
using System.Collections.Generic;
using System.IO;
using McvsOffline.Patcher.Resources;

namespace McvsOffline.Patcher.Art
{
    /// <summary>
    /// The background pictures of the game's own data installer (Gameloft's GameInstaller download and check screens):
    /// <c>res/drawable/installing_splash_bg.png</c> (800x600, in <c>loading_splash_elements.xml</c>) and its two halves
    /// <c>installing_splash_bg_left.png</c> / <c>_right.png</c> (512x768 each, side by side in <c>loading.xml</c>, each
    /// half-screen ImageView <c>centerCrop</c>, so a wide phone shows the middle band of the 1024x768 picture they make).
    /// An art folder can give any of them as <c>installer/&lt;name&gt;.png</c>, at exactly the stock size (a drawable in
    /// <c>res/drawable/</c> is decoded scaled to the screen density, so a larger file would cost memory at the same size on
    /// screen). The pictures can be someone else's artwork: keep them in a folder outside the repository and pass it with
    /// <c>--art-extra</c> (art/android/README.md §5).
    /// </summary>
    public static class InstallerArt
    {
        public const string Folder = "installer/";

        public sealed class StockEntry
        {
            public StockEntry(string file, int width, int height, int bytes, uint crc)
            {
                File = file;
                Width = width;
                Height = height;
                Bytes = bytes;
                Crc = crc;
            }

            /// <summary>The file name, the same in the art folder (under installer/) and in res/drawable/.</summary>
            public string File { get; }

            public string Name { get { return "res/drawable/" + File; } }

            public int Width { get; }

            public int Height { get; }

            public int Bytes { get; }

            public uint Crc { get; }
        }

        /// <summary>The shipped 1.17.32 files (all STORED).</summary>
        public static readonly IReadOnlyList<StockEntry> StockEntries = new[]
        {
            new StockEntry("installing_splash_bg.png", 800, 600, 875851, 0x46d5835b),
            new StockEntry("installing_splash_bg_left.png", 512, 768, 556919, 0x3ed0ba49),
            new StockEntry("installing_splash_bg_right.png", 512, 768, 682046, 0x7fc24a00),
        };

        public static bool IsInstallerFile(string path)
        {
            if (!path.StartsWith(Folder, StringComparison.Ordinal)) return false;
            foreach (StockEntry e in StockEntries)
                if (path == Folder + e.File) return true;
            return false;
        }

        /// <summary>
        /// The replacements the art source gives, checked: a whole, readable PNG (8-bit, not interlaced) at the stock size.
        /// The two halves come together (one alone would show half of each picture). Empty when there are none.
        /// </summary>
        public static List<(StockEntry Stock, byte[] Data)> Build(ArtSource source)
        {
            var result = new List<(StockEntry, byte[])>();
            foreach (StockEntry e in StockEntries)
            {
                string path = Folder + e.File;
                if (!source.Contains(path)) continue;
                byte[] data = source.Read(path);
                Png image;
                try
                {
                    image = Png.Decode(data);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException("art: " + path + " is not a usable PNG: " + ex.Message);
                }
                if (image.Width != e.Width || image.Height != e.Height)
                {
                    throw new InvalidDataException("art: " + path + " is " + image.Width + "x" + image.Height + "; it must be " + e.Width + "x" +
                                                   e.Height + ", the size of the game's own file");
                }
                result.Add((e, data));
            }
            bool left = source.Contains(Folder + StockEntries[1].File), right = source.Contains(Folder + StockEntries[2].File);
            if (left != right)
                throw new InvalidDataException("art: " + Folder + StockEntries[1].File + " and " + StockEntries[2].File + " come together (the installer shows them side by side)");
            return result;
        }
    }
}
