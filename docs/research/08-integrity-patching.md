# 08 - Anti-tamper, TLS, and feasibility of modifying the app

Scope: everything that would block redirecting or modifying MCVS v1.17.32, and the feasibility/effort/risk of each
modification strategy. APK signature & native integrity checks, root/debug/emulator detection, TLS validation/pinning,
cleartext policy, request signing/secrets, save encryption, BanTool, analytics/crash SDKs, and the Xamarin/Mono
internals that decide whether IL patching, native `.so` injection, or Frida hooking are viable.

All paths relative to `/home/user/mcvs-work`. **[V]** = verified (I read the code / disassembly / parsed the binary).
**[L]** = likely (strong inference). **[S]** = speculative.

Tooling note: no Ghidra. Native analysis done with `readelf`, `llvm-objdump` (aarch64), `strings`, and a small
`pyelftools`+manual-ADRP xref script under the scratchpad. All constants independently recomputed.

---

## 0. TL;DR / bottom line

* **[V] There is exactly ONE hard integrity gate that matters: a native signing-certificate check inside
  `libnativeengine.so`.** It calls back into Java `AndroidUtils.retrieveBarrels()` → `SUtils.retrieveBarrels()`
  (the APK's `signatures[].hashCode()`), and compares entry `[0]` against the hard-coded constant **404250541**
  (`0x18185FAD`). I independently computed the real Gameloft cert's Java `Signature.hashCode()` = **404250541**:
  they match. Any re-signed APK produces a different hashCode and fails this check.
* **[V] If you do NOT modify the APK at all, nothing blocks you.** The stock signature passes, TLS validation is
  globally disabled in both the C# and Java stacks, cleartext is allowed to `gameloft.com` and `127.0.0.1`, and there
  is no request-level HMAC/secret. So the "redirect with `args.txt` + external/local server" route (report 01's
  recipe) is unobstructed by anything in my area.
* **[V] If you DO modify the APK (IL patch, add a `.so`, add an assembly, Frida-gadget), you must re-sign, and then you
  must also neutralise that one native hashCode comparison.** It is a single `cmp`/constant in one function; patching
  it (or the Java `retrieveBarrels` result, or the C# consumer) is trivial. There is **no** ptrace anti-debug, **no**
  Frida/Xposed/Magisk detection, **no** emulator detection, and **no** Play Licensing (LVL).
* **[V] IL patching is highly feasible.** Assemblies are stored **uncompressed, one `.dll` per file** under
  `assemblies/` in the APK (classic "legacy" Xamarin layout — no XALZ, no assembly-store blob, no `.config`). Mono AOT
  mode is **`normal`** (hybrid AOT **with JIT fallback**), and `libmonosgen-2.0.so` (the JIT) is present. So: edit a
  DLL with Mono.Cecil, delete its matching `libaot-<name>.dll.so`, and Mono JITs the patched assembly. The gameplay
  assemblies (`Game.dll`, `Gameloft.*.dll`) are **not** in `libxamarin-app.so`'s typemap, so modifying them does **not**
  require preserving their MVID. Only 6 Android-binding assemblies are MVID-locked.
* **[V] Re-signing breaks only online/cross-app features** (Google Play Games sign-in, Google OAuth, Facebook login,
  the `glshare` cross-Gameloft-app content provider). All of these are irrelevant to an offline build, **but** the
  re-signed manifest must not keep declaring the vendor-wide `glshare` permission (see §L below, correction).

---

## 1. APK signing and package identity

### 1.1 What's in the APK

**[V]** Package `com.gameloft.android.ANMP.GloftMVHM`, versionName `1.17.32`, versionCode `1173200`, minSdk 21,
targetSdk 29 (`decompiled-java/resources/AndroidManifest.xml:3-13`).

**[V]** Signed with **both** v1 (JAR) and v2/v3 (APK Signing Block):
* v1: `extracted/META-INF/GAMELOFT.RSA`, `GAMELOFT.SF`, `MANIFEST.MF`.
* v2/v3: the string `APK Sig Block 42` is present at file offset `60039152` in `apk/mcvs.apk`
  (`python3`-located). This means `zipalign`+`apksigner` must be used when repacking; a v1-only re-sign may be rejected
  on modern Android for a targetSdk-29 app if v2 was expected, but since minSdk is 21 a v1+v2 re-sign is the safe path.

**[V] The signing certificate** (`openssl pkcs7 -in GAMELOFT.RSA -print_certs`):
* Subject/Issuer: `C=US, ST=NewYork, L=New York, O=Gameloft` (self-signed).
* Validity: `notBefore=Jan 16 2009`, `notAfter=Apr 8 2043`.
* SHA-256: `21:73:A2:48:E2:B1:1F:97:60:B9:16:7F:39:8B:18:3C:92:81:6C:F8:FC:74:AF:D3:C6:4B:22:ED:DF:50:48:1C`
* SHA-1: `EB:B8:A6:58:8F:72:89:48:8F:8D:30:D1:1D:C0:20:BC:8D:4C:FB:0C`
* MD5: `9F:B7:62:C4:77:0E:EB:51:A8:C9:16:75:1A:6E:FB:AD`
* **Java `Signature.hashCode()` of the DER cert = `404250541` (`0x18185FAD`)** — computed with Java's
  `Arrays.hashCode(byte[])` algorithm (`h = 31*h + (signed)byte`, seed 1) over the 517-byte DER. **This is the value
  the native check expects (see §2).**

### 1.2 Java-side signature reads (informational, NOT gates)

**[V]** `com/gameloft/android/GLUtils/SUtils.java:1093-1109` — `retrieveBarrels()` reads
`getPackageInfo(pkg, GET_SIGNATURES).signatures`, takes up to 4 `.hashCode()` values into an `int[]`, and returns
them. Exposed to native via `AndroidUtils.retrieveBarrels()` (`PackageUtils/AndroidUtils.java:154-156`). This is the
data source for the native check; by itself it enforces nothing.

**[V]** `SUtils.getGLUID()` (`SUtils.java:1110-1127`) MD5s the package name — a device/user id, not a gate.

**[V]** `com/gameloft/GLSocialLib/GameAPI/GameHelperUtils.java:127-150` — `getSHA1CertFingerprint()` only **logs**
the SHA-1 for Google Play Games debugging (`Log.w("GameHelper", "**** Cert SHA1 fingerprint: ...")`). Not a gate.

---

## 2. The native signing-cert check in `libnativeengine.so` (the one real gate)

**[V]** `libnativeengine.so` is a 24 MB stripped aarch64 ELF (`.text` at vaddr `0x2ac4d8`). Build date Mar 17 2021.

I located the `retrieveBarrels` / `()[I` JNI-signature strings (`.rodata` `0x133e9e9` / `0x133e9f9`) and xref'd them:
the only reader is an internal function at **`0xed2f00`** (nearest exported symbol is far away —
`...JNIBridge_NotifyTrimMemory+0xac1c` — i.e. this is a static helper, not that export).

### 2.1 What the function does (disassembled `0xed2f00`–`0xed31c8`)

1. `AttachCurrentThread` via the cached `JavaVM` (global pointer at `0x181a288`).
2. `FindClass("com/gameloft/android/PackageUtils/AndroidUtils")` — the class string is assembled from a base plus the
   literal `/PackageUtils/AndroidUtils` at `.rodata` `0x133ce3c` **[V]**.
3. `GetStaticMethodID("retrieveBarrels","()[I")`, `CallStaticObjectMethod` → the `int[]`.
4. `GetArrayLength` + `GetIntArrayElements`, then loops over the array.

### 2.2 The comparison (the load-bearing constants)

The loop body (`0xed315c`–`0xed3190`) computes, per index `i`:

```
w11 = (i == 0) ? 2021<<1 : 2021        ; 0x7e5, doubled on the first element only
expected = w11*100000 + 50541          ; madd w15, w11, (0x186a0), (0xc56d)
if (arr[i] != expected) -> mark invalid
```

* `100000` = `0x186A0` (`movk w12,#0x1,lsl#16 | mov w12,#0x86a0`), `50541` = `0xC56D`, `2021` = `0x7E5`.
* Index 0 expected: `(2021*2)*100000 + 50541 = ` **`404250541`** — matches the real cert hashCode exactly.
* Index >0 expected: `2021*100000 + 50541 = 202150541`, and the surrounding logic effectively requires any extra
  signers to be absent (single-signer APK). **[V]** (constants recomputed in Python; they match the cert.)

### 2.3 How the result is used (NOT an immediate abort)

**[V]** The single caller is at `0x5e001c`; immediately after the `bl`, `and w22, w0, #1` keeps the boolean and
`stur w22, [x29,#-0xa8]` stores it into a stack struct that is handed to an engine registration/property call
(`0x5c1e20`). So the "signature valid" bit becomes a piece of engine state rather than a local `exit()`.

**[L]** Most plausibly this flag is folded into the online auth/anti-cheat/tracking payload (Gameloft's house pattern),
so a wrong value would be rejected **server-side**, not locally. For a fully offline build that never reaches a
Gameloft server, the flag likely has no local teeth — but because it is cheap to get wrong in some code path, the safe
move for any repack is to force it true.

### 2.4 How to neutralise it (for any repacked/re-signed APK)

Any of (easiest first):
* **Java:** patch `SUtils.retrieveBarrels()` (smali in `classes*.dex`) to return `{404250541,0,0,0}` regardless of the
  real signatures. Survives re-signing, no native edit. **[L] recommended.**
* **Native:** change the `0x7E5`/`0x186A0`/`0xC56D` constants or the final compare to force the valid branch; or NOP
  the `and w22,w0,#1` and `mov w22,#1`. Single function, one page.
* **C#:** if the consumer turns out to be managed, patch there.

---

## 3. Root / debugger / emulator detection

**[V]** A native function at `~0xed2e80`–`0xed2ef4` (adjacent to the sig check) does a classic **root check**:
`fopen`/`stat` on `/system/bin/su`, `/system/xbin/su`, `/system/app/Superuser.apk` (`.rodata` strings at
`0x133e9ca`, `0x133e9d9`, plus `/system/app/Superuser.apk`). Exported helpers exist: `IsDeviceRooted`
(`0x1300c67`) and `PlatformInfo_GetJailBroken` (an engine component binding).

* **[V]** The `IsDeviceRooted` string at `0x1300c67` is referenced from the **Vungle ad SDK** JNI
  (`...VungleAds_ReportInternalError`), i.e. ad targeting — not a game gate.
* **[L]** `PlatformInfo_GetJailBroken` feeds the engine's component/property system (and from there telemetry). No code
  path found that hard-blocks on it. For an offline build it is irrelevant; it is not a modification blocker.

**[V] Absent protections** (searched all game `.so`s and the DEX):
* **No `ptrace`** import anywhere (`readelf --dyn-syms` UND set = only `abort`, `exit`, `__cxa_atexit`,
  `pthread_exit`, `android_set_abort_message`). No self-debugger/anti-attach.
* **No Frida / Xposed / Magisk / `TracerPid` / `/proc/self/status` strings.**
* **No emulator/`goldfish`/`qemu`/`genymotion` detection.**
* **No `fork`/`system`/`popen`.**
* **No Play Licensing (LVL):** no `LicenseChecker`/`ILicensingService`/`com.android.vending.licensing` in Gameloft
  code (only unrelated GMS internals). `com.android.vending.BILLING` permission is for IAP, not licensing.

The large garbled blob in `.rodata` is **[L]** XOR/obfuscated string table (common Gameloft practice), but given the
absence of any anti-debug/anti-hook imports it is unlikely to be anti-tamper logic; more likely obfuscated asset/URL
strings.

---

## 4. TLS: validation is OFF end-to-end, no pinning

### 4.1 C# (the whole Federation / online stack)

**[V]** `Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/Federation.cs:103-107,129-132`:

```csharp
static Federation() {
    ...
    ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificatesPlease;
}
public static bool AcceptAllCertificatesPlease(object sender, X509Certificate cert, X509Chain chain,
                                               SslPolicyErrors e) => true;
```

This is **global** (`ServicePointManager` is process-wide) and set in a static constructor, so it is in force before any
request. **[V]** The fed HTTP client is `new HttpClient(new FedRequestHandler(new HttpClientHandler(), this))`
(`Federation.cs:468-472`); `FedRequestHandler.SendAsync` just forwards (`FedRequestHandler.cs:21-41`), no custom
validation, no pinning.

**[V]** TLS provider / handler are pinned by the Xamarin app env in `libxamarin-app.so` (`app_environment_variables`):
`XA_HTTP_CLIENT_HANDLER_TYPE = System.Net.Http.HttpClientHandler` and `XA_TLS_PROVIDER = btls` (Mono BTLS). The
managed `HttpClientHandler` on this config honours `ServicePointManager.ServerCertificateValidationCallback`, so the
accept-all callback covers every fed request.

### 4.2 Java (installer / CDN / billing helpers)

**[V]** `com/gameloft/android/GLUtils/g.java` is an `X509TrustManager` with **empty** `checkClientTrusted`/
`checkServerTrusted` and `getAcceptedIssuers()` returning `null` — a trust-all manager used by `GLUtils.HTTP`. Same
trust-all pattern in the IAP helpers `com/gameloft/android/iab/M.java` and `com/gameloft/android/iab/utils/b.java`.

### 4.3 Network security config + cleartext

**[V]** `res/xml/network_security_config.xml` (referenced from `AndroidManifest.xml:32`):

```xml
<network-security-config>
  <domain-config cleartextTrafficPermitted="true">
    <domain includeSubdomains="true">127.0.0.1</domain>
    <domain includeSubdomains="true">gameloft.com</domain>
  </domain-config>
</network-security-config>
```

So **plain `http://` is allowed** to `127.0.0.1` and anything under `gameloft.com`. `gameloft.org` and other hosts are
**not** cleartext-whitelisted (would need https, but any cert passes). No `<pin-set>` anywhere — **no pinning**.

**[V]** No host whitelist in managed code: `OnlineLibsSettingsComponent.EnvironmentSwitcherUrl` is taken verbatim from
`Args.EnvironmentSwitcherUrl ?? "https://eve.gameloft.com/"` (`OnlineLibsSettingsComponent.cs:68`) and used directly to
build request URLs (`DownloadableContent.cs:401`, `OnlinePlatformComponent.cs:73,119,144`). No validation that it
points at Gameloft.

**Consequence:** DNS/hosts redirection, a transparent proxy, or a local server on `127.0.0.1` all work with either a
self-signed cert (any host) or plain http (to a `*.gameloft.com` / `127.0.0.1` name). The cleanest no-APK-change route
is `args.txt -EnvironmentSwitcherUrl http://<something>.gameloft.com:port/` resolved via `/etc/hosts`-style redirect,
or just `http://127.0.0.1:port/`.

---

## 5. Request signing, secrets, save encryption, BanTool

* **[V] No request HMAC / signing secret.** The only per-request hash is `X-ETS-sha1` on the ETS tracking endpoint:
  `EtsRequestMessage.ComputeSHA1()` is a **keyless** `SHA1(params.ToString())`
  (`Gameloft.Engine/Gameloft.Engine/EtsRequestMessage.cs:110-117`) — an integrity/dedup checksum, not auth. Auth is the
  server-issued fed session token (`Session.cs`), not a client secret.
* **[V] Lobby password "encryption" is an identity stub.** `ClientSettings.EncryptPassword`/`DecryptPassword` return
  their input unchanged (`ClientSettings.cs:428-436`).
* **[V] BanTool is a server-admin API, not a client gate.** `BanToolApi` targets `https://bantoolv3.gameloft.org`
  (`BanToolApi.cs:12`) and every call requires a moderator `credential`+`password` (`BanToolApi.cs:21-75`). The client
  only exposes player reporting: `AntiCheatManagerComponent.ReportProfile` (max 4/game, `MaxReportsPerGame`,
  `AntiCheatManagerComponent.cs:28,57-90`) and it no-ops when offline (`!IsServer && !IsConnected`). `AntiCheatComponent`
  itself is an empty shell (only `IFastAccess` plumbing). No client-side cheat **detection** that could flag a mod.
* **[V] IAP receipt validation is native + server** ("`[Store Pop Transaction] Tampered data`",
  `PKCS7_signatureVerify` in BTLS). Purchase-side only; irrelevant to an offline progression build.
* **Save-file encryption:** out of my area (see report 03). Within integrity scope I found no AES/Rijndael/ProtectedData
  wrapping of the local profile; the password stub above is plaintext. The managed `Aes`/crypto references
  (`LotteryComponent`, `TrackingHitsComponent`, `Session`, `LobbyApi`) are request/transport helpers, not local save
  encryption. **[L]** Local save is not cryptographically locked against editing.

---

## 6. Analytics / crash SDKs and offline behaviour

**[V]** SDKs wired at startup: Crashlytics/Fabric (`CrashlyticsInitProvider`, `initOrder 100`, `io.fabric.ApiKey
54a9ba4848aa5c2df4ce12081da23e3581e5cc0e`, `AndroidManifest.xml`), Google Analytics (`UA-33292614-97`,
`res/xml/analytics.xml`), Adjust (`AdjustReferrerReceiver`), Facebook SDK (`FacebookInitProvider`), plus ad SDKs
(MoPub, Unity Ads, AudienceNetwork, AdColony, Vungle, IronSource).

**[L]** These are all network-tolerant/async by design (Fabric/Adjust/GA queue and retry); none found to synchronously
block on a network reply at boot. The boot-blocking step is the Java `GameInstaller` + the fed handshake (reports 01/02),
not these SDKs. **[S]** Still, for a clean offline build it is worth stubbing or short-circuiting Crashlytics/Adjust/GA
init to avoid wakelocks, retry loops, and log noise — easily done in smali or by removing the providers/receivers from
the manifest (which requires a re-sign anyway).

---

## 7. Xamarin / Mono internals (decides IL-patch / injection viability)

### 7.1 Versions

* **[V] Mono runtime: the `2020-02` release branch** — `libmonosgen-2.0.so` strings include
  `/Users/builder/jenkins/workspace/archive-mono/2020-02/android/release/...` and `2020-02/165f4b03417`.
* **[V] Xamarin.Android ~D16.6 (VS 2019 16.6, ~mid-2020):** `Mono.Android.dll` carries
  `10.3.1.5; git-rev-head:0689340; git-branch:fix/d16-6/debug-symbols`. `XAMARIN_BUILD_ID = bee7892d-ffcb-45da-9f1c-
  a7eb0e896c3a` (`libxamarin-app.so`). Managed assemblies target `MonoAndroid,Version=v10.0` (`XaLaunchANMP.dll`).
* **[V]** This is **pre-.NET-6 "classic" Xamarin.Android** — important: it uses the per-file assembly layout and the
  `libxamarin-app.so` typemap/env model, not the newer assembly-store/`rc.bin` model.

### 7.2 Assembly layout in the APK

* **[V]** `assemblies/` holds **59 `.dll` files, stored uncompressed** (zip `compress_type == 0`), each a plain PE
  (`MZ` header — **not** `XALZ`-compressed, confirmed on `Game.dll`, `mscorlib.dll`, `Mono.Android.dll`).
* **[V]** **No assembly-store blob, no `.manifest`, no per-assembly `.config`** under `assemblies/`.
* **[V]** Mono loads them straight out of the APK: `libmonodroid.so` strings — `assemblies/`, `open_from_bundles:
  loaded assembly: %p`, `Setting up for DSO lookup directly in the APK`, `Package '%s' contains %i assemblies`, mmap
  from the zip entry. Because the entries are stored+aligned, they are mmap'd in place.

**Consequence:** a Cecil-patched DLL is dropped back into `assemblies/<name>.dll` as a **stored, 4-byte-aligned** zip
entry. No compression, no blob surgery.

### 7.3 AOT mode = `normal` ⇒ JIT fallback works

* **[V]** `libxamarin-app.so` `mono_aot_mode_name = "normal"`. This is **hybrid AOT**, not `full`/`aot-only`/
  `interp-only`. `libmonosgen-2.0.so` (the full JIT, 3.5 MB) is present; its strings include the `--full-aot`,
  `aot-only`, and interpreter options but the active mode is `normal`.
* **[V]** Each `libaot-<Asm>.dll.so` is a **supplemental** precompiled image registered via
  `mono_aot_register_module`. Mono validates it against the loaded assembly by MVID/GUID; the runtime strings prove the
  fallback: `AOT: module %s is unusable (GUID of dependent assembly %s doesn't match (expected '%s', got '%s'))`,
  `Image out of date`, and it then JITs.

**Consequence (the key IL-patch enabler):** patch `Game.dll` (or any `Gameloft.*.dll`), **delete the matching
`lib/arm64-v8a/libaot-Game.dll.so` and `lib/armeabi-v7a/libaot-Game.dll.so`**, and Mono will JIT the modified assembly.
You may also delete the AOT images of assemblies that *depend* on it (they'll be flagged out-of-date and JIT too) — or
just delete all `libaot-*.dll.so` to force pure JIT (slower cold start, but simplest and guaranteed correct). JIT is
available because mode is `normal` and the SGen JIT lib ships.

### 7.4 What `libxamarin-app.so` pins (MVID / typemap)

I parsed `libxamarin-app.so` (`pyelftools`). It is tiny (36 KB) and contains:
* `application_config` → package `com.gameloft.android.ANMP.GloftMVHM`, `environment_variable_count=3`,
  `system_property_count=0`, plus the env block (§4.1) and `__XA_PACKAGE_NAMING_POLICY__ = LowercaseCrc64`. **[V]**
* `map_module_count = 6`, `java_type_count = 278`. **[V]**
* **The 6 typemap modules, each with its MVID (`map_modules`, 72-byte entries):** **[V]**

| idx | assembly (module name)       | MVID                                   | managed→java entries |
|-----|------------------------------|----------------------------------------|----------------------|
| 0   | `BindGPSrv-auth-base`        | `1eb2fe08-c549-4265-9ab1-afff71961bcd` | 1 (+2 dup)           |
| 1   | `BindAF`                     | `1b415f4d-ecf1-453b-abd2-19eb25e95efa` | 2                    |
| 2   | `XaLaunchANMP`               | `9d564c5d-ae17-44f3-99bd-a4850ae57b9a` | 2                    |
| 3   | `AdjustSdk.Xamarin.Android`  | `66c8e283-02ac-4962-a247-c3b030bf47c6` | 62 (+1 dup)          |
| 4   | `BindUnityAds`               | `6822908b-62ef-43f3-b702-e2c9d0fc43ce` | 22                   |
| 5   | `Mono.Android`               | `237d2ef0-9808-44e7-8ccc-4467e350e1fe` | 189 (+39 dup)        |

**Consequence — this is the single most important IL-patch fact:** only these 6 assemblies have Java-type maps and
MVIDs baked into `libxamarin-app.so`. **`Game.dll`, `Gameloft.Engine.dll`, `Gameloft.OnlinePlatform.dll`,
`Gameloft.*`, `mscorlib`, `System.*`, `Newtonsoft.Json`, `NicePlaySDK` are NOT typemap modules.** They define no
Java-callable types (the game's one `Activity`, `XamarinMainActivity`, lives in `XaLaunchANMP`). Therefore:

* **Patching the gameplay/engine assemblies does NOT require preserving their MVID** and does not touch
  `libxamarin-app.so` at all. (Default Cecil behaviour — regenerating the MVID — is fine for these.)
* **If you must patch one of the 6** (e.g. `XaLaunchANMP` to change the boot/args handling, or `Mono.Android`), you
  **must keep its MVID**, or the managed↔Java typemap lookups fail at runtime
  (`typemap: module matching MVID [%s] not found`). **[V] Mono.Cecil can preserve the MVID**: set
  `module.Mvid = <original Guid>` before `Write(...)` (and keep `Name`/`Mvid` stable). The table above gives the exact
  GUIDs to restore. You do **not** need to touch `libxamarin-app.so` as long as the MVID is preserved.

### 7.5 Adding a brand-new assembly

**[L]** Feasible but with constraints: (a) add `assemblies/New.dll` as a stored+aligned entry; (b) it is only loaded if
something already loaded references it — so add a reference from a patched existing assembly (e.g. `Game.dll`);
(c) it needs **no** AOT image (JIT handles it) and **no** `libxamarin-app.so` entry **unless** it defines Java-derived
types (it shouldn't — keep it pure managed). This is the clean way to drop in a self-contained offline/bot/server shim
in C# rather than native code.

---

## 8. What re-signing breaks (and what it doesn't)

Re-signing with your own key is mandatory for any APK modification. It breaks **only online/cross-app** features:

* **[V] The native hashCode gate (§2)** — must be neutralised. (This is in-APK, not server; see §2.4.)
* **[L] Google Play Games sign-in & Google OAuth** — `game_api_app_id` builds a `...apps.googleusercontent.com` client
  id (`GLSocialLib/GameAPI/RestManager.java:19`). Google binds Android OAuth clients to package + signing-cert SHA-1;
  a new cert ⇒ Google rejects sign-in. Online-only; dead already with servers down.
* **[L] Facebook login** — FB app (`facebook_app_id`) validates the signing **key hash**; new cert ⇒ FB login fails.
  Online-only.
* **[L] `glshare` cross-app data sharing** — the `KeyProvider` content provider and `glshare.permission.
  ACCESS_SHARED_DATA` are `protectionLevel="signature"` (`AndroidManifest.xml:19-22`, `249+30`). After re-signing, this
  app can no longer share the `gameloft_sharing` SQLite db with **other** Gameloft apps signed by the original key.
  Irrelevant for standalone MCVS.
  **Correction (review, October 2026): the sharing is irrelevant, the declaration is not.** The permission name is not
  package-scoped and every Gameloft game declares it (Asphalt 8/9, MC5, N.O.V.A. 3, Six-Guns, Gameloft Pad...). Since
  Android 5.0 PackageManager refuses a package that redeclares a permission owned by an installed package with a
  different certificate (`INSTALL_FAILED_DUPLICATE_PERMISSION: ... glshare.permission.ACCESS_SHARED_DATA already owned
  by com.gameloft.android.ANMP.GloftA9HM`, field report on 4PDA), so a re-signed APK that keeps the declaration cannot
  be installed next to any official Gameloft game, and blocks their installation afterwards. The patcher therefore
  renames the pool string (shared by `<permission>`, `<uses-permission>` and the provider's `android:permission`) to
  `<package>.mcvs.glshare.permission.ACCESS_SHARED_DATA` (`patcher/Manifest/ManifestPatch.cs`). The only other custom
  permission, `C2D_MESSAGE`, and every provider authority are already package-scoped. Nothing is lost: the game reads
  its own provider without a permission check (same UID), and `DataSharing.fillDBArray` swallows the
  `SecurityException` from other apps' providers.
* **No Play Licensing to worry about** (§3).

Net: for a fully offline target, re-signing costs you nothing you still want.

---

## 9. Modification-strategy matrix

Effort/Risk are relative, for the preservation goal (offline play, progression, items, bots, P2P).

| # | Strategy | Re-sign? | Blockers (from my area) | Effort | Risk | Verdict |
|---|----------|----------|--------------------------|--------|------|---------|
| 1 | **phantomArgs only** (Intent extra, launcher shim) | **No** | Sig check passes (no APK change). But `phantomArgs` splits on `:` so it **cannot carry a URL** (report 01 [V]); limited to flag-style args; needs a launcher Activity to set the extra. | Low | Low | **Insufficient alone.** Use for toggles, not redirection. |
| 2 | **Redirect via `args.txt` + external/local server** (no APK change) | **No** | **None in my area.** TLS accept-all (C#+Java), cleartext to `gameloft.com`/`127.0.0.1`, no pinning, no host whitelist, no request secret, sig check intact. The work is building the server (reports 01/02/06/10). | Low (client) / High (server) | Low | **Recommended as the clean-signature path** if you keep an external/local server. Preserves GPG/FB in theory. |
| 3 | **IL patch with Mono.Cecil** (force offline/bots/unlock-all in `Game.dll`/`Gameloft.*`) | **Yes** | Must neutralise native sig check (§2.4, trivial). Gameplay DLLs are **not** MVID/typemap-locked (§7.4). Delete matching `libaot-*.dll.so` → JIT (§7.3). Lose GPG/FB (online-only). | Medium | Medium | **Recommended as the core technique.** Most direct control; low Xamarin friction. |
| 4 | **Native `.so` injection (Dead-Trigger-2 style embedded server)** | **Yes** | Re-sign ⇒ sig check (§2.4). Add a `lib/<abi>/lib*.so` loaded at boot (or hook an existing one). **But note:** MCVS already ships an in-process **LocalAnubis** lobby/controller/finder (report 06), so a native server may be unnecessary — the managed route (3) can just force those paths. | Med-High | Medium | **Only if** you specifically want a native P2P/relay layer; otherwise (3) is less work. |
| 5 | **Frida-gadget hooking** | Yes (or root, no re-sign) | Re-sign ⇒ sig check (§2.4); or load gadget via root/zygisk with no re-sign. No Frida/ptrace detection to defeat (§3). | Low | Medium | **Great for R&D/prototyping** (discover runtime values, test offline toggles live). Not for distribution. |

### Recommendation

**Primary: Strategy 3 (Cecil IL patching) as the engine, optionally backed by Strategy 2 (local stub server) for any
residual online call you choose not to patch out.**

Concretely, for a repacked offline APK:
1. Patch the gameplay/engine assemblies (`Game.dll`, `Gameloft.Engine.dll`, `Gameloft.OnlinePlatform.dll`) with Cecil to
   force the offline/bot/unlock flows (details are reports 01/04/05/06's domain). **No MVID preservation needed** for
   these (§7.4).
2. If you touch `XaLaunchANMP` or `Mono.Android`, **preserve the MVID** from the table in §7.4.
3. **Delete every `lib/arm64-v8a/libaot-*.dll.so` and `lib/armeabi-v7a/libaot-*.dll.so`** (or at minimum the ones for
   assemblies you patched plus their dependents) so Mono JITs; mode is `normal`, JIT ships (§7.3).
4. Put the patched DLLs back as **stored, aligned** `assemblies/*.dll` entries (§7.2).
5. **Neutralise the native signature gate** — simplest is to patch Java `SUtils.retrieveBarrels()` in smali to return
   `{404250541,0,0,0}` (§2.4); survives re-signing, no native edit.
6. `zipalign` + `apksigner` (v1+v2) with your own key. Accept the loss of Google/Facebook online sign-in (dead anyway).
7. **[S]** Optionally strip/stub Crashlytics/Adjust/GA/ad providers from the manifest to keep the offline process quiet.

Because the assemblies are uncompressed per-file, the gameplay DLLs carry no MVID/typemap lock, and AOT is `normal`
(JIT fallback), the Xamarin/Mono side imposes **no real obstacle** to IL patching. The only genuine anti-tamper in the
whole app is the one native cert-hashCode comparison, and it is a one-line patch.

---

## 10. Open questions (outside my area)

* **[L]** Exactly where the native "signature valid" flag (§2.3) is consumed — is it ever checked locally, or only
  bundled into an online auth/tracking payload? If purely online, a fully offline build may not even need the §2.4
  patch. (Native call-graph dig; or test empirically with Frida.) Flagging for whoever owns the auth/tracking payload
  (reports 02/boot).
* Local save encryption/signing specifics are in report 03's scope; within integrity scope nothing locks the save.
* Whether `GameInstaller` performs its own data-pack signature/checksum verification that would reject a modified data
  pack — "`bad checksum`"/"`checksumCache.txt`" strings exist in native; the CDN/data-pack integrity path is report 10's
  scope.
* The obfuscated `.rodata` string blob — decoding it (to confirm it holds only asset/URL strings and no hidden gate) is
  a deeper native task; low priority given no anti-debug/anti-hook imports exist.
