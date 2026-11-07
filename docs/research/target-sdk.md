# Raising the APK's targetSdkVersion (29 -> 35)

Scope: which `targetSdkVersion` the patched 1.17.32 APK needs so that Android 14/15/16 (and the next releases) install it
without the "built for an older version of Android" warning, and a full audit of every behaviour change between API 29
and that target against this app: the binary manifest after our `debloat` edits, the game's Java (dex), the Xamarin/Mono
runtime, `libnativeengine.so` and our own host code in `hosts/embedded`. Research only; no repository code was changed.

Evidence sources: the player's APK `/home/user/mcvs-work/apk/mcvs.apk`; a patched APK built today from `main-dev`
(`531bf4c`) with the default patch set (`McvsOffline.Patcher --apk ... --out .../tsdk/patched.apk`); aapt2 2.19
(`dump xmltree`, `dump badging`), dexdump and d8 from `/home/user/mcvs-work/tools/android/build-tools`; readelf; the
decompiled Java (`/home/user/mcvs-work/decompiled-java`, jadx line numbers below) and C# (`/home/user/mcvs-work/decompiled`);
AOSP sources fetched from android.googlesource.com (`android13/14/15/16-release` branches, October 2026); the Android
developer documentation (behaviour-change pages for 14, 15 and 16; Play Protect developer guidance, last updated
2026-08-18). Labels: **[V]** verified here on the real files, **[P]** verified by a prototype (aapt2/dexdump/d8, not on
a device), **[D]** from Android documentation or AOSP source, **[U]** unknown.

## 1. Summary

- **What the warning is [D].** On a sideloaded install, Google Play Protect warns "This app was built for an older
  version of Android and does not include the latest privacy protections" when the APK's targetSdkVersion is **more
  than 2 below the device's API level**. Android 16 (API 36, the owner's S22) therefore warns below 34, Android 17
  (API 37, released June 2026) below 35, Android 18 below 36. It is not the OS: AOSP's install block is only below 24
  (`MIN_INSTALLABLE_TARGET_SDK = Flags.minTargetSdk24() ? N : M`, `PackageManagerService.java:570`, android16-release),
  and the OS launch dialog (`DeprecatedTargetSdkVersionDialog`) only below 28 (`RELEASE_PLATFORM_MIN_SUPPORTED_TARGET_SDK_VERSION`
  = "28", `build/release`, unchanged through `main`). Target 29 passes both, which is why the game installs and runs.
- **Recommended target: 35 [D].** It removes the warning on Android 14, 15, 16 and 17. The S22 ends at Android 16
  (One UI 8 was its fourth and last major upgrade). 36 would add one more year (Android 18) but brings three changes
  that hit this game directly (predictive back stops delivering `KEYCODE_BACK`, no edge-to-edge opt-out, orientation
  ignored on large screens) and two of the opt-outs are already announced as temporary. 34 is the minimum for the S22
  only and warns again on Android 17. Make the target a patcher parameter so 36 can follow later (section 7, WP-T8).
