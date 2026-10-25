# Verification of claim C5-unlock-all

**Claim (as given):** Unlock-all can be done without patching:
(a) setting CRM `game._ServerConfig` `IsDevelopment=true` plus `AdditionalDeveloperIpRange "0.0.0.0/0 ::/0"`
enables a developer cheat menu with UnlockAllMercs/Skins/Weapons/Camos/Accessories/Mods/Signatures and MaxCurrencies;
(b) coupon codes (`GET asset coupons/{cid}/{code}` → `[{type, quantity}]`, including `UnlockMerc_ALL`) work when
`GameFeatures.RedeemCoupons` is enabled;
(c) the IAP `inventory` storage ledger `{rewardId: count}` is diffed and granted at login.
It also says every item can be earned through progression if the stub's CRM `offline_store` prices every
merc, skin and signature in soft currency.

**Verdict: PARTIALLY CONFIRMED.**

* (a), (b) and (c) are all real, and none of them needs a code patch.
* (a) is described the wrong way round for Embedded mode. In Embedded/LocalAnubis mode the cheat menu is already
  **on by default**, with no CRM settings at all. The stub has to send `IsDevelopment:false` to turn it off. The
  IP range in the claim only matters for non-loopback (P2P) peers, and with it set every guest becomes a developer.
* (b) and (c) have format and ordering traps that can silently lose grants. They are listed below.
* The "everything obtainable through progression" part is **refuted** as stated:
  * `offline_store` drives only merc prices, skins, kill-signature avatars and backgrounds, a few packs, and
    generic `#VersusCoins` hub items.
  * The `offline_store` code path for weapons is dead.
  * Camos, accessories, mods, gear and signature sounds have no `offline_store` screen.
  * About 85% of camo and accessory items have no local (non-server) progression source.

Data pack: present and complete (`datapack/1.17.32/ETC/files/Data`, 15,745 hashed files). Files were decoded with
`tools/datapack/gcbf.py`, and names were resolved as `"%x" % crc32(path)` (`tools/datapack/names.py`). Logical
paths below include the on-disk hash.

Abbreviations: `GM/` = `decompiled/Game/Game/`, `EN/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`,
`EL/` = `decompiled/Gameloft.Engine/Gameloft.Engine.LocalAnubis/`, `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`,
`UT/` = `decompiled/Gameloft.Util/Gameloft.Util/`.

---

## Summary table

| Sub-claim | Result | Key correction |
|---|---|---|
| (a) cheat menu via CRM `IsDevelopment` + `AdditionalDeveloperIpRange` | **Partially.** The mechanism is real, but the settings in the claim are not what controls it | The loopback client is always in the built-in edge list, and `IsDevelopment` defaults to `true`, so the menu is **on by default** in Embedded mode. The IP range is only needed for P2P guests. Open the menu with the tap sequence below. The cheats run on the TS and are saved by the normal TS saves. |
| (a) list of cheats | Confirmed (exact names below) | `UnlockAllMercSkins`/`UnlockAllMercWeapons`/`Unlock All Accessories (Mercs)`/`UnlockAllWeaponCamos` only affect **unlocked** mercs, so run `UnlockAllMercs` first. `MaxCurrencies` caps Credits at 500. |
| (b) coupons | Confirmed with caveats | The shipped `RedeemCoupons` cell is `0` (off). It can be forced on with `_OverrideTables`. The coupon is redeemed on the TS, its rewards go to the inbox (7-day expiry), and they are claimed in array order. The code is marked "claimed" before the body is parsed, and the claimed list is stored in the profile. `UnlockMerc_ALL_MAX` also unlocks all mods. |
| (c) inventory diff | Confirmed with caveats | The diff runs on the TS when `IapStoreComponent` initialises, and again on a `SetProfile` alert. Values **must be JSON integers**. Keys are processed in JSON order, except `MercSkin*`, which go last. A key that is claimed before its merc is unlocked becomes an alternate reward and is lost. |
| (d) everything obtainable via `offline_store` | **Refuted as stated** | Only mercs, skins, avatars, backgrounds and generic `#VersusCoins` items work. The weapons store path is dead code. Camos and accessories come only from the daily shop (HC), mastery, VIP, or server time-limited events (TLE). `Mod_*` items are hidden in generic shop tabs by an inverted `IsAvailable`. |

