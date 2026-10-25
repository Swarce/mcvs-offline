using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using McvsOffline.Patcher.Zip;

namespace McvsOffline.Patcher.Signing
{
    /// <summary>
    /// Fallback signer for players without the Android build-tools: JAR signing (v1, needed by Android 5-6, minSdk 21)
    /// plus APK Signature Scheme v2 (Android 7+), RSA with SHA-256, the same layout apksigner produces for this
    /// minSdk. apksigner is preferred when it is available (it also adds v3); <c>apksigner verify</c> accepts this
    /// output (tests/McvsOffline.Embed.Tests checks it when build-tools are installed).
    /// </summary>
    public static class BuiltInSigner
    {
        public const string SignerName = "CERT";
        private const string CreatedBy = "1.0 (Android)";
        private const uint V2BlockId = 0x7109871a;
        private const uint RsaPkcs1Sha256 = 0x0103;
        private const int ChunkSize = 1024 * 1024;

        /// <summary>Signs an aligned, unsigned APK (entries as produced by <see cref="ZipArchiveWriter"/>).</summary>
        public static byte[] Sign(IList<ZipEntry> entries, X509Certificate2 certificate)
        {
            RSA key = certificate.GetRSAPrivateKey() ?? throw new ArgumentException("RSA private key required");

            // v1: MANIFEST.MF, CERT.SF and CERT.RSA, appended after every other entry.
            var all = entries.Where(e => !IsSignatureFile(e.Name)).ToList();
            ZipEntry template = all.FirstOrDefault();
            byte[] manifest = BuildManifest(all, out Dictionary<string, byte[]> sections);
            byte[] signatureFile = BuildSignatureFile(manifest, sections);
            byte[] signatureBlock = BuildPkcs7(signatureFile, certificate, key);
            all.Add(ZipEntry.CreateDeflated("META-INF/MANIFEST.MF", manifest, template));
            all.Add(ZipEntry.CreateDeflated("META-INF/" + SignerName + ".SF", signatureFile, template));
            all.Add(ZipEntry.CreateDeflated("META-INF/" + SignerName + ".RSA", signatureBlock, template));

            // v2 over the v1-signed zip.
            byte[] zip = ZipArchiveWriter.Write(all);
            return AddV2Signature(zip, certificate, key);
        }

        /// <summary>JAR signature files that must not be digested (and are replaced when re-signing).</summary>
        public static bool IsSignatureFile(string name)
        {
            if (!name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) return false;
            string file = name.Substring("META-INF/".Length);
            if (file.Contains('/')) return false;
            string upper = file.ToUpperInvariant();
            return upper == "MANIFEST.MF" || upper.EndsWith(".SF") || upper.EndsWith(".RSA") || upper.EndsWith(".DSA") ||
                   upper.EndsWith(".EC") || upper.StartsWith("SIG-");
        }

        // ------------------------------------------------------------------ v1 (JAR signing)

        private static byte[] BuildManifest(List<ZipEntry> entries, out Dictionary<string, byte[]> sections)
        {
            sections = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var output = new MemoryStream();
            WriteAscii(output, "Manifest-Version: 1.0\r\nCreated-By: " + CreatedBy + "\r\n\r\n");
            using (SHA256 sha = SHA256.Create())
            {
                foreach (ZipEntry e in entries.Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal))
                {
                    string digest = Convert.ToBase64String(sha.ComputeHash(e.GetData()));
                    var section = new MemoryStream();
                    WriteAttribute(section, "Name", e.Name);
                    WriteAttribute(section, "SHA-256-Digest", digest);
                    WriteAscii(section, "\r\n");
                    byte[] bytes = section.ToArray();
                    sections[e.Name] = bytes;
                    output.Write(bytes, 0, bytes.Length);
                }
            }
            return output.ToArray();
        }

