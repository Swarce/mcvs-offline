#!/usr/bin/env python3
"""Owner-side helper for the launcher icon and launch-screen art (art/android/README.md,
docs/research/app-icon-and-launch-screen.md WP-1b). Needs Pillow; never run by the build.

From the masters in art/android/ (icon/foreground.png, icon/background.png or art.json
icon.background, icon/legacy.png, optional icon/monochrome.png) it writes into a
git-ignored folder (default art/previews/):

    densities/<density>/ic_launcher*.png   every density, Lanczos-resampled (premultiplied alpha)
    masks.png         the adaptive icon under circle, squircle, rounded-square and teardrop masks,
                      the legacy icon, the themed icon (monochrome tinted), at launcher size and large
    keylines.png      foreground over background with the 72 dp visible square and the 66 dp safe circle
    splash-light.png  the Android 12+ launch screen as AOSP draws it (approximate), light mode
    splash-dark.png   the same in dark mode (splash.backgroundNight)

Usage:
  python3 -I tools/art/export-icons.py                  # art/android -> art/previews
  python3 -I tools/art/export-icons.py --art DIR --out DIR
  python3 -I tools/art/export-icons.py --install        # also copy the density exports into
                                                        # art/android/icon/<density>/ (they then replace
                                                        # the patcher's own resampling of the masters)

The patcher does not need this script: it validates the art and resamples the masters itself. Run the
patcher with --art art/android --dry-run to see exactly what it would write. Never commit previews.

Exit status: 0 = written, 1 = art problem (message on stderr), 2 = usage error / Pillow missing.
"""

import argparse
import json
import math
import os
import re
import shutil
import sys

sys.dont_write_bytecode = True

try:
    from PIL import Image, ImageChops, ImageDraw
except ImportError:  # pragma: no cover
    sys.stderr.write("export-icons.py needs Pillow: python3 -m pip install Pillow\n")
    sys.exit(2)

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))

# Must match patcher/Art/IconArt.cs.
DENSITIES = [("ldpi", 120), ("mdpi", 160), ("hdpi", 240), ("xhdpi", 320), ("xxhdpi", 480), ("xxxhdpi", 640)]
LAYER_DP, LEGACY_DP, VISIBLE_DP, SAFE_DP = 108, 48, 72, 66
LAYERS = {  # layer -> (master file, design dp, per-density file name)
    "foreground": ("icon/foreground.png", LAYER_DP, "ic_launcher_foreground.png"),
    "background": ("icon/background.png", LAYER_DP, "ic_launcher_background.png"),
    "legacy": ("icon/legacy.png", LEGACY_DP, "ic_launcher.png"),
    "monochrome": ("icon/monochrome.png", LAYER_DP, "ic_launcher_monochrome.png"),
}
STOCK_SPLASH = (0xFA, 0xFA, 0xFA)  # Theme.Material.Light colorBackground (material_grey_50): today's splash
PREVIEW_BG = (0x80, 0x80, 0x80)


class ArtError(Exception):
    pass


def px(dp, dpi):
    return dp * dpi // 160


def load_jsonc(path):
    """art.json allows // and /* */ comments and trailing commas (like the patcher)."""
    text = open(path, encoding="utf-8").read()
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            i = text.find("\n", i)
            i = n if i < 0 else i
        elif text.startswith("/*", i):
            i = text.find("*/", i)
            i = n if i < 0 else i + 2
        else:
            out.append(c)
            i += 1
    cleaned = re.sub(r",(\s*[}\]])", r"\1", "".join(out))
    try:
        return json.loads(cleaned)
    except ValueError as e:
        raise ArtError("art.json: not valid JSON: %s" % e)


def color(value, what):
    if value is None:
        return None
    if not isinstance(value, str) or not re.fullmatch(r"#[0-9A-Fa-f]{6}", value):
        raise ArtError('art.json: %s must be "#RRGGBB", not %r' % (what, value))
    return tuple(int(value[k:k + 2], 16) for k in (1, 3, 5))