- **The bump is not a one-line change. Without fixes, target 35 breaks at least these [V]:**
  1. **Install fails** (31+): three components with an intent filter have no `android:exported`:
     `XamarinMainActivity` (the launcher), `IGPFreemiumActivity`, `LocalPushReceiver` (six without `debloat`).
  2. **Crashes / dead features from `PendingIntent` mutability** (31+, `IllegalArgumentException`): the game's own
     restart `MainActivity.c()` (graphics-setting restart and the settings page's "Restart"), every local-reminder alarm
     and notification (`LocalPushManager`, `SimplifiedAndroidUtils.generateNotification`), the data-download
     notification (`GameInstaller.a(ILjava/lang/String;II)V`) and Google Analytics' dispatch alarm (`zzaoa.zzyk`).
  3. **`SecurityException` from dynamic receivers** (34+): the GA network receiver (`com.google.analytics.RADIO_POWERED`),
     `GMPHelper.<init>` (`PURCHASES_UPDATED`) and `SimplifiedAndroidUtils.Init` (`..._pushbroadcast`; caught by the plugin
     loop, so harmless).
  4. **Local reminders silently stop** (33+): no `POST_NOTIFICATIONS` declaration and nobody requests it.
  5. **Settings page partly under the navigation bar** (35+): `InGameBrowser` is not immersive and gets edge-to-edge.
  6. **External links stop opening** (30+): `InGameBrowser.sAllowBrowser/openIntent` query for a browser without
     `<queries>`.
- **All of it is fixable inside the existing patcher model [P].** Manifest: attribute insertion (the `exported` name is
  already in the string pool and resource map) plus a few appended strings. Dex: 14 **in-place, same-size** edits in
  `classes.dex` (constants, one register field, three `return-void`s), then the dex header checksum and SHA-1. A
  prototype of both (target 35 + exported + all 14 dex edits) passes `aapt2 dump badging/xmltree`, `dexdump -c`
  ("Checksum verified"), a full `d8` re-dex, `zipalign -c -p 4` and `apksigner verify`. It has not run on a device.
- **Things that do not change [V]/[D]:** storage (the app already runs scoped: no storage permission, no
  `requestLegacyExternalStorage`), cleartext to `127.0.0.1` (network security config, rule unchanged since 28), exact
  alarms (only inexact `set()` is used), foreground services (none), the share sheet (our host never queries
  packages), `resources.arsc` (already STORED and aligned), signing (v1+v2+v3 already), 16 KB pages (device, not
  target).
- **Invariant I1 is partly wrong [D].** `docs/ARCHITECTURE.md` I1 says the relaunch alarm relies on target 29. AOSP
  shows AlarmManager has denied background-activity-start privileges to every alarm `PendingIntent` since Android 13,
  independent of target (`mActivityOptsRestrictBal.setPendingIntentBackgroundActivityLaunchAllowed(false)`,
  `AlarmManagerService.java:1876`, android13-release; present in 14, 15, 16; absent in 12). On the S22 the relaunch is
  most likely already blocked at target 29 (STATUS device-test item 9 will tell). The share chooser claim is also not
  target-bound (no package query in our code). The other I1 items are real and are handled below.

## 2. Which target, with evidence

| Source | Rule | Effect on target 29 | Evidence |
|---|---|---|---|
| Play Protect (install of a sideloaded APK) | warn if `targetSdkVersion < deviceApi - 2` | warns on Android 14 (34 -> <32), 15, 16, 17 | developers.google.com/android/play-protect/warning-dev-guidance ("more than 2 versions lower than the current Android API level", example: Android 13 warns below 31) [D] |
| AOSP install block | `INSTALL_FAILED_DEPRECATED_SDK_VERSION` below 23 (14) / 24 (15, 16) | none | `PackageManagerService.java:570` and `InstallPackageHelper.java:1543-1548` (android16-release); 14: `MIN_INSTALLABLE_TARGET_SDK = M` [D] |
| AOSP launch dialog | warning when an activity starts if target < `PLATFORM_MIN_SUPPORTED_TARGET_SDK_VERSION` | none (29 >= 28) | `core/version_util.mk:265-270` (android14: 28); android15/16 take `RELEASE_PLATFORM_MIN_SUPPORTED_TARGET_SDK_VERSION`, declared "28" in `build/release/flag_declarations`, no override in any `flag_values/*` [D] |

| Target | Android 16 (S22) | Android 17 | Android 18 | Cost beyond 35 |
|---|---|---|---|---|
| 34 | no warning | warns | warns | - |
| **35** | no warning | no warning | warns | baseline of this report |
| 36 | no warning | no warning | no warning | `enableOnBackInvokedCallback="false"` (temporary per Google), `appCategory="game"` for sw600dp screens, edge-to-edge without opt-out; 37 would remove the back opt-out and need real dex changes |

Not covered by any target: Google's developer verification for sideloading (from 30 September 2026 in Brazil,
Indonesia, Singapore and Thailand, global in 2027; unverified apps need the one-day "advanced flow" or adb) [D, news
reports, outside this topic's scope].

## 3. The APK as patched today (input to the bump)

Binary manifest of the patched APK (aapt2 `dump xmltree`; `line=` is the source line kept in the AXML) [V]:

- `<uses-sdk minSdkVersion=21 targetSdkVersion=29>`, `compileSdkVersion=29`, `platformBuildVersionCode=29`.
- `<application>`: `networkSecurityConfig=@xml/network_security_config` (cleartext allowed for `127.0.0.1` and
  `gameloft.com` only), `allowBackup=false`, `largeHeap=true`; no `requestLegacyExternalStorage`, no
  `extractNativeLibs` (default true), no `allowNativeHeapPointerTagging`, no `enableOnBackInvokedCallback`.
- Permissions kept: INTERNET, ACCESS_NETWORK_STATE, WAKE_LOCK, BILLING, ACCESS_WIFI_STATE, VIBRATE,
  RECEIVE_BOOT_COMPLETED, the scoped glshare permission. No storage, location, phone or notification permission.

Components with an `<intent-filter>` (all others have none, or an explicit `exported`):

| line | Component | Filter | `exported` today | At 31+ |
|---|---|---|---|---|
| 10 | activity `com.gameloft.android.XamarinMainActivity` | MAIN + LAUNCHER | absent | **install fails**; must be `true` |
| 63 | activity `com.gameloft.igp.IGPFreemiumActivity` | MAIN | absent | **install fails**; `true` keeps today's semantics |
| 110 | receiver `com.gameloft.pushnotification.LocalPushReceiver` | BOOT_COMPLETED | absent | **install fails**; `true` keeps today's semantics |

With `--patch none` (no `debloat`) three more need it: `AppLinksActivity` (line 53), `GCMBroadcastReceiver` (102),
`BootCompletedReceiver` (125). Exported-with-filter elements already carry `exported=true` (they are removed by debloat).

Other facts the audit uses: `resources.arsc` is STORED (875,808 bytes) and `zipalign -c -p 4` passes; the signer
writes v1+v2+v3; `classes.dex` (8,779,916 bytes, CRC32 `3fc9b62d`) and `classes2.dex` are untouched by our patches.

## 4. Audit, change by change

Columns: what changes, whether it applies to this app (with evidence), what breaks, the fix and where it lives.

### 4.1 `android:exported` (31)

Applies [V] (section 3). Fix: manifest attribute insertion in `patcher/Manifest/BinaryXml.cs`, generic rule "every
activity/receiver/service with an `<intent-filter>` and no `exported` gets `exported=true`" (true = the pre-31
default, so no behaviour change; it also covers the no-debloat manifest). The string `exported` is already pool index
23 mapped to `0x01010010`, so this needs no string-pool change: a 20-byte attribute (`ns=android`, `name=23`,
`raw=-1`, `TYPE_INT_BOOLEAN 0x12`, `data=0xFFFFFFFF`) inserted at its resource-id sorted position, attribute count and
chunk sizes updated. Prototype: `scratchpad/tsdk-scripts/axml.py` inserted it at positions 3, 2, 1 of the three
elements; aapt2 reads `exported(0x01010010)=true` on each and `badging` still shows `launchable-activity` [P].
Alternative for `LocalPushReceiver`: `false` also works for BOOT_COMPLETED (sent by the system uid) [D]; keep `true` to
avoid a behaviour question.

### 4.2 `PendingIntent` mutability (31) and the dex patches

At target >= 31 every `PendingIntent.get*` without `FLAG_IMMUTABLE` (0x04000000) or `FLAG_MUTABLE` (0x02000000) throws
`IllegalArgumentException` [D]. dexdump finds 29 creation sites in `classes.dex`, none in `classes2.dex`; none in the
decompiled C# and no `PendingIntent` string in `libnativeengine.so` [V]. Reachable ones:

| # | Site (classes.dex) | Flags today | Reached when | Effect at 31+ | Fix (in place) |
|---|---|---|---|---|---|
| P1 | `MainActivity.c()V` @0x2db230 `const v3, 0x00008000` (jadx `MainActivity.java:107-116`) | 0x8000 (an Intent flag passed by mistake) | `MainActivity.a(true)` -> `finish()` -> `onPause` -> `c()` (`:147-158`, `:315-318`): the graphics-setting restart (`GamePreferencesComponent.NativeChangeGraphicSetting` -> `MenuManager.RestartAppAndroid` -> native `MenuManager_RestartAppAndroid`) and the settings page's ViaGame restart (`hosts/embedded/Android/AndroidExit.cs:40-60`) | uncaught exception in `onPause` on the UI thread: process crash instead of a restart | literal -> `0x04000000` |
| P2-P6 | `LocalPushManager.SetAlarm` @0x308196, `SetRestoredAlarm` @0x30861c, `TriggerAlarm` @0x30878e, `CancelAlarm` @0x308254, `CancelAll` @0x308302 (`LocalPushManager.java:46,110,149,167,181`) | `FLAG_UPDATE_CURRENT` via `const/high16 vX, 0x0800` | scheduling, firing, cancelling reminders; boot rescheduling | reminders not scheduled; `DeleteMessageGroup` swallows the exception (`SimplifiedAndroidUtils.java:538-560`) | `0x0800 -> 0x0c00` (UPDATE_CURRENT \| IMMUTABLE) |
| P7 | `SimplifiedAndroidUtils.generateNotification` @0x30a1ae, one register for 3 calls (`:332-337`, `:418`) | UPDATE_CURRENT | a reminder alarm fires (`LocalPushReceiver`) | exception in the receiver: no notification, possible crash of the background process | `0x0800 -> 0x0c00` |
| P8 | `GameInstaller.a(ILjava/lang/String;II)V` @0x2f2a92 (`GameInstaller.java:3055-3073`) | `const/4 v2, 0` shared with requestCode: `invoke-static {v7, v2, v0, v2}` | download progress (`:1447`, state 12) and download end in background (`:2756`, `:2772`) | exception on the download path | see below |
| P9 | GA `zzaoa.zzyk()` @0x39d364 (`zzaoa.java`, `cancel()` and `schedule()`) | 0 | GA dispatch scheduling; GA starts with every boot through `GoogleAnalyticsTrackerPlugin` (`PluginListInternal.java:5`) | exception in GA's worker | neutralise GA (P12-P14) |

Not reachable offline (left alone): GCM (`GCMRegistrar`, `gcm/a`, `GoogleCloudMessaging`, `iid/zze`,
`GcmNetworkManager`; push-token request disabled by `debloat`), Play Services resolution intents (`GoogleApiActivity`,
`zzbp`, `zzy`, `common/zze`, `zzash`; Play Games/Facebook not initialised since build 8), support-library
`TaskStackBuilder`, `MediaSessionCompat`, `TransportMediator`, `SearchView`. Consequence: target >= 31 must require
`debloat` (or the patcher refuses the combination).

P8 needs a register: `const/4 v2, 0` is also the request code. In place, without growing the method: rebuild the
same intent in fewer code units and use the room for the flags. The 24 units at @0x2f2a62 (pc 0x25-0x3c:
`new Intent("android.intent.action.MAIN")`, `addCategory("android.intent.category.LAUNCHER")`,
`setComponent(new ComponentName("com.gameloft.android.ANMP.GloftMVHM", "com.gameloft.android.XamarinMainActivity"))`)
become `new-instance v3 ComponentName` + the two `const-string` + `invoke-direct ComponentName.<init>` +
`invoke-static {v3} Intent.makeMainActivity` (method@0290, already in `classes.dex`: MAIN + LAUNCHER + component) +
`move-result-object v0` + `const/high16 v6, 0x0400` + 9 `nop`, and the invoke's last register nibble changes v2 -> v6
(@0x2f2a92 `7140 a301 2720` -> `...2760`). v6 is a local first written at 0x006d; v0, v3, v4 and v5 leave the block
holding what they held before. The category must stay. The first patcher version dropped it, assuming that an explicit
intent does not need it. That was wrong. The task's root intent is the launcher's MAIN + LAUNCHER + component, and
GameInstaller sits on top of XamarinMainActivity while it shows this notification. A notification `PendingIntent` is
started with `FLAG_ACTIVITY_NEW_TASK` and reuses that task. `ActivityStarter.complyActivityFlags` then adds a new
activity to the task when the intent is not `filterEquals` to the root intent (`Task.isSameIntentFilter`), and
`filterEquals` compares the categories. Without LAUNCHER, a tap would start a second XamarinMainActivity, and with it
a second GameInstaller, instead of bringing the task back.

Our host [V]: `AndroidExit.ScheduleRelaunch` already passes `FLAG_CANCEL_CURRENT | FLAG_IMMUTABLE` on 23+
(`hosts/embedded/Android/AndroidExit.cs:115-129`). No other `PendingIntent` in `hosts/embedded` or `server/`.

Side effect [D]: `FLAG_IMMUTABLE` is part of a `PendingIntent`'s identity (`PendingIntentController.java:155` masks only
NO_CREATE/CANCEL_CURRENT/UPDATE_CURRENT; `PendingIntentRecord.Key.equals` compares `flags`, `:231`). After the update the
new `CancelAll`/`CancelAlarm` do not match alarms set by the previous build, so already scheduled reminders can fire once
more. One-time, harmless; mention in the release notes. On API 21-22 the 0x04000000 bit is unknown and only changes the
identity key [D].

### 4.3 Exact alarms (31/33/34) and the inexact window

Not applicable [V]: every alarm in the game and in our host uses `AlarmManager.set()` (grep for `setExact`,
`setExactAndAllowWhileIdle`, `setAlarmClock` over all Java finds none). `SCHEDULE_EXACT_ALARM`/`USE_EXACT_ALARM` are
not needed. The 10-minute minimum window (`ENFORCE_MINIMUM_WINDOW_ON_INEXACT_ALARMS`) applies only to explicit
`setWindow` windows (`AlarmManagerService.java:2214-2226`); legacy `set()` uses the heuristic, which does not fuzz near
alarms. Unchanged: non-core uids get `MIN_FUTURITY` (5 s) at any target.

### 4.4 Notifications: `POST_NOTIFICATIONS` (33) and trampolines (31)

- **Permission [D]+[V].** At target <= 32 Android 13+ shows the permission prompt itself when the app creates its
  first notification channel; at 33+ the app must declare and request it. The game creates channels in
  `GameInstaller.java:3084-3089` (download) and `SimplifiedAndroidUtils.java:349-363` (reminders) and never requests the
  permission. Effect: reminders and the download notification silently disappear unless the player enables them in
  system settings. Fix: `<uses-permission android:name="android.permission.POST_NOTIFICATIONS"/>` (new element; or
  reuse one of the `uses-permission` elements `debloat` removes and point its name at a new string), plus a one-time
  runtime request from our host over JNI (`Context.checkSelfPermission`, `Activity.requestPermissions`, both API 23) on
  the game's activity at the first menu when SDK >= 33, remembered in `settings.json`. `MainActivity` has no
  `onRequestPermissionsResult` override (only `PermissionActivity` does), so the result is ignored safely [V].