---

## (a) Developer cheat menu

### How `IsValidDeveloper` is computed

```csharp
// GM/PlayerProfileComponent.cs:1126-1136
public virtual bool IsValidDeveloper { get { if (IsDeveloper) return IsDevelopment; return false; } }
// GM/PlayerProfileComponent.cs:542      private bool isDevelopment = MainLoop.IsEditor;   (per-profile field)
// EN/MainProfileComponent.cs:46-56      public virtual bool IsDeveloper {get;set;}  [SyncReliable]
```

Both `IsDeveloper` and `IsDevelopment` are public get/set properties. Under `DefaultConverter.KeepProperty`
(`decompiled/Gameloft.Templates/Gameloft.Templates/DefaultConverter.cs:414-440`) they are **persisted in the saved
profile** and synced to the client.

TS handshake (`GM/GameServerComponent.cs:976-993`):

```csharp
IPAddress ip = ((connection.ControlChannel.Socket as ITcpSocket)?.RemoteEndPoint as IPEndPoint)?.Address;
if (!playerProfile.IsValidDeveloper && IsTS) {
    await WaitForCRM(ct);                                   // max 1 s, :1175-1189
    ... bool num = await geolocalization.IsInGameloftEdgeList(ip, AdditionalDeveloperIpRange, token.Token);
    playerProfile.IsDeveloper = num;  playerProfile.IsQA = num;
}
playerProfile.IsDevelopment = IsDevelopment;                // server-wide flag, every handshake
...
reply["IsDevelopper"] = playerProfile.IsValidDeveloper;     // :1066 -> GameClientComponent.IsDeveloperByHandshake (GM/GameClientComponent.cs:3630)
```

Server-wide defaults (`GM/GameServerComponent.cs:123,127`): `IsDevelopment = true` and `AdditionalDeveloperIpRange = ""`.
These are overridden only by `OverrideFromCRM` (`:275-291`), which starts at server init (`:233`). It reads
`CRMServerSettingsComponent.GetGameParametersAsync`, which comes from `GET configs/users/me/game` on the
**game-server super session** and uses `_ServerConfig.default`, merged with `_ServerConfig.<clientId>`
(`EN/CRMServerSettingsComponent.cs:100-129`, `OP/CrmApi.cs:7-19`). So the claim's "CRM `game._ServerConfig`" path is
right. The shipped template does **not** override `IsDevelopment`. `SceneComponents.tx` (`1/52218da.f`), lines
304-331, sets `GameServerComponent` with only `EnableRegionFilter`, `KeepAlive*`, `RegionsAllowed`,
`PromotedGameClientIds`, `PromotedCredentialsTypes` and `ThreadedNetwork`.

The edge list (`UT/Geolocalization.cs`):

```csharp
// :172-176  every list is prefixed with hard-coded ranges
list = $"127.0.0.0/8 192.0.0.0/24 10.0.0.0/8 {list}";
// :117-130  additionalRange is split on ' ' and unioned; :148-170 file "Config/EdgeList.txt" via GlfStream (EN/ServerComponent.cs:168-170)
// :38-69 IsInRange: byte-length must match (4 vs 16); prefix 0 => loop never runs => true
```

* `Config/EdgeList.txt` (`1/bc02a9e.f`) is **not in the data pack**, so the list is exactly
  `127/8, 192.0.0/24, 10/8` plus whatever the CRM adds.
