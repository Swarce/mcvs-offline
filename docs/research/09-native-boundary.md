# 09 – Native boundary and options for embedding a server in-process

Scope: every managed/native and Java/native boundary in MCVS 1.17.32 (arm64-v8a build analysed; armeabi-v7a ships the
same set of libraries), what each native library does, where networking actually happens (managed vs native vs Java),
and a ranked evaluation of ways to run a re-implemented server inside the game process plus how the game would reach it.

Paths are relative to `/home/user/mcvs-work`. Abbreviations: `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`,
`LA/` = `decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`, `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`,
`J/` = `decompiled-java/sources/`, `NE` = `extracted/lib/arm64-v8a/libnativeengine.so`, `MD` = `extracted/lib/arm64-v8a/libmonodroid.so`.

Confidence tags: **[V]** verified (read the code / disassembly / ELF tables, or built/patched a test artifact offline).
**[L]** likely (strong inference, one link unchecked). **[S]** speculative. Nothing here was run on a device; the
proof-of-concept builds described below were compiled and IL-patched offline only.

---

## 0. TL;DR

1. **The entire online client is managed C#.** Federation HTTP (eve, pandora, auth, storage, social, matchmaker,
   etc.), the Anubis TCP control protocol, the game TCP control channel, and the in-process LocalAnubis servers
   (`TcpListener` + `HttpListener`) all run in Mono via `System.Net` icalls in `libmonosgen-2.0.so`; managed TLS goes
   through `libmono-btls-shared.so`. **[V]**
2. **`libnativeengine.so` also contains a complete networking stack, but the C# game does not route its online traffic
   through it.** Statically linked into NE: libcurl 7.49.1, OpenSSL/asio, ENet (UDP game transport), and Gameloft's
   own native online SDK (Gaia/Janus/Pandora/Iris/Seshat/CRM/IAP). NE imports `socket/connect/bind/listen/accept/
   getaddrinfo/send*/recv*` from libc. So native networking exists, but in this app it is used for a *small, specific*
   set of things (see §5), not for the gameplay/matchmaking client the user cares about. The one piece of game
   networking that is native is **ENet UDP** (the in-match datagram transport) and the raw TCP socket primitive. **[V]**
3. **Only two P/Invoke libraries are referenced by managed code: `"nativeengine"` (2188 declarations) and
   `"NativeNicePlaySDK"` (58 declarations).** `NativeNicePlaySDK` is **not present in the APK** and `NicePlaySDKDLL.Enabled`
   is a hard `false`, so those 58 are dead. Everything else managed calls is `nativeengine`. **[V]**
4. **The game server logic is C#, and MCVS already ships an in-process embedded-server mode (LocalAnubis).** So a
   Dead-Trigger-2-style *native* server `.so` is **not the natural fit here**: the authoritative server, matchmaker and
   lobby are managed classes (`GameServerComponent`, `LocalAnubis.*`, `ServerManager`). The cleanest "embedded server"
   is a **managed C# assembly loaded into the same Mono runtime**, backing the Federation HTTP the client already
   speaks. I verified this is buildable: a small `MCVS.Server.dll` compiled against the shipped trimmed BCL + engine
   assemblies, hosting a `System.Net.HttpListener` on `127.0.0.1:18080` and/or interposing an in-process
   `HttpMessageHandler`. **[V — compiled offline]**
5. **Getting managed code to load with no adb and no new activity: reuse the existing boot hook.**
   `XaLaunchANMP.XamarinMainActivity.OnCreate` already reflectively calls `GameDll.OnInitialize`,
   `AdjustSdkDll.OnInitialize`, `AdjustReferrerReceiver.OnInitialize` and `NicePlaySDKDLL.OnInitialize` *before*
   `MainLoop.Start` (`XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:72-107`). `NicePlaySDKDLL.OnInitialize` currently
   does nothing (`Enabled => false`). A 2-instruction IL patch of that method to call `MCVS.Server.Bootstrap.Init()`
   gives a guaranteed, early, managed entry point with zero smali and zero native work. I built and verified this patch
   offline with Mono.Cecil. **[V — patched offline]**
6. **Supplying `phantomArgs`/args without adb:** the native engine already reads `res/raw/args.txt` and a
   `<DataPath>/args.txt` at start-up (NE strings + the reader function at `0x3d59fc`), but it only extracts `-UseDlc`
   and `-LogCategoryFilter` in Java (`J/com/gameloft/android/PackageUtils/AndroidUtilsPlugin.java:120-147`). A full
   managed arg injector is cleaner: wrap `MainLoop.OnStartedDelegate` from `Bootstrap.Init()` and append
   `-EnvironmentSwitcherUrl http://127.0.0.1:18080/ -ServerLocation Embedded -AnubisCommand QuickLaunch ...` to the
   `argument` string before `GameApplication.Create` parses it. The eve URL contains `:` and `/` so it cannot go
   through `phantomArgs` (report 01), but an injector sets it directly. I compiled this injector offline. **[V]**
7. **Ranked embedding options (full detail §8):** (A) **managed server assembly + IL hook in NicePlaySDKDLL** — least
   work, no native code, reuses LocalAnubis; recommended. (B) **IL-interpose the Federation `HttpClientHandler`** so the
   client talks to an in-process handler with no socket at all — most robust, no port, no TLS; recommended as the
   transport. (C) **native `.so` injected via smali `System.loadLibrary` or by replacing `libcrashlytics.so`** — the
   DT2 analogue; works but you would still implement the server in C++ and re-expose it over localhost HTTP, which is
   strictly more work than A/B here. (D) **external/local companion process** — fine but needs a second APK/service.
