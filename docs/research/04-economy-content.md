# 04 - Items, economy, stores, rewards, live config and remote content

Scope: how MCVS defines and delivers items, currencies, rewards, stores, crates, events/seasons and live
configuration; which parts are local data and which need a server; what a replacement server or local stub must
return; and a plan to make every item obtainable offline, plus an "unlock all" switch.

All paths are relative to `/home/user/mcvs-work`. Abbreviations: `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`,
`OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`, `GM/` = `decompiled/Game/Game/`,
`UT/` = `decompiled/Gameloft.Util/Gameloft.Util/`.
Tags: **[V]** = verified (I read the code). **[L]** = likely (strong inference). **[S]** = speculative.

---

## 0. TL;DR

1. **[V] Three layers of content.**
   * **Local design tables** (`Design/*.xlsx`, compiled to JSON in the CDN data pack and loaded with
     `AssetStream.ReadAllText`). They hold almost all *balance*: crate drop tables and weights, match/bot/level/league/
     achievement/season reward tables, daily shop, merc/skin/camo/weapon/mod/gear catalogs, mod costs, conversion
     rates and FTUE. These need no server (`EN/Database.cs:510-537`).
   * **CRM** (`GET {config}/configs/users/me`, a per-user JSON). It holds the *commerce and live-ops* layer:
     soft/hard-currency **store prices** (`offline_store`), the **IAP catalog** (`iap`), **pointcut actions** (popups,
     ads, server gifts), the **list of live event ids**, news feed, and `game` parameters. `game` contains
     `_OverrideTables` (live patches to any design-table cell) and `_ServerConfig` (matchmaking, AFK, dev flags).
     (`OP/StandardCrm.cs:164-176`).
   * **Osiris social events** (`GET {social}/events?event_ids=`). These define the time-limited content: **shop
     crates, HC bundles, TLE achievements/daily quests (Shards), arena modes with reward multipliers, division
     seasons, carousel and special offers** (`EN/TimeLimitedEventsComponent.cs:160-231`).
2. **[V] Prices for buying mercs, skins and kill signatures exist only in CRM.** No local table has a price for them
   (`GM/GameOfflineStore.cs:2184-2191`, `GM/MercsComponent.cs:1426-1437`). The shop-crate list comes only from
   `ShopCrateV2Event` TLEs (`GM/GameOfflineStore.cs:474-497` `DispatchCrates`). Bundles come only from `CustomBundleEvent`
   TLEs. **Without a CRM + events server the merc shop, skin shop, crate shop and bundle shop are empty.** Crates
   (loot crate with Credits, free crate, rewards) still work from local tables.
3. **[V] Real-money IAP cannot work offline.** It goes through the native `GLEcommCRMService`, which checks ecomm
   responses with signature keys (`GM/IapKeys.cs`, strings in `extracted/lib/arm64-v8a/libnativeengine.so`). **IAP
   *fulfilment* is a plain storage diff:** the server writes the Seshat profile section `inventory`
   (`{"HardCurrency":500,"MercSkin_3_2":1,...}`). The game grants every increase as rewards, on each login and on a
   `SetProfile` alert (`EN/IapStoreComponent.cs:1243-1306`, `GM/PlayerProfileComponent.cs:6055-6130`). That is a
   ready-made **server-side "grant anything" channel**.
4. **[V] Built-in "unlock all" paths already exist. None needs a code patch:**
   * **Coupon codes** (Settings, Redeem). `GET {asset}/coupons/{clientId}/{code}` returns
     `[{"type":"UnlockMerc_ALL","quantity":1}, ...]` and the game puts those rewards in the inbox
     (`GM/PlayerProfileComponent.cs:3524-3581`). `UnlockMerc_ALL` calls `CheatUnlockAllMercs`
     (`GM/UnlockMercRewardItem.cs:109-120`). The Redeem button is gated by the `GameFeatures.RedeemCoupons` table
     cell. CRM `_OverrideTables` can force that cell on.
   * **The `inventory` diff** (point 3).
   * **The developer cheat menu**, about 300 cheats including UnlockAllMercs, UnlockAllMercSkins,
     UnlockAllMercWeapons, UnlockAllWeaponCamos, Unlock All Accessories, Unlock All Mods, AllSignatures,
     MaxCurrencies and Unlock All Gear Slots (`GM/CheatsComponent.cs:495-890`). It is enabled for every player when
     CRM `game._ServerConfig.default` has `"IsDevelopment": true` and `"AdditionalDeveloperIpRange":
     "0.0.0.0/0 ::/0"` (`GM/GameServerComponent.cs:279-288,977-993`, `UT/Geolocalization.cs:112-130`).
5. **[V] Bot matches do *not* give full progression once PvP is unlocked.** After `PVPActivated`, an offline bot match
   pays only the `BotMatchesInfo` reward table. It gives no trophies, no merc XP and no MVP bonus
   (`GM/OfflineBotsGameplayComponent.cs:374-399,475-517`). Full progression (Credits, VersusCoins, MVP, career and
   division trophies, merc XP) comes only from the PvP path on a game server (`GM/GameplayComponent.cs:1129-1166`).
   So offline progression needs either (a) "PvP" matches on the embedded GS filled with bots (`EnableBotInLobby`
   defaults to `true`, `GM/GameMatchmakingComponent.cs:89`), or (b) a small IL patch to the bot-match reward code.
6. **[V] Hard requirements for a stub (otherwise there are hangs or NullReferenceExceptions):**
   * `GET configs/users/me/game` (game-server super session) must return 200 JSON. Otherwise
     `CRMServerSettingsComponent.GetGameParametersAsync` never completes, so the GS handshake and the GS start-level
     hang (`EN/CRMServerSettingsComponent.cs:34-62,100-150`, `GM/GameplayCRMComponent.cs:68-132`,
     `GM/GameServerComponent.cs:1191-1194`).
   * `configs/users/me` must contain `"game":{...}`. Otherwise new-profile creation throws a
     NullReferenceException (`GM/GameProfileSection.cs:60-75,147-153`) and the review popup code throws one too
     (`GM/PlayerProfileComponent.cs:4427`).
   * `offline_store` must exist, at minimum `{"prices":[]}`. Otherwise `UnifiedStoreComponent.RefreshOfflineStore`
     dereferences null (`GM/UnifiedStoreComponent.cs:431-435`) and `GetOfflineStoreItems` returns empty for every
     shop tab, crates included (`GM/GameOfflineStore.cs:297-313`).
   * **[L]** `iap` must exist, and the native IAP lib must call back `StoreRefreshed`. Otherwise the client
     `IapStoreComponent` never registers, so `UnifiedStoreComponent` and the shop UI block forever
     (`EN/IapStoreComponent.cs:829-865,883-927,1452-1482`, `EN/ManagerBase.cs:133-153`). This needs a runtime test.
     The fallback is a one-method IL patch.

---

## 1. How the economy fits together

