#if MCVS_WINDOWS || !MCVS_EMBEDDED
// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Windows (Steam PC build): compiled into the PC DLL and the net8 tests, never into the Android DLL.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using McvsOffline.Embedded.Diagnostics;
using McvsOffline.Server.Core;

namespace McvsOffline
{
    /// <summary>
    /// Entry points of the PC-only patches (docs/design/pc-port.md §3.2): <c>quicklaunch</c> P2/P3 and <c>headless-diag</c>
    /// M1/M4. Part of the patch contract (PcHookContractTests): public static, BCL-typed, never throw, return quickly on the
    /// calling thread.
    /// <para>
    /// Why P2/P3 (§8.3, [QL] §2): the PC engine lost the client plumbing that routes an Embedded link to LocalAnubis, so the
    /// client's quick_launch went to our Federation matchmaker (404 by design) and the error popup came up. <see cref="QuickLaunch"/>
    /// does what Android's engine does: start LocalAnubis (Lobby, Controller, the Finder on http.sys :51750) and quick_launch
    /// through the local Finder. No async/await (repo rule): it composes with <c>ContinueWith</c> on the game thread's
    /// <c>SynchronizationContext</c>, so every continuation, and with it LocalAnubis's own awaits, runs on the game thread as on
    /// Android (WR2 Q13 checks the thread ids in the log). The game's types are found from the arguments' assemblies (never
    /// <c>Type.GetType(name)</c>), so the tests run it against look-alikes. Every catch writes one log line; the lines are
    /// rate-limited per message (the first 5, then every 50th).
    /// </para>
    /// </summary>
    public static class PcHooks
    {
        public const string EngineLinkType = "Gameloft.Engine.ClientLink";
        public const string ManagerType = "Gameloft.Engine.LocalAnubisManager";
        public const string SessionType = "Gameloft.OnlinePlatform.Session";
        public const string MatchmakerType = "Gameloft.OnlinePlatform.MatchmakerService";
        public const string EmbeddedLocation = "Embedded";

        private const int ServerWorldReportsLogged = 10;
        private const int ComponentTypesLogged = 20;

        private static volatile Log log;
        private static volatile BootTrace boot;
        private static readonly object gate = new object();
        private static readonly Dictionary<string, int> messageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> componentTypes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> locationsSeen = new HashSet<string>(StringComparer.Ordinal);
        private static int serverWorldReports;
        private static int componentReports;
        private static int automatchDefaults;
        private static int startedLogged;
        private static int noContextLogged;

        /// <summary>Set by EmbeddedHost on PC before the game starts. Thread-safe; never throws.</summary>
        public static void Configure(Log hostLog, BootTrace bootTrace)
        {
            log = hostLog;
            boot = bootTrace;
        }

        /// <summary>"M1 ServerManager._AddClientAsync failures n, M4 components not created n" for the status line.</summary>
        public static string Describe()
        {
            return "pc: server World failures " + serverWorldReports.ToString(CultureInfo.InvariantCulture) + ", components not created " +
                   componentReports.ToString(CultureInfo.InvariantCulture) + ", automatch through the local Finder " +
                   automatchDefaults.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ P2: ClientComponent.QuickLaunch

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
                object settings = Get(link, "Settings");
                object location = Get(settings, "ServerLocation");
                string where = location == null ? "null" : location.ToString();
                if (where != EmbeddedLocation)
                {
                    bool first;
                    lock (gate) first = locationsSeen.Add(where);
                    if (first) Info("quicklaunch: ServerLocation " + where + ": the game's own path");
                    return StockQuickLaunch(link, ct);
                }
                var launch = new Launch(component, link, ct, Get(settings, "ServerType") as string);
                SynchronizationContext context = SynchronizationContext.Current;
                if (context != null)
                {
                    launch.Scheduler = TaskScheduler.FromCurrentSynchronizationContext();
                }
                else
                {
                    launch.Scheduler = TaskScheduler.Current;
                    if (Interlocked.Exchange(ref noContextLogged, 1) == 0)
                        Warn("quicklaunch: no synchronization context on the calling thread (thread " + Tid() + "): the continuations run on " +
                             "the current task scheduler, not necessarily on the game thread", null);
                }
                Task start = launch.Started() ? Completed() : launch.Start();
                start.ContinueWith(launch.AfterStart, CancellationToken.None, TaskContinuationOptions.None, launch.Scheduler);
                return launch.Result.Task;
            }
            catch (Exception ex)
            {
                Exception inner = Unwrap(ex);
                Warn("quicklaunch: cannot start: " + inner.GetType().Name + ": " + inner.Message, inner);
                return Faulted(new[] { inner });
            }
        }

