# PC (Steam) port: what the Steam build is and how our server gets into it

Scope: the Steam build of Modern Combat Versus (app 723780, depot 723781, build 6372049, March 2021) in
`/home/user/mcvs-work/pc/install`, compared with the Android build we support (APK 1.17.32). The goal: the Steam build
plays offline like our Android build (bot matches, progression, live ops, the settings page in a browser), with our
server inside the game process. This is research only. No repository code was changed.

Markers: **[V]** verified from the binaries (IL, disassembly, file contents, or a tool run over the real files).
**[I]** inferred and not yet tested on Windows. Nothing here was run on Windows. Every **[I]** that the plan depends on
is listed again in §8 (unknowns) and is checked first in WP0.

Owner decision: the installer data-restore fix (Android `install.*`, [INSTALL-OFFLINE.md](../INSTALL-OFFLINE.md)) is
**not** part of the PC port. The Steam depot ships the complete `Data/` folder, so the PC build has no download step
to repair (§4.4).

Tools and intermediate files (not committed; in the session scratchpad
`/tmp/claude-0/-home-user-mcvs-offline/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad/`):
* `pc-decompiled/`: ILSpy 9.1 projects of every PC managed assembly, plus `ModernCombatVersus.il` (the IL of the
  exe's managed part). Other agents can reuse this folder.
* `pctool/`: a Cecil 0.11.5 tool. It references the current `McvsOffline.Patcher.dll` and the net8
  `McvsOffline.Server.dll` (built from `main-dev` 531bf4c into the git-ignored `artifacts/`). Its modes:
  * `info`: assembly identities.
  * `diff` / `ndiff`: method-level IL diffs, raw and normalised.
  * `fps`: every patch fingerprint checked against the PC assemblies.
  * `apply android|pc`: each patch run alone on the real assemblies.
  * `pcfile` / `datacmp`: decode PC data files and compare them with the Android pack.
* `pcscripts/`: small PE helpers: `callers.py` (E8 call xrefs), `iat.py` (IAT slots), `exports.py`.
* Outputs: `pc-diff/*.ndiff.txt`, `pc-fps.txt`, `pc-apply.txt`, `pc-datacmp.diff.txt`, `pc-datacmp.missing.txt`,
  `pcdata/` (decoded `Config/glue.json`, `MCVS.game`, `GameOptions.json` and others).

Source references `EN/…`, `GM/…` and `OP/…` in this report point into the **PC** decompilation (`pc-decompiled/Gameloft.Engine/`,
`pc-decompiled/Game/`, `pc-decompiled/Gameloft.OnlinePlatform/`) unless they are marked as Android. Addresses `0x1…` are
virtual addresses in `NativeEngine.dll` (image base `0x10000000`); `0x40…` addresses are in `ModernCombatVersus.exe`
(image base `0x400000`).

---------------------------------------------------------------------------------------------------

## 0. Key findings

1. **The process is a 32-bit native C++/CLI host that loads the .NET Framework 4 CLR. It does not use Mono.**
   **[V]** `ModernCombatVersus.exe` is PE32 i386, GUI subsystem. It has a native entry point (AddressOfEntryPoint
   `0xccc2`), a CLR header (data directory 14 at RVA `0xedb0`, size `0x48`; metadata runtime `v4.0.30319`) and imports
   `mscoree.dll`, `NativeEngine.dll`, `WCPToolkit.dll` and `steam_api.dll`. Its `<Module>::Wrapper.Start` creates
   `EngineBridge.Class1` and calls `Class1.Start()`, and `Class1.Start()` is one line:
   `MainLoop.Start(MainLoop.ModeType.NativeHostsDotNet)`. Every managed DLL is `ILOnly`, targets
   `.NETFramework,Version=v4.5` and references `mscorlib 4.0.0.0`. `_CommonRedist` ships the .NET Framework 4.5.2
   installer.
2. **The PC build is 1.17.30, revision 162066, and its client id is `2620:70425:1.17.30:steam:steam`.** Android is
   1.17.32, revision 162074. The game logic is almost the same. **[V]** The decoded `Config/glue.json` reads
   `"version": "1.17.30"`, `"revision": "162066"`, `"clientID": "2620:70425:1.17.30:steam:steam"`,
   `"gameCredential": "game:mcvsGameServer"`. The normalised IL diff, which ignores source-path strings, line numbers,
   compiler-generated names and the Android-only `IFastAccess` weaving, gives:

   | assembly | identical methods | changed | only on Android | only on PC |
   |---|---|---|---|---|
   | Game | 32,302 | 306 | 101 | 159 |
   | Gameloft.OnlinePlatform | 2,567 | 10 | 3 | 6 |
   | Gameloft.Util | 1,087 | 4 | 0 | 4 |

   The 101 Android-only Game methods exclude 2,121 `IFastAccess` methods. The protocol layer is the same.
3. **Federation on PC is the same as on Android: an anonymous credential, plain HttpClient, no pinning, and the same
   URLs.** **[V]**
   * `CredentialType` has no Steam type. The only difference from Android is that PC adds `microsoftgraph`.
   * `Federation` sets `ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificatesPlease` and
     builds `new HttpClient(new FedRequestHandler(new HttpClientHandler(), this))` (`OP/Federation.cs:107,472`).
   * The hard-coded URL literals in Game, Engine, OnlinePlatform and Util are the same set on both platforms
     (`https://eve.gameloft.com/`, `http://ip.ecomapis.gameloft.org/location/?ip=` and others).
   * `GameApplication.Arguments` declares the same public members on both platforms (62 declaration lines, no diff).
     Our args.txt block therefore means the same thing on PC.
4. **Steam is used for four things:**
   * **Process start.** It aborts without the Steam client: "Steam must be running to play this game
     (SteamAPI_Init() failed)." **[V]**
   * **Achievements and stats**, through `nativegame.dll`. **[V]**
   * **The overlay callback.** **[V]**
   * **Steam microtransactions for IAP**, in native code (`iap_steam_crm_billing.cpp`). **[V]**

   There is no Steam login and no Steam auth ticket in the Federation flow. **[V]**
5. **Patch status on PC.** `pctool apply pc` runs each patch alone on the real PC assemblies (`pc-apply.txt`). The
   Android control run (`apply android`) passes all 16. **[V]**
   * **8 apply unchanged on PC:** `headless-engine`, `physics`, `geo`, `bottile`, `finder`, `store`, `signatures`,
     `forum`.
   * **`hook` cannot be reused.** It patches the wrong place: nothing on PC calls `NicePlaySDKDLL.OnInitialize`.
   * **7 fail their fingerprint or shape check:** `headless`, `nativeonline`, `waits`, `lan`, `adminpage`, `debloat`,
     `adminlink`. For 4 of them the semantic change is small (§2.3).
6. **The best hook point is `EngineBridge.Class1.Start`. It runs before the native engine reads its arguments.**
   **[V]** The call chain from `Class1.Start` reaches the args-file reader:

   `MainLoop_Start` (export `0x100ccba0`) → `0x1010a3a0` → `0x1010a200` (call at `0x1010a471`) → `0x1010a540` (call
   at `0x1010a259`) → `0x1010b120` (call at `0x1010a941`) → `0x1010b900`

   `0x1010b900` chooses `"<prefix>_args.txt"` if that file exists, and `"args.txt"` otherwise (strings at
   `0x10cdff24` and `0x10cdff18`). The order is therefore the same as on Android: our code runs first and writes
   args.txt, then the engine reads it.
7. **On PC we can inject without changing any Steam file.** A `ModernCombatVersus.exe.config` with `<codeBase>`
   redirects can point `EngineBridge`, `Game`, `Gameloft.Engine` and `Gameloft.Util` at patched copies in a subfolder.
   * **[V]** The game loads its game assemblies with `Assembly.Load(new AssemblyName(name))`, i.e. the Load context
     (`EN/ComponentAssemblies.cs:177`).
   * **[V]** None of these assemblies is strong-named.
   * **[I]** Whether the CLR honours the `.exe.config` of a mixed-mode exe must be checked on Windows (WP0).

   In-place patching with backups is the fallback.
8. **Three new PC risks:**
   * **The process is 32-bit and not large-address-aware.** **[V]** COFF Characteristics are `0x0102`; flag `0x0020`
     is not set. The client, the TS World and the GS World would share 2 GB of address space.
   * **Embedded TS/GS Worlds have never run on the Windows native engine.** **[I]**
   * **PC-only components have to be audited for headless Worlds.** Examples: keyboard listeners in
     `GameplayComponent.OnStart`, `SteamAchievementsComponent` with `[KeepInHeadlessMode(true)]`, and
     `InputSettingsComponent.OnStart` calling `World.GetUI()`. **[V]** (code). **[I]** (behaviour).
9. **The server can be reused almost entirely, but it needs a separate PC build.** The game data the server reads
   matches Android:
   * **[V]** `Config/crmpreconfiguration.json` is byte-identical to Android's.
   * **[V]** No gameplay table (`Config/*`, `*.tx` templates) differs except `MetaGame.tx` (it adds
     `SteamAchievements`), `SceneComponents.tx` (one camera value) and the platform files `glue.json`, `snsconfig.json`
     and `Assemblies.mjson`.

   The data pack is named differently. **[V]** On PC the file name is the decimal CRC-32 of the logical path plus
   `.f`, in one flat folder (`Config/crmpreconfiguration.json` → `Data/3186060502.f`). Android uses hex with the first
   digit as a directory (`Data/b/de76cd6.f`). The server's `DataPack.HashedPath` must learn the PC scheme.

   The embedded DLL must be compiled against .NET Framework reference assemblies, not the Xamarin BCL (§3.3).

---------------------------------------------------------------------------------------------------

## 1. How the PC process is built

### 1.1 Binaries **[V]**

| file | kind | notes |
|---|---|---|
| `ModernCombatVersus.exe` (7.7 MB, 7.3 MB of it `.rsrc`) | PE32 i386 GUI, **mixed-mode C++/CLI** | Imports: `steam_api.dll` (SteamAPI_Init, RunCallbacks, Shutdown, WriteMiniDump, …), `WCPToolkit.dll` (window, input, render setup), `NativeEngine.dll` (`NativeEngineAccess::initEntryPoint`, `render`, input events), MSVC 14 CRT, **`mscoree.dll`**. Sections: `.text .rdata .data .gfids .rsrc .reloc`. There is **no `.bind` section, so no SteamStub DRM wrapper**. sha256 `884f7461…`. |
| `EngineBridge.dll` (4 KB) | managed, net451 | `EngineBridge.Class1.Start() { MainLoop.Start(MainLoop.ModeType.NativeHostsDotNet); }`. Reference: `Gameloft.Engine 0.0.0.0`. |
| `NativeEngine.dll` (14.7 MB) | native PE32 | The engine (`nativeengine` in P/Invokes). D3D11 + OpenGL32, WinINet (`InternetCheckConnectionA` only), WS2_32, Havok, Steam (Billing, UserStats, Friends). The PDB paths are `E:\Windows\OnlySteamTrunk\…`, so this is a Steam-only branch. |
| `nativegame.dll` (69 KB) | native PE32 | Steam stats, achievements and overlay callback (`NativeGame_SetStat`, `NativeGame_UnlockAchievement`, `Steam_OverlayChanged`, `Steam_ResetStats`), resolution, clipboard, fullscreen. |
| `WCPToolkit.dll` | native PE32 | The Windows platform layer (XInput, GDI+, D3D11, IME). |
| `GameManager.dll` (12.8 KB) | managed, net45 | P/Invoke wrappers for `nativegame`. It also contains the PC copy of the `Gameloft.NicePlaySDK` namespace, as an empty stub: `OnInitialize() { }`. |
| `Game.dll`, `Gameloft.*.dll`, `Newtonsoft.Json.dll` 7.0.1 | managed, net45, not strong-named (Newtonsoft is the exception) | The same assemblies as Android, PC build. `Gameloft.Engine` is version **0.0.0.0** (1.0.0.0 on Android). `Game.dll` additionally references `GameManager` and `System.Windows.Forms`. |
| `steam_api.dll`, `steam_appid.txt` (`723780`) | | The `steam_appid.txt` file lets the exe run when it is not started from Steam (as long as Steam is running). |

Assembly identities (MVIDs, for a future `KnownBuildPc`) **[V]** (`pctool info`, `pc-info.txt`):

| assembly | MVID |
|---|---|
| EngineBridge | `b5913b9a-f9dc-42d6-bd3a-435a8280e8f1` |
| GameManager | `48d3404b-15f5-49a8-84cb-db6b6af913ad` |
| Game | `577aad49-7a49-41ba-86f1-ff84bbbb33d9` |
| Gameloft.Engine | `f8984bac-0e4b-4ce8-84d8-020035dcbb92` |
| Gameloft.Util | `0f8f34ba-99d2-49b3-8715-9fc261b9fa5e` |
| Gameloft.OnlinePlatform | `ffdcc8c6-c6b8-4694-a296-d69b2e7c0d87` |
| ModernCombatVersus.exe (managed part) | `9df8988c-17d3-4a7f-b910-e8035cb07ec4` |

sha256 hashes of the files we would patch:

| file | sha256 |
|---|---|
| Game.dll | `7459eea9…a946bf9d` |
| Gameloft.Engine.dll | `e38e7eea…b4785` |
| Gameloft.Util.dll | `8adb1944…82fe1` |
| EngineBridge.dll | `ce2655ee…7a595` |

The full hashes were printed by `sha256sum` during the research.

### 1.2 Runtime: .NET Framework 4 CLR, 32-bit **[V]**

* The exe is not `ILOnly`: the `pctool info` attributes are `0`, against `ILOnly` for the DLLs. It is a C++/CLI image
  with native CRT startup. A mixed-mode image can only run on the Microsoft CLR (Mono cannot load it), so Mono is not
  involved.
* The IL of the exe (`ModernCombatVersus.il`) contains the usual C++/CLI startup: `ICLRRuntimeHost`/`ICorRuntimeHost`
  GUIDs, `<CrtImplementationDetails>.GetDefaultDomain`, `DoCallBackInDefaultDomain`. `Wrapper.{ctor}` does
  `newobj EngineBridge.Class1::.ctor()` and keeps it in a `gcroot`. `Wrapper.Start` calls `Class1::Start()` (IL lines
  292–306 and 309–371).
* There is no `ModernCombatVersus.exe.config` in the depot. The default domain therefore runs on the newest installed
  CLR 4 (.NET Framework 4.8 or 4.8.1 on Windows 10/11).
* Bitness: every image is i386. The managed DLLs are AnyCPU (`ILOnly`), but they run in the 32-bit process.
  **`ModernCombatVersus.exe` lacks IMAGE_FILE_LARGE_ADDRESS_AWARE** (Characteristics `0x0102`), so the process gets
  2 GB of user address space even on 64-bit Windows (risk R2, §7).

### 1.3 Start-up sequence **[V]**

1. Native start: a named mutex `GL_MCVS_UNIQUE_INSTANCE` (exe `0x4010f0` → `CreateMutexW`; string at `0x40f234`).
   Then `SteamAPI_Init` (exe `0x4033a9`, IAT `0x40e2e4`). On failure it shows a `MessageBoxW` "Steam must be running
   to play this game (SteamAPI_Init() failed)." (`0x40f298`) and exits with code 1 (`0x4033d8`). The Steam Controller
   init failing only shows a warning (`0x40f338`/`0x40f380`).
2. `Wrapper.Start` → `EngineBridge.Class1.Start` → `MainLoop.Start(NativeHostsDotNet, args: null)`. The managed side
   replaces null with `{ "" }` (`MainLoop.Executable` is `""`), so **no command-line argument reaches the engine from
   managed code**. Then `MainLoop_Start(1, {""}, 0, OnStarted)` (P/Invoke).
3. Native `MainLoop_Start` (`0x100ccba0`) reaches `0x1010b120`. That function loads the args file (`0x1010b900`:
   `<this+0x390>_args.txt` if it exists, else `args.txt`), merges argc/argv into it (`0x1003f110`), and then calls
   back `MainLoop.OnStarted(argument)` → `GameApplication.Create(Arguments.FromString(argument))`.
4. `GameApplication`'s constructor: `RootPath = FindRootPath()`, which is the directory of
   `GetModuleFileNameW(NULL)`, i.e. the exe's folder (`EN/GameApplication.cs:1101,1322-1336`). `Data` is
   `RootPath\Data`. Then `InitGameAssemblies("Config/Assemblies.mjson")` → `Assembly.Load("Game")`, and so on.
5. Unlike Android, **`MainLoop_GetGlfRootPath` returns null** (export `0x10052050`: `xor eax,eax; ret`). Code that
   needs the game root must use `GameApplication.FindRootPath()` or the exe's directory.

Unknown: whether the relative `"args.txt"` resolves against the current directory or the engine's root. Both are the
exe folder when Steam starts the game. Our bootstrap can remove the doubt by setting `Environment.CurrentDirectory` to
the exe folder before `MainLoop.Start` (§3.1).

### 1.4 What PC has that Android does not, and the reverse **[V]**

* There is no Java, no `XamarinMainActivity`, no `NicePlaySDKDLL.OnInitialize` caller (no reference to `NicePlaySDKDLL`
  in any PC assembly), no AOT images, no APK signature and no typemap MVID pinning. The managed DLLs are plain files
  that the CLR JIT-compiles. Nothing verifies them: they are not strong-named, and .NET Framework does not check
  unsigned assemblies.
* The engine interop marshals strings differently on PC. The PC `Gameloft.Engine` passes UTF-8 `byte[]` to native code
  and reads strings back with `Component.UTF8_Encoding_GetString(IntPtr)`. Android uses `string` with
  `Marshal.PtrToStringAnsi`. This one source of difference accounts for many of the 672 changed `Gameloft.Engine`
  methods: `GameApplication` 24, `PlatformInfo` 17, `AppPreferences` 17, `BrowserComponent` 9, …
* PC lacks the `Gameloft.Aspects` `IFastAccess` weaving. These are generated getters and setters, 2,121 methods in
  Game and 912 in Engine.

---------------------------------------------------------------------------------------------------

## 2. Managed assemblies: PC against Android 1.17.32

### 2.1 Versions **[V]**

| | Android | PC |
|---|---|---|
| game version (`glue.json` `version`) | 1.17.32 | **1.17.30** |
| revision | 162074 | 162066 |
| client id | `2620:66102:1.17.32:android:googleplay` | `2620:70425:1.17.30:steam:steam` |
| game codes | | `"steam": "MCST"`, `"win32": "MCVI"` |
| `gameCredential` | | `game:mcvsGameServer` |
| BCL | Xamarin.Android 10.3 Mono, `mscorlib 2.0.5.0` | .NET Framework 4.x, `mscorlib 4.0.0.0` |
| `Gameloft.Engine` version | 1.0.0.0 | 0.0.0.0 |
| source tree (log paths baked into IL) | `D:\mcvs\trunk\…`, `E:\Projects\MCVS\trunk\…` | `E:\Windows\OnlySteamTrunk\…` |

Saves can move between the platforms **[V]**. `ProfileManagerComponent` throws
`ClientGameVersionIsGreaterThanServerGameVersionException` only when the profile's `GameVersion` is newer by
`FeaturesCompareTo`, which compares major and minor only (`EN/GameVersion.cs:69-82`; `GM/ProfileManagerComponent.cs:262`).
1.17.32 and 1.17.30 compare equal, so an Android save (MCVS-SAVE transfer) loads on PC without the new-user reset.

### 2.2 Method-level differences **[V]** (`pctool ndiff`; lists in `pc-diff/*.ndiff.txt`)

The raw diff counts every method whose log call carries a different source path or line number. The normalised diff
replaces `ldstr "<drive>:\…"` with PATH and the following line-number `ldc.i4` with LINE. It also renumbers
compiler-generated names (`<X>d__N`, `DisplayClassN_M`, `<>f__AnonymousTypeN`), compares branch targets by
instruction index, and treats `ldc.i4.s`/`ldc.i4.N` as equal.

| assembly | methods A / P | identical | changed | only A (without IFastAccess) | only P |
|---|---|---|---|---|---|
| Game | 34,830 / 32,767 | 32,302 | 306 | 101 | 159 |
| Gameloft.Engine | 24,184 / 23,177 | 22,119 | 672 | 481 | 386 |
| Gameloft.OnlinePlatform | 2,580 / 2,583 | 2,567 | 10 | 3 | 6 |
| Gameloft.Util | 1,091 / 1,095 | 1,087 | 4 | 0 | 4 |
| Gameloft.Sequences | 1,500 / 1,607 | 1,463 | 37 | 0 | 107 |
| Gameloft.Engine.Math | 882 / 882 | 882 | 0 | 0 | 0 |

The most-changed Game types are:

| type | changed methods | why |
|---|---|---|
| `PlayerProfileComponent` | 47 | Steam achievements, account menu |
| `CheatsComponent` | 20 | |
| `UIComponent` | 18 | keyboard UI, `onGetStore "steam"` |
| `GameIapStoreComponent` | 16 | Steam billing |
| `GameClientComponent` | 15 | |
| `GameplayComponent` | 14 | keyboard listeners in `OnStart`/`OnStop` instead of `HudGamepadComponent` |
| `GameTrackingComponent` | 12 | |
| `FirstPersonHudInterfaceComponent`, `HudGamepadComponent`, `FirstPersonControlsComponent`, `InputSettingsComponent`, `AimAssistComponent` | | mouse and keyboard |

The 10 changed `Gameloft.OnlinePlatform` methods (`pc-diff/Gameloft.OnlinePlatform.ndiff.txt`):
`CredentialType.get_IsSns` and `.cctor` (`microsoftgraph`), `NewsFeedData.FromJson` (a refactor),
`Configuration.ParseStatus`, `BanToolService.ApplyActions`/`RetrieveCampaigns`, and `Federation.SendRequest`,
`Session.Authorize`, `Session.AuthorizeTransferCode`, `Session.LookupAlias` (async state machines). The request
formats our conformance harness pins have to be re-checked against the PC DLL (WP5). The public surface and
`Arguments` match.

### 2.3 Our IL patches on PC **[V]** (`pctool fps` → `pc-fps.txt`; `pctool apply pc` → `pc-apply.txt`)

The patcher and the embedded server DLL were built from `main-dev` 531bf4c. Each patch ran alone on a fresh copy of
the PC assembly. The resolver held the PC folder plus the .NET Framework 4.5 reference assemblies, and the embedded
server DLL was the context. The Android control run (same patches, Android assemblies) is all OK.

| patch | target | PC result | what differs | needed for PC |
|---|---|---|---|---|
| `hook` | NicePlaySDK.dll | "OK" on `GameManager.dll` (the method exists), **but never called** | PC has no caller of `NicePlaySDKDLL.OnInitialize` | **Replace** with a hook in `EngineBridge.Class1.Start` (§3.1) |
| `headless` | Game | **FAIL**: `InputSettingsComponent.OnStart() does not start like the verified 1.17.32 code` | PC `OnStart` registers UI callbacks via `World.GetUI().OverrideNativeFunction("ChangeKeyAction", …)` and others (`d-pc-isc.txt`). PC `GameplayComponent` has **0** `OfType<HudGamepadComponent>().FirstOrDefault()` sites (Android has 4). Instead it calls `World.GetGameInputManager().AddKeyPressListener(...)` in `OnStart`/`OnStop` (`GM/GameplayComponent.cs:403-406, 1873-1876`) | **Required, re-derived** for PC (WP4) |
| `headless-engine` | Gameloft.Engine | OK, unchanged | | Required |
| `physics` | Gameloft.Engine | OK (both fingerprints identical) | | Required (same Havok issue in-process; **[I]**) |
| `nativeonline` | Gameloft.Engine | **FAIL**: `Init_private` shape | PC marshals the 3 strings as UTF-8 `byte[]` before `OnlinePlatformComponent_Init_private(byte[],byte[],byte[])` | New shape check; same change |
| `waits` | Gameloft.Engine | **FAIL**: fingerprint | Codegen only: `IL_0065 dup` on Android, `IL_0065 ldloc.0` on PC (`d-*-wait.txt`) | New fingerprints; same edit |
| `geo` | Gameloft.Util | OK, unchanged | | Recommended |
| `bottile` | Game | OK, unchanged | | Recommended |
| `finder` | Gameloft.Engine | OK, unchanged | | **Not on Windows by default.** It turns the Finder's `HttpListener` prefix into `http://*:51750/`; on Windows `HttpListener` is http.sys, and a wildcard prefix needs administrator rights or a `netsh http add urlacl` (**[I]**, standard http.sys behaviour) |
| `lan` | Gameloft.Engine | **FAIL**: P3 site (`ClientSettings::.ctor` has no `call get_PublicHost; stfld <LocalAnubisFinderHost>`) | The initialiser is compiled differently | LAN is not a PC goal; defer |
| `store` | Game | OK (both fingerprints identical) | | Default |
| `signatures` | Game | OK (both fingerprints identical) | | Default |
| `adminpage` | Game | **FAIL**: `GetAccountMenuButtonList` fingerprint | PC list: RedeemCoupons, then GameCenter on Win32 or a `ShowLinkCode` button. Same anonymous-object shape (`customAction`, `isClickable`, `label`, `icon`) | New fingerprints; same hook contract |
| `adminlink` | Gameloft.Engine | **FAIL**: `BrowserComponent.OpenUrl` fingerprint | PC adds `IsValidUri(url)` and a `surveyplatform.gameloft.com` branch, and drops the Facebook tracking hit | New fingerprint; same prologue |
| `forum` | Game | OK (`UIComponent.GoToForum` identical) | | Optional |
| `debloat` | Gameloft.Engine | **FAIL**: `SocialLib_private/<Init>d__14::MoveNext` differs; `<LoginImpl>d__67` missing (renumbered) | Crashlytics, push and notification sites are identical; Social differs | **Android-only**: its manifest and file parts do not exist on PC, and Crashlytics/GCM are Android SDKs. Re-check Facebook auto-login only if it shows up on PC |

Summary:
* **8 apply as they are:** `headless-engine`, `physics`, `geo`, `bottile`, `store`, `signatures`, `forum`, and
  `finder` (which we do not want on Windows).
* **5 need new fingerprints or shape checks with the same edit:** `nativeonline`, `waits`, `adminpage`, `adminlink`,
  `lan`.
* **1 needs a fresh analysis:** `headless`.
* **1 is Android-only:** `debloat`.
* **1 is replaced:** `hook`.

One subtlety: today the patches import the server's methods from the embedded **Android** DLL. A PC patch must import
them from the **PC** build of `McvsOffline.Server.dll` (§3.3), so that the patched assemblies reference
`mscorlib 4.0.0.0` types.

---------------------------------------------------------------------------------------------------

## 3. Online flow on PC and how to put our server in

### 3.1 The online flow **[V]** unless marked

* **Eve, pandora and Federation**: identical client code. The base URL comes from `-EnvironmentSwitcherUrl` or the
  hard-coded `https://eve.gameloft.com/`. `-DataCenter`, `-SuperCredential`, `-SuperPassword` and `-DeviceId` work as
  on Android (same `Arguments` fields). The server must answer eve for the client id
  `2620:70425:1.17.30:steam:steam`. `EveService` already answers every client id and only logs the mismatch
  (`server/Services/Eve/EveService.cs:73-74`).
* **Credential**: anonymous. `AuthenticationComponent.AnonymousCredentialType`, `AnonymousUsername` and
  `AnonymousPassword` come from native exports (`0x100c2d00`, `0x100c2ce0`, …), as on Android. `NativeEngine.dll`
  contains `anonymous`, `anonymous:` and `anonymous_credential`, and no Steam credential type exists. How the native
  side derives the username on PC (GDID, Steam ID, machine id) is **unknown** and does not matter to us: our server
  accepts any anonymous credential.
* **TLS and pinning**: none. All certificates are accepted, and we use `http://127.0.0.1:<port>/`. Proxy: .NET
  Framework's `HttpClientHandler` follows the WinINet proxy settings. Loopback normally bypasses a proxy (**[I]**,
  risk R8).
