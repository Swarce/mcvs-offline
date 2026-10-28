using System;
using System.IO;
using System.Security.Cryptography;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The Steam build the research verified (pc-port.md §1.1: app 723780, depot 723781, build 6372049, March 2021,
    /// game version 1.17.30). The kit only reports a mismatch; the redirect test refuses through the IL fingerprint.
    /// </summary>
    public static class KnownPcBuild
    {
        public const string GameVersion = "1.17.30";
        public const string ClientId = "2620:70425:1.17.30:steam:steam";
        public const string ExeSha256 = "884f74611cb0daccbe889b054bda16d6ba96d5da4911e1bcd38d38d675231852";
        public const string EngineBridgeSha256 = "ce2655eea9ed0d31363bb3424eb683c12d583d9408ecbd7a2a082d2a4a97a595";
        public const string EngineBridgeMvid = "b5913b9a-f9dc-42d6-bd3a-435a8280e8f1";

        /// <summary>COFF Characteristics of ModernCombatVersus.exe: EXECUTABLE_IMAGE | 32BIT_MACHINE, no LARGE_ADDRESS_AWARE (R2).</summary>
        public const int ExeCharacteristics = 0x0102;

        public static string Sha256(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            }
        }

        /// <summary>"known (build 6372049)" or "UNKNOWN (sha256 ...)" for summary lines; "missing" when absent.</summary>
        public static string Describe(string file, string expectedSha256)
        {
            if (!File.Exists(file)) return "missing";
            try
            {
                string actual = Sha256(file);
                return actual == expectedSha256 ? "known (Steam build 6372049, sha256 " + actual + ")" : "UNKNOWN build (sha256 " + actual + ")";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "unreadable: " + ex.Message;
            }
        }
    }

    /// <summary>The COFF header fields of a PE image the summary reports (R2: the 2 GB limit of a 32-bit exe).</summary>
    public static class PeHeader
    {
        public const int LargeAddressAware = 0x0020;

        /// <summary>Machine and Characteristics of a PE file; throws <see cref="InvalidDataException"/> when it is not one.</summary>
        public static void Read(string file, out int machine, out int characteristics)
        {
            using (var stream = File.OpenRead(file))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException(file + " is not an MZ image");
                stream.Position = 0x3C;
                int peOffset = reader.ReadInt32();
                if (peOffset < 0 || peOffset > stream.Length - 24) throw new InvalidDataException(file + ": bad PE offset");
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException(file + ": no PE signature");
                machine = reader.ReadUInt16();
                stream.Position = peOffset + 4 + 18;
                characteristics = reader.ReadUInt16();
            }
        }

        public static string Describe(string file)
        {
            try
            {
                int machine, characteristics;
                Read(file, out machine, out characteristics);
                string arch = machine == 0x14c ? "i386 (32-bit)" : machine == 0x8664 ? "x64" : "machine 0x" + machine.ToString("x4");
                bool laa = (characteristics & LargeAddressAware) != 0;
                return arch + ", Characteristics 0x" + characteristics.ToString("x4") + ", large-address-aware: " + (laa ? "yes (4 GB on 64-bit Windows)" : "no (2 GB address space)");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                return "unreadable: " + ex.Message;
            }
        }
    }
}
