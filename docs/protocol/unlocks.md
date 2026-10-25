# Unlock-all through the inventory ledger (`server/Services/Unlocks`)

Owner: core (lead). Glue between STORAGE (`IProfileAccess`), CONTENT (`server/Content/unlocks.json`) and MISC
(`alerts.push`). Evidence: [verify-C5](../research/verify-C5-unlock-all.md) (c) and its "Recommended design".

## What it does

With `"unlockAll": true` in `settings.json`, the first time in a server run that the game saves one of the
player's game-profile sections (`POST profiles/.../myprofile/_mcvs.gameProfile`, R4, or the `_mcvs.matchers` /
`_mcvs.snapshot` Sets, R6, of an existing profile's TS load; verify-C3 §2.3) **and** the stored profile already holds
a non-empty game profile and an `inventory` section, the server:

1. waits until the player has an alert stream that takes `set_profile` (the TS's AlertsComponent; at most
   `unlocks.streamWaitSeconds`, 60), then `unlocks.startDelaySeconds` (10) more;
2. backs up the profile (backup reason `unlockall`, taken with the game profile in it; the change is one-way);
3. writes `inventory.UnlockMerc_ALL_MAX = 1` on its own and pushes a `set_profile` alert to the player;
4. writes the remaining keys of the `unlocks.json` groups in their `order` (weapons, gear slots, signatures, camos,
   accessories, skins last) with count `1`, in batches of `unlocks.batchSize` (400), each followed by a
   `set_profile` alert, `unlocks.batchDelaySeconds` (10) apart. Before each batch the ledger is read again: if
   `UnlockMerc_ALL_MAX` has gone (a late R3 Set, a hand edit), it is written again first, alone, and lost keys are
   written again. Every merge also starts with the merc unlock at its current count (a no-op while it is there), so
   a section replaced between that read and the write still gets the merc unlock ahead of the batch.

An account that is not ready yet (status `waiting: ...`) is tried again at its next save; it is only marked done
once every key is in the ledger.

The TS's `IapStoreComponent` diffs the `inventory` section against what the profile already received
(`CrmStandardData.Inventory`) at start-up and on every `set_profile` alert, and claims each new key as a reward
(`EN/IapStoreComponent.cs CheckInventory`, `GM/PlayerProfileComponent.cs IapStore_PurchaseCompleted`). The result is
saved by the TS's normal saves. Nothing in the game is patched, and the developer menu stays off.

Keys already present with a count of at least 1 are never touched (idempotent; real IAP-ledger entries are kept). A
missed alert is harmless: the next login diffs whatever is left, still in order. Turning `unlockAll` off later keeps
everything that was granted.

## Why this order and these values

* Values must be JSON integers: the TS casts `(int)(long)JValue.Value` and an `InvalidCastException` aborts the diff.
* `MercAccessory_*`, `WeaponCamo_*`, `MercWeapon_*` and `MercSkin_*` claimed while their merc is locked become an
  alternate reward and are never retried, so `UnlockMerc_ALL_MAX` (every merc and every mod) goes first and alone.
* The TS processes keys in JSON order (except `MercSkin*`, last); STORAGE keeps insertion order in the section.
* **Never during a new player's first load.** `LoadSections` creates each absent section with an R3 Set of `{}`
  (`OP/ProfileSection.cs:232-239, 326-333`), inventory included, and a Set replaces the whole ledger. The four R3s
  are sent together and in any order, and the very first one already creates the profile, so "a profile exists" is
  not enough: an R3 `inventory` landing after the first grant would erase `UnlockMerc_ALL_MAX`, and every
  merc-dependent key claimed after it would become an alternate reward, for good (verify-C5 (c)). Requiring the game
  profile (written by R4, which follows `LoadSections`: `GM/ProfileManagerComponent.cs:891-894`) and the inventory
  section rules that out (review finding, October 2026).
* Waiting for the player's alert stream, then a few seconds, keeps the first alert from being lost and lets the TS's
  own start-up diff and its `PurchaseCompleted` handler settle first (the "subscription race").

## Settings

| Key | Default | Meaning |
|---|---|---|
| `unlockAll` | `false` | Turn the ledger unlock on |
| `unlocks.batchSize` | `400` | Keys per batch after the merc unlock (1-20000) |
| `unlocks.batchDelaySeconds` | `10` | Pause between batches (0-600) |
| `unlocks.streamWaitSeconds` | `60` | Longest wait for the player's alert stream before the first batch (0-600); without one the keys are written anyway and the TS diffs them at its next start |
| `unlocks.startDelaySeconds` | `10` | Pause after the stream is open, before the first batch (0-600) |

## Diagnostics (loopback only)

* `GET /_mcvs/unlocks` → `{enabled, keys, accounts: {<id>: "queued" | "waiting: ..." | "granting n/m" | "done (...)" | "failed: ..."}}`
* `POST /_mcvs/unlocks/grant` `account=<id or fed_id:id>` → queue a grant now (409 when `unlockAll` is off).

## Needs a device test

* The TS really receives `set_profile` on the user's alert stream and runs `CheckInventory` (MISC's Kairos stream on
  Mono).
* Reward popups and performance with batches of 400 keys; about 11.3k keys in total.
* Whether the GS (also a server World) runs its own `CheckInventory` for the player and double-grants (harmless for
  unlocks, but would show duplicate-compensation popups).