* `"0.0.0.0/0"` matches every IPv4 address and `"::/0"` matches every IPv6 address, including IPv4-mapped
  `::ffff:a.b.c.d`. The syntax in the claim is valid.

**The client IP in Embedded mode.** `LocalAnubisManager.Host` is `IPAddress.Loopback` unless `-LocalAnubisHost` is set
(`EN/LocalAnubisManager.cs:44-54`). The Lobby/Controller use `new TcpListener(host, port)` (`EL/BaseServer.cs:201`). The
accepted `TcpClient` socket becomes `ConnectionContext.GameSocket.Socket` (`EL/BaseServer.cs:102-110`), and that is the
`ITcpSocket` handed to `ServerManager.AddClientAsync` (`EL/Controller.cs:83-95`).

So the TS sees the peer as IPv4 `127.0.0.1`, which is always inside `127.0.0.0/8`. Result: **with Embedded mode, and
with no CRM `_ServerConfig` at all (or a slow one, since `WaitForCRM` gives up after 1 s), the local player is a
valid developer.** The CRM settings in the claim are sufficient but unnecessary. The opposite setting is the one that
matters: to hide the menu, the stub must serve `_ServerConfig.default.IsDevelopment:false` quickly.

`ip == null` would throw a NullReferenceException inside `IsInRange`, with no catch around it in the handshake.
This cannot happen with LocalAnubis, because the socket is always a `TcpSocket`.

**When it fails or behaves differently:**

* **P2P/LAN** (`-LocalAnubisHost` set to a LAN address):
  * Guests from `192.168.x.x` are **not** developers unless an extra range is added.
  * Guests on a **10.x LAN are developers automatically** whenever `IsDevelopment` is true.
  * With `"0.0.0.0/0 ::/0"`, every guest is a developer, and can run destructive cheats such as `Reset` on their
    own profile, which the host's TS is serving.
* **Developer status sticks.** The IP check runs only when `!IsValidDeveloper`. A profile saved with
  `IsDeveloper=true` and `IsDevelopment=true` skips the check on its next handshake. It then stays a developer on
  any TS whose `IsDevelopment` is true, whatever its IP.
* `IsDevelopment` is server-wide, not per user. Per-user control is possible only through IP ranges.

### How the menu is opened (decoded from `Menus/UI.swf`, `4/1ffa170.f`, AS3 ABC at file offset 9723047)

I wrote a minimal AVM2 disassembler, because there is no SWF tool on the box. The dump is at
`/tmp/claude-0/-home-user/98d9334c-9d5f-5995-bde1-c27c1652af38/scratchpad/c5/cheatmanager.txt` and the tool is
`.../scratchpad/c5/avm2.py`.

* `SwfMain.init` always calls `CheatManager.initialize(stage)`.
* `CheatManager.<cinit>` sets `TAP_DELAY = 1500` and `_cheatTapsNeeded = 6`.
* `initialize` registers the sequence `cheatSequence='123414'`, `cheatName='CheatsMenu'`,
  `cheatEvent='SHOW_CHEATS'`, and listens for `MouseEvent.MOUSE_UP` on the stage.
* `onScreenCheatTap` (offsets 13-120) maps each tap to a quadrant:
  * `stageX < vp.width/2` and `stageY < vp.y + vp.height/2` → `'1'` (top-left)
  * left half, lower part → `'3'` (bottom-left)
  * right half, upper part → `'2'` (top-right)
  * right half, lower part → `'4'` (bottom-right)
* A 1.5 s timer resets the sequence on each tap.
* When the sequence completes, it dispatches `GlobalEvent('SHOW_CHEATS')`.
  `MenusManager.setupListeners` → `onShowCheats` does: `if (Native.CanUseCheats()) MenusManager.pushMenu('menu_cheats')`.

