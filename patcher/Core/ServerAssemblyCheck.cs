using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace McvsOffline.Patcher.Core
{
    /// <summary>
    /// What McvsOffline.Server.dll and the patched assemblies may reference for one build (docs/design/pc-port.md §7.2, P3):
    /// the server is named McvsOffline.Server, references only the build's allowed assemblies (the BCL ones with the corlib
    /// version and token of the game's own assemblies, so a second mscorlib can never appear), and on PC imports only the
    /// allowed native modules. ILVerify does not notice a second mscorlib ([PP-V] §3), so this check is the only guard.
    /// Failures are <see cref="InvalidDataException"/> with one sentence for the player.
    /// </summary>
    public static class ServerAssemblyCheck
    {
        public const string ServerAssemblyName = "McvsOffline.Server";

        /// <summary>The assemblies whose references must carry the build's corlib version and public key token.</summary>
        public static readonly IReadOnlyCollection<string> BclNames = new[] { "mscorlib", "System", "System.Core" };

        /// <summary>Named in the PC refusal (a build of the wrong project): they are outside the allowed set anyway.</summary>
        public static readonly IReadOnlyCollection<string> PcRefusedNames = new[] { "Mono.Android", "Java.Interop", "System.Runtime", "netstandard", "System.Windows.Forms" };

        /// <summary>Gameloft.Logs as the Steam build ships it: 1.0.0.0, not strong-named.</summary>
        public static readonly Version PcGameLogsVersion = new Version(1, 0, 0, 0);

        /// <summary>Checks the server DLL for <paramref name="build"/>; <paramref name="path"/> only names it in the messages.</summary>
        public static void Check(byte[] bytes, string path, BuildProfile build)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (build == null) throw new ArgumentNullException(nameof(build));
            string hint = build.IsPc
                ? "Build server/McvsOffline.Server.Pc.csproj (scripts/build.sh) and use its McvsOffline.Server.dll."
                : "Build server/McvsOffline.Server.Embedded.csproj against your APK's assemblies (scripts/build.sh).";
            using (var pe = new PEReader(new MemoryStream(bytes)))
            {
                if (!pe.HasMetadata) throw new InvalidDataException(path + " is not a .NET assembly");
                MetadataReader md = pe.GetMetadataReader();
                string name = md.GetString(md.GetAssemblyDefinition().Name);
                if (name != ServerAssemblyName) throw new InvalidDataException(path + " is " + name + ", expected " + ServerAssemblyName);
                int corlibs = 0;
                foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
                {
                    AssemblyReference r = md.GetAssemblyReference(h);
                    string refName = md.GetString(r.Name);
                    if (!build.ServerAllowedReferences.Contains(refName))
                    {
                        string why = build.IsPc && PcRefusedNames.Contains(refName) ? " (" + refName + " is never available in the PC game)" : "";
                        throw new InvalidDataException(path + " references " + refName + why + ": that is not the " +
                                                       (build.IsPc ? "PC" : "embedded") + " build. " + hint);
                    }
                    if (refName == "mscorlib") corlibs++;
                    if (BclNames.Contains(refName))
                    {
                        string token = Token(md.GetBlobBytes(r.PublicKeyOrToken));
                        if (r.Version != build.CorlibVersion || token != build.CorlibPublicKeyToken)
                        {
                            throw new InvalidDataException(path + " references " + refName + " " + r.Version + " (" + (token.Length == 0 ? "no token" : token) +
                                                           "), expected the game's " + build.CorlibVersion + " (" + build.CorlibPublicKeyToken + "). " + hint);
                        }
                    }
                    if (build.IsPc && refName == "Gameloft.Logs" && r.Version != PcGameLogsVersion)
                        throw new InvalidDataException(path + " references Gameloft.Logs " + r.Version + ", expected the game's " + PcGameLogsVersion);
                }
                if (corlibs > 1) throw new InvalidDataException(path + " references mscorlib " + corlibs + " times. " + hint);
                if (build.ServerAllowedModuleRefs == null) return;
                int moduleRefs = md.GetTableRowCount(TableIndex.ModuleRef);
                for (int row = 1; row <= moduleRefs; row++)
                {
                    string module = md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name);
                    if (!build.ServerAllowedModuleRefs.Contains(NativeModuleName(module)))
                    {
                        throw new InvalidDataException(path + " imports native code from " + module + ", which the PC build must not use (allowed: " +
                                                       string.Join(", ", build.ServerAllowedModuleRefs.OrderBy(m => m, StringComparer.Ordinal)) + "). " + hint);
                    }
                }
            }
        }

        /// <summary>The assembly references of the server DLL, as "name, Version=v, PublicKeyToken=t" (the kit's publish check prints them).</summary>
        public static List<string> References(byte[] bytes)
        {
            var list = new List<string>();
            using (var pe = new PEReader(new MemoryStream(bytes)))
            {
                MetadataReader md = pe.GetMetadataReader();
                foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
                {
                    AssemblyReference r = md.GetAssemblyReference(h);
                    string token = Token(md.GetBlobBytes(r.PublicKeyOrToken));
                    list.Add(md.GetString(r.Name) + ", Version=" + r.Version + ", PublicKeyToken=" + (token.Length == 0 ? "null" : token));
                }
            }
            return list;
        }

        /// <summary>
        /// P3 for one patched assembly: it references exactly its original references plus, when a patch calls a hook,
        /// McvsOffline.Server with the server's own version, and still exactly one mscorlib with the build's version and token.
        /// </summary>
        public static void CheckPatchedReferences(string file, byte[] original, byte[] patched, Version serverVersion, BuildProfile build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            List<string> before = References(original);
            List<string> after = References(patched);
            string server = ServerAssemblyName + ", Version=" + serverVersion + ", PublicKeyToken=null";
            foreach (string reference in before)
            {
                if (!after.Contains(reference)) throw new InvalidDataException(file + ": the patched copy lost its reference to " + reference);
            }
            foreach (string reference in after)
            {
                if (before.Contains(reference) || reference == server) continue;
                throw new InvalidDataException(file + ": the patched copy references " + reference + ", which the original does not (allowed: " + server + ")");
            }
            string corlib = "mscorlib, Version=" + build.CorlibVersion + ", PublicKeyToken=" + build.CorlibPublicKeyToken;
            int corlibs = after.Count(r => r.StartsWith("mscorlib,", StringComparison.Ordinal));
            if (corlibs != 1 || !after.Contains(corlib))
                throw new InvalidDataException(file + ": the patched copy must reference exactly one mscorlib (" + corlib + "), it has " + corlibs);
        }

        /// <summary>"KERNEL32.dll" → "kernel32"; the comparison the allowed module set uses.</summary>
        public static string NativeModuleName(string module)
        {
            string name = (module ?? "").Trim().ToLowerInvariant();
            return name.EndsWith(".dll", StringComparison.Ordinal) ? name.Substring(0, name.Length - 4) : name;
        }

        private static string Token(byte[] keyOrToken)
        {
            if (keyOrToken == null || keyOrToken.Length == 0) return "";
            byte[] token = keyOrToken;
            if (keyOrToken.Length != 8)
            {
                // A full public key: the token is the last 8 bytes of its SHA-1, reversed.
                using (var sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(keyOrToken);
                    token = hash.Skip(hash.Length - 8).Reverse().ToArray();
                }
            }
            return string.Concat(token.Select(b => b.ToString("x2")));
        }
    }
}
