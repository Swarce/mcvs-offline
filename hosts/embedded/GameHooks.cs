// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLL and into the net8 tests.
using System;
using System.Globalization;
using System.Threading;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry points the patched game code calls (patcher/Patches: NativeOnlinePatch, HeadlessEnginePatch). Their signatures
    /// are part of the patch contract (the patcher looks them up by name and parameter types). They run on the game thread
    /// or a loading-pool thread: cheap, never block, never throw (the patched IL also guards every call).
    /// </summary>
    public static class GameHooks
    {
        private static volatile bool allowNativeOnline;
        private static volatile Log log;
        private static volatile BootTrace boot;
        private static int nativeOnlineCalls;
        private static int headlessReports;

        /// <summary>Set by the embedded host once settings.json is read (before the game creates any World).</summary>
        public static void Configure(bool nativeOnlineSdk, Log hostLog, BootTrace bootTrace)
        {
            allowNativeOnline = nativeOnlineSdk;
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>Times the game asked to start the native online SDK (tests, status line).</summary>
        public static int NativeOnlineCalls { get { return nativeOnlineCalls; } }

        /// <summary>Headless-World component exceptions reported so far.</summary>
        public static int HeadlessReports { get { return headlessReports; } }

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
    }
}
