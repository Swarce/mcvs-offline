using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace McvsOffline.Patcher.Dex
{
    /// <summary>
    /// A Dalvik executable (classes*.dex), read as far as the patcher needs it: the header, the string/type/proto/field/method
    /// id tables, the class definitions with their class_data, and the code items. Read-only: the edits are made on a copy of
    /// the bytes (<see cref="DexEditor"/>), which never moves anything, so every offset read here stays valid.
    /// <para>
    /// Layout (source.android.com "Dalvik executable format", dex_file.h): a 0x70-byte header that holds an Adler-32 checksum of
    /// everything after its own field (offset 12 on) and a SHA-1 signature of everything after the signature (offset 32 on),
    /// then the id tables at the offsets the header names. Strings are MUTF-8 with a ULEB128 UTF-16 length; class_data and
    /// the catch handlers use (S/U)LEB128 numbers. Versions 035 to 039 share this layout; 041 (one container of several dex
    /// files) is refused.
    /// </para>
    /// </summary>
    public sealed class DexFile
    {
        public const int HeaderSize = 0x70;
        public const int ChecksumOffset = 8;
        public const int SignatureOffset = 12;
        public const int SignatureSize = 20;
        private const uint EndianConstant = 0x12345678;
        public const uint NoIndex = 0xFFFFFFFF;

        private readonly byte[] data;
        private readonly string[] strings;
        private Dictionary<string, int> classIndex;
        private Dictionary<string, int> stringIndex;

        private DexFile(string name, byte[] data)
        {
            Name = name;
            this.data = data;
            Version = Encoding.ASCII.GetString(data, 4, 3);
            StringIdsSize = Count(56); StringIdsOff = Offset(60, StringIdsSize, 4);
            TypeIdsSize = Count(64); TypeIdsOff = Offset(68, TypeIdsSize, 4);
            ProtoIdsSize = Count(72); ProtoIdsOff = Offset(76, ProtoIdsSize, 12);
            FieldIdsSize = Count(80); FieldIdsOff = Offset(84, FieldIdsSize, 8);
            MethodIdsSize = Count(88); MethodIdsOff = Offset(92, MethodIdsSize, 8);
            ClassDefsSize = Count(96); ClassDefsOff = Offset(100, ClassDefsSize, 32);
            strings = new string[StringIdsSize];
        }

        /// <summary>The entry name (classes.dex, classes2.dex, ...) used in messages.</summary>
        public string Name { get; }

        /// <summary>"035" ... "039".</summary>
        public string Version { get; }

        public int StringIdsSize { get; }
        public int StringIdsOff { get; }
        public int TypeIdsSize { get; }
        public int TypeIdsOff { get; }
        public int ProtoIdsSize { get; }
        public int ProtoIdsOff { get; }
        public int FieldIdsSize { get; }
        public int FieldIdsOff { get; }
        public int MethodIdsSize { get; }
        public int MethodIdsOff { get; }
        public int ClassDefsSize { get; }
        public int ClassDefsOff { get; }

        /// <summary>
        /// Whether the stored SHA-1 signature is the SHA-1 of the content. Not required: Android never reads it (ART and
        /// dexdump check only the Adler-32 checksum), and both dex files of 1.17.32 carry one that does not match their content
        /// while their checksums do (the tool that wrote them last did not update it). An edited file gets the correct one.
        /// </summary>
        public bool SignatureMatches { get; private set; }

        /// <summary>The file as read (not a copy: callers must not change it).</summary>
        public byte[] Bytes { get { return data; } }

        /// <summary>
        /// Parses and checks a dex file: magic and version, file_size, header_size, endian tag, every id table inside the file,
        /// and the stored checksum against the content (a dex whose checksum does not match was damaged or changed without
        /// care: refused, like every other mismatch). The signature is only compared (<see cref="SignatureMatches"/>).
        /// </summary>
        public static DexFile Parse(string name, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < HeaderSize || data[0] != 'd' || data[1] != 'e' || data[2] != 'x' || data[3] != '\n' || data[7] != 0)
                throw Bad(name, "not a dex file (no 'dex\\n' magic)");
            string version = Encoding.ASCII.GetString(data, 4, 3);
            if (string.CompareOrdinal(version, "035") < 0 || string.CompareOrdinal(version, "039") > 0)
                throw Bad(name, "dex version " + version + " is not supported (035-039)");
            if (U32(data, 32) != (uint)data.Length) throw Bad(name, "file_size " + U32(data, 32) + " does not match the file (" + data.Length + " bytes)");
            if (U32(data, 36) != HeaderSize) throw Bad(name, "header_size is " + U32(data, 36) + ", expected 0x70");
            if (U32(data, 40) != EndianConstant) throw Bad(name, "endian_tag is not 0x12345678");
            uint checksum = ComputeChecksum(data);
            if (checksum != U32(data, ChecksumOffset))
                throw Bad(name, "the stored checksum " + U32(data, ChecksumOffset).ToString("x8") + " does not match the content (" + checksum.ToString("x8") + ")");
            var dex = new DexFile(name, data);
            dex.SignatureMatches = ComputeSignature(data).AsSpan().SequenceEqual(data.AsSpan(SignatureOffset, SignatureSize));
            return dex;
        }

        // ------------------------------------------------------------------ checksum and signature

        /// <summary>Adler-32 of everything after the checksum field (offset 12 to the end).</summary>
        public static uint ComputeChecksum(byte[] dex)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int i = SignatureOffset;
            while (i < dex.Length)
            {
                int end = Math.Min(dex.Length, i + 5552);   // the largest run that cannot overflow b before the reduction
                for (; i < end; i++)
                {
                    a += dex[i];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }

        /// <summary>SHA-1 of everything after the signature field (offset 32 to the end).</summary>
        public static byte[] ComputeSignature(byte[] dex)
        {
            using (SHA1 sha = SHA1.Create())
            {
                return sha.ComputeHash(dex, SignatureOffset + SignatureSize, dex.Length - SignatureOffset - SignatureSize);
            }
        }

        /// <summary>Writes the SHA-1 signature, then the checksum (which covers the signature), into <paramref name="dex"/>.</summary>
        public static void UpdateChecksumAndSignature(byte[] dex)
        {
            Buffer.BlockCopy(ComputeSignature(dex), 0, dex, SignatureOffset, SignatureSize);
            uint checksum = ComputeChecksum(dex);
            dex[ChecksumOffset] = (byte)checksum;
            dex[ChecksumOffset + 1] = (byte)(checksum >> 8);
            dex[ChecksumOffset + 2] = (byte)(checksum >> 16);
            dex[ChecksumOffset + 3] = (byte)(checksum >> 24);
        }

        // ------------------------------------------------------------------ id tables

        /// <summary>The string with index <paramref name="index"/> (MUTF-8 decoded, cached).</summary>
        public string GetString(int index)
        {
            CheckIndex(index, StringIdsSize, "string");
            string s = strings[index];
            if (s == null) strings[index] = s = DecodeString(Offset32(StringIdsOff + 4 * index, "string_data_off"));
            return s;
        }

        /// <summary>A type descriptor such as "Landroid/content/Intent;" or "I".</summary>
        public string GetTypeDescriptor(int typeIndex)
        {
            CheckIndex(typeIndex, TypeIdsSize, "type");
            return GetString(Checked(U32(data, TypeIdsOff + 4 * typeIndex), "descriptor_idx"));
        }

        /// <summary>A prototype as a method descriptor: "(Landroid/content/Context;I)V".</summary>
        public string GetProtoDescriptor(int protoIndex)
        {
            CheckIndex(protoIndex, ProtoIdsSize, "proto");
            int at = ProtoIdsOff + 12 * protoIndex;
            var text = new StringBuilder("(");
            uint parameters = U32(data, at + 8);
            if (parameters != 0)
            {
                int list = Offset32(at + 8, "parameters_off");
                int size = Checked(U32(data, list), "type_list size");
                if (list + 4 + 2L * size > data.Length) throw Bad(Name, "type_list at 0x" + list.ToString("x") + " runs past the end");
                for (int i = 0; i < size; i++) text.Append(GetTypeDescriptor(U16(data, list + 4 + 2 * i)));
            }
            return text.Append(')').Append(GetTypeDescriptor(Checked(U32(data, at + 4), "return_type_idx"))).ToString();
        }

        /// <summary>class_idx, proto_idx and name_idx of a method id.</summary>
        public (int Class, int Proto, int Name) GetMethodId(int methodIndex)
        {
            CheckIndex(methodIndex, MethodIdsSize, "method");
            int at = MethodIdsOff + 8 * methodIndex;
            return (U16(data, at), U16(data, at + 2), Checked(U32(data, at + 4), "name_idx"));
        }

        /// <summary>"Lcom/example/A;->name(Ljava/lang/String;)V".</summary>
        public string GetMethodRef(int methodIndex)
        {
            var m = GetMethodId(methodIndex);
            return GetTypeDescriptor(m.Class) + "->" + GetString(m.Name) + GetProtoDescriptor(m.Proto);
        }

        /// <summary>"Lcom/example/A;->name:Ljava/lang/String;".</summary>
        public string GetFieldRef(int fieldIndex)
        {
            CheckIndex(fieldIndex, FieldIdsSize, "field");
            int at = FieldIdsOff + 8 * fieldIndex;
            return GetTypeDescriptor(U16(data, at)) + "->" + GetString(Checked(U32(data, at + 4), "name_idx")) + ":" + GetTypeDescriptor(U16(data, at + 2));
        }

        /// <summary>
        /// The index of the method id with this exact reference ("Lcls;->name(params)ret"), or -1. Method ids are sorted by
        /// class, name and proto, but a linear scan keeps this independent of that order (it runs a handful of times).
        /// </summary>
        public int FindMethodId(string reference)
        {
            int arrow = reference.IndexOf("->", StringComparison.Ordinal);
            int paren = reference.IndexOf('(', arrow + 2);
            if (arrow <= 0 || paren < 0) throw new ArgumentException("not a method reference: " + reference);
            string cls = reference.Substring(0, arrow), name = reference.Substring(arrow + 2, paren - arrow - 2), proto = reference.Substring(paren);
            int nameIndex = FindString(name);
            if (nameIndex < 0) return -1;
            for (int i = 0; i < MethodIdsSize; i++)
            {
                var m = GetMethodId(i);
                if (m.Name == nameIndex && GetTypeDescriptor(m.Class) == cls && GetProtoDescriptor(m.Proto) == proto) return i;
            }
            return -1;
        }

        /// <summary>The index of the string with this exact text, or -1.</summary>
        public int FindString(string text)
        {
            if (stringIndex == null)
            {
                var map = new Dictionary<string, int>(StringIdsSize, StringComparer.Ordinal);
                for (int i = 0; i < StringIdsSize; i++) map[GetString(i)] = i;
                stringIndex = map;
            }
            return stringIndex.TryGetValue(text, out int index) ? index : -1;
        }

        /// <summary>The index of the type id with this exact descriptor ("Landroid/content/ComponentName;"), or -1.</summary>
        public int FindTypeId(string descriptor)
        {
            int name = FindString(descriptor);
            if (name < 0) return -1;
            for (int i = 0; i < TypeIdsSize; i++)
            {
                if (U32(data, TypeIdsOff + 4 * i) == (uint)name) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ classes and methods

        /// <summary>One class_def_item.</summary>
        public sealed class ClassDef
        {
            internal ClassDef(int index, string descriptor, int classDataOff)
            {
                Index = index;
                Descriptor = descriptor;
                ClassDataOff = classDataOff;
            }

            public int Index { get; }

            public string Descriptor { get; }

            /// <summary>0 for a class without fields and methods (an interface with no code, a marker class).</summary>
            public int ClassDataOff { get; }
        }

        /// <summary>One encoded_method of a class_data_item.</summary>
        public sealed class Method
        {
            internal Method(DexFile dex, ClassDef owner, int methodIndex, uint accessFlags, int codeOff)
            {
                Owner = owner;
                MethodIndex = methodIndex;
                AccessFlags = accessFlags;
                CodeOff = codeOff;
                var id = dex.GetMethodId(methodIndex);
                Name = dex.GetString(id.Name);
                Descriptor = dex.GetProtoDescriptor(id.Proto);
            }

            public ClassDef Owner { get; }

            public int MethodIndex { get; }

            public uint AccessFlags { get; }

            /// <summary>0 for an abstract or native method.</summary>
            public int CodeOff { get; }

            public string Name { get; }

            /// <summary>"(Landroid/content/Context;)V".</summary>
            public string Descriptor { get; }

            /// <summary>"Lcom/example/A;->name(...)V".</summary>
            public string Reference { get { return Owner.Descriptor + "->" + Name + Descriptor; } }

            /// <summary>"com.example.A.name(...)V", for reports.</summary>
            public string DisplayName { get { return JavaName(Owner.Descriptor) + "." + Name + Descriptor; } }

            public override string ToString()
            {
                return Reference;
            }
        }

        /// <summary>"Lcom/example/A$B;" -> "com.example.A$B".</summary>
        public static string JavaName(string descriptor)
        {
            if (descriptor.Length > 2 && descriptor[0] == 'L' && descriptor[descriptor.Length - 1] == ';')
                return descriptor.Substring(1, descriptor.Length - 2).Replace('/', '.');
            return descriptor;
        }

        public ClassDef GetClassDef(int index)
        {
            CheckIndex(index, ClassDefsSize, "class_def");
            int at = ClassDefsOff + 32 * index;
            string descriptor = GetTypeDescriptor(Checked(U32(data, at), "class_idx"));
            uint classData = U32(data, at + 24);
            return new ClassDef(index, descriptor, classData == 0 ? 0 : Offset32(at + 24, "class_data_off"));
        }

        public IEnumerable<ClassDef> Classes()
        {
            for (int i = 0; i < ClassDefsSize; i++) yield return GetClassDef(i);
        }

        /// <summary>The class defined in this file with this descriptor, or null.</summary>
        public ClassDef FindClass(string descriptor)
        {
            if (classIndex == null)
            {
                var map = new Dictionary<string, int>(ClassDefsSize, StringComparer.Ordinal);
                for (int i = 0; i < ClassDefsSize; i++)
                {
                    string d = GetClassDef(i).Descriptor;
                    if (map.ContainsKey(d)) throw Bad(Name, "class " + d + " is defined twice");
                    map[d] = i;
                }
                classIndex = map;
            }
            return classIndex.TryGetValue(descriptor, out int index) ? GetClassDef(index) : null;
        }

        /// <summary>The direct methods, then the virtual methods, of a class (empty without class_data).</summary>
        public List<Method> GetMethods(ClassDef cls)
        {
            var methods = new List<Method>();
            if (cls.ClassDataOff == 0) return methods;
            int at = cls.ClassDataOff;
            int staticFields = Checked(ReadUleb(ref at), "static_fields_size");
            int instanceFields = Checked(ReadUleb(ref at), "instance_fields_size");
            int direct = Checked(ReadUleb(ref at), "direct_methods_size");
            int virtuals = Checked(ReadUleb(ref at), "virtual_methods_size");
            for (int i = 0; i < staticFields + instanceFields; i++)
            {
                ReadUleb(ref at);
                ReadUleb(ref at);
            }
            foreach (int count in new[] { direct, virtuals })
            {
                long index = 0;
                for (int i = 0; i < count; i++)
                {
                    index += ReadUleb(ref at);
                    uint access = ReadUleb(ref at);
                    uint code = ReadUleb(ref at);
                    if (code != 0 && (code < HeaderSize || code >= data.Length)) throw Bad(Name, "code_off 0x" + code.ToString("x") + " out of range in " + cls.Descriptor);
                    methods.Add(new Method(this, cls, Checked((uint)index, "method_idx"), access, (int)code));
                }
            }
            return methods;
        }

        /// <summary>
        /// The one method of <paramref name="classDescriptor"/> with this name and descriptor, or null when the class is not in
        /// this file. Throws when the class is here but the method is not (or is there twice, which a valid dex cannot have).
        /// </summary>
        public Method FindMethod(string classDescriptor, string name, string descriptor)
        {
            ClassDef cls = FindClass(classDescriptor);
            if (cls == null) return null;
            var matches = GetMethods(cls).Where(m => m.Name == name && m.Descriptor == descriptor).ToList();
            if (matches.Count != 1)
                throw Bad(Name, "class " + classDescriptor + " has " + matches.Count + " methods " + name + descriptor + ", expected exactly one");
            return matches[0];
        }

        // ------------------------------------------------------------------ code items

        /// <summary>One try_item.</summary>
        public struct TryItem
        {
            public int StartAddr;
            public int InsnCount;

            /// <summary>Offset of its encoded_catch_handler, relative to the start of the handler list.</summary>
            public int HandlerOff;
        }

        /// <summary>One encoded_catch_handler: typed catches in order, then the optional catch-all.</summary>
        public sealed class CatchHandler
        {
            public List<(int TypeIndex, int Addr)> Catches { get; } = new List<(int, int)>();

            /// <summary>-1 when there is no catch-all.</summary>
            public int CatchAllAddr { get; set; } = -1;

            public IEnumerable<int> Addresses
            {
                get { return Catches.Select(c => c.Addr).Concat(CatchAllAddr >= 0 ? new[] { CatchAllAddr } : new int[0]); }
            }
        }

        /// <summary>A code_item: header, instructions, tries and handlers, with its exact extent in the file.</summary>
        public sealed class CodeItem
        {
            public int Offset { get; internal set; }

            public int RegistersSize { get; internal set; }

            public int InsSize { get; internal set; }

            public int OutsSize { get; internal set; }

            public uint DebugInfoOff { get; internal set; }

            /// <summary>File offset of insns[0].</summary>
            public int InsnsOffset { get; internal set; }

            /// <summary>The instructions as 16-bit code units (a copy).</summary>
            public ushort[] Insns { get; internal set; }

            public List<TryItem> Tries { get; } = new List<TryItem>();

            /// <summary>Handlers by their offset in the handler list (what <see cref="TryItem.HandlerOff"/> names).</summary>
            public Dictionary<int, CatchHandler> Handlers { get; } = new Dictionary<int, CatchHandler>();

            /// <summary>File offset just past the item (after the handlers, or after insns when there are no tries).</summary>
            public int EndOffset { get; internal set; }

            /// <summary>File offset of the code unit at <paramref name="pc"/>.</summary>
            public int FileOffsetOf(int pc)
            {
                return InsnsOffset + 2 * pc;
            }
        }

        public CodeItem GetCode(Method method)
        {
            if (method.CodeOff == 0) throw Bad(Name, method + " has no code (abstract or native)");
            return GetCode(method.CodeOff);
        }

        public CodeItem GetCode(int codeOff)
        {
            if ((codeOff & 3) != 0 || codeOff + 16 > data.Length) throw Bad(Name, "code_item at 0x" + codeOff.ToString("x") + " is misaligned or out of range");
            var code = new CodeItem
            {
                Offset = codeOff,
                RegistersSize = U16(data, codeOff),
                InsSize = U16(data, codeOff + 2),
                OutsSize = U16(data, codeOff + 4),
                DebugInfoOff = U32(data, codeOff + 8),
                InsnsOffset = codeOff + 16,
            };
            int triesSize = U16(data, codeOff + 6);
            int units = Checked(U32(data, codeOff + 12), "insns_size");
            if (code.InsnsOffset + 2L * units > data.Length) throw Bad(Name, "insns of the code_item at 0x" + codeOff.ToString("x") + " run past the end");
            var insns = new ushort[units];
            for (int i = 0; i < units; i++) insns[i] = U16(data, code.InsnsOffset + 2 * i);
            code.Insns = insns;
            int at = code.InsnsOffset + 2 * units;
            if (triesSize == 0)
            {
                code.EndOffset = at;
                return code;
            }
            if ((units & 1) != 0) at += 2;   // padding to a 4-byte boundary before the tries
            if (at + 8L * triesSize > data.Length) throw Bad(Name, "tries of the code_item at 0x" + codeOff.ToString("x") + " run past the end");
            for (int i = 0; i < triesSize; i++)
            {
                code.Tries.Add(new TryItem { StartAddr = Checked(U32(data, at), "start_addr"), InsnCount = U16(data, at + 4), HandlerOff = U16(data, at + 6) });
                at += 8;
            }
            int listStart = at;
            int handlers = Checked(ReadUleb(ref at), "handlers size");
            for (int i = 0; i < handlers; i++)
            {
                var handler = new CatchHandler();
                int relative = at - listStart;
                int size = ReadSleb(ref at);
                for (int j = 0; j < Math.Abs(size); j++)
                {
                    int type = Checked(ReadUleb(ref at), "catch type_idx");
                    int addr = Checked(ReadUleb(ref at), "catch addr");
                    handler.Catches.Add((type, addr));
                }
                if (size <= 0) handler.CatchAllAddr = Checked(ReadUleb(ref at), "catch_all_addr");
                code.Handlers[relative] = handler;
            }
            foreach (TryItem t in code.Tries)
            {
                if (!code.Handlers.ContainsKey(t.HandlerOff)) throw Bad(Name, "a try_item of the code_item at 0x" + codeOff.ToString("x") + " names no handler");
            }
            code.EndOffset = at;
            return code;
        }

        /// <summary>The code_item exactly as stored, from its first byte to <see cref="CodeItem.EndOffset"/>.</summary>
        public byte[] GetCodeBytes(CodeItem code)
        {
            return data.AsSpan(code.Offset, code.EndOffset - code.Offset).ToArray();
        }

        // ------------------------------------------------------------------ low level

        private string DecodeString(int at)
        {
            ReadUleb(ref at);   // utf16_size; the text is decoded from the bytes up to the terminating 0
            var chars = new StringBuilder();
            while (true)
            {
                if (at >= data.Length) throw Bad(Name, "unterminated string");
                int b = data[at++];
                if (b == 0) return chars.ToString();
                if (b < 0x80)
                {
                    chars.Append((char)b);
                }
                else if ((b & 0xE0) == 0xC0)
                {
                    chars.Append((char)(((b & 0x1F) << 6) | (Next(ref at) & 0x3F)));
                }
                else if ((b & 0xF0) == 0xE0)
                {
                    int b2 = Next(ref at), b3 = Next(ref at);
                    chars.Append((char)(((b & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F)));
                }
                else
                {
                    throw Bad(Name, "invalid MUTF-8 byte 0x" + b.ToString("x2") + " at 0x" + (at - 1).ToString("x"));
                }
            }
        }

        private int Next(ref int at)
        {
            if (at >= data.Length || (data[at] & 0xC0) != 0x80) throw Bad(Name, "invalid MUTF-8 sequence at 0x" + at.ToString("x"));
            return data[at++];
        }

        internal uint ReadUleb(ref int at)
        {
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                if (at >= data.Length) throw Bad(Name, "LEB128 number runs past the end");
                byte b = data[at++];
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw Bad(Name, "LEB128 number longer than 5 bytes at 0x" + at.ToString("x"));
        }

        private int ReadSleb(ref int at)
        {
            int result = 0, shift = 0;
            byte b;
            do
            {
                if (at >= data.Length || shift >= 35) throw Bad(Name, "SLEB128 number out of range at 0x" + at.ToString("x"));
                b = data[at++];
                result |= (b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);
            if (shift < 32 && (b & 0x40) != 0) result |= -1 << shift;
            return result;
        }

        private int Count(int field)
        {
            return Checked(U32(data, field), "header count at 0x" + field.ToString("x"));
        }

        private int Offset(int field, int count, int itemSize)
        {
            uint off = U32(data, field);
            if (count == 0) return 0;
            if (off < HeaderSize || off + (long)count * itemSize > data.Length)
                throw Bad(Name, "the table at header offset 0x" + field.ToString("x") + " lies outside the file");
            return (int)off;
        }

        private int Offset32(int field, string what)
        {
            uint off = U32(data, field);
            if (off < HeaderSize || off >= data.Length) throw Bad(Name, what + " 0x" + off.ToString("x") + " out of range");
            return (int)off;
        }

        private void CheckIndex(int index, int size, string what)
        {
            if (index < 0 || index >= size) throw Bad(Name, what + " index " + index + " out of range (" + size + ")");
        }

        private int Checked(uint value, string what)
        {
            if (value > int.MaxValue) throw Bad(Name, what + " " + value + " out of range");
            return (int)value;
        }

        internal static ushort U16(byte[] d, int at)
        {
            return (ushort)(d[at] | d[at + 1] << 8);
        }

        internal static uint U32(byte[] d, int at)
        {
            return (uint)(d[at] | d[at + 1] << 8 | d[at + 2] << 16 | d[at + 3] << 24);
        }

        internal static InvalidDataException Bad(string name, string why)
        {
            return new InvalidDataException((name ?? "dex") + ": " + why);
        }
    }
}