```
           CDN data pack (local after install)                     Server (to be replaced)
 ┌───────────────────────────────────────────────┐     ┌───────────────────────────────────────────────┐
 │ Design/MCVS_Meta.xlsx, MCVS_System.xlsx, AI,  │     │ config  : configs/users/me  (CRM JSON)        │
 │ Gameplay, Notifications DBs (JSON tables)     │◄────┤   game._OverrideTables  -> patch table cells  │
 │  crates, rewards, catalogs, costs, FTUE ...   │     │   offline_store  -> shop prices               │
 │ Config/crmpreconfiguration.json (fallback CRM)│     │   iap            -> IAP catalog (native)      │
 │ Config/EdgeList.txt (developer IP ranges)     │     │   pointcut_actions -> popups/gifts/ads        │
 └──────────────┬────────────────────────────────┘     │   groupware.events -> TLE ids                 │
                │                                      │ social  : events?event_ids=  (TLE templates)  │
                ▼                                      │ storage : myprofile (+inventory, adsInventory)│
   C# "server" logic (TS/GS, runs in-process when      │ message : Hermes gifts -> inbox               │
   embedded) is authoritative for every grant:         │ leaderboard : season ranking                  │
   RewardItem.Claim(profile) -> profile template       │ asset   : images, tech_<lang>.json, coupons   │
   saved to storage myprofile                          │ ecomm/crm_iap : native IAP (signed)           │
                                                       └───────────────────────────────────────────────┘
```

* **[V]** All grants are server-side C# (`[Remote(Client→Server)]` request methods such as
  `GameOfflineStore.BuyItemRequest` `GM/GameOfflineStore.cs:1128-1186` and `CheatsComponent.DoCheatRequest`
  `GM/CheatsComponent.cs:949-976`). With the embedded/local server, that logic runs on the device. A replacement
  backend therefore never computes rewards. It only has to **serve data** (CRM, events) and **persist** the profile.
* **[V]** The player profile, including currencies, mercs, skins, crates and inbox, is one template string in the
  storage section `<GameProfileSelector>.<GameProfileKey>` (`GM/GameProfileSection.cs:100-123`). The storage
  protocol is covered by the storage report.

---

## 2. The reward/item grammar (everything is a string)

**[V]** An item id is `Type[_Sub1[_Sub2[_Sub3...]]][_Bonus][:qty]` (`GM/ExtendedRewardType.cs:29-67`).
`RewardItem.Parse` looks up the class `"{Type}RewardItem"` by reflection (`GM/RewardItem.cs:227-250`,
`GM/RewardHelper.cs`, `EN/TypeUtil.cs:11-41`). `Subtype3` takes the rest of the string. `Random` is a wildcard for
several subtypes (`RewardItem.RandomType`). If an item is not available (for example a duplicate), `Claim()` falls
back to an alternate reward from the `AlternateRewards` table. The lookup order is id, `Type_Sub_Sub2`, `Type_Sub`,
then `Type` (`GM/RewardItem.cs:129-152,346-371`).

The same strings are used in reward tables (`Type` column), CRM store `item`/`bundle`, TLE `rewards`, Hermes
`gifts[].name`, coupon `type`, the IAP `inventory` keys and the cheat menu.

| Type string | Class | Subtypes | Effect (OnClaimed) |
|---|---|---|---|
| `UnlockMerc_<mercId>` / `UnlockMerc_<rarity>` / `UnlockMerc_ALL[_<tierAlteration>]` | UnlockMercRewardItem | merc id, or rarity id (random locked merc of that rarity), or `ALL` | `profile.UnlockMerc`, or `MercsComponent.CheatUnlockAllMercs(profile, alteration)` for `ALL` (`GM/UnlockMercRewardItem.cs:11,84-120`, `GM/MercsComponent.cs:2443-2470`) |
| `TrialMerc_<merc>_<?>_<alter>` | TrialMercRewardItem | merc, alteration in Sub3 | timed trial; HC compensation if already owned (`TrialCompensation`, default 5) |
| `MercSkin_<merc>_<skin>` | MercSkinRewardItem | merc, skin | `UnlockMercSkin` and its linked camo (`MercSkinSpecs.WeaponCamoId`) |
| `WeaponCamo_<merc>_<camo>` | WeaponCamoRewardItem | merc or `Random`, camo or `Random` | `UnlockWeaponCamo` |
| `MercWeapon_<merc>_<weaponId>` | MercWeaponRewardItem | merc, weapon | `UnlockMercWeapon`; a duplicate becomes `WeaponAlternate × Fragments` |
| `MercWeaponFragment_<n>_<weaponId>` | MercWeaponFragmentRewardItem | fragment count (or qty), weapon | `AddWeaponFragments` |
| `TrialWeapon_<?>_<weaponId>` | TrialWeaponRewardItem | weapon in Sub2 | timed trial |
| `Mod_<merc>_<modId>` | ModRewardItem | merc, mod (`Random`, Super, Perk) | `UnlockModForMerc` |
| `ModTier_<merc>_<tier>[_WithPerks]` | ModTierRewardItem | merc, tier 1..4 | unlocks every mod up to that tier |
| `Gear_<slot>_<tier>_<stat>_<level>` | GearRewardItem | slot, tier, bonus stat, level (all `Random`-able) | `GearsComponent.AddGearServer`; if inventory is full, sold for `SellingCurrency` |
| `GearSlot_<merc>_<slot>` | GearSlotRewardItem | merc, slot | unlocks a gear slot |
| `MercAccessory_<merc>_<acc>` | MercAccessoryRewardItem | merc, accessory | `UnlockMercAccessory` |
| `KillSignature_<Avatar|Background|Sound>_<id>` | KillSignatureRewardItem | kind, id | `AddSignatureAvatar` / `Background` / `Sound` |
| `Crate_<crateId>` | CrateRewardItem | crate id | `CratesInventoryComponent.OpenCrateOnServer` (quantity × `CratesCount`) |
| `CrateTimerReduction`, `OpenFreeCrate` | ... | | free-crate timer helpers |
| `SoftCurrency`, `HardCurrency`, `AgentsCurrency`, `UpgradeTokens`, `Credits`, `SeasonTickets`, `VersusCoins`, `Shards` | currency items | none | `profile.AddCurrency` (free/paid stacks for HC) |
| `LotteryCurrency_<currencyId>` | LotteryCurrencyRewardItem | currency | adds that currency |
| `PlayerXP` | PlayerXPRewardItem | none | `AddXp` |
| `SubscriptionCard_<id>` | SubscriptionCardRewardItem | card id | registers a VIP card (`SubscriptionCard_{id}_*` tables) |
| `DailyReward_*`, `DailyShop_<entry>` | DailyRewardItem / DailyShopRewardItem | | subscription daily / daily-shop entry |
| `Reset*` (`ResetProfile`, `ResetHardCurrency`, ...) | ResetRewardItem family | | admin resets. **A stub must never send these.** |
| `MercPortrait` | `[Obsolete]` | | none |

The `RewardType` constants list `EventTickets` (`GM/RewardItem.cs:72-127`), but no `EventTicketsRewardItem` class
exists, so it is dead.

**Auto-claim rule [V]:** a gift is claimed silently unless it is `PlayerXP`, `UnlockMerc*`, `Reset*`, `Daily*`, a
crate-timer reduction or `OpenFreeCrate`, or is an ads-shop item. Those go to the inbox
(`GM/MessagingExtensions.cs:56-63`).

---

## 3. Currencies [V] (`GM/CurrencyType.cs`)

