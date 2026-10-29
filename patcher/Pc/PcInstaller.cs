using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>Exit codes of the PC installer, the patcher's <c>pc</c> verb and the kit (docs/design/pc-port.md §7.6).</summary>
    public static class PcExitCodes
    {
        public const int Ok = 0;
        public const int Io = 1;
        public const int Usage = 2;
        /// <summary>Unknown build, site mismatch, cannot resolve, a foreign config or a *_args.txt: nothing written.</summary>
        public const int Refused = 3;
        public const int GameRunning = 4;
    }

    /// <summary>What <see cref="PcInstaller.Install"/> works with (§7.6).</summary>
    public sealed class PcInstallOptions
    {
        public PcInstallOptions()
        {
            Port = 51780;
            Files = new PcFileOps();
            InstallerName = "McvsOffline.Patcher";
        }

        /// <summary>The Steam install folder (ModernCombatVersus.exe, the four DLLs, Data\MCVS.game).</summary>
        public string GameFolder { get; set; }

        /// <summary>The PC McvsOffline.Server.dll (the kit's embedded copy, or --server).</summary>
        public byte[] ServerDll { get; set; }

        /// <summary>
        /// The folder that holds <see cref="ServerDll"/> as McvsOffline.Server.dll, for the resolver (the kit's
        /// <c>%TEMP%\mcvs-pckit-&lt;guid&gt;\</c>); null: the installer writes it to a temporary folder of its own and deletes it.
        /// </summary>
        public string ServerFolder { get; set; }

        /// <summary>The .NET Framework 4 BCL directory for <see cref="PcAssemblyResolver"/> (mandatory).</summary>
        public string BclDirectory { get; set; }

        /// <summary>The server port written to install.json (the bootstrap reads settings.json "port").</summary>
        public int Port { get; set; }

        /// <summary>Optional patch ids on top of the mandatory ones (null: the build's defaults).</summary>
        public ICollection<string> OptionalPatches { get; set; }

        /// <summary>
        /// The exact patches to apply instead of <c>PatchPlan.For(PcSteam6372049, OptionalPatches)</c> (tests: a plan of the
        /// patches that exist so far). Each must edit one of <see cref="GameFolder.PatchedFiles"/>.
        /// </summary>
        public IList<IlPatch> Patches { get; set; }

        /// <summary>Install on a build-identity mismatch with a warning (the site fingerprints still protect).</summary>
        public bool Force { get; set; }

        /// <summary>Verify every site and report, write nothing.</summary>
        public bool DryRun { get; set; }

        /// <summary>Whether ModernCombatVersus is running (null: Process.GetProcessesByName).</summary>
        public Func<bool> GameRunning { get; set; }

        public IPcFileOps Files { get; set; }

        public Action<string> Log { get; set; }

        /// <summary>install.json "installer.name": "McvsOffline.PcKit" or "McvsOffline.Patcher".</summary>
        public string InstallerName { get; set; }

        /// <summary>install.json "installer.version" (null: this assembly's informational version).</summary>
        public string InstallerVersion { get; set; }

        /// <summary>install.json "installer.writtenUtc" (null: now).</summary>
        public Func<DateTime> UtcNow { get; set; }
    }

    /// <summary>What <see cref="PcInstaller.Uninstall"/> works with (§7.6).</summary>
    public sealed class PcUninstallOptions
    {
        public PcUninstallOptions()
        {
            Files = new PcFileOps();
        }

        public string GameFolder { get; set; }

        /// <summary>--remove-data: also delete McvsOffline\ (saves, logs) after the confirmation.</summary>
        public bool RemoveData { get; set; }

        public Func<bool> GameRunning { get; set; }

        public IPcFileOps Files { get; set; }

        public Action<string> Log { get; set; }
    }

    /// <summary>The install state <see cref="PcInstaller.Status"/> reports (§7.6).</summary>
    public enum PcInstallState
    {
        NotInstalled,
        Current,
        Stale,
        Damaged,
        Partial,
    }

    /// <summary>The result of an install, uninstall or verify run.</summary>
    public sealed class PcInstallReport
    {
        public PcInstallReport()
        {
            Written = new List<string>();
            WrittenSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
            Removed = new List<string>();
            LeftBehind = new List<string>();
            Notes = new List<string>();
            Warnings = new List<string>();
            Applied = new List<string>();
        }

        /// <summary>One of <see cref="PcExitCodes"/>.</summary>
        public int ExitCode { get; set; }

        /// <summary>One sentence for the player when <see cref="ExitCode"/> is not Ok.</summary>
        public string Error { get; set; }

        /// <summary>Paths relative to the game folder with '/', in the order written (§7.4).</summary>
        public List<string> Written { get; private set; }

        /// <summary>Path → lower-case SHA-256 of what was written (or would be, in a dry run; verify: what is there now).</summary>
        public Dictionary<string, string> WrittenSha256 { get; private set; }

        public List<string> Removed { get; private set; }

        /// <summary>Things uninstall could not remove ("quit the game and run uninstall again").</summary>
        public List<string> LeftBehind { get; private set; }

        /// <summary>PatchContext.Notes and other information for the player.</summary>
        public List<string> Notes { get; private set; }

        public List<string> Warnings { get; private set; }

        /// <summary>Install: the patch ids applied (or verified, in a dry run), in the plan's order.</summary>
        public List<string> Applied { get; private set; }

        /// <summary>Verify: the status it started from.</summary>
        public PcStatusReport Status { get; set; }
    }

    /// <summary>What <see cref="PcInstaller.Status"/> found in the game folder (§7.6).</summary>
    public sealed class PcStatusReport
    {
        public PcStatusReport()
        {
            UnknownFiles = new List<string>();
            Leftovers = new List<string>();
            Errors = new List<string>();
            GameWritten = new List<string>();
            OtherFiles = new List<string>();
            Identity = new List<PcFileIdentity>();
            Problems = new List<string>();
        }

        public string GameFolder { get; set; }

        public PcInstallState State { get; set; }

        /// <summary>One sentence on the state (what is missing or changed, and what to do).</summary>
        public string StateDetail { get; set; }

        /// <summary>The DAMAGED / PARTIAL / STALE details, one line per file.</summary>
        public List<string> Problems { get; private set; }

        /// <summary>Files whose SHA-256 or MVID is not build 6372049's.</summary>
        public List<string> UnknownFiles { get; private set; }

        /// <summary>The build identity, file by file (exe, the four DLLs, the witnesses, Gameloft.Logs).</summary>
        public List<PcFileIdentity> Identity { get; private set; }

        /// <summary>A foreign or spike config, an args.txt block while not installed, *.mcvs-tmp files, the spike's data folder.</summary>
        public List<string> Leftovers { get; private set; }

        /// <summary>E.g. a *_args.txt in the game folder (the engine reads it instead of args.txt).</summary>
        public List<string> Errors { get; private set; }

        /// <summary>Informational: written by the game, not ours (dr0.bin..dr3.bin, OF_PP.bin).</summary>
        public List<string> GameWritten { get; private set; }

        /// <summary>Informational: other top-level entries that are neither the depot's nor ours.</summary>
        public List<string> OtherFiles { get; private set; }

        /// <summary>"absent", "ours", "spike" or "foreign".</summary>
        public string Config { get; set; }

        /// <summary>install.json as read, or null (absent or unreadable: <see cref="ManifestError"/>).</summary>
        public InstallManifest Manifest { get; set; }

        public string ManifestError { get; set; }

        public bool GameRunning { get; set; }

        /// <summary>The size of McvsOffline\ without bin\ (settings, accounts, profiles, logs, exports, kit).</summary>
        public long DataFolderBytes { get; set; }

        public int? SettingsPort { get; set; }

        public int? InstalledPort { get; set; }

        public string LastBootstrapLine { get; set; }
    }

    /// <summary>
    /// Install / Uninstall / Status / Verify of the offline mode in a Steam install (docs/design/pc-port.md §7.4-§7.6), used by
    /// the patcher's <c>pc</c> verb and the kit. Option A: patched copies of EngineBridge, Game, Gameloft.Engine and
    /// Gameloft.Util plus the PC server DLL in <c>McvsOffline\bin\</c>, <c>install.json</c> beside them, and
    /// <c>ModernCombatVersus.exe.config</c> redirecting the five assemblies there; no Steam file is changed (P1). Everything is
    /// checked and patched in memory before the first byte is written, the files are written in an order that never leaves a
    /// config pointing at a missing file, and every write goes through <c>&lt;name&gt;.mcvs-tmp</c> + rename.
    /// </summary>
    public static class PcInstaller
    {
        public static readonly BuildProfile Build = BuildProfile.PcSteam6372049;

        /// <summary>
        /// The witnesses of §3.9 (patch id → assembly, type, member): a second application is refused on them, the host's probe
        /// reports them, and install/verify re-read them. String literals on purpose (WP4's patch constants and WP2's
        /// GamePatchProbe hold the same names; PcHookContractTests asserts the three copies are equal).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, PcWitness> Witnesses = new Dictionary<string, PcWitness>(StringComparer.Ordinal)
        {
            ["hook-pc"] = new PcWitness("EngineBridge.dll", "EngineBridge.Class1", "McvsOfflineInit"),
            ["quicklaunch"] = new PcWitness("Gameloft.Engine.dll", "Gameloft.Engine.McvsQuickLaunch", null),
            ["headless-diag"] = new PcWitness("Gameloft.Engine.dll", "Gameloft.Engine.McvsWorldDiagnostics", null),
            ["graphics"] = new PcWitness("Gameloft.Engine.dll", "Gameloft.Engine.McvsGraphics", null),
            ["store"] = new PcWitness("Game.dll", "Game.McvsStoreSafety", null),
        };

        public const string StaleAdvice = "Steam updated the game: run uninstall, wait for a kit that knows this build";

        // ================================================================================================ install

        public static PcInstallReport Install(PcInstallOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var report = new PcInstallReport();
            Action<string> log = options.Log ?? (s => { });
            IPcFileOps files = options.Files ?? new PcFileOps();
            if (string.IsNullOrEmpty(options.GameFolder)) return Fail(report, PcExitCodes.Usage, "the game folder is required (--game)");
            if (options.ServerDll == null || options.ServerDll.Length == 0) return Fail(report, PcExitCodes.Usage, "the PC McvsOffline.Server.dll is required (--server)");
            if (options.Port < 1 || options.Port > 65535) return Fail(report, PcExitCodes.Usage, "the port must be between 1 and 65535");
            string game = Path.GetFullPath(options.GameFolder);

            // 1. The folder, the running game, a *_args.txt.
            string missing = GameFolder.MissingGameFiles(game);
            if (missing != null) return Fail(report, PcExitCodes.Refused, missing);
            if ((options.GameRunning ?? GameFolder.IsGameRunning)())
                return Fail(report, PcExitCodes.GameRunning, "the game is running: quit it (wait until ModernCombatVersus.exe is gone from the Task Manager), then run install again");
            List<string> prefixed = GameFolder.PrefixedArgsFiles(game);
            if (prefixed.Count > 0) return Fail(report, PcExitCodes.Refused, PrefixedArgsMessage(prefixed[0]) + ". Move it away and run install again");

            // 2. Build identity.
            List<PcFileIdentity> identity = GameFolder.Identity(game, Build);
            List<PcFileIdentity> unknown = identity.Where(i => !i.Known).ToList();
            if (unknown.Count > 0)
            {
                string what = string.Join("; ", unknown.Select(u => u.ToString()));
                if (!options.Force)
                {
                    return Fail(report, PcExitCodes.Refused, "this is not the verified Steam build 6372049 (" + what + "). Nothing was written; " +
                                                             "--force installs anyway (every patch site is still checked)");
                }
                report.Warnings.Add("not the verified build 6372049, installing anyway (--force): " + what);
            }

            // 3. An existing config: ours or the spike's are replaced, anything else is refused (never merged).
            string configPath = Path.Combine(game, PcExeConfig.FileName);
            string configKind = ConfigKind(configPath);
            if (configKind == "foreign")
                return Fail(report, PcExitCodes.Refused, "a " + PcExeConfig.FileName + " that is not ours exists; move it away and run install again");
            if (configKind == "spike") report.Notes.Add(PcExeConfig.FileName + " is the WP0 spike's redirect test: replaced by ours");

            // 4. The server DLL.
            try
            {
                ServerAssemblyCheck.Check(options.ServerDll, GameFolder.ServerFile, Build);
            }
            catch (InvalidDataException ex)
            {
                return Fail(report, PcExitCodes.Refused, ex.Message);
            }
            catch (BadImageFormatException ex)
            {
                return Fail(report, PcExitCodes.Refused, GameFolder.ServerFile + " is not a .NET assembly: " + ex.Message);
            }

            // 5. The plan, patched in memory before anything is written.
            IList<IlPatch> plan;
            try
            {
                IEnumerable<string> optional = options.OptionalPatches ?? (IEnumerable<string>)PatchPlan.Ids(Build).Default;
                plan = options.Patches ?? PatchPlan.For(Build, optional).ToList();
            }
            catch (ArgumentException ex)
            {
                return Fail(report, PcExitCodes.Usage, ex.Message);
            }
            IlPatch stray = plan.FirstOrDefault(p => !GameFolder.PatchedFiles.Contains(p.AssemblyFile));
            if (stray != null) return Fail(report, PcExitCodes.Refused, "patch " + stray.Id + " edits " + stray.AssemblyFile + ", which the PC install does not redirect");
            if (string.IsNullOrEmpty(options.BclDirectory))
                return Fail(report, PcExitCodes.Refused, "cannot patch: no .NET Framework 4 folder (--bcl) to read the game's assemblies with");

            var outputs = new List<Output>();
            var notes = new List<string>();
            string ownTemp = null;
            try
            {
                string serverFolder = options.ServerFolder;
                if (string.IsNullOrEmpty(serverFolder))
                {
                    ownTemp = Path.Combine(Path.GetTempPath(), "mcvs-pcinstall-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(ownTemp);
                    File.WriteAllBytes(Path.Combine(ownTemp, GameFolder.ServerFile), options.ServerDll);
                    serverFolder = ownTemp;
                }
                using (var resolver = new PcAssemblyResolver(options.BclDirectory, game, serverFolder))
                {
                    ReaderParameters reader = resolver.CreateReaderParameters();
                    using (ModuleDefinition server = ModuleDefinition.ReadModule(new MemoryStream(options.ServerDll), reader))
                    {
                        var context = new PatchContext(server, options.Port, Build);
                        Version serverVersion = server.Assembly.Name.Version;
                        foreach (string file in GameFolder.PatchedFiles)
                        {
                            List<IlPatch> group = plan.Where(p => p.AssemblyFile == file).ToList();
                            byte[] original = File.ReadAllBytes(Path.Combine(game, file));
                            byte[] bytes = original;
                            if (group.Count > 0)
                            {
                                bytes = AssemblyPatcher.Patch(original, group, context, reader, server.Mvid, log);
                                ServerAssemblyCheck.CheckPatchedReferences(file, original, bytes, serverVersion, Build);
                            }
                            else
                            {
                                log(file + ": no patch in the plan, the redirected copy is the original");
                            }
                            outputs.Add(new Output(GameFolder.BinRelative(file), bytes, group.Select(p => p.Id).ToList()));
                        }
                        string serverVersionText, serverBuild;
                        ReadServerVersion(server, out serverVersionText, out serverBuild);
                        bool gameLog = server.AssemblyReferences.Any(r => r.Name == "Gameloft.Logs");
                        outputs.Add(new Output(GameFolder.BinRelative(GameFolder.ServerFile), options.ServerDll, null)
                        {
                            ServerVersion = serverVersionText, ServerBuild = serverBuild, GameLog = gameLog,
                        });
                        notes.AddRange(context.Notes);
                    }
                }
            }
            catch (PatchException ex)
            {
                return Fail(report, PcExitCodes.Refused, "a patch site does not match the verified game code, nothing was written: " + ex.Message);
            }
            catch (AssemblyResolutionException ex)
            {
                return Fail(report, PcExitCodes.Refused, "cannot patch: " + ex.AssemblyReference.Name + " not found in " + options.BclDirectory + ", " + game +
                                                         " or the server's folder; nothing was written");
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is BadImageFormatException || ex is ArgumentException)
            {
                return Fail(report, PcExitCodes.Refused, "cannot patch, nothing was written: " + ex.Message);
            }
            finally
            {
                if (ownTemp != null)
                {
                    try { Directory.Delete(ownTemp, true); } catch (Exception) { }
                }
            }
            report.Applied.AddRange(plan.Select(p => p.Id));
            report.Notes.AddRange(notes);

            // The config (versions read from the written bytes) and install.json, which records the config's hash.
            var codeBases = outputs.Select(o => new PcCodeBase(GameFolder.ReadAssemblyName(o.Bytes), GameFolder.ReadAssemblyVersion(o.Bytes), o.Path)).ToList();
            byte[] config = PcExeConfig.Encode(PcExeConfig.Build(codeBases));
            InstallManifest manifest = BuildManifest(options, identity, outputs, config, report.Applied, notes);
            byte[] manifestBytes = manifest.ToBytes();
            foreach (Output o in outputs) Record(report, o.Path, o.Bytes);
            Record(report, GameFolder.DataDirName + "/" + InstallManifest.FileName, manifestBytes);
            Record(report, PcExeConfig.FileName, config);

            // 6. Dry run: the report only. Else write (§7.4 order), then read everything back.
            if (options.DryRun)
            {
                log("dry run: every patch site verified, nothing written");
                return report;
            }
            string step = null;
            try
            {
                foreach (Output o in outputs)
                {
                    step = o.Path;
                    WriteViaTemp(files, GameFolder.Resolve(game, o.Path), o.Bytes);
                    log("wrote " + o.Path);
                }
                step = GameFolder.DataDirName + "/" + InstallManifest.FileName;
                WriteViaTemp(files, GameFolder.Resolve(game, step), manifestBytes);
                log("wrote " + step);
                step = PcExeConfig.FileName;
                WriteViaTemp(files, configPath, config);
                log("wrote " + step);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Fail(report, PcExitCodes.Io, "could not write " + step + ": " + ex.Message +
                                                    " (is the game folder writable, and the game closed? 'status' shows what is there; 'uninstall' removes it)");
            }
            if (configKind == "spike") RemoveSpikeCopy(game, files, report, log);

            List<string> problems = CheckWritten(game, manifest, options.BclDirectory);
            if (problems.Count > 0) return Fail(report, PcExitCodes.Io, "the written files do not read back as written: " + string.Join("; ", problems));
            log("read back: every written file, the witnesses and the config are as written");
            return report;
        }

        private sealed class Output
        {
            public Output(string path, byte[] bytes, List<string> patches)
            {
                Path = path;
                Bytes = bytes;
                Patches = patches;
            }

            public string Path { get; }

            public byte[] Bytes { get; }

            /// <summary>Null for the server DLL.</summary>
            public List<string> Patches { get; }

            public string ServerVersion { get; set; }

            public string ServerBuild { get; set; }

            public bool GameLog { get; set; }
        }

        private static InstallManifest BuildManifest(PcInstallOptions options, List<PcFileIdentity> identity, List<Output> outputs, byte[] config,
                                                     List<string> applied, List<string> notes)
        {
            Output serverOutput = outputs.Single(o => o.Patches == null);
            var m = new InstallManifest
            {
                InstallerName = options.InstallerName,
                InstallerVersion = options.InstallerVersion ?? ToolVersion(),
                ServerBuild = serverOutput.ServerBuild,
                WrittenUtc = (options.UtcNow ?? (() => DateTime.UtcNow))().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                BuildId = Build.Id,
                GameVersion = Build.GameVersion,
                ClientId = Build.ClientId,
                ExeSha256 = identity.Single(i => i.File == GameFolder.ExeName).Sha256,
                Port = options.Port,
                Bcl = options.BclDirectory,
            };
            foreach (string file in GameFolder.PatchedFiles)
            {
                PcFileIdentity id = identity.Single(i => i.File == file);
                m.Originals.Add(new InstallOriginal { File = file, Sha256 = id.Sha256, Mvid = id.Mvid ?? Guid.Empty });
            }
            foreach (Output o in outputs)
            {
                var w = new InstallWritten
                {
                    Path = o.Path, Sha256 = GameFolder.Sha256(o.Bytes), Mvid = GameFolder.TryReadMvid(o.Bytes), Version = GameFolder.ReadAssemblyVersion(o.Bytes),
                };
                if (o.Patches != null)
                {
                    w.Patches = o.Patches;
                }
                else
                {
                    w.ServerVersion = o.ServerVersion;
                    w.GameLog = o.GameLog;
                }
                m.Written.Add(w);
            }
            m.Written.Add(new InstallWritten { Path = PcExeConfig.FileName, Sha256 = GameFolder.Sha256(config) });
            m.Patches.AddRange(applied);
            m.Notes.AddRange(notes);
            return m;
        }

        /// <summary>McvsOffline.Server.Core.McvsVersion.Current and .Build of the server DLL (constants), or null.</summary>
        private static void ReadServerVersion(ModuleDefinition server, out string version, out string build)
        {
            version = null;
            build = null;
            TypeDefinition type = server.GetType("McvsOffline.Server.Core.McvsVersion");
            if (type == null) return;
            FieldDefinition current = type.Fields.FirstOrDefault(f => f.Name == "Current" && f.HasConstant);
            FieldDefinition number = type.Fields.FirstOrDefault(f => f.Name == "Build" && f.HasConstant);
            version = current == null ? null : current.Constant as string;
            build = number == null ? null : number.Constant as string;
        }

        /// <summary>This patcher's informational version (Directory.Build.props), without a source-revision suffix.</summary>
        public static string ToolVersion()
        {
            var attribute = typeof(PcInstaller).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = attribute == null ? typeof(PcInstaller).Assembly.GetName().Version.ToString() : attribute.InformationalVersion;
            int plus = version.IndexOf('+');
            return plus < 0 ? version : version.Substring(0, plus);
        }

        private static void Record(PcInstallReport report, string path, byte[] bytes)
        {
            report.Written.Add(path);
            report.WrittenSha256[path] = GameFolder.Sha256(bytes);
        }

        /// <summary>Writes <c>&lt;path&gt;.mcvs-tmp</c>, then renames it over <paramref name="path"/>.</summary>
        private static void WriteViaTemp(IPcFileOps files, string path, byte[] bytes)
        {
            string temp = path + GameFolder.TempSuffix;
            files.WriteAllBytes(temp, bytes);
            files.Rename(temp, path);
        }

        /// <summary>The spike's <c>McvsOffline\spike\</c> goes when it holds only its EngineBridge.dll copy.</summary>
        private static void RemoveSpikeCopy(string game, IPcFileOps files, PcInstallReport report, Action<string> log)
        {
            string spike = GameFolder.Resolve(game, GameFolder.SpikeCopyFolder);
            if (!Directory.Exists(spike)) return;
            string[] entries = Directory.GetFileSystemEntries(spike);
            if (entries.Length > 1 || (entries.Length == 1 && !string.Equals(Path.GetFileName(entries[0]), "EngineBridge.dll", StringComparison.OrdinalIgnoreCase)))
            {
                report.Notes.Add(GameFolder.SpikeCopyFolder.Replace('/', '\\') + " holds more than the spike's EngineBridge.dll: left as it is");
                return;
            }
            try
            {
                files.DeleteDirectory(spike);
                report.Removed.Add(GameFolder.SpikeCopyFolder + "/");
                log("removed the spike's " + GameFolder.SpikeCopyFolder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                report.LeftBehind.Add(GameFolder.SpikeCopyFolder.Replace('/', '\\') + ": " + ex.Message);
            }
        }

        // ================================================================================================ uninstall

        public static PcInstallReport Uninstall(PcUninstallOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var report = new PcInstallReport();
            Action<string> log = options.Log ?? (s => { });
            IPcFileOps files = options.Files ?? new PcFileOps();
            if (string.IsNullOrEmpty(options.GameFolder)) return Fail(report, PcExitCodes.Usage, "the game folder is required (--game)");
            string game = Path.GetFullPath(options.GameFolder);
            if (!Directory.Exists(game)) return Fail(report, PcExitCodes.Refused, "the folder " + game + " does not exist");
            if ((options.GameRunning ?? GameFolder.IsGameRunning)())
                return Fail(report, PcExitCodes.GameRunning, "the game is running: quit it (wait until ModernCombatVersus.exe is gone from the Task Manager), then run uninstall again");

            // The config first: from here on nothing redirects to a file we are about to delete.
            string configPath = Path.Combine(game, PcExeConfig.FileName);
            string kind = ConfigKind(configPath);
            if (kind == "ours" || kind == "spike")
            {
                Delete(files, configPath, PcExeConfig.FileName, report, log);
                if (File.Exists(configPath))
                {
                    // The config still redirects into McvsOffline\bin: deleting what it points at would stop the game from starting.
                    return Fail(report, PcExitCodes.Io, PcExeConfig.FileName + " could not be removed, so nothing else was: " +
                                                        "quit the game, clear its read-only attribute and run uninstall again");
                }
            }
            else if (kind == "foreign") report.Notes.Add(PcExeConfig.FileName + " is not ours: left as it is");

            string bin = GameFolder.BinDir(game);
            if (Directory.Exists(bin))
            {
                foreach (string file in Directory.GetFiles(bin).OrderBy(f => f, StringComparer.Ordinal))
                    Delete(files, file, GameFolder.DataDirName + "/" + GameFolder.BinDirName + "/" + Path.GetFileName(file), report, log);
                if (Directory.GetFileSystemEntries(bin).Length == 0) DeleteDir(files, bin, GameFolder.DataDirName + "/" + GameFolder.BinDirName + "/", report, log);
                else report.LeftBehind.Add(GameFolder.DataDirName + "\\" + GameFolder.BinDirName + "\\ is not empty: quit the game and run uninstall again");
            }
            string spike = GameFolder.Resolve(game, GameFolder.SpikeCopyFolder);
            if (Directory.Exists(spike))
            {
                string[] entries = Directory.GetFileSystemEntries(spike);
                if (entries.All(e => string.Equals(Path.GetFileName(e), "EngineBridge.dll", StringComparison.OrdinalIgnoreCase)))
                    DeleteDir(files, spike, GameFolder.SpikeCopyFolder + "/", report, log);
            }
            string manifest = Path.Combine(GameFolder.DataDir(game), InstallManifest.FileName);
            if (File.Exists(manifest)) Delete(files, manifest, GameFolder.DataDirName + "/" + InstallManifest.FileName, report, log);

            // args.txt: strip the bootstrap's block; delete the file when nothing else is left (the depot has none, F9).
            string args = Path.Combine(game, GameFolder.ArgsFileName);
            if (File.Exists(args))
            {
                try
                {
                    byte[] existing = File.ReadAllBytes(args);
                    if (PcArgsBlock.HasBlock(existing))
                    {
                        bool onlyBlock;
                        byte[] rest = PcArgsBlock.Remove(existing, out onlyBlock);
                        if (onlyBlock)
                        {
                            files.DeleteFile(args);
                            report.Removed.Add(GameFolder.ArgsFileName);
                            log("removed args.txt (it held only our block)");
                        }
                        else
                        {
                            WriteViaTemp(files, args, rest);
                            report.Removed.Add(GameFolder.ArgsFileName + " (our block; your own lines are kept)");
                            log("args.txt: removed our block, kept your own lines");
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    report.LeftBehind.Add("args.txt still has the MCVS Offline block: " + ex.Message);
                }
            }

            // Leftovers of an interrupted install.
            foreach (string folder in new[] { game, GameFolder.DataDir(game) })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string temp in Directory.GetFiles(folder, "*" + GameFolder.TempSuffix).OrderBy(f => f, StringComparer.Ordinal))
                    Delete(files, temp, Relative(game, temp), report, log);
            }

            string data = GameFolder.DataDir(game);
            if (options.RemoveData && Directory.Exists(data))
            {
                DeleteDir(files, data, GameFolder.DataDirName + "/ (saves, settings, logs)", report, log);
            }
            else if (Directory.Exists(data) && HoldsOnlyKitFiles(game))
            {
                // Installed but never started: nothing of the player's is in it (the kit's own logs at most; the kit has moved its log to %TEMP%).
                DeleteDir(files, data, GameFolder.DataDirName + "/ (nothing of yours in it)", report, log);
            }
            else if (Directory.Exists(data))
            {
                report.Notes.Add("your data stays in " + data + " (settings, saves, logs); 'uninstall --remove-data' deletes it");
            }
            if (report.LeftBehind.Count > 0)
            {
                report.ExitCode = PcExitCodes.Io;
                report.Error = "some files could not be removed: quit the game and run uninstall again";
            }
            return report;
        }

        /// <summary>
        /// True when <c>McvsOffline\</c> exists and every file below it is in <c>McvsOffline\kit\</c> (the kit's logs and listings):
        /// an install that was never started, which uninstall removes without --remove-data.
        /// </summary>
        public static bool HoldsOnlyKitFiles(string game)
        {
            string data = GameFolder.DataDir(game);
            if (!Directory.Exists(data)) return false;
            string kit = Path.Combine(data, GameFolder.KitDirName) + Path.DirectorySeparatorChar;
            return AllFilesBelow(data, f => f.StartsWith(kit, StringComparison.Ordinal));
        }

        /// <summary>
        /// True when nothing below <c>McvsOffline\</c> is the player's: only the kit's files and the install's own (bin\, install.json,
        /// the spike's copy, *.mcvs-tmp). After an uninstall such a folder is <see cref="HoldsOnlyKitFiles"/>, and goes.
        /// </summary>
        public static bool HoldsNothingOfThePlayers(string game)
        {
            string data = GameFolder.DataDir(game);
            if (!Directory.Exists(data)) return false;
            string[] ours =
            {
                Path.Combine(data, GameFolder.KitDirName) + Path.DirectorySeparatorChar, GameFolder.BinDir(game) + Path.DirectorySeparatorChar,
                GameFolder.Resolve(game, GameFolder.SpikeCopyFolder) + Path.DirectorySeparatorChar,
            };
            string manifest = Path.Combine(data, InstallManifest.FileName);
            return AllFilesBelow(data, f => ours.Any(o => f.StartsWith(o, StringComparison.Ordinal)) || f.StartsWith(manifest, StringComparison.Ordinal));
        }

        /// <summary>False also when a folder below <paramref name="folder"/> cannot be listed (then it may hold the player's files).</summary>
        private static bool AllFilesBelow(string folder, Func<string, bool> predicate)
        {
            try
            {
                return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).All(predicate);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void Delete(IPcFileOps files, string path, string name, PcInstallReport report, Action<string> log)
        {
            try
            {
                files.DeleteFile(path);
                report.Removed.Add(name);
                log("removed " + name);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                report.LeftBehind.Add(name.Replace('/', '\\') + ": " + ex.Message + " (quit the game and run uninstall again)");
            }
        }

        private static void DeleteDir(IPcFileOps files, string path, string name, PcInstallReport report, Action<string> log)
        {
            try
            {
                files.DeleteDirectory(path);
                report.Removed.Add(name);
                log("removed " + name);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                report.LeftBehind.Add(name.Replace('/', '\\') + ": " + ex.Message);
            }
        }

        // ================================================================================================ status

        public static PcStatusReport Status(string gameFolder, Func<bool> gameRunning)
        {
            return Status(gameFolder, gameRunning, DefaultSpikeDataFolder());
        }

        /// <summary>The WP0 spike's data folder, <c>%LOCALAPPDATA%\McvsOffline\spike</c> (null when there is no such folder location).</summary>
        public static string DefaultSpikeDataFolder()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "McvsOffline", "spike");
        }

        /// <param name="spikeDataFolder">Where to look for the spike's data folder (tests: a temp folder; null: not checked).</param>
        public static PcStatusReport Status(string gameFolder, Func<bool> gameRunning, string spikeDataFolder)
        {
            var r = new PcStatusReport();
            string game = Path.GetFullPath(gameFolder);
            r.GameFolder = game;
            r.GameRunning = (gameRunning ?? GameFolder.IsGameRunning)();
            if (!Directory.Exists(game))
            {
                r.Errors.Add("the folder " + game + " does not exist");
                r.State = PcInstallState.NotInstalled;
                r.Config = "absent";
                return r;
            }
            r.Identity.AddRange(GameFolder.Identity(game, Build));
            r.UnknownFiles.AddRange(r.Identity.Where(i => !i.Known).Select(i => i.ToString()));
            foreach (string name in GameFolder.PrefixedArgsFiles(game)) r.Errors.Add(PrefixedArgsMessage(name) + "; move it away");

            string configPath = Path.Combine(game, PcExeConfig.FileName);
            r.Config = ConfigKind(configPath);
            string manifestPath = Path.Combine(GameFolder.DataDir(game), InstallManifest.FileName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    r.Manifest = InstallManifest.Parse(ReadShared(manifestPath));
                    r.InstalledPort = r.Manifest.Port;
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    r.ManifestError = ex.Message;
                }
            }
            string bin = GameFolder.BinDir(game);
            List<string> binFiles = GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile })
                                              .Where(f => File.Exists(Path.Combine(bin, f))).ToList();
            bool anything = r.Config == "ours" || File.Exists(manifestPath) || binFiles.Count > 0;
            if (!anything)
            {
                r.State = PcInstallState.NotInstalled;
                r.StateDetail = "not installed";
            }
            else if (r.Manifest == null || r.Config != "ours")
            {
                r.State = PcInstallState.Partial;
                if (r.Manifest == null) r.Problems.Add(InstallManifest.FileName + (r.ManifestError != null ? " unreadable: " + r.ManifestError : " missing"));
                if (r.Config != "ours") r.Problems.Add(PcExeConfig.FileName + (r.Config == "absent" ? " missing" : " is not ours"));
                foreach (string f in GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }).Where(f => !binFiles.Contains(f)))
                    r.Problems.Add(GameFolder.BinRelative(f) + " missing");
                r.StateDetail = "PARTIAL: an install was interrupted (" + string.Join("; ", r.Problems) + "): run install again, or uninstall";
            }
            else
            {
                foreach (string expected in ExpectedWritten())
                {
                    if (r.Manifest.FindWritten(expected) == null) r.Problems.Add(expected + " is not in " + InstallManifest.FileName);
                }
                foreach (InstallWritten w in r.Manifest.Written)
                {
                    string path = GameFolder.Resolve(game, w.Path);
                    if (!File.Exists(path)) r.Problems.Add(w.Path + " missing");
                    else if (!string.Equals(GameFolder.TrySha256(path), w.Sha256, StringComparison.Ordinal)) r.Problems.Add(w.Path + " changed");
                }
                if (r.Problems.Count > 0)
                {
                    r.State = PcInstallState.Damaged;
                    r.StateDetail = "DAMAGED: " + string.Join("; ", r.Problems) + ": run install again";
                }
                else
                {
                    var stale = new List<string>();
                    foreach (InstallOriginal o in r.Manifest.Originals)
                    {
                        string now = GameFolder.TrySha256(Path.Combine(game, o.File));
                        if (!string.Equals(now, o.Sha256, StringComparison.Ordinal)) stale.Add(o.File);
                    }
                    if (r.Manifest.ExeSha256 != null &&
                        !string.Equals(GameFolder.TrySha256(Path.Combine(game, GameFolder.ExeName)), r.Manifest.ExeSha256, StringComparison.Ordinal))
                        stale.Add(GameFolder.ExeName);
                    if (stale.Count > 0)
                    {
                        r.State = PcInstallState.Stale;
                        r.Problems.AddRange(stale.Select(s => s + " differs from the one the install was made from"));
                        r.StateDetail = "STALE (" + string.Join(", ", stale) + " changed): " + StaleAdvice;
                    }
                    else
                    {
                        r.State = PcInstallState.Current;
                        r.StateDetail = "installed, current";
                    }
                }
            }

            // Leftovers.
            if (r.Config == "foreign") r.Leftovers.Add(PcExeConfig.FileName + " is not ours (install refuses to replace it; move it away first)");
            if (r.Config == "spike") r.Leftovers.Add(PcExeConfig.FileName + " is the WP0 spike's redirect test (install replaces it, uninstall removes it)");
            string args = Path.Combine(game, GameFolder.ArgsFileName);
            if (r.State == PcInstallState.NotInstalled && File.Exists(args))
            {
                byte[] bytes = ReadSharedBytes(args);
                if (bytes != null && PcArgsBlock.HasBlock(bytes))
                    r.Leftovers.Add("args.txt still has the MCVS Offline block while nothing is installed: run uninstall");
            }
            foreach (string folder in new[] { game, GameFolder.DataDir(game), bin })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string temp in Directory.GetFiles(folder, "*" + GameFolder.TempSuffix).OrderBy(f => f, StringComparer.Ordinal))
                    r.Leftovers.Add(Relative(game, temp) + " (left by an interrupted install; install overwrites it, uninstall deletes it)");
            }
            if (Directory.Exists(GameFolder.Resolve(game, GameFolder.SpikeCopyFolder)))
                r.Leftovers.Add(GameFolder.SpikeCopyFolder.Replace('/', '\\') + "\\ (the WP0 spike's copy folder)");
            if (!string.IsNullOrEmpty(spikeDataFolder) && Directory.Exists(spikeDataFolder))
                r.Leftovers.Add("the WP0 spike's data folder " + spikeDataFolder + " (delete it by hand once you no longer need its logs)");

            foreach (string entry in Directory.EnumerateFileSystemEntries(game).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                GameFolderOwner owner = GameFolder.Classify(entry);
                if (owner == GameFolderOwner.Game) r.GameWritten.Add(entry);
                else if (owner == GameFolderOwner.Other && !entry.EndsWith("_args.txt", StringComparison.OrdinalIgnoreCase)) r.OtherFiles.Add(entry);
            }

            string data = GameFolder.DataDir(game);
            if (Directory.Exists(data))
            {
                foreach (FileInfo f in new DirectoryInfo(data).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (f.FullName.StartsWith(bin + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                    r.DataFolderBytes += f.Length;
                }
                r.SettingsPort = SettingsPort(Path.Combine(data, "settings.json"));
                r.LastBootstrapLine = LastLine(Path.Combine(data, "logs", "bootstrap.log"));
            }
            return r;
        }

        /// <summary>The paths install.json must list: the five bin files and the config.</summary>
        private static IEnumerable<string> ExpectedWritten()
        {
            return GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }).Select(GameFolder.BinRelative).Concat(new[] { PcExeConfig.FileName });
        }

        // ================================================================================================ verify

        /// <summary>Status plus the re-read of every written file; the report carries the written SHA-256 values (WR2 step 3).</summary>
        public static PcInstallReport Verify(string gameFolder, string bclDirectory, Func<bool> gameRunning)
        {
            var report = new PcInstallReport();
            PcStatusReport status = Status(gameFolder, gameRunning);
            report.Status = status;
            if (status.Manifest != null)
            {
                foreach (InstallWritten w in status.Manifest.Written)
                {
                    report.Written.Add(w.Path);
                    report.WrittenSha256[w.Path] = GameFolder.TrySha256(GameFolder.Resolve(status.GameFolder, w.Path)) ?? "missing";
                }
            }
            if (status.State == PcInstallState.NotInstalled) return Fail(report, PcExitCodes.Refused, "MCVS Offline is not installed in " + status.GameFolder);
            if (status.State != PcInstallState.Current) return Fail(report, PcExitCodes.Io, status.StateDetail);
            List<string> problems = CheckWritten(status.GameFolder, status.Manifest, bclDirectory);
            if (problems.Count > 0) return Fail(report, PcExitCodes.Io, "the install does not read back as written: " + string.Join("; ", problems));
            foreach (string error in status.Errors) report.Warnings.Add(error);
            return report;
        }

        /// <summary>
        /// The re-read of install step 6 and verify: every written file has the recorded SHA-256 and (assemblies) the recorded
        /// MVID read with Cecil, the witness of every applied patch that has one is present, and the config parses back to the
        /// five codeBases whose hrefs exist.
        /// </summary>
        public static List<string> CheckWritten(string game, InstallManifest manifest, string bclDirectory)
        {
            var problems = new List<string>();
            PcAssemblyResolver resolver = null;
            try
            {
                if (!string.IsNullOrEmpty(bclDirectory) && File.Exists(Path.Combine(bclDirectory, "mscorlib.dll")))
                    resolver = new PcAssemblyResolver(bclDirectory, GameFolder.BinDir(game), game);
                foreach (InstallWritten w in manifest.Written)
                {
                    string path = GameFolder.Resolve(game, w.Path);
                    if (!File.Exists(path))
                    {
                        problems.Add(w.Path + " missing");
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(path);
                    if (GameFolder.Sha256(bytes) != w.Sha256) problems.Add(w.Path + ": SHA-256 " + GameFolder.Sha256(bytes) + ", recorded " + w.Sha256);
                    if (!w.Mvid.HasValue) continue;
                    ReaderParameters reader = resolver != null ? resolver.CreateReaderParameters() : new ReaderParameters { ReadingMode = ReadingMode.Deferred };
                    try
                    {
                        using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(bytes), reader))
                        {
                            if (module.Mvid != w.Mvid.Value) problems.Add(w.Path + ": MVID " + module.Mvid + ", recorded " + w.Mvid.Value);
                            foreach (string id in w.Patches ?? new List<string>())
                            {
                                PcWitness witness;
                                if (!Witnesses.TryGetValue(id, out witness)) continue;
                                if (!witness.IsIn(module)) problems.Add(w.Path + ": the witness of '" + id + "' (" + witness + ") is missing");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is BadImageFormatException || ex is AssemblyResolutionException || ex is InvalidOperationException)
                    {
                        problems.Add(w.Path + ": cannot be read as an assembly: " + ex.Message);
                    }
                }
                string config = Path.Combine(game, PcExeConfig.FileName);
                if (File.Exists(config))
                {
                    try
                    {
                        List<PcCodeBase> codeBases = PcExeConfig.Parse(File.ReadAllText(config));
                        var expected = GameFolder.PatchedFiles.Concat(new[] { GameFolder.ServerFile }).Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
                        if (codeBases.Count != expected.Count || !expected.All(e => codeBases.Any(c => c.AssemblyName == e)))
                            problems.Add(PcExeConfig.FileName + ": expected the five codeBases " + string.Join(", ", expected) + ", found " +
                                         string.Join(", ", codeBases.Select(c => c.AssemblyName)));
                        foreach (PcCodeBase c in codeBases)
                        {
                            try
                            {
                                PcExeConfig.CheckHref(c.Href);
                                if (!File.Exists(GameFolder.Resolve(game, c.Href))) problems.Add(PcExeConfig.FileName + ": " + c.Href + " does not exist");
                            }
                            catch (ArgumentException ex)
                            {
                                problems.Add(PcExeConfig.FileName + ": " + ex.Message);
                            }
                        }
                    }
                    catch (FormatException ex)
                    {
                        problems.Add(PcExeConfig.FileName + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                if (resolver != null) resolver.Dispose();
            }
            return problems;
        }

        // ================================================================================================ helpers

        /// <summary>"absent", "ours", "spike" or "foreign" (an unreadable file counts as foreign: it is never touched).</summary>
        public static string ConfigKind(string configPath)
        {
            if (!File.Exists(configPath)) return "absent";
            string text = ReadShared(configPath);
            if (PcExeConfig.IsOurs(text)) return "ours";
            if (PcExeConfig.IsSpikeConfig(text)) return "spike";
            return "foreign";
        }

        public static string PrefixedArgsMessage(string name)
        {
            return name + " is in the game folder; the game reads it instead of args.txt, so MCVS Offline would be ignored and the game would go online";
        }

        private static PcInstallReport Fail(PcInstallReport report, int code, string error)
        {
            report.ExitCode = code;
            report.Error = error;
            return report;
        }

        private static string Relative(string game, string path)
        {
            string relative = Path.GetRelativePath(game, path);
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>The text of a file another process may have open; null when it cannot be read.</summary>
        private static string ReadShared(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static byte[] ReadSharedBytes(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static int? SettingsPort(string settings)
        {
            string text = ReadShared(settings);
            if (text == null) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                {
                    JsonElement port;
                    int value;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("port", out port) &&
                        port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out value))
                        return value;
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        private static string LastLine(string file)
        {
            string text = ReadShared(file);
            if (text == null) return null;
            string[] lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Trim().Length > 0) return line;
            }
            return null;
        }
    }

    /// <summary>A patch's witness (§3.9): a type the patch adds to the assembly, or a member of an existing type.</summary>
    public sealed class PcWitness
    {
        public PcWitness(string assemblyFile, string typeName, string memberName)
        {
            AssemblyFile = assemblyFile;
            TypeName = typeName;
            MemberName = memberName;
        }

        public string AssemblyFile { get; }

        /// <summary>The full type name ("Game.McvsStoreSafety", "EngineBridge.Class1").</summary>
        public string TypeName { get; }

        /// <summary>A method of <see cref="TypeName"/> (hook-pc: "McvsOfflineInit"), or null when the type itself is the witness.</summary>
        public string MemberName { get; }

        public bool IsIn(ModuleDefinition module)
        {
            TypeDefinition type = module.GetType(TypeName);
            if (type == null) return false;
            return MemberName == null || type.Methods.Any(m => m.Name == MemberName);
        }

        public override string ToString()
        {
            return MemberName == null ? TypeName : TypeName + "::" + MemberName;
        }
    }
}
