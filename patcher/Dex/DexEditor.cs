using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace McvsOffline.Patcher.Dex
{
    /// <summary>One in-place replacement inside a method: whole instructions at <see cref="Pc"/>, as text, before and after.</summary>
    public sealed class DexSiteEdit
    {
        public DexSiteEdit(int pc, string[] before, string[] after)
        {
            Pc = pc;
            Before = before;
            After = after;
        }

        /// <summary>Code-unit offset of the first replaced instruction (dexdump's "|0016:").</summary>
        public int Pc { get; }

        /// <summary>The instructions found there, as <see cref="DexInstruction.Render"/> prints them.</summary>
        public string[] Before { get; }

        /// <summary>Their replacement, the same size in code units, in the same text form.</summary>
        public string[] After { get; }
    }

    /// <summary>The edits of one method, located by class, name and descriptor and pinned by its code fingerprint.</summary>
    public sealed class DexMethodEdit
    {
        public DexMethodEdit(string id, string classDescriptor, string name, string descriptor, string fingerprint, string reason, params DexSiteEdit[] sites)
        {
            Id = id;
            ClassDescriptor = classDescriptor;
            Name = name;
            Descriptor = descriptor;
            Fingerprint = fingerprint;
            Reason = reason;
            Sites = sites;
        }

        /// <summary>The report's label (P1, P8, ...).</summary>
        public string Id { get; }

        public string ClassDescriptor { get; }

        public string Name { get; }

        public string Descriptor { get; }

        /// <summary><see cref="DexEditor.Fingerprint"/> of the verified, unpatched method.</summary>
        public string Fingerprint { get; }

        public string Reason { get; }

        public IReadOnlyList<DexSiteEdit> Sites { get; }

        public string Reference { get { return ClassDescriptor + "->" + Name + Descriptor; } }
    }

    /// <summary>
    /// Applies <see cref="DexMethodEdit"/>s to a dex file, in place and without moving a byte: a site's replacement has the
    /// size of the instructions it replaces, so no offset, index or table changes, and only the edited code units, the SHA-1
    /// signature and the Adler-32 checksum differ from the input.
    /// <para>
    /// Like the IL patches it verifies everything before it changes anything and throws <see cref="InvalidDataException"/> on
    /// the first mismatch: the method is found by class + name + descriptor (never by offset), its code fingerprint must be
    /// the verified one, each site must start an instruction and consist of exactly the expected whole instructions, the
    /// replacement must assemble to the same size, and the edited method must still be well formed: every branch target and
    /// try boundary starts an instruction, a move-result still follows an invoke, a return-void only goes into a void method,
    /// a register whose write is dropped is dead where the write would have taken effect, a register the replacement starts to
    /// write was dead there, and a register the replacement starts to read can only hold the value the replacement wrote.
    /// </para>
    /// </summary>
    public static class DexEditor
    {
        /// <summary>
        /// SHA-256 (hex) of a method's code as stored and as meant: the code_item's bytes (registers, ins, outs, tries,
        /// debug_info_off, insns, try items, handlers) in hex, then every instruction rendered with its pc and resolved
        /// references, then every try with its catch types. The bytes pin the exact shipped code, the text pins what its
        /// string/type/field/method indices mean in this file (the same bytes in a dex with other tables do not match).
        /// </summary>
        public static string Fingerprint(DexFile dex, DexFile.Method method)
        {
            DexFile.CodeItem code = dex.GetCode(method);
            var text = new StringBuilder();
            text.Append(method.Reference).Append('\n');
            text.Append("code ").Append(Convert.ToHexString(dex.GetCodeBytes(code)).ToLowerInvariant()).Append('\n');
            foreach (DexInstruction i in DexInstruction.DecodeAll(code.Insns)) text.Append(DexInstruction.Hex(i.Pc)).Append(' ').Append(i.Render(dex)).Append('\n');
            foreach (DexFile.TryItem t in code.Tries)
            {
                DexFile.CatchHandler h = code.Handlers[t.HandlerOff];
                text.Append("try ").Append(DexInstruction.Hex(t.StartAddr)).Append('+').Append(t.InsnCount).Append(" ->");
                foreach (var c in h.Catches) text.Append(' ').Append(dex.GetTypeDescriptor(c.TypeIndex)).Append(DexInstruction.Hex(c.Addr));
                if (h.CatchAllAddr >= 0) text.Append(" all").Append(DexInstruction.Hex(h.CatchAllAddr));
                text.Append('\n');
            }
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
            }
        }

        /// <summary>The one method an edit names, in this file (null when the class is not defined here).</summary>
        public static DexFile.Method Locate(DexFile dex, DexMethodEdit edit)
        {
            return dex.FindMethod(edit.ClassDescriptor, edit.Name, edit.Descriptor);
        }

        /// <summary>
        /// Verifies every edit against <paramref name="dex"/>, then returns a patched copy with a new signature and checksum.
        /// Every class the edits name must be defined in this file. One note per site.
        /// </summary>
        public static byte[] Apply(DexFile dex, IReadOnlyList<DexMethodEdit> edits, ICollection<string> notes)
        {
            var writes = new List<(int Offset, byte[] Bytes)>();
            var siteNotes = new List<string>();
            foreach (DexMethodEdit edit in edits)
            {
                DexFile.Method method = Locate(dex, edit) ?? throw Refused(dex, edit, "the class is not defined in " + dex.Name);
                if (edits.Count(e => e.Reference == edit.Reference) != 1) throw Refused(dex, edit, "the method is edited by more than one entry");
                writes.AddRange(PlanMethod(dex, method, edit, siteNotes));
            }

            byte[] patched = (byte[])dex.Bytes.Clone();
            foreach (var w in writes) Buffer.BlockCopy(w.Bytes, 0, patched, w.Offset, w.Bytes.Length);
            DexFile.UpdateChecksumAndSignature(patched);

            // Self-check on the bytes: only the edited code units and the header's checksum and signature changed, the result
            // parses with a matching checksum and signature, and every edited method decodes to exactly the expected text.
            var allowed = new bool[patched.Length];
            for (int k = DexFile.ChecksumOffset; k < DexFile.SignatureOffset + DexFile.SignatureSize; k++) allowed[k] = true;
            foreach (var w in writes)
            {
                for (int k = 0; k < w.Bytes.Length; k++) allowed[w.Offset + k] = true;
            }
            for (int k = 0; k < patched.Length; k++)
            {
                if (patched[k] != dex.Bytes[k] && !allowed[k]) throw new InvalidOperationException(dex.Name + ": byte 0x" + k.ToString("x") + " changed outside the edits");
            }
            DexFile check = DexFile.Parse(dex.Name, patched);
            if (!check.SignatureMatches) throw new InvalidOperationException(dex.Name + ": the new signature does not verify");
            foreach (DexMethodEdit edit in edits)
            {
                DexFile.Method method = Locate(check, edit);
                Dictionary<int, DexInstruction> byPc = DexInstruction.DecodeAll(check.GetCode(method).Insns).ToDictionary(i => i.Pc);
                foreach (DexSiteEdit site in edit.Sites)
                {
                    int pc = site.Pc;
                    foreach (string expected in site.After)
                    {
                        string actual = byPc.TryGetValue(pc, out DexInstruction i) ? i.Render(check) : "(no instruction)";
                        if (actual != expected) throw new InvalidOperationException(dex.Name + " " + method + ": " + DexInstruction.Hex(pc) + " re-reads as '" + actual + "', not '" + expected + "'");
                        pc += i.Size;
                    }
                }
            }
            foreach (string n in siteNotes) notes?.Add(n);
            return patched;
        }

        /// <summary>Checks one method's edits and returns the file writes they need (nothing is written yet).</summary>
        private static List<(int, byte[])> PlanMethod(DexFile dex, DexFile.Method method, DexMethodEdit edit, List<string> notes)
        {
            string actual = Fingerprint(dex, method);
            if (actual != edit.Fingerprint)
                throw Refused(dex, edit, "it is not the verified " + KnownBuild.Version + " code (fingerprint " + actual + ", expected " + edit.Fingerprint + ")");
            DexFile.CodeItem code = dex.GetCode(method);
            List<DexInstruction> original = DexInstruction.DecodeAll(code.Insns);
            var flow = new DexFlow(code, original);
            ushort[] insns = (ushort[])code.Insns.Clone();
            var ranges = new List<(int Start, int End)>();
            var newWrites = new List<(List<DexInstruction> Before, List<DexInstruction> After)>();

            foreach (DexSiteEdit site in edit.Sites)
            {
                // The expected whole instructions at the site.
                var before = new List<DexInstruction>();
                int pc = site.Pc;
                foreach (string expected in site.Before)
                {
                    if (!flow.ByPc.TryGetValue(pc, out DexInstruction i)) throw Refused(dex, edit, DexInstruction.Hex(pc) + " is not the start of an instruction");
                    string text = i.Render(dex);
                    if (text != expected) throw Refused(dex, edit, DexInstruction.Hex(pc) + " is '" + text + "', expected '" + expected + "'");
                    before.Add(i);
                    pc += i.Size;
                }
                int start = site.Pc, end = pc;
                if (ranges.Any(r => start < r.End && r.Start < end)) throw Refused(dex, edit, "two sites overlap at " + DexInstruction.Hex(start));
                ranges.Add((start, end));

                // The replacement: same size, whole instructions.
                var units = new List<ushort>();
                foreach (string text in site.After) units.AddRange(DexInstruction.Assemble(text, dex));
                if (units.Count != end - start)
                    throw Refused(dex, edit, "the replacement at " + DexInstruction.Hex(start) + " is " + units.Count + " code units, the original " + (end - start));
                for (int k = 0; k < units.Count; k++) insns[start + k] = units[k];
                var after = new List<DexInstruction>();
                for (int at = start; at < end;)
                {
                    DexInstruction i = DexInstruction.Decode(insns, at);
                    after.Add(i);
                    at += i.Size;
                }
                if (!after.Select(i => i.Render(dex)).SequenceEqual(site.After))
                    throw Refused(dex, edit, "the replacement at " + DexInstruction.Hex(start) + " does not decode as written: " + string.Join("; ", after.Select(i => i.Render(dex))));

                if (after.Any(i => i.Opcode == 0x0e) && !method.Descriptor.EndsWith(")V", StringComparison.Ordinal))
                    throw Refused(dex, edit, "return-void in a method that returns " + method.Descriptor.Substring(method.Descriptor.IndexOf(')') + 1));

                // Registers: a dropped write must be dead where it would have taken effect (after the site, and at every handler
                // the site's instructions can throw to); a new write must not destroy a live value.
                var beforeWrites = new HashSet<int>(before.SelectMany(i => i.Writes));
                var afterWrites = new HashSet<int>(after.SelectMany(i => i.Writes));
                foreach (int r in beforeWrites.Where(r => !afterWrites.Contains(r)))
                {
                    if (flow.IsLiveBefore(r, end, out DexInstruction reader))
                        throw Refused(dex, edit, "v" + r + " is no longer written at " + DexInstruction.Hex(start) + " but " + reader.Name + " at " + DexInstruction.Hex(reader.Pc) + " reads it");
                    foreach (int h in before.SelectMany(i => flow.HandlersAt(i.Pc)).Distinct())
                    {
                        if (flow.IsLiveBefore(r, h, out reader))
                            throw Refused(dex, edit, "v" + r + " is no longer written at " + DexInstruction.Hex(start) + " but the handler at " + DexInstruction.Hex(h) + " reads it");
                    }
                }
                foreach (int r in afterWrites.Where(r => !beforeWrites.Contains(r)))
                {
                    if (flow.IsLiveBefore(r, start, out DexInstruction reader))
                        throw Refused(dex, edit, "the replacement at " + DexInstruction.Hex(start) + " writes v" + r + ", whose value " + reader.Name + " at " + DexInstruction.Hex(reader.Pc) + " still reads");
                }
                newWrites.Add((before, after));
                notes.Add(dex.Name + ": " + method.DisplayName + " " + DexInstruction.Hex(start) + ": " + string.Join("; ", site.Before) + " -> " +
                          string.Join("; ", site.After) + " (" + edit.Id + ": " + edit.Reason + ")");
            }

            // The edited method as a whole: well formed, and every register the replacements start to read holds a value
            // the replacements wrote (e.g. P8's new flags register).
            List<DexInstruction> patched = DexInstruction.DecodeAll(insns);
            DexFlow patchedFlow;
            try
            {
                patchedFlow = new DexFlow(code, patched);
            }
            catch (InvalidDataException ex)
            {
                throw Refused(dex, edit, "the edited method is not well formed: " + ex.Message);
            }
            // A reachable move-result must still directly follow the invoke (or filled-new-array) whose result it takes. Code
            // after a new return-void is unreachable and is left as it was (ART only flow-checks reachable code).
            var reachable = patchedFlow.Reachable();
            for (int k = 0; k < patched.Count; k++)
            {
                DexInstruction i = patched[k];
                if (!i.IsMoveResult || !reachable.Contains(i.Pc)) continue;
                DexInstruction prior = k > 0 ? patched[k - 1] : null;
                if (prior == null || !(prior.IsInvoke || prior.Opcode == 0x24 || prior.Opcode == 0x25) || !reachable.Contains(prior.Pc))
                    throw Refused(dex, edit, i.Name + " at " + DexInstruction.Hex(i.Pc) + " would no longer follow an invoke");
            }

            var ourWrites = newWrites.SelectMany(w => w.After).ToList();
            foreach (var w in newWrites)
            {
                var oldReads = new HashSet<int>(w.Before.SelectMany(i => i.Reads));
                foreach (DexInstruction reader in w.After)
                {
                    foreach (int r in reader.Reads.Where(r => !oldReads.Contains(r)))
                    {
                        List<DexInstruction> sources = patchedFlow.ReachingWrites(r, reader.Pc, out bool fromEntry);
                        bool onlyOurs = sources.All(s => ourWrites.Any(o => o.Pc == s.Pc && o.Writes.Contains(r)));
                        if (fromEntry || sources.Count == 0 || !onlyOurs)
                        {
                            throw Refused(dex, edit, reader.Name + " at " + DexInstruction.Hex(reader.Pc) + " now reads v" + r + ", which can hold a value the edits did not write" +
                                                     (sources.Count == 0 ? "" : " (written at " + string.Join(", ", sources.Select(x => DexInstruction.Hex(x.Pc))) + ")"));
                        }
                    }
                }
            }

            var result = new List<(int, byte[])>();
            foreach (var range in ranges)
            {
                var bytes = new byte[2 * (range.End - range.Start)];
                for (int k = 0; k < range.End - range.Start; k++)
                {
                    bytes[2 * k] = (byte)insns[range.Start + k];
                    bytes[2 * k + 1] = (byte)(insns[range.Start + k] >> 8);
                }
                result.Add((code.FileOffsetOf(range.Start), bytes));
            }
            return result;
        }

        private static InvalidDataException Refused(DexFile dex, DexMethodEdit edit, string why)
        {
            return new InvalidDataException(dex.Name + ": " + DexFile.JavaName(edit.ClassDescriptor) + "." + edit.Name + edit.Descriptor + " (" + edit.Id + "): " + why +
                                            "; nothing was changed");
        }
    }
}
