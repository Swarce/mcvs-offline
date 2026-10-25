# Verify C7 — integrity / TLS / IL-patch feasibility (adversarial)

**Claim C7-integrity-patching** (from report 08 + 09): the only integrity gate is a native cert-hashCode check in
`libnativeengine.so` comparing Java `Signature.hashCode()` to `404250541`, stored as an engine flag (not an exit);
TLS validation is globally off and cleartext is allowed for `127.0.0.1`; IL patching is feasible because Mono runs
"hybrid AOT with JIT fallback" (delete `libaot-X.dll.so` → Mono JITs patched `X.dll`); only 6 Android-binding
assemblies are MVID-pinned in `libxamarin-app.so` typemaps so `Game.dll`/`Gameloft.*.dll` are freely rewritable;
and adding a new `MCVS.Server.dll` called from a patched `NicePlaySDKDLL.OnInitialize` works.

**Verdict: CONFIRMED** on every load-bearing point — a re-signed, IL-patched APK is expected to run. I independently
re-derived the native constant, the typemap MVIDs, the AOT mode, and the assembly-loading behaviour from the binaries
and source. Two refinements to report 08's *internal details* (not to C7's conclusion), and one terminology nit, below.
All paths relative to `/home/user/mcvs-work`. Method notes: capstone/pyelftools from `tools/venv`; native `.text` is
file-offset == vaddr (LOAD0 `R E`, off 0, vaddr 0, size `0x164ef54`).

---

## 1. The native gate (`libnativeengine.so`) — CONFIRMED, and weaker than claimed

Disassembled the function at **`0xed2f00`–`0xed31c8`** (capstone). It:

1. `GetEnv`/`AttachCurrentThread` via the cached `JavaVM*` at `0x181a288` (`0xed2f1c-0xed2f64`).
2. resolves `com/gameloft/android/PackageUtils/AndroidUtils` (string `0x133ce3c`) and
   `GetStaticMethodID("retrieveBarrels","()[I")` — strings `0x133e9e9`/`0x133e9f9`, loaded at `0xed3010-0xed3020`,
   vtable off `0x388` = `GetStaticMethodID` (`0xed3028`).
3. `GetArrayLength` (vtable `0x558`, `0xed3104`) + `GetIntArrayElements` (vtable `0x5d8`, `0xed3128`).
4. loops (`0xed315c-0xed3190`) with these recomputed constants:
   `0x186a0`=100000, `0x7e5`=2021, `0xc56d`=50541. Per element `i`: `w15=(i==0)`; `lsl w11,w11,w15`;
   `expected = w11*100000 + 50541`. For `i==0`: `w11 = 2021<<1 = 4042` → **`expected = 4042*100000+50541 = 404250541`**.
5. returns `0`/`1` in `w0`. **No `abort`/`exit`/branch-to-exit anywhere in the function** — its only `bl` targets are
   string/alloc helpers (`0x2aa518`,`0x2aa4c8`,`0xa05360`,`0xa03a30`) and JNI (`0xed5e78`,`0x409400`). On match it jumps
   to `0xed3208` (ReleaseIntArrayElements vtable `0x618`, DeleteLocalRef vtable `0xb8`, `w20=1`).

**Caller `0x5e001c`:** `bl 0xed2f00; and w22, w0, #1; stur w22, [x29,#-0xa8]` then `bl 0x5c1e20` (a property/registration
call) with `x2 = &struct`. Immediately after, `bl 0xed2e68` (the adjacent root check) → `and w24, w0, #1` registered the
same way. So the signature bit (and the root bit) become **entries in an engine property table** — **confirmed "stored
as a flag, not an exit."**

**Independent constant check (closed the loop):** extracted the signing cert DER (517 bytes) from
`extracted/META-INF/GAMELOFT.RSA` and computed Java `Signature.hashCode()` = `Arrays.hashCode(DER)` (`h=31h+signedByte`,
seed 1) = **`404250541`** — exactly the native expected value. So the **stock single-signer APK passes at `i=0` and
exits early**; a re-signed cert yields a different `hashCode`.

**Java source** (`decompiled-java/sources/com/gameloft/android/GLUtils/SUtils.java:1091`): `retrieveBarrels()` returns a
4-int array zero-filled then set to up to 4 `signatures[i].hashCode()` — so stock = `{404250541,0,0,0}`. Exposed via
`.../PackageUtils/AndroidUtils.java:154`.

