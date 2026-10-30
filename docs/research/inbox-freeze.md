# Inbox freeze after some redeem codes

The investigation behind build 17 (docs/STATUS.md "Build 17"). It was made on build 15 (`wip/pc-port` at `feb1684`), so
its file:line references are those of that commit, and its predictions per code are derived from the code, not measured
on a device: the only observations were the owner's report and WR2's 180 s freeze with stacked codes on PC (§2 C1, 6).
What build 17 did with it: F1 (every `UNLOCK*` code through the inventory ledger, `UnlockGranter.GrantKeys`; an inbox
code above 300 rewards is refused, F3) and F2 (the `inbox` Game.dll patch, PC and Android); see
[protocol/unlocks.md](../protocol/unlocks.md) "Unlock codes", [protocol/content.md](../protocol/content.md) "Coupons"
and [design/pc-port.md](../design/pc-port.md) §8.1.

Owner report: "the game simply freezes when i go to the inbox but only after redeeming specific codes not all of them".

Path shorthands used below:
- PCD = an ILSpy decompile of the Steam 1.17.30 `Game.dll` (from `MCVS_PC_REFS`; not kept in the repo or in `/home/user/mcvs-work`)
- ENG = the same of the Steam `Gameloft.Engine.dll`
- AND = `/home/user/mcvs-work/decompiled/Game/Game` (Android 1.17.32)
- AS = the `UI.swf` ActionScript decompiled from the Android data pack (`scripts/`). That the PC SWF is the same is [G].
- WR2 = `/home/user/mcvs-work/pc/wr2-results/logs`
- OUT = the coupon payloads of build 15 measured on the standalone server (`GET coupons/{cid}/{code}` per code; the
  counts are in the table of §2 and in `server/Content/coupons.json`)

Guesses are marked [G].

**Short answer.** A redeem code turns into ONE inbox message that holds every reward of the code. Opening the inbox rebuilds every reward of every unclaimed coupon message synchronously on the game thread: C# lookups, then several deep copies into Flash, then one Flash card per reward. While the inbox stays open, the C# rebuild repeats every 10th wall-clock second.

The small codes hold 6–150 rewards and the big ones 1,549–11,368. That size is the whole difference between codes that freeze and codes that don't. The 'signatures' patch does not touch this path. The 'store' patch, now really loaded, removed the old crash on UNLOCKALL and UNLOCKSIGNATURES, so those messages are now built in full and freeze instead of crashing.

---

## 1. Ask the owner (5 items) and what to do right now

**Questions and checks**

1. **Which codes, in what order, and which were claimed?** List every code redeemed on this profile (including numbered repeats like UNLOCKALL2, or the same code in a different letter case). Say which ones you pressed **Claim** on. Which code was the last one before an inbox visit that froze, and which code was last before a visit that did not freeze? Important: once one big message is sitting unclaimed in the inbox, every later visit freezes, even after a small code. So "the code I just redeemed" may not be the one that causes it.
2. **PC or phone, and how long did you wait?** Try once and leave it for 5 minutes without touching anything. Does the inbox appear at some point? On PC, does the window stay frozen or does it close by itself?
3. **If it comes back:** while the inbox stays open, does it freeze again about every 10 seconds (at :00, :10, :20 … on the PC clock)? Is it smooth once you leave the inbox?
4. **Send the logs from the run that froze:**
   - `bootstrap.log`: the `[boot] assemblies:` / `patches:` lines, and every `[watchdog] STALL` line around the freeze.
   - `game.log`: the last ~50 lines before the freeze.
   - `server.log`: the `GET /coupons/...` lines, and the Content-Length of `POST /profiles/me/myprofile/_mcvs.gameProfile`. That is about 740 B per unclaimed reward, so it tells us how many rewards are in the inbox.
   - If the kit is used: run `watch` during the freeze. It writes a minidump on a STALL ≥ 60 s plus telemetry (virtual MB). Then run `collect`.
   - On a phone: the logcat watchdog thread dump.
5. **Control test** (on a backup or fresh profile): redeem `UNLOCKSKINS`, open the inbox, then claim. Expect no freeze. Then redeem `UNLOCKPAIDCAMOS` and open the inbox. Expect a stall. That confirms the cause in a few minutes.

**Workaround now, no new build needed**

