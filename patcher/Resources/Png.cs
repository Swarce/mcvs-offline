using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace McvsOffline.Patcher.Resources
{
    /// <summary>
    /// The PNG subset the art patch reads and writes (app-icon-and-launch-screen.md §4.3, §5.1): 8-bit greyscale, RGB,
    /// greyscale+alpha and RGBA, not interlaced, no APNG. <see cref="Decode"/> checks the whole file (signature, IHDR first,
    /// every chunk's CRC, one IDAT run, IEND last with nothing after it, no unknown critical chunk) and unfilters the
    /// pixels to RGBA, so the patch can look at the alpha channel and resample masters. <see cref="Encode"/> writes
    /// IHDR, one zlib IDAT and IEND; no image library is needed.
    /// </summary>
    public sealed class Png
    {
        public static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public const byte TypeGreyscale = 0, TypeRgb = 2, TypePalette = 3, TypeGreyscaleAlpha = 4, TypeRgba = 6;

        /// <summary>Larger images are refused before their pixels are inflated (a 2048x2048 RGBA buffer is 16 MiB).</summary>
        public const int MaxDimension = 2048;

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>IHDR colour type of the decoded file (0, 2, 4 or 6); for an image made in memory, what <see cref="Encode"/> writes (2 or 6).</summary>
        public byte ColorType { get; private set; }

        /// <summary>Pixels, 4 bytes (R, G, B, A) per pixel, rows top to bottom, not premultiplied.</summary>
        public byte[] Rgba { get; private set; }

        public Png(int width, int height, byte[] rgba)
        {
            if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension) throw new ArgumentOutOfRangeException(nameof(width));
            if (rgba == null || rgba.Length != width * height * 4) throw new ArgumentException("expected " + width + "x" + height + " RGBA pixels", nameof(rgba));
            Width = width;
            Height = height;
            Rgba = rgba;
            ColorType = HasTransparency ? TypeRgba : TypeRgb;
        }

        /// <summary>A single-colour opaque image (an adaptive background given as a colour in art.json).</summary>
        public static Png Solid(int size, uint rgb)
        {
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(rgb >> 16);
                pixels[i + 1] = (byte)(rgb >> 8);
                pixels[i + 2] = (byte)rgb;
                pixels[i + 3] = 0xFF;
            }
            return new Png(size, size, pixels);
        }

        /// <summary>True when at least one pixel is not fully opaque.</summary>
        public bool HasTransparency
        {
            get
            {
                for (int i = 3; i < Rgba.Length; i += 4)
                {
                    if (Rgba[i] != 0xFF) return true;
                }
                return false;
            }
        }

        public static string ColorTypeName(byte colorType)
        {
            switch (colorType)
            {
                case TypeGreyscale: return "greyscale";
                case TypeRgb: return "RGB";
                case TypePalette: return "palette";
                case TypeGreyscaleAlpha: return "greyscale+alpha";
                case TypeRgba: return "RGBA";
                default: return "colour type " + colorType;
            }
        }

        /// <summary>The IHDR of a PNG file (after the signature and chunk CRC checks of <see cref="Decode"/>, without inflating it).</summary>
        public static (int Width, int Height, byte BitDepth, byte ColorType) ReadHeader(byte[] file)
        {
            List<Chunk> chunks = Chunks(file);
            Chunk ihdr = chunks[0];
            return (ihdr.ReadInt32(0), ihdr.ReadInt32(4), file[ihdr.DataOffset + 8], file[ihdr.DataOffset + 9]);
        }

        /// <summary>
        /// Checks and decodes a PNG file. Throws <see cref="InvalidDataException"/> with the reason for anything outside the
        /// subset: a damaged file (CRC, truncation, trailing bytes), 16-bit or palette images, interlacing, APNG.
        /// </summary>
        public static Png Decode(byte[] file)
        {
            List<Chunk> chunks = Chunks(file);
            Chunk ihdr = chunks[0];
            int width = ihdr.ReadInt32(0), height = ihdr.ReadInt32(4);
            byte bitDepth = file[ihdr.DataOffset + 8], colorType = file[ihdr.DataOffset + 9];
            byte compression = file[ihdr.DataOffset + 10], filter = file[ihdr.DataOffset + 11], interlace = file[ihdr.DataOffset + 12];
            if (width < 1 || height < 1) throw new InvalidDataException("IHDR has an empty size " + width + "x" + height);
            if (width > MaxDimension || height > MaxDimension)
                throw new InvalidDataException(width + "x" + height + " is larger than " + MaxDimension + "x" + MaxDimension);
            if (compression != 0 || filter != 0) throw new InvalidDataException("IHDR compression/filter method " + compression + "/" + filter + " (only 0/0 exists)");
            if (interlace != 0) throw new InvalidDataException("interlaced (Adam7) PNG: export without interlacing");
            if (colorType == TypePalette) throw new InvalidDataException("palette (indexed) PNG: export as 8-bit RGBA");
            int channels;
            switch (colorType)
            {
                case TypeGreyscale: channels = 1; break;
                case TypeRgb: channels = 3; break;
                case TypeGreyscaleAlpha: channels = 2; break;
                case TypeRgba: channels = 4; break;
                default: throw new InvalidDataException("unknown colour type " + colorType);
            }
            if (bitDepth != 8) throw new InvalidDataException(bitDepth + "-bit " + ColorTypeName(colorType) + " PNG: export with 8 bits per channel");

            byte[] transparent = null;
            var idat = new MemoryStream();
            int idatRun = 0;   // 0 before the first IDAT, 1 inside the run, 2 after it
            for (int i = 1; i < chunks.Count; i++)
            {
                Chunk c = chunks[i];
                switch (c.Type)
                {
                    case "IDAT":
                        if (idatRun == 2) throw new InvalidDataException("IDAT chunks are not consecutive");
                        idatRun = 1;
                        idat.Write(file, c.DataOffset, c.Length);
                        continue;
                    case "IHDR":
                        throw new InvalidDataException("a second IHDR chunk");
                    case "PLTE":
                        if (colorType == TypeGreyscale || colorType == TypeGreyscaleAlpha) throw new InvalidDataException("PLTE in a greyscale PNG");
                        break;   // a suggested palette of a truecolour image: allowed and ignored
                    case "tRNS":
                        if (idatRun != 0) throw new InvalidDataException("tRNS after the image data");
                        if (colorType == TypeGreyscale && c.Length == 2 || colorType == TypeRgb && c.Length == 6)
                        {
                            transparent = new byte[c.Length];
                            Array.Copy(file, c.DataOffset, transparent, 0, c.Length);
                            break;
                        }
                        throw new InvalidDataException("tRNS of " + c.Length + " bytes in a " + ColorTypeName(colorType) + " PNG");
                    case "acTL":
                    case "fcTL":
                    case "fdAT":
                        throw new InvalidDataException("animated PNG (APNG, " + c.Type + "): export a still image");
                    case "IEND":
                        break;
                    default:
                        if (char.IsUpper(c.Type[0])) throw new InvalidDataException("unknown critical chunk " + c.Type);
                        break;   // ancillary (sRGB, gAMA, pHYs, tEXt, iCCP...): kept as stored, not interpreted
                }
                if (idatRun == 1) idatRun = 2;
            }
            if (idatRun == 0) throw new InvalidDataException("no IDAT chunk");

            int stride = width * channels;
            long expected = (long)height * (stride + 1);
            byte[] raw = Inflate(idat.ToArray(), expected);
            if (raw.Length != expected) throw new InvalidDataException("image data inflates to " + raw.Length + " bytes, expected " + expected);
            Unfilter(raw, height, stride, channels);

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int src = y * (stride + 1) + 1, dst = y * width * 4;
                for (int x = 0; x < width; x++, src += channels, dst += 4)
                {
                    switch (colorType)
                    {
                        case TypeGreyscale:
                            rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = raw[src];
                            rgba[dst + 3] = transparent != null && transparent[0] == 0 && transparent[1] == raw[src] ? (byte)0 : (byte)0xFF;
                            break;
                        case TypeGreyscaleAlpha:
                            rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = raw[src];
                            rgba[dst + 3] = raw[src + 1];
                            break;
                        case TypeRgb:
                            rgba[dst] = raw[src];
                            rgba[dst + 1] = raw[src + 1];
                            rgba[dst + 2] = raw[src + 2];
                            rgba[dst + 3] = transparent != null && transparent[0] == 0 && transparent[1] == raw[src] && transparent[2] == 0 &&
                                            transparent[3] == raw[src + 1] && transparent[4] == 0 && transparent[5] == raw[src + 2] ? (byte)0 : (byte)0xFF;
                            break;
                        default:
                            Array.Copy(raw, src, rgba, dst, 4);
                            break;
                    }
                }
            }
            return new Png(width, height, rgba) { ColorType = colorType };
        }

        /// <summary>
        /// Encodes the pixels as an 8-bit PNG: RGB when every pixel is opaque, RGBA otherwise. Each row gets the filter
        /// with the smallest sum of absolute differences (the usual libpng heuristic), and the data is one zlib stream.
        /// </summary>
        public byte[] Encode()
        {
            bool alpha = HasTransparency;
            int channels = alpha ? 4 : 3;
            int stride = Width * channels;
            var raw = new byte[Height * stride];
            for (int i = 0, o = 0; i < Rgba.Length; i += 4)
            {
                raw[o++] = Rgba[i];
                raw[o++] = Rgba[i + 1];
                raw[o++] = Rgba[i + 2];
                if (alpha) raw[o++] = Rgba[i + 3];
            }
            var filtered = new byte[Height * (stride + 1)];
            var candidate = new byte[stride];
            var best = new byte[stride];
            for (int y = 0; y < Height; y++)
            {
                long bestScore = long.MaxValue;
                byte bestType = 0;
                for (byte type = 0; type <= 4; type++)
                {
                    long score = 0;
                    for (int x = 0; x < stride; x++)
                    {
                        int a = x >= channels ? raw[y * stride + x - channels] : 0;
                        int b = y > 0 ? raw[(y - 1) * stride + x] : 0;
                        int c = x >= channels && y > 0 ? raw[(y - 1) * stride + x - channels] : 0;
                        byte v = (byte)(raw[y * stride + x] - Predict(type, a, b, c));
                        candidate[x] = v;
                        score += v < 128 ? v : 256 - v;
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestType = type;
                        Array.Copy(candidate, best, stride);
                    }
                }
                filtered[y * (stride + 1)] = bestType;
                Array.Copy(best, 0, filtered, y * (stride + 1) + 1, stride);
            }

            byte[] idat;
            using (var buffer = new MemoryStream())
            {
                using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, true)) zlib.Write(filtered, 0, filtered.Length);
                idat = buffer.ToArray();
            }

            var file = new MemoryStream();
            file.Write(Signature, 0, Signature.Length);
            var ihdr = new byte[13];
            WriteInt32(ihdr, 0, Width);
            WriteInt32(ihdr, 4, Height);
            ihdr[8] = 8;
            ihdr[9] = alpha ? TypeRgba : TypeRgb;
            WriteChunk(file, "IHDR", ihdr);
            WriteChunk(file, "IDAT", idat);
            WriteChunk(file, "IEND", new byte[0]);
            return file.ToArray();
        }

        /// <summary>
        /// Downscales a square image to <paramref name="size"/> by area averaging: every output pixel is the coverage-weighted
        /// mean of the source pixels under it (fractional at the edges, so 432 -> 81 works as well as 864 -> 432), computed
        /// on premultiplied alpha so transparent pixels do not bleed their colour into the edges.
        /// </summary>
        public Png Downscale(int size)
        {
            if (Width != Height) throw new InvalidOperationException("only square images are resampled");
            if (size > Width) throw new InvalidOperationException("upscaling " + Width + " -> " + size + " is not done");
            if (size == Width) return new Png(Width, Height, (byte[])Rgba.Clone());
            List<(int Source, double Weight)>[] taps = Taps(Width, size);

            // Horizontal pass into premultiplied doubles, then vertical pass.
            var rows = new double[Height * size * 4];
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    foreach (var tap in taps[x])
                    {
                        int i = (y * Width + tap.Source) * 4;
                        double alpha = Rgba[i + 3] * tap.Weight;
                        r += Rgba[i] * alpha;
                        g += Rgba[i + 1] * alpha;
                        b += Rgba[i + 2] * alpha;
                        a += alpha;
                    }
                    int o = (y * size + x) * 4;
                    rows[o] = r;
                    rows[o + 1] = g;
                    rows[o + 2] = b;
                    rows[o + 3] = a;
                }
            }
            var result = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double r = 0, g = 0, b = 0, a = 0;
                    foreach (var tap in taps[y])
                    {
                        int i = (tap.Source * size + x) * 4;
                        r += rows[i] * tap.Weight;
                        g += rows[i + 1] * tap.Weight;
                        b += rows[i + 2] * tap.Weight;
                        a += rows[i + 3] * tap.Weight;
                    }
                    int o = (y * size + x) * 4;
                    byte alpha = ToByte(a);
                    result[o + 3] = alpha;
                    if (alpha == 0) continue;
                    result[o] = ToByte(r / a);
                    result[o + 1] = ToByte(g / a);
                    result[o + 2] = ToByte(b / a);
                }
            }
            return new Png(size, size, result);
        }

        /// <summary>For each output index, the source indices it covers and their coverage (summing to 1).</summary>
        private static List<(int Source, double Weight)>[] Taps(int from, int to)
        {
            var taps = new List<(int, double)>[to];
            double scale = (double)from / to;
            for (int o = 0; o < to; o++)
            {
                taps[o] = new List<(int, double)>();
                double start = o * scale, end = (o + 1) * scale;
                for (int s = (int)Math.Floor(start); s < Math.Min(from, (int)Math.Ceiling(end)); s++)
                {
                    double cover = Math.Min(end, s + 1) - Math.Max(start, s);
                    if (cover > 1e-9) taps[o].Add((s, cover / scale));
                }
            }
            return taps;
        }

        private static byte ToByte(double v)
        {
            return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v, MidpointRounding.AwayFromZero);
        }

        private static int Predict(byte type, int a, int b, int c)
        {
            switch (type)
            {
                case 0: return 0;
                case 1: return a;
                case 2: return b;
                case 3: return (a + b) >> 1;
                default:
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            }
        }

        private static void Unfilter(byte[] raw, int height, int stride, int bpp)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * (stride + 1);
                byte type = raw[row];
                if (type > 4) throw new InvalidDataException("row " + y + " has filter type " + type);
                for (int x = 0; x < stride; x++)
                {
                    int i = row + 1 + x;
                    int a = x >= bpp ? raw[i - bpp] : 0;
                    int b = y > 0 ? raw[i - stride - 1] : 0;
                    int c = x >= bpp && y > 0 ? raw[i - stride - 1 - bpp] : 0;
                    raw[i] = (byte)(raw[i] + Predict(type, a, b, c));
                }
            }
        }

        private static byte[] Inflate(byte[] zlib, long expected)
        {
            try
            {
                using (var input = new MemoryStream(zlib))
                using (var inflate = new ZLibStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, n);
                        if (output.Length > expected) break;   // more data than the IHDR allows: reported by the caller
                    }
                    return output.ToArray();
                }
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("the image data is not a valid zlib stream: " + ex.Message);
            }
        }

        private readonly struct Chunk
        {
            public readonly string Type;
            public readonly int DataOffset;
            public readonly int Length;
            private readonly byte[] file;

            public Chunk(byte[] file, string type, int dataOffset, int length)
            {
                this.file = file;
                Type = type;
                DataOffset = dataOffset;
                Length = length;
            }

            public int ReadInt32(int at)
            {
                return ReadBigEndian(file, DataOffset + at);
            }
        }

        /// <summary>Splits the file into chunks, checking the signature, every CRC, IHDR first and IEND last.</summary>
        private static List<Chunk> Chunks(byte[] file)
        {
            if (file == null || file.Length < Signature.Length || !file.AsSpan(0, Signature.Length).SequenceEqual(Signature))
                throw new InvalidDataException("not a PNG file (no PNG signature)");
            var chunks = new List<Chunk>();
            int p = Signature.Length;
            while (true)
            {
                if (p + 12 > file.Length) throw new InvalidDataException("truncated PNG (chunk header at byte " + p + ")");
                int length = ReadBigEndian(file, p);
                if (length < 0 || length > file.Length - p - 12) throw new InvalidDataException("truncated PNG (chunk at byte " + p + " claims " + (uint)length + " bytes)");
                string type = Encoding.ASCII.GetString(file, p + 4, 4);
                foreach (char ch in type)
                {
                    if (!(ch >= 'A' && ch <= 'Z' || ch >= 'a' && ch <= 'z')) throw new InvalidDataException("damaged PNG (chunk type bytes at byte " + p + ")");
                }
                uint crc = (uint)ReadBigEndian(file, p + 8 + length);
                uint actual = Zip.Crc32.Compute(file, p + 4, length + 4);
                if (crc != actual) throw new InvalidDataException("damaged PNG: chunk " + type + " at byte " + p + " has CRC " + crc.ToString("x8") + ", computed " + actual.ToString("x8"));
                if (chunks.Count == 0 && (type != "IHDR" || length != 13)) throw new InvalidDataException("the first chunk is " + type + " (" + length + " bytes), not a 13-byte IHDR");
                chunks.Add(new Chunk(file, type, p + 8, length));
                p += 12 + length;
                if (type == "IEND")
                {
                    if (length != 0) throw new InvalidDataException("IEND carries data");
                    if (p != file.Length) throw new InvalidDataException((file.Length - p) + " bytes after IEND");
                    return chunks;
                }
            }
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            var header = new byte[8];
            WriteInt32(header, 0, data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, header, 4);
            output.Write(header, 0, 8);
            output.Write(data, 0, data.Length);
            var crcInput = new byte[4 + data.Length];
            Array.Copy(header, 4, crcInput, 0, 4);
            Array.Copy(data, 0, crcInput, 4, data.Length);
            var crc = new byte[4];
            WriteInt32(crc, 0, (int)Zip.Crc32.Compute(crcInput));
            output.Write(crc, 0, 4);
        }

        private static int ReadBigEndian(byte[] b, int at)
        {
            return b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3];
        }

        private static void WriteInt32(byte[] b, int at, int value)
        {
            b[at] = (byte)(value >> 24);
            b[at + 1] = (byte)(value >> 16);
            b[at + 2] = (byte)(value >> 8);
            b[at + 3] = (byte)value;
        }
    }
}
