# tools/hd-textures

The desktop side of the HD texture work: it turns the textures of the PC (Steam) build into sharper replacements for the
phone's ETC data pack (`build`), or into a whole ASTC data pack for ASTC phones (`astc-pack`). Runs on the player's computer (net8). It reads only the player's own files (the Steam install and
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

## astc-pack: the full ASTC data pack (ASTC phones)

The ETC packs above only fit the ETC data pack. A phone whose GPU has ASTC (the Galaxy S22 and every recent phone) is
better served by Gameloft's **ASTC** data pack, whose shaders read one RGBA texture (no split alpha), so the HD textures
for it are ASTC too and come as a **whole data pack**: `astc-pack` takes Gameloft's ASTC pack (a mirror made with
`tools/cdn-mirror --tex ASTC --extract`, or a backup of an ASTC phone's `files/`) and the Steam install, and writes the
complete `files/` tree in which every texture the PC has larger (all categories: surfaces, lightmaps, characters,
equipment, props, vfx, menus3d, minimaps; 2,215 files) is the PC texture re-encoded to ASTC at the PC resolution and
every other file is Gameloft's own, unchanged. Installing it on the phone: [docs/INSTALL-OFFLINE.md](../../docs/INSTALL-OFFLINE.md),
"The ASTC HD data pack".

```sh
# Gameloft's ASTC pack, once (about 1.5 GB download, 2 GB extracted):
python3 tools/cdn-mirror/mirror.py --out ~/mcvs-mirror --tex ASTC --extract
# The pack (about an hour on 4 cores; needs about 5 GB free: 3.4 GB of parts, 1.4 GB of work files while it runs):
dotnet run -c Release --project tools/hd-textures -- astc-pack --astc-files ~/mcvs-mirror/1.17.32/ASTC/files \
  --pc "D:\SteamLibrary\steamapps\common\Modern Combat Versus" --astcenc ~/astcenc/bin/astcenc-avx2 --out astc-hd/
#    -> astc-hd/mcvs-astc-hd-01-of-10.zip ... -10-of-10.zip, SHA256SUMS.txt, astc-hd-report.txt
```

| option | meaning |
|---|---|
| `--astc-files` | Gameloft's ASTC pack: the `files/` folder holding `Data/` and `McvsOffline/install/pack.info` (or `pack.info` next to `Data/`); default `MCVS_ASTC_MIRROR`. Only read |
| `--pc` | the Steam game folder or its `Data/` folder (default: the Steam install, or `MCVS_PC_INSTALL`) |
| `--astcenc` | the astcenc binary (below); default `MCVS_ASTCENC`, then `astcenc-avx2` / `astcenc-sse4.1` / `astcenc-sse2` / `astcenc-neon` / `astcenc` next to the tool, in an `astcenc/` folder beside it, or on PATH |
| `--out` | the folder for the upload parts |
| `--blocks` | block size (and quality) per category over the defaults, for example `surfaces=4x4,lightmaps=6x6:medium` or `all=6x6` |
| `--quality` | the astcenc preset for every category: `fastest`, `fast`, `medium`, `thorough` (default), `verythorough`, `exhaustive` |
| `--work` | the resumable work folder (default `<out>/work`): finished textures and `journal.tsv` |
| `--keep-work` | keep the work folder after the parts are written (to write the parts again without encoding) |
| `--encode-only` | encode, write no parts (a run spread over several sessions) |
| `--part-size` | MiB per part (default 380; each part stays below it) |
| `--threads` | parallel astcenc calls (default: every core; each call `-j 1`) |
| `--dry-run` | only the scope, the texels to encode and the GPU bytes before and after |

**Resumable.** Each finished texture is written to `<work>/done/<hash>.f` and gets a line in `<work>/journal.tsv`; a run
that is interrupted (closed window, power cut) continues where it stopped when started again with the same options. A
texture finished with another block size or quality than asked now is encoded again. The parts are written only after
every texture is done (a few minutes), each part first as `.partial`.

**What it writes**, per texture (pc-textures.md §2.2, the 2,215 files of `hd-index.tsv` where the PC is larger):
* every PC mip level decoded (BC1/BC3) and encoded by `astcenc -cl` (or `-cs` for an sRGB original; the pack is linear,
  `0x93B4`) at the PC size in the category's block size, one call per level;
