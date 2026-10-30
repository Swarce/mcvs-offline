// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Storage;

namespace McvsOffline.Embedded
{
    /// <summary>
    /// What the patcher did to the client this process runs, read from the loaded <c>Game</c> assembly
    /// (docs/design/economy-collector.md §2.13). <c>XamarinMainActivity.OnCreate</c> calls <c>GameDll.OnInitialize</c> before
    /// <c>NicePlaySDKDLL.OnInitialize</c> runs the bootstrap, so Game.dll is loaded by then. The server gets the result as
    /// <c>ServerOptions.ClientCapabilities</c>; this assembly never references the game, so the check is by name.
    /// <para>On PC the bootstrap runs in <c>EngineBridge.Class1.Start</c>, before <c>GameApplication</c> loads Game
    /// (docs/design/pc-port.md §6.2), so <see cref="Watch(ClientCapabilities, Log, Func{Action{Assembly}, Action}, BootTrace, string)"/>
    /// probes now and again when Game loads, and writes the "[boot] assemblies:" and "[boot] patches:" lines from the patch
    /// witnesses of §3.9.</para>
    /// </summary>
    public static class GamePatchProbe
    {
        /// <summary>The game's assembly (Game.dll).</summary>
        public const string GameAssembly = "Game";

        /// <summary>The class the 'store' patch adds to Game.dll (patcher/Patches/StorePatch.cs, <c>SafetyType</c>).</summary>
        public const string StoreSafetyType = "Game.McvsStoreSafety";

        /// <summary>The class the 'inbox' patch adds to Game.dll (patcher/Patches/InboxPatch.cs, <c>HelperType</c>).</summary>
        public const string InboxType = "Game.McvsInbox";

        // The PC patch witnesses (docs/design/pc-port.md §3.9): copies of the patch classes' constants (WP4) and of PcInstaller's
        // (WP3); PcHookContractTests keeps the three equal.
        public const string EngineBridgeAssembly = "EngineBridge";
        public const string EngineAssembly = "Gameloft.Engine";
        public const string UtilAssembly = "Gameloft.Util";
        public const string ServerAssembly = "McvsOffline.Server";

        /// <summary>hook-pc: the private static helper <c>McvsOfflineInit</c> in <c>EngineBridge.Class1</c>.</summary>
        public const string HookPcType = "EngineBridge.Class1";
        public const string HookPcMethod = "McvsOfflineInit";

        /// <summary>quicklaunch (Gameloft.Engine.dll).</summary>
        public const string QuickLaunchType = "Gameloft.Engine.McvsQuickLaunch";

        /// <summary>headless-diag (Gameloft.Engine.dll).</summary>
        public const string WorldDiagnosticsType = "Gameloft.Engine.McvsWorldDiagnostics";

        /// <summary>graphics (Gameloft.Engine.dll).</summary>
        public const string GraphicsType = "Gameloft.Engine.McvsGraphics";

        /// <summary>gameload (Gameloft.Engine.dll): the engine loads Game by its full name (WR2 F1).</summary>
        public const string GameLoadType = "Gameloft.Engine.McvsGameLoad";

        /// <summary>The assemblies the "[boot] assemblies:" lines report.</summary>
        public static readonly string[] WatchedAssemblies = { EngineBridgeAssembly, ServerAssembly, EngineAssembly, GameAssembly, UtilAssembly };

        /// <summary>Probes the assemblies loaded in this process. Never throws: what cannot be found out is null (unknown).</summary>
        public static ClientCapabilities Detect(Log log = null)
        {
            Assembly[] loaded;
            try
            {
                loaded = AppDomain.CurrentDomain.GetAssemblies();
            }
            catch (Exception ex)
            {
                Warn(log, ex);
                return new ClientCapabilities();
            }
            return Detect(loaded, log);
        }

