using System;
using System.Collections.Generic;

namespace McvsOffline.Patcher.Core
{
    /// <summary>
    /// One game build the patches were verified against (docs/design/pc-port.md §3.4, §7.2): its identity (MVIDs, and for PC the
    /// SHA-256 of every file the installer reads), the corlib its assemblies reference, and what McvsOffline.Server.dll may
    /// reference for it. Patches select their per-build site tables with <c>IlPatch.ForBuild</c>; an unknown build is refused
    /// before anything is written (P2).
    /// </summary>
    public sealed class BuildProfile
    {
        /// <summary>The Google Play APK 1.17.32 (versionCode 1173200): the constants of <see cref="KnownBuild"/>.</summary>
        public static readonly BuildProfile Android1_17_32 = new BuildProfile(
            id: "android-1.17.32",
            isPc: false,
            gameVersion: KnownBuild.Version,
            clientId: "2620:66102:1.17.32:android:googleplay",
            corlibVersion: KnownBuild.MonoProfileVersion,
            corlibPublicKeyToken: "7cec85d7bea7798e",
            patchedOriginals: KnownBuild.PatchedOriginals,
            unchangedWitnesses: new Dictionary<string, Guid>(StringComparer.Ordinal),
            originalSha256: new Dictionary<string, string>(StringComparer.Ordinal),
            serverAllowedReferences: KnownBuild.ServerAllowedReferences,
            serverAllowedModuleRefs: null,
            gameOptionsFile: "GameOptions_android.json",
            exeCharacteristics: null);

