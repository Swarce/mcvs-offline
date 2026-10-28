# Design: the Cosmetic crate ("economy collector")

Status: **implemented in build 9 (WP-0, A–H; integration review round 3 applied, §0.5), awaiting its device test (§7,
STATUS.md build 9 checks 14–28; crates start 2026-11-09)**; owner set 5 items per crate (O3) (October 2026, designed on
`main-dev` `e4bff79`). Roadmap item "economy polish". Player and tester guide: [../ECONOMY.md](../ECONOMY.md) §5. This document is the contract between the work packages in §6: anything another package relies on
(content file schemas, CRM cells, C# signatures, settings keys, test names) is fixed here. Change it only through the
integrator (WP-I). §0.4 lists what changed in round 2 and why, §0.5 what the integration review (round 3) changed.

Evidence: `polish-research.md` (section *economy*) and its scratch scripts; the decoded 1.17.32 data pack
(`/home/user/mcvs-work/datapack-decoded`, `Design/MCVS_Meta.xlsx`, `Design/MCVS_Gameplay.xlsx`, `_catalog.json`); the
decompiled client (`/home/user/mcvs-work/decompiled`; `GM/` = `Game/Game/`, `EN/` = `Gameloft.Engine/Gameloft.Engine/`).
Every count below was recomputed for this revision (session scratchpad `collector/`: `scripts/pools.py`, `verify.py`;
round 2 in `collector/rev/`: `pools2.py`, `pace2.py`, `cells2.py`, `cells-{game,mirror,guard}.json`, and `bench/` +
`rev/bench2/`, a net8 run of the **real** `Gameloft.Engine.dll` and `Game.dll` table code over the real `MCVS_Meta`).

## 0. Decisions

### 0.1 Owner decisions (binding, not reopened)

| # | Decision |
|---|---|
| O1 | One server-defined **Cosmetic crate** built from the unused `Crate_Event_Special` (its `LootCrates` row and its 100-row content table, filled with CRM `_OverrideTables` SET cells). No IL patch. |
| O2 | Sources: the daily-mission bonus, the weekly-event milestones and final, the division season rewards of ranks 4–30. |
| O3 | `economy.collector.itemsPerCrate`, default **5** (owner, after the pace table of §4.3; first answer was 3), runtime setting, 1–10. |
| O4 | Random crate only (no choice shop now). |
| O5 | Pools: the base no-source set (avatars, backgrounds, sounds 2–5, the 16 skins, the 12 hidden jerseys) **plus** the VIP-only camo and jersey pools **plus** explicit rows for Witch (17) and Bli-P (21). **Kayac/Kayac2 emblems are not in the crate** (they stay VersusCoins-only). |
| O6 | Season rewards: ranks 4–30 get 1–4 Cosmetic crates by division band; 1st–3rd keep their HardCurrency and additionally get that season's emblem (`BundledRewards`); ranks 31+ unchanged. |
| O7 | Everything opt-out with `economy.collector.enabled=false`, which restores today's behaviour. See D9: from `since` on, "today's behaviour" is met **behaviourally** (5 guard cells on a crate nothing hands out), not byte for byte. Owner informed with the N = 5 decision. |
| O8 | Weights must deliver the no-source items at a good pace despite the large VIP pool. |

### 0.2 Design decisions taken here

| # | Decision | Why (details in the section) |
|---|---|---|
| D1 | **Items per crate = `LootCrates.CratesCount`**, with `RewardChance.2` set to 0. Not `RewardsCount`. | The client overwrites `RewardsCount` from the `RewardChance.N` cells (§0.3.1, verified with the real `Game.dll`). |
| D2 | **Tiered weights with a base share** (default 75 % of draws go to the no-source tier while it has items), proportional to the player's remaining items inside each tier, recomputed **per player** from the game-profile dump. | §4.1. The alternative "one guaranteed base item + one paid item per sub-crate" *can* be expressed (`RewardChance.2 = 100`: round 0 draws `Weight.1`, round 1 draws `Weight.2`) but is rejected on its costs: a fixed 50 % share, item counts tied to 2 × `CratesCount` (no odd counts, 3 impossible), the "WithBonus" content text and bonus flags on every second item, and a wasted round whenever one tier is empty. |
| D3 | Ranks 4–30 are served by **retargeting the 14 tier crates** (`Crate_<Tier>_4To10Place`, `_11To20Place`) to the Cosmetic crate's content table, with `CratesCount = band × itemsPerCrate` (capped, §2.4). `PVPDivisionRewardsType`/`PVPDivisionRewards` stay untouched. | 84 cells instead of 918; `liveops.seasonRewardMultiplier` keeps working; identical display (§2.4). |
| D4 | The 36 starred season emblems (`I_1`…`XII_3`) are **reserved** for 1st–3rd place and are not in the crate; the 12 unstarred numerals are. The per-player document picks the first **unowned** emblem of the right star count (§2.5). | Otherwise the top-3 bonus would mostly be paid as duplicate gear (§1.3); the numeral cycle repeats every 12 seasons. |
| D5 | Live-ops rewards and the CRM layer start together at a content date **`collector.since`** (a season start), behind **one predicate** (`CollectorRuntime`, §2.12). No `revision` bump. | A bump resets the running day and week for everybody at install time (§3.3); one predicate means the crate is never handed out unless the document carries its rows. |
| D6 | The Unlockable "window" covers the **whole** remaining set by default (≤ 139 + 75 + 6 cells). | Measured ~5 ms each for the 139 `true` and the 75 Kayac `false` cells on net8 (§2.10). A window setting remains as a safety valve. |
| D7 | When every collectible is owned, each draw pays **70 VersusCoins** (`VersusCoins_Tiers4` row). | Unlock-all profiles get something useful and the crate is never "unavailable" (§5.1). |
| D8 | **Sounds ride as bundles** on four "carrier" rows (random **background** + `BundledRewards = KillSignature_Sound_<n>`, always given; round 3: was a random avatar). No row has `Type KillSignature / Subtype Sound`. | A sound row with weight throws `KeyNotFoundException` in the crate preview, which every TLE screen builds (§2.3); a background row's checks build the 35-entry background list, not the 1,518-entry avatar list (§2.3, §8). |
| D9 | From `guardSince` (a day before `since`) on, and for good once live ops served the crate, `Crate_Event_Special` **never has an empty content table**: the document carries either the collector layer or a 5-cell **guard** (row 1 = 70 VersusCoins). | An empty content table throws in `GenerateRewards` (no `Crate_LastChance` fallback); stale crates outlive a switch-off, a `since` change and a clock set back (§2.12). |
| D10 | **Mirror policy**: the static document's `_ServerConfig.default._OverrideTables` (the `game` selector the GS/TS install) carries the layer **minus** the Unlockable `true` cells; the per-player document's mirror carries only the guard. | Server Worlds read the static selector; the per-player mirror is read by nothing we know of, so it is made safe and small (§2.9, §2.10). |
| D11 | The collector **requires the patcher's default `store` patch** (S1); the embedded host reports whether it is present and the collector stays off (guard) when it is known to be missing. | Vanilla `GetAvailableId` throws for sounds and for a random signature that found nothing, on the reveal and inbox paths (§2.13). |
| D12 | **One crate per milestone** (round 3): live ops put the Cosmetic crate only on milestones that hold no other crate. | `ClaimAchievementMilestoneRequest` replaces the claim's reward list by each crate's generated items, so a milestone reveals only its last crate (§3.1). |

### 0.3 Corrections to the research (verified for this design)

1. **`RewardsCount` is not a usable knob.** `GM/CratesSpecsCollection.cs:17-40` calls `CrateSpecs.SetRewardChance` for every
   non-empty `RewardChance.N` cell, and `GM/CrateSpecs.cs:81` then sets `RewardsCount = RewardsChances.Count(x => x > 0)`.
   Bench (real Game.dll): `Crate_Event_Special` today `RewardsCount=2 CratesCount=1 chances=100/100`; after our cells
   `RewardsCount=1 CratesCount=3 chances=100/0`. The shipped "N items" crates use `CratesCount` too (`Crate_Event_1_x10`).
2. **Ranks 4–30 do not get LastChance tokens today.** The tier crates have `RewardChance.2 = 100` with only a `Weight.2`
   gear row: ranks 4–10 get one `Gear_Random_3_Random`, 11–30 one `Gear_Random_2_Random`; 1st–3rd get the HardCurrency
   row plus `Gear_Random_4` (Starter/Bronze/Silver) or `Gear_Random_5` (Gold and up).
3. Shipped `Unlockable=true` backgrounds: **18** (1–6, 24–35), not 23.
4. Kayac = **1,066** avatar ids (1,064 released) by the rule of §1.2; **75 of them are `Unlockable=true` today** (598–672).
5. The "VIP pools" are the generic random forms `WeaponCamo_Random_Random` / `MercAccessory_Random_Random`; they draw
   from **every** locked non-Kayac camo/visible jersey of an unlocked agent (9,365 pairs), of which the VIP-only pairs
   are a 5,624 subset. A 100-row table cannot list the 268 VIP-only ids explicitly (§1.5).
6. **An empty crate does not fall back to `Crate_LastChance`; it throws.** `GenerateRewards` reads
   `rewards[0].WeightsCount` (`GM/CratesInventoryComponent.cs:547`) before any draw, and round 0 always runs
   (`RewardChance[0] = 100`). With the shipped table (100 rows, all `Type` null, so `CrateRewards` is empty) that is an
   `ArgumentOutOfRangeException` inside `CrateRewardItem.OnClaimed`, after `RewardItem.Claim` has set `Claimed = true`
   (`GM/RewardItem.cs:354`). The `Crate_LastChance` branch (`OpenCrateOnServer`, lines 1440-1450) only runs when rows
   exist but nothing was drawn.

### 0.4 Review round 2: what changed

| Finding | Resolution |
|---|---|
| Blocker: sound rows throw in `GetPreview` (`dictionary2["skinImg"]` indexer; `KillSignatureRewardItem.GetImage` is null for "Sound") on every TLE/inbox/season screen | D8: sounds are bundles on avatar carrier rows (§2.3); a preview-safety rule in content-gen, `CollectorContent` and WP-F (§2.3.1); device check 1 opens the weekly screen with a fresh profile. |
| Blocker: empty content table throws instead of LastChance | §0.3.6, D9: one predicate for live ops and CRM (§2.12), a 5-cell guard whenever the layer is absent from `since` on, the guard in the minimal document and on any collector build failure; WP-F pins the hazard; §2.1, §5.3, §5.8 corrected. |
| Kayac rule misses 384 (and 382/383) | §1.2: folder **or** `kayac` anywhere in the file name (case-insensitive) **plus** 382 and 383 by their art (Kayac logo / Japan Cup series). Counts recomputed: 304 crate avatars, 139 `true` cells, 596 no-source items, base tier 665, everything 10,030. |
| Agent `Locked` default unverified | WP-F writes a real `Merc.ShortDef` list through the game's Templates writer and checks WP-B's fixture against it; a plausibility guard (squad agents must read unlocked); device check 14. |
| O7 not testable | WP-0 captures golden hashes from `e4bff79` before any change; `CollectorOffMatchesGolden` (WP-D, WP-E); separate known-flags per inventory part; agent parse skipped when the collector is not active. |
| Phone cost covered only the apply | §2.10 now covers parse size per document, applies per return to the menu **and** per match, with the D10 mirror policy (per-player growth 12.9 KB / 9.9 KB instead of ~25 KB). WP-F times parse + apply. |
| `store` patch dependency | D11, §2.13, WP-H (host reports it). |
| Minor: `MaxRewardsInAnimation` is 10 | §4.4 and checks 2–4: > 10 items show the plain list (shipped behaviour). |
| Minor: validator invariant for weighted rows without a `Type` | §2.8: checked on the merged document; failure → guard. |
| Minor: preview cost on the TLE screens | Device check 1 times it; fallback in §8 (fewer crate entries per event, content-only). Preview size corrected to ≤ 33 entries. |
| Minor: emblem numerals repeat after 12 seasons | D4 per-player emblem choice (21 cells). |
| Minor: `CratesOpened` counts sub-crates | §3.1 and pace: kept, +0.75 crates/week for casual players. Regular players settle in Diamond (band 3). |
| Minor: static agent units contradict the assumption; 40-gear duplicates | Static agent units 0; default `weights.agents` 2; windfall documented (§4.3). |
| Minor: D2 reasoning | Reworded. |
| Minor: settings defaults, one parser, release date | `economy.collector.enabled` in `Settings.CreateDefaults` (WP-D); live ops read `CollectorOptions`; content-gen warns when `since` is < 7 days ahead; release checklist (WP-G). |
| Minor: unbounded season multiplier | `maxSeasonItems` (40) caps one season claim (§2.4). |

### 0.5 Integration review round 3: what changed

| Finding | Resolution |
|---|---|
| Major: the guard depended only on the current `since` and clock; a crate handed out under an earlier `since` (a `liveops.json` override) or a later device clock could meet a `Dormant` document (D9 hazard) | §2.12: a **latch** in `<data>/collector-guard.json`: once live ops served an event carrying the crate (`CollectorRuntime.NoteCratesServed`, called by `LiveOpsService.FindEvent`) the phase is never `Dormant` again; a broken file counts as set; `GET _mcvs/crm` / `_mcvs/liveops` show `cratesServed`. Tests `TheLatchKeepsTheGuardForever`, `TheGuardStaysOnceCratesWereServed`. |
| Minor: the day before `since` the TS fetches tomorrow's quests and the announced next week, both with the crate, while the document was `Dormant` | `guardSince = min(since, ShippedSince) − CollectorGuard.Lead` (1 day). The 5 guard cells move one day earlier (allowed by the O7 reading). `EveryServedCrateHasRows` fetches every listed participation id. |
| Minor: the store filter waited for a profile read after a restart (not byte-identical to `ac64bfd`) | §2.9: the store filter loads the dump on disk at read count 0 too, as before; only the collector's per-player cells wait for a read. Test `StoreFilterBeforeTheFirstReadAfterARestart` (golden text). |
| Major: a milestone with two crates reveals only the last one (daily bonus with `Crate_Contracts`, weekly final with the event crate) | D12, §3.1: the daily crate moves to the **day's first mission** (new `collector.dailyMission`), the final's two crates to **tier 3** (1, 1, 2 per objective); the daily bonus and the weekly final keep today's rewards exactly. `ParseCollector` and content-gen refuse a crate beside another one. Owner point (§8). |
| Major: the weekly screen rebuilt the 1,518-entry avatar list up to 78 times (`IsAvailable` over rows 1, 3–6; the preview on every weighted avatar row) | §2.3: the exhausted VersusCoins row is now **row 1** (always available: `IsAvailable` stops there) and the carriers are **random background** rows (D8): at most one avatar-list build per crate object (the preview of row 2). WP-F test 10 logs the cost per weekly screen. |
| Minor: `Crate.Cosmetic_Crate` reads the same as the 65 event crates' `Crate.Cosmetics_Crate` in 7 languages | §2.2: the crate is named `crm.CrateCustomization` ("CUSTOMIZATION CRATE", distinct in all 15 languages). |
| Minor: WP-F never ran the paths a player hits | WP-F tests 9 (claim semantics, Cecil), 10 (list builds per screen and their cost), 11 (the real weighted draw over the served tables); the claim and reveal themselves stay device checks 2–4 (§7). |

### 0.6 Build-9 integration review: what changed

| Finding | Resolution |
|---|---|
| Major: the page showed the shorthand `"collector": false` as on, and saving the item count replaced it with `{"itemsPerCrate": N}`, switching the crate back on | §4.4: the page shows both keys as `CollectorOptions` reads them (`SettingsSchema.Read`), flagged with a note when settings.json holds something else; a write of either key first turns a non-object `collector` into `{"enabled": <what the collector read>}`. |
| Minor: the page's invalid-value note said "the default is used" for the kill switch (read as off) and showed item counts the collector reads differently (`"8"`, 15) | §4.4: the note says what the server uses (`invalidNote`); `itemsPerCrate` is shown parsed and clamped as the collector does. |
| Minor: a settings.json the server cannot parse ran on the defaults, collector on | §4.4: an unparseable file leaves the kill switch unreadable, so the collector is off (O7) until it is fixed or repaired; the repair panel says so. |
| Minor: switching the crate on the page restarts the running events without warning | The switch asks first (`confirmWhen` any; no backup: the events' progress is not in the save). |
| Minor: check 2 expected background + sound for every carrier draw; "the day's first mission" is not the first row on the phone | §7 check 2 and §3.1: a repeated sound pays a gear piece; the crate's mission is named by its card. |

The design's cells were recomputed by `collector/rev3/cells3.py` (`cells2.py` with the exhausted row first, background
carriers and the new loc id): 510 / 365 cells as before, hashes pinned by `StaticLayerMatchesTheDesignCells`.

## 1. Pools

### 1.1 Released no-source set and exclusions

"No-source" = released (in the `UNLOCKALL` groups of `unlocks.json`) and `content_gen.shipped_sources(entry)` empty,
with visible non-Kayac jerseys counted as sourced by the VIP pool (algorithm of `polish/scripts/inv2.py`).

| Category | No-source (released) | Excluded from the crate | In the crate |
|---|---:|---|---:|
| Avatars | 1,404 | Kayac 1,064 released (§1.2); starred season emblems 36 (D4) | **304** |
| Backgrounds | 18 (18–35) | – | **18** |
| Signature sounds | 4 (2–5) | – | **4** (as bundles, D8) |
| Skins | 16 | – | **16** |
| Hidden jerseys | 12 jerseys × 21 agents = 252 pairs | – | **252 pairs** |
| Agents | Witch 17, Bli-P 21 (they also have the 0.25 % `UnlockMerc_Rarity06` loot-crate row) | – | **2** |
| **No-source total** | | | **596** |

Shared random rows also reach signatures that do have another source (§1.3): 64 avatars and 5 backgrounds for a
fresh profile. The **base tier** a fresh profile drains is therefore **665 items**.

Paid tier (O5): 4,409 camo pairs + 4,956 jersey pairs = **9,365 pairs** reachable by the random rows (VIP-only subset:
2,684 + 2,940 = 5,624). Everything = 665 + 9,365 = **10,030 items** for a fresh profile once all 21 agents are unlocked.

### 1.2 Kayac / Kayac2 (exact)

Rule (content-gen `collector_tuning.json` `kayac`): the avatar's `Asset` is under `Menus/assets/png/profile/Kayac/`
(including `Kayac/holidays/`) or `Menus/assets/png/profile/Kayac2/`, **or** its file name contains `kayac`
(case-insensitive), **or** its id is in `kayac.extra` (default `[382, 383]`).

| Part | Ids | Count | Unlockable today |
|---|---|---:|---|
| `Kayac/` | 443–498, 500–531, 533–589, 598–603, 664–672 | 160 | 598–603, 664–672 true (15) |
| `Kayac/holidays/` (`emblem_JP_newyear_*`, `emblem_Xmas_*`, …) | 604–663 | 60 | all true (60) |
| `Kayac2/` | 673–1514 | 842 (1513–1514 unreleased) | all false |
| file name contains "kayac" elsewhere | 384 `avatars/emblem_JP_kayac_guest_384.png`, 499 `avatars/Kayac_mirrativ.png` | 2 | false |
| `kayac.extra` | 382 `avatars/emblem_JP_japancup_382.png`, 383 `avatars/emblem_JP_kaya_383.png` | 2 | false |
| **Total** | **382–384, 443–531, 533–589, 598–1514** | **1,066** (1,064 released) | **75 true: 598–672** |

Why 382 and 383 (decoded textures, `collector/rev/jp.png`): 383 and 384 carry Kayac's kayak logo, with 運営
("operator/staff") and 公認 ("official") under it; 382 is named `japancup`, and the other Japan Cup emblems
(`emblem_JP_japancupWAR/ZONE/SUMMER_TM`) live in `Kayac/`. They are `Unlockable=false` today, so excluding them only
means not sending them a `true` cell. Owner points: the 60 holidays are generic-looking (New Year, Christmas) but live in
the Kayac folder; removing `Kayac/holidays/` from the rule would add 60 crate avatars and drop 60 `false` cells;
emptying `kayac.extra` would put 382/383 back.

They all stay in the VersusCoins signature shop (the shop does not read `Unlockable`: `GM/GameOfflineStore.cs`
`GetKillSignItems`). Not Kayac by this rule and in the crate: 306–321 (`season_rewards/emblem_JP_agent_*`, generic
agent emblems with laurels, no Kayac logo) and 442 (`emblem_SKIN_ONI-MASA`).

### 1.3 Avatars (304 in the crate)

| Set | Ids | Count |
|---|---|---:|
| Crate avatars (base) | 61–76, 78, 80–90, 92–219, 221–253, 257, 261, 265, 269, 273, 277, 281, 285, 289, 293, 297, 301–321, 362–381, 385–391, 393–442, 532, 593–597 | 304 |
| … already `Unlockable=true` | 61–76, 78, 80–90, 92–219, 221–224, 593–597 | 165 |
| … `Unlockable=false` → **`true` cells** | 225–253, 257, 261, 265, 269, 273, 277, 281, 285, 289, 293, 297, 301–321, 362–381, 385–391, 393–442, 532 | 139 |
| Other drawable (shipped `true`, not Kayac, not base) | 1–6, 9–60, 77, 79, 91, 220, 590–592 (1 = starting avatar) | 65 |
| Kayac `true` → **`false` cells** | 598–672 | 75 |
| Reserved starred season emblems (stay `false`) | 254–256, 258–260, 262–264, 266–268, 270–272, 274–276, 278–280, 282–284, 286–288, 290–292, 294–296, 298–300 | 36 |
| Unreleased (never touched) | 1513–1518 (two Kayac2, four Bli-P emblems) | 6 |

By folder the 304 are: generic `avatars/` 225, `season_rewards/` 28 (12 unstarred numerals + 16 JP agent emblems),
`KillSign_Agents/` 51; by rarity R1 164, R2 75, R3 27, R4 38. The "other drawable" 65 are 57 shop-only, 4 event-crate
random-pool and 3 league rank-up rewards (590–592): if the crate hands out one of the latter first, the later rank-up
reward pays its duplicate compensation (one `Gear_Random_1`); accepted.

Drawable after the game layer = shipped-true minus Kayac plus the 139 = **369** (bench: real
`SignatureItemDataCollection` fill: 369 of 1,518 unlockable, none in the 1,066 Kayac set); minus what the player owns.
With only the mirror (no `true` cells): 230, none Kayac.

### 1.4 Backgrounds, sounds, skins, hidden jerseys, agents

| Category | Ids | Notes |
|---|---|---|
| Backgrounds (base) | 18–35 | 18–23 are `Unlockable=false` → `true` cells (6); 24–35 already true. Other drawable: 1–6 (1 = starting). Drawable after the cells: 24. 7–17 are shop-only, not drawable. |
| Sounds | 2, 3, 4, 5 | Share sound 1's asset `sfx_ui_crate_reveal`. Delivered as `BundledRewards` of carrier rows (D8, §2.3); the bundled `KillSignatureRewardItem` claims through `OnPrepareToClaim` (`!KillSignatureSounds.Contains`), an owned one pays `Gear_Random_1_Random`. The signature screen reads owned sounds from the backgrounds list (game bug, STATUS build 6), so a won sound may still show in the shop. |
| Skins (merc_skin) | 2_5, 4_6, 6_4, 8_7, 9_3, 9_4, 10_7, 11_3, 11_4, 12_4, 15_6, 17_6, 17_11, 17_12, 19_4, 20_4 | Explicit `MercSkin/<merc>/<skin>` rows (all `Locked=false` in `SkinsList_MercNN`). The random `MercSkin` row would draw any unowned available skin (39), most of which have sources. |
| Hidden jerseys | 112 USSR, 143/144 ARTfire1/2, 152/153 HOKUSAI, 212–214 Spring01–03, 268–271 ID54–57 | `VisibleInShop=false`, `IsKayac=false`, no `MercId`. **11 of the 12 use art from `First100Jerseys/Kayac/` or `Kayac2/`** (all but USSR); O5 names them explicitly, so they stay (owner point; remove ids from `hiddenJerseys` in `collector_tuning.json` to drop them). Every random jersey pool filters `VisibleInShop` (`GM/MercAccessoriesComponent.cs:118-130`), so they need explicit `MercAccessory/Random/<id>` rows: `OnPrepareToClaim` (`GM/MercAccessoryRewardItem.cs:100-118`) picks a random unlocked agent lacking that jersey, with no visibility filter. |
| Agents | 17 Witch, 21 Bli-P (both `Rarity06`, displayed) | `UnlockMerc/<id>` rows (`GM/UnlockMercRewardItem.cs:84-91`). Once owned the row's per-player weight is 0; drawn anyway (stale weights within a session) the claim pays `AlternateRewards` `UnlockMerc_Rarity06` = 40 × `Gear_Random_3_Random` (§4.3). In the preview both rows have `img ""` and merge into one agent entry. |

### 1.5 Paid tier: camos and jerseys (verified random forms)

| Form | Client code | How it picks |
|---|---|---|
| `WeaponCamo_Random_Random` | `GM/WeaponCamoRewardItem.cs:101-149` | Random unlocked agent (not "0"), then a random camo of its `VanillaWeapon` table that it does not own (`GetCamosList`: non-Kayac for a non-Kayac profile); an agent with none left is removed and another drawn. Fails (→ alternate) only when no unlocked agent has a camo left. |
| `MercAccessory_Random_Random` | `GM/MercAccessoryRewardItem.cs:100-129` | Random unlocked agent, then a random jersey from `GetLockedAccessoriesForMerc` (`VisibleInShop`, not Kayac-hidden, agent-matching, not owned); agent removed when it has none. |
| `MercAccessory_Random_<id>` | same, lines 109-117 | Random unlocked agent lacking jersey `<id>` (no visibility filter). Used for the hidden jerseys. |

Pool per agent (verified equal to the `camos`/`accessories` groups of `unlocks.json`, minus the 12 hidden jerseys):
camos 210–212 per agent (202 for agent 21; Kayac camos excluded; camo "1" is the default) = **4,409** pairs; visible
non-Kayac jerseys **236** × 21 = **4,956** pairs. Listing only the VIP-only pairs would need 268 rows (> 100), so the
crate's paid tier is "every locked camo/jersey pair of an unlocked agent", which includes the VIP-only ones.

### 1.6 Row types and their duplicate behaviour

| Row | Resolves at claim to | When nothing is left / already owned | Alternate (`MCVS_Meta.AlternateRewards`) |
|---|---|---|---|
| `KillSignature/Avatar/Random` | random locked **and** unlockable avatar (`GM/KillSignatureRewardItem.cs:117-143`) | no candidate | `Gear_Random_1_Random` ×1 |
| `KillSignature/Background/Random` | same for backgrounds | no candidate | `Gear_Random_1_Random` ×1 |
| carrier (random background row + bundle `KillSignature_Sound_<n>`) | a random background **and** that sound | each part independently | `Gear_Random_1_Random` ×1 per owned part |
| `MercSkin/<m>/<s>` | that skin | owned, or agent locked | `Gear_Random_3_Random` ×1 |
| `MercAccessory/Random/<id>` | that jersey on a random unlocked agent lacking it | every unlocked agent has it | `Gear_Random_2_Random` ×1 |
| `UnlockMerc/<id>` | that agent | unlocked | `Gear_Random_3_Random` ×40 |
| `WeaponCamo/Random/Random`, `MercAccessory/Random/Random` | §1.5 | nothing left on unlocked agents | `Gear_Random_2_Random` ×1 |
| `VersusCoins/Tiers4` | 70 VersusCoins (`VersusCoinsTiers`) | never | – |

## 2. CRM cells

All cells are `game._OverrideTables.MCVS_Meta.<table>["<index>@<field>"]["<key>"]`; what goes into
`game._ServerConfig.default._OverrideTables` is §2.9.3 (D10). `#` stands for `.` in field names. Numbers are JSON
integers; strings are non-empty JSON strings. Only existing rows can change (`EN/Database.cs:143-196`, first row whose
index field equals the key; a null target cell is simply set).

### 2.1 How the crate draws (why `CratesCount`)

* `CrateRewardItem.OnClaimed` (`GM/CrateRewardItem.cs:77`): `Quantity *= CratesCount`, then
  `OpenCrateOnServer(id, profile, Quantity)` → `GenerateCratesRewards` loops `Quantity` sub-crates, claiming each item.
* Each sub-crate runs `GenerateRewards` (`GM/CratesInventoryComponent.cs:530-585`) for `RewardsCount` rounds; round *i*
  uses weight column *i* while `i < WeightsCount` (2), else column 0. With `RewardChance.2 = 0`, `RewardsCount = 1`: every
  sub-crate is one `Weight.1` draw, **plus** the drawn row's `BundledRewards` when `AlwaysGiveBundledRewards` is true
  (carriers, §2.3). Draws are not filtered by availability; random subtypes resolve at claim (§1.6).
* **The content table must have at least one typed row** (`rewards[0]`, §0.3.6). `Crate_LastChance` only covers "rows
  exist but every weight is 0". Hence D9 and §2.12.
* UI: `rewardInfo["count"] = CratesCount × Quantity`, `contentDesc` = `menu.ShopCrateContent` ("CONTAINS {n} ITEM(S)";
  "WithBonus" only when round-1 chance > 0), `preview` = `GetPreview` (lines 790-895), rebuilt on **every** `GetObject`
  of a crate reward, claimed or not (TLE milestone lists, inbox, season-end screen, claim popups).
* Turning-point modifiers (`CrateItemsAbove/BelowTPMultiplier`) match by `RewardId`; none of ours match and every
  `CrateTurningPoints.PowerProgression` is 0. No effect.

### 2.2 `LootCrates` row `Crate_Event_Special` (3 cells; row 247 of 247)

| Field changer | Key | Value | Today |
|---|---|---|---|
| `Id@LocId` | `Crate_Event_Special` | `"crm.CrateCustomization"` ("CUSTOMIZATION CRATE", `Localization/crm_*.json`, all 15 languages) | `menu.LegendaryPremium` |
| `Id@CratesCount` | `Crate_Event_Special` | `itemsPerCrate` (1–10) | 1 |
| `Id@RewardChance#2` | `Crate_Event_Special` | `0` | 100 |

Image (`crate_Legendary_Open.png`), Rarity06, ShopType Loot stay. The crate is not sold anywhere. Round 3: the name was
`Crate.Cosmetic_Crate` ("COSMETIC CRATE"), which reads the same as the 65 weekly event crates' `Crate.Cosmetics_Crate`
in 7 languages (id, ja, ko, th, tr, zh, zt); `crm.CrateCustomization` differs from it in every language and is used by
no shipped crate.

### 2.3 Content table `Crate_Event_Special` (39 rows, 161 cells)

Rows 1–39 get `Id@Type`, `Id@Subtype`, `Id@Subtype2` (omitted when "–") and `Id@Weight#1` (always sent, 0 allowed);
the carrier rows 4–7 also get `Id@BundledRewards` and `Id@AlwaysGiveBundledRewards = "true"` (the shipped event crates
use the same pair: `Crate_Event_1`…`5`, skin rows with a camo bundle). `Weight.2` and `FixedQuantity` are never sent.
Rows 40–100 keep a null `Type` and are skipped (`CrateRewards.AddElement`). Never send `""` (CRMTableCell throws
`FormatException`) or null. Numeric-looking strings (`"133"`) become doubles in the cell; the typed reader converts
them back with `Convert.ChangeType(…, InvariantCulture)` (bench: `RewardId=MercAccessory_Random_112`). `Weight.N` is
re-read with `float.Parse(cell.ToString(), Invariant)`, so weights are integers only.

| Row ids | Category | Type | Subtype | Subtype2 | Bundle |
|---|---|---|---|---|---|
| 1 | exhausted | `VersusCoins` | `Tiers4` (setting `exhaustedReward`) | – | – |
| 2 | avatars | `KillSignature` | `Avatar` | `Random` | – |
| 3 | backgrounds | `KillSignature` | `Background` | `Random` | – |
| 4–7 | sounds (carriers) | `KillSignature` | `Background` | `Random` | `KillSignature_Sound_2` … `_5` |
| 8–23 | skins | `MercSkin` | merc | skin, in the order of §1.4 (8 = `2`/`5` … 23 = `20`/`4`) | – |
| 24–35 | hiddenJerseys | `MercAccessory` | `Random` | `112`, `143`, `144`, `152`, `153`, `212`, `213`, `214`, `268`, `269`, `270`, `271` | – |
| 36, 37 | agents | `UnlockMerc` | `17`, `21` | – | – |
| 38 | camos | `WeaponCamo` | `Random` | `Random` | – |
| 39 | jerseys | `MercAccessory` | `Random` | `Random` | – |

Bench readback (real `CrateRewards` fill, WP-F test 3): 39 typed rows; row 4 `RewardId=KillSignature_Background_Random
Bundled=KillSignature_Sound_2 Always=True`; rows 40–100 absent. (Round 2 had the exhausted row last and avatar
carriers; `collector_tuning.json` `rows` and `soundCarrier` hold the order and the carrier kind.)

**Why the exhausted row first (round 3).** Every crate reward object on a TLE screen (the weekly event builds 12, one
per milestone, claimed or not: `GetMilestonesObject`) runs `CrateRewardItem.IsAvailable` =
`GetCrateRewards(...).Any(row => CreateRewardItem(row).IsAvailable(profile))`, the rows in table order, weight not
considered. A random avatar row's `IsAvailable` rebuilds the 1,518-entry avatar list (`GetSignatureAssetsList`:
`ownedIds.Contains` per entry on a `List<string>`; 0.5–6.9 ms on net8 for 50–1,400 owned, review measurement). Row 1 =
`VersusCoins` is always available, so the check stops there without any list, whatever the player owns; its weight stays
0 until everything is owned (§4.1), so it is neither drawn nor previewed before.

