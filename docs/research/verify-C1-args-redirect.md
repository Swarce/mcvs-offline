# Verification C1: redirect via args.txt with no APK change

Verdict: **PARTIALLY CONFIRMED.** The main mechanism holds. Two sub-claims are wrong or overstated, and a few conditions apply.

* (a) **Confirmed.** `phantomArgs` cannot carry an absolute URL.
* (b) **Wrong as written.** The native engine reads exactly one args file: `<GlfRootPath>/args.txt` on external storage. Every token in that file reaches `GameApplication.Arguments`. `res/raw/args.txt` is **not** part of that stream. A separate Java reader opens it, extracts only `-UseDlc` and `-LogCategoryFilter`, and those values *overwrite* the two fields after parsing. Report 09 §6.3 says args.txt is a "weak channel" (Java only extracts `-UseDlc` and `-LogCategoryFilter`). That is **refuted**: it conflates the two readers.
* (c) **Confirmed for the Federation/eve chain, with conditions.** "All" traffic is too strong: the Java installer and some hard-coded managed hosts are not redirected.

I traced every step myself. Paths below are relative to `/home/user/mcvs-work`. NE means `extracted/lib/arm64-v8a/libnativeengine.so` (the file offset equals the vaddr for `.text` and `.rodata`). I disassembled it with `llvm-objdump --triple=aarch64` and found xrefs with an ADRP/ADD and BL scanner (`scratchpad/c1/xref.py`, `bl.py`). I did not execute anything.

---

## 1. The real data flow (code-verified)

```
XamarinMainActivity.OnCreate (C#)
  second = ["<Exe>/../..", phantomArgs.Split(':')...]          XaLaunchANMP/.../XamarinMainActivity.cs:65-71
  MainLoop.Start(DotNetHostsNative, second)                    :107
    -> NE MainLoop_Start @0x39eb50 -> 0x3d50f0: mode<2, so argv is copied into the
       global std::string[] at 0x17028a0+0x60, argc at +0x4, onStarted at +0x58; returns
MainActivity.onResume -> startActivityForResult(GameInstaller,100)   MainActivity.java:136
MainActivity.onActivityResult(100, RESULT 1) -> JNIBridge.NativeInit()  MainActivity.java:167-172
  NE NativeInit @0xec7cfc
    -> 0xec5a78: AndroidUtils.Retrieve{SDCard,Obb,Data,Save,Temp,NativeLibrary}Path()
       (strings at 0x133de10..0x133de66, GetStaticMethodID = env+0x388, GetStringUTFChars = env+0x548)
       RetrieveDataPath() is stored in the global std::string at 0x181a1f0+0x30
    -> 0x55c174: pthread_create(entry 0x55be0c)
       -> 0x3d4f98 new MainLoop(0x470) -> ctor 0x3d545c
          ctor: if global argc>0, use the argv saved by MainLoop_Start      @0x3d5588-0x3d55fc
          -> 0x3d59fc (args reader), called @0x3d57b8
             path = *(0xeca304 -> 0x181a220) + "/args.txt"     @0x3d5a2c-0x3d5a3c (0x38cb28 = operator+)
             parser(this+0x440).LoadFile(path)                 @0x3d5a48 -> 0x31e4ac
             parser.ParseArgv(argc, argv)                      @0x3d5a68 -> 0x31df94
             ... native keys read: UnixDomainSocketPath, syslog, mono, headless, server, ...
             this+0x3b8 = parser.ToString()                    @0x3d6514 -> 0x31e86c, stored @0x3d6544
       later 0x3d68f4: onStarted(this+0x3b8 c_str)             @0x3d6938-0x3d6950 (blr [this+0x3e8])
MainLoop.OnStarted(argument) -> GameApplication.Create(Arguments.FromString(argument))  MainLoop.cs:100-110
GameApplication ctor: GetArguments(); arguments.UseDlc = UseDlc; arguments.LogCategoryFilter = LogCategoryFilter
                                                               GameApplication.cs:1162-1164
```

`MainLoop_GetGlfRootPath` (@0x39ebb0) calls the same getter `0xeca304`. `GameApplication.FindRootPath()` returns that value (`GameApplication.cs:1374-1380`), and `Data` is `RootPath/Data` (`:1182`). So **args.txt sits in the same directory as the downloaded `Data/` folder.**

### 1.1 Which file, and the exact on-device path

`AndroidUtils.RetrieveDataPath()` returns the `SDFolder` pref, or `getExternalFilesDir(null)` when it is unset:

```java
// decompiled-java/sources/com/gameloft/android/PackageUtils/AndroidUtils.java:137-140
String preferenceString = SUtils.getPreferenceString(SUtils.getApplicationContext(), "SDFolder", GameInstaller.mPreferencesName);
return preferenceString != "" ? preferenceString : SUtils.getApplicationContext().getExternalFilesDir(null).getAbsolutePath();
```

