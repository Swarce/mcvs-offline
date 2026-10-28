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
  zip (`SPLIT` + `rgb.ktx` + `alpha.ktx`). The `build` converter refuses an ASTC `Data/` folder; ASTC phones get the
  whole ASTC data pack of §6 instead (owner request after build 11).
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

## 6. The full ASTC data pack (`tools/hd-textures astc-pack`)

For a phone on (or moving to) Gameloft's ASTC pack. Not an overlay like §2: the whole `files/` tree, because the ASTC and
ETC packs differ in their shaders (`#undef SPLIT_ALPHA` in the ASTC pack's `Shaders/Engine/PBR_*.glsl`), so ASTC
textures only work inside the ASTC pack. Input: Gameloft's 1.17.32 ASTC pack (`tools/cdn-mirror --tex ASTC --extract`,
only read) and the Steam install. Output: upload parts plus the installer index for exactly the files they hold.

**Scope.** Every row of `hd-index.tsv` (all eight categories, 2,215 files; the PC texture is larger in each); the rest
of the 15,747 files are Gameloft's ASTC files byte for byte. A level's lightmaps are replaced together or not at all.

**A replacement file** (checked against real files of the ASTC pack: all 2,215 originals are GCBF with eight equal LZ4
blocks around a KTX 1.1 with glType 0, glTypeSize 0, glFormat 0, glInternalFormat `0x93B4`, glBaseInternalFormat
`0x1908`, no key/value data, a full chain or one level):
* the original's 13 header words with the new width and height (the PC's), level count and glInternalFormat (the ASTC
  format of the category's block size, the linear range `0x93B0`-`0x93BD`, or the sRGB range if an original were sRGB);
  no key/value data, as the original;
* levels: a 1-level original stays 1 level; a chain gets one more level per doubling and ends at the same smallest size
  (1x1), so the texture stays mipmap-complete for GL ES;
* each level: the PC level decoded (BC1/BC3; a level the PC lacks box-filtered from the one above), encoded by ARM
  astcenc 5.x `-cl` with the category's block size and preset (`-thorough` by default); one RGBA texture, no split
  alpha; alpha 255 everywhere when the original's decoded alpha is opaque (at least 250 everywhere), else the PC alpha;
* wrapped in GCBF with eight equal LZ4 blocks, as the original.

**Default block sizes** (measured with `astc-measure`, tools/hd-textures/README.md "Block sizes"): 5x5 for surfaces,
lightmaps, characters, vfx and menus3d; 4x4 for equipment, props and minimaps; all `-thorough`. Every one beats
Gameloft's 6x6 file at the phone's size by 10-20 dB of PSNR against the PC texture.

**The parts.** `mcvs-astc-hd-NN-of-MM.zip`: plain zips, every entry stored, no data descriptors, no ZIP64, each file
below 380 MiB; entries `files/Data/<h[0]>/<h[1:]>.f` in ordinal path order; the last part also holds
`files/McvsOffline/install/pack.info`, `pack.info.txt`, `replaced.tsv`, `astc-hd.json` and `README.txt` (at the root).
`SHA256SUMS.txt` (sha256sum format) and `astc-hd-report.txt` lie next to the parts.

**`McvsOffline/install/pack.info`.** Gameloft's ASTC index with each replaced file's entries replaced by
`PackIndex.EntriesFor` + `PackIndex.Replace` (hosts/embedded/Install, the code the HD installer uses): the new size, the
CRC32 of the whole file or of each 2 MiB piece, a file over 2 MiB (and not an exact multiple) split into
`<name>.split_NNNN` pieces with the total after piece 1; the archive offsets and ZIP sizes of the original's pieces are
kept (they only matter for a CDN download). Everything else is Gameloft's bytes. It is a Gameloft-style index (non-zero
ZIP sizes), so the host restores it like any saved index and runs the background CRC check on it.

**`McvsOffline/install/replaced.tsv`.** UTF-8 text, `#` comments, then a header line `mcvs-replaced<TAB>1<TAB>ASTC<TAB>1.17.32`,
then one line per replaced file: `<path, e.g. Data/1/9921a.f><TAB><size><TAB><crc32, 8 hex><TAB><Gameloft's size><TAB><Gameloft's crc32>`.
The host (`InstallerData`, class `ReplacedFiles`) counts a listed file with Gameloft's size only while it has the listed
size, so the 1.17.32 ASTC pack (15,747 files, 2,042,882,427 bytes) is recognised; a reader refuses another format
number. `HdInstaller` uses the same list for its "which pack is this phone on" check.

**`McvsOffline/install/astc-hd.json`.** Informational: `format` 1, `kind` `astc-hd-data-pack`, `tool`, `astcenc`,
`created`, `gameVersion`, `pack` `ASTC`, `settings` (category to block size and preset), `totals`, and per file `path`,
`hash`, `category`, `levels`, `group`, `block`, `quality`, `original` and `replacement` (`size`, `crc32`, `width`,
`height`, `mipLevels`, `gpuBytes`), `opaque`, `psnr`, `alphaPsnr`, `originalPsnr` (at the phone's size) and
`originalUpscaledPsnr` (Gameloft's file enlarged bilinearly to the PC size).

**The host** (docs/INSTALL-OFFLINE.md, "The ASTC HD data pack"): when the installer's own index does not match the data
but the saved `McvsOffline/install/pack.info` does (a pushed pack over an old one), the saved index replaces the
installer's; the variant comes from `replaced.tsv`. No `SKIP_VALIDATION` is involved.
