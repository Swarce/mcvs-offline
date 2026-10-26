# Design: the Cosmetic crate ("economy collector")

Status: **design, ready to implement** (October 2026, `main-dev` `e4bff79`). Roadmap item "economy polish". This document is
the contract between the work packages in §6: anything another package relies on (content file schemas, CRM cells,
C# signatures, settings keys, test names) is fixed here. Change it only through the integrator (WP-I).

Evidence: `polish-research.md` (section *economy*) and its scratch scripts; the decoded 1.17.32 data pack
(`/home/user/mcvs-work/datapack-decoded`, `Design/MCVS_Meta.xlsx`, `Design/MCVS_Gameplay.xlsx`, `_catalog.json`); the
decompiled client (`/home/user/mcvs-work/decompiled`; `GM/` = Game, `EN/` = Gameloft.Engine). Every count below was
recomputed for this document (scripts and outputs in the session scratchpad `collector/`: `pools.py`, `verify.py`,
`pace.py`, `cells.py`, and `bench/`, a net8 run of the **real** `Gameloft.Engine.dll` and `Game.dll` table code over the
real `MCVS_Meta` tables).

## 0. Decisions

### 0.1 Owner decisions (binding, not reopened)

| # | Decision |
|---|---|
| O1 | One server-defined **Cosmetic crate** built from the unused `Crate_Event_Special` (its `LootCrates` row and its 100-row content table, filled with CRM `_OverrideTables` SET cells). No IL patch. |
| O2 | Sources: the daily-mission bonus, the weekly-event milestones and final, the division season rewards of ranks 4–30. |
| O3 | `economy.collector.itemsPerCrate`, default **3**, runtime setting, 1–10. |
| O4 | Random crate only (no choice shop now). |
| O5 | Pools: the base no-source set (avatars, backgrounds, sounds 2–5, the 16 skins, the 12 hidden jerseys) **plus** the VIP-only camo and jersey pools **plus** explicit rows for Witch (17) and Bli-P (21). **Kayac/Kayac2 emblems are not in the crate** (they stay VersusCoins-only). |
| O6 | Season rewards: ranks 4–30 get 1–4 Cosmetic crates by division band; 1st–3rd keep their HardCurrency and additionally get that season's emblem (`BundledRewards`); ranks 31+ unchanged. |
| O7 | Everything opt-out with `economy.collector.enabled=false`, which restores **exactly** today's behaviour. |
| O8 | Weights must deliver the no-source items at a good pace despite the large VIP pool. |

### 0.2 Design decisions taken here

| # | Decision | Why (details in the section) |
|---|---|---|
| D1 | **Items per crate = `LootCrates.CratesCount`**, with `RewardChance.2` set to 0. Not `RewardsCount`. | The client overwrites `RewardsCount` from the `RewardChance.N` cells (§2.1, verified with the real `Game.dll`). |
| D2 | **Tiered weights with a guaranteed base share** (default 75 % of draws go to the no-source tier while it has items), proportional to the player's remaining items inside each tier, recomputed **per player** from the game-profile dump. | §4.1. A "guaranteed base row per crate" cannot be expressed: every sub-crate draws with `Weight.1` (§2.1). |
| D3 | Ranks 4–30 are served by **retargeting the 14 tier crates** (`Crate_<Tier>_4To10Place`, `_11To20Place`) to the Cosmetic crate's content table, with `CratesCount = band × itemsPerCrate`. `PVPDivisionRewardsType`/`PVPDivisionRewards` stay untouched. | 84 cells instead of 918; `liveops.seasonRewardMultiplier` keeps working unchanged; identical display (§2.4). |
| D4 | The 36 starred season emblems (`I_1`…`XII_3`) are **reserved** for 1st–3rd place and are not in the crate; the 12 unstarred numerals are. | Otherwise the top-3 bonus would mostly be paid as duplicate gear for the players who collect fastest (§1.3). Switch: `reservePlacementEmblems` in the content tuning. |
| D5 | Live-ops rewards are gated by a content date **`collector.since`** (a season start), **not** by a `revision` bump. | A bump changes every event's `_version` and resets the running day and week for everybody at install time (§3.3). |
| D6 | The Unlockable "window" covers the **whole** remaining set by default (≤ 142 + 75 + 6 cells). | With Kayac excluded the set is small; measured cost ~12–18 ms warm on net8 (§2.10). A window size setting remains as a safety valve. |
| D7 | When every collectible is owned, each draw pays **70 VersusCoins** (`VersusCoins_Tiers4` row). | Unlock-all profiles get something useful instead of gear spam and the crate is never "unavailable" (§5.1). |

### 0.3 Corrections to the research (verified for this design)

1. **`RewardsCount` is not a usable knob.** `GM/CratesSpecsCollection.cs:17-40` calls `CrateSpecs.SetRewardChance` for every
   non-empty `RewardChance.N` cell, and `GM/CrateSpecs.cs:81` then sets `RewardsCount = RewardsChances.Count(x => x > 0)`.
   Every `LootCrates` row has a `RewardChance.2` cell, so `RewardsCount` ∈ {1, 2} whatever its own column says. Bench
   output (real Game.dll): `Crate_Event_Special` today: `RewardsCount=2 CratesCount=1 chances=100/100`; after our cells:
   `RewardsCount=1 CratesCount=3 chances=100/0`. The shipped "N items" crates use `CratesCount` too (`Crate_Event_<n>_x10`:
   `CratesCount=10`).
2. **Ranks 4–30 do not get LastChance tokens today.** The tier crates have `RewardChance.2 = 100`, so round 1 draws with
   `Weight.2`: ranks 4–10 get one `Gear_Random_3_Random`, 11–30 one `Gear_Random_2_Random`; 1st–3rd get the
   HardCurrency row plus `Gear_Random_4` (Starter/Bronze/Silver) or `Gear_Random_5` (Gold and up).
   `PlayerLevelXP.SecondRewardChanceMultiplier` is 1 on every level, so round 1 always happens.
3. Shipped `Unlockable=true` backgrounds: **18** (1–6, 24–35), not 23.
4. Kayac = 1,063 avatar ids (1,061 released), including the 60 `Kayac/holidays` emblems and `avatars/Kayac_mirrativ.png`;
   **75 of them are `Unlockable=true` today** (598–672), so a random avatar row would hand them out unless they get
   `false` cells (§1.2).
5. The "VIP pools" are the generic random forms `WeaponCamo_Random_Random` / `MercAccessory_Random_Random`; they draw
   from **every** locked non-Kayac camo/visible jersey of an unlocked agent (9,365 pairs), of which the VIP-only pairs
   are a 5,624 subset. A 100-row table cannot list the 268 VIP-only ids explicitly (§1.5).

## 1. Pools

### 1.1 Released no-source set and exclusions

"No-source" = released (in the `UNLOCKALL` groups of `unlocks.json`) and `content_gen.shipped_sources(entry)` empty,
with visible non-Kayac jerseys counted as sourced by the VIP pool (algorithm of `polish/scripts/inv2.py`).

| Category | No-source (released) | Excluded from the crate | In the crate |
|---|---:|---|---:|
| Avatars | 1,404 | Kayac/Kayac2 1,061 (§1.2); starred season emblems 36 (D4); unreleased 1513–1518 are not released at all | **307** |
| Backgrounds | 18 (18–35) | – | **18** |
| Signature sounds | 4 (2–5) | – | **4** |
| Skins | 16 | – | **16** |
| Hidden jerseys | 12 jerseys × 21 agents = 252 pairs | – | **252 pairs** |
| Agents | Witch 17, Bli-P 21 (they also have the 0.25 % `UnlockMerc_Rarity06` loot-crate row) | – | **2** |
| **No-source total** | | | **599** |

Shared random rows also reach some signatures that do have another source (§1.3): 64 avatars and 5 backgrounds for a
fresh profile. The **base tier** a fresh profile has to drain is therefore **668 draws**.

Paid tier (O5): 4,409 camo pairs + 4,956 jersey pairs = **9,365 pairs** reachable by the random rows (VIP-only subset:
2,684 + 2,940 = 5,624). Everything = 668 + 9,365 = **10,033 draws** for a fresh profile with all 21 agents unlocked.

### 1.2 Kayac / Kayac2 (exact)

Rule (content-gen `collector_tuning.json` `kayac`): the avatar's `Asset` is under `Menus/assets/png/profile/Kayac/`
(including `Kayac/holidays/`) or `Menus/assets/png/profile/Kayac2/`, **or** its file name starts with `Kayac`.

| Part | Ids | Count | Unlockable today |
|---|---|---:|---|
| `Kayac/` | 443–498, 500–531, 533–589, 598–603, 664–672 | 160 | 598–603, 664–672 true (15) |
| `Kayac/holidays/` (`emblem_JP_newyear_*`, `emblem_Xmas_*`, …) | 604–663 | 60 | all true (60) |
| `Kayac2/` | 673–1514 | 842 (1513–1514 unreleased) | all false |
| `avatars/Kayac_mirrativ.png` | 499 | 1 | false |
| **Total** | **443–531, 533–589, 598–1514** | **1,063** (1,061 released) | **75 true: 598–672** |

They stay in the VersusCoins signature shop (the shop does not read `Unlockable`: `GM/GameOfflineStore.cs`
`GetKillSignItems`). Because 598–672 are `Unlockable=true`, the collector sends `ID@Unlockable = "false"` for every one
of them the player does not own (§2.6). Open owner point: the 60 holidays are generic-looking (New Year, Christmas)
but live in the Kayac folder with `emblem_JP_*` names; they are excluded by the rule above and can be moved into the
crate by removing `Kayac/holidays/` from the rule (then 60 more crate avatars, 60 fewer `false` cells).

Not Kayac by this rule but Japanese-themed: 306–321 (`season_rewards/emblem_JP_agent_*`, 16) and 442
(`emblem_SKIN_ONI-MASA`); they are in the crate.

### 1.3 Avatars (307 in the crate)

| Set | Ids | Count |
|---|---|---:|
| Crate avatars (base) | 61–76, 78, 80–90, 92–219, 221–253, 257, 261, 265, 269, 273, 277, 281, 285, 289, 293, 297, 301–321, 362–391, 393–442, 532, 593–597 | 307 |
| … already `Unlockable=true` | 61–76, 78, 80–90, 92–219, 221–224, 593–597 | 165 |
| … `Unlockable=false` → **`true` cells** | 225–253, 257, 261, 265, 269, 273, 277, 281, 285, 289, 293, 297, 301–321, 362–391, 393–442, 532 | 142 |
| Other drawable (shipped `true`, not Kayac, not base) | 1–6, 9–60, 77, 79, 91, 220, 590–592 (1 = starting avatar) | 65 |
| Kayac `true` → **`false` cells** | 598–672 | 75 |
| Reserved starred season emblems (stay `false`) | 254–256, 258–260, 262–264, 266–268, 270–272, 274–276, 278–280, 282–284, 286–288, 290–292, 294–296, 298–300 | 36 |
| Unreleased (never touched) | 1513–1518 (two Kayac2, four Bli-P emblems) | 6 |

By folder the 307 are: generic `avatars/` 228, `season_rewards/` 28 (12 unstarred numerals + 16 JP agent emblems),
`KillSign_Agents/` 51; by rarity R1 167, R2 75, R3 27, R4 38. The "other drawable" 65 are 57 shop-only, 4 event-crate
random-pool and 3 league rank-up rewards (590–592): if the crate hands out one of the latter first, the later rank-up
reward pays its duplicate compensation (one `Gear_Random_1`); accepted.

Static drawable set (what the avatar row can pick once the cells are applied) = shipped-true minus Kayac plus the
142 = **1–6, 9–224, 590–597 ∪ the 142**; minus what the player owns.

### 1.4 Backgrounds, sounds, skins, hidden jerseys, agents

| Category | Ids | Notes |
|---|---|---|
| Backgrounds (base) | 18–35 | 18–23 are `Unlockable=false` → `true` cells (6); 24–35 already true. Other drawable: 1–6 (1 = starting). Drawable after the cells: 1–6, 18–35 (24). 7–17 are shop-only, not drawable. |
| Sounds | 2, 3, 4, 5 | Share sound 1's asset `sfx_ui_crate_reveal`. There is **no random sound form** (`KillSignatureRewardItem.GetAvailableId` has no Sound branch; the `store` IL patch makes it return null), so one explicit row each. |
| Skins (merc_skin) | 2_5, 4_6, 6_4, 8_7, 9_3, 9_4, 10_7, 11_3, 11_4, 12_4, 15_6, 17_6, 17_11, 17_12, 19_4, 20_4 | Explicit `MercSkin/<merc>/<skin>` rows (all `Locked=false` in `SkinsList_MercNN`). Explicit rows, not the random `MercSkin` row: the random one draws any unowned available skin (39), most of which have sources. |
| Hidden jerseys | 112 USSR, 143/144 ARTfire1/2, 152/153 HOKUSAI, 212–214 Spring01–03, 268–271 ID54–57 | `VisibleInShop=false`, `IsKayac=false`, no `MercId`. Every random jersey pool filters `VisibleInShop` (`GM/MercAccessoriesComponent.cs:118-130`), so they need explicit `MercAccessory/Random/<id>` rows: `OnPrepareToClaim` (`GM/MercAccessoryRewardItem.cs:100-118`, `IsRandomMerc && !IsRandomAccessory`) picks a random unlocked agent that lacks that jersey, with no visibility filter. Owned ones display (`NativeGetMercAccessories` lists owned accessories, filtering only Kayac-only ones). |
| Agents | 17 Witch, 21 Bli-P (both `Rarity06`, displayed) | `UnlockMerc/<id>` rows. `OnPrepareToClaim` (`GM/UnlockMercRewardItem.cs:84-91`): a non-rarity subtype unlocks exactly that agent while it is locked. Once owned the row's per-player weight is 0; if it is drawn anyway (stale or static weights) the claim pays `AlternateRewards` `UnlockMerc_Rarity06` = 40 × `Gear_Random_3_Random`. |

### 1.5 Paid tier: camos and jerseys (verified random forms)

| Form | Client code | How it picks |
|---|---|---|
| `WeaponCamo_Random_Random` | `GM/WeaponCamoRewardItem.cs:101-149` | Random unlocked agent (not "0"), then a random camo of its `VanillaWeapon` table that it does not own (`GetWeaponCamosIds` → `WeaponCamoSpecsCollection.GetCamosList`: non-Kayac for a non-Kayac profile); an agent with none left is removed and another drawn. Fails (→ alternate) only when no unlocked agent has a camo left. |
| `WeaponCamo_Random_<camoId>` | same, `!IsCamoRandom` branch | Random unlocked agent lacking that camo (only if the camo is in its weapon's table). Used by `Crate_Event_5`. |
| `WeaponCamo_Random_RarityNN`, `WeaponCamo_<merc>_Random[_RarityNN]` | same | Rarity filter / fixed agent. Not used. |
| `MercAccessory_Random_Random` | `GM/MercAccessoryRewardItem.cs:100-129` | Random unlocked agent, then a random jersey from `GetLockedAccessoriesForMerc` (`VisibleInShop`, not Kayac-hidden, agent-matching, not owned); agent removed when it has none. |
| `MercAccessory_Random_<id>` | same, lines 109-117 | Random unlocked agent lacking jersey `<id>` (no visibility filter). Used for the hidden jerseys. |
| `MercAccessory_<merc>_Random[_RarityNN]`, `MercAccessory_Random_RarityNN` | same | Fixed agent / rarity filter. Not used. |

Pool per agent (verified equal to the `camos`/`accessories` groups of `unlocks.json`, minus the 12 hidden jerseys):
camos 210–212 per agent (202 for agent 21; Kayac camos 156–165 excluded; camo "1" is the default and not in the
tables) = **4,409** pairs; visible non-Kayac jerseys **236** × 21 = **4,956** pairs. The VIP-only pairs (`paidCamos`
2,684, `paidAccessories` 2,940) are a subset that the random forms cannot single out: listing them would need 128 camo
ids + 140 jersey ids = 268 rows (> 100). So the crate's paid tier is "every locked camo/jersey pair of an unlocked
agent", which includes the VIP-only ones; the VC shop's 10-packs draw from the same pools.

### 1.6 Row types and their duplicate behaviour

| Row | Resolves at claim to | When nothing is left / already owned | Alternate (`MCVS_Meta.AlternateRewards`) |
|---|---|---|---|
| `KillSignature/Avatar/Random` | random locked **and** unlockable avatar (`GM/KillSignatureRewardItem.cs:117-143`) | no candidate | `Gear_Random_1_Random` ×1 |
| `KillSignature/Background/Random` | same for backgrounds | no candidate | `Gear_Random_1_Random` ×1 |
| `KillSignature/Sound/<id>` | that sound | owned | `Gear_Random_1_Random` ×1 |
| `MercSkin/<m>/<s>` | that skin | owned, or agent locked | `Gear_Random_3_Random` ×1 |
| `MercAccessory/Random/<id>` | that jersey on a random unlocked agent lacking it | every unlocked agent has it | `Gear_Random_2_Random` ×1 |
| `UnlockMerc/<id>` | that agent | unlocked | `Gear_Random_3_Random` ×40 |
| `WeaponCamo/Random/Random`, `MercAccessory/Random/Random` | §1.5 | nothing left on unlocked agents | `Gear_Random_2_Random` ×1 |
| `VersusCoins/Tiers4` | 70 VersusCoins (`VersusCoinsTiers`, shipped precedent `Crate_VSCoins` row `VersusCoins/Tiers1`) | never | – |

## 2. CRM cells

All cells are `game._OverrideTables.MCVS_Meta.<table>["<index>@<field>"]["<key>"]`, mirrored into
`game._ServerConfig.default._OverrideTables` by the existing builder pass (protocol/content.md item 7). `#` stands for `.`
in field names. Numbers are JSON integers; strings are non-empty JSON strings. Rows: only existing rows can change
(`EN/Database.cs:143-196`, first row whose index field equals the key).

### 2.1 How the crate draws (why `CratesCount`)

* `CrateRewardItem.OnClaimed` (`GM/CrateRewardItem.cs:77`): `Quantity *= CratesCount`, then
  `OpenCrateOnServer(id, profile, Quantity)` → `GenerateCratesRewards` loops `Quantity` sub-crates.
* Each sub-crate runs `GenerateRewards` (`GM/CratesInventoryComponent.cs:530-585`) for `RewardsCount` rounds; round *i*
  uses weight column *i* while `i < WeightsCount` (2 here), else column 0 (line 548). With `RewardChance.2 = 0`,
  `RewardsCount = 1`: every item is one `Weight.1` draw. Draws are not filtered by availability; random subtypes resolve
  at claim (§1.6).
* A crate that yields nothing at all falls back to `Crate_LastChance` (upgrade tokens), lines 1440-1450.
* UI: `rewardInfo["count"] = CratesCount × Quantity`, `contentDesc` = `menu.ShopCrateContent` ("CONTAINS {n} ITEM(S)";
  "WithBonus" only when round-1 chance > 0), `preview` = rows with `Weight.1 > 0` (`GetPreview`, lines 790-895; owned or
  locked-agent `MercSkin` rows are hidden by the client itself).
* Turning-point modifiers (`CrateItemsAbove/BelowTPMultiplier`) match by `RewardId`; none of ours match and every
  `CrateTurningPoints.PowerProgression` is 0. No effect.

### 2.2 `LootCrates` row `Crate_Event_Special` (3 cells, static)

| Field changer | Key | Value | Today |
|---|---|---|---|
| `Id@LocId` | `Crate_Event_Special` | `"Crate.Cosmetic_Crate"` ("COSMETIC CRATE") | `menu.LegendaryPremium` |
| `Id@CratesCount` | `Crate_Event_Special` | `itemsPerCrate` (1–10) | 1 |
| `Id@RewardChance#2` | `Crate_Event_Special` | `0` | 100 |

Image (`crate_Legendary_Open.png`), Rarity06, ShopType Loot stay. The crate is not sold anywhere (cost cells unused).

### 2.3 Content table `Crate_Event_Special` (39 rows, 153 cells)

Rows 1–39 get `Id@Type`, `Id@Subtype`, `Id@Subtype2` (omitted when "–") and `Id@Weight#1` (always sent, 0 allowed).
`BundledRewards`, `Weight.2`, `FixedQuantity`, `AlwaysGiveBundledRewards` are never sent. Rows 40–100 keep a null
`Type` and are skipped (`CrateRewards.AddElement`). Never send `""` (CRMTableCell throws `FormatException`) or null
(skipped). Numeric-looking strings (`"133"`, `"2"`) become doubles in the cell; the typed reader converts them back with
`Convert.ChangeType(…, InvariantCulture)` → `"133"` (verified on the bench: `RewardId=MercAccessory_Random_112`).

| Row ids | Category | Type | Subtype | Subtype2 |
|---|---|---|---|---|
| 1 | avatars | `KillSignature` | `Avatar` | `Random` |
| 2 | backgrounds | `KillSignature` | `Background` | `Random` |
| 3–6 | sounds | `KillSignature` | `Sound` | `2`, `3`, `4`, `5` |
| 7–22 | skins | `MercSkin` | merc | skin, in the order of §1.4 (7 = `2`/`5` … 22 = `20`/`4`) |
| 23–34 | hiddenJerseys | `MercAccessory` | `Random` | `112`, `143`, `144`, `152`, `153`, `212`, `213`, `214`, `268`, `269`, `270`, `271` |
| 35, 36 | agents | `UnlockMerc` | `17`, `21` | – |
| 37 | camos | `WeaponCamo` | `Random` | `Random` |
| 38 | jerseys | `MercAccessory` | `Random` | `Random` |
| 39 | exhausted | `VersusCoins` | `Tiers4` (setting `exhaustedReward`) | – |

`Weight#1` values: §4.1 (static document: §4.2; per player: §2.9).

### 2.4 Ranks 4–30: retarget the 14 tier crates (84 cells, static)

Divisions map to tiers: 1 Starter; 2–4 Bronze; 5–7 Silver; 8–10 Gold; 11–13 Platinum; 14–16 Diamond; 17 Master
(`PVPDivisionRewardsType`, ranks 4–10 → `Crate_<Tier>_4To10Place`, 11–30 → `Crate_<Tier>_11To20Place`, quantity 1).
For each of these 14 crates (`LootCrates` rows 211–242), with `band` from `economy.collector.seasonCrates`
(default Starter 1, Bronze 1, Silver 2, Gold 2, Platinum 3, Diamond 3, Master 4):

| Field changer | Value |
|---|---|
| `Id@ContentTableName` | `"Crate_Event_Special"` |
| `Id@CratesCount` | `band × itemsPerCrate` |
| `Id@RewardChance#2` | `0` |
| `Id@LocId` | `"Crate.Cosmetic_Crate"` |
| `Id@Image`, `Id@ImageShop` | `"Menus/assets/png/Crates/Crates/crate_Legendary_Open.png"` |

A band of 0 for a tier leaves its two crates alone (today's single gear piece). The rank → crate tables and the
`liveops.seasonRewardMultiplier` MUL cells on `PVPDivisionRewards` (quantity) keep working: quantity *q* opens
*q* × `CratesCount` sub-crates. Ranks 4–30 lose today's single random gear piece (accepted: it is what the owner's
"1–4 Cosmetic crates" replaces). These 14 crates are handed out by nothing but `PVPDivisionRewardsType`
(`polish/scripts/crates.py`).

Rejected alternative (the research's literal proposal): SET `PVPDivisionRewardsType` `Rank@Division01..17` for ranks 4–30
to `"Crate_Event_Special"` and `PVPDivisionRewards` to the band — 2 × 27 × 17 = **918 cells**, the season multiplier
would have to be folded into SET cells (a SET cell is never multiplied, `CombineMul`), same display.

### 2.5 Ranks 1–3: the season emblem (42 cells, static per season)

On row `1` of each `Crate_<Tier>_FirstPlace`, `_SecondPlace`, `_ThirdPlace` content table (21 tables). Row 1 is the
only `Weight.1 > 0` row in all 21 (HardCurrency, 60–360; verified), so round 0 always draws it and the bundle follows
(`GenerateRewards` adds `BundledRewards` after the drawn row; `AlwaysGiveBundledRewards` makes it unconditional):

| Field changer | Key | Value |
|---|---|---|
| `Id@BundledRewards` | `1` | `"KillSignature_Avatar_<id>"` |
| `Id@AlwaysGiveBundledRewards` | `1` | `"true"` |

Emblems (`season_rewards/`, `Rarity04`; ids are in alphabetical order of the numerals): base = no star, `_1` = ★,
`_2` = ★★, `_3` = ★★★ (decoded textures). **1st place → ★★★, 2nd → ★★, 3rd → ★**.

| Numeral | I | II | III | IV | V | VI | VII | VIII | IX | X | XI | XII |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| base | 253 | 257 | 261 | 265 | 273 | 277 | 281 | 285 | 269 | 289 | 293 | 297 |
| ★ / ★★ / ★★★ | 254/255/256 | 258/259/260 | 262/263/264 | 266/267/268 | 274/275/276 | 278/279/280 | 282/283/284 | 286/287/288 | 270/271/272 | 290/291/292 | 294/295/296 | 298/299/300 |

Numeral = `I…XII[(LiveOpsSchedule.Ordinal(endedSeason)) mod 12]`, `endedSeason = new LiveOpsWindow(season.Start −
season.Days, season.Days)` where `season` = the snapshot's current season. Example: the document served during the season
starting 2026-10-26 (ordinal 147) names the season that ended (146) → III → 1st 264, 2nd 263, 3rd 262; from 2026-11-02:
IV → 268/267/266. Emitted only while live-ops division seasons are on (`economy.collector.seasonEmblems`, default true).

### 2.6 `Unlockable` cells and the per-player window

| Table | Field changer | Static document | Per-player document |
|---|---|---|---|
| `KillsignatureAvatars` | `ID@Unlockable` | `"true"` for the first `unlockableWindow` ids (ascending) of the 142; `"false"` for 598–672 | `"true"` for the first `unlockableWindow` of the 142 **the player does not own**; `"false"` for the unowned of 598–672 |
| `KillsignatureBackgrounds` | `ID@Unlockable` | `"true"` for 18–23 | `"true"` for the unowned of 18–23 |

`unlockableWindow` defaults to 160 (≥ 142, so the whole remaining set); 0 sends no `true` cells (the avatar row then
draws only from the 165 + 65 already-unlockable ones). The Kayac `false` cells are not windowed: they enforce O5.
Sizes: ≤ 142 + 75 + 6 = 223 cells for a new profile, 0 for an unlock-all profile. What else reads `Unlockable`:
level-up rewards (`AvatarRewards`, 0 unless §2.7), `IsAvailable` of a specific signature reward (UI flag only; the claim
checks ownership), bots' random signatures on the GS (`GM/MultiplayerComponent.cs:1217-1225`): bots start showing the
new emblems and stop showing Kayac holidays. The signature shop and the UI SWFs never read it.

Bench (real `SignatureItemDataCollection` fill after the cells): 372 of 1,518 avatars unlockable (305 + 142 − 75), none
of them Kayac; 24 of 35 backgrounds.

### 2.7 Optional level-up avatars (49 cells)

`economy.collector.levelUpAvatars` = N (0–3, default 0): `PlayerLevelXP` `Level@AvatarRewards` = N for levels 2–50. Each
level-up then grants N random unlockable avatars (`GM/PlayerProfileComponent.cs:6973-7030`; the current level's row is
used for every level crossed). Front-loaded for new profiles (≤ 49 × N), nothing retroactive, extra draws during the
`UNLOCKALL` level jump (duplicates later). No conflict: `economy.xpMultiplier` targets `XPRequired`, Trophy fever
`DivisionTrophiesBonus`.

### 2.8 The SET writer (`CrmDocumentBuilder`)

Next to `AddMul`:

```csharp
/// <summary>SET cell: replaces whatever the cell holds (MUL, ADD or SET; one operation per cell, CRMTableCell).
/// value: a non-empty string or an integral long. Never "", null, NaN or a fraction (the typed table readers re-read
/// numbers through object.ToString() in the phone's culture).</summary>
internal static void SetCell(Dictionary<string, object> tables, string db, string table, string index, string field,
                             string key, object value);
```

`field` is given with dots and written with `#`. Call order inside `Build`: after `ApplyLiveOpsCells`, before
`ApplyEvents` and before `crm.overlay` (the overlay still wins). The collector touches no table/field that a MUL
target uses (`economy.json` targets, live-ops modifiers, `seasonRewards`), which a unit test asserts.
`CollectorPlan` (§6 WP-C) is pure: it returns a list of `CollectorCell { Table, Index, Field, Key, Value }`; the builder
writes them with `SetCell`.

### 2.9 Per-player document

Today `CrmService` serves `CrmOwnedStoreItems.JsonFor(doc, account)` for `configs/users/me` with a player's token when
`crm.omitOwnedSignatures` is on. New rule: the per-player path runs when `omitOwnedSignatures` **or** the collector layer
is active; selectors and `configs/users/{user}` stay static.

1. **Inventory** (`PlayerInventory.Scan(dump)`, taken on the first request after `storage.gameprofile.read`, exactly
   like today's owned set):
   * Signatures: today's string scan of `KillSignatureAvatars` / `KillSignatureBackgrounds`, plus `KillSignatureSounds`
     (same refusal rules; the store filter output must stay byte-identical).
   * Agents: `JsonSpanScanner.TryFindValue(dump, ["$default", "/Profile", "Mercs"])`, then MiniJson on that span only
     (the dump is ~560 KB; the span ~1–200 KB). Element = object with `Id` (string or number), `Locked` (bool or
     `"true"/"false"`, **absent = true**), `Skins` (absent = `["1"]`), `Camos` (absent = `["1"]`), `Accessories`
     (absent = `[]`). An agent missing from the list is locked (`MercsComponent.ProfileLoaded` adds locked ShortDefs at
     load). Agent 1 is unlocked by the starting inventory. Any other shape → `MercsKnown = false`.
   * Unknown parts fall back to the static assumptions of §4.2 (signatures: only the starting ones owned; agents: all
     unlocked, nothing owned) and are logged once per read with the reason.
2. **Cells**: `CollectorPlan.PlayerCells(inventory)` → the three field changers that vary per player:
   `Crate_Event_Special` `Id@Weight#1` (39 cells), `KillsignatureAvatars` `ID@Unlockable`, `KillsignatureBackgrounds`
   `ID@Unlockable`. A changer that ends up empty is removed (never an empty changer).
3. **JSON**: `CrmDocument.JsonFor(ICollection<string> omitStoreItems, IList<FieldPatch> patches)` shallow-copies the
   dictionaries along `game._OverrideTables.MCVS_Meta.<table>` **and** `game._ServerConfig.default._OverrideTables
   .MCVS_Meta.<table>`, replaces the patched field changers (keys in ascending numeric order), filters
   `offline_store.prices` as `JsonWithout` does, and serializes once. The cached tree is never mutated. A patched key is
   only replaced if the static document still holds the collector's own static value there (a `crm.overlay` cell on
   the same key wins).
4. **Caching key**: per account, `(CrmDocument reference, profile-read counter)` (the `OwnedSet` mechanism). Same
   inventory → same text, so `DoSaveProfile`'s refresh is recognised as equal (`StandardCrm.Data.Equals`) and not
   re-applied. The text changes at most once per return to the menu, where the TS's forced refresh re-applies the
   databases anyway (STATUS.md, device test 5: `RefreshDatabases` reloads `MCVS_Meta` on every forced refresh).
5. Logging: one line per rebuild: `CRM: collector for <account>: base N left (avatars a, backgrounds b, …), paid M left,
   K Unlockable cells, mercs known|unknown (<reason>)`.

Who applies which tree (why both are patched): the TS's per-user databases get the per-player document's
`game._OverrideTables` (`EN/CRMComponent.cs:166-180, 280-291` `RefreshDatabases` on every CRM set); the client gets
the same through the TS sync (`GM/GameplayCRMComponent.cs:166-186`); server Worlds also install
`serverSettings.GameParameters` (= `_ServerConfig.default` of the **static** `configs/users/me/game` selector) on
`DatabaseLoaded` (`GM/GameplayCRMComponent.cs:174-181`). The static document therefore carries a complete, valid
collector layer with neutral weights, so a database that ends up with the static changer still opens a valid crate.

### 2.10 Cost and limits

| Part | Cells | Table rows scanned per cell (position) |
|---|---:|---|
| `LootCrates` (`Crate_Event_Special` + 14 tier crates) | 3 + 84 = 87 | 211–246 of 247 |
| `Crate_Event_Special` rows | 153 | 1–39 of 100 |
| 21 placement crates, row 1 | 42 | 1 |
| `KillsignatureAvatars` | ≤ 217 | 225–672 of 1,518 |
| `KillsignatureBackgrounds` | ≤ 6 | 18–23 |
| `PlayerLevelXP` (optional) | 0 / 49 | 2–50 |
| **Total** | **≤ 554** (unlock-all profile: ≤ 331) | |

Measured with the real `Table.ApplyCRMTableChanges` on net8 (bench: the 505 cells above without the optional level-up
cells, itemsPerCrate 3, static weights): warm
**14.6–20.4 ms** (avatars 12–18 ms, `LootCrates` 2 ms, crate rows 0.4–0.6 ms), cold 63–93 ms (JIT). The added JSON is
12.5 KB, ×2 with the mirror (the current document: 89 KB unlock-all, 403 KB new player). Applied by the TS (per-user
databases) and the client on each forced refresh, and by the GS per match (static layer). Budget: **≤ 600 collector
cells per document**, enforced by `CollectorPlanTests.StaticLayerStaysWithinBudget`; a device-check target of
≤ +0.3 s on the return-to-menu "connecting to the server" time (build 8: ~11 s, of which the CRM step ~4.4 s).

### 2.11 Season transition

* The static document changes at the season expiry (the CRM cache key already contains `LiveOpsSnapshot.Key`, which
  contains the season); the per-player document follows (new document reference).
* Ranks 4–30: the cells do not depend on the season. The reward type and quantity are fixed when the TS evaluates the
  season (`GM/DivisionsComponent.cs:362-421`, `SendRewardToInbox`, 30-day message); the crate's **contents** are drawn
  when the player claims it, with the then-current weights.
* Ranks 1–3: the bundle is read **at claim time**. The document of season S names numeral(S−1), so a claim during
  the season after the one that ended (the normal case, including the race where `OnEventsStarted` evaluates before the
  CRM refresh: the crate sits in the inbox until claimed) gets the right emblem. Catch-up after weeks away: the player
  is evaluated on the last season played but gets numeral(S−1) of the current calendar. A claim made seasons later gets
  the then-previous numeral. Accepted: the inbox message carries the generic tier crate, and per-message state is not
  available.
* Collector switched off between evaluation and claim: §5.3.

## 3. Live-ops content

### 3.1 Rewards (appended to today's lists)

| Event | Today | Added (from `collector.since`, collector on) | Crates when fully done |
|---|---|---|---:|
| Daily missions, "complete all" bonus | `Crate_Contracts` ×1 | `Crate_Event_Special` ×1 | 1/day |
| Weekly event, milestone tier 1 (×4 objectives) | 300 VC, 20 Credits | `Crate_Event_Special` ×1 | 4 |
| tier 2 | 600 VC, 50 tokens | `Crate_Event_Special` ×1 | 4 |
| tier 3 | 1,000 VC, 100 tokens | `Crate_Event_Special` ×1 | 4 |
| final | 20 HC, `Crate_Event_<n>` | `Crate_Event_Special` ×2 | 2 |
| Season end, ranks 4–30 | one gear piece | Cosmetic crate with `band × itemsPerCrate` items (§2.4) | 1–4 crates' worth |

The weekly "crates opened" objective (`CratesOpened`, goals 2/5/10) gets easier as a side effect.

### 3.2 `liveops.json` `collector` block and the server

`tools/content-gen/liveops_tuning.json` gains (validated like the other rewards: currencies or catalogue crates):

```json
"collector": {
  "since": "2026-11-09T00:00:00Z",
  "dailyBonus": [{"item": "Crate_Event_Special", "quantity": 1}],
  "weeklyMilestones": [[{"item": "Crate_Event_Special", "quantity": 1}],
                       [{"item": "Crate_Event_Special", "quantity": 1}],
                       [{"item": "Crate_Event_Special", "quantity": 1}]],
  "weeklyFinal": [{"item": "Crate_Event_Special", "quantity": 2}]
}
```

and `liveops.json` carries it unchanged (`since` normalised to `"yyyy-MM-ddTHH:mm:ssZ"`). Validation: `since` is a Monday
00:00 UTC (a season start of the default grid: whole weeks since 2024-01-01); `weeklyMilestones` has one list per
milestone tier; every item is a currency or a catalogue crate; the `revision` is **not** changed.

Server (`server/Services/Social/LiveOps/`):

* `LiveOpsCatalog.Collector` (`LiveOpsCollectorRewards { DateTime Since; List<LiveOpsReward> DailyBonus;
  List<List<LiveOpsReward>> WeeklyMilestones; List<LiveOpsReward> WeeklyFinal }`, null when absent or broken; broken
  = logged, live ops otherwise unaffected).
* `LiveOpsSettings.CollectorEnabled` = `settings.GetBool("economy.collector.enabled", true)`, part of `Fingerprint`;
  `LiveOpsService.Settings` re-parses when the `liveops` subtree **or** `economy.collector.enabled` changes (today it
  keys on the `liveops` subtree only).
* `LiveOpsEvents.DailyQuest(catalog, day, collector)` / `WeeklyEvent(catalog, season, modifier, collector)`: when
  `collector && catalog.Collector != null && window.Start >= catalog.Collector.Since`, append `DailyBonus` to the bonus
  list, `WeeklyMilestones[i]` to tier *i*, `WeeklyFinal` after the event crate. Otherwise the template, and therefore
  `_version`, is byte-identical to today's.
* `GET /_mcvs/liveops` shows `collector: {enabled, since, activeFor: [daily id, weekly id]}`.

### 3.3 Release on a Monday boundary: `since`, not `revision`

`_version = LiveOpsHash.Revision("mcvs-offline/event/v1|" + revision + "|" + start + "|" + end + "|" + template)`
(`LiveOpsEvents.Finish`). The TS keeps a stored event with the same `_version` and replaces it (losing progress and
unclaimed milestones) when it changes (LIVEOPS.md §9).

* Bumping `revision` changes the `_version` of every event, including the day and week running when the player installs
  the update, whatever the install time: everyone loses that day's and that week's progress once. **Do not bump.**
* With `since`, windows starting before it keep a byte-identical template and `_version`; windows from `since` on are
  new events. Set `since` to the first Monday at least 7 days after the build is published.
* Residual: a player who installs after `since`, mid-week, has that week's event and that day's missions replaced once
  (their stored copies were generated without the crate). Toggling `economy.collector.enabled` mid-week does the same.
  Unavoidable without player-dependent event content, which would change `_version` as the player collects.

### 3.4 `collector.json` (new content file, CRM-owned)

Generated by `tools/content-gen` from `collector_tuning.json` + the catalogue (`build_collector`), read by
`CrmContent.Load` (`"collector.json"`, overridable in `<data>/content/` like the other files). Ids only, no game text.

```json
{
  "format": "<FORMAT>",
  "crate": {"id": "Crate_Event_Special", "table": "Crate_Event_Special", "locId": "Crate.Cosmetic_Crate",
            "image": "Menus/assets/png/Crates/Crates/crate_Legendary_Open.png"},
  "rows": [
    {"id": 1, "category": "avatars", "type": "KillSignature", "subtype": "Avatar", "subtype2": "Random"},
    {"id": 3, "category": "sounds", "type": "KillSignature", "subtype": "Sound", "subtype2": "2", "item": 2},
    {"id": 7, "category": "skins", "type": "MercSkin", "subtype": "2", "subtype2": "5", "merc": 2, "item": 5},
    {"id": 23, "category": "hiddenJerseys", "type": "MercAccessory", "subtype": "Random", "subtype2": "112", "item": 112},
    {"id": 35, "category": "agents", "type": "UnlockMerc", "subtype": "17", "merc": 17},
    {"id": 37, "category": "camos", "type": "WeaponCamo", "subtype": "Random", "subtype2": "Random"},
    {"id": 38, "category": "jerseys", "type": "MercAccessory", "subtype": "Random", "subtype2": "Random"},
    {"id": 39, "category": "exhausted", "type": "VersusCoins", "subtype": "Tiers4"}
  ],
  "avatars": {"base": "61-76,78,…", "drawable": "1-6,9-224,590-597", "unlock": "225-253,257,…,532",
              "lock": "598-672", "kayac": "443-531,533-589,598-1514", "reserved": "254-256,…,298-300"},
  "backgrounds": {"base": "18-35", "drawable": "1-6,24-35", "unlock": "18-23"},
  "camos": {"1": "2,4-110,120-155,166-231", "…": "…"},
  "jerseys": "<ranges of the 236 visible non-Kayac jersey ids>",
  "season": {
    "tiers": ["Starter", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Master"],
    "divisionTier": [0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6],
    "retarget": [["Crate_Starter_4To10Place", "Crate_Starter_11To20Place"], "…"],
    "placement": [["Crate_Starter_FirstPlace", "Crate_Starter_SecondPlace", "Crate_Starter_ThirdPlace"], "…"],
    "emblems": [[253, 254, 255, 256], [257, 258, 259, 260], "… (numerals I..XII: none, ★, ★★, ★★★)"]
  },
  "levels": "2-50",
  "summary": {"baseItems": 599, "baseTier": 668, "paidPairs": 9365, "vipOnlyPairs": 5624, "kayacAvatars": 1061}
}
```

(Ellipses stand for the full lists of §1, written by the generator with `compress_ranges`.) The full row list is the 39
rows of §2.3 in that order.

`content_gen.py` validations (`ContentError` otherwise): crate, tables and the 35 tier crates exist; `rows` ≤ 100 and
fit the table; every row type is one of `KillSignature`, `MercSkin`, `MercAccessory`, `WeaponCamo`, `UnlockMerc`,
`VersusCoins` (cases of `GenerateRewardItem`); skins exist with `Locked=false`; hidden jerseys are
`VisibleInShop=false` and not `IsKayac`; agents are displayed; every avatar/background id exists and is released;
`kayac ∩ base = ∅`, `reserved ∩ base = ∅`, `1513-1518 ∩ (base ∪ unlock ∪ lock) = ∅`; `unlock` ⊆ base with
`Unlockable=false`; `lock` = Kayac ∩ `Unlockable=true` ∩ released; per-agent `camos` equals the `unlocks.json` camo
group and `jerseys` equals the accessories group minus the hidden jerseys (both equalities hold for 1.17.32); row 1 of
each placement crate is the only `Weight.1 > 0` row and is `HardCurrency`; the loc id `Crate.Cosmetic_Crate` exists;
the image exists in `names.json`; the 48 season emblems resolve by asset name (`I.png` … `XII_3.png`). The `manifest.json`
lists `collector.json`; the counts of §1 are asserted in `test_content_gen.py`.

## 4. Weighting, pace and settings

### 4.1 Model

Per document, units per category (per player from §2.9, or the static assumptions of §4.2):

| Unit | Definition |
|---|---|
| `uA` | avatars the player does not own in (`avatars.drawable` ∪ the window ids sent as `true`) |
| `uB` | backgrounds not owned in (`backgrounds.drawable` ∪ `backgrounds.unlock`) |
| `uS(row)` | 1 if that sound is not owned |
| `uK(row)` | 1 if that skin is not owned and its agent is unlocked |
| `uH(row)` | number of unlocked agents lacking that hidden jersey |
| `uM(row)` | `weights.agents` (default 4) if that agent is locked |
| `uC` | Σ over unlocked agents of camos in its pool not owned |
| `uJ` | Σ over unlocked agents of visible non-Kayac jerseys not owned |

Each unit is multiplied by its category weight (`weights.avatars` … `weights.jerseys`, default 1). Then

```
base = uA + uB + ΣuS + ΣuK + ΣuH + ΣuM        paid = uC + uJ
share_base = baseShare (0.75) if base > 0 and paid > 0; 1 if paid = 0; 0 if base = 0
share_paid = 1 − share_base
Weight#1(row) = round(1,000,000 × share_tier × u(row) / tier_total), at least 1 when u(row) > 0, else 0
Weight#1(39 exhausted) = 1 if base = paid = 0, else 0
```

Inside a tier every remaining item is equally likely, so the categories of a tier finish together; random rows never
waste a draw while their category has items; the only waste is a row drawn after it ran out **within one session**
(weights refresh at each return to the menu). Simulated waste: 0.02–0.5 % of draws.

### 4.2 Static weights (fresh profile, all 21 agents unlocked)

base = 371 + 23 + 4 + 16 + 12 × 21 + 2 × 4 = 674 units; paid = 4,409 + 4,956 = 9,365.

| Row | Weight#1 | Row | Weight#1 |
|---|---:|---|---:|
| 1 avatars | 412,834 | 23–34 each hidden jersey | 23,368 |
| 2 backgrounds | 25,593 | 35, 36 each agent | 4,451 |
| 3–6 each sound | 1,113 | 37 camos | 117,699 |
| 7–22 each skin | 1,113 | 38 jerseys | 132,301 |
| 39 exhausted | 0 | | |

(Sum 1,000,005: rounding; `PickRandomWeightedItem` only needs the relative values.)

### 4.3 Pace

Crates per week (assumptions: daily missions need ~5–6 matches; weekly tiers per §3.1 goals):

| Player | Play | Dailies | Weekly milestones + final | Season (ranks 4–30) | Crates/week |
|---|---|---:|---:|---:|---:|
| Casual | ~25 matches/week | 3 | 6 + 0 | 0 (ranks 31+) | **9** |
| Regular | ~10 matches/day | 6 | 12 + 2 | 2 (Silver/Gold) | **22** |
| Dedicated | every day, Diamond/Master | 7 | 12 + 2 | 4 (Master 4–10) | **25** |

Weeks to finish, fresh profile with all agents unlocked (analytic `base = 668 / (0.75 × C × N)`, `all = 10,033 / (C × N)`;
the per-session Monte Carlo of `pace.py` agrees within a week):

| itemsPerCrate N | Casual base / everything | Regular base / everything | Dedicated base / everything |
|---:|---|---|---|
| 1 | 99 w / 1,115 w | 40 w / 456 w | 36 w / 401 w |
| 2 | 49 w / 557 w | 20 w / 228 w | 18 w / 201 w |
| **3 (default)** | **33 w (7.6 mo) / 372 w (7.1 y)** | **13.5 w (3.1 mo) / 152 w (2.9 y)** | **12 w (2.7 mo) / 134 w (2.6 y)** |
| 4 | 25 w (5.7 mo) / 279 w | 10 w / 114 w | 9 w / 100 w |
| 5 | 20 w / 223 w | 8 w / 91 w | 7 w / 80 w |
| 10 | 10 w / 111 w (2.1 y) | 4 w / 46 w (10.5 mo) | 3.6 w / 40 w (9.2 mo) |

* **Base set at 3**: ~3 months regular/dedicated, ~7.6 months casual (4 per crate brings casual under 6 months).
* **Everything in ~6 months (26 weeks) from crates alone** needs N ≈ **17.5 (regular), 15.4 (dedicated), 43 (casual)**,
  all outside 1–10; at the cap of 10: 10.5 months regular, 9.2 months dedicated, 2.1 years casual. The paid tier is
  9,365 pairs; the VC shop's camo/jersey 10-packs (≈ 432k VC) remain the main path to it (a regular player earning
  17–21k VC a week clears it in ~22 weeks spending everything there). If the owner wants crates alone to reach 6
  months, raise the range to 20 (the client has no cap on `CratesCount` here; the reveal of 20–80 items is a device
  check) or add crates to the weekly event.
* Fewer unlocked agents shrink the paid tier proportionally (and the hidden-jersey pairs); `baseShare` 0.5 / 0.9 moves the
  regular base set to 20 / 12 weeks without changing "everything".
* The agent units (4) make Witch and Bli-P arrive after ~3–4 weeks each for a regular player (8 units: ~2 weeks; 1 unit:
  uniformly with the base tier, ~7 weeks).

### 4.4 Settings (`settings.json`, `"economy": {"collector": {…}}`)

| Key | Default | Range | Effect | Applies |
|---|---|---|---|---|
| `enabled` | `true` | bool | `false`: no collector cells, events as before (byte-identical), per-player document = store filter only | next CRM fetch; running day/week events replaced once (§3.3) |
| `itemsPerCrate` | 3 | 1–10 | `CratesCount` of the Cosmetic crate; tier crates `band × N` | next CRM fetch, also for crates already in the inbox |
| `baseShare` | 0.75 | 0–1 | share of draws for the no-source tier while both tiers have items | next per-player document |
| `weights` | `{"avatars":1,"backgrounds":1,"sounds":1,"skins":1,"hiddenJerseys":1,"agents":4,"camos":1,"jerseys":1}` | each 0–100 (0 removes the category; `agents` = units per agent) | §4.1 | same |
| `seasonCrates` | `[1,1,2,2,3,3,4]` | 7 ints 0–10 (Starter…Master; 0 = today's tier crate) | §2.4 | next season evaluation |
| `seasonEmblems` | `true` | bool | §2.5 | same |
| `unlockableWindow` | 160 | 0–400 | §2.6 | next per-player document |
| `levelUpAvatars` | 0 | 0–3 | §2.7 | next CRM fetch |
| `exhaustedReward` | `"VersusCoins_Tiers4"` | `VersusCoins_Tiers0`…`6`, `UpgradeTokens_Tiers0`…`11` | row 39 | next CRM fetch |

Out-of-range values are clamped and logged (`settings: economy.collector.<key> …`), like the other economy keys. The
whole settings snapshot is already part of the CRM cache key. If the settings page (docs/design/settings-page.md) is
built, its key registry gets these keys (owner: that design's work packages). The existing `liveops.seasonRewardMultiplier`
still scales the season reward quantity (number of tier crates).

## 5. Edge cases

### 5.1 Unlock-all and complete profiles

Every unit is 0 → only row 39 has weight: each item pays 70 VC (3 per crate, 210 VC for the daily crate). The preview
lists only the VersusCoins row; `CrateRewardItem.IsAvailable` (any row available) stays true because a currency row is
always available, so TLE milestones never show the crate as unavailable. Per-player cells drop to 39 weights + 0
Unlockable cells. If the dump cannot be read, static weights apply and draws turn into the client's duplicate
compensation (§1.6: one random gear piece per signature/camo/jersey, 40 for an agent).

### 5.2 A category runs out

Its rows get weight 0 in the next per-player document (next return to the menu). Within the current session a draw on
it pays its alternate reward. When the base tier runs out the paid tier gets 100 %, and vice versa; both empty → §5.1.

### 5.3 Collector disabled (or enabled) mid-season

At the next CRM fetch all collector cells disappear and the TS reloads its databases (as on every forced refresh).
Running daily/weekly events are replaced once (progress and unclaimed milestones of that day/week are lost, §3.3).
Cosmetic crates already in the inbox or claimed later open the empty legendary crate → `Crate_LastChance` tokens (1
round per crate). Season rewards evaluated later use the original tier crates (ranks 1–3: no emblem; 4–30: the gear
piece). Re-enabling restores everything at the next fetch.

### 5.4 Existing saves

Nothing is migrated; the server only reads the dump. Items bought with VC are never duplicated by random rows (they
resolve to unowned items) and get weight 0 on explicit rows. The store keeps leaving owned signatures out
(`omitOwnedSignatures`). Players who updated after `since` lose that week's event progress once (§3.3).

### 5.5 CRM apply time on the phone

§2.10: ≤ 554 cells (12.5 KB, ×2 mirrored), ~15–20 ms warm on net8 per apply; TS + client per return to the menu, GS per
match. The per-player text changes only after a profile read, so no extra database reload is caused (the forced refresh
reloads anyway). Safety valves: `unlockableWindow` (the avatar cells are 75 % of the cost), `levelUpAvatars` 0.

### 5.6 `crm.omitOwnedSignatures`

Independent switch, shared dump scan. With it off and the collector on, the per-player document has the whole store and
the collector cells. Avatars won from crates leave the store at the next profile read, as bought ones do. Kayac avatars
stay in the store.

### 5.7 Inventory unreadable

`MercsKnown=false` (unexpected `Mercs` shape) or signatures unreadable: §2.9 fallbacks, logged once per read; the
document stays valid. `GET /_mcvs/collector?account=<id>` shows what was read.

### 5.8 Live ops off

The collector layer is emitted only when `economy.collector.enabled` **and** live ops are on (`liveops.enabled` with a
non-empty catalogue; in code: `liveOps != null && liveOps.Expiry.HasValue`, which `LiveOpsService.Snapshot` sets only
then) **and** `collector.json` parsed: nothing hands out the crate otherwise, and the document stays byte-identical to
today's with live ops off. A missing/broken `collector.json` while `liveops.json` hands out the crate is logged as an
error (the crate then opens empty → `Crate_LastChance`); `GeneratedContentTests` keeps the two files consistent.

### 5.9 LAN

One TS serves several players: per-player documents are per account; the GS uses the static layer.

## 6. Work packages

File ownership is disjoint; a package edits only its files. WP-I integrates.

| WP | Files (owned) | Depends on |
|---|---|---|
| **A. Content** | `tools/content-gen/content_gen.py` (`build_collector`, `_find_nosource`, liveops `collector` block), `tools/content-gen/collector_tuning.json` (new), `tools/content-gen/liveops_tuning.json`, `tools/content-gen/test_content_gen.py`, `tools/content-gen/README.md`, `server/Content/collector.json` (new, generated), `server/Content/liveops.json`, `server/Content/manifest.json`, `server/Content/README.md`, `tests/McvsOffline.Tests/Content/GeneratedContentTests.cs` | – |
| **B. Inventory** | `server/Services/Crm/PlayerInventory.cs` (new), `tests/McvsOffline.Tests/Content/PlayerInventoryTests.cs` (new) | – |
| **C. Collector plan** | `server/Services/Crm/Collector/CollectorContent.cs`, `CollectorOptions.cs`, `CollectorPlan.cs`, `CollectorWeights.cs` (all new), `tests/McvsOffline.Tests/Content/CollectorPlanTests.cs` (new) | A (schema; fixtures until generated), B (type) |
| **D. CRM integration** | `server/Services/Crm/CrmDocumentBuilder.cs`, `CrmDocumentCache.cs`, `CrmOwnedStoreItems.cs`, `CrmService.cs`, `CrmOptions.cs`, `CrmContent.cs`, `CrmDocumentValidator.cs`, `tests/McvsOffline.Tests/Content/CrmDocumentBuilderTests.cs`, `CrmOwnedStoreItemsTests.cs`, `CrmServiceTests.cs`, `CollectorDocumentTests.cs` (new) | B, C |
| **E. Live ops** | `server/Services/Social/LiveOps/LiveOpsCatalog.cs`, `LiveOpsEvents.cs`, `LiveOpsService.cs`, `LiveOpsSettings.cs`, `tests/McvsOffline.Tests/Misc/LiveOpsTests.cs`, `Misc/LiveOpsConformanceTests.cs` | A (schema) |
| **F. Real-client conformance and timing** | `tests/McvsOffline.Tests/Conformance/CollectorConformanceTests.cs` (new), `tests/McvsOffline.Tests/Conformance/RealTables.cs` (new helper) | A, D, E |
| **G. Docs** | `docs/ECONOMY.md`, `docs/LIVEOPS.md`, `docs/protocol/content.md`, `docs/STATUS.md` (device checklist), `docs/ROADMAP.md`, `docs/design/economy-collector.md` (status) | all |
| **I. Integrator** | merge, `scripts/build.sh` run (net8 + embedded), full test run with `MCVS_REFS`, `MCVS_DATAPACK(_DECODED)`, `MCVS_REQUIRE_REAL_CLIENT=1`, APK build | all |

### WP-A Content
`_find_nosource` (algorithm §1.1), the Kayac rule (§1.2), the sets of §1.3–1.5, `build_collector` → `collector.json`
(§3.4), the liveops `collector` block (§3.2) with its validations. Tests (`test_content_gen.py`): exact counts (307, 142,
75, 165 + 65, 18/6, 4, 16, 12, 2, 4,409, 4,956, 1,061 Kayac, 36 reserved); the per-agent pool equalities; every
`ContentError` path (unknown crate, row > 100, Kayac in base, `since` not a Monday, wrong tier count, bad loc id,
missing image, placement row 1 not the only `Weight.1` row); deterministic output. `GeneratedContentTests` (MiniJson
level only): `collector.json` has the §3.4 keys and 39 rows, its crate id is the one `liveops.json`'s `collector` block
hands out, `manifest.json` lists it.

### WP-B Inventory
`public sealed class PlayerInventory { bool SignaturesKnown; HashSet<int> Avatars, Backgrounds, Sounds; bool MercsKnown;
Dictionary<int, MercState> Mercs; string Problem; static PlayerInventory Scan(string dump); IEnumerable<string>
OwnedStoreNames(); }`, `MercState { bool Unlocked; HashSet<int> Skins, Camos, Accessories; }`. Signature part keeps the
exact refusal rules of today's `CrmOwnedStoreItems.Scan`. Tests: today's scan fixtures give the same names; sounds;
`$default`/`/Profile`/`Mercs` path with defaults (absent `Locked` = locked, absent `Skins` = `["1"]`), string and bool
`Locked`, numeric `Id`; a `Mercs` key elsewhere (`Squads[].Mercs`) ignored; malformed agent list → `MercsKnown=false`
with reason; a 600 KB synthetic unlock-all dump scans in < 50 ms on net8; the data pack's `DefaultProfile.tx` (the
shape the real Templates writer emits, see `EndToEndConformanceTests`; `"Mercs": []`) scans as "every agent locked but
agent 1".

### WP-C Collector plan
`CollectorContent.Parse(text, log)` (null + error on a broken file); `CollectorOptions.FromSettings(Settings, Log)`
(§4.4, clamping); `CollectorPlan.Static(content, options, LiveOpsSnapshot)` → `IList<CollectorCell>`;
`CollectorPlan.PlayerCells(content, options, PlayerInventory)` → `IList<FieldPatch>`;
`CollectorWeights.Compute(...)` (§4.1). Tests: the static weights of §4.2 exactly; `StaticLayerStaysWithinBudget`
(≤ 600 cells, incl. `levelUpAvatars` 3); never `""`/null/fractions; numeral mapping (2026-10-26 → 264/263/262,
2026-11-02 → 268/267/266, `seasonDays` 1 and 14); band × N cells; window sizes (160, 50, 0); unlock-all inventory → only
row 39 = 1 and 0 Unlockable cells; base empty → paid 100 %; paid empty → base 100 %; owned skins/sounds/agents → 0;
locked agent → its skins 0 and its pairs not counted; hidden-jersey units per unlocked agent; weights deterministic;
the generated `server/Content/collector.json` parses with `CollectorContent` and yields the 39 rows of §2.3.

### WP-D CRM integration
`SetCell` (§2.8); `Build` writes `CollectorPlan.Static` when the layer is active (§5.8); `CrmContent.Collector`;
`CrmOptions.Collector` (in `Fingerprint`); `CrmDocument.Collector` + `JsonFor(omit, patches)`; `CrmOwnedStoreItems`
becomes the per-player document source (inventory per account, store filter, collector patch, cache key §2.9.4);
`CrmService` per-player condition; `CrmOwnedStoreItems.Scan` is removed in favour of `PlayerInventory.Scan` (its tests
move to WP-B's fixtures, the HTTP-level tests stay here); `GET _mcvs/crm` gains `collector {active, cells, itemsPerCrate, staticWeights}`;
`GET _mcvs/collector?account=<id>` (remaining units per category, weights, Unlockable cell count, inventory problems);
validator: collector cells are scalar, non-empty, integral. Tests: **`CollectorOffIsByteIdentical`** (documents with
`enabled=false` equal documents built without `collector.json`, for 16 simulated days, with and without a player
token); `CollectorCellsAreMirrored`; `CollectorDoesNotTouchMulTargets`; `OverlayWinsOverCollector`;
`PlayerDocumentChangesOnlyAfterProfileRead` (same text after a save); `PlayerDocumentDropsOwnedUnlockableCells`;
`StoreFilterUnchangedWithCollector` (store part byte-identical to today's per-player text); unreadable dump → static
layer; selectors stay static; the minimal-document fallback has no collector cells.

### WP-E Live ops
§3.2. Tests: events before `since` and with the collector off are byte-identical (`_version` and text) to today's; from
`since` the bonus/tiers/final carry the crates in the right order; toggling `economy.collector.enabled` changes the
`LiveOpsSnapshot.Key` and `_version` of windows ≥ `since` only; a broken `collector` block leaves live ops running.
`LiveOpsConformanceTests` (real client): the 16-day run is extended with a content override whose `since` falls inside
it; every event still passes `StandardCrm.Data`, `SocialEvent`, `ParseTemplate`, `TypeUtil.GetType` and `Initialize`,
and `RewardHelper.CreateRewardItem("Crate_Event_Special", n)` yields `Game.CrateRewardItem` with quantity n in the daily
bonus, every weekly tier and the final. Make its `NativeTrackingStub` `internal` for WP-F.

### WP-F Real-client conformance and timing
`RealTables` loads `Design/MCVS_Meta.xlsx` (from `MCVS_DATAPACK` via the Core `DataPack`, or `MCVS_DATAPACK_DECODED`) into
real `Database.Table`s (the bench technique: `Database` via `GetUninitializedObject`, `Table.Database` set). Tests
(skipped without `MCVS_REFS`, mandatory with `MCVS_REQUIRE_REAL_CLIENT=1`):
1. **CRMTableChanger** on the static and on a worst-case per-player layer taken from the served document: every changer
   parses (`new CRMTableChanger`), applies, and every target cell holds the intended value.
2. **CratesSpecsCollection fill**: before = `RewardsCount 2` for `Crate_Event_Special`; after = `RewardsCount 1`,
   `CratesCount N`, `LocId`, tier crates retargeted with `band × N` (N = 1, 3, 10).
3. **CrateRewards fill** (`CratesInventoryComponent+CrateRewards`): 39 rows, `RewardId`s exactly as §2.3, `Weight.1` as
   served, rows 40–100 absent, placement crates' row 1 `BundledRewards`/`AlwaysGiveBundledRewards=true`.
4. **GenerateRewardItem per row** through an uninitialised `CratesInventoryComponent` (the generic branch does not touch
   component state): classes `KillSignatureRewardItem` (`IsRandom` true for Avatar/Background Random, false for sounds),
   `MercSkinRewardItem` (MercId/SkinId), `MercAccessoryRewardItem` (`IsRandomMerc` true, `IsRandomAccessory` false for
   the 12, both true for row 38), `WeaponCamoRewardItem` (`IsRandom(null)` true), `UnlockMercRewardItem`,
   `VersusCoinsRewardItem` via `GenerateVersusCoinsByTiers` needs `metagameComponent` (assert the type route only);
   every image/loc id the display branches reference exists (`Random_Player_icon.png`, `Random_Player_bg.png`,
   `camo_random.png`, `jersey_random.png`, `menu.JerseyTitle`, `menu.CamoTitle`, `menu.RandomAgent`, `menu.AgentSkin`,
   `menu.ShopCrateContent`). The `GetRewardInfo`/preview branches need a World (`profile.World.GetMetaGame()`) and are
   device checks. (The random `MercSkin` display branch, `crateMercIcon.png`, is not used: skin rows are explicit.)
5. **SignatureItemDataCollection fill**: 372 unlockable avatars, none Kayac, 24 backgrounds (static layer).
6. **CRM-apply timing**: static and worst-case per-player layers, 1 cold + 5 warm applications; log the numbers, assert
   warm < 60 ms on net8 (3× the measured value) and cells ≤ 600.

### WP-G Docs
ECONOMY.md §1 (collector as the play path for the no-source set), §3 (settings table), §4 (live-ops income table with
crates; corrected season rewards §0.3.2); LIVEOPS.md §1/§7/§10 (crates, `since`, do not bump `revision`); protocol
content.md (per-player collector cells, `_mcvs/collector`); STATUS.md device checklist (§7); ROADMAP.md.

## 7. Device checks

1. Daily missions and weekly event screens: the "COSMETIC CRATE" reward shows its image, "CONTAINS 3 ITEM(S)" and a
   preview listing only rows with weight (owned sounds/skins absent: proves the per-player cells reached the client).
2. Claim the daily bonus (two crates in one claim: Contracts + Cosmetic): reveal of 1 + 3 items, no stall; a weekly tier
   reward with three entries renders.
3. Weekly final (HC + event crate + 2 Cosmetic crates): 1 + 6 items revealed.
4. `itemsPerCrate` 10: weekly final reveals 20 items, a Master rank-4 season crate 40; time the reveal.
5. Random avatar/background reveals show the concrete emblem; 50 avatar draws contain no Kayac id (598–672) and no
   starred season emblem.
6. A hidden jersey (e.g. USSR) from the crate appears in that agent's jersey list and can be equipped.
7. Witch or Bli-P from the crate: agent unlocked, unlock flow plays; its skins then appear in the preview.
8. Season end (set `liveops.seasonStart` to the next hour): rank 4–30 inbox "DIVISION REWARD" with a Cosmetic crate of
   band × 3 items; rank 1–3: HC + the ★★★/★★/★ numeral of the ended season + gear.
9. Unlock-all profile: crates pay 70 VC per item; no "unavailable" state on milestones.
10. Return-to-menu "connecting to the server" time with the collector on vs off (target ≤ +0.3 s); match start time
    unchanged; server log shows the collector line once per return.
11. Collector off (`economy.collector.enabled: false`): events as before, ranks 4–10 get one gear piece, no collector
    cells in `GET /configs/users/me`.
12. `CratesCount` sent as a JSON integer arrives as an int (the TS opens exactly N items) under a comma-decimal locale.
13. `GET /_mcvs/collector?account=…` after a few crates: remaining counts fall; merc inventory "known".

## 8. Risks and open points

* **Per-user database changer race on the TS**: server Worlds also install the static selector document on
  `DatabaseLoaded`; if that handler runs last, the TS opens crates with neutral weights until the next CRM set (valid,
  more duplicates). Mitigated by a complete static layer; check 1 + 13.
* **Reveal UI** with several crates in one claim and with 20–40 items (checks 2–4); preview length (up to 36 entries).
* **Dump shape for `Mercs`** is inferred from `DefaultProfile.tx` and the ShortDef defaults; unexpected shapes fall
  back to static agent assumptions (logged).
* **Mono/ARM apply time** of ~550 cells (×2–5 net8 expected) on each forced refresh and match start.
* **Event replacement** once for players updating after `since` or toggling the collector mid-week (§3.3).
* **Emblem numeral** of a late claim is that of the season before the claim (§2.11).
* Ranks 4–30 lose today's single gear piece; 3 league rank-up avatars (590–592) can come from the crate first.
* Owner points: the 60 `Kayac/holidays` emblems are excluded by the folder rule (§1.2); "everything in 6 months" from
  crates alone needs itemsPerCrate ≈ 15–18, outside 1–10 (§4.3); the reserved starred emblems (D4) leave the crate set
  at 307 avatars.