`GameInstaller.onCreate` sets `SDFolder = C()` on first run (`installer/GameInstaller.java:1927-1933`). `C()` and `B()` (`:2040-2110`) list `getExternalFilesDir(null)` plus every `/proc/mounts` line containing `/mnt/sdcard` or `/storage/sdcard`. `C()` returns the **last** entry. On current devices that is just the external files dir:

**`/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/args.txt`** (that is, `/sdcard/Android/data/<pkg>/files/args.txt`).

Caveat: on old devices with `/storage/sdcard*` mounts, the folder may be on that volume instead. The rule that always holds is: put the file next to `Data/`.

`res/raw/args.txt` is never written anywhere. Its only reference is `openRawResource(R.raw.args)` (`AndroidUtilsPlugin.java:121`). Searching the Java sources for `args.txt` or `R.raw.args` finds nothing else. The only NE xref to `"/args.txt"` (0x12e87b2) is at 0x3d5a30. The only APK file named args is `res/raw/args.txt`, and there is no `assets/args.txt`.

### 1.2 Tokenizer and merge rules (NE 0x31e178, 0x31db24, 0x31df94, 0x31e86c)

* **File tokenizer:** the delimiter mask is `0x100002400`, so the delimiters are **space, `\n` and `\r` only** (@0x31e1c0-0x31e21c). **Tab is not a delimiter.** There is no quoting or escaping. A UTF-8 BOM becomes part of the first token, so the first `-Key` is lost.
* **argv:** each argv element is one token and is never split on spaces (@0x31dfec-0x31e068).
* **Per-token logic (0x31db24):**
  * A token starting with `-` flushes the pending (name, value) into a `std::map<string,string>` at parser+0x18. That token minus the `-` becomes the new name.
  * Any other token becomes the value. Later tokens are appended as `" " + token` (0x31de3c).
  * A token with no pending name is stored as positional, keyed by index.
  * A sentinel `"-"` (0x12e739a) flushes at the end of each source.
* **Insert is emplace-if-absent:** it checks `*slot != 0` and skips (@0x31dba4-0x31dba8). The map comparator uses `memcmp` (0x36d534), so it is **case-sensitive**.
* **Order:** the file is parsed **before** argv. So for a key spelled identically, **args.txt wins over phantomArgs**. Keys that differ only in case are both emitted.
* **Output (0x31e86c):** positional values come first, each followed by `\n`. Then, for each key in byte-sorted order: `-Key\n`, then `value\n` if the value is not empty (newline `0xa` @0x31e8b8 and @0x31ec64; empty-value skip @0x31ed7c).
* **C# side:** `ApplicationArguments.Parse` (`decompiled/Gameloft.Engine/Gameloft.Engine/ApplicationArguments.cs:27-86`) splits on `\n`.
  * It ignores non-`-` tokens and matches fields with `IgnoreCase`.
  * A bool field takes no value.
  * An enum field uses `Enum.Parse(type, text)`, which is **case-sensitive**: `-ServerLocation embedded` throws.
  * A non-bool key with no value as the last token throws `"Need to set a value for"`.

A simulation of these rules (`scratchpad/c1/sim.py`) gives:

```
[phantomArgs only] native->OnStarted: '<Unknown>/../..\n-EnvironmentSwitcherUrl\nhttp //127.0.0.1 18080/\n'
    C#: {'EnvironmentSwitcherUrl': 'http //127.0.0.1 18080/'}
[args.txt only] ...'-EnvironmentSwitcherUrl\nhttp://127.0.0.1:18080/\n-ServerLocation\nEmbedded\n'
    C#: {'EnvironmentSwitcherUrl': 'http://127.0.0.1:18080/', 'ServerLocation': 'Embedded'}
[both, same key]  args.txt value kept
[BOM first line]  C#: {}      [tab separator]  C#: {}
```

### 1.3 (a) phantomArgs

```csharp
// decompiled/XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:66-71
string stringExtra = Intent.GetStringExtra("phantomArgs");
if (stringExtra != null) { second = stringExtra.Split(':'); }
second = Enumerable.Repeat(Path.Combine(MainLoop.Executable, "..", ".."), 1).Concat(second).ToArray();
```

Every absolute URI contains `scheme:`. The native side re-joins the pieces with single spaces, so the value becomes `http //host port/`. A scheme-less value does not help either: the eve URL is used raw in `new Uri(new Uri(uriString), Path)` (`FedRequestMessage.cs:222`), and `new Uri("127.0.0.1/")` is relative, so it throws. **(a) is confirmed.**