* **The game's own TS/GS servers**: `Gameloft.Engine.LocalAnubis` (Lobby, Controller, Finder) and
  `ServerLocation.Embedded` exist on PC with the same enum (`Auto, None, Online, Remote, Embedded, SSH, Tunneling, P2P`).
  * `LocalAnubisManager.Host` defaults to `IPAddress.Loopback`, and `PublicHost` is `"localhost"` in that case
    (`EN/LocalAnubisManager.cs:44-81`).
  * The Finder is an `HttpListener` on `http://localhost:51750/` (`EN/HTTPServer.cs:28-31`). On Windows that is
    http.sys. A `localhost` prefix normally needs no URL ACL (**[I]**).
  * The Lobby and Controller are `TcpListener`s on loopback, so they cause no firewall prompt.
* **Achievements**: `SteamAchievementsComponent` (in `Templates/MetaGame/MetaGame.tx` on PC only) has
  `[KeepInHeadlessMode(true)]`.
  * It is active on clients and on servers when the client id platform is `steam` or `windows`
    (`GM/SteamAchievementsComponent.cs:11-60`).
  * It hooks the static events `GameOptionManager.OnKillEvent` and `OnWinEvent` and writes progress through
    `GameManager.UpdateSteamAchievement`/`UnlockSteamAchievement`, which become `nativegame` `SetStat` and
    `UnlockAchievement`.
  * Offline this keeps working while Steam runs: Steam queues the stats in offline mode (**[I]**).
  * In-process TS/GS Worlds would register the same static handlers a second time (risk R5).
