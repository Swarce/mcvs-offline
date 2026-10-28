using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Patcher.Manifest;
using McvsOffline.Patcher.Patches;
using McvsOffline.Patcher.Signing;
using McvsOffline.Patcher.Tools;
using McvsOffline.Patcher.Zip;
using Mono.Cecil;
using ModuleDefinition = Mono.Cecil.ModuleDefinition;
using AssemblyReference = System.Reflection.Metadata.AssemblyReference;

namespace McvsOffline.Patcher
{
    /// <summary>What a run did (also printed for the player).</summary>
    public sealed class PatchReport
    {
        public List<string> Modified { get; } = new List<string>();
        public List<string> Added { get; } = new List<string>();
        public List<string> Removed { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public int UnchangedEntries { get; set; }
        public string Signer { get; set; }
        public string KeystorePath { get; set; }
        public bool KeystoreCreated { get; set; }
        public string Verification { get; set; }
    }

    /// <summary>
    /// Turns the player's own APK into the offline APK (ARCHITECTURE §2.3, verify-C7 §7):
    /// <list type="number">
    /// <item>IL-patch NicePlaySDK.dll so the game starts McvsOffline.Bootstrap.Init() (plus the optional patches);</item>
    /// <item>add McvsOffline.Server.dll to assemblies/ (STORED, 4-byte aligned, like every assembly there);</item>
    /// <item>delete lib/&lt;abi&gt;/libaot-X.dll.so of every modified assembly X so Mono JITs the patched IL (AOT
    /// mode "normal" falls back to JIT, verify-C7 §3);</item>
    /// <item>keep the native libraries of the selected ABIs (default: every ABI the APK ships; with --abi arm64-v8a the
    /// other lib/&lt;abi&gt;/ folders are dropped, nothing else in the APK refers to them, Android picks the ABI from lib/);</item>
    /// <item>never touch the six typemap-pinned assemblies (their MVIDs are baked into libxamarin-app.so);</item>
    /// <item>rename the manifest's vendor-wide signature permission into the package's namespace, so the re-signed APK
    /// installs next to other Gameloft games (<see cref="ManifestPatch"/>);</item>
    /// <item>with "debloat" (default): remove the manifest declarations of SDK components that are dead offline
    /// (<see cref="ManifestDebloat"/>) and drop the Crashlytics/Fabric files nothing loads (<see cref="DebloatDroppedEntries"/>);</item>
    /// <item>keep every other entry byte-identical (same compression method and bytes; lib/ stays DEFLATED, which
    /// matches the default extractNativeLibs=true of this manifest);</item>
    /// <item>align (4 / 4096 for stored .so) and sign v1+v2(+v3) with the player's key.</item>
    /// </list>
    /// The native signature check needs no patch: its result is only stored as an engine property and nothing
    /// local reads it (verify-C7 §1).
    /// </summary>
    public static class ApkPatcher
    {
        public static readonly IlPatch[] AllPatches =
        {
            new BootstrapHookPatch(), new HeadlessGamePatch(), new HeadlessEnginePatch(), new PhysicsPatch(), new NativeOnlinePatch(),
            new WaitsPatch(), new GeoPatch(), new BotTilePatch(), new FinderPatch(), new LanPatch(), new StorePatch(), new SignaturesPatch(),
            new AdminPagePatch(), new DebloatEnginePatch(), new AdminLinkPatch(), new ForumPatch(), new GraphicsPatch(),
        };