def resample(image, size):
    """Lanczos on premultiplied alpha, so transparent pixels do not bleed into edges."""
    if image.size == (size, size):
        return image.copy()
    return image.convert("RGBa").resize((size, size), Image.LANCZOS).convert("RGBA")


def open_png(path, what):
    try:
        image = Image.open(path)
        image.load()
    except Exception as e:  # noqa: BLE001 - Pillow raises many types
        raise ArtError("%s: cannot read %s: %s" % (what, path, e))
    if image.format != "PNG":
        raise ArtError("%s: %s is %s, not PNG" % (what, path, image.format))
    if image.mode not in ("RGBA", "RGB", "L", "LA"):
        raise ArtError("%s: %s is mode %s; export 8-bit RGBA (no palette, no 16-bit)" % (what, path, image.mode))
    if image.info.get("interlace"):
        raise ArtError("%s: %s is interlaced; export without interlacing" % (what, path))
    if getattr(image, "is_animated", False):
        raise ArtError("%s: %s is animated (APNG)" % (what, path))
    return image.convert("RGBA")


def load_layers(art):
    """layer -> {density: RGBA image} for every density, from masters, per-density files or the colour."""
    settings = load_jsonc(os.path.join(art, "art.json")) if os.path.exists(os.path.join(art, "art.json")) else {}
    icon_bg = color((settings.get("icon") or {}).get("background"), "icon.background")
    splash = settings.get("splash") or {}
    result = {}
    for layer, (master_name, dp, density_name) in LAYERS.items():
        master_path = os.path.join(art, master_name)
        master = None
        if os.path.exists(master_path):
            master = open_png(master_path, layer)
            unit = px(dp, 640)
            if master.width != master.height or master.width % unit:
                raise ArtError("%s: %s is %dx%d; a master is %dx%d or an exact multiple"
                               % (layer, master_name, master.width, master.height, unit, unit))
        images = {}
        for name, dpi in DENSITIES:
            size = px(dp, dpi)
            path = os.path.join(art, "icon", name, density_name)
            if os.path.exists(path):
                image = open_png(path, layer)
                if image.size != (size, size):
                    raise ArtError("%s: icon/%s/%s is %dx%d, expected %dx%d" % (layer, name, density_name, image.width, image.height, size, size))
                images[name] = image
            elif master is not None:
                images[name] = resample(master, size)
            elif layer == "background" and icon_bg:
                images[name] = Image.new("RGBA", (size, size), icon_bg + (255,))
        if images and len(images) < len(DENSITIES):
            missing = [n for n, _ in DENSITIES if n not in images]
            raise ArtError("%s: no master and no per-density file for %s" % (layer, ", ".join(missing)))
        if images:
            result[layer] = images
    present = set(result)
    if present and not {"foreground", "background", "legacy"} <= present:
        raise ArtError("incomplete icon: missing %s (see art/android/README.md)"
                       % ", ".join(sorted({"foreground", "background", "legacy"} - present)))
    return result, splash


# ---- masks (unit square, size px) ----

