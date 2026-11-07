#!/usr/bin/env python3
"""Owner-side helper: the game's data installer background (art/android/README.md section 5). Needs Pillow.

From a landscape picture with the game's logo on a dark band in its lower-right corner (as Gameloft's "Marketing
Detail B" key art), it moves the logo up onto the band so a wide phone keeps it, puts a sticker (the "encore" logo)
under it, and writes, into OUT:

    installing_splash_bg_left.png, installing_splash_bg_right.png   512 x 768 each (together 1024 x 768)
    installing_splash_bg.png                                        800 x 600

and, with PREVIEW, what a 2340 x 1080 phone shows (each half centre-cropped, side by side). Keep the preview out of the
--art-extra folder: the patcher refuses files it does not know.

The picture fills the width; above and below it a blurred mirror of its edges fills the 4:3 canvas. The logo box, the
band's edge and the move are the values measured on Marketing Detail B (3000 x 1504); change them for another picture.
The output is not committed: keep it in art/local/installer/ (git-ignored) and give art/local to the patcher with
--art-extra.

Usage: python3 -I tools/art/installer-background.py PICTURE STICKER OUT [PREVIEW.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

LOGO_BOX = (2128, 1254, 2506, 1458)      # the logo on the band, in picture pixels
EDGE = lambda x: 1213 - (x - 1900) * 0.262   # the band's upper edge (y at column x)
MOVE = (300, -150)                        # where the logo goes, relative to its place
STICKER_WIDTH = 0.66                      # of the logo's width; its top at 60 % of the logo's height (under VERSUS)


def main(picture, sticker, out, preview_path=None):
    src = Image.open(picture).convert('RGB')
    a = np.asarray(src).astype(np.float32)
    h0, w0 = a.shape[:2]
    yy, xx = np.mgrid[0:h0, 0:w0]
    x0, y0, x1, y1 = LOGO_BOX
    on_band = (yy > EDGE(xx) + 12)[y0:y1, x0:x1]
    logo = a[y0:y1, x0:x1].copy()
    logo[~on_band] = 0
    b = a.copy()
    # the old logo painted out with a patch of the same band just right of it (its own grain: no darker ghost)
    region = b[y0:y1, x0:x1]
    region[on_band] = a[y0:y1, x1:x1 + (x1 - x0)][on_band]
    floor = 10
    lit = np.clip((logo - floor) / (255 - floor), 0, 1)      # screen blend: the band's black is transparent
    nx, ny = x0 + MOVE[0], y0 + MOVE[1]
    under = b[ny:ny + lit.shape[0], nx:nx + lit.shape[1]] / 255
    b[ny:ny + lit.shape[0], nx:nx + lit.shape[1]] = (1 - (1 - under) * (1 - lit)) * 255
    art = Image.fromarray(np.clip(b, 0, 255).astype(np.uint8))
    lw, lh = x1 - x0, y1 - y0
    stick = Image.open(sticker).convert('RGBA')
    sw = round(lw * STICKER_WIDTH)
    stick = stick.resize((sw, round(stick.height * sw / stick.width)), Image.LANCZOS)
    art.paste(stick, (nx + (lw - sw) // 2 + 8, ny + round(lh * 0.60)), stick)

    w, h = 2048, 1536                     # 2 x the 1024 x 768 of the two halves
    ah = round(h0 * w / w0)
    top = (h - ah) // 2
    pic = art.resize((w, ah), Image.LANCZOS)
    canvas = Image.new('RGB', (w, h))
    upper = pic.crop((0, 0, w, top)).transpose(Image.FLIP_TOP_BOTTOM).filter(ImageFilter.GaussianBlur(24))
    lower = pic.crop((0, ah - (h - top - ah), w, ah)).transpose(Image.FLIP_TOP_BOTTOM).filter(ImageFilter.GaussianBlur(24))
    canvas.paste(upper, (0, 0))
    canvas.paste(lower, (0, top + ah))
    canvas.paste(pic, (0, top))

    os.makedirs(out, exist_ok=True)
    full = canvas.resize((1024, 768), Image.LANCZOS)
    left, right = full.crop((0, 0, 512, 768)), full.crop((512, 0, 1024, 768))
    left.save(os.path.join(out, 'installing_splash_bg_left.png'))
    right.save(os.path.join(out, 'installing_splash_bg_right.png'))
    canvas.resize((800, 600), Image.LANCZOS).save(os.path.join(out, 'installing_splash_bg.png'))

    if not preview_path:
        return

    def half(img):
        scale = 1170 / img.width
        hh = round(img.height * scale)
        r = img.resize((1170, hh), Image.LANCZOS)
        o = (hh - 1080) // 2
        return r.crop((0, o, 1170, o + 1080))
    preview = Image.new('RGB', (2340, 1080))
    preview.paste(half(left), (0, 0))
    preview.paste(half(right), (1170, 0))
    preview.save(preview_path)


if __name__ == '__main__':
    if len(sys.argv) not in (4, 5):
        sys.stderr.write(__doc__)
        sys.exit(2)
    main(*sys.argv[1:])
