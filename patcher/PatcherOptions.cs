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
        /// <summary>Optional patches applied when --patch is not given.</summary>
        public static readonly string[] DefaultPatches = { "nativeonline", "waits", "geo", "bottile", "finder", "store" };

        public static readonly string[] OptionalPatches = { "nativeonline", "waits", "geo", "bottile", "finder", "lan", "store" };

        /// <summary>
        /// Patches applied whatever --patch says: the server hook and the fixes the in-process TS/GS Worlds need (headless-World
        /// components, device test 1; single-threaded server physics and one Havok step at a time, device test 2).
        /// </summary>
        public static readonly string[] MandatoryPatches = { "hook", "headless", "headless-engine", "physics" };

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

        /// <summary>Patch even when the APK is not the verified 1.17.32 build (locators still verify every site).</summary>
        public bool Force { get; set; }

        /// <summary>Locate and verify every patch site, report, write nothing.</summary>
        public bool DryRun { get; set; }

        public bool Verbose { get; set; }

        /// <summary>Tests only: apply only the hook as mandatory patch (synthetic APKs have no Game.dll or Gameloft.Engine.dll).</summary>
        internal bool TestOnlyHookIsMandatory { get; set; }

        /// <summary>Tests only: accept a server assembly built for net8 (look-alike fixtures cannot reference the game BCL).</summary>
        internal bool SkipServerReferenceCheck { get; set; }

        /// <summary>Parses a --patch value: comma-separated ids, "none", "all" or "default".</summary>
        public static SortedSet<string> ParsePatchList(string value)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string raw in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string id = raw.ToLowerInvariant();
                if (id == "none") continue;
                if (id == "all") result.UnionWith(OptionalPatches);
                else if (id == "default") result.UnionWith(DefaultPatches);
                else if (OptionalPatches.Contains(id)) result.Add(id);
                else throw new ArgumentException("unknown patch '" + raw + "' (known: " + string.Join(", ", OptionalPatches) + ", none, all, default)");
            }
            if (result.Contains("lan")) result.Add("finder");
            return result;
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

        public static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new ArgumentException("--port needs a number between 1 and 65535");
            return port;
        }
    }
}
