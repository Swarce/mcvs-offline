using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using McvsOffline.Patcher.Pc;
using Mono.Cecil;
using Xunit;

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

        // ------------------------------------------------------------------ the Steam (PC) build (docs/design/pc-port.md §3.4, §12)

        /// <summary>
        /// The read-only Steam install (MCVS_PC_REFS, default /home/user/mcvs-work/pc/install) when it holds the game's
        /// assemblies, or null. PC real-file tests skip without it, unless <see cref="RequirePc"/>.
        /// </summary>
        public static string PcRefs
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_PC_REFS");
                if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/pc/install";
                return File.Exists(Path.Combine(dir, "Gameloft.Engine.dll")) && File.Exists(Path.Combine(dir, "ModernCombatVersus.exe")) ? dir : null;
            }
        }

        /// <summary>MCVS_REQUIRE_PC=1: PC real-file tests fail instead of skipping when a file is missing.</summary>
        public static bool RequirePc
        {
            get { return Environment.GetEnvironmentVariable("MCVS_REQUIRE_PC") == "1"; }
        }

        /// <summary>
        /// The .NET Framework 4.5.2 reference assemblies (MCVS_NET452_REFS, default the NuGet package
        /// microsoft.netframework.referenceassemblies.net452 1.0.3): the BCL directory of every Linux PC patch run, or null.
        /// </summary>
        public static string Net452Refs
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_NET452_REFS");
                if (string.IsNullOrEmpty(dir))
                {
                    string home = Environment.GetEnvironmentVariable("HOME") ?? "";
                    dir = Path.Combine(home, ".nuget", "packages", "microsoft.netframework.referenceassemblies.net452", "1.0.3", "build",
                                       ".NETFramework", "v4.5.2");
                }
                return File.Exists(Path.Combine(dir, "mscorlib.dll")) ? Path.GetFullPath(dir) : null;
            }
        }

        /// <summary>
        /// The PC server DLL of this test assembly's own configuration (artifacts/bin/McvsOffline.Server.Pc/&lt;config&gt;/), or
        /// null when it was not built. Never another configuration's (a stale Debug DLL must not pass a Release run).
        /// </summary>
        public static string PcServerDll
        {
            get
            {
                string config = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                string dll = Path.Combine(RepoRoot, "artifacts", "bin", "McvsOffline.Server.Pc", config, "McvsOffline.Server.dll");
                return File.Exists(dll) ? dll : null;
            }
        }

        /// <summary>
        /// How every PC real-file test reads a PC assembly (C12): immediate, in memory, with a <see cref="PcAssemblyResolver"/> over
        /// <see cref="Net452Refs"/>, the game folder and the server folder, nothing else. Dispose the resolver
        /// (<c>reader.AssemblyResolver</c>) when done.
        /// </summary>
        public static ReaderParameters PcReader(string gameFolder, string serverFolder)
        {
            string bcl = Net452Refs;
            if (bcl == null) throw new InvalidOperationException("the .NET Framework 4.5.2 reference assemblies are missing (MCVS_NET452_REFS)");
            return new PcAssemblyResolver(bcl, gameFolder, serverFolder).CreateReaderParameters();
        }

        /// <summary>
        /// Asserts that <paramref name="module"/>'s mscorlib resolves, through the module's own resolver, to the file in
        /// <see cref="Net452Refs"/> (4.0.0.0, b77a5c561934e089), not to .NET 8's facade, which framework-dependent Cecil finds through
        /// the trusted platform assemblies and which would check members against the wrong library ([PP-V] §9).
        /// </summary>
        public static void AssertPcCorlib(ModuleDefinition module)
        {
            Assert.NotNull(module);
            AssemblyNameReference corlib = module.AssemblyReferences.FirstOrDefault(r => r.Name == "mscorlib");
            Assert.True(corlib != null, module.Name + " references no mscorlib");
            Assert.Equal(new Version(4, 0, 0, 0), corlib.Version);
            Assert.Equal("b77a5c561934e089", string.Concat((corlib.PublicKeyToken ?? new byte[0]).Select(b => b.ToString("x2"))));
            AssemblyDefinition resolved = module.AssemblyResolver.Resolve(corlib);
            Assert.True(resolved != null, "mscorlib did not resolve for " + module.Name);
            string file = resolved.MainModule.FileName;
            Assert.False(string.IsNullOrEmpty(file), "the resolved mscorlib has no file name");
            string bcl = Net452Refs;
            Assert.True(bcl != null, "MCVS_NET452_REFS is missing");
            Assert.True(string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), bcl.TrimEnd('/'), StringComparison.Ordinal),
                        module.Name + ": mscorlib resolved to " + file + ", not into " + bcl);
        }

        private static readonly object PruneLock = new object();
        private static bool pruned;

        /// <summary>A new folder under the temp dir. Tests never delete theirs (APKs and DLLs often stay mapped), so the
        /// first call of a run removes folders older than an hour, which earlier runs left behind.</summary>
        public static string NewTempDir(string prefix)
        {
            string root = Path.Combine(Path.GetTempPath(), "mcvs-embed-tests");
            PruneStale(root, TimeSpan.FromHours(1));
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
