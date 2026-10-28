# Plug-and-play app icon and launch screen (Android)

Scope: how the 1.17.32 APK shows its launcher icon and its launch screen today, and a mechanism that lets the patcher
put the owner's own art in (icon layers, splash background) as soon as the files exist, without code changes.
Research only; no repository code was changed. The owner draws the art later.

Evidence sources: the player's APK `/home/user/mcvs-work/apk/mcvs.apk` read with aapt2 34.0.0
(`dump badging`, `dump xmltree`, `dump resources`), the zip directory (`unzip -v`), Python/Pillow on the extracted PNGs,
a byte-level parse of `resources.arsc`, the decompiled Java (`/home/user/mcvs-work/decompiled-java`), the decompiled C#
(`/home/user/mcvs-work/decompiled`), the decoded data pack, and AOSP `frameworks/base` (branch `main`, fetched October
2026) for the Android 12+ splash screen logic. Labels: **[V]** verified here, **[P]** verified by prototype (aapt2 only,
not on a device), **[D]** from Android documentation or AOSP source, **[U]** unknown.

## 1. Summary

- **Launcher icon [V].** `android:icon=@mipmap/ic_launcher` (0x7f030001) on `<application>`, no `roundIcon`, no icon on
  the launcher activity. On Android 8+ the icon is the adaptive icon `res/mipmap-anydpi-v26/ic_launcher.xml`
  (background `@mipmap/ic_launcher_background`, foreground `@mipmap/ic_launcher_foreground`, no monochrome layer). Below
  Android 8 it is the bitmap `ic_launcher.png` in six densities. Every icon file is a plain PNG, STORED in the zip.
- **The shipped adaptive layers are low resolution [V].** The foreground is 192 px at xxxhdpi where a 108 dp layer
  needs 432 px, so the launcher scales it up by 2.25. The foreground is opaque RGB with no alpha. The background is a
  solid white greyscale PNG.