**No other gate.** `readelf --dyn-syms` UND set has **no `ptrace`/`fork`/`system`/`popen`/`kill`** (only `abort`/`exit`/
`__cxa_atexit`/`pthread_exit`/`android_set_abort_message`, none called from the check). No `frida`/`xposed`/`magisk`/
`TracerPid`/`qemu`/`goldfish` strings; no Play-Licensing. Confirms "the only integrity gate."

### Consumption of the flag — what C7 asked me to pin down
No C# P/Invoke reads it back (`grep` for `Barrels`/signature-valid in `decompiled/` = 0 hits) and **no NE export is a
getter** for it (only `PlatformInfo_GetJailBroken` for root). The key (`0x17a72b8`) is a `.bss` `std::string` built by a
static initializer, so it is an internal engine property. **Therefore a mismatch cannot locally refuse/corrupt saves,
change gameplay, hit BanTool, or crash** — the check has no local teeth. Its only plausible use is inside an online
auth/telemetry payload (report 08 §2.3 [L]), which is moot offline. I could not statically name the property (the
whole-`.text` linear xref desyncs on this 24 MB stripped image — a tool limitation, noted); so "sent to server" stays
**[L]**, but it does not affect feasibility either way.

### Refinements / corrections (do not change C7's conclusion)
- **Report 08 §2.2 is wrong for index>0.** It states index>0 expected = `2021*100000+50541 = 202150541`. The code does
  `lsl w11,w11,w15` **in place with no reset**, and `w15=0` for `i>0`, so `w11` stays `4042` → expected stays
  `404250541` for every index. Moot in practice (stock matches at `i=0`).
- **"Any re-signed APK fails this check" is too strong.** The fallback (`0xed3178-0xed3188`:
  `w20 &= (arr[i] < 1)`) returns **valid when all non-matching entries are `<= 0`**. A single-signer re-sign therefore
  **passes the native check if the new cert's Java hashCode is negative** (~50% of keys). This makes patching *easier*,
  not harder — and is irrelevant anyway given the flag has no local teeth.

---

## 2. TLS off + cleartext — CONFIRMED

