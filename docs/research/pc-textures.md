# PC (Steam) textures on the phone: formats, the quality gap, and a port plan

Scope: can the Android build (APK 1.17.32 plus its data pack) use the higher-quality textures of the Steam PC build
(app 723780, depot 723781, build 6372049, March 2021)? This report covers the formats of the data packs on both sides, how
the game finds and reads a texture, the texture formats inside them, a comparison over every texture both builds share,
the worst offenders, and how a port would work on the owner's Samsung Galaxy S22 (Android 16). This is research only:
no repository code was changed.

Markers: **[V]** verified (a tool run over the real files, decompiled code, or native strings). **[I]** inferred and not
yet tested on a device. Every **[I]** that the plan relies on is repeated in §9 (unknowns) and is checked first in WP1.

Legal model: as with the APK, **the player supplies their own Steam files**. The tool runs on the player's own computer,
and nothing derived from the PC build is redistributed, committed or uploaded. Nothing in this report or in
`docs/research/` contains game data.

Inputs:
* Android ETC data pack `/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data` and its decoded tree
  `/home/user/mcvs-work/datapack-decoded` (report 11).
* ASTC pack index `captures/cdn/08_astc_header.bin` (report 10), plus **147 ASTC texture files and 3 shader files fetched
  from Gameloft's still-live CDN with HTTP range requests** (16.6 MB; the S22 uses the ASTC pack, see §2.2).
* PC install `/home/user/mcvs-work/pc/install` (`Data/` holds 15,921 numbered `.f` files, 3.5 GB).
* Decompiled Android C# `/home/user/mcvs-work/decompiled` (`GM/` Game, `EN/` Gameloft.Engine); arm64
  `libnativeengine.so` from `/home/user/mcvs-work/extracted/lib/arm64-v8a`.

Scripts and outputs (not committed; scratchpad
`/tmp/claude-0/-home-user-mcvs-offline/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad/pctex/`):
`scan_pc.py` (sniffs every PC file, giving `pc_scan.json`), `textab.py` (KTX/DDS header parser and the Android-to-PC
join, giving `tex_rows.json` with 7,568 rows), `astcidx.py` / `fetch_astc.py` / `fetch_one.py` (ASTC pack index and
sample fetch), `quality.py` / `astcq.py` (decode and PSNR, giving `quality.json`), `bdae_cmp.py` (model texture references).

---------------------------------------------------------------------------------------------------

## 0. Key findings

1. **The phone can use the PC textures, but they must be transcoded.** PC textures are DDS DXT1/DXT5 (BC1/BC3). Android
   GPUs (the S22's Adreno 730 included) have no BCn in GLES, so the engine's Android pack uses KTX with ETC1 or ASTC. The
   engine finds a texture by the CRC-32 of its logical path, and that path is **the same on both platforms**: PC
   `Data/<decimal>.f`, Android `Data/<hex[0]>/<hex[1:]>.f`. **7,502 of the 7,568 Android textures have a PC counterpart
   with the same hash**, and every sampled model references the same texture paths on both platforms. **[V]**
2. **The S22 gets the ASTC pack, and its format is known: KTX1, `GL_COMPRESSED_RGBA_ASTC_6x6_KHR` (0x93B4), one RGBA
   texture, full mip chain, GCBF/LZ4-wrapped.** It has the same resolution as the ETC pack, and its shaders turn
   `SPLIT_ALPHA` off. A transcoded PC texture written in exactly this form at the same path is something the engine
   already reads. **[V]** (147 ASTC files, §2.2)
3. **The gap is resolution, not encoding.** 2,583 of the 7,502 shared textures (34 %) are **2× wider and 2× higher on
   PC (4× the pixels)**. The other 4,919 have the same size. The 2× set is world and third-person content:
   * all 953 `3d/Environment` textures; 768 of them are **256² on the phone and 512² on PC**;
   * 724 of 863 character textures: third-person bodies, heads and jerseys;
   * all VFX, grenades and equipment, turrets, vehicles, 3D menu coins and minimaps;
   * the level lightmaps: **2048² on the phone, 4096² on PC**.

   First-person weapons (871 of 872), first-person arms and every UI image (`Menus/`) have the **same resolution** on
   both. At equal size, ASTC 6×6 is within 35-41 dB PSNR of the PC texture, so for those the PC version is barely
   better. **[V]**
4. **The PC's extra resolution is real detail, not an upscale.** For the 2× textures, PSNR between the PC texture and
   the same texture halved and re-enlarged is about 27-31 dB (median per category, §3.3), so a large part of the
   high-frequency detail is missing on the phone. **[V]**
5. **A second cause of the "ugly" look is the GPU profile, and it decides whether HD textures show at all.** No rule in
   `GameOptions_android.json` names the S22's GPU, so it falls to the catch-all `MidGPU` (STATUS.md; **[I]**: the match
   is evaluated natively). MidGPU compiles the PBR shader with `MID_END`:
   * **no normal mapping** (`#define NORMAL_MAPPING` is commented out);
   * `LIGHTMAP_LOW`;
   * the **low-end lightmaps** (`*.lowend.tga`, RGB only, no directional map);
   * the **low-end map layers** (`enableLowResMaps` true makes the game skip the `HighEndOnly` layers);
   * no shadow maps and no bloom.

   Under MidGPU, the 405 PC normal maps in the 2× set would change nothing. Gameloft's own `HighGPU` profile
   (shipped for Adreno 630/616/540 and Mali G72/G76 phones) turns all of this on, using data already on the phone.
   **[V]** code and data, **[I]** profile actually active.
6. **The cost on the S22 is moderate.** Porting every 2× texture at PC resolution in ASTC 6×6 grows its GPU size from
   **about 294 MB to about 1,169 MB** (the whole pack, not per match). By scope:

   | scope | now → at PC resolution (ASTC 6×6) |
   |---|---|
   | environment | 56 → 221 MB |
   | characters | 55 → 218 MB |
   | lightmaps | 98 → 392 MB (220 MB with 8×8) |

   A match loads one map and about 10 characters, so the estimate per match is **+150 to 250 MB of texture memory**
   **[I]**. The phone has 8 GB of RAM. Disk use on the phone is about **+0.7 to 0.9 GB**. Loading should take well
   under a second longer, since this is I/O plus an LZ4 decode **[I]**.
