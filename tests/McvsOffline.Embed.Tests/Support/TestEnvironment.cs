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

        public static string NewTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), "mcvs-embed-tests", prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
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