So the gesture is: **tap top-left, top-right, bottom-left, bottom-right, top-left, bottom-right, with less than
1.5 s between taps.** `Native.CanUseCheats` is overridden by `CheatsComponent.NativeCanUseCheats`
(`GM/CheatsComponent.cs:228-230,440-450`). It returns the client profile's `IsValidDeveloper`, or
`GameClientComponent.IsDeveloperByHandshake` if there is no profile. `CheatsComponent` is in
`Templates/MetaGame/MetaGame.tx` (`c/45354a9.f`), lines 69-70.

### Cheat names and implementations (exact IDs)

* `BuildCheatsTable` (`GM/CheatsComponent.cs:495-889`). On the server it is built in `OnProfileLoaded` (`:324-326`).
* Default `ActionType` is `ONLINE` (`:877`).
* `ONLINE` → `DoCheatOnServer` → `[Remote(Client→Server)] DoCheatRequest` (`:950-976`). This rechecks
  `IsValidDeveloper` on the server, then runs the delegate on the **TS** profile.

| ID (category) | Line | Effect | Caveat |
|---|---|---|---|
| `UnlockAllMercs` (Mercs) | 730 → 1441 | `MercsComponent.CheatUnlockAllMercs` (`GM/MercsComponent.cs:2443-2470`) | only `IsDisplayed` specs. In the data, all 21 non-zero mercs are `YES` (MCVS_Gameplay `MercsList`). |
| `UnlockAllMercSkins` | 735 → 1603 | `MercSkinsComponent.UnlockAllSkins` (`GM/MercSkinsComponent.cs:192-215`) | unlocked mercs only |
| `UnlockAllMercWeapons` | 736 → 1609 | `MercWeaponComponent.UnlockAllWeapons` (`GM/MercWeaponComponent.cs:171-190`) | unlocked mercs only |
| `UnlockAllWeaponCamos` (Mercs) | 740 → 1627 | `WeaponCamosComponent.UnlockAllCamos` (`GM/WeaponCamosComponent.cs:252-276`) | requires `IsDeveloper`; covers only the vanilla weapon's camo list, and only unlocked mercs |
| `Unlock all weapon camos` (Camos) | 630 → 3949 | all `GetAllWeaponCamoSpecs` for every merc | no lock filter |
| `Unlock all accessories` (Accessories) | 628 → 3917 | every merc | |
| `Unlock All Accessories` (Mercs) | 739 → 1586 | unlocked mercs only | |
| `Unlock All Mods` | 1019 → 1053-1075 | `GetUnlockedModsListForTiers(id, 5)` | |
| `AllSignatures` | 689 → 1153-1174 | every row of the avatar, background and sound tables (1518 + 35 + 5) | |
| `Unlock All Gear Slots` | 680 → 1385-1394 | | |
| `MaxCurrencies` | 699 → 1242-1249 | HC, UpgradeTokens, SeasonTickets and VersusCoins to 999999; Credits to **500** | |
| `ForceSave` | 506 → 2437 | `profile.ForceSaveProfile` | use it after unlocking |

**Persistence.** Results go through the normal TS save:

* the autosave timer (`AutoSaveIntervalMinutes`, default 5; `GM/PlayerProfileComponent.cs:4736-4760`)
* pause/release (`GM/GameServerComponent.cs:758-768`)
* scene load (`GM/PlayerProfileComponent.cs:4764`)
* the `ForceSave` cheat

If the app is killed before one of these, the changes are lost.

**Side effects of being a valid developer.** All of these are on by default in Embedded mode:

* locked mercs can be picked in matches (`GM/PlayerComponent.cs:988`)
* AFK immunity (`GM/CharacterInputComponent.cs:337`)
* developer-only maps added to the map lists (`GM/MapsComponent.cs:48,88`)
* region-filter bypass (`GM/GameServerComponent.cs:1032`)
* currency tracking (`GM/PlayerProfileComponent.cs:3136,3207`)
* destructive cheats exposed: `Reset` (498), `ResetProfileAndLoadItems` (499), `Lock AAAALL Mercs` (732),
  `Empty Cyrrencies` (700), `Generate Crash` (840), `SetDatacenter*` (866), and `DisableAllCRMCampaigns`, which
  resets the CRM to the preconfiguration (2533)

