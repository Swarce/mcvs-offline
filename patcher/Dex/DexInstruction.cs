using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace McvsOffline.Patcher.Dex
{
    /// <summary>Instruction formats (source.android.com "Dalvik bytecode formats"); the number is the size in code units.</summary>
    public enum DexFormat
    {
        F10x, F12x, F11n, F11x, F10t, F20t, F22x, F21t, F21s, F21h, F21c, F23x, F22b, F22t, F22s, F22c, F30t, F32x, F31i, F31t, F31c,
        F35c, F3rc, F45cc, F4rcc, F51l,

        /// <summary>packed-switch-payload, sparse-switch-payload, fill-array-data-payload (data inside insns, never executed).</summary>
        Payload,
    }

    /// <summary>What an instruction's index operand refers to.</summary>
    public enum DexIndexKind
    {
        None, String, Type, Field, Method, CallSite, MethodHandle, Proto,
    }

    /// <summary>
    /// One decoded Dalvik instruction: its position and size in code units, opcode, register operands, literal, index or branch
    /// offset, plus what the patcher's checks need: the registers it reads and writes, where control goes next, and a
    /// dexdump-like text with every index resolved ("invoke-static {v8, v1, v2, v3}, Landroid/app/PendingIntent;->getActivity(...)").
    /// Covers every opcode of dex 035-039; the unused ones (0x3e-0x43, 0x73, 0x79-0x7a, 0xe3-0xf9) are refused.
    /// </summary>
    public sealed class DexInstruction
    {
        private struct OpInfo
        {
            public string Name;
            public DexFormat Format;
            public DexIndexKind Index;
        }

        private static readonly OpInfo[] Ops = BuildTable();

        /// <summary>Code-unit offset in the method (the "pc").</summary>
        public int Pc { get; private set; }

        /// <summary>Size in code units.</summary>
        public int Size { get; private set; }

        /// <summary>The opcode (low byte of the first unit); for payloads the ident (0x0100, 0x0200, 0x0300).</summary>
        public int Opcode { get; private set; }

        public string Name { get; private set; }

        public DexFormat Format { get; private set; }

        public DexIndexKind IndexKind { get; private set; }

        /// <summary>vA, vB, vC in the format's naming (vAA, vBBBB, ... are the same fields).</summary>
        public int A { get; private set; }

        public int B { get; private set; }

        public int C { get; private set; }

        /// <summary>The argument registers of an invoke or filled-new-array (35c/3rc/45cc/4rcc), else empty.</summary>
        public int[] Args { get; private set; } = new int[0];

        /// <summary>The literal of a const or a /lit instruction, already shifted for the /high16 forms.</summary>
        public long Literal { get; private set; }

        public int Index { get; private set; }

        /// <summary>The proto index of invoke-polymorphic (45cc/4rcc).</summary>
        public int Index2 { get; private set; }

        /// <summary>Branch offset in code units, relative to <see cref="Pc"/> (gotos, ifs, and the payload of switch/fill-array-data).</summary>
        public int BranchOffset { get; private set; }

        /// <summary>For a payload: its entries (switch keys are not kept, only the targets, relative to the switch instruction).</summary>
        public int[] PayloadTargets { get; private set; } = new int[0];

        public bool IsPayload { get { return Format == DexFormat.Payload; } }

        public bool IsInvoke { get { return (Opcode >= 0x6e && Opcode <= 0x72) || (Opcode >= 0x74 && Opcode <= 0x78) || (Opcode >= 0xfa && Opcode <= 0xfd); } }

        /// <summary>move-result, move-result-wide, move-result-object.</summary>
        public bool IsMoveResult { get { return Opcode >= 0x0a && Opcode <= 0x0c; } }

        public bool IsReturn { get { return Opcode >= 0x0e && Opcode <= 0x11; } }

        public bool IsSwitch { get { return Opcode == 0x2b || Opcode == 0x2c; } }

        // ------------------------------------------------------------------ decoding

        /// <summary>Decodes a whole method body; every code unit must belong to exactly one instruction or payload.</summary>
        public static List<DexInstruction> DecodeAll(ushort[] insns)
        {
            var list = new List<DexInstruction>();
            int pc = 0;
            while (pc < insns.Length)
            {
                DexInstruction i = Decode(insns, pc);
                list.Add(i);
                pc += i.Size;
            }
            return list;
        }

        /// <summary>Decodes the instruction (or payload) that starts at <paramref name="pc"/>.</summary>
        public static DexInstruction Decode(ushort[] insns, int pc)
        {
            if (pc < 0 || pc >= insns.Length) throw new InvalidDataException("instruction offset " + pc + " out of range");
            ushort unit = insns[pc];
            if (unit == 0x0100 || unit == 0x0200 || unit == 0x0300) return DecodePayload(insns, pc, unit);
            int op = unit & 0xFF;
            OpInfo info = Ops[op];
            if (info.Name == null) throw new InvalidDataException("unused opcode 0x" + op.ToString("x2") + " at " + Hex(pc));
            var i = new DexInstruction { Pc = pc, Opcode = op, Name = info.Name, Format = info.Format, IndexKind = info.Index };
            i.Size = SizeOf(info.Format);
            if (pc + i.Size > insns.Length) throw new InvalidDataException(info.Name + " at " + Hex(pc) + " runs past the end of the method");
            int hi = unit >> 8;
            ushort u1 = i.Size > 1 ? insns[pc + 1] : (ushort)0;
            ushort u2 = i.Size > 2 ? insns[pc + 2] : (ushort)0;
            switch (info.Format)
            {
                case DexFormat.F10x:
                    break;
                case DexFormat.F12x:
                    i.A = hi & 0xF; i.B = hi >> 4;
                    break;
                case DexFormat.F11n:
                    i.A = hi & 0xF; i.Literal = (sbyte)(hi & 0xF0) >> 4;
                    break;
                case DexFormat.F11x:
                    i.A = hi;
                    break;
                case DexFormat.F10t:
                    i.BranchOffset = (sbyte)hi;
                    break;
                case DexFormat.F20t:
                    i.BranchOffset = (short)u1;
                    break;
                case DexFormat.F22x:
                    i.A = hi; i.B = u1;
                    break;
                case DexFormat.F21t:
                    i.A = hi; i.BranchOffset = (short)u1;
                    break;
                case DexFormat.F21s:
                    i.A = hi; i.Literal = (short)u1;
                    break;
                case DexFormat.F21h:
                    i.A = hi;
                    i.Literal = op == 0x19 ? (long)u1 << 48 : (int)((uint)u1 << 16);
                    break;
                case DexFormat.F21c:
                    i.A = hi; i.Index = u1;
                    break;
                case DexFormat.F23x:
                    i.A = hi; i.B = u1 & 0xFF; i.C = u1 >> 8;
                    break;
                case DexFormat.F22b:
                    i.A = hi; i.B = u1 & 0xFF; i.Literal = (sbyte)(u1 >> 8);
                    break;
                case DexFormat.F22t:
                    i.A = hi & 0xF; i.B = hi >> 4; i.BranchOffset = (short)u1;
                    break;
                case DexFormat.F22s:
                    i.A = hi & 0xF; i.B = hi >> 4; i.Literal = (short)u1;
                    break;
                case DexFormat.F22c:
                    i.A = hi & 0xF; i.B = hi >> 4; i.Index = u1;
                    break;
                case DexFormat.F30t:
                    i.BranchOffset = (int)(u1 | (uint)u2 << 16);
                    break;
                case DexFormat.F32x:
                    i.A = u1; i.B = u2;
                    break;
                case DexFormat.F31i:
                    i.A = hi; i.Literal = (int)(u1 | (uint)u2 << 16);
                    break;
                case DexFormat.F31t:
                    i.A = hi; i.BranchOffset = (int)(u1 | (uint)u2 << 16);
                    break;
                case DexFormat.F31c:
                    i.A = hi; i.Index = (int)(u1 | (uint)u2 << 16);
                    break;
                case DexFormat.F35c:
                case DexFormat.F45cc:
                {
                    int count = hi >> 4;
                    if (count > 5) throw new InvalidDataException(info.Name + " at " + Hex(pc) + " has " + count + " arguments");
                    int[] regs = { u2 & 0xF, (u2 >> 4) & 0xF, (u2 >> 8) & 0xF, u2 >> 12, hi & 0xF };
                    i.Args = regs.Take(count).ToArray();
                    i.Index = u1;
                    if (info.Format == DexFormat.F45cc) i.Index2 = insns[pc + 3];
                    break;
                }
                case DexFormat.F3rc:
                case DexFormat.F4rcc:
                    i.Args = Enumerable.Range(u2, hi).ToArray();
                    i.Index = u1;
                    if (info.Format == DexFormat.F4rcc) i.Index2 = insns[pc + 3];
                    break;
                case DexFormat.F51l:
                    i.A = hi;
                    i.Literal = (long)(u1 | (ulong)u2 << 16 | (ulong)insns[pc + 3] << 32 | (ulong)insns[pc + 4] << 48);
                    break;
                default:
                    throw new InvalidDataException("format " + info.Format + " not handled");
            }
            return i;
        }

        private static DexInstruction DecodePayload(ushort[] insns, int pc, ushort ident)
        {
            if (pc + 2 > insns.Length) throw new InvalidDataException("payload at " + Hex(pc) + " runs past the end of the method");
            var i = new DexInstruction { Pc = pc, Opcode = ident, Format = DexFormat.Payload };
            int size = insns[pc + 1];
            long units;
            if (ident == 0x0100)
            {
                i.Name = "packed-switch-payload";
                units = 4 + 2L * size;
            }
            else if (ident == 0x0200)
            {
                i.Name = "sparse-switch-payload";
                units = 2 + 4L * size;
            }
            else
            {
                i.Name = "fill-array-data-payload";
                if (pc + 4 > insns.Length) throw new InvalidDataException("payload at " + Hex(pc) + " runs past the end of the method");
                long elements = insns[pc + 2] | (long)insns[pc + 3] << 16;
                units = 4 + (size * elements + 1) / 2;
            }
            if (pc + units > insns.Length) throw new InvalidDataException(i.Name + " at " + Hex(pc) + " runs past the end of the method");
            i.Size = (int)units;
            if (ident == 0x0100)
                i.PayloadTargets = Enumerable.Range(0, size).Select(k => Int32(insns, pc + 4 + 2 * k)).ToArray();
            else if (ident == 0x0200)
                i.PayloadTargets = Enumerable.Range(0, size).Select(k => Int32(insns, pc + 2 + 2 * size + 2 * k)).ToArray();
            return i;
        }

        private static int Int32(ushort[] insns, int at)
        {
            return (int)(insns[at] | (uint)insns[at + 1] << 16);
        }

        private static int SizeOf(DexFormat f)
        {
            switch (f)
            {
                case DexFormat.F10x: case DexFormat.F12x: case DexFormat.F11n: case DexFormat.F11x: case DexFormat.F10t:
                    return 1;
                case DexFormat.F20t: case DexFormat.F22x: case DexFormat.F21t: case DexFormat.F21s: case DexFormat.F21h: case DexFormat.F21c:
                case DexFormat.F23x: case DexFormat.F22b: case DexFormat.F22t: case DexFormat.F22s: case DexFormat.F22c:
                    return 2;
                case DexFormat.F45cc: case DexFormat.F4rcc:
                    return 4;
                case DexFormat.F51l:
                    return 5;
                default:
                    return 3;
            }
        }

        // ------------------------------------------------------------------ control flow

        /// <summary>
        /// Where execution can continue in the same method without an exception: the next instruction, branch targets and
        /// switch targets (none after a return, throw or goto). Exceptional edges come from the code item's tries
        /// (<see cref="DexFlow"/>). Needs the decoded method for the switch payloads.
        /// </summary>
        public IEnumerable<int> Successors(IReadOnlyDictionary<int, DexInstruction> byPc)
        {
            if (IsPayload || IsReturn || Opcode == 0x27) yield break;            // throw: exceptional edges only
            if (Opcode >= 0x28 && Opcode <= 0x2a)
            {
                yield return Pc + BranchOffset;
                yield break;
            }
            yield return Pc + Size;
            if (Opcode >= 0x32 && Opcode <= 0x3d) yield return Pc + BranchOffset;
            if (IsSwitch)
            {
                if (!byPc.TryGetValue(Pc + BranchOffset, out DexInstruction payload) || !payload.IsPayload || payload.Opcode != (Opcode == 0x2b ? 0x0100 : 0x0200))
                    throw new InvalidDataException(Name + " at " + Hex(Pc) + " does not point at its payload");
                foreach (int t in payload.PayloadTargets) yield return Pc + t;
            }
        }

        /// <summary>Every code-unit offset this instruction refers to (branch and switch targets, payloads): each must start an instruction.</summary>
        public IEnumerable<int> ReferencedOffsets(IReadOnlyDictionary<int, DexInstruction> byPc)
        {
            if ((Opcode >= 0x28 && Opcode <= 0x2a) || (Opcode >= 0x32 && Opcode <= 0x3d) || Opcode == 0x26 || IsSwitch) yield return Pc + BranchOffset;
            if (IsSwitch)
            {
                foreach (int t in Successors(byPc).Skip(1)) yield return t;
            }
        }

        // ------------------------------------------------------------------ registers

        /// <summary>The registers this instruction reads (both halves of a wide pair).</summary>
        public IReadOnlyList<int> Reads
        {
            get
            {
                var r = new List<int>();
                Effects(r, null);
                return r;
            }
        }

        /// <summary>The registers this instruction writes (both halves of a wide pair).</summary>
        public IReadOnlyList<int> Writes
        {
            get
            {
                var w = new List<int>();
                Effects(null, w);
                return w;
            }
        }

        private void Effects(List<int> reads, List<int> writes)
        {
            void R(int reg, bool wide = false) { if (reads != null) { reads.Add(reg); if (wide) reads.Add(reg + 1); } }
            void W(int reg, bool wide = false) { if (writes != null) { writes.Add(reg); if (wide) writes.Add(reg + 1); } }
            int op = Opcode;
            if (IsPayload || op == 0x00 || op == 0x0e || (op >= 0x28 && op <= 0x2a)) return;
            if (op == 0x01 || op == 0x02 || op == 0x03 || (op >= 0x07 && op <= 0x09)) { W(A); R(B); return; }
            if (op >= 0x04 && op <= 0x06) { W(A, true); R(B, true); return; }
            if (op == 0x0a || op == 0x0c || op == 0x0d) { W(A); return; }
            if (op == 0x0b) { W(A, true); return; }
            if (op == 0x0f || op == 0x11 || op == 0x1d || op == 0x1e || op == 0x27 || op == 0x26 || op == 0x2b || op == 0x2c) { R(A); return; }
            if (op == 0x10) { R(A, true); return; }
            if ((op >= 0x12 && op <= 0x15) || (op >= 0x1a && op <= 0x1c) || op == 0x22 || op == 0xfe || op == 0xff) { W(A); return; }
            if (op >= 0x16 && op <= 0x19) { W(A, true); return; }
            if (op == 0x1f) { R(A); W(A); return; }
            if (op == 0x20 || op == 0x21 || op == 0x23) { R(B); W(A); return; }
            if (op == 0x24 || op == 0x25 || IsInvoke) { foreach (int a in Args) R(a); return; }
            if (op == 0x2d || op == 0x2e) { R(B); R(C); W(A); return; }
            if (op >= 0x2f && op <= 0x31) { R(B, true); R(C, true); W(A); return; }
            if (op >= 0x32 && op <= 0x37) { R(A); R(B); return; }
            if (op >= 0x38 && op <= 0x3d) { R(A); return; }
            if (op >= 0x44 && op <= 0x4a) { R(B); R(C); W(A, op == 0x45); return; }
            if (op >= 0x4b && op <= 0x51) { R(A, op == 0x4c); R(B); R(C); return; }
            if (op >= 0x52 && op <= 0x58) { R(B); W(A, op == 0x53); return; }
            if (op >= 0x59 && op <= 0x5f) { R(A, op == 0x5a); R(B); return; }
            if (op >= 0x60 && op <= 0x66) { W(A, op == 0x61); return; }
            if (op >= 0x67 && op <= 0x6d) { R(A, op == 0x68); return; }
            if (op >= 0x7b && op <= 0x8f)
            {
                // neg/not/conversions: (source wide, destination wide)
                const string SrcWide = "001101000111000111000", DstWide = "001101101001011010000";
                int k = op - 0x7b;
                R(B, SrcWide[k] == '1');
                W(A, DstWide[k] == '1');
                return;
            }
            if (op >= 0x90 && op <= 0xcf)
            {
                bool twoAddr = op >= 0xb0;
                int k = op - (twoAddr ? 0xb0 : 0x90);
                bool wide = (k >= 11 && k <= 21) || k >= 27;      // long and double
                bool shift = k >= 19 && k <= 21;                  // shl/shr/ushr-long: the shift amount is an int
                if (twoAddr)
                {
                    R(A, wide); R(B, wide && !shift); W(A, wide);
                }
                else
                {
                    R(B, wide); R(C, wide && !shift); W(A, wide);
                }
                return;
            }
            if (op >= 0xd0 && op <= 0xe2) { R(B); W(A); return; }
            throw new InvalidOperationException("no register effects for opcode 0x" + op.ToString("x2"));
        }

        // ------------------------------------------------------------------ text

        /// <summary>
        /// dexdump-like text with resolved references: "const v3, #0x00008000", "const/high16 v5, #0x08000000",
        /// "if-eqz v0, @0028", "invoke-direct {v2}, Landroid/content/IntentFilter;-><init>()V", "const-string v3, \"text\"".
        /// </summary>
        public string Render(DexFile dex)
        {
            if (IsPayload) return Name + " (" + Size + " units)";
            var s = new StringBuilder(Name);
            string reg(int r) => "v" + r;
            switch (Format)
            {
                case DexFormat.F10x:
                    return Name;
                case DexFormat.F12x:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(reg(B)).ToString();
                case DexFormat.F11x:
                    return s.Append(' ').Append(reg(A)).ToString();
                case DexFormat.F11n:
                case DexFormat.F21s:
                    return s.Append(' ').Append(reg(A)).Append(", #").Append(Literal.ToString(CultureInfo.InvariantCulture)).ToString();
                case DexFormat.F21h:
                case DexFormat.F31i:
                case DexFormat.F51l:
                    bool wide = Opcode == 0x17 || Opcode == 0x18 || Opcode == 0x19;
                    return s.Append(' ').Append(reg(A)).Append(", #0x").Append(wide ? Literal.ToString("x16") : ((uint)Literal).ToString("x8")).ToString();
                case DexFormat.F10t:
                case DexFormat.F20t:
                case DexFormat.F30t:
                    return s.Append(' ').Append(Target(Pc + BranchOffset)).ToString();
                case DexFormat.F22x:
                case DexFormat.F32x:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(reg(B)).ToString();
                case DexFormat.F21t:
                case DexFormat.F31t:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(Target(Pc + BranchOffset)).ToString();
                case DexFormat.F22t:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(reg(B)).Append(", ").Append(Target(Pc + BranchOffset)).ToString();
                case DexFormat.F21c:
                case DexFormat.F31c:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(Reference(dex, IndexKind, Index)).ToString();
                case DexFormat.F23x:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(reg(B)).Append(", ").Append(reg(C)).ToString();
                case DexFormat.F22b:
                case DexFormat.F22s:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(reg(B)).Append(", #").Append(Literal.ToString(CultureInfo.InvariantCulture)).ToString();
                case DexFormat.F22c:
                    return s.Append(' ').Append(reg(A)).Append(", ").Append(reg(B)).Append(", ").Append(Reference(dex, IndexKind, Index)).ToString();
                case DexFormat.F35c:
                case DexFormat.F45cc:
                    s.Append(" {").Append(string.Join(", ", Args.Select(reg))).Append("}, ").Append(Reference(dex, IndexKind, Index));
                    if (Format == DexFormat.F45cc) s.Append(", ").Append(Reference(dex, DexIndexKind.Proto, Index2));
                    return s.ToString();
                case DexFormat.F3rc:
                case DexFormat.F4rcc:
                    s.Append(" {").Append(Args.Length == 0 ? "" : reg(Args[0]) + " .. " + reg(Args[Args.Length - 1])).Append("}, ").Append(Reference(dex, IndexKind, Index));
                    if (Format == DexFormat.F4rcc) s.Append(", ").Append(Reference(dex, DexIndexKind.Proto, Index2));
                    return s.ToString();
                default:
                    return s.ToString();
            }
        }

        private static string Target(int pc)
        {
            return "@" + pc.ToString("x4");
        }

        private static string Reference(DexFile dex, DexIndexKind kind, int index)
        {
            switch (kind)
            {
                case DexIndexKind.String:
                    return "\"" + Escape(dex.GetString(index)) + "\"";
                case DexIndexKind.Type:
                    return dex.GetTypeDescriptor(index);
                case DexIndexKind.Field:
                    return dex.GetFieldRef(index);
                case DexIndexKind.Method:
                    return dex.GetMethodRef(index);
                case DexIndexKind.Proto:
                    return dex.GetProtoDescriptor(index);
                case DexIndexKind.CallSite:
                    return "call_site@" + index;
                case DexIndexKind.MethodHandle:
                    return "method_handle@" + index;
                default:
                    return "@" + index;
            }
        }

        private static string Escape(string s)
        {
            var b = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 0x20 || c > 0x7e) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.ToString();
        }

        public override string ToString()
        {
            return Hex(Pc) + ": " + Name;
        }

        internal static string Hex(int pc)
        {
            return "@" + pc.ToString("x4");
        }

        // ------------------------------------------------------------------ assembling (the replacement instructions)

        /// <summary>
        /// Encodes one instruction from the text <see cref="Render"/> produces, for the few forms the patcher writes:
        /// <c>nop</c>, <c>return-void</c>, <c>move-result-object</c>, <c>const/4</c>, <c>const/high16</c>, <c>const</c>,
        /// <c>const-string</c>, <c>new-instance</c> and the five 35c invokes. Strings, types and method references must
        /// already be in the dex's id tables (nothing is added to a dex).
        /// </summary>
        public static ushort[] Assemble(string text, DexFile dex)
        {
            text = text.Trim();
            int space = text.IndexOf(' ');
            string name = space < 0 ? text : text.Substring(0, space);
            string operands = space < 0 ? "" : text.Substring(space + 1).Trim();
            int op = Array.FindIndex(Ops, o => o.Name == name);
            if (op < 0) throw new ArgumentException("unknown instruction: " + text);
            switch (name)
            {
                case "nop":
                case "return-void":
                    if (operands.Length > 0) throw new ArgumentException(name + " takes no operands: " + text);
                    return new[] { (ushort)op };
                case "move-result-object":
                {
                    int a = Register(operands);
                    if (a > 255) throw new ArgumentException("register out of range: " + text);
                    return new[] { (ushort)(op | a << 8) };
                }
                case "const-string":
                case "new-instance":
                {
                    int comma = operands.IndexOf(',');
                    if (comma < 0) throw new ArgumentException("expected 'vA, reference': " + text);
                    int a = Register(operands.Substring(0, comma));
                    if (a > 255) throw new ArgumentException("register out of range: " + text);
                    string reference = operands.Substring(comma + 1).Trim();
                    int index = name == "const-string" ? dex.FindString(Unquote(reference, text)) : dex.FindTypeId(reference);
                    if (index < 0) throw new InvalidDataException(dex.Name + " has no " + (name == "const-string" ? "string " : "type id ") + reference + " (the patcher never adds one)");
                    if (index > 0xFFFF) throw new InvalidDataException("index of " + reference + " does not fit 21c");
                    return new[] { (ushort)(op | a << 8), (ushort)index };
                }
                case "const/4":
                case "const/high16":
                case "const":
                {
                    string[] parts = operands.Split(',');
                    if (parts.Length != 2) throw new ArgumentException("expected 'vA, #literal': " + text);
                    int a = Register(parts[0]);
                    long value = ParseLiteral(parts[1].Trim(), text);
                    if (name == "const/4")
                    {
                        if (a > 15 || value < -8 || value > 7) throw new ArgumentException("out of range for const/4: " + text);
                        return new[] { (ushort)(op | a << 8 | ((int)value & 0xF) << 12) };
                    }
                    if (a > 255) throw new ArgumentException("register out of range: " + text);
                    if (name == "const/high16")
                    {
                        if ((value & 0xFFFF) != 0 || value < int.MinValue || value > uint.MaxValue) throw new ArgumentException("not a /high16 value: " + text);
                        return new[] { (ushort)(op | a << 8), (ushort)((uint)value >> 16) };
                    }
                    if (value < int.MinValue || value > uint.MaxValue) throw new ArgumentException("not a 32-bit value: " + text);
                    return new[] { (ushort)(op | a << 8), (ushort)((uint)value & 0xFFFF), (ushort)((uint)value >> 16) };
                }
                case "invoke-virtual":
                case "invoke-super":
                case "invoke-direct":
                case "invoke-static":
                case "invoke-interface":
                {
                    int close = operands.IndexOf('}');
                    if (!operands.StartsWith("{", StringComparison.Ordinal) || close < 0 || operands.IndexOf(',', close) < 0)
                        throw new ArgumentException("expected '{vC, ...}, Lcls;->name(...)R': " + text);
                    string list = operands.Substring(1, close - 1).Trim();
                    int[] regs = list.Length == 0 ? new int[0] : list.Split(',').Select(Register).ToArray();
                    if (regs.Length > 5 || regs.Any(r => r > 15)) throw new ArgumentException("not encodable as 35c: " + text);
                    string reference = operands.Substring(operands.IndexOf(',', close) + 1).Trim();
                    int method = dex.FindMethodId(reference);
                    if (method < 0) throw new InvalidDataException(dex.Name + " has no method id " + reference + " (the patcher never adds one)");
                    if (method > 0xFFFF) throw new InvalidDataException("method index of " + reference + " does not fit 35c");
                    int[] r5 = regs.Concat(new int[5]).Take(5).ToArray();
                    return new[]
                    {
                        (ushort)(op | (regs.Length << 12) | (r5[4] << 8)),
                        (ushort)method,
                        (ushort)(r5[0] | r5[1] << 4 | r5[2] << 8 | r5[3] << 12),
                    };
                }
                default:
                    throw new ArgumentException("the patcher does not assemble " + name + ": " + text);
            }
        }

        private static int Register(string text)
        {
            text = text.Trim();
            if (text.Length < 2 || text[0] != 'v' || !int.TryParse(text.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int r))
                throw new ArgumentException("not a register: " + text);
            return r;
        }

        /// <summary>The text of a string operand as <see cref="Render"/> quotes it (the inverse of <see cref="Escape"/>).</summary>
        private static string Unquote(string quoted, string context)
        {
            if (quoted.Length < 2 || quoted[0] != '"' || quoted[quoted.Length - 1] != '"') throw new ArgumentException("expected a quoted string: " + context);
            var b = new StringBuilder();
            for (int k = 1; k < quoted.Length - 1; k++)
            {
                char c = quoted[k];
                if (c == '"') throw new ArgumentException("unescaped quote: " + context);
                if (c != '\\') { b.Append(c); continue; }
                if (k + 1 >= quoted.Length - 1) throw new ArgumentException("bad escape: " + context);
                char e = quoted[++k];
                if (e == '"' || e == '\\') b.Append(e);
                else if (e == 'u' && k + 4 < quoted.Length - 1 && int.TryParse(quoted.Substring(k + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int u))
                {
                    b.Append((char)u);
                    k += 4;
                }
                else throw new ArgumentException("bad escape: " + context);
            }
            return b.ToString();
        }

        private static long ParseLiteral(string text, string context)
        {
            if (!text.StartsWith("#", StringComparison.Ordinal)) throw new ArgumentException("literal must start with '#': " + context);
            string v = text.Substring(1);
            bool negative = v.StartsWith("-", StringComparison.Ordinal);
            if (negative) v = v.Substring(1);
            long value = v.StartsWith("0x", StringComparison.Ordinal)
                ? long.Parse(v.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                : long.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture);
            return negative ? -value : value;
        }

        // ------------------------------------------------------------------ opcode table

        private static OpInfo[] BuildTable()
        {
            var t = new OpInfo[256];
            void Set(int op, string name, DexFormat f, DexIndexKind k = DexIndexKind.None) { t[op] = new OpInfo { Name = name, Format = f, Index = k }; }
            void Run(int first, DexFormat f, DexIndexKind k, params string[] names) { for (int i = 0; i < names.Length; i++) Set(first + i, names[i], f, k); }

            Set(0x00, "nop", DexFormat.F10x);
            Set(0x01, "move", DexFormat.F12x); Set(0x02, "move/from16", DexFormat.F22x); Set(0x03, "move/16", DexFormat.F32x);
            Set(0x04, "move-wide", DexFormat.F12x); Set(0x05, "move-wide/from16", DexFormat.F22x); Set(0x06, "move-wide/16", DexFormat.F32x);
            Set(0x07, "move-object", DexFormat.F12x); Set(0x08, "move-object/from16", DexFormat.F22x); Set(0x09, "move-object/16", DexFormat.F32x);
            Run(0x0a, DexFormat.F11x, DexIndexKind.None, "move-result", "move-result-wide", "move-result-object", "move-exception");
            Set(0x0e, "return-void", DexFormat.F10x);
            Run(0x0f, DexFormat.F11x, DexIndexKind.None, "return", "return-wide", "return-object");
            Set(0x12, "const/4", DexFormat.F11n); Set(0x13, "const/16", DexFormat.F21s); Set(0x14, "const", DexFormat.F31i);
            Set(0x15, "const/high16", DexFormat.F21h); Set(0x16, "const-wide/16", DexFormat.F21s); Set(0x17, "const-wide/32", DexFormat.F31i);
            Set(0x18, "const-wide", DexFormat.F51l); Set(0x19, "const-wide/high16", DexFormat.F21h);
            Set(0x1a, "const-string", DexFormat.F21c, DexIndexKind.String); Set(0x1b, "const-string/jumbo", DexFormat.F31c, DexIndexKind.String);
            Set(0x1c, "const-class", DexFormat.F21c, DexIndexKind.Type);
            Set(0x1d, "monitor-enter", DexFormat.F11x); Set(0x1e, "monitor-exit", DexFormat.F11x);
            Set(0x1f, "check-cast", DexFormat.F21c, DexIndexKind.Type); Set(0x20, "instance-of", DexFormat.F22c, DexIndexKind.Type);
            Set(0x21, "array-length", DexFormat.F12x); Set(0x22, "new-instance", DexFormat.F21c, DexIndexKind.Type);
            Set(0x23, "new-array", DexFormat.F22c, DexIndexKind.Type); Set(0x24, "filled-new-array", DexFormat.F35c, DexIndexKind.Type);
            Set(0x25, "filled-new-array/range", DexFormat.F3rc, DexIndexKind.Type); Set(0x26, "fill-array-data", DexFormat.F31t);
            Set(0x27, "throw", DexFormat.F11x); Set(0x28, "goto", DexFormat.F10t); Set(0x29, "goto/16", DexFormat.F20t); Set(0x2a, "goto/32", DexFormat.F30t);
            Set(0x2b, "packed-switch", DexFormat.F31t); Set(0x2c, "sparse-switch", DexFormat.F31t);
            Run(0x2d, DexFormat.F23x, DexIndexKind.None, "cmpl-float", "cmpg-float", "cmpl-double", "cmpg-double", "cmp-long");
            Run(0x32, DexFormat.F22t, DexIndexKind.None, "if-eq", "if-ne", "if-lt", "if-ge", "if-gt", "if-le");
            Run(0x38, DexFormat.F21t, DexIndexKind.None, "if-eqz", "if-nez", "if-ltz", "if-gez", "if-gtz", "if-lez");
            string[] kinds = { "", "-wide", "-object", "-boolean", "-byte", "-char", "-short" };
            Run(0x44, DexFormat.F23x, DexIndexKind.None, kinds.Select(k => "aget" + k).ToArray());
            Run(0x4b, DexFormat.F23x, DexIndexKind.None, kinds.Select(k => "aput" + k).ToArray());
            Run(0x52, DexFormat.F22c, DexIndexKind.Field, kinds.Select(k => "iget" + k).ToArray());
            Run(0x59, DexFormat.F22c, DexIndexKind.Field, kinds.Select(k => "iput" + k).ToArray());
            Run(0x60, DexFormat.F21c, DexIndexKind.Field, kinds.Select(k => "sget" + k).ToArray());
            Run(0x67, DexFormat.F21c, DexIndexKind.Field, kinds.Select(k => "sput" + k).ToArray());
            Run(0x6e, DexFormat.F35c, DexIndexKind.Method, "invoke-virtual", "invoke-super", "invoke-direct", "invoke-static", "invoke-interface");
            Run(0x74, DexFormat.F3rc, DexIndexKind.Method, "invoke-virtual/range", "invoke-super/range", "invoke-direct/range", "invoke-static/range",
                "invoke-interface/range");
            Run(0x7b, DexFormat.F12x, DexIndexKind.None, "neg-int", "not-int", "neg-long", "not-long", "neg-float", "neg-double", "int-to-long",
                "int-to-float", "int-to-double", "long-to-int", "long-to-float", "long-to-double", "float-to-int", "float-to-long", "float-to-double",
                "double-to-int", "double-to-long", "double-to-float", "int-to-byte", "int-to-char", "int-to-short");
            string[] binops =
            {
                "add-int", "sub-int", "mul-int", "div-int", "rem-int", "and-int", "or-int", "xor-int", "shl-int", "shr-int", "ushr-int",
                "add-long", "sub-long", "mul-long", "div-long", "rem-long", "and-long", "or-long", "xor-long", "shl-long", "shr-long", "ushr-long",
                "add-float", "sub-float", "mul-float", "div-float", "rem-float", "add-double", "sub-double", "mul-double", "div-double", "rem-double",
            };
            Run(0x90, DexFormat.F23x, DexIndexKind.None, binops);
            Run(0xb0, DexFormat.F12x, DexIndexKind.None, binops.Select(b => b + "/2addr").ToArray());
            Run(0xd0, DexFormat.F22s, DexIndexKind.None, "add-int/lit16", "rsub-int", "mul-int/lit16", "div-int/lit16", "rem-int/lit16", "and-int/lit16",
                "or-int/lit16", "xor-int/lit16");
            Run(0xd8, DexFormat.F22b, DexIndexKind.None, "add-int/lit8", "rsub-int/lit8", "mul-int/lit8", "div-int/lit8", "rem-int/lit8", "and-int/lit8",
                "or-int/lit8", "xor-int/lit8", "shl-int/lit8", "shr-int/lit8", "ushr-int/lit8");
            Set(0xfa, "invoke-polymorphic", DexFormat.F45cc, DexIndexKind.Method); Set(0xfb, "invoke-polymorphic/range", DexFormat.F4rcc, DexIndexKind.Method);
            Set(0xfc, "invoke-custom", DexFormat.F35c, DexIndexKind.CallSite); Set(0xfd, "invoke-custom/range", DexFormat.F3rc, DexIndexKind.CallSite);
            Set(0xfe, "const-method-handle", DexFormat.F21c, DexIndexKind.MethodHandle); Set(0xff, "const-method-type", DexFormat.F21c, DexIndexKind.Proto);
            return t;
        }
    }
}