`Gameloft.OnlinePlatform/.../Federation.cs:104-107` static ctor sets
`ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificatesPlease`; `:129` returns `true`
unconditionally. `ServicePointManager` is process-wide and set in a static ctor → in force before any request.
`FedRequestHandler.cs` is a pass-through `DelegatingHandler` (adds timeout/logging only — no validation, no pinning).
`decompiled-java/resources/res/xml/network_security_config.xml` permits cleartext for `127.0.0.1` and `gameloft.com`
(`includeSubdomains`), **no `<pin-set>`** (ref'd from `AndroidManifest.xml:32`). The Option-B in-process handler and
Option-A localhost HTTP both avoid TLS entirely.

---

## 3. AOT mode = JIT fallback — CONFIRMED (terminology: "normal", not "hybrid")

Read the `mono_aot_mode_name` symbol in `libxamarin-app.so` (not stripped): it points to the string **`"normal"`**.
Mono AOT mode `NORMAL` loads AOT images when valid and **JITs otherwise** — it is *not* `aot-only`. The abort paths in
`libmonosgen-2.0.so` ("Attempting to JIT ... while running in aot-only mode", "Failed to load AOT module '%s' in
aot-only mode") are **gated on aot-only, which is inactive**. On a patched DLL, the matching `libaot-X.dll.so` is either
deleted (not registered → JIT) or MVID-mismatched → monosgen logs
`AOT: module %s is unusable (GUID of dependent assembly %s doesn't match ...)` / `... because dependency %s is not
found.` and JITs. `libmonosgen-2.0.so` (the SGen JIT) ships. So the mechanism holds.
**Nit:** C7 says "hybrid AOT"; the actual mode is Mono `NORMAL` (distinct from `MONO_AOT_MODE_HYBRID`). Functionally
identical for the claim (JIT fallback works).

Caveat for the recipe: `XaLaunchANMP` references `NicePlaySDKDLL` by `typeof` (see §5), so patching NicePlaySDK
invalidates XaLaunchANMP's **AOT image** (dependency out-of-date) → XaLaunchANMP JITs. That is harmless (its **MVID** is
untouched, so its typemap entry still matches). The conservative "delete **all** `libaot-*.dll.so`" sidesteps every AOT
dependency subtlety.

---

## 4. Typemap / MVID pinning — CONFIRMED exactly

Parsed `libxamarin-app.so` (pyelftools): `map_module_count = 6`, `java_type_count = 278`, `map_modules` = 432 bytes =
6 × 72. Extracted the 6 MVIDs and the 6 managed-type-counts, and independently read the `#GUID`-heap MVID of each shipped
DLL (`extracted/assemblies/*.dll`). They line up precisely:

| typemap MVID | entry/dup | assembly (matched by MVID) |
|---|---|---|
| `1eb2fe08-c549-4265-9ab1-afff71961bcd` | 1/2 | BindGPSrv-auth-base.dll |
| `1b415f4d-ecf1-453b-abd2-19eb25e95efa` | 2/0 | BindAF.dll |
| `9d564c5d-ae17-44f3-99bd-a4850ae57b9a` | 2/0 | **XaLaunchANMP.dll** |
| `66c8e283-02ac-4962-a247-c3b030bf47c6` | 62/1 | AdjustSdk.Xamarin.Android.dll |
| `6822908b-62ef-43f3-b702-e2c9d0fc43ce` | 22/0 | BindUnityAds.dll |
| `237d2ef0-9808-44e7-8ccc-4467e350e1fe` | 189/39 | **Mono.Android.dll** |

**NOT pinned** (independently extracted MVIDs, absent from the table): `Game.dll` `d9067738…`,
`Gameloft.Engine.dll` `e4f25834…`, `Gameloft.OnlinePlatform.dll` `6ea070f4…`, `Gameloft.Templates.dll` `ca358d5d…`,
`Gameloft.Util.dll` `fa21a3ea…`, `NicePlaySDK.dll` `fcb3acb6…`, `Newtonsoft.Json.dll` `0d551458…`,
`System.dll` `d6aaea76…`, `mscorlib.dll` `07007a8c…`. So Cecil's default MVID regeneration on those is safe; only
`XaLaunchANMP`/`Mono.Android` (and the 4 Bind* ) need MVID preservation if patched. Confirmed.

---

## 5. Assembly layout + adding a new assembly — CONFIRMED (mechanism), residual device-test risk

- **Layout:** `apk/mcvs.apk` `assemblies/` holds **59 `.dll`, all `compress_type=0` (STORED)**, plain `MZ` (not XALZ),
  **no store blob, no `.config`/`.manifest`**. A Cecil-patched DLL goes back as a stored, 4-byte-aligned entry. Confirmed.
- **On-demand loading (the crux of "add a new assembly"):** the preload list
  `decompiled-java/sources/mono/MonoPackageManager_Resources.java:5` has **47** entries, passed to `Runtime.initInternal`
  (`MonoPackageManager.java:55`). But **12 assemblies ship in the APK yet are NOT in that list** — `mscorlib.dll`,
  `System.dll`, `System.Core.dll`, `System.Net.Http.dll`, `Mono.Android.dll`, `Java.Interop.dll`, `Mono.Security.dll`,
  `System.Xml*.dll`, etc. — and the game plainly uses them. That **proves** monodroid resolves assemblies by filename
  from the APK on demand (`mono_install_assembly_preload_hook`, "Setting up for DSO lookup directly in the APK"). So a
  new `assemblies/MCVS.Server.dll` referenced from a patched assembly loads the same way — **no need to edit the Java
  list** (adding it wouldn't hurt). Needs no AOT image (JIT). Confirmed as mechanism; only a device run can prove it
  end-to-end (residual).
- **Boot hook:** `XaLaunchANMP/.../XamarinMainActivity.cs:63-107` reflectively calls `GameDll.OnInitialize`,
  `AdjustSdkDll…`, `AdjustReferrerReceiver…`, and `NicePlaySDKDLL.OnInitialize` (`:96-101`) **before** `MainLoop.Start`
  (`:107`). **`GameDll` has no `OnInitialize`** (empty class) so its reflective probe is a no-op — the PoC correctly
  targets `NicePlaySDKDLL.OnInitialize`, which **does** exist and **is** invoked
  (`NicePlaySDK/.../NicePlaySDKDLL.cs:8`; body is inert because `Enabled => false` at `:6`). Confirmed.

---

## 6. PoC review (`scratchpad/patcher`, `scratchpad/poc`)

Decompiled the PoC's **output** DLLs to verify round-trip correctness:
- `poc/out/NicePlaySDK.dll` → `NicePlaySDKDLL.OnInitialize()` body is now `Bootstrap.Init();` ✔ (valid IL, decompiles clean)
- `poc/out/Gameloft.OnlinePlatform.dll` → `CreateRequestHandler` is now
  `new HttpClient(new FedRequestHandler(Bootstrap.CreateFedInnerHandler(), this))` ✔ — the patcher's assertion that
  `IL_0000` is `newobj HttpClientHandler::.ctor` held (else it throws), consistent with the source at
  `Federation.cs:470`.
- `poc/MCVS.Server.dll` AssemblyRef table names only `mscorlib`/`System`/`System.Net.Http`/`Newtonsoft.Json`/
  `Gameloft.Engine` — **no `netstandard`, no `System.Private.CoreLib`, no `System.Runtime`** facade. Drops into
  `assemblies/` as a classic Xamarin assembly. (The compiled artifact is the non-`await` `Bootstrap2.cs` variant; the
  trimmed `mscorlib`'s `TaskAwaiter` lacks `INotifyCompletion`, so `await` won't compile — report 09 noted this.)
- API usage is real: `new HTTPServer(handler, 18080, "localhost")` matches
  `Gameloft.Engine/HTTPServer.cs:23`; `FedHttpFrontEnd : HTTPServerMessageHandler` overrides the real abstract
  `HandleMessage(HttpListenerContext, CancellationToken)`.

**Correctness risks to flag (none fatal):**
1. **Patcher does not emit `MCVS.Server.dll`** — it only rewrites the two existing DLLs; packaging must separately drop
   `MCVS.Server.dll` into `assemblies/` (stored+aligned). (It does `ImportReference` the new methods, so the AssemblyRef
   is added correctly.)
2. **Delete the stale AOT images** for the two patched assemblies (`libaot-NicePlaySDK.dll.so`,
   `libaot-Gameloft.OnlinePlatform.dll.so`, both ABIs) or rely on MVID-mismatch auto-JIT; safest is delete **all**
   `libaot-*.dll.so`.
3. **`HttpListener` on Android/Mono** (Option A localhost) is the fragile part (report 06's `localhost` Host-header
   quirk). Option B (in-process `HttpMessageHandler`, no socket) is more robust; the `Router` is a stub by design
   (payloads are reports 02/03/04/06).
4. Do **not** patch `XaLaunchANMP`/`Mono.Android`/the 4 `Bind*` without preserving MVID (Cecil `module.Mvid =`).

---

## 7. Minimal change set for a runnable re-signed, IL-patched offline APK

1. **IL (Cecil):** `NicePlaySDK.dll::NicePlaySDKDLL.OnInitialize` → `call MCVS.Server.Bootstrap.Init; ret`;
   `Gameloft.OnlinePlatform.dll::Federation.CreateRequestHandler` `newobj HttpClientHandler::.ctor` →
   `call Bootstrap.CreateFedInnerHandler`. (Both non-pinned → MVID regen OK.)
2. **New assembly:** add `assemblies/MCVS.Server.dll` (stored, 4-byte-aligned). No typemap/AOT/preload-list edit needed.
3. **Native/AOT:** delete `lib/{arm64-v8a,armeabi-v7a}/libaot-*.dll.so` (at least for the two patched assemblies +
   dependents; simplest = all) so Mono JITs.
4. **Integrity gate (OPTIONAL for offline):** the native flag has no local teeth, so an un-neutralised gate still boots
   locally. If you want it forced-valid anyway, the cheapest survivable edit is smali:
   `SUtils.retrieveBarrels()` → return `{404250541,0,0,0}` (survives re-sign, no native edit). The native edit
   (constants at `0xed3140-0xed3158`, or force `w20=1`) is the alternative.
5. **Re-sign:** `zipalign` + `apksigner` (v1+v2/v3) with your own key. Loses Google/Facebook/`glshare` online sign-in
   (dead with servers down). No LVL to defeat. *Added after review:* first rename the manifest's vendor-wide signature
   permission `glshare.permission.ACCESS_SHARED_DATA` into the package namespace, or the re-signed APK fails with
   `INSTALL_FAILED_DUPLICATE_PERMISSION` next to any official Gameloft game (report 08 §L correction).

---

## 8. Remaining risks (device-only)
- End-to-end on-demand load of a *brand-new* assembly name (not just BCL) on this exact monodroid — mechanism proven,
  but unrun. (`__XA_PACKAGE_NAMING_POLICY__=LowercaseCrc64` governs the type map, not assembly-file lookup, so it should
  not interfere — unverified on device.)
- Whether the registered signature/root property is read by any **distant** native consumer that gates a feature
  (static xref desynced on the 24 MB image; no local branch found at the call site).
- `HttpListener`/loopback behaviour under Mono on Android (Option A); Option B avoids it.
- Full-JIT cold-start cost if all `libaot-*` are deleted (performance, not correctness).