| Currency | Role (from spend/earn sites) | Earned from |
|---|---|---|
| `HardCurrency` (gems) | skins (CRM store), bundles (`CustomBundleEvent.cost`), missing-currency conversion (`ConversionHC*` tables), name change (`ChangeNameCost`), TLE unlock costs, trials compensation | crates/reward tables, achievements, league rank-up, IAP `inventory`, VIP, gifts |
| `AgentsCurrency` | buying mercs (`UnlockMerc_*` priced in AgentsCurrency, `GM/GameOfflineStore.cs:819-848`) | reward tables, daily caps (cheat `MaxKorpensDailyCap`) |
| `UpgradeTokens` | buying mods (`GM/ModsMetaComponent.cs:468`) | reward tables, crates |
| `Credits` | loot-crate meter: `CreditsForACrate` credits = 1 `MainLootCrate` (`GM/CratesInventoryComponent.cs:1188-1210`) | PvP win/lose/MVP (`CreditsCurrencyMatchRewards` per division, daily cap), bot-match tables, player level-ups (`PlayerLevelData.CreditsRewards`) |
| `VersusCoins` | the "#VersusCoins" store tab (CRM offline_store items priced in VersusCoins) | PvP (`VersusCoinsMatchRewards`, daily cap) |
| `SeasonTickets` | store/event prices (CRM/TLE) **[L]** | reward tables |
| `SoftCurrency` | legacy store prices **[L]** | reward tables |
| `Shards` | per-TLE currency for the final milestone (`GM/TimeLimitedAchievementEvent.cs:194-213`) | TLE milestones |

Daily caps live in the profile (`PVPCurrencyRewardCountForWins`, `VersusCoinsRewardCountForWins`) and reset at the
daily "black market" timer (`GM/MetagameComponent.cs:1142-1200`).

---

## 4. Local design tables (TableLoader / Database)

### 4.1 Loading [V]
* `Database.File = path`, then `Load(path)`, then `Json.LoadObject(AssetStream.ReadAllText(path))`. Each top-level
  key is a table and is populated into `Table{ string[] Fields; object[][] Rows }` (`EN/Database.cs:412-439,510-537`).
  The source is the local data pack, not the server. `File` throws `FileNotFoundException` if
  `GlfStream.Exists(path)` is false.
* Paths come from template data: `DatabasePathsComponent.{MetaDatabasePath, GameplayDatabasePath, AIDatabasePath,
  NotificationsDatabasePath}` (`GM/DatabasePaths.cs`). Literals seen: `Design/MCVS_Meta.xlsx`
  (`GM/CameraCaptureComponent.cs:249`) and `Design/MCVS_System.xlsx` (`GM/PlayerProfileComponent.cs:4523`). Other
  databases are `Design/{name}.xlsx` (`GM/GameplayCRMComponent.cs:193`).
* Per-user copies exist: `PerUserDatabase.GetDatabase(path, fedId)` (`EN/PerUserDatabase.cs:45-64`). Each user can
  get a different CRM patch.
* Typed views: `TableLoader.GenericTable<K,V>` (key column `Id`/`Index` via `[ColumnName]`), `CaseInsensitiveTable`
  and `GenericListTable` (`EN/TableLoader.cs`). Reward tables are `Game.Rewards : GenericListTable<DatabaseReward>`.
  The row class comes from the `Type` column through `RewardHelper.GetRewardType` (`{Type}Reward`)
  (`GM/Rewards.cs`, `GM/DatabaseReward.cs`). Columns: `Index, Type, Weight, AmountMin, AmountMax, DailyLimit,
  CarouselPicture, Category (Regular|Additional)`.
* **The table data is not in our hands yet.** The CDN pack uses hashed file names (`captures/cdn/08_astc_header.bin`
  lists `.\Data\1\0018344.f`...). Name resolution lives in native `GlfStream` (`EN/GlfStream_private.cs:120`).

### 4.2 CRM live patching of tables [V] (`EN/CRMComponent.cs:246-291`, `EN/CRMTableChanger.cs`, `EN/CRMTableFieldChanger.cs`, `EN/CRMTableCell.cs`, `EN/Database.cs:248-301,470-497`)

```json
"game": { "_OverrideTables": {
   "MCVS_Meta": {                         // database file name without extension
     "<TableName>": {
       "<IndexField>@<FieldToChange>": {   // '#' in FieldToChange is replaced by '.'
          "<indexValue>": "<value>"        // "123" = SET, "+5" = ADD, "-5" = ADD(-5), "*2" = MUL, or a JSON int = SET
       } } } } }
```

* **Only existing rows can be changed.** The row is looked up with `GetRowByFieldValue(IndexField, ref)` and
  skipped if missing. **No rows can be added.** Numeric-looking strings become `double`.
* The TS/menu side reads `game._OverrideTables` (`RawGameParameters`, `EN/CRMComponent.cs:280-291`). The GS reads
  `_ServerConfig.default._OverrideTables` (`GM/GameplayCRMComponent.cs:119-203`) and sends it to clients in the
  handshake reply. **Put the same block in both places.**

### 4.3 Tables referenced by the economy code (names from code defaults; templates may rename) [V]
Meta: `MercsList`, `Mercenaries_Stats`, `MercClasses`, `Abilities`, `AbilitiesStats`, `ASyncSquads`, `MercsWeapons`,
`WeaponBakedStats`, `CamosListPerWeapon` (camo tables per weapon), skins-per-merc (via `MercSkinsComponent.Table`),
`MercAccessories`, mods tables (`MercDataSpecs.ModsTableName`), `ModColours`, gear tables (`GearsComponent`),
`RarityList`, `AlternateRewards`, `ShopLockedMercs`, `AgentsMasterySettings`, `AgentsMasteryXP`.
Crates: `CratesInfo*` (`CratesInfoTableName`), `ShopCratesInfo*`, `FreeCrateInfo*`, `MainLootCrate`,
`CreditsForACrate`, `CrateTurningPoints`, `CrateItemsAboveTPMultiplier`, `CrateItemsBelowTPMultiplier`,
`Crate_Basic_OldUser`, `Crate_LastChance`, `AgentsExpected`, `FTUECratesOrderTablePool`, `MaxRewardsInAnimation`,
`SchematicsGranted`, `SchematicsInfo_Tournament`, `LiveTournamentAvailableCrates`, `CratesAds`, plus one content table
per crate (`CrateSpecs.ContentTableName`, columns `Id, Type, FixedQuantity, Subtype, Subtype2, BundledRewards,
AlwaysGiveBundledRewards, Weight.1..Weight.N` per round, `GM/CratesInventoryComponent.cs:33-195`).
Rewards: `BotMatchesInfo` (`Match, HasCrate, WinRewardsTableName, LoseRewardsTableName`, `GM/OfflineRewardInfo.cs`),
`CreditsCurrencyMatchRewards` and `VersusCoinsMatchRewards` (`Id`=division, `DailyCap, VictoryAmount, DefeatAmount,
MVPAmount`, `GM/PVPCurrenciesReward.cs`), `LeaguesRewardsTables`, `RankUpReward_League{N}`, `PVPLeaguesList`,
`DivisionsList`, `PVPDivisionRewards`, `PVPDivisionRewardsType`, `PVPWindowPoints`, `PVPShiftingWindow`,
`TrophiesRange`, player levels (`XPRequired, TrophiesBonus, DivisionTrophiesBonus, CreditsRewards, AvatarRewards,
BackgroundRewards, SoundRewards, ...`, `GM/PlayerProfileComponent.cs:398-422`), `PlayerLevelUnlocks`,
`Achievements_General/_Tank/_Assassins/_Assault/_Disruptors` (`GM/AchievementsComponent.cs:18`), `FTUE_Rewards_List`,
`AIOnboardingSpecs`, `TutoMatches`.
Shops: `DailyShopContent` and its sub-tables (`GM/DailyShopComponent.cs:276-282`), `ShopCategoryPrio`,
`ShopSubcategoryPrio_<type>`, `StoreCarouselContent` (OfflineStoreContents), `ModifiersCarousel`,
`AdsShopContent{N}`, `AdsShopPerDay`, `AdsShopStageCount`, `EndMatchAdAdditionalRewards`,
`EndMatchAdRewardMultiplier`, `TopUpRewards` (NicePlay), `SubscriptionCard_{id}_{Milestones|DailyRewards|RewardMultiplier}`.
Currency: `ImagesCommon*` (currency list), `StartingValues*`, `ConversionHC*`, `UpgradeTokensTiers`,
`SeasonTicketsTiers`, `VersusCoinsTiers`, `HardCurrencyTiers`, `CurrenciesTransitionRates[Kayac]`,
`KayacHCtoSCRatio`, `KayacDiamondRatio`.
System: `MCVS_System.GameFeatures` (`Id, Enabled`; only `RedeemCoupons` is referenced), `DemoMode`,
`MatchmakingBotAddingTime` (`TrophyRange, MaxLobbyTimeInSeconds`).