- **Trampoline [D]+[V].** The reminder's content intent is `PendingIntent.getService(... LaunchPushService ...)` for
  most types (`SimplifiedAndroidUtils.java:332-337`); the `IntentService` then calls `startActivity`
  (`LaunchPushService.onHandleIntent` -> `LaunchAppFromPN`, `:166-179`). From 31 an activity start from a
  notification trampoline is blocked: tapping a reminder dismisses it but does not open the game. Fix options: accept
  (recommended for now: the game is opened from the launcher anyway), or a later dex change that builds the activity
  intent directly (not in place; needs a method-ID-adding dex writer).

### 4.5 Foreground services (34/35), JobScheduler, WorkManager

No foreground service exists [V]: no `startForeground` in any Java, no `foregroundServiceType`, services are GA's,
`LaunchPushService`, `DeletePushService` (plain `IntentService`s started from notification `PendingIntent`s, which get a
temporary allowlist). The 34/35 FGS rules (types, `dataSync` timeout, BOOT_COMPLETED FGS limits) do not apply.
JobScheduler: only GA's `AnalyticsJobService` (`zzaoa.schedule`); 34 requires ACCESS_NETWORK_STATE for network
constraints, which is declared. Neutralising GA (4.10) stops new scheduling; a job an earlier build already scheduled
survives the update, which is why the GA components leave the manifest from 31 on (4.10, build-10 review). No WorkManager
in the dex.

### 4.6 Background activity starts (relaunch after "Restart")

- Baseline [D]: since Android 13 AlarmManager sends every activity alarm with
  `setPendingIntentBackgroundActivityLaunchAllowed(false)` (`AlarmManagerService.java:1876, 4833-4834` android13;
  `:1781, 4682-4684` android16), not target-gated. When the relaunch alarm fires, the game's process is dead, so the
  creator has no visible window either. Expected result on the S22 at target 29: the game closes and does not come back.
  This is STATUS device-test item 9 (C12, design Q8) and should be observed before the bump.
- 34 (sender opt-in) does not matter: the sender is the system. 35 (creators deny by default) can only make it worse.
  Fix if item 9 shows a relaunch at 29: in `AndroidExit.ScheduleRelaunch` use `PendingIntent.getActivity(Context,int,
  Intent,int,Bundle)` with `ActivityOptions.makeBasic().setPendingIntentCreatorBackgroundActivityStartMode(1)` on SDK
  >= 34 (JNI table entries with MinSdk 34), and prefer `admin.restartVia=host`. Otherwise reword the page: "Restart
  closes the game; start it again" on Android 13+.

### 4.7 Storage (29 -> 30: scoped storage enforced, `requestLegacyExternalStorage` ignored)

No change [V]: the app already has no storage permission (the maxSdk-18 WRITE_EXTERNAL_STORAGE was removed by
`debloat`) and never set `requestLegacyExternalStorage`, so it has been running in scoped storage on Android 10+ at
target 29. All its own paths stay accessible without permission at any target: `Android/data/<pkg>/files` (GlfRootPath,
our `McvsOffline/`, `qaTestingConfigs.txt` read in `MainActivity.onKeyUp`), `Android/obb/<pkg>`
(`GameInstaller.marketPath`, `AndroidUtilsPlugin.java:100`). Our MediaStore Downloads code (`AndroidShare.cs:78-130`,
`MediaStoreLedger.cs`) and the public log mirror's direct writes into `Download/MCVS-Offline/` (`PublicMirror.cs`,
Android 11+) are the same for scoped apps at 29 and 35. `ShareFileUses` keys on the device SDK, not the target
(`AndroidJni.cs:216-221`). The only reads of other apps' storage are ads code (`glads` DCIM), dead offline.

### 4.8 Package visibility (30)

- `InGameBrowser.openBrowser/openIntent/sAllowBrowser` (`InGameBrowser.java:521-541, 618-633`) call
  `queryIntentActivities(VIEW https://www.google.com, MATCH_DEFAULT_ONLY)`; at 30+ without `<queries>` the browser is
  invisible, the query returns 0 and external links do nothing (`InGameBrowserExternalPageFailedToOpenCallback`). Our
  settings page has no external link [V]; game links (support, terms) would stop opening. Fix: `<queries><intent>
  <action android:name="android.intent.action.VIEW"/><category android:name="android.intent.category.BROWSABLE"/>
  <data android:scheme="https"/></intent>` (and `http`). The pool already holds `android.intent.action.VIEW` (101),
  `android.intent.category.BROWSABLE` (102), `data` (103), `scheme` (32, mapped), `https` (105); `queries`, `intent` and
  `http` must be appended (non-resource strings, appended at the pool end, no index shift).
- Billing (`iab/C0072y.java:30`, `queryIntentServices` for the Play billing service) -> not found -> billing reports
  unavailable. IAP cannot work offline anyway. Optional `<package android:name="com.android.vending"/>`.