        /// <summary><see cref="Detect(Log)"/> over a given list of assemblies (tests). Never throws.</summary>
        internal static ClientCapabilities Detect(IEnumerable<Assembly> loaded, Log log)
        {
            var result = new ClientCapabilities();
            try
            {
                Assembly game = null;
                foreach (Assembly assembly in loaded)
                {
                    if (assembly != null && assembly.GetName().Name == GameAssembly)
                    {
                        game = assembly;
                        break;
                    }
                }
                if (game == null)
                {
                    if (log != null) log.Info("no Game assembly is loaded: the client's patches are unknown");
                    return result;
                }
                result.StorePatch = game.GetType(StoreSafetyType, false, false) != null;
                result.InboxPatch = game.GetType(InboxType, false, false) != null;
                if (log != null)
                {
                    log.Debug("Game.dll: 'store' patch " + (result.StorePatch == true ? "present" : "missing") + ", 'inbox' patch " +
                              (result.InboxPatch == true ? "present" : "missing"));
                }
            }
            catch (Exception ex)
            {
                result.StorePatch = null;
                result.InboxPatch = null;
                Warn(log, ex);
            }
            return result;
        }

        /// <summary>
        /// Like <see cref="Detect(Log)"/>, but fills <paramref name="target"/> (the instance the server reads per use). When Game is
        /// not loaded yet and <paramref name="watchLoads"/> is given (the PC host: <c>AppDomain.AssemblyLoad</c>, which the game's
        /// Android mscorlib does not have), it is filled when Game loads, and the subscription ends once everything was seen. With
        /// a boot trace, every watched assembly gets an "assemblies:" line as it loads (a WARN when it is not from McvsOffline\bin:
        /// the .exe.config redirect did not apply, WR2 Q1) and the witnesses one "patches:" line once Game and Gameloft.Engine are
        /// loaded. Never throws.
        /// </summary>
        /// <param name="watchLoads">Subscribes a callback to assembly loads and returns the unsubscribe action; null: probe once.</param>
        public static PatchWatcher Watch(ClientCapabilities target, Log log, Func<Action<Assembly>, Action> watchLoads, BootTrace boot, string gameRoot)
        {
            return Watch(target, log, watchLoads, boot, gameRoot, () =>
            {
                try
                {
                    return AppDomain.CurrentDomain.GetAssemblies();
                }
                catch (Exception ex)
                {
                    Warn(log, ex);
                    return new Assembly[0];
                }
            });
        }

        /// <summary>The probe of docs/design/pc-port.md §6.2 without a load subscription or the boot lines (Android: Game is loaded).</summary>
        public static PatchWatcher Watch(ClientCapabilities target, Log log)
        {
            return Watch(target, log, null, null, null);
        }

        /// <summary>
        /// <see cref="Watch(ClientCapabilities, Log, Func{Action{Assembly}, Action}, BootTrace, string)"/> over a given list of loaded
        /// assemblies (tests); <paramref name="loaded"/> is asked once the load subscription is in place.
        /// </summary>
        internal static PatchWatcher Watch(ClientCapabilities target, Log log, Func<Action<Assembly>, Action> watchLoads, BootTrace boot, string gameRoot,
                                           Func<IEnumerable<Assembly>> loaded)
        {
            var watcher = new PatchWatcher(target ?? new ClientCapabilities(), log, boot, gameRoot);
            watcher.Start(loaded, watchLoads);
            return watcher;
        }

        /// <summary>The value of a missing witness whose patch install.json does not list (an optional patch left out: not an error).</summary>
        public const string NotApplied = "off (not applied)";