        /// <summary>The stock body: <c>link.QuickLaunch(ct)</c> (public, <c>PC-EN/ClientLink.cs:391-396</c>).</summary>
        private static Task StockQuickLaunch(object link, CancellationToken ct)
        {
            MethodInfo quickLaunch = link.GetType().GetMethod("QuickLaunch", BindingFlags.Public | BindingFlags.Instance, null,
                                                              new[] { typeof(CancellationToken) }, null);
            if (quickLaunch == null) throw new MissingMethodException(link.GetType().FullName, "QuickLaunch(CancellationToken)");
            var task = quickLaunch.Invoke(link, new object[] { ct }) as Task;
            if (task == null) throw new InvalidOperationException("ClientLink.QuickLaunch returned no Task");
            return task;
        }

        /// <summary>One quick launch: the reflection targets and the three steps (start, quick_launch, room).</summary>
        private sealed class Launch
        {
            private readonly object component;
            private readonly object link;
            private readonly CancellationToken ct;
            private readonly string serverType;
            private readonly Type manager;

            public Launch(object component, object link, CancellationToken ct, string serverType)
            {
                this.component = component;
                this.link = link;
                this.ct = ct;
                this.serverType = serverType;
                Result = new TaskCompletionSource<object>();
                Type linkType = BaseType(link.GetType(), EngineLinkType);
                if (linkType == null) throw new MissingMemberException(link.GetType().FullName + " is not a " + EngineLinkType);
                manager = linkType.Assembly.GetType(ManagerType, false);
                if (manager == null) throw new MissingMemberException(linkType.Assembly.GetName().Name + " has no " + ManagerType);
            }

            public TaskCompletionSource<object> Result { get; private set; }

            public TaskScheduler Scheduler { get; set; }

            public bool Started()
            {
                return (bool)GetStatic(manager, "Started");
            }

            /// <summary>LocalAnubisManager.Start(CancellationToken): the super session, then Lobby, Controller and Finder.</summary>
            public Task Start()
            {
                MethodInfo start = manager.GetMethod("Start", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(CancellationToken) }, null);
                if (start == null) throw new MissingMethodException(ManagerType, "Start(CancellationToken)");
                var task = Invoke(start, null, ct) as Task;
                if (task == null) throw new InvalidOperationException(ManagerType + ".Start returned no Task");
                return task;
            }