- `AppDetection.getInstalledPackages/Applications` (used by native for cross-promotion) returns fewer packages; no
  offline feature depends on it. `DataSharing` (other Gameloft apps' providers, verify-C1) becomes invisible; it is a
  future "helper app" idea only and would then need `<queries><provider android:authorities=.../>`.
- Our share sheet is unaffected: `AndroidShare` builds `ACTION_SEND` + `Intent.createChooser` + `startActivity`
  (`AndroidShare.cs:380-405`) and never queries packages; `startActivity` is not filtered [V]. The I1 note about the
  chooser is therefore not a constraint.

### 4.9 Cleartext and network security config

No change [V]+[D]: cleartext is blocked by default since target 28; the app's `network_security_config` already
permits `127.0.0.1` and `gameloft.com` (res/xml/network_security_config.xml), which is how the WebView and Java stacks
reach our server today. No later target changes this. Note for LAN mode (unchanged by the bump): Java/WebView cleartext
to a LAN IP is blocked by this config, managed Mono sockets and native libcurl are not subject to it. Android 17's local
network permission is only enforced for apps targeting 37 [D].

### 4.10 Implicit intents and dynamically registered receivers (34)

Rule [D]: a context-registered receiver without `RECEIVER_EXPORTED`/`RECEIVER_NOT_EXPORTED` throws `SecurityException`
unless every action in its filter is a protected broadcast (`BroadcastController.java:370-460`, android16; an empty
filter has an empty action iterator and passes, `IntentFilter.java:470-473, 898-899`). All 46 `registerReceiver` call sites (2-argument and 4-argument forms)
in the dex, classified [V] (protected-broadcast lines from `core/res/AndroidManifest.xml` android16):

| Site | Actions | Status |
|---|---|---|
| `MainActivity.onResume` (`MainActivity.java:142-143`) | CONNECTIVITY_CHANGE (348); BATTERY_LOW/OKAY, ACTION_POWER_(DIS)CONNECTED (71-74) | protected: OK |
| `mono.MonoPackageManager.LoadApplication` (`MonoPackageManager.java:33`) | TIMEZONE_CHANGED (34) | protected: OK (Mono runtime start) |
| `UtilsBatteryStateReceiver` (`:11,19`) | BATTERY_CHANGED with `null` receiver | sticky query, exempt |
| `WifiInfoPlugin.RegisterScannerReceiver` (`:124`) | SCAN_RESULTS (426) | OK |
| `GameInstaller.c` (`GameInstaller.java:2694-2697`) | android.net.wifi.STATE_CHANGE (428) | OK |
| `SimplifiedAndroidUtils.Init` (`:107-109`) | `com.gameloft.android.ANMP.GloftMVHM_pushbroadcast` | **throws**; it is the last statement and `PushNotificationPlugin.onPluginStart` runs inside the per-plugin `try/catch` of `PluginSystem/b.java:41-55`, so the effect is only a logged stack trace and no in-game push relay (`LocalPushReceiver.java:98-100` -> `e.onReceive` -> `nativeSendPNData`) while the game is open. Optional in-place fix P10 |
| `GMPHelper.<init>` (`GMPHelper.java:70`), built by `InAppBilling.a(I)Z` | `com.android.vending.billing.PURCHASES_UPDATED` | **throws** from the constructor when the IAP layer picks Google billing. Fix P11 |
| GA `zzaoo.zzyu` | CONNECTIVITY_CHANGE + `com.google.analytics.RADIO_POWERED` | **throws** in GA. Fix P12-P14 (GA off) |
| GCM/iid, ad SDKs (ironSource, Unity Ads, Vungle, MoPub, Facebook Ads, AVID/OMID), Crashlytics, AppCompat night mode | various | not started offline (debloat, no ad init) |

P10/P11: `invoke-direct {vF, vS}, IntentFilter.<init>(String)` (method@02b8) -> `invoke-direct {vF}, IntentFilter.<init>()`
(method@02b7, already in the dex) at @0x309da6 and @0x2e60ee: same 6-byte format 35c, empty filter, no exception.
P12-P14: `GoogleAnalyticsTracker.Init` @0x2fe114, `activityStart` @0x2fe130, `activityStop` @0x2fe160 become
`return-void` + `nop`s (whole first instruction replaced); `trackEvent` already returns when the tracker is null
(`GoogleAnalyticsTracker.java`). This stops the game from starting GA, so GA makes no new P9 alarm, registers no
RADIO_POWERED receiver and schedules no new job. GA cannot send anything offline, so no feature is lost; it is the same
class of change as build 8's Crashlytics removal.

Correction (build-10 review): P12-P14 do not cover GA started by the system. A dispatch job (`AnalyticsJobService`, only a
latency and an override deadline, no constraints) or alarm (`AnalyticsReceiver`) that build 9 (GA on, same package) left
with JobScheduler/AlarmManager survives the update (only an uninstall or a force-stop cancels it). Its `onCreate`/`onReceive`
runs `zzamu.zzbg` on the main thread, whose init reaches `zzaoa.zzuk` -> `cancel` -> `zzyk`, the P9
`PendingIntent.getBroadcast(ctx, 0, intent, 0)`: IllegalArgumentException on Android 12+ at 31+, which kills the process.
So from 31 on "targetsdk" also removes the four GA declarations (`AnalyticsReceiver`, `AnalyticsService`,
`AnalyticsJobService`, `CampaignTrackingService`; `ManifestTargetSdk.GoogleAnalyticsComponents`): nothing of build 10
uses them, and the system drops a job or broadcast whose component is gone. `RealDexTargetSdkTests` runs a conservative
reachability (rapid type analysis) from the Application class and every declared component and finds no unflagged
PendingIntent site reachable; with the build-9 component set it finds exactly `zzyk` from the three GA components.
The one exception, `GoogleApiActivity` (not exported, nothing reachable names it), only a Play services resolution
starts.

Implicit intents to own non-exported components (34): the game's own intents are explicit (`setComponent`,
`new Intent(context, X.class)`); the push relay above is the only implicit one and is moot.

### 4.11 Edge-to-edge, stable configuration (35) and the 36 UI changes

