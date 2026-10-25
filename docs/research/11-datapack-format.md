# 11 — Data pack: GCBF container, obfuscated file names, content inventory

Scope: the downloaded data pack for 1.17.32 (`datapack/1.17.32/ETC/files/Data/`, 15,747 files,
2.04 GB on disk), as the game stores it under `/sdcard/Android/data/<pkg>/files/Data/`.
Markers: **[V]** verified (by code, disassembly or a test over the whole pack), **[I]** inferred.

Deliverables
* Decoder and name resolver: `tools/datapack/decode.py` (library: `gcbf.py`, `names.py`, `kinds.py`). Usage is in
  `tools/datapack/README.md`.
* Decoded tree: `datapack-decoded/` (2.4 GB). It contains `names.json`, `_unresolved_refs.json`,
  `_catalog.json` (`tools/datapack/build_catalog.py`) and `_tables.json`.

---------------------------------------------------------------------------------------------------

## 0. Key findings

1. **On-disk name = `"%x" % crc32(logical_path)`, with the first hex digit used as the directory**
   (`Config/Settings.tx` → crc32 `ca0f90c8` → `Data/c/a0f90c8.f`). The CRC is plain zlib CRC-32
   (poly 0xEDB88320, init 0, final xor). The path is case-sensitive, uses `/`, is relative to `Data/`,
   and is never zero-padded. **[V]** Native code: 0x57de58 → 0x5b5490 → zlib `crc32` at 0xc87f90.
2. **GCBF** is a seekable block container: a `{raw_offset, file_offset}` seek table followed by
   independently compressed blocks. Each block has a 36-byte `padA` header with a codec FourCC
   (`Lz4 `, `Zlib`, `Raw `, `Yapy`), sizes and two CRC-32s **seeded with 1**. Only LZ4 is used in
   this pack. 6,461 files are GCBF (6,460 `.f` plus `MCVS.game`, 24,828 blocks). All of them decode
   with every field and both CRCs checked: **0 failures**. 9,286 files are stored raw: zip, BRES, JSON,
   GLSL and others. **[V]**
3. **Name coverage: 14,221 of 15,745 hashed files (90.32 %), or 87.2 % of the decoded bytes.** By
   confidence: 11,957 high (literal references), 1,216 medium (structural rules taken from game/engine
   code) and 1,048 low (combinatorial guesses). A decoy audit puts the low tier at about 1-2 wrong names
   out of 1,048. The 1,524 unnamed files are mostly unreferenced textures, models and templates, 110
   empty `{}` JSON files and 105 duplicate localisation tables. **[V]**
4. **A complete default CRM ships in the pack.** `Config/crmpreconfiguration.json` (2.2 MB) contains:
   * `pointcut_actions`
   * `game`: `_OverrideTables` lists 6,092 overridable columns, but every value is empty
   * `offline_store`: 95 soft/hard-currency prices
   * `iap`: per-language IAP catalogues for 18 languages, 58 products in `en`

   `CRMComponent` loads this file when the online CRM request fails, or always when
   `DoNotLoadConfig` is set. **[V]** (`EN/CRMComponent.cs:394-466`)
5. **The ecomm catalogue ships too.** `Config/game_object.json` (4.7 MB) holds 14,363 item
   definitions (the ids the server inventory stores). `Menus/stores/iap/FakeIOSBillingData_CRM.json`
   holds the fake-billing price list. The `DefaultProfile.tx`, `DefaultCrm.tx`, `DefaultClan.tx`,
   `DefaultSocial.tx` and `DefaultMessaging.tx` templates sit at the pack root. **[V]**
6. **Some referenced files are not in the pack.** Missing: `Config/EdgeList.txt`,
   `Menus/stores/InitialFeed.dat` (`CrmDefaultFilename`), `Security/AntiCheat.json`, and every
   `Levels/Gym/*` level. `MercsComponent.PracticeLevel` defaults to the missing
   `Levels/Gym/Gym_PVP.level`, but the shipped `SceneComponents.tx` overrides it to
   `Levels/Bootcamp/Bootcamp.level`. Three maps listed in `MapsList` (Barrage, Bridge, Downtown) have
   no level files. **[V]**

---------------------------------------------------------------------------------------------------

## 1. Pack layout and integrity