---

## 5. CRM ("config" / Hestia)

### 5.1 Requests [V]
| Who | Request | Notes |
|---|---|---|
| TS (per player, server world) | `GET {config}/configs/users/me` scope `config storage_ro`, optional `If-Match` | `CRMComponent.RefreshCrmConfig` (`EN/CRMComponent.cs:418-477`). Never on the client (throws). |
| GS (game super session) | `GET {config}/configs/users/me/game` | `CRMServerSettingsComponent.RetrieveServerConfig` (`EN/CRMServerSettingsComponent.cs:100-150`). A 404 whose body contains `needs a profile` makes it create an empty storage profile and retry. |
| Profile create (super session `mcvsGameServer`) | `GET configs/users/me` (uses `["game"]["_ServerConfig"]`) | `GM/GameProfileSection.cs:60-75` |
| Server | `GET configs/users/me/game.CrashlyticsParams.Enable` | `EN/ServerComponent.cs:365` |
| Native IAP | `GET {crm_iap}/configs/users/me/iap` **[L]** | string in `libnativeengine.so` |

A selector (`/game`, `/game.X.Y`) must return **the sub-tree at that path** (the GS parses the body directly as the
`game` object). An empty body is an error (`OP/CrmSection.cs:60-94`).

### 5.2 Failure behaviour [V]
* An online error, a cancel or invalid JSON in `RefreshCrmConfig` makes it use `Config/crmpreconfiguration.json`
  from the data pack. If that file has an `iap` key, it is indexed by the profile language
  (`EN/CRMComponent.cs:394-416,449-467`). `DoNotLoadConfig=true` (template) always uses the preconfiguration.
  **[S]** The preconfiguration may already contain a baseline `offline_store`. Extract it from the data pack.
* The GS `GetGameParametersAsync` has **no fallback**: it waits for an event that only fires on HTTP 200
  (`EN/CRMServerSettingsComponent.cs:34-62`). The GS start (`GM/GameServerComponent.cs:1191-1194`) and the GS
  handshake (`GM/GameplayCRMComponent.cs:119-132`) await it.
* The CRM JSON is pushed to native via `SetCRMConfig_private` (`EN/CRMComponent.cs:521-526`) and synced to the
  client as `CrmJsonString`.

### 5.3 Schema (everything `StandardCrm.Data` reads) [V] (`OP/StandardCrm.cs`, `OP/PointCut*.cs`, `OP/OfflineStore*.cs`, `OP/Iap*.cs`, `OP/NewsFeedData.cs`)

```jsonc
{
  "expiry": "2030-01-01T00:00:00Z",          // optional; when passed, the TS re-fetches CRM
  "tags": ["..."],
  "ads": { "priority_map": "...", "placement_ids": "..." },
  "pointcut_actions": [ {
      "id": "daily_hc", "type": "give_object",  // popup | give_object | give_object_secured | give_bundle_secured | show_ad | show_ad_button | show_ad_banner | log | log_name | log_stats
      "frequency": 1.0,                         // 0 = never fires; otherwise the chance to fire
      "priority": -1, "priority_group": "", "fatigue_group": "",
      "pointcuts": [ { "name": "launch", "conditions": ["first_time == False"], "action_args": {"ads_location": ""} } ],  // REQUIRED array
      "limit":     [ { "max": 1, "interval": 86400 } ],                                                           // REQUIRED array
      "item": "HardCurrency", "quantity": 50,   // or "bundle":[{"object":..,"quantity":..}] or "object":[..]
      "popup_id": "", "stats_name": "", "offline": false, "description": "crm.Daily", "gift_image": ""
  } ],
  "fatigue_groups": [ { "name": "g", "limit": [ {"max": 3, "interval": 86400} ] } ],
  "offline_store": {                            // REQUIRED, at least {"prices":[]}
    "prices": [
      { "item": "UnlockMerc_7", "quantity": 1, "category": ["#Mercs"], "hidden": false,
        "billing_methods": [ { "name": "AgentsCurrency", "type": "virtual",          // REQUIRED array
            "price": [ {"currency": "AgentsCurrency", "price": 1500} ],               // REQUIRED array
            "replaced_price": [] } ] },                                                // REQUIRED array (promo old price)
      { "group": "Pack_X", "bundle": [ {"item":"Crate_Y","quantity":1,"replaced_quantity":null} ],
        "category": ["Crates"], "billing_methods": [ ... ], "promotion": {"end_date": "..."} }
    ],
    "promotion": { "end_date": "...", "description": "..." }   // omit if unused (do not send null)
  },
  "iap": { "prices": [ /* IapStoreItem: _id, entry_id, name, description, cid, sku, tier, hidden(REQUIRED bool),
             icon, quantity, item, category[], bundle[{item,quantity,replaced_quantity}],
             billing_methods[{name,type,content_id,price,replaced_price,display_price,currency,currency_symbol,multiplier}] */ ] },
  "groupware": { "events": { "participation": [ {"id": "<osirisEventId>"} ], "advertisement": [ {"id": "..."} ] } },
  "newsfeeds": [ { "news_id": "n1", "title": "..", "body": "..", "asset": "", "redirect": "", "youtube": "",
                   "timestamp": 1700000000, "end_date": "..." } ],
  "game": {                                     // REQUIRED object
    "13yolimitationpopup": { "enabled": false },
    "AgeGateParams":     { "Ignore": true, "DefaultAge": 25, "DefaultGender": "m", "RussiaAge": null },
    "ReviewPopupParams": { "Ignore": true, "MinSecondsSinceLastAdView": 0, "MinSecondsSinceLastShopView": 0 },
    "CrashlyticsParams": { "Enable": false },
    "_OverrideTables": { },
    "_ServerConfig": {
      "default": {
        "IsDevelopment": false, "AdditionalDeveloperIpRange": "",
        "EnableRegionFilter": false, "RegionsAllowed": [], "ValidateDatacenterForCountries": [],
        "MaxGSReconnectAttempts": 3, "ThreadedNetwork": false,
        "DisableServerTracking": true, "DisableServerTrackingKills": true, "DisableServerTrackingCaptures": true,
        "TrackingSamplingPercentage": 0,
        "AFK": { "AfkActivated": false, "AfkAfterBattleStartTolerance": 45, "AfkDuration": 35, "DevelopperCanBeKickedByAFK": false },
        "Matchmaking": { /* see 5.4 */ },
        "MatchmakingRating": { "MinDeviation": 0, "MaxDeviation": 0, "MaxDiffRating": 0 },
        "UnsupportedLinkFrom": [], "UnsupportedLinkTo": [],
        "_OverrideTables": { }
      },
      "<clientId or clientId with . : , replaced by _>": { /* per-client override, merged over default */ }
    }
  }
}
```