            /// <summary>Step 2 (game thread): LocalAnubis runs; quick_launch through the local Finder.</summary>
            public void AfterStart(Task start)
            {
                try
                {
                    if (start.IsFaulted)
                    {
                        IList<Exception> inners = Inners(start);
                        Exception first = inners[0];
                        string text = "quicklaunch: LocalAnubis did not start: " + first.GetType().Name + ": " + first.Message;
                        if (HasType(inners, "HttpListenerException"))
                            text += "; http.sys refused http://localhost:51750/ (WR2 Q2, PLAYER-SETUP-PC troubleshooting)";
                        Warn(text, first);
                        Result.TrySetException(inners);
                        return;
                    }
                    if (start.IsCanceled)
                    {
                        Result.TrySetCanceled();
                        return;
                    }
                    object finder = GetStatic(manager, "Finder");
                    string host = Get(finder, "PublicHost") as string;
                    int port = Convert.ToInt32(Get(finder, "PublicPort"), CultureInfo.InvariantCulture);
                    if (Interlocked.Exchange(ref startedLogged, 1) == 0)
                    {
                        object lobby = GetStatic(manager, "Lobby");
                        Mark("quicklaunch: LocalAnubis started (Finder http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/, lobby port " +
                             Show(TryGet(lobby, "Port")) + ", controller port " + Show(TryGet(TryGet(lobby, "Controller"), "Port")) +
                             ", thread " + Tid() + ")");
                    }
                    object session = Get(Get(component, "Authentication"), "Session");
                    if (session == null) throw new InvalidOperationException("the client has no session (Authentication.Session is null)");
                    Type sessionType = BaseType(session.GetType(), SessionType);
                    if (sessionType == null) throw new MissingMemberException(session.GetType().FullName + " is not a " + SessionType);
                    Type matchmakerType = sessionType.Assembly.GetType(MatchmakerType, false);
                    if (matchmakerType == null) throw new MissingMemberException(sessionType.Assembly.GetName().Name + " has no " + MatchmakerType);
                    MethodInfo getService = sessionType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (getService == null || !getService.IsGenericMethodDefinition) throw new MissingMethodException(SessionType, "GetService<T>()");
                    object matchmaker = Invoke(getService.MakeGenericMethod(matchmakerType), session);
                    if (matchmaker == null) throw new InvalidOperationException("the session has no MatchmakerService");
                    MethodInfo quickLaunch = matchmakerType.GetMethod("QuickLaunch", BindingFlags.Public | BindingFlags.Instance, null,
                                                                      new[] { typeof(string), typeof(CancellationToken), typeof(string), typeof(int?) }, null);
                    if (quickLaunch == null) throw new MissingMethodException(MatchmakerType, "QuickLaunch(string, CancellationToken, string, int?)");
                    var query = Invoke(quickLaunch, matchmaker, serverType, ct, host, (int?)port) as Task;
                    if (query == null) throw new InvalidOperationException("MatchmakerService.QuickLaunch returned no Task");
                    query.ContinueWith(AfterQuickLaunch, CancellationToken.None, TaskContinuationOptions.None, Scheduler);
                }
                catch (Exception ex)
                {
                    Exception inner = Unwrap(ex);
                    Warn("quicklaunch: the local quick launch could not be sent: " + inner.GetType().Name + ": " + inner.Message, inner);
                    Result.TrySetException(inner);
                }
            }