        private static byte[] BuildSignatureFile(byte[] manifest, Dictionary<string, byte[]> sections)
        {
            var output = new MemoryStream();
            using (SHA256 sha = SHA256.Create())
            {
                WriteAscii(output, "Signature-Version: 1.0\r\nCreated-By: " + CreatedBy + "\r\n");
                WriteAttribute(output, "SHA-256-Digest-Manifest", Convert.ToBase64String(sha.ComputeHash(manifest)));
                // Tells Android 7+ that a v2 signature must be present (protects against stripping it).
                WriteAscii(output, "X-Android-APK-Signed: 2\r\n\r\n");
                foreach (var kv in sections)
                {
                    WriteAttribute(output, "Name", kv.Key);
                    WriteAttribute(output, "SHA-256-Digest", Convert.ToBase64String(sha.ComputeHash(kv.Value)));
                    WriteAscii(output, "\r\n");
                }
            }
            return output.ToArray();
        }

        /// <summary>"Key: value" wrapped at 72 bytes per line, continuation lines starting with one space.</summary>
        private static void WriteAttribute(Stream output, string key, string value)
        {
            byte[] line = Encoding.UTF8.GetBytes(key + ": " + value);
            int p = 0;
            bool firstLine = true;
            while (p < line.Length)
            {
                int max = firstLine ? 72 : 71;
                int n = Math.Min(max, line.Length - p);
                // Never split a UTF-8 sequence.
                while (n > 1 && p + n < line.Length && (line[p + n] & 0xC0) == 0x80) n--;
                if (!firstLine) output.WriteByte((byte)' ');
                output.Write(line, p, n);
                output.WriteByte((byte)'\r');
                output.WriteByte((byte)'\n');
                p += n;
                firstLine = false;
            }
        }

        private static void WriteAscii(Stream output, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
        }

        /// <summary>Detached PKCS#7 SignedData over the .SF file, no signed attributes (what apksigner emits).</summary>
        private static byte[] BuildPkcs7(byte[] signatureFile, X509Certificate2 certificate, RSA key)
        {
            const string Sha256 = "2.16.840.1.101.3.4.2.1";
            const string RsaEncryption = "1.2.840.113549.1.1.1";
            byte[] signature = key.SignData(signatureFile, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var w = new AsnWriter(AsnEncodingRules.DER);
            using (w.PushSequence())                                       // ContentInfo
            {
                w.WriteObjectIdentifier("1.2.840.113549.1.7.2");           // signedData
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                using (w.PushSequence())                                   // SignedData
                {
                    w.WriteInteger(1);
                    using (w.PushSetOf())
                    {
                        WriteAlgorithm(w, Sha256);
                    }
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier("1.2.840.113549.1.7.1");   // data, detached
                    }
                    using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    {
                        w.WriteEncodedValue(certificate.RawData);
                    }
                    using (w.PushSetOf())
                    using (w.PushSequence())                               // SignerInfo
                    {
                        w.WriteInteger(1);
                        using (w.PushSequence())                           // IssuerAndSerialNumber
                        {
                            w.WriteEncodedValue(certificate.IssuerName.RawData);
                            w.WriteInteger(certificate.SerialNumberBytes.Span);
                        }
                        WriteAlgorithm(w, Sha256);
                        WriteAlgorithm(w, RsaEncryption);
                        w.WriteOctetString(signature);
                    }
                }
            }
            return w.Encode();
        }

        private static void WriteAlgorithm(AsnWriter w, string oid)
        {
            using (w.PushSequence())
            {
                w.WriteObjectIdentifier(oid);
                w.WriteNull();
            }
        }

        // ------------------------------------------------------------------ v2 (APK Signature Scheme v2)