### 1.4 The res/raw/args.txt path (the Java reader)

```java
// decompiled-java/sources/com/gameloft/android/PackageUtils/AndroidUtilsPlugin.java:120-131
BufferedReader r = new BufferedReader(new InputStreamReader(...openRawResource(R.raw.args)));
... if (readLine.equals("-UseDlc")) h = true; else if (readLine.contains("-LogCategoryFilter")) { i += " "; i += readLine; }
```

* NE `GameApplication_GetArguments` (0x39d5e4 → 0x410624) calls `AndroidUtilsPlugin.getArguments()` (strings 0x12e9387 and 0x12e95bd, call at @0x410708-0x410724).
* `GameApplication.cs:1162-1164` then overwrites `arguments.UseDlc` and `arguments.LogCategoryFilter` unconditionally.
* Consequences:
  * `-UseDlc` and `-LogCategoryFilter` placed in the user args.txt have no effect on the managed side.
  * `res/raw/args.txt` (`-LogCategoryFilter Fatal`) does not take part in the token merge.

### 1.5 Does the installer overwrite or delete the user file? No code path found

These are all the delete sites in `GameInstaller.java`:

| Line | What it deletes |
|---|---|
| 445 | Zip entries that do not match |
| 554 | File-list entries not in the components |
| 808-818 | `Data/` when `Data/1/0/0/` exists |
| 1619 | `shader_info_cache` |
| 1696 | `RemoveFolderFromData`: no callers |
| 1974 | `SaveFolder/pack*.info` |
| 2028-2031, 2046-2048 | `File.delete()` of the files dir: this fails on a non-empty directory |
| 2904 | One file-list entry |

`AndroidUtilsPlugin.onPluginStart` deletes only `/sdcard/Android/obb/<pkg>` (`:100`).

The CDN pack manifest (`datapack/1.17.32/ETC/manifest.json`) has a root section `".\"` with `count 0`. So extracting the pack writes no root-level file and cannot clobber args.txt. The datapack was present and its `index.json` says it is complete. I only used its manifest.

### 1.6 When the file is read

The file is read **once per process**, inside the game thread created by `NativeInit`, which runs only after `GameInstaller` returns `RESULT 1` (`MainActivity.java:167-172`). The path is resolved at that point, after the `SDFolder` pref exists. Editing the file therefore needs a full process kill and relaunch.

---

## 2. (c) Does the URL value work?

```csharp
// decompiled/Gameloft.Engine/Gameloft.Engine/OnlineLibsSettingsComponent.cs:68
public static string EnvironmentSwitcherUrl => GameApplication.Instance.Args.EnvironmentSwitcherUrl ?? "https://eve.gameloft.com/";
// decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/Federation.cs:404-406
if ((string)service == (string)OnlineService.EnvironmentSwitcher) { return EnvironmentSwitcher; }   // raw, no FormatUri
// FedRequestMessage.cs:220-222
string uriString = await fed.LocateService(Service, Protocol, ct);
return await PrepareToSend(new Uri(new Uri(uriString), Path), ct);
```

* **The eve base URL is used verbatim, with no host whitelist.** `http://` and any port are accepted. The trailing `/` matters: with a base that has a path segment and no trailing slash, that segment is dropped by RFC 3986 resolution.
* **Every Federation, LocalAnubis and server-side Federation consumer reads `OnlineLibsSettingsComponent.EnvironmentSwitcherUrl`:**
  * `OnlinePlatformComponent.cs:73,119,144`
  * `LocalAnubisManager.cs:36,117`
  * `GameClientComponent.cs:3295`
  * `GameServerComponent.cs:922`
  * `UIComponent.cs:490` via `AssetsService`
  * `Connectivity.cs:19` (a plain GET on the base URL, so the stub must answer `GET /`)
* **Other service URLs come from the stub's config and go through `FormatUri`** (`Federation.cs:456-466`):
  * A value without a leading `http` becomes `https://…`.
  * When the port is 80, 443 or default **and** a `DefaultProtocol` is set, the scheme is rewritten. `DefaultProtocol` comes from `Args` or from the component template (`OnlinePlatformComponent.cs:82-94`; the template value is unknown because the data is LZ4/GCBF).
  * So the stub should return explicit `http://host:NONSTD_PORT` URLs, or args.txt can add `-DefaultProtocol HTTP`. The enum value is case-sensitive.
* **DLC path quirks** (`DownloadableContent.cs:401,416`):
  * It builds `"{url}/config/…"`, so with the trailing slash the request is `//config/...`. The stub must tolerate the double slash.
  * The asset base is forced to `"https://" + host`.
  * This is probably inactive, because `UseDlc` is forced from `res/raw` to false.
