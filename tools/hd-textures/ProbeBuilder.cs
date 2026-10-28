using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Tools.HdTextures
{
    /// <summary>One coloured copy of the probe texture in the zip.</summary>
    internal sealed class ProbeVariant
    {
        public string Entry { get; set; }
        public string Naming { get; set; }
        public ProbeColour Colour { get; set; }
        public byte[] Bytes { get; set; }
        public byte[] Payload { get; set; }
    }

    /// <summary>The probe zip and what it holds.</summary>
    internal sealed class ProbeResult
    {
        public byte[] Zip { get; set; }
        public string ManifestJson { get; set; }
        public Ktx Original { get; set; }
        public string LogicalPath { get; set; }
        public string HashedPath { get; set; }
        public string Pack { get; set; }
        public bool Gcbf { get; set; }
        public List<ProbeVariant> Variants { get; } = new List<ProbeVariant>();
    }

    /// <summary>
    /// The WP1 delivery probe of docs/research/pc-textures.md: from the player's own copy of one texture (its Data/ file, GCBF as
    /// on the phone) it builds <c>hd-textures.zip</c> holding the same texture, recoloured to a solid colour, once per naming the
    /// engine might look up inside a mounted zip:
    /// <list type="table">
    /// <item><term>logical</term><description>"3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga": magenta (yellow with --raw)</description></item>
    /// <item><term>hashed</term><description>"8/dbd2f1.f" (crc32 of the logical path, as in Data/): green (cyan with --raw)</description></item>
    /// <item><term>hashed under Data/</term><description>"Data/8/dbd2f1.f": blue (orange with --raw)</description></item>
    /// </list>
    /// Every copy keeps the original's format (ASTC on the S22's pack, ETC1 on ETC phones), size and mip chain, so whichever the
    /// renderer uses is visibly that colour in a match. The entries are GCBF-wrapped like Data/ files (default) or bare KTX
    /// (<c>--raw</c>, different colours), and <c>mcvs-hd.json</c> lists them with their CRC-32s so the embedded host can log
    /// which copy the engine's own lookup returns. Nothing from the PC build is involved.
    /// </summary>
    internal static class ProbeBuilder
    {
        public const string DefaultTexture = "3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga";
        public const string ManifestEntry = "mcvs-hd.json";

        public static ProbeResult Build(byte[] dataFile, string logicalPath, bool raw, DateTime nowUtc)
        {
            if (dataFile == null || dataFile.Length == 0) throw new InvalidDataException("the texture file is empty");
            string logical = (logicalPath ?? DefaultTexture).Replace('\\', '/').TrimStart('/');
            byte[] decoded = Gcbf.IsGcbf(dataFile) ? Gcbf.Decode(dataFile, true) : dataFile;
            if (decoded.Length >= 4 && decoded[0] == 'P' && decoded[1] == 'K')
                throw new InvalidDataException(logical + " is a split-alpha zip (SPLIT + rgb.ktx + alpha.ktx, the ETC pack's layout for textures with " +
                                               "alpha); pick a texture without alpha, or use the phone's ASTC pack");
            Ktx original = Ktx.Parse(decoded);
            if (original.Block() == null)
                throw new InvalidDataException(logical + " is " + original.FormatName + "; the probe supports ASTC (ASTC pack) and ETC1 (ETC pack)");

            string hashed = DataPack.HashedPath(logical);
            var result = new ProbeResult
            {
                Original = original, LogicalPath = logical, HashedPath = hashed, Gcbf = !raw,
                Pack = original.IsAstc ? "ASTC" : "ETC",
            };
            Add(result, logical, "logical", raw ? ProbeColour.Yellow : ProbeColour.Magenta, original, raw);
            Add(result, hashed, "hashed", raw ? ProbeColour.Cyan : ProbeColour.Green, original, raw);
            Add(result, "Data/" + hashed, "hashed under Data/", raw ? ProbeColour.Orange : ProbeColour.Blue, original, raw);

            var variants = new List<object>();
            foreach (ProbeVariant v in result.Variants)
            {
                variants.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["entry"] = v.Entry,
                    ["naming"] = v.Naming,
                    ["colour"] = v.Colour.Name,
                    ["entryCrc32"] = Hex(Crc32.Compute(v.Bytes)),
                    ["payloadCrc32"] = Hex(Crc32.Compute(v.Payload)),
                    ["size"] = (long)v.Bytes.Length,
                });
            }
            var manifest = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["format"] = 1L,
                ["kind"] = "probe",
                ["tool"] = "McvsOffline.HdTextures probe",
                ["created"] = nowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["probe"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["logicalPath"] = logical,
                    ["hashedPath"] = hashed,
                    ["pack"] = result.Pack,
                    ["texture"] = original.FormatName + " " + original.Width + "x" + original.Height + ", " + original.MipLevels + " mip levels",
                    ["wrap"] = raw ? "raw KTX" : "GCBF",
                    ["originalCrc32"] = Hex(Crc32.Compute(decoded)),
                    ["variants"] = variants,
                },
            };
            result.ManifestJson = MiniJson.Serialize(manifest, true) + "\n";

            var zip = new StoredZipWriter();
            zip.Add(ManifestEntry, Encoding.UTF8.GetBytes(result.ManifestJson));
            foreach (ProbeVariant v in result.Variants) zip.Add(v.Entry, v.Bytes);
            result.Zip = zip.ToArray();
            return result;
        }

        private static void Add(ProbeResult result, string entry, string naming, ProbeColour colour, Ktx original, bool raw)
        {
            byte[] ktx = ProbeTexture.Recolour(original, colour).Write();
            result.Variants.Add(new ProbeVariant
            {
                Entry = entry, Naming = naming, Colour = colour, Payload = ktx, Bytes = raw ? ktx : GcbfWriter.Encode(ktx),
            });
        }

        private static string Hex(uint crc)
        {
            return crc.ToString("x8", CultureInfo.InvariantCulture);
        }
    }
}
