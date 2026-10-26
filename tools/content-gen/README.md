# tools/content-gen (owner: CONTENT)

Generates our own content (store catalogue, prices, unlock lists, coupon codes, economy tuning, the Cosmetic crate)
from the player's decoded data pack into `server/Content/`, where the build compiles it into `McvsOffline.Server.dll`
(see `server/Content/README.md`). Python 3 standard library only.

```sh
# regenerate server/Content from the decoded pack (tools/datapack output) and pricing.json
python3 -I tools/content-gen/content_gen.py --decoded /home/user/mcvs-work/datapack-decoded

# verify that the committed files are up to date (non-zero exit when stale; also run by dotnet test)
python3 -I tools/content-gen/content_gen.py --decoded /home/user/mcvs-work/datapack-decoded --check

# unit tests on a synthetic catalogue (no game data needed; also run by dotnet test), plus the collector's exact
# counts on the decoded pack when it is there ($MCVS_DATAPACK_DECODED or the default; skipped otherwise)
python3 -I tools/content-gen/test_content_gen.py
```

`--decoded` defaults to `$MCVS_DATAPACK_DECODED` or `/home/user/mcvs-work/datapack-decoded`; `--pricing` to
`tools/content-gen/pricing.json`; `--liveops` to `tools/content-gen/liveops_tuning.json`; `--collector` to
`tools/content-gen/collector_tuning.json`; `--out` to `server/Content`.

Warnings (printed, exit code 0): `collector.since` in `liveops_tuning.json` is less than 7 days after the generation
time. Set it to the first Monday 00:00 UTC at least 7 days after the build is published; never bump `revision` for the
collector (docs/design/economy-collector.md §3.3).

## Inputs

| File | Used for |
|---|---|
| `<decoded>/_catalog.json` | mercs, skins, weapons, camos, accessories, kill signatures, gear slots, crates, VIP cards, divisions, player levels (ids, rarities, flags) |
| `<decoded>/_tables.json` | field names of the design tables the economy multipliers patch (validated before use) |
| `<decoded>/Design/MCVS_Meta.xlsx` | index column of `AgentsMasteryXP` (ids only); the collector's rules: signature `Asset`/`Unlockable`, skin `Locked`, jersey and camo pools, `LootCrates` and crate content rows, `PVPDivisionRewardsType`, `PlayerLevelXP` levels (ids and flags read, ids written) |
| `<decoded>/Config/game_object.json` (optional) | the live ecomm ids (ids only): an item without one and without a shipped source is "unreleased" |
| `<decoded>/names.json` (optional) | the data pack's file list: an unreleased item is only offered when its asset is on disk (without it, none is) |
| `pricing.json` | **our** tuning: every price, pack sizes, which crates the hub sells, hub order |
| `liveops_tuning.json` | **our** live-ops tuning: quest/event objectives, goals, rewards, weekly modifiers, rival targets (docs/LIVEOPS.md); the `collector` block (the Cosmetic crates of the daily bonus, weekly milestones and final, and `since`) |
| `collector_tuning.json` | **our** Cosmetic crate rules (docs/design/economy-collector.md §1): the crate, the Kayac rule, reserved season emblems, the explicit sound/skin/hidden-jersey/agent rows, the exhausted reward, the row order, season tiers and emblem numerals |
| `<decoded>/Localization/*_en.json` (optional) | the localization ids (ids only) the live-ops titles refer to |

## Outputs (`server/Content/`)

| File | Content |
|---|---|
| `offline_store.json` | CRM `offline_store`: every merc, skin, kill signature, weapon, gear slot, mod tier, sound, crate and VIP card priced in VersusCoins, plus per-merc "mystery" camos/accessories and 10-packs |
| `unlocks.json` | reward-id groups, ids compressed as `Prefix_{2,4-231}`: the full unlock (`"order"`), the opt-in paid/unreleased groups (not in `"order"`), and what was excluded and why |
| `coupons.json` | built-in coupon codes (UNLOCKALL and the split codes, UNLOCKPAID*, UNLOCKUNRELEASED) referring to unlock groups |
| `economy.json` | `_OverrideTables` targets for the settings.json economy multipliers, and a cost summary |
| `crm_overlay.json` | the static CRM game parameters we always send |
| `liveops.json` | the live-ops catalogue: `liveops_tuning.json` with every crate, table field, image and loc id checked against the player's data and the id lists expanded (divisions, levels, event crates, season-reward ranks); the `collector` block with `since` normalised (a Monday 00:00 UTC of the default season grid) |
| `collector.json` | the Cosmetic crate (docs/design/economy-collector.md §3.4): its 39 content rows, the avatar/background sets the CRM `Unlockable` cells need (no-source base, drawable, unlock, Kayac lock, reserved emblems), sounds, per-agent camo pools, the jersey pool, the season tier crates and emblems, levels, a count summary. Checked: the crate and the 35 tier crates exist, every row passes the preview-safety rule (§2.3.1), Kayac and reserved emblems never in the base set, per-agent pools equal the unlock groups, placement row 1 is the only weighted row |
| `manifest.json` | what was generated from what |

Only generated item ids, prices and our own tuning are written: no Gameloft text and no table values. The
output is deterministic (stable order, one store entry per line, no timestamps) so diffs stay reviewable.

The economy design, the earn rates the prices are balanced against, and how to tune it are in
[docs/ECONOMY.md](../../docs/ECONOMY.md); the CRM/coupon protocol is in
[docs/protocol/content.md](../../docs/protocol/content.md).
