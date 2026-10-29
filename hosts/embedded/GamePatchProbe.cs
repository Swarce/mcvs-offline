// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using McvsOffline.Server.Core;

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
                if (log != null) log.Debug("Game.dll: 'store' patch " + (result.StorePatch == true ? "present" : "missing"));
            }
            catch (Exception ex)
            {
                result.StorePatch = null;
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

        /// <summary>
        /// "patches: hook-pc yes, quicklaunch yes, headless-diag yes, graphics yes, store yes" from the loaded assemblies ("NO" for a
        /// missing witness, "unknown" for an assembly that is not loaded).
        /// </summary>
        internal static string DescribePatches(Assembly engineBridge, Assembly engine, Assembly game)
        {
            var sb = new StringBuilder("patches: ");
            sb.Append("hook-pc ").Append(YesNo(engineBridge, a =>
            {
                Type type = a.GetType(HookPcType, false, false);
                return type != null && type.GetMethod(HookPcMethod, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static) != null;
            }));
            sb.Append(", quicklaunch ").Append(YesNo(engine, a => a.GetType(QuickLaunchType, false, false) != null));
            sb.Append(", headless-diag ").Append(YesNo(engine, a => a.GetType(WorldDiagnosticsType, false, false) != null));
            sb.Append(", graphics ").Append(YesNo(engine, a => a.GetType(GraphicsType, false, false) != null));
            sb.Append(", store ").Append(YesNo(game, a => a.GetType(StoreSafetyType, false, false) != null));
            return sb.ToString();
        }

        private static string YesNo(Assembly assembly, Func<Assembly, bool> test)
        {
            if (assembly == null) return "unknown (not loaded)";
            try
            {
                return test(assembly) ? "yes" : "NO";
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

        /// <summary>The "patches:" line once written, else null.</summary>
        public string PatchesLine { get; private set; }

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
                    Action stop = watchLoads(Loaded);
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
                if (boot != null) ReportLocation(name, assembly);
                if (name == GamePatchProbe.GameAssembly)
                {
                    target.StorePatch = GamePatchProbe.Detect(new[] { assembly }, log).StorePatch;
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
                string line = GamePatchProbe.DescribePatches(engineBridge, engine, game);
                PatchesLine = line;
                if (line.IndexOf(" NO", StringComparison.Ordinal) >= 0) boot.Warn(line);
                else boot.Mark(line);
            }
            if (done) Stop();
        }

        /// <summary>"assemblies: Game C:\...\McvsOffline\bin\Game.dll (McvsOffline\bin)"; a WARN when it came from elsewhere.</summary>
        private void ReportLocation(string name, Assembly assembly)
        {
            string location = null;
            try
            {
                // By reflection: Assembly.get_Location is a stub in the game's Android mscorlib (EmbeddedDllResolutionTests), and
                // this file is compiled into the Android DLL too; only the PC host (with a boot trace) gets here.
                PropertyInfo property = typeof(Assembly).GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);
                location = property == null ? null : property.GetValue(assembly, null) as string;
            }
            catch (Exception)
            {
            }
            string where = Where(location, gameRoot);
            string line = "assemblies: " + name + " " + (string.IsNullOrEmpty(location) ? "(no file)" : location) + " (" + where + ")";
            if (where == BinFolder) boot.Mark(line);
            else boot.Warn(line + ": the .exe.config redirect did not apply");
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
