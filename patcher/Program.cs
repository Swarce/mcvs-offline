// Tool: patcher - owner group: EMBED (hosts/embedded, patcher/)
// Turns the player's own APK into the offline APK (ARCHITECTURE §2.3, verify-C7 §7). Only ever operates on the
// player's files; never downloads or ships Gameloft binaries.
using System;
using System.IO;
using McvsOffline.Patcher.Patches;

namespace McvsOffline.Patcher
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            PatcherOptions options;
            try
            {
                options = Parse(args);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                Console.Error.WriteLine("run with --help for usage");
                return 2;
            }
            if (options == null)
            {
                PrintUsage();
                return 0;
            }

            try
            {
                PatchReport report = ApkPatcher.Run(options, line => Console.WriteLine("  " + line));
                Print(report, options);
                return 0;
            }
            catch (PatchException ex)
            {
                Console.Error.WriteLine("error: a patch site does not match the verified game code, nothing was written:\n" + ex.Message);
                return 3;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException || ex is ArgumentException ||
                                       ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                if (options.Verbose) Console.Error.WriteLine(ex);
                return 1;
            }
        }

        internal static PatcherOptions Parse(string[] args)
        {
            var o = new PatcherOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        return null;
                    case "--apk": o.InputApk = Value(args, ref i); break;
                    case "--server": o.ServerDll = Value(args, ref i); break;
                    case "--out": o.OutputApk = Value(args, ref i); break;
                    case "--patch": o.Patches = PatcherOptions.ParsePatchList(Value(args, ref i)); break;
                    case "--port": o.Port = PatcherOptions.ParsePort(Value(args, ref i)); break;
                    case "--keystore": o.Keystore = Value(args, ref i); break;
                    case "--ks-pass": o.KeystorePassword = Password(Value(args, ref i)); break;
                    case "--ks-alias": o.KeyAlias = Value(args, ref i); break;
                    case "--build-tools": o.BuildToolsDir = Value(args, ref i); break;
                    case "--signer":
                        string signer = Value(args, ref i);
                        if (signer == "auto") o.Signer = SignerChoice.Auto;
                        else if (signer == "apksigner") o.Signer = SignerChoice.ApkSigner;
                        else if (signer == "builtin") o.Signer = SignerChoice.BuiltIn;
                        else throw new ArgumentException("--signer must be auto, apksigner or builtin");
                        break;
                    case "--abi": o.Abis = PatcherOptions.ParseAbiList(Value(args, ref i)); break;
                    case "--strip-aot":
                        string mode = Value(args, ref i);
                        if (mode == "modified") o.StripAot = AotStripMode.Modified;
                        else if (mode == "all") o.StripAot = AotStripMode.All;
                        else throw new ArgumentException("--strip-aot must be modified or all");
                        break;
                    case "--game-log":
                        string gameLog = Value(args, ref i);
                        if (gameLog == "all") o.GameLog = GameLogMode.All;
                        else if (gameLog == "fatal") o.GameLog = GameLogMode.Fatal;
                        else throw new ArgumentException("--game-log must be all or fatal");
                        break;
                    case "--force": o.Force = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "-v":
                    case "--verbose": o.Verbose = true; break;
                    default: throw new ArgumentException("unknown argument '" + arg + "'");
                }
            }
            if (o.InputApk == null) throw new ArgumentException("--apk is required");
            if (o.ServerDll == null) throw new ArgumentException("--server is required");
            if (o.OutputApk == null && !o.DryRun) throw new ArgumentException("--out is required");
            return o;
        }

        /// <summary>"pass:secret", "env:NAME" (like apksigner) or the plain password.</summary>
        private static string Password(string value)
        {
            if (value.StartsWith("pass:", StringComparison.Ordinal)) return value.Substring(5);
            if (value.StartsWith("env:", StringComparison.Ordinal))
            {
                string name = value.Substring(4);
                return Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException("environment variable " + name + " is not set");
            }
            return value;
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException(args[i] + " needs a value");
            return args[++i];
        }

        private static void Print(PatchReport report, PatcherOptions options)
        {
            Console.WriteLine();
            foreach (string note in report.Notes) Console.WriteLine("patched  " + note);
            foreach (string m in report.Modified) Console.WriteLine("modified " + m);
            foreach (string a in report.Added) Console.WriteLine("added    " + a);
            foreach (string r in report.Removed) Console.WriteLine("removed  " + r);
            Console.WriteLine("kept     " + report.UnchangedEntries + " other entries byte-identical");
            foreach (string w in report.Warnings) Console.WriteLine("note     " + w);
            if (options.DryRun) return;
            Console.WriteLine("signed   " + report.Signer + " with " + report.KeystorePath + (report.KeystoreCreated ? " (new key)" : ""));
            Console.WriteLine("verified " + report.Verification.Replace("\n", "\n         "));
            Console.WriteLine();
            Console.WriteLine("Wrote " + options.OutputApk);
            if (report.KeystoreCreated)
            {
                Console.WriteLine("Back up " + report.KeystorePath + ": future updates must be signed with the same key, or Android");
                Console.WriteLine("refuses to install them over this one (uninstalling deletes the game's data, saves included).");
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "MCVS Offline patcher: turns your own Modern Combat Versus " + KnownBuild.Version + " APK into the offline APK.\n\n" +
                "Usage: McvsOffline.Patcher --apk <original.apk> --server <McvsOffline.Server.dll> --out <mcvs-offline.apk> [options]\n\n" +
                "  --patch <list>        optional patches, comma-separated (default: " + string.Join(",", PatcherOptions.DefaultPatches) + ")\n" +
                "                          nativeonline  native online SDK (GaiaInit) only when settings.json embedded.nativeOnlineSdk\n" +
                "                                   is true (its network threads stalled boot with the internet on)\n" +
                "                          waits    the engine's pumping waits (TaskExtension.WaitAndWork) re-check every 100 ms (hardening)\n" +
                "                          geo      IP geolocation URL -> the embedded server (no DNS wait in the handshake)\n" +
                "                          bottile  BOTMATCH tile -> bot-filled PvP with full progression\n" +
                "                          finder   LocalAnubis Finder on all interfaces (survives Wi-Fi changes)\n" +
                "                          lan      LAN hosting helpers, implies finder (experimental)\n" +
                "                          store    kill-signature store items: no null list for sounds, a failing item is skipped\n" +
                "                          signatures  kill-signature screen looks owned ids up in a set (no freeze with many)\n" +
                "                          debloat  remove the manifest declarations of SDK parts dead offline (ad screens, GCM\n" +
                "                                   push, install-referrer and boot receivers, Crashlytics/Facebook init providers,\n" +
                "                                   location/legacy permissions) and drop the Crashlytics/Fabric files\n" +
                "                        'none' = only the mandatory patches (server hook, headless TS/GS fixes, server physics),\n" +
                "                        'all' = everything\n" +
                "  --game-log <m>        all (default): the game logs every level to logcat (mono-stdout); fatal: Fatal only,\n" +
                "                        as shipped (res/raw/args.txt unchanged)\n" +
                "  --port <n>            port of the embedded server baked into the geo URL (default 51780)\n" +
                "  --keystore <file>     signing keystore (.p12; .jks needs apksigner). Default: a key generated once in\n" +
                "                        " + Signing.SigningKey.DefaultPath() + "\n" +
                "  --ks-pass <pw>        keystore password: pass:<pw>, env:<VAR> or plain (default " + Signing.SigningKey.DefaultPassword + ")\n" +
                "  --ks-alias <alias>    key alias (only needed when the keystore holds several keys)\n" +
                "  --build-tools <dir>   folder with apksigner and zipalign (else PATH, then $ANDROID_HOME/build-tools/*)\n" +
                "  --signer <s>          auto (default: apksigner if found, else built-in), apksigner, builtin (v1+v2, no Java needed)\n" +
                "  --strip-aot <m>       modified (default): delete the AOT images of patched assemblies; all: delete every AOT image\n" +
                "  --abi <list>          native-library ABIs to keep (default: all; e.g. " + PatcherOptions.Arm64 + " for a 64-bit-only APK)\n" +
                "  --dry-run             verify every patch site and report, write nothing\n" +
                "  --force               patch an APK that is not the verified " + KnownBuild.Version + " build (sites are still verified)\n" +
                "  -v, --verbose         more output\n\n" +
                "See docs/PLAYER-SETUP.md.");
        }
    }
}