- **Do not open the inbox** while a big coupon message is unclaimed.
- **Best way out of a stuck profile:** restore a save from before the redeem on the settings page (Saves). This works only if a backup from that moment exists [G]. Otherwise the coupon message expires 7 days after the redeem: the expiry is set at PCD/PlayerProfileComponent.cs:3617 and filtered at PCD/InboxComponent.cs:474.
- **With a single medium code** (UNLOCKPAIDCAMOS, UNLOCKPAIDACCESSORIES): open the inbox once and wait until it draws (minutes). Then press Claim straight away, not on a :x0 second. Claiming removes the message (PCD/InboxComponent.cs:646-648). Expect one more stall: the claim builds a rewards popup with every reward in it (PCD/InboxComponent.cs:575-582; AS/menus/inbox/Inbox.as:654-670).
- **With UNLOCKALL or several big codes stacked,** the inbox will likely never become usable. Use the backup or wait for the expiry.
- **From now on:** use `"unlockAll": true` in settings.json instead of UNLOCKALL. It goes through the inventory ledger in 400-key batches and makes no inbox message (server/Services/Unlocks/UnlockGranter.cs:13-44). Otherwise use only the small codes (MERCS, WEAPONS, GEARSLOTS, SKINS, UNRELEASED). Claim each code before redeeming the next.

---

## 2. Surviving causes, ranked

### C1: Opening the inbox rebuilds every reward of every unclaimed coupon message, synchronously on the game thread (primary; high; PC and Android)

