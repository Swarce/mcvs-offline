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
            if (k == 0 || (original.Width << k) != dds.Width || (original.Height << k) != dds.Height)
                throw new InvalidDataException("the PC texture is " + dds.Width + "x" + dds.Height + ", not a larger power-of-two multiple of the original " + original.Width + "x" + original.Height);
            int levels = TextureConverter.LevelCount(original.MipLevels, k);

            // The original's top level, decoded: does it use alpha, and how close is it to the PC texture today?
            RgbaImage originalTop = encoder.Decompress(original.Levels[0], original.Width, original.Height, ob[0], ob[1], srgb);
            bool opaque = MinAlpha(originalTop) >= 250;

            var result = new AstcResult
            {
                Setting = setting, Width = dds.Width, Height = dds.Height, MipLevels = levels, Opaque = opaque,
                OriginalWidth = original.Width, OriginalHeight = original.Height, OriginalMipLevels = original.MipLevels,
                OriginalFormat = original.FormatName,
            };
            foreach (byte[] l in original.Levels) result.OriginalGpuBytes += l.Length;

            var encoded = new List<byte[]>();
            RgbaImage level0 = null, current = null;
            for (int i = 0; i < Math.Max(levels, k + 1); i++)
            {
                current = i < dds.Levels.Count ? dds.Decode(i) : current.HalfSize();
                int w = Ktx.MipSize(dds.Width, i), h = Ktx.MipSize(dds.Height, i);
                if (current.Width != w || current.Height != h)
                    throw new InvalidDataException("PC mip level " + i + " is " + current.Width + "x" + current.Height + ", " + w + "x" + h + " expected");
                if (opaque) SetOpaque(current);
                if (i == 0) level0 = current;
                if (i == k) result.OriginalPsnr = RgbaImage.Psnr(originalTop, current, 0, 3);
                if (i >= levels) continue;
                byte[] blocks = encoder.Compress(current, setting.BlockX, setting.BlockY, setting.Quality, srgb);
                encoded.Add(blocks);
                result.GpuBytes += blocks.Length;
                result.Texels += (long)w * h;
            }

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
