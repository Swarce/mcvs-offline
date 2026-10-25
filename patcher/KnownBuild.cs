using System;
using System.Collections.Generic;

namespace McvsOffline.Patcher
{
    /// <summary>Facts about the one game build the patches were verified against (APK 1.17.32, versionCode 1173200).</summary>
    public static class KnownBuild
    {
        public const string PackageName = "com.gameloft.android.ANMP.GloftMVHM";
        public const string Version = "1.17.32";
        public const int MinSdkVersion = 21;

        public const string AssembliesDir = "assemblies/";
        public const string ServerAssemblyFile = "McvsOffline.Server.dll";

        /// <summary>
        /// The six assemblies whose MVIDs are baked into libxamarin-app.so's managed-to-Java type maps (verify-C7 §4,
        /// report 08 §7.4). The patcher never modifies them: a changed MVID breaks every Java/managed type lookup.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, Guid> TypemapPinned = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["BindGPSrv-auth-base.dll"] = new Guid("1eb2fe08-c549-4265-9ab1-afff71961bcd"),
            ["BindAF.dll"] = new Guid("1b415f4d-ecf1-453b-abd2-19eb25e95efa"),
            ["XaLaunchANMP.dll"] = new Guid("9d564c5d-ae17-44f3-99bd-a4850ae57b9a"),
            ["AdjustSdk.Xamarin.Android.dll"] = new Guid("66c8e283-02ac-4962-a247-c3b030bf47c6"),
            ["BindUnityAds.dll"] = new Guid("6822908b-62ef-43f3-b702-e2c9d0fc43ce"),
            ["Mono.Android.dll"] = new Guid("237d2ef0-9808-44e7-8ccc-4467e350e1fe"),
        };

        /// <summary>MVIDs of the assemblies the patches edit, as shipped in 1.17.32 (an unknown MVID means another build).</summary>
        public static readonly IReadOnlyDictionary<string, Guid> PatchedOriginals = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["NicePlaySDK.dll"] = new Guid("fcb3acb6-9d11-4fa4-a1c6-31dce75fc37f"),
            ["Game.dll"] = new Guid("d9067738-21d0-412d-b185-e040fd20b5ee"),
            ["Gameloft.Engine.dll"] = new Guid("e4f25834-594a-49fc-9afd-8767ed2d9d03"),
            ["Gameloft.Util.dll"] = new Guid("fa21a3ea-bb70-4229-9e02-7fc537749a0c"),
        };

        /// <summary>The only assemblies McvsOffline.Server.dll may reference: the game's own copies (verify-C7 §6).</summary>
        public static readonly IReadOnlyCollection<string> ServerAllowedReferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "mscorlib", "System", "System.Core", "Mono.Android", "Java.Interop", "Gameloft.Logs",
        };

        /// <summary>
        /// res/raw/args.txt: the only thing the Java side reads from it is whether a line equals "-UseDlc" and every line
        /// that contains "-LogCategoryFilter" (AndroidUtilsPlugin.getArguments). GameApplication's constructor enables every
        /// log level when that filter is empty and keeps the Fatal-only default otherwise (the value itself is ignored: the
        /// SetLogCategoryFilter call is compiled out of the release Gameloft.Engine).
        /// </summary>
        public const string RawArgsEntry = "res/raw/args.txt";

        /// <summary>The shipped content of <see cref="RawArgsEntry"/> (24 bytes, STORED, CRC32 38799ea7, no newline).</summary>
        public const string RawArgsOriginal = "-LogCategoryFilter Fatal";

        public const uint RawArgsOriginalCrc = 0x38799ea7;

        /// <summary>The replacement for --game-log all: no -LogCategoryFilter line and no -UseDlc line.</summary>
        public const string RawArgsAllLevels = "-McvsOfflineGameLogAll\n";

        /// <summary>The Xamarin.Android (Mono) profile's mscorlib version; a net8 build would reference System.Runtime instead.</summary>
        public static readonly Version MonoProfileVersion = new Version(2, 0, 5, 0);
    }
}
