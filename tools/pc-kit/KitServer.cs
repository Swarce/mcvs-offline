using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using McvsOffline.Patcher.Pc;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// The PC McvsOffline.Server.dll the kit carries as an embedded resource (docs/design/pc-port.md §9.1), and the temporary
    /// folders <c>%TEMP%\mcvs-pckit-&lt;guid&gt;\</c> it is written to for the resolver during 'install'.
    /// </summary>
    public static class KitServer
    {
        public const string ResourceName = "McvsOffline.Server.dll";
        public const string TempPrefix = "mcvs-pckit-";

        /// <summary>The kit's server DLL, or null when the kit was built without it (a development build).</summary>
        public static byte[] Embedded()
        {
            using (Stream stream = typeof(KitServer).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return null;
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }

        /// <summary>A new, empty <c>mcvs-pckit-&lt;guid&gt;</c> folder under <paramref name="tempRoot"/>.</summary>
        public static string CreateTempFolder(string tempRoot)
        {
            string folder = Path.Combine(tempRoot, TempPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Deletes a temp folder; a failure is only logged (it is retried by <see cref="CleanStale"/> on a later start).</summary>
        public static void DeleteTempFolder(string folder, KitLog log)
        {
            if (string.IsNullOrEmpty(folder)) return;
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (log != null) log.Warn("could not delete the temporary folder " + folder + ": " + ex.Message);
            }
        }

        /// <summary>Deletes <c>mcvs-pckit-*</c> folders older than <paramref name="age"/> (left by a killed run); returns how many went.</summary>
        public static int CleanStale(string tempRoot, DateTime nowUtc, TimeSpan age)
        {
            if (string.IsNullOrEmpty(tempRoot) || !Directory.Exists(tempRoot)) return 0;
            int removed = 0;
            string[] folders;
            try
            {
                folders = Directory.GetDirectories(tempRoot, TempPrefix + "*");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return 0;
            }
            foreach (string folder in folders)
            {
                try
                {
                    if (nowUtc - Directory.GetLastWriteTimeUtc(folder) < age) continue;
                    Directory.Delete(folder, true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            return removed;
        }

        /// <summary>The kit's version (Directory.Build.props), without a source-revision suffix.</summary>
        public static string Version()
        {
            var attribute = typeof(KitServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            string version = attribute == null ? typeof(KitServer).Assembly.GetName().Version.ToString() : attribute.InformationalVersion;
            int plus = version.IndexOf('+');
            return plus < 0 ? version : version.Substring(0, plus);
        }

        /// <summary>
        /// The kit's own file: the single-file exe the player runs (<see cref="Environment.ProcessPath"/>), or this assembly when it
        /// runs under the dotnet host (a development build). Null when neither is known.
        /// </summary>
        public static string OwnPath()
        {
            string process = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(process) && !string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
                return process;
            string assembly = typeof(KitServer).Assembly.Location;
            return string.IsNullOrEmpty(assembly) ? null : assembly;
        }

        private static readonly object hashGate = new object();
        private static string hashedPath;
        private static string hashedValue;

        /// <summary>
        /// "kit 0.1.0, sha256 0ca7d218…" (the whole hash) for the header of every pc-kit.log command and summary.txt (WR2 F9:
        /// a report names exactly which kit wrote it); "sha256 unknown" when the file cannot be read. Hashed once per path.
        /// </summary>
        public static string Identity(KitEnvironment env)
        {
            string path = null;
            try
            {
                path = env == null || env.KitPath == null ? null : env.KitPath();
            }
            catch (Exception)
            {
            }
            return "kit " + Version() + ", sha256 " + (Sha256Of(path) ?? "unknown");
        }

        private static string Sha256Of(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (hashGate)
            {
                if (string.Equals(hashedPath, path, StringComparison.Ordinal)) return hashedValue;
                string value = GameFolder.TrySha256(path);
                hashedPath = path;
                hashedValue = value;
                return value;
            }
        }

        public static string Stamp(DateTime time)
        {
            return time.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }
    }
}