        /// <summary>
        /// "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, gameload yes, store yes, inbox yes" from the loaded assemblies ("NO" for a
        /// missing witness, "unknown" for an assembly that is not loaded). With <paramref name="applied"/> (install.json
        /// <c>options.patches</c>), a missing witness of a patch the install did not apply is <see cref="NotApplied"/> instead of
        /// "NO" (e.g. <c>--patch none</c> leaves out the default 'store'); null: every missing witness is "NO".
        /// </summary>
        internal static string DescribePatches(Assembly engineBridge, Assembly engine, Assembly game, ICollection<string> applied = null)
        {
            var sb = new StringBuilder("patches: ");
            sb.Append("hook-pc ").Append(YesNo(engineBridge, "hook-pc", applied, a =>
            {
                Type type = a.GetType(HookPcType, false, false);
                return type != null && type.GetMethod(HookPcMethod, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) != null;
            }));
            sb.Append(", quicklaunch ").Append(YesNo(engine, "quicklaunch", applied, a => a.GetType(QuickLaunchType, false, false) != null));
            sb.Append(", headless-diag ").Append(YesNo(engine, "headless-diag", applied, a => a.GetType(WorldDiagnosticsType, false, false) != null));
            sb.Append(", graphics ").Append(YesNo(engine, "graphics", applied, a => a.GetType(GraphicsType, false, false) != null));
            sb.Append(", gameload ").Append(YesNo(engine, "gameload", applied, a => a.GetType(GameLoadType, false, false) != null));
            sb.Append(", store ").Append(YesNo(game, "store", applied, a => a.GetType(StoreSafetyType, false, false) != null));
            sb.Append(", inbox ").Append(YesNo(game, "inbox", applied, a => a.GetType(InboxType, false, false) != null));
            return sb.ToString();
        }

        /// <summary>
        /// install.json's <c>options.patches</c> (every applied id) under <paramref name="gameRoot"/>\McvsOffline, or null when it is
        /// missing, does not parse, has another format or no list (then every missing witness counts). Never throws.
        /// </summary>
        internal static ICollection<string> AppliedPatches(string gameRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(gameRoot)) return null;
                string path = Path.Combine(Path.Combine(gameRoot, EmbeddedPaths.DataFolderName), "install.json");
                string text = AtomicFile.ReadAllTextOrNull(path);
                object parsed;
                if (text == null || !MiniJson.TryParse(text, out parsed)) return null;
                var root = parsed as Dictionary<string, object>;
                object value;
                if (root == null || !root.TryGetValue("format", out value) || !(value is long) || (long)value != 1) return null;
                var options = root.TryGetValue("options", out value) ? value as Dictionary<string, object> : null;
                var list = options != null && options.TryGetValue("patches", out value) ? value as List<object> : null;
                if (list == null) return null;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (object id in list)
                {
                    var s = id as string;
                    if (s != null) ids.Add(s);
                }
                return ids;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string YesNo(Assembly assembly, string id, ICollection<string> applied, Func<Assembly, bool> test)
        {
            if (assembly == null) return "unknown (not loaded)";
            try
            {
                if (test(assembly)) return "yes";
                return applied != null && !applied.Contains(id) ? NotApplied : "NO";
            }
            catch (Exception ex)
            {
                return "unknown (" + ex.GetType().Name + ")";
            }
        }

