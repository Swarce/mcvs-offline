# MCVS Offline: launcher icon and launch screen art

This folder holds the project's own icon and launch-screen art for the offline Android build. It is empty on purpose
until the art exists: while it holds no art files, the patcher keeps Gameloft's icon and the stock launch screen, byte
for byte. As soon as files are added here and the patcher is rebuilt (`scripts/build.sh`), every patch run uses them.
Players run the same command as before.

Only art that belongs to the project goes here. Never copy Gameloft's icons (or edits of them) into this folder, into
test fixtures or into previews that get committed.

Background: [docs/research/app-icon-and-launch-screen.md](../../docs/research/app-icon-and-launch-screen.md).

## 1. What to deliver

| File | What | Size (px) | Needed |
|---|---|---|---|
| `icon/foreground.png` | Adaptive icon, front layer: the logo on transparency | 432 x 432 (or 864, 1296, 1728) | yes, for an icon |
| `icon/background.png` | Adaptive icon, back layer: full-bleed picture | 432 x 432 (or 864, 1296, 1728) | yes, unless `art.json` gives `icon.background` (a flat colour) |
| `icon/legacy.png` | The icon for Android 5-7 (no layers): the finished, shaped icon | 192 x 192 (or 384, 576, 768) | yes, for an icon |
| `icon/monochrome.png` | Themed icon (Android 13+): one white shape on transparency | 432 x 432 (or multiples) | optional |
| `art.json` | Colours: launch screen, and the icon background as a colour | - | optional; copy `art.example.json` |

All three icon parts come together: if any icon file is here, foreground, background (or its colour) and legacy must be
too, or the patcher refuses and writes nothing. The launch screen colour (`art.json` `splash`) works with or without
icon files.

The patcher makes every smaller density (ldpi to xxhdpi) from these masters by area averaging. A master is either the
exact xxxhdpi size above or an exact multiple of it; larger masters give the smoother small sizes. Anything else is
refused.

## 2. The adaptive icon (Android 8 and later)

The launcher stacks the foreground on the background and cuts both with its mask (circle, squircle, rounded square,
teardrop: the phone decides). Both layers are drawn on a **108 x 108 dp** canvas; at the 432 px master size 1 dp = 4 px.

```
  432 px (108 dp)  whole layer. The outer 72 px (18 dp) on each side is bleed: never visible at rest,
  |                but the launcher can shift or scale the layers into it for motion effects.
  |   288 px (72 dp)  the visible area: the largest any mask shows.
  |   |   264 px (66 dp)  the safe circle: content inside it survives every mask shape.
  |   |   |
  +---+---+------- centre of the canvas
```

- **Foreground**: draw the logo **inside the central 264 px circle** (66 dp), on **transparency**. Leave everything
  else transparent so the background shows through. A fully opaque foreground (like Gameloft's) hides the background and
  fills the whole mask; the patcher warns about it, and about pixels outside the safe circle or the visible area.
- **Background**: fill the **whole 432 px**, bleed included, with no transparency. A plain colour is fine and can be
  given in `art.json` (`"icon": {"background": "#RRGGBB"}`) instead of a file. Using the launch screen colour here gives
  the cleanest launch screen (see 4).
- Keep strong detail away from the edges of the visible area: masks differ by a few dp from phone to phone.

## 3. The other two icons

- **Legacy** (`icon/legacy.png`, 48 x 48 dp, 192 px at xxxhdpi): Android 5-7 show this file as it is, with no mask, so
  it is the finished icon: your shape (round, rounded square...) with transparent corners. Leave about 8 px (2 dp) of
  transparent margin around the shape.
- **Monochrome** (`icon/monochrome.png`, optional, same 432 px canvas and 264 px safe circle as the foreground): a single
  white shape on transparency. Android 13+ tints it when the user turns on themed icons; only its alpha channel counts.
  Without this file the launcher shows the normal icon in themed mode. Whether Samsung's One UI uses it for apps
  installed outside the Play Store is not known yet.

## 4. The launch screen (Android 12 and later)

Android 12+ draws the launch screen itself: a colour with the app icon in the middle. A full-screen picture is not
possible there. What you control:

- `splash.background`: the colour. A **dark** colour is recommended, because the next screen (the game's installer) is
  black, so a light colour flashes before it. `splash.backgroundNight` sets dark mode's colour (default: the same).
- The icon on it is the adaptive icon. If the icon background is a flat colour equal (or close) to the splash colour,
  only the **foreground** is drawn, a bit larger when it has transparency; otherwise the masked icon is drawn. Either
  way the logo has to stay inside the 66 dp safe circle (the splash shows the same central part as the launcher).
- `splash.iconBackground`: an optional disc colour behind the icon.
- `splash.iconPreferred`: also show the icon when the game is started by the settings page's Restart or a reminder
  (Android 13+). Recommended `true`.
- `splash.windowBackground`: also paint the game's window with the splash colour. This changes the background of the
  MCVS Offline page and the installer screens too; leave it `false` until it has been looked at on a phone.

Android 5-11 show a plain near-white window before the game; the art does not change that.

## 5. File format

- PNG, 8 bits per channel: RGBA (preferred), RGB, greyscale or greyscale with alpha. Not 16-bit, not palette
  (indexed), not interlaced, not animated (APNG). sRGB.
- Square, at the sizes above. Masters up to 2048 x 2048 px.
- Each file that ends up in the APK (every exported density) must be 512 KiB or smaller, and all icon files together
  4 MiB or smaller. Masters are not limited this way; flat areas and transparency compress well.

## 6. Hand-tuned densities (optional)

To control a small size by hand (for example pixel-hinting the 36 px ldpi legacy icon), put that one file under
`icon/<density>/`, named like Android Studio's output. It replaces the export of the master for that density and is
copied byte for byte. A layer can also come only from per-density files, with no master.

| Density | Foreground, background, monochrome | Legacy |
|---|---|---|
| `ldpi` | 81 | 36 |
| `mdpi` | 108 | 48 |
| `hdpi` | 162 | 72 |
| `xhdpi` | 216 | 96 |
| `xxhdpi` | 324 | 144 |
| `xxxhdpi` | 432 | 192 |

File names: `ic_launcher_foreground.png`, `ic_launcher_background.png`, `ic_launcher.png` (legacy),
`ic_launcher_monochrome.png`; for example `icon/xxhdpi/ic_launcher_foreground.png`.

## 7. Checking the art before committing

1. Previews: `python3 tools/art/export-icons.py` (needs Pillow) draws the icon under the circle, squircle and
   rounded-square masks, the 66 dp safe circle and the launch screen in light and dark mode into `art/previews/`
   (git-ignored; never commit previews). `--install` also writes its per-density exports into `icon/<density>/`.
2. Validation, without rebuilding the patcher: run it with `--art art/android --dry-run` on your APK. It lists every
   file it would replace and every warning, and refuses art that is off-spec.
3. A test build: the same with `--out`, then install. If the launcher keeps showing the old icon, reboot the phone (the
   launcher caches icons).