---

## (b) Coupons

* **UI.** Settings → Account → "Redeem code". The button is added only if `HasFeatureEnabled("RedeemCoupons")`
  (`GM/PlayerProfileComponent.cs:4539-4548`). That reads `Design/MCVS_System.xlsx` → `GameFeatures`, looked up by Id →
  `Enabled` (`:4521-4524`), through `Convert.ToBoolean`.
  * The shipped table (`b/80fbc81.f`) is `{"GameFeatures":{"Fields":["Id","Enabled"],"Rows":[["RedeemCoupons",0]]}}`,
    so the button is **disabled**.
  * SWF: `MenuAccountSettings.doAction` with `customAction=='showRedeem'` → `submitRedeem` → `Native.RedeemCoupon(text)`.
    `MenuHelp` has a second redeem entry, gated by `_platform`. I did not resolve which platforms.
* **Enabling it with the CRM only.** Path: `game._OverrideTables.MCVS_System.GameFeatures["Id@Enabled"]["RedeemCoupons"] = "1"`.
  * The changer is selected by DB file name (`EN/CRMComponent.cs:245-257`). The key is split on `@`
    (`EN/CRMTableChanger.cs`).
  * The value `"1"` is parsed as a double and SETs the cell (`EN/CRMTableCell.cs`, `EN/Database.cs:143-195`).
  * `"true"` also works: it is stored as a string, and `Convert.ToBoolean("true")` is true.
  * The gate is UI-only. `RedeemCouponRemote` itself checks nothing.
* **Flow.** `NativeRedeemCoupon` → `[Remote(Any→Server)] RedeemCouponRemote` (`GM/PlayerProfileComponent.cs:3548-3581`)
  → `AssetsComponent.RedeemCoupon` (`EN/AssetsComponent.cs:180-210`) → `AssetsService.RedeemCoupon` → **GET**
  `coupons/{Federation.ClientId}/{code}` on `OnlineService.Asset` (`OP/AssetsApi.cs:39-43`; GET is the default,
  `OP/FedRequestMessage.cs:111-113`). The request uses the user's TS session.
  * 404 → "CouponDoesNotExist"; any other failure → "CouponUnknownError".
  * On success, `PushCouponDataToInbox` (`:3524-3545`) runs `Json.LoadArray(body)`. For each object it reads
    `type` (a reward string) and `quantity` (`SafeGetInt`; a missing value gives 0), and calls
    `RewardHelper.CreateRewardItem`. The result is **one inbox message, expiring after 7 days**.
* **Claiming.** `InboxComponent.ClaimRewardsForMessageRequest` (`GM/InboxComponent.cs:593-639`) claims the rewards
  **sequentially in array order** on the server. `Claim()` evaluates availability at claim time, and an unavailable
  item becomes its alternate reward (`GM/RewardItem.cs:346-372`).
  * So one coupon is enough if `UnlockMerc_ALL…` comes first. The report-04 advice to split it into two coupons is
    not needed.
* **`UnlockMerc_ALL`** (`GM/UnlockMercRewardItem.cs:67-120`):
  * `"ALL"` is not a rarity key (`RarityList` = Rarity01..06), so `IsAvailable` returns `GetMerc("ALL")?.Locked ?? true`,
    which is always true.
  * `OnClaimed` → `CheatUnlockAllMercs(profile, Subtype2)`.
  * `Subtype2` set to `TiersN` or `MAX` (9999) also unlocks the mods up to that tier (`GM/MercsComponent.cs:2400-2411,2456-2465`).
  * So **`UnlockMerc_ALL_MAX` unlocks every merc and every mod.**
