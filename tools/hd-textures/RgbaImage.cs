using System;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>An 8-bit RGBA image, rows top to bottom, 4 bytes per pixel.</summary>
    internal sealed class RgbaImage
    {
        public RgbaImage(int width, int height)
        {
            if (width < 1 || height < 1) throw new ArgumentException("bad image size " + width + "x" + height);
            Width = width;
            Height = height;
            Pixels = new byte[checked(width * height * 4)];
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        /// <summary>The next mip level: a 2x2 box filter (an odd edge repeats its last row or column), at least 1x1.</summary>
        public RgbaImage HalfSize()
        {
            int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
            var half = new RgbaImage(w, h);
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Min(2 * y, Height - 1), y1 = Math.Min(2 * y + 1, Height - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Min(2 * x, Width - 1), x1 = Math.Min(2 * x + 1, Width - 1);
                    int a = (y0 * Width + x0) * 4, b = (y0 * Width + x1) * 4, c = (y1 * Width + x0) * 4, d = (y1 * Width + x1) * 4;
                    int o = (y * w + x) * 4;
                    for (int ch = 0; ch < 4; ch++)
                        half.Pixels[o + ch] = (byte)((Pixels[a + ch] + Pixels[b + ch] + Pixels[c + ch] + Pixels[d + ch] + 2) >> 2);
                }
            }
            return half;
        }

        /// <summary>
        /// This image enlarged to <paramref name="width"/> x <paramref name="height"/> with bilinear filtering between texel
        /// centres (edges clamped), as a GPU magnifies a texture: how a smaller texture looks where the larger one would be.
        /// </summary>
        public RgbaImage Bilinear(int width, int height)
        {
            var big = new RgbaImage(width, height);
            double sx = (double)Width / width, sy = (double)Height / height;
            for (int y = 0; y < height; y++)
            {
                double fy = Math.Max(0, (y + 0.5) * sy - 0.5);
                int y0 = Math.Min((int)fy, Height - 1), y1 = Math.Min(y0 + 1, Height - 1);
                double ty = fy - y0;
                for (int x = 0; x < width; x++)
                {
                    double fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                    int x0 = Math.Min((int)fx, Width - 1), x1 = Math.Min(x0 + 1, Width - 1);
                    double tx = fx - x0;
                    int a = (y0 * Width + x0) * 4, b = (y0 * Width + x1) * 4, c = (y1 * Width + x0) * 4, d = (y1 * Width + x1) * 4;
                    int o = (y * width + x) * 4;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        double top = Pixels[a + ch] + (Pixels[b + ch] - Pixels[a + ch]) * tx;
                        double bottom = Pixels[c + ch] + (Pixels[d + ch] - Pixels[c + ch]) * tx;
                        big.Pixels[o + ch] = (byte)Math.Min(255, Math.Max(0, (int)Math.Round(top + (bottom - top) * ty)));
                    }
                }
            }
            return big;
        }

        /// <summary>True when every alpha value is 255.</summary>
        public bool IsOpaque()
        {
            for (int i = 3; i < Pixels.Length; i += 4)
            {
                if (Pixels[i] != 255) return false;
            }
            return true;
        }

        /// <summary>A copy with alpha copied into R, G and B and alpha 255 (the split-alpha texture of the ETC pack).</summary>
        public RgbaImage AlphaAsGrey()
        {
            var grey = new RgbaImage(Width, Height);
            for (int i = 0; i < Pixels.Length; i += 4)
            {
                byte a = Pixels[i + 3];
                grey.Pixels[i] = a;
                grey.Pixels[i + 1] = a;
                grey.Pixels[i + 2] = a;
                grey.Pixels[i + 3] = 255;
            }
            return grey;
        }

        /// <summary>
        /// PSNR in dB between two images of the same size over the given channels (0 = R ... 3 = A), 8 bit;
        /// <see cref="double.PositiveInfinity"/> when they are identical.
        /// </summary>
        public static double Psnr(RgbaImage a, RgbaImage b, int firstChannel, int channelCount)
        {
            if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("PSNR of images of different sizes");
            long sum = 0;
            for (int i = 0; i < a.Pixels.Length; i += 4)
            {
                for (int c = firstChannel; c < firstChannel + channelCount; c++)
                {
                    int d = a.Pixels[i + c] - b.Pixels[i + c];
                    sum += d * d;
                }
            }
            if (sum == 0) return double.PositiveInfinity;
            double mse = (double)sum / ((long)a.Width * a.Height * channelCount);
            return 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }
    }
}