8. **Re-signing is required for any APK modification, and there is exactly one native integrity gate to neutralise**
   (the cert-hashCode check in NE, report 08 §2). TLS validation is globally disabled in both the C# and Java stacks,
   cleartext is allowed to `*.gameloft.com` and `127.0.0.1`, there is no pinning and no request signing (report 08).
   So localhost-HTTP or DNS-redirect interception is unobstructed. **[V, cross-ref report 08]**

---

## 1. The managed→native P/Invoke surface

### 1.1 Totals and libraries **[V]**

I parsed every `[DllImport(...)]` in the decompiled C# (script `scratchpad/pinv.py`): **2246 declarations**, all
`CallingConvention = CallingConvention.Cdecl`, across exactly two libraries:

| Library string | Count | In APK? | Notes |
|---|---|---|---|
| `"nativeengine"` | 2188 | yes (`lib/<abi>/libnativeengine.so`) | the engine. Every entry point is a flat `ClassName_Method` C export. **[V]** cross-checked: all 2188 entry-point names resolve to real `FUNC GLOBAL` dynamic symbols in NE (`scratchpad/pinv.json` ∩ `ne_exp_names.txt` → 0 missing). |
| `"NativeNicePlaySDK"` | 58 | **no** | 9sPlay SDK. There is no `libNativeNicePlaySDK.so` in the APK, and `NicePlaySDKDLL.Enabled => false` (`NicePlaySDK/Gameloft.NicePlaySDK/NicePlaySDKDLL.cs:6`), so `PluginBootstrap.Boot()` is never called. These 58 P/Invokes are dead in this build. **[V]** |

Mono resolves `"nativeengine"` to `libnativeengine.so` via the format string `lib%s.so` (NE string table; also the
dllmap/`load_dso` path in MD). The declarations live almost entirely in `Gameloft.Engine.dll` (2188); the 58 NicePlay
ones are in `NicePlaySDK.dll`. No P/Invokes reference `libcurl`, `libc`, `libmonosgen`, `libcrashlytics`, or
`libgenerator` directly — the game never calls those by DllImport; they are reached only transitively (NE links curl;
Java loads crashlytics/generator; Mono loads monosgen).

### 1.2 P/Invokes grouped by purpose **[V]** (full table in `scratchpad/cat.py` output)

| Area | #decls | Representative classes (decl count) |
|---|---|---|
| Boot / app core | 286 | `GameApplication` (71), `GameOptionManager` (43), `PlatformInfo` (34), `AppPreferences` (18), `MainLoop` (15), `GameDefinitionComponent` (11), `Profiler` (23), `Watchdog` (5) |
| Rendering / scene | 800 | `Material` (97), `LightmapComponent` (69), `MeshComponent` (67), `Texture` (50), `CameraComponent` (49), `World` (42), `GameObject` (36) |
| UI (Flash/SWF) / text / webviews | 273 | `MenuManager` (45), `ASCharacter` (45), `MenuComponent` (34), `BrowserComponent` (32), `SWFFile` (27), `ASValue` (27), `FlashEngine` (23) |
| Physics (Havok) | 131 | `RigidBodyComponent` (34), `PhysicsObjectComponent` (26), `ColliderComponent` (23), `PhysicsComponent` (22) |
| Animation | 110 | `AnimatorComponent` (44), `EmbeddedAnimatorComponent` (29), `AnimationBinderComponent` (23) |
| Navigation / AI | 83 | `NavMeshComponent` (60), `NavQueryComponent` (14), `NavQuery` (9) |
| Audio (Vox) | 81 | `VoxManager` (47), `SoundEmitter` (28) |
| Input | 54 | `InputManager` (23), `Mouse` (9), `TouchGamepad` (9) |
| File system / data packs | 54 | `GlfStream_private` (26), `GlfMemoryStream_private` (15), `NativeFile` (6) |
| **Game networking (sockets)** | **36** | **`EnetSocket` (23), `TcpNativeSocket` (10), `ServerManager` (3)** |
| **Native Gameloft online libs** | **178** | `NativeNotificationSystem` (33), `SocialLib_private` (23), `TrackingManager` (23), `IapStoreComponent` (19), `AdsComponent` (17), `IapStoreComponentV2` (17), `DlcComponent` (8), `CRMComponent` (7), `AuthenticationComponent` (6), `OnlinePlatformComponent` (2) |
| Crash / analytics | 59 | `Crashlytics` (24), `Fabric` (10), `CrashlyticsStackFrame*` (25) |
| Android / 9sPlay bridges | 7 | `AndroidBridge`, `AndroidNativeMigrate`, `AndroidNicePlayBridge` |
| Logging / QA / dev | 36 | `QAAutomationComponent` (15), `DrawDebug` (9) |

The two groups that matter for networking are **Game networking (sockets)** and **Native Gameloft online libs**.
Everything else is rendering/physics/audio/UI/IO/input and is irrelevant to the server-embedding question.

### 1.3 The socket P/Invokes (the only game-networking native calls) **[V]**

`EN/EnetSocket.cs` (ENet UDP transport, the in-match datagram channel):
```
EnetSocket_Construct(int instance, OnConnectionFunction OnConnected, OnConnectionFunction OnDisconnected, OnPacketReceivedFunction OnPacketReceived)  // :505
EnetSocket_StartServer_private(IntPtr self, uint connections, int listenPort, uint channelLimit)                                                      // :513
EnetSocket_StartClient_private(IntPtr self, uint serverCount, uint channelCount)                                                                      // :527
EnetSocket_Connect_private(IntPtr self, string ip, int port, uint channelCount)                                                                       // :541
EnetSocket_SendPacket(IntPtr self, IntPtr data, int dataLength, int peerId, uint channelId, PacketType type)                                          // :596
EnetSocket_ProcessEvent_private / _Update_private / _FlushHost / _GetPort / _Disconnect / _Shutdown ...
```
`EN/TcpNativeSocket.cs` (a thin wrapper over a raw fd handed to C# — used only for the dedicated-server hand-off, see
§4): `TcpNativeSocket_Construct(int socket)`, `_SendData`, `_ReceiveData`, `_GetAvailable`, `_GetPollWrite`,
`_GetLocalEndPoint`, `_GetRemoteEndPoint`, `_CloseSocket` (`TcpNativeSocket.cs:207-369`).