* `Data/MCVS.game` is GCBF (the game definition). `Data/Config/EngineConfig.json` is plain JSON. Both
  are stored **unhashed**. Every other file is `Data/<d>/<name>.f`, where `d ∈ {1..9,a..f}`.
  `GameApplication` resolves `<GameName>.game` by enumerating `*.game` (`EN/GameApplication.cs:1345,1369`),
  and the native code opens `Config/EngineConfig.json` by literal name, which is why both stay
  readable. **[V]**
* Name-length distribution: 14,813 names with 7 digits, 873 with 6, 58 with 5 and 1 with 4. This is
  exactly what a uniform 32-bit value printed with `%x` gives (expected 14,762 / 923 / 58 / 3.6). It is
  also why directory `0` never exists. **[V]**
* All 15,747 files, including the 101 that pack.info splits into pieces, match the CRC-32s in
  `pack.info`/`manifest.json`. **[V]**

## 2. Hash function (logical path → on-disk name)

```
h   = zlib.crc32(logical_path.encode())          # e.g. "Levels/Port/Port_Tutorial.level"
s   = "%x" % h                                   # "d837f097"   (lower case, no padding)
rel = s[0] + "/" + s[1:] + ".f"                  # "d/837f097.f"
```

Native evidence (`libnativeengine.so` arm64, 1.17.32; static disassembly only, using `tools/datapack/dis.py`):

| address | what |
|---|---|
| 0x57de58 | the obfuscated-path builder. Callers: 0x5781d4 (×2), 0x57db80, 0x57eedc (the file-open paths **[I]**). It makes the path relative to the data root, then at **0x57e020** calls `0x5b5490(0, buf, len)`. |
| 0x5b5490 | 6-instruction wrapper that tail-calls **0xc87f90 = zlib `crc32()`**. That function uses `get_crc_table()` at 0xc87f84 (table 0x14531b8, 8-byte entries, so old zlib on LP64). |
| 0x57e100-0x57e128 | sets `ios_base::basefield = hex` (`flags & ~0x4a \| 8`) and calls `operator<<(unsigned)` at 0x4c47fc, so the value is printed as `%x` |
| 0x57e3dc-0x57e518 | copies the hex string character by character, appends `"/"` (string 0x12e8186) after the first character, then appends `".f"` (string 0x12f0d9f, xref at 0x57e624) |
| 0x5b54a8 | case-insensitive CRC variant (`tolower` per byte, same table). It is **not** used for names: lower-cased candidates never match, case-preserved ones do. **[V]** |

Managed side: `GameDefinitionComponent.UseObfuscatedFileSystem` defaults to `true`
(`EN/GameDefinitionComponent.cs:212`). `GameApplication` passes it to native code with
`GameApplication_SetUseObfuscatedFileSystem` (`EN/GameApplication.cs:1208,1784-1793`), which stores
a byte flag at native object offset 0x195a (0x39b658). Every C# file access goes through
`AssetStream`/`GlfStream_private_Construct(relativePath, …)`. C# never sees hashed names. **[V]**

Empirical confirmation: `crc32` of `Config/Settings.tx`, `Levels/Connection.level`,
`Levels/Port/Port_Tutorial.level`, `Levels/Menus.level` and `Config/glue.json` (taken from `MCVS.game`
and native strings) matched on-disk files on the first try. Variants with `Data/`, `\`, lower or upper
case did not match. 11,957 further literal paths confirm it (§4). Re-check any path with
`tools/venv/bin/python -I tools/datapack/names.py <path>`. **[V]**

## 3. GCBF container

All integers are u32 little-endian. Reader at **0xb06328**: it `strncmp`s `"GCBF"` (0x13facbd) at
0xb06598, reads the count, then the table. The probe at **0xb06a10** reads 4 bytes, compares them
with `"GCBF"` (0x13facb8) and seeks back. Any file that fails this check is read as a plain stream.
That is why raw and GCBF files coexist under the same naming scheme. **[V]**

```
File:
  +0   "GCBF"
  +4   N                       seek-table entry count (= number of blocks + 1)
  +8   N × { raw_offset, file_offset }
         entry 0 = (0, 8+8N); entry N-1 = (total_raw_size, file_size)   [sentinel]
         (reader stores them as {file_offset+base, raw_offset}; total size = last raw_offset)
  then N-1 blocks; block i spans file_offset[i] .. file_offset[i+1] and decodes to raw_offset[i] .. raw_offset[i+1]