- `MainActivity` [V]: immersive sticky (`LowProfileListener.ActivateImmersiveMode`, `setSystemUiVisibility(5894)`,
  re-applied in `onWindowFocusChanged`), `layoutInDisplayCutoutMode = SHORT_EDGES` (`MainActivity.java:72-75`), a
  full-window `SurfaceView`, cutout insets read by `f.onApplyWindowInsets` -> `CutoutHelper`. At 35 SHORT_EDGES is read
  as ALWAYS; in landscape (the S22's cutout is on a short edge) that is the same area. No change expected for the game
  surface. `GameInstaller` is immersive too (`GameInstaller.java:2000, 2365`).
- `InGameBrowser` (the settings page) [V]: theme `Theme.acp.notitlebar.fullscreen` ->
  `Theme.Material.Light.NoActionBar.Fullscreen` (status bar hidden), not immersive, so the navigation bar stays. At 35
  the window draws under it (3-button: 80 % scrim; gesture: transparent). In landscape the S22's 3-button bar sits on a
  side, over the page's edge. Fix (works at 35 and 36): `android:fitsSystemWindows="true"` on the root
  `RelativeLayout` (`@+id/ingamebrowser_mview`) of `res/layout-land/` and `res/layout-port/activity_in_game_browser.xml`
  (attr `0x010100dd`; these AXML pools are UTF-8 with a 15-entry resource map, so the name must be inserted into the
  mapped region with an index remap). Host-side alternative without AXML work: on the UI thread, read the private
  static `InGameBrowser.c` (`InGameBrowser.java:57,134`) and call `setFitsSystemWindows(true)` + `requestApplyInsets()`
  on `ingamebrowser_mview` after `PageOpener` starts it. 35-only alternative: `windowOptOutEdgeToEdgeEnforcement`
  (`0x0101069a`) in a `v35` config of style 0x7f0b017e (the icon topic's `resources.arsc` chunk inserter, WP-2 of
  app-icon-and-launch-screen.md); ignored at 36.
- Stable configuration (35): only the ads code reads `Configuration.screenWidthDp/HeightDp`
  (`adsmanager/JavaUtils.java:93-96`); the game uses `DisplayMetrics`. No effect.
- 36 only (if the target ever goes there) [D]: predictive back -> `onBackPressed` not called, `KEYCODE_BACK` not
  dispatched. The game's back button goes through `MainActivity.onKeyDown/onKeyUp` -> `JNIBridge.NativeKeyAction(4)`
  and the settings page's Back guard through `InGameBrowser.onBackPressed` (`:413-420`): both break without
  `<application android:enableOnBackInvokedCallback="false">` (`0x0101066c`). Orientation/resizability ignored on
  sw >= 600dp displays unless `android:appCategory="game"` (`0x01010545`) — not the S22 (sw < 600dp), but tablets and
  open foldables. (Since the PC-port release `appCategory` comes from the separate default patch `game`
  (`patcher/Manifest/ManifestGame.cs`, docs/design/pc-port.md §10), at every target and together with `isGame`;
  "targetsdk" no longer writes it, and the patcher refuses 36 without `game`.) No edge-to-edge opt-out (the layout fix above is required). `elegantTextHeight` ignored (TextViews in
  the installer only).

### 4.12 16 KB page size (Android 15+ devices; not a targetSdk rule)

arm64 `LOAD` alignment (readelf) [V]: `libnativeengine.so` 0x1000, `libmonodroid.so` 0x1000; `libmonosgen-2.0.so`,
`libmono-native.so`, `libmono-btls-shared.so`, `libgenerator.so`, `libxamarin-app.so` 0x10000. The `libaot-*.so` Mono
AOT images have no program headers. Libraries are deflated in the zip and extracted at install (`extractNativeLibs`
default), so zip alignment is irrelevant. On a 16 KB-page device the two 4 KB-aligned libraries cannot load normally;
Android 16 runs such apps in its 16 KB back-compat mode and shows a warning at first launch [D]
(source.android.com "Enable 16 KB backcompat option"). The S22 uses 4 KB pages. Optional: `android:pageSizeCompat="enabled"`
(`0x010106ab`, value 32, `attrs_manifest.xml:1945-1951`) on `<application>` opts in explicitly and suppresses that
warning. Re-linking `libnativeengine.so` for 16 KB is not feasible.

### 4.13 Xamarin/Mono runtime, `libnativeengine.so` and other target-gated items

| Item (target) | Applies? | Evidence | Action |
|---|---|---|---|
| Heap pointer tagging, arm64 (30) | yes, new at 30 | Scudo tags the top byte of malloc pointers for apps targeting 30+ on TBI devices; Havok/engine/Mono 2021 code never ran with it | add `android:allowNativeHeapPointerTagging="false"` (`0x01010612`) as a precaution; A/B test on device |
| SELinux app domain, netlink (30) | small | target 30+ apps cannot send `RTM_GETLINK`; `libmonodroid.so` uses bionic `getifaddrs` when present ("This libc has getifaddrs/freeifaddrs"), and bionic already behaves that way for every non-system app; neither the game's C# nor ours uses `NetworkInterface`/`NetworkChange` | none; LAN-mode device test |
| Hidden API lists (all targets, stricter per target) | small | only `SystemProperties.get("ro.serialno")` via reflection (`Device.java:102-103`, `SendInfo.java:225-226`, inside try) and ads' proxy hacks | none |
| Mono Java glue | none | `MonoPackageManager` uses public APIs only; `IncrementalClassLoader` reflection is debug-only | none |
| Dynamic code loading read-only (34) | no | `SUtils.getInjectedSerialKey` opens installed APKs (read-only) with `PathClassLoader` only for a resource | none |
| Zip path traversal (34) | low | `GameInstaller` `ZipFile` and `GLUtils/ZipFile` unzip Gameloft packs; entries with `..` would now throw | device test of a fresh data download |
| TLS 1.0/1.1 off (35) | low | Java `HttpURLConnection` downloads in `GameInstaller`; the CDN serves TLS 1.2+ [U for every host] | data-download test |
| OpenJDK changes (35) | no | no `(String[]) Arrays.asList(..).toArray()`, no `%0` formats, no `iw/in/ji` literals in game Java | none |
| Audio focus (35) | no | `AudioListenerPlugin.requestAudioFocus` runs with the game in front | none |
| Custom-view toasts from background (30) | cosmetic | `installer/h.java:32` `toast.setView` (download toast) | none |
| `allowBackup=false` ignored for device-to-device transfer (31) | cosmetic | internal app data may now migrate on a phone change; the saves live in `Android/data` | none |
| `resources.arsc` STORED + 4-byte aligned (30) | already true | `unzip -v`, `zipalign -c -p 4` | keep a test |
| APK Signature v2 required (30) | already true | patcher signs v1+v2+v3 | none |
| Safer intents (36, opt-in) | no | - | none |

### 4.14 Our C# host (`hosts/embedded`)

Uses only target-neutral APIs except where noted [V]: share (MediaStore on 10+, `file://` with the StrictMode
relaxation on 7-9, keyed on device SDK), clipboard, toast (plain text), `ExitReasons` (API 30), restart (4.6), log
mirror (4.7). Additions for the bump: the `POST_NOTIFICATIONS` request (4.4), optionally the BAL creator opt-in (4.6)
and the browser insets fallback (4.11); each needs JNI table entries (`AndroidJni.cs`, MinSdk-gated) and the existing
JNI-table test against `android.jar` must then use an API >= 34 jar for those members (today it uses android-29).

## 5. Prototype (scratch only, not installed)

- Dex: `scratchpad/tsdk-scripts/dexpatch.py` applies P1-P8 and P10-P14 to `classes.dex` with byte-exact "expected"
  checks, then rewrites the SHA-1 signature and Adler-32 checksum. `dexdump -c`: "Checksum verified"; disassembly shows
  `const v3, #04000000`, `const/high16 v2, #c00`, `const/high16 v6, #400` + `invoke-static {v7, v2, v0, v6}`,
  `invoke-direct {v2}, IntentFilter.<init>:()V`, `return-void` at the three GA entry points; `d8 --release --min-api 21`
  re-dexes the whole file without error (60 bytes differ from re-dexing the original). A first attempt that replaced
  only the first code unit of a multi-unit instruction was rejected by d8 ("Illegal Opcode: 0xe4"): the patcher must
  replace whole instructions (and its tests should run a dex parser over the result).
- Manifest: `scratchpad/tsdk-scripts/axml.py` sets `targetSdkVersion` 35 and inserts the three `exported` attributes.
- APK: both put into a copy of today's patched APK, `zipalign -p 4`, `apksigner sign` (throwaway key) -> `apksigner
  verify` OK, `aapt2 dump badging`: `targetSdkVersion:'35'`, `launchable-activity: com.gameloft.android.XamarinMainActivity`.
- Not proven: ART's verifier on the device (it is stricter than d8 on dead code and register types), runtime behaviour.

## 6. Correction to invariant I1

`docs/ARCHITECTURE.md` (I1), `docs/design/settings-page.md` §0 and `ManifestTargetSdkTests` pin target 29 for five
reasons. Status after this audit: game restart without `FLAG_IMMUTABLE` -> real, fixed by P1; share chooser -> not
target-bound (4.8); relaunch alarm -> blocked since Android 13 at any target (4.6); browser edge-to-edge -> real at 35,
fixed by 4.11; `file://` share on 7-9 -> device-SDK-bound, unchanged. The bump replaces I1 with "I1': target 35, every
intent-filter component has `exported`, no reachable `PendingIntent` creation without a mutability flag, GA off,
POST_NOTIFICATIONS declared", asserted by tests.

## 7. Recommended plan

| WP | Content | Size | Depends |
|---|---|---|---|
| **WP-T0** | Before the bump, on the S22 at target 29: STATUS item 9 (does the relaunch happen?), current notification permission state, navigation mode (buttons/gestures), a screen recording of the settings page. These are the baselines for WP-T7. | device | - |
| **WP-T1** | `BinaryXml` growth: (a) set an int attribute; (b) insert an attribute whose name is already resource-mapped; (c) append non-resource strings at the pool end; (d) insert a resource-mapped attribute name (pool + resource map insert at index N, remap every string reference: namespaces, element/attribute names, raw and typed string values); (e) insert an element subtree. Unit tests on synthetic AXML; byte-identical output when nothing is asked. Shared with the icon topic (WP-4 monochrome) and with layout edits. | M | - |
| **WP-T2** | Patch id `targetsdk` (default on) in `patcher/Manifest/`: target 35 (CLI `--target-sdk 29|35|36`, 29 = today's output); `exported=true` on every intent-filter component lacking it; `allowNativeHeapPointerTagging=false`; `POST_NOTIFICATIONS`; `<queries>` for http/https VIEW; optional `pageSizeCompat=enabled`. Refuse target >= 31 without `debloat`. Leave `compileSdkVersion`/`platformBuildVersion*` as they are (informational). | S | T1 |
| **WP-T3** | Dex patching in the patcher (`patcher/Dex/`): minimal reader (header, string/type/proto/method/field ids, class_defs, class_data, code_item); locate methods by descriptor, never by file offset; SHA-256 fingerprint of each method's code_item (like the Cecil patches); whole-instruction, same-size edits P1-P8, P10-P14; header checksum + signature; refuse on any mismatch. Tests with the real 1.17.32 dex (`MCVS_APK`): fingerprints, the edits, every other byte unchanged, dexdump/d8 parse when the tools exist. | M | - |
| **WP-T4** | Settings page under the nav bar: `fitsSystemWindows` on both `activity_in_game_browser` layouts (needs T1 d), or the host-side JNI fallback. | S | T1 |
| **WP-T5** | Host: one-time POST_NOTIFICATIONS request (SDK >= 33, not granted, not asked before; a settings-page row showing the state); restart wording on Android 13+ and, only if WP-T0 shows a working relaunch, the BAL creator opt-in; JNI table entries + `android.jar` >= 34 for their test. | S | - |
| **WP-T6** | Tests and docs: replace `ManifestTargetSdkTests` with the I1' assertions (target, exported, flags at every reachable `PendingIntent` site via a dex scan, GA entry points return, POST_NOTIFICATIONS, `<queries>`); `resources.arsc` STORED/aligned assertion; update ARCHITECTURE (I1), settings-page.md §0, STATUS, PLAYER-SETUP (update over the previous build with the same key; no reinstall; one-time stale reminders). | S | T2-T5 |
| **WP-T7** | Device test on the S22 (Android 16): install shows no Play Protect "older version" warning; boot, menus, matches, saves; Back in game and in the settings page; settings page layout with buttons and with gestures; graphics-setting restart and page Restart (no crash, relaunch per WP-T0); reminders: scheduled, fired, tapped (expect no game open: trampoline), after reboot; the notification permission prompt; a fresh data download with its notification; share sheet and Save to Downloads; LAN mode; `adb logcat` free of `IllegalArgumentException ... FLAG_IMMUTABLE`, `SecurityException ... RECEIVER_EXPORTED`, `VerifyError`, "Background activity launch blocked" surprises. If any regression: `--target-sdk 29` reproduces today's APK. | device | T2-T6 |
| WP-T8 (later, optional) | Target 36: `enableOnBackInvokedCallback=false`, `appCategory="game"`, verify T4 without any opt-out; a tablet/foldable or emulator sw600dp check. | S | T7 |

Order: T0 and T1/T3 in parallel, then T2, T4, T5, T6, then T7. T2 alone is not shippable: T3 is what prevents the
crashes.

## 8. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | ART's verifier rejects a patched method (`VerifyError` makes the whole class unusable: `MainActivity`, `LocalPushManager`, `GameInstaller`) | same-size, same-type edits only; whole instructions; d8/dexdump in tests; device test; `--target-sdk 29` fallback |
| R2 | A reachable `PendingIntent`/receiver path was missed (e.g. a GMS path reached through a code path not seen statically) | the I1' dex scan lists every creation site and its state; logcat review in WP-T7 |
| R3 | Native code (Havok, engine, Mono) misbehaves with heap pointer tagging | `allowNativeHeapPointerTagging=false`; A/B on device |
| R4 | `fitsSystemWindows` interacts badly with the page's own sizing (250-330 CSS px of height) | screenshots in both navigation modes and orientations; host-side fallback |
| R5 | Notification permission state after the update differs from expectations (already granted via the old auto-prompt, or denied) | WP-T0 baseline; the host only asks when not granted |
| R6 | A future build or another patch edits `classes.dex` | method-descriptor lookup + fingerprints refuse a different dex; `--force` does not bypass dex fingerprints |
| R7 | Play Protect warns for other reasons (unknown developer, the coming developer verification) | outside targetSdk; document the "advanced flow"/adb path in PLAYER-SETUP when it reaches the player's country |
| R8 | Samsung One UI deviates from AOSP (BAL, splash, edge-to-edge) | device test |

## 9. Unknowns

- U1: ART verification of the 14 dex edits on Android 16 (only d8/dexdump checked).
- U2: Whether the relaunch alarm brings the game back on the S22 at target 29 (AOSP says no since Android 13; device
  test item 9 pending), and therefore whether any BAL work is worth doing.
- U3: Whether `POST_NOTIFICATIONS` is already granted to the installed build (auto-prompt at target 29) and survives the
  update.
- U4: Whether `libnativeengine.so`/Havok or Mono 2021 code depends on untagged heap pointers (decides whether the
  tagging opt-out is needed or only a precaution).
- U5: Whether `InAppBilling.a(I)Z` (and so `GMPHelper.<init>`) runs during a normal offline session.
- U6: The S22's navigation mode and how much of the settings page the bar covers at 35 without the fix.
- U7: Whether every CDN host used by `GameInstaller` negotiates TLS 1.2+ (assumed).
- U8: Exact Play Protect behaviour on Samsung devices (the rule is Google's; One UI ships Play Protect unchanged as far
  as known).

## 10. Decisions for the owner

1. Target 35 now (recommended) or 36 (one more year without the warning, needs the back-key opt-out that Google calls
   temporary and the large-screen exemption).
2. GA: turn Google Analytics off entirely (recommended; required for a clean 31+/34+ without patching GA internals).
3. Reminder taps: accept that tapping a reminder no longer opens the game (trampoline rule), or plan a larger dex change.
4. Notification permission: ask once at the first menu (recommended), or only from a settings-page button.
5. `exported` values: `true` everywhere (no behaviour change, recommended) or `false` for `IGPFreemiumActivity` and
   `LocalPushReceiver`.
6. Heap-tagging opt-out on by default (recommended) or only after a crash is seen.
7. Restart: keep the game's own restart path (with P1) or switch the page to `admin.restartVia=host` by default.
8. Optional `pageSizeCompat="enabled"` for future 16 KB-page phones.

## Critic notes

Completeness review (research only).

1. **Verified.**
   * Google's Play Protect developer guidance (fetched 2026-10-28, "Last updated 2026-08-18 UTC") states the rule
     "more than 2 versions lower than the current Android API level", with the Android 13 example "lower than 31". The
     34/35 thresholds in §1 follow from it.
   * The stock manifest (aapt2 `dump xmltree`) has exactly six intent-filter components without `exported`:
     `XamarinMainActivity`, `AppLinksActivity`, `IGPFreemiumActivity`, `GCMBroadcastReceiver`, `LocalPushReceiver` and
     `BootCompletedReceiver`. This matches §3.
   * `MainActivity.java:109` passes flags 32768 (0x8000) to `PendingIntent.getActivity` (P1).
2. **Conflict with app-icon-and-launch-screen.md.** The "35-only alternative" in §4.11 (a `v35` config of style
   0x7f0b017e with `windowOptOutEdgeToEdgeEnforcement`) would hide the icon report's `v31` splash attributes on API 35+:
   style items are not merged across configs. Prefer the `fitsSystemWindows` fix (WP-T4). If the v35 style is used,
   it must repeat all splash items in `v35` and `night-v35`. On the splash itself the two reports agree: no
   targetSdk gate.
3. **Gap in §4.6: the graphics-setting restart.** The graphics-setting restart (`MainActivity.c()`) is itself an
   AlarmManager `getActivity` relaunch (`MainActivity.java:109`). By §4.6's own analysis it is BAL-blocked on Android 13+
   at any target. So P1 turns a crash at 35 into "the game closes and does not return", not into a working restart.
   WP-T0 should observe the graphics-setting restart at target 29 as well as the page's Restart. WP-T5's wording change
   should cover the in-game graphics dialog too, which is game UI and cannot be reworded by the host.
4. **Unverified edit (P8).** Dropping `const-string v3` is safe only if v3 is not read before its next write. The
   report checks v6's liveness but not v3's. Add a register-liveness check to WP-T3's fingerprint tests. ART's verifier
   rejects a read of an undefined or conflicting register (U1).
5. **Missing risk: target-gated runtime behaviour in Xamarin.Android 10.3/Mono.**
   * The hidden-API max-target lists (`max-target-q/r/s…`) are enforced by target. §4.13 audits only the Java side.
     JNI lookups from `libmonodroid.so`/Java.Interop are not covered.
   * WP-T7 should grep logcat for `Accessing hidden method` and `NoSuchMethodError` from Mono.
6. **The rollback path is worth a sentence.** Reinstalling `--target-sdk 29` over a 35 build with the same key and
   versionCode is allowed. Android only refuses an update that goes from targetSdk >= 23 to < 23
   (permission-model downgrade). WP-T7's fallback therefore works without an uninstall. This is from AOSP behaviour and
   was not tested.