**Why carriers (D8).** `GetPreview` builds each entry's dedup key as `Type##` + `(dictionary2["img"] as string) ??
(dictionary2["skinImg"] as string)` — the second part is a plain `Dictionary` indexer. `KillSignatureRewardItem.GetImage`
returns null for "Sound" (and for a non-random background) and its `GetRewardInfo` never sets `skinImg`, so any sound
row with `Weight.1 > 0` throws `KeyNotFoundException` in every preview. A carrier is a random-background row: its key
`KillSignature##…/Random_Player_bg.png` equals row 3's, so it merges into row 3's preview entry and nothing new is
shown; `GetPreview` ignores bundles. `GenerateRewards` adds the bundle after the drawn background
(`(Claimed || !IsAvailable) && !AlwaysGive` is false). A carrier draw therefore yields **two** items (background +
sound); with backgrounds exhausted its background part pays one `Gear_Random_1` (at most four times per profile).
Weight-0 rows are skipped before the key is built (`IsSpawnable`), so the hazard only concerns weighted rows. Round 3:
the carriers were random-avatar rows; `GetPreview` calls `GetObject`, so `IsAvailable`, on every spawnable row before
it deduplicates, so each avatar carrier rebuilt the avatar list once more per crate object (up to 6 builds with row 1's
`IsAvailable`); a background carrier builds the 35-entry background list instead. A crate object now builds the avatar
list at most once (row 2's preview), never for a complete profile (row 2 then has weight 0); WP-F test 10.

#### 2.3.1 Preview-safety rule (enforced in three places)

A row may carry `Weight#1 > 0` only if its reward is one of: `KillSignature/Avatar|Background/Random`, `MercSkin/<m>/<s>`
(sets `skinImg`), `MercAccessory/Random/<id|Random>`, `UnlockMerc/<id>` (`img ""`), `WeaponCamo/Random/Random`,
`VersusCoins/Tiers<n>`. Sounds only as `BundledRewards`, on `KillSignature/Avatar|Background/Random` rows of the sounds
category (each its own sound). Enforced by content-gen (`ContentError`), by `CollectorContent.Parse` (file refused →
collector inactive, guard), and by WP-F against the real client.

`Weight#1` values: §4.1 (static document: §4.2; per player: §2.9).

### 2.4 Ranks 4–30: retarget the 14 tier crates (84 cells)

Divisions map to tiers: 1 Starter; 2–4 Bronze; 5–7 Silver; 8–10 Gold; 11–13 Platinum; 14–16 Diamond; 17 Master
(`PVPDivisionRewardsType`, ranks 4–10 → `Crate_<Tier>_4To10Place`, 11–30 → `Crate_<Tier>_11To20Place`, quantity 1).
For each of these 14 crates (`LootCrates` rows 212–243), with `band` from `economy.collector.seasonCrates` (default
Starter 1, Bronze 1, Silver 2, Gold 2, Platinum 3, Diamond 3, Master 4):

| Field changer | Value |
|---|---|
| `Id@ContentTableName` | `"Crate_Event_Special"` |
| `Id@CratesCount` | `max(1, min(band × itemsPerCrate, ⌊maxSeasonItems / q⌋))`, q = `⌈liveops.seasonRewardMultiplier⌉` (1 when ≤ 1) |
| `Id@RewardChance#2` | `0` |
| `Id@LocId` | `"crm.CrateCustomization"` |
| `Id@Image`, `Id@ImageShop` | `"Menus/assets/png/Crates/Crates/crate_Legendary_Open.png"` |

`maxSeasonItems` (default 40 = band 4 × N 10) caps the items of one season claim: the season multiplier's MUL cell on
`PVPDivisionRewards` multiplies the number of tier crates *q*, and quantity *q* opens *q* × `CratesCount` sub-crates,
so without the cap band 4 × N 10 × multiplier 100 would generate 4,000 sub-crates in one claim. A band of 0 leaves the
tier's two crates alone (today's gear piece). Ranks 4–30 lose today's single random gear piece (accepted: the owner's
"1–4 Cosmetic crates" replace it). Only `PVPDivisionRewardsType` references these crates (`polish/scripts/crates.py`).