* **Gotchas:**
  * `claimedCoupons.Add(code)` runs **before** the body is parsed, and `claimedCoupons` is `[TemplateIgnore(false)]`
    on the profile's `AssetsComponent` (`EN/AssetsComponent.cs:40-41`; `DefaultProfile.tx` `c/fa4981c.f` line 11).
    So each code works **once per profile**, even if the body was malformed.
  * A non-array body throws out of `RedeemCouponRemote`.
  * A full unlock is about 11k reward entries in one inbox message: 4,409 camo pairs + 5,208 accessory pairs +
    1,558 signatures + skins and weapons. UI and save-size behaviour at that size is untested.

---

## (c) IAP `inventory` ledger diff

* On the TS, `IapStoreComponent.InitAsync` takes the non-local branch, then if `World.IsServer()`:
  `InitAlerts`, `await CheckInventory(null, isInit:true)` (`EN/IapStoreComponent.cs:829-866`).
  * On an alert of type `SetProfile`, `CheckInventory(isInit:false)` runs again (`:957-970`).
  * The CRM component is `GameIapStoreComponent` (`DefaultCrm.tx` `8/56e9861.f`). It does not override these methods.
* `CheckInventory` (`:1243-1306`):
  * It loads storage `profiles/<cred>/myprofile/inventory` (`OP/StorageApi.cs:98-111`).
  * For each key it computes `num = (int)(long)((JValue)v).Value`. **A non-integer JSON value (1.0, "1", an object)
    throws `InvalidCastException`, which is rethrown.**
  * It diffs against `CrmStandardData.Inventory`, which is persisted in the profile, writes `inventory[key]=num`,
    and raises `PurchaseCompleted` through `PendingPurchaseCompletedArgs`.
* The handler `PlayerProfileComponent.IapStore_PurchaseCompleted` (`GM/PlayerProfileComponent.cs:6055-6145`) is
  subscribed in `InitIapAsync` (`:2643-2653`), on the server only (`:2513-2516`).
  * It iterates `e.Diff.OrderBy(MercSkin* ? 1 : 0)` (`:6089`). This is a stable sort, so every other key keeps
    its JSON order.
  * Each entry runs `CreateRewardItem(key, delta)` → `ClaimRewardOfType(force:true)` (`:6133`), which is an
    immediate `Claim` (`:5746-5770`).
* **Ordering trap.** `MercAccessory_*`, `WeaponCamo_*`, `MercWeapon_*` and `MercSkin_*` are unavailable for locked
  mercs (the `IsAvailable` methods in `GM/*RewardItem.cs`), so they are turned into alternate rewards. The ledger is
  already updated by then, so the item is **never retried**. Put `UnlockMerc_ALL_MAX` first in the JSON object.
* **Subscription race [L].** The component registers in `base.InitAsync` before `CheckInventory`. The profile's
  handler is attached through an awaited continuation. If the diff fires before the handler is attached, the grant
  is consumed and lost. The network load of the `inventory` section makes this unlikely, but only a device test
  can settle it.
* The stub can drive the diff in batches without a relogin by writing the section and emitting `SetProfile` alerts.

---

## (d) Does `offline_store` make everything obtainable?

The `offline_store` → UI consumers are `GM/GameOfflineStore.cs`, `GM/GameUnifiedStoreComponent.cs` and
`GM/MercsComponent.cs`:

| Category | `offline_store` consumer | Local (no server) progression sources found in `MCVS_Meta` | Verdict |
|---|---|---|---|
| Mercs (21) | `UnlockMerc_<id>`: price on the merc detail screen (`GM/MercsComponent.cs:1426-1437`), `BuyMercRequest` takes any `CurrencyType` (`GM/GameOfflineStore.cs:1439-1493`). `#LOCKED`/`locked_*` hide it (`:819-853`). The shop-hub tile `AGENTSHOP` has `Displayed=false` (`ShopMenuElements`). | main loot crate `Crate_Base_Shop`→`Crate_Basic_Shop` (random merc by rarity; bought with Credits, 100 per crate), rank-up rewards | **OK** |
| Skins (about 42 non-default) | `MercSkin_<m>_<s>` in the skins shop; `#IAP` hides it (`:324-366,855-895`) | rank-up 1, event crates | **OK** via `offline_store` |
| Signature avatars (1518) and backgrounds (35) | `KillSignature_Avatar/Background_<id>` in the signature shop (`:937-995`, `BuyKillsignatureRequest` `:1330-1383`, no Purchasable or Unlockable check) | mastery 42, rank-up 3, start | **OK** (1,553 entries) |
| Signature sounds (5) | **none**: the shop lists only avatar and background | start (`KillSignature_Sound_1`) only | only via a generic item, cheat, coupon or inventory |
| Weapons (13 alternates) | **Dead code.** `MercsComponent` (`:1857-1880`) looks for `"weaponId"` in `GetSkinObject` output, but `GetSkinObject` (`GM/GameOfflineStore.cs:396-472`) never sets that key. | **none.** No local reward table references `MercWeapon*`/`MercWeaponFragment*`. Locked weapons say "FindInLootCrates". | **not obtainable locally** |
| Camos (4,409 non-Kayac merc×camo pairs) | none | `DailyShop_Cosmetics` 570 pairs (HC, 3 random slots per day), mastery 21, crates `WeaponCamo_Random_<id>` ×3, VIP `WeaponCamo_Random_Random`, event crates 50 ids (needs TLE) | **mostly not obtainable** (about 13% covered) |
| Accessories (5,208 non-Kayac merc×acc pairs) | none | daily shop 570, mastery 21, crates 2 random, VIP random | **mostly not obtainable** |
| Mods | none (`Mod_*` is also hidden in generic tabs, because `ModRewardItem.IsAvailable` returns false for every **unlocked** merc: `GM/ModRewardItem.cs:57-65`) | main loot crate `Mod_Tiers1..3`, `Crate_MOD_*` (daily-shop crates), rank-up | T1-T3 OK; T4/Super/Perk only from crates that need HC or a TLE |
| Gear and gear slots | none | loot crates, mastery `Gear_Random_N_Random` + 56 `GearSlot_*` | OK by progression |

* **Generic channel.** An `offline_store` item with category `#VersusCoins` becomes a shop-hub item of type
  "VersusCoins" (`GM/GameUnifiedStoreComponent.cs:251-268,306`).
  * It is shown only if `Content[0].IsAvailable(profile)` (`:472`).
  * It is serialised generically (`GM/UnifiedStoreItemSerializerEx.cs:31-101`).
  * It is bought through `BuyItemRequest`, which claims **any** reward string or opens a crate
    (`GM/GameOfflineStore.cs:1129-1186`).
  * So weapons, sounds, crates and per-merc random camos can be sold there. Whether the SWF's VersusCoins tab
    renders those reward types is **unverified**.
* `_OverrideTables` can only rewrite **existing** rows (`EN/Database.cs:154-158` skips missing rows). For example,
  it can repoint or reprice the 1,140 `DailyShop_Cosmetics` rows. It cannot add rows.
* The shipped `Config/crmpreconfiguration.json` (`b/de76cd6.f`) contains an old catalog:
  * 12 mercs priced in obsolete `AgentsCurrency` or HC, 3 skins, 77 avatars/backgrounds, 3 SoftCurrency packs
  * `_OverrideTables` with every cell empty
  * `AgeGateParams.Ignore:false`

  It is a template, not a full catalog.
* **"Soft currency" is ambiguous.** `CurrencyType.SoftCurrency` is not earned by any reward table; it is only sold
  for HC. The earn rates:
  * HC from progression is **finite**: about 1,425 from all rank-ups.
  * `VersusCoins` is repeatable: 50 per win, daily cap 1,000-1,200 or more (`VersusCoinsMatchRewards`), plus
    10,325 from mastery and 16,175 from rank-ups.
  * Price progression items in **VersusCoins**.