* **IAP**: Steam microtransactions run in native code (`iap_steam_crm_billing.cpp`, `iap_steam_crm_item_manager.cpp`)
  through Gameloft's ecomm, which is dead. Our economy does not use real-money IAP, so nothing changes. The status
  screen's Android "IAP initialised" probe has no PC equivalent.
* **Overlay**: `GamePreferencesComponent` calls `GameManager.InitOverlayChanged(...)`. `steam_api.dll` exports the flat
  API `SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage` (ordinal list index 221), `SteamClient` (826),
  `SteamAPI_ISteamClient_GetISteamFriends` (167), `SteamAPI_GetHSteamUser` and `SteamAPI_GetHSteamPipe`. The Steam
  overlay browser can therefore show our settings page inside the game (§3.4).
* **Browser**: on PC, `BrowserComponent.OpenUrl` ends in native `BrowserComponent_OpenURL_private` (export
  `0x100bc7a0` → `0x10050650` → `0x107a0650`). That function builds its strings with XOR obfuscation, and it was not
  traced to the end. **Whether it opens an embedded view or the system browser is unknown.** `NativeEngine.dll` calls
  `ShellExecuteA(0, "open", …)` from `0x10730e20` and others. We do not need the game's browser (§3.4).

### 3.2 Ways to inject the server

