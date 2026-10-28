# tools/hd-textures

The desktop side of the HD texture work: it turns the textures of the PC (Steam) build into sharper replacements for the
phone's ETC data pack. Runs on the player's computer (net8). It reads only the player's own files (the Steam install and
the `Data/` folder pulled from the phone) and never downloads or redistributes game data. The pack it writes holds
textures converted from those files: keep it for your own phone, do not share it.

Contract with the phone side (the pack file and its manifest): [docs/design/hd-textures.md](../../docs/design/hd-textures.md).
Background (formats, the 2x gap, the scope): [docs/research/pc-textures.md](../../docs/research/pc-textures.md).

## build

```sh
# 1. The phone's data pack (about 2 GB; the ETC pack, as on the owner's Galaxy S22):
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data phone-Data

# 2. Convert (Windows finds the Steam install itself; elsewhere give --pc <the game folder or its Data folder>):
dotnet run -c Release --project tools/hd-textures -- build --android-data phone-Data --out packs/
#    -> packs/mcvs-hd-default.zip
dotnet run -c Release --project tools/hd-textures -- build --android-data phone-Data --pc "D:\SteamLibrary\steamapps\common\Modern Combat Versus" --maps Port
#    -> mcvs-hd-default-Port.zip (Port's surfaces and lightmaps, plus the characters and equipment)
```

| option | meaning |
|---|---|
| `--android-data` | the phone's `Data/` folder (or the `files/` folder holding it) |
| `--pc` | the Steam game folder, its `Data/` folder or the exe; default: the Steam install (registry, `libraryfolders.vdf`, like `tools/pc-spike`) |
| `--scope` | `default` = `surfaces,lightmaps,characters,equipment` (research §7 tiers 1 and 2, 1,754 files); `all` adds `props,vfx,menus3d,minimaps`; or a comma list |
| `--maps` | levels for surfaces, lightmaps and minimaps: `Port` (Map01), `Favelas` (Map02), `Desert` (Map03), `Snow` (Map04), `Rooftop` (Map05), `DogDays` (Map06), `Stadium` (Map08), `Stadium2` (Map09), `Bootcamp`; default all |
| `--out` | the zip, or a folder for the default name `mcvs-hd-<scope>[-<maps>].zip` |
| `--min-psnr` | flag textures whose self-check is below this many dB (default 24) |
| `--threads` | default: every core |
| `--dry-run` | only list what the scope holds (no files needed) |

What it does per texture (only where the PC copy is larger; first-person weapons and arms, UI and IBL cubemaps never are
in scope): reads the phone's file (a GCBF-wrapped ETC1 KTX, or a split-alpha zip `SPLIT` + `rgb.ktx` + `alpha.ktx`), reads
the PC file (GCBF around a DDS DXT1/DXT5), decodes every mip level, encodes each to ETC1 at the PC size, writes the
same layout, KTX header fields and key/value data as the phone's file, wraps it the same way (GCBF in the pack's eight
blocks, LZ4), then decodes the result and prints its PSNR against the PC texture next to the phone file's own PSNR
against the PC texture at its size. A level's lightmaps are converted together, all or none.

Measured on this repository's build machine (4 vCPUs, Xeon 2.8 GHz, 5.5 Mtexel/s): the Port pack (`--maps Port`, 907
files, 1,067 Mtexel) takes about 200 s and is 252 MB; the whole default scope is 2,971 Mtexel, about 9 minutes there and
a few minutes on a desktop with more cores. Originals without mips (every lightmap, a few emissive surfaces) stay
without mips. Median PSNR against the PC textures 34 dB (5 % below 24 dB:
noisy roughness and normal maps, which ETC1's one modifier for all three channels cannot follow); per texture the
median is 3.4 dB above what the phone's own file scores against the PC texture at its size.

## The table (`hd-index.tsv`)

The tool carries our own table of the convertible textures: CRC-32 hash of the logical path, category, levels, the
Android layout and both files' sizes, no names and no content. Maintainers regenerate it from the decoded data pack of
report 11, the ETC `Data/` folder and the PC install:

```sh
dotnet run -c Release --project tools/hd-textures -- index --decoded /home/user/mcvs-work/datapack-decoded \
  --android-data /home/user/mcvs-work/datapack/1.17.32/ETC/files/Data --pc /home/user/mcvs-work/pc/install \
  --out tools/hd-textures/hd-index.tsv
```

Levels come from each level's own files (layers, `StaticGeo`, lightmaps), following the models and templates they name;
a level's `MapNN` alias is the kit folder its art uses most.

## Code

| file | what |
|---|---|
| `Etc1.cs`, `Etc1Encoder.cs` | ETC1 decoding (Khronos spec) and our encoder: both orientations, both modes, all tables screened; least-squares base refinement for the best tables |
| `Dds.cs` | DDS header and BC1/BC3 decoding |
| `Ktx.cs` | KTX 1.1 read/write |
| `GcbfWriter.cs`, `Lz4Compressor.cs` | the engine's GCBF container with LZ4 blocks |
| `StoredZipWriter.cs` | stored zips: aligned (the pack) and Gameloft's split-alpha shape |
| `TextureConverter.cs` | one texture: PC DDS to the phone file's layout at the PC size, with the self-check |
| `HdIndex.cs`, `IndexBuilder.cs`, `hd-index.tsv` | the table, its categories and levels, and its generator |
| `PackBuilder.cs`, `Program.cs` | the pack and its manifest; the command line |

Tests: `tests/McvsOffline.Embed.Tests/HdTextures` (synthetic files always; the real files with `MCVS_PC_INSTALL` and
`MCVS_DATAPACK`).