`EN/ServerManager.cs` (dedicated-server UDS hand-off — **compiled out on Android**, see §4):
`ServerManager_private_StartServer(string path, ...callbacks..., int localPort, bool tunneling)` (`:264`),
`ServerManager_private_Shutdown` (`:272`), `ServerManager_IsLocalServer` (`:280`).

Key point: the **client's** TCP control channel is **not** `TcpNativeSocket`. `LA/BaseServer.cs:30,124` and the client
`EN/TcpSocket.cs:187-190` use **managed** `System.Net.Sockets.Socket`/`TcpClient`/`TcpListener`/`NetworkStream`. The
native `TcpNativeSocket` only wraps a file descriptor that the (Android-stubbed) dedicated-server path would deliver.
So on Android, **the only game networking that is truly native is ENet UDP**; TCP control and all HTTP are managed.

### 1.4 The "native online libs" P/Invokes (what they really reach) **[V]**

These 178 declarations front Gameloft's own native online SDK compiled into NE (strings: `Gaia`, `Janus`, `Pandora`,
`Iris`, `Seshat`, `hermes`, `osiris`, `FederationCRMService`, `glwebtools`, `GlWebTools/` user-agent). Important ones:

* `OnlinePlatformComponent_Init_private(string datacenter, string datacenterCountry, string eveConfig)`
  (`EN/OnlinePlatformComponent.cs:262`). This is the bridge that **feeds the C#-fetched eve config into the native
  online SDK** (`InitializeNative` at `:152-166` passes `Federation.WaitForConfig(...).Config.ToString()`), so if the
  native SDK does any HTTP (CRM/IAP/Iris GameOptions), it resolves through the same `pandora` the C# config named.
* `DlcComponent_DownloadToc_private(IntPtr, string revision, string accessToken)` / `_DownloadFiles_private`
  (`EN/DlcComponent.cs:421,435`). **DLC TOC + file download is native (libcurl).** Driven from
  `GM/GameClientComponent.cs:3009` but *only if* `Args.UseDlc` and a slot change (`:2998-3010`); default build does not
  use DLC. The access token is obtained in C# (`DownloadToc` at `DlcComponent.cs:160-190`).
* `TrackingManager_*` (23): ETS analytics, geo, `IsInternetAvailable` — native curl to tracking endpoints.
* `IapStoreComponent*`, `AdsComponent`, `NativeNotificationSystem`, `SocialLib_private`, `CRMComponent`: IAP receipts,
  ad SDKs, push, GPG/Facebook social, CRM config — all native online, all irrelevant to offline play and all dead
  with servers down.

**Conclusion:** native online code exists, but for the user's goals (offline progression, bots, P2P) the client path
that matters (boot → Federation auth → storage/profile → matchmaker → Anubis → game) is **managed C#**; the native
online SDK is a secondary consumer fed by the same config, and its live uses in this build (DLC, tracking, IAP, ads,
CRM) are either disabled or droppable. See §5 for the exhaustive native-networking inventory.

---

## 2. The Java→native boundary (loadLibrary order and JNI)

### 2.1 Load order **[V]** (`grep System.loadLibrary` across `J/`)

