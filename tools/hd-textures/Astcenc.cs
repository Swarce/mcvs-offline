using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>
    /// ARM's astc-encoder (astcenc, Apache-2.0, https://github.com/ARM-software/astc-encoder) as a command-line tool: one call
    /// per mip level, the level passed as an uncompressed RGBA8 KTX and read back as a <c>.astc</c> file (16-byte header +
    /// blocks). The binary is the player's: <c>--astcenc PATH</c>, else <c>MCVS_ASTCENC</c>, else the first of
    /// <c>astcenc-avx2</c> (when the CPU has AVX2), <c>astcenc-sse4.1</c>, <c>astcenc-sse2</c>, <c>astcenc-neon</c>,
    /// <c>astcenc</c> next to this tool, in an <c>astcenc/</c> folder beside it, or on PATH. It is never shipped with the
    /// repository (tools/hd-textures/README.md says how to get or build it).
    /// </summary>
    internal sealed class Astcenc
    {
        public const string EnvironmentVariable = "MCVS_ASTCENC";

        private static readonly uint AstcMagic = 0x5CA1AB13;
        private static int counter;

        public Astcenc(string path)
        {
            Path = path;
        }

        /// <summary>The binary.</summary>
        public string Path { get; }

        /// <summary>Threads per call (<c>-j</c>); the builder runs one call per worker.</summary>
        public int ThreadsPerCall { get; set; } = 1;

        /// <summary>Folder for the per-call files (deleted after each call).</summary>
        public string TempDir { get; set; } = System.IO.Path.GetTempPath();

        /// <summary>Finds the binary (see the class summary); <paramref name="how"/> says where it came from.</summary>
        public static Astcenc Locate(string given, out string how)
        {
            if (!string.IsNullOrWhiteSpace(given))
            {
                string p = System.IO.Path.GetFullPath(given.Trim().Trim('"'));
                if (!File.Exists(p)) throw new ArgumentException("--astcenc " + p + " does not exist");
                how = "--astcenc";
                return new Astcenc(p);
            }
            string env = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(env))
            {
                string p = System.IO.Path.GetFullPath(env.Trim().Trim('"'));
                if (!File.Exists(p)) throw new ArgumentException(EnvironmentVariable + "=" + p + " does not exist");
                how = EnvironmentVariable;
                return new Astcenc(p);
            }
            var folders = new List<string>();
            string baseDir = AppContext.BaseDirectory;
            folders.Add(baseDir);
            folders.Add(System.IO.Path.Combine(baseDir, "astcenc"));
            folders.Add(System.IO.Path.Combine(baseDir, "astcenc", "bin"));
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            folders.AddRange(pathVar.Split(System.IO.Path.PathSeparator).Where(f => f.Length > 0));
            foreach (string name in CandidateNames())
            {
                foreach (string folder in folders)
                {
                    string p;
                    try
                    {
                        p = System.IO.Path.Combine(folder, name);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    if (File.Exists(p))
                    {
                        how = folder == baseDir || folder.StartsWith(System.IO.Path.Combine(baseDir, "astcenc"), StringComparison.Ordinal) ? "next to the tool" : "PATH";
                        return new Astcenc(p);
                    }
                }
            }
            throw new ArgumentException("astcenc (ARM's ASTC encoder) was not found: give --astcenc <path to astcenc-avx2 or astcenc-sse4.1>, " +
                                        "set " + EnvironmentVariable + ", or put it next to this tool (tools/hd-textures/README.md, \"astcenc\")");
        }

        /// <summary>The binary names to look for, best first, for this CPU and OS.</summary>
        internal static IEnumerable<string> CandidateNames()
        {
            string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
            var names = new List<string>();
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64 || RuntimeInformation.ProcessArchitecture == Architecture.X86)
            {
                if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) names.Add("astcenc-avx2");
                if (System.Runtime.Intrinsics.X86.Sse41.IsSupported) names.Add("astcenc-sse4.1");
                names.Add("astcenc-sse2");
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                names.Add("astcenc-neon");
            }
            names.Add("astcenc");
            return names.Select(n => n + exe);
        }

        /// <summary>"astcenc v5.7.0, 64-bit avx2+popcnt+f16c" (the first line of <c>-version</c>).</summary>
        public string Version()
        {
            string output = Run(new[] { "-version" }, out int code);
            string line = (output ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("astcenc", StringComparison.OrdinalIgnoreCase));
            if (code != 0 || line == null) throw new InvalidDataException(Path + " is not astcenc (-version: exit " + code + ", " + Shorten(output) + ")");
            return line;
        }

        /// <summary>Compresses one image; returns the ASTC blocks (the <c>.astc</c> file without its header).</summary>
        public byte[] Compress(RgbaImage image, int blockX, int blockY, string quality, bool srgb, string[] extra = null)
        {
            string stem = NewStem();
            string input = stem + ".ktx", output = stem + ".astc";
            try
            {
                File.WriteAllBytes(input, UncompressedKtx(image));
                var args = new List<string> { srgb ? "-cs" : "-cl", input, output, blockX + "x" + blockY, "-" + quality, "-silent", "-j", ThreadsPerCall.ToString(CultureInfo.InvariantCulture) };
                if (extra != null) args.AddRange(extra);
                string log = Run(args, out int code);
                if (code != 0 || !File.Exists(output)) throw new InvalidDataException("astcenc failed (exit " + code + "): " + Shorten(log));
                byte[] file = File.ReadAllBytes(output);
                return Blocks(file, image.Width, image.Height, blockX, blockY);
            }
            finally
            {
                TryDelete(input);
                TryDelete(output);
            }
        }

        /// <summary>Decompresses ASTC blocks of an image of the given size (the self-check and the original's PSNR).</summary>
        public RgbaImage Decompress(byte[] blocks, int width, int height, int blockX, int blockY, bool srgb)
        {
            string stem = NewStem();
            string input = stem + ".astc", output = stem + ".out.ktx";
            try
            {
                File.WriteAllBytes(input, AstcFile(blocks, width, height, blockX, blockY));
                string log = Run(new[] { srgb ? "-ds" : "-dl", input, output, "-silent" }, out int code);
                if (code != 0 || !File.Exists(output)) throw new InvalidDataException("astcenc could not decompress (exit " + code + "): " + Shorten(log));
                return ReadUncompressedKtx(File.ReadAllBytes(output), width, height);
            }
            finally
            {
                TryDelete(input);
                TryDelete(output);
            }
        }

        /// <summary>Bytes of an ASTC image (16 per block).</summary>
        public static int DataSize(int width, int height, int blockX, int blockY) => ((width + blockX - 1) / blockX) * ((height + blockY - 1) / blockY) * 16;

        /// <summary>A <c>.astc</c> file: magic, block size (x, y, z = 1), then the 24-bit width, height and depth.</summary>
        internal static byte[] AstcFile(byte[] blocks, int width, int height, int blockX, int blockY)
        {
            var file = new byte[16 + blocks.Length];
            BitConverter.GetBytes(AstcMagic).CopyTo(file, 0);
            file[4] = (byte)blockX;
            file[5] = (byte)blockY;
            file[6] = 1;
            Put24(file, 7, width);
            Put24(file, 10, height);
            Put24(file, 13, 1);
            Buffer.BlockCopy(blocks, 0, file, 16, blocks.Length);
            return file;
        }

        /// <summary>The blocks of a <c>.astc</c> file, checked against the expected block and image size.</summary>
        internal static byte[] Blocks(byte[] file, int width, int height, int blockX, int blockY)
        {
            if (file.Length < 16 || BitConverter.ToUInt32(file, 0) != AstcMagic) throw new InvalidDataException("astcenc wrote no .astc file");
            int bx = file[4], by = file[5], bz = file[6];
            int w = file[7] | (file[8] << 8) | (file[9] << 16), h = file[10] | (file[11] << 8) | (file[12] << 16);
            if (bx != blockX || by != blockY || bz != 1 || w != width || h != height)
                throw new InvalidDataException("astcenc wrote a " + w + "x" + h + " image in " + bx + "x" + by + "x" + bz + " blocks, " + width + "x" + height + " in " + blockX + "x" + blockY + " expected");
            int size = DataSize(width, height, blockX, blockY);
            if (file.Length != 16 + size) throw new InvalidDataException("astcenc wrote " + (file.Length - 16) + " bytes of blocks, " + size + " expected");
            var blocks = new byte[size];
            Buffer.BlockCopy(file, 16, blocks, 0, size);
            return blocks;
        }

        /// <summary>An uncompressed KTX 1.1 (GL_UNSIGNED_BYTE, GL_RGBA, GL_RGBA8), one level, rows top to bottom.</summary>
        internal static byte[] UncompressedKtx(RgbaImage image)
        {
            var ktx = new Ktx();
            ktx.Header = new uint[] { 0x04030201, 0x1401, 1, 0x1908, 0x8058, 0x1908, (uint)image.Width, (uint)image.Height, 0, 0, 1, 1, 0 };
            ktx.Levels.Add(image.Pixels);
            return ktx.Write();
        }

        internal static RgbaImage ReadUncompressedKtx(byte[] data, int width, int height)
        {
            Ktx ktx = Ktx.Parse(data);
            if (ktx.Width != width || ktx.Height != height) throw new InvalidDataException("astcenc decoded a " + ktx.Width + "x" + ktx.Height + " image, " + width + "x" + height + " expected");
            // astcenc writes only the components the decoded image uses (astcenccli_image.cpp, determine_image_components):
            // GL_RED = luminance, GL_RG = luminance + alpha, GL_RGB = opaque colour, GL_RGBA.
            int components;
            switch (ktx.Header[3])
            {
                case 0x1903: components = 1; break;
                case 0x8227: components = 2; break;
                case 0x1907: components = 3; break;
                case 0x1908: components = 4; break;
                default: components = 0; break;
            }
            if (ktx.Header[1] != 0x1401 || components == 0)
                throw new InvalidDataException("astcenc decoded to an unexpected KTX format (type 0x" + ktx.Header[1].ToString("x") + ", format 0x" + ktx.Header[3].ToString("x") + ")");
            var image = new RgbaImage(width, height);
            byte[] src = ktx.Levels[0];
            int rowBytes = width * components;
            int stride = rowBytes;   // astcenc packs the rows tightly (only the whole image is padded to 4 bytes)
            if (src.Length < stride * (height - 1) + rowBytes) throw new InvalidDataException("astcenc's decoded image is short");
            byte[] dst = image.Pixels;
            for (int y = 0; y < height; y++)
            {
                int s = y * stride, d = y * width * 4;
                for (int x = 0; x < width; x++, d += 4, s += components)
                {
                    switch (components)
                    {
                        case 1:
                            dst[d] = dst[d + 1] = dst[d + 2] = src[s];
                            dst[d + 3] = 255;
                            break;
                        case 2:
                            dst[d] = dst[d + 1] = dst[d + 2] = src[s];
                            dst[d + 3] = src[s + 1];
                            break;
                        case 3:
                            dst[d] = src[s];
                            dst[d + 1] = src[s + 1];
                            dst[d + 2] = src[s + 2];
                            dst[d + 3] = 255;
                            break;
                        default:
                            dst[d] = src[s];
                            dst[d + 1] = src[s + 1];
                            dst[d + 2] = src[s + 2];
                            dst[d + 3] = src[s + 3];
                            break;
                    }
                }
            }
            return image;
        }

        private string NewStem()
        {
            Directory.CreateDirectory(TempDir);
            int n = Interlocked.Increment(ref counter);
            return System.IO.Path.Combine(TempDir, "astc-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + n.ToString(CultureInfo.InvariantCulture));
        }

        private string Run(IList<string> args, out int exitCode)
        {
            var psi = new ProcessStartInfo(Path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using (Process p = Process.Start(psi))
            {
                if (p == null) throw new IOException("cannot start " + Path);
                var err = new StringBuilder();
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) lock (err) err.AppendLine(e.Data);
                };
                p.BeginErrorReadLine();
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
                lock (err) return stdout + err;
            }
        }

        private static void Put24(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Trim().Replace('\n', ' ');
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