Consumers of `game`: `GM/AgeGateChecker.cs:110-119`, `GM/UIComponent.cs:1279,1387`, `GM/PlayerProfileComponent.cs:4427`.
Consumers of `_ServerConfig`: `GM/GameServerComponent.cs:275-290` (dev/region), `GM/GameMatch.cs:410-426` (AFK),
`GM/GameMatchmakingComponent.cs:845-890` (Matchmaking), `GM/GameTrackingComponent.cs` `OnServerConfigReceived`
(tracking flags), `GM/GameProfileSection.cs:77-96` (account linking lists).

### 5.4 `Matchmaking` keys [V] (`GM/GameMatchmakingComponent.cs:845-890`, `GM/MatchMakingSettings.cs`, `GM/MMRFilter.cs`)
`MaxOpenTimeInSeconds, MinPlayers, DynamicRangeInterpolate, DynamicRangeEnabled, MinDynamicRange, MaxDynamicRange,
MinDynamicTimeInSeconds, MaxDynamicTimeInSeconds, DynamicRangeStep, ScoreBucketSize,
UpdateLobbyRecurringDelayInMilliseconds, MatchmakingDistributionWindowInMinutes, TeamReviewDurationInMilliseconds,
DisplayedExpectedLobbyTimeFactor, MaxDisplayedExpectedLobbyTimeInSeconds, EnableBotInLobby (default true),
EndedMatchExpirationInSeconds, GroupSynergyScoreMultipliers[], GroupSynergyScoreOffsets[], EnableBotFactor,
ExpectedLobbyTimeMultiplier, MMRFilters[{Id, Min, Max, CompressionFactor, ...}], TerminatorThreshold, MMRCap,
NoobSettings[{Id, MatchCount, AffectMMR, MaxLobbyTimeInSeconds}], SplitGroupPlay`.
The bot-fill wait comes from the local table `MatchmakingBotAddingTime` (`TrophyRange` → `MaxLobbyTimeInSeconds`,
`GM/GameMatchmakingComponent.cs:636-656`). For quick offline bot-filled "PvP" matches, set
`Matchmaking.NoobSettings[*].MaxLobbyTimeInSeconds` to a small value, or override that table.

### 5.5 Pointcuts [V] (`EN/CRMComponent.cs:675-805,950-1100`)
Locations fired by the game: `launch {first_time, resumed}` (15-tick delay), `level_up {level}`,
`acquire/use/not_enough_resources/resource_empty {item}`, `pause`, `purchase {group, store: online|offline, quantity}`,
`close_popup`, `enter_section/exit_section`, `start/finish/abort/unlock_mission`, `unlock_achievement`.
A condition is `"<field> <op> <value>"` (ops `== != > >= < <=`, compared as the field's type).
Limits are per action id and per fatigue group, stored in the profile (`CrmStandardData.CRMLimits`).
`give_object` creates an inbox message (or auto-claims) using **only the first item**
(`GM/InboxComponent.cs:326-356`, `GM/PlayerProfileComponent.cs:6334-6345`). `give_*_secured` posts a Hermes gift to
self (`EN/MessagingSessionComponent.cs:75-109`). **This gives a server-free daily-gift or level-up reward track
with no code change.**

---

## 6. Stores

### 6.1 Composition [V]
`GameUnifiedStoreComponent.Store = iapItems ∪ offlineCrmItems ∪ tleItems (ShopCrateV2Event + CustomBundleEvent) ∪
dailyShopItems` (`GM/UnifiedStoreComponent.cs:330,416-435`, `GM/GameUnifiedStoreComponent.cs:308,579-604`).
All purchases go through `GameOfflineStore.BuyItemRequest`, which looks up `UnifiedStore`, checks
`profile.GetCurrency(price.Currency) >= price*qty`, clones and claims the content (crates open server-side), calls
`SpendCurrency`, then fires the pointcut `purchase` (`GM/GameOfflineStore.cs:1128-1186`).

| Shop/tab | Source of items and prices | Server? |
|---|---|---|
| Merc shop (`BuyMerc`) | CRM `offline_store` `UnlockMerc_<id>`, priced in `AgentsCurrency` (and others); category tags `#LOCKED`/`locked_*` lock it (`GM/GameOfflineStore.cs:819-853,1438-1490`) | **CRM** |
| Skin shop | CRM `offline_store` `MercSkin_<merc>_<skin>` priced in `HardCurrency`; `#IAP`-tagged skins are hidden from this tab (`GM/GameOfflineStore.cs:327-395,855-895`) | **CRM** |
| Kill-signature shop | CRM `offline_store` `KillSignature_*` (`GM/GameOfflineStore.cs:906-995,1328-1382`) | **CRM** |
| Soft/VersusCoins/gems packs | CRM `offline_store` items (`#VersusCoins`, `SoftCurrency`, `HardCurrency` names) | **CRM** |
| Crate shop | **only** `ShopCrateV2Event` TLEs (`crateId, title, crate_count, promo_enabled, visual, prices[{cost, oldCost, currency}]`) (`GM/ShopCrateV2Event.cs`, `GM/GameOfflineStore.cs:474-497`) | **events** |
| Bundles | `CustomBundleEvent` TLEs priced in HC (`cost, oldCost, limit, days/hours/..., rewards[{item,quantity}]` or `merc_reward`, `img, shophub_img, shophub_title, orderId, permanent, promo_enabled`) (`GM/CustomBundleEvent.cs`, `GM/GameOfflineStore.cs:1930-2008`) | **events** |
| Daily shop | local `DailyShopContent` tables; regenerated daily; waits for the CRM data-set event first (`GM/DailyShopComponent.cs:276-410`) | local |
| IAP gems/bundles/VIP | CRM `iap` → native lib → Google Play → `StoreData{store[], promo_data}` (`EN/IapStoreComponent.cs:292-457,871-1066`) | **not possible offline** |
| Ads shop / end-match ad bonus / crate-timer ad skip | `AdsShopContent{N}`, `EndMatchAdAdditionalRewards`, `CratesAds` (local) + ad SDK + storage `adsInventory` (`EN/AdsComponent.cs:540-575`) | ad SDK |
| Store carousel | local `StoreCarouselContent` / `ModifiersCarousel` + `CarouselEvent` TLEs (`GM/GameOfflineStoreVisual.cs:200-202`) | local + events |

### 6.2 IAP internals [V/L]
* Client `IapStoreComponent.InitAsync`: `ConstructNative`, then `InitNative` (locates `ecomm_api_root` and `crm_iap`
  from the eve URL map and waits for the native lib to initialize), then `InitCrm` (sends CRM `iap` to native and
  **waits for the `StoreRefreshed` native callback**), and only then registers the component
  (`EN/IapStoreComponent.cs:829-865,871-927`). **[L]** If native never answers, everything awaiting the client
  `IapStoreComponent` (UnifiedStore, VIP card) hangs. This needs a runtime test.
* Native `GLEcommCRMService` (`Engine/Externals/in_app_purchase/source/service/gl_ecomm_crm/iap_gl_ecomm_crm.cpp`):
  `pre_buy_registration, pre_transaction, verify_transaction, end_transaction, reprocess_transaction,
  check_limitations, get_non_consumables, restore_subscriptions, app_receipt, verify_subscription`. Ecomm responses
  carry a hash header checked against obfuscated keys pushed from C# (`IapKeys.Signatures`, `SignatureKeys`,
  `EN/IapStoreComponent.cs:1508-1523`). **Forging ecomm is not a reasonable route.**