- **Launch screen [V]+[D].** There is no splash activity, no layer-list and no `windowSplashScreen*` attribute in the
  APK. On Android 12+ (the owner's S22 runs Android 16) the system draws its own splash. Its colour comes from the
  launcher activity's theme `Theme.acp.notitlebar.fullscreen`, which resolves on API 21+ to
  `Theme.Material.Light.NoActionBar.Fullscreen`, whose `windowBackground` is `?colorBackground` = `material_grey_50`
  (#FAFAFA in AOSP). The icon on it is the adaptive icon's foreground, because its white background counts as "similar
  to the theme colour". That is the white screen with the icon. After it come the Java `GameInstaller` (black, with a
  spinner, set in code) and then the engine. The engine shows no splash image: `SplashScreenFilename` is not set in
  `MCVS.game`.
- **The `windowSplashScreen*` attributes do not depend on targetSdk [D].** AOSP reads them from the activity theme
  with no targetSdk check (`SplashscreenContentDrawer.getWindowAttrs`). Every app gets the new splash; the legacy path
  is only for packages on a device-config exception list (`ActivityRecord` "useLegacy" ->
  `StartingSurfaceController.isExceptionApp`). The splash can be customised at targetSdk 29. The topic brief assumed
  the opposite.
- **Recommended mechanism.** (1) Icon: replace the existing icon PNG entries in place, under the same names and
  densities. This needs no `resources.arsc` change and no ID change. (2) Splash: insert two new `style` type chunks
  (`v31` and `night-v31`) into `resources.arsc`, each holding a single entry: the launcher theme 0x7f0b017e with
  `windowSplashScreenBackground` (and the optional extras). No ID moves, no string-pool change, no other chunk is
  edited. This was prototyped and `aapt2 dump resources` parses it as intended [P]. (3) Optional later: a monochrome
  layer for themed icons, which needs real `resources.arsc` and binary-XML growth. The art lives in the repository
  under `art/android/` (the project's own art, never Gameloft's) and is built into the patcher, so players run the
  same command as today. With no art present, the patcher keeps the stock icon.

## 2. How the APK shows its icon (evidence)

### 2.1 Manifest and badging [V]

`aapt2 dump xmltree --file AndroidManifest.xml mcvs.apk`:

| Element | Attribute | Value |
|---|---|---|
| `<application>` | `android:icon` | `@0x7f030001` (mipmap/ic_launcher) |
| `<application>` | `android:theme` | `@0x01030007` (`@android:style/Theme.NoTitleBar.Fullscreen`) |
| `<application>` | `android:label` | `@0x7f0900a7` (string/icon_label = "MCVS" in all 18 configs) |
| `<application>` | `android:roundIcon` | absent |
| `<activity com.gameloft.android.XamarinMainActivity>` (MAIN/LAUNCHER) | `android:theme` | `@0x7f0b017e` (style/Theme.acp.notitlebar.fullscreen) |
| same | `android:icon` | absent (inherits the application icon) |
| `<uses-sdk>` | min/target | 21 / 29 |

The C# side agrees: `decompiled/XaLaunchANMP/Properties/AssemblyInfo.cs:16`
`[assembly: Application(... Icon = "@mipmap/ic_launcher", Label = "@string/icon_label", ... Theme = "@android:style/Theme.NoTitleBar.Fullscreen")]`
and `decompiled/XaLaunchANMP/XaLaunchANMP/XamarinMainActivity.cs:22` (`MainLauncher = true`,
`Theme = "@style/Theme.acp.notitlebar.fullscreen"`). `Resource.cs:3951` `Theme_acp_notitlebar_fullscreen = 2131427710`
(= 0x7f0b017e): resource IDs are compiled into managed code, so **no edit may renumber resources** (this rules out an
apktool decode/rebuild).

`aapt2 dump badging`: `application-icon-120` ... `-640` and `-65534` all resolve to
`res/mipmap-anydpi-v26/ic_launcher.xml`, and `launchable-activity ... icon=''`.

### 2.2 Resource table entries (`aapt2 dump resources`, type `mipmap` id 0x03, 6 entries) [V]

| ID | Name | Configs -> file |
|---|---|---|
| 0x7f030000 | ic_ad_choices | mdpi..xxxhdpi PNG (ads; unrelated) |
| 0x7f030001 | **ic_launcher** | `()` `res/mipmap/ic_launcher.png`; `ldpi-v4`..`xxxhdpi-v4` `res/mipmap-<d>-v4/ic_launcher.png`; `anydpi-v26` `res/mipmap-anydpi-v26/ic_launcher.xml` |
| 0x7f030002 | **ic_launcher_background** | `()` `res/mipmap/ic_launcher_background.png`; ldpi..xxxhdpi |
| 0x7f030003 | **ic_launcher_foreground** | ldpi..xxxhdpi only (no default config) |
| 0x7f030004 | ic_launcher_foreground1 | `()` `res/mipmap/ic_launcher_foreground1.png` only. No reference in Java (`grep` over `decompiled-java/sources`, only `R.java`) and only a constant in `Resource.cs:2497`. A spare slot. |
| 0x7f030005 | iconiab | hdpi only (in-app-billing UI) |

Adaptive icon XML (`aapt2 dump xmltree --file res/mipmap-anydpi-v26/ic_launcher.xml`): `<adaptive-icon>` with
`<background android:drawable=@0x7f030002>` and `<foreground android:drawable=@0x7f030003>`, nothing else. The binary
file is 552 bytes and its string pool is `drawable, android, <ns>, "", adaptive-icon, background, foreground`. There is
no `monochrome` element and no `<inset>`.

### 2.3 The PNG files (zip entries; all STORED, so compression does not matter) [V]

| Entry | Size px | PNG mode | Bytes | CRC32 |
|---|---|---|---|---|
| res/mipmap/ic_launcher.png | 72x72 | RGB | 12414 | 666099d7 |
| res/mipmap-ldpi-v4/ic_launcher.png | 36 | RGB | 3345 | bdae05fd |
| res/mipmap-mdpi-v4/ic_launcher.png | 48 | RGB | 5811 | 6e31ac2d |
| res/mipmap-hdpi-v4/ic_launcher.png | 72 | RGB | 12414 | 666099d7 |
| res/mipmap-xhdpi-v4/ic_launcher.png | 96 | RGB | 21031 | f0220f0e |
| res/mipmap-xxhdpi-v4/ic_launcher.png | 144 | RGB | 44087 | 66ec0e3b |
| res/mipmap-xxxhdpi-v4/ic_launcher.png | 192 | RGB | 75314 | c3b9f1e6 |
| res/mipmap/ic_launcher_background.png | 72 | L (white) | 103 | 66d1875e |
| res/mipmap-ldpi-v4/ic_launcher_background.png | 36 | L | 85 | 6b12b715 |
| res/mipmap-mdpi-v4/ic_launcher_background.png | 48 | L | 91 | 1b94c04c |
| res/mipmap-hdpi-v4/ic_launcher_background.png | 72 | L | 103 | 66d1875e |
| res/mipmap-xhdpi-v4/ic_launcher_background.png | 96 | L | 118 | 50976dc3 |
| res/mipmap-xxhdpi-v4/ic_launcher_background.png | 144 | L | 166 | 41e5472d |
| res/mipmap-xxxhdpi-v4/ic_launcher_background.png | 192 | L | 228 | 2af98fdb |
| res/mipmap-ldpi-v4/ic_launcher_foreground.png | 36 | RGB | 3339 | 498cf086 |
| res/mipmap-mdpi-v4/ic_launcher_foreground.png | 48 | RGB | 5744 | 845dd923 |
| res/mipmap-hdpi-v4/ic_launcher_foreground.png | 72 | RGB | 12142 | 8dd2b27d |
| res/mipmap-xhdpi-v4/ic_launcher_foreground.png | 96 | RGB | 20424 | 359ec2ab |
| res/mipmap-xxhdpi-v4/ic_launcher_foreground.png | 144 | RGB | 42239 | 8b03d5ca |
| res/mipmap-xxxhdpi-v4/ic_launcher_foreground.png | 192 | RGB | 69211 | b4e9bd07 |
| res/mipmap/ic_launcher_foreground1.png | 72 | RGB | 12154 | 09fbe797 |
| res/mipmap-anydpi-v26/ic_launcher.xml | (AXML) | | 552 (DEFLATED) | b49f237e |
| resources.arsc | | | 875808 (STORED) | 2a11d4f0 |

Findings:
- Every background PNG is a single colour (Pillow `getcolors`: one colour, (255,255,255)).
- The adaptive layers are 48 dp-sized bitmaps (36..192 px) used as 108 dp layers, so they are scaled up by 2.25.
  An adaptive layer should be 108 dp: 81/108/162/216/324/432 px.
- The foreground has no alpha channel, so the white background layer never shows. Viewed, the xxxhdpi foreground is
  the full-bleed key art with a copy scaled to about 2/3 pasted in the centre (the visible 72 dp window). That is
  Gameloft's own way of filling the bleed area.
- `res/mipmap/ic_launcher.png` and `res/mipmap/ic_launcher_background.png` (the default, no-density config, which the
  framework treats as mdpi/160 dpi) are copies of the hdpi files. They are only fallbacks: the six density configs
  cover every bucket.

### 2.4 Which file a device uses [D]

- API 26+ (including the S22 on Android 16): `mipmap-anydpi-v26/ic_launcher.xml` wins over every density PNG (badging,
  2.1). The launcher draws background + foreground through its mask (One UI: squircle). The visible area is the inner
  72 dp of the 108 dp layers; content inside the central 66 dp circle survives every mask shape.
- API 21-25: `mipmap-<density>-v4/ic_launcher.png` (48 dp bitmap, no mask).
- Themed icons (Android 13+, "Themed icons" in the launcher) need a `<monochrome>` layer. Without one the launcher
  shows the normal icon. Whether One UI themes third-party icons from that layer is [U].

## 3. How the launch screen works (evidence)

Sequence on a cold start from the home screen:

1. **System splash (Android 12+) [D].** WindowManager adds a starting window built by
   `SplashscreenContentDrawer` (WM Shell). What decides its look:
   - `ActivityRecord` (around line 2413 on AOSP `main`): `useLegacy = type == STARTING_WINDOW_TYPE_SPLASH_SCREEN &&
     mStartingSurfaceController.isExceptionApp(packageName, mTargetSdk, ...)`. The legacy (pre-12 style) splash is only
     for apps on the device-config exception list. Every other app, at any targetSdk, gets the new splash.
   - `SplashscreenContentDrawer.getWindowAttrs` reads `windowBackground`, `windowSplashScreenBackground`,
     `windowSplashScreenAnimatedIcon`, `windowSplashScreenBrandingImage` and `windowSplashScreenIconBackgroundColor`
     from the activity's theme with `obtainStyledAttributes`. There is no targetSdk test.
   - `peekWindowBGColor`: it uses `windowSplashScreenBackground` if set (non-zero), otherwise it estimates the colour
     of the `windowBackground` drawable.
   - Icon (`SplashViewBuilder.build` / `processAdaptiveIcon`): `windowSplashScreenAnimatedIcon` if set, otherwise the
     app icon. For an `AdaptiveIconDrawable` it draws **only the foreground** when the icon background colour is
     transparent and the adaptive background is "not complex" and either similar to the splash colour or greyscale
     with a contrasting foreground. In that case the size is scaled 192/160 if the foreground is mostly transparent.
     Otherwise it draws the whole masked icon.
   - `ActivityRecord.isIconStylePreferred` reads `windowSplashScreenBehavior` (API 33 attr); `icon_preferred` (1)
     keeps the icon when the launch source would otherwise give a solid-colour splash (for example a start from a
     notification or an alarm `PendingIntent`, `shouldUseSolidColorSplashScreen`).

   For MCVS today: the activity theme 0x7f0b017e has configs `()` parent 0x01030007, `v11` parent 0x0103006d and
   `v21` parent 0x01030242 = `Theme.Material.Light.NoActionBar.Fullscreen`, all with no items (`aapt2 dump resources`).
   In `android.jar` (API 29) `Theme.Material.Light` sets `windowBackground=?attr/colorBackground` and
   `colorBackground=@color/background_material_light` = `@color/material_grey_50` (#FAFAFA). The adaptive background
   is plain white (not complex, similar), so the splash is the **opaque square foreground, masked to the splash
   circle, on near-white**. That matches what the owner sees. (The device's framework could theme these colours
   differently; [U] for One UI.)
2. **XamarinMainActivity / MainActivity [V].** `MainActivity.onCreate` (`decompiled-java/.../android/MainActivity.java:57-90`)
   loads `libnativeengine`, sets a `RelativeLayout` with a `SurfaceView`. There is no image, no splash view and no
   window-background change. The system splash is removed when this window draws its first frame. Mono start-up and
   our `Bootstrap.Init` run before that (`XamarinMainActivity.OnCreate`, report 01 rows 3-4), so the splash covers
   that time.
3. **GameInstaller [V].** `MainActivity.onResume` starts `com.gameloft.android.installer.GameInstaller` on every
   launch (`MainActivity.java`, `onResume`). Its `onCreate` sets a `RelativeLayout` with
   `setBackgroundColor(Color.parseColor("#000000"))` and a large `ProgressBar` (`GameInstaller.java:1940-1951`), so the
   screen is black with a spinner. The colour is set in code, not by a resource. The download screens
   (`data_downloader_*` layouts with `@drawable/installing_splash_bg*` 800x600 / 512x768 PNGs, and `gi_layout_logo`
   with `@drawable/data_downloader_spash_logo_20years` on `gi_gameloft_logo_background_color` #ff000000) only appear
   when data must be downloaded or checked.
4. **Engine [V].** `GameApplication` calls `Initialize(component.SplashScreenFilename, component.SplashWideScreenFilename)`
   (`decompiled/Gameloft.Engine/Gameloft.Engine/GameApplication.cs:1209`, P/Invoke `GameApplication_Initialize` at
   :1771). The data pack's `MCVS.game` `/GameDefinition` sets neither property
   (`datapack-decoded/MCVS.game`), and `libnativeengine.so` (arm64) has no splash file name string. So there is no
   engine splash image. The first engine screen is level content (`LevelsComponent.Levels` starts with
   `Levels/Connection.level`). That content is game data on the player's phone, outside this mechanism.
5. `com.gameloft.ingamebrowser.SplashScreenActivity` exists in the dex as an empty class but is not declared in the
   manifest. It is dead.

Before Android 12 (API 21-30), the starting window is the theme's `windowBackground` (#FAFAFA, no icon) [D].

## 4. Design: what the owner provides

The folder convention below is the contract. Every file is optional as a group: no icon files means the stock icon is
kept, and no splash settings means the stock splash colours are kept.

```
art/android/
  README.md                       the spec below, for the artist
  art.json                        optional settings (splash colours, label)
  icon/
    foreground.png                adaptive foreground layer, 108 dp design
    background.png                adaptive background layer, 108 dp design (or "background" colour in art.json)
    legacy.png                    pre-Android-8 icon, 48 dp design, already shaped (required with the icon set, see 4.3)
    monochrome.png                optional, themed icon (Android 13+), only with WP-4
  icon/<density>/...              optional hand-tuned exports per density (overrides the masters)
```

### 4.1 Sizes

| Layer | Design grid | ldpi | mdpi | hdpi | xhdpi | xxhdpi | xxxhdpi |
|---|---|---|---|---|---|---|---|
| foreground / background / monochrome | 108x108 dp; visible 72 dp; keep the logo inside the central 66 dp circle | 81 | 108 | 162 | 216 | 324 | **432** |
| legacy `ic_launcher.png` | 48x48 dp | 36 | 48 | 72 | 96 | 144 | **192** |

Recommended delivery: **one master per layer at xxxhdpi** (432x432 for the layers, 192x192 for legacy), or larger
exact multiples (864, 1296). Per-density files under `icon/<density>/` take precedence, so an artist exporting from
Android Studio "Image Asset" or icon.kitchen can drop their `mipmap-*` output in, using the file names
`ic_launcher_foreground.png`, `ic_launcher_background.png`, `ic_launcher.png`, `ic_launcher_monochrome.png`.

Formats:
- PNG, 8-bit per channel, RGBA (colour type 6) for foreground, legacy and monochrome; RGB or RGBA for background.
  Not interlaced, no APNG (`acTL`). sRGB.
- Foreground must have transparency around the logo. A fully opaque foreground hides the background, as the stock one
  does. If the foreground has transparency, the splash scales it 192/160 (3.1).
- Background: either an image or just a colour. A solid colour that equals the splash background gives the cleanest
  splash: the system then draws only the foreground (3.1).
- Monochrome: one colour (white) plus alpha. The launcher tints it.

### 4.2 Splash settings (`art.json`)

```json
{
  "splash": {
    "background": "#10181F",        // windowSplashScreenBackground, light theme (required to change the splash)
    "backgroundNight": "#000000",   // optional; same as background when absent
    "iconBackground": null,         // optional windowSplashScreenIconBackgroundColor: a disc behind the icon
    "iconPreferred": true,          // optional windowSplashScreenBehavior=icon_preferred (API 33+): icon also on relaunch
    "windowBackground": false       // optional: also set windowBackground to the splash colour (see 6, risk R4)
  },
  "label": null                     // optional, see WP-5
}
```

The splash icon is the adaptive icon itself (no separate splash logo). This is deliberate. The splash icon guideline
(icon "288x288 dp within a 192 dp circle" without icon background, "240x240 dp within 160 dp" with one; Android
developer docs, Splash screens) has the same 2/3 proportion as the adaptive foreground (72/108), so a foreground drawn
to the adaptive-icon keylines is already a correct splash icon. A different splash logo is WP-6 (optional).

A dark splash colour is recommended: the next screen (GameInstaller) is hard-coded black, so a dark splash avoids a
white-to-black flash.

### 4.3 Validation (patcher, before anything is written)

- The file starts with the PNG signature, IHDR is the first chunk, bit depth 8, colour type 2 or 6 (0/4 allowed for
  background), interlace 0, no `acTL`. Every chunk CRC is checked.
- Width == height, and either the exact density size from 4.1 or (for masters) an exact multiple of the xxxhdpi size.
  Masters larger than xxxhdpi are only accepted if WP-1 includes a resampler (see 5.1); otherwise exact per-density
  files are required. The recommendation is to keep the patcher dependency-free and require exact sizes, with a helper
  script that exports them (WP-1b).
- Size limits: each file at most 512 KiB, all icon files together at most 4 MiB. A 432x432 RGBA layer is 746 KiB
  uncompressed and typically 30-200 KiB as PNG. The limits stop accidental 4K exports from bloating launcher memory.
- Foreground and monochrome must contain transparent pixels. Warn if the foreground is fully opaque (the stock flaw).
- Monochrome: warn if pixels with alpha > 0 have more than one RGB value.
- Colours in `art.json`: `#RRGGBB` only. Alpha is forced to FF: a value of 0 means "unset" to the framework
  (`peekWindowBGColor` checks `!= 0`), and a translucent splash colour is not meaningful.
- Completeness: if any icon file is present, the full set (foreground, background or a background colour, legacy) must
  resolve for all six densities, or the patch fails. A half-replaced icon (new adaptive icon, old Gameloft legacy
  icon) is never written.
- Licence: `art/android/` holds only project-owned art. The stock Gameloft icons must never be copied there or into
  test fixtures. Tests use synthetic PNGs.

## 5. Design: how the patcher applies it

### 5.1 Icon: replace PNG entries in place (no table change) — WP-1

The 20 icon entries in 2.3 (ic_launcher x7, ic_launcher_background x7, ic_launcher_foreground x6) are replaced
byte-for-byte with the owner's PNGs, keeping each entry name, STORED method and alignment. `resources.arsc` stores only
the path strings (`res/mipmap-xxxhdpi-v4/ic_launcher_foreground.png`, ...), and the framework decodes bitmaps by
content, not by declared size, so a 432 px file under an entry that held a 192 px file is valid: `AdaptiveIconDrawable`
scales each layer to its 108 dp bounds. Default-config files (`res/mipmap/ic_launcher.png`,
`res/mipmap/ic_launcher_background.png`) get the **mdpi** export, because the framework decodes a no-density resource
as 160 dpi (the shipped hdpi copies there are a Gameloft quirk; they are never selected on a real device, since all six
densities exist). ldpi gets its own 36/81 px export (Android Studio does not produce ldpi; the helper script does).

If the background is given as a colour only, the patcher writes a 1-colour PNG of the right size for each density.
Writing a PNG is trivial (IHDR + one zlib IDAT via `System.IO.Compression.ZLibStream` + IEND); no image library is
needed. Resampling a master into six sizes would need one (ImageSharp/SkiaSharp) or a hand-written filter. The
recommendation is that the patcher accept exact sizes only and that WP-1b provide the exporter.

Fingerprint first, like `RawArgsFor` (`patcher/ApkPatcher.cs:206`): every replaced entry must have the CRC in 2.3, or
the patch refuses unless `--force`. The output loop gets one more branch next to the `res/raw/args.txt` and manifest
branches (`ApkPatcher.cs:313-326`), and the names go into `report.Modified`/`modifiedNames` so the post-write check
"entry changed unexpectedly" (`ApkPatcher.cs:400-406`) stays strict for every other entry.

### 5.2 Splash colours: insert `style` type chunks into `resources.arsc` — WP-2

Facts from the table (byte parse of `resources.arsc`; script in the scratchpad):
- One package 0x7f at offset 0x58b00 (header 288 bytes); global string pool UTF-8, 6179 strings, no styles.
- `style` (type id 0x0b) typeSpec at 0xc36d0 with 426 entries, followed by 18 `ResTable_type` chunks (configs default,
  v4, night-v8, v11, v12, v14, v17, v21 x2, v22..v25, hdpi-v4, ...). The last one ends at 0xd0a4c, right before the
  `dimen` typeSpec. **None has `sdkVersion` 31.**
- Entry 0x17e (Theme.acp.notitlebar.fullscreen) exists in configs `()`, `v11`, `v21`. Each is a map entry with key
  string index 1541, `count` 0 and parents 0x01030007 / 0x0103006d / 0x01030242.

The change is two new `ResTable_type` chunks for type 0x0b, inserted at the end of the style type's chunks (offset
0xd0a4c in 1.17.32; found by parsing, never hard-coded):
- `config`: 64 bytes like the existing ones, `sdkVersion` = 31; the second also has `uiMode` = 0x20 (night yes).
- `entryCount` 426 (same as the typeSpec), offsets all `0xFFFFFFFF` (NO_ENTRY) except index 0x17e = 0.
- One `ResTable_map_entry`: `size` 16, `flags` FLAG_COMPLEX, `key` = the key index of the existing entry (1541),
  `parent` = the parent of its highest existing config (v21: 0x01030242), and any items of that config copied (none
  today). Then our items, **sorted by attribute ID** as aapt2 emits them:
  - 0x01010054 `windowBackground` = colour (only with `"windowBackground": true`),
  - 0x0101062c `windowSplashScreenBackground` = `TYPE_INT_COLOR_ARGB8` (0x1c),
  - 0x01010630 `windowSplashScreenIconBackgroundColor` = colour (optional),
  - 0x01010668 `windowSplashScreenBehavior` = `TYPE_INT_DEC` 1 (`icon_preferred`; an API 33 attribute, ignored by 31/32).
  Attribute IDs are from `core/res/res/values/public-final.xml` (AOSP `main`).
- Then the package chunk size and the table header size grow by the inserted length. Nothing else changes: no
  typeSpec, no string pool, no key pool, no other chunk's content. There are no absolute offsets after the package
  header that point past the insertion (the type and key string pool offsets come before it).

Why this layout:
- No new resource ID, so `Resource.cs` constants and every `R.java` constant stay valid.
- Inserting right after the style chunks (not at the end of the package) keeps every `ResTable_type` behind its own
  typeSpec. Android 10+ `LoadedArsc` maps chunks by type id, but older loaders attach a type chunk to the most recent
  typeSpec. minSdk is 21, so the safe placement is used.
- A `-v31` config is chosen only on API 31+, where the attributes exist. On 21-30 the v21 entry is used unchanged.
- `night-v31` beats `v31` in night mode (more specific); outside night mode it does not match.
- It is the same table shape aapt2 itself produces for a `values-v31/themes.xml`.

Prototype [P]: the insertion above (light #101820, night #000000, also `windowBackground`) was applied to a copy of the
1.17.32 table; the APK copy was re-zipped and `aapt2 dump resources` printed
```
resource 0x7f0b017e style/Theme.acp.notitlebar.fullscreen
  () (style) size=0 parent=0x01030007
  (v11) (style) size=0 parent=0x0103006d
  (v21) (style) size=0 parent=0x01030242
  (v31) (style) size=2 parent=0x01030242
    0x01010054=#ff101820
    0x0101062c=#ff101820
  (night-v31) (style) size=2 parent=0x01030242
    0x01010054=#ff000000
    0x0101062c=#ff000000
```
with the rest of the dump identical (diff shows only these 6 lines) and `aapt2 dump badging` unchanged. Inserted
size: 3656 bytes (2 x (84 header + 1704 offsets + 40 entry)). This was not tested on a device.

Fingerprints: `resources.arsc` CRC32 2a11d4f0 / 875808 bytes, plus structural checks (no v31 style chunk yet, entry
0x17e has exactly the three configs and parents above), or refuse unless `--force`. The new arsc stays STORED and
4-byte aligned. That is required for targetSdk >= 30 (section 7) and is already what the zip writer does for stored
entries.

### 5.3 CLI and build integration — WP-3

- The patcher csproj embeds `../art/android/**` as manifest resources (`LogicalName="art/%(RecursiveDir)%(Filename)%(Extension)"`).
  The player's command line does not change. When the owner adds files and rebuilds, the next patch uses them. This is
  the plug-and-play path.
- `--art auto` (default): built-in art if any, else stock with a note "no project art built in; Gameloft icon kept".
  `--art stock`: never touch icon/splash. `--art <dir>`: use a folder (for the artist's own test builds without
  rebuilding the patcher).
- `--dry-run` prints the plan: each entry to be replaced (old CRC -> new size), the splash items, and validation
  warnings.
- `PatchReport`: "icon: 20 entries replaced", "resources.arsc: style 0x7f0b017e +v31 +night-v31 (windowSplashScreenBackground #...)".
- Optional patch id `art` in `PatcherOptions.DefaultPatches`, so `--patch` can disable it like the others.

### 5.4 Verification (automated, in the existing real-APK tests)

- `aapt2 dump resources` of the output equals the input dump plus exactly the expected style lines.
- `aapt2 dump badging` unchanged (icon path, label, launchable activity).
- `aapt2 dump xmltree` of `ic_launcher.xml` unchanged (unless WP-4).
- Replaced entries: CRC equals the art file; all other entries are byte-identical (existing check).
- `zipalign -c 4` and `apksigner verify` as today. `ManifestTargetSdkTests` is untouched by this work.
- Unit tests on a synthetic table: insertion keeps every other chunk byte-identical; a second run refuses (v31 already
  present); a table without entry 0x17e refuses.

## 6. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | A device's resource loader rejects or misreads the inserted chunk (prototype checked by aapt2 only) | Device test on the S22 (Android 16), plus an emulator at API 21 or 23 and API 28 (old loaders), before release. WP-2 is separable from WP-1. |
| R2 | One UI changes the AOSP splash logic (colour cache, icon choice) | Device test, light and dark (`adb shell cmd uimode night yes/no`). Fallback: icon-only (WP-1) still improves the splash, because the splash icon is the foreground. |
| R3 | The launcher keeps the old icon after reinstall (icon cache keyed by package and update time; versionCode stays 1173200) | Normal install updates `lastUpdateTime`. If it sticks: reboot, or clear One UI Home's cache. Document in PLAYER-SETUP. |
| R4 | Setting `windowBackground` in v31 also changes the window background of `InGameBrowser` (same theme 0x7f0b017e, which shows the settings page) and of `GameInstaller` (`Theme.GameInstaller*` inherit it). The installer content is opaque black, and the browser's loading background would be dark | Off by default; device-check the settings page before turning it on. The splash attributes alone only affect starting windows. |
| R5 | Owner art is not to spec (opaque foreground, logo outside 66 dp, huge files) | Validation in 4.3 and preview images from the helper script (WP-1b). |
| R6 | Copyright: stock icons committed by mistake (e.g. as test fixtures or previews) | Synthetic fixtures only; the helper script's preview output goes to a git-ignored folder. |
| R7 | A future game build or another patch that edits `resources.arsc` (for example targetSdk work) conflicts | One shared `ArscTable` reader/writer in `patcher/Resources/`, with all arsc edits applied in one pass and fingerprinted against the input CRC. |

## 7. Interaction with the targetSdk bump (other topic)

- **Splash:** no change in behaviour. AOSP has no targetSdk gate on the splash attributes or on the new splash itself
  (3.1). The only targetSdk-gated splash change found is `ALLOW_COPY_SOLID_COLOR_VIEW` (`@EnabledSince TIRAMISU`,
  `StartingSurfaceController.java:63-65`), which only concerns apps that use the `SplashScreen` exit-animation API.
  MCVS does not. So WP-2 can ship before, with or after the bump.
- **Icon:** no change. Adaptive icons need API 26 at runtime, not a targetSdk, and monochrome needs API 33 at runtime.
- **targetSdk >= 30:** `resources.arsc` must be STORED and 4-byte aligned or installation fails
  (`INSTALL_PARSE_FAILED_RESOURCES_ARSC_COMPRESSED`). It is STORED today and WP-2 keeps it so. The bump's tests should
  assert this for the output.
- **targetSdk >= 31:** the launcher activity `XamarinMainActivity` has an intent filter but no `android:exported`
  (2.1). The bump must add `android:exported="true"` to it (and to every other component with an intent filter) or
  installation fails. This needs attribute insertion in `BinaryXml`, which today can only remove elements and replace
  strings (`patcher/Manifest/BinaryXml.cs:162,284`). WP-4 (monochrome element) needs the same insertion support, so
  both topics should share it.
- **targetSdk >= 35:** edge-to-edge is enforced. It does not affect the splash or the icon. It does affect
  `windowBackground` visibility behind system bars if R4's option is used.
- Invariant I1 in `docs/ARCHITECTURE.md` (targetSdk 29, enforced by `ManifestTargetSdkTests`) is owned by the bump
  topic. Nothing here depends on it.

## 8. Recommended plan

| WP | Content | Size | Depends |
|---|---|---|---|
| **WP-1** | Icon replacement in place (5.1): PNG reader/validator (IHDR, chunk CRC, alpha scan via `ZLibStream` + unfilter), solid-colour PNG writer, 20-entry map with CRC fingerprints, output-loop branch, report lines, tests with synthetic PNGs and a synthetic APK | S-M | – |
| WP-1b | `tools/art/export-icons.py` (Pillow, owner-side only): from 432/192 masters, write all densities into `art/android/icon/<density>/`, plus previews (circle, squircle and rounded-square masks; a simulated splash on the chosen colour; a 66 dp keyline overlay) into a git-ignored folder | S | – |
| **WP-2** | `resources.arsc` reader/writer for this one edit (5.2): parse table, package, type chunks; insert v31 / night-v31 style chunks for 0x7f0b017e; fingerprint; tests | M | – |
| **WP-3** | Build and CLI plumbing (5.3): embedded `art/android/**`, `--art auto|stock|<dir>`, `art.json` parsing, `art/android/README.md` with 4.1-4.3 for the artist, PLAYER-SETUP note | S | WP-1, WP-2 |
| WP-4 (optional) | Themed icon: `<monochrome android:drawable=@mipmap/ic_launcher_foreground1>` in a regenerated 4-element `ic_launcher.xml` AXML (pool + "monochrome"). Density configs for 0x7f030004 by growing each density `mipmap` type chunk by one entry and appending six path strings to the UTF-8 global pool (no styles, unsorted, so appending is simple). Default config file replaced with the mdpi monochrome | M | WP-2's table code |
| WP-5 (optional) | Launcher label (for example "MCVS Offline"): append one string to the global pool and repoint the 18 `string/icon_label` values (in-place 4-byte `data` changes) | S | WP-2's table code |
| WP-6 (optional) | A splash logo different from the icon: `windowSplashScreenAnimatedIcon` -> a drawable. Needs density configs for a spare entry (as WP-4), so it is only worth doing if the owner wants it | M | WP-4 |
| WP-7 (optional) | Pre-Android-12 launch screen: a `windowBackground` layer-list (colour + centred logo) for API 21-30. Needs a new XML drawable (AXML) plus table growth. Low value: the owner's device is Android 16 | M | WP-4 |
| WP-8 (optional) | Notification icons: local push notifications (kept by `debloat`) use `R.drawable.pn_status_icon` as small icon and `PushTheme.getIcon()` (`pn_custom_icon`/`pn_icon`/`icon`) as large icon (`GLUtils/PushBuilders/a.java:23,41`, `PushTheme.java:20-37`). The same in-place replacement as WP-1 applies | S | WP-1 |

Order: WP-1 + WP-2 + WP-3 make the requested "plug and play": the owner drops `foreground.png`, `background.png` (or a
colour), `legacy.png` and a splash colour into `art/android/`, rebuilds, patches. Then device test D1: install on the
S22; check the launcher icon (One UI squircle), a cold start in light and dark mode (screen record with
`adb shell screenrecord`), a relaunch through the settings page's restart (the `icon_preferred` case), and that the
settings page in `InGameBrowser` looks unchanged.

## 9. Unknowns

- U1: Android 16 / One UI 8 behaviour with the inserted `v31` style chunks (prototype verified with aapt2 only).
- U2: Old-loader behaviour (API 21-28) with the inserted chunks; placement follows the safe rule, but it is untested.
- U3: Whether One UI's launcher themes third-party icons from a `<monochrome>` layer (decides whether WP-4 is worth it).
- U4: Whether One UI applies its own splash colour or theming instead of the AOSP logic in section 3.
- U5: What is visible between splash removal and the first GameInstaller frame (the window background, #FAFAFA on
  AOSP, around a not-yet-drawn `SurfaceView`): a white flash or not. This decides whether `windowBackground` (R4)
  should be on.
- U6: The S22's density bucket (likely xxhdpi, about 420-450 dpi; the launcher may load icons one bucket higher).
  This only affects which export is used, not the mechanism.
- U7: Whether the launcher's icon cache refreshes on reinstall with the same versionCode (R3).
- U8: How the system splash looks when the game is relaunched by the alarm `PendingIntent`. Per AOSP it is solid
  colour without `icon_preferred` (launch source "system", not a new start); not observed on the device.

## 10. Decisions for the owner

1. Splash background colour, and whether dark mode gets its own colour.
2. Whether to also set the window background to the splash colour (avoids a possible white flash; changes the
   background behind the settings page and installer windows) — after U5 is observed.
3. Whether the adaptive background is a flat colour (recommended: equal to the splash colour, giving a clean splash)
   or an image.
4. Themed (monochrome) icon: yes or no (WP-4).
5. Change the launcher label "MCVS" (WP-5)?
6. A separate splash logo (WP-6) or the icon foreground (default)?
7. Also brand notification icons (WP-8)?
8. With no project art built in: keep the stock icon (recommended) or refuse to patch.

## Critic notes

Completeness review (research only).

1. **Conflict with target-sdk.md §4.11 (a v35 style config).** target-sdk.md offers a `v35` config of style 0x7f0b017e
   with `windowOptOutEdgeToEdgeEnforcement` as one fix for the settings page. It would be inserted with this report's
   chunk inserter.
   * Android does not merge a style's items across configurations. For one entry it picks the single best-matching
     config, and inheritance runs only through the `parent` reference.
   * On the S22 (API 36), a `v35` entry would win over `v31`/`night-v31`, and the splash attributes would disappear.
   * If both features ship, the writer must emit `v35` and `night-v35` entries that repeat every `v31` item, from one
     shared `ArscTable` pass (this report's R7).
   * WP-2 can only ship "before, with or after the bump" (§7) if this holds.
   * The simpler choice is to use target-sdk.md's `fitsSystemWindows` layout fix instead of the v35 style.
2. **Unanswered: what "background" means to the owner.** The owner said the icon and the background are custom art.
   This report treats "background" as the adaptive-icon background layer plus a splash colour.
   * If the owner means a full-screen launch image, the Android 12+ system splash cannot show one. It supports a
     colour, a centred icon and an optional bottom branding image (`windowSplashScreenBrandingImage`), which this
     report does not plan.
   * A full-bleed image would need one of:
     * a bitmap `windowBackground`, shown only after the system splash, and with risk R4 on `InGameBrowser`/installer;
     * a change to the hard-coded black `GameInstaller` screen (`GameInstaller.java:1940-1951`), which is a dex edit
       (target-sdk.md WP-T3 machinery).
   * Add this as owner decision 0, before WP-2.
3. **"Plug and play" still needs an export step.** WP-1 accepts exact per-density sizes only, so the owner must run
   the WP-1b Pillow script before dropping files in. A truly plug-and-play path is one 432 px layer master plus one
   192 px legacy master, resized by the patcher (a small area-average resampler). The sizes 81/108/162/216/324 are not
   integer divisors of 432, so a box filter alone is not enough. Otherwise, state clearly in `art/android/README.md`
   that the script is part of the contract.
4. **Verified here.** The stock manifest (aapt2 34.0.0 `dump xmltree`) has `minSdkVersion=21`, `targetSdkVersion=29`
   and the launcher theme/icon attributes as in §2.1. The inserted size arithmetic is consistent:
   `2 × (84 + 426·4 + 40) = 3656`.
5. **Not verified here (source [D] only).**
   * The AOSP claim that the `windowSplashScreen*` attributes have no targetSdk gate, and that the legacy splash applies
     only to exception-list apps. target-sdk.md does not contradict it; neither report quotes line-pinned code for the
     `getWindowAttrs` path.
   * Device test D1 remains the only proof (U1, U4).