        private static void Warn(Log log, Exception ex)
        {
            try
            {
                if (log != null) log.Warn("the loaded Game.dll could not be probed: the client's patches are unknown", ex);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>
    /// The lazy probe of <see cref="GamePatchProbe.Watch"/>. <see cref="Loaded"/> runs on the loading thread (the game thread): it
    /// only compares names and looks up a few types, and never throws.
    /// </summary>
    public sealed class PatchWatcher
    {
        internal const string BinFolder = "McvsOffline\\bin";
        internal const string GameFolder = "GAME FOLDER";

        private readonly object gate = new object();
        private readonly ClientCapabilities target;
        private readonly Log log;
        private readonly BootTrace boot;
        private readonly string gameRoot;
        private readonly Dictionary<string, Assembly> seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private readonly List<string> notActive = new List<string>();
        private Action unsubscribe;
        private bool storeDone;
        private bool patchesDone;
        private bool stopped;

        internal PatchWatcher(ClientCapabilities target, Log log, BootTrace boot, string gameRoot)
        {
            this.target = target;
            this.log = log;
            this.boot = boot;
            this.gameRoot = gameRoot;
        }

        /// <summary>The capabilities the server reads.</summary>
        public ClientCapabilities Target { get { return target; } }

        /// <summary>The file an assembly came from instead of <c>Assembly.Location</c> (tests: a sandbox assembly has none).</summary>
        internal Func<Assembly, string> LocationOf { get; set; }

        /// <summary>install.json's applied ids for the game root instead of <see cref="GamePatchProbe.AppliedPatches"/> (tests).</summary>
        internal Func<string, ICollection<string>> AppliedOf { get; set; }

        /// <summary>The "patches:" line once written, else null.</summary>
        public string PatchesLine { get; private set; }

        /// <summary>The loading thread's managed stack instead of <c>Environment.StackTrace</c> (tests).</summary>
        internal Func<string> StackOf { get; set; }

        /// <summary>The most frames <see cref="LoadStack"/> keeps.</summary>
        internal const int MaxStackFrames = 25;

        /// <summary>
        /// With a boot trace (PC), why the patched client is not the one running: "Game loaded from the game folder", "the
        /// 'store' patch is missing" (joined with "; "); null while everything seen so far is ours. Also in
        /// <see cref="ClientCapabilities.NotActive"/>. WR2 F2.
        /// </summary>
        public string NotActive
        {
            get { lock (gate) return notActive.Count == 0 ? null : string.Join("; ", notActive.ToArray()); }
        }

        /// <summary>"installed, NOT ACTIVE (Game loaded from the game folder)", the error the status line and the page show.</summary>
        public static string NotActiveText(string reason)
        {
            return "installed, NOT ACTIVE (" + reason + ")";
        }

        /// <summary>True while subscribed to assembly loads.</summary>
        public bool Watching
        {
            get { lock (gate) return unsubscribe != null; }
        }

        /// <summary>
        /// Subscribes first, then asks <paramref name="loaded"/> what is loaded already (so nothing loads unseen in between; an
        /// assembly seen both ways counts once).
        /// </summary>
        internal void Start(Func<IEnumerable<Assembly>> loaded, Func<Action<Assembly>, Action> watchLoads)
        {
            try
            {
                if (watchLoads != null)
                {
                    Action stop = watchLoads(LoadedByEvent);
                    bool already;
                    lock (gate)
                    {
                        already = stopped;
                        if (!already) unsubscribe = stop;
                    }
                    if (already && stop != null) stop();
                }
                IEnumerable<Assembly> present = loaded == null ? null : loaded();
                if (present != null)
                {
                    foreach (Assembly assembly in present) Loaded(assembly);
                }
                bool waiting;
                lock (gate) waiting = !storeDone;
                if (waiting && log != null)
                {
                    log.Info(watchLoads == null ? "no Game assembly is loaded: the client's patches are unknown"
                                                : "Game is not loaded yet: the client's patches are probed when it loads");
                }
                Finish();
            }
            catch (Exception ex)
            {
                try
                {
                    if (log != null) log.Warn("the client's patches could not be watched: they stay unknown", ex);
                }
                catch (Exception)
                {
                }
                Stop();
            }
        }

        /// <summary>An assembly loaded (or found loaded). Never throws.</summary>
        public void Loaded(Assembly assembly)
        {
            Loaded(assembly, false);
        }

        /// <summary>The load subscription's callback: it runs on the loading thread, inside the load, so its stack names the loader.</summary>
        private void LoadedByEvent(Assembly assembly)
        {
            Loaded(assembly, true);
        }

        private void Loaded(Assembly assembly, bool byEvent)
        {
            try
            {
                if (assembly == null) return;
                string name = assembly.GetName().Name;
                if (Array.IndexOf(GamePatchProbe.WatchedAssemblies, name) < 0) return;
                lock (gate)
                {
                    if (seen.ContainsKey(name)) return;
                    seen[name] = assembly;
                }
                if (boot != null) ReportLocation(name, assembly, byEvent);
                if (name == GamePatchProbe.GameAssembly)
                {
                    ClientCapabilities found = GamePatchProbe.Detect(new[] { assembly }, log);
                    target.StorePatch = found.StorePatch;
                    target.InboxPatch = found.InboxPatch;
                    lock (gate) storeDone = true;
                }
                Finish();
            }
            catch (Exception)
            {
            }
        }

        private void Finish()
        {
            Assembly engineBridge, engine, game;
            bool writePatches, done;
            lock (gate)
            {
                seen.TryGetValue(GamePatchProbe.EngineBridgeAssembly, out engineBridge);
                seen.TryGetValue(GamePatchProbe.EngineAssembly, out engine);
                seen.TryGetValue(GamePatchProbe.GameAssembly, out game);
                writePatches = boot != null && !patchesDone && engine != null && game != null;
                if (writePatches) patchesDone = true;
                done = storeDone && (boot == null || (patchesDone && seen.Count == GamePatchProbe.WatchedAssemblies.Length));
            }
            if (writePatches)
            {
                Func<string, ICollection<string>> appliedOf = AppliedOf ?? GamePatchProbe.AppliedPatches;
                string line = GamePatchProbe.DescribePatches(engineBridge, engine, game, appliedOf(gameRoot));
                PatchesLine = line;
                if (line.IndexOf(" NO", StringComparison.Ordinal) >= 0)
                {
                    boot.Warn(line);
                    foreach (string item in line.Substring("patches: ".Length).Split(','))
                    {
                        string[] parts = item.Trim().Split(' ');
                        if (parts.Length == 2 && parts[1] == "NO") AddNotActive("the '" + parts[0] + "' patch is missing", false);
                    }
                }
                else
                {
                    boot.Mark(line);
                }
                ReportNotActive();
            }
            if (done) Stop();
        }

        /// <summary>Records a reason; after the "patches:" line a new one is reported at once (an assembly that loads late).</summary>
        private void AddNotActive(string reason, bool report)
        {
            lock (gate)
            {
                if (notActive.Contains(reason)) return;
                notActive.Add(reason);
            }
            target.NotActive = NotActive;
            if (report) ReportNotActive();
        }

        /// <summary>The one ERROR line of a start that does not run our files: "install: installed, NOT ACTIVE (...)".</summary>
        private void ReportNotActive()
        {
            string reason = NotActive;
            if (reason == null || boot == null) return;
            boot.Error("install: " + NotActiveText(reason) + ": the game runs its own files, so MCVS Offline's fixes are missing; run the kit's " +
                       "status, then install again (docs/PLAYER-SETUP-PC.md)");
        }

        /// <summary>
        /// "assemblies: Game C:\...\McvsOffline\bin\Game.dll (McvsOffline\bin)"; a WARN when it came from elsewhere, and from the
        /// game folder also the "load stack:" WARN (<see cref="ReportLoader"/>).
        /// </summary>
        private void ReportLocation(string name, Assembly assembly, bool byEvent)
        {
            string location = null;
            try
            {
                Func<Assembly, string> locationOf = LocationOf;
                if (locationOf != null) location = locationOf(assembly);
                // By reflection: Assembly.get_Location is a stub in the game's Android mscorlib (EmbeddedDllResolutionTests), and
                // this file is compiled into the Android DLL too; only the PC host (with a boot trace) gets here.
                PropertyInfo property = locationOf != null ? null : typeof(Assembly).GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);
                if (property != null) location = property.GetValue(assembly, null) as string;
            }
            catch (Exception)
            {
            }
            string where = Where(location, gameRoot);
            string line = "assemblies: " + name + " " + (string.IsNullOrEmpty(location) ? "(no file)" : location) + " (" + where + ")";
            if (where == BinFolder)
            {
                boot.Mark(line);
                return;
            }
            boot.Warn(line + ": the .exe.config redirect did not apply");
            if (where == GameFolder) ReportLoader(name, byEvent);
            bool reported;
            lock (gate) reported = patchesDone;
            AddNotActive(name + (where == GameFolder ? " loaded from the game folder" : " loaded from outside " + BinFolder + " (" + where + ")"), reported);
        }

        /// <summary>
        /// Who loaded a redirected assembly from the game folder: the managed stack of the load (this runs inside the
        /// AssemblyLoad event, on the loading thread), from the loader's caller on, so the next log names it. Build 17's line came
        /// from our own GameReflection.Load (a partial-name Type.GetType) and was only found by reading the code.
        /// </summary>
        private void ReportLoader(string name, bool byEvent)
        {
            try
            {
                string header = "load stack: " + name + " (" + GameFolder + ")";
                if (!byEvent)
                {
                    boot.Warn(header + ": already loaded when the host started watching, so its loader is unknown");
                    return;
                }
                Func<string> stackOf = StackOf;
                string frames = LoadStack(stackOf != null ? stackOf() : Environment.StackTrace, MaxStackFrames);
                boot.Warn(header + " was loaded by (innermost first):" + (frames == null ? " (no managed frames)" : "\n" + frames));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The frames of <paramref name="stackTrace"/> (Environment.StackTrace inside the AssemblyLoad event) from the code that
        /// asked for the load on: our handler's frames (Environment, this class, the platform's WatchAssemblyLoads, up to
        /// AppDomain.OnAssemblyLoadEvent) and then the runtime's loader internals (System.Reflection.*, System.AppDomain.*,
        /// System.RuntimeType*, System.Runtime.Loader.*) are cut, at most <paramref name="maxFrames"/> are kept ("... n more
        /// frames" after them). One frame per line, indented by three spaces; null when nothing is left.
        /// </summary>
        internal static string LoadStack(string stackTrace, int maxFrames)
        {
            if (string.IsNullOrEmpty(stackTrace)) return null;
            var frames = new List<string>();
            foreach (string raw in stackTrace.Replace("\r", "").Split('\n'))
            {
                string frame = raw.Trim();
                if (frame.Length > 0) frames.Add(frame);
            }
            int start = -1;
            for (int i = 0; i < frames.Count && start < 0; i++)
            {
                if (Method(frames[i]).StartsWith("System.AppDomain.OnAssemblyLoadEvent", StringComparison.Ordinal)) start = i + 1;
            }
            if (start < 0)
            {
                start = 0;
                while (start < frames.Count && IsHandlerFrame(Method(frames[start]))) start++;
            }
            while (start < frames.Count && IsLoaderFrame(Method(frames[start]))) start++;
            if (start >= frames.Count) return null;
            var sb = new StringBuilder();
            int kept = Math.Min(frames.Count - start, Math.Max(1, maxFrames));
            for (int i = start; i < start + kept; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("   ").Append(frames[i]);
            }
            int more = frames.Count - start - kept;
            if (more > 0) sb.Append("\n   ... ").Append(more).Append(more == 1 ? " more frame" : " more frames");
            return sb.ToString();
        }

        /// <summary>A frame without its leading "at" (localized on some Windows installs: "bei", "à").</summary>
        private static string Method(string frame)
        {
            int space = frame.IndexOf(' ');
            return space > 0 ? frame.Substring(space + 1) : frame;
        }

        private static bool IsHandlerFrame(string method)
        {
            return method.StartsWith("System.Environment.", StringComparison.Ordinal) ||
                   method.StartsWith("System.Diagnostics.StackTrace", StringComparison.Ordinal) ||
                   method.StartsWith("McvsOffline.Embedded.PatchWatcher.", StringComparison.Ordinal) ||
                   method.IndexOf("WatchAssemblyLoads", StringComparison.Ordinal) >= 0;
        }

        private static bool IsLoaderFrame(string method)
        {
            return method.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                   method.StartsWith("System.AppDomain.", StringComparison.Ordinal) ||
                   method.StartsWith("System.RuntimeType", StringComparison.Ordinal) ||
                   method.StartsWith("System.Runtime.Loader.", StringComparison.Ordinal);
        }

        /// <summary>McvsOffline\bin, GAME FOLDER (the stock copy next to the exe) or "elsewhere".</summary>
        internal static string Where(string location, string gameRoot)
        {
            if (string.IsNullOrEmpty(location)) return "elsewhere";
            string dir = Normalize(Path.GetDirectoryName(location.Replace('\\', '/')));
            if (dir.EndsWith("/mcvsoffline/bin", StringComparison.Ordinal)) return BinFolder;
            if (!string.IsNullOrEmpty(gameRoot) && dir == Normalize(gameRoot)) return GameFolder;
            return "elsewhere";
        }

        private static string Normalize(string path)
        {
            return (path ?? "").Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>Unsubscribes (idempotent).</summary>
        public void Stop()
        {
            Action u;
            lock (gate)
            {
                stopped = true;
                u = unsubscribe;
                unsubscribe = null;
            }
            if (u == null) return;
            try
            {
                u();
            }
            catch (Exception)
            {
            }
        }
    }
}
