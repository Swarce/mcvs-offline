using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Dex;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The dex reader and in-place editor (patcher/Dex) on small synthetic dex files written by <see cref="DexWriter"/>: parse
    /// round trips, instruction decoding and assembling, the edits' byte-exact result, and every refusal (fingerprint, text,
    /// boundaries, size, register liveness through branches and handlers, move-result, return type, multi-dex ownership).
    /// </summary>
    public class DexEditorTests
    {
        private const string Cls = "Lcom/example/Alarm;";
        private const string Ga = "Lcom/example/Ga;";
        private const string GetBroadcast = "Landroid/app/PendingIntent;->getBroadcast(Landroid/content/Context;ILandroid/content/Intent;I)Landroid/app/PendingIntent;";
        private const string FilterWithAction = "Landroid/content/IntentFilter;-><init>(Ljava/lang/String;)V";
        private const string EmptyFilter = "Landroid/content/IntentFilter;-><init>()V";
        private const string Use = Cls + "->use(I)V";
        private const string Make = Cls + "->make()I";

        private readonly ITestOutputHelper output;

        public DexEditorTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>One class with a method per scenario (the comments give each method's pcs).</summary>
        internal static byte[] Fixture(string text = "custom")
        {
            var w = new DexWriter();
            // schedule: 0000 const/4 v0 | 0001 new-instance v1 | 0003 invoke-direct Intent() | 0006 const/high16 v2 |
            //           0008 invoke-static getBroadcast | 000b move-result-object v3 | 000c return-void
            w.Method(Cls, "schedule", "(Landroid/content/Context;)V", 5, 1, 4, d => DexWriter.Code(
                DexWriter.Const4(0, 0), DexWriter.NewInstance(1, d.TypeIndex("Landroid/content/Intent;")),
                DexWriter.Invoke(0x70, d.MethodIndex("Landroid/content/Intent;-><init>()V"), 1),
                DexWriter.ConstHigh16(2, 0x08000000), DexWriter.Invoke(0x71, d.MethodIndex(GetBroadcast), 4, 0, 1, 2),
                DexWriter.MoveResultObject(3), DexWriter.ReturnVoid()));
            // track: 0000 invoke-static Ga.get | 0003 move-result-object v0 | 0004 sput-object | 0006 return-void
            w.Method(Cls, "track", "(Landroid/content/Context;)V", 2, 1, 1, d => DexWriter.Code(
                DexWriter.Invoke(0x71, d.MethodIndex(Ga + "->get(Landroid/content/Context;)" + Ga), 1), DexWriter.MoveResultObject(0),
                DexWriter.SputObject(0, d.FieldIndex(Ga + "->last:" + Ga)), DexWriter.ReturnVoid()));
            // filter: 0000 new-instance v0 | 0002 const-string v1 | 0004 invoke-direct IntentFilter(String) | 0007 sput-object | 0009 return-void
            w.Method(Cls, "filter", "()V", 3, 0, 2, d => DexWriter.Code(
                DexWriter.NewInstance(0, d.TypeIndex("Landroid/content/IntentFilter;")), DexWriter.ConstString(1, d.StringIndex(text)),
                DexWriter.Invoke(0x70, d.MethodIndex(FilterWithAction), 0, 1),
                DexWriter.SputObject(0, d.FieldIndex(Cls + "->filter:Landroid/content/IntentFilter;")), DexWriter.ReturnVoid()));
            // empty: makes IntentFilter.<init>()V a method id of the file.
            w.Method(Cls, "empty", "()V", 1, 0, 1, d => DexWriter.Code(
                DexWriter.NewInstance(0, d.TypeIndex("Landroid/content/IntentFilter;")), DexWriter.Invoke(0x70, d.MethodIndex(EmptyFilter), 0), DexWriter.ReturnVoid()));
            // flags(I)I: 0000 const/4 v0, #1 | 0001 if-eqz v1, @0004 | 0003 const/4 v0, #2 | 0004 return v0
            w.Method(Cls, "flags", "(I)I", 2, 1, 0, d => DexWriter.Code(
                DexWriter.Const4(0, 1), DexWriter.IfEqz(1, 3), DexWriter.Const4(0, 2), DexWriter.Return(0)));
            // param(I)V: 0000 const/4 v0, #0 | 0001 invoke-static use(v1) | 0004 return-void
            w.Method(Cls, "param", "(I)V", 2, 1, 1, d => DexWriter.Code(
                DexWriter.Const4(0, 0), DexWriter.Invoke(0x71, d.MethodIndex(Use), 1), DexWriter.ReturnVoid()));
            // branchy(I)V: 0000 if-eqz v1, @0003 | 0002 const/4 v0, #1 | 0003 const/4 v0, #2 | 0004 return-void
            w.Method(Cls, "branchy", "(I)V", 2, 1, 0, d => DexWriter.Code(
                DexWriter.IfEqz(1, 3), DexWriter.Const4(0, 1), DexWriter.Const4(0, 2), DexWriter.ReturnVoid()));
            // result()V: 0000 invoke-static make() | 0003 move-result v0 | 0004 return-void
            w.Method(Cls, "result", "()V", 1, 0, 0, d => DexWriter.Code(
                DexWriter.Invoke(0x71, d.MethodIndex(Make)), DexWriter.MoveResult(0), DexWriter.ReturnVoid()));
            // guarded()V: try 0000-0004 { 0000 const/4 v0, #1 | 0001 invoke-static make() } 0004 return-void |
            //             catch-all 0005: invoke-static use(v0) | 0008 return-void
            w.Method(Cls, "guarded", "()V", 1, 0, 1, d => DexWriter.Code(
                DexWriter.Const4(0, 1), DexWriter.Invoke(0x71, d.MethodIndex(Make)), DexWriter.ReturnVoid(),
                DexWriter.Invoke(0x71, d.MethodIndex(Use), 0), DexWriter.ReturnVoid()), (0, 4, 5));
            // switchy(I)V: 0000 packed-switch v1, @0006 | 0003 const/4 v0, #0 | 0004 return-void | 0005 nop (align) | 0006 payload -> 0004
            w.Method(Cls, "switchy", "(I)V", 2, 1, 0, d => DexWriter.Code(
                DexWriter.PackedSwitch(1, 6), DexWriter.Const4(0, 0), DexWriter.ReturnVoid(), DexWriter.Nop(), DexWriter.PackedSwitchPayload(0, 4)));
            w.Method(Cls, "use", "(I)V", 1, 1, 0, d => DexWriter.ReturnVoid());
            w.Method(Cls, "make", "()I", 1, 0, 0, d => DexWriter.Code(DexWriter.Const4(0, 3), DexWriter.Return(0)));
            return w.ToArray();
        }

        private static DexFile Parse(byte[] bytes, string name = "classes.dex")
        {
            return DexFile.Parse(name, bytes);
        }

        private static DexMethodEdit Edit(DexFile dex, string method, string descriptor, params DexSiteEdit[] sites)
        {
            DexFile.Method m = dex.FindMethod(Cls, method, descriptor);
            return new DexMethodEdit("T", Cls, method, descriptor, DexEditor.Fingerprint(dex, m), "test", sites);
        }

        private static DexSiteEdit Site(int pc, string before, string after)
        {
            return new DexSiteEdit(pc, new[] { before }, new[] { after });
        }

        private static string[] Render(DexFile dex, string method, string descriptor)
        {
            return DexInstruction.DecodeAll(dex.GetCode(dex.FindMethod(Cls, method, descriptor)).Insns)
                                 .Select(i => DexInstruction.Hex(i.Pc) + " " + i.Render(dex)).ToArray();
        }

        // ------------------------------------------------------------------ reading

        [Fact]
        public void ASyntheticDexParsesBackToWhatWasWritten()
        {
            DexFile dex = Parse(Fixture());
            Assert.Equal("035", dex.Version);
            Assert.True(dex.SignatureMatches);
            DexFile.ClassDef cls = dex.FindClass(Cls);
            Assert.NotNull(cls);
            Assert.Null(dex.FindClass("Lcom/example/Missing;"));
            Assert.Equal(new[] { "branchy", "empty", "filter", "flags", "guarded", "make", "param", "result", "schedule", "switchy", "track", "use" },
                         dex.GetMethods(cls).Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(Cls + "->schedule(Landroid/content/Context;)V", dex.FindMethod(Cls, "schedule", "(Landroid/content/Context;)V").Reference);
            Assert.Equal("com.example.Alarm.flags(I)I", dex.FindMethod(Cls, "flags", "(I)I").DisplayName);
            Assert.Equal(new[]
            {
                "@0000 const/4 v0, #0", "@0001 new-instance v1, Landroid/content/Intent;",
                "@0003 invoke-direct {v1}, Landroid/content/Intent;-><init>()V", "@0006 const/high16 v2, #0x08000000",
                "@0008 invoke-static {v4, v0, v1, v2}, " + GetBroadcast, "@000b move-result-object v3", "@000c return-void",
            }, Render(dex, "schedule", "(Landroid/content/Context;)V"));
            Assert.Equal(new[] { "@0000 new-instance v0, Landroid/content/IntentFilter;", "@0002 const-string v1, \"custom\"",
                                 "@0004 invoke-direct {v0, v1}, " + FilterWithAction, "@0007 sput-object v0, " + Cls + "->filter:Landroid/content/IntentFilter;",
                                 "@0009 return-void" }, Render(dex, "filter", "()V"));
            Assert.Equal(new[] { "@0000 const/4 v0, #1", "@0001 if-eqz v1, @0004", "@0003 const/4 v0, #2", "@0004 return v0" }, Render(dex, "flags", "(I)I"));
            Assert.Equal(new[] { "@0000 packed-switch v1, @0006", "@0003 const/4 v0, #0", "@0004 return-void", "@0005 nop", "@0006 packed-switch-payload (6 units)" },
                         Render(dex, "switchy", "(I)V"));

            DexFile.CodeItem guarded = dex.GetCode(dex.FindMethod(Cls, "guarded", "()V"));
            DexFile.TryItem t = Assert.Single(guarded.Tries);
            Assert.Equal((0, 4), (t.StartAddr, t.InsnCount));
            Assert.Equal(5, guarded.Handlers[t.HandlerOff].CatchAllAddr);
            Assert.Equal(dex.GetCodeBytes(guarded).Length, guarded.EndOffset - guarded.Offset);
            Assert.Equal(-1, dex.FindMethodId(Cls + "->nothing()V"));
            Assert.True(dex.FindMethodId(EmptyFilter) >= 0);
        }

        [Fact]
        public void TheFingerprintPinsTheCodeBytesAndWhatTheyReferTo()
        {
            byte[] bytes = Fixture();
            DexFile dex = Parse(bytes);
            DexFile.Method schedule = dex.FindMethod(Cls, "schedule", "(Landroid/content/Context;)V");
            string fingerprint = DexEditor.Fingerprint(dex, schedule);
            Assert.Matches("^[0-9a-f]{64}$", fingerprint);
            Assert.Equal(fingerprint, DexEditor.Fingerprint(Parse(Fixture()), Parse(Fixture()).FindMethod(Cls, "schedule", "(Landroid/content/Context;)V")));

            // One literal changed: another fingerprint.
            byte[] changed = (byte[])bytes.Clone();
            int at = dex.GetCode(schedule).FileOffsetOf(6) + 2;
            changed[at + 1] = 0x0c;
            DexFile.UpdateChecksumAndSignature(changed);
            DexFile other = Parse(changed);
            Assert.NotEqual(fingerprint, DexEditor.Fingerprint(other, other.FindMethod(Cls, "schedule", "(Landroid/content/Context;)V")));

            // The same code bytes in a file whose tables say something else: "custon" takes the index of "custom".
            DexFile renamed = Parse(Fixture("custon"));
            DexFile.Method filter = dex.FindMethod(Cls, "filter", "()V"), renamedFilter = renamed.FindMethod(Cls, "filter", "()V");
            Assert.Equal(dex.GetCodeBytes(dex.GetCode(filter)), renamed.GetCodeBytes(renamed.GetCode(renamedFilter)));
            Assert.NotEqual(DexEditor.Fingerprint(dex, filter), DexEditor.Fingerprint(renamed, renamedFilter));
        }

        [Fact]
        public void ParseRefusesAnythingButAnIntactDex()
        {
            byte[] good = Fixture();
            byte[] Mutated(Action<byte[]> change, bool fixChecksum)
            {
                byte[] b = (byte[])good.Clone();
                change(b);
                if (fixChecksum) DexFile.UpdateChecksumAndSignature(b);
                return b;
            }
            Assert.Contains("magic", Assert.Throws<InvalidDataException>(() => Parse(Mutated(b => b[0] = (byte)'x', false))).Message);
            Assert.Contains("version 041", Assert.Throws<InvalidDataException>(() => Parse(Mutated(b => { b[5] = (byte)'4'; b[6] = (byte)'1'; }, false))).Message);
            Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => Parse(Mutated(b => b[b.Length - 1] ^= 1, false))).Message);
            Assert.Contains("file_size", Assert.Throws<InvalidDataException>(() => Parse(good.Concat(new byte[4]).ToArray())).Message);
            Assert.Contains("endian_tag", Assert.Throws<InvalidDataException>(() => Parse(Mutated(b => b[40] = 0x21, true))).Message);
            Assert.Contains("lies outside", Assert.Throws<InvalidDataException>(() => Parse(Mutated(b => b[61] = 0x7f, true))).Message);
            Assert.Throws<InvalidDataException>(() => Parse(new byte[10]));

            // Only the signature wrong (the checksum, which covers it, right): accepted, and reported.
            DexFile unsigned = Parse(Mutated(b =>
            {
                b[12] ^= 0xff;
                BitConverter.GetBytes(DexFile.ComputeChecksum(b)).CopyTo(b, DexFile.ChecksumOffset);
            }, false));
            Assert.False(unsigned.SignatureMatches);
        }

        // ------------------------------------------------------------------ instructions

        [Fact]
        public void InstructionsDecodeRenderAndReportTheirRegisters()
        {
            DexFile dex = Parse(Fixture());
            DexInstruction Decode(params ushort[] units) => DexInstruction.Decode(units, 0);

            DexInstruction c = Decode(0x0314, 0x8000, 0x0000);
            Assert.Equal(("const", DexFormat.F31i, 3, 0x8000L), (c.Name, c.Format, c.Size, c.Literal));
            Assert.Equal("const v3, #0x00008000", c.Render(dex));
            Assert.Equal(new[] { 3 }, c.Writes);
            Assert.Empty(c.Reads);

            DexInstruction high = Decode(0x0515, 0x0800);
            Assert.Equal("const/high16 v5, #0x08000000", high.Render(dex));
            Assert.Equal("const/4 v2, #-1", Decode(0xf212).Render(dex));
            Assert.Equal("const-wide/high16 v0, #0x4000000000000000", Decode(0x0019, 0x4000).Render(dex));

            DexInstruction wide = Decode(0x040b);   // move-result-wide v4
            Assert.Equal(new[] { 4, 5 }, wide.Writes);
            DexInstruction addLong = Decode(0x009b, 0x0402);   // add-long v0, v2, v4
            Assert.Equal(new[] { 2, 3, 4, 5 }, addLong.Reads);
            Assert.Equal(new[] { 0, 1 }, addLong.Writes);
            DexInstruction shl = Decode(0x00a3, 0x0402);   // shl-long v0, v2, v4: the shift amount is an int
            Assert.Equal(new[] { 2, 3, 4 }, shl.Reads);
            DexInstruction toLong = Decode(0x2081);   // int-to-long v0, v2
            Assert.Equal(new[] { 2 }, toLong.Reads);
            Assert.Equal(new[] { 0, 1 }, toLong.Writes);
            DexInstruction cast = Decode(0x021f, 0x0001);
            Assert.Equal(new[] { 2 }, cast.Reads);
            Assert.Equal(new[] { 2 }, cast.Writes);
            DexInstruction range = Decode(0x0377, 0x0000, 0x0005);   // invoke-static/range {v5 .. v7}
            Assert.Equal(new[] { 5, 6, 7 }, range.Args);
            Assert.True(range.IsInvoke);
            Assert.Equal(1, Decode(0x0000, 0x0000).Size);   // a nop is one unit

            Assert.Throws<InvalidDataException>(() => Decode(0x003e));        // unused opcode
            Assert.Throws<InvalidDataException>(() => Decode(0x0014, 0x0000)); // const runs past the end
            Assert.Throws<InvalidDataException>(() => Decode(0x0100, 0x0005)); // switch payload runs past the end
        }

        [Fact]
        public void TheAssemblerWritesWhatTheDecoderReads()
        {
            DexFile dex = Parse(Fixture());
            foreach (string text in new[]
            {
                "nop", "return-void", "const/4 v7, #-8", "const/4 v1, #7", "const/high16 v6, #0x04000000", "const v3, #0x04000000",
                "const v200, #0xffffffff", "invoke-direct {v2}, " + EmptyFilter, "invoke-direct {v2, v0}, " + FilterWithAction,
                "invoke-static {v4, v0, v1, v2}, " + GetBroadcast, "invoke-static {}, " + Make,
            })
            {
                ushort[] units = DexInstruction.Assemble(text, dex);
                DexInstruction i = DexInstruction.Decode(units, 0);
                Assert.Equal(units.Length, i.Size);
                Assert.Equal(text, i.Render(dex));
            }
            Assert.Throws<ArgumentException>(() => DexInstruction.Assemble("const/high16 v6, #0x04000001", dex));
            Assert.Throws<ArgumentException>(() => DexInstruction.Assemble("const/4 v16, #0", dex));
            Assert.Throws<ArgumentException>(() => DexInstruction.Assemble("goto @0004", dex));
            Assert.Throws<ArgumentException>(() => DexInstruction.Assemble("invoke-static {v16}, " + Use, dex));
            Assert.Contains("has no method id", Assert.Throws<InvalidDataException>(() => DexInstruction.Assemble("invoke-direct {v0}, Lcom/example/Nope;-><init>()V", dex)).Message);
        }

        [Fact]
        public void FlowFollowsBranchesSwitchesAndHandlers()
        {
            DexFile dex = Parse(Fixture());
            DexFlow FlowOf(string name, string descriptor)
            {
                DexFile.CodeItem code = dex.GetCode(dex.FindMethod(Cls, name, descriptor));
                return new DexFlow(code, DexInstruction.DecodeAll(code.Insns));
            }

            DexFlow flags = FlowOf("flags", "(I)I");
            Assert.True(flags.IsLiveBefore(0, 1, out DexInstruction reader));   // through the branch to the return
            Assert.Equal(4, reader.Pc);
            Assert.False(flags.IsLiveBefore(0, 0, out _));                      // written first
            List<DexInstruction> writes = flags.ReachingWrites(0, 4, out bool fromEntry);
            Assert.Equal(new[] { 0, 3 }, writes.Select(w => w.Pc));
            Assert.False(fromEntry);
            flags.ReachingWrites(1, 1, out fromEntry);
            Assert.True(fromEntry);                                             // a parameter

            DexFlow guarded = FlowOf("guarded", "()V");
            Assert.Equal(new[] { 5 }, guarded.HandlersAt(1));
            Assert.True(guarded.IsLiveBefore(0, 1, out reader));                // only the handler reads v0
            Assert.Equal(5, reader.Pc);
            Assert.Equal(new[] { 0, 1, 4, 5, 8 }, guarded.Reachable().OrderBy(p => p));

            DexFlow switchy = FlowOf("switchy", "(I)V");
            Assert.Equal(new[] { 0, 3, 4 }, switchy.Reachable().OrderBy(p => p));
        }

        // ------------------------------------------------------------------ editing

        [Fact]
        public void AnEditChangesOnlyItsCodeUnitsAndTheHeader()
        {
            byte[] original = Fixture();
            DexFile dex = Parse(original);
            var edits = new[]
            {
                Edit(dex, "schedule", "(Landroid/content/Context;)V", Site(6, "const/high16 v2, #0x08000000", "const/high16 v2, #0x0c000000")),
                Edit(dex, "filter", "()V", Site(4, "invoke-direct {v0, v1}, " + FilterWithAction, "invoke-direct {v0}, " + EmptyFilter)),
                Edit(dex, "track", "(Landroid/content/Context;)V", new DexSiteEdit(0, new[] { "invoke-static {v1}, " + Ga + "->get(Landroid/content/Context;)" + Ga },
                                                                                   new[] { "return-void", "nop", "nop" })),
            };
            var notes = new List<string>();
            byte[] patched = DexEditor.Apply(dex, edits, notes);
            foreach (string n in notes) output.WriteLine(n);
            Assert.Equal(3, notes.Count);
            Assert.Equal("classes.dex: com.example.Alarm.schedule(Landroid/content/Context;)V @0006: const/high16 v2, #0x08000000 -> const/high16 v2, #0x0c000000 (T: test)", notes[0]);

            Assert.Equal(original.Length, patched.Length);
            var changed = Enumerable.Range(0, original.Length).Where(k => original[k] != patched[k]).ToList();
            var allowed = new HashSet<int>(Enumerable.Range(8, 24));
            DexFile.CodeItem Code(string name, string descriptor) => dex.GetCode(dex.FindMethod(Cls, name, descriptor));
            allowed.UnionWith(Enumerable.Range(Code("schedule", "(Landroid/content/Context;)V").FileOffsetOf(6), 4));
            allowed.UnionWith(Enumerable.Range(Code("filter", "()V").FileOffsetOf(4), 6));
            allowed.UnionWith(Enumerable.Range(Code("track", "(Landroid/content/Context;)V").FileOffsetOf(0), 6));
            Assert.DoesNotContain(changed, k => !allowed.Contains(k));
            Assert.Contains(changed, k => k >= 32);

            DexFile result = Parse(patched);
            Assert.True(result.SignatureMatches);
            Assert.Equal("@0006 const/high16 v2, #0x0c000000", Render(result, "schedule", "(Landroid/content/Context;)V")[3]);
            Assert.Equal("@0004 invoke-direct {v0}, " + EmptyFilter, Render(result, "filter", "()V")[2]);
            Assert.Equal(new[] { "@0000 return-void", "@0001 nop", "@0002 nop", "@0003 move-result-object v0" },
                         Render(result, "track", "(Landroid/content/Context;)V").Take(4));

            // The same edits again on the result: refused, the fingerprints no longer match.
            Assert.Contains("fingerprint", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(result, edits, null)).Message);
        }

        [Fact]
        public void ANewRegisterThatOnlyTheEditsWriteIsAccepted()
        {
            // Like P8: the first site starts writing v0 (here: another value), the second starts reading it.
            DexFile dex = Parse(Fixture());
            var edit = Edit(dex, "param", "(I)V", Site(0, "const/4 v0, #0", "const/4 v0, #7"), Site(1, "invoke-static {v1}, " + Use, "invoke-static {v0}, " + Use));
            DexFile result = Parse(DexEditor.Apply(dex, new[] { edit }, null));
            Assert.Equal(new[] { "@0000 const/4 v0, #7", "@0001 invoke-static {v0}, " + Use, "@0004 return-void" }, Render(result, "param", "(I)V"));
        }

        public static IEnumerable<object[]> Refusals()
        {
            // (method, descriptor, pc, before, after, expected message part)
            yield return new object[] { "schedule", "(Landroid/content/Context;)V", 6, "const/high16 v2, #0x0c000000", "const/high16 v2, #0x0c000000", "is 'const/high16 v2, #0x08000000', expected" };
            yield return new object[] { "schedule", "(Landroid/content/Context;)V", 7, "const/high16 v2, #0x08000000", "const/high16 v2, #0x0c000000", "is not the start of an instruction" };
            yield return new object[] { "schedule", "(Landroid/content/Context;)V", 6, "const/high16 v2, #0x08000000", "const v2, #0x0c000000", "is 3 code units, the original 2" };
            yield return new object[] { "flags", "(I)I", 0, "const/4 v0, #1", "return-void", "return-void in a method that returns I" };
            yield return new object[] { "flags", "(I)I", 3, "const/4 v0, #2", "nop", "v0 is no longer written at @0003 but return at @0004 reads it" };
            yield return new object[] { "param", "(I)V", 0, "const/4 v0, #0", "const/4 v1, #0", "writes v1, whose value invoke-static at @0001 still reads" };
            yield return new object[] { "param", "(I)V", 1, "invoke-static {v1}, " + Use, "invoke-static {v0}, " + Use, "now reads v0, which can hold a value the edits did not write (written at @0000)" };
            yield return new object[] { "result", "()V", 0, "invoke-static {}, " + Make, "nop", "is 1 code units, the original 3" };
            yield return new object[] { "guarded", "()V", 0, "const/4 v0, #1", "nop", "v0 is no longer written at @0000 but invoke-static at @0005 reads it" };
        }

        [Theory]
        [MemberData(nameof(Refusals))]
        public void AnEditThatDoesNotFitIsRefused(string method, string descriptor, int pc, string before, string after, string message)
        {
            DexFile dex = Parse(Fixture());
            var ex = Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { Edit(dex, method, descriptor, Site(pc, before, after)) }, null));
            output.WriteLine(ex.Message);
            Assert.Contains(message, ex.Message);
            Assert.EndsWith("nothing was changed", ex.Message);
        }

        [Fact]
        public void AnEditThatBreaksTheMethodsShapeIsRefused()
        {
            DexFile dex = Parse(Fixture());

            // A branch target (0003) would fall inside the replacement.
            var split = Edit(dex, "branchy", "(I)V", new DexSiteEdit(2, new[] { "const/4 v0, #1", "const/4 v0, #2" }, new[] { "const/high16 v0, #0x00010000" }));
            Assert.Contains("not well formed", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { split }, null)).Message);

            // A reachable move-result would no longer follow its invoke.
            var orphan = Edit(dex, "result", "()V", new DexSiteEdit(0, new[] { "invoke-static {}, " + Make }, new[] { "nop", "nop", "nop" }));
            Assert.Contains("move-result at @0003 would no longer follow an invoke", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { orphan }, null)).Message);

            // Wrong fingerprint, overlapping sites, a method that is not there, a class that is not there.
            DexMethodEdit good = Edit(dex, "flags", "(I)I", Site(0, "const/4 v0, #1", "const/4 v0, #3"));
            var wrong = new DexMethodEdit("T", Cls, "flags", "(I)I", new string('0', 64), "test", good.Sites.ToArray());
            Assert.Contains("is not the verified", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { wrong }, null)).Message);
            var overlap = Edit(dex, "flags", "(I)I", Site(0, "const/4 v0, #1", "const/4 v0, #3"), Site(0, "const/4 v0, #1", "const/4 v0, #3"));
            Assert.Contains("overlap", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { overlap }, null)).Message);
            Assert.Contains("more than one entry", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { good, good }, null)).Message);
            var missing = new DexMethodEdit("T", Cls, "absent", "()V", good.Fingerprint, "test", good.Sites.ToArray());
            Assert.Contains("0 methods absent()V", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { missing }, null)).Message);
            var noClass = new DexMethodEdit("T", "Lcom/example/Nope;", "flags", "(I)I", good.Fingerprint, "test", good.Sites.ToArray());
            Assert.Contains("is not defined in classes.dex", Assert.Throws<InvalidDataException>(() => DexEditor.Apply(dex, new[] { noClass }, null)).Message);

            // After a refusal the next good edit still works (nothing was kept).
            Assert.NotNull(DexEditor.Apply(dex, new[] { good }, null));
        }

        // ------------------------------------------------------------------ APK level

        private static ZipEntry Entry(string name, byte[] data)
        {
            return ZipEntry.CreateDeflated(name, data, null);
        }

        [Fact]
        public void TheEditsGoToTheOneDexThatDefinesTheClass()
        {
            byte[] main = new DexWriter().Method("Lcom/example/Main;", "run", "()V", 0, 0, 0, d => DexWriter.ReturnVoid()).ToArray();
            byte[] second = Fixture();
            DexFile secondDex = Parse(second, "classes2.dex");
            DexMethodEdit edit = Edit(secondDex, "flags", "(I)I", Site(0, "const/4 v0, #1", "const/4 v0, #3"));
            var entries = new List<ZipEntry> { Entry("AndroidManifest.xml", new byte[] { 1 }), Entry("classes.dex", main), Entry("classes2.dex", second),
                                               Entry("assets/classes3.dex", new byte[] { 0 }) };
            var notes = new List<string>();
            IReadOnlyDictionary<string, byte[]> result = DexTargetSdkEdits.Apply(entries, 35, notes, new[] { edit });
            Assert.Equal(new[] { "classes2.dex" }, result.Keys);
            Assert.StartsWith("classes2.dex: com.example.Alarm.flags(I)I @0000:", Assert.Single(notes));
            Assert.Equal("@0000 const/4 v0, #3", Render(Parse(result["classes2.dex"], "classes2.dex"), "flags", "(I)I")[0]);

            // The replacement entry keeps the original's name and compression method.
            ZipEntry deflated = DexTargetSdkEdits.ReplacementEntry(entries[2], result["classes2.dex"]);
            Assert.Equal(("classes2.dex", ZipEntry.MethodDeflated), (deflated.Name, deflated.Method));
            Assert.Equal(result["classes2.dex"], deflated.GetData());
            ZipEntry stored = DexTargetSdkEdits.ReplacementEntry(ZipEntry.CreateStored("classes2.dex", second, null), result["classes2.dex"]);
            Assert.Equal(ZipEntry.MethodStored, stored.Method);
            Assert.Equal(result["classes2.dex"], stored.GetData());

            // Below target 31: nothing is read (not even a broken dex) and nothing changes.
            var broken = new List<ZipEntry> { Entry("classes.dex", new byte[] { 1, 2, 3 }) };
            Assert.Empty(DexTargetSdkEdits.Apply(broken, 30, notes, new[] { edit }));
            Assert.Contains("not a dex file", Assert.Throws<InvalidDataException>(() => DexTargetSdkEdits.Apply(broken, 31, null, new[] { edit })).Message);

            // The class in two dex files, or in none: refused.
            var twice = new List<ZipEntry> { Entry("classes.dex", second), Entry("classes2.dex", second) };
            Assert.Contains("is defined in classes.dex and classes2.dex", Assert.Throws<InvalidDataException>(() => DexTargetSdkEdits.Apply(twice, 35, null, new[] { edit })).Message);
            var none = new List<ZipEntry> { Entry("classes.dex", main) };
            Assert.Contains("is defined in no dex file", Assert.Throws<InvalidDataException>(() => DexTargetSdkEdits.Apply(none, 35, null, new[] { edit })).Message);
            Assert.Contains("no classes.dex", Assert.Throws<InvalidDataException>(() => DexTargetSdkEdits.Apply(new List<ZipEntry>(), 35, null, new[] { edit })).Message);
        }

        [Fact]
        public void DexEntriesAreTheRootClassesFiles()
        {
            Assert.True(DexTargetSdkEdits.IsDexEntry("classes.dex"));
            Assert.True(DexTargetSdkEdits.IsDexEntry("classes2.dex"));
            Assert.True(DexTargetSdkEdits.IsDexEntry("classes12.dex"));
            Assert.False(DexTargetSdkEdits.IsDexEntry("assets/classes.dex"));
            Assert.False(DexTargetSdkEdits.IsDexEntry("classes.dex.bak"));
            Assert.False(DexTargetSdkEdits.IsDexEntry("classesX.dex"));
        }

        [Fact]
        public void TheEditTableIsWellFormed()
        {
            Assert.Equal(new[] { "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "P10", "P11", "P12", "P13", "P14" }, DexTargetSdkEdits.Edits.Select(e => e.Id));
            Assert.Equal(DexTargetSdkEdits.Edits.Count, DexTargetSdkEdits.Edits.Select(e => e.Reference).Distinct().Count());
            Assert.All(DexTargetSdkEdits.Edits, e => Assert.Matches("^[0-9a-f]{64}$", e.Fingerprint));
            Assert.All(DexTargetSdkEdits.Edits.SelectMany(e => e.Sites), s => Assert.True(s.Before.Length > 0 && s.After.Length > 0));
            Assert.Equal(31, DexTargetSdkEdits.MinTargetSdk);
        }

        // ------------------------------------------------------------------ Android's own tools

        [Fact]
        public void DexdumpAcceptsTheSyntheticFilesBeforeAndAfterAnEdit()
        {
            string tools = TestEnvironment.BuildToolsDir;
            string dexdump = tools == null ? null : Path.Combine(tools, "dexdump");
            if (dexdump == null || !File.Exists(dexdump))
            {
                output.WriteLine("dexdump not found (MCVS_BUILD_TOOLS): skipped");
                return;
            }
            DexFile dex = Parse(Fixture());
            byte[] patched = DexEditor.Apply(dex, new[]
            {
                Edit(dex, "schedule", "(Landroid/content/Context;)V", Site(6, "const/high16 v2, #0x08000000", "const/high16 v2, #0x0c000000")),
            }, null);
            string dir = TestEnvironment.NewTempDir("dex-synthetic");
            try
            {
                foreach (var (name, bytes) in new[] { ("before.dex", dex.Bytes), ("after.dex", patched) })
                {
                    string file = Path.Combine(dir, name);
                    File.WriteAllBytes(file, bytes);
                    int code = BuildTools.Run(dexdump, new[] { "-c", file }, null, out string text);
                    Assert.True(code == 0, text);
                    Assert.Contains("Checksum verified", text);
                    code = BuildTools.Run(dexdump, new[] { "-d", file }, null, out text);
                    Assert.True(code == 0, text);
                    output.WriteLine(text);
                    Assert.Contains("Class descriptor  : 'Lcom/example/Alarm;'", text);
                    Assert.Contains(name == "before.dex" ? "const/high16 v2, #int 134217728" : "const/high16 v2, #int 201326592", text);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}
