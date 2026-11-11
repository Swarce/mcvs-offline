using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace McvsOffline.Patcher
{
    public enum SignerChoice
    {
        /// <summary>apksigner when found, otherwise the built-in v1+v2 signer.</summary>
        Auto,
        ApkSigner,
        BuiltIn,
    }

    public enum GameLogMode
    {
        /// <summary>res/raw/args.txt unchanged ("-LogCategoryFilter Fatal"): the game logs Fatal entries only.</summary>
        Fatal,

        /// <summary>res/raw/args.txt without a -LogCategoryFilter line: the game enables every log level (diagnostic builds).</summary>
        All,
    }

    public enum AotStripMode
    {
        /// <summary>Delete libaot-X.dll.so (all ABIs) for every modified assembly X (default).</summary>
        Modified,

        /// <summary>Delete every libaot-*.dll.so: pure JIT, slower cold start, no stale-image questions at all.</summary>
        All,
    }

    public sealed class PatcherOptions
    {
        /// <summary>
        /// Optional patches applied when --patch is not given. "debloat" is not only IL: it also removes dead SDK components
        /// from the manifest (Manifest.ManifestDebloat) and drops the Crashlytics/Fabric files (ApkPatcher.DebloatDroppedEntries).
        /// "adminpage" and "adminlink" open the settings page (Settings › Account button, inbox message) through
        /// McvsOffline.AdminHooks of the embedded server. "targetsdk" is no IL patch: it raises the manifest's targetSdkVersion to
        /// <see cref="TargetSdk"/> with what that target needs (Manifest.ManifestTargetSdk, Manifest.BrowserLayoutInsets); by
        /// default 35 (Manifest.ManifestTargetSdk.DefaultTarget), with the classes.dex edits that needs (Dex.DexTargetSdkEdits). "graphics" passes
        /// GameOptions_android.json through McvsOffline.GraphicsHooks (settings graphics.profile; auto leaves it unchanged) and
        /// logs the effective options. "game" is no IL patch either: android:isGame and android:appCategory="game" on
        /// &lt;application&gt; (Manifest.ManifestGame), at any target; --target-sdk 36 needs it. "inbox" caps what an inbox message
        /// builds (Patches.InboxPatch: the big redeem codes froze the inbox). Build 30's hitch fixes: "crmtables" keeps a design
        /// database's tables when the CRM hands it the same cells again instead of re-reading its file (Patches.CrmTablesPatch: the
        /// match start and the return to the menu), "jsonnorm" does not normalize the same large profile text twice
        /// (Patches.JsonNormPatch: every profile save).
        /// </summary>
        public static readonly string[] DefaultPatches =
        {
            "nativeonline", "waits", "geo", "finder", "store", "signatures", "inbox", "debloat", "adminpage", "adminlink", "graphics", "targetsdk", "game",
            "crmtables", "jsonnorm",
        };

        /// <summary>
        /// Every id --patch accepts; "lan", "forum" and "botmove" are the ones not in <see cref="DefaultPatches"/>. "botmove" (build 27)
        /// lets a resting bot take its first step on frames shorter than 1/60 s (Patches.BotMovePatch): the PC's 60 FPS limit froze
        /// the bots, the phone's are fine, so the default APK stays without it (its Game.dll unchanged); an uncapped 90/120 Hz phone
        /// whose bots wait at spawn can add it.
        /// </summary>
        public static readonly string[] OptionalPatches =
        {
            "nativeonline", "waits", "geo", "finder", "lan", "store", "signatures", "inbox", "debloat", "adminpage", "adminlink", "targetsdk", "forum",
            "graphics", "game", "botmove", "crmtables", "jsonnorm",
        };

        /// <summary>
        /// Patches applied whatever --patch says: the server hook and the fixes the in-process TS/GS Worlds need (headless-World
        /// components, device test 1; single-threaded server physics and one Havok step at a time, device test 2), and since build 26
        /// the client's LocalAnubis Finder port, which the host moves when 51750 is busy at start (port fallback).
        /// </summary>
        public static readonly string[] MandatoryPatches = { "hook", "headless", "headless-engine", "physics", "finderport" };

        public string InputApk { get; set; }

        public string ServerDll { get; set; }

        public string OutputApk { get; set; }

        /// <summary>Optional patch ids ("hook" is always applied). 'lan' implies 'finder'.</summary>
        public SortedSet<string> Patches { get; set; } = new SortedSet<string>(DefaultPatches, StringComparer.Ordinal);

        public int Port { get; set; } = 51780;

        public string Keystore { get; set; }

        public string KeystorePassword { get; set; }

        public string KeyAlias { get; set; }

        public string BuildToolsDir { get; set; }

        public SignerChoice Signer { get; set; } = SignerChoice.Auto;

        public AotStripMode StripAot { get; set; } = AotStripMode.Modified;

        /// <summary>The 64-bit ARM ABI (--abi arm64-v8a gives a 64-bit-only APK).</summary>
        public const string Arm64 = "arm64-v8a";

        /// <summary>
        /// Native-library ABIs kept in the output (lib/&lt;abi&gt;/**); every other lib/ folder is dropped. Null, the default,
        /// keeps every ABI the APK ships (--abi all), so 32-bit phones keep working; --abi arm64-v8a drops the 32-bit copies
        /// (lib/armeabi-v7a, about half of lib/).
        /// </summary>
        public SortedSet<string> Abis { get; set; }

        /// <summary>
        /// What the game itself logs (Gameloft.Logs to logcat "mono-stdout"). The default is All while the offline build is
        /// being brought up on devices; <see cref="GameLogMode.Fatal"/> keeps the shipped res/raw/args.txt.
        /// </summary>
        public GameLogMode GameLog { get; set; } = GameLogMode.All;

        /// <summary>
        /// --target-sdk: the targetSdkVersion the "targetsdk" patch writes (29, 35 or 36); null means
        /// <see cref="Manifest.ManifestTargetSdk.DefaultTarget"/> (35). 29 is the shipped value: the manifest, layouts and
        /// classes.dex then stay exactly as without the patch. Another value needs "targetsdk" in <see cref="Patches"/>, and
        /// 31 or more also needs "debloat" (ApkPatcher refuses the run otherwise); "targetsdk" then also edits classes.dex.
        /// </summary>
        public int? TargetSdk { get; set; }

        /// <summary>The output's targetSdkVersion: <see cref="TargetSdk"/> (or the default) with "targetsdk", else the shipped 29.</summary>
        public int EffectiveTargetSdk
        {
            get
            {
                return Patches != null && Patches.Contains(Manifest.ManifestTargetSdk.PatchId)
                    ? TargetSdk ?? Manifest.ManifestTargetSdk.DefaultTarget
                    : Manifest.ManifestTargetSdk.ShippedTarget;
            }
        }

        /// <summary>
        /// --art: the project's own launcher icon and launch screen (Art.ArtPatch): "auto" (default) uses the art built in from
        /// art/android/ when there is any and otherwise keeps the stock icon and splash; "stock" never touches them; any other
        /// value is a folder laid out like art/android/ (the artist's test builds).
        /// </summary>
        public string Art { get; set; } = "auto";

        /// <summary>
        /// --art-extra: a folder laid over the art of --art (Art.OverlayArtSource), for files kept out of the repository, such
        /// as the data installer's background (installer/*.png); null = none.
        /// </summary>
        public string ArtExtra { get; set; }

        /// <summary>Patch even when the APK is not the verified 1.17.32 build (locators still verify every site).</summary>
        public bool Force { get; set; }

        /// <summary>Locate and verify every patch site, report, write nothing.</summary>
        public bool DryRun { get; set; }

        public bool Verbose { get; set; }

        /// <summary>What the command line asked for and the run ignores (a removed patch id in --patch); printed before the run.</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Tests only: apply only the hook as mandatory patch (synthetic APKs have no Game.dll or Gameloft.Engine.dll).</summary>
        internal bool TestOnlyHookIsMandatory { get; set; }

        /// <summary>
        /// Tests only: "debloat" changes the manifest and the files but not Gameloft.Engine.dll (synthetic APKs have no real
        /// engine to fingerprint).
        /// </summary>
        internal bool TestOnlyDebloatWithoutIl { get; set; }

        /// <summary>Tests only: accept a server assembly built for net8 (look-alike fixtures cannot reference the game BCL).</summary>
        internal bool SkipServerReferenceCheck { get; set; }

        /// <summary>Parses a --patch value: comma-separated ids, "none", "all" or "default".</summary>
        public static SortedSet<string> ParsePatchList(string value)
        {
            return ParsePatchList(value, null);
        }

        /// <summary>
        /// Parses a --patch value: comma-separated ids, "none", "all" or "default". An id an earlier build had
        /// (<see cref="Core.PatchPlan.Removed"/>, e.g. 'bottile' from a saved command line) is left out, with its
        /// <see cref="Core.PatchPlan.RemovedWarning"/> added to <paramref name="warnings"/> (when given); any other unknown id
        /// is an <see cref="ArgumentException"/>.
        /// </summary>
        public static SortedSet<string> ParsePatchList(string value, ICollection<string> warnings)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string raw in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string id = raw.ToLowerInvariant();
                if (id == "none") continue;
                if (id == "all") result.UnionWith(OptionalPatches);
                else if (id == "default") result.UnionWith(DefaultPatches);
                else if (OptionalPatches.Contains(id)) result.Add(id);
                else if (Core.PatchPlan.Removed.ContainsKey(id)) AddOnce(warnings, Core.PatchPlan.RemovedWarning(id));
                else throw new ArgumentException("unknown patch '" + raw + "' (known: " + string.Join(", ", OptionalPatches) + ", none, all, default)");
            }
            if (result.Contains("lan")) result.Add("finder");
            return result;
        }

        private static void AddOnce(ICollection<string> list, string item)
        {
            if (list != null && !list.Contains(item)) list.Add(item);
        }

        /// <summary>Android ABIs a lib/ folder can be named after.</summary>
        public static readonly string[] KnownAbis = { "arm64-v8a", "armeabi-v7a", "armeabi", "x86", "x86_64", "mips", "mips64" };

        /// <summary>Parses an --abi value: comma-separated ABIs, or "all" (keep every lib/ folder; returns null).</summary>
        public static SortedSet<string> ParseAbiList(string value)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string raw in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string abi = raw.ToLowerInvariant();
                if (abi == "all") return null;
                if (!KnownAbis.Contains(abi)) throw new ArgumentException("unknown ABI '" + raw + "' (known: " + string.Join(", ", KnownAbis) + ", all)");
                result.Add(abi);
            }
            if (result.Count == 0) throw new ArgumentException("--abi needs at least one ABI, or all");
            return result;
        }

        /// <summary>Parses a --target-sdk value: one of <see cref="Manifest.ManifestTargetSdk.SupportedTargets"/>.</summary>
        public static int ParseTargetSdk(string value)
        {
            int target;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out target) || !Manifest.ManifestTargetSdk.SupportedTargets.Contains(target))
                throw new ArgumentException("--target-sdk must be " + string.Join(", ", Manifest.ManifestTargetSdk.SupportedTargets));
            return target;
        }

        public static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new ArgumentException("--port needs a number between 1 and 65535");
            return port;
        }
    }
}
