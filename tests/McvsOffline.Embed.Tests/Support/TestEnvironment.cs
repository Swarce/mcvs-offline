using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace McvsOffline.Embed.Tests.Support
{
    /// <summary>Paths and optional inputs (the player's game files and Android build-tools are never in the repository).</summary>
    public static class TestEnvironment
    {
        public static string RepoRoot
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "McvsOffline.sln"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new InvalidOperationException("repository root (McvsOffline.sln) not found above " + AppContext.BaseDirectory);
                return dir;
            }
        }

        /// <summary>The APK's assemblies/ folder (MCVS_REFS, as for the embedded build), or null.</summary>
        public static string GameAssemblies
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_REFS");
                if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/extracted/assemblies";
                return File.Exists(Path.Combine(dir, "NicePlaySDK.dll")) ? dir : null;
            }
        }

        /// <summary>The player's original APK (MCVS_APK), or null.</summary>
        public static string GameApk
        {
            get
            {
                string apk = Environment.GetEnvironmentVariable("MCVS_APK");
                if (string.IsNullOrEmpty(apk)) apk = "/home/user/mcvs-work/apk/mcvs.apk";
                return File.Exists(apk) ? apk : null;
            }
        }

        /// <summary>Android build-tools folder from MCVS_BUILD_TOOLS, or null (then PATH/ANDROID_HOME are searched).</summary>
        public static string BuildToolsDir
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_BUILD_TOOLS");
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                const string local = "/home/user/mcvs-work/tools/android/build-tools/34.0.0";
                return Directory.Exists(local) ? local : null;
            }
        }

        /// <summary>aapt2 from <see cref="BuildToolsDir"/> or PATH, or null.</summary>
        public static string Aapt2
        {
            get
            {
                string dir = BuildToolsDir;
                if (dir != null && File.Exists(Path.Combine(dir, "aapt2"))) return Path.Combine(dir, "aapt2");
                foreach (string p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                {
                    if (p.Length > 0 && File.Exists(Path.Combine(p, "aapt2"))) return Path.Combine(p, "aapt2");
                }
                return null;
            }
        }

        /// <summary>The embedded build of the server, if scripts/build.sh produced it.</summary>
        public static string EmbeddedServerDll
        {
            get
            {
                string bin = Path.Combine(RepoRoot, "artifacts", "bin", "McvsOffline.Server.Embedded");
                if (!Directory.Exists(bin)) return null;
                return Directory.GetFiles(bin, "McvsOffline.Server.dll", SearchOption.AllDirectories)
                                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
        }

        private static readonly object PruneLock = new object();
        private static bool pruned;

        /// <summary>A new folder under the temp dir. Tests never delete theirs (APKs and DLLs often stay mapped), so the
        /// first call of a run removes folders older than six hours, which earlier runs left behind.</summary>
        public static string NewTempDir(string prefix)
        {
            string root = Path.Combine(Path.GetTempPath(), "mcvs-embed-tests");
            PruneStale(root, TimeSpan.FromHours(6));
            string dir = Path.Combine(root, prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void PruneStale(string root, TimeSpan age)
        {
            lock (PruneLock)
            {
                if (pruned) return;
                pruned = true;
                if (!Directory.Exists(root)) return;
                DateTime cutoff = DateTime.UtcNow - age;
                foreach (string old in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(old) < cutoff) Directory.Delete(old, true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