* **Cleartext:** `network_security_config.xml:3-7` allows cleartext only for `127.0.0.1` and `gameloft.com`, and the app targets SDK 29 (`AndroidManifest.xml:13,32`). However, Federation builds `new HttpClient(new FedRequestHandler(new HttpClientHandler(), this))`. The shipped `HttpClientHandler.CreateDefaultHandler()` returns `SocketsHttpHandler` unless `XA_HTTP_CLIENT_HANDLER_TYPE` is set (checked with `ilspycmd -t System.Net.Http.HttpClientHandler extracted/assemblies/System.Net.Http.dll`). That is a managed socket stack, which does not consult the Android network security config. So a LAN IP should also work for Federation. This is likely, not tested.

**Not redirected by args.txt** (so "all traffic" is an overstatement):

* Java installer and helpers: `XPlayer.java:160,195` (`https://eve.gameloft.com:443/...`), `GameInstaller.java:1522` (pandora `vgold-apa`), `d.cdn.php`, and Tracking.
* Hard-coded managed hosts: `ChatApi.cs:80` (`cmt.gameloft.com`), `BanToolApi.cs:12`, `Geolocalization.cs:93-95`, and `BrowserComponent.cs:78`.

---

## 3. Other zero-APK-change channels

| Channel | Carries a URL? | Reaches Federation? | Evidence |
|---|---|---|---|
| `<SDFolder>/qaTestingConfigs.txt` `DATA_LINK=` | Yes (the installer data link) | No, Java installer only | `GameInstaller.java:2148-2152`. Also `SKIP_VALIDATION`, `WIFI_MODE`, `OUTPUT_AF_REVISION`, `PRINT_QA_LOGS`, `LANGUAGE_WITH_VOLUME` (fixed `/sdcard/...` path in `MainActivity.java:277`) |
| DataSharing content providers (any installed app whose provider authority starts with `com.gameloft`) | Yes: `MCVS_SELECTED_ECOMM_API_ROOT`; NE also reads `_SELECTED_DC` | No, installer data root only | `DataSharing.java:10-27`, `GameInstaller.java:2156-2163`. targetSdk 29 means no package-visibility filter. Needs a helper app |
| `ServerInfos.json` (GlfStream under `Data/`) | No: only `SuperCredential` and `SuperPassword` | n/a | `Game/Game/GameAuthenticationComponent.cs:21-31`, `LocalAnubisManager.cs:86-96` |
| SharedPreferences (`pandoraURL`, `SDFolder`) | Yes | Installer only | `allowBackup="false"` (`AndroidManifest.xml:29`), so they cannot be edited without root |

**args.txt is the only zero-APK-change channel that sets the eve URL for the managed Federation stack.**

---

## 4. Corrected claim

`phantomArgs` is split on `:` (C#) and re-joined with spaces (native), so it cannot carry a URL. The native engine reads one file, `<GlfRootPath>/args.txt`. `GlfRootPath` is `AndroidUtils.RetrieveDataPath()`, which is the `SDFolder` pref, normally `/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files`, the same directory that holds `Data/`. That happens once at `NativeInit`, after the installer.

* The file is split on space, CR and LF only.
* It is merged with argv. The file is parsed first, and for an identical key the first value wins, so the file beats `phantomArgs`.
* The merged result is re-emitted as `\n`-separated `-Key`/value pairs and parsed into `GameApplication.Arguments` (case-insensitive field names, case-sensitive enum values).
* `res/raw/args.txt` is a separate Java reader that only overrides `UseDlc` and `LogCategoryFilter`.

A user file containing `-EnvironmentSwitcherUrl http://127.0.0.1:18080/` therefore sets the eve base URL verbatim. All Federation and LocalAnubis traffic is then discovered through it, provided that:

* the stub answers `GET /` and tolerates `//config`;
* the stub returns explicit `http://` service URLs, preferably on a non-default port;
* the file has no BOM and no tabs.

It does not redirect the Java installer or the hard-coded chat, ban-tool and geolocation hosts.

## 5. What only a device test can settle

* Whether `adb push` to `/sdcard/Android/data/<pkg>/files/` works on the target Android 11–15 builds, and whether the app can then read a file owned by shell. This is platform behaviour, not code. Expected: it works on Android 11–14. File managers are blocked from Android/data on Android 13+.
* Whether the NE VFS (`0x5747ec` / `0x577bf8`) opens the absolute path on the first try. Its fallback (`0x3c1718`, kind 1) uses a root from `0x580e54` that I did not resolve. It is likely to work, because the same VFS later opens absolute `RootPath/Data`.
* The `DefaultProtocol` value in the shipped `OnlinePlatformComponent` template.
* Whether `SocketsHttpHandler` on this Mono build really bypasses the Android network security config when given a LAN IP.
