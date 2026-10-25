# 01 - Boot flow, argument injection, service discovery and redirect

Scope: what runs from the Android launcher icon to the main menu, how start-up arguments get in, how the client finds
Gameloft services (eve, then pandora), what each boot step needs from the network, and how to point the game at a
custom server.

All paths are relative to `/home/user/mcvs-work`. **[V]** means verified (I read the code or disassembly).
**[L]** means likely: a strong inference from the code, but not every link in the chain was read. **[S]** means speculative.

---

## 0. TL;DR

* **[V] There is no built-in offline boot.** The main menu level is not loaded by the client. The Transaction
  Server (TS) pushes it as a synchronized `LevelPath` property after the client connects. Before that the client must:
  read the eve config, open a fed anonymous session (auth), run matchmaker QuickLaunch, log in to the Anubis
  controller, and complete the game handshake. With no network, or with today's servers, the client loops on the
  "Connection" level with an error popup and reconnect attempts. Bot matches, the practice range and the tutorial
  are "offline links" that run client-side, but they can only be started once the client is already connected or in
  the menu.
* **[V] Today, eve is alive but MCVS is de-provisioned.** `GET https://eve.gameloft.com/` returns
  `{"eve": "on", "version": "3.9.79"}`. `.../config/2620:66102:1.17.32:android:googleplay/datacenters` returns
  404 "no configs found". `.../datacenters/mdc/urls` returns 404 "client_id ... not configured". So the stock client
  cannot even get a service list. See `captures/eve-*.http` and `captures/eve-requests.txt`.
* **[V] There are two ways to inject arguments:**
  1. **`phantomArgs`**, an Intent string extra, split on `:`.
  2. A plain-text **`<DataPath>/args.txt`**, split on whitespace (space, CR, LF), which the native engine reads at
     start-up. `<DataPath>` is normally `/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files`.

  Both end up as one `\n`-joined string, which C# parses with `ApplicationArguments.Parse`: `-Name value` tokens,
  field names case-insensitive.
* **[V] A URL cannot be passed through phantomArgs.** Splitting on `:` breaks `http://host:port/`, and the native layer
  re-joins the pieces with spaces, giving `http //host port/`. **Use `args.txt` for `-EnvironmentSwitcherUrl`.**
* **[V] TLS validation is off in the C# online stack.** `ServicePointManager.ServerCertificateValidationCallback = AcceptAllCertificatesPlease`.
  The Java helper HTTP uses a trust-all `X509TrustManager`. So DNS/hosts redirection with a self-signed certificate
  also works.
* **[V] The Java `GameInstaller` runs on every launch, before any C#/native game code starts.** On first run, or
  whenever its file list is missing or invalid, it needs the Gameloft data link `d.cdn.php`, otherwise it blocks on an
  error screen. `qaTestingConfigs.txt` with `SKIP_VALIDATION=1` (and `DATA_LINK=...` to avoid the eve lookups)
  removes every installer network access.
* **[L] Correction to the OBB lead:** this build appears **not** to use a Play OBB. `AndroidUtils.RetrieveObbPath()`
  returns `"NA"`, and `AndroidUtilsPlugin.onPluginStart` deletes `/sdcard/Android/obb/<pkg>`. Game data (about 1.5 GB)
  is downloaded by the installer from the Gameloft CDN as an `.amz` pack and unpacked into
  `<DataPath>/Data/...`. Another agent's `captures/cdn/*` shows that the CDN still answers.

---

## 1. Step-by-step boot sequence

| # | Where | What happens | Evidence |
|---|---|---|---|
| 1 | Manifest | Application class `com.gameloft.android.FrameworkApplication` (only stores context, plus MultiDex). Content providers that run at process start: Crashlytics (`initOrder 100`), MonoRuntimeProvider, MultiDexLoader, FacebookInitProvider. | `decompiled-java/resources/AndroidManifest.xml:27,51-66,77-80`; `decompiled-java/sources/com/gameloft/android/FrameworkApplication.java:20-24` |
| 2 | Manifest | The **MAIN/LAUNCHER activity is `com.gameloft.android.XamarinMainActivity`**. There is no Java splash in front of it. `GameInstaller` is an internal activity started from `onResume`. | `AndroidManifest.xml:35-46,137-144` |
| 3 | Java `XamarinMainActivity.onCreate` | Calls straight into managed code: `n_onCreate` maps to C# `XaLaunchANMP.XamarinMainActivity.OnCreate`. | `decompiled-java/sources/com/gameloft/android/XamarinMainActivity.java:16-29` |
| 4 | C# `XamarinMainActivity.OnCreate` | `Intent.GetStringExtra("phantomArgs")`, then `.Split(':')`, prefixed with `MainLoop.Executable + "/../.."`. Calls the `OnInitialize` hooks of GameDll, AdjustSdkDll, AdjustReferrerReceiver and NicePlaySDKDLL, then `base.OnCreate(new Bundle())` (Java `MainActivity.onCreate`), then `MainLoop.Start(ModeType.DotNetHostsNative, args)`. | `decompiled/XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:63-108` |
| 5 | Java `MainActivity.onCreate` | Finishes if not task root and action==MAIN. Runs `System.loadLibrary("nativeengine")` and sets up the SurfaceView. | `decompiled-java/sources/

---

**NOTE: This report is incomplete. Writing stopped partway through section 1. Sections 2-9 (argument tables, eve service discovery details, the boot dependency table and the redirect recipe) were not written.**