* the KTX with the original's header fields (glType, glTypeSize, glFormat, glBaseInternalFormat `0x1908`, no key/value
  data), the new width, height, level count and ASTC format (`0x93B0` 4x4, `0x93B2` 5x5, `0x93B4` 6x6);
* the chain ends at the same smallest size as the original's (a full chain stays a full chain to 1x1, which GL ES needs
  for a complete texture: one level more than the original per doubling), a 1-level original (lightmaps, a few
  surfaces) stays 1 level;
* alpha: the original's alpha is decoded; where it is opaque everywhere the replacement is made opaque too (alpha 255,
  so astcenc spends no bits on it), otherwise the PC alpha is kept (DXT5 alpha, DXT1 punch-through);
* GCBF around it as the original (eight LZ4 blocks of equal size, as every texture of the pack);
* five originals are not a power of two (85x85, 73x66, 105x120, 200x200, 1024x778: the ASTC pack keeps the artist's size
  where the ETC pack and the PC stretched the picture to a power of two): the PC texture is taken when it is larger in
  both directions and the same picture (both reduced to at most 64 a side, PSNR of RGB at least 24 dB; otherwise
  `failed ... is not the original ... stretched` and Gameloft's file stays), with a full chain at the PC size
  (design/hd-textures.md §6, "Stretched originals"). A power-of-two original whose PC texture is not a power-of-two
  multiple of it is refused as before;
* a self-check: the top level decoded again (`astcenc -dl`) and compared with the PC texture (PSNR of RGB and alpha), next
  to Gameloft's own 6x6 at the phone's size enlarged bilinearly to the PC size (what the phone shows today there).

A level's lightmaps are one unit (one texture array in the engine): all are replaced, or none.

**The parts.** Stored zips below 380 MiB each (no compression: ASTC hardly compresses and the phone copies them
straight), entries `files/Data/<h>/<rest>.f` in path order; the last part also holds:

| entry | what |
|---|---|
| `files/McvsOffline/install/pack.info` | the installer's index of exactly these files: Gameloft's ASTC index with each replaced file's entries replaced (`PackIndex.Replace` of hosts/embedded, the HD installer's code): its size, the CRC32 of the file or of each 2 MiB piece, a file over 2 MiB split as Gameloft's packer does. Every other entry stays byte for byte |
| `files/McvsOffline/install/pack.info.txt` | a note on what the index is |
| `files/McvsOffline/install/replaced.tsv` | the replaced files: path, size, CRC32, Gameloft's size and CRC32 (the offline build recognises the pack by it) |
| `files/McvsOffline/install/astc-hd.json` | per texture: category, block size, sizes, levels, GPU bytes before and after, PSNRs |
| `README.txt` | the numbers of this pack and the install steps |