* **Fulfilment** is a diff on the storage section `inventory` against `CrmStandardData.Inventory`, which is persisted
  in the profile. Each positive diff `key → n` becomes `RewardHelper.CreateRewardItem(key, n)` and is claimed
  (`EN/IapStoreComponent.cs:1243-1306`, `GM/PlayerProfileComponent.cs:6055-6130`). It runs at TS init
  (`isInit:true`, `EN/IapStoreComponent.cs:855-862`) and on a `SetProfile` alert (`EN/IapStoreComponent.cs:964-970`).
  `adsInventory` works the same way with `ads_<item>` keys (`EN/AdsComponent.cs:540-575`).

---

## 7. Crates / gacha [V] (`GM/CratesInventoryComponent.cs`)
* Fully local. A crate is `CrateSpecs{Id, ContentTableName, Type, Rarity, Currency, Cost, RewardsCount, CratesCount,
  ShopType, IrisAsset...}`. Content rows have per-round weights `Weight.1..N`. "Turning points" adjust weights by how
  many mercs the player owns (`CrateTurningPoints`, `...Above/BelowTPMultiplier`, `AgentsExpected`).
  `BundledRewards` are always-given extras. Unavailable rewards (owned items) get re-rolled or fall back through
  `AlternateRewards`.
* Sources: the **loot crate** (`MainLootCrate`, opened when `Credits ≥ CreditsForACrate`), the **free crate** timer
  (`FreeCrateInfo*`, ad skip `CratesAds`), FTUE crate order (`FTUECratesOrderTablePool`), the last-chance and
  old-user crates, shop crates (TLE), tournament crates, bundle and inbox rewards.
* The cheat menu has `Simulate Crates Opening` (CSV output) to validate drop rates once the data is available.
* Drop rates can be tuned live with `_OverrideTables` on the content table (`Id@Weight.1` etc.).

---

## 8. Match rewards and progression [V]

| Situation | Code path | Rewards |
|---|---|---|
| Tutorial/training | `RewardComponent.Win(..., isInTraining)` → `AIOnboardingSpecs.RewardsTable` (`GM/RewardComponent.cs:386-394`) | table |
| Bot match while `!PVPActivated` (FTUE) | `OfflineBotsGameplayComponent.OnEndOfMatch` (`GM/OfflineBotsGameplayComponent.cs:374-388,475-517`) | `BotMatchesInfo` first row, Win or Lose table; MVP Credits/VersusCoins; **merc XP**; **career trophies** (`ApplyTrophiesResult`, no division trophies); achievements (`GameTypeTracker.PVE`) |
| Bot match after `PVPActivated` | same, `else` branch (`:389-394`) | **only** the `BotMatchesInfo` table (Credits capped by the daily cap); no XP, trophies or MVP |
| PvP on a GS (bots may fill the lobby) | `GameplayComponent.ProcessRewards` (`GM/GameplayComponent.cs:1129-1166`) → `RewardComponent.GenerateCurrency` (`GM/RewardComponent.cs:346-381`) | Credits and VersusCoins per division with daily caps × `ArenaParams.EndMatchRewardMultipliers`; MVP bonus; trophies, division trophies, XP, mastery (`profile.OnProcessMatchResult`); `CratesTrackWinsCount` |
| Private match/tournament | `RewardComponent.Win/Draw/Lose(isPrivateMatch)` | room `_rewardId` to the inbox / Hermes; ticket refund on draw |

`BotMatchesAvailable` is never set to false (`GM/MetagameComponent.cs:1155-1162`), so bot matches are unlimited.
Level-ups grant `CreditsRewards` and avatar, background and sound signatures (`GM/PlayerProfileComponent.cs:6975-7030`).
League rank-ups (local `RankUpReward_League{N}`, `GM/LeagueComponent.cs:299-364,528-560`) and achievements
(local, `GM/AchievementsComponent.cs`) are claimable rewards.

**Arena params [V]:** an in-progress `ArenaModeEvent` is serialized into the matchmaker room kwargs
`default_room_kwargs._arenaParams` (`GM/GameMatchmakingComponent.cs:934-948`). The GS reads the room attributes
`_arena` and `_arenaParams` (`GM/GameServerComponent.cs:1243-1245,1313`). JSON = `ArenaParams{MatchType
(None|TeamDeathMatch|Survivor|ControlPoint|ShootingRange|Uphold|Tutorial|Dominion), RestrictedAgents[int],
MatchParametersSets[str], AllowDuplicateMercs, ForcedMercModTier, GearsAllowed, PlayerLevelBonus, MapName,
EndMatchRewardMultipliers{CreditsMultipliers{VictoryAmountMultiplier, DefeatAmountMultiplier, MVPAmountMultiplier},
VersusCoinsMultipliers{...}}}` (`GM/ArenaParams.cs`, `GM/CurrencyMultipliers.cs`, `GM/ArenaModeEvent.cs:24-76`).

---

## 9. Events, seasons, leagues