            /// <summary>Step 3 (game thread): the Finder's room becomes the link's room.</summary>
            public void AfterQuickLaunch(Task query)
            {
                try
                {
                    if (query.IsFaulted)
                    {
                        IList<Exception> inners = Inners(query);
                        Warn("quicklaunch: the local Finder answered no room: " + inners[0].GetType().Name + ": " + inners[0].Message, inners[0]);
                        Result.TrySetException(inners);
                        return;
                    }
                    if (query.IsCanceled || ct.IsCancellationRequested)
                    {
                        Result.TrySetCanceled();
                        return;
                    }
                    object room = Get(query, "Result");
                    PropertyInfo roomProperty = link.GetType().GetProperty("Room", BindingFlags.Public | BindingFlags.Instance);
                    if (roomProperty == null || !roomProperty.CanWrite) throw new MissingMemberException(EngineLinkType, "Room");
                    roomProperty.SetValue(link, room, null);
                    Mark("quicklaunch: room " + Show(TryGet(room, "Id")) + " (controller " + Show(TryGet(room, "ControllerHost")) + ":" +
                         Show(TryGet(room, "ControllerPort")) + ", thread " + Tid() + ")");
                    Result.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    Exception inner = Unwrap(ex);
                    Warn("quicklaunch: the room could not be set: " + inner.GetType().Name + ": " + inner.Message, inner);
                    Result.TrySetException(inner);
                }
            }
        }

        // ------------------------------------------------------------------ P3: MatchmakingComponent.AddAutomatchHostSettings

        /// <summary>
        /// Patch quicklaunch P3: prologue of MatchmakingComponent.AddAutomatchHostSettings. TS World thread.
        /// settings: the MatchmakingComponent.AutomatchSettings. Never throws.
        /// <para>When LocalAnubis runs (only after our start) and the settings carry neither Host nor Port, they get the local
        /// Finder's; settings seeded from MatchmakingComponent.Host/Port are kept, and the stock body still overwrites both when
        /// the request carries them.</para>
        /// </summary>
        public static void DefaultAutomatchHost(object settings)
        {
            try
            {
                if (settings == null) return;
                Type manager = FindManager(settings.GetType());
                if (manager == null) throw new MissingMemberException("no " + ManagerType + " next to " + settings.GetType().FullName);
                if (!(bool)GetStatic(manager, "Started")) return;
                string host = Get(settings, "Host") as string;
                object port = Get(settings, "Port");
                if (!string.IsNullOrEmpty(host) || port != null) return;
                object finder = GetStatic(manager, "Finder");
                string publicHost = Get(finder, "PublicHost") as string;
                int publicPort = Convert.ToInt32(Get(finder, "PublicPort"), CultureInfo.InvariantCulture);
                Set(settings, "Host", publicHost);
                Set(settings, "Port", (int?)publicPort);
                int n = Interlocked.Increment(ref automatchDefaults);
                if (n <= 3) Info("quicklaunch: automatch through the local Finder " + publicHost + ":" + publicPort.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Exception inner = Unwrap(ex);
                Warn("quicklaunch: the automatch host could not be set: " + inner.GetType().Name + ": " + inner.Message, inner);
            }
        }

        // ------------------------------------------------------------------ M1, M4: headless-diag

        /// <summary>Patch headless-diag M1: the catch (Exception) of ServerManager._AddClientAsync. Never throws.</summary>
        public static void ServerWorldFailed(Exception exception)
        {
            try
            {
                int n = Interlocked.Increment(ref serverWorldReports);
                if (n > ServerWorldReportsLogged && n % 50 != 0) return;
                string text = "server world: ServerManager._AddClientAsync failed: " + Describe(exception) +
                              (n > ServerWorldReportsLogged ? " (" + n.ToString(CultureInfo.InvariantCulture) + " failures so far)" : "");
                WarnWithBoot(text, exception);
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
                Interlocked.Increment(ref componentReports);
                string name = type == null ? "?" : type.FullName;
                bool first;
                lock (gate) first = componentTypes.Count < ComponentTypesLogged && componentTypes.Add(name);
                if (!first) return;
                WarnWithBoot("component " + name + " was not created: " + Describe(exception), exception);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Tests: back to the state before <see cref="Configure"/>.</summary>
        internal static void ResetForTests()
        {
            log = null;
            boot = null;
            lock (gate)
            {
                messageCounts.Clear();
                componentTypes.Clear();
                locationsSeen.Clear();
            }
            serverWorldReports = 0;
            componentReports = 0;
            automatchDefaults = 0;
            startedLogged = 0;
            noContextLogged = 0;
        }

        /// <summary>LocalAnubisManager from the assembly of <paramref name="type"/>, its declaring type or a base type, or null.</summary>
        private static Type FindManager(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                Type manager = t.Assembly.GetType(ManagerType, false);
                if (manager == null && t.DeclaringType != null) manager = t.DeclaringType.Assembly.GetType(ManagerType, false);
                if (manager != null) return manager;
            }
            return null;
        }

        /// <summary>The type named <paramref name="fullName"/> in <paramref name="type"/>'s base chain, or null.</summary>
        private static Type BaseType(Type type, string fullName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.FullName == fullName) return t;
            }
            return null;
        }

        private static object Get(object target, string name)
        {
            if (target == null) throw new NullReferenceException("null while reading ." + name);
            PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) throw new MissingMemberException(target.GetType().FullName, name);
            return p.GetValue(target, null);
        }

        private static object TryGet(object target, string name)
        {
            try
            {
                return target == null ? null : Get(target, name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Set(object target, string name, object value)
        {
            PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) throw new MissingMemberException(target.GetType().FullName, name);
            p.SetValue(target, value, null);
        }

        private static object GetStatic(Type type, string name)
        {
            PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (p == null) throw new MissingMemberException(type.FullName, name);
            return p.GetValue(null, null);
        }

        private static object Invoke(MethodInfo method, object target, params object[] arguments)
        {
            return method.Invoke(target, arguments);
        }

        private static string Show(object value)
        {
            return value == null ? "?" : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Tid()
        {
            return NativeThreads.CurrentTid().ToString(CultureInfo.InvariantCulture);
        }

        private static string Describe(Exception exception)
        {
            return exception == null ? "(no exception)" : exception.GetType().Name + ": " + exception.Message;
        }

        /// <summary>A TargetInvocationException's (or a one-element AggregateException's) inner exception, else the exception.</summary>
        internal static Exception Unwrap(Exception ex)
        {
            while (true)
            {
                if (ex is TargetInvocationException && ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    continue;
                }
                var aggregate = ex as AggregateException;
                if (aggregate != null && aggregate.InnerExceptions.Count == 1)
                {
                    ex = aggregate.InnerExceptions[0];
                    continue;
                }
                return ex;
            }
        }

        /// <summary>The faulted task's exceptions, each with its own type (so the game's exception filters see a stock failure).</summary>
        private static IList<Exception> Inners(Task task)
        {
            var list = new List<Exception>();
            if (task.Exception != null)
            {
                foreach (Exception e in task.Exception.InnerExceptions) list.Add(Unwrap(e));
            }
            if (list.Count == 0) list.Add(new InvalidOperationException("the task faulted without an exception"));
            return list;
        }

        private static bool HasType(IEnumerable<Exception> exceptions, string name)
        {
            foreach (Exception e in exceptions)
            {
                for (Type t = e == null ? null : e.GetType(); t != null; t = t.BaseType)
                {
                    if (t.Name == name) return true;
                }
            }
            return false;
        }

        private static Task Completed()
        {
            var source = new TaskCompletionSource<object>();
            source.SetResult(null);
            return source.Task;
        }

        private static Task Faulted(IEnumerable<Exception> exceptions)
        {
            var source = new TaskCompletionSource<object>();
            source.SetException(exceptions);
            return source.Task;
        }

        /// <summary>The first 5 lines of a message, then every 50th (keyed by the text up to the first ':' after "quicklaunch: ").</summary>
        private static bool Allow(string text)
        {
            string key = text;
            int colon = text.IndexOf(':', text.IndexOf(':') + 1);
            if (colon > 0) key = text.Substring(0, colon);
            lock (gate)
            {
                int n;
                messageCounts.TryGetValue(key, out n);
                messageCounts[key] = ++n;
                return n <= 5 || n % 50 == 0;
            }
        }

        private static void Info(string text)
        {
            try
            {
                if (!Allow(text)) return;
                Log l = log;
                if (l != null) l.Info(text);
                else SelfLog.Write(SelfLog.Info, "[pc] " + text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A quick-launch milestone (LocalAnubis started, the room): INFO in the host log, like every "[pc]" line.</summary>
        private static void Mark(string text)
        {
            Info(text);
        }

        private static void Warn(string text, Exception exception)
        {
            try
            {
                if (!Allow(text)) return;
                Log l = log;
                if (l != null) l.Warn(text, exception);
                else SelfLog.Write(SelfLog.Warn, "[pc] " + text + (exception == null ? "" : ": " + exception));
            }
            catch (Exception)
            {
            }
        }

        /// <summary>M1/M4: a WARN milestone in the boot trace and the full exception in the log (as the headless-World hook does).</summary>
        private static void WarnWithBoot(string text, Exception exception)
        {
            try
            {
                BootTrace b = boot;
                if (b != null) b.Warn(text);
                Log l = log;
                if (l != null) l.Warn(text, exception);
                else if (b == null) SelfLog.Write(SelfLog.Warn, "[headless] " + text + ": " + exception);
            }
            catch (Exception)
            {
            }
        }
    }
}
#endif