---

## Recommended design

**User-facing "Unlock all" toggle, no patch.** This is a launcher or stub setting that makes the stub write the
storage `inventory` section and send a `SetProfile` alert, or wait for the next login. Order the keys like this:

1. `{"UnlockMerc_ALL_MAX":1, ...}`
2. currencies, if wanted (integers)
3. `MercWeapon_<m>_<w>` (13)
4. `GearSlot_<m>_<slot>` (126)
5. `KillSignature_*` (1,558)
6. `WeaponCamo_<m>_<c>` (4,409)
7. `MercAccessory_<m>_<a>` (5,208)
8. `MercSkin_*`, which are processed last anyway

All values must be integers. Push them in batches of a few hundred keys per alert to keep each reward popup small.

Why this option:

* it is server-side
* it is saved by the normal TS saves
* it needs no UI navigation
* it has none of the developer side effects

It is one-way, so the stub should back up the save before enabling it.

**Alternatives:**

* A coupon such as `UNLOCKALL` in the same order (needs `RedeemCoupons` on, gives one inbox message, works once
  per profile).
* An "advanced/dev" switch that leaves `IsDevelopment` true, combined with the 6-tap gesture.

**Default for normal players:** the stub must serve `_ServerConfig.default.IsDevelopment:false`. The P2P host must do
the same, otherwise loopback, 10.x and "sticky" guests get the menu. If an IL patch becomes acceptable, report 03's
`OnProfileLoaded` hook that calls the cheat helpers is still the cleanest option: the same result in a handful of
calls, with no 11k-item reward lists.

**Everything obtainable through progression (CRM plus events stub):**

1. Generate an `offline_store` with these entries, priced in `VersusCoins`, with no `#LOCKED` or `#IAP`:
   * `UnlockMerc_1..21`
   * every non-default `MercSkin_<m>_<s>`
   * every `KillSignature_Avatar_*` and `KillSignature_Background_*`
2. Add `#VersusCoins` generic items for the things that have no screen:
   * `MercWeapon_<m>_<w>`, `KillSignature_Sound_2..5`
   * crates (`Crate_MOD_T*`, `Crate_MOD_Super`, `Crate_Perk_Random`, `Crate_GearsTier*_Random`)
   * per-merc "mystery" items for camos and accessories. `WeaponCamo_*` and `MercAccessory_*` with a `Random` part
     resolve to an **unowned** item (`GM/WeaponCamoRewardItem.cs:101-140`, `GM/MercAccessoryRewardItem.cs:96-126`).
   * Avoid `Mod_*`, which is hidden because of the `IsAvailable` bug. Avoid `MercAccessory_Random_*`, because
     `IsMercUnlocked("Random")` is false, so it is hidden too.
3. Alternatively, or as well, use `_OverrideTables` on `DailyShop_Cosmetics` to change `Type` to `VersusCoins` and
   to rotate the `Reward` values over the full catalog. Or serve TLE `CustomBundleEvent`/`ShopCrateV2Event` from
   the events stub. Bundles are HC-only (`GM/GameUnifiedStoreComponent.cs:200-210`).

---

## Remaining risks (need a device test)

* The 6-tap gesture registers on Android touch, and `menu_cheats` actually opens.
* The VersusCoins hub tab renders arbitrary reward types: weapons, camos, crates, sounds.
* The inventory-diff subscription race, and UI/performance with thousands of rewards in one diff or one inbox message.
* The TS sees `127.0.0.1` as IPv4 under Mono on Android, so `IsDevelopper` is true in the handshake reply.
* The 1 s `WaitForCRM` window: if the stub's `configs/users/me/game` for the super session is slow, the default
  `IsDevelopment=true` leaks into that session.
* The `DailyShop_Cosmetics.Type` column really is the price currency.