def mask(shape, size):
    """A launcher mask of the visible 72 dp area, drawn 4x and reduced for smooth edges."""
    big = size * 4
    m = Image.new("L", (big, big), 0)
    d = ImageDraw.Draw(m)
    if shape == "circle":
        d.ellipse((0, 0, big - 1, big - 1), fill=255)
    elif shape == "rounded square":
        d.rounded_rectangle((0, 0, big - 1, big - 1), radius=int(big * 0.18), fill=255)
    elif shape == "teardrop":
        d.ellipse((0, 0, big - 1, big - 1), fill=255)
        d.rectangle((big // 2, big // 2, big - 1, big - 1), fill=255)
    else:  # squircle: superellipse |x|^5 + |y|^5 = 1
        r = big / 2.0
        points = []
        for k in range(720):
            t = 2 * math.pi * k / 720
            c, s = math.cos(t), math.sin(t)
            points.append((r + r * math.copysign(abs(c) ** 0.4, c), r + r * math.copysign(abs(s) ** 0.4, s)))
        d.polygon(points, fill=255)
    return m.resize((size, size), Image.LANCZOS)


def adaptive(layers, size, shape, monochrome_tint=None):
    """The adaptive icon at `size` px of visible area (72 dp): both layers at 108 dp, cropped to 72 dp, masked."""
    full = round(size * LAYER_DP / VISIBLE_DP)
    if monochrome_tint:
        mono = resample(layers["monochrome"]["xxxhdpi"], full)
        tinted = Image.new("RGBA", mono.size, monochrome_tint[0] + (255,))
        tinted.putalpha(mono.getchannel("A"))
        canvas = Image.new("RGBA", mono.size, monochrome_tint[1] + (255,))
        canvas.alpha_composite(tinted)
    else:
        canvas = resample(layers["background"]["xxxhdpi"], full)
        canvas.alpha_composite(resample(layers["foreground"]["xxxhdpi"], full))
    off = (full - size) // 2
    return masked(canvas.crop((off, off, off + size, off + size)), shape)


def masked(image, shape):
    """The image with its alpha multiplied by the mask."""
    out = image.copy()
    out.putalpha(ImageChops.multiply(image.getchannel("A"), mask(shape, image.width)))
    return out


def label(image, text, xy, fill=(255, 255, 255, 255)):
    ImageDraw.Draw(image).text(xy, text, fill=fill)


def masks_sheet(layers):
    shapes = ["circle", "squircle", "rounded square", "teardrop"]
    sizes = [48 * 3, 288]  # a 48 dp launcher icon at xxhdpi, and large
    width = 20 + (max(sizes) + 20) * (len(shapes) + 2)
    sheet = Image.new("RGBA", (width, 60 + sum(s + 50 for s in sizes)), PREVIEW_BG + (255,))
    y = 20
    for size in sizes:
        x = 20
        for shape in shapes:
            sheet.alpha_composite(adaptive(layers, size, shape), (x, y))
            label(sheet, shape, (x, y + size + 5))
            x += max(sizes) + 20
        sheet.alpha_composite(resample(layers["legacy"]["xxxhdpi"], size), (x, y))
        label(sheet, "legacy (Android 5-7)", (x, y + size + 5))
        x += max(sizes) + 20
        if "monochrome" in layers:
            sheet.alpha_composite(adaptive(layers, size, "circle", monochrome_tint=((0x1F, 0x2D, 0x3A), (0xC8, 0xE6, 0xFF))), (x, y))
            label(sheet, "themed (Android 13+)", (x, y + size + 5))
        y += size + 50
    return sheet


def keylines_sheet(layers):
    size = 432 * 2
    canvas = resample(layers["background"]["xxxhdpi"], size)
    canvas.alpha_composite(resample(layers["foreground"]["xxxhdpi"], size))
    d = ImageDraw.Draw(canvas)
    unit = size / LAYER_DP
    v0, v1 = (LAYER_DP - VISIBLE_DP) / 2 * unit, (LAYER_DP + VISIBLE_DP) / 2 * unit
    s0, s1 = (LAYER_DP - SAFE_DP) / 2 * unit, (LAYER_DP + SAFE_DP) / 2 * unit
    d.rectangle((v0, v0, v1, v1), outline=(255, 0, 255, 255), width=3)
    d.ellipse((s0, s0, s1, s1), outline=(0, 255, 255, 255), width=3)
    label(canvas, "magenta: 72 dp visible area   cyan: 66 dp safe circle   outside magenta: bleed", (10, 10), (255, 0, 255, 255))
    return canvas


def similar(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b)) <= 3 * 24 ** 2


def splash(layers, colour, icon_background, scale=3):
    """Approximation of SplashscreenContentDrawer on a 411x891 dp phone at xxhdpi (3x)."""
    w, h = 411 * scale, 891 * scale
    screen = Image.new("RGBA", (w, h), colour + (255,))
    if not layers:
        return screen
    bg = layers["background"]["xxxhdpi"].convert("RGB")
    flat = bg.getcolors(1) is not None
    if icon_background is None and flat and similar(bg.getpixel((0, 0)), colour):
        # Only the foreground is drawn: 108 dp of layer in a 288 dp view, a 192 dp circle visible.
        view, visible = 288 * scale, 192 * scale
        layer = resample(layers["foreground"]["xxxhdpi"], view)
    else:
        # The whole masked icon: 240 dp view, 160 dp circle, on the optional icon background disc.
        view, visible = 240 * scale, 160 * scale
        layer = resample(layers["background"]["xxxhdpi"], view)
        layer.alpha_composite(resample(layers["foreground"]["xxxhdpi"], view))
    off = (view - visible) // 2
    icon = layer.crop((off, off, off + visible, off + visible))
    x, y = (w - visible) // 2, (h - visible) // 2
    if icon_background is not None:
        screen.alpha_composite(masked(Image.new("RGBA", (visible, visible), icon_background + (255,)), "circle"), (x, y))
    screen.alpha_composite(masked(icon, "circle"), (x, y))
    return screen


def main(argv=None):
    parser = argparse.ArgumentParser(description="Export the launcher icon art to every density and draw previews.")
    parser.add_argument("--art", default=os.path.join(REPO, "art", "android"), help="art folder (default art/android)")
    parser.add_argument("--out", default=os.path.join(REPO, "art", "previews"), help="preview folder (default art/previews, git-ignored)")
    parser.add_argument("--install", action="store_true", help="also write the density exports into <art>/icon/<density>/")
    args = parser.parse_args(argv)
    try:
        layers, splash_settings = load_layers(args.art)
        if not layers and not splash_settings:
            raise ArtError("no art in %s: nothing to export (see art/android/README.md)" % args.art)
        os.makedirs(args.out, exist_ok=True)
        written = []
        for layer, images in layers.items():
            for name, image in images.items():
                folder = os.path.join(args.out, "densities", name)
                os.makedirs(folder, exist_ok=True)
                path = os.path.join(folder, LAYERS[layer][2])
                image.save(path, optimize=True)
                written.append(path)
                if args.install:
                    target = os.path.join(args.art, "icon", name)
                    os.makedirs(target, exist_ok=True)
                    shutil.copyfile(path, os.path.join(target, LAYERS[layer][2]))
        if layers:
            masks_sheet(layers).save(os.path.join(args.out, "masks.png"))
            keylines_sheet(layers).save(os.path.join(args.out, "keylines.png"))
            written += [os.path.join(args.out, "masks.png"), os.path.join(args.out, "keylines.png")]
        light = color(splash_settings.get("background"), "splash.background") or STOCK_SPLASH
        dark = color(splash_settings.get("backgroundNight"), "splash.backgroundNight") or light
        icon_bg = color(splash_settings.get("iconBackground"), "splash.iconBackground")
        icon_bg_night = color(splash_settings.get("iconBackgroundNight"), "splash.iconBackgroundNight") or icon_bg
        splash(layers, light, icon_bg).save(os.path.join(args.out, "splash-light.png"))
        splash(layers, dark, icon_bg_night).save(os.path.join(args.out, "splash-dark.png"))
        written += [os.path.join(args.out, "splash-light.png"), os.path.join(args.out, "splash-dark.png")]
    except ArtError as e:
        sys.stderr.write("error: %s\n" % e)
        return 1
    exports = [p for p in written if os.sep + "densities" + os.sep in p]
    print("wrote %d density exports to %s" % (len(exports), os.path.join(args.out, "densities")))
    for path in written:
        if path not in exports:
            print("wrote " + path)
    if args.install:
        print("installed the density exports into %s (they replace the patcher's own resampling)" % os.path.join(args.art, "icon", "<density>"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