### 9.1 TLE delivery [V] (`EN/TimeLimitedEventsComponent.cs:160-231,407-578`)
1. Ids = CRM `groupware.events.participation[].id` ∪ `advertisement[].id` ∪ persistent ids (the current division
   season id and the profile's `TleAchievements`) (`GM/GameTimeLimitedEventsComponent.cs:155-171`).
2. `GET {social}/events?event_ids=a,b,c&language=xx` (scope `social`) returns an array of Osiris events. Top-level
   fields: `id, name, start_date, end_date (ISO8601), _version, tournament{leaderboard{name, order}, type:
   award_by_rank|award_by_percentile, awards[{gifts[{name, value}], rank_interval[], minimum_score, percentile}]}`.
   Any other field goes to `Fields` (`_template`, `_priority`, `_batch_tag`, `_participation`, ...)
   (`OP/SocialEvent.cs:70-139`).
3. `_template` is a JSON **string** `{"event_tuning":{ key: SEMElement }, "milestones": SEMElement}`. A SEM element is
   `{"type":"text|text_plain|list_single|list_append|list_multi|range|time|duration|date|timestamp", "value":...}`,
   or `{"sub":{...}}`, or `{"itemtype":..,"id":..,"value":..,"consumable":bool}` (game item), or
   **`{"private":"1","value":<raw JSON>}` (raw value, the easiest for a stub)**. An unknown `type` throws.
4. `event_tuning.event_type` must be a type name resolvable by `TypeUtil` (for example `ShopCrateV2Event`). An event
   is kept if `template_version <= TimeLimitedEventsComponent.TemplateVersion` (send 0). `CanParticipate` = the id
   is in `participation`.

Event types [V]: `ShopCrateV2Event`, `ShopCrateEvent` (old crate shop with loot tables), `CustomBundleEvent`,
`TimeLimitedAchievementEvent` (shards, milestones, final milestone, `cost`, `user_duration`, `user_cooldown`,
trackers), `TimeLimitedDailyQuestEvent`, `ArenaModeEvent`, `DivisionSeasonEvent`, `CarouselEvent`, `SpecialOfferEvent`,
`BonusMultiplierEvent`, `FrenzyCrateEvent`, `FeedbackButtonEvent`, `LivePVPEvent` (legacy; only tracking and
notifications use it).
Image fields (`visual`, `img`, ...) are converted with `DownloadableAsset(string)`, which calls `.Trim()`. **Always
send a string, never null** (`EN/DownloadableAsset.cs:104-107`).

### 9.2 Division seasons [V] (`GM/DivisionsComponent.cs:330-460`, `GM/DivisionSeasonEvent.cs`)
`DivisionSeasonEvent` needs `Fields._batch_tag` (season id) and `Fields._participation` containing
`"... CurrentDivision == <n>)"` (at a non-zero index, for example `"(CurrentDivision == 3)"`). When a new season
starts, the server reads `LeaderboardService.GetLeaderboard(LeaderboardId).SelfData` (Olympus) to get the rank, then
computes promotion/demotion and rewards from local `PVPDivisionRewards`. A stub needs rolling season events plus a
leaderboard self-entry (the player's division-trophy rank among simulated players).

### 9.3 Leagues (career stars) [V]
Local only: trophies (`GentleTrophies`) map to `PVPLeaguesList`, and `RankUpReward_League{N}` gives rewards
(`GM/LeagueComponent.cs`).

### 9.4 Live tournaments [V/L]
Tickets are crates (`Crate_Tournament_*`). Rooms are private matches with `_rewardId`. Results are sent by Hermes
(`GM/RewardComponent.cs:88-160`, `GM/LiveTournamentComponent.cs`). This needs matchmaker rooms and messaging, so it
is lower priority.

---

## 10. Server-granted gifts (all paths) [V]
| Channel | Wire format | Lands in |
|---|---|---|
| Storage `inventory` diff | `GET {storage}/profiles/me/myprofile/inventory` → `{"<rewardId>": cumulativeCount}` | claimed directly (`IapStore_PurchaseCompleted`) |
| Storage `adsInventory` | `{"ads_<rewardId>": count}` | inbox (Ad reward) |
| Hermes gift message | body `{"gifts":[{"name":"<rewardId>","value":"<n>"}],"message":".."}` with `isGift` | inbox or auto-claim (`GM/InboxComponent.cs:245-290`) |
| CRM pointcut `give_object` | section 5.5 | inbox or auto-claim |
| Coupon | `GET {asset}/coupons/{clientId}/{code}` → `[{"type":"<rewardId>","quantity":n}]`; 404 = does not exist | inbox, 7-day expiry (`GM/PlayerProfileComponent.cs:3524-3581`) |
| Cheat `ForceRewardWithQuantity` (developer only) | remote call | ads reward path (`EN/AdsComponent.cs:874-890`) |

---

## 11. Remote assets (Iris, service `asset`) [V] (`OP/AssetsApi.cs`, `OP/AssetsService.cs`, `EN/AssetsComponent.cs`, `EN/DownloadableAsset.cs`)
* `GET assets/{cid}` → array `[{name, hash, ...}]`; `GET assets/{cid}/{name}` (optional Range header) → bytes;
  `GET assets/{cid}/{name}/metadata/hash` → `{"hash":"<sha256 hex>"}`; `GET assets/{cid}/{name}/url` → text.
  Timeout 40 s. The client-only cache lives at `GlfStream.CachePath/<Subdirectory>/<name>`.
* What is fetched: `tech_<lang>.json` (strings for the upgrade/maintenance popups, `GM/UIComponent.cs:482-497`,
  without a session), TLE and IAP images, `CrateSpecs.IrisAsset`, news images, menu-element images, inbox zip or png
  assets (`GM/InboxComponent.cs:740-800`), ad billboard images (`GM/AdBillboardManagerComponent.cs:40-60`), the
  localized top-up promo image, and ban-message assets (`OP/BannedFrom.cs:54`).
* **If absent:** every use either checks the local data first (`SearchInData`) or catches the failure and uses the
  default image. **Nothing gameplay-critical depends on Iris.** The stub can return 404 for everything except
  coupons.

## 12. GamePortal [V]
`GamePortalApi`/`GamePortalService` (`ios/1916/public/gamePortal/gamePortal.wsgi`, `op_code` POST) have **no callers**
in any decompiled assembly. Ignore them.

---

## 13. Catalog of content sources (what a replacement must provide)

| Content | Defined in | Delivered by | Server-dependent? | Minimal stub data |
|---|---|---|---|---|
| Item catalogs (mercs, skins, camos, weapons, mods, gears, accessories, signatures) | design tables | data pack | no | none |
| Crate contents and rates | design tables | data pack | no (CRM can patch) | none |
| Match/bot/level/league/achievement/season reward amounts | design tables | data pack | no | none |
| Merc/skin/kill-signature/pack **prices** | CRM `offline_store` | config | **yes** | a generated `offline_store` (section 14.2) |
| IAP catalog | CRM `iap` + native + Play | config/ecomm | **yes, unusable** | `"iap":{"prices":[]}`; move the content to offline_store/bundles |
| Shop crates | `ShopCrateV2Event` | social events | **yes** | permanent events per crate (14.3) |
| HC bundles | `CustomBundleEvent` | social events | **yes** | permanent events mirroring the old IAP bundles |
| TLE achievements / daily quests / Shards | `TimeLimitedAchievementEvent` / `TimeLimitedDailyQuestEvent` | social events | **yes** | a rotating set (template-generated) |
| Arena modes and multipliers | `ArenaModeEvent` | social events + matchmaker kwargs | **yes** | optional rotation |
| Division seasons | `DivisionSeasonEvent` + Olympus leaderboard | social + leaderboard | **yes** | weekly synthetic seasons + self-rank |
| Live config (matchmaking, AFK, dev flags, tracking) | CRM `game._ServerConfig` | config | **yes (hangs without it)** | section 5.3 skeleton |
| Popups/ads/gifts/news | CRM pointcuts/newsfeeds | config | yes (optional) | `[]` |
| Server gifts | Hermes/inventory/coupons | message/storage/asset | yes (optional) | used for "unlock all" |
| Images/tech strings | Iris | asset | cosmetic | 404 |
| Developer IP list | `Config/EdgeList.txt` (local; URL unused because `fromLocalFile=true`) | data pack | no | none |

---

## 14. Plan: every item obtainable offline + "unlock all"

### 14.1 Principles
* Keep the game's own server logic. Only **serve data**. Ship one versioned "content pack" JSON (CRM + events +
  coupon table) inside the embedded server `.so`, so every device in a P2P session sees the **same** prices and
  events. The GS host's CRM `_OverrideTables` goes to all clients through the handshake.
* Generate the store from the design tables at first run, or offline with a script once the data pack is decoded:
  enumerate `MercsList` (displayed mercs, `Id != "0"`), skins per merc, camos per weapon, `MercsWeapons`,
  `MercAccessories`, signature tables and gear catalogs.

### 14.2 Soft/hard-currency store (CRM `offline_store`), no code change
* `UnlockMerc_<id>` for every displayed merc. Price in `AgentsCurrency` (IsMercBuyable only shows mercs that have an
  AgentsCurrency price) plus an HC alternative. Tier the price by `MercDataSpecs.Rarity`.
* `MercSkin_<merc>_<skin>` for **every** skin, including those formerly `#IAP` or event-only. Do not tag them `#IAP`.
  Price in HC by skin rarity. (`IsSkinBuyable` requires an HC price.)
* `KillSignature_<kind>_<id>` for every signature not granted by levels.
* The `#VersusCoins` tab: sell skins, camos (`WeaponCamo_<merc>_<camo>`), accessories (`MercAccessory_*`), weapon
  fragments and gears for VersusCoins. These items cannot be bought anywhere else, and VersusCoins is earned in every
  PvP match.
* Currency conversion packs (for example `group` items selling `UpgradeTokens` for HC).

### 14.3 Crates and bundles (TLE events, no code change)
* A permanent `ShopCrateV2Event` (start 2000, end 2099) for each shop crate id in `ShopCratesInfo`. Price in
  HC/VersusCoins/SeasonTickets.
* `CustomBundleEvent`s that recreate the old IAP bundles (merc + skins + crates). Set `limit: 0` (unlimited) and
  `permanent: true`, priced in HC.
* A rotating `TimeLimitedAchievementEvent` (weekly) and `TimeLimitedDailyQuestEvent` (daily) generated from templates.
  The milestone `rewards` hand out accessories and camos, and the final milestone gives event skins for `Shards`.
  Use `GameTypeTracker.AllPVP` or `PVE` so that bot matches count.

### 14.4 Make progression pay enough
* Provide HC through progression: a CRM pointcut daily login gift (`launch`, `limit:[{max:1, interval:86400}]`,
  `give_object HardCurrency`), a pointcut level-up reward track (`level_up` with `level >= N` conditions, one action
  per level band), and `_OverrideTables` multipliers on HC rows of `BotMatchesInfo` win tables, achievement tables
  and `RankUpReward_League*` (`"*2"` operator).
* Raise the daily caps (`CreditsCurrencyMatchRewards`/`VersusCoinsMatchRewards` `Id@DailyCap`) via `_OverrideTables`.
* Real "full progression" in solo play needs one of these:
  1. **Preferred (no patch):** "PvP" quick matches on the embedded GS with bot fill (`EnableBotInLobby=true`, short
     `MatchmakingBotAddingTime`/`NoobSettings.MaxLobbyTimeInSeconds`). This goes through `GameplayComponent.ProcessRewards`.
     It depends on the lobby/matchmaker emulation (other reports).
  2. **IL patch (small):** in `OfflineBotsGameplayComponent.OnEndOfMatch` and `ProcessRewards`, take the
     `!PVPActivated` branch always (call `ApplyTrophiesResult` + merc XP + MVP), and in `PrepareOfflineMatchOnServer`
     use `RewardComponent.Win/Lose(isOfflineMatch:false)` so Credits and VersusCoins follow the division tables.
* Adding *new* rows to crate/reward tables (for example, putting event-only skins in a crate) needs data-pack table
  edits. CRM can only repurpose existing rows (section 4.2).

### 14.5 IAP-only content
Never route through native IAP. Re-home every IAP item:
* Gem packs: drop them. HC comes from progression (14.4).
* IAP bundles → `CustomBundleEvent` (HC) or offline_store `group` items.
* VIP `SubscriptionCard_<id>` → sell as an offline_store item priced in HC (`item: "SubscriptionCard_1"`). The
  daily/milestone rewards come from local `SubscriptionCard_*` tables.
* `#IAP` skins → plain offline_store skins (14.2).
* Keep `"iap":{"prices":[]}`. If native does not call back `StoreRefreshed` for an empty catalog, IL-patch
  `IapStoreComponent.InitAsync` (local-player branch) to skip `InitNative`/`InitCrm` and call
  `SetStoreData("{\"store\":[]}")` (`EN/IapStoreComponent.cs:829-845,1452-1456`).

### 14.6 "Unlock all" (pick one or more)
1. **Coupon code (recommended user toggle, no patch).** The stub implements `GET coupons/{cid}/{code}`. For example
   `UNLOCKALL` returns `[{"type":"UnlockMerc_ALL_<maxTier>","quantity":1}]` plus one entry per skin, camo, weapon,
   accessory and signature (`MercSkin_<m>_<s>`, `WeaponCamo_<m>_<c>`, `MercWeapon_<m>_<w>`, `MercAccessory_<m>_<a>`,
   `KillSignature_<k>_<id>`, `GearSlot_<m>_<slot>`), plus currencies. Enable the Redeem button with
   `_OverrideTables.MCVS_System.GameFeatures["Id@Enabled"]["RedeemCoupons"]="true"`. Note that `UnlockMerc_ALL` must
   be claimed before the skin/accessory items, because those check that the merc is unlocked. Send the mercs as a
   first coupon and the rest as a second one, or claim the inbox in order.
2. **Inventory push (launcher-side toggle).** The stub adds the same keys to the storage `inventory` section. They
   are granted automatically at next login. Choose the order of keys with care: `MercSkin*` keys are processed last
   (`GM/PlayerProfileComponent.cs:6089`), and `UnlockMerc_ALL` counts as available.
3. **Developer cheat menu.** CRM `_ServerConfig.default` = `{"IsDevelopment":true,"AdditionalDeveloperIpRange":
   "0.0.0.0/0 ::/0"}`. `IsDevelopment` already defaults to true in code (`GM/GameServerComponent.cs:123`); the edge
   list check needs the IP range (`GM/GameServerComponent.cs:977-993`). This exposes every cheat, including
   destructive ones (Reset profile, crash tests), and side effects: locked mercs can be picked in matches
   (`GM/PlayerComponent.cs:988`), AFK immunity, and developer error popups. Use it for testing only, or as an
   "advanced" option.
4. **IL patch** with a dedicated settings toggle that calls `MercsComponent.CheatUnlockAllMercs`,
   `MercSkinsComponent.UnlockAllSkins`, `MercWeaponComponent.UnlockAllWeapons`, `WeaponCamosComponent.UnlockAllCamos`,
   the accessories loop, `CheatsComponent.AllSignatures` and `UnlockAllMods` server-side. These functions exist
   (`GM/CheatsComponent.cs:1047-1627`).

---

## 15. Risks and hang points
* The GS waits forever on `configs/users/me/game` (section 5.2).
* A missing `game`, `offline_store`, `billing_methods`, `price` or `replaced_price` gives a NullReferenceException
  (the parsers iterate without null checks: `OP/OfflineStoreData.cs:19`, `OP/OfflineStoreItem.cs:53,61`,
  `OP/OfflineStoreBillingMethod.cs:28,32`, `OP/PointCutAction.cs:57,61`).
* `"promotion": null` or `"expiry"` set to a bad date. `promotion` presence is tested with `!= null`, so a JSON null
  passes the test, then `SafeGetString`/`FromISO8601` run on it (`OP/OfflineStoreData.cs:23-29`, `OP/OfflineStoreItem.cs:71`). Omit the key
  instead.
* Client IAP init waits on the native callback (section 6.2).
* TLE image fields set to null throw in `DownloadableAsset` (section 9.1).
* `Reset*` gifts wipe data. The stub must never emit them.
* `_OverrideTables` numeric-string coercion to `double` (section 4.2).

## 16. Verified vs inferred
* **Verified:** everything cited as [V]: CRM schema and consumers, the hang and NRE points, store composition, the
  inventory diff path, the coupon path, the cheat gating, the reward grammar, the bot-match reward branches, TLE
  parsing, the asset uses, and that GamePortal is unused.
* **Likely:** native `/configs/users/me/iap` use on `crm_iap`; the client shop hang if native IAP never calls back;
  the roles of SeasonTickets/SoftCurrency; the preconfiguration file contents.
* **Speculative:** whether `crmpreconfiguration.json` already contains a usable `offline_store`; how native reacts to
  an empty `iap`.

## 17. Open questions
1. Decode the data pack (hashed names) to get `Design/MCVS_*.xlsx` JSON, `Config/crmpreconfiguration.json`,
   `Config/EdgeList.txt` and the templates (`DatabasePaths`, `TemplateVersion`, `DoNotLoadConfig`, table-name
   overrides). This is a prerequisite for generating `offline_store` and for checking HC earn rates.
2. Runtime test: does native `RefreshStore_private` with `{"prices":[]}` and no Google Play call back `StoreRefreshed`?
3. An archived real CRM response would give real prices and categories. Search community dumps and proxies.
4. Does the embedded TS see the client's TCP peer as `127.0.0.1` or `::ffff:127.0.0.1`? This matters for the
   `AdditionalDeveloperIpRange` value.
5. For P2P: the host's TS/GS computes guest rewards and reads the guest CRM through the host's Federation. Every peer
   must ship the identical content pack (storage/P2P analysts).
6. What the ecomm / crm_iap request paths look like (native only). Not needed if IAP is bypassed.