| Order | Caller | Library | When |
|---|---|---|---|
| 1 | `mono/MonoPackageManager.LoadApplication` (`MonoPackageManager.java:51-54`) | `monosgen-2.0`, then `xamarin-app`, then `monodroid` | from `MonoRuntimeProvider.attachInfo` (ContentProvider `initOrder 1999999998`, `AndroidManifest.xml:60-63`) — runs at process start, before any activity |
| 2 | `com.crashlytics.android.ndk.JniNativeApi` static init (`JniNativeApi.java:12-13`) | `crashlytics` (= `libcrashlytics.so`, `BuildConfig.ARTIFACT_ID`) | lazily, when `CrashlyticsNdk` kit is initialised (`CrashlyticsNdk.onPreExecute` → `new JniNativeApi()`, `:39`); kit started by `AndroidUtilsPlugin.CrashlyticStart()` (`:256`), itself called from native (NE JNI, see below) |
| 3 | `com.gameloft.android.MainActivity.onCreate` (`MainActivity.java:77`) | `nativeengine` | after `XamarinMainActivity.OnCreate` (C#) runs `base.OnCreate` |
| 4 | `com.gameloft.android.installer.GameInstaller.run` (`GameInstaller.java:744-755`) | each `.so` in `sNativeLibs` via `System.load(LIBS_PATH + name)`, else fallback `System.loadLibrary("nativeengine")` | background installer thread, only in the data-download path |
| 5 | `com.gameloft.android.GLUtils.Device` (`Device.java:149`) | `generator` (= `libgenerator.so`) | lazily, first `getHDIDFV()` (device-id hashing) |

So by the time managed game code runs, Mono (3 libs) is up; `nativeengine` loads during `XamarinMainActivity.OnCreate`
(Java `MainActivity.onCreate` at `MainActivity.java:77`, reached via `base.OnCreate` from the C# override). The
monodroid DSO path (`load_dso`, `get_full_dso_path`) can also pull native libs from the APK lib dir and from a
`.__override__` dir, but note **`load_dso_from_override_dirs` returns null in this build** (MD `0x9820: mov x0, xzr; ret`),
so the Xamarin "fast-deploy override" directory is a no-op here. **[V]**

### 2.2 The big JNI surface is in NE (native→Java callbacks) **[V]**

NE exports **103 `Java_*` JNI functions** (`scratchpad/ne_exp_names.txt`). The hub is
`com.gameloft.android.PackageUtils.JNIBridge` (`JNIBridge.java`): `NativeInit`, `NativeOnPause/Resume`, `NativeOnTouch`,
`NativeSurfaceChanged`, plus Google-Play-Games/leaderboard callbacks. Other JNI name-spaces: `GLSocialLib` (FB/GPG/
Weibo), `adsmanager` (AdMob/FAN/IronSource/Unity/Vungle), `glads`/`ingamebrowser` (webviews),
`popupslib`, `pushnotification`, `iab.InAppBilling`, `gameoptions.GameOptions`, `installer.GameInstaller.DownloadGO`.
These are native code calling *into* Java SDKs (ads, billing, social, push). None are needed offline. `JNI_OnLoad`
exists at NE `0xec7b08`. **[V]**

`libcrashlytics.so` exports `JNI_OnLoad`, `JNI_Init`, `external_api_*` (set_user_email/log/initialize) — a self-
contained breakpad NDK crash handler, reached only through `JniNativeApi.nativeInit`. `libgenerator.so` exports just
`Java_..._hdidfv_HDIDFV_getNHDIDFV[Version]` — a device-ID generator, nothing else. **[V]**

---

## 3. Role of each native library (confirmed from ELF + strings)

| Library | Size | NEEDED / SONAME | Role (verified) |
|---|---|---|---|
| `libnativeengine.so` | 24 MB | libGLESv2/v3, libEGL, libOpenSLES, libandroid, liblog, libdl, libstdc++, libm, libc | **The engine.** Rendering (GLES2/3, EGL), audio (OpenSLES, "Vox"), physics (Havok — thousands of `hk*` symbols), nav, Flash/SWF UI, file IO, **ENet UDP**, and a **statically linked online stack**: libcurl 7.49.1-DEV, OpenSSL + asio::ssl, Gaia/Janus/Pandora/Iris/Seshat/CRM/IAP. 15703 exported FUNC/OBJECT symbols incl. 103 `Java_*`, 11638 C++-mangled, 2188 flat `ClassName_Method` C exports for the C# P/Invokes. **[V]** |
| `libmonosgen-2.0.so` | 3.5 MB | libz, libdl, libc, libm, liblog | **Mono runtime + SGen GC + JIT.** Build branch `2020-02`. Exports the full embedding API (`mono_jit_init`, `mono_assembly_load_from`, `mono_add_internal_call`, `mono_runtime_invoke`, `mono_image_open_from_data`, `mono_profiler_*`, `mono_dl_fallback_register`) and the `ves_icall_System_Net_Sockets_*` icalls — **this is where managed sockets bottom out** (imports libc `socket/connect/bind/listen/accept/getaddrinfo`). **[V]** |
| `libmonodroid.so` | 218 KB | **libmonosgen-2.0**, libz, liblog, **libxamarin-app**, libm, libstdc++, libdl, libc (SONAME `libmono-android.release.so`) | **Xamarin.Android bridge.** Boots Mono, loads assemblies straight from the APK (`open_from_bundles`, `Setting up for DSO lookup directly in the APK`, mmap of stored zip entries), drives the managed↔Java typemap, registers AOT images (`libaot-*.dll.so`), `mono_jit_set_aot_mode(normal)`. Reads `debug.mono.*` system properties; `set_profile_options` can `monodroid_profiler_load` a `libmono-profiler-*.so` (a secondary native-load vector, §8.5). **[V]** |
| `libxamarin-app.so` | 36 KB | (none) SONAME `libxamarin-app.so` | **Generated app config blob**, not code (0 FUNC exports). Holds `application_config`, the 6-module managed↔Java typemap + MVIDs (report 08 §7.4), and the env block: `mono_aot_mode_name=normal`, `MONO_GC_PARAMS=major=marksweep`, `XA_HTTP_CLIENT_HANDLER_TYPE=System.Net.Http.HttpClientHandler`, `XA_TLS_PROVIDER=btls`, `__XA_PACKAGE_NAMING_POLICY__=LowercaseCrc64`. **[V]** The `btls`/HttpClientHandler env is why managed HTTPS uses `libmono-btls-shared.so`. |
| `libmono-btls-shared.so` | 1.3 MB | libm, libdl, libc | **Mono's BoringSSL (BTLS) provider** for managed TLS (`mono_btls_ssl_connect`, `mono_btls_x509_*`). Used by `System.Net.Http.HttpClientHandler`/`HttpWebRequest`. Irrelevant offline because TLS validation is off anyway (report 08 §4). **[V]** |
| `libmono-native.so` | 685 KB | libmonosgen-2.0, libz, … | **Mono `System.Native` PAL** (`SystemNative_*` for file/socket/poll syscalls) + Brotli. Backs `libmono-native`-mapped icalls. **[V]** |
| `libgenerator.so` | 866 KB | libc, libm, libdl | **Device-ID generator** (`HDIDFV`). Two JNI exports only; no networking imports of note (libc locale/pthread only). Compiled clang r339409/LLVM 8. **[V]** |
| `libcrashlytics.so` | 297 KB | liblog, libandroid, libstdc++, libm, libc, libdl | **Fabric/Crashlytics NDK (breakpad)** native crash capture. Self-contained (`JNI_OnLoad`, `external_api_*`). Imports no sockets. **A good candidate "non-essential lib to replace/proxy" (§8.5)** because nothing the game needs depends on it. **[V]** |
| `libaot-*.dll.so` (×59) | — | — | **Precompiled AOT images**, one per managed assembly, registered by monodroid. Supplemental to the `.dll`; on MVID mismatch Mono JITs instead (report 08 §7.3). Not libraries you call. **[V]** |

---

## 4. Dedicated-server hand-off is stubbed on Android (confirms the managed/listen-server route) **[V]**

`ServerManager.StartServer()` (`EN/ServerManager.cs:221-243`): if `!LocalServer` (i.e. `Args.UnixDomainSocketPath` is
set) it calls native `ServerManager_private_StartServer(path, onAnubisMessage, onClosed, port, tunneling)`; else it
calls `LocalAnubisManager.Start()` (the managed in-process path).

Disassembly of the native export (NE `0x399ac8`) shows it tail-calls a function (`0x3e2894`) that is literally
`mov w0,#-1; ret`. So on Android the native UDS dedicated-server hand-off **always returns -1**, and `StartServer()`
takes the `<=0` branch → `StopServer()`. `ServerManager_IsLocalServer` (`0x399b08`) and `_Shutdown` just read/clear a
byte. Therefore the native "accept a client fd over a unix socket and wrap it in `TcpNativeSocket`" path does not exist
on this build — **the only working server path is the managed LocalAnubis listen-server** (reports 05 §9, 06). This is
the strongest single argument that the embedded server should be **managed**, not a native `.so`.

(Native `listen/accept/bind` call-sites do exist in NE, but string-xref shows they belong to Havok's `hkBsdSocket`
debug server, the glf_make/AutoMake dev service, and the native online SDK's HTTP — not to the game server. `scratchpad/xref.py` output.)

---

## 5. Exhaustive inventory: does native code do HTTP or game sockets? **[V]**

NE imports from libc (dyn relocations): `socket, connect, bind, listen, accept, getaddrinfo, getnameinfo,
gethostbyname, send, sendto, sendmsg, recv, recvfrom, recvmsg, getsockname, getpeername, getsockopt, setsockopt,
shutdown, if_indextoname, inet_*`. So NE *can* do arbitrary sockets. What it actually uses them for (string-xref of
each call site, `scratchpad/xref.py`):

* **libcurl 7.49.1-DEV + OpenSSL/asio** statically linked (strings `libcurl/7.49.1-DEV`, `CURLOPT_*`,
  `openssl.org/support/faq.html`, `asio::ssl::openssl_init`). This is the native online SDK's HTTP client
  ("glwebtools", user-agent `GlWebTools/`, `glwebtools::JsonReader/JsonWriter`). Call sites at `0xe34240`/`0xe43680`/
  `0xe68264` carry strings like `POST`, `Host:`, `sent post data to host`, `Establish HTTP proxy tunnel to %s:%hu`.
  Used by: **CRM** (`FederationCRMService`, `[CRM] ...iris...`), **IAP** (`iap_federation_crm.cpp`), **Iris asset /
  GameOptions** download (`/locate/asset`, `/assets/`, `/GameOptions`, `gameoptions.gameloft.com`), **DLC** TOC/files,
  **tracking** (`sink.gameloft.com`, `track.php`), **ads** (`ingameads.gameloft.com`), **IGP cross-promo**
  (`201205igp.gameloft.com`). Hardcoded native hosts: `eve.gameloft.com`, `http://eve.gameloft.com:20001/config/`,
  `gameoptions.gameloft.com`, `sink.gameloft.com`, `ingameads.gameloft.com`, `201205igp.gameloft.com`. **[V]**
* **ENet UDP** statically linked (100 `enet` strings; `EnetSocket_*` exports). This is the **in-match datagram
  transport** — the one piece of *game* networking that is native. Bound via `EnetSocket_StartServer_private(...,
  listenPort, ...)`; the listen-server binds an ephemeral/any-address port (report 05 §3.2, 06 §6). **[V]**
* **Havok `hkBsdSocket`** debug/telemetry server (`System/Io/Platform/Bsd/hkBsdSocket.cpp`, `Listening on host [...]`)
  and the **glf_make/AutoMake** dev content service (`Unable to open socket for glf_make service`) — developer-only,
  not reached in a retail launch. **[V]**

**What native networking means for the project:**
* The **client's Federation HTTP, Anubis TCP, and game TCP control** are **managed** → you intercept them in C#
  (redirect eve URL, or interpose the `HttpClientHandler`, §8.2). No native work.
* The native SDK's HTTP (CRM/IAP/Iris/tracking/ads) is fed the eve config through
  `OnlinePlatformComponent_Init_private`, so **if you point eve at your stub, the native side also resolves pandora →
  your host**; you can additionally DNS/hosts-redirect the hardcoded hosts above, or simply let those calls fail
  (they are telemetry/IAP/ads, non-blocking for offline play — report 08 §6). The one native online call that could
  block the menu is the **Iris `GameOptions`** fetch (GPU quality profile); it has ETag/disk caching
  (`GameOptions_saved.json`, `RequestIrisObject::GetGameObjectFromDisk`) so a once-captured file satisfies it, and
  `-DataCenter`/offline handling (`federation_offline`, `ProccesGameObjectWONetwork`) exists. **[L]** (exact Iris
  offline behaviour is report 04/10 territory.)
* **ENet** must keep working for in-match traffic. You do **not** reimplement it — you reuse it. For LAN/P2P the host's
  managed server just calls the existing `EnetSocket_StartServer_private`; guests `EnetSocket_Connect_private` to the
  host IP (report 05/06). So native ENet is an asset, not an obstacle.

Net: **no game-critical networking that you must reimplement lives in native code.** The server you embed is the
managed Federation + LocalAnubis backend; native ENet stays as-is.

---

## 6. The existing managed entry points you can hook (no adb, no new activity) **[V]**

### 6.1 Boot hook chain
`XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:63-107` (`OnCreate`):
```
second = Intent.GetStringExtra("phantomArgs")?.Split(':') ?? [];
second = [ MainLoop.Executable + "/../.." , ...second ];
GameDll.OnInitialize();                 // reflective, may be null
AdjustSdkDll.OnInitialize();
AdjustReferrerReceiver.OnInitialize();
NicePlaySDKDLL.OnInitialize();          // <-- currently a no-op (Enabled => false)
base.OnCreate(new Bundle());            // Java MainActivity.onCreate -> System.loadLibrary("nativeengine")
MainLoop.Start(ModeType.DotNetHostsNative, second);   // -> native MainLoop_Start -> callback OnStarted(argument) -> GameApplication.Create(Arguments.FromString(argument))
```
`GameDll` is an empty class (`Game/Game/GameDll.cs`), `NicePlaySDKDLL.OnInitialize` only logs and (when `Enabled`,
which is false) boots the NicePlay native plugin (`NicePlaySDK/.../NicePlaySDKDLL.cs:8-17`). So **either `GameDll`
(add a static `OnInitialize`) or `NicePlaySDKDLL.OnInitialize` is a free, early, guaranteed managed hook.** Both run
before `MainLoop.Start`, i.e. before the engine and before `GameApplication` exists — perfect for starting a server
and installing an arg rewriter.

### 6.2 Argument parsing you can target
`MainLoop.OnStarted` (`EN/MainLoop.cs:100-110`) is a `[MonoPInvokeCallback]` the native `MainLoop_Start` calls back
with the final `argument` string; it then does `GameApplication.Create(Arguments.FromString(argument))`.
`Arguments.FromString` → `ApplicationArguments.Parse<Arguments>` splits on `\n`, treats `-Name value` tokens, field
names case-insensitive (`EN/ApplicationArguments.cs:25-89`). The `Arguments` class exposes `EnvironmentSwitcherUrl`,
`ServerLocation`, `AnubisCommand`, `SuperCredential`, `SuperPassword`, `DataCenter`, `LocalAnubis*Port`, `Offline`,
etc. (`EN/GameApplication.cs:44-170`). **If you rewrite `argument` before `GameApplication.Create` sees it, you set any
of these with no adb and no colon problem** (the eve URL has `:`/`/` which breaks `phantomArgs`, but a managed injector
builds the `\n`-joined string directly).

I verified an injector offline (`scratchpad/poc/ArgInject.cs`): from `Bootstrap.Init()`, reflect
`MainLoop.OnStartedDelegate` (private static), wrap it so the inner delegate appends
`-EnvironmentSwitcherUrl\nhttp://127.0.0.1:18080/\n-ServerLocation\nEmbedded\n-AnubisCommand\nQuickLaunch\n...` to
`argument`, keep both delegates in static fields (so the native thunk stays alive), and write the field back. Compiles
clean against the shipped trimmed assemblies. **[V]**

### 6.3 The `args.txt` native reader (secondary, Java-limited)
NE reads `res/raw/args.txt` and `<DataPath>/args.txt` at start-up (string `/args.txt` at NE `0x12e87b2`, reader
function `0x3d59fc`; `UnixDomainSocketPath` et al. parsed there). But the Java `AndroidUtilsPlugin.getArguments`
(`:120-147`) only extracts `-UseDlc` and `-LogCategoryFilter` from `R.raw.args` into native globals
(`GameApplication_GetArguments/GetUseDlc/GetLogCategoryFilter`, consumed at `EN/GameApplication.cs:1161-1163`).
`res/raw/args.txt` in the APK currently contains just `-LogCategoryFilter Fatal`. So args.txt is a *weak* channel here;
the managed injector (§6.2) is the reliable one.

---

## 7. Can a managed server actually be built against the shipped (trimmed) assemblies? Yes — verified **[V]**

Risk with Xamarin builds: the BCL in the APK is **linker-trimmed**, so types you need might be gone. I checked and
then compiled a working server against the **exact shipped DLLs** (`extracted/assemblies/*.dll`), not a fresh BCL.

Surviving, usable types (confirmed with `ilspycmd -l`/`-t`):
* `System.Net.HttpListener`, `HttpListenerContext/Request/Response`, `HttpListenerPrefixCollection`,
  `GetContextAsync()` — **present** in the trimmed `System.dll`. `Response.OutputStream/StatusCode/ContentType/
  Headers/ContentLength64/Close` all present. **[V]** (This is exactly what the engine's own `HTTPServer`/LocalAnubis
  `Finder` already use — `EN/HTTPServer.cs:30`, so it must be present.)
* `System.Net.Sockets.TcpListener/TcpClient/UdpClient/Socket/NetworkStream`, `System.Net.Dns`, `IPAddress` — present.
* `System.Net.Http.HttpMessageHandler` (abstract, overridable `SendAsync`), `HttpClient`, `HttpClientHandler`,
  `HttpRequestMessage/HttpResponseMessage` (settable `Content`, `RequestMessage`; addable `Headers` incl. `ETag`,
  `Date`), `StringContent`, `StreamContent`, `DelegatingHandler` — present in trimmed `System.Net.Http.dll`. **[V]**
* `Newtonsoft.Json` 12-ish — present. Async plumbing: `ICriticalNotifyCompletion`/`IAsyncStateMachine` present, but
  `TaskAwaiter` only implements `ICriticalNotifyCompletion` (not `INotifyCompletion`), so **C# `await` does not compile
  against this mscorlib**; write the server with `.Result`/`Task.FromResult`/continuations instead of `await`. (My
  first PoC failed on exactly this; the non-async version compiles.) **[V]**

**Compiled artifact:** `scratchpad/poc/MCVS.Server.dll` (Roslyn `csc`, `-nostdlib -langversion:7.3`, referencing only
the shipped `mscorlib/System/System.Core/System.Net.Http/Newtonsoft.Json/Gameloft.Engine/Gameloft.OnlinePlatform/
Gameloft.Util`). It defines:
* `Bootstrap.Init()` — idempotent; starts `new HTTPServer(new FedHttpFrontEnd(), 18080, "localhost")` (reusing the
  engine's own `HTTPServer` class) and/or exposes `CreateFedInnerHandler()`.
* `Router.Handle(method, uri, body) -> (status, contentType, body, etag)` — the pure request handler (stub returns
  `{"eve":"on",...}` for `/`; you fill in the Federation routes from reports 02/03/04/06).
* `InProcessFedHandler : HttpMessageHandler` — an in-process handler that answers eve/pandora/auth/... with **no
  socket**, and passes through `localhost`/LAN hosts (e.g. the LocalAnubis Finder on :51750, or a real LAN host) to a
  real `HttpClientHandler`.
* `FedHttpFrontEnd : HTTPServerMessageHandler` — the same `Router` behind a real localhost `HttpListener`, for native-
  SDK HTTP and for debugging with curl.
The reference set confirms it only needs assemblies that are already in the APK (`scratchpad/poc` ref list), and the
built DLL's assembly-ref table names only `mscorlib/System*/Newtonsoft.Json/Gameloft.Engine` — **no netstandard facade,
no System.Private.CoreLib** — so it drops into `assemblies/` as a classic Xamarin assembly. **[V]**

---

## 8. Ranked embedding options

Effort/Risk are relative to the preservation goals. All APK-modifying options require re-sign + neutralising the one
native cert gate (report 08 §2.4, a one-line smali/IL patch) and re-adding the patched DLLs as **stored, 4-byte-
aligned** `assemblies/*.dll` entries with the matching `libaot-*.dll.so` deleted so Mono JITs (report 08 §7.3).

### Option A — Managed server assembly + IL hook in `NicePlaySDKDLL.OnInitialize` **(recommended core)**
**What:** Add `assemblies/MCVS.Server.dll` (§7). IL-patch `NicePlaySDKDLL.OnInitialize` (or add `GameDll.OnInitialize`)
to `call MCVS.Server.Bootstrap.Init()`. `Init()` installs the arg injector (§6.2) to force
`-ServerLocation Embedded -AnubisCommand QuickLaunch -EnvironmentSwitcherUrl http://127.0.0.1:18080/` and starts the
Federation backend. The game then boots through its own LocalAnubis listen-server (reports 05/06); your backend only
answers the Federation HTTP the client already makes.
**Why best here:** the server/matchmaker/lobby are already managed and already in-process; you add *backend* logic, not
a transport. No native code, no new activity, no adb. Reuses the engine's `HTTPServer`/`HttpListener`.
**Steps (verified offline up to the patched DLLs):**
1. Build `MCVS.Server.dll` against `extracted/assemblies/*.dll` (done; `scratchpad/poc`).
2. Cecil-patch `NicePlaySDK.dll`: clear `NicePlaySDKDLL.OnInitialize`, emit `call Bootstrap.Init; ret`. (done;
   `scratchpad/patcher`, output verified by re-decompiling: body is now `Bootstrap.Init();`.) `NicePlaySDK` is **not**
   a typemap/MVID module (report 08 §7.4 lists the 6 locked ones; NicePlaySDK/Game/Gameloft.* are not among them), so
   no MVID preservation needed. Add `MCVS.Server.dll` reference from the patched method (Cecil `ImportReference`).
3. Add `MCVS.Server.dll` + patched `NicePlaySDK.dll` to `assemblies/` (stored+aligned). Delete
   `libaot-NicePlaySDK.dll.so` (both ABIs). `MCVS.Server.dll` needs no AOT image.
4. Neutralise the native cert gate; zipalign + apksigner (report 08).
**Risks:** `Init()` runs very early (before `GameApplication`); keep it to "start listener + install injector", defer
anything needing the engine. The injector reflects a private static field (`MainLoop.OnStartedDelegate`) — stable in
this build but re-verify the field name after any patch. Keep delegates rooted (static fields) or the GC frees the
native thunk. Effort: **Medium.** Risk: **Low-Medium.**

### Option B — In-process `HttpClientHandler` interpose (recommended transport, pairs with A)
**What:** IL-patch `Federation.CreateRequestHandler` (`OP/Federation.cs:468-472`, IL at RVA 0xcfe4) so the inner
handler is `MCVS.Server.Bootstrap.CreateFedInnerHandler()` instead of `new HttpClientHandler()`. That returns an
`InProcessFedHandler : HttpMessageHandler` that answers eve/pandora/auth/storage/... **directly in managed memory**
(no socket, no port, no TLS, no cleartext-policy concern), while passing through real hosts (LocalAnubis `localhost:
51750`, LAN peers) to a real `HttpClientHandler`.
**Why:** removes the only fragile parts of the localhost-HTTP approach (binding a port, `HttpListener` ACLs, the
`localhost` Host-header quirk that already bites LocalAnubis — report 06 §4). Request/response flow is a pure function
you can unit-test. `Connectivity.IsInternetAvailable` and `AssetsService` make their own `new HttpClient()` without the
Fed handler (`EN/Connectivity.cs:10`, `OP/AssetsService.cs`), so those still need the eve URL redirect or will just
fail-soft; keeping the localhost `HttpListener` from Option A as a fallback covers them.
**Verified offline:** the single-instruction IL edit (`newobj HttpClientHandler::.ctor` → `call CreateFedInnerHandler`)
applied with Cecil and re-decompiled to `new HttpClient(new FedRequestHandler(Bootstrap.CreateFedInnerHandler(), this))`
(`scratchpad/patcher`). `Gameloft.OnlinePlatform.dll` is not MVID-locked. `HttpMessageHandler.SendAsync` is overridable;
build responses with `Task.FromResult` (no `await`, §7). **[V]** Effort: **Low.** Risk: **Low.**

### Option C — Native `.so` injected (the Dead-Trigger-2 analogue)
**What:** Ship a `lib/<abi>/libmcvsserver.so` and get it loaded, then expose the server over `127.0.0.1:<port>` HTTP
(the C# client reaches it via the eve redirect). Load vectors:
* **smali `System.loadLibrary("mcvsserver")`** added to `MainActivity.onCreate` right after
  `System.loadLibrary("nativeengine")` (`MainActivity.java:77`) — classic, requires editing `classes*.dex` (smali) and
  re-sign.
* **Replace/proxy `libcrashlytics.so`** — it is loaded via `System.loadLibrary(BuildConfig.ARTIFACT_ID="crashlytics")`
  from `JniNativeApi` and nothing the game needs depends on its behaviour (report 08 §6); a drop-in `libcrashlytics.so`
  that also starts your server (and no-ops the crash API, or forwards to the real one renamed) loads with no smali
  edit. But it loads **late/lazily** (only when `CrashlyticStart()` runs), so it is worse than the boot hook for timing.
* **A managed `[DllImport("mcvsserver")]`** from a patched assembly — but then you are editing managed code anyway, so
  Option A is simpler.
* **monodroid profiler load** (`debug.mono.profile` → `libmono-profiler-<name>.so`) — a documented native-load vector
  but gated by a system property and meant for profilers; not for shipping.
**Why not first choice here:** unlike DT2, MCVS's server is **C#**, and an in-process C# server already exists
(LocalAnubis). A native server would mean re-implementing matchmaker/lobby/storage in C++ and *still* bridging to the
C# client over localhost HTTP — strictly more work than A/B. Use C only if you specifically want a native relay/P2P
layer or to keep the server reusable outside Mono. Effort: **Med-High.** Risk: **Medium.**

### Option D — External/local companion process (no in-process code)
**What:** A separate tiny Android service/APK (or a userland binary) runs the Federation+matchmaker server on
`127.0.0.1:<port>` or a LAN IP; the game is pointed at it via `args.txt`/eve-redirect/DNS (report 01/02). No injection
into the game process at all.
**Why:** cleanest separation, easiest to iterate/debug, and the server can be the same code for LAN hosts. For P2P this
is natural (the host runs the companion; guests point `-RemoteIp` at it — report 06 §10). Downside: a second component
to install and keep alive; loopback only works per-device unless bound to the LAN IP. Effort: **Medium.** Risk: **Low.**

### Recommended combination
**A + B**, with **D** for LAN/internet hosting. Concretely: one `MCVS.Server.dll` that (1) is started by the IL hook in
`NicePlaySDKDLL.OnInitialize`, (2) installs the arg injector to set `Embedded`/`QuickLaunch` and the eve URL, (3)
provides both the in-process `HttpMessageHandler` (via the `Federation.CreateRequestHandler` patch) and a localhost
`HttpListener` fallback for the non-Fed `HttpClient` callers and the native SDK. The native side (ENet, engine) is
untouched. For LAN/P2P, the same server binds the LAN IP and guests redirect to it (report 06). This uses **no native
server code**, reuses MCVS's own LocalAnubis and ENet, and needs only: 2 IL patches (NicePlaySDK, OnlinePlatform) +
1 new managed DLL + the cert-gate neutralisation + re-sign.

### How the game reaches the embedded server (summary)
| Transport | Reached by | Served by |
|---|---|---|
| Federation HTTP (eve/pandora/auth/storage/social/matchmaker) | `EnvironmentSwitcherUrl` redirect **or** `HttpClientHandler` interpose (Option B) | `MCVS.Server` Router (in-process or localhost) |
| LocalAnubis Finder/Lobby/Controller | already in-process (loopback TCP/HTTP) | the game's own managed LocalAnubis (reuse, reports 05/06) |
| Game control TCP + ENet UDP | loopback / LAN, via the engine | native ENet (reuse) + managed TcpSocket |
| Native SDK HTTP (CRM/IAP/Iris/tracking/ads) | eve config via `OnlinePlatformComponent_Init_private`, or DNS-redirect of the hardcoded hosts | localhost `HttpListener` or fail-soft |

---

## 9. Verified vs inferred

**Verified (read/disassembled/compiled/patched):** the 2 P/Invoke libraries and 2246 decls and their grouping; all
2188 `nativeengine` entry points resolve to real exports; `NativeNicePlaySDK` absent and disabled; the loadLibrary
order and JNI surfaces; each native library's role from ELF deps + symbols + strings; NE statically links libcurl
7.49.1 + OpenSSL/asio + ENet + the Gameloft online SDK; native hardcoded hosts; the Android dedicated-server UDS stub
returns -1; managed `HttpListener`/sockets/`HttpMessageHandler`/`HttpClient` survive trimming; the boot hook chain and
`MainLoop.OnStarted`/`Arguments.FromString`; `btls`/`HttpClientHandler` env in `libxamarin-app.so`; the override-DSO
loader is a stub in monodroid; **built `MCVS.Server.dll` against the shipped assemblies**; **Cecil-patched
`NicePlaySDKDLL.OnInitialize` → `Bootstrap.Init()` and `Federation.CreateRequestHandler` → `CreateFedInnerHandler()`
and re-decompiled to confirm.**

**Likely:** that pointing eve at the stub also steers the native SDK's HTTP (fed through
`OnlinePlatformComponent_Init_private`); that the Iris `GameOptions` native fetch is the only native online call that
can gate the menu and is satisfiable by a cached file; that `libcrashlytics.so` can be replaced without side effects.

**Speculative:** exact runtime behaviour of the native online SDK when all hosts are redirected/blocked (needs a device
capture); whether a headless `-Server` Android instance initialises (report 05 §9).

## 10. Open questions (outside this area)
* Exact Federation route bodies (reports 02/03/04) that `Router.Handle` must return — this report only proves the
  hosting mechanism, not the payloads.
* Whether the native Iris `GameOptions`/CRM fetch blocks first boot offline, and the on-disk cache format (report 04/10).
* Whether `GameApplication` reads `ClientId` from OBB/`GameDefinitionComponent` template data in a way that needs the
  data pack present before the server is reachable (report 01/10).
