using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// A texture file of the ETC pack as it lies in <c>Data/</c> (pc-textures.md §2.1): a GCBF-wrapped ETC1 KTX, or a
    /// split-alpha zip (stored entries <c>SPLIT</c> = 4 zero bytes, <c>rgb.ktx</c>, <c>alpha.ktx</c>, each KTX GCBF-wrapped)
    /// whose alpha texture is ETC1 with the alpha in its colour.
    /// </summary>
    internal sealed class AndroidTexture
    {
        public const string LayoutKtx = "ktx";
        public const string LayoutSplit = "split";

        public string Layout { get; private set; }

        /// <summary>Whether the KTX (ktx layout) or each KTX inside the zip (split layout) is GCBF-wrapped.</summary>
        public bool Wrapped { get; private set; }

        public Ktx Rgb { get; private set; }
        public Ktx Alpha { get; private set; }

        /// <summary>Split layout: the zip's entries in file order (name, bytes as stored).</summary>
        public List<KeyValuePair<string, byte[]>> Entries { get; } = new List<KeyValuePair<string, byte[]>>();

        public static AndroidTexture Parse(byte[] file)
        {
            var t = new AndroidTexture();
            if (file.Length >= 4 && file[0] == 'P' && file[1] == 'K' && file[2] == 3 && file[3] == 4)
            {
                t.Layout = LayoutSplit;
                using (var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read))
                {
                    // Entries keep the central directory's order, which is the file order in every zip of the pack.
                    foreach (ZipArchiveEntry e in zip.Entries)
                    {
                        using (Stream s = e.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            t.Entries.Add(new KeyValuePair<string, byte[]>(e.FullName, ms.ToArray()));
                        }
                    }
                }
                byte[] rgb = t.Entry("rgb.ktx"), alpha = t.Entry("alpha.ktx");
                if (rgb == null || alpha == null || t.Entry("SPLIT") == null)
                    throw new InvalidDataException("a zip texture without SPLIT, rgb.ktx and alpha.ktx (entries: " + string.Join(", ", t.Entries.Select(e => e.Key)) + ")");
                if (Gcbf.IsGcbf(rgb) != Gcbf.IsGcbf(alpha)) throw new InvalidDataException("rgb.ktx and alpha.ktx are wrapped differently");
                t.Wrapped = Gcbf.IsGcbf(rgb);
                t.Rgb = ParseKtx(rgb, "rgb.ktx");
                t.Alpha = ParseKtx(alpha, "alpha.ktx");
                if (t.Rgb.Width != t.Alpha.Width || t.Rgb.Height != t.Alpha.Height || t.Rgb.MipLevels != t.Alpha.MipLevels)
                    throw new InvalidDataException("rgb.ktx and alpha.ktx differ in size or mip levels");
            }
            else
            {
                t.Layout = LayoutKtx;
                t.Wrapped = Gcbf.IsGcbf(file);
                t.Rgb = ParseKtx(file, "the texture");
            }
            return t;
        }

        public byte[] Entry(string name)
        {
            foreach (KeyValuePair<string, byte[]> e in Entries)
            {
                if (e.Key == name) return e.Value;
            }
            return null;
        }

        private static Ktx ParseKtx(byte[] bytes, string what)
        {
            byte[] raw = Gcbf.IsGcbf(bytes) ? Gcbf.Decode(bytes, true) : bytes;
            if (!Ktx.LooksLikeKtx(raw)) throw new InvalidDataException(what + " is not a KTX texture");
            Ktx ktx = Ktx.Parse(raw);
            if (ktx.GlInternalFormat != Ktx.GlEtc1)
                throw new InvalidDataException(what + " is " + ktx.FormatName + ", not ETC1: this is not the ETC pack (the converter writes ETC1 only)");
            for (int level = 0; level < ktx.MipLevels; level++)
            {
                int expected = Etc1.DataSize(Ktx.MipSize(ktx.Width, level), Ktx.MipSize(ktx.Height, level));
                if (ktx.Levels[level].Length != expected)
                    throw new InvalidDataException(what + " mip level " + level + " holds " + ktx.Levels[level].Length + " bytes, " + expected + " expected");
            }
            return ktx;
        }
    }

    /// <summary>What one conversion produced.</summary>
    internal sealed class ConversionResult
    {
        public byte[] File { get; set; }
        public string Layout { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int MipLevels { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public int OriginalMipLevels { get; set; }

        /// <summary>PSNR (dB) of the top level's RGB against the PC texture.</summary>
        public double Psnr { get; set; }

        /// <summary>Split layout: PSNR of the alpha texture against the PC alpha; NaN otherwise.</summary>
        public double AlphaPsnr { get; set; } = double.NaN;

        /// <summary>PSNR (dB) of the Android original's RGB against the PC texture's level of the same size: the quality the phone has today.</summary>
        public double OriginalPsnr { get; set; }

        /// <summary>Texels encoded (both textures of a split file, every level).</summary>
        public long Texels { get; set; }

        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Turns one PC texture into the replacement for its Android file (docs/design/hd-textures.md §5): the PC DDS decoded
    /// level by level, each level encoded to ETC1 at the PC size, written with the Android file's own KTX header fields,
    /// key/value data, layout and wrapping, then decoded again for the PSNR self-check.
    /// </summary>
    internal static class TextureConverter
    {
        public static ConversionResult Convert(byte[] androidFile, byte[] pcFile, int threads)
        {
            var clock = Stopwatch.StartNew();
            AndroidTexture android = AndroidTexture.Parse(androidFile);
            byte[] pcRaw = Gcbf.IsGcbf(pcFile) ? Gcbf.Decode(pcFile, true) : pcFile;
            Dds dds = Dds.Parse(pcRaw);
            Ktx a = android.Rgb;
            int k = 0;
            while ((a.Width << k) < dds.Width) k++;
            if (k == 0 || (a.Width << k) != dds.Width || (a.Height << k) != dds.Height)
                throw new InvalidDataException("the PC texture is " + dds.Width + "x" + dds.Height + ", not a larger power-of-two multiple of the Android " + a.Width + "x" + a.Height);
            int levels = a.MipLevels + k;

            // Alpha: from DXT5, or from DXT1's punch-through only when the Android original uses alpha at all.
            bool split = android.Layout == AndroidTexture.LayoutSplit;
            bool opaqueAlpha = false;
            if (split && dds.IsBc1)
            {
                RgbaImage originalAlpha = Etc1.Decode(android.Alpha.Levels[0], 0, android.Alpha.Width, android.Alpha.Height);
                opaqueAlpha = MinRed(originalAlpha) >= 250;
            }

            var rgbLevels = new List<byte[]>();
            var alphaLevels = new List<byte[]>();
            RgbaImage level = null, top = null;
            long texels = 0;
            double originalPsnr = double.NaN;
            byte[] whiteBlock = null;
            for (int i = 0; i < levels; i++)
            {
                level = i < dds.Levels.Count ? dds.Decode(i) : level.HalfSize();
                int w = Ktx.MipSize(dds.Width, i), h = Ktx.MipSize(dds.Height, i);
                if (level.Width != w || level.Height != h)
                    throw new InvalidDataException("PC mip level " + i + " is " + level.Width + "x" + level.Height + ", " + w + "x" + h + " expected");
                if (i == 0) top = level;
                if (i == k) originalPsnr = RgbaImage.Psnr(Etc1.Decode(a.Levels[0], 0, a.Width, a.Height), level, 0, 3);
                rgbLevels.Add(Etc1.Encode(level, threads));
                texels += (long)w * h;
                if (split)
                {
                    if (opaqueAlpha || level.IsOpaque())
                    {
                        if (whiteBlock == null) whiteBlock = Etc1.Encode(Solid(4, 4, 255), 1);
                        var blocks = new byte[Etc1.DataSize(w, h)];
                        for (int o = 0; o < blocks.Length; o += Etc1.BlockBytes) Buffer.BlockCopy(whiteBlock, 0, blocks, o, Etc1.BlockBytes);
                        alphaLevels.Add(blocks);
                    }
                    else
                    {
                        alphaLevels.Add(Etc1.Encode(level.AlphaAsGrey(), threads));
                    }
                    texels += (long)w * h;
                }
            }

            var result = new ConversionResult
            {
                Layout = android.Layout, Width = dds.Width, Height = dds.Height, MipLevels = levels, Texels = texels,
                OriginalWidth = a.Width, OriginalHeight = a.Height, OriginalMipLevels = a.MipLevels, OriginalPsnr = originalPsnr,
            };
            RgbaImage decoded = Etc1.Decode(rgbLevels[0], 0, dds.Width, dds.Height);
            result.Psnr = RgbaImage.Psnr(decoded, top, 0, 3);
            byte[] rgbKtx = Rebuild(a, rgbLevels, dds.Width, dds.Height).Write();
            if (!split)
            {
                result.File = android.Wrapped ? GcbfWriter.Encode(rgbKtx) : rgbKtx;
            }
            else
            {
                RgbaImage decodedAlpha = Etc1.Decode(alphaLevels[0], 0, dds.Width, dds.Height);
                RgbaImage sourceAlpha = opaqueAlpha ? Solid(dds.Width, dds.Height, 255) : top.AlphaAsGrey();
                result.AlphaPsnr = RgbaImage.Psnr(decodedAlpha, sourceAlpha, 0, 1);
                byte[] alphaKtx = Rebuild(android.Alpha, alphaLevels, dds.Width, dds.Height).Write();
                var entries = new List<KeyValuePair<string, byte[]>>();
                foreach (KeyValuePair<string, byte[]> e in android.Entries)
                {
                    byte[] bytes = e.Value;
                    if (e.Key == "rgb.ktx") bytes = android.Wrapped ? GcbfWriter.Encode(rgbKtx) : rgbKtx;
                    else if (e.Key == "alpha.ktx") bytes = android.Wrapped ? GcbfWriter.Encode(alphaKtx) : alphaKtx;
                    entries.Add(new KeyValuePair<string, byte[]>(e.Key, bytes));
                }
                result.File = StoredZipWriter.Build(false, entries);
            }
            result.Elapsed = clock.Elapsed;
            return result;
        }

        /// <summary>The original's header and key/value data with the new size, level count and levels.</summary>
        internal static Ktx Rebuild(Ktx original, List<byte[]> levels, int width, int height)
        {
            var ktx = new Ktx { Header = (uint[])original.Header.Clone(), KeyValueData = (byte[])original.KeyValueData.Clone() };
            ktx.Header[6] = (uint)width;
            ktx.Header[7] = (uint)height;
            ktx.Header[11] = (uint)levels.Count;
            ktx.Levels.AddRange(levels);
            return ktx;
        }

        internal static RgbaImage Solid(int w, int h, byte v)
        {
            var image = new RgbaImage(w, h);
            for (int i = 0; i < image.Pixels.Length; i++) image.Pixels[i] = v;
            return image;
        }

        private static int MinRed(RgbaImage image)
        {
            int min = 255;
            for (int i = 0; i < image.Pixels.Length; i += 4) min = Math.Min(min, image.Pixels[i]);
            return min;
        }
    }
}
