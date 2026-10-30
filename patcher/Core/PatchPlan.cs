using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Patcher.Patches;

namespace McvsOffline.Patcher.Core
{
    /// <summary>The patch ids of one build, by role (docs/design/pc-port.md §8.1).</summary>
    public sealed class PatchIds
    {
        public PatchIds(IEnumerable<string> mandatory, IEnumerable<string> defaults, IEnumerable<string> optional, IEnumerable<string> excluded)
        {
            Mandatory = mandatory.ToList().AsReadOnly();
            Default = defaults.ToList().AsReadOnly();
            Optional = optional.ToList().AsReadOnly();
            Excluded = excluded.ToList().AsReadOnly();
        }

        /// <summary>Always applied.</summary>
        public IReadOnlyList<string> Mandatory { get; }

        /// <summary>Applied unless the player chooses otherwise (--patch default).</summary>
        public IReadOnlyList<string> Default { get; }

        /// <summary>Accepted on request, not applied by default.</summary>
        public IReadOnlyList<string> Optional { get; }

        /// <summary>Ids of the other build: asking for one is an error (§8.8).</summary>
        public IReadOnlyList<string> Excluded { get; }
    }

    /// <summary>
    /// Which IL patches a build gets, in the build's fixed order (docs/design/pc-port.md §8.1). Android is exactly
    /// <see cref="ApkPatcher.SelectPatches"/> (P5: same ids, same order; non-IL ids such as <c>targetsdk</c> select no IL patch);
    /// the PC list lives here only.
    /// </summary>
    public static class PatchPlan
    {
        /// <summary>The PC order: per file as ApkPatcher.AllPatches, then the new ones (§8.1).</summary>
        private static readonly string[] PcOrder =
        {
            "hook-pc",
            "headless", "bottile", "store", "signatures", "inbox", "adminpage", "forum",
            "headless-engine", "physics", "quicklaunch", "headless-diag", "nativeonline", "waits", "adminlink", "graphics", "gameload",
            "geo",
        };

        private static readonly PatchIds PcIds = new PatchIds(
            new[] { "hook-pc", "headless", "headless-engine", "physics", "quicklaunch", "headless-diag", "graphics", "gameload" },
            new[] { "nativeonline", "waits", "adminlink", "geo", "bottile", "store", "signatures", "inbox", "adminpage" },
            new[] { "forum" },
            new[] { "hook", "finder", "lan", "debloat", "targetsdk", "game" });

        private static readonly PatchIds AndroidIds = new PatchIds(
            PatcherOptions.MandatoryPatches,
            PatcherOptions.DefaultPatches,
            PatcherOptions.OptionalPatches.Where(id => !PatcherOptions.DefaultPatches.Contains(id)),
            new[] { "hook-pc", "quicklaunch", "headless-diag", "gameload" });

        public static PatchIds Ids(BuildProfile build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            return build.IsPc ? PcIds : AndroidIds;
        }

        /// <summary>
        /// The mandatory patches plus <paramref name="optionalIds"/> (default or optional ids of the build), in the build's order.
        /// An id the build excludes, or an unknown one, is an <see cref="ArgumentException"/>.
        /// </summary>
        public static IReadOnlyList<IlPatch> For(BuildProfile build, IEnumerable<string> optionalIds)
        {
            PatchIds ids = Ids(build);
            var chosen = new HashSet<string>(ids.Mandatory, StringComparer.Ordinal);
            foreach (string id in optionalIds ?? Enumerable.Empty<string>())
            {
                if (ids.Excluded.Contains(id)) throw new ArgumentException("patch '" + id + "' is not available on build " + build.Id);
                if (!ids.Default.Contains(id) && !ids.Optional.Contains(id) && !ids.Mandatory.Contains(id))
                    throw new ArgumentException("unknown patch '" + id + "' for build " + build.Id);
                chosen.Add(id);
            }
            if (!build.IsPc) return ApkPatcher.SelectPatches(chosen).AsReadOnly();
            return PcOrder.Where(chosen.Contains).Select(CreatePc).ToList().AsReadOnly();
        }

        private static IlPatch CreatePc(string id)
        {
            switch (id)
            {
                case "hook-pc": return new EngineBridgeHookPatch();
                case "headless": return new HeadlessGamePatch();
                case "bottile": return new BotTilePatch();
                case "store": return new StorePatch();
                case "signatures": return new SignaturesPatch();
                case "inbox": return new InboxPatch();
                case "adminpage": return new AdminPagePatch();
                case "forum": return new ForumPatch();
                case "headless-engine": return new HeadlessEnginePatch();
                case "physics": return new PhysicsPatch();
                case "quicklaunch": return new QuickLaunchPatch();
                case "headless-diag": return new HeadlessDiagnosticsPatch();
                case "nativeonline": return new NativeOnlinePatch();
                case "waits": return new WaitsPatch();
                case "adminlink": return new AdminLinkPatch();
                case "graphics": return new GraphicsPatch();
                case "gameload": return new GameAssemblyLoadPatch();
                case "geo": return new GeoPatch();
                default: throw new ArgumentException("unknown PC patch '" + id + "'");
            }
        }
    }
}