Bench: `Crate_Gold_4To10Place` after the cells: `RewardsCount=1 CratesCount=6 chances=100/0
LocId=crm.CrateCustomization Content=Crate_Event_Special` (round 2 measured it with the former loc id).

Rejected alternative: SET `PVPDivisionRewardsType` for ranks 4–30 to `"Crate_Event_Special"` — 918 cells, and the
season multiplier would have to be folded into SET cells (a SET cell is never multiplied).

### 2.5 Ranks 1–3: the season emblem (42 cells; 21 of them per player)

On row `1` of each `Crate_<Tier>_FirstPlace`, `_SecondPlace`, `_ThirdPlace` content table (21 tables). Row 1 is the
only `Weight.1 > 0` row in all 21 (HardCurrency, 60–360; `Weight.2 = 0`), so round 0 always draws it and the bundle
follows unconditionally:

| Field changer | Key | Value |
|---|---|---|
| `Id@BundledRewards` | `1` | `"KillSignature_Avatar_<id>"` |
| `Id@AlwaysGiveBundledRewards` | `1` | `"true"` |

Emblems (`season_rewards/`, `Rarity04`): base = no star, `_1` = ★, `_2` = ★★, `_3` = ★★★. **1st place → ★★★, 2nd →
★★, 3rd → ★**.

| Numeral | I | II | III | IV | V | VI | VII | VIII | IX | X | XI | XII |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| base | 253 | 257 | 261 | 265 | 273 | 277 | 281 | 285 | 269 | 289 | 293 | 297 |
| ★ / ★★ / ★★★ | 254/255/256 | 258/259/260 | 262/263/264 | 266/267/268 | 274/275/276 | 278/279/280 | 282/283/284 | 286/287/288 | 270/271/272 | 290/291/292 | 294/295/296 | 298/299/300 |

Static document: numeral = `I…XII[(LiveOpsSchedule.Ordinal(endedSeason)) mod 12]`, `endedSeason = new
LiveOpsWindow(season.Start − season.Days, season.Days)`. Example: during the season starting 2026-10-26 (ordinal 147)
the ended season is 146 → III → 1st 264, 2nd 263, 3rd 262; from 2026-11-02: IV → 268/267/266.

**Per player (D4):** for each star count, starting at the season's numeral and cycling through I…XII, the first
emblem the player does not own; all 12 owned → the season's numeral (paid as one `Gear_Random_1`). The 3 values are
written into the 21 tables' `Id@BundledRewards` (7 tiers share them). Unknown avatars inventory → static value. The
bundle is read **at claim time** on the TS, so the claim uses the document current at that moment. Emitted only while
division seasons are on (`economy.collector.seasonEmblems`, default true).

### 2.6 `Unlockable` cells and the per-player window

| Table | Field changer | Static document | Per-player document |
|---|---|---|---|
| `KillsignatureAvatars` | `ID@Unlockable` | `"true"` for the first `unlockableWindow` ids (ascending) of the 139; `"false"` for 598–672 | `"true"` for the first `unlockableWindow` of the 139 **the player does not own**; `"false"` for the unowned of 598–672 |
| `KillsignatureBackgrounds` | `ID@Unlockable` | `"true"` for 18–23 | `"true"` for the unowned of 18–23 |

`unlockableWindow` defaults to 160 (≥ 139, the whole remaining set); 0 sends no `true` cells (the avatar row then
draws only from the 165 + 65 already-unlockable ones). The Kayac `false` cells are not windowed: they enforce O5. Sizes:
≤ 139 + 75 + 6 = 220 cells for a new profile, 0 for an unlock-all profile. Other readers of `Unlockable`: level-up
rewards (`AvatarRewards`, 0 unless §2.7), `IsAvailable` of a specific signature reward (UI flag only), bots' random
signatures on the GS (`GM/MultiplayerComponent.cs:1217-1225`; the GS sees the mirror: no Kayac, no `true` cells). The
signature shop and the UI SWFs never read it.

