using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using McvsOffline.Server.Core.Http;

namespace McvsOffline.Server.Core
{
    /// <summary>Process-relative time for boot traces: zero is the first call (the embedded host calls it first thing).</summary>
    public static class BootClock
    {
        private static int startTick;
        private static int started;

        /// <summary>Starts the clock if it is not running yet.</summary>
        public static void Touch()
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) == 0) startTick = Environment.TickCount;
        }

        public static int ElapsedMs
        {
            get
            {
                Touch();
                return Environment.TickCount - startTick;
            }
        }

        /// <summary>"+12.345 s".</summary>
        public static string Stamp()
        {
            return "+" + (ElapsedMs / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + " s";
        }
    }

    /// <summary>
    /// The boot trace (device test 1): one "[boot]" line per milestone of the offline boot chain, recognised from the requests
    /// the game makes (ARCHITECTURE §2.2, verify-C2, STATUS "expected log sequence"), plus the milestones hosts and game hooks
    /// report (<see cref="Mark"/>). While the game boots, a quiet period without any request is reported after 10, 30, 60 and
    /// 120 s with the last milestone and the last request, so a stall like RUN 1 (nothing after the LocalAnubis datacenter
    /// request for 20 s) names itself in our own log. Cheap and thread-safe; nothing here can fail a request.
    /// </summary>
    public sealed class BootTrace : IDisposable
    {
        private static readonly int[] QuietReportMs = { 10000, 30000, 60000, 120000 };
        private const int BootWindowMs = 10 * 60 * 1000;

        /// <summary>Route names of the settings page (server/Services/Admin).</summary>
        private const string AdminRoutePrefix = "admin.";

        private readonly object gate = new object();
        private readonly Log log;
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> milestones = new List<string>();
        private Thread monitor;
        private volatile bool disposed;
        private int firstRequestMs = -1;
        private int lastRequestMs = -1;
        private string lastRequest;
        private string lastMilestone;
        private int lastMilestoneMs;
        private int inFlight;
        private long requests;
        private int quietReports;
        private int tsSessionMs = -1;

        public BootTrace(Log log)
        {
            this.log = log ?? Log.Null;
            BootClock.Touch();
        }

        /// <summary>Starts the quiet-period monitor (a background thread that wakes once a second).</summary>
        public void StartMonitor()
        {
            lock (gate)
            {
                if (monitor != null || disposed) return;
                monitor = new Thread(RunMonitor);
                monitor.IsBackground = true;
                monitor.Name = "mcvs-boot-trace";
                monitor.Start();
            }
        }

        /// <summary>Logs a milestone: "[boot] +12.345 s text".</summary>
        public void Mark(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string stamp = BootClock.Stamp();
            lock (gate)
            {
                lastMilestone = text;
                lastMilestoneMs = BootClock.ElapsedMs;
                if (milestones.Count < 200) milestones.Add(stamp + " " + text);
            }
            log.Info(stamp + " " + text);
        }

        /// <summary>A warning milestone (something that should not happen during boot).</summary>
        public void Warn(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string stamp = BootClock.Stamp();
            lock (gate)
            {
                if (milestones.Count < 200) milestones.Add(stamp + " WARN " + text);
            }
            log.Warn(stamp + " " + text);
        }

        /// <summary>Called when a request arrives (before it is routed).</summary>
        public void RequestStarted(HttpRequest request)
        {
            int now = BootClock.ElapsedMs;
            lock (gate)
            {
                if (firstRequestMs < 0) firstRequestMs = now;
                lastRequestMs = now;
                lastRequest = "#" + request.Id.ToString(CultureInfo.InvariantCulture) + " " + request.Method + " " +
                              (request.Path.Length > 120 ? request.Path.Substring(0, 120) + "..." : request.Path);
                inFlight++;
                requests++;
                quietReports = 0;
            }
        }

        /// <summary>Called with the response; recognises milestones. Never throws.</summary>
        public void RequestFinished(HttpRequest request, HttpResponse response)
        {
            lock (gate)
            {
                if (inFlight > 0) inFlight--;
            }
            try
            {
                string milestone = Recognise(request, response);
                if (milestone != null) Mark(milestone + " (#" + request.Id.ToString(CultureInfo.InvariantCulture) + ")");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>For crash reports and the watchdog: "last request #26 GET /verify 3.2 s ago, 0 in flight; last milestone ...".</summary>
        public string Describe()
        {
            lock (gate)
            {
                int now = BootClock.ElapsedMs;
                var sb = new StringBuilder();
                sb.Append(requests.ToString(CultureInfo.InvariantCulture)).Append(" requests");
                if (lastRequest != null)
                {
                    sb.Append(", last ").Append(lastRequest).Append(' ')
                      .Append(((now - lastRequestMs) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s ago");
                }
                sb.Append(", ").Append(inFlight.ToString(CultureInfo.InvariantCulture)).Append(" in flight");
                if (lastMilestone != null)
                {
                    sb.Append("; last milestone '").Append(lastMilestone).Append("' ")
                      .Append(((now - lastMilestoneMs) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s ago");
                }
                return sb.ToString();
            }
        }

        /// <summary>Every milestone so far (oldest first, at most 200).</summary>
        public List<string> Milestones()
        {
            lock (gate) return new List<string>(milestones);
        }

        public void Dispose()
        {
            disposed = true;
        }

        private void RunMonitor()
        {
            while (!disposed)
            {
                try
                {
                    Thread.Sleep(1000);
                    string report = null;
                    lock (gate)
                    {
                        int now = BootClock.ElapsedMs;
                        if (firstRequestMs < 0 || now - firstRequestMs > BootWindowMs || inFlight > 0) continue;
                        int quiet = now - lastRequestMs;
                        if (quietReports < QuietReportMs.Length && quiet >= QuietReportMs[quietReports])
                        {
                            quietReports++;
                            report = "quiet: no request for " + (quiet / 1000).ToString(CultureInfo.InvariantCulture) + " s since " + lastRequest +
                                     "; last milestone '" + (lastMilestone ?? "-") + "' (normal once the main menu is up; during the loading " +
                                     "screen it means the game waits for something: see the watchdog lines)";
                        }
                    }
                    if (report != null) log.Info(BootClock.Stamp() + " " + report);
                }
                catch (Exception)
                {
                }
            }
        }

        private int Count(string key)
        {
            lock (gate)
            {
                int n;
                counts.TryGetValue(key, out n);
                counts[key] = ++n;
                return n;
            }
        }

        /// <summary>
        /// Maps a request to a milestone name (first occurrences only), or null. Never one for the settings page
        /// (<c>admin.*</c> routes, any <c>/_mcvs/</c> path): it is not on the boot path, and its first status probe is a 401
        /// by design (no session cookie yet), which as "request failed" became status.txt's last milestone (WR2 F8).
        /// </summary>
        private string Recognise(HttpRequest request, HttpResponse response)
        {
            string route = request.RouteName ?? "";
            if (route.StartsWith(AdminRoutePrefix, StringComparison.Ordinal) || LocalAccessGate.IsAdminPath(request)) return null;
            int status = response == null ? 0 : response.StatusCode;
            string path = request.Path ?? "";
            if (status >= 400)
            {
                // A failure on the boot path is always worth a line of its own (the request log has the details).
                return Count("fail:" + route) <= 3 ? "request failed: " + request.Method + " " + path + " -> " + status.ToString(CultureInfo.InvariantCulture) : null;
            }
            switch (route)
            {
                case "eve.datacenters":
                    return Count("datacenters") == 1 ? "eve datacenter list (a Federation without -DataCenter: expected only with old args.txt)" : null;
                case "eve.urls":
                    {
                        int n = Count("eve.urls");
                        if (n == 1) return "client Federation: eve config";
                        if (n == 2) return "second Federation: eve config (LocalAnubis)";
                        if (n == 3) return "third Federation: eve config (TS World)";
                        return null;
                    }
                case "auth.authorize":
                    {
                        string type = request.Param("credential_type") ?? "";
                        string scope = request.Param("scope") ?? "";
                        string device = request.Param("device_id") ?? "";
                        if (type == "anonymous" && request.Param("for_username") == null)
                            return Count("anon") == 1 ? "client anonymous session (Authentication.Connect)" : null;
                        if (request.Param("for_username") != null || request.Param("for_credential_type") != null)
                            return Count("onbehalf") == 1 ? "TS handshake: session on behalf of the player" : null;
                        if (scope.IndexOf("auth_admin_ro", StringComparison.Ordinal) >= 0)
                            return Count("anubis") == 1 ? "LocalAnubisManager.Start: super session (Embedded mode)" : null;
                        if (scope.IndexOf("lobby_game_admin", StringComparison.Ordinal) >= 0)
                            return Count("finder") == 1 ? "QuickLaunch: Finder session (lobby_game_admin)" : null;
                        if (device == "default" && scope.IndexOf("storage", StringComparison.Ordinal) >= 0)
                        {
                            if (Count("ts") != 1) return null;
                            lock (gate) tsSessionMs = BootClock.ElapsedMs;
                            return "TS World: game-server session (the TS World exists)";
                        }
                        return null;
                    }
                case "matchmaker.lobbyconfigs":
                    {
                        if (path.EndsWith("/gs", StringComparison.Ordinal))
                            return "GS match: lobbyconfigs gs #" + Count("lobby.gs").ToString(CultureInfo.InvariantCulture);
                        int n = Count("lobby.ts");
                        if (n == 1) return "QuickLaunch: lobbyconfigs ts";
                        if (n == 2) return "Controller 'connect game': creating the TS World (Args = cmd_args)";
                        return null;
                    }
                case "auth.verify":
                    {
                        if (request.Param("credential") == null) return null;
                        int n = Count("verify.anubis");
                        if (n == 1) return "Controller login of the client (verify system:Anubis)";
                        if (n == 2) return "TS lobby login: the TS World now loads Levels/Menus.level";
                        return null;
                    }
                case "crm.config.selector":
                case "crm.config":
                case "crm.config.user":
                case "crm.config.user.selector":
                    {
                        if (Count("crm") != 1) return null;
                        int since;
                        lock (gate) since = tsSessionMs < 0 ? -1 : BootClock.ElapsedMs - tsSessionMs;
                        string when = since < 0 ? "" : " " + since.ToString(CultureInfo.InvariantCulture) + " ms after the TS session";
                        if (since > 1000) Warn("CRM config came " + since.ToString(CultureInfo.InvariantCulture) + " ms after the TS session (the TS reads IsDevelopment within 1 s, verify-C5)");
                        return "first CRM config: " + path + when;
                    }
                case "alerts.listen":
                    return Count("alerts") == 1 ? "alert stream open (the TS handshake reached the player's session)" : null;
                case "geo.location":
                case "geo.location.path":
                case "geo.country":
                    return Count("geo") == 1 ? "TS handshake: geolocation lookup answered locally" : null;
                case "auth.alias":
                    return Count("alias") == 1 ? "TS handshake: games/mygame/alias" : null;
                case "auth.users.by_device":
                    return Count("bydevice") == 1 ? "TS handshake: accounts for this device" : null;
            }
            if (route.StartsWith("storage.profile", StringComparison.Ordinal) || route.StartsWith("storage.field", StringComparison.Ordinal))
            {
                bool write = request.Method != "GET";
                if (!write) return Count("storage.read") == 1 ? "TS handshake: first profile read (Seshat)" : null;
                return Count("storage.write") == 1 ? "first profile write (Seshat)" : null;
            }
            return null;
        }
    }
}