        /// <summary>
        /// The Steam build (app 723780, depot build 6372049, game 1.17.30, ModernCombatVersus.exe 32-bit, .NET Framework 4 CLR).
        /// Values from the read-only install ([PP] §5.4, design §7.2).
        /// </summary>
        public static readonly BuildProfile PcSteam6372049 = new BuildProfile(
            id: "pc-steam-6372049",
            isPc: true,
            gameVersion: "1.17.30",
            clientId: "2620:70425:1.17.30:steam:steam",
            corlibVersion: new Version(4, 0, 0, 0),
            corlibPublicKeyToken: "b77a5c561934e089",
            patchedOriginals: new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["EngineBridge.dll"] = new Guid("b5913b9a-f9dc-42d6-bd3a-435a8280e8f1"),
                ["Game.dll"] = new Guid("577aad49-7a49-41ba-86f1-ff84bbbb33d9"),
                ["Gameloft.Engine.dll"] = new Guid("f8984bac-0e4b-4ce8-84d8-020035dcbb92"),
                ["Gameloft.Util.dll"] = new Guid("0f8f34ba-99d2-49b3-8715-9fc261b9fa5e"),
            },
            unchangedWitnesses: new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["GameManager.dll"] = new Guid("48d3404b-15f5-49a8-84cb-db6b6af913ad"),
                ["Gameloft.OnlinePlatform.dll"] = new Guid("ffdcc8c6-c6b8-4694-a296-d69b2e7c0d87"),
            },
            originalSha256: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ModernCombatVersus.exe"] = "884f74611cb0daccbe889b054bda16d6ba96d5da4911e1bcd38d38d675231852",
                ["EngineBridge.dll"] = "ce2655eea9ed0d31363bb3424eb683c12d583d9408ecbd7a2a082d2a4a97a595",
                ["Game.dll"] = "7459eea9478df895257c1470888bb2b96666d71c892eb9a50783f2a1a946bf9d",
                ["Gameloft.Engine.dll"] = "e38e7eeab9a22e89b5b5c67b1a7c8452205d5a224b22d69ae84405fc19cb4785",
                ["Gameloft.Util.dll"] = "8adb19442c9237edc57ef8ef62f7ea3a674282864ffb0e9e87b49c547cd82fe1",
                ["GameManager.dll"] = "3c97ab98cc74a4e9d88659facf8e9c994d298dee82515f7543a3b84929630284",
                ["Gameloft.OnlinePlatform.dll"] = "c93650f8fe8957f24c1ea5611e69197fe5ac75e61308577821f1e0a272e7c333",
                ["Gameloft.Logs.dll"] = "33314aa441830790efbad7803c73f8626d23adca6d56015645224d36533319e7",
            },
            serverAllowedReferences: new HashSet<string>(StringComparer.Ordinal) { "mscorlib", "System", "System.Core", "Gameloft.Logs" },
            serverAllowedModuleRefs: new HashSet<string>(StringComparer.Ordinal) { "kernel32", "user32", "steam_api" },
            gameOptionsFile: "GameOptions_windows.json",
            exeCharacteristics: 0x0102);

        private BuildProfile(string id, bool isPc, string gameVersion, string clientId, Version corlibVersion, string corlibPublicKeyToken,
                             IReadOnlyDictionary<string, Guid> patchedOriginals, IReadOnlyDictionary<string, Guid> unchangedWitnesses,
                             IReadOnlyDictionary<string, string> originalSha256, IReadOnlyCollection<string> serverAllowedReferences,
                             IReadOnlyCollection<string> serverAllowedModuleRefs, string gameOptionsFile, int? exeCharacteristics)
        {
            Id = id;
            IsPc = isPc;
            GameVersion = gameVersion;
            ClientId = clientId;
            CorlibVersion = corlibVersion;
            CorlibPublicKeyToken = corlibPublicKeyToken;
            PatchedOriginals = patchedOriginals;
            UnchangedWitnesses = unchangedWitnesses;
            OriginalSha256 = originalSha256;
            ServerAllowedReferences = serverAllowedReferences;
            ServerAllowedModuleRefs = serverAllowedModuleRefs;
            GameOptionsFile = gameOptionsFile;
            ExeCharacteristics = exeCharacteristics;
        }

        /// <summary>"android-1.17.32", "pc-steam-6372049".</summary>
        public string Id { get; }

        public bool IsPc { get; }

        /// <summary>"1.17.32" / "1.17.30".</summary>
        public string GameVersion { get; }

        /// <summary>The Federation client id the build sends.</summary>
        public string ClientId { get; }

        /// <summary>The mscorlib the game's assemblies reference: 2.0.5.0 (Xamarin.Android profile) / 4.0.0.0.</summary>
        public Version CorlibVersion { get; }

        /// <summary>"7cec85d7bea7798e" / "b77a5c561934e089".</summary>
        public string CorlibPublicKeyToken { get; }

        /// <summary>The assemblies the patches edit, file name to the shipped MVID.</summary>
        public IReadOnlyDictionary<string, Guid> PatchedOriginals { get; }

        /// <summary>Assemblies that are not patched but identify the build (PC: GameManager, Gameloft.OnlinePlatform); Android: empty.</summary>
        public IReadOnlyDictionary<string, Guid> UnchangedWitnesses { get; }

        /// <summary>Lower-case SHA-256 of the shipped files (PC: the exe and 7 DLLs); Android: empty (MVIDs only).</summary>
        public IReadOnlyDictionary<string, string> OriginalSha256 { get; }

        /// <summary>The only assembly names McvsOffline.Server.dll may reference for this build.</summary>
        public IReadOnlyCollection<string> ServerAllowedReferences { get; }

        /// <summary>PC: the only native modules the server DLL may import (kernel32, user32, steam_api); Android: null (unchecked).</summary>
        public IReadOnlyCollection<string> ServerAllowedModuleRefs { get; }

        /// <summary>"GameOptions_android.json" / "GameOptions_windows.json" (the patch descriptions and the graphics hook's text).</summary>
        public string GameOptionsFile { get; }

        /// <summary>PC: the exe's PE file-header characteristics (0x0102: no LARGE_ADDRESS_AWARE); Android: null.</summary>
        public int? ExeCharacteristics { get; }

        public override string ToString()
        {
            return Id;
        }
    }
}
