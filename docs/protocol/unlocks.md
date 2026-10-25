# Unlock-all through the inventory ledger (`server/Services/Unlocks`)

Owner: core (lead). Glue between STORAGE (`IProfileAccess`), CONTENT (`server/Content/unlocks.json`) and MISC
(`alerts.push`). Evidence: [verify-C5](../research/verify-C5-unlock-all.md) (c) and its "Recommended design".

## What it does

With `"unlockAll": true` in `settings.json`, the first time the game saves a player's game profile in a server
run (any `POST profiles/.../myprofile/_mcvs...` by the TS or GS), the server:

1. backs up the profile (backup reason `unlockall`; the change is one-way);
2. writes `inventory.UnlockMerc_ALL_MAX = 1` on its own and pushes a `set_profile` alert to the player;
3. writes the remaining keys of the `unlocks.json` groups in their `order` (weapons, gear slots, signatures, camos,
   accessories, skins last) with count `1`, in batches of `unlocks.batchSize` (400), each followed by a
   `set_profile` alert, `unlocks.batchDelaySeconds` (10) apart.

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
* Writing only after the TS has saved the game profile avoids the start-up "subscription race" (the diff firing
  before the profile's handler is attached).

## Settings

| Key | Default | Meaning |
|---|---|---|
| `unlockAll` | `false` | Turn the ledger unlock on |
| `unlocks.batchSize` | `400` | Keys per batch after the merc unlock (1-20000) |
| `unlocks.batchDelaySeconds` | `10` | Pause between batches (0-600) |

## Diagnostics (loopback only)

* `GET /_mcvs/unlocks` → `{enabled, keys, accounts: {<id>: "queued" | "granting n/m" | "done (...)" | "failed: ..."}}`
* `POST /_mcvs/unlocks/grant` `account=<id or fed_id:id>` → queue a grant now (409 when `unlockAll` is off).

## Needs a device test

* The TS really receives `set_profile` on the user's alert stream and runs `CheckInventory` (MISC's Kairos stream on
  Mono).
* Reward popups and performance with batches of 400 keys; about 11.3k keys in total.
* Whether the GS (also a server World) runs its own `CheckInventory` for the player and double-grants (harmless for
  unlocks, but would show duplicate-compensation popups).
