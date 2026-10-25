# tools/content-gen (owner: CONTENT)

Generates our own content (store catalogue, prices, unlock lists, coupon codes, economy tuning) from the player's
decoded data pack into `server/Content/`, where the build compiles it into `McvsOffline.Server.dll`
(see `server/Content/README.md`). Python 3 standard library only.

```sh
# regenerate server/Content from the decoded pack (tools/datapack output) and pricing.json
python3 -I tools/content-gen/content_gen.py --decoded /home/user/mcvs-work/datapack-decoded

# verify that the committed files are up to date (non-zero exit when stale; also run by dotnet test)
python3 -I tools/content-gen/content_gen.py --decoded /home/user/mcvs-work/datapack-decoded --check

# unit tests on a synthetic catalogue (no game data needed; also run by dotnet test)
python3 -I tools/content-gen/test_content_gen.py
```

`--decoded` defaults to `$MCVS_DATAPACK_DECODED` or `/home/user/mcvs-work/datapack-decoded`; `--pricing` to
`tools/content-gen/pricing.json`; `--out` to `server/Content`.

## Inputs

| File | Used for |
|---|---|
| `<decoded>/_catalog.json` | mercs, skins, weapons, camos, accessories, kill signatures, gear slots, crates, VIP cards, divisions, player levels (ids, rarities, flags) |
| `<decoded>/_tables.json` | field names of the design tables the economy multipliers patch (validated before use) |
| `<decoded>/Design/MCVS_Meta.xlsx` (optional) | index column of `AgentsMasteryXP` (ids only) |
| `<decoded>/Config/game_object.json` (optional) | the live ecomm ids (ids only): an item without one and without a shipped source is "unreleased" |
| `<decoded>/names.json` (optional) | the data pack's file list: an unreleased item is only offered when its asset is on disk (without it, none is) |
| `pricing.json` | **our** tuning: every price, pack sizes, which crates the hub sells, hub order |

## Outputs (`server/Content/`)

| File | Content |
|---|---|
| `offline_store.json` | CRM `offline_store`: every merc, skin, kill signature, weapon, gear slot, mod tier, sound, crate and VIP card priced in VersusCoins, plus per-merc "mystery" camos/accessories and 10-packs |
| `unlocks.json` | reward-id groups, ids compressed as `Prefix_{2,4-231}`: the full unlock (`"order"`), the opt-in paid/unreleased groups (not in `"order"`), and what was excluded and why |
| `coupons.json` | built-in coupon codes (UNLOCKALL and the split codes, UNLOCKPAID*, UNLOCKUNRELEASED) referring to unlock groups |
| `economy.json` | `_OverrideTables` targets for the settings.json economy multipliers, and a cost summary |
| `crm_overlay.json` | the static CRM game parameters we always send |
| `manifest.json` | what was generated from what |

Only generated item ids, prices and our own tuning are written: no Gameloft text and no table values. The
output is deterministic (stable order, one store entry per line, no timestamps) so diffs stay reviewable.

The economy design, the earn rates the prices are balanced against, and how to tune it are in
[docs/ECONOMY.md](../../docs/ECONOMY.md); the CRM/coupon protocol is in
[docs/protocol/content.md](../../docs/protocol/content.md).
