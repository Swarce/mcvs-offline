#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry points of the PC-only patches (docs/design/pc-port.md §3.2): <c>quicklaunch</c> P2/P3 and <c>headless-diag</c>
    /// M1/M4. Part of the patch contract (PcHookContractTests): public static, BCL-typed, never throw, return quickly on the
    /// calling thread.
    /// <para>Day-0 bodies: <see cref="QuickLaunch"/> takes the game's own path (<c>link.QuickLaunch(ct)</c> by reflection), the
    /// others only log. WP2 adds the LocalAnubis start and the Finder route (§8.3) and the diagnostics of §8.5.</para>
    /// </summary>
    public static class PcHooks
    {
        private static volatile Log log;
        private static volatile BootTrace boot;
        private static int serverWorldReports;
        private static int componentReports;

        /// <summary>Set by EmbeddedHost on PC before the game starts. Thread-safe; never throws.</summary>
        public static void Configure(Log hostLog, BootTrace bootTrace)
        {
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>
        /// Patch quicklaunch P2: replaces ClientComponent.QuickLaunch(ClientLink, CancellationToken). Game thread.
        /// component: the Gameloft.Engine.ClientComponent; link: the Gameloft.Engine.ClientLink. Never returns null and never
        /// throws: every failure is a faulted Task carrying the original exception (§8.3).
        /// </summary>
        public static Task QuickLaunch(object component, object link, CancellationToken ct)
        {
            try
            {
                if (link == null) throw new ArgumentNullException("link");
                MethodInfo quickLaunch = link.GetType().GetMethod("QuickLaunch", BindingFlags.Public | BindingFlags.Instance, null,
                                                                  new[] { typeof(CancellationToken) }, null);
                if (quickLaunch == null) throw new MissingMethodException(link.GetType().FullName, "QuickLaunch(CancellationToken)");
                var task = quickLaunch.Invoke(link, new object[] { ct }) as Task;
                if (task == null) throw new InvalidOperationException("ClientLink.QuickLaunch returned no Task");
                return task;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Warn("[pc] quicklaunch: cannot start", inner);
                return Faulted(inner);
            }
        }

        /// <summary>
        /// Patch quicklaunch P3: prologue of MatchmakingComponent.AddAutomatchHostSettings. TS World thread.
        /// settings: the MatchmakingComponent.AutomatchSettings. Never throws.
        /// </summary>
        public static void DefaultAutomatchHost(object settings)
        {
            try
            {
                Log l = log;
                if (l != null) l.Debug("[pc] quicklaunch: automatch host settings " + (settings == null ? "null" : settings.GetType().FullName));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Patch headless-diag M1: the catch (Exception) of ServerManager._AddClientAsync. Never throws.</summary>
        public static void ServerWorldFailed(Exception exception)
        {
            try
            {
                int n = Interlocked.Increment(ref serverWorldReports);
                if (n > 10) return;
                Warn("[McvsOffline] server world: ServerManager._AddClientAsync failed (report " + n.ToString(CultureInfo.InvariantCulture) + ")", exception);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Patch headless-diag M4: the catch (Exception) of ComponentAssemblies.CreateInstance. Never throws.</summary>
        public static void ComponentCreateFailed(Exception exception, Type type)
        {
            try
            {
                int n = Interlocked.Increment(ref componentReports);
                if (n > 20) return;
                Warn("[McvsOffline] component " + (type == null ? "?" : type.FullName) + " was not created", exception);
            }
            catch (Exception)
            {
            }
        }

        private static void Warn(string text, Exception exception)
        {
            try
            {
                BootTrace b = boot;
                if (b != null) b.Warn(text);
                Log l = log;
                if (l != null) l.Warn(text, exception);
                else SelfLog.Write(SelfLog.Warn, text + ": " + exception);
            }
            catch (Exception)
            {
            }
        }

        private static Task Faulted(Exception exception)
        {
            var source = new TaskCompletionSource<object>();
            source.SetException(exception);
            return source.Task;
        }
    }
}
#endif