**1. The payload becomes one message.**
- The server answers only a flat `[{type, quantity:1}]` array (server/Services/Assets/AssetsService.cs:38-61; CouponCatalog.Resolve server/Services/Assets/CouponCatalog.cs:105-131; MaxRewards = 50000 at :30).
- The TS's RedeemCouponRemote → PushCouponDataToInbox puts all of them into ONE InboxMessage: From "Coupon", expiry UtcNow + 7 d, a new Guid (PCD/PlayerProfileComponent.cs:3608-3630, 3617).
- AddMessage inserts it at index 0 (PCD/PlayerProfileComponent.cs:3540-3546).
- Nothing else differs between codes. Measured payloads are in OUT/*.json; see the table below.

**2. The C# rebuild on open.**
- Inbox.onFocusIn → getListMsg → `Native.ToggleInboxMessages(GETINBOXMSG,true)` (AS/menus/inbox/Inbox.as:115-135).
- NativeToggleMessages → GetMessages → GetMessagesObject (PCD/InboxComponent.cs:371-390, 465-505). It calls GetObject for every unexpired message.
- GetObject (:664-713) puts `GenerateRewardsObjects(message, profile)` (:695, :715-718) into "RewardsObjects". That is `reward.GetObject(profile)` for every reward, with no cap and no cache.
- RewardItem.GetObject → GetRewardInfo calls GetName, GetImage and IsAvailable for each reward (PCD/RewardItem.cs:393-443).

**3. Cost per reward type** (this is what makes the "specific codes" split):
- **WeaponCamo** (4,409 in CAMOS/ALL, 2,684 in PAID*): MercsComponent.GetBuiltMerc in IsAvailable (PCD/WeaponCamoRewardItem.cs:83) and again in GetRewardInfo (:213), plus GetWeaponCamosIds, which builds a new list (:232). GetBuiltMerc is not memoised: it does Distinct().ToList() on the weapon lists and builds a whole Merc with mods, abilities and gear bonuses (PCD/MercsComponent.cs:528-720).
- **MercAccessory** (5,208 / 2,940): GetBuiltMerc (PCD/MercAccessoryRewardItem.cs:54, 209) plus GetAllAccessoriesForMerc / GetUnlockedAccessoriesForMerc. Each is a scan of the whole accessory table with an IsAccessoryHidden lookup per row (PCD/MercAccessoriesComponent.cs:100-139, 165-177). So the cost per reward is proportional to the table size.
- **KillSignature Avatar** (1,511): the 1,518-row signature list is built twice per reward, in IsAvailable → GetAvailableId (PCD/KillSignatureRewardItem.cs:113-139) and in GetImage (:141-162). GetSignatureAssetsList is ToList plus a flag pass (PCD/PlayerProfileComponent.cs:6814-6824). The 'signatures' patch only makes the Contains check a HashSet (patcher/Patches/SignaturesPatch.cs:8-26). With it, one list costs 0.34–2 ms at unlock-all scale (docs/STATUS.md:891-912), so 1,511 × 2 lists ≈ 1–6 s per pass.
- **UnlockMerc, ModTier, MercWeapon, GearSlot, MercSkin:** no table scans. The small codes cost milliseconds.

**4. Deep copies into Flash.**
- `UI.DispatchEvent(callback, new { messages })` wraps the anonymous object as a managed AS object (ENG/MenuComponent.cs:669-707; ENG/ASValue.cs:197-245).
- Every AS read of `_data.messages` goes through ASManagedObject.GetMember → `SetObject(value)` (ENG/ASManagedObject.cs:92-116). That deep-converts the whole message and reward list again through SetArray / SetDictionary → SetAsDictionary (ENG/ASValue.cs:58-133). There are roughly 17 SetDictionaryElement calls per reward [G: key count]. On PC each key is also UTF8-encoded into a fresh byte[] (ENG/ASValue.cs:598-620).
- On the first open, refreshInbox reads `_data.messages` at AS/menus/inbox/Inbox.as:152, 154, 155, and once per message at :164. That is 3 + M full deep copies of every reward of every message.

**5. Flash cards for the selected message.**
- `_selectedMsgIndex` defaults to 0 (Inbox.as:59), and the newest coupon sits at index 0. So refreshInbox → `mc_list.dataLength` (:175) → onItemSet (:268-275) → setMsgInfo → setRewards(RewardsObjects) (:427).
- setRewards (:545-580) builds, for every reward: one `lib_reward_inbox` ImgAndCost, `setup()` (type frame, text, ImageContainer.img → a new flash.display.Loader; AS/elements/ImgAndCost.as:40-87, AS/gluic/elements/ImageContainer.as:66-106, 207-214), addChild, and 2 trace() calls.
- This happens once per selection (see C2), inside the same synchronous native call.

**6. Evidence.**
- WR2 process 18:56 had a stock Game.dll and camo/accessory-only codes, so no Sound NRE was possible. UNLOCKPAID was unclaimed, then UNLOCKCAMOS (19:01:17) and UNLOCKPAIDCAMOS (19:01:41) were redeemed: 12,798 rewards.
- There was no STALL between the two redeems. Then STALL 5/15/60/180 s from about 19:01:59. The frame clock (totalTime) did not move for about 180 s, there was no work item and 0 requests were in flight. The process was killed at about 19:05:55 (WR2/bootstrap.log:1980-2026).
- That the inbox was opened is not logged, because opening it calls no server endpoint [G]. The timing fits.
- WR2 runs 2 and 3 record the inbox-open path by stack: ToggleInboxMessages → GenerateRewardsObjects (WR2/game.log:2525-2562, 3619-3656).
- On PC the game thread runs near 100 % CPU even in normal play, so "Running + CPU" is not diagnostic. Only "frame clock unchanged" is.

**Freeze kind.** One uninterrupted stall per open, lasting seconds to minutes depending on the total number of unclaimed rewards and their mix. WR2 shows ≥ 180 s for about 12.8k camo and accessory rewards on a LowEnd PC. It does not converge while the inbox stays open (C2). For UNLOCKALL or stacked codes it looks permanent.

**Corrections to the CHECKED inputs**
- **The Flash card build does NOT repeat on every refresh.** setMsgInfo rebuilds the rewards only when `_selectedMsgId != Id` (AS/menus/inbox/Inbox.as:331). It sets `_selectedMsgId` at :434, and only onFocusIn resets it, at :120. So on a refresh the else branch at :182-185 just loops over the ids. setRewards runs once per open or selection change, not every 10 s. The first H1 check said otherwise; the H2 (pcdiff) check is right.
- **Claim removes the message at PCD/InboxComponent.cs:646-648,** not at :675-680.

### C2: RefreshUI repeats the C# rebuild on every constant frame of every 10th second while the inbox is open (amplifier; high; both platforms)

- RefreshUI runs `if (refreshUIActive && DateTime.Now.Second % 10 == 0)`, then GetMessagesObject + DispatchEvent (PCD/InboxComponent.cs:351-369).
- `lastRefreshUIDone` is written at :361 and never read. The store guards with `lastRefreshUIDone.Second != DateTime.Now.Second` (GameOfflineStore.cs:274, GameOfflineStoreVisual.cs:587). The Android code is identical (AND/InboxComponent.cs:362-374).
- Constant frames run at 0.05 s, at most 4 per Update (Gameloft.Engine World.cs:1977-1978, 2310-2369).
- `refreshUIActive` is cleared only by onFocusOut (Inbox.as:137-142).
- Each refresh costs the full C# rebuild (C1 steps 2–3), plus one deep copy for the `_data.messages.length` comparison at Inbox.as:152, plus the setMsgInfo id loop. It does not rebuild the Flash cards (see the correction above).
- Effect: if one refresh takes T seconds, about T of every 10 s are frozen, and with T ≥ about 9–10 s it is practically continuous. Back or Esc only gets through in the gaps.
- It does not explain the very first stall (C1 does) and it does not split the codes by itself. It is the reason that "wait it out" does not work while the inbox stays open.
- Telling C1 from C2 in bootstrap.log: one STALL sequence that keeps growing (5 → 15 → 60 → 180 s) means one pass that never ends (C1). Short stalls that repeat on :x0 seconds mean C2.

### C3: Stacking, which explains why "specific codes" can look inconsistent (multiplier; high; both)

- GetMessagesObject builds every unexpired message (PCD/InboxComponent.cs:465-497).
- A coupon message lives until it is claimed or until its 7 days are up. It is saved with the profile and survives restarts: WR2/server.log has gameProfile saves that contain crm.CouponMessageTitle.
- So a small code redeemed after an unclaimed big one also freezes the inbox, and the freeze comes back on every visit for 7 days.
- Numbered repeats (CouponCatalog.cs:157-171) and case variants (Normalize, :174-177) each add another full message. WR2 had UNLOCKPAIDCAMOS + UNLOCKall + UNLOCKALL ≈ 25.4k rewards at once.

### C4: PC only, 32-bit address space pressure; GC churn, then OOM or a native AV (crash that looks like a freeze; medium; PC)

- ModernCombatVersus.exe is PE 0x14c with characteristics 0x102, no LARGEADDRESSAWARE, so it has 2 GB. The WR2 boot line also says "LAA no".
- Each pass allocates thousands of reward dictionaries and 3 + M deep native copies. The unclaimed RewardItems also live in the client World and the TS World, and they are serialized into 10–19 MB gameProfile saves (WR2/server.log POST sizes).
- WR2 during the 19:01–19:05 freeze: virtual 1,592 MB, peak 1,727 MB (84 %), private 1,001 MB rising about 0.6 MB/s. In the menu the same profile used 806–915 MB.
- WR2 C3 was an AccessViolation in ASValue::SetAsDictionary during a 103 s CPU-bound stall. The cause is unproven [G].
- Expect it mainly with UNLOCKALL or stacked codes. The symptom is a long freeze, then the window closes; look for WER 1000 / CLR20r3 and exception-*.txt "PROBABLY FATAL".

### C5: InboxMessages is a StreamCompare-synced property, so redeeming, reading, the expiry check and claiming re-serialize and resend the whole list (contributor; medium-low; both) [G on frequency]

- `[SyncReliable(StreamCompare)]` is at PCD/PlayerProfileComponent.cs:1030-1032. The serialize-and-compare is in ENG/SynchronizationComponent.cs:2251-2300.
- onFocusIn sends `ReadInboxMessage("")`, which runs CheckExpirationDates on the TS. setMsgInfo sends `ReadInboxMessage(id)` → MarkMessageAsRead (Inbox.as:119, :439; PCD/InboxComponent.cs:392-428; PCD/PlayerProfileComponent.cs:3548-3557). An IsRead flip changes the list.
- WR2 run 3 had a 5–15 s stall right after the UNLOCKALL redeem, before the inbox was opened (WR2/bootstrap.log:271-302). The second busy thread in the STALL snapshots might be this or the GC [G].
- The fix is the same as for C1 (small messages). There is no sensible client patch.

### C6: The 'store' patch is not active, so the Sound-reward NRE fires on open (crash after about 10 s; low, conditional; mainly PC)

- Only UNLOCKALL and UNLOCKSIGNATURES contain KillSignature_Sound_2..5. Stock GetAvailableId leaves `list` null for Sound (PCD/KillSignatureRewardItem.cs:113-139). The exception is rethrown into native code by MenuComponent.OnInvokeDelegate (ENG/MenuComponent.cs:886-896), and the process dies.
- WR2 C1 and C2: WR2/game.log:2525-2560, 3619-3641, with a 9,985 ms STALL first (WR2/bootstrap.log:167).
- StorePatch S1 fixes it (patcher/Patches/StorePatch.cs:12-22, 84-124). Rule it out by reading `patches: ... store yes` in the boot line of the run that froze.

### C7: The claim path (low; both; it starts on Claim, not on entering the inbox)

- ClaimRewardsForMessageRequest claims every reward on the TS (PCD/InboxComponent.cs:621-651).
- The client then runs GetObject for every claimed reward and dispatches the whole list (:575-582). Flash shows POPUP_REWARDS with all of them (Inbox.as:654-670) and calls ToggleInboxMessages(true) again.
- The UNLOCKALL / MERCS ModTier rewards add XP, which brings level-ups and one save per level (docs/PLAYER-SETUP.md:220).

### C8: PC only, `KillSignatureRewardItem.IsRandom` is not null-safe (low; only custom coupons)

- PC: `Subtype2.ToLower()` (PCD/KillSignatureRewardItem.cs:21-32). Android has the `?.` null check.
- It only matters for a custom settings `assets.coupons` entry with `KillSignature_Avatar` or `KillSignature_Background` and no id, which IsKnownRewardId accepts (CrmDocumentValidator.cs:34-42). Every built-in code has ids.

### Predicted behaviour per code

Messages = new inbox messages per redeem. Counts are measured in OUT/*.json.

| Code | Msgs | Rewards | Types | Freezes on inbox open? |
|---|---|---|---|---|
| UNLOCKALL (and UNLOCKALL2, …, case variants) | 1 each | 11,368 | 20 UnlockMerc, 21 ModTier, 13 MercWeapon, 109 GearSlot, 1,511 Avatar, 34 Background, 4 Sound, 4,409 WeaponCamo, 5,208 MercAccessory, 39 MercSkin | **Yes, worst.** Practically permanent while on the inbox. PC: may end in OOM or AV (C4). Stock DLL: NRE crash instead (C6). |
| UNLOCKPAID | 1 | 5,644 | 20 UnlockMerc, 2,684 WeaponCamo, 2,940 MercAccessory | **Yes**, minutes, repeats every 10 s |
| UNLOCKACCESSORIES | 1 | 5,249 | 20 UnlockMerc, 21 ModTier, 5,208 MercAccessory | **Yes**, minutes (the accessory table scan per reward makes it heavy) |
| UNLOCKCAMOS | 1 | 4,450 | 20 UnlockMerc, 21 ModTier, 4,409 WeaponCamo | **Yes** |
| UNLOCKPAIDACCESSORIES | 1 | 2,960 | 20 UnlockMerc, 2,940 MercAccessory | **Yes**, shorter |
| UNLOCKPAIDCAMOS | 1 | 2,704 | 20 UnlockMerc, 2,684 WeaponCamo | **Yes**, shorter (WR2 run 15 together with others) |
| UNLOCKSIGNATURES | 1 | 1,549 | 1,511 Avatar, 34 Background, 4 Sound | **Likely**: a stall of seconds (about 1–6 s in C# alone with 'signatures', more once the avatars are owned, plus 1,549 cards), repeating every 10 s [G magnitude] |
| UNLOCKGEARSLOTS | 1 | 150 | 20 UnlockMerc, 21 ModTier, 109 GearSlot | No |
| UNLOCKSKINS | 1 | 80 | 20 UnlockMerc, 21 ModTier, 39 MercSkin | No |
| UNLOCKWEAPONS | 1 | 54 | 20 UnlockMerc, 21 ModTier, 13 MercWeapon | No |
| UNLOCKMERCS | 1 | 41 | 20 UnlockMerc, 21 ModTier | No |
| UNLOCKUNRELEASED | 1 | 6 | 6 Avatar (1513–1518) | No |
| MCVSOFFLINE | 0 | – | the server always answers 404 (AssetsService.cs:127-186) | No |
| GIFT-style | – | – | none defined by default (GIFT500 → 404); a custom small coupon is fine (but see C8) | No |
| **Any code while a big message above is still unclaimed** | +1 | – | – | **Yes**: the cost is inherited (C3) |

---

## 3. Fix design

Use F1 and F2 together:
- **F1** stops new giant messages from being created.
- **F2** caps the cost of whatever is already in a profile, including the owner's current stuck profile, and hardens both platforms.
- **F3** and **F4** are cheap guards.

### F1. Server: deliver the big codes through the inventory ledger, not the inbox (preferred; no client change)

**Change**
1. `server/Content/coupons.json`: add a per-code flag, for example `"delivery": "ledger"`, on UNLOCKALL, UNLOCKCAMOS, UNLOCKACCESSORIES, UNLOCKSIGNATURES, UNLOCKPAID, UNLOCKPAIDCAMOS and UNLOCKPAIDACCESSORIES. Otherwise make it the default for any code above about 300 rewards. CouponCatalog (server/Services/Assets/CouponCatalog.cs:105-131) exposes the flag and the expanded key list.
2. `server/Services/Assets/AssetsService.cs` Coupon (:38-61): for a ledger code, resolve the current player and queue a grant of exactly that code's keys. Return `[]` (HTTP 200). An empty 200 makes an empty, cheap coupon message, which the game marks as claimed (comment at :129-131). Claiming it does nothing and removes it (PCD/InboxComponent.cs:621-651). Do not leave the merc unlocks in the inbox: a camo claimed by the ledger diff while its merc is still locked becomes an alternate reward (UnlockGranter.cs:24-27). The grant itself writes the merc keys first.
3. `server/Services/Unlocks/UnlockGranter.cs`: add `GrantKeys(accountId, keys, reason)` next to Grant (:166-256). It reuses the same loop: merc keys first and alone, batches of unlocks.batchSize (400), a set_profile alert per batch, the WaitUntilListening stream wait, a backup before the first write, and idempotence (keys already at ≥ 1 are skipped). Track jobs per (account, code), separate from the unlockAll `done` set.

**Risks**
- **Which account to grant to.** The coupon request carries no access token (AssetsService.cs:140-144). Resolve the account the way the admin `CurrentPlayerResolver` does (server/Services/Admin/CurrentPlayerResolver.cs:33-73): game credential, then last session, then a single device account. In LAN mode a guest's redemption runs on the host's menu server (AssetsService.cs:135-136), so refuse ledger delivery there (answer 404 and log it) and whenever the account is ambiguous.
- **Repeats change meaning.** UNLOCKALL2 and similar become no-ops, because the keys are already in the ledger. Document this. The UNLOCKMERCS duplicate compensation stays, since the small codes keep inbox delivery.
- **Keys claimed earlier through an inbox message are not in the ledger,** so the TS diff would claim them again. The duplicate-claim behaviour for camos and accessories is [G]; test it once on a profile that already claimed UNLOCKCAMOS.
- **Reward popups.** There is one popup per 400-key batch. That is the same path as unlockAll, judged acceptable at docs/STATUS.md:1634 and helped by 'signatures' (STATUS.md:911-920).
- **The grant needs the player's alert stream.** It is open while the player is redeeming in the menu.

**Tests**
- `tests/McvsOffline.Tests/Content/AssetsTests.cs`: a ledger code returns `[]`; a small code returns its full list unchanged; MCVSOFFLINE still returns 404; in LAN mode or with an ambiguous account, a ledger code returns 404 and nothing is written.
- `tests/McvsOffline.Tests/Unlocks/UnlockGranterTests.cs`: `GrantKeys` for UNLOCKCAMOS writes the 41 merc keys first, alone, then 4,409 camo keys in batches of ≤ 400 with one set_profile alert each. A second run writes nothing. A merc key removed between batches is written again first.
- `tests/McvsOffline.Tests/Conformance/EndToEndConformanceTests.cs`, step 13 (:248-292), with real client code: redeem UNLOCKCAMOS through the real `AssetsService.RedeemCoupon` and `Json.LoadArray` and assert 0 rewards parsed. Then assert that the player's stored `inventory` section holds the camo keys. If the harness can run the TS's IapStoreComponent.CheckInventory, assert the diff claims them [G whether the harness covers it].
- Content guard test: every code in coupons.json that is not ledger-delivered expands to ≤ 300 rewards. This catches a future UNLOCK code that would recreate the bug.

### F2. Client IL patch `inbox` (Game.dll, PC and Android): bound the inbox cost no matter what is in the profile

This patch also rescues profiles that already hold giant messages, which F1 cannot do.

**(a) Display cap.** In InboxComponent.GetObject (PCD/InboxComponent.cs:664-713) / GenerateRewardsObjects (:715-718), on the display path (predicate == null): when `Rewards.Count > N` (N ≈ 30), apply `Take(N)` BEFORE `reward.GetObject`, so the C# cost is capped as well, not just the output. Add a `"RewardsTotal"` count; Flash ignores keys it doesn't read [G]. Keep every Gear reward in the capped list: onClaimMsg checks `_arMsgRewards[i].type == "Gear"` for the gear-inventory-full warning (AS/menus/inbox/Inbox.as:589-606). This one change caps the C# rebuild (C1-3), the 3 + M deep copies (C1-4), the Flash cards and Loaders (C1-5) and the per-refresh cost (C2).

**(b) Claim popup cap.** In ClaimRewardsForMessage (PCD/InboxComponent.cs:542 and :575-582), cap the `rewards` list sent to Flash in the same way. ClaimRewardsForMessageRequest still claims every item of `message.Rewards` (:630-646), so nothing granted is lost.

**(c) RefreshUI latch.** In RefreshUI (:357-369), require `(DateTime.Now - lastRefreshUIDone).TotalSeconds >= 9` in addition to `% 10 == 0`. `lastRefreshUIDone` starts at default(DateTime), so the first refresh still fires. That keeps one refresh per 10 s window, so the expiry countdown text stays current, instead of up to 20 per window.

**(d) Hardening, optional.** Wrap the per-reward selector in GenerateRewardsObjects in try/catch and drop a reward that throws from the display list. That covers future C6/C8-type throws. Also add a `?.` null guard in KillSignatureRewardItem.IsRandom (C8).

**Patch mechanics:** pattern it on patcher/Patches/StorePatch.cs. Fingerprint the shipped method bodies on PC 1.17.30 and Android 1.17.32. Add `inbox` to the default plan (patcher/Core/PatchPlan.cs:40-51; patcher/PatcherOptions.cs:49) and to the boot `patches:` line / ClientCapabilities (server/Core/ClientCapabilities.cs), so the settings page shows "NOT ACTIVE" if it does not load.

**Risks**
- The player sees only the first 30 rewards of a big message. That is cosmetic; the claim is complete.
- Android and PC fingerprints differ in small details: the PC GetMessagesObject has a NewsFeedData block (PCD/InboxComponent.cs:478-493).
- The PC Game.dll must really be loaded, which is the binding issue fixed in build 15.

**Tests** (in the style of tests/McvsOffline.Embed.Tests/Patcher/RealStorePatchTests.cs and RealSignaturesPatchTests.cs: the real Game.dll from MCVS_REFS, patched and unpatched in two load contexts, skipped without MCVS_REFS unless MCVS_REQUIRE_REAL_CLIENT=1)
- **Fingerprints:** the shipped GetObject, GenerateRewardsObjects, ClaimRewardsForMessage and RefreshUI have the verified fingerprints. A changed body refuses to patch.
- **Display cap:** build a real InboxMessage from OUT/UNLOCKALL.json through the real `RewardHelper.CreateRewardItem`. Call GetObject on an uninitialized InboxComponent with a fake profile and assert:
  - "RewardsObjects".Count == 30, and they equal the unpatched dictionaries for the same first 30 rewards. Use reward types whose GetObject runs on fake state, or a counting RewardItem subclass if GetObject is overridable [G].
  - reward.GetObject is called ≤ 30 (+ Gear) times patched, against 11,368 unpatched.
- **Claim is unaffected:** ClaimRewardsForMessageRequest on a fake server-side profile calls Claim() on all 11,368 rewards and removes the message. The dispatched list is ≤ 30.
- **Latch:** with refreshUIActive = true and lastRefreshUIDone = Now, 20 RefreshUI calls do no rebuild (count GetMessagesObject through the counting reward). With lastRefreshUIDone = Now − 10 s, exactly one rebuild runs.
- **Timing guard:** patched GetObject on the UNLOCKALL message finishes in under about 50 ms on net8, recorded in the test output, the way RealSignaturesPatchTests does.

### F3. Cheap server guard (a stopgap if F1 is delayed)
- Lower `MaxRewards` (server/Services/Assets/CouponCatalog.cs:30), or warn in the log and on the settings page when a code expands above about 300, so the owner is told before redeeming.
- Change docs/PLAYER-SETUP.md 'Unlock codes' (:213-227) to recommend `unlockAll` and warn that the big codes freeze the inbox until claimed.
- Splitting the codes into numbered parts (UNLOCKCAMOS1..N) is a poor substitute. UNLOCKALL would need about 38 codes, and every unclaimed part is still rebuilt on every open (C3).

### F4. Rescue for stuck profiles without F2
A settings-page action that removes unclaimed `crm.CouponMessageTitle` messages from the stored gameProfile. This depends on the game loading the profile from the server and on the dump format [G]. The pre-redeem backup restore is simpler.

### Rejected options
- **Splitting the payload into several smaller messages** (an IL patch in PushCouponDataToInbox, PCD/PlayerProfileComponent.cs:3608-3630). GetMessagesObject still builds every message on every open, so the total C# and copy cost stays. Only the Flash cards of the selected message shrink.
- **"Skip types the client cannot render".** Every shipped id renders. The PC and Android design tables are byte-identical, and unknown ids are already dropped (CouponCatalog.cs:133-140). The cost is the count, not the types.
- **Extending 'signatures'** to cache the avatar list per rebuild. This helps only UNLOCKSIGNATURES and UNLOCKALL, not the camo and accessory codes. F2(a) makes it unnecessary.

---

## 4. Ruled out

- **The 'signatures' patch as the fix.** It changes only GetSignatureAssetsList's Contains and GameOfflineStore.GetKillSignItems (patcher/Patches/SignaturesPatch.cs:8-26). Nothing in InboxComponent, WeaponCamo, MercAccessory or GetBuiltMerc is touched. WR2 run 15 froze for 180 s with camo-only codes.
- **The Sound NRE as today's cause** with the patched Game.dll. S1 handles Sound (patcher/Patches/StorePatch.cs:12-22, 84-124), and the camo, accessory and paid codes have no Sound rewards. It remains only as C6, if the patch is not loaded.
- **Per-type throws for the shipped ids** with the patched DLL:
  - GearSlot 1–6 parse correctly (GearData.cs:13-21).
  - GetMerc is non-null for every spec merc (MercsComponent.cs:2254-2255).
  - Unreleased avatars 1513–1518 only make IsAvailable false.
- **A swallowed exception retried every frame.** The open path rethrows into native code (ENG/MenuComponent.cs:886-896), and GenerateRewardsObjects has no catch-and-continue.
- **The server or the coupon endpoint.** Replies take 16–71 ms, even for UNLOCKALL at 494,956 B (the server log of the OUT measurement; WR2 22–61 ms). Every WR2 STALL had 0 requests in flight.
- **The Federation (Hermes) mailbox or its pagination.** Coupons never touch it (server/Services/Messaging/Mailboxes.cs:50-56). After all codes were redeemed, the data dir had no messages/ store.
- **Field or format differences between codes** (title, expiry, quantity type). The client builds the same message shape for every code (PCD/PlayerProfileComponent.cs:3608-3630). All quantities are the integer 1.
- **Item ids missing from the PC data.** The PC and Android design tables are byte-identical after GCBF decode.
- **The MCVS Offline newsfeed item.** It has no rewards and is not auto-selected, because coupons are inserted at index 0 and nothing is pinned.
- **settings.unlockAll batches as an inbox-specific cause.** They create no inbox message. They can cause general stalls (WR2 F4), but not one that starts on entering the inbox.
- **The redeem step itself as the main freeze.** WR2 run 15 had no STALL between two redeems 24 s apart. The redeem only dispatches COUPON_OP_RESULT (PCD/PlayerProfileComponent.cs:3673-3678). A shorter sync stall on the redeem is still possible (C5).
- **A PC-only bug in the inbox code.** The Android InboxComponent is identical (AND/InboxComponent.cs:362-374, 704-707). PC differs only in having no managed stacks in the watchdog and less memory headroom (C4).
- **game.log or trace() volume.** The 180 s WR2 freeze wrote no log lines, and AS traces do not reach game.log.
- **Watchdog, NativeCrashFilter or PcHooks lengthening the stall.** The PC watchdog only takes Process.Threads snapshots (EmbeddedHost.cs:728-731; GameThreadWatchdog.cs:255-271). The one exception is the kit `watch` full minidump at a STALL ≥ 60 s, which suspends the game while it writes.
- **A render-thread deadlock or the PC/Android DispatchEvent mutex difference.** RenderThreadEnabled is False on PC, and PC DispatchEvent takes no mutex.
- **MCVSOFFLINE and GIFT coupons.** MCVSOFFLINE always answers 404 and makes no message. No GIFT coupons are defined by default.
- **The small codes on their own** (6–150 rewards, no table scans). Their inbox cost is negligible, which matches "only specific codes".
- **"The Flash cards are rebuilt every 10 s".** The `_selectedMsgId` guard prevents it (AS/menus/inbox/Inbox.as:331, 434). Only the C# rebuild and one deep copy repeat (C2).