Block (header 0x24 = 36 bytes), encoder 0x590438, decoder 0x5905a0:
  +0x00 magic   0x41646170  ("padA" on disk, FourCC 'Adap')
  +0x04 codec   FourCC: 'Lz4 '=0x4c7a3420 (" 4zL"), 'Zlib'=0x5a6c6962, 'Raw '=0x52617720, 'Yapy'=0x59617079
                 (getId() stubs 0x594ef8, 0x594d0c, 0x594c80, 0x595dc4)
  +0x08 payload size         (= block span − 36)
  +0x0c decoded size
  +0x10 crc32(payload, init=1)     checked only when the caller asks (decoder flag w5)
  +0x14 crc32(decoded, init=1)
  +0x18 required output buffer = decoded size (+64 when codec == 'Yapy')
  +0x1c 0, +0x20 0                reserved
  +0x24 payload (LZ4: raw block format, no frame, no size prefix)
Decoder return codes: 0 ok, 1 output buffer too small, 5 CRC mismatch, 6 bad magic / unknown codec.
```

* The writer (`AssetStream.OpenWrite(compressed)` uses `OpenMode.WriteCompressed`; `MakeSettings` sets
  `GlfMakePackCompressType: "lz4"` in `Config/DefaultSettings.tx`) emits **128 KiB blocks**. 4,064 files
  have 1 block and the biggest has 248. The 2,381 files with exactly 8 blocks are all KTX textures,
  apparently one block per writer `Write()` call (header, key/values, mip levels). **[V]** for the
  counts, **[I]** for the per-`Write()` reason.
* Strict validation (`tools/datapack/validate.py`) checks magic, table monotonicity, sentinel = file
  size, every block size field, the reserved zeros, the buffer-size rule and both CRCs. Result:
  **6,461/6,461 OK, 24,828 blocks, all `Lz4 `, 0 failures** (845,998,549 decoded bytes, 1.9 s).
  `gcbf.encode()` writes the same layout: round-trip verified on 108 files. **[V]**
* `Zlib` and `Raw ` are implemented in `gcbf.py` but unused here. `Yapy` (presumably Yandex "Yappy")
  is not implemented, because no block uses it. **[V]** (unused) / **[I]** (algorithm)

Decoded content kinds (sniffed, `kinds.py`):

| kind | files | notes |
|---|---|---|
| zip with `SPLIT`+`rgb.ktx`+`alpha.ktx` | 5,186 | the engine's split-alpha texture; logical names end in `.png`, `.tga`, `.dds` or `.exr` |
| JSON | 4,733 | templates `.tx`/`.level`/`.layer`, `Design/*.xlsx` tables, localisation, configs (4,043 GCBF + 690 raw) |
| KTX (ETC) | 2,382 | named `*.tga` |
| BRES (`.bdae` model/animation) | 2,214 (+391 zipped `little_endian_quantized.bdae`) | |
| raw TGA | 319 | |
| other binary | 226 | `.trib` particle data, `.StaticGeo`, `.NavMesh`, `.VisibilityGraph`, `.StaticMeshCollider`, `.glotevents` (encrypted) |
| GLSL | 155 | `Shaders/**/_VS.glsl`, `_FS.glsl` |
| PVS0 / Irr2 / LMMI | 20 / 16 / 15 | `Levels/<L>/<L>.pvs`, `.irr`, `Baked/Levels/<L>/<L>.Lightmap` |
| DKIF (IVF video) | 21 | `Menus/assets/video/*.ivf` |
| Voxa sound packs | 7 | `Audio/{setup,sfx,music,base,embedded,vo_en,vo_fr}.voxpack` |
| TrueType | 10 | `Menus/fonts/*.ttf` |
| swf-idx (`idx…FsCd` + uncompressed FWS) | 5 | `Menus/UI.swf`, `GUI.swf`, `_library.swf`, `LoadingGeneric.swf`, … |
| `.StaticGeo.Layer<n>` zips | 15 | `batch.zip`, `streaming.grid`, … |
| XML (`.seq`, TechniqueMap) | 24 | |

## 4. Name recovery

Method (`decode.py`, fully automatic, about 3 min): harvest every string from every decoded file
(parsed JSON keys and values, printable ASCII/UTF-16 runs in binaries, zip member names, 14.9 M strings,
cached), 20.9 k C# literals from `decompiled/`, and 68 k native strings. Then generate candidates and
iterate to a fixed point. A candidate is **accepted only if** its CRC is an on-disk name **and** its
extension matches the sniffed content kind (`.tx`/`.level`/`.xlsx`/`.layer` must be JSON, `.bdae` must
be BRES, `.png` must be a texture kind, and so on). Low-confidence rules must additionally fit the
ext→kind distribution seen in the literal names. Localisation candidates must be an `Id`/`Text` list.

| rule | names | source of the pattern |
|---|---|---|
| literal (normalised: `\`→`/`, strip `./`, `Data/`, `@…`) | 11,957 | content, C#, native |
| ext-swap | 428 | `X.tga`↔`X.png`, `X.bdae`→`X.tx`, … |
| loc-table `Localization/{table}_{lang}.json` | 450 | `EN/LocalizationFormatterComponent.cs:754-761` (`{table}_{lang}` or `{lang}_{table}`) |
| layer `<dir>/<name>.<Layer>.layer` | 78 | `Gameloft.Templates/JsonTemplateReader.cs:190-206` (`@layers`) |
| relative (token joined with the referencing file's dir) | 102 | |
| code-pattern | 111 | `<lightmap>_<i>.tga/.lowend.tga/.dirirr.tga` (`EN/LightmapComponent.cs:289-294`), `.StaticGeo.Layer<n>` (`EN/StaticBatchingComponent.cs:604`), `.low.dds` (`EN/IBLProbeComponent.cs:100`), jersey rigs (`GM/JerseyComponent.cs:121-126`), minimap, … |
| baked-component `Baked/<level>.<ComponentName>` | 39 | `EN/BakeableExtension.cs:28,51`. Gives `NavMesh`, `VisibilityGraph` and `StaticMeshCollider` for 13 levels |
| baked | 2 | |
| rare-kind `Audio/<name>.voxpack` | 6 | native strings `"Audio/"`, `".voxpack"` (≈0x12e8…); brute force limited to the 7 Voxa files |
| folder-x-file | 9 | e.g. `FontDataFolder "Menus/fonts"` × `"TCFNoli-SemiBold.ttf"` |
| numbered / suffix-swap / dir-x-basename (low) | 245 / 439 / 355 | `_00`→`_01`, `_bc`→`_nm/_rom/_tint/…`, unresolved basenames in known dirs |

**False-positive audit** (`tools/datapack/audit_fp.py`): each rule was re-run with decoy candidates of
identical volume (a salt inserted before the extension), twice per rule. Literal (1.59 M tests), ext-swap,
loc-table (3.2 M), relative and numbered (6.3 M) let through 0 decoys. Suffix-swap (30.6 M tests) let
through 0 and 2, and dir-x-basename (3.7 M) 0 and 1. Expect roughly 1-2 wrong names among the
1,048 low-confidence names. `names.json` carries `rule` and `confidence` for every file. **[V]**

Coverage by kind (named/total): JSON 4,323/4,733; split textures 4,638/5,186; KTX 2,098/2,382;
BRES 2,093/2,214 (+390/391 zipped); TGA 289/319; bin 173/226; GLSL 119/155; IVF 21/21; Voxa 7/7;
fonts 9/10; swf 5/5; PVS 11/20; Irr 10/16; Lightmap 9/15; StaticGeo layer zips 9/15; XML 16/24.

**What is left (1,524).** 548 split textures, 284 KTX, 121 BRES, 410 JSON (110 are 2-byte `{}`;
105 are second copies of localisation tables, older 838-row `menu`/`crm`/… or alternate zh/ja builds,
in an unknown folder; the rest are unreferenced templates such as menu-camera layers and FIFA-skin
variants), 53 bin, 36 GLSL. Also baked data for 3 levels that are not shipped (pvs, irr, Lightmap,
NavMesh, …). For unnamed JSON, `names.json` gives a `hint` (`@base`, top keys, "localization
table"). **[V]**

`_unresolved_refs.json` lists 141 paths that JSON/XML content references but the pack lacks. They are
mostly textures referenced by templates that probably fall back at runtime, plus the
`Menus/sounds/archive_build/*.voxpack` names from Settings (the engine actually loads
`Audio/<name>.voxpack`). **[V]**

## 5. Content inventory

### 5.1 Root, config and default templates (`datapack-decoded/`)

| path | what |
|---|---|
| `MCVS.game` | GameDefinition. Settings come from `Config/Settings.tx`. Levels: `Levels/Connection.level`, `Levels/Port/Port_Tutorial.level`, `Levels/Menus.level`. Profile selector `_mcvs`. |
| `Config/Settings.tx` (← `Config/DefaultSettings.tx`) | `DatabasePaths`: Meta/Gameplay/AI/Notifications `.xlsx`. `PathsSettings`: CRM default `Menus/stores/InitialFeed.dat` (**absent**), fake billing `Menus/stores/iap/FakeIOSBillingData_CRM.json`, `Security/AntiCheat.json` (**absent**), fonts and localisation. |
| `Config/crmpreconfiguration.json` | default/fallback CRM (§6.1) |
| `Config/game_object.json` | ecomm item catalogue (14,363 items, §5.5) |
| `Config/glue.json` | native glue: languages, `clientID 2620:66102:1.17.32:android:googleplay`, revision 162074 |
| `Config/packs.json`, `Config/regions.json`, `Config/snsconfig.json`, `Config/Assemblies.mjson`, `Config/GLOT/Events*_android.glotevents` (encrypted) | |
| `SceneComponents.tx` | client/server link table: `ts` (QuickLaunch, port 51737, `InitialLevel Levels/Menus.level`, `AllowP2P false`), `quickMatch` (Automatch, gs, 51738, `AllowP2P true`), `privateMatch` (gs, 51738, P2P). All use `ServerLocation: Auto`, `Host mda-anubis-gsa001.gameloft.com`, `ServerIp 127.0.0.1`. `/MetaGame/Mercs.PracticeLevel = Levels/Bootcamp/Bootcamp.level`. |
| `LevelComponents.tx` | base GameObject merged into levels (`GameplayComponent`, `GameMode: PVP`, HUD and mode parameters) **[I]** |
| `DefaultProfile.tx` | new-profile template: Currencies `{SeasonTickets 0, HardCurrency 0, Credits 0}`, `MatchMakingRating 1500`, squad `["1"]`, `StartingInventoryTableName StartingInventory`, `PlayerLevelTable PlayerLevelXP`, League/Divisions/Gears/DailyShop/SubscriptionCard components pointing at `Design/MCVS_Meta.xlsx`, `GameVersion "1.5.4"`, `Version 4` |
| `DefaultCrm.tx` | `GameUnifiedStoreComponent`, `GameIapStoreComponent{GameObjectsPath: Config/game_object.json}`, `GameOfflineStore`, ads app ids |
| `DefaultClan.tx`, `DefaultSocial.tx`, `DefaultMessaging.tx` | default clan, social and messaging templates |
| `GameOptions.json`, `GameOptions_android.json` | graphics option profiles |
| `Menus/stores/iap/FakeIOSBillingData_CRM.json` | `products[]` with `content_id`, title and USD price |
| `Localization/*.json` | 30 tables × 15 languages (`ar de en es fr id it ja ko pt ru th tr zh zt`), plus `Remap.json`, `Credits.html`, `default_ban_messages.json`, `ban_tool_custom_messages.json` |

Not in the pack, though code references them: `Config/EdgeList.txt` (`Gameloft.Util/Geolocalization.cs:91`,
developer IP ranges; there is a URL fallback), `Menus/stores/InitialFeed.dat`, `Security/AntiCheat.json`,
`Config/XtraConfig.xtra`, `Config/GlitchConfig.xtra`, `Packs/*.pak` (from `packs.json`), and
`Levels/Gym/*.level`. **[V]**

### 5.2 Design tables (`Design/MCVS_*.xlsx`, JSON `{Table: {Fields:[…], Rows:[[…]]}}`)

The full list with row counts and columns is in `datapack-decoded/_tables.json`.

| database | tables | highlights |
|---|---|---|
| `MCVS_Meta.xlsx` (1.6 MB) | 541 | `LootCrates` (247) and about 200 `Crate_*` content tables (65 `Crate_Event_N`), `SkinsListPerMerc` + 21 `SkinsList_MercNN`, `CamosListPerWeapon` + 21 `CamosList_<weapon>` (about 220 rows each), `MercAccessories` (280 jerseys), `KillsignatureAvatars` (1,518), `KillsignatureBackgrounds` (35), `KillsignatureSounds` (5), `StartingInventory`, `StartingKillSignature`, `PlayerLevelXP`/`PlayerLevelUnlocks` (50), `PVPLeaguesList` (123), `DivisionsList` (17), `RankUpReward_League*` (122), `PVPDivisionRewards`, `Agent*MasteryRewards` (21), `DailyShop_*` (Cosmetics 1,140, VIP 1,140, Crates 18), `Gear*`, `Slot*Stat`, `ClanEmblems` (30), `CurrenciesImages`, `SoftCurrencyConversion`, `SubscriptionCard_*`, `Top_Up_*`, `FTUE_*`/`FIRSTVISIT_*`, `BotMatchesInfo`, `BotMatchLimitPerDay` (3), `BotMatch1Win/LossRewards` (5/0 Credits), `MenuMatchInfos` (QUICKPLAY, BOTMATCH), `MatchmakingBotAddingTime`, `IsCRM` ("false"), `SquadSize` (3), `Update9ToXCosmetics` |
| `MCVS_Gameplay.xlsx` (0.4 MB) | 253 | `MercsList` (22 rows: id 0 is the "Switch" placeholder, then 21 agents), `Mercenaries_Stats`, `MercsWeapons` (34), `WeaponsList` (20), `Merc*_Upgrades/_MetaUpgrades/_Tooltips`, `Mods_Merc01`, `ModsTable` (95), `Abilities` (23), per-ability tables, `Cards` (9 score-streak cards), `MapsList` (14), `MatchTypeSpecs` (7), `MatchTypeProbability`, playlists `Regular`/`Competitive`/`Special`/`Blitz`/`ScoreCharge` (10 each), `Arena*`, `*MatchSettings[Default]`, `*SpawningSettings`, `<Map>ZoneSettings`, `Medals` (45), `WeaponBakedStats*` |
| `MCVS_AI.xlsx` (76 KB) | 51 | `PVPBots` (MMR → difficulty: 0→2 … 2500+→7), `BotDifficulty` (8 levels), `SoloBots`, `AllowedBots`, `BotNames` (293), `TutoMatches` (30 onboarding matches: map, squad, mode), `Match1`…`Match30` (scripted bot rosters), `WeaponBehaviour`, `CommanderSystem`, `Grunt*` |
| `MCVS_Notifications.xlsx` | 1 | `PushNotifications` |
| `MCVS_System.xlsx` | 1 | `GameFeatures` (`RedeemCoupons` 0) |

Where the analysts' references live: `BotMatchesInfo`, `MenuMatchInfos` and `BotMatchLimitPerDay` are in
Meta. `PVPBots` and `BotDifficulty` are in AI. `DefaultProfile.tx` is at the root. `Templates/AI/Commander.tx`
exists. `Levels/Gym/Gym_PVP.level` does **not** exist, and the shipped `SceneComponents.tx` replaces
it with Bootcamp. `Config/EdgeList.txt` is absent. **[V]**

### 5.3 Levels, maps, modes

* 14 `.level` files: `Levels/{Connection,Menus,New}.level` plus `Levels/<M>/<M>.level` for
  `Bootcamp, Desert, DogDays, Favelas, Port (+Port_Multi, Port_Tutorial), Rooftop, Snow, Stadium,
  Stadium2`. There are 80 `.layer` files (`<level>.<Layer>.layer`: Ally, Audio, Cinematic, Gameplay,
  VFX, Lighting, ClientSide, …) and 15 `.seq` sequences.
* `MapsList`: the playable PVP/Tournament/BotMatch maps are Snow, Desert, Favelas, Rooftop, DogDays,
  Port (→ `Port_Multi.level`), Stadium and Stadium2 ("aftermath"). Barrage, Bridge and Downtown are
  listed but have no level files and no `AvailableInGameModes`.
* Modes (`MatchTypeSpecs`): ControlPoint, Uphold, Dominion, TeamDeathMatch (CaptureFlag inactive), plus
  the playlist/arena variants (`ArenaScorecharge{ZoneControl,TDM,Warzone,Bounty}`, `ArenaBlitz`,
  `ArenaHavoc`, `ArenaOverride`, `ArenaAmbush`, `ArenaGrandBounty`, `ArenaTripleElixir`).
* Baked per-level data (§4):
  * `Baked/<level>.{NavMesh,VisibilityGraph,StaticMeshCollider}` exists for all 13 shipped levels,
    including Menus, Connection, Port_Multi and Port_Tutorial.
  * `.Lightmap` (+ `Lightmap_<i>{,.lowend,.dirirr}.tga`) and IBL `.dds` exist for the 9 maps.
  * `Levels/<L>/<L>.pvs` exists for 11 levels and `.irr` for 10.
  * `.StaticGeo` (+ `.LayerN` zips) is named for 5 maps. The other 4 are among the unnamed bins.

  The AI navigation data that offline bots need is therefore local. **[V]**

### 5.4 Item catalogue (`datapack-decoded/_catalog.json`)

Built from `game_object.json` (canonical ids) joined with the design tables, English localisation and
the default CRM. Every item has `unlock[]` (every shipped source: table and column, crate, price,
weight) and a summarised `acquisition` / `exclusive` (crate-only, iap-only).

| category | count | notes |
|---|---|---|
| agents (mercs) | 22 (21 + placeholder) | rarity, weapon, ability/ultimate names. Unlock via offline store (12), IAP agent packs (14), shop crates (19), league rank-up (2), starting inventory (Lock) |
| skins | 100 | 63 in `SkinsList_*` (21 defaults) plus 37 that exist only in ecomm. Sources: event crates 23, legacy compensation 35, offline store 3, IAP 1, league 1 |
| weapons | 34 | 21 default plus 13 alternates unlocked by fragments (`MercWeaponFragment_*`, `AlternateType VersusCoins ×100`) |
| camos | 4,668 | 4,619 table rows (21 weapons × about 220) plus 49 ecomm-only. 210 Kayac-only. Daily shop 570, event crates 1,050, 1,113 crate-only |
| jerseys (accessories) | 280 designs (5,906 per-agent ecomm ids) | daily shop 30, event crates 50, Kayac 32, 152 with no shipped source |
| mods | 655 ecomm ids | tier-based random rewards (`ModTierRewardItem`: a tier-R reward picks a mod with tier ≤ R) |
| kill signatures | 1,558 | 1,518 avatars, 35 backgrounds, 5 sounds. 60 avatars and 17 backgrounds are sold in `offline_store`, 40 avatars come from mastery, 1,410 avatars have no shipped source |
| gear | 226 items, 6 slots, 6 tiers, 8 stats, 109 slot unlocks | daily gear crates (`Crate_Gear_<Weekday>`), league and mastery rewards |
| clan emblems | 30 | free choice |
| currencies | 12 | HardCurrency (diamonds), SoftCurrency, AgentsCurrency, VersusCoins(_NoMult), Credits(_NoMult), UpgradeTokens, SeasonTickets, Shards, PlayerXP |
| crates | 251 | `LootCrates` definitions plus ecomm-only crates. 13 are directly purchasable for hard currency (`LootCrates.Cost` > 0 or the daily shop) |
| trial agents | 270 | `TrialMerc_<id>_<hours>[_TiersN]` |
| VIP/subscription | 1 | `SubscriptionCard_30` plus 30-day milestone tables |
| IAP products | 58 | from the CRM preconfig `iap.en` (USD prices, bundle contents) |
| other | 9 score-streak cards, 23 abilities, 45 medals, 14 maps, 7 match types, 17 divisions, 123 career leagues, 50 player levels | |

The ecomm categories in `game_object.json` are: mercaccessory 6,211, weaponcamo 4,912, killsign 1,554,
mods 655, trial_merc 270, gear 226, crate 127, Gear_unlock 109, merc_weapon 102, merc_unlock 92,
merc_skin 79, reset 10, soft_currency 8, bundle 4, hard_currency 2, player_xp 1, subscriptions 1.

"No shipped source" means the live server granted the item through CRM gifts, events or crates whose
contents only the server knew. An offline server must invent a policy for these items, for example
unlock-all or putting them in crates. **[I]**

## 6. What matters for the offline server

### 6.1 The default CRM is in the pack
`Config/crmpreconfiguration.json`, top-level keys:
* `pointcut_actions`: 1 action (`GUYANA_ADS_test_IV_MCVS_zhira`, `show_ad_button`) with 14 pointcuts.
  Harmless.
* `game`:
  * `AgeGateParams{Ignore:false}`, `CrashlyticsParams{Enable:true}`
  * `tags` (`WORLD_GAME_Default`, …)
  * `_OverrideTables`: `MCVS_Gameplay` 186 tables and `MCVS_Meta` 896 tables, 6,092 columns, **all
    `{}`**. This is the schema of what the CRM can patch, not actual patches.
* `offline_store`:
  * `prices`: 95 entries, `{item, quantity, billing_methods:[{type:"offline", price:[{currency, price}]}]}`
    in HardCurrency/AgentsCurrency. They cover soft-currency packs, 12 agent unlocks, 77 kill-signature
    avatars/backgrounds and 3 skins.
  * `tags`: `WORLD_ITEMS_Default`
* `iap`: one entry per language for 18 languages (`en ar de es fr id it ja ko pl pt ru th tr vi zh zt
  fa`). Each has `prices` (58 for `en`: HC packs, XP packs, starter kits, agent packs, league bundles,
  weekly/monthly deals, with `content_id`, USD price, `bundle[]`), plus `store_info` and `tags`.
* **No `groupware`** key, so no time-limited events by default.

Loading: `CRMComponent.LoadPreconfiguration()` (`EN/CRMComponent.cs:394-404`).
`RefreshCrmConfig` uses it when `DoNotLoadConfig` is set (l. 433-436), **and as the fallback when
`GetStandardConfiguration` throws `IOnlineRequestException`, `OperationCanceledException` or
`CrmSection.InvalidDataException`** (l. 450-466). In both cases it picks `iap[<profile language>]`.
`StandardCrm.Data` reads `offline_store` (`Gameloft.OnlinePlatform/StandardCrm.cs:104-106`). A local
federation stub can therefore serve this file as `configs/users/me` (or just fail the request),
and the game gets real shop prices without any patch. **[V]** for the code path, **[I]** for the
runtime behaviour.

### 6.2 Other server-side data that the pack provides
* `Config/game_object.json`: the ecomm item catalogue that `GameIapStoreComponent` loads
  (`DefaultCrm.tx`). Use these ids for the inventory and for `delivery: "profile"` grants.
* `DefaultProfile.tx` + `StartingInventory` (Credits 0, HardCurrency 0, KillSignature Avatar 1 /
  Background 1 / Sound 1, UnlockMerc 1): everything a brand-new profile needs.
* Economy constants: `SoftCurrencyConversion`, `*_To_HardCurrency_Conversion`, `CreditsForACrate`,
  `FreeCrate` (`Crate_Free_Steam`, 14,400 s), `FreeSchematic`, `ChangeNameCost`, `BotMatchLimitPerDay`,
  `CreditsCurrencyMatchRewards` and `VersusCoinsMatchRewards` (per division), `PlayerLevelXP`,
  `TrophiesRange`, `PVPShiftingWindow`/`PVPWindowPoints`.
* Bots: `MCVS_AI.xlsx` holds the MMR→difficulty curve, per-difficulty aim/strafe/respawn parameters,
  293 bot names and the 30 scripted onboarding matches. `MatchmakingBotAddingTime` sets when bots fill
  a lobby. Every shipped level has local NavMesh/VisibilityGraph data.
* Network defaults (`SceneComponents.tx`): link ids `ts`/`quickMatch`/`privateMatch`, ports 51737/51738,
  `ServerLocation: Auto`, gs links `AllowP2P: true`.

### 6.3 What the pack does *not* provide
No TLE/event definitions (`groupware`, `social events?event_ids=`), no `InitialFeed.dat`, no EdgeList,
no anti-cheat config, no leaderboard or season data, no inbox/gift templates (only localisation),
no Gym practice level. Event crates exist (`Crate_Event_1..65`, `Crate_Event_Special`), but their
schedule came from the server. **[V]**

## 7. Surprises
* The first entry of the seek table, `0`, looks like a "flags" field, and `8+8N` looks like a
  header-size field. Neither is a field: together they are seek-table entry 0. The `padA`/`Adap` FourCC
  is a per-block "adapter" header, so different blocks could in principle use different codecs.
* Block CRCs use **init value 1** (`crc32(1, …)`), not zlib's usual 0. Names use init 0.
* Most "textures" are not GCBF at all. They are raw zips (`SPLIT` + `rgb.ktx` + `alpha.ktx`) named
  `.png`/`.tga`. KTX files that are GCBF-wrapped come in exactly 8 blocks.
* `Design/*.xlsx` files are JSON, not spreadsheets.
* The game and settings point at `Menus/sounds/archive_build/*.voxpack`, but native code opens
  `Audio/<name>.voxpack`. Seven packs exist, including `vo_en`/`vo_fr`, `base` and `embedded`.
* About 105 localisation tables exist twice with different content (older or alternate builds). Their
  second location is unknown.
* `crmpreconfiguration.json` ships IAP catalogues for `pl`, `vi` and `fa`, three languages the
  game's localisation does not have.

## 8. Reproduce
```sh
cd /home/user/mcvs-work
tools/venv/bin/python -I tools/datapack/validate.py datapack/1.17.32/ETC/files/Data      # container
tools/venv/bin/python -I tools/datapack/names.py Config/Settings.tx DefaultProfile.tx    # hash
tools/venv/bin/python -I tools/datapack/decode.py --clean                                # tree + names.json
tools/venv/bin/python -I tools/datapack/build_catalog.py                                 # _catalog.json, _tables.json
tools/venv/bin/python -I tools/datapack/audit_fp.py                                      # resolver FP audit (~20 min)
tools/venv/bin/python -I tools/datapack/dis.py 0x57de58 420                              # hash builder disassembly
```
