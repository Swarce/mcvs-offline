# tools/art (owner: EMBED)

`export-icons.py` is the owner's helper for the launcher icon and launch-screen art in `art/android/` (the spec for the
artist is [art/android/README.md](../../art/android/README.md)). It needs Pillow and is never run by the build: the
patcher validates the art and resamples the masters itself.

```sh
# every density plus previews (masks, keylines, launch screen light/dark) into art/previews/ (git-ignored)
python3 -I tools/art/export-icons.py

# another art folder or preview folder
python3 -I tools/art/export-icons.py --art my-art --out /tmp/previews

# also copy the Lanczos density exports into art/android/icon/<density>/, where they replace the patcher's
# area-average resampling of the masters (hand-tune them there if needed)
python3 -I tools/art/export-icons.py --install
```

The launch-screen preview follows AOSP's `SplashscreenContentDrawer` only approximately (view sizes 288/240 dp, the
"similar colour" test); the real check is a phone. To see exactly what the patcher would write, run it with
`--art art/android --dry-run`. Never commit previews, and never put Gameloft's icons into `art/`.
