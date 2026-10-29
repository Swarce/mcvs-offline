using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McvsOffline.Patcher.Patches;
using Mono.Cecil;

namespace McvsOffline.Patcher.Core
{
    /// <summary>
    /// Patches one assembly in memory with a group of IL patches and gives it a new, deterministic MVID (the APK path and the
    /// PC installer share it; docs/design/pc-port.md §3.4). Every patch checks its sites before it changes anything, so a
    /// <see cref="PatchException"/> leaves nothing written.
    /// </summary>
    public static class AssemblyPatcher
    {
        /// <summary>Applies the group's patches to one assembly and gives it a new (deterministic) MVID.</summary>
        public static byte[] Patch(byte[] original, IList<IlPatch> group, PatchContext ctx, ReaderParameters reader, Guid serverMvid,
                                   Action<string> log)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(original), reader))
            {
                foreach (IlPatch patch in group)
                {
                    log("patch " + patch.Id + ": " + patch.Description);
                    patch.Apply(module, ctx);
                }
                // A new MVID is essential: other assemblies' AOT images record this one's MVID, and a match would make
                // Mono run AOT code compiled against the unpatched metadata (verify-C7 §3). On PC it marks our copy.
                module.Mvid = DerivedMvid(module.Mvid, group.Select(p => p.Id), serverMvid);
                var stream = new MemoryStream();
                module.Write(stream);
                byte[] patched = stream.ToArray();
                using (ModuleDefinition check = ModuleDefinition.ReadModule(new MemoryStream(patched), reader))
                {
                    if (check.Mvid != module.Mvid) throw new InvalidOperationException("re-reading " + module.Name + " failed");
                }
                return patched;
            }
        }

        /// <summary>The MVID of a patched assembly: a hash of the original MVID, the server's MVID and the patch ids, as a v4 GUID.</summary>
        public static Guid DerivedMvid(Guid original, IEnumerable<string> patchIds, Guid serverMvid)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] seed = original.ToByteArray().Concat(serverMvid.ToByteArray())
                    .Concat(Encoding.UTF8.GetBytes("mcvs-offline:" + string.Join(",", patchIds))).ToArray();
                byte[] hash = sha.ComputeHash(seed);
                var bytes = new byte[16];
                Array.Copy(hash, bytes, 16);
                bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // RFC 4122 version 4 (in .NET's byte order)
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // variant
                return new Guid(bytes);
            }
        }
    }
}