| option | how | Steam files touched | survives "Verify integrity" | effort | verdict |
|---|---|---|---|---|---|
| **A. Patched assemblies loaded via `ModernCombatVersus.exe.config`** | Patcher writes patched copies of `EngineBridge.dll`, `Game.dll`, `Gameloft.Engine.dll`, `Gameloft.Util.dll` plus `McvsOffline.Server.dll` into `<install>\McvsOffline\bin\`, and writes `ModernCombatVersus.exe.config` with `<runtime><assemblyBinding><dependentAssembly><assemblyIdentity name="Game"/><codeBase version="0.0.0.0" href="McvsOffline/bin/Game.dll"/>…` | **none** (two new items: the .config and a folder) | **yes**. Verify only checks depot files and does not delete extra files (**[I]**, standard Steam behaviour) | low | **Recommended**, if WP0 confirms the CLR reads the config of this mixed-mode exe and honours `codeBase` for these simply named assemblies (**[I]**) |
| B. Patched assemblies in place | Replace the 4 DLLs in the install folder; keep `*.mcvs-orig` backups; add `McvsOffline.Server.dll` | 4 DLLs | **no**. Verify or an update restores the originals, and the game then runs online-mode and fails as today | low | Fallback if A does not work. The boot probe (`GamePatchProbe`) and a "re-run the patcher" message cover it |
| C. Runtime detours (Harmony-style) from an AppDomainManager (`<appDomainManagerAssembly>` in the .config) | No IL rewrite; detour the patched methods at start | none | yes | high: every patch rewritten as a detour; MonoMod/Harmony on CLR4 x86; harder to verify than Cecil IL with fingerprints | Not recommended. It discards the fingerprinted Cecil pipeline that is our safety net |
| D. Native proxy DLL (e.g. `version.dll`, `dxgi.dll` next to the exe) | Loads first, hosts our managed code | none | yes | high, and fragile (32-bit native code, CLR hosting by hand) | No |
| E. Standalone server + args.txt only (hosts file not needed) | Run `hosts/standalone` (net8) as a separate process; args.txt points eve at it, like the Android dev loop | none | yes | none for the client | **Dev loop only.** Bot matches need the in-process TS/GS patches (`headless`, `physics`, …), which a separate process cannot apply. Useful for WP0 to see how far the stock PC client gets |

A hosts-file redirect is never needed: `-EnvironmentSwitcherUrl` in args.txt redirects Federation, and the only other
hard-coded host (`ip.ecomapis.gameloft.org`) is handled by the `geo` patch.

**Hook point.** Option A or B. Rewrite `EngineBridge.Class1.Start` (4 KB, one method) into:

```
try { McvsOffline.Bootstrap.Init(); } catch { }
MainLoop.Start(MainLoop.ModeType.NativeHostsDotNet);
```

This is the Android `hook` pattern with a different site: verify the 3-instruction body exactly, then prepend a
guarded call. `Init()` starts the server thread and writes or merges args.txt before native `MainLoop_Start` reads it
(§1.3).

Alternative: pass the arguments through `MainLoop.Start(mode, string[] args)`. Native code merges argv into the args
file (`0x1003f110`), so this would avoid writing args.txt into the Steam folder. Keep args.txt for parity with Android
unless WP0 shows a problem.

### 3.3 Which BCL the server must compile against

The PC process runs the .NET Framework 4.x CLR, and its `mscorlib`, `System` and `System.Core` are 4.0.0.0.

* **The Android embedded DLL will not load cleanly.** It references Mono's `mscorlib/System/System.Core 2.0.5.0`
  (Silverlight-profile identities that are not marked retargetable) plus `Mono.Android`, `Java.Interop` and
  `Gameloft.Logs` (`KnownBuild.ServerAllowedReferences`). **[I]** for the exact failure mode; mscorlib unifies, but
  `System 2.0.5.0` with that key does not.
* **Build target.** Add a third server project, `McvsOffline.Server.Pc.csproj` (net452 reference assemblies from
  `Microsoft.NETFramework.ReferenceAssemblies.net452`, x86 or AnyCPU), with `server/Core/**`, `server/Services/**`
  and `hosts/embedded/**` minus `Android/` plus a new `hosts/embedded/Windows/`. Its define is `MCVS_EMBEDDED_WIN`.
* **Split `MCVS_EMBEDDED`.** Today it means both "inside the game" and "Android/Mono/libc". Use `MCVS_EMBEDDED` for
  the first and `MCVS_ANDROID` for the second. Sites to touch **[V]**:
  * `server/Core/Storage/FileSystem.cs:30,44,102` (libc fsync/rename P/Invokes)
  * `server/Core/Http/SocketReuse.cs:81,84` and `server/Core/Http/HttpServer.cs:85` (SO_REUSEADDR via libc)
  * `server/Services/Assets/LocalAssets.cs:66`
  * `hosts/embedded/Bootstrap.cs` (Logcat writer, `AndroidPlatform`)
  * `Diagnostics/ThreadDump.cs`, `NativeThreads.cs`, `CrashReporter.cs` (libc)

  The Windows equivalents: `FileStream.Flush(true)` and `File.Replace`/`File.Move`, and
  `Socket.SetSocketOption(ReuseAddress)` or `ExclusiveAddressUse = false`.
* **Language rules.** The shared rules (C# 7.3, no async, MiniJson, no HttpListener) still apply because the code is
  shared with Android. The PC build itself could use the full .NET Framework 4.5 BCL.
* **Tests.** The conformance harness should also run against the PC `Gameloft.OnlinePlatform.dll`. Only 10 of its
  methods differ (§2.2), and loading a net45 DLL under net8 works today for the Android copy.

### 3.4 The settings page in a browser

`AdminHooks.OpenPage` and `OpenIfPageMarker` keep their contract; the PC side of `IAdminPlatform` decides how the page
opens:

1. **Steam overlay browser** (preferred, it stays in the game): `SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage`.
   * Call it with the friends interface from `SteamClient()` → `GetISteamFriends(GetHSteamUser(), GetHSteamPipe(),
     "SteamFriends015")`. `"SteamFriends015"` is the version string in `NativeEngine.dll` and `nativegame.dll`.
   * The overlay's browser runs in the Steam client on the same machine. **[I]** It can reach `127.0.0.1`, keeps the
     URL fragment, and accepts our `HttpOnly; SameSite=Strict` session cookie.
   * It needs the overlay enabled and, in fullscreen, a working D3D11 overlay hook.
2. **The system browser**: `Process.Start(url)` (ShellExecute). This works everywhere. It leaves fullscreen, and the
   one-time ticket appears in the browser's command line for a moment, where another local process could read it.
   That matters little on a single-user PC: the ticket expires after 120 s and is single-use.
3. The game's `BrowserComponent`: its native behaviour on PC is unknown (§3.1). Use it only if WP0 shows it is an
   in-game view.

`CurrentPlayerResolver` only accepts a "last session" whose client id contains `":android:"`
(`server/Services/Admin/CurrentPlayerResolver.cs:61`). That check must accept the PC client id. `GameRequest.IsClientId`
and `HasUserAgent` are generic and accept `2620:70425:1.17.30:steam:steam` as they are **[V]**
(`server/Core/Http/GameRequest.cs:44-75`).

The other `IAdminPlatform` members on Windows:
* Clipboard: `GameManager.SetClipboard` (native) or WinForms `Clipboard`; `Game.dll` already references
  `System.Windows.Forms`.
* "Share": open Explorer on the file (`explorer /select,`).
* Toast: an overlay notification or a no-op.
* Restart: a helper process that waits for exit, then launches `steam://rungameid/723780`. The single-instance mutex
  forbids a second copy while the first runs.
* Free space: `DriveInfo`.
* `MissingJniMembers`: empty.

### 3.5 Install, verify and uninstall for a PC player

* **Finding the game**:
  * Steam path from `HKCU\Software\Valve\Steam\SteamPath`.
  * Libraries from `steamapps\libraryfolders.vdf`.
  * The game in the library whose `steamapps\appmanifest_723780.acf` exists, under
    `steamapps\common\<installdir>`. The player can also pass the folder.
* **Checks**: the MVIDs or sha256 of the patched DLLs (§1.1) and `Config/glue.json`'s version must match before
  anything is written, like `KnownBuild` on Android. If an unknown build is found, nothing is written.
* **Writes (option A)**: `McvsOffline\bin\*.dll` (patched copies plus `McvsOffline.Server.dll`),
  `ModernCombatVersus.exe.config`, and a `McvsOffline\install.json` (versions, hashes, patch list).
* **Writes (option B)**: `*.dll.mcvs-orig` backups, the replaced DLLs, and `McvsOffline.Server.dll`.
* **Uninstall**: delete the .config and the folder (A), or restore the backups (B). "Verify integrity of game files"
  also undoes B.
* **Data directory**:
  * The default proposal is `%LOCALAPPDATA%\McvsOffline\`, holding settings.json, accounts, profiles, logs and
    device-id.txt. A Steam uninstall deletes the install folder, and the Steam library may sit under `Program Files`.
  * A setting or args switch could keep it in `<install>\McvsOffline\data` for portable installs.
  * `EmbeddedPaths` currently derives everything from the game root (`hosts/embedded/EmbeddedPaths.cs`). The Windows
    platform needs a separate data-root resolver.
* **Steam must be running** (§1.3). For true offline play the player starts Steam in offline mode. We do not bypass
  Steam or remove its check. The port is for people who own the game.
* **Updates**: build 6372049 is from March 2021. If Steam ever ships a new build, the MVID check refuses it and the
  player keeps the online-dead stock game until we add that build.

---------------------------------------------------------------------------------------------------

## 4. Data and content on PC

### 4.1 Data-pack naming **[V]**

* `Data/` is flat: 15,922 entries, of which 15,921 are files, plus `MCVS.game` and `Config/EngineConfig.json` stored
  under their own names.
* Every other file is named **`<decimal crc32(logical path)>.f`**. Examples: `Config/Settings.tx` (crc
  `0xca0f90c8` = 3390017736) → `Data/3390017736.f`; `Config/crmpreconfiguration.json` → `Data/3186060502.f`;
  `Config/glue.json` → `Data/187993782.f`.
* It is the same CRC-32 as Android (report 11 §2), printed in decimal and without a directory level.
* Of the 14,101 PC files that match an Android name, 10,619 are GCBF and decode with our `Gcbf` (all CRCs checked)
  and 3,482 are raw.

Server change: `DataPack.HashedPath` and `Locate` must also try `Data/<decimal>.f`
(`server/Core/Content/DataPack.cs:45-63`). Detection rule: `MCVS.game` present and no `Data/1..f` subfolders, or simply
try both.

### 4.2 Content compared with Android **[V]** (`pctool datacmp`: the Android decoded tree against PC, by logical path)

* 14,101 Android logical paths exist on PC: 5,159 byte-identical (1,016 MiB) and 8,942 different (996 MiB).
* 122 are missing on PC: `Menus/GamepadButtons.json`, GLSL shaders (PC uses `Shaders_*.pak`), a few kit models.
* 1,820 PC files have no Android name.
* The differences are textures and models (`Menus/assets` 3,262, `3d/*` 5,100+), shaders, SWF UI files and minimaps:
  platform formats.
* Gameplay files that differ:
  * `Config/glue.json`, `Config/snsconfig.json`, `Config/Assemblies.mjson`: platform files.
  * `Templates/MetaGame/MetaGame.tx`: adds `"/SteamAchievements@type": "SteamAchievementsComponent"`.
  * `SceneComponents.tx`: `CameraSmoothing` 0.5 → 0.0.
  * `Levels/Connection.level`: the shader preload list is grouped differently.
* `Config/crmpreconfiguration.json`, `game_object.json`, every `Config/*` table and every other `*.tx` template are
  byte-identical.

Consequence: `server/Content/` (generated from the 1.17.32 tables by `tools/content-gen`), the economy, live ops, the
Cosmetic crate cells and the CRM overlay apply to PC as they are. The server must not hard-code the 1.17.32 client id
(`McvsConstants.ClientId` is only a fallback in `AuthEndpoints`, `WallService` and `ClanStore`), but a per-platform
value would be tidier.

### 4.3 GameOptions **[V]**

There is no `GameOptions_android.json` on PC; `GameOptions.json` holds the generic profiles. Its option list includes
`multithreadedPhysics`, `deferredPhysics` and `enableRenderThread`. The Android `physics` patch applies unchanged
(identical fingerprints), so the shared Havok worker issue (ARCHITECTURE §2.3) is handled the same way; whether it
occurs on PC is **unknown**.

### 4.4 Installer and data download

There is none to fix on PC. The depot ships the full data (3.5 GB, `steam-dl.log`: 3,923,048,036 bytes committed), and
no PC component downloads it. As the owner decided, the Android `Install/` code (`InstallerData`, `PackIndex`,
`AndroidInstaller`) stays Android-only.

---------------------------------------------------------------------------------------------------

## 5. Recommended plan

The order puts the biggest unknowns first. Each package ends with something the owner can test on Windows.

### WP0: Windows spike (owner on a Windows PC with Steam; about 1 session) **[gate for everything else]**

1. **Stock client against the standalone server.** Run the standalone server (net8, `hosts/standalone`) on the same
   PC and write `args.txt` next to the exe with the Android block (`-ServerLocation Embedded`,
   `-EnvironmentSwitcherUrl http://127.0.0.1:51780/`, `-SuperCredential …`, `-DeviceId …`, `-DataCenter mcvs`). Start
   the game from Steam. The server log shows how far the stock PC client gets: eve, auth, storage, CRM, the TS World
   start. Expected (**[I]**): login and menu work, and the in-process TS World fails on a headless problem.
2. **Config redirect.** Add a `ModernCombatVersus.exe.config` with one `<codeBase>` for `EngineBridge` pointing at a
   trivially modified copy, for example one that writes a marker file. This shows whether the CLR honours the config
   and the codeBase for this mixed-mode exe (option A).
3. **Telemetry.** Record the process's private bytes and virtual size in the menu and in a match. This is the
   2 GB-limit baseline (R2). Also note whether Windows Firewall asks anything when a match starts.
4. **Browser.** Note what `BrowserComponent.OpenUrl` does on PC: open any CRM or news link.

### WP1: Server builds for PC (about 2–3 days)

* `McvsOffline.Server.Pc.csproj` (net452 reference assemblies), `MCVS_EMBEDDED`/`MCVS_ANDROID` split, Windows versions
  of `FileSystem` and `SocketReuse`.
* `DataPack` PC naming (§4.1).
* `CurrentPlayerResolver` platform check; per-platform `McvsConstants.ClientId`.
* A reference check like `CheckServerAssembly` for the PC DLL: only `mscorlib`, `System`, `System.Core` 4.0.0.0
  (plus, if used, `Gameloft.Logs` and `System.Windows.Forms`).
* Tests: the existing suites, plus DataPack tests against `/home/user/mcvs-work/pc/install/Data` when present, and the
  conformance harness against the PC `Gameloft.OnlinePlatform.dll`.

### WP2: Windows host (about 3–5 days)

* `hosts/embedded/Windows/`. `WindowsPlatform : IEmbeddedPlatform` resolves the game root as the exe directory (via
  `Process.GetCurrentProcess().MainModule` or `AppDomain.BaseDirectory`), and uses a separate data root (§3.5).
* `IAdminPlatform`: Steam overlay, then system browser, plus clipboard, Explorer, restart through
  `steam://rungameid/723780`.
* `IPlatformDiagnostics`: an `OutputDebugString` or log-file sink, a crash hook, `FallbackLogDirectory`
  `%TEMP%\McvsOffline`.
* `PcBootstrap.Init`: set the current directory, then the same `EmbeddedHost.Start`.
* The watchdog's thread dump uses Mono APIs today. On .NET Framework, use the managed `Thread`/`StackTrace` limits or
  drop the native fallback.

### WP3: Patcher PC mode (about 3–4 days)

* `KnownBuildPc`: MVIDs and hashes (§1.1), version 1.17.30, client id.
* A Steam-folder locator.
* Option A output: copies + .config + `install.json`, uninstall. Option B as fallback, with backups.
* New `EngineBridgeHookPatch` (`Class1.Start`).
* PC fingerprints for `nativeonline`, `waits`, `adminpage`, `adminlink`. The same patch classes take a per-build
  fingerprint table.
* Unchanged: `headless-engine`, `physics`, `geo`, `bottile`, `store`, `signatures`, `forum`.
* Excluded on PC: `debloat`, `finder`, `lan`, and all APK, manifest, AOT, signing and ABI code.
* Patches import from the PC server DLL.
* Verification as on Android: fingerprints, ILVerify against the net45 reference assemblies, execution tests with
  recording hooks.

### WP4: Headless audit for PC (about 2–4 days, plus one Windows test round)

Repeat the device-1 audit (report `29`, `HeadlessGamePatch`) for the components a PC TS/GS World creates:

* `InputSettingsComponent.OnStart` (`World.GetUI()` in a headless World).
* `GameplayComponent.OnStart`/`OnStop` (`World.GetGameInputManager()` keyboard listeners).
* `GamePreferencesComponent` (`GameManager.FullScreen`, `InitOverlayChanged`).
* `SteamAchievementsComponent` (`[KeepInHeadlessMode(true)]`, static event handlers, `ResetStats` on first run).
* `FirstPersonControlsComponent`, `HudGamepadComponent`, `AimAssistComponent`.

Produce `HeadlessGamePatch` PC sites with fingerprints. Use WP0's log to start from the real first failure.

### WP5: Integration and checks (about 2 days, plus test rounds)

* A full build with a PC references folder (`MCVS_PC_REFS=/home/user/mcvs-work/pc/install`).
* A dry run of the patcher against the real Steam files.
* `docs/PLAYER-SETUP-PC.md` and a Windows check-list mirroring the Android ones: boot, bot match, saves, shop,
  signatures, live ops, the page, Steam achievements, verify-files behaviour, uninstall.

### Total and sharing

About **12–18 working days** of agent work plus **2–4 Windows test rounds**. Roughly one third of the Android effort,
because the server, the content, the economy, live ops and the page are shared.

**Shared with Android:**
* All of `server/Core/**` and `server/Services/**`, including the settings page (`server/Content/admin/`) and the
  generated content.
* The AdminHooks contract.
* `EmbeddedHost`, `ArgsFile`, `DeviceIdentity`, `GamePatchProbe`, the admin host and the diagnostics framework.
* The patcher's `IlPatch` infrastructure and 12 of the 16 patch classes: 7 unchanged, 4 with a second fingerprint
  set, and `headless` with new PC sites.
* The conformance harness.

**Android-only:** the APK pipeline (zip, sign, manifest, AOT, ABIs, debloat), the JNI and Java host, the installer
data restore, the LAN Finder patches.

**PC-only:** the Windows platform, the Steam-folder installer and the .config writer, the EngineBridge hook, the PC
headless sites, and the DataPack decimal naming.

---------------------------------------------------------------------------------------------------

## 6. What is not an issue

* **DRM.** There is no SteamStub section (§1.1). We only need managed files, and the native exe and DLLs stay
  untouched.
* **Integrity checks.** None for managed DLLs on PC: no signature, no typemap, no AOT MVIDs (§1.4).
* **TLS.** Client certificates are accepted globally, and we use plain HTTP on loopback (§3.1).
* **Game data.** Everything the server reads is present and identical to Android (§4.2).
* **Save compatibility.** Android 1.17.32 and PC 1.17.30 saves are feature-equal (§2.1).

---------------------------------------------------------------------------------------------------

## 7. Risks

| id | risk | likelihood / impact | mitigation |
|---|---|---|---|
| R1 | The Windows native engine cannot run headless TS/GS Worlds in the client process (never exercised; Gameloft's servers ran elsewhere) | medium / **blocking** | WP0 step 1 shows it early. Fallback: a second game process started headless as a local "server", if the engine accepts it (unknown) |
| R2 | **2 GB address space.** The exe is not large-address-aware (Characteristics `0x0102`); client (D3D11 textures) + TS + GS Worlds in one 32-bit process may run out of memory | medium / high | Measure in WP0. Setting the LAA bit on the exe would give 4 GB on 64-bit Windows, but it changes a Steam file (one header bit; no DRM) and verify would undo it. Excluded loading layers (`excludedLoadingLayers`) for headless Worlds already exist |
| R3 | The CLR ignores `.exe.config` codeBase for this mixed-mode exe | low–medium / medium | Option B (in place, with backups) |
| R4 | PC-only components crash or misbehave in headless Worlds | high / high (the same class of bug as Android device test 1) | WP4 |
| R5 | `SteamAchievementsComponent` hooks process-wide static events in client **and** server Worlds: achievements counted twice, or `ResetStats` run again | medium / low | Disable it in headless Worlds (one more headless site) |
| R6 | Steam must run; a player without the game in their library cannot play | certain / by design | Document. Steam offline mode for no-internet play |
| R7 | Windows Firewall prompt when a GS socket binds a non-loopback address (ENet UDP binding not checked) | unknown / low | Check in WP0; loopback play works even if the player declines |
| R8 | A system proxy (WinINet) intercepting `127.0.0.1` Federation calls | low / medium | Windows bypasses loopback by default; if it shows up, add a `<defaultProxy><bypasslist>` entry to the .config |
| R9 | Antivirus flags a tool that rewrites game DLLs or adds a .config | low–medium / medium | Option A writes no Steam files; ship source; deterministic outputs |
| R10 | A client difference in the 10 changed `OnlinePlatform` methods (e.g. `Session.Authorize`) changes a request our server answers strictly | low / medium | Conformance harness against the PC DLL (WP1) |
| R11 | `nativeonline` ("GaiaInit") behaviour differs on PC (Steam/native online SDK) | unknown / medium | Keep the patch (new shape) with `embedded.nativeOnlineSdk` off by default |

---------------------------------------------------------------------------------------------------

## 8. Unknowns (explicit)

1. Whether `ModernCombatVersus.exe.config` (`<codeBase>`, and `<appDomainManager*>` if ever needed) is read for this
   mixed-mode exe (WP0.2).
2. Whether headless TS/GS Worlds work on the Windows native engine, and the first failure if not (WP0.1).
3. The process's memory headroom with three Worlds in 2 GB (WP0.3).
4. Whether the relative `args.txt` resolves against the current directory or the exe folder. It is the same folder
   when started from Steam. The bootstrap can also set the current directory.
5. What native `BrowserComponent_OpenURL_private` does on PC: the path is string-obfuscated and was not traced to the
   end (`0x107a0650`).
6. How the native code derives the anonymous username and the GDID on PC. This does not matter for our server.
7. Whether the http.sys `localhost` Finder prefix registers without elevation for a normal user (it is the default in
   the game, so it must have worked for Gameloft's dev mode; not proven).
8. Whether GS/ENet sockets bind non-loopback (firewall prompt).
9. Whether the Steam overlay browser reaches `127.0.0.1` and keeps the fragment and cookies.
10. Whether Steam still serves updates for app 723780 (no change since build 6372049, March 2021).
11. Whether the owner has a Windows PC with Steam and the game for the WP0 spike. **The plan needs it.**

---------------------------------------------------------------------------------------------------

## Critic notes

Completeness review (research only). Rechecked against the real files.

1. **Data-pack naming is confirmed, but the file count needs a correction.** `zlib.crc32` gives
   `Config/crmpreconfiguration.json` = 3186060502, `Config/glue.json` = 187993782, `Config/Settings.tx` = 3390017736 and
   `Grenade_Frag_rom.tga` = 1675802. All four exist as `Data/<decimal>.f`.
   * `Data/` holds 15,920 `.f` files, `MCVS.game`, and a real subfolder `Config/` containing `EngineConfig.json`. So the
     folder is "flat" with one exception, and `DataPack.Locate` must keep the literal-name lookup for those two files.
   * GCBF: 11,876 of the 15,920 `.f` files start with `GCBF`. The 10,619 in §4.1 counts only the files that match an
     Android name. pc-textures.md §1.2 quotes 10,619 as the PC total, and it reports "15,921 numbered `.f` files". Both
     figures are off.
2. **The owner decision on the installer restore needs one concrete step.** The proposed `McvsOffline.Server.Pc.csproj`
   compiles "`hosts/embedded/**` minus `Android/`". That still includes `hosts/embedded/Install/` (`InstallerData.cs`,
   `PackIndex.cs`) and `EmbeddedHost.PrepareInstallerData` (`EmbeddedHost.cs:301,390-411`). Today that code is inert
   only because it is gated on `platform as IInstallerPlatform`. WP1 should exclude `Install/` from the PC project, or
   test that `WindowsPlatform` never implements `IInstallerPlatform`. This keeps the owner's "no installer restore fix
   on PC" true by construction.
3. **Missing unknown: write access to the install folder.** The plan writes `args.txt` (on every boot) and the
   `.exe.config` next to the exe. For a library under `Program Files` this works only because the Steam client normally
   grants Users write access there. That is not verified. Add it to WP0, and add a fallback: pass the arguments through
   `MainLoop.Start(mode, args)` (§3.2).
4. **R2 (2 GB address space) is understated.**
   * PC textures are 2× the Android size for 34 % of the shared textures (pc-textures.md §3.1), so the PC client's
     texture footprint is larger than on the phone.
   * The layer exclusion that saves memory is process-wide: `excludedLoadingLayers` feeds the static
     `World.GlobalExcludeLayers` (`EN/PerformanceOptionApplierComponent.cs:331-338`, `EN/World.cs:144`). Whatever the PC
     `GameOptions.json` profile excludes, or does not exclude, applies to the in-process TS/GS Worlds too.
   * WP0 step 3 should record the active PC profile options along with the memory figures.
5. **The patch counts differ between places.** §2.3's summary has "5 need new fingerprints" (including `lan`). The
   summary given to the orchestrator says 4. §5 says "12 of the 16". These agree once `lan` is deferred. Say that
   explicitly in §0.5.
6. **Still [I] and central to the recommendation:** CLR `.exe.config`/`codeBase` support for this mixed-mode exe. .NET
   Framework documents that for assemblies without a strong name the `codeBase` href must lie under the application
   base, which `McvsOffline/bin/` does. Nothing was run on Windows, so WP0 remains the gate.
7. **Not checked:** the flat Steamworks export `SteamAPI_ISteamFriends_ActivateGameOverlayToWebPage` in this old
   `steam_api.dll`. Its signature changed between SDK versions (a mode parameter was added). Confirm the arity from the
   export's disassembly before WP2.
