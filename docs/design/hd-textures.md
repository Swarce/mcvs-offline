# Design: HD textures from the PC build (route A)

Status: **converter implemented** (`tools/hd-textures`, command `build`); the phone side (the installer that puts the files
into `Data/`) is a separate package and reads this document. This file is the contract between the two: the pack file,
its manifest and the rules the installer can rely on. Change it only together with both sides.

Background: [research/pc-textures.md](../research/pc-textures.md) (formats, the 2x gap, the scope tiers, the "Owner's phone:
ETC pack" section) and [INSTALL-OFFLINE.md](../INSTALL-OFFLINE.md) (the Java installer's index and its size check).

## 1. Decisions (device test of 2026-10-28, owner's Galaxy S22, build 10)

* **Route A.** Replacement files written into `files/Data/<h>/<rest>.f` show in a match (13 recoloured ETC1 files, green
  floors in Port). Route B (an override zip on the engine's zip search path) does not reach the renderer and is reverted.
* **ETC1 for the ETC pack.** The owner's phone runs the ETC pack (`google_lowres_v162074_Nexus_9_ETC`), whose shaders keep
  `SPLIT_ALPHA`. The converter writes exactly the shape of the file it replaces: a GCBF-wrapped ETC1 KTX, or a split-alpha
  zip (`SPLIT` + `rgb.ktx` + `alpha.ktx`). ASTC output is not implemented; the converter refuses an ASTC `Data/` folder.
* **Scope.** Map surfaces, lightmaps (every lightmap of a level together), third-person characters and grenades/equipment
  (research §7 tiers 1 and 2, about 1,750 files). Never: UI (`Menus/`), first-person weapons and arms, IBL cubemaps.
* **Legal model.** The converter runs on the player's computer and reads the player's own Steam install and the player's
  own `Data/` folder (pulled from the phone). The repository ships only our own table of hashes, sizes, categories and
  levels (`tools/hd-textures/hd-index.tsv`), no game data and no logical paths. The pack it writes contains derived game
  data: it stays on the player's computer and phone and is never shared.

## 2. The pack file

`mcvs-hd-<scope>.zip`, for example `mcvs-hd-default.zip`, `mcvs-hd-default-Port.zip` (default scope, Port only),
`mcvs-hd-lightmaps.zip`.

* A plain zip: every entry **stored** (method 0), no data descriptors, no ZIP64, no encryption, no comment.
* Entry data starts on a **4-byte boundary** (a zipalign-style extra field, id `0xD935`, pads the local header), so the
  phone can copy an entry straight out of the file.
* Names are ASCII. At most 65,535 entries; the whole file stays below 4 GiB (the converter refuses more).
* Entries, in this order:
  1. one per replaced file: `Data/<h[0]>/<h[1:]>.f`, exactly the path of the file it replaces under the game's
     `files/` folder (`h` = lower-case hex CRC-32 of the logical path, unpadded; INSTALL-OFFLINE.md, report 11 §2). The
     entry's bytes are the complete new file. Its zip CRC-32 equals `replacement.crc32` in the manifest.
  2. `mcvs-hd.json`, the manifest (§3), last.

What a replacement file is (§4): the same container and layout as the original, the same texture format (ETC1), the
same KTX header fields and key/value data, at the PC resolution and with a mip chain that ends at the same smallest size
as the original's. A 1-level original (every lightmap, a few emissive surfaces) stays 1 level.

## 3. The manifest `mcvs-hd.json`

UTF-8 JSON, one object:

```json
{
  "format": 1,
  "kind": "hd-textures",
  "tool": "McvsOffline.HdTextures 1",
  "created": "2026-10-28T20:15:00Z",
  "gameVersion": "1.17.32",
  "pack": "ETC",
  "scope": "default",
  "categories": ["surfaces", "lightmaps", "characters", "equipment"],
  "maps": ["Port"],
  "files": [
    {
      "entry": "Data/1/9921a.f",
      "hash": "0019921a",
      "logicalPath": null,
      "category": "equipment",
      "levels": [],
      "group": null,
      "layout": "ktx",
      "original": { "size": 457642, "crc32": "8c1f0e2a", "width": 1024, "height": 1024, "mipLevels": 11 },
      "replacement": { "size": 1712345, "crc32": "0b7d33e1", "width": 2048, "height": 2048, "mipLevels": 12 },
      "psnr": 27.0,
      "originalPsnr": 24.4,
      "alphaPsnr": null
    }
  ],
  "totals": { "files": 1, "originalBytes": 457642, "replacementBytes": 1712345 }
}
```

| field | meaning |
|---|---|
| `format` | `1`. A reader refuses a higher number. |
| `kind` | always `"hd-textures"`. |
| `gameVersion` | the data pack version the originals belong to (`1.17.32`). |
| `pack` | the texture pack the replacements are for: `"ETC"`. The installer refuses the pack on a phone whose `Data/` is another pack (ASTC); the `original` checks below catch it as well. |
| `scope`, `categories`, `maps` | what the player asked for. `maps` lists the level names (`Port`, `Desert`, ...); every level when none was chosen. |
| `files[].entry` | the zip entry, which is also the path under the game's `files/` folder. |
| `files[].hash` | the 8-digit lower-case hex CRC-32 of the logical path (the same number the entry name spells unpadded). |
| `files[].logicalPath` | the logical path when the converter knows it (lightmaps: built from the level name and checked against the hash), else `null`. Informational only. |
| `files[].category` | `surfaces`, `lightmaps`, `characters`, `equipment`, or with `--scope all`: `props`, `vfx`, `menus3d`, `minimaps`. |
| `files[].levels` | level names whose files reference this texture (surfaces: may be several; lightmaps: exactly one); empty for level-independent textures. |
| `files[].group` | `null`, or a name shared by files that must be installed and removed **together, all or none**: `"lightmaps:<Level>"` for every lightmap file of a level (the engine loads a level's lightmaps into one texture array; research R7). |
| `files[].layout` | `"ktx"` (a GCBF-wrapped KTX) or `"split"` (a split-alpha zip). |
| `files[].original` | the Android file this entry replaces, as it was in the `Data/` folder the converter read: `size` in bytes, `crc32` (8 lower-case hex digits), texture `width`, `height`, `mipLevels`. |
| `files[].replacement` | the new file: `size`, `crc32`, `width`, `height`, `mipLevels`. |
| `files[].psnr` | the converter's self-check: PSNR in dB of the top mip level (RGB, 8 bit) of the replacement, decoded, against the PC texture it came from (99 when identical). |
| `files[].originalPsnr` | for comparison: the PSNR of the Android original's top level against the PC texture's mip level of the same size, i.e. the quality the phone has today. |
| `files[].alphaPsnr` | split layout: the self-check of the alpha texture against the PC alpha; `null` for the ktx layout. |
| `totals` | the number of files and the summed sizes. |

CRC-32 everywhere is the standard one (zlib, `java.util.zip.CRC32`: polynomial `0xEDB88320`, initial value 0) over the
whole file as it is stored in `Data/`. Sizes are bytes on disk.

## 4. Rules the installer can rely on

1. **Only replacements.** Every entry replaces a file that exists in the 1.17.32 ETC pack at the same path. No entry adds
   or removes a file, so the pack's file list (15,747 files) does not change; only sizes and CRCs do.
2. **Check before replacing.** A phone file whose size or CRC-32 differs from `original` is not the file the converter
   read (another pack, a damaged file, an earlier HD install): the installer leaves it alone, and with it the whole
   `group` it belongs to.
3. **Groups are atomic.** All files of a `group` are installed, or none; the same on removal.
4. **Undo is a byte copy.** Restoring the original file (from a backup, the player's mirror or the phone's own copy)
   undoes a replacement completely; nothing else on the phone was touched by the pack.
5. **Sizes.** Replacements are larger than the originals (about 3 to 4 times for a 2x texture); files over 2 MiB are the
   rule for lightmaps (up to about 22 MB), so the installer index's split-piece rules (INSTALL-OFFLINE.md, points 3-4)
   apply to them.

How the installer keeps the Java installer's index consistent (`pack.info`, the split-piece journal,
`SKIP_VALIDATION`), where it keeps backups and how the player removes the pack is the installer package's design, not
part of this contract.

## 5. What the converter does (`tools/hd-textures build`)

For each texture of the chosen scope (from `hd-index.tsv`), only where the PC texture is larger than the Android one:

1. Read the Android original (`Data/<h>/<rest>.f`): GCBF → KTX, or a split zip (each part GCBF → KTX). Refuse anything
   that is not ETC1 (`0x8D64`) or the ETC pack's layouts.
2. Read the PC file (`Data/<decimal crc>.f`, GCBF → DDS DXT1/DXT5) and decode every mip level (BC1/BC3, our own decoder).
   Levels the PC file lacks are box-filtered from the level above.
3. Encode each level to ETC1 at the PC size (our encoder: both subblock orientations, individual and differential
   modes, all eight tables, least-squares base refinement; squared RGB error). The number of levels is the original's
   plus one per doubling, so the chain ends at the same size as the original's; a 1-level original stays 1 level (the
   engine samples lightmaps without mips, `LinearMipNone`, so a second level would only cost a quarter more texels).
4. Write the KTX with the original's header (format, `glBaseInternalFormat`, key/value data such as
   `KTXorientation S=r,T=d` and `CRNLIB_FOURCC`) and the new size and level count. Split-alpha textures get the same zip
   layout as the original (`SPLIT` = 4 zero bytes, `rgb.ktx`, `alpha.ktx`; stored, no alignment, as Gameloft wrote them),
   alpha as an ETC1 grey texture (R = G = B = alpha). Every KTX the original wraps in GCBF is wrapped in GCBF (8 LZ4
   blocks, the engine writer's split).
5. Decode the result and compute the PSNR against the PC texture, and the original's PSNR against the PC texture at its
   size; flag textures below `--min-psnr` (default 24 dB: ETC1's one modifier for all three channels limits noisy
   roughness and normal maps to about 24-30 dB, and the shipped ETC pack scores lower still on those).
6. Add the file to the pack (§2) and its row to the manifest (§3).

Lightmaps are taken per level, all files of a level or none (a level whose lightmaps cannot all be converted gets none).