Next to the parts: `SHA256SUMS.txt` (`sha256sum -c SHA256SUMS.txt`) and `astc-hd-report.txt` (per category: GPU bytes,
disk bytes and PSNR before and after; per map: the map's own textures). Every file going into a part is checked first:
Gameloft's files against every CRC32 of Gameloft's index, the replacements against the new index.

### Block sizes (measured)

`astc-measure` encodes a few textures per category at each block size and prints PSNR against the PC texture, memory and
time; the defaults come from this run (6 textures per category, `-thorough`, astcenc 5.7.0 avx2, 4 threads on this
repository's build machine; PSNR of RGB in dB against the PC texture at the PC size, median; "Gameloft" is the ASTC pack's
6x6 at the phone's size enlarged to the PC size; GPU MB is the whole category):

| category | 4x4 | 5x5 | 6x6 | Gameloft | GPU MB at 4x4 / 5x5 / 6x6 (Gameloft) | default |
|---|---|---|---|---|---|---|
| surfaces (953) | 60.6 | 40.6 | 37.7 | 24.4 | 490 / 317 / 221 (56) | **5x5** |
| lightmaps (63) | 60.9 | 46.5 | 42.6 | 28.6 | 880 / 565 / 392 (98) | **5x5** |
| characters (705) | 59.1 | 40.1 | 37.2 | 27.2 | 459 / 296 / 206 (52) | **5x5** |
| equipment (33) | 65.4 | 45.9 | 42.3 | 27.1 | 63 / 41 / 28 (7) | **4x4** |
| props (36) | 63.4 | 43.3 | 40.3 | 29.0 | 68 / 44 / 30 (8) | **4x4** |
| vfx (386) | 52.6 | 46.6 | 43.9 | 33.7 | 63 / 41 / 29 (7) | **5x5** |
| menus3d (32) | 65.9 | 52.0 | 46.5 | 34.7 | 95 / 61 / 43 (11) | **5x5** |
| minimaps (7) | 64.8 | 55.0 | 50.3 | 30.5 | 8 / 5 / 4 (1) | **4x4** |

Reading: every size beats Gameloft's file by 10 to 20 dB, because the phone's file lacks the PC's detail, not because of
its encoding. 4x4 is nearly lossless here (ASTC 4x4 can represent the PC's BC1/BC3 4x4 blocks almost exactly) but takes
2.25 times the memory of 6x6. 5x5 keeps 40-52 dB (no visible loss at phone pixel density) for 1.44 times 6x6, and is the
default for the large categories; 4x4 is the default where it costs a few MB (grenades and devices seen close up in
first person, props, minimaps). The 4096 lightmaps are smooth; 5x5 (46.5 dB) over 6x6 (42.6 dB) costs about 20 MB per
map. `-exhaustive` instead of `-thorough` gained 0.2-0.4 dB at 5x5 for three times the time, so the defaults stay
`-thorough` (whole pack about an hour on 4 cores; `-medium` would halve it).

### astcenc

ARM's ASTC encoder, [github.com/ARM-software/astc-encoder](https://github.com/ARM-software/astc-encoder), Apache License
2.0. The tool runs the command-line program; it is **not** part of this repository and is never committed or shipped
with a pack (the parts contain only ASTC data it produced). Get it one of two ways:

* **Release binaries:** the project's GitHub releases page has ready builds for Windows, macOS and Linux
  (`astcenc-<version>-<os>-x64.zip`, with `astcenc-avx2` and `astcenc-sse4.1`, and `astcenc-neon` for Arm). Unzip it and
  give `--astcenc <folder>/bin/astcenc-avx2` (or `astcenc-sse4.1` on CPUs without AVX2), or put `bin/` next to the tool.
* **Build it** (CMake 3.15+ and a C++14 compiler), as this repository's build machine did from the 5.7.0 tag:

  ```sh
  git clone --branch 5.7.0 --depth 1 https://github.com/ARM-software/astc-encoder.git
  cd astc-encoder && mkdir build && cd build
  cmake .. -DCMAKE_BUILD_TYPE=Release -DASTCENC_ISA_AVX2=ON -DASTCENC_ISA_SSE41=ON -DCMAKE_INSTALL_PREFIX=$HOME/astcenc
  cmake --build . --parallel && cmake --install .      # -> ~/astcenc/bin/astcenc-avx2, astcenc-sse4.1
  ```

Licence notice: astcenc is Copyright (c) 2011-2026 Arm Limited, licensed under the Apache License, Version 2.0
(`LICENSE.txt` in its source and release archives; http://www.apache.org/licenses/LICENSE-2.0). Anyone who passes the
binary on must include that licence. The textures it encodes are yours (from your own game files) and stay on your
computer and phone.

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
| `Astcenc.cs` | ARM's astcenc as a command-line tool: finding it, one call per level, `.astc` and uncompressed KTX in and out |
| `AstcConverter.cs` | one texture: PC DDS to the ASTC pack's file at the PC size, with the self-check |
| `AstcPack.cs`, `AstcPackBuilder.cs` | `astc-pack` and `astc-measure`: Gameloft's ASTC pack as a source, the resumable encode, the parts, their index (`PackIndex.cs` of hosts/embedded, linked), `replaced.tsv` |

Tests: `tests/McvsOffline.Embed.Tests/HdTextures` (synthetic files always; the real files with `MCVS_PC_INSTALL` and
`MCVS_DATAPACK`; the ASTC encoding with astcenc, `MCVS_ASTCENC` or on PATH, else skipped, `MCVS_REQUIRE_ASTCENC=1` fails
instead; the finished ASTC pack with `MCVS_ASTC_HD_OUT=<the folder of its parts>`).