        /// <summary>Inserts an APK Signing Block with a v2 signer before the central directory of <paramref name="zip"/>.</summary>
        public static byte[] AddV2Signature(byte[] zip, X509Certificate2 certificate, RSA key)
        {
            int eocd = FindEocd(zip);
            int cdOffset = (int)ReadU32(zip, eocd + 16);
            int cdSize = (int)ReadU32(zip, eocd + 12);
            if (cdOffset + cdSize != eocd) throw new InvalidDataException("unexpected data between the central directory and its end record");

            // Digested sections: entries, central directory, end record (its CD offset is the block offset = cdOffset).
            byte[] digest = ContentDigest(new[]
            {
                new ArraySegment<byte>(zip, 0, cdOffset),
                new ArraySegment<byte>(zip, cdOffset, cdSize),
                new ArraySegment<byte>(zip, eocd, zip.Length - eocd),
            });

            byte[] signedData = Concat(
                LengthPrefixed(LengthPrefixed(Concat(U32(RsaPkcs1Sha256), LengthPrefixed(digest)))),
                LengthPrefixed(LengthPrefixed(certificate.RawData)),
                LengthPrefixed(new byte[0]));
            byte[] signature = key.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            byte[] publicKey = certificate.PublicKey.ExportSubjectPublicKeyInfo();
            byte[] signer = Concat(
                LengthPrefixed(signedData),
                LengthPrefixed(LengthPrefixed(Concat(U32(RsaPkcs1Sha256), LengthPrefixed(signature)))),
                LengthPrefixed(publicKey));
            byte[] v2Value = LengthPrefixed(LengthPrefixed(signer));

            byte[] pair = Concat(U64((ulong)(4 + v2Value.Length)), U32(V2BlockId), v2Value);
            ulong blockSize = (ulong)(pair.Length + 8 + 16);
            byte[] block = Concat(U64(blockSize), pair, U64(blockSize), Encoding.ASCII.GetBytes("APK Sig Block 42"));

            var output = new byte[zip.Length + block.Length];
            Buffer.BlockCopy(zip, 0, output, 0, cdOffset);
            Buffer.BlockCopy(block, 0, output, cdOffset, block.Length);
            Buffer.BlockCopy(zip, cdOffset, output, cdOffset + block.Length, zip.Length - cdOffset);
            int newEocd = eocd + block.Length;
            WriteU32(output, newEocd + 16, (uint)(cdOffset + block.Length));
            return output;
        }

        /// <summary>Chunked SHA-256: 1 MiB chunks, 0xa5 + length + chunk each, then 0x5a + count + chunk digests.</summary>
        private static byte[] ContentDigest(IEnumerable<ArraySegment<byte>> sections)
        {
            var chunkDigests = new MemoryStream();
            int count = 0;
            using (SHA256 sha = SHA256.Create())
            {
                foreach (ArraySegment<byte> section in sections)
                {
                    for (int offset = 0; offset < section.Count; offset += ChunkSize)
                    {
                        int length = Math.Min(ChunkSize, section.Count - offset);
                        sha.Initialize();
                        byte[] prefix = Concat(new byte[] { 0xa5 }, U32((uint)length));
                        sha.TransformBlock(prefix, 0, prefix.Length, null, 0);
                        sha.TransformFinalBlock(section.Array, section.Offset + offset, length);
                        chunkDigests.Write(sha.Hash, 0, sha.Hash.Length);
                        count++;
                    }
                }
                byte[] top = Concat(new byte[] { 0x5a }, U32((uint)count), chunkDigests.ToArray());
                return sha.ComputeHash(top);
            }
        }

        private static int FindEocd(byte[] zip)
        {
            for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 22 - 0xFFFF); i--)
            {
                if (ReadU32(zip, i) == 0x06054b50 && i + 22 + (zip[i + 20] | zip[i + 21] << 8) == zip.Length) return i;
            }
            throw new InvalidDataException("no end of central directory record");
        }

        private static byte[] LengthPrefixed(byte[] data)
        {
            return Concat(U32((uint)data.Length), data);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var result = new byte[parts.Sum(p => p.Length)];
            int o = 0;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, result, o, p.Length);
                o += p.Length;
            }
            return result;
        }

        private static byte[] U32(uint v)
        {
            return new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
        }

        private static byte[] U64(ulong v)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
            return b;
        }

        private static uint ReadU32(byte[] d, int o)
        {
            return (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
        }

        private static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)v;
            d[o + 1] = (byte)(v >> 8);
            d[o + 2] = (byte)(v >> 16);
            d[o + 3] = (byte)(v >> 24);
        }
    }
}
