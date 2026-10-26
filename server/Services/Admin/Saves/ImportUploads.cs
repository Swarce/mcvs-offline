using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin.Saves
{
    /// <summary>What one paste did: the state of the export it added parts to, or the whole bundle once complete.</summary>
    internal sealed class PasteOutcome
    {
        public PasteOutcome()
        {
            Have = new List<int>();
            Missing = new List<int>();
            Rejected = new List<TransferRejection>();
        }

        /// <summary>Every part of the export is there (or the paste was a whole bundle).</summary>
        public bool Complete { get; set; }

        public string Set { get; set; }

        public int Total { get; set; }

        public List<int> Have { get; private set; }

        public List<int> Missing { get; private set; }

        public List<TransferRejection> Rejected { get; private set; }

        /// <summary>One or two sentences for the player ("Got parts 1 and 3 of 3. Paste part 2.").</summary>
        public string Message { get; set; }

        /// <summary>The bundle text (UTF-8 decoded) when complete and decodable.</summary>
        public string BundleText { get; set; }

        /// <summary>Complete, but the parts did not decode to the save they describe.</summary>
        public bool Undecodable { get; set; }

        /// <summary>Complete, but larger than 16 MiB once inflated (413).</summary>
        public bool TooLarge { get; set; }

        /// <summary>The text holds no part and is not a JSON object.</summary>
        public bool NothingFound { get; set; }
    }

    /// <summary>A validated upload kept for staging.</summary>
    internal sealed class UploadRecord
    {
        public string Id { get; set; }

        public DateTime CreatedUtc { get; set; }

        /// <summary>"paste" or "file".</summary>
        public string SourceType { get; set; }

        /// <summary>The import file's name, or null.</summary>
        public string Name { get; set; }

        /// <summary>The bundle (or raw profile) JSON text.</summary>
        public string Text { get; set; }
    }

    /// <summary>
    /// The server side of imports (design §4.6, §6.2), so a re-created browser loses nothing: transfer parts collected
    /// across pastes in &lt;data&gt;/admin/uploads/set-&lt;set&gt;/ (part-&lt;k&gt;.txt plus meta.json; a set lives one hour,
    /// the newest 3 are kept), and validated uploads in &lt;data&gt;/admin/uploads/&lt;uploadId&gt;.json (one hour, newest 3).
    /// Every file name is built from validated values (8-hex set, part number ≤ 999, <c>u-</c> + 16 hex).
    /// </summary>
    internal sealed class ImportUploads
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
        public const int KeepUploads = 3;
        public const int KeepSets = 3;

        private const string SetPrefix = "set-";
        private const string MetaFile = "meta.json";

        private readonly string folder;
        private readonly IClock clock;
        private readonly Log log;
        private readonly object gate = new object();

        public ImportUploads(string folder, IClock clock, Log log)
        {
            this.folder = folder;
            this.clock = clock;
            this.log = log ?? Log.Null;
            AtomicFile.DeleteLeftovers(folder);
        }

        public string Folder
        {
            get { return folder; }
        }

        public static bool IsUploadId(string id)
        {
            return id != null && id.Length == 18 && id.StartsWith("u-", StringComparison.Ordinal) && AdminFiles.IsLowerHex(id.Substring(2), 16);
        }

        // ------------------------------------------------------------------ pastes

        /// <summary>
        /// Adds every transfer part of a pasted text to its set and reports on the set of the last part found. A text without
        /// any part that starts with '{' is taken as a whole bundle or raw profile file.
        /// </summary>
        public PasteOutcome AddPaste(string text)
        {
            var rejected = new List<TransferRejection>();
            List<TransferPart> parts = SaveTransfer.Extract(text, rejected);
            if (parts.Count == 0 && rejected.Count == 0)
            {
                string trimmed = (text ?? "").Trim();
                if (trimmed.StartsWith("{", StringComparison.Ordinal))
                    return new PasteOutcome { Complete = true, BundleText = trimmed, Message = "The whole save was pasted." };
                return new PasteOutcome
                {
                    NothingFound = true,
                    Message = "No part of an MCVS Offline save was found in the pasted text (everything from MCVS-SAVE to ~).",
                };
            }

            lock (gate)
            {
                Prune();
                string lastSet = null;
                var outcome = new PasteOutcome();
                foreach (TransferPart part in parts)
                {
                    TransferPart meta = LoadMeta(part.Set);
                    if (meta != null && !meta.SameExport(part))
                    {
                        outcome.Rejected.Add(new TransferRejection(part.Set, part.Number, SaveTransfer.OtherExport));
                        continue;
                    }
                    if (meta != null && StoredChars(part.Set, meta.Total, part.Number) + part.Data.Length > SaveTransfer.DataChars(part.Bytes))
                    {
                        // More data than the export holds: parts of a different cut of the same set.
                        outcome.Rejected.Add(new TransferRejection(part.Set, part.Number, SaveTransfer.OtherExport));
                        continue;
                    }
                    string dir = Path.Combine(folder, SetPrefix + part.Set);
                    if (AdminFiles.IsLink(dir)) throw new IOException("refusing to use " + dir + ": it is a symbolic link");
                    Directory.CreateDirectory(dir);
                    AtomicFile.WriteAllText(PartPath(part.Set, part.Number), part.Data);
                    SaveMeta(part, meta);
                    lastSet = part.Set;
                }
                foreach (TransferRejection r in rejected) outcome.Rejected.Add(r);
                if (lastSet == null)
                {
                    // Nothing usable: report on the set the rejected parts belong to, if it is known.
                    foreach (TransferRejection r in outcome.Rejected)
                    {
                        if (r.Set != null && LoadMeta(r.Set) != null) lastSet = r.Set;
                    }
                }
                if (lastSet == null)
                {
                    outcome.Message = "No usable part in the pasted text." + RejectionText(outcome.Rejected);
                    return outcome;
                }
                Describe(lastSet, outcome);
                if (outcome.Complete) Assemble(LoadParts(lastSet), outcome);
                return outcome;
            }
        }

        /// <summary>The outcome for a text that should hold a whole export (an import file): no set is stored.</summary>
        public static PasteOutcome FromText(string text)
        {
            var rejected = new List<TransferRejection>();
            List<TransferPart> parts = SaveTransfer.Extract(text, rejected);
            var outcome = new PasteOutcome();
            outcome.Rejected.AddRange(rejected);
            if (parts.Count == 0)
            {
                string trimmed = (text ?? "").Trim();
                if (rejected.Count == 0 && trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    outcome.Complete = true;
                    outcome.BundleText = trimmed;
                    return outcome;
                }
                outcome.NothingFound = rejected.Count == 0;
                outcome.Message = rejected.Count == 0 ? "The file holds no MCVS Offline save." : "No usable part in the file." + RejectionText(rejected);
                return outcome;
            }
            // The first export in the file; parts of another one are ignored.
            TransferPart first = parts[0];
            var byNumber = new SortedDictionary<int, TransferPart>();
            foreach (TransferPart part in parts)
            {
                if (!part.SameExport(first))
                {
                    if (part.Set == first.Set) outcome.Rejected.Add(new TransferRejection(part.Set, part.Number, SaveTransfer.OtherExport));
                    continue;
                }
                byNumber[part.Number] = part;
            }
            outcome.Set = first.Set;
            outcome.Total = first.Total;
            for (int k = 1; k <= first.Total; k++)
            {
                if (byNumber.ContainsKey(k)) outcome.Have.Add(k);
                else outcome.Missing.Add(k);
            }
            outcome.Complete = outcome.Missing.Count == 0;
            outcome.Message = PartsMessage(outcome);
            if (outcome.Complete) Assemble(new List<TransferPart>(byNumber.Values), outcome);
            return outcome;
        }

        private static void Assemble(List<TransferPart> parts, PasteOutcome outcome)
        {
            try
            {
                byte[] bundle = SaveTransfer.Decode(parts);
                outcome.BundleText = AdminFiles.Utf8(bundle, 0, bundle.Length);
            }
            catch (TransferTooLargeException)
            {
                outcome.TooLarge = true;
            }
            catch (TransferException)
            {
                outcome.Undecodable = true;
            }
        }

        private void Describe(string set, PasteOutcome outcome)
        {
            TransferPart meta = LoadMeta(set);
            outcome.Set = set;
            outcome.Total = meta.Total;
            for (int k = 1; k <= meta.Total; k++)
            {
                if (File.Exists(PartPath(set, k))) outcome.Have.Add(k);
                else outcome.Missing.Add(k);
            }
            outcome.Complete = outcome.Missing.Count == 0;
            outcome.Message = PartsMessage(outcome);
        }

        private List<TransferPart> LoadParts(string set)
        {
            TransferPart meta = LoadMeta(set);
            var parts = new List<TransferPart>();
            for (int k = 1; k <= meta.Total; k++)
            {
                string data = AtomicFile.ReadAllTextOrNull(PartPath(set, k));
                if (data == null) return parts;
                parts.Add(new TransferPart
                {
                    Set = set, Number = k, Total = meta.Total, Method = meta.Method, Bytes = meta.Bytes, Crc = meta.Crc, Data = data,
                });
            }
            return parts;
        }

        /// <summary>Data characters stored for a set, without part <paramref name="except"/> (which a paste may replace).</summary>
        private long StoredChars(string set, int total, int except)
        {
            long chars = 0;
            for (int k = 1; k <= total; k++)
            {
                if (k == except) continue;
                string path = PartPath(set, k);
                if (File.Exists(path)) chars += new FileInfo(path).Length;
            }
            return chars;
        }

        private string PartPath(string set, int number)
        {
            return Path.Combine(folder, SetPrefix + set, "part-" + number.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        private TransferPart LoadMeta(string set)
        {
            if (!AdminFiles.IsLowerHex(set, 8)) return null;
            string path = Path.Combine(folder, SetPrefix + set, MetaFile);
            Dictionary<string, object> o;
            try
            {
                string text = AdminFiles.IsPlainFile(path) ? AtomicFile.ReadAllTextOrNull(path) : null;
                if (text == null) return null;
                o = MiniJson.ParseObject(text);
            }
            catch (Exception)
            {
                return null;
            }
            string method = JsonValue.GetString(o, "method");
            long total = JsonValue.GetLong(o, "total", 0);
            if (total < 1 || total > SaveTransfer.MaxParts || (method != "d" && method != "s")) return null;
            return new TransferPart
            {
                Set = set, Total = (int)total, Method = method[0], Bytes = JsonValue.GetLong(o, "bytes", 0),
                Crc = JsonValue.GetString(o, "crc"),
            };
        }

        private void SaveMeta(TransferPart part, TransferPart existing)
        {
            string now = TimeFormat.Iso8601Millis(clock.UtcNow);
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            o["set"] = part.Set;
            o["total"] = (long)part.Total;
            o["method"] = part.Method.ToString();
            o["bytes"] = part.Bytes;
            o["crc"] = part.Crc;
            o["updated"] = now;
            AtomicFile.WriteAllText(Path.Combine(folder, SetPrefix + part.Set, MetaFile), MiniJson.Serialize(o));
        }

        // ------------------------------------------------------------------ uploads

        /// <summary>Keeps a validated bundle text for staging; returns its id ("u-" + 16 hex).</summary>
        public string Save(string text, string sourceType, string name)
        {
            lock (gate)
            {
                Prune();
                string id = "u-" + Ids.RandomHex(8);
                var o = new Dictionary<string, object>(StringComparer.Ordinal);
                o["id"] = id;
                o["created"] = TimeFormat.Iso8601Millis(clock.UtcNow);
                o["sourceType"] = sourceType;
                o["name"] = name;
                o["text"] = text;
                AtomicFile.WriteAllText(Path.Combine(folder, id + ".json"), MiniJson.Serialize(o));
                PruneUploads(id);
                return id;
            }
        }

        /// <summary>The upload, or null when the id is malformed, unknown or older than an hour.</summary>
        public UploadRecord Load(string id)
        {
            if (!IsUploadId(id)) return null;
            lock (gate)
            {
                string path = AdminFiles.FileIn(folder, id + ".json");
                if (path == null) return null;
                Dictionary<string, object> o;
                try
                {
                    o = MiniJson.ParseObject(AtomicFile.ReadAllTextOrNull(path));
                }
                catch (Exception)
                {
                    return null;
                }
                DateTime created;
                if (!TimeFormat.TryParse(JsonValue.GetString(o, "created"), out created) || clock.UtcNow - created > Lifetime)
                {
                    AdminFiles.DeleteQuietly(path);
                    return null;
                }
                return new UploadRecord
                {
                    Id = id,
                    CreatedUtc = created,
                    SourceType = JsonValue.GetString(o, "sourceType"),
                    Name = JsonValue.GetString(o, "name"),
                    Text = JsonValue.GetString(o, "text"),
                };
            }
        }

        public void Delete(string id)
        {
            if (!IsUploadId(id)) return;
            lock (gate) AdminFiles.DeleteQuietly(Path.Combine(folder, id + ".json"));
        }

        // ------------------------------------------------------------------ housekeeping

        /// <summary>Drops part sets and uploads older than an hour, and sets beyond the newest 3.</summary>
        private void Prune()
        {
            DateTime now = clock.UtcNow;
            var sets = new List<KeyValuePair<string, DateTime>>();
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(folder);
            }
            catch (Exception)
            {
                dirs = new string[0];
            }
            foreach (string dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (!name.StartsWith(SetPrefix, StringComparison.Ordinal) || !AdminFiles.IsLowerHex(name.Substring(SetPrefix.Length), 8)) continue;
                DateTime updated = DateTime.MinValue;
                try
                {
                    string meta = Path.Combine(dir, MetaFile);
                    string text = AdminFiles.IsPlainFile(meta) ? AtomicFile.ReadAllTextOrNull(meta) : null;
                    if (text != null) TimeFormat.TryParse(JsonValue.GetString(MiniJson.ParseObject(text), "updated"), out updated);
                }
                catch (Exception)
                {
                    updated = DateTime.MinValue;
                }
                sets.Add(new KeyValuePair<string, DateTime>(dir, updated));
            }
            sets.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < sets.Count; i++)
            {
                if (i >= KeepSets || now - sets[i].Value > Lifetime) AdminFiles.DeleteFolderQuietly(sets[i].Key);
            }
            PruneUploads(null);
        }

        private void PruneUploads(string keep)
        {
            DateTime now = clock.UtcNow;
            var uploads = new List<KeyValuePair<string, DateTime>>();
            foreach (FileInfo f in AdminFiles.PlainFiles(folder))
            {
                if (!f.Name.EndsWith(".json", StringComparison.Ordinal) || !IsUploadId(f.Name.Substring(0, f.Name.Length - 5))) continue;
                DateTime created = DateTime.MinValue;
                try
                {
                    TimeFormat.TryParse(ScanCreated(f.FullName), out created);
                }
                catch (Exception)
                {
                    created = DateTime.MinValue;
                }
                uploads.Add(new KeyValuePair<string, DateTime>(f.FullName, created));
            }
            uploads.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < uploads.Count; i++)
            {
                bool isKept = keep != null && Path.GetFileName(uploads[i].Key) == keep + ".json";
                if (isKept) continue;
                if (i >= KeepUploads || now - uploads[i].Value > Lifetime) AdminFiles.DeleteQuietly(uploads[i].Key);
            }
        }

        /// <summary>The "created" member of an upload file without parsing the whole (large) file.</summary>
        private static string ScanCreated(string path)
        {
            string text = AtomicFile.ReadAllTextOrNull(path);
            int start, length;
            if (text == null || !JsonSpanScanner.TryFindValue(text, new[] { "created" }, out start, out length) || length > 64) return null;
            object value;
            return MiniJson.TryParse(text.Substring(start, length), out value) ? value as string : null;
        }

        // ------------------------------------------------------------------ wording

        internal static string PartsMessage(PasteOutcome outcome)
        {
            if (outcome.Complete) return "All " + Count(outcome.Total, "part") + " received.";
            var sb = new StringBuilder();
            if (outcome.Have.Count == 0) sb.Append("No part of ").Append(outcome.Total.ToString(CultureInfo.InvariantCulture)).Append(" received yet.");
            else
            {
                sb.Append(outcome.Have.Count == 1 ? "Got part " : "Got parts ").Append(ListText(outcome.Have)).Append(" of ")
                  .Append(outcome.Total.ToString(CultureInfo.InvariantCulture)).Append('.');
            }
            sb.Append(outcome.Missing.Count == 1 ? " Paste part " : " Paste parts ").Append(ListText(outcome.Missing)).Append('.');
            sb.Append(RejectionText(outcome.Rejected));
            return sb.ToString();
        }

        private static string RejectionText(List<TransferRejection> rejected)
        {
            var sb = new StringBuilder();
            foreach (TransferRejection r in rejected)
            {
                if (r.Part > 0) sb.Append(" Part ").Append(r.Part.ToString(CultureInfo.InvariantCulture)).Append(" was not used: ").Append(r.Reason).Append('.');
                else sb.Append(" A part was not used: ").Append(r.Reason).Append('.');
            }
            return sb.ToString();
        }

        /// <summary>"1", "1 and 3", "1, 3 and 5", "2 to 23", "1 to 3 and 5".</summary>
        internal static string ListText(List<int> numbers)
        {
            var ranges = new List<string>();
            for (int i = 0; i < numbers.Count; i++)
            {
                int start = numbers[i];
                int end = start;
                while (i + 1 < numbers.Count && numbers[i + 1] == end + 1)
                {
                    end++;
                    i++;
                }
                string s = start.ToString(CultureInfo.InvariantCulture);
                if (end == start + 1) ranges.Add(s + ", " + end.ToString(CultureInfo.InvariantCulture));
                else if (end > start) ranges.Add(s + " to " + end.ToString(CultureInfo.InvariantCulture));
                else ranges.Add(s);
            }
            if (ranges.Count == 1)
            {
                string only = ranges[0];
                int comma = only.IndexOf(", ", StringComparison.Ordinal);
                return comma < 0 ? only : only.Substring(0, comma) + " and " + only.Substring(comma + 2);
            }
            string joined = string.Join(", ", ranges.GetRange(0, ranges.Count - 1).ToArray());
            return joined + " and " + ranges[ranges.Count - 1];
        }

        private static string Count(int n, string noun)
        {
            return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
        }
    }
}
