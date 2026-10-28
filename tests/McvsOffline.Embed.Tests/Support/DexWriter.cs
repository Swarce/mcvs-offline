using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Patcher.Dex;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>
    /// Writes a small, valid dex file the way d8 lays one out: sorted string/type/proto/field/method ids, class_defs, then the
    /// data section (code items, type lists, string data, class data, map list), with the SHA-1 signature and the Adler-32
    /// checksum. Every method is a direct method (public static, or a constructor) without debug info. A method's code is a
    /// function of the writer so it can ask for the index of a string, type, field or method; it runs twice, once to collect
    /// the references and once, after sorting, to encode them. Test fixture only.
    /// </summary>
    internal sealed class DexWriter
    {
        public const string Object = "Ljava/lang/Object;";

        private sealed class MethodDef
        {
            public string Class;
            public string Name;
            public string Descriptor;
            public int Registers;
            public int Ins;
            public int Outs;
            public Func<DexWriter, ushort[]> Code;
            public (int Start, int Count, int CatchAll)[] Tries;
        }

        private readonly List<MethodDef> methods = new List<MethodDef>();
        private readonly List<string> extraClasses = new List<string>();
        private bool collecting;
        private SortedSet<string> stringSet, typeSet;
        private HashSet<string> protoSet, fieldSet, methodSet;
        private List<string> strings, types, protos, fields, methodRefs;

        /// <summary>Adds a method (and its class, if new) with this code.</summary>
        public DexWriter Method(string cls, string name, string descriptor, int registers, int ins, int outs, Func<DexWriter, ushort[]> code,
                                params (int Start, int Count, int CatchAll)[] tries)
        {
            methods.Add(new MethodDef { Class = cls, Name = name, Descriptor = descriptor, Registers = registers, Ins = ins, Outs = outs, Code = code, Tries = tries });
            return this;
        }

        /// <summary>Adds a class without methods.</summary>
        public DexWriter EmptyClass(string cls)
        {
            extraClasses.Add(cls);
            return this;
        }

        public ushort StringIndex(string s)
        {
            if (collecting) { stringSet.Add(s); return 0; }
            return Find(strings, s, "string");
        }

        public ushort TypeIndex(string descriptor)
        {
            if (collecting) { AddType(descriptor); return 0; }
            return Find(types, descriptor, "type");
        }

        /// <summary>"Lcls;->name:Type".</summary>
        public ushort FieldIndex(string reference)
        {
            if (collecting)
            {
                fieldSet.Add(reference);
                (string cls, string name, string type) = SplitField(reference);
                AddType(cls); AddType(type); stringSet.Add(name);
                return 0;
            }
            return Find(fields, reference, "field");
        }

        /// <summary>"Lcls;->name(params)ret".</summary>
        public ushort MethodIndex(string reference)
        {
            if (collecting)
            {
                AddMethodRef(reference);
                return 0;
            }
            return Find(methodRefs, reference, "method");
        }

        public byte[] ToArray()
        {
            // Pass 1: collect every reference.
            collecting = true;
            stringSet = new SortedSet<string>(StringComparer.Ordinal);
            typeSet = new SortedSet<string>(StringComparer.Ordinal);
            protoSet = new HashSet<string>(StringComparer.Ordinal);
            fieldSet = new HashSet<string>(StringComparer.Ordinal);
            methodSet = new HashSet<string>(StringComparer.Ordinal);
            AddType(Object);
            foreach (string c in extraClasses) AddType(c);
            foreach (MethodDef m in methods)
            {
                AddMethodRef(m.Class + "->" + m.Name + m.Descriptor);
                m.Code(this);
            }
            collecting = false;

            // Sorted ids, as the format requires.
            strings = stringSet.ToList();
            types = typeSet.OrderBy(t => strings.IndexOf(t)).ToList();
            protos = protoSet.OrderBy(p => types.IndexOf(ReturnOf(p))).ThenBy(p => string.Join(",", ParamsOf(p).Select(t => types.IndexOf(t).ToString("d6"))), StringComparer.Ordinal).ToList();
            fields = fieldSet.Select(f => (f, SplitField(f))).OrderBy(x => types.IndexOf(x.Item2.Class)).ThenBy(x => strings.IndexOf(x.Item2.Name))
                             .ThenBy(x => types.IndexOf(x.Item2.Type)).Select(x => x.f).ToList();
            methodRefs = methodSet.Select(r => (r, SplitMethod(r))).OrderBy(x => types.IndexOf(x.Item2.Class)).ThenBy(x => strings.IndexOf(x.Item2.Name))
                                  .ThenBy(x => protos.IndexOf(x.Item2.Proto)).Select(x => x.r).ToList();
            var classes = methods.Select(m => m.Class).Concat(extraClasses).Distinct().OrderBy(c => types.IndexOf(c)).ToList();

            int stringIdsOff = DexFile.HeaderSize;
            int typeIdsOff = stringIdsOff + 4 * strings.Count;
            int protoIdsOff = typeIdsOff + 4 * types.Count;
            int fieldIdsOff = protoIdsOff + 12 * protos.Count;
            int methodIdsOff = fieldIdsOff + 8 * fields.Count;
            int classDefsOff = methodIdsOff + 8 * methodRefs.Count;
            int dataOff = classDefsOff + 32 * classes.Count;

            var data = new MemoryStream();
            var map = new List<(ushort Type, int Size, int Offset)>();
            int Pos() => dataOff + (int)data.Length;
            void Align4() { while (Pos() % 4 != 0) data.WriteByte(0); }

            // Code items.
            var codeOffsets = new Dictionary<MethodDef, int>();
            Align4();
            int codeStart = Pos();
            foreach (MethodDef m in methods)
            {
                Align4();
                codeOffsets[m] = Pos();
                ushort[] insns = m.Code(this);
                W16(data, m.Registers); W16(data, m.Ins); W16(data, m.Outs); W16(data, m.Tries.Length);
                W32(data, 0); W32(data, insns.Length);
                foreach (ushort u in insns) W16(data, u);
                if (m.Tries.Length > 0)
                {
                    if (insns.Length % 2 != 0) W16(data, 0);
                    var handlers = new MemoryStream();
                    WriteUleb(handlers, (uint)m.Tries.Length);
                    var handlerOffsets = new List<int>();
                    foreach (var t in m.Tries)
                    {
                        handlerOffsets.Add((int)handlers.Length);
                        WriteSleb(handlers, 0);                 // catch-all only
                        WriteUleb(handlers, (uint)t.CatchAll);
                    }
                    for (int i = 0; i < m.Tries.Length; i++)
                    {
                        W32(data, m.Tries[i].Start); W16(data, m.Tries[i].Count); W16(data, handlerOffsets[i]);
                    }
                    data.Write(handlers.ToArray());
                }
            }
            if (methods.Count > 0) map.Add((0x2001, methods.Count, codeStart));

            // Type lists (proto parameters).
            var typeListOffsets = new Dictionary<string, int>();
            Align4();
            int typeListStart = Pos();
            foreach (string p in protos.Where(p => ParamsOf(p).Count > 0))
            {
                Align4();
                typeListOffsets[p] = Pos();
                List<string> ps = ParamsOf(p);
                W32(data, ps.Count);
                foreach (string t in ps) W16(data, types.IndexOf(t));
            }
            if (typeListOffsets.Count > 0) map.Add((0x1001, typeListOffsets.Count, typeListStart));

            // String data.
            var stringOffsets = new List<int>();
            int stringDataStart = Pos();
            foreach (string s in strings)
            {
                stringOffsets.Add(Pos());
                WriteUleb(data, (uint)s.Length);
                data.Write(Mutf8(s));
                data.WriteByte(0);
            }
            map.Add((0x2002, strings.Count, stringDataStart));

            // Class data.
            var classDataOffsets = new Dictionary<string, int>();
            int classDataStart = Pos();
            foreach (string c in classes)
            {
                List<MethodDef> own = methods.Where(m => m.Class == c).OrderBy(m => methodRefs.IndexOf(m.Class + "->" + m.Name + m.Descriptor)).ToList();
                if (own.Count == 0) continue;
                classDataOffsets[c] = Pos();
                WriteUleb(data, 0); WriteUleb(data, 0); WriteUleb(data, (uint)own.Count); WriteUleb(data, 0);
                int previous = 0;
                foreach (MethodDef m in own)
                {
                    int index = methodRefs.IndexOf(m.Class + "->" + m.Name + m.Descriptor);
                    WriteUleb(data, (uint)(index - previous));
                    previous = index;
                    WriteUleb(data, m.Name == "<init>" ? 0x10001u : 0x9u);
                    WriteUleb(data, (uint)codeOffsets[m]);
                }
            }
            if (classDataOffsets.Count > 0) map.Add((0x2000, classDataOffsets.Count, classDataStart));

            // Map list.
            Align4();
            int mapOff = Pos();
            map.Add((0x1000, 1, mapOff));
            var head = new List<(ushort Type, int Size, int Offset)> { (0x0000, 1, 0), (0x0001, strings.Count, stringIdsOff), (0x0002, types.Count, typeIdsOff) };
            if (protos.Count > 0) head.Add((0x0003, protos.Count, protoIdsOff));
            if (fields.Count > 0) head.Add((0x0004, fields.Count, fieldIdsOff));
            if (methodRefs.Count > 0) head.Add((0x0005, methodRefs.Count, methodIdsOff));
            if (classes.Count > 0) head.Add((0x0006, classes.Count, classDefsOff));
            var all = head.Concat(map).OrderBy(e => e.Offset).ToList();
            W32(data, all.Count);
            foreach (var e in all)
            {
                W16(data, e.Type); W16(data, 0); W32(data, e.Size); W32(data, e.Offset);
            }

            // Header and id tables.
            var file = new MemoryStream();
            file.Write(Encoding.ASCII.GetBytes("dex\n035\0"));
            file.Write(new byte[24]);                        // checksum and signature, filled in last
            int fileSize = dataOff + (int)data.Length;
            W32(file, fileSize); W32(file, DexFile.HeaderSize); W32(file, 0x12345678); W32(file, 0); W32(file, 0); W32(file, mapOff);
            W32(file, strings.Count); W32(file, strings.Count == 0 ? 0 : stringIdsOff);
            W32(file, types.Count); W32(file, types.Count == 0 ? 0 : typeIdsOff);
            W32(file, protos.Count); W32(file, protos.Count == 0 ? 0 : protoIdsOff);
            W32(file, fields.Count); W32(file, fields.Count == 0 ? 0 : fieldIdsOff);
            W32(file, methodRefs.Count); W32(file, methodRefs.Count == 0 ? 0 : methodIdsOff);
            W32(file, classes.Count); W32(file, classes.Count == 0 ? 0 : classDefsOff);
            W32(file, (int)data.Length); W32(file, dataOff);
            foreach (int o in stringOffsets) W32(file, o);
            foreach (string t in types) W32(file, strings.IndexOf(t));
            foreach (string p in protos)
            {
                W32(file, strings.IndexOf(Shorty(p))); W32(file, types.IndexOf(ReturnOf(p)));
                W32(file, typeListOffsets.TryGetValue(p, out int tl) ? tl : 0);
            }
            foreach (string f in fields)
            {
                var x = SplitField(f);
                W16(file, types.IndexOf(x.Class)); W16(file, types.IndexOf(x.Type)); W32(file, strings.IndexOf(x.Name));
            }
            foreach (string r in methodRefs)
            {
                var x = SplitMethod(r);
                W16(file, types.IndexOf(x.Class)); W16(file, protos.IndexOf(x.Proto)); W32(file, strings.IndexOf(x.Name));
            }
            foreach (string c in classes)
            {
                W32(file, types.IndexOf(c)); W32(file, 1); W32(file, types.IndexOf(Object)); W32(file, 0);
                W32(file, unchecked((int)DexFile.NoIndex)); W32(file, 0); W32(file, classDataOffsets.TryGetValue(c, out int cd) ? cd : 0); W32(file, 0);
            }
            if (file.Length != dataOff) throw new InvalidOperationException("id tables do not end at data_off");
            file.Write(data.ToArray());
            byte[] bytes = file.ToArray();
            DexFile.UpdateChecksumAndSignature(bytes);
            return bytes;
        }

        // ------------------------------------------------------------------ encoding helpers for test code

        public static ushort[] Code(params ushort[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        public static ushort[] Nop() => new ushort[] { 0x0000 };
        public static ushort[] ReturnVoid() => new ushort[] { 0x000e };
        public static ushort[] Return(int a) => new ushort[] { (ushort)(0x0f | a << 8) };
        public static ushort[] ReturnObject(int a) => new ushort[] { (ushort)(0x11 | a << 8) };
        public static ushort[] Const4(int a, int value) => new ushort[] { (ushort)(0x12 | a << 8 | (value & 0xF) << 12) };
        public static ushort[] Const16(int a, int value) => new ushort[] { (ushort)(0x13 | a << 8), (ushort)value };
        public static ushort[] Const(int a, uint value) => new ushort[] { (ushort)(0x14 | a << 8), (ushort)value, (ushort)(value >> 16) };
        public static ushort[] ConstHigh16(int a, uint value) => new ushort[] { (ushort)(0x15 | a << 8), (ushort)(value >> 16) };
        public static ushort[] ConstString(int a, ushort index) => new ushort[] { (ushort)(0x1a | a << 8), index };
        public static ushort[] NewInstance(int a, ushort type) => new ushort[] { (ushort)(0x22 | a << 8), type };
        public static ushort[] SputObject(int a, ushort field) => new ushort[] { (ushort)(0x69 | a << 8), field };
        public static ushort[] SgetObject(int a, ushort field) => new ushort[] { (ushort)(0x62 | a << 8), field };
        public static ushort[] MoveResult(int a) => new ushort[] { (ushort)(0x0a | a << 8) };
        public static ushort[] MoveResultObject(int a) => new ushort[] { (ushort)(0x0c | a << 8) };
        public static ushort[] MoveException(int a) => new ushort[] { (ushort)(0x0d | a << 8) };
        public static ushort[] Move(int a, int b) => new ushort[] { (ushort)(0x01 | a << 8 | b << 12) };
        public static ushort[] AddInt2Addr(int a, int b) => new ushort[] { (ushort)(0xb0 | a << 8 | b << 12) };
        public static ushort[] Goto(int offset) => new ushort[] { (ushort)(0x28 | (offset & 0xFF) << 8) };
        public static ushort[] IfEqz(int a, int offset) => new ushort[] { (ushort)(0x38 | a << 8), (ushort)offset };
        public static ushort[] Throw(int a) => new ushort[] { (ushort)(0x27 | a << 8) };

        /// <summary>A 35c invoke: 0x6e virtual, 0x70 direct, 0x71 static.</summary>
        public static ushort[] Invoke(int opcode, ushort method, params int[] args)
        {
            int[] r = args.Concat(new int[5]).Take(5).ToArray();
            return new[] { (ushort)(opcode | args.Length << 12 | r[4] << 8), method, (ushort)(r[0] | r[1] << 4 | r[2] << 8 | r[3] << 12) };
        }

        public static ushort[] PackedSwitch(int a, int payloadOffset) => new ushort[] { (ushort)(0x2b | a << 8), (ushort)payloadOffset, (ushort)(payloadOffset >> 16) };

        /// <summary>packed-switch-payload; targets are relative to the switch instruction.</summary>
        public static ushort[] PackedSwitchPayload(int firstKey, params int[] targets)
        {
            var units = new List<ushort> { 0x0100, (ushort)targets.Length, (ushort)firstKey, (ushort)(firstKey >> 16) };
            foreach (int t in targets) { units.Add((ushort)t); units.Add((ushort)(t >> 16)); }
            return units.ToArray();
        }

        // ------------------------------------------------------------------ internals

        private void AddType(string descriptor)
        {
            typeSet.Add(descriptor);
            stringSet.Add(descriptor);
        }

        private void AddMethodRef(string reference)
        {
            methodSet.Add(reference);
            var x = SplitMethod(reference);
            AddType(x.Class);
            stringSet.Add(x.Name);
            protoSet.Add(x.Proto);
            stringSet.Add(Shorty(x.Proto));
            AddType(ReturnOf(x.Proto));
            foreach (string p in ParamsOf(x.Proto)) AddType(p);
        }

        private static ushort Find(List<string> list, string item, string what)
        {
            int i = list.IndexOf(item);
            if (i < 0) throw new InvalidOperationException(what + " " + item + " was not collected");
            return (ushort)i;
        }

        private static (string Class, string Name, string Type) SplitField(string reference)
        {
            int arrow = reference.IndexOf("->", StringComparison.Ordinal), colon = reference.IndexOf(':', arrow);
            return (reference.Substring(0, arrow), reference.Substring(arrow + 2, colon - arrow - 2), reference.Substring(colon + 1));
        }

        private static (string Class, string Name, string Proto) SplitMethod(string reference)
        {
            int arrow = reference.IndexOf("->", StringComparison.Ordinal), paren = reference.IndexOf('(', arrow);
            return (reference.Substring(0, arrow), reference.Substring(arrow + 2, paren - arrow - 2), reference.Substring(paren));
        }

        private static string ReturnOf(string proto)
        {
            return proto.Substring(proto.IndexOf(')') + 1);
        }

        private static List<string> ParamsOf(string proto)
        {
            var list = new List<string>();
            string p = proto.Substring(1, proto.IndexOf(')') - 1);
            for (int i = 0; i < p.Length;)
            {
                int start = i;
                while (p[i] == '[') i++;
                if (p[i] == 'L') i = p.IndexOf(';', i);
                i++;
                list.Add(p.Substring(start, i - start));
            }
            return list;
        }

        private static string Shorty(string proto)
        {
            static char S(string t) => t[0] == '[' || t[0] == 'L' ? 'L' : t[0];
            return S(ReturnOf(proto)) + new string(ParamsOf(proto).Select(S).ToArray());
        }

        private static byte[] Mutf8(string s)
        {
            var b = new List<byte>();
            foreach (char c in s)
            {
                if (c != 0 && c < 0x80) b.Add((byte)c);
                else if (c < 0x800) { b.Add((byte)(0xC0 | c >> 6)); b.Add((byte)(0x80 | (c & 0x3F))); }
                else { b.Add((byte)(0xE0 | c >> 12)); b.Add((byte)(0x80 | ((c >> 6) & 0x3F))); b.Add((byte)(0x80 | (c & 0x3F))); }
            }
            return b.ToArray();
        }

        private static void W16(Stream s, int v)
        {
            s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8));
        }

        private static void W32(Stream s, int v)
        {
            s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 24));
        }

        private static void WriteUleb(Stream s, uint v)
        {
            do
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                s.WriteByte(v != 0 ? (byte)(b | 0x80) : b);
            }
            while (v != 0);
        }

        private static void WriteSleb(Stream s, int v)
        {
            while (true)
            {
                byte b = (byte)(v & 0x7F);
                v >>= 7;
                bool done = (v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0);
                s.WriteByte(done ? b : (byte)(b | 0x80));
                if (done) return;
            }
        }
    }
}
