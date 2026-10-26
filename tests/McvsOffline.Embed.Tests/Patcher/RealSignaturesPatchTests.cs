using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using FieldAttributes = System.Reflection.FieldAttributes;
using MethodAttributes = System.Reflection.MethodAttributes;
using TypeAttributes = System.Reflection.TypeAttributes;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// The device-test-6 'signatures' patch applied to the player's REAL Game.dll (MCVS_REFS) and executed on net8 next to the
    /// unpatched Game.dll: <c>PlayerProfileComponent.GetSignatureAssetsList</c> (G1) and <c>GameOfflineStore.GetKillSignItems</c>
    /// (G2) are run in two load contexts on identical fake state (a meta Database whose table cache holds a
    /// <c>SignatureItemDataCollection</c>, a profile with owned/new ids, an <c>OfflineStoreData</c> built from a CRM-like
    /// "prices" document) and must give exactly the same results, row flags, exceptions and dictionaries; the patched pair is
    /// also timed at the unlock-all scale of device test 6 (1,518 avatars, 1,512 owned and new).
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1.
    /// </summary>
    [Collection("GameHooks")]
    public class RealSignaturesPatchTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Rows of the real KillsignatureAvatars table of 1.17.32 (ids 1..1518) and avatars owned after unlock-all.</summary>
        private const int AllAvatars = 1518, UnlockAllOwned = 1512;

        private readonly ITestOutputHelper output;

        public RealSignaturesPatchTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Game.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Game.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        private static readonly object PatchGate = new object();
        private static byte[] server;
        private static Dictionary<string, byte[]> patched;
        private static List<string> patchNotes;

        /// <summary>The stand-in server and Game.dll patched with 'signatures' alone (patched once for the class).</summary>
        private static Dictionary<string, byte[]> Patched(string refs, out byte[] serverImage)
        {
            lock (PatchGate)
            {
                if (patched == null)
                {
                    server = FixtureCompiler.Compile("McvsOffline.Server", RealGamePatchTests.HookServerSource);
                    var notes = new List<string>();
                    patched = RealGamePatchTests.PatchReal(refs, server, notes, new SignaturesPatch());
                    patchNotes = notes;
                }
                serverImage = server;
                return patched;
            }
        }

        // ------------------------------------------------------------------ IL

        [Fact]
        public void TheShippedMethodsHaveTheVerifiedFingerprints()
        {
            string refs = Refs();
            if (refs == null) return;
            using (var module = ModuleDefinition.ReadModule(Path.Combine(refs, "Game.dll")))
            {
                TypeDefinition profile = module.GetType(SignaturesPatch.ProfileType);
                MethodDefinition assets = profile.Methods.Single(m => m.Name == "GetSignatureAssetsList");
                Assert.Equal(SignaturesPatch.AssetsListFingerprint, IlPatch.Fingerprint(assets));
                TypeDefinition store = module.GetType(SignaturesPatch.StoreType);
                MethodDefinition items = store.Methods.Single(m => m.Name == "GetKillSignItems");
                Assert.Equal(SignaturesPatch.KillSignItemsFingerprint, IlPatch.Fingerprint(items));
            }
        }

        [Fact]
        public void ThePatchedIlHasTheExpectedShapeJitsAndIsNotPatchedTwice()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            foreach (string n in patchNotes) output.WriteLine(n);
            Assert.Equal(2, patchNotes.Count);

            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(refs);
                foreach (string d in resolver.GetSearchDirectories().Where(d => d != refs).ToList()) resolver.RemoveSearchDirectory(d);
                using (var module = ModuleDefinition.ReadModule(new MemoryStream(images["Game.dll"]), new ReaderParameters { AssemblyResolver = resolver }))
                {
                    // G1: no List<string>.Contains left, two ICollection<string>.Contains, two HashSet<string>(IEnumerable<string>)
                    // guarded by Count > 16; the HashSet comes from the GAME's System.Core (Mono BCL), not from net8.
                    MethodDefinition assets = module.GetType(SignaturesPatch.ProfileType).Methods.Single(m => m.Name == "GetSignatureAssetsList");
                    var code = assets.Body.Instructions;
                    Assert.DoesNotContain(code, i => i.Operand is MethodReference r && r.Name == "Contains" && r.DeclaringType.FullName == "System.Collections.Generic.List`1<System.String>");
                    var contains = code.Where(i => i.Operand is MethodReference r && r.Name == "Contains").ToList();
                    Assert.Equal(2, contains.Count);
                    Assert.All(contains, i =>
                    {
                        Assert.Equal(OpCodes.Callvirt, i.OpCode);
                        Assert.Equal("System.Collections.Generic.ICollection`1<System.String>", ((MethodReference)i.Operand).DeclaringType.FullName);
                    });
                    var ctors = code.Where(i => i.OpCode == OpCodes.Newobj && ((MethodReference)i.Operand).DeclaringType.Name == "HashSet`1").ToList();
                    Assert.Equal(2, ctors.Count);
                    foreach (Instruction ctor in ctors)
                    {
                        var reference = (MethodReference)ctor.Operand;
                        Assert.Equal("System.Collections.Generic.HashSet`1<System.String>", reference.DeclaringType.FullName);
                        MethodDefinition resolved = reference.Resolve();
                        Assert.NotNull(resolved);
                        Assert.Equal("System.Core", resolved.Module.Assembly.Name.Name);
                        Assert.StartsWith(Path.GetFullPath(refs), Path.GetFullPath(resolved.Module.FileName));
                        Assert.Equal("System.Collections.Generic.IEnumerable`1<T>", Assert.Single(resolved.Parameters).ParameterType.FullName);
                    }
                    Assert.Equal(2, code.Count(i => i.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(i.Operand) == SignaturesPatch.SetThreshold));
                    Assert.Equal(2, code.Count(i => i.OpCode == OpCodes.Ble_S || i.OpCode == OpCodes.Ble));
                    Assert.Equal(5, assets.Body.Variables.Count);
                    Assert.All(assets.Body.Variables.Skip(3), v => Assert.Equal("System.Collections.Generic.ICollection`1<System.String>", v.VariableType.FullName));
                    Assert.True(assets.Body.InitLocals);
                    Assert.Single(assets.Body.ExceptionHandlers);

                    // G2: still one call of each list getter, each behind a null check of its own new cache local.
                    MethodDefinition items = module.GetType(SignaturesPatch.StoreType).Methods.Single(m => m.Name == "GetKillSignItems");
                    foreach (string getter in new[] { "GetSignatureAvatarsList", "GetSignatureBackgroundsList" })
                    {
                        Instruction call = Assert.Single(items.Body.Instructions, i => i.Operand is MethodReference r && r.Name == getter);
                        Assert.Equal(OpCodes.Call, call.OpCode);
                        // ldloc cache; brtrue HAVE; call getter; stloc cache; br LOAD; HAVE: pop; LOAD: ldloc cache
                        Instruction load = call.Previous.Previous;
                        Assert.True(load.OpCode.Code == Code.Ldloc_S || load.OpCode.Code == Code.Ldloc, load.ToString());
                        var cache = (VariableDefinition)load.Operand;
                        Assert.Equal("System.Collections.Generic.List`1<Game.PlayerProfileComponent/SignatureItemData>", cache.VariableType.FullName);
                        Assert.True(call.Previous.OpCode.Code == Code.Brtrue_S || call.Previous.OpCode.Code == Code.Brtrue);
                        Instruction have = (Instruction)call.Previous.Operand;
                        Assert.Equal(OpCodes.Pop, have.OpCode);
                        Assert.True(call.Next.OpCode.Code == Code.Stloc_S || call.Next.OpCode.Code == Code.Stloc);
                        Assert.Same(cache, call.Next.Operand);
                        Assert.Same(have.Next, call.Next.Next.Operand);
                        Assert.Same(cache, have.Next.Operand);
                        // the profile?. null check still jumps to the first instruction of the site
                        Assert.Contains(items.Body.Instructions, i => i.Operand == load);
                        // the cache is never written anywhere else
                        Assert.Single(items.Body.Instructions, i => i.Operand == cache && (i.OpCode.Code == Code.Stloc_S || i.OpCode.Code == Code.Stloc));
                    }
                    Assert.True(items.Body.InitLocals);
                }
            }

            using (var game = new GameContext(refs, images, serverImage))
            {
                Assembly g = game.Get("Game");
                foreach (MethodBase m in new MethodBase[]
                {
                    g.GetType(SignaturesPatch.ProfileType, true).GetMethod("GetSignatureAssetsList", Any),
                    g.GetType(SignaturesPatch.StoreType, true).GetMethod("GetKillSignItems", Any),
                })
                {
                    RuntimeHelpers.PrepareMethod(m.MethodHandle);   // InvalidProgramException on bad IL
                    output.WriteLine("JIT ok: " + m.DeclaringType + "." + m.Name);
                }
            }

            // Patched once: the fingerprints no longer match.
            string dir = TestEnvironment.NewTempDir("signatures-repatch");
            foreach (string f in Directory.GetFiles(refs, "*.dll")) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            File.WriteAllBytes(Path.Combine(dir, "Game.dll"), images["Game.dll"]);
            Assert.Contains("IL fingerprint", Assert.Throws<PatchException>(() => RealGamePatchTests.PatchReal(dir, serverImage, null, new SignaturesPatch())).Message);
        }

        // ------------------------------------------------------------------ G1 differential

        private sealed class AssetsCase
        {
            public string Name;
            public bool Table = true;          // false: the table is not in the database at all
            public List<Row> Rows = new List<Row>();
            public List<string> Owned;
            public List<string> New;
        }

        [Fact]
        public void GetSignatureAssetsListMatchesTheOriginalExactly()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            List<AssetsCase> cases = AssetsCases();
            using (var original = new Scene(refs, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new Scene(refs, images, serverImage))
            {
                Assert.False(original.IsPatched);
                Assert.True(fixedScene.IsPatched);
                foreach (AssetsCase c in cases)
                {
                    string before = original.RunAssets(c, out _);
                    string after = fixedScene.RunAssets(c, out _);
                    output.WriteLine(c.Name + ": " + Shorten(before));
                    Assert.True(before == after, c.Name + "\noriginal: " + before + "\npatched:  " + after);
                    if (c.Table && c.Owned != null && c.New != null) Assert.Equal(Reference(c), before);
                }

                // A profile without a valid FedId: PerUserDatabase.GetDatabase throws before any row is read; the patched
                // method builds its sets first, which changes nothing visible (same exception, lists untouched).
                foreach (Scene s in new[] { original, fixedScene }) s.SetCredential(false);
                foreach (int n in new[] { 3, 30 })
                {
                    var invalid = new AssetsCase { Name = "invalid credential (" + n + ")", Rows = Enumerable.Range(1, 50).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "img" + i)).ToList(), Owned = Ids(1, n).ToList(), New = Ids(1, n).ToList() };
                    string before = original.RunAssets(invalid, out _), after = fixedScene.RunAssets(invalid, out _);
                    output.WriteLine(invalid.Name + ": " + Shorten(before));
                    Assert.StartsWith("throws System.InvalidOperationException rows [\"1\":LN ", before);
                    Assert.True(before == after, invalid.Name + "\noriginal: " + before + "\npatched:  " + after);
                }
                foreach (Scene s in new[] { original, fixedScene }) s.SetCredential(true);

                // The public wrappers (avatars/backgrounds/sounds read the profile's lists and table names).
                foreach (Scene s in new[] { original, fixedScene })
                {
                    s.SetTable("KillsignatureAvatars", UnlockAllRows("a"));
                    s.SetTable("KillsignatureBackgrounds", Enumerable.Range(1, 35).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "c" + i)).ToList());
                    s.SetTable("KillsignatureSounds", Enumerable.Range(1, 5).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "sfx" + i)).ToList());
                    s.SetProfileLists(Ids(1, 40).Concat(new[] { "1600", null }).ToList(), Ids(30, 50).ToList(),
                                      Ids(1, 20).ToList(), Ids(5, 6).ToList(), Ids(1, 2).ToList(), new List<string>());
                }
                foreach (string getter in new[] { "GetSignatureAvatarsList", "GetSignatureBackgroundsList", "GetSignatureSoundsList" })
                {
                    string before = original.RunGetter(getter), after = fixedScene.RunGetter(getter);
                    output.WriteLine(getter + ": " + Shorten(before));
                    Assert.True(before == after, getter + "\noriginal: " + before + "\npatched:  " + after);
                }
            }
        }

        private static List<AssetsCase> AssetsCases()
        {
            List<Row> small = new[] { "1", "2", "3", "4", "5" }.Select(i => new Row(i, "img" + i)).ToList();
            List<Row> fifty = Enumerable.Range(1, 50).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "img" + i)).ToList();
            var cases = new List<AssetsCase>
            {
                new AssetsCase { Name = "null lists, empty table" },
                new AssetsCase { Name = "null lists, rows", Rows = small },
                new AssetsCase { Name = "owned null, new 3", Rows = small, New = Ids(1, 3).ToList() },
                new AssetsCase { Name = "owned null, new 30", Rows = small, New = Ids(1, 30).ToList() },
                new AssetsCase { Name = "owned 3, new null", Rows = small, Owned = Ids(1, 3).ToList() },
                new AssetsCase { Name = "owned 30, new null", Rows = small, Owned = Ids(1, 30).ToList() },
                new AssetsCase { Name = "owned 30, new null, empty table", Owned = Ids(1, 30).ToList() },
                new AssetsCase { Name = "empty lists, empty table", Owned = new List<string>(), New = new List<string>() },
                new AssetsCase { Name = "empty lists, rows", Rows = small, Owned = new List<string>(), New = new List<string>() },
                new AssetsCase { Name = "no such table", Table = false, Owned = Ids(1, 3).ToList(), New = new List<string>() },
                new AssetsCase { Name = "no such table, 30 ids", Table = false, Owned = Ids(1, 30).ToList(), New = Ids(1, 30).ToList() },
                new AssetsCase { Name = "no such table, null lists", Table = false },
            };
            foreach (int n in new[] { 1, 15, 16, 17, 18, 49, 50 })
            {
                cases.Add(new AssetsCase { Name = "owned " + n + " (first), new " + n + " (last)", Rows = fifty, Owned = Ids(1, n).ToList(), New = Ids(51 - n, 50).ToList() });
                cases.Add(new AssetsCase { Name = "owned " + n + " (some missing), new empty", Rows = fifty, Owned = Ids(40, 39 + n).ToList(), New = new List<string>() });
            }
            // Repeated ids around the threshold: 16 entries (kept as a list) and 17 (copied into a set), and 40 copies of one id.
            cases.Add(new AssetsCase { Name = "16 with duplicates", Rows = fifty, Owned = Ids(1, 8).Concat(Ids(1, 8)).ToList(), New = Ids(8, 15).Concat(Ids(8, 15)).ToList() });
            cases.Add(new AssetsCase { Name = "17 with duplicates", Rows = fifty, Owned = Ids(1, 8).Concat(Ids(1, 9)).ToList(), New = Ids(8, 16).Concat(Ids(8, 15)).ToList() });
            cases.Add(new AssetsCase { Name = "40 times the same id", Rows = fifty, Owned = Enumerable.Repeat("7", 40).ToList(), New = Enumerable.Repeat("8", 40).ToList() });
            cases.Add(new AssetsCase { Name = "ids not in the table (16/17/100)", Rows = fifty, Owned = Ids(100, 115).ToList(), New = Ids(200, 216).ToList() });
            cases.Add(new AssetsCase { Name = "ids not in the table (100)", Rows = fifty, Owned = Ids(1000, 1099).ToList(), New = Ids(1000, 1099).ToList() });
            // null ids in the lists, rows whose Id is null (the table is keyed by another string), empty ids.
            List<Row> odd = fifty.Take(20).Concat(new[] { new Row("nullrow", null, "imgnull"), new Row("", "", "imgempty"), new Row("x", "X", "imgX") }).ToList();
            foreach (int n in new[] { 3, 16, 17, 30 })
            {
                cases.Add(new AssetsCase { Name = "null in lists (" + n + ")", Rows = odd, Owned = Ids(1, n - 1).Concat(new string[] { null }).ToList(), New = new string[] { null }.Concat(Ids(5, 3 + n)).ToList() });
                cases.Add(new AssetsCase { Name = "no null in lists, null row (" + n + ")", Rows = odd, Owned = Ids(1, n).ToList(), New = Ids(2, 1 + n).ToList() });
                cases.Add(new AssetsCase { Name = "empty id in lists (" + n + ")", Rows = odd, Owned = Ids(1, n - 1).Concat(new[] { "" }).ToList(), New = Ids(1, n - 1).Concat(new[] { "x" }).ToList() });
            }
            cases.Add(new AssetsCase { Name = "only nulls (20)", Rows = odd, Owned = Enumerable.Repeat((string)null, 20).ToList(), New = Enumerable.Repeat((string)null, 2).ToList() });
            // case: the table is case-insensitive, Contains is ordinal in both versions.
            List<Row> cased = new[] { new Row("a", "a", "lower"), new Row("B", "B", "upper"), new Row("avatar_c", "Avatar_C", "mixed"), new Row("i", "i", "dotted") }.ToList();
            foreach (int pad in new[] { 0, 20 })
            {
                List<string> filler = Ids(500, 499 + pad).ToList();
                cases.Add(new AssetsCase { Name = "case (" + (pad + 4) + ")", Rows = cased, Owned = new[] { "A", "b", "avatar_c", "I" }.Concat(filler).ToList(), New = new[] { "a", "B", "AVATAR_C", "\u0130" }.Concat(filler).ToList() });
            }
            // The device-test-6 profile: 1,518 avatars, 1,512 owned and new (in the order unlock-all added them).
            List<string> owned = Shuffled(Ids(1, UnlockAllOwned), 6).ToList();
            cases.Add(new AssetsCase { Name = "unlock-all", Rows = UnlockAllRows("a"), Owned = owned, New = new List<string>(owned) });
            cases.Add(new AssetsCase { Name = "unlock-all, new cleared", Rows = UnlockAllRows("a"), Owned = owned, New = new List<string>() });
            return cases;
        }

        /// <summary>What GetSignatureAssetsList is meant to do, in plain C#: the table's rows with locked = !owned.Contains(id), isNew = new.Contains(id).</summary>
        private static string Reference(AssetsCase c)
        {
            var sb = new StringBuilder("returns [");
            for (int i = 0; i < c.Rows.Count; i++)
            {
                Row r = c.Rows[i];
                sb.Append(i).Append(':').Append(Q(r.Id)).Append(':').Append(Q(r.Asset)).Append(':')
                  .Append(!c.Owned.Contains(r.Id) ? "L" : "-").Append(c.New.Contains(r.Id) ? "N" : "-").Append(' ');
            }
            sb.Append(']');
            return sb.ToString() + " rows " + RowsAfter(c.Rows.Select(r => (r.Id, !c.Owned.Contains(r.Id), c.New.Contains(r.Id))));
        }

        // ------------------------------------------------------------------ G2 differential

        private sealed class StoreCase
        {
            public string Name;
            public string Subtype;
            public bool NullProfile;
            public bool NullData;
            public bool AvatarTable = true;
            public List<Row> Avatars = new List<Row>();
            public List<Row> Backgrounds = new List<Row>();
            public List<string> OwnedAvatars = new List<string>(), NewAvatars = new List<string>();
            public List<string> OwnedBackgrounds = new List<string>(), NewBackgrounds = new List<string>();
            public List<string> OwnedSounds = new List<string> { "1" };
            public List<string> Store = new List<string>();   // item names of the CRM offline_store document
            public bool Crates;
            public string Promotion;

            public string StoreJson => StoreDocument(Store, Crates, Promotion);

            /// <summary>The kill signatures of this subtype the shop sells to the player: each one goes through GetKillSignItemObject.</summary>
            public int Unowned(List<string> owned)
            {
                return Store.Count(n => n.StartsWith("KillSignature_", StringComparison.Ordinal) && n.ToLowerInvariant().Contains(Subtype.ToLowerInvariant()) &&
                                        !owned.Contains(n.Split('_')[2]));
            }
        }

        [Fact]
        public void GetKillSignItemsMatchesTheOriginalExactly()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);

            List<Row> avatars = Enumerable.Range(1, 60).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "Menus/assets/png/profile/avatars/avatar" + i + ".png")).ToList();
            List<Row> backgrounds = Enumerable.Range(1, 35).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), (0xb82448 + i).ToString("x6", CultureInfo.InvariantCulture))).ToList();
            List<string> ownedA = new[] { "3", "1", "59", "1", "999", null, "60", "2" }.ToList();
            List<string> ownedB = Ids(1, 20).Concat(new[] { "35", "77", "1" }).ToList();
            // Owned items are in the store (the CRM leaves them out now, older documents have them) and filtered out by the
            // method itself; other kinds of items and the other subtypes are skipped by GetItemType / the name check.
            List<string> ownedStore = ownedA.Where(i => i != null).Select(i => "KillSignature_Avatar_" + i)
                .Concat(ownedB.Select(i => "KillSignature_Background_" + i))
                .Concat(new[] { "KillSignature_Sound_1", "SoftCurrency_500", "HardCurrency_10", "UnlockMerc_5" }).ToList();
            // The whole kill-signature shop: every avatar, background and sound, owned or not, at different prices.
            List<string> wholeShop = Ids(1, 60).Select(i => "KillSignature_Avatar_" + i).Concat(Ids(1, 35).Select(i => "KillSignature_Background_" + i))
                .Concat(Ids(1, 5).Select(i => "KillSignature_Sound_" + i)).Concat(new[] { "SoftCurrency_500", "KillSignature_Avatar_61" }).ToList();

            var cases = new List<StoreCase>();
            foreach (string subtype in new[] { "avatar", "background", "sound", "Avatar", "BACKGROUND" })
            {
                cases.Add(new StoreCase { Name = subtype + ": nothing owned", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": owned, duplicates, unknown and null ids", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds,
                    OwnedAvatars = ownedA, NewAvatars = Ids(1, 30).ToList(), OwnedBackgrounds = ownedB, NewBackgrounds = Ids(1, 2).ToList(),
                    Store = ownedStore, Crates = true,
                });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": all owned, empty store", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds,
                    OwnedAvatars = Ids(1, 60).ToList(), NewAvatars = Ids(1, 60).ToList(), OwnedBackgrounds = Ids(1, 35).ToList(), NewBackgrounds = Ids(1, 35).ToList(),
                });
                cases.Add(new StoreCase { Name = subtype + ": no profile", Subtype = subtype, NullProfile = true, OwnedAvatars = ownedA, Store = wholeShop });
                cases.Add(new StoreCase { Name = subtype + ": no store data", Subtype = subtype, NullData = true, OwnedAvatars = ownedA });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": no avatar table", Subtype = subtype, AvatarTable = false, Backgrounds = backgrounds,
                    OwnedAvatars = ownedA, OwnedBackgrounds = ownedB,
                });
                cases.Add(new StoreCase { Name = subtype + ": null owned lists", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds, OwnedAvatars = null, OwnedBackgrounds = null });
                cases.Add(new StoreCase { Name = subtype + ": null new lists", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds, OwnedAvatars = ownedA, OwnedBackgrounds = ownedB, NewAvatars = null, NewBackgrounds = null });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": two unowned items in the store", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds,
                    OwnedAvatars = ownedA, OwnedBackgrounds = ownedB,
                    Store = new List<string> { "KillSignature_Avatar_40", "KillSignature_Background_30", "KillSignature_Sound_4" },
                });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": the whole shop", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds,
                    OwnedAvatars = ownedA, NewAvatars = Ids(1, 30).ToList(), OwnedBackgrounds = ownedB, NewBackgrounds = Ids(1, 2).ToList(),
                    OwnedSounds = new List<string> { "2", "5" }, Store = wholeShop,
                });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": the whole shop, nothing owned", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds, Store = wholeShop,
                });
                cases.Add(new StoreCase
                {
                    Name = subtype + ": the whole shop, a promotion", Subtype = subtype, Avatars = avatars, Backgrounds = backgrounds,
                    OwnedAvatars = ownedA, OwnedBackgrounds = ownedB, Store = wholeShop, Promotion = "Kill signatures -50%",
                });
            }
            // Unlock-all: the per-row work of the original is ~2.3 million compares x 1,512 calls, so a quarter of it here; the
            // full scale is compared in UnlockAllIsFastAndUnchanged.
            List<string> quarter = Shuffled(Ids(1, UnlockAllOwned / 4), 7).ToList();
            cases.Add(new StoreCase
            {
                Name = "avatar: unlock-all rows, 378 owned", Subtype = "avatar", Avatars = UnlockAllRows("Menus/assets/png/profile/avatars/avatar"), Backgrounds = backgrounds,
                OwnedAvatars = quarter, NewAvatars = new List<string>(quarter), OwnedBackgrounds = Ids(1, 35).ToList(), NewBackgrounds = Ids(1, 35).ToList(),
                Store = Ids(370, 400).Select(i => "KillSignature_Avatar_" + i).ToList(),
            });

            using (var original = new Scene(refs, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new Scene(refs, images, serverImage))
            {
                foreach (StoreCase c in cases)
                {
                    string before = original.RunKillSignItems(c, out int originalCopies);
                    string after = fixedScene.RunKillSignItems(c, out int patchedCopies);
                    output.WriteLine(c.Name + ": " + Shorten(before) + " (list builds: original " + originalCopies + ", patched " + patchedCopies + ")");
                    Assert.True(before == after, c.Name + "\noriginal: " + before + "\npatched:  " + after);

                    // G2: one list per call of GetKillSignItems instead of one per owned item (none when nothing is owned); each
                    // item the shop sells still builds its own in GetKillSignItemObject (not patched).
                    string kind = c.Subtype.ToLowerInvariant();
                    if (!c.NullProfile && !c.NullData && (kind == "avatar" || kind == "background") && before.StartsWith("returns ", StringComparison.Ordinal))
                    {
                        List<string> owned = kind == "avatar" ? c.OwnedAvatars : c.OwnedBackgrounds;
                        Assert.Equal(owned.Count + c.Unowned(owned), originalCopies);
                        Assert.Equal((owned.Count == 0 ? 0 : 1) + c.Unowned(owned), patchedCopies);
                    }
                }
                // The thrown cases were exercised, the regular cases did not throw, the shop path ran in full.
                Assert.StartsWith("throws System.NullReferenceException", original.RunKillSignItems(cases.First(c => c.Name == "avatar: null owned lists"), out _));
                Assert.StartsWith("throws System.ArgumentNullException", original.RunKillSignItems(cases.First(c => c.Name == "avatar: no avatar table"), out _));
                Assert.StartsWith("returns {\"slots\":[{\"img\":\"Menus/assets/png/profile/avatars/avatar3.png\",\"id\":\"KillSignature_Avatar_3\",\"locked\":false},",
                                  fixedScene.RunKillSignItems(cases.First(c => c.Name == "avatar: owned, duplicates, unknown and null ids"), out _));
                Assert.All(cases.Where(c => c.Name.Contains("the whole shop")), c => Assert.StartsWith("returns {\"slots\":[", fixedScene.RunKillSignItems(c, out _)));
                string shop = fixedScene.RunKillSignItems(cases.First(c => c.Name == "avatar: the whole shop"), out _);
                Assert.Contains("{\"id\":\"KillSignature_Avatar_4\",\"name\":\"\",\"count\":Int32(1),\"cost\":{\"id\":\"VersusCoins\",", shop);
                Assert.Contains("\"img\":\"Menus/assets/png/profile/avatars/avatar4.png\",\"locked\":true}", shop);
            }
        }

        // ------------------------------------------------------------------ unlock-all timing

        [Fact]
        public void UnlockAllIsFastAndUnchanged()
        {
            string refs = Refs();
            if (refs == null) return;
            Dictionary<string, byte[]> images = Patched(refs, out byte[] serverImage);
            List<Row> rows = UnlockAllRows("Menus/assets/png/profile/avatars/avatar");
            List<Row> backgrounds = Enumerable.Range(1, 35).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "c" + i)).ToList();
            List<string> owned = Shuffled(Ids(1, UnlockAllOwned), 6).ToList();
            // Device test 6: 1,518 avatars, 1,512 owned and new; the store document of the CRM before it left owned items
            // out (all 1,518 avatars: GetKillSignItems drops the owned ones itself, the other 6 are sold).
            var c = new StoreCase
            {
                Name = "unlock-all", Subtype = "avatar", Avatars = rows, Backgrounds = backgrounds,
                OwnedAvatars = owned, NewAvatars = new List<string>(owned), OwnedBackgrounds = Ids(1, 35).ToList(), NewBackgrounds = Ids(1, 35).ToList(),
                Store = Ids(1, AllAvatars).Select(i => "KillSignature_Avatar_" + i).ToList(),
            };

            using (var original = new Scene(refs, new Dictionary<string, byte[]>(), serverImage))
            using (var fixedScene = new Scene(refs, images, serverImage))
            {
                // warm-up (JIT) on a small profile
                var warm = new StoreCase { Name = "warm", Subtype = "avatar", Avatars = rows.Take(20).ToList(), OwnedAvatars = Ids(1, 5).ToList(), Store = Ids(1, 8).Select(i => "KillSignature_Avatar_" + i).ToList() };
                original.RunKillSignItems(warm, out _);
                fixedScene.RunKillSignItems(warm, out _);

                // One GetSignatureAssetsList call at unlock-all scale (what every signature lookup of the game costs).
                var oneCall = new AssetsCase { Name = "unlock-all", Rows = rows, Owned = owned, New = new List<string>(owned) };
                Assert.Equal(original.RunAssets(oneCall, out _), fixedScene.RunAssets(oneCall, out _));
                double originalCallMs = Median(Enumerable.Range(0, 5).Select(_ => { original.RunAssets(oneCall, out double ms); return ms; }));
                double patchedCallMs = Median(Enumerable.Range(0, 5).Select(_ => { fixedScene.RunAssets(oneCall, out double ms); return ms; }));
                output.WriteLine("GetSignatureAssetsList, 1,518 rows, 1,512 owned + 1,512 new: original " + originalCallMs.ToString("F2", CultureInfo.InvariantCulture) +
                                 " ms, patched " + patchedCallMs.ToString("F3", CultureInfo.InvariantCulture) + " ms per call (x" +
                                 (originalCallMs / patchedCallMs).ToString("F0", CultureInfo.InvariantCulture) + ")");

                // The signature screen: GetKillSignItems("avatar") with 1,512 owned avatars.
                string after = fixedScene.RunKillSignItems(c, out int patchedCopies);
                double patchedMs = fixedScene.LastMilliseconds;
                Assert.Equal(1 + (AllAvatars - UnlockAllOwned), patchedCopies);
                Assert.StartsWith("returns {\"slots\":[", after);
                output.WriteLine("GetKillSignItems(\"avatar\"), 1,512 owned, 6 sold: patched " + patchedMs.ToString("F1", CultureInfo.InvariantCulture) + " ms");
                Assert.True(patchedMs < 2000, "patched GetKillSignItems took " + patchedMs + " ms");

                // The original rebuilds the list once per owned avatar: ~1,512 x the call above. Run it in full with
                // MCVS_SLOW_TESTS=1 when that is affordable here (tens of seconds on a desktop net8; the phone's Mono JIT took
                // minutes), else time a subset (GetKillSignItemsMatchesTheOriginalExactly compares 378 owned of 1,518 in full).
                double estimateMs = originalCallMs * AllAvatars;
                output.WriteLine("original GetKillSignItems estimate: 1,518 x " + originalCallMs.ToString("F2", CultureInfo.InvariantCulture) + " ms = " +
                                 (estimateMs / 1000).ToString("F1", CultureInfo.InvariantCulture) + " s");
                if (estimateMs < 90000 && Environment.GetEnvironmentVariable("MCVS_SLOW_TESTS") == "1")
                {
                    string before = original.RunKillSignItems(c, out int originalCopies);
                    double originalMs = original.LastMilliseconds;
                    Assert.Equal(AllAvatars, originalCopies);
                    output.WriteLine("GetKillSignItems(\"avatar\"), 1,512 owned, 6 sold: original " + (originalMs / 1000).ToString("F1", CultureInfo.InvariantCulture) + " s (measured), patched " +
                                     patchedMs.ToString("F1", CultureInfo.InvariantCulture) + " ms: x" + (originalMs / patchedMs).ToString("F0", CultureInfo.InvariantCulture));
                    Assert.True(before == after, "unlock-all\noriginal: " + Shorten(before) + "\npatched:  " + Shorten(after));
                }
                else
                {
                    var subset = new StoreCase { Name = "subset", Subtype = "avatar", Avatars = rows, OwnedAvatars = owned.Take(50).ToList(), NewAvatars = new List<string>(owned) };
                    original.RunKillSignItems(subset, out _);
                    output.WriteLine("original, 50 of the 1,518 list builds: " + original.LastMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms -> ~" +
                                     (original.LastMilliseconds * AllAvatars / 50 / 1000).ToString("F0", CultureInfo.InvariantCulture) + " s for all of them");
                }

                // GetSignatureAssetsList is O(rows + ids) now: 1,512 calls (the original loop's count) stay well under 2 s.
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < UnlockAllOwned; i++) fixedScene.InvokeAssets("KillsignatureAvatars", owned, owned);
                double loopMs = sw.Elapsed.TotalMilliseconds;
                output.WriteLine("patched GetSignatureAssetsList x 1,512 at unlock-all scale: " + loopMs.ToString("F0", CultureInfo.InvariantCulture) + " ms");
                Assert.True(loopMs < 2000, "1,512 patched GetSignatureAssetsList calls took " + loopMs + " ms");

                // Not covered by G2: every kill signature the shop SELLS goes through GetKillSignItemObject, which builds the
                // list again (G1 makes each build O(rows + ids)). Half-way through the collection that is ~760 builds.
                List<string> half = Shuffled(Ids(1, AllAvatars), 8).Take(AllAvatars / 2).ToList();
                var halfWay = new StoreCase
                {
                    Name = "half-way", Subtype = "avatar", Avatars = rows, Backgrounds = backgrounds, OwnedAvatars = half, NewAvatars = half.Take(100).ToList(),
                    Store = Ids(1, AllAvatars).Select(i => "KillSignature_Avatar_" + i).ToList(),
                };
                fixedScene.RunKillSignItems(halfWay, out int halfCopies);
                double halfMs = fixedScene.LastMilliseconds;
                Assert.Equal(1 + (AllAvatars - half.Count), halfCopies);
                var halfCall = new AssetsCase { Name = "half-way", Rows = rows, Owned = half, New = halfWay.NewAvatars };
                Assert.Equal(original.RunAssets(halfCall, out _), fixedScene.RunAssets(halfCall, out _));
                double halfCallMs = Median(Enumerable.Range(0, 3).Select(_ => { original.RunAssets(halfCall, out double ms); return ms; }));
                output.WriteLine("GetKillSignItems(\"avatar\"), 759 owned, 759 sold: patched " + halfMs.ToString("F0", CultureInfo.InvariantCulture) + " ms (" + halfCopies +
                                 " list builds); original: 1,518 builds x " + halfCallMs.ToString("F1", CultureInfo.InvariantCulture) + " ms = ~" +
                                 (halfCallMs * AllAvatars / 1000).ToString("F0", CultureInfo.InvariantCulture) + " s (estimated)");
                Assert.True(halfMs < 5000, "patched half-way GetKillSignItems took " + halfMs + " ms");
            }
        }

        // ------------------------------------------------------------------ fixtures

        private sealed class Row
        {
            public Row(string id, string asset) : this(id, id, asset) { }

            public Row(string key, string id, string asset)
            {
                Key = key;
                Id = id;
                Asset = asset;
            }

            public string Key { get; }   // the table's dictionary key (normally the Id)

            public string Id { get; }

            public string Asset { get; }
        }

        private static IEnumerable<string> Ids(int first, int last)
        {
            for (int i = first; i <= last; i++) yield return i.ToString(CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> Shuffled(IEnumerable<string> ids, int seed)
        {
            var random = new Random(seed);
            return ids.OrderBy(_ => random.Next()).ToList();
        }

        private static List<Row> UnlockAllRows(string assetPrefix)
        {
            return Enumerable.Range(1, AllAvatars).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), assetPrefix + i.ToString("D2", CultureInfo.InvariantCulture) + ".png")).ToList();
        }

        /// <summary>A CRM offline_store document like the server's: one item per name, one offline billing method, varied prices.</summary>
        private static string StoreDocument(IEnumerable<string> names, bool crates = false, string promotion = null)
        {
            var items = names.Select((n, i) =>
                "{\"item\":\"" + n + "\",\"quantity\":1,\"category\":[" + (n.StartsWith("SoftCurrency", StringComparison.Ordinal) ? "\"Currencies\"" : "") +
                "],\"hidden\":false,\"billing_methods\":[{\"name\":\"offline\",\"type\":\"offline\",\"price\":[{\"currency\":\"VersusCoins\",\"price\":" +
                (50 + i * 37 % 200).ToString(CultureInfo.InvariantCulture) + "}],\"replaced_price\":[" +
                (promotion != null && i % 3 == 0 ? "{\"currency\":\"VersusCoins\",\"price\":" + (300 + i).ToString(CultureInfo.InvariantCulture) + "}" : "") + "]}]}").ToList();
            if (crates)
                items.Add("{\"item\":\"Crate_KillSignature_Avatar_1\",\"quantity\":1,\"category\":[\"Crates\"],\"hidden\":false,\"billing_methods\":[{\"name\":\"offline\",\"type\":\"offline\",\"price\":[],\"replaced_price\":[]}]}");
            return "{\"prices\":[" + string.Join(",", items) + "]" + (promotion == null ? "" : ",\"promotion\":{\"description\":\"" + promotion + "\"}") + "}";
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }

        private static string Q(string s)
        {
            return s == null ? "null" : "\"" + s + "\"";
        }

        private static string Shorten(string s)
        {
            return s.Length <= 400 ? s : s.Substring(0, 300) + " ... (" + s.Length + " chars) ... " + s.Substring(s.Length - 80);
        }

        private static string RowsAfter(IEnumerable<(string Id, bool Locked, bool New)> rows)
        {
            return "[" + string.Join(" ", rows.Select(r => Q(r.Id) + ":" + (r.Locked ? "L" : "-") + (r.New ? "N" : "-"))) + "]";
        }

        /// <summary>
        /// One load context (original or patched Game.dll) with the fake state the two methods read: the meta Database of
        /// PerUserDatabase (path of DatabasePaths.MetaDatabase, the profile's FedId) whose table cache holds counting
        /// SignatureItemDataCollections, an uninitialised PlayerProfileComponent and an uninitialised GameOfflineStore.
        /// </summary>
        private sealed class Scene : IDisposable
        {
            private const string MetaPath = "Design/MCVS_Meta.xlsx";

            private readonly GameContext context;
            private readonly Type profileType, itemType, countingType, storeDataType, jtoken;
            private readonly IDictionary tableCache;
            private readonly MethodInfo assets, killSignItems;
            private readonly FieldInfo copies, dataField, fedId;
            private readonly object profile, store, me;

            public Scene(string refs, Dictionary<string, byte[]> images, byte[] serverImage)
            {
                context = new GameContext(refs, images, serverImage);
                IsPatched = images.ContainsKey("Game.dll");
                Assembly g = context.Get("Game"), engine = context.Get("Gameloft.Engine"), online = context.Get("Gameloft.OnlinePlatform");

                // DatabasePaths.MetaDatabase reads DatabasePathsComponent.ClassInstance.MetaDatabasePath.
                Type pathsType = g.GetType("Game.DatabasePathsComponent", true);
                object paths = RuntimeHelpers.GetUninitializedObject(pathsType);
                pathsType.GetProperty("MetaDatabasePath", Any).SetValue(paths, MetaPath);
                pathsType.GetField("ClassInstance", Any).SetValue(null, paths);

                // A Database without a file (its constructor needs GameApplication): empty Tables, a table cache we fill.
                Type databaseType = engine.GetType("Gameloft.Engine.Database", true);
                object database = RuntimeHelpers.GetUninitializedObject(databaseType);
                Type tableBase = engine.GetType("Gameloft.Engine.TableLoader+TableBase", true);
                tableCache = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), tableBase));
                databaseType.GetField("tableCache", Any).SetValue(database, tableCache);
                Type rawTable = databaseType.GetNestedType("Table", Any);
                databaseType.GetField("<Tables>k__BackingField", Any).SetValue(database, Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), rawTable)));

                // PerUserDatabase.GetDatabase(path, credential) finds it under "path@credential".
                Type credentialType = online.GetType("Gameloft.OnlinePlatform.Credential", true);
                object me = credentialType.GetField("Me", Any).GetValue(null);
                Type perUser = engine.GetType("Gameloft.Engine.PerUserDatabase", true);
                var databases = (IDictionary)perUser.GetField("databases", Any).GetValue(null);
                databases[MetaPath + "@" + me] = database;
                MethodInfo getDatabase = perUser.GetMethod("GetDatabase", Any, null, new[] { typeof(string), credentialType, typeof(bool) }, null);
                Assert.Same(database, getDatabase.Invoke(null, new[] { MetaPath, me, false }));

                profileType = g.GetType("Game.PlayerProfileComponent", true);
                profile = RuntimeHelpers.GetUninitializedObject(profileType);
                fedId = engine.GetType("Gameloft.Engine.UserComponentBase", true).GetField("fedId", Any);
                this.me = me;
                fedId.SetValue(profile, me);
                profileType.GetProperty("AvatarsTableName", Any).SetValue(profile, "KillsignatureAvatars");
                profileType.GetProperty("BackgroundTableName", Any).SetValue(profile, "KillsignatureBackgrounds");
                profileType.GetProperty("SoundsTableName", Any).SetValue(profile, "KillsignatureSounds");
                SetProfileLists(new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>(), new List<string>());

                itemType = profileType.GetNestedType("SignatureItemData", Any);
                Type collectionType = profileType.GetNestedType("SignatureItemDataCollection", Any);
                countingType = CountingTable(g, collectionType, itemType);
                copies = countingType.GetField("ListCopies");
                assets = profileType.GetMethod("GetSignatureAssetsList", Any);

                Type storeType = g.GetType("Game.GameOfflineStore", true);
                store = RuntimeHelpers.GetUninitializedObject(storeType);
                killSignItems = storeType.GetMethod("GetKillSignItems", Any, null, new[] { typeof(string), profileType }, null);
                dataField = engine.GetType("Gameloft.Engine.OfflineStoreBase", true).GetField("<Data>k__BackingField", Any);
                storeDataType = online.GetType("Gameloft.OnlinePlatform.OfflineStoreData", true);
                jtoken = context.Get("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JToken", true);

                // The shop side (GetKillSignItemObject, for the kill signatures the player does not own): the store's World has
                // no global components (GetItemLocId: not a server, no Localization -> ""), CurrenciesComponent has empty user
                // data for the credential (no currency images), the profile has some VersusCoins.
                Type worldType = engine.GetType("Gameloft.Engine.World", true);
                Type componentType = engine.GetType("Gameloft.Engine.Component", true);
                Type gameObjectType = engine.GetType("Gameloft.Engine.GameObject", true);
                object world = RuntimeHelpers.GetUninitializedObject(worldType);
                Type cacheType = context.Get("Gameloft.Util").GetType("Gameloft.Util.ConcurrentCache`2", true).MakeGenericType(typeof(Type), componentType);
                Delegate none = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(Type), componentType),
                                                        typeof(Scene).GetMethod(nameof(NoComponent), Any).MakeGenericMethod(componentType));
                worldType.GetField("globalComponents", Any).SetValue(world, Activator.CreateInstance(cacheType, none));
                object gameObject = RuntimeHelpers.GetUninitializedObject(gameObjectType);
                gameObjectType.GetField("world", Any).SetValue(gameObject, world);
                componentType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single(f => f.FieldType == gameObjectType).SetValue(store, gameObject);

                Type currenciesType = g.GetType("Game.CurrenciesComponent", true);
                object currencies = RuntimeHelpers.GetUninitializedObject(currenciesType);
                Type userDataType = currenciesType.GetNestedType("UserData", Any);
                object userData = Activator.CreateInstance(userDataType);
                FieldInfo currencyTable = userDataType.GetField("currencies", Any);
                currencyTable.SetValue(userData, Activator.CreateInstance(currencyTable.FieldType));
                var perUserData = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(credentialType, userDataType));
                perUserData[me] = userData;
                currenciesType.BaseType.GetField("perUserData", Any).SetValue(currencies, perUserData);
                storeType.GetField("currencies", Any).SetValue(store, currencies);
                profileType.GetField("Currencies", Any).SetValue(profile, new Dictionary<string, int> { ["VersusCoins"] = 120, ["HardCurrency"] = 3 });
                // RewardItem.Parse finds KillSignatureRewardItem through TypeUtil, which searches the registered game assemblies.
                ((IList)engine.GetType("Gameloft.Engine.ComponentAssemblies", true).GetField("gameAssemblies", Any).GetValue(null)).Add(g);
            }

            private static TComponent NoComponent<TComponent>(Type type) where TComponent : class
            {
                return null;
            }

            public bool IsPatched { get; }

            /// <summary>How long the last GetKillSignItems call itself took (the fake state is built before the clock starts).</summary>
            public double LastMilliseconds { get; private set; }

            /// <summary>
            /// A SignatureItemDataCollection subclass (GetTable&lt;SignatureItemDataCollection&gt; accepts it) that re-implements
            /// IEnumerable&lt;SignatureItemData&gt; to count the <c>table.ToList()</c> of each GetSignatureAssetsList call; the
            /// method's own foreach binds to GenericTable.GetEnumerator() and is not counted.
            /// </summary>
            private static Type CountingTable(Assembly game, Type collectionType, Type itemType)
            {
                using (AssemblyLoadContext.EnterContextualReflection(game))
                {
                    AssemblyBuilder ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("McvsCountingTable" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
                    ModuleBuilder mb = ab.DefineDynamicModule("CountingTable");
                    TypeBuilder tb = mb.DefineType("CountingSignatureItemDataCollection", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, collectionType);
                    Type enumerable = typeof(IEnumerable<>).MakeGenericType(itemType);
                    Type enumerator = typeof(IEnumerator<>).MakeGenericType(itemType);
                    tb.AddInterfaceImplementation(enumerable);
                    FieldBuilder count = tb.DefineField("ListCopies", typeof(int), FieldAttributes.Public);
                    MethodBuilder get = tb.DefineMethod("CountedGetEnumerator",
                        MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                        enumerator, Type.EmptyTypes);
                    ILGenerator il = get.GetILGenerator();
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldfld, count);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
                    il.Emit(System.Reflection.Emit.OpCodes.Add);
                    il.Emit(System.Reflection.Emit.OpCodes.Stfld, count);
                    il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
                    il.Emit(System.Reflection.Emit.OpCodes.Call, collectionType.GetMethod("GetEnumerator", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null));
                    il.Emit(System.Reflection.Emit.OpCodes.Ret);
                    tb.DefineMethodOverride(get, enumerable.GetMethod("GetEnumerator"));
                    tb.DefineDefaultConstructor(MethodAttributes.Public);
                    return tb.CreateType();
                }
            }

            /// <summary>The profile's FedId: Credential.Me, or an invalid (default) credential.</summary>
            public void SetCredential(bool valid)
            {
                fedId.SetValue(profile, valid ? me : Activator.CreateInstance(fedId.FieldType));
            }

            public void SetProfileLists(List<string> avatars, List<string> newAvatars, List<string> backgrounds, List<string> newBackgrounds,
                                        List<string> sounds, List<string> newSounds)
            {
                profileType.GetProperty("KillSignatureAvatars", Any).SetValue(profile, Copy(avatars));
                profileType.GetProperty("KillSignatureNewAvatars", Any).SetValue(profile, Copy(newAvatars));
                profileType.GetProperty("KillSignatureBackgrounds", Any).SetValue(profile, Copy(backgrounds));
                profileType.GetProperty("KillSignatureNewBackgrounds", Any).SetValue(profile, Copy(newBackgrounds));
                profileType.GetProperty("KillSignatureSounds", Any).SetValue(profile, Copy(sounds));
                profileType.GetProperty("KillSignatureNewSounds", Any).SetValue(profile, Copy(newSounds));
            }

            private static List<string> Copy(List<string> ids)
            {
                return ids == null ? null : new List<string>(ids);
            }

            /// <summary>A fresh table of new rows (locked = isNew = true, as the loader creates them) in the database's cache.</summary>
            public object SetTable(string name, List<Row> rows)
            {
                object table = Activator.CreateInstance(countingType);
                var dictionary = (IDictionary)countingType.GetProperty("Table").GetValue(table);
                foreach (Row r in rows)
                {
                    object item = Activator.CreateInstance(itemType);
                    itemType.GetProperty("Id").SetValue(item, r.Id);
                    itemType.GetProperty("value").SetValue(item, r.Asset);
                    itemType.GetProperty("locId").SetValue(item, "loc" + r.Key);
                    itemType.GetProperty("rarity").SetValue(item, "Rarity01");
                    dictionary[r.Key] = item;
                }
                tableCache[name] = table;
                return table;
            }

            public void RemoveTable(string name)
            {
                tableCache.Remove(name);
            }

            private int Copies(string name)
            {
                return tableCache.Contains(name) ? (int)copies.GetValue(tableCache[name]) : 0;
            }

            public object InvokeAssets(string table, List<string> owned, List<string> fresh)
            {
                return assets.Invoke(profile, new object[] { table, owned, fresh });
            }

            /// <summary>Runs GetSignatureAssetsList on the case; the outcome (returned rows or exception, then every row's flags) as text.</summary>
            public string RunAssets(AssetsCase c, out double milliseconds)
            {
                const string name = "KillsignatureAvatars";
                object table = null;
                if (c.Table) table = SetTable(name, c.Rows);
                else RemoveTable(name);
                List<string> owned = Copy(c.Owned), fresh = Copy(c.New);
                string result;
                var sw = Stopwatch.StartNew();
                try
                {
                    object list = InvokeAssets(name, owned, fresh);
                    milliseconds = sw.Elapsed.TotalMilliseconds;
                    result = "returns " + DescribeRows((IList)list, table);
                }
                catch (TargetInvocationException e)
                {
                    milliseconds = sw.Elapsed.TotalMilliseconds;
                    result = "throws " + e.InnerException.GetType().FullName;
                }
                if (table != null) result += " rows " + RowsAfter(TableRows(table).Select(r => ((string)Get(r, "Id"), (bool)Get(r, "locked"), (bool)Get(r, "isNew"))));
                // the lists are read, never written
                Assert.Equal(c.Owned == null ? null : string.Join(",", c.Owned), owned == null ? null : string.Join(",", owned));
                Assert.Equal(c.New == null ? null : string.Join(",", c.New), fresh == null ? null : string.Join(",", fresh));
                return result;
            }

            public string RunGetter(string getter)
            {
                IList list = (IList)profileType.GetMethod(getter, Any, null, Type.EmptyTypes, null).Invoke(profile, null);
                return DescribeRows(list, null);
            }

            private string DescribeRows(IList list, object table)
            {
                List<object> rows = table == null ? null : TableRows(table).ToList();
                var sb = new StringBuilder("[");
                foreach (object item in list)
                {
                    int index = rows == null ? -2 : rows.FindIndex(r => ReferenceEquals(r, item));
                    sb.Append(index).Append(':').Append(Q((string)Get(item, "id"))).Append(':').Append(Q((string)Get(item, "value"))).Append(':')
                      .Append((bool)Get(item, "locked") ? "L" : "-").Append((bool)Get(item, "isNew") ? "N" : "-").Append(' ');
                }
                return sb.Append(']').ToString();
            }

            private static IEnumerable<object> TableRows(object table)
            {
                return ((IEnumerable)table).Cast<object>();
            }

            private object Get(object item, string member)
            {
                FieldInfo f = itemType.GetField(member, Any);
                return f != null ? f.GetValue(item) : itemType.GetProperty(member, Any).GetValue(item);
            }

            /// <summary>Runs GetKillSignItems(subtype, profile); the returned dictionary (or exception) as canonical JSON-like text.</summary>
            public string RunKillSignItems(StoreCase c, out int listCopies)
            {
                SetProfileLists(c.OwnedAvatars, c.NewAvatars, c.OwnedBackgrounds, c.NewBackgrounds, c.OwnedSounds, new List<string> { "1" });
                if (c.AvatarTable) SetTable("KillsignatureAvatars", c.Avatars);
                else RemoveTable("KillsignatureAvatars");
                SetTable("KillsignatureBackgrounds", c.Backgrounds);
                SetTable("KillsignatureSounds", Enumerable.Range(1, 5).Select(i => new Row(i.ToString(CultureInfo.InvariantCulture), "sfx_" + i)).ToList());
                object data = null;
                if (!c.NullData)
                {
                    object token = jtoken.GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { c.StoreJson });
                    data = Activator.CreateInstance(storeDataType, token);
                }
                dataField.SetValue(store, data);
                string result;
                var sw = Stopwatch.StartNew();
                try
                {
                    object items = killSignItems.Invoke(store, new[] { c.Subtype, c.NullProfile ? null : profile });
                    LastMilliseconds = sw.Elapsed.TotalMilliseconds;
                    result = "returns " + Canonical(items);
                }
                catch (TargetInvocationException e)
                {
                    LastMilliseconds = sw.Elapsed.TotalMilliseconds;
                    result = "throws " + e.InnerException.GetType().FullName + ": " + e.InnerException.Message + " " + Thrower(e.InnerException);
                }
                listCopies = c.Subtype.ToLowerInvariant() == "background" ? Copies("KillsignatureBackgrounds") : Copies("KillsignatureAvatars");
                return result;
            }

            /// <summary>The innermost game frame the exception came from (the same method in both versions).</summary>
            private static string Thrower(Exception e)
            {
                string frame = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("at ", StringComparison.Ordinal)) ?? "";
                return "(" + frame + ")";
            }

            private static string Canonical(object value)
            {
                var sb = new StringBuilder();
                Write(sb, value);
                return sb.ToString();
            }

            private static void Write(StringBuilder sb, object value)
            {
                switch (value)
                {
                    case null:
                        sb.Append("null");
                        return;
                    case string s:
                        sb.Append('"').Append(s).Append('"');
                        return;
                    case bool b:
                        sb.Append(b ? "true" : "false");
                        return;
                    case IConvertible n when value.GetType().IsPrimitive:
                        sb.Append(value.GetType().Name).Append('(').Append(n.ToString(CultureInfo.InvariantCulture)).Append(')');
                        return;
                    case IDictionary d:
                        sb.Append('{');
                        bool first = true;
                        foreach (DictionaryEntry e in d)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            sb.Append('"').Append(e.Key).Append("\":");
                            Write(sb, e.Value);
                        }
                        sb.Append('}');
                        return;
                    case IEnumerable list:
                        sb.Append('[');
                        bool head = true;
                        foreach (object o in list)
                        {
                            if (!head) sb.Append(',');
                            head = false;
                            Write(sb, o);
                        }
                        sb.Append(']');
                        return;
                    default:
                        sb.Append(value.GetType().FullName).Append('<').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('>');
                        return;
                }
            }

            public void Dispose()
            {
                context.Dispose();
            }
        }
    }
}