7. **Getting the files into the game is the hard part, not the transcoding.** Gameloft's Java installer checks the size
   of every `Data/` file against its index on each start and **deletes any file with a different size**
   (INSTALL-OFFLINE.md §"How the game's installer decides", point 3), so dropping bigger `.f` files into `Data/` does
   not survive the next start by itself. Two routes:
   * **(A)** replace the files **and** rewrite the installer index, the split-piece journal and our saved copy, with
     our existing install code (`hosts/embedded/Install/`);
   * **(B)** an override zip mounted by the engine's own zip search path (`GlfStream.PushZip_protected` → native
     `GlfStream_private_AddZipSearchPath` / `PushZipSearchPathFront`), which never touches `Data/`.

   Route (B) is cleaner, but its lookup order and naming are unknown; WP1 is a small device probe that settles it.
8. **The tooling is permissively licensed.** ARM `astcenc` (Apache-2.0) does the ASTC encoding. BC1/BC3 decoding is
   about 150 lines of our own code, or `bcdec` (MIT/Unlicense) or DirectXTex (MIT). `etc2comp` (Apache-2.0) is only
   needed for ETC-only phones, which are out of scope. The transcode runs offline on the player's PC, next to the
   patcher, from the player's own Steam install.

**Recommendation:**
* WP0: let the player force the HighGPU profile. It is cheap, and the textures are worthless without it.
* WP1: probe how the game can pick up replacement files.
* Then port the **"worst offenders" scope**:
  * environment;
  * third-person characters, heads and jerseys;
  * equipment, props and vehicles;
  * the lightmaps of the levels being played.

  That is about 1,750 files and about +640 MB GPU at PC resolution for the whole set (all levels). Leave the UI, first-person
  weapons and arms, and IBL cubemaps alone: their size is already the same as on PC.

---------------------------------------------------------------------------------------------------

## 1. The data packs and how the game reads a texture

### 1.1 Naming: the same hash on both platforms **[V]**

* Android: `Data/<h[0]>/<h[1:]>.f`, where `h = "%x" % crc32(logical_path)`, unpadded (report 11 §2; native builder at
  0x57de58 → zlib `crc32` at 0xc87f90).
* PC: `Data/<decimal crc32>.f`, flat (pc-port.md §4.1).
* Example, re-checked here: `3d/Equipment/Grenade_Frag/Textures/Grenade_Frag_rom.tga` → crc32 `0x0019921a`. On Android
  it is `Data/1/9921a.f` (457,642 B: KTX ETC1, 1024², 11 mips). On PC it is `Data/1675802.f` (1,968,741 B: GCBF around a
  DDS DXT1, 2048², 12 mips).
* Matching works **by hash**, so the 822 Android textures without a recovered name (report 11, `_unnamed/`) match too.
  `pctex/textab.py` joins all 7,568 Android texture files to PC by hash: 7,502 are DDS on PC, 64 are a raw PVR v2 RGBA8
  (Menus ability icons, see §2.3), 1 is a KTX (`brdf_lut.ktx`) and 1 is missing on PC. In the other direction, 162 PC DDS (185 MB) have no Android
  file.

### 1.2 Container: GCBF, the same on both platforms **[V]**

GCBF (report 11 §3) is a seekable block container with LZ4 blocks; the native file stream decodes it transparently.
Android wraps 2,381 of its 2,382 bare KTX files in GCBF (the exception is `Shaders/Engine/brdf_lut.ktx`, raw). PC wraps
10,619 files (pc-port.md §4.1). The texture payload inside is what differs.

### 1.3 How the game opens a texture **[V]** code, **[I]** native internals

* Model materials (`.bdae`, BRES) name their textures by logical path. The native Glitch loader opens those directly.
  Managed code loads only a few textures itself, for example lightmaps through
  `Texture.FromFile(path, CreateOptions.SRGB)` → `Texture_LoadGlitchTexture_private` (`EN/Texture.cs:218,453`,
  `EN/LightmapComponent.cs:285-299`).
* The **file extension does not set the format**. Logical names end in `.tga`, `.png`, `.dds` or `.exr`, but the bytes
  are KTX, a split zip or DDS. The native loader sniffs the content: `libnativeengine.so` holds loaders and messages for
  `.ktx`, `.dds`, `.pvr` and `.tga` (strings `DDS : EPF_DXT1 format`, `UNSUPORTED DDS FORMAT TEXTURE`, `PVR loader`) and
  a full GL format table that includes ASTC 4×4…12×12, ETC1/ETC2, S3TC/DXT and PVRTC.
* The obfuscated file system is switched on by `GameDefinitionComponent.UseObfuscatedFileSystem`
  (`EN/GameApplication.cs:1208`). Above `Data/`, the native stream supports **zip search paths**:
  * `GlfStream_private.AddZipSearchPath(relativePath, LocationType)`, `RemoveZipSearchPath` and
    `PushZipSearchPathFront(relativePath)` (`EN/GlfStream_private.cs:248-270`);
  * `LocationType` is `Asset`, `Save`, `Cache` or `AssetBuiltWin`;
  * they are wrapped by `GlfStream.PushZip_protected` / `PopZip` (`EN/GlfStream.cs:231-245`) and used by
    `InboxComponent` for cache zips (`GM/InboxComponent.cs:837-863`) and by `PackManager.AddGroupToSearchPath`, which
    pushes `.pak` packs **to the front** (`EN/PackManager.cs:247-262`, `Config/packs.json`: `Packs/dlc.pak`, `audio.pak`,
    `data.pak`; these are not shipped and are only used with a dev `--level` argument).

  Whether a pushed zip is searched before `Data/`, and whether entries inside it use logical or obfuscated names, is not
  known (§9, U2).

### 1.4 Not "lowres assets" **[V]**