### 2.7 Optional level-up avatars (49 cells)

`economy.collector.levelUpAvatars` = N (0–3, default 0): `PlayerLevelXP` `Level@AvatarRewards` = N for levels 2–50
(`GM/PlayerProfileComponent.cs:6973-7030`). Level-ups are processed on a server World (TS or GS), so these cells are in
the game layer **and** the mirror. On the GS (mirror, no `true` cells) they draw from the 230 shipped-unlockable
non-Kayac avatars. No conflict with `economy.xpMultiplier` (`XPRequired`) or Trophy fever (`DivisionTrophiesBonus`).

### 2.8 The SET writer, the guard and validation (`CrmDocumentBuilder`)

Next to `AddMul`:

```csharp
/// <summary>SET cell: replaces whatever the cell holds (MUL, ADD or SET; one operation per cell, CRMTableCell).
/// value: a non-empty string or an integral long. Never "", null, NaN or a fraction.</summary>
internal static void SetCell(Dictionary<string, object> tables, string db, string table, string index, string field,
                             string key, object value);
```

`field` is given with dots and written with `#`. Call order inside `Build`: after `ApplyLiveOpsCells`, before
`ApplyEvents` and before `crm.overlay` (the overlay still wins). The collector touches no table/field that a MUL target
uses (`economy.json` targets, live-ops modifiers, `seasonRewards`), asserted by a test.

`CollectorPlan` (WP-C) is pure and returns `CollectorLayer { IList<CollectorCell> Game; IList<CollectorCell> Mirror; }`
(`CollectorCell { Table, Index, Field, Key, Value }`). The builder writes `Game` into `game._OverrideTables` and
`Mirror` into the mirror after the generic mirror pass (§2.9.3).

**The guard** (`CollectorGuard.Cells`, compiled constants, no content file needed), 5 cells:
`Crate_Event_Special` `Id@Type` `1` = `"VersusCoins"`, `Id@Subtype` `1` = `"Tiers4"`, `Id@Weight#1` `1` = `1`;
`LootCrates` `Id@LocId` `Crate_Event_Special` = `"crm.CrateCustomization"`, `Id@RewardChance#2` `Crate_Event_Special` =
`0`. A stale Cosmetic crate then shows "CUSTOMIZATION CRATE, CONTAINS 1 ITEM", previews one VersusCoins entry and pays 70
VersusCoins per crate (bench readback: `RewardId=VersusCoins_Tiers4 W1=1 W2=0`, 1 typed row). Not the `CratesCount=0`
variant: it ends in an empty `NotifyCrateReceived`. The guard goes into both trees.

**Isolation:** the collector part of `Build` runs in `try/catch`; an exception or a validation failure replaces the
layer by the guard (both trees), logs `CRM: collector layer replaced by the guard: <reason>` and keeps the rest of the
document. `CrmService.MinimalDocument` always carries the guard.

**Validation** (`CrmDocumentValidator.CheckCollector`, on the **merged** document, overlay included, for both trees):
every collector cell is scalar, non-empty and integral when numeric; every `Crate_Event_Special` row that holds any
`Weight#N` cell also holds a non-empty `Id@Type` (the shipped rows have none; `CrateRewards.FillDataWithTableImpl` does
`base[id].SetDefaultWeight(...)` and would throw a `NullReferenceException` during the TS's table refill); every row
with `Weight#1 > 0` passes §2.3.1; `Crate_Event_Special` has at least one row with `Weight#1 > 0`. Per-player patches
are checked the same way before serialisation (failure → the static document is served).

### 2.9 Per-player document

Today `CrmService` serves `CrmOwnedStoreItems.JsonFor(doc, account)` for `configs/users/me` with a player's token when
`crm.omitOwnedSignatures` is on. New rule: the per-player path runs when `omitOwnedSignatures` **or** the collector is
`Active` (§2.12); the collector's cells only for an account whose game profile has been read since the server started
(`storage.gameprofile.read`), the store filter also before (round 3: as today, the dump on disk is taken once per read
count, 0 included, so the first fetch after a restart is filtered exactly as before the collector existed); selectors
and `configs/users/{user}` stay static. Server Worlds fetch their configuration through a `mcvsGameServer` session logged
in with the `game:` super credential (`GM/GameProfileSection.cs:65`, `EN/CRMServerSettingsComponent`,
`AuthEndpoints.CheckGameCredential`), an account without a game profile, so they always get the static document (WP-D
test `ServerSessionGetsStaticDocument`). The TS's per-user `CRMComponent` fetches with the player's on-behalf token and
gets the per-player document.

1. **Inventory** (`PlayerInventory.Scan(dump, CollectorPhase)`, on the first request after `storage.gameprofile.read`,
   like today's owned set). Each part has its own known flag and problem text:
   * `AvatarsKnown`, `BackgroundsKnown`: today's string scan of `KillSignatureAvatars` / `KillSignatureBackgrounds`
     with today's exact refusal rules. **The store filter depends on these two only** and stays byte-identical.
   * `SoundsKnown`: `KillSignatureSounds`, same rules, independent.
   * `MercsKnown` (parsed only when the phase is `Active`): `JsonSpanScanner.TryFindValue(dump, ["$default",
     "/Profile", "Mercs"])`, then MiniJson on that span (~1–200 KB of a ~560 KB dump). Element = object with `Id`
     (string or number), `Locked` (bool or `"true"/"false"`), `Skins`, `Camos`, `Accessories` (absent = the
     `Merc.ShortDef` defaults: `Locked` true, `Skins ["1"]`, `Camos ["1"]`, `Accessories []`). An agent missing from
     the list is locked (`MercsComponent.ProfileLoaded` adds locked ShortDefs at load); agent 1 is unlocked by the
     starting inventory. **Plausibility guard:** every agent id in `Squads[].Mercs` (`"1"` in `DefaultProfile.tx`) that
     is present in `Mercs` must read unlocked, else `MercsKnown=false` ("squad agent N reads locked: unexpected
     Mercs shape"). Any other shape → `MercsKnown=false`. The element shape is pinned by WP-F (real Templates writer).
   * An unknown part falls back to the static assumptions of §4.2 for that part only, logged once per read.
2. **Cells**: `CollectorPlan.PlayerCells(content, options, inventory, snapshot)` → patches of `game._OverrideTables`:
   `Crate_Event_Special` `Id@Weight#1` (39), `KillsignatureAvatars` `ID@Unlockable`, `KillsignatureBackgrounds`
   `ID@Unlockable`, and the 21 placement tables' `Id@BundledRewards` (§2.5). A changer that ends up empty is removed.
3. **Mirror (D10)**: in the per-player document, the collector's cells in `game._ServerConfig.default._OverrideTables`
   are replaced by the guard (5 cells); non-collector mirror cells are untouched. The builder precomputes this
   per-player mirror once per static document (`CrmDocument.PlayerMirrorTables`). If anything did read the per-player
   mirror, a crate would pay VersusCoins (safe, visible in device checks 1 and 15), never throw.
4. **JSON**: `CrmDocument.JsonFor(ICollection<string> omitStoreItems, IList<FieldPatch> patches)` shallow-copies the
   dictionaries along the patched paths, replaces the patched field changers (keys in ascending numeric order) and the
   mirror tables of step 3, filters `offline_store.prices` as `JsonWithout` does, and serializes once. The cached tree is
   never mutated. A patched key is only replaced if the static document still holds the collector's own static value
   there (a `crm.overlay` cell on the same key wins).
5. **Caching key**: per account, `(CrmDocument reference, profile-read counter)`; before the first read (counter 0) the
   per-player text is the store filter alone, with the static layer and the static mirror. Same inventory → same text, so
   `DoSaveProfile`'s refresh is recognised as equal (`StandardCrm.Data.Equals`) and not re-applied.
6. Logging: one line per rebuild: `CRM: collector for <account>: base N left (avatars a, backgrounds b, …), paid M left,
   K Unlockable cells, parts known avatars|backgrounds|sounds|mercs (<problems>)`.

Who applies which tree: the TS's `CRMComponent.RefreshDatabases` (`EN/CRMComponent.cs:280-291`, on every CRM set and
in its `DatabaseLoaded` handler) and the client (`GM/GameplayCRMComponent.cs:166-186`, `RawGameParameters` = `game`)
apply the per-player `game._OverrideTables`. Server Worlds' `GameplayCRMComponent` `DatabaseLoaded` handler (lines
174-181) and the GS's `OnClientConnected` → `RefreshDatabases(gameParameters)` (line 143, per match; the client gets the
same `gameParameters` and applies them too) install the **static** mirror. `PerUserDatabase` is one process-wide
registry and `DatabaseLoaded` is a static event, so whichever handler runs last owns a per-user database's changer
until the next set; the TS's forced refresh on every return to the menu re-applies the per-player layer. Every
changer the server can produce opens a valid crate (layer, mirror or guard); see §8 for the residual race.

### 2.10 Cost and limits

Cells and size (compact JSON, `cells2.py`); warm apply = minimum of 25 warm runs of the real
`Table.ApplyCRMTableChanges` on net8 (`rev/bench2`; cold first apply 63–98 ms, JIT):

| Tree | Cells | JSON | Warm apply (net8) | Applied by |
|---|---:|---:|---:|---|
| Game layer, static document / fresh player (`LootCrates` 87, crate rows 161, placement 42, avatars 214, backgrounds 6) | 510 | 12.7 KB | 13.5–14.1 ms (avatars 11.3–11.6: `true` cells 4.7–6.9, Kayac `false` 5.0–5.7) | TS + client per return to the menu |
| Game layer, unlock-all player (no Unlockable cells) | 290 | 9.7 KB | ~2.5 ms | same |
| Mirror, static document / `game` selector (no `true` cells) | 365 | 10.8 KB | 3.8–8.3 ms | GS + client per match; server Worlds on `DatabaseLoaded` |
| Mirror, per-player document = guard | 5 | 0.2 KB | < 0.1 ms | (nobody known) |
| Optional level-up cells (each tree) | +49 | +1.3 KB | ~0.3 ms | |

Document growth: per-player fresh 403 KB → +12.9 KB (+3.2 %); per-player unlock-all 89 KB → +9.9 KB (+11 %); static
document +23.5 KB. Per return to the menu the TS parses the per-player text twice in the forced refresh and twice more
in `DoSaveProfile`'s refresh, the client and the native engine parse it again (≥ 6 parses), and the TS and client apply
it twice. Upper estimate from build 5's timings (~5.6 s of CRM work for 403 KB, fixed costs included ≈ 14 ms/KB):
+0.18 s fresh / +0.14 s unlock-all for parsing, plus 2 applies × 14 ms × 3–5 (Mono/ARM) = 0.08–0.14 s → **+0.2–0.3 s**,
at the device target (≤ +0.3 s, check 10). Per match: 2 mirror applies × 4–8 ms × 3–5 = 25–80 ms (check 10).

Budget: ≤ 600 collector cells per tree, enforced by `CollectorPlanTests.LayersStayWithinBudget`. Safety valves, in
order: `unlockableWindow` (the 139 `true` cells, ~40 % of the game layer's cost), `levelUpAvatars` 0. Documented
fallback if check 10 still misses (not built now): explicit rolling `KillSignature/Avatar/<id>` rows in the free rows
40–100 instead of Unlockable cells (no avatar-table cells at all; Kayac excluded by construction; costs: a longer
preview, `IsAvailable` false for `Unlockable=false` ids in the UI).

### 2.11 Season transition

* The static document changes at the season expiry (the CRM cache key contains `LiveOpsSnapshot.Key`); the per-player
  document follows (new document reference).
* Ranks 4–30: the reward type and quantity are fixed when the TS evaluates the season (`GM/DivisionsComponent.cs:362-421`,
  `SendRewardToInbox`, 30-day message); the crate's **contents** are drawn when the player claims it, with the
  then-current weights.
* Ranks 1–3: the bundle is read **at claim time**. The document of season S names numeral(S−1) (statically) or the
  player's next unowned emblem (§2.5); a claim during the season after the one that ended (the normal case) gets the
  right emblem. Catch-up after weeks away gets the numeral of the current calendar's previous season. Accepted.
* Collector switched off between evaluation and claim: §5.3.

### 2.12 Never an empty crate: one predicate and the guard (D5, D9)

`CollectorRuntime` (WP-C, one instance per server: `CollectorRuntime.For(context)`, get-or-create in
`context.Features`) is the **only** place that decides whether the collector runs. Inputs: `CollectorOptions` (parsed
once per settings snapshot, the single parser of `economy.collector.*`), `CollectorContent` (`collector.json` through
`context.Content`, re-read when the file changes; null + problem text when missing, broken or failing §2.3.1),
`ClientCapabilities.StorePatch` (§2.13), and from the caller: live ops on, `since` (the `collector` block of
`liveops.json`), the time.

```csharp
public enum CollectorPhase { Dormant, Guard, Active }
public sealed class CollectorStatus { public CollectorPhase Phase; public string Reason; public DateTime? Since; }
public CollectorStatus Status(DateTime utcNow, bool liveOpsOn, DateTime? since);   // CRM builder
public bool CratesInWindow(DateTime windowStart, DateTime? since);                    // LiveOpsEvents
public void NoteCratesServed(string eventId, DateTime windowStart, DateTime utcNow);  // LiveOpsService.FindEvent (round 3)
public DateTime? CratesServedAt { get; }                                              // the latch (round 3)
```

* `Active` ⇔ `enabled` ∧ live ops on ∧ content valid ∧ `StorePatch != false` ∧ `since` known ∧ `utcNow ≥ since`.
* `Guard` ⇔ not `Active` ∧ (`utcNow ≥ guardSince` ∨ the latch is set), `guardSince = min(since,
  CollectorGuard.ShippedSince) − CollectorGuard.Lead` (`since` unknown → `ShippedSince − Lead`; `ShippedSince` is the
  shipped `since` compiled in and asserted equal to `server/Content/liveops.json` by `GeneratedContentTests`; `Lead` = 1
  day, round 3).
* `Dormant` otherwise: no cells at all; documents and events are **byte-identical** to `e4bff79` (golden, WP-0).

**The lead (round 3).** Live ops list the next quest day for the whole current day and announce the next weekly event
during the last day of a season (`LiveOpsService.Snapshot`), and the TS fetches every listed event; the first window
from `since` on is therefore in the TS's hands up to a day before `since`, with the crate in it (shown from the
LootCrates row with an empty preview, `IsAvailable` false). From `since − 1 day` the document carries the guard, so such
a crate is never shown or claimable without a typed row. With the shipped `since` the golden comparisons therefore strip
the 5 guard cells from 2026-11-08 on.