        public static PatchReport Run(PatcherOptions options, Action<string> log)
        {
            log = log ?? (s => { });
            var report = new PatchReport();
            if (string.IsNullOrEmpty(options.InputApk)) throw new ArgumentException("--apk is required");
            if (string.IsNullOrEmpty(options.ServerDll)) throw new ArgumentException("--server is required");
            if (string.IsNullOrEmpty(options.OutputApk) && !options.DryRun) throw new ArgumentException("--out is required");
            if (!options.DryRun && Path.GetFullPath(options.OutputApk) == Path.GetFullPath(options.InputApk))
                throw new ArgumentException("--out must differ from --apk (keep your original APK)");

            log("reading " + options.InputApk);
            ZipArchiveReader.Archive input = ZipArchiveReader.Read(options.InputApk);
            if (input.Find(KnownBuild.AssembliesDir + "NicePlaySDK.dll") == null)
                throw new InvalidDataException(options.InputApk + " has no assemblies/NicePlaySDK.dll: not a Modern Combat Versus " + KnownBuild.Version + " APK");
            if (input.Find(KnownBuild.AssembliesDir + KnownBuild.ServerAssemblyFile) != null)
                throw new InvalidDataException(options.InputApk + " already contains " + KnownBuild.ServerAssemblyFile + ": run the patcher on your original APK");
            CheckBuildIdentity(input, options, report, log);

            byte[] serverBytes = File.ReadAllBytes(options.ServerDll);
            if (!options.SkipServerReferenceCheck) CheckServerAssembly(serverBytes, options.ServerDll);

            List<IlPatch> patches = SelectPatches(options.Patches, !options.TestOnlyHookIsMandatory);
            if (options.TestOnlyDebloatWithoutIl) patches.RemoveAll(p => p is DebloatEnginePatch);
            var modifiedAssemblies = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            string resolverDir = Path.Combine(Path.GetTempPath(), "mcvs-patcher-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(resolverDir);
                foreach (ZipEntry e in input.Entries.Where(e => IsAssembly(e.Name)))
                    File.WriteAllBytes(Path.Combine(resolverDir, Path.GetFileName(e.Name)), e.GetData());
                File.WriteAllBytes(Path.Combine(resolverDir, KnownBuild.ServerAssemblyFile), serverBytes);

                using (var resolver = new DefaultAssemblyResolver())
                {
                    resolver.AddSearchDirectory(resolverDir);
                    foreach (string dir in resolver.GetSearchDirectories().Where(d => d != resolverDir).ToList()) resolver.RemoveSearchDirectory(dir);
                    var reader = new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadingMode = ReadingMode.Immediate };
                    using (ModuleDefinition server = ModuleDefinition.ReadModule(new MemoryStream(serverBytes), reader))
                    {
                        var context = new PatchContext(server, options.Port);
                        foreach (var group in patches.GroupBy(p => p.AssemblyFile))
                        {
                            string file = group.Key;
                            if (KnownBuild.TypemapPinned.ContainsKey(file))
                                throw new InvalidOperationException("refusing to modify " + file + ": its MVID is pinned in libxamarin-app.so (verify-C7 §4)");
                            ZipEntry entry = input.Find(KnownBuild.AssembliesDir + file) ?? throw new InvalidDataException("assemblies/" + file + " missing");
                            modifiedAssemblies[file] = PatchAssembly(entry.GetData(), group.ToList(), context, reader, server.Mvid, log);
                        }
                        report.Notes.AddRange(context.Notes);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(resolverDir, true); } catch (Exception) { }
            }

            ZipEntry manifestEntry = input.Find(ManifestPatch.EntryName) ?? throw new InvalidDataException(options.InputApk + " has no " + ManifestPatch.EntryName);
            byte[] manifest = ManifestPatch.Apply(manifestEntry.GetData(), report.Notes);
            bool debloat = options.Patches != null && options.Patches.Contains(ManifestDebloat.PatchId);
            if (debloat)
            {
                log("patch " + ManifestDebloat.PatchId + ": dead SDK components out of the manifest, Crashlytics/Fabric files out of the APK");
                CheckDebloatEntries(input);
                manifest = ManifestDebloat.Apply(manifest ?? manifestEntry.GetData(), report.Notes);
            }

            byte[] rawArgs = RawArgsFor(input, options, report);

            CheckAbis(input, options.Abis, report);
            List<ZipEntry> output = ComposeEntries(input, modifiedAssemblies, manifest, rawArgs, serverBytes, options.StripAot, options.Abis, debloat, report);
            report.Warnings.AddRange(StaleAotNotes(input, output, modifiedAssemblies.Keys));
            byte[] unsigned = ZipArchiveWriter.Write(output);
            var modifiedEntries = modifiedAssemblies.Keys.Select(f => KnownBuild.AssembliesDir + f).ToList();
            if (manifest != null) modifiedEntries.Add(ManifestPatch.EntryName);
            if (rawArgs != null) modifiedEntries.Add(KnownBuild.RawArgsEntry);
            VerifyUnchanged(input, ZipArchiveReader.Read(unsigned), modifiedEntries, report);

            if (options.DryRun)
            {
                log("dry run: all patch sites verified, nothing written");
                return report;
            }

            Sign(options, output, unsigned, report, log);
            return report;
        }

