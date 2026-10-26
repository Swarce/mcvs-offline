using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>One part of a transfer text, as read from a paste or a file.</summary>
    public sealed class TransferPart
    {
        public string Set { get; set; }

        public int Number { get; set; }

        public int Total { get; set; }

        /// <summary>'d' (raw deflate) or 's' (stored).</summary>
        public char Method { get; set; }

        /// <summary>Length of the whole binary stream.</summary>
        public long Bytes { get; set; }

        /// <summary>CRC-32 of the whole binary stream (8 lower-case hex).</summary>
        public string Crc { get; set; }

        /// <summary>This part's base64 characters, whitespace removed.</summary>
        public string Data { get; set; }

        /// <summary>True when <paramref name="other"/> describes the same export (count, method, size and CRC).</summary>
        public bool SameExport(TransferPart other)
        {
            return other != null && Set == other.Set && Total == other.Total && Method == other.Method && Bytes == other.Bytes
                   && Crc == other.Crc;
        }
    }

    /// <summary>A part that could not be used: its number (0 when unreadable) and why, in words for the player.</summary>
    public sealed class TransferRejection
    {
        public TransferRejection(string set, int part, string reason)
        {
            Set = set;
            Part = part;
            Reason = reason;
        }

        public string Set { get; private set; }

        public int Part { get; private set; }

        public string Reason { get; private set; }
    }

    /// <summary>An encoded save: the set id and the part texts (each "MCVS-SAVE:...:\n&lt;data&gt;\n~", unwrapped).</summary>
    public sealed class TransferExport
    {
        public string Set { get; set; }

        public char Method { get; set; }

        /// <summary>Length of the binary stream (deflated or stored bundle).</summary>
        public long Bytes { get; set; }

        public int PartChars { get; set; }

        public List<string> Parts { get; set; }

        /// <summary>Total characters of every part text (what the player copies).</summary>
        public long Chars
        {
            get
            {
                long chars = 0;
                foreach (string p in Parts) chars += p.Length;
                return chars;
            }
        }
    }

    /// <summary>The transfer text failed a check after every part was there (length, CRC, base64, inflate).</summary>
    public sealed class TransferException : Exception
    {
        public TransferException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>The binary stream inflates to more than <see cref="SaveTransfer.MaxInflatedBytes"/> (answered 413).</summary>
    public sealed class TransferTooLargeException : Exception
    {
        public TransferTooLargeException(long limit) : base("the save is larger than " + limit.ToString(CultureInfo.InvariantCulture) + " bytes")
        {
            Limit = limit;
        }

        public long Limit { get; private set; }
    }

    /// <summary>
    /// The text form of a save bundle that survives the clipboard, chat apps and text viewers (design §6.2):
    /// <code>
    /// MCVS-SAVE:1:&lt;set&gt;:&lt;k&gt;/&lt;n&gt;:&lt;method&gt;:&lt;bytes&gt;:&lt;crc&gt;:&lt;partcrc&gt;:
    /// &lt;data&gt;
    /// ~
    /// </code>
    /// <c>set</c> 8 lower-case hex, random per export; 1 ≤ k ≤ n ≤ 999; method <c>d</c> = raw deflate (RFC 1951) of the
    /// bundle's UTF-8 bytes, <c>s</c> = stored; <c>bytes</c> and <c>crc</c> describe the binary stream (≤ 16 MiB),
    /// <c>partcrc</c> this part's data characters after whitespace removal. Data is standard base64 without padding
    /// (base64url's '_' and '*' are markdown in Telegram and Discord) cut into parts by characters; space, tab, CR, LF and
    /// U+00A0 inside data are ignored, anything else makes the part damaged; '~' ends a part. Text outside parts is
    /// ignored, so one paste may hold any number of parts, in any order, with chat noise around them.
    /// </summary>
    public static class SaveTransfer
    {
        public const string Marker = "MCVS-SAVE:";
        public const int FormatVersion = 1;
        public const int MaxBinaryBytes = 16 * 1024 * 1024;
        public const int MaxInflatedBytes = 16 * 1024 * 1024;
        public const int MaxParts = 999;

        /// <summary>One or two whole parts fit one clipboard operation (128,000 characters, §7.4).</summary>
        public const int DefaultPartChars = 60000;

        /// <summary>Telegram's 4,096-character message limit.</summary>
        public const int TelegramPartChars = 3900;

        /// <summary>Discord's 2,000-character message limit.</summary>
        public const int DiscordPartChars = 1900;

        /// <summary>Data characters per line in the .txt export.</summary>
        public const int LineWidth = 76;

        public const string Damaged = "damaged (copied incompletely?)";
        public const string OtherExport = "belongs to another export";

        private static readonly object ProbeGate = new object();
        private static bool probed;
        private static bool canDeflate;

        public static bool IsPartSize(long chars)
        {
            return chars == DefaultPartChars || chars == TelegramPartChars || chars == DiscordPartChars;
        }

        // ------------------------------------------------------------------ deflate

        /// <summary>
        /// Whether deflate works here: one compress/inflate round trip of a small buffer at first use. On the phone the
        /// zlib calls of System.dll's DeflateStream are Mono internal calls into the system libz; on any failure one WARN is
        /// logged and exports are stored (method 's'). Imports accept both methods either way.
        /// </summary>
        public static bool CanDeflate(Log log)
        {
            lock (ProbeGate)
            {
                if (probed) return canDeflate;
                probed = true;
                string problem;
                canDeflate = Probe(Deflate, data => Inflate(data, 1024), out problem);
                if (!canDeflate && log != null) log.Warn("admin: deflate does not work here (" + problem + "); saves and log zips are stored uncompressed");
                return canDeflate;
            }
        }

        /// <summary>One round trip through the given functions; false (and why) on any exception or a wrong result.</summary>
        internal static bool Probe(Func<byte[], byte[]> deflate, Func<byte[], byte[]> inflate, out string problem)
        {
            problem = null;
            try
            {
                byte[] sample = Encoding.UTF8.GetBytes("MCVS Offline deflate probe, MCVS Offline deflate probe, 0123456789");
                byte[] back = inflate(deflate(sample));
                if (back.Length != sample.Length) problem = "round trip changed the length";
                else
                {
                    for (int i = 0; i < sample.Length; i++)
                    {
                        if (back[i] == sample[i]) continue;
                        problem = "round trip changed the data";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
            }
            return problem == null;
        }

        /// <summary>Raw deflate (RFC 1951, no zlib header) of <paramref name="data"/>.</summary>
        public static byte[] Deflate(byte[] data)
        {
            var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
            {
                deflate.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        /// <summary>Inflates raw deflate data; more than <paramref name="maxBytes"/> of output throws <see cref="TransferTooLargeException"/>.</summary>
        public static byte[] Inflate(byte[] data, int maxBytes)
        {
            var output = new MemoryStream();
            using (var inflate = new DeflateStream(new MemoryStream(data, false), CompressionMode.Decompress, false))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int n;
                while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    if (total > maxBytes) throw new TransferTooLargeException(maxBytes);
                    output.Write(buffer, 0, n);
                }
            }
            return output.ToArray();
        }

        // ------------------------------------------------------------------ writing

        /// <summary>
        /// Encodes a bundle's UTF-8 bytes in parts of <paramref name="partChars"/> data characters. With
        /// <paramref name="deflate"/> the bytes are deflated (method 'd'), unless that does not make them smaller.
        /// </summary>
        public static TransferExport Encode(byte[] bundle, int partChars, string set, bool deflate)
        {
            if (bundle == null) throw new ArgumentNullException("bundle");
            if (partChars < 4) throw new ArgumentOutOfRangeException("partChars");
            if (!AdminFiles.IsLowerHex(set, 8)) throw new ArgumentException("set must be 8 lower-case hex", "set");
            char method = 's';
            byte[] stream = bundle;
            if (deflate)
            {
                byte[] deflated = Deflate(bundle);
                if (deflated.Length < bundle.Length)
                {
                    stream = deflated;
                    method = 'd';
                }
            }
            if (stream.Length > MaxBinaryBytes) throw new TransferTooLargeException(MaxBinaryBytes);
            string data = Convert.ToBase64String(stream).TrimEnd('=');
            int count = Math.Max(1, (data.Length + partChars - 1) / partChars);
            if (count > MaxParts) throw new TransferTooLargeException(MaxBinaryBytes);
            string crc = Hex(Crc32.Compute(stream));
            string bytes = stream.Length.ToString(CultureInfo.InvariantCulture);
            var parts = new List<string>(count);
            for (int k = 0; k < count; k++)
            {
                int start = k * partChars;
                string chunk = data.Substring(start, Math.Min(partChars, data.Length - start));
                var sb = new StringBuilder(chunk.Length + 80);
                sb.Append(Marker).Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append(':').Append(set).Append(':')
                  .Append((k + 1).ToString(CultureInfo.InvariantCulture)).Append('/').Append(count.ToString(CultureInfo.InvariantCulture))
                  .Append(':').Append(method).Append(':').Append(bytes).Append(':').Append(crc).Append(':')
                  .Append(Hex(Crc32.Compute(Encoding.ASCII.GetBytes(chunk)))).Append(":\n").Append(chunk).Append("\n~");
                parts.Add(sb.ToString());
            }
            return new TransferExport { Set = set, Method = method, Bytes = stream.Length, PartChars = partChars, Parts = parts };
        }

        /// <summary>
        /// The .txt export: <paramref name="header"/> lines, then every part with its data wrapped at 76 characters per line,
        /// parts separated by an empty line.
        /// </summary>
        public static string FileText(TransferExport export, string header)
        {
            var sb = new StringBuilder((int)Math.Min(64L * 1024 * 1024, export.Chars + export.Chars / LineWidth + 1024));
            if (!string.IsNullOrEmpty(header)) sb.Append(header.TrimEnd('\n')).Append("\n\n");
            for (int i = 0; i < export.Parts.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                string part = export.Parts[i];
                int headerEnd = part.IndexOf('\n');
                int dataEnd = part.LastIndexOf('\n');
                sb.Append(part, 0, headerEnd + 1);
                for (int p = headerEnd + 1; p < dataEnd; p += LineWidth)
                {
                    sb.Append(part, p, Math.Min(LineWidth, dataEnd - p)).Append('\n');
                }
                sb.Append('~');
            }
            sb.Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// Every part in <paramref name="text"/>, in the order found. Parts that cannot be used (unreadable header, a
        /// character that does not belong, no closing '~', a failed part CRC) go to <paramref name="rejected"/>.
        /// </summary>
        public static List<TransferPart> Extract(string text, List<TransferRejection> rejected)
        {
            var parts = new List<TransferPart>();
            if (string.IsNullOrEmpty(text)) return parts;
            int pos = 0;
            while (true)
            {
                int marker = text.IndexOf(Marker, pos, StringComparison.Ordinal);
                if (marker < 0) break;
                int next;
                TransferPart part = ReadPart(text, marker + Marker.Length, rejected, out next);
                if (part != null) parts.Add(part);
                pos = Math.Max(next, marker + Marker.Length);
            }
            return parts;
        }

        /// <summary>Reads one part after its marker; <paramref name="next"/> is where scanning continues.</summary>
        private static TransferPart ReadPart(string s, int pos, List<TransferRejection> rejected, out int next)
        {
            next = pos;
            var fields = new string[7];
            int p = pos;
            for (int f = 0; f < fields.Length; f++)
            {
                int colon = s.IndexOf(':', p);
                if (colon < 0 || colon - p > 16)
                {
                    Reject(rejected, null, 0, Damaged);
                    return null;
                }
                fields[f] = s.Substring(p, colon - p);
                p = colon + 1;
            }
            next = p;

            string set = fields[1];
            int k = 0, n = 0;
            int slash = fields[2].IndexOf('/');
            bool numbers = slash > 0 && TryDigits(fields[2].Substring(0, slash), 3, out k) && TryDigits(fields[2].Substring(slash + 1), 3, out n);
            long bytes;
            bool headerOk = fields[0] == "1" && AdminFiles.IsLowerHex(set, 8) && numbers && k >= 1 && k <= n && n <= MaxParts
                            && (fields[3] == "d" || fields[3] == "s") && TryLong(fields[4], out bytes) && bytes > 0
                            && bytes <= MaxBinaryBytes && AdminFiles.IsLowerHex(fields[5], 8) && AdminFiles.IsLowerHex(fields[6], 8);
            if (!headerOk)
            {
                if (fields[0] == "1" && numbers && k >= 1) Reject(rejected, AdminFiles.IsLowerHex(set, 8) ? set : null, k, Damaged);
                else Reject(rejected, null, 0, fields[0] == "1" ? Damaged : "made by a newer version of MCVS Offline");
                return null;
            }
            TryLong(fields[4], out bytes);

            // The data, up to '~'. Another marker before it means this part was cut off.
            long maxData = (MaxBinaryBytes / 3 + 1) * 4L;
            var data = new StringBuilder();
            while (true)
            {
                if (p >= s.Length)
                {
                    next = p;
                    Reject(rejected, set, k, Damaged);
                    return null;
                }
                char c = s[p];
                if (c == '~')
                {
                    next = p + 1;
                    break;
                }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ' ')
                {
                    p++;
                    continue;
                }
                if (c == 'M' && string.CompareOrdinal(s, p, Marker, 0, Marker.Length) == 0)
                {
                    // The next part starts before this one ended: this one was cut off.
                    next = p;
                    Reject(rejected, set, k, Damaged);
                    return null;
                }
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/')
                {
                    data.Append(c);
                    if (data.Length > maxData)
                    {
                        next = p;
                        Reject(rejected, set, k, Damaged);
                        return null;
                    }
                    p++;
                    continue;
                }
                // Anything else (another part's marker, chat text after a cut-off copy...): this part is damaged; the
                // scan continues right here, so a following part is still found.
                next = p;
                Reject(rejected, set, k, Damaged);
                return null;
            }

            string chunk = data.ToString();
            if (chunk.Length == 0 || chunk.Length > DataChars(bytes) || Hex(Crc32.Compute(Encoding.ASCII.GetBytes(chunk))) != fields[6])
            {
                Reject(rejected, set, k, Damaged);
                return null;
            }
            return new TransferPart
            {
                Set = set, Number = k, Total = n, Method = fields[3][0], Bytes = bytes, Crc = fields[5], Data = chunk,
            };
        }

        /// <summary>
        /// The bundle's UTF-8 bytes from all parts of one export (sorted by number, all present). Throws
        /// <see cref="TransferException"/> when the stream does not match its length or CRC or does not inflate, and
        /// <see cref="TransferTooLargeException"/> above 16 MiB.
        /// </summary>
        public static byte[] Decode(IList<TransferPart> parts)
        {
            if (parts == null || parts.Count == 0) throw new TransferException("no parts");
            TransferPart first = parts[0];
            long expected = DataChars(first.Bytes);
            var data = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].Number != i + 1 || !parts[i].SameExport(first) || first.Total != parts.Count)
                    throw new TransferException("the parts do not form one complete export");
                if (data.Length + parts[i].Data.Length > expected) throw new TransferException("the parts hold more data than the export");
                data.Append(parts[i].Data);
            }
            if (data.Length != expected) throw new TransferException("the parts hold less data than the export");
            int remainder = data.Length % 4;
            if (remainder == 1) throw new TransferException("the data has an impossible length");
            if (remainder == 2) data.Append("==");
            else if (remainder == 3) data.Append('=');
            byte[] stream;
            try
            {
                stream = Convert.FromBase64String(data.ToString());
            }
            catch (FormatException ex)
            {
                throw new TransferException("the data is not base64", ex);
            }
            if (stream.Length != first.Bytes || Hex(Crc32.Compute(stream)) != first.Crc)
                throw new TransferException("the data does not match its length or checksum");
            if (first.Method == 's') return stream;
            try
            {
                return Inflate(stream, MaxInflatedBytes);
            }
            catch (TransferTooLargeException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new TransferException("the data does not inflate", ex);
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Base64 characters (without padding) of a binary stream of <paramref name="bytes"/> bytes.</summary>
        public static long DataChars(long bytes)
        {
            return (bytes * 4 + 2) / 3;
        }

        public static string Hex(uint crc)
        {
            return crc.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static void Reject(List<TransferRejection> rejected, string set, int part, string reason)
        {
            if (rejected != null) rejected.Add(new TransferRejection(set, part, reason));
        }

        private static bool TryDigits(string s, int maxDigits, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s) || s.Length > maxDigits) return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        private static bool TryLong(string s, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s) || s.Length > 9) return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }
    }
}
