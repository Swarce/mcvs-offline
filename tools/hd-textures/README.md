# tools/hd-textures

The desktop side of the HD texture work ([docs/research/pc-textures.md](../../docs/research/pc-textures.md)). Runs on the
player's computer (net8). It reads only the player's own files and never downloads or redistributes game data.

Today it has one command, `probe`: the WP1 delivery probe. It answers the open question U2 of the report, whether the
engine's zip search path is searched before `Data/` and which entry names it looks up, with one device run.

## probe

```sh
# 1. Get your own copy of the probe texture from the phone (one file, about 40 KB on the ETC pack):
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data/8/dbd2f1.f

# 2. Build the zip (or --data <a copied Data folder> instead of --file):
dotnet run --project tools/hd-textures -- probe --file dbd2f1.f --out hd-textures.zip

# 3. Put it where the embedded host looks (game closed):
adb shell mkdir -p /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/hd
adb push hd-textures.zip /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/McvsOffline/hd/hd-textures.zip
```

The zip holds `3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga` (Data file `8/dbd2f1.f`, the crc32 of that
path) three times, each recoloured to one solid colour, plus a manifest `mcvs-hd.json`:

| entry | naming | colour (default) | colour (`--raw`) |
|---|---|---|---|
| `3d/Environment/01_WorkTextures/ENV_GenericConcrete05_bc.tga` | logical | magenta | yellow |
| `8/dbd2f1.f` | hashed (as in `Data/`) | green | cyan |
| `Data/8/dbd2f1.f` | hashed under `Data/` | blue | orange |

Each copy keeps the original's format (ASTC 6x6 on the S22's ASTC pack, ETC1 on the ETC pack), size, mip chain and KTX
key/value data; only the blocks change (ASTC void-extent blocks, flat ETC1 blocks), so no encoder is needed. By default the
entries are GCBF/LZ4-wrapped like the files in `Data/`; `--raw` stores bare KTX (other colours) for the second question of
the report (U3: does the loader take an unwrapped KTX). Entries are stored, 4-byte aligned, no ZIP64.

What the phone does with it, and how to read the result: [docs/STATUS.md](../../docs/STATUS.md), "HD texture probe".

Options: `--texture <logical path>` picks another texture (it must be ASTC or ETC1 without split alpha; the split-alpha
zips of the ETC pack are refused), `--out <file>` (default `hd-textures.zip`).

The zip contains a recoloured copy of a game file: keep it on your own phone, do not share it.
