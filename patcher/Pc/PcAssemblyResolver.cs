using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace McvsOffline.Patcher.Pc
{
    /// <summary>
    /// Resolves the references of the PC assemblies from exactly the directories it is given, in order: the .NET Framework
    /// BCL directory (mscorlib, System*, Microsoft.*), the game folder, the server DLL's folder; <c>&lt;name&gt;.dll</c>, then
    /// <c>.exe</c> (docs/design/pc-port.md §7.3, C12). It implements <see cref="IAssemblyResolver"/> directly: Cecil's
    /// BaseAssemblyResolver falls back to the trusted platform assemblies (netstandard build) or the framework directory and
    /// the GAC, so a forgotten BCL directory would silently check members against .NET 8's facades instead of the game's
    /// .NET Framework 4. No GAC, no TPA, no current directory, no ResolveFailure event: a name found nowhere throws
    /// <see cref="AssemblyResolutionException"/> (the installer's exit code 3).
    /// </summary>
    public sealed class PcAssemblyResolver : IAssemblyResolver
    {
        private readonly List<string> directories = new List<string>();
        private readonly Dictionary<string, AssemblyDefinition> cache = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private bool disposed;

        /// <param name="bclDirectory">Mandatory: must hold mscorlib.dll (Windows: %WINDIR%\Microsoft.NET\Framework\v4.0.30319;
        /// Linux: MCVS_NET452_REFS).</param>
        /// <param name="gameFolder">The Steam install folder (may be null).</param>
        /// <param name="serverFolder">The folder of the PC McvsOffline.Server.dll (may be null).</param>
        public PcAssemblyResolver(string bclDirectory, string gameFolder, string serverFolder)
        {
            if (string.IsNullOrEmpty(bclDirectory)) throw new ArgumentException("the BCL directory is required", nameof(bclDirectory));
            if (!File.Exists(Path.Combine(bclDirectory, "mscorlib.dll")))
                throw new ArgumentException(bclDirectory + " has no mscorlib.dll (not a .NET Framework 4 directory)", nameof(bclDirectory));
            BclDirectory = Path.GetFullPath(bclDirectory);
            directories.Add(BclDirectory);
            if (!string.IsNullOrEmpty(gameFolder)) directories.Add(Path.GetFullPath(gameFolder));
            if (!string.IsNullOrEmpty(serverFolder)) directories.Add(Path.GetFullPath(serverFolder));
        }

        public string BclDirectory { get; }

        /// <summary>The directories searched, in order.</summary>
        public IReadOnlyList<string> Directories { get { return directories; } }

        /// <summary>What every PC assembly is read with: immediate, in memory, this resolver.</summary>
        public ReaderParameters CreateReaderParameters()
        {
            return new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true, AssemblyResolver = this };
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, CreateReaderParameters());
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(PcAssemblyResolver));
                AssemblyDefinition cached;
                if (cache.TryGetValue(name.FullName, out cached)) return cached;
                string file = Find(name.Name);
                if (file == null) throw new AssemblyResolutionException(name);
                // Always our own resolver, whatever the caller passed: a hit must never resolve its own references elsewhere.
                // Deferred (as Cecil's own resolvers read references): an immediate read of System.dll would resolve its
                // attribute types while it is read, and the BCL's references are cyclic (System <-> System.Configuration).
                var reader = new ReaderParameters { ReadingMode = ReadingMode.Deferred, InMemory = true, AssemblyResolver = this };
                AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(file, reader);
                cache[name.FullName] = assembly;
                return assembly;
            }
        }

        /// <summary>The first <c>&lt;name&gt;.dll</c> or <c>&lt;name&gt;.exe</c> in <see cref="Directories"/>, or null.</summary>
        public string Find(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName) || assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            foreach (string dir in directories)
            {
                foreach (string extension in new[] { ".dll", ".exe" })
                {
                    string candidate = Path.Combine(dir, assemblyName + extension);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (AssemblyDefinition assembly in cache.Values) assembly.Dispose();
                cache.Clear();
            }
        }
    }
}