        /// <summary>Applies the group's patches to one assembly and gives it a new (deterministic) MVID.</summary>
        internal static byte[] PatchAssembly(byte[] original, List<IlPatch> patches, PatchContext context, ReaderParameters reader,
                                             Guid serverMvid, Action<string> log)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(original), reader))
            {
                foreach (IlPatch patch in patches)
                {
                    log("patch " + patch.Id + ": " + patch.Description);
                    patch.Apply(module, context);
                }
                // A new MVID is essential: other assemblies' AOT images record this one's MVID, and a match would make
                // Mono run AOT code compiled against the unpatched metadata (verify-C7 §3).
                module.Mvid = DerivedMvid(module.Mvid, patches.Select(p => p.Id), serverMvid);
                var stream = new MemoryStream();
                module.Write(stream);
                byte[] patched = stream.ToArray();
                using (ModuleDefinition check = ModuleDefinition.ReadModule(new MemoryStream(patched), reader))
                {
                    if (check.Mvid != module.Mvid) throw new InvalidOperationException("re-reading " + module.Name + " failed");
                }
                return patched;
            }
        }

        public static Guid DerivedMvid(Guid original, IEnumerable<string> patchIds, Guid serverMvid)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] seed = original.ToByteArray().Concat(serverMvid.ToByteArray())
                    .Concat(Encoding.UTF8.GetBytes("mcvs-offline:" + string.Join(",", patchIds))).ToArray();
                byte[] hash = sha.ComputeHash(seed);
                var bytes = new byte[16];
                Array.Copy(hash, bytes, 16);
                bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // RFC 4122 version 4 (in .NET's byte order)
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // variant
                return new Guid(bytes);
            }
        }

        public static List<IlPatch> SelectPatches(ICollection<string> optional, bool headless = true)
        {
            var ids = new HashSet<string>(optional ?? new string[0], StringComparer.Ordinal);
            ids.UnionWith(headless ? PatcherOptions.MandatoryPatches : new[] { "hook" });
            if (ids.Contains("lan")) ids.Add("finder");
            return AllPatches.Where(p => ids.Contains(p.Id)).ToList();
        }

        /// <summary>
        /// --game-log all: the new content of res/raw/args.txt (a marker line without -LogCategoryFilter, so the game enables
        /// every log level), or null to keep the entry. The entry keeps its name, so resources.arsc (which stores the path)
        /// still resolves raw/args to it. Refuses an entry that is not the shipped one unless --force (then it is kept).
        /// </summary>
        internal static byte[] RawArgsFor(ZipArchiveReader.Archive input, PatcherOptions options, PatchReport report)
        {
            if (options.GameLog != GameLogMode.All) return null;
            ZipEntry e = input.Find(KnownBuild.RawArgsEntry);
            byte[] data = e == null ? null : e.GetData();
            bool original = e != null && e.Crc32 == KnownBuild.RawArgsOriginalCrc &&
                            Encoding.ASCII.GetString(data) == KnownBuild.RawArgsOriginal;
            if (!original)
            {
                string what = e == null ? "is missing" : "is not the shipped '" + KnownBuild.RawArgsOriginal + "' (" + data.Length + " bytes)";
                if (!options.Force) throw new InvalidDataException(KnownBuild.RawArgsEntry + " " + what + ": use --game-log fatal, or --force to keep it as it is");
                report.Warnings.Add(KnownBuild.RawArgsEntry + " " + what + ": left unchanged, the game keeps its own log levels");
                return null;
            }
            report.Notes.Add(KnownBuild.RawArgsEntry + ": \"" + KnownBuild.RawArgsOriginal + "\" -> \"" + KnownBuild.RawArgsAllLevels.TrimEnd('\n') +
                             "\" (no -LogCategoryFilter: the game logs every level to logcat mono-stdout; --game-log fatal keeps the original)");
            return Encoding.ASCII.GetBytes(KnownBuild.RawArgsAllLevels);
        }

        /// <summary>lib/&lt;abi&gt;/... -> abi, or null for any other entry.</summary>
        internal static string AbiOf(string entryName)
        {
            if (!entryName.StartsWith("lib/", StringComparison.Ordinal)) return null;
            int slash = entryName.IndexOf('/', 4);
            return slash < 0 ? null : entryName.Substring(4, slash - 4);
        }

        /// <summary>
        /// Every kept ABI must be in the APK with the Mono runtime (libmonodroid.so), and something must be left: an output with no
        /// native code for the phone would install and then fail to start.
        /// </summary>
        private static void CheckAbis(ZipArchiveReader.Archive input, SortedSet<string> keep, PatchReport report)
        {
            var present = new SortedSet<string>(input.Entries.Select(e => AbiOf(e.Name)).Where(a => a != null), StringComparer.Ordinal);
            if (present.Count == 0)
            {
                report.Warnings.Add("the APK has no native libraries (lib/): nothing to select by ABI");
                return;
            }
            if (keep == null)
            {
                report.Notes.Add("native libraries: every ABI kept (" + string.Join(", ", present) + ")");
                return;
            }
            foreach (string abi in keep)
            {
                if (input.Find("lib/" + abi + "/libmonodroid.so") == null)
                    throw new InvalidDataException("the APK has no lib/" + abi + "/libmonodroid.so: cannot keep only " + string.Join(",", keep) +
                                                   " (ABIs in the APK: " + (present.Count == 0 ? "none" : string.Join(", ", present)) + "; see --abi)");
            }
            var dropped = present.Where(a => !keep.Contains(a)).ToList();
            report.Notes.Add("native libraries: kept " + string.Join(", ", keep) + (dropped.Count == 0 ? "" : "; dropped " + string.Join(", ", dropped) +
                             " (the offline build is " + (keep.Count == 1 && keep.Contains(PatcherOptions.Arm64) ? "64-bit ARM only" : string.Join("/", keep) + " only") + ")"));
        }

        private static List<ZipEntry> ComposeEntries(ZipArchiveReader.Archive input, Dictionary<string, byte[]> modified, byte[] manifest,
                                                     byte[] rawArgs, byte[] serverBytes, AotStripMode stripAot, SortedSet<string> keepAbis,
                                                     bool debloat, PatchReport report)
        {
            var removeAot = new HashSet<string>(modified.Keys.Select(f => "libaot-" + f + ".so"), StringComparer.Ordinal);
            var output = new List<ZipEntry>();
            var debloated = new List<string>();
            int lastAssembly = -1;
            ZipEntry assemblyTemplate = null;
            foreach (ZipEntry e in input.Entries)
            {
                if (BuiltInSigner.IsSignatureFile(e.Name))
                {
                    report.Removed.Add(e.Name + " (old signature)");
                    continue;
                }
                string abi = AbiOf(e.Name);
                if (abi != null && keepAbis != null && !keepAbis.Contains(abi))
                {
                    report.Removed.Add(e.Name + " (native library of an ABI not kept, --abi)");
                    continue;
                }
                if (debloat && IsDebloatEntry(e.Name))
                {
                    report.Removed.Add(e.Name + " (" + DebloatDroppedEntries[e.Name] + "; " + ManifestDebloat.PatchId + ")");
                    debloated.Add(e.Name);
                    continue;
                }
                if (IsAotImage(e.Name))
                {
                    string file = e.Name.Substring(e.Name.LastIndexOf('/') + 1);
                    if (stripAot == AotStripMode.All || removeAot.Contains(file))
                    {
                        report.Removed.Add(e.Name + " (AOT image; Mono JITs the assembly)");
                        continue;
                    }
                }
                if (IsAssembly(e.Name))
                {
                    string file = Path.GetFileName(e.Name);
                    if (modified.TryGetValue(file, out byte[] patched))
                    {
                        output.Add(ZipEntry.CreateStored(e.Name, patched, e));
                        report.Modified.Add(e.Name);
                    }
                    else
                    {
                        output.Add(e);
                        report.UnchangedEntries++;
                    }
                    lastAssembly = output.Count - 1;
                    assemblyTemplate = e;
                    continue;
                }
                if (rawArgs != null && e.Name == KnownBuild.RawArgsEntry)
                {
                    output.Add(e.Method == ZipEntry.MethodStored ? ZipEntry.CreateStored(e.Name, rawArgs, e) : ZipEntry.CreateDeflated(e.Name, rawArgs, e));
                    report.Modified.Add(e.Name);
                    continue;
                }
                if (manifest != null && e.Name == ManifestPatch.EntryName)
                {
                    // Same compression method as the original (DEFLATED in 1.17.32).
                    output.Add(e.Method == ZipEntry.MethodStored ? ZipEntry.CreateStored(e.Name, manifest, e) : ZipEntry.CreateDeflated(e.Name, manifest, e));
                    report.Modified.Add(e.Name);
                    continue;
                }
                output.Add(e);
                report.UnchangedEntries++;
            }
            string serverName = KnownBuild.AssembliesDir + KnownBuild.ServerAssemblyFile;
            output.Insert(lastAssembly + 1, ZipEntry.CreateStored(serverName, serverBytes, assemblyTemplate));
            report.Added.Add(serverName);
            if (debloat)
            {
                int libraries = debloated.Count(n => n.StartsWith("lib/", StringComparison.Ordinal));
                report.Notes.Add(ManifestDebloat.PatchId + ": dropped " + debloated.Count + " Crashlytics/Fabric files nothing loads offline (" +
                                 libraries + " lib/<abi>/libcrashlytics.so, " + (debloated.Count - libraries) + " Fabric/Crashlytics .properties)");
            }
            return output;
        }

        private const string DebloatLibrary = "Crashlytics NDK library, never loaded offline";
        private const string DebloatProperties = "read only by Fabric.with, which never runs";

        /// <summary>
        /// Patch "debloat", files: the Crashlytics/Fabric entries of 1.17.32 (unzip -l) that nothing loads offline, with the
        /// reason reported. libcrashlytics.so is loaded only by the static initializer of com.crashlytics.android.ndk.JniNativeApi,
        /// reached only from managed Crashlytics.Start (when the TS handshake says EnableCrashlytics: CRM
        /// game.CrashlyticsParams.Enable, false in our crm_overlay.json; the debloat IL patch also makes Start a no-op); no native
        /// library dlopens it and no P/Invoke names it. The .properties files are read only inside Fabric.with, which never
        /// runs (no google_app_id; ManifestDebloat also removes CrashlyticsInitProvider and io.fabric.ApiKey).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> DebloatDroppedEntries = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["assets/crashlytics-build.properties"] = "Crashlytics build id, " + DebloatProperties,
            ["fabric/com.crashlytics.sdk.android.answers.properties"] = "Fabric kit version, " + DebloatProperties,
            ["fabric/com.crashlytics.sdk.android.beta.properties"] = "Fabric kit version, " + DebloatProperties,
            ["fabric/com.crashlytics.sdk.android.crashlytics-core.properties"] = "Fabric kit version, " + DebloatProperties,
            ["fabric/com.crashlytics.sdk.android.crashlytics-ndk.properties"] = "Fabric kit version, " + DebloatProperties,
            ["fabric/com.crashlytics.sdk.android.crashlytics.properties"] = "Fabric kit version, " + DebloatProperties,
            ["fabric/io.fabric.sdk.android.fabric.properties"] = "Fabric kit version, " + DebloatProperties,
            ["lib/arm64-v8a/libcrashlytics.so"] = DebloatLibrary,
            ["lib/armeabi-v7a/libcrashlytics.so"] = DebloatLibrary,
        };

        /// <summary>The entry kinds "debloat" drops: lib/&lt;abi&gt;/libcrashlytics.so, fabric/*.properties, assets/crashlytics-build.properties.</summary>
        internal static bool IsDebloatEntry(string name)
        {
            if (name == "assets/crashlytics-build.properties") return true;
            if (name.StartsWith("fabric/", StringComparison.Ordinal))
                return name.IndexOf('/', "fabric/".Length) < 0 && name.EndsWith(".properties", StringComparison.Ordinal);
            string abi = AbiOf(name);
            return abi != null && name == "lib/" + abi + "/libcrashlytics.so";
        }

        /// <summary>
        /// "debloat" drops files by kind only from the APK it was verified on: the entries of those kinds must be exactly
        /// <see cref="DebloatDroppedEntries"/> (another set means another build), otherwise nothing is written.
        /// </summary>
        private static void CheckDebloatEntries(ZipArchiveReader.Archive input)
        {
            var present = new SortedSet<string>(input.Entries.Select(e => e.Name).Where(IsDebloatEntry), StringComparer.Ordinal);
            var missing = DebloatDroppedEntries.Keys.Where(k => !present.Contains(k)).ToList();
            var unexpected = present.Where(p => !DebloatDroppedEntries.ContainsKey(p)).ToList();
            if (missing.Count == 0 && unexpected.Count == 0) return;
            var problems = new List<string>();
            if (missing.Count > 0) problems.Add("missing " + string.Join(", ", missing));
            if (unexpected.Count > 0) problems.Add("unexpected " + string.Join(", ", unexpected));
            throw new InvalidDataException("the APK's Crashlytics/Fabric files are not the verified " + KnownBuild.Version + " set, nothing was written: " +
                                           string.Join("; ", problems) + " (leave '" + ManifestDebloat.PatchId + "' out of --patch)");
        }

        /// <summary>Every entry we did not mean to change must come out with the same method, CRC and stored bytes.</summary>
        private static void VerifyUnchanged(ZipArchiveReader.Archive input, ZipArchiveReader.Archive output, IEnumerable<string> modifiedEntries, PatchReport report)
        {
            var modifiedNames = new HashSet<string>(modifiedEntries, StringComparer.Ordinal);
            var removed = new HashSet<string>(report.Removed.Select(r => r.Split(' ')[0]), StringComparer.Ordinal);
            foreach (ZipEntry e in input.Entries)
            {
                if (removed.Contains(e.Name)) continue;
                ZipEntry o = output.Find(e.Name) ?? throw new InvalidOperationException("entry lost: " + e.Name);
                if (modifiedNames.Contains(e.Name)) continue;
                if (o.Method != e.Method || o.Crc32 != e.Crc32 || !o.RawData.AsSpan().SequenceEqual(e.RawData))
                    throw new InvalidOperationException("entry changed unexpectedly: " + e.Name);
            }
            foreach (string pinned in KnownBuild.TypemapPinned.Keys)
            {
                ZipEntry e = input.Find(KnownBuild.AssembliesDir + pinned);
                if (e != null && modifiedNames.Contains(e.Name)) throw new InvalidOperationException("pinned assembly modified: " + pinned);
            }
            foreach (ZipEntry e in output.Entries.Where(x => x.Method == ZipEntry.MethodStored))
            {
                if (e.Name.StartsWith(KnownBuild.AssembliesDir, StringComparison.Ordinal) && e.RawData.Length > 0 && e.RawData[0] != (byte)'M')
                    throw new InvalidOperationException(e.Name + " is not a plain PE file");
            }
        }

        /// <summary>
        /// Kept AOT images of assemblies that reference a modified one go stale (their recorded dependency MVID no longer
        /// matches) and Mono JITs those assemblies instead: harmless, a little slower to start (verify-C7 §3).
        /// </summary>
        private static IEnumerable<string> StaleAotNotes(ZipArchiveReader.Archive input, List<ZipEntry> output, IEnumerable<string> modified)
        {
            var modifiedNames = new HashSet<string>(modified.Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
            var kept = new HashSet<string>(output.Where(e => IsAotImage(e.Name)).Select(e => e.Name.Substring(e.Name.LastIndexOf("/libaot-", StringComparison.Ordinal) + 8)),
                                           StringComparer.Ordinal);
            var notes = new List<string>();
            foreach (ZipEntry e in input.Entries.Where(e => IsAssembly(e.Name)))
            {
                string file = Path.GetFileName(e.Name);
                if (!kept.Contains(file + ".so") || modifiedNames.Contains(Path.GetFileNameWithoutExtension(file))) continue;
                var dependsOn = ReferencedAssemblies(e.GetData()).Where(modifiedNames.Contains).ToList();
                if (dependsOn.Count > 0)
                {
                    notes.Add("AOT image of " + file + " references modified " + string.Join(", ", dependsOn) +
                              ": Mono will reject it and JIT " + file + " (harmless, verify-C7 §3)");
                }
            }
            return notes;
        }

        private static void CheckBuildIdentity(ZipArchiveReader.Archive input, PatcherOptions options, PatchReport report, Action<string> log)
        {
            var problems = new List<string>();
            foreach (var expected in KnownBuild.TypemapPinned.Concat(KnownBuild.PatchedOriginals))
            {
                ZipEntry e = input.Find(KnownBuild.AssembliesDir + expected.Key);
                if (e == null)
                {
                    problems.Add(expected.Key + " missing");
                    continue;
                }
                Guid mvid = ReadMvid(e.GetData());
                if (mvid != expected.Value) problems.Add(expected.Key + " MVID " + mvid + " (expected " + expected.Value + ")");
            }
            if (problems.Count == 0)
            {
                log("recognised Modern Combat Versus " + KnownBuild.Version);
                return;
            }
            string message = "this APK is not the Modern Combat Versus " + KnownBuild.Version + " build the patches were verified against: " +
                             string.Join("; ", problems);
            if (!options.Force) throw new InvalidDataException(message + " (use --force to try anyway)");
            report.Warnings.Add(message);
        }

        private static void CheckServerAssembly(byte[] bytes, string path)
        {
            using (var pe = new PEReader(new MemoryStream(bytes)))
            {
                if (!pe.HasMetadata) throw new InvalidDataException(path + " is not a .NET assembly");
                MetadataReader md = pe.GetMetadataReader();
                string name = md.GetString(md.GetAssemblyDefinition().Name);
                if (name != "McvsOffline.Server") throw new InvalidDataException(path + " is " + name + ", expected McvsOffline.Server");
                foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
                {
                    AssemblyReference r = md.GetAssemblyReference(h);
                    string refName = md.GetString(r.Name);
                    if (!KnownBuild.ServerAllowedReferences.Contains(refName))
                    {
                        throw new InvalidDataException(path + " references " + refName + ": that is not the embedded build. Build " +
                                                       "server/McvsOffline.Server.Embedded.csproj against your APK's assemblies (scripts/build.sh).");
                    }
                    if (refName == "mscorlib" && r.Version != KnownBuild.MonoProfileVersion)
                        throw new InvalidDataException(path + " references mscorlib " + r.Version + ", expected the game's " + KnownBuild.MonoProfileVersion);
                }
            }
        }

        private static void Sign(PatcherOptions options, List<ZipEntry> entries, byte[] unsigned, PatchReport report, Action<string> log)
        {
            BuildTools tools = BuildTools.Locate(options.BuildToolsDir);
            string outputPath = Path.GetFullPath(options.OutputApk);
            string outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

            // No --keystore: one key generated once in the user's profile. A --keystore that does not exist yet is
            // created there, so a player can choose where the key lives.
            string keystore = string.IsNullOrEmpty(options.Keystore) ? SigningKey.DefaultPath() : options.Keystore;
            string password = options.KeystorePassword ?? SigningKey.DefaultPassword;
            SigningKey key;
            if (File.Exists(keystore))
            {
                key = SigningKey.Load(keystore, password);
            }
            else
            {
                log("creating your signing key " + keystore + " (keep it: updates must be signed with the same key)");
                key = SigningKey.Generate(keystore, password);
                report.KeystoreCreated = true;
            }
            report.KeystorePath = keystore;
            bool apksignerUsable = true;

            using (key)
            {
                bool useApkSigner = options.Signer == SignerChoice.ApkSigner ||
                                    (options.Signer == SignerChoice.Auto && tools.ApkSigner != null) ||
                                    key.Certificate == null;
                if (useApkSigner && tools.ApkSigner == null)
                {
                    throw new InvalidOperationException(key.Certificate == null
                        ? keystore + " is not a PKCS#12 file (or the password is wrong); only apksigner can use it, and it was not found"
                        : "apksigner not found: install Android build-tools, pass --build-tools <dir>, or use --signer builtin");
                }

                string temp = outputPath + ".unsigned.tmp";
                if (File.Exists(outputPath)) File.Delete(outputPath);
                try
                {
                    if (useApkSigner)
                    {
                        File.WriteAllBytes(temp, unsigned);
                        if (tools.ZipAlign != null) CheckAlignment(tools.ZipAlign, temp, "before signing");
                        log("signing with " + tools.ApkSigner + " (v1+v2+v3)");
                        try
                        {
                            ExternalApkSigner.Sign(tools.ApkSigner, temp, outputPath, key, options.KeyAlias);
                            report.Signer = "apksigner (v1+v2+v3)";
                        }
                        catch (Exception ex) when (options.Signer == SignerChoice.Auto && key.Certificate != null &&
                                                   (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception))
                        {
                            // e.g. no Java on PATH: the built-in signer needs nothing else.
                            report.Warnings.Add("apksigner failed, used the built-in signer instead: " + ex.Message.Split('\n')[0]);
                            apksignerUsable = false;
                        }
                    }
                    if (report.Signer == null)
                    {
                        log("signing with the built-in signer (v1+v2)");
                        File.WriteAllBytes(outputPath, BuiltInSigner.Sign(entries, key.Certificate));
                        report.Signer = "built-in (v1+v2)";
                    }
                }
                finally
                {
                    try { File.Delete(temp); } catch (Exception) { }
                }
            }

            var verification = new StringBuilder();
            if (tools.ZipAlign != null)
            {
                CheckAlignment(tools.ZipAlign, outputPath, "after signing");
                verification.AppendLine("zipalign -c -p 4: OK");
            }
            if (tools.ApkSigner != null && apksignerUsable)
            {
                string text = ExternalApkSigner.Verify(tools.ApkSigner, outputPath);
                verification.AppendLine("apksigner verify: OK");
                if (options.Verbose) verification.AppendLine(text);
            }
            if (verification.Length == 0) verification.AppendLine("not verified (apksigner/zipalign not found)");
            report.Verification = verification.ToString().TrimEnd();
        }

        private static void CheckAlignment(string zipalign, string apk, string when)
        {
            int code = BuildTools.Run(zipalign, new[] { "-c", "-p", "4", apk }, null, out string text);
            if (code != 0) throw new InvalidOperationException("zipalign -c -p 4 failed " + when + ":\n" + text);
        }

        internal static bool IsAssembly(string name)
        {
            return name.StartsWith(KnownBuild.AssembliesDir, StringComparison.Ordinal) && name.IndexOf('/', KnownBuild.AssembliesDir.Length) < 0 &&
                   name.EndsWith(".dll", StringComparison.Ordinal);
        }

        internal static bool IsAotImage(string name)
        {
            if (!name.StartsWith("lib/", StringComparison.Ordinal)) return false;
            string file = name.Substring(name.LastIndexOf('/') + 1);
            return file.StartsWith("libaot-", StringComparison.Ordinal) && file.EndsWith(".dll.so", StringComparison.Ordinal);
        }

        internal static Guid ReadMvid(byte[] assembly)
        {
            using (var pe = new PEReader(new MemoryStream(assembly)))
            {
                MetadataReader md = pe.GetMetadataReader();
                return md.GetGuid(md.GetModuleDefinition().Mvid);
            }
        }

        private static IEnumerable<string> ReferencedAssemblies(byte[] assembly)
        {
            using (var pe = new PEReader(new MemoryStream(assembly)))
            {
                MetadataReader md = pe.GetMetadataReader();
                return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
            }
        }
    }
}