**The latch (round 3).** The phase is decided from the current `since` and clock, but crates already handed out stay in
the TS's profile (a kept-alive weekly event, a completed daily that blocks the next one): after a `liveops.json` override
with an earlier `since` is removed, or the device clock goes back (the embedded server uses the phone's clock), the
same rules would give `Dormant` and such a crate an empty content table. So once `LiveOpsService.FindEvent` served an
event whose template carries `Crate_Event_Special`, `CollectorRuntime.NoteCratesServed` writes
`<data>/collector-guard.json` (`cratesServed`, `firstWindow`, `event`; written once, and again for an earlier window) and
`Status` never returns `Dormant` again (reason "… (guard kept: Cosmetic crates were served at …)"); `Active` is
unaffected. An unreadable or broken file counts as set. Deleting the file with the server stopped makes the server
dormant again (a tester's reset). `GET _mcvs/crm` (`collector.cratesServed`) and `GET _mcvs/liveops` show it.
* `CratesInWindow` ⇔ `enabled` ∧ content valid ∧ `StorePatch != false` ∧ `since` known ∧ `windowStart ≥ since`.
  A crate can only be claimed after progress, i.e. once its window has started (`now ≥ windowStart ≥ since`; the next
  weekly event is announced, "coming soon", during the last day before it, but cannot progress yet), and the TS
  refetches CRM at the window start (the schedule step is the CRM expiry) and on every return to the menu. So under the
  same inputs, whenever a crate-carrying milestone can be claimed, the document the TS holds is `Active` (whatever the
  season grid or daily reset hour); input changes are what the guard covers.

Where the guard is needed even though events and document flip together: the TS applies CRM and fetches events at
different moments; an ended weekly event with unclaimed rewards stays in the profile until claimed
(`MustKeepEventAliveAfterEnd`, LIVEOPS.md §7) and a daily one blocks the next until claimed, i.e. a completed
Cosmetic-crate milestone can be claimed weeks after a switch-off; the minimal-document fallback; a collector build or
validation failure. In all of them the crate pays 70 VersusCoins instead of throwing in `OnClaimed` on the TS.

The CRM cache key gains the phase (computed per request from the clock and the latch), so a `Dormant` → `Guard` change
without live ops (no CRM expiry) is picked up at the next fetch.

### 2.13 Client requirement: the `store` patch (D11)

Vanilla `KillSignatureRewardItem.GetAvailableId` throws `NullReferenceException` for a sound (list null) and when
`Subtype2` is null — which `OnPrepareToClaim` sets for a random avatar/background row that found no candidate. Both are
reached by `RewardItem.GetRewardInfo` → `IsAvailable` on the reveal, the claim popups and the inbox: for every bundled
sound and every "nothing left" draw of rows 1–6. The patcher's default `store` patch (S1, `patcher/Patches/StorePatch.cs`,
fixed in build 4 for the same reveal path) makes both return null. The patch can be opted out of (`--patches`), so:

* The embedded host reports `ClientCapabilities.StorePatch = true/false` from the loaded `Game` assembly (the patch adds
  `Game.McvsStoreSafety`); the standalone host reports `null` (unknown, assumed present, logged once).
* `StorePatch == false` → the collector is never `Active` (phase `Guard` with reason "store patch missing"), shown in
  `GET _mcvs/crm`.
* WP-F runs its per-row checks against the patched `Game.dll`, as `RealStorePatchTests` does.

## 3. Live-ops content

### 3.1 Rewards (appended to today's lists, one crate per milestone)

| Event | Today | Added (windows from `collector.since`, `CratesInWindow`) | Crates when fully done |
|---|---|---|---:|
| Daily missions, the day's **first mission** (round 3) | 150–200 VC, 10 Credits | `Crate_Event_Special` ×1 | 1/day |
| Daily missions, "complete all" bonus | `Crate_Contracts` ×1 | – (round 2: ×1) | – |
| Weekly event, milestone tier 1 (×4 objectives) | 300 VC, 20 Credits | `Crate_Event_Special` ×1 | 4 |
| tier 2 | 600 VC, 50 tokens | `Crate_Event_Special` ×1 | 4 |
| tier 3 | 1,000 VC, 100 tokens | `Crate_Event_Special` ×**2** (round 3: the final's two) | 8 |
| final | 20 HC, `Crate_Event_<n>` | – (round 2: ×2) | – |
| Season end, ranks 4–30 | one gear piece | Cosmetic crate with `band × itemsPerCrate` items (§2.4) | 1–4 crates' worth |

**One crate per milestone (D12, round 3).** `ClaimAchievementMilestoneRequest` (`GM/TimeLimitedAchievementsComponent.cs`,
pinned by WP-F test 9) claims a milestone's rewards in template order: a crate sets the claim's list to its
`RewardsGenerated` (`result.Rewards = crateRewardItem.RewardsGenerated`, a replace), any other reward is added to the
current list; `ClaimAchievementMilestone` then opens the crate lottery with that list whenever a crate was claimed. So a
milestone holding two crates reveals only the last one (the other's items are granted unseen), which ruled out round 2's
daily bonus (`Crate_Contracts` + Cosmetic) and weekly final (event crate + Cosmetic ×2). The collector block now puts
its crate only where today's milestone holds none; `LiveOpsCatalog.ParseCollector` and content-gen refuse anything else
(and the crate twice in one list). The Contracts and event crates keep their own reveal, exactly as today; the weekly
total for full completion goes from 14 to 16 crates (§4.3). "First" is the server's pick order: the phone lists the
"complete all" bonus first, then claimable, in-progress and claimed missions (`GetMilestonesObject`), so the crate's
mission is the one whose card shows the CUSTOMIZATION CRATE (usually right under the Contracts bonus row until another
mission completes). A milestone with a crate still **credits its currencies
without showing them** (they are claimed before the crate replaces the list; the milestone card lists them beforehand):
the daily mission's VC/Credits, the tiers' VC/Credits/tokens (round 2's tiers too), as today's weekly final already
does with its 20 HardCurrency. Device checks 2 and 3.

Side effect: the weekly "crates opened" objective (`CratesOpened`, goals 2/5/10; drawn in about half the weeks, 3 of
the 6 non-theme objectives) counts **sub-crates**: `OpenCrateOnServer` reports `max(CrateGenerationRound) + 1` and
`BaseAchievementsComponent.OnCrateClaimed` adds it, so one Cosmetic crate adds `itemsPerCrate` (a season crate band ×
N). In those weeks a few dailies complete all three tiers, and each tier's crate feeds the objective again (harmless,
bounded by the 3 tiers). Kept: it is one objective's normal reward; the pace (§4.3) counts it (+0.75 crates/week for a
casual player; regular and dedicated players complete every objective anyway).

### 3.2 `liveops.json` `collector` block and the server

`tools/content-gen/liveops_tuning.json` gains (validated like the other rewards):

```json
"collector": {
  "since": "2026-11-09T00:00:00Z",
  "dailyBonus": [],
  "dailyMission": [{"item": "Crate_Event_Special", "quantity": 1}],
  "weeklyMilestones": [[{"item": "Crate_Event_Special", "quantity": 1}],
                       [{"item": "Crate_Event_Special", "quantity": 1}],
                       [{"item": "Crate_Event_Special", "quantity": 2}]],
  "weeklyFinal": []
}
```

`liveops.json` carries it unchanged (`since` normalised to `"yyyy-MM-ddTHH:mm:ssZ"`). Validation: `since` is a Monday
00:00 UTC on the default season grid (whole weeks since 2024-01-01); `weeklyMilestones` has one list per tier; every
item is a currency or a catalogue crate; one crate per milestone (round 3: the crate at most once per list, and never on
a list whose milestone already holds a crate: the daily bonus, any daily mission, a weekly tier, the weekly final with
its event crate); the `revision` is **not** changed. content-gen **warns** (does not fail) when
`since` is earlier than generation time + 7 days ("set collector.since to the first Monday at least 7 days after the
build is published"); the release checklist repeats it (WP-G).

Server (`server/Services/Social/LiveOps/`):

* `LiveOpsCatalog.Collector` (`LiveOpsCollectorRewards { DateTime Since; List<LiveOpsReward> DailyBonus, DailyMission;
  List<List<LiveOpsReward>> WeeklyMilestones; List<LiveOpsReward> WeeklyFinal }`, null when absent or broken; broken =
  logged, live ops otherwise unaffected; `LiveOpsCatalog.CollectorKeys` the five required keys).
  `LiveOpsSnapshot.CollectorSince` passes `Since` to the CRM builder.
* `LiveOpsSettings` no longer parses `economy.collector.*`: its `Fingerprint` includes
  `CollectorRuntime.InputsKey` (enabled, content validity, store patch), and `LiveOpsService.Settings` re-parses when
  the `liveops` subtree or that key changes.
* `LiveOpsEvents.DailyQuest(catalog, day, runtime)` / `WeeklyEvent(catalog, season, modifier, runtime)`: when
  `catalog.Collector != null && runtime.CratesInWindow(window.Start, catalog.Collector.Since)`, append `DailyMission` to
  the first picked mission's rewards, `DailyBonus` to the bonus list, `WeeklyMilestones[i]` to tier *i*, `WeeklyFinal`
  after the event crate. Otherwise the template, and so `_version`, is byte-identical to today's.
* `LiveOpsService.FindEvent` sets the collector's latch when the event it serves carries the crate (§2.12).
* `GET /_mcvs/liveops` shows `collector: {phase, reason, since, activeFor: [daily id, weekly id], block, cratesServed}`.

### 3.3 Release on a Monday boundary: `since`, not `revision`

`_version = LiveOpsHash.Revision("mcvs-offline/event/v1|" + revision + "|" + start + "|" + end + "|" + template)`. The TS
keeps a stored event with the same `_version` and replaces it (losing progress and unclaimed milestones) when it
changes (LIVEOPS.md §9).

* Bumping `revision` changes the `_version` of every event, including the day and week running at install: everyone
  loses that day's and week's progress once. **Do not bump.**
* With `since`, windows starting before it keep a byte-identical template and `_version`; windows from `since` on are
  new events, and the CRM layer starts at the same moment.
* Residual: a player who installs after `since`, mid-week, has that week's event and that day's missions replaced once.
  Toggling `economy.collector.enabled`, breaking `collector.json` or a missing `store` patch does the same for the
  running and the kept-alive windows from `since` on. Unavoidable without player-dependent event content.

### 3.4 `collector.json` (new content file, CRM-owned)

Generated by `tools/content-gen` from `collector_tuning.json` + the catalogue (`build_collector`), read through
`CollectorRuntime` (`"collector.json"`, overridable in `<data>/content/`). Ids only, no game text.

```json
{
  "format": "<FORMAT>",
  "crate": {"id": "Crate_Event_Special", "table": "Crate_Event_Special", "locId": "crm.CrateCustomization",
            "image": "Menus/assets/png/Crates/Crates/crate_Legendary_Open.png"},
  "rows": [
    {"id": 1, "category": "exhausted", "type": "VersusCoins", "subtype": "Tiers4"},
    {"id": 2, "category": "avatars", "type": "KillSignature", "subtype": "Avatar", "subtype2": "Random"},
    {"id": 4, "category": "sounds", "type": "KillSignature", "subtype": "Background", "subtype2": "Random",
     "bundle": "KillSignature_Sound_2", "item": 2},
    {"id": 8, "category": "skins", "type": "MercSkin", "subtype": "2", "subtype2": "5", "merc": 2, "item": 5},
    {"id": 24, "category": "hiddenJerseys", "type": "MercAccessory", "subtype": "Random", "subtype2": "112", "item": 112},
    {"id": 36, "category": "agents", "type": "UnlockMerc", "subtype": "17", "merc": 17},
    {"id": 38, "category": "camos", "type": "WeaponCamo", "subtype": "Random", "subtype2": "Random"},
    {"id": 39, "category": "jerseys", "type": "MercAccessory", "subtype": "Random", "subtype2": "Random"}
  ],
  "avatars": {"base": "61-76,78,…", "drawable": "1-6,9-224,590-597", "unlock": "225-253,257,…,532",
              "lock": "598-672", "kayac": "382-384,443-531,533-589,598-1514", "reserved": "254-256,…,298-300"},
  "backgrounds": {"base": "18-35", "drawable": "1-6,24-35", "unlock": "18-23"},
  "sounds": [2, 3, 4, 5],
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
  "summary": {"baseItems": 596, "baseTier": 665, "paidPairs": 9365, "vipOnlyPairs": 5624, "kayacAvatars": 1064}
}
```

(Ellipses stand for the full lists of §1, written with `compress_ranges`.) The full row list is the 39 rows of §2.3.

`collector_tuning.json` holds the rule inputs: `kayac` (`folders`, `nameContains: "kayac"`, `extra: [382, 383]`),
`reservePlacementEmblems`, `sounds`, `soundCarrier` (`Background`; `Avatar` allowed), `skins`, `hiddenJerseys`, `agents`,
the row order (`rows`, exhausted first).

`content_gen.py` validations (`ContentError` otherwise): crate, tables and the 35 tier crates exist; `rows` ≤ 100;
row types are cases of `GenerateRewardItem`; **§2.3.1 preview safety** (no `Sound` row; bundles only
`KillSignature_Sound_<n>` on `KillSignature/Avatar|Background/Random` rows of the sounds category, each its own sound);
skins exist with `Locked=false`; hidden jerseys are
`VisibleInShop=false` and not `IsKayac`; agents are displayed; every avatar/background id exists and is released;
`kayac ∩ base = ∅`, `reserved ∩ base = ∅`, `1513-1518 ∩ (base ∪ unlock ∪ lock) = ∅`; `unlock` ⊆ base with
`Unlockable=false`; `lock` = Kayac ∩ `Unlockable=true` ∩ released; per-agent `camos`/`jerseys` equal the `unlocks.json`
groups (minus hidden jerseys); placement row 1 is the only `Weight.1 > 0` row and is `HardCurrency`; the loc id and
image exist; the 48 season emblems resolve by asset name. `manifest.json` lists `collector.json`.

## 4. Weighting, pace and settings

### 4.1 Model

Per document, units per category (per player from §2.9, or the static assumptions of §4.2):

| Unit | Definition |
|---|---|
| `uA` | avatars the player does not own in (`avatars.drawable` ∪ the window ids sent as `true`) |
| `uB` | backgrounds not owned in (`backgrounds.drawable` ∪ `backgrounds.unlock`) |
| `uS(row)` | 1 if that carrier's sound is not owned |
| `uK(row)` | 1 if that skin is not owned and its agent is unlocked |
| `uH(row)` | number of unlocked agents lacking that hidden jersey |
| `uM(row)` | `weights.agents` (default 2) if that agent is locked |
| `uC` | Σ over unlocked agents of camos in its pool not owned |
| `uJ` | Σ over unlocked agents of visible non-Kayac jerseys not owned |

Each unit is multiplied by its category weight (`weights.avatars` … `weights.jerseys`, default 1). Then

```
base = uA + uB + ΣuS + ΣuK + ΣuH + ΣuM        paid = uC + uJ
share_base = baseShare (0.75) if base > 0 and paid > 0; 1 if paid = 0; 0 if base = 0
Weight#1(row) = round(1,000,000 × share_tier × u(row) / tier_total), at least 1 when u(row) > 0, else 0
Weight#1(1 exhausted) = 1 if base = paid = 0, else 0
```

Inside a tier every remaining item is equally likely, so a tier's categories finish together. Carrier draws also
deliver a background (round 3; round 2: an avatar; counted in the simulation). The only waste is a row drawn after it ran out within one session
(weights refresh at each return to the menu): simulated 0.04–0.45 % of draws.

### 4.2 Static weights (assumption: all 21 agents unlocked, nothing owned but the starting items)

base = 368 + 23 + 4 + 16 + 12 × 21 + 2 × 0 = **663** units (agents 0: the assumption says they are owned); paid =
4,409 + 4,956 = 9,365.

| Row | Weight#1 | Row | Weight#1 |
|---|---:|---|---:|
| 1 exhausted | 0 | 24–35 each hidden jersey | 23,756 |
| 2 avatars | 416,290 | 36, 37 each agent | 0 |
| 3 backgrounds | 26,018 | 38 camos | 117,699 |
| 4–7 each sound carrier | 1,131 | 39 jerseys | 132,301 |
| 8–23 each skin | 1,131 | | |

(Sum exactly 1,000,000; bench readback matches.) Example per-player document, fresh profile with the 19 obtainable
agents and Witch/Bli-P locked: base = 368 + 23 + 4 + 13 (Witch's 3 skins out) + 12 × 19 + 2 × 2 = 640.

### 4.3 Pace

Crates per week (daily missions need ~5–6 matches; milestones per §3.1 with round 3's placement; divisions per
LIVEOPS.md §5):

| Player | Play | Dailies (first mission) | Weekly milestones (tier 3 ×2) | Season (ranks 4–30) | Crates/week |
|---|---|---:|---:|---:|---:|
| Casual | ~25 matches/week on 3 days | 3 | 6 + 1.0 (`CratesOpened` weeks) | 0 (rank 31+) | **10** |
| Regular | ~10 matches/day, 6 days | 6 | 16 | 3 (settles in Diamond, band 3) | **25** |
| Dedicated | every day, Master | 7 | 16 | 4 (Master ranks 4–10) | **27** |

(Round 2: 9.75 / 23 / 25 with the final's two crates; moving them to each tier 3 adds 2 per fully completed week.)

Simulation (`rev3/pace3.py`, `rev/pace2.py` with the round-3 placement and background carriers; mean of 5 seeds):
reference profile = the 19 obtainable agents unlocked, Witch and Bli-P locked (their skins, hidden jerseys, camos and
jerseys join when won), no cosmetics but the starting ones; claims in bursts (dailies on play days, milestones spread
over the week, season crate on Monday); weights refreshed once per play day (the conservative case); carriers deliver
background + sound.

| itemsPerCrate N | Casual base / everything | Regular base / everything | Dedicated base / everything |
|---:|---|---|---|
| 1 | 88 w / 1,003 w (19.2 y) | 36 w / 402 w (7.7 y) | 33 w / 372 w (7.1 y) |
| 2 | 44 w / 502 w (9.6 y) | 18 w / 201 w (3.9 y) | 17 w / 186 w (3.6 y) |
| 3 | 30 w (6.9 mo) / 335 w (6.4 y) | 12 w (2.8 mo) / 134 w (2.6 y) | 11 w (2.6 mo) / 124 w (2.4 y) |
| 4 | 23 w (5.2 mo) / 251 w (4.8 y) | 9 w (2.1 mo) / 101 w (1.9 y) | 9 w (2.0 mo) / 93 w (1.8 y) |
| **5 (default)** | **18 w (4.1 mo) / 201 w (3.9 y)** | **7.8 w (1.8 mo) / 81 w (1.6 y)** | **7 w (1.6 mo) / 75 w (1.4 y)** |
| 10 | 9 w (2.1 mo) / 101 w (1.9 y) | 4 w (0.9 mo) / 41 w (9.4 mo) | 4 w (0.9 mo) / 38 w (8.7 mo) |

* **Base set at 5 (default)**: under 2 months regular/dedicated, ~4 months casual; everything ~1.6 years regular (round
  2: 8.0 / 88 weeks regular, 7.5 / 81 dedicated, 18.7 / 206 casual).
* **Everything in ~6 months (26 weeks) from crates alone** needs N ≈ **15.4 (regular), 14.3 (dedicated), 38.6
  (casual)**, all outside 1–10; at the cap of 10: 9.4 months regular, 8.7 months dedicated, 1.9 years casual. The VC
  shop's camo/jersey 10-packs remain the main path to the paid tier (a regular player earning 17–21k VC a week clears
  it in ~22 weeks spending everything there). If the owner wants crates alone to reach 6 months, raise the range to 20
  (`maxSeasonItems` then 80) or add crates to the weekly event; reveals of > 10 items are plain lists (§4.4).
* `baseShare` 0.5 / 0.9 moves the regular base set to 18 / 10 weeks at N = 3 (12 at 0.75); "everything" is unchanged.
* Agents (2 units each): Witch and Bli-P arrive in weeks ~3–5 for a regular player at N = 5 (4 units: earlier).
  After an agent is won its row keeps weight until the next profile read: with 2 units a second hit on that agent in
  the rest of a 60-draw batch (the 36-draw batch of N = 3 scaled) has ~12 % probability (4 units: ~24 %) and pays 40
  random gear pieces (documented windfall).

### 4.4 Settings (`settings.json`, `"economy": {"collector": {…}}`)

| Key | Default | Range | Effect | Applies |
|---|---|---|---|---|
| `enabled` | `true` | bool | `false`: phase `Guard` from `since` (5 cells), events as before | next CRM fetch; running and kept-alive windows from `since` replaced once (§3.3) |
| `itemsPerCrate` | 5 | 1–10 | `CratesCount` of the Cosmetic crate; tier crates `band × N` | next CRM fetch, also for crates already in the inbox |
| `baseShare` | 0.75 | 0–1 | share of draws for the no-source tier while both tiers have items | next per-player document |
| `weights` | `{"avatars":1,"backgrounds":1,"sounds":1,"skins":1,"hiddenJerseys":1,"agents":2,"camos":1,"jerseys":1}` | each 0–100 (0 removes the category; `agents` = units per agent) | §4.1 | same |
| `seasonCrates` | `[1,1,2,2,3,3,4]` | 7 ints 0–10 (Starter…Master; 0 = today's tier crate) | §2.4 | next season evaluation |
| `maxSeasonItems` | 40 | 10–100 | cap of one season claim (§2.4) | same |
| `seasonEmblems` | `true` | bool | §2.5 | same |
| `unlockableWindow` | 160 | 0–400 | §2.6 | next per-player document |
| `levelUpAvatars` | 0 | 0–3 | §2.7 | next CRM fetch |
| `exhaustedReward` | `"VersusCoins_Tiers4"` | `VersusCoins_Tiers0`…`6`, `UpgradeTokens_Tiers0`…`11` | row 1 | next CRM fetch |

Parsed only by `CollectorOptions.FromSettings` (WP-C); out-of-range values are clamped and logged
(`settings: economy.collector.<key> …`). The kill switch fails toward today's behaviour (O7): `"collector": false` (a
bool for the object) and the spellings `"off"`, `0`, `"no"` read as off; an `enabled` or `collector` value that cannot be
read, and a settings.json the server cannot parse at all (`Settings.IsFallback`: it runs on the defaults, which say on),
count as **off**, logged as an error. The settings page (Economy: "Cosmetic crate", "Items per Cosmetic crate") shows
both keys as `FromSettings` reads them (`SettingsSchema.Read`), with a note (`invalidNote`) whenever settings.json holds
something else (`"maybe"`, `"8"`, 15); saving either key first turns a non-object `collector` into
`{"enabled": <what the collector read>}`, so an item-count edit never drops the kill switch. The switch asks before
saving (the running daily missions and weekly event restart, §5.3). `Settings.CreateDefaults` writes `economy.collector.enabled: true` so the
opt-out is discoverable in a new settings.json (WP-D); the other keys stay implicit. The settings snapshot is part of
the CRM cache key. The lottery animates a claim only when its item count is ≤ `MaxRewardsInAnimation` (MCVS_Meta row 1:
**10**; `PlayerProfileComponent.NotifyCrateReceived` counts bonus items too): at N = 5 the daily mission's crate (5–6
items with a carrier), a weekly tier 1/2 crate (5–6), a band 1–2 season crate (5 / 10) and a weekly tier 3 without a
carrier (×2: 10) animate; a tier 3 with a carrier (11–12), a season crate of band ≥ 3 (15+) or any claim over 10 shows
the plain reward list (shipped behaviour for large claims). The table is not changed.

## 5. Edge cases

### 5.1 Unlock-all and complete profiles

Every unit is 0 → only row 1 has weight: each item pays 70 VC (350 VC for the daily crate at N = 5). The preview lists
the VersusCoins row; `CrateRewardItem.IsAvailable` (any row available, weight not considered) stays true, at row 1. Per-player
cells: 39 weights + 21 emblem cells + 0 Unlockable cells. Note: `UNLOCKALL` does not include the VIP-only pools
(`UNLOCKPAID` does), so an unlock-all profile still has the paid tier.

### 5.2 A category runs out

Its rows get weight 0 in the next per-player document. Within the current session a draw on it pays its alternate
reward (§1.6). Base empty → the paid tier gets 100 %, and vice versa; both empty → §5.1.

### 5.3 Collector disabled (or enabled) mid-season

At the next CRM fetch the layer is replaced by the guard (phase `Guard`) and the TS reloads its databases (as on every
forced refresh). Events for windows from `since` regenerate without crates (`_version` changes): the running day/week
and any kept-alive ended event are replaced once (progress and unclaimed milestones of those windows are lost, §3.3).
A Cosmetic crate still claimable in between (a milestone of an event the TS has not replaced yet) pays 70 VersusCoins
per crate; the latch (§2.12) keeps the guard for it however long that takes. TLE rewards are claimed directly (nothing sets `SendToInbox`), so no Cosmetic crate sits in the inbox; the
inbox only holds retargeted **tier** crates, which revert to their own content table (today's gear piece). Season
rewards evaluated later use the original tier crates (ranks 1–3: no emblem). Re-enabling restores everything at the
next fetch (events replaced once again).

### 5.4 Existing saves

Nothing is migrated; the server only reads the dump. Items bought with VC are never duplicated by random rows and get
weight 0 on explicit rows. Players who updated after `since` lose that week's event progress once (§3.3).

### 5.5 CRM cost on the phone

§2.10: +12.9 KB / +9.9 KB per-player text, ≤ 14 ms warm net8 per apply (TS + client per return to the menu), mirror
4–8 ms (GS + client per match). Estimated +0.2–0.3 s per return to the menu; device check 10. Safety valves and the
documented fallback: §2.10.

### 5.6 `crm.omitOwnedSignatures`

Independent switch, shared scan. With it off and the collector `Active`, the per-player document has the whole store and
the collector cells. Avatars won from crates leave the store at the next profile read. Kayac avatars stay in the store.

### 5.7 Inventory unreadable

Each part falls back separately (§2.9.1), logged once per read; the document stays valid. `GET
/_mcvs/collector?account=<id>` shows what was read, per part, with the problem texts.

### 5.8 Live ops off, content missing, store patch missing

Phase `Guard` (from `guardSince`) or `Dormant` (before): live ops never hand out the crate without `CratesInWindow`, and
the document carries the guard for stale crates. A missing/broken `collector.json` makes `CratesInWindow` false **and**
the phase `Guard`, logged as an error and shown in `GET _mcvs/crm`; `GeneratedContentTests` keeps the shipped files
consistent. Before `guardSince` (a day before `since`, §2.12) everything is byte-identical to today (golden); events
stay byte-identical for every window that starts before `since`.

### 5.9 LAN

One TS serves several players: per-player documents are per account; the GS uses the static mirror.

## 6. Work packages

File ownership is disjoint; a package edits only its files. WP-0 runs first, on `e4bff79`, before anything else lands.
WP-I integrates.

| WP | Files (owned) | Depends on |
|---|---|---|
| **0. Golden capture** | `tests/McvsOffline.Tests/Golden/GoldenScenarios.cs`, `GoldenCaptureTests.cs`, `golden-e4bff79.json` (all new) | – |
| **A. Content** | `tools/content-gen/content_gen.py` (`build_collector`, `_find_nosource`, liveops `collector` block), `tools/content-gen/collector_tuning.json` (new), `tools/content-gen/liveops_tuning.json`, `tools/content-gen/test_content_gen.py`, `tools/content-gen/README.md`, `server/Content/collector.json` (new, generated), `server/Content/liveops.json`, `server/Content/manifest.json`, `server/Content/README.md`, `tests/McvsOffline.Tests/Content/GeneratedContentTests.cs` | – |
| **B. Inventory** | `server/Services/Crm/PlayerInventory.cs` (new), `tests/McvsOffline.Tests/Content/PlayerInventoryTests.cs` (new; holds the `internal const string WriterMercsFixture`, the expected Templates-writer shape of a `Mercs` list) | – |
| **C. Collector plan + runtime** | `server/Services/Crm/Collector/CollectorContent.cs`, `CollectorOptions.cs`, `CollectorPlan.cs`, `CollectorWeights.cs`, `CollectorRuntime.cs`, `CollectorGuard.cs` (all new), `tests/McvsOffline.Tests/Content/CollectorPlanTests.cs`, `CollectorRuntimeTests.cs` (new) | A (schema; fixtures until generated), B (type), H (type) |
| **D. CRM integration** | `server/Services/Crm/CrmDocumentBuilder.cs`, `CrmDocumentCache.cs`, `CrmOwnedStoreItems.cs`, `CrmService.cs`, `CrmOptions.cs`, `CrmContent.cs`, `CrmDocumentValidator.cs`, `server/Core/Settings.cs` (`CreateDefaults` only), `tests/McvsOffline.Tests/SettingsTests.cs`, `tests/McvsOffline.Tests/Content/CrmDocumentBuilderTests.cs`, `CrmOwnedStoreItemsTests.cs`, `CrmServiceTests.cs`, `CollectorDocumentTests.cs` (new), `tests/McvsOffline.Tests/Golden/GoldenScenarios.cs` (round 3: `GoldenRun` restarts on a data dir) | 0, B, C |
| **E. Live ops** | `server/Services/Social/LiveOps/LiveOpsCatalog.cs`, `LiveOpsEvents.cs`, `LiveOpsService.cs` (incl. `LiveOpsSnapshot`), `LiveOpsSettings.cs`, `tests/McvsOffline.Tests/Misc/LiveOpsTests.cs`, `Misc/LiveOpsConformanceTests.cs` | 0, A (schema), C |
| **F. Real-client conformance and timing** | `tests/McvsOffline.Tests/Conformance/CollectorConformanceTests.cs` (new), `tests/McvsOffline.Tests/Conformance/RealTables.cs` (new helper) | A, C, D, E |
| **H. Client capability** | `server/Core/ClientCapabilities.cs` (new), `server/Core/McvsServer.cs` (`ServerOptions.ClientCapabilities` → `context.Features`), `hosts/embedded/GamePatchProbe.cs` (new), `hosts/embedded/EmbeddedHost.cs` (one call), `tests/McvsOffline.Embed.Tests/Embedded/GamePatchProbeTests.cs` (new) | – |
| **G. Docs** | `docs/ECONOMY.md`, `docs/LIVEOPS.md`, `docs/protocol/content.md`, `docs/STATUS.md` (device checklist), `docs/ROADMAP.md` (release checklist: `since`), `docs/design/economy-collector.md` (status) | all |
| **I. Integrator** | merge, `scripts/build.sh` run (net8 + embedded), full test run with `MCVS_REFS`, `MCVS_DATAPACK(_DECODED)`, `MCVS_REQUIRE_REAL_CLIENT=1`, APK build | all |

### WP-0 Golden capture
Runs on `e4bff79` (no other change). `GoldenScenarios` defines: a fake clock over 16 days 2026-11-02 … 2026-11-17
(crossing the Monday season boundary 2026-11-09, which the collector tests later use as `since` via a content
override); live ops on and off; settings default; dumps fresh (`DefaultProfile.tx` shape), unlock-all and malformed
(the existing `CrmOwnedStoreItemsTests` fixtures). Outputs per scenario: the full static document, the `game` selector,
the per-player documents (3 dumps × 3 days), the daily and weekly events (text including `_version`) of each day.
`GoldenCaptureTests.Write` (runs only with `MCVS_WRITE_GOLDEN=1`) stores `{scenario: {sha256, length}}` in
`golden-e4bff79.json`; `GoldenCaptureTests.FileIsComplete` checks every scenario has an entry. Regenerating the file
later is a deliberate, reviewed act.

### WP-A Content
`_find_nosource` (§1.1), the Kayac rule (§1.2), the sets of §1.3–1.5, `build_collector` → `collector.json` (§3.4), the
liveops `collector` block (§3.2) with its validations and the `since` warning. Tests (`test_content_gen.py`): exact
counts (304, 139, 75, 165 + 65, 18/6, 4, 16, 12, 2, 4,409, 4,956, 1,066 / 1,064 Kayac, 36 reserved, 596, 665); 382, 383
and 384 in `kayac`, not in `base`, not in `unlock`; the per-agent pool equalities; every `ContentError` path (unknown
crate, row > 100, Kayac in base, a `Sound` row, a bundle on a non-carrier row, `since` not a Monday, wrong tier count,
bad loc id, missing image, placement row 1 not the only `Weight.1` row; round 3: a bad `soundCarrier`, a crate beside
another crate or twice in one list, a missing `dailyMission`); the `since` warning; deterministic output; round 3: the
shipped row order (exhausted first), background carriers and `crm.CrateCustomization` on the decoded pack.
`GeneratedContentTests` (MiniJson level): `collector.json` has the §3.4 keys and 39 rows, its crate id is the one
`liveops.json`'s block hands out, its loc id is `CollectorGuard.LocId`, `manifest.json` lists it,
`CollectorGuard.ShippedSince` equals the shipped `since`, the block's keys are `LiveOpsCatalog.CollectorKeys`.

### WP-B Inventory
`public sealed class PlayerInventory { bool AvatarsKnown, BackgroundsKnown, SoundsKnown, MercsKnown; HashSet<int>
Avatars, Backgrounds, Sounds; Dictionary<int, MercState> Mercs; IDictionary<string, string> Problems; static
PlayerInventory Scan(string dump, bool parseMercs); IEnumerable<string> OwnedStoreNames(); }`, `MercState { bool
Unlocked; HashSet<int> Skins, Camos, Accessories; }`. `OwnedStoreNames` depends on avatars/backgrounds only and keeps the
exact refusal rules of today's `CrmOwnedStoreItems.Scan`. Tests: today's scan fixtures give the same names, also when
the sounds or `Mercs` parts are malformed (`StoreNamesIgnoreOtherParts`); sounds; the `Mercs` path with defaults,
string and bool `Locked`, numeric `Id`; a `Mercs` key elsewhere (`Squads[].Mercs`) not taken as the agent list; the
squad plausibility guard (agent 1 present and locked → `MercsKnown=false`); `parseMercs=false` leaves agents unknown
without touching the span; malformed agent list → `MercsKnown=false` with reason; **`WriterMercsFixture`** (written
from the `Merc.ShortDef` property list: every public property, `Locked` as a JSON bool; WP-F test 7 checks it against
the real writer) reads as one unlocked agent with its skins/camos/jerseys and one locked default agent;
`DefaultProfile.tx` scans as "every agent locked but agent 1"; a 600 KB synthetic dump scans in < 50 ms on net8.

### WP-C Collector plan + runtime
`CollectorContent.Parse(text, log)` (null + problem on a broken file or a §2.3.1 violation);
`CollectorOptions.FromSettings(Settings, Log)` (§4.4, clamping); `CollectorRuntime` (§2.12);
`CollectorPlan.Static(content, options, snapshot)` → `CollectorLayer`; `CollectorPlan.PlayerCells(content, options,
inventory, snapshot)` → `IList<FieldPatch>`; `CollectorWeights.Compute(...)` (§4.1); `CollectorGuard` (cells,
`ShippedSince`). Tests: the static weights of §4.2 exactly; `LayersStayWithinBudget` (≤ 600 cells per tree, incl.
`levelUpAvatars` 3); mirror = game layer minus the `true` cells; never `""`/null/fractions; the design cells
(`StaticLayerMatchesTheDesignCells`, round 3: `cells3.py`); numeral mapping
(2026-10-26 → 264/263/262, 2026-11-02 → 268/267/266, `seasonDays` 1 and 14); per-player emblem choice (owned season
numeral → next unowned of the same star count; all owned → season numeral); band × N cells and the `maxSeasonItems`
cap (multiplier 1, 2.5, 100); window sizes (160, 50, 0); unlock-all inventory → only row 1 (exhausted) = 1 and 0 Unlockable cells;
base empty → paid 100 %; paid empty → base 100 %; owned skins/sounds/agents → 0; locked agent → its skins 0 and its
pairs not counted; each unknown part → static values for that part only; weights deterministic; every row with weight
passes §2.3.1; the generated `server/Content/collector.json` parses and yields the 39 rows of §2.3. `CollectorRuntime`:
the phase table of §2.12 for every combination of enabled × live ops × content valid/missing/broken × store patch
true/false/null × before/after `since` × before/after `guardSince`, and `CratesInWindow` true ⇒ `Status` `Active` at that
window's start (`EveryCrateWindowHasRows`); round 3: `guardSince = min(since, ShippedSince) − 1 day`
(`GuardSinceIsADayBeforeTheEarlierOfSinceAndTheShippedSince`) and the latch (`TheLatchKeepsTheGuardForever`: never
`Dormant` once crates were served, read back by the next server, a broken file counts as set).

### WP-D CRM integration
`SetCell` (§2.8); `Build` takes `CollectorRuntime` and writes the layer (`Active`), the guard (`Guard`) or nothing
(`Dormant`) inside the isolation `try/catch`; `CrmDocument.Collector` (status, layer) and `PlayerMirrorTables`;
`JsonFor(omit, patches)` (§2.9.4); `CrmOwnedStoreItems` becomes the per-player document source (inventory per account,
store filter, collector patch, mirror swap, cache key §2.9.5); `CrmService` per-player condition and
`MinimalDocument` with the guard; `CrmOwnedStoreItems.Scan` removed in favour of `PlayerInventory.Scan` (its fixture
tests move to WP-B, the HTTP-level tests stay here); validator `CheckCollector` (§2.8); `GET _mcvs/crm` gains
`collector {phase, reason, cells, itemsPerCrate, staticWeights}`; `GET _mcvs/collector?account=<id>`;
`Settings.CreateDefaults` writes `economy.collector.enabled: true`. Tests: **`CollectorOffMatchesGolden`** (with
`enabled=false`: every WP-0 document before `since` byte-identical; from `since` identical after removing exactly the 5
guard cells from each tree, and those cells present; round 3: from `guardSince`, a day before `since`);
**`DormantMatchesGolden`** (live ops off, before `guardSince`); `EveryServedCrateHasRows` (for every combination of
settings, content validity and store patch, and every day, if `liveops` hands out `Crate_Event_Special` in any event the
document lists in `groupware.events.participation` then the served document — static, per-player, minimal — has a
typed `Crate_Event_Special` row in `game._OverrideTables`); round 3: `TheGuardStaysOnceCratesWereServed` (a `since`
override, a crate served, the override removed and the clock set back: static, selector and per-player documents carry
the 5 guard cells, also after a restart), `StoreFilterBeforeTheFirstReadAfterARestart` (the first fetch after a
restart, before the read: the golden per-player text with the collector off); `CollectorFailureServesGuard` (a throwing plan, an overlay
that clears `Id@Type`); `MirrorPolicy` (static mirror = layer minus `true` cells; per-player mirror = guard);
`CollectorDoesNotTouchMulTargets`; `OverlayWinsOverCollector`; `PlayerDocumentChangesOnlyAfterProfileRead`;
`PlayerDocumentDropsOwnedUnlockableCells`; `StoreFilterUnchangedWithCollector` (store part byte-identical to the
golden per-player text); `MercsNotParsedWhenNotActive`; `ServerSessionGetsStaticDocument` (a `game:` super-credential token gets the static
text); unreadable dump → static layer; selectors stay static; `CreateDefaultsHasCollectorEnabled`.

### WP-E Live ops
§3.2. Tests: **`LiveOpsCollectorOffMatchesGolden`** (events with the collector off, content missing or store patch
false, and all events before `since`, byte-identical to WP-0's, `_version` included); from `since` the first
mission and the tiers carry the crates in the right order (round 3; the bonus and the final as today); toggling `enabled` changes `LiveOpsSnapshot.Key` and the
`_version` of windows ≥ `since` only; a broken `collector` block leaves live ops running; live ops read no
`economy.collector.*` key directly; round 3: a crate beside another crate, or twice in one list, is refused
(`BrokenCollectorBlocks`), and `NoGeneratedMilestoneHoldsTwoCrates` sweeps seven weeks of events (reset hours 0 and 6,
seasons of 7 and 1 days, every modifier). `LiveOpsConformanceTests` (real client): the 16-day run with a content override
whose `since` falls inside it; every event passes `StandardCrm.Data`, `SocialEvent`, `ParseTemplate`,
`TypeUtil.GetType` and `Initialize`; no milestone holds two `Game.CrateRewardItem`s; and
`RewardHelper.CreateRewardItem("Crate_Event_Special", n)` yields `Game.CrateRewardItem` with quantity n on the day's
first mission (1) and every weekly tier (1, 1, 2), and nowhere else. Make its
`NativeTrackingStub` `internal` for WP-F.

### WP-F Real-client conformance and timing
`RealTables` loads `Design/MCVS_Meta.xlsx` (`MCVS_DATAPACK` via the Core `DataPack`, or `MCVS_DATAPACK_DECODED`) into
real `Database.Table`s (the bench technique). Uses the **store-patched** `Game.dll` (as `RealStorePatchTests`). Tests
(skipped without `MCVS_REFS`, mandatory with `MCVS_REQUIRE_REAL_CLIENT=1`):
1. **CRMTableChanger** on every tree the server can emit (static game layer, worst-case per-player layer, static mirror,
   guard; taken from served documents): every changer parses, applies, and every target cell holds the intended value.
2. **CratesSpecsCollection fill**: before `RewardsCount 2`; after `RewardsCount 1`, `CratesCount N`, `LocId`; tier crates
   retargeted with the capped `band × N` (N = 1, 3, 10; multiplier 1 and 100); guard: `LocId`, `RewardChance 100/0`.
3. **CrateRewards fill**: 39 rows, `RewardId`s exactly as §2.3, carriers' `BundledRewards`/`AlwaysGiveBundledRewards`,
   `Weight.1` as served, rows 40–100 absent; guard: exactly one row `VersusCoins_Tiers4` weight 1; placement row 1
   bundle and `AlwaysGiveBundledRewards=true`.
4. **Reward items per row and bundle**: `RewardHelper.CreateRewardItem` for every row and for `KillSignature_Sound_2…5`
   gives the expected classes (`KillSignatureRewardItem` with `IsRandom` true for rows 1–6, `Subtype "Sound"` for the
   bundles; `MercSkinRewardItem`; `MercAccessoryRewardItem` with `IsRandomMerc`/`IsRandomAccessory`;
   `WeaponCamoRewardItem`; `UnlockMercRewardItem`; `VersusCoinsRewardItem` type route). **Preview safety**: for every row
   with `Weight.1 > 0` in every emitted tree, the reward is in the §2.3.1 set, and for the `KillSignature` rows
   `GetImage(null)` is non-null (a `Sound` row returns null and fails). Every image/loc id the display branches
   reference exists.
5. **Empty-table hazard pinned**: `GenerateRewards(specs, <empty list>, null, rng, source)` on an uninitialised
   `CratesInventoryComponent` throws `ArgumentOutOfRangeException` (the reason for D9); on the guard table the private
   `PickRandomWeightedItem` returns the VersusCoins row for round 0 and null for round 1.
6. **SignatureItemDataCollection fill**: game layer → 369 unlockable avatars, none of the 1,066 Kayac ids, 24
   backgrounds; mirror → 230 avatars, none Kayac.
7. **Agent element shape**: build a `Merc.ShortDef` with `Locked=false`, `Skins ["1","3"]`, two camos, one jersey, and a
   default one, serialise them inside a profile-shaped `Mercs` list with the game's `TemplateActivator` +
   `JsonTemplateWriter` (the `EndToEndConformanceTests` technique); the output must parse with `PlayerInventory` to the
   same states as WP-B's `WriterMercsFixture`, and match it key for key (whitespace-insensitive). A mismatch means the
   fixture, not the parser, is wrong: fix the fixture and re-run WP-B's tests.
8. **Timing**: `JObject.Parse` (as `StandardCrm.Data`) of the per-player documents (fresh, unlock-all) with and without
   the layer, and the apply of each tree; 1 cold + 5 warm; log per return to the menu (≥ 6 parses + 2 game-layer applies)
   and per match (2 mirror applies); assert warm apply < 60 ms per tree, cells ≤ 600, per-player text growth ≤ 15 %.
9. **Claim semantics** (round 3, `AMilestoneRevealsOnlyItsLastCrate`): Cecil on the real `Game.dll`: in
   `ClaimAchievementMilestoneRequest`'s state machine `RequestRewardsResult::set_Rewards` follows
   `CrateRewardItem::get_RewardsGenerated` (a replace) and other rewards go through `get_Rewards` + `List.Add`;
   `ClaimAchievementMilestone` reads `IsCrate` before `LotteryComponent.OpenSchematicLottery` (D12, §3.1).
10. **TLE screen cost** (round 3, `ATleScreenBuildsTheAvatarListAtMostOncePerCrate`): on the real `CrateRewards` fill of
   every served tree, no avatar row before the first non-signature row (`IsAvailable` builds no list) and at most one
   spawnable avatar row (the preview builds it once); the round-2 layout gives 5 and 5. Logged: the crate objects of the
   served weekly (12) and daily (1) events and the net8 cost of one avatar-list build over the real ids with 50, 300,
   700 and 1,400 owned, per weekly screen (device check 1 compares against it).
11. **The draw** (round 3, `TheServedTableDrawsByItsWeights`): 20,000 draws of the real `PickRandomWeightedItem` (round
   0) over the served static, fresh and unlock-all tables follow the weights (≥ 2 % rows within 1.5 points), never a
   weight-0 row, only row 1 for unlock-all; carriers bundle exactly their sound, always given. `GenerateRewards` over N
   sub-crates and the reveal need a live `PlayerProfileComponent` (carrier `IsAvailable`): device checks 2–4.

### WP-H Client capability
`public sealed class ClientCapabilities { public bool? StorePatch { get; set; } }` registered in `context.Features` by
`McvsServer.Create` from `ServerOptions.ClientCapabilities` (null → unknown). `GamePatchProbe.Detect()` (embedded):
finds the loaded assembly named `Game` and reports `StorePatch = (type "Game.McvsStoreSafety" exists)`; never throws
(exception → null, logged). Tests: probe on the real patched/unpatched `Game.dll` under `MCVS_REFS` (patched via the
patcher in memory, as `RealStorePatchTests`), and on no `Game` assembly (null).

### WP-G Docs
ECONOMY.md §1 (collector as the play path for the no-source set), §3 (settings table), §4 (live-ops income with crates;
corrected season rewards §0.3.2); LIVEOPS.md §1/§7/§10 (crates, `since`, do not bump `revision`, the guard); protocol
content.md (per-player collector cells, mirror policy, `_mcvs/collector`); STATUS.md device checklist (§7); ROADMAP.md
(release checklist: set `collector.since` to the first Monday ≥ 7 days after publishing, regenerate content).

## 7. Device checks

1. **Fresh profile**, collector on, right after login and again right after a match: open the daily missions and the
   weekly event screen. The "CUSTOMIZATION CRATE" reward (round 3: was "COSMETIC CRATE") shows its image, "CONTAINS 5
   ITEM(S)" and a preview (≤ 33 entries; one random-emblem entry for row 2, one random-background entry for rows 3–7;
   one agent entry; no VersusCoins entry until everything is owned). The weekly final's event crate keeps its own name
   ("COSMETICS CRATE" in English), different from ours in every language. A profile owning some no-source skins: they
   are absent from the preview (proves the per-player layer reached the client and the TS). **Time the weekly screen
   opening** with the collector off, on with a fresh profile, on with a profile owning ~1,400 avatars with some left, on
   with a complete profile (target ≤ +0.5 s; WP-F test 10 logs the net8 estimate: 12 avatar-list builds per screen,
   ~0.9–13 ms each for 50–1,400 owned on the build machine; else the §8 fallback). Also time the daily missions screen
   (1 Cosmetic crate object).
2. Claim the daily **first mission** (its currencies + the Cosmetic crate): the crate lottery reveals the crate's items
   (≤ 10, `MaxRewardsInAnimation`) and **not** the mission's VersusCoins/Credits, which are credited unseen (check the
   balance; D12, §3.1); it is the mission whose card shows the CUSTOMIZATION CRATE, not necessarily the first row.
   Claim the daily bonus: the Contracts crate's own reveal, as today. Force carrier draws (`weights` everything 0 but
   `sounds`): on a fresh profile every item is a random background plus a sound; the draws are with replacement over
   the 4 carriers, so a sound drawn a second time (at least one of 5) is already owned and shows a gear piece instead
   (`AlternateRewards` KillSignature → `Gear_Random_1_Random`), as do later crates until the next profile read; once
   all 4 sounds are owned and read, every item pays 70 VersusCoins. No exception in the log.
3. Weekly tier 1 or 2 (VC + Credits/tokens + 1 Cosmetic crate): the lottery shows the 5 items, the currencies credited
   unseen. Weekly tier 3 (VC + tokens + 2 Cosmetic crates): 10 items animate; with a carrier (11–12) the plain list.
   Weekly final (20 HC + the event crate): the event crate's reveal, as today.
4. `itemsPerCrate` 10: weekly tier 3 (20 items) and a Master rank-4 season crate (40) show the plain reward list (no
   lottery), every item listed; time it.
5. Random avatar/background reveals show the concrete emblem; 50 avatar draws contain no Kayac id (382–384, 443–531,
   533–589, 598–1514) and no starred season emblem.
6. A hidden jersey (e.g. USSR) from the crate appears in that agent's jersey list and can be equipped.
7. Witch or Bli-P from the crate: agent unlocked, unlock flow plays; its skins then appear in the preview.
8. Season end (set `liveops.seasonStart` to the next hour): rank 4–30 inbox "DIVISION REWARD" with a "CUSTOMIZATION
   CRATE" of band × 5 items; rank 1–3: HC + a ★★★/★★/★ emblem the player did not own + gear.
9. Unlock-all + `UNLOCKPAID` profile: crates pay 70 VC per item; no "unavailable" state on milestones.
10. Return-to-menu "connecting to the server" time with the collector on vs off, fresh and unlock-all profiles (target
    ≤ +0.3 s); match start time on vs off (target ≤ +0.1 s); the server log shows the collector line once per return.
11. Collector off (`economy.collector.enabled: false`) after `since`: events as before, ranks 4–10 get one gear piece,
    `GET /configs/users/me` carries only the 5 guard cells in each tree.
12. **Stale crate**: collector on, complete the day's first mission without claiming it, set `enabled: false`, return
    to the menu; if the old event is still shown, claim it → 70 VC, no exception; otherwise the replaced event shows no
    crate. Latch (round 3): with a `liveops.json` override moving `since` back, get a crate-carrying event, remove the
    override and set the phone's clock back before the shipped `since`: `GET /_mcvs/crm` shows `cratesServed` and the
    phase `Guard`; claiming the crate pays 70 VC.
13. `CratesCount` sent as a JSON integer arrives as an int (the TS opens exactly N items) under a comma-decimal locale.
14. `GET /_mcvs/collector?account=…` after a few crates: remaining counts fall; every part "known"; the agent states
    (locked/unlocked, skin counts) match the in-game agent list.
15. Race watch: on 5 logins, claim a Cosmetic crate before the first match; none pays VersusCoins while
    `_mcvs/collector` shows items left (a VC result would mean a guard/mirror changer won, §8).

## 8. Risks and open points

* **Per-user database changer race on the TS**: `DatabaseLoaded` is a static event and `PerUserDatabase` one
  process-wide registry; if a server World's handler (static mirror) runs after the TS's `CRMComponent`, the TS opens
  crates with the mirror (static weights, no `true` cells: avatars only from the 230 shipped-unlockable, still no
  Kayac) until the next forced refresh. Valid and O5-safe; more duplicates. Checks 1 and 15.
* **Preview cost on the TLE screens**: every crate reward object rebuilds a preview (`CreateRewardItem` + `GetObject` +
  `IsAvailable` per weighted row, signature list builds, agent scans) and runs `CrateRewardItem.IsAvailable` (rows in
  table order until one is available); the weekly screen builds 12 of them on the game thread. Round 3 (§2.3): the
  exhausted row first and background carriers bring the avatar-list builds from up to 78 per weekly screen (6 per crate
  object, 13 objects) to at most 12 (one per object, none for a complete profile); WP-F test 10 logs the net8 cost. Not
  measured: the camo/jersey random rows' `IsAvailable` (agent scans). Check 1 times it. Fallback (content only, no
  code): give the crate only on milestone tier 3 (`collector` block of `liveops_tuning.json`, e.g. `[[], [], [3]]`, the
  daily mission's crate kept), which cuts the crate objects per weekly screen to 4.
* **Dump shape for `Mercs`**: pinned by WP-F's real-writer fixture and the squad plausibility guard; check 14.
* **Mono/ARM cost** (§2.10): estimated +0.2–0.3 s per return to the menu, 25–80 ms per match; check 10 and the
  documented fallback.
* **`store` patch**: a build without it gets the guard only (D11); a standalone server serving an unpatched APK cannot
  tell (assumed patched, logged).
* **Event replacement** once for players updating after `since` or toggling the collector mid-week (§3.3).
* **Reveal UI**: > 10 items per claim show a plain list (check 4).
* Ranks 4–30 lose today's single gear piece; 3 league rank-up avatars (590–592) can come from the crate first.
* **Owner points**: (1) O7 read as "behaviourally identical" from `guardSince` (5 guard cells; byte-identical before;
  round 3: from a day before `since`, and for good once live ops served the crate — a tester's server that served it
  keeps the 5 cells until `<data>/collector-guard.json` is deleted); (2) Kayac exclusion includes 382–384 by art/name
  evidence and keeps the 60 `Kayac/holidays` emblems out (§1.2); (3) 11 of the 12 hidden jerseys O5 includes are
  Kayac-folder art (§1.4); (4) "everything in 6 months" from crates alone needs itemsPerCrate ≈ 14–16
  (regular/dedicated), outside 1–10 (§4.3); (5) the reserved starred emblems (D4) keep the crate set at 304 avatars;
  (6) round 3, O2's sources: the daily crate comes with the **day's first mission** instead of the "complete all"
  bonus, and the weekly final's two crates with **each tier 3** (16 per completed week instead of 14), because a
  milestone reveals only its last crate (D12); the alternative that keeps O2 literally moves today's Contracts crate and
  the week's event crate instead (a content change of their own); (7) round 3: the crate is called "CUSTOMIZATION CRATE"
  (`crm.CrateCustomization`), since "COSMETIC CRATE" read the same as the event crates in 7 languages; (8) round 3:
  the sounds come with a random background (was a random avatar), for the screen cost.
