using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>An ASTC block footprint and the astcenc quality preset to encode with.</summary>
    internal sealed class AstcSetting
    {
        public AstcSetting(int blockX, int blockY, string quality)
        {
            if (Ktx.AstcFormat(blockX, blockY, false) == 0) throw new ArgumentException("not an ASTC block size: " + blockX + "x" + blockY);
            if (Array.IndexOf(Qualities, quality) < 0) throw new ArgumentException("unknown astcenc quality '" + quality + "' (known: " + string.Join(", ", Qualities) + ")");
            BlockX = blockX;
            BlockY = blockY;
            Quality = quality;
        }

        public static readonly string[] Qualities = { "fastest", "fast", "medium", "thorough", "verythorough", "exhaustive" };

        public int BlockX { get; }
        public int BlockY { get; }
        public string Quality { get; }

        public string Block => BlockX.ToString(CultureInfo.InvariantCulture) + "x" + BlockY.ToString(CultureInfo.InvariantCulture);

        public override string ToString() => Block + " -" + Quality;

        /// <summary>"6x6", "6x6:thorough", "5x5:medium".</summary>
        public static AstcSetting Parse(string text, string defaultQuality)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            string quality = defaultQuality;
            int colon = t.IndexOf(':');
            if (colon >= 0)
            {
                quality = t.Substring(colon + 1).Trim().TrimStart('-');
                t = t.Substring(0, colon).Trim();
            }
            string[] p = t.Split('x');
            if (p.Length != 2 || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out int y))
                throw new ArgumentException("bad ASTC block size '" + text + "' (for example 6x6 or 5x5:thorough)");
            return new AstcSetting(x, y, quality);
        }
    }

    /// <summary>What one ASTC conversion produced.</summary>
    internal sealed class AstcResult
    {
        public byte[] File { get; set; }
        public AstcSetting Setting { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int MipLevels { get; set; }
        public int OriginalWidth { get; set; }
        public int OriginalHeight { get; set; }
        public int OriginalMipLevels { get; set; }
        public string OriginalFormat { get; set; }

        /// <summary>Bytes of texture data on the GPU (every level's blocks), new and original.</summary>
        public long GpuBytes { get; set; }
        public long OriginalGpuBytes { get; set; }

        /// <summary>
        /// The original's size is not a power of two (the ASTC pack's own size for a few textures) and the PC texture is the same
        /// picture stretched to a power of two (<see cref="AstcConverter.StretchedPsnr"/>); the replacement is a full-size chain
        /// at the PC size like any other.
        /// </summary>
        public bool Stretched { get; set; }

        /// <summary>The original's alpha is opaque everywhere, so the replacement's alpha is set to 255 too.</summary>
        public bool Opaque { get; set; }

        /// <summary>PSNR (dB) of the top level's RGB against the PC texture; alpha against the PC alpha (NaN when opaque).</summary>
        public double Psnr { get; set; }
        public double AlphaPsnr { get; set; } = double.NaN;

        /// <summary>PSNR (dB) of Gameloft's ASTC original against the PC texture's level of the same size.</summary>
        public double OriginalPsnr { get; set; }

        /// <summary>
        /// PSNR (dB) of Gameloft's original, enlarged bilinearly to the PC size, against the PC texture: what the phone shows
        /// today where the replacement will be, on the same scale as <see cref="Psnr"/>.
        /// </summary>
        public double OriginalUpscaledPsnr { get; set; }

        public long Texels { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Turns one PC texture into the replacement for its file in Gameloft's ASTC data pack (pc-textures.md §2.2): every PC mip
    /// level decoded (BC1/BC3), each encoded by astcenc at the PC size in the chosen block size, written as a KTX with the
    /// original's header fields (glType, glFormat, glBaseInternalFormat, key/value data) and the new size, level count and
    /// ASTC format (linear or sRGB as the original), and wrapped in GCBF as the original is (eight LZ4 blocks). The chain
    /// ends at the same smallest size as the original's; a 1-level original stays 1 level. When the original's alpha is
    /// opaque everywhere the replacement's is too. Then decoded again for the PSNR self-check.
    /// </summary>
    internal static class AstcConverter
    {
        public static AstcResult Convert(byte[] originalFile, byte[] pcFile, AstcSetting setting, Astcenc encoder)
        {
            var clock = Stopwatch.StartNew();
            bool wrapped = Gcbf.IsGcbf(originalFile);
            byte[] raw = wrapped ? Gcbf.Decode(originalFile, true) : originalFile;
            if (!Ktx.LooksLikeKtx(raw)) throw new InvalidDataException("the ASTC pack's file is not a KTX texture (a split-alpha zip means the ETC pack)");
            Ktx original = Ktx.Parse(raw);
            if (!original.IsAstc) throw new InvalidDataException("the original is " + original.FormatName + ", not ASTC: this is not Gameloft's ASTC pack");
            int[] ob = original.Block();
            bool srgb = original.IsAstcSrgb;
            for (int level = 0; level < original.MipLevels; level++)
            {
                int expected = Astcenc.DataSize(Ktx.MipSize(original.Width, level), Ktx.MipSize(original.Height, level), ob[0], ob[1]);
                if (original.Levels[level].Length != expected)
                    throw new InvalidDataException("the original's mip level " + level + " holds " + original.Levels[level].Length + " bytes, " + expected + " expected");
            }

            byte[] pcRaw = Gcbf.IsGcbf(pcFile) ? Gcbf.Decode(pcFile, true) : pcFile;
            Dds dds = Dds.Parse(pcRaw);
            int k = 0;
            while (k < 15 && (original.Width << k) < dds.Width) k++;
            bool multiple = k > 0 && (original.Width << k) == dds.Width && (original.Height << k) == dds.Height;
            // A few originals of the ASTC pack keep the artist's size that is not a power of two (85x85, 200x200, 1024x778)
            // where the ETC pack and the PC have it stretched to one (128x128, 256x256 and 512x512, 1024x1024 and 2048x2048):
            // texture coordinates are normalised, so the PC texture fits once it is shown to be the same picture stretched
            // (below). Only such an original: a power-of-two original keeps the power-of-two rule unchanged, a PC texture that is
            // not a power-of-two multiple of it is refused as before.
            bool stretched = !multiple && (!IsPowerOfTwo(original.Width) || !IsPowerOfTwo(original.Height));
            if (!multiple && !stretched)
                throw new InvalidDataException("the PC texture is " + dds.Width + "x" + dds.Height + ", not a larger power-of-two multiple of the original " + original.Width + "x" + original.Height);
            if (stretched && (dds.Width <= original.Width || dds.Height <= original.Height))
                throw new InvalidDataException("the PC texture is " + dds.Width + "x" + dds.Height + ", not larger than the original " + original.Width + "x" + original.Height);
            // The chain ends at the same smallest size as the original's (one level more per doubling); 1 level stays 1. For a
            // stretched original the same rule in levels: as many levels fewer than a full chain at the PC size as the
            // original has fewer than a full chain at its own size.
            int levels = !stretched ? TextureConverter.LevelCount(original.MipLevels, k)
                       : original.MipLevels == 1 ? 1 : original.MipLevels + FullChain(dds.Width, dds.Height) - FullChain(original.Width, original.Height);
            if (stretched && (levels < 1 || levels > FullChain(dds.Width, dds.Height)))
                throw new InvalidDataException("the original's " + original.MipLevels + " levels give no chain at " + dds.Width + "x" + dds.Height);

            // The original's top level, decoded: does it use alpha, and how close is it to the PC texture today?
            RgbaImage originalTop = encoder.Decompress(original.Levels[0], original.Width, original.Height, ob[0], ob[1], srgb);
            bool opaque = MinAlpha(originalTop) >= 250;
            if (stretched)
            {
                double same = StretchedPsnr(originalTop, dds);
                if (same < MinStretchedPsnr)
                    throw new InvalidDataException("the PC texture " + dds.Width + "x" + dds.Height + " is not the original " + original.Width + "x" + original.Height +
                                                   " stretched (PSNR " + same.ToString("0.0", CultureInfo.InvariantCulture) + " dB at " + CompareSize(original.Width, original.Height) +
                                                   ", " + MinStretchedPsnr.ToString("0", CultureInfo.InvariantCulture) + " needed): Gameloft's file stays");
            }

            var result = new AstcResult
            {
                Setting = setting, Width = dds.Width, Height = dds.Height, MipLevels = levels, Opaque = opaque, Stretched = stretched,
                OriginalWidth = original.Width, OriginalHeight = original.Height, OriginalMipLevels = original.MipLevels,
                OriginalFormat = original.FormatName,
            };
            foreach (byte[] l in original.Levels) result.OriginalGpuBytes += l.Length;

            var encoded = new List<byte[]>();
            RgbaImage level0 = null, current = null;
            for (int i = 0; i < Math.Max(levels, stretched ? 1 : k + 1); i++)
            {
                current = i < dds.Levels.Count ? dds.Decode(i) : current.HalfSize();
                int w = Ktx.MipSize(dds.Width, i), h = Ktx.MipSize(dds.Height, i);
                if (current.Width != w || current.Height != h)
                    throw new InvalidDataException("PC mip level " + i + " is " + current.Width + "x" + current.Height + ", " + w + "x" + h + " expected");
                if (opaque) SetOpaque(current);
                if (i == 0) level0 = current;
                if (i == k && !stretched) result.OriginalPsnr = RgbaImage.Psnr(originalTop, current, 0, 3);
                if (i >= levels) continue;
                byte[] blocks = encoder.Compress(current, setting.BlockX, setting.BlockY, setting.Quality, srgb);
                encoded.Add(blocks);
                result.GpuBytes += blocks.Length;
                result.Texels += (long)w * h;
            }

            if (stretched) result.OriginalPsnr = RgbaImage.Psnr(originalTop, level0.Bilinear(original.Width, original.Height), 0, 3);
            RgbaImage decoded = encoder.Decompress(encoded[0], dds.Width, dds.Height, setting.BlockX, setting.BlockY, srgb);
            result.Psnr = RgbaImage.Psnr(decoded, level0, 0, 3);
            result.OriginalUpscaledPsnr = RgbaImage.Psnr(originalTop.Bilinear(dds.Width, dds.Height), level0, 0, 3);
            if (!opaque) result.AlphaPsnr = RgbaImage.Psnr(decoded, level0, 3, 1);

            var ktx = new Ktx { Header = (uint[])original.Header.Clone(), KeyValueData = (byte[])original.KeyValueData.Clone() };
            ktx.Header[4] = Ktx.AstcFormat(setting.BlockX, setting.BlockY, srgb);
            ktx.Header[6] = (uint)dds.Width;
            ktx.Header[7] = (uint)dds.Height;
            ktx.Header[11] = (uint)levels;
            ktx.Levels.AddRange(encoded);
            byte[] file = ktx.Write();
            result.File = wrapped ? GcbfWriter.Encode(file) : file;
            result.Elapsed = clock.Elapsed;
            return result;
        }

        /// <summary>
        /// Below this PSNR (dB, RGB) between a stretched original and the PC texture, both reduced to at most
        /// <see cref="CompareMax"/> texels a side (<see cref="StretchedPsnr"/>), the PC texture is not taken. Measured on the
        /// real pack (docs/design/hd-textures.md §6): the five stretched originals score 28.5-51.7 dB against their PC
        /// texture, 1.9-11.4 dB against another one's, 6-21 dB against their own moved by a tenth of its size (one almost
        /// flat texture aside).
        /// </summary>
        public const double MinStretchedPsnr = 24.0;

        /// <summary>The largest side at which <see cref="StretchedPsnr"/> compares.</summary>
        public const int CompareMax = 64;

        /// <summary>
        /// How alike a stretched original and the PC texture are: the PC level nearest above the original's size, stretched
        /// bilinearly to it, and both halved alike until no side is above <see cref="CompareMax"/>; PSNR of RGB. At full size
        /// a hard edge (a white emblem on black) costs tens of dB when one side is resampled by a factor that is not a power
        /// of two, the same picture or not (the 200x200 menu emblem: 23.2 dB at 200x200, 28.5 dB at 50x50); the reduced size
        /// keeps the picture and its placement and drops that.
        /// </summary>
        internal static double StretchedPsnr(RgbaImage originalTop, Dds dds)
        {
            int w = originalTop.Width, h = originalTop.Height;
            int i = 0;
            while (i + 1 < dds.Levels.Count && Ktx.MipSize(dds.Width, i + 1) >= w && Ktx.MipSize(dds.Height, i + 1) >= h) i++;
            RgbaImage a = originalTop, b = dds.Decode(i).Bilinear(w, h);
            while (Math.Max(a.Width, a.Height) > CompareMax)
            {
                a = a.HalfSize();
                b = b.HalfSize();
            }
            return RgbaImage.Psnr(a, b, 0, 3);
        }

        private static string CompareSize(int width, int height)
        {
            while (Math.Max(width, height) > CompareMax)
            {
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            return width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

        /// <summary>Levels of a full chain down to 1x1.</summary>
        internal static int FullChain(int width, int height)
        {
            int levels = 1;
            while (Math.Max(width, height) >> levels > 0) levels++;
            return levels;
        }

        /// <summary>The KTX header of an ASTC pack file (GCBF-wrapped or not): format, size, levels and GPU bytes; null when not an ASTC KTX.</summary>
        public static Ktx ReadHeader(byte[] file)
        {
            byte[] raw;
            try
            {
                raw = Gcbf.IsGcbf(file) ? Gcbf.Decode(file, false) : file;
            }
            catch (Exception ex) when (ex is DataPackException || ex is InvalidDataException || ex is IndexOutOfRangeException || ex is ArgumentException)
            {
                return null;
            }
            if (!Ktx.LooksLikeKtx(raw)) return null;
            try
            {
                return Ktx.Parse(raw);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static int MinAlpha(RgbaImage image)
        {
            int min = 255;
            for (int i = 3; i < image.Pixels.Length; i += 4) min = Math.Min(min, image.Pixels[i]);
            return min;
        }

        private static void SetOpaque(RgbaImage image)
        {
            for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = 255;
        }
    }
}
