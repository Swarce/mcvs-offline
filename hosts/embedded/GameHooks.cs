// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry points the patched game code calls (patcher/Patches: NativeOnlinePatch, HeadlessEnginePatch, PhysicsPatch, StorePatch). Their
    /// signatures are part of the patch contract (the patcher looks them up by name and parameter types). They run on the game
    /// thread or a loading-pool thread: cheap, never throw (the patched IL also guards every call), and the only one that can
    /// wait is <see cref="EnterPhysicsStep"/>, for at most <c>embedded.physics.stepGateTimeoutMs</c>.
    /// </summary>
    public static class GameHooks
    {
        private static volatile bool allowNativeOnline;
        private static volatile Log log;
        private static volatile BootTrace boot;
        private static int nativeOnlineCalls;
        private static int headlessReports;
        private static int storeReports;
        private static readonly System.Collections.Generic.HashSet<string> storeReported = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        // Physics (device test 2). The defaults are the fix; settings.json can revert each part for an A/B run.
        private static volatile bool serverPhysicsMultithreaded;
        private static volatile bool clientPhysicsMultithreaded = true;
        private static volatile bool physicsStepGate = true;
        private static volatile int physicsStepGateTimeoutMs = DefaultStepGateTimeoutMs;
        private static readonly object physicsGate = new object();
        private static int physicsGateOwner;   // managed thread id of the World stepping now, 0 = free (under physicsGate)
        private static int physicsWorlds;
        private static int serverWorldsSingleThreaded;
        private static int physicsSteps;
        private static int physicsContended;
        private static int physicsMaxWaitMs;
        private static int physicsTimeouts;

        /// <summary>How long a physics step waits for another World's step before it goes ahead without the gate.</summary>
        public const int DefaultStepGateTimeoutMs = 2000;

        /// <summary>Set by the embedded host once settings.json is read (before the game creates any World).</summary>
        public static void Configure(bool nativeOnlineSdk, Log hostLog, BootTrace bootTrace)
        {
            allowNativeOnline = nativeOnlineSdk;
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>
        /// settings.json <c>embedded.physics</c> (device test 2): <paramref name="serverMultithreaded"/> lets TS/GS Worlds keep the
        /// game's multithreaded Havok (default false, as on Gameloft's dedicated servers), <paramref name="clientMultithreaded"/>
        /// (default true) the client World, <paramref name="stepGate"/> (default true) serializes every World's physics step.
        /// </summary>
        public static void ConfigurePhysics(bool serverMultithreaded, bool clientMultithreaded, bool stepGate, int stepGateTimeoutMs)
        {
            serverPhysicsMultithreaded = serverMultithreaded;
            clientPhysicsMultithreaded = clientMultithreaded;
            physicsStepGate = stepGate;
            physicsStepGateTimeoutMs = stepGateTimeoutMs < 1 ? 1 : stepGateTimeoutMs;
        }

        /// <summary>Times the game asked to start the native online SDK (tests, status line).</summary>
        public static int NativeOnlineCalls { get { return nativeOnlineCalls; } }

        /// <summary>Headless-World component exceptions reported so far.</summary>
        public static int HeadlessReports { get { return headlessReports; } }

        /// <summary>Physics steps that went through the gate, contended entries, and entries that timed out (status line, tests).</summary>
        public static int PhysicsSteps { get { return physicsSteps; } }

        public static int PhysicsContended { get { return physicsContended; } }

        public static int PhysicsTimeouts { get { return physicsTimeouts; } }

        /// <summary>
        /// Called by <c>OnlinePlatformComponent.Init_private</c> (the managed side of the native "GaiaInit"), once per World
        /// that knows its datacenter. True lets the native online SDK start (settings.json <c>embedded.nativeOnlineSdk</c>).
        /// </summary>
        public static bool AllowNativeOnlineInit(string datacenter)
        {
            try
            {
                bool allow = allowNativeOnline;
                int n = Interlocked.Increment(ref nativeOnlineCalls);
                if (n <= 5)
                {
                    Mark("native online SDK init requested (OnlinePlatformComponent.Init_private, datacenter '" + datacenter + "', call " +
                         n.ToString(CultureInfo.InvariantCulture) + "): " +
                         (allow ? "started, settings embedded.nativeOnlineSdk is true" : "skipped, settings embedded.nativeOnlineSdk is false"));
                }
                return allow;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Called by the headless-World safety net (<c>McvsHeadlessSafeAction</c> in Gameloft.Engine) when a component event
        /// of a TS/GS World threw; the rest of that event was skipped. Each line names a component that needs a precise fix.
        /// </summary>
        public static void HeadlessComponentException(string component, string eventName, Exception exception)
        {
            try
            {
                int n = Interlocked.Increment(ref headlessReports);
                if (n > 100) return;
                string text = "headless world: " + component + "." + eventName + " threw, the rest of it is skipped (report " +
                              n.ToString(CultureInfo.InvariantCulture) + ")";
                BootTrace b = boot;
                if (b != null) b.Warn(text);
                Log l = log;
                if (l != null) l.Warn(text, exception);
                else SelfLog.Write(SelfLog.Warn, "[headless] " + text + ": " + exception);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Called by the shop's safety net (patch 'store', <c>Game.McvsStoreSafety.Report</c>) when the availability check of one
        /// unified-store item threw; that item is left out of the shop. One line per item id (the shop rebuilds its lists on every
        /// visit), on stdout (logcat mono-stdout) and in bootstrap.log.
        /// </summary>
        public static void StoreItemException(object item, Exception exception)
        {
            try
            {
                string id = ItemId(item);
                lock (storeReported)
                {
                    if (storeReported.Count >= 100 || !storeReported.Add(id)) return;
                }
                Interlocked.Increment(ref storeReports);
                string text = "store item " + id + " threw " + (exception == null ? "?" : exception.ToString());
                try
                {
                    Console.WriteLine("[McvsOffline] " + text);
                }
                catch (Exception)
                {
                }
                Log l = log;
                if (l != null) l.Warn("store item " + id + " is left out of the shop: its availability check threw", exception);
                else SelfLog.Write(SelfLog.Warn, "[store] " + text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Items the store safety net reported (distinct ids).</summary>
        public static int StoreReports { get { return storeReports; } }

        private static string ItemId(object item)
        {
            if (item == null) return "(null)";
            try
            {
                PropertyInfo p = item.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object v = p == null ? null : p.GetValue(item, null);
                if (v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
            }
            return item.GetType().FullName;
        }

        /// <summary>
        /// Called by the <c>PhysicsComponent</c> constructor (patch 'physics', <c>McvsPhysics.UseMultithreading</c>) for every
        /// World's Havok world, with the game's own choice (<c>PhysicsComponent.PhysicsMultithreaded</c>, from
        /// GameOptions_android.json: true) and whether the World is a headless TS/GS. Returns what the native constructor gets.
        /// <para>
        /// Multithreaded Havok worlds all share ONE process-wide hkCpuThreadPool and hkJobQueue, which support one stepping thread
        /// at a time. Gameloft ran each server World alone in its own Linux process without GameOptions (single-threaded Havok);
        /// in-process, the client World stepped on a loading-pool thread while the TS World stepped on the game thread, through
        /// the same pool, and the game thread never came back (device test 2). Server Worlds therefore get single-threaded Havok.
        /// </para>
        /// </summary>
        public static bool PhysicsMultithreading(bool headless, bool requested)
        {
            bool use = requested;
            try
            {
                use = requested && (headless ? serverPhysicsMultithreaded : clientPhysicsMultithreaded);
                if (headless && requested && !use) Interlocked.Increment(ref serverWorldsSingleThreaded);
                int n = Interlocked.Increment(ref physicsWorlds);
                if (n <= 8)
                {
                    string text = "physics: " + (headless ? "server (headless) World" : "client World") + " #" + n.ToString(CultureInfo.InvariantCulture) +
                                  ": Havok " + (use ? "multithreaded" : "single-threaded") + " (GameOptions multithreadedPhysics " + Bool(requested) +
                                  (headless ? ", settings embedded.physics.serverMultithreaded " + Bool(serverPhysicsMultithreaded)
                                            : ", settings embedded.physics.clientMultithreaded " + Bool(clientPhysicsMultithreaded)) +
                                  "; step gate " + (physicsStepGate ? "on" : "off") + ")";
                    if (n == 1) text += "; " + DescribeEngineThreading();
                    Mark(text);
                }
            }
            catch (Exception)
            {
            }
            return use;
        }

        /// <summary>
        /// Called by <c>PhysicsComponent.UpdateSimulationSteps</c> (patch 'physics') before a World's 1-4 Havok steps, on the
        /// thread that steps (the game thread, or a loading-pool thread for the client's deferred physics). True: the caller
        /// holds the process-wide step gate and must call <see cref="ExitPhysicsStep"/>. False: gate off, or another World's step
        /// held it for longer than the timeout (logged), and the caller steps without it, as the original game did.
        /// </summary>
        public static bool EnterPhysicsStep()
        {
            try
            {
                if (!physicsStepGate) return false;
                Interlocked.Increment(ref physicsSteps);
                int me = Thread.CurrentThread.ManagedThreadId;
                int waited = -1;
                bool taken = false;
                // The game's trimmed mscorlib has no timed Monitor.TryEnter: the gate is an owner field under a short lock, and a
                // waiter sleeps in Monitor.Wait(lock, remaining), which releases the lock.
                lock (physicsGate)
                {
                    if (physicsGateOwner == 0)
                    {
                        physicsGateOwner = me;
                        return true;
                    }
                    if (physicsGateOwner == me) return false;   // re-entry (not expected): the outer call releases it
                    int started = Environment.TickCount;
                    int timeout = physicsStepGateTimeoutMs;
                    while (physicsGateOwner != 0)
                    {
                        int left = timeout - (Environment.TickCount - started);
                        if (left <= 0) break;
                        Monitor.Wait(physicsGate, left);
                    }
                    if (physicsGateOwner == 0)
                    {
                        physicsGateOwner = me;
                        taken = true;
                    }
                    waited = Environment.TickCount - started;
                }
                Interlocked.Increment(ref physicsContended);
                if (waited > physicsMaxWaitMs) physicsMaxWaitMs = waited;
                if (!taken)
                {
                    int n = Interlocked.Increment(ref physicsTimeouts);
                    if (n <= 5)
                    {
                        Warn("physics step gate: another World's physics step held it for " + waited.ToString(CultureInfo.InvariantCulture) +
                             " ms; stepping without the gate (timeout " + n.ToString(CultureInfo.InvariantCulture) + ", thread " +
                             me.ToString(CultureInfo.InvariantCulture) + ")");
                    }
                }
                return taken;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Releases the gate taken by <see cref="EnterPhysicsStep"/> (same thread; only when it returned true).</summary>
        public static void ExitPhysicsStep()
        {
            try
            {
                int me = Thread.CurrentThread.ManagedThreadId;
                lock (physicsGate)
                {
                    if (physicsGateOwner != me) return;
                    physicsGateOwner = 0;
                    Monitor.PulseAll(physicsGate);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>One status-line part: what the physics hooks decided and did.</summary>
        public static string DescribePhysics()
        {
            int worlds = physicsWorlds;
            if (worlds == 0) return "physics: no World built yet";
            return "physics: " + worlds.ToString(CultureInfo.InvariantCulture) + " Havok worlds built (" +
                   serverWorldsSingleThreaded.ToString(CultureInfo.InvariantCulture) + " server Worlds made single-threaded), step gate " +
                   (physicsStepGate ? "on: " + physicsSteps.ToString(CultureInfo.InvariantCulture) + " entries, " +
                                      physicsContended.ToString(CultureInfo.InvariantCulture) + " waited (max " +
                                      physicsMaxWaitMs.ToString(CultureInfo.InvariantCulture) + " ms), " +
                                      physicsTimeouts.ToString(CultureInfo.InvariantCulture) + " timeouts"
                                    : "off");
        }

        /// <summary>The engine's threading choices (GameOptions), read once by reflection for the boot trace.</summary>
        private static string DescribeEngineThreading()
        {
            try
            {
                Type physics = Type.GetType("Gameloft.Engine.PhysicsComponent, Gameloft.Engine", false);
                Type app = Type.GetType("Gameloft.Engine.GameApplication, Gameloft.Engine", false);
                string deferred = StaticProperty(physics, "DefaultDeferred");
                object instance = app == null ? null : app.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                string renderThread = InstanceProperty(app, instance, "RenderThreadEnabled");
                string renderMode = InstanceProperty(app, instance, "RenderThreadMode");
                return "engine threading: PhysicsComponent.DefaultDeferred " + deferred + " (client physics on a loading-pool thread), " +
                       "GameApplication.RenderThreadEnabled " + renderThread + ", RenderThreadMode " + renderMode;
            }
            catch (Exception ex)
            {
                return "engine threading unavailable: " + ex.GetType().Name;
            }
        }

        private static string StaticProperty(Type type, string name)
        {
            try
            {
                PropertyInfo p = type == null ? null : type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                object v = p == null ? null : p.GetValue(null, null);
                return v == null ? "?" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string InstanceProperty(Type type, object instance, string name)
        {
            try
            {
                if (instance == null) return "? (no GameApplication yet)";
                PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object v = p == null ? null : p.GetValue(instance, null);
                return v == null ? "?" : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static void Mark(string text)
        {
            BootTrace b = boot;
            if (b != null)
            {
                b.Mark(text);
                return;
            }
            Log l = log;
            if (l != null) l.Info(text);
            else SelfLog.Write(SelfLog.Info, "[boot] " + text);
        }

        private static void Warn(string text)
        {
            BootTrace b = boot;
            if (b != null)
            {
                b.Warn(text);
                return;
            }
            Log l = log;
            if (l != null) l.Warn(text);
            else SelfLog.Write(SelfLog.Warn, "[physics] " + text);
        }
    }
}