`lowResAssets` (true on the S22's profile) only swaps **VFX templates** to `*_ld.*` variants
(`GM/GameGameOptionsComponent.cs:28-55`, `ResolveLowRes`; the pack has no `_ld` file) and turns off blood traces and
shells. The mipmap selector **skips no mips** on Android: `customGameplayMipmapsToSkip` and `customMenuMipmapsToSkip`
are both 0 in `GameOptions_android.json` (MEM profile). So the phone renders its textures at full pack resolution, and
the 2× gap is in the data itself.

---------------------------------------------------------------------------------------------------

## 2. Texture formats

### 2.1 Android ETC pack (`google_lowres_v162074_Nexus_9_ETC`) **[V]**

| layout | files | content |
|---|---|---|
| zip `SPLIT` + `rgb.ktx` + `alpha.ktx` (stored, not deflated) | 5,186 | two ETC1 KTX of the same size (each GCBF-wrapped inside the zip): colour in one, alpha in the other's red channel. `SPLIT` = 4 zero bytes |
| bare KTX (GCBF) | 2,381 | ETC1 (`0x8D64`), no alpha |
| `zip SPLIT + rgb.pvr + alpha.pvr` | 2 | legacy |
| `brdf_lut.ktx` | 1 | `0x822F` (RG16F), raw |

* 7,254 of 7,502 KTX carry the key-value pair `CRNLIB_FOURCC=ETC1`. They were written by **crnlib** (Crunch's
  rate-distortion ETC1), which explains why they compress to 46 % under LZ4.
* All have `KTXorientation S=r,T=d`.
* Mips: full chains for 3D textures. UI images (`Menus/…`) and lightmaps have 1 level.
* The shaders in this pack define `SPLIT_ALPHA` ("Para ETC", `Shaders/Engine/PBR_FS.glsl:4-9`) and sample
  `BaseColor_alpha`, `Rome_alpha`, `lightMap_alpha` and so on as separate textures (l. 397-440).

### 2.2 Android ASTC pack (what the S22 uses) **[V]**

* The installer picks ASTC when `GL_KHR_texture_compression_astc_ldr` is present (report 10 §3.4); the S22 has it
  (INSTALL-OFFLINE.md).
* Both packs list the same 15,747 paths. For the texture files, the ASTC pack holds 725.7 MB on disk against 719.7 MB
  for ETC (from the two `pack.info` indexes).
* 147 sampled ASTC textures, fetched with range requests from `…/136808/google_lowres_v162074_Nexus_9_ATC.amz`, are all:
  * **one KTX1 with `glInternalFormat 0x93B4` (`COMPRESSED_RGBA_ASTC_6x6_KHR`), `glBaseInternalFormat 0x1908` (RGBA)**;
  * no key-value data;
  * GCBF-wrapped (9 table entries, as in the ETC pack);
  * **the same resolution and mip count as the ETC file** (UI images are 2-3 px smaller: ETC pads to a multiple of 4).

  Alpha lives in the same texture.
* The ASTC pack's `Shaders/Engine/PBR_FS.glsl` and `PBR_VS.glsl` differ from the ETC copies in exactly two lines:
  `#undef SPLIT_ALPHA //Para ASTC` (`pctex/astc_misc/`, diff). On an ASTC phone the shaders therefore expect **one RGBA
  texture**, which is the shape a replacement must have.
* ASTC 6×6 is 3.56 bit/pixel. The packed files compress to 74 % under LZ4 (sample).

### 2.3 PC (Steam) **[V]**

| content | count (shared with Android) |
|---|---|
| DDS DXT5 (BC3) | 4,875 (Android split zips), 10 (Android bare KTX) |
| DDS DXT1 (BC1) | 2,370 (Android bare KTX), 247 (Android split zips: alpha unused or 1-bit) |
| DDS DXT5 cubemaps (`caps2` 0x200) | IBL probes `Baked/Levels/*/*.IBL.dds`, same size as Android (6 faces) |
| PVR v2 legacy header (`0x34` length, `PVR!`), format 0x12 = RGBA8888, uncompressed | 64 (Menus ability icons, 108²) |
| KTX | 1 (`brdf_lut.ktx`) |

* No DX10 header, no BC7, no sRGB DXGI formats: sRGB is applied by the engine (`Texture.CreateOptions.SRGB`), as on
  Android.
* Mips: full chains for 3D textures, 1 level for UI images and lightmaps (same rule as Android).
* Orientation matches Android: the comparisons in §3.3 line up without flipping.

---------------------------------------------------------------------------------------------------

## 3. Comparison over every shared texture

### 3.1 Resolution ratio PC : Android (`pctex/textab.py`, all 7,502 DDS pairs) **[V]**

| PC size / Android size | textures |
|---|---|
| 1× (same) | 4,918 |
| 2× in each dimension | 2,561 |
| about 1.7× (UI portraits, e.g. `mercs_box/Merc10-BBQ.png` 224×288 → 376×480) | 22 |
| 0.9× (`icons/trophy_reward.png` 276×296 → 256×256) | 1 |

### 3.2 By category (all shared textures, GPU size in ASTC 6×6) **[V]**

| category | textures | 2× on PC | Android max | PC max | GPU now | GPU at PC resolution |
|---|---|---|---|---|---|---|
| `3d/Environment` | 953 | **953** | 1024 | 2048 | 56 MB | 221 MB |
| `3d/Characters` | 863 | **724** | 2048 | 2048 | 144 MB | 307 MB |
| `Baked/Levels` (lightmaps + IBL) | 309 | 63 (all lightmaps) | 2048 | 4096 | 142 MB | 436 MB |
| unnamed | 822 | 328 | 2048 | 2048 | 110 MB | 260 MB |
| `3d/Weapons` | 872 | **1** | 1024 | 2048 | 225 MB | 227 MB |
| `Menus/` (UI) | 3,188 | 20 (portraits) | 2048 | 2048 | 149 MB | 150 MB |
| `3d/VFX` | 386 | 386 | 1024 | 2048 | 7 MB | 28 MB |
| `3d/Menus` (3D coins, tokens) | 31 | 31 | 1024 | 2048 | 11 MB | 42 MB |
| `3d/Equipment` (grenades, devices) | 33 | 33 | 1024 | 2048 | 7 MB | 28 MB |
| `3d/Vehicles`, `3d/Objects`, `3d/Maps`, `Levels/*.minimap` | 43 | 43 | 1024 | 2048 | 9 MB | 35 MB |

Inside the categories:

* **Environment:** 768 textures 256² → 512², 60 textures 512² → 1024², 28 textures 1024² → 2048² (skydomes such as
  `3d/Environment/04_Skydome/Map_09/SKY_Map09.exr`).
* **Characters:** third-person bodies 415 (2×), heads 196 (2×), jerseys 94 (2×). First-person arms: 139 same, 19 at 2×.
  Distribution: 300 textures 512² → 1024², 218 textures 256² → 512², 167 textures 64² → 128².
* **Weapons:** first-person and third-person weapon textures and camos are the same size on both (1024/512/256); the one
  exception is `LMG04_TP_rom_05.tga`.
* **Lightmaps:** every `Lightmap_N.tga`, `.dirirr.tga` and `.lowend.tga` is 2048² → 4096² (one level is 1024² → 2048²).
  IBL cubemaps are the same size.

### 3.3 Quality (decode and PSNR; `pctex/quality.py`, `astcq.py`; 12 random textures per category) **[V]**

PSNR on RGB, 8-bit, between decoded top mip levels. "PC↓" is the PC texture box-filtered down to the Android size.
"Detail loss" compares the PC texture with itself after it is halved and enlarged again: the lower the value, the more
real detail the extra resolution carries.

| category | ETC1 vs PC↓ | ASTC 6×6 vs PC↓ | detail loss of PC at mobile size |
|---|---|---|---|
| Environment | 31.7 | 31.3 | **29.4** |
| Characters | 27.5 | 35.6 | **28.0** |
| Equipment | 30.2 | 35.0 | **29.2** |
| Vehicles | 33.4 | 34.9 | 30.7 |
| VFX | 37.5 | 36.6 | 35.8 |
| Lightmaps (`Baked/Levels`) | 33.6 | 41.3 (n=1) | **28.3** |
| Weapons (same size) | 31.1 | 39.4 | n/a |
| Menus (same size) | 37.3 | 36.1 | n/a |

Reading:
* The ASTC pack the S22 uses is noticeably closer to PC than ETC1 (crnlib), by 1 to 7 dB.
* At equal size (weapons, UI) the ASTC 6×6 copy is already within 36-39 dB of PC, which is hard to tell apart in play.
* In the 2× categories the missing detail (27-31 dB) is the dominant loss. Example rows from `quality.json`:

  | texture | phone → PC | detail loss of PC |
  |---|---|---|
  | `ENV_SnowRock_A_bc.tga` | 256² → 512² | 19.2 dB |
  | `ENV_DecalPaint02_bc.tga` | 256² → 512² | 20.7 dB |
  | `JKb_Head_bc.tga` | 256² → 512² | 23.5 dB |

### 3.4 Models, materials and shaders **[V]**

* **Texture references are identical.** For 236 sampled `.bdae` models across 14 folders (`pctex/bdae_cmp.py`), the
  set of texture paths in the PC and Android files is the same in every case. Environment models are about 6× larger on
  PC (more geometry or vertex data), the others are about the same size. No model needs to change to take a bigger
  texture: UVs are normalised.
* **Same shader source, different tier.** Android ships GLSL in `Shaders/`. PC ships `Shaders_high_end.pak` (8.2 MB) and
  `Shaders_low_end.pak` (2.8 MB) holding HLSL cross-compiled from the same source:
  * the GLSL has `GLSL2HLSL` / `GLSL2METAL` branches;
  * the PC pak has the same sampler names (`Texture2D<float4> BaseColor`, `Rome`, `BaseColorMix`, `BaseColorDecal`)
    and still contains `#ifdef SPLIT_ALPHA`.

  The texture semantics (`_bc` base colour, `_rom` roughness/occlusion/metal mask, `_nm` RGB normal map, `_tint`, alpha
  use) are therefore the same, and a PC texture fed to the mobile shader is correct, not "broken".
* **What differs is the tier**, and it decides which textures are sampled at all (`Shaders/Engine/PBR_FS.glsl:119-224`):

  | tier (define) | set by | normal map | lightmap | other |
  |---|---|---|---|---|
  | `LOW_END` / `LOW_END_1TEXTURE` | `lowEndMaterial` true | no | `LIGHTMAP_LOW` | no IBL env map, no shadows |
  | **`MID_END`** (S22 today, **[I]**) | MidGPU `addShaderDefine "MID_END"` | **no** (`//#define NORMAL_MAPPING`) | `LIGHTMAP_LOW` | no `MIX`/`SUPERMIX`/`ROME_MASK`, no shadows; `LOW_END_SPECULAR` (MidGPU) removes `SPECULAR` |
  | `HIGH_END` | HighGPU | **yes** | full + directional | no `MIX`/`SUPERMIX`/`ROME_MASK` |
  | `GOD_END` | High_Ultra | yes | full + directional | everything, shadow maps |

* **Lightmaps follow the profile too**:
  * `lowLightMap` true gives `LightmapComponent.LowEndMode`, which loads `<level>.Lightmap_N.lowend.tga` (RGB, no
    directional map); with it false the game loads `_N.tga` plus `_N.dirirr.tga` (`EN/LightmapComponent.cs:285-295,
    342-346`);
  * `enableLowResMaps` true swaps `.StaticGeo` for `_lowend.StaticGeo`, excludes the `HighEndOnly` level layers
    (`GM/GameGameOptionsComponent.cs:76-81`, `GM/GameStaticBatchingComponent.cs:44-52`) and swaps the lightmap file for
    `_lowend.Lightmap` (`GM/GameLightmapComponent.cs:17-27`).

  MidGPU leaves both at their default, true.

Consequence: on today's MidGPU profile, an HD `_nm` or `.dirirr` texture is never sampled, and only `_bc`, `_rom`,
`_tint` and the `.lowend` lightmaps would show. The GPU profile has to move first (WP0).

---------------------------------------------------------------------------------------------------

## 4. What the S22 renders today **[I]** unless marked

* Pack: **ASTC** (INSTALL-OFFLINE.md; the installer's GL-extension rule **[V]**).
* GPU profile: `GameOptions_android.json` (`datapack-decoded/GameOptions_android.json`) selects by `GPU$$` and `MODEL$$`
  substrings. No rule names Adreno 7xx or the Exynos Xclipse 920, so the S22 should fall to the last rule
  `{"MidGPU": [[["CORES>1"]]]}` (STATUS.md §device test 2 says the same; the match is native and unverified).
  Effective MidGPU values: `lowEndMaterial false`, `lowEndSpecular true`, `addShaderDefine MID_END`, `lowCubeMap true`,
  `lowLightMap true`, `enableLowResMaps true`, `lowResAssets true`, `renderTargetSizeScale 0.6`, `enableShadowMap false`,
  `enableBloom false`, `enableRenderThread true`, `renderThreadMode 3`.
* HighGPU differs in:
  * `lowEndSpecular true` (kept);
  * `lowLightMap false`, `enableLowResMaps false`;
  * `addShaderDefine HIGH_END`;
  * `enableShadowMap true`, `enableBloom true`;
  * `renderTargetPixelFormat B10F_G11F_R11F`;
  * `dynamicRenderTargetSizeScale true`;
  * `anisotropicFiltering 2.0`.

  Its threading values (`enableRenderThread true`, `renderThreadMode 3`) are **identical** to MidGPU, so a profile
  change does not touch the physics and render-thread analysis in STATUS.md. **[V]** (JSON)
* `renderTargetSizeScale 0.6` (MidGPU) renders the 3D scene at 60 % of the screen resolution. That blurs every texture
  more than any texture choice does; HighGPU uses `dynamicRenderTargetSizeScale`.
* Where the profile is applied: `GameOptionPerformanceProfileComponent.OnCreate` reads `GameOptions_android.json` and
  calls `GameOptionManager.InitializeGameOption(text, clientId)` (`EN/GameOptionPerformanceProfileComponent.cs:23-41`).
  `ApplyGameOptions` then passes every resolved option to `ApplyPerformanceOption` (l. 44-93). Either step is a clean IL
  hook point for a "graphics profile" setting, with no data-file change. The remote GameOptions download is off in our
  build: the native SDK does not start (`patcher/Patches/NativeOnlinePatch.cs`, STATUS.md).

---------------------------------------------------------------------------------------------------

## 5. Can the phone use them? Feasibility in detail

### 5.1 Transcoding pipeline (offline, on the player's PC)

For each PC texture in scope:

1. Read `Data/<decimal>.f`, GCBF-decode it (we have `Gcbf` in C# and `tools/datapack/gcbf.py`), and parse the DDS header
   (the 128-byte header; DX10 never occurs).
2. **Decode every mip level** from BC1/BC3 to RGBA8. Reuse the PC mip chain instead of regenerating it. Do not touch
   gamma: the data is passed through as stored, and the engine applies sRGB.
3. Encode each level with **astcenc** as `-cl` (LDR, linear: the pack uses 0x93B4, not the sRGB 0x93D4), **6×6**,
   preset `-medium` (or `-thorough` for the small worst-offender scope). Do **not** use `-normal` for `_nm`. That mode
   moves X/Y into RA and needs a shader that rebuilds Z, while the game's shader reads RGB.
4. Write KTX1:
   * `glType 0`, `glTypeSize 1`, `glFormat 0`;
   * `glInternalFormat 0x93B4`, `glBaseInternalFormat 0x1908`;
   * PC width and height, depth 0, array 0;
   * faces 1 (6 for cubemaps, which are out of scope);
   * mips = PC count;
   * no key-value data;
   * each level as `u32 imageSize` + blocks, 4-byte aligned.

   This mirrors the ASTC pack exactly (§2.2). Wrap it in GCBF/LZ4 as Gameloft does: we have the encoder in
   `tools/datapack/gcbf.py:156`, and a C# port is small.
5. Name the output file by the **same hash**: Android `Data/<hex[0]>/<hex[1:]>.f`.

Notes:
* **Double lossy encoding.** DXT1 → ASTC 6×6 re-encodes 4-bit/pixel BC data at 3.56 bit/pixel. Any BC1 block artefacts
  carry over. ASTC 4×4 (8 bit/pixel) avoids most of this but takes 2.25× the memory of 6×6 (§6). Measure both on 20
  textures in WP2 before choosing. The PSNR of PC→ASTC was **not measured**, because running the downloaded astcenc
  binary was blocked in this session (§9, U5).
* **Split alpha does not apply on ASTC.** The ASTC shader does not use split alpha (§2.2), so DXT5 alpha simply goes into
  the ASTC RGBA block. For an **ETC-only** phone, a port would need ETC1 split zips (crnlib/etc2comp) at PC resolution;
  out of scope.
* **Lightmaps.** Lightmaps load into a `Texture2DArray`, one slice per lightmap, with `LoadSubTexture` and
  `LinearMipNone` (`EN/LightmapComponent.cs:330-360`), so **every lightmap of a level must change together**. Each has 1
  mip and goes up to 4096². The S22's GL max texture size (Adreno 730: 16384) is not a limit. They are DXT5 on PC with
  data in alpha (RGBM-style). ASTC 6×6 keeps RGBA. ASTC 8×8 halves the memory (§6) but is lossier on smooth gradients;
  test it.
* **Minimaps.** 512² → 1024². The minimap mapping uses `MinimapSourceComponent.TextureWidth`, which is computed from the
  level scale and `PixelPerMeter` (`GM/MinimapSourceComponent.cs:41-55,151-159`), not from the image. So a bigger image
  should only look sharper **[I]**. Low priority.
* **UI is excluded.** The UI is the same resolution, except 22 merc portraits whose PC size differs (224×288 →
  376×480). SWF layouts place bitmaps by pixel size, so a different size risks wrong scaling.

### 5.2 Tools and licences

| tool | use | licence | ship? |
|---|---|---|---|
| ARM astcenc 5.x (`ARM-software/astc-encoder`) | ASTC encode (CLI, or the library via P/Invoke) | Apache-2.0 | yes, binary + NOTICE, or build from source |
| own BC1/BC3 decoder (or `bcdec`, `DirectXTex`) | DDS decode | ours / MIT or Unlicense / MIT | yes |
| `tools/datapack/gcbf.py`, C# `Gcbf` | GCBF read/write | ours | yes |
| Google etc2comp | ETC2 for non-ASTC phones | Apache-2.0 | not needed for the S22 |
| crnlib / crunch | ETC1 like Gameloft | zlib | not needed |
| PVRTexTool, Compressonator | alternatives | proprietary / MIT | no / optional |

The transcode runs on the player's computer, like the patcher. On the phone (on-device encoding) it would be slow and
heat the device, so the phone side should only receive finished files.

### 5.3 How the patched game would pick them up

**(A) Replace in `Data/` and keep the installer's index consistent.**
* The Java installer checks every `Data/` file's **size** against `/data/data/<pkg>/files/pack.info` on each start and
  deletes mismatches. Files over 2 MiB are split into 2 MiB pieces, which must also be listed in
  `files/d_o_w_n_l_o_a_d_e_d.txt` (INSTALL-OFFLINE.md, points 3-4; report 10 §4).
* Our boot code already restores and rebuilds `pack.info` and the journal (`hosts/embedded/Install/InstallerData.cs`,
  `PackIndex.cs`), and checks CRCs in the background (`damaged.txt`).
* Route A extends that code. For each overridden file it:
  * writes the new size, CRC and piece list into the index and the journal;
  * keeps an `McvsOffline/hd/manifest.json` (hash, original size and CRC, new size and CRC) so the background CRC
    check, `rebuildIndex` and a "remove HD textures" action all know the overrides;
  * backs up the original `.f` files, or re-reads them from the player's backup or mirror.

  **Risk:** if anything resets the index (a `gl_ver` mismatch, a reinstall, `manageInstaller false`), the installer
  deletes the HD files and tries Gameloft's CDN.

**(B) Override zip on the engine's search path (preferred if WP1 confirms it).**
* Keep `Data/` untouched. Put `McvsOffline/hd/hd-textures.zip` (stored entries, under 2 GB, no ZIP64) in the game's
  Save or Cache location.
* An IL hook at start-up mounts it, before the first level loads, with `GlfStream.PushZip_protected(path, Save)` or
  `GlfStream_private.PushZipSearchPathFront`.
* Unknowns: is a pushed zip searched **before** `Data/`, which only `PushZipSearchPathFront` suggests? Are entry names
  the logical paths or the obfuscated `<h>/<…>.f` names? Which root does each `LocationType` map to? WP1 settles all
  three with one coloured test texture.
* Advantages:
  * the installer never sees the files;
  * removal means deleting one file;
  * the zip can be swapped per scope.

**(C) Not viable:** managed redirection. Most textures are opened by the native model loader, and only lightmaps and a
few others pass through `Texture.FromFile`.

---------------------------------------------------------------------------------------------------

## 6. Size, memory and load time on the S22

GPU bytes = sum over mip levels of `ceil(w/b)·ceil(h/b)·16`; "now" is the ASTC 6×6 pack. On disk add about 0.8-1.0×
(GCBF/LZ4 barely compresses astcenc output; Gameloft's ASTC averages 0.74). **[V]** computed from the headers; the
per-match figures are **[I]**.

| scope (2× textures only) | files | GPU now | at PC resolution, 6×6 | 8×8 | 4×4 | PC DDS |
|---|---|---|---|---|---|---|
| lightmaps (irr + dir + lowend, all levels) | 63 | 98 MB | 392 MB | 220 MB | 880 MB | 704 MB |
| environment | 953 | 56 MB | 221 MB | 123 MB | 491 MB | 342 MB |
| characters (TP, heads, jerseys, some FP) | 724 | 55 MB | 218 MB | 121 MB | 484 MB | 355 MB |
| unnamed (by hash; usage unknown) | 328 | 51 MB | 201 MB | 112 MB | 450 MB | 298 MB |
| props (equipment, objects, vehicles, maps) | 69 | 15 MB | 59 MB | 33 MB | 131 MB | 79 MB |
| 3D menus (coins, tokens) | 32 | 11 MB | 43 MB | 24 MB | 95 MB | 59 MB |
| VFX | 386 | 7 MB | 28 MB | 16 MB | 63 MB | 61 MB |
| minimaps, other | 28 | 2 MB | 8 MB | 4 MB | 17 MB | 14 MB |
| **all 2×** | 2,583 | **294 MB** | **1,169 MB** | 653 MB | 2,612 MB | 1,912 MB |

* **Per match** (one level, about 10 characters, shared environment and VFX textures; **[I]**):
  * lightmaps of one level: for example Port has 3 lightmaps. High profile (irr + dir) goes from 2 × 3 × 1.9 MB =
    11 MB to 45 MB at 4096² 6×6. Low-end lightmaps go from 6 to 22 MB.
  * Environment: a level uses a fraction of the 953 shared `01_WorkTextures`; at most +165 MB, estimated +50 to 100 MB.
  * Characters: about 10 of the roughly 40 agent and skin sets: about +40 MB.
  * Total: **about +150 to 250 MB of GPU/RAM per match** in 6×6. The S22 has 8 GB of LPDDR5. The process is 64-bit only
    (the patcher drops the 32-bit libraries), so the address space is no concern.
* **A runtime safety valve already exists.** `GameGameOptionsComponent.SelectMipmapToSkip` is installed as the
  engine's custom mip selector (`useCustomMipmapSelector true`) and returns `customGameplayMipmapsToSkip` per texture
  name (`GM/GameGameOptionsComponent.cs:127-137`). Setting it to 1 brings every gameplay texture back to mobile size,
  still from the better PC source. A hook could also make it name-based (for example skip 1 mip on lightmaps only).
* **Load time:** at the S22's UFS 3.1 sequential read (over 1 GB/s) plus LZ4 and `glCompressedTexImage2D`, +150 to
  250 MB costs well under a second per level load. **Not measured.**
* **Phone storage:** the worst-offender scope (§7) adds about 0.6 GB, everything about 0.9 GB, on top of the 2.04 GB
  pack.
* **Encode time on the player's PC:** about 2.8 Gpixel for all 2× textures with their mips. astcenc throughput depends
  on the preset and core count. **Not measured here** (§9, U5): estimate minutes, not hours, on a desktop.

---------------------------------------------------------------------------------------------------

## 7. Worst offenders and proposed scope

Ranked by how visible the texture is in play and how large the gap is. **[V]** sizes, **[I]** visibility.

| tier | content | files | gap | GPU at PC resolution (6×6) | worth it |
|---|---|---|---|---|---|
| 1 | **map surfaces** `3d/Environment/01_WorkTextures`, kits, props, skydomes | 953 | 256² → 512² for 768 of them | 221 MB | yes: the most visible "ugly", and the whole screen |
| 1 | **lightmaps** of the levels in rotation | 63 | 2048² → 4096² | 392 MB (all levels, all 3 variants) | yes, **only with WP0** (High profile loads `.tga` + `.dirirr`) |
| 2 | **third-person characters**: bodies, heads, jerseys | 705 | 256-512 → 512-1024 | about 210 MB | yes: enemies are what you look at |
| 2 | **equipment**: grenades, devices (seen in first person) | 33 | 1024 → 2048 | 28 MB | yes, cheap |
| 3 | turrets, vehicles, gym maps, 3D menu coins and tokens, minimaps | 108 | 2× | about 110 MB | optional |
| 3 | VFX | 386 | 2× (small textures) | 28 MB | low gain (particles are soft) |
| 4 | unnamed 2× textures | 328 | 2× | 201 MB | after naming or usage checks only |
| none | first-person weapons and camos, first-person arms, UI, IBL cubemaps | about 4,900 | same size | | no: ASTC 6×6 is already 36-41 dB from PC |

Proposal: **tiers 1 and 2** (about 1,750 files, about +640 MB GPU for all levels, about +150 to 250 MB per match, about
0.6 GB on the phone), shipped as one HD pack with per-tier switches. Tier 3 is a checkbox. Normal maps (`_nm`, 405 of the
2× set) are only worth including once the High profile is active.

---------------------------------------------------------------------------------------------------

## 8. Recommended plan

Each work package ends with something the owner can test on the S22.

**WP0: graphics profile setting** (managed IL patch plus a setting; data already on the phone; independent of the PC
build)
* Add `graphics.profile` = `auto | mid | high | ultra` (default `auto`, which is today's behaviour) to settings.json and
  to the settings page.
* Patch `GameOptionPerformanceProfileComponent.OnCreate` (or `ApplyGameOptions`) so that, when it is not `auto`, the
  chosen profile's overrides from the same JSON win. The source of truth stays Gameloft's own `HighGPU` and `High_Ultra`
  blocks. Log the effective options as a `[boot] graphics:` line, which also settles U1.
* Test: Port and Desert under mid, high and ultra; frame rate (`maxFramerateValues`), heat over 10 minutes, and
  screenshots.
* Risks: HighGPU has `enableShadowMap` and `B10F_G11F_R11F` render targets, both used by Gameloft on Adreno 6xx. Shadow
  maps on the Xclipse 920 GPU are unknown.

**WP1: delivery probe** (small, decides A or B)
* Build, by hand with `tools/datapack`, one obviously recoloured ASTC KTX for a texture that is easy to spot (for example
  `ENV_GenericConcrete05_bc.tga`), as a raw `.f`, as a GCBF `.f`, and inside a stored zip under logical and under
  obfuscated names.
* Try route B with a temporary hook that calls `GlfStream.PushZip_protected(…, Save | Cache)` and
  `PushZipSearchPathFront` at boot.
* Try route A by putting the file into `Data/` with `SKIP_VALIDATION=1` (INSTALL-OFFLINE.md settings).
* Also confirm that a 2× KTX at the same path renders: mips, sRGB, orientation.
* Output: which route, zip naming, location root.

**WP2: transcoder in the desktop tool** (`patcher/`, or a sibling `tools/hd-textures/`; C#, net8)
* Input: the player's Steam folder (auto-detect `steamapps/common/Modern Combat Versus`) or `--pc-data DIR`.
* Index: the 7,568-row Android texture list (hash, logical path if known, category, Android size), shipped as **our own
  table without game data**: hashes, sizes and categories only.
* Steps: decode GCBF and DDS, decode BC1/BC3 per mip level, astcenc per level (6×6 by default, 8×8 for lightmaps
  configurable, 4×4 optional), write KTX1, GCBF, then a zip or folder plus `manifest.json`.
* Self-check: decode the output with the same decoder and print a PSNR against the PC texture (expected 38 dB or more).
* Unit tests: synthetic BC1/BC3 → RGBA, KTX header round trip, GCBF round trip, hash naming. The real PC data runs only
  when `MCVS_PC_DATA` is set, as with the existing real-client tests.

**WP3: install on the phone** (route from WP1)
* B: the embedded host mounts `McvsOffline/hd/hd-textures.zip` at boot (`hd.enabled`, `hd.tiers`), logs
  `[boot] hd textures: N files, tier …`, and falls back silently when the zip is missing or broken.
* A: extend `InstallerData`/`PackIndex` with an override manifest (index sizes/CRCs, split pieces, journal), backups,
  a "remove HD textures" action, and a background CRC check against the manifest.
* PLAYER-SETUP.md: one `adb push` line and a section on the settings page.

**WP4: scope and tuning on device**
* Tier 1 and 2 pack. Measure:
  * memory: `dumpsys meminfo` and GPU memory with each of `customGameplayMipmapsToSkip` 0 and 1;
  * level load time from the log;
  * frame time.
* Decide the lightmap format (8×8 or 6×6) and the tier-3 default.

**WP5 (optional):** ETC-only phones (ETC1 split zips with crnlib or etc2comp, since the ETC shaders keep `SPLIT_ALPHA`)
and the unnamed 2× textures after a usage scan of the level files.

Order: WP0 and WP1 in parallel (both small), then WP2, then WP3, then WP4.

### 8.1 Risks

| # | risk | likelihood / impact | mitigation |
|---|---|---|---|
| R1 | Route A: an index reset makes the installer delete the HD files and try the CDN | medium / medium (needs network; a stall offline) | prefer route B; in route A keep the manifest and restore originals before any index rebuild |
| R2 | Route B: the zip search path is not consulted before `Data/`, or needs the obfuscated names | unknown / blocks B | WP1 probe; route A as fallback |
| R3 | Memory pressure or thermal throttling at the High profile with HD textures | low-medium / medium | tiers; mip-skip valve (§6); 8×8 lightmaps |
| R4 | Double-lossy BC → ASTC looks worse than expected in places | medium / low | 4×4 for `_bc` of tier 1 only; PSNR self-check; the owner compares screenshots in WP4 |
| R5 | High profile features (shadow maps) misbehave on the Xclipse 920 (Exynos S22) | unknown / medium | `graphics.profile` stays player-selectable; `ultra` and `high` separate |
| R6 | A UI image or minimap that changes size breaks a layout | low / low | UI excluded; minimaps tier 3 |
| R7 | A level's lightmap set is mixed (some 4096, some 2048) and the array upload fails | medium if partial / high (black lighting) | the tool treats a level's lightmaps as one unit (all or none) |
| R8 | Legal: PC data leaving the player's machine | n/a | the tool reads the local Steam folder; outputs stay local; the docs say never to share the HD pack |

---------------------------------------------------------------------------------------------------

## 9. Unknowns

* **U1** Which GPU profile the S22 really gets (MidGPU expected). Settle it with a `[boot] graphics:` log of
  `m_options` (WP0).
* **U2** Search-path semantics:
  * whether `AddZipSearchPath` / `PushZipSearchPathFront` are searched before the obfuscated `Data/`;
  * entry naming inside a zip (logical or `<h>/<…>.f`);
  * which directory each `LocationType` (`Save`, `Cache`) maps to on Android.

  Native code; WP1.
* **U3** Whether the native loader takes a raw (non-GCBF) ASTC KTX under a `.tga` name. One raw KTX is read today
  (`brdf_lut.ktx`), so it probably does. WP1 tests both forms.
* **U4** Whether textures larger than the pack's sizes trip any limit in the Glitch loader or the texture-array code
  (lightmap arrays at 4096²). WP1 and WP4.
* **U5** PC → ASTC quality and encode time. The astcenc 5.3.0 release binary was downloaded for a measurement, but the
  session's permission policy blocked running it; the download was deleted. WP2 measures it.
* **U6** Per-match memory and load time on the S22 (§6 estimates only). WP4.
* **U7** Usage of the 328 unnamed 2× textures and the 162 PC-only DDS files.
* **U8** The Exynos variant (Xclipse 920) in other S22 models: profile rules and ASTC both still apply, but it is not
  tested. The owner's phone is SM-S901E (Snapdragon / Adreno 730, per the device-test-2 logcat in STATUS.md).

---------------------------------------------------------------------------------------------------

## 10. Reproduce

```sh
S=/tmp/claude-0/-home-user-mcvs-offline/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad
cd $S/pctex
/home/user/mcvs-work/tools/venv/bin/python -I scan_pc.py     # PC kinds -> pc_scan.json (about 1 min)
/home/user/mcvs-work/tools/venv/bin/python -I textab.py      # Android to PC join, KTX/DDS headers -> tex_rows.json (about 2 min)
python3 -I astcidx.py                                         # ASTC/ETC pack.info sizes -> astc_idx.json
python3 -I fetch_astc.py                                      # 147 ASTC samples from the CDN (16.6 MB, range requests)
python3 -I fetch_one.py Shaders/Engine/PBR_FS.glsl            # one ASTC-pack file by logical path
$S/texvenv/bin/python -I quality.py                           # PSNR tables (Pillow, numpy, texture2ddecoder, lz4)
$S/texvenv/bin/python -I astcq.py                             # ASTC vs ETC vs PC
python3 -I bdae_cmp.py                                        # texture references in models
```

The Python environment `texvenv` and the fetched ASTC samples were deleted after the run (disk). Recreate them with
`python3 -m venv texvenv && texvenv/bin/pip install pillow numpy texture2ddecoder lz4`, then run `fetch_astc.py` again.

---------------------------------------------------------------------------------------------------

## Critic notes

Completeness review (research only). This topic is not one of the owner's three requests in this round (icon/launch
screen, targetSdk, PC support). Treat it as background.

1. **Contradicted: "a profile change does not touch the physics and render-thread analysis" (§0.5, §4).** The threading
   values are indeed equal. But `GameOptions_android.json` also gives:
   * `MidGPU` sets `"excludedLoadingLayers": "Physics"`; `HighGPU` sets `"-"`. The default is `"Physics VFX"`.
   * `HighGPU` sets `"reducedAiUpdateFrequency": false`; the default is true.

   Both are process-wide statics:
   * `excludedLoadingLayers` feeds `World.GlobalExcludeLayers.Add` (`EN/PerformanceOptionApplierComponent.cs:331-338`,
     `public static List<string> GlobalExcludeLayers`, `EN/World.cs:144`).
   * `reducedAiUpdateFrequency` sets `BT_Root.AiModulo` to 4 or 2 (`GM/GameGameOptionsComponent.cs:109-110`, `static`
     at `GM/BT_Root.cs:34`).
   * `enableLowResMaps false` likewise adds `LowEndOnly` instead of `HighEndOnly` to the same global list
     (`GM/GameGameOptionsComponent.cs:80`).

   Forcing HighGPU therefore makes the in-process TS/GS Worlds load the level's `Physics` layer and the high-end
   geometry, and run bot AI twice as often. That touches exactly the Havok and game-thread load that STATUS.md's
   physics work is about. WP0 needs a bot-match stability test, and probably separate overrides for headless Worlds,
   or it must keep `excludedLoadingLayers`/`AiModulo` at the MidGPU values.
2. **Wrong reason: "the process is 64-bit only (the patcher drops the 32-bit libraries)" (§6).**
   `patcher/PatcherOptions.cs:86-87` keeps every ABI by default. `--abi arm64-v8a` is opt-in. The S22 still runs the
   arm64 copy (its primary ABI), so the conclusion holds and the reason should be corrected.
3. **Internal inconsistencies.**
   * The "2,583 2× textures" includes the 22 UI portraits at about 1.7× (§3.1: 2,561 at 2×). The "4,919 same size"
     includes the one 0.9× image.
   * Phone storage is "+0.7 to 0.9 GB" in §0.6 but "0.6 GB (scope) / 0.9 GB (all)" in §6/§7.
   * The PC file count (15,921) and the GCBF count (10,619) are corrected in pc-port.md's critic notes. There are 15,920
     `.f` files, 11,876 of them GCBF.
4. **Missing risk: which pack the phone actually has.** The plan assumes ASTC. A player whose `Data/` came from an ETC
   source (an old backup or mirror restored by our install code) has the `SPLIT_ALPHA` shaders. Single-texture ASTC
   replacements would then sample a missing `_alpha` texture. WP2/WP3 must detect the installed pack (shader
   `#undef SPLIT_ALPHA`, or `pack.info`) and refuse on ETC.
5. **Still unanswered:**
   * route B's search order and naming (U2), which is the deciding fact for the whole delivery design;
   * the PC→ASTC quality and speed (U5), which was not measured.

   Both stay as WP1/WP2 gates. Verified here: the MidGPU/HighGPU override blocks and the catch-all
   `{"MidGPU": [[["CORES>1"]]]}` rule match §4. `renderTargetSizeScale` is 0.6 in MidGPU against a default of 0.7.

## Owner's phone: ETC pack, not ASTC (2026-10-28)

The owner's S22 sent its `Data/8/dbd2f1.f` (`ENV_GenericConcrete05_bc.tga`) for the HD probe. It is byte-identical to the
**ETC** pack's copy (SHA-256 `636c5aea…56b0`, 43,301 bytes; ETC1 256x256, 9 mips). The phone runs on the ETC pack that the
offline install copied, not on the ASTC pack this report assumed for the S22. For the texture port this means one of:

* convert the PC textures to ETC1 (split alpha, as the ETC shaders expect): works with today's data, lower quality than ASTC;
* or move the phone to the ASTC pack first (Gameloft's download server still served ASTC files during the research), then
  convert to ASTC as planned. This needs the whole ASTC pack, not only the HD textures, because the shaders differ
  (no `SPLIT_ALPHA`).

The probe zip for the device test was built from this file (ETC1, the probe tool's ETC path).

## ASTC re-encode measured (U5 answered, 2026-10-28)

Owner request after build 11: replace the phone's ETC data by a full ASTC pack in which every sharper texture is the PC
texture re-encoded to ASTC at the PC size (design/hd-textures.md §6, `tools/hd-textures astc-pack`). Measured with ARM
astcenc 5.7.0 (built from the official tag, avx2), `-thorough`, `astc-measure` on 6 textures per category, PSNR of RGB
in dB against the PC texture at the PC size (median); "Gameloft" is Gameloft's ASTC 6x6 file at the phone's size,
enlarged bilinearly to the PC size, i.e. what the phone shows today: **[V]**

| category | 4x4 | 5x5 | 6x6 | Gameloft | throughput at 5x5 (Mtexel/s per thread) |
|---|---|---|---|---|---|
| surfaces | 60.6 | 40.6 | 37.7 | 24.4 | 0.13 |
| lightmaps (4096) | 60.9 | 46.5 | 42.6 | 28.6 | 0.18 |
| characters | 59.1 | 40.1 | 37.2 | 27.2 | 0.16 |
| equipment | 65.4 | 45.9 | 42.3 | 27.1 | 0.34 |
| vfx | 52.6 | 46.6 | 43.9 | 33.7 | 0.13 |

* The double lossy encoding (§5.1) costs little: ASTC 4x4 reproduces the PC's BC1/BC3 almost exactly (about 60 dB), 5x5
  keeps 40-52 dB. The phone's file is 10-20 dB further from the PC texture than any of them, because the detail is
  missing, as §3.3 found.
* `-exhaustive` gains 0.2-0.4 dB over `-thorough` at 5x5 for three times the encode time.
* The whole pack (2,215 textures, 2.2 Gtexel with mips) encodes in about an hour on 4 vCPUs at `-thorough` with the
  chosen sizes (5x5 for the large categories, 4x4 for equipment, props and minimaps).
* The owner's phone ran the ETC pack (section above), so the full ASTC pack replaces the whole `Data/`; the host
  switches the installer to the pack's own index (INSTALL-OFFLINE.md, "The ASTC HD data pack").
