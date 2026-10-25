#!/usr/bin/env python3
"""MCVS Offline content generator (owner: CONTENT).

Reads the player's own decoded data pack (``datapack-decoded/_catalog.json`` and ``_tables.json``, optionally
``Design/*.xlsx`` for index columns) plus our tuning in ``pricing.json`` and writes the generated content that
the server compiles into ``McvsOffline.Server.dll`` (``server/Content/``):

  offline_store.json  CRM ``offline_store`` priced in VersusCoins (merc/skin/kill-signature screens and the
                      generic ``#VersusCoins`` shop-hub tab), see docs/ECONOMY.md and verify-C5 (d)
  unlocks.json        ordered reward-id groups for a full unlock (coupon payloads), compressed as
                      ``Prefix_{2,4-231}`` ranges
  coupons.json        built-in coupon codes (UNLOCKALL first claims UnlockMerc_ALL_MAX, verify-C5 (b))
  economy.json        ``_OverrideTables`` targets for the settings.json economy multipliers + a cost summary
  crm_overlay.json    static CRM overlay (game parameters we always send)
  manifest.json       what was generated from what

Only generated item ids, prices and our own tuning are written: no Gameloft text, no table values.
Output is deterministic (stable order, no timestamps) so diffs stay reviewable.

Usage (Python 3 standard library only; run isolated):
  python3 -I tools/content-gen/content_gen.py --decoded /home/user/mcvs-work/datapack-decoded
  python3 -I tools/content-gen/content_gen.py --decoded ... --check     # verify server/Content is up to date
"""

import argparse
import json
import os
import sys

GENERATOR = "tools/content-gen/content_gen.py"
FORMAT = 1

# Reward types the client resolves as "<Type>RewardItem" (GM/RewardItem.cs Parse); anything else would make
# UnifiedStoreComponent throw while building the shop (ContentEntry rejects null prototypes).
KNOWN_REWARD_TYPES = {
    "UnlockMerc", "MercSkin", "KillSignature", "MercWeapon", "WeaponCamo", "MercAccessory", "GearSlot",
    "ModTier", "Crate", "SubscriptionCard", "VersusCoins", "HardCurrency", "Credits", "UpgradeTokens",
    "SeasonTickets", "Gear", "Mod", "PlayerXP",
}

# Offline-store names the client parses by substring (GM/GameOfflineStore.cs GetItemType/GetMercItem/GetSkinItem):
# our generated pack names must contain none of them.
RESERVED_NAME_PARTS = ("UnlockMerc", "MercSkin", "KillSignature", "SoftCurrency", "HardCurrency")

PACK_PREFIX = "MCVSPack"


class ContentError(Exception):
    pass


# ----------------------------------------------------------------------------------------------- helpers

def load_json(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def round_price(value, step):
    """Rounds to the nearest multiple of ``step`` (at least ``step``)."""
    step = max(1, int(step))
    return max(step, int(round(float(value) / step)) * step)


def compress_ranges(numbers):
    """[2, 4, 5, 6, 9] -> "2,4-6,9" (sorted, de-duplicated)."""
    values = sorted(set(int(n) for n in numbers))
    parts = []
    i = 0
    while i < len(values):
        j = i
        while j + 1 < len(values) and values[j + 1] == values[j] + 1:
            j += 1
        parts.append(str(values[i]) if i == j else "%d-%d" % (values[i], values[j]))
        i = j + 1
    return ",".join(parts)


def expand_pattern(pattern):
    """Inverse of the pattern form used in unlocks.json: "A_{1,3-4}" -> ["A_1", "A_3", "A_4"]."""
    start = pattern.find("{")
    if start < 0:
        return [pattern]
    end = pattern.index("}", start)
    prefix, suffix = pattern[:start], pattern[end + 1:]
    out = []
    for part in pattern[start + 1:end].split(","):
        if "-" in part:
            a, b = part.split("-", 1)
            out.extend("%s%d%s" % (prefix, n, suffix) for n in range(int(a), int(b) + 1))
        else:
            out.append(prefix + part + suffix)
    return out


def reward_type(item_id):
    return item_id.split("_", 1)[0]


def price_entry(vc, hc=None):
    prices = [{"currency": "VersusCoins", "price": int(vc)}]
    if hc is not None:
        prices.append({"currency": "HardCurrency", "price": int(hc)})
    # billing_methods, price and replaced_price must all be arrays (OP/OfflineStoreBillingMethod.cs iterates them
    # without null checks); never send "promotion": null (report 04 §15).
    return [{"name": "offline", "type": "offline", "price": prices, "replaced_price": []}]


def single_item(item_id, vc, hc=None, categories=None):
    return {
        "item": item_id,
        "quantity": 1,
        "category": categories or [],
        "hidden": False,
        "billing_methods": price_entry(vc, hc),
    }


def pack_item(name, content_id, size, vc):
    # A group with N identical "mystery" entries: BuyItemRequest claims every entry in turn, and each Random
    # reward resolves to a different still-locked item (GM/WeaponCamoRewardItem.cs OnPrepareToClaim).
    return {
        "group": name,
        "bundle": [{"item": content_id, "quantity": 1} for _ in range(size)],
        "category": ["#VersusCoins"],
        "hidden": False,
        "billing_methods": price_entry(vc),
    }


# ----------------------------------------------------------------------------------------------- catalogue view

class Catalog(object):
    """The parts of the decoded catalogue the economy needs, normalised to reward ids."""

    def __init__(self, catalog, tables, design, include_locked_skins=False):
        cats = catalog.get("categories")
        if not isinstance(cats, dict):
            raise ContentError("_catalog.json has no 'categories' object")
        self.game_version = catalog.get("game_version")
        self.tables = tables.get("databases", {})
        self.design = design or {}

        self.mercs = []          # (merc_id, rarity)
        for m in cats.get("mercs", []):
            mid = int(m["id"])
            if mid == 0 or str(m.get("displayed", "YES")).upper() != "YES":
                continue  # merc 0 is the placeholder; only displayed mercs are unlockable (GM/MercsComponent.cs:2443)
            self.mercs.append((mid, m.get("rarity")))
        self.merc_ids = [m for m, _ in self.mercs]
        if not self.mercs:
            raise ContentError("no displayed mercs in the catalogue")

        self.skins = []          # (item_id, crate_only)
        self.locked_skins = []
        for s in cats.get("skins", []):
            item = s.get("item_id")
            if not item or "locked_flag" not in s:
                continue  # default skins have no item id; entries without design-table rows are ecomm-only ids
            if "default" in (s.get("unlock_kinds") or []):
                continue
            if str(s.get("locked_flag")).lower() == "true" and not include_locked_skins:
                self.locked_skins.append(item)
                continue
            self.skins.append((item, s.get("exclusive") == "crate-only"))

        self.weapons = []        # (item_id, fragments)
        for w in cats.get("weapons", []):
            if str(w.get("is_default")).upper() == "YES":
                continue
            item = "MercWeapon_%d_%d" % (int(w["merc_id"]), int(w["id"]))
            self.weapons.append((item, int(w.get("fragments_required") or 0)))

        self.camos = {}          # merc_id -> [camo ids], design-table camos only, Kayac collaboration excluded
        for c in cats.get("camos", []):
            if "merc_id" not in c or c.get("kayac_only"):
                continue
            self.camos.setdefault(int(c["merc_id"]), []).append(int(c["camo_id"]))

        self.accessories = {}    # merc_id -> [accessory ids]
        for a in cats.get("accessories_jerseys", []):
            if a.get("kayac_only"):
                continue
            for item in a.get("item_ids", []):
                parts = item.split("_")
                self.accessories.setdefault(int(parts[1]), []).append(int(parts[2]))

        sig = cats.get("kill_signatures", {})
        self.avatars = [(int(x["id"]), x.get("rarity")) for x in sig.get("avatars", [])]
        self.backgrounds = [(int(x["id"]), str(x.get("purchasable")).lower() == "true") for x in sig.get("backgrounds", [])]
        self.sounds = [int(x["id"]) for x in sig.get("sounds", [])]
        # Starting-inventory signatures are owned by every profile: no shop entry needed for sounds, and they are
        # left out of the unlock groups (an owned reward would only turn into its alternate reward).
        self.default_signatures = set()
        for kind in ("avatars", "backgrounds", "sounds"):
            for x in sig.get(kind, []):
                if "starting-inventory" in (x.get("unlock_kinds") or []):
                    self.default_signatures.add(x["item_id"])
        self.default_sounds = [s for s in self.sounds if "KillSignature_Sound_%d" % s in self.default_signatures]

        self.gear_slots = []
        for g in cats.get("gear_slot_unlocks", []):
            item = g["item_id"]
            parts = item.split("_")
            if len(parts) == 3 and int(parts[1]) in self.merc_ids:
                self.gear_slots.append(item)

        self.crates = set(c["id"] for c in cats.get("crates", []))
        self.vip = set(v["item_id"] for v in cats.get("subscriptions_vip", []))
        self.divisions = [int(d["id"]) for d in cats.get("divisions", [])]
        self.player_levels = [int(l["level"]) for l in cats.get("player_levels", [])]

    def table_fields(self, db, table):
        t = self.tables.get("Design/%s.xlsx" % db, {}).get(table)
        return t["fields"] if t else None

    def index_values(self, db, table, index_field, fallback=None):
        """Index column values of a design table (ids only), from Design/<db>.xlsx when available."""
        d = self.design.get(db, {}).get(table)
        if d:
            col = d["Fields"].index(index_field)
            return [row[col] for row in d["Rows"]]
        return fallback


# ----------------------------------------------------------------------------------------------- generators

def build_offline_store(cat, pricing):
    step = pricing.get("roundTo", 5)
    hc_alt = pricing.get("hardCurrencyAlternative") or {}
    hc_categories = set(hc_alt.get("categories", []))
    vc_per_hc = float(hc_alt.get("versusCoinsPerHardCurrency", 10))
    hc_step = hc_alt.get("roundTo", 5)

    def hc_for(category, vc):
        if category not in hc_categories:
            return None
        return round_price(vc / vc_per_hc, hc_step)

    prices = []
    summary = {}

    def add(category, entry, vc):
        prices.append(entry)
        s = summary.setdefault(category, {"items": 0, "versusCoins": 0})
        s["items"] += 1
        s["versusCoins"] += int(vc)

    # Merc unlocks: priced by rarity, bought on the merc detail screen (GM/GameOfflineStore.cs BuyMercRequest takes
    # any currency listed in the first billing method).
    mp = pricing["mercs"]
    for mid, rarity in cat.mercs:
        vc = round_price(mp["byRarity"].get(rarity, mp.get("fallback", 6000)), step)
        add("mercs", single_item("UnlockMerc_%d" % mid, vc, hc_for("mercs", vc)), vc)

    # Skins: the skins shop lists every MercSkin_* item without an #IAP/#LOCKED category (GetMercsSkinsList).
    sp = pricing["skins"]
    for item, crate_only in cat.skins:
        vc = round_price(sp["crateOnly"] if crate_only else sp["price"], step)
        add("skins", single_item(item, vc, hc_for("skins", vc)), vc)

    # Kill signatures: the signature screen lists KillSignature_* items, priced by the first price (GetBuildItemData).
    ap = pricing["avatars"]
    for aid, rarity in cat.avatars:
        vc = round_price(ap["byRarity"].get(rarity, ap.get("fallback", 100)), step)
        add("avatars", single_item("KillSignature_Avatar_%d" % aid, vc), vc)
    bp = pricing["backgrounds"]
    for bid, purchasable in cat.backgrounds:
        vc = round_price(bp["purchasable"] if purchasable else bp["other"], step)
        add("backgrounds", single_item("KillSignature_Background_%d" % bid, vc), vc)

    # Everything without its own screen goes to the generic #VersusCoins shop-hub tab
    # (GM/GameUnifiedStoreComponent.cs:251-268). The hub hides an entry once its first reward is unavailable
    # (owned item, locked merc, empty crate), so lists shrink as the player progresses.
    hub = {}
    hub_cat = ["#VersusCoins"]

    hub["crates"] = []
    for crate_id, vc in pricing.get("crates", []):
        if crate_id not in cat.crates:
            raise ContentError("pricing.json lists unknown crate %s" % crate_id)
        hub["crates"].append((single_item(crate_id, round_price(vc, step), categories=hub_cat), round_price(vc, step)))

    wp = pricing["weapons"]
    hub["weapons"] = []
    for item, fragments in cat.weapons:
        vc = round_price(max(wp["minimum"], fragments * wp["perFragment"]), step)
        hub["weapons"].append((single_item(item, vc, categories=hub_cat), vc))

    def mystery(kind, ids_by_merc, reward_prefix, conf):
        out = []
        size = int(conf.get("packSize", 0))
        unit = round_price(conf["mystery"], step)
        for mid in cat.merc_ids:
            if not ids_by_merc.get(mid):
                continue
            content = "%s_%d_Random" % (reward_prefix, mid)
            out.append((single_item(content, unit, categories=hub_cat), unit))
            if size > 1:
                vc = round_price(unit * size * (1.0 - float(conf.get("packDiscount", 0))), step)
                out.append((pack_item("%s_%s_%d_%d" % (PACK_PREFIX, kind, mid, size), content, size, vc), vc))
        return out

    hub["camos"] = mystery("Camo", cat.camos, "WeaponCamo", pricing["camos"])
    hub["accessories"] = mystery("Accessory", cat.accessories, "MercAccessory", pricing["accessories"])

    gp = round_price(pricing["gearSlots"]["price"], step)
    hub["gearSlots"] = [(single_item(item, gp, categories=hub_cat), gp) for item in cat.gear_slots]

    mt = pricing["modTiers"]
    mtp = round_price(mt["price"], step)
    hub["modTiers"] = [(single_item("ModTier_%d_%d" % (mid, int(mt["tier"])), mtp, categories=hub_cat), mtp)
                       for mid in cat.merc_ids]

    snd = round_price(pricing["sounds"]["price"], step)
    hub["sounds"] = [(single_item("KillSignature_Sound_%d" % sid, snd, categories=hub_cat), snd)
                     for sid in cat.sounds if sid not in cat.default_sounds]

    hub["vip"] = []
    for vip_id, vc in pricing.get("vip", []):
        if vip_id not in cat.vip:
            raise ContentError("pricing.json lists unknown VIP card %s" % vip_id)
        hub["vip"].append((single_item(vip_id, round_price(vc, step), categories=hub_cat), round_price(vc, step)))

    order = pricing.get("hubOrder") or sorted(hub)
    if sorted(order) != sorted(hub):
        raise ContentError("pricing.json hubOrder must list exactly: %s" % ", ".join(sorted(hub)))
    for key in order:
        for entry, vc in hub[key]:
            add("hub." + key, entry, vc)

    validate_offline_store(prices)
    return {"prices": prices}, summary


def validate_offline_store(prices):
    names = set()
    for p in prices:
        name = p.get("group") or p.get("item")
        if not name or name in names:
            raise ContentError("missing or duplicate offline_store name %r" % name)
        names.add(name)
        if "group" in p:
            if any(part in name for part in RESERVED_NAME_PARTS):
                raise ContentError("pack name %s contains a substring the client parses" % name)
            contents = [b["item"] for b in p["bundle"]]
        else:
            contents = [p["item"]]
        for c in contents:
            if reward_type(c) not in KNOWN_REWARD_TYPES:
                raise ContentError("unknown reward type in %s" % c)
        bm = p.get("billing_methods")
        if not bm or not bm[0].get("price"):
            raise ContentError("%s has no price" % name)
        for price in bm[0]["price"]:
            if not isinstance(price["price"], int) or price["price"] <= 0:
                raise ContentError("%s has a non-positive or non-integer price" % name)


def build_unlocks(cat):
    """Ordered groups for a full unlock (verify-C5 recommended order). UnlockMerc_ALL_MAX must come first:
    skins, weapons, camos, accessories and gear slots of a locked merc are unavailable and would be converted to
    their alternate reward and lost (GM/RewardItem.cs Claim)."""
    groups = []
    groups.append({"id": "mercs", "items": ["UnlockMerc_ALL_MAX"],
                   "note": "every merc, plus every mod up to the MAX tier (GM/MercsComponent.cs:2400-2470)"})
    groups.append({"id": "weapons", "items": sorted((w for w, _ in cat.weapons), key=natural_key)})
    groups.append({"id": "gearSlots", "items": compress_items(cat.gear_slots)})
    sig = []
    sig.append("KillSignature_Avatar_{%s}" % compress_ranges(
        a for a, _ in cat.avatars if "KillSignature_Avatar_%d" % a not in cat.default_signatures))
    sig.append("KillSignature_Background_{%s}" % compress_ranges(
        b for b, _ in cat.backgrounds if "KillSignature_Background_%d" % b not in cat.default_signatures))
    sounds = [s for s in cat.sounds if s not in cat.default_sounds]
    if sounds:
        sig.append("KillSignature_Sound_{%s}" % compress_ranges(sounds))
    groups.append({"id": "signatures", "items": sig})
    groups.append({"id": "camos", "items": ["WeaponCamo_%d_{%s}" % (m, compress_ranges(cat.camos[m]))
                                            for m in cat.merc_ids if cat.camos.get(m)]})
    groups.append({"id": "accessories", "items": ["MercAccessory_%d_{%s}" % (m, compress_ranges(cat.accessories[m]))
                                                  for m in cat.merc_ids if cat.accessories.get(m)]})
    groups.append({"id": "skins", "items": sorted((s for s, _ in cat.skins), key=natural_key),
                   "note": "MercSkin_* last: the inventory diff processes them last too (GM/PlayerProfileComponent.cs:6089)"})
    counts = {}
    for g in groups:
        n = sum(len(expand_pattern(p)) for p in g["items"])
        g["count"] = n
        counts[g["id"]] = n
        for p in g["items"]:
            for item in expand_pattern(p)[:1]:
                if reward_type(item) not in KNOWN_REWARD_TYPES:
                    raise ContentError("unknown reward type in unlock group %s: %s" % (g["id"], item))
    return {"format": FORMAT, "order": [g["id"] for g in groups], "groups": groups}, counts


def natural_key(item_id):
    return [int(p) if p.isdigit() else p for p in item_id.split("_")]


def compress_items(items):
    """["GearSlot_1_1", "GearSlot_1_2", ...] -> ["GearSlot_1_{1-6}", ...] grouped by prefix, in id order."""
    by_prefix = {}
    order = []
    for item in sorted(items, key=natural_key):
        prefix, _, last = item.rpartition("_")
        if prefix not in by_prefix:
            by_prefix[prefix] = []
            order.append(prefix)
        by_prefix[prefix].append(int(last))
    return ["%s_{%s}" % (p, compress_ranges(by_prefix[p])) for p in order]


def build_coupons():
    merc_dependent = ["weapons", "gearSlots", "camos", "accessories", "skins"]
    codes = {
        "UNLOCKALL": {
            "groups": ["mercs", "weapons", "gearSlots", "signatures", "camos", "accessories", "skins"],
            "description": "Full unlock in one inbox message (about 11k rewards, claimed in this order)",
        },
        "UNLOCKMERCS": {"groups": ["mercs"], "description": "Every merc and every mod"},
        "UNLOCKSIGNATURES": {"groups": ["signatures"], "description": "Every kill-signature avatar, background and sound"},
    }
    names = {"weapons": "UNLOCKWEAPONS", "gearSlots": "UNLOCKGEARSLOTS", "camos": "UNLOCKCAMOS",
             "accessories": "UNLOCKACCESSORIES", "skins": "UNLOCKSKINS"}
    for g in merc_dependent:
        # The split codes start with the merc unlock too: a code is "claimed" before its rewards are processed,
        # so redeeming UNLOCKCAMOS before UNLOCKMERCS would otherwise lose every camo of a locked merc (verify-C5).
        codes[names[g]] = {"groups": ["mercs", g], "description": "Every merc, then all %s" % g}
    for c in codes.values():
        c["allowNumericSuffix"] = True
    ordered = {}
    for k in ["UNLOCKALL", "UNLOCKMERCS", "UNLOCKWEAPONS", "UNLOCKGEARSLOTS", "UNLOCKSIGNATURES", "UNLOCKCAMOS",
              "UNLOCKACCESSORIES", "UNLOCKSKINS"]:
        ordered[k] = codes[k]
    return {"format": FORMAT, "codes": ordered}


def build_economy(cat, store_summary, unlock_counts):
    def target(table, index, fields, cap_fields, ids, inverse=False):
        have = cat.table_fields("MCVS_Meta", table)
        if have is None:
            raise ContentError("_tables.json has no MCVS_Meta table %s" % table)
        for f in [index] + fields + cap_fields:
            if f not in have:
                raise ContentError("MCVS_Meta.%s has no field %s" % (table, f))
        t = {"database": "MCVS_Meta", "table": table, "index": index, "ids": ids, "fields": fields}
        if cap_fields:
            t["capFields"] = cap_fields
        if inverse:
            t["inverse"] = True
        return t

    mastery_rows = (cat.tables.get("Design/MCVS_Meta.xlsx", {}).get("AgentsMasteryXP") or {}).get("rows", 0)
    mastery_ids = cat.index_values("MCVS_Meta", "AgentsMasteryXP", "Level", list(range(1, mastery_rows + 1)))
    multipliers = {
        "credits": [target("CreditsCurrencyMatchRewards", "Division", ["VictoryAmount", "DefeatAmount", "MVPAmount"],
                           ["DailyCap"], cat.divisions)],
        "versusCoins": [target("VersusCoinsMatchRewards", "Division", ["VictoryAmount", "DefeatAmount", "MVPAmount"],
                               ["DailyCap"], cat.divisions)],
        # XP: the requirement columns are divided instead (same effect as earning more XP per match).
        "xp": [target("PlayerLevelXP", "Level", ["XPRequired"], [], cat.player_levels, inverse=True),
               target("AgentsMasteryXP", "Level", ["XPCost"], [], mastery_ids, inverse=True)],
    }
    summary = {"offlineStore": store_summary, "unlockGroups": unlock_counts,
               "catalogue": {"mercs": len(cat.mercs), "skins": len(cat.skins), "lockedSkinsExcluded": len(cat.locked_skins),
                             "weapons": len(cat.weapons), "avatars": len(cat.avatars), "backgrounds": len(cat.backgrounds),
                             "sounds": len(cat.sounds), "gearSlots": len(cat.gear_slots),
                             "camoPairs": sum(len(v) for v in cat.camos.values()),
                             "accessoryPairs": sum(len(v) for v in cat.accessories.values())}}
    return {"format": FORMAT, "multipliers": multipliers, "summary": summary}


def build_crm_overlay():
    """Game parameters we always send (settings-driven values are applied by CrmDocumentBuilder on top)."""
    return {
        "tags": [],
        "game": {
            "13yolimitationpopup": {"enabled": False},
            "AgeGateParams": {"Ignore": True},
            "ReviewPopupParams": {"Ignore": True},
            "CrashlyticsParams": {"Enable": False},
            "_OverrideTables": {
                # Shows Settings > Account > Redeem code (GM/PlayerProfileComponent.cs:4539; verify-C5 (b)).
                "MCVS_System": {"GameFeatures": {"Id@Enabled": {"RedeemCoupons": "1"}}},
            },
            "_ServerConfig": {
                "default": {
                    "IsDevelopment": False,
                    "DisableServerTracking": True,
                    "DisableServerTrackingKills": True,
                    "DisableServerTrackingCaptures": True,
                    "UnsupportedLinkFrom": [],
                    "UnsupportedLinkTo": [],
                },
            },
        },
        "iap": {"prices": []},
    }


# ----------------------------------------------------------------------------------------------- output

def dumps(value):
    """Deterministic JSON: objects in insertion order, every array of objects one element per line."""
    out = []
    _write(value, out, 0)
    return "".join(out) + "\n"


def _write(value, out, depth):
    pad = "  " * depth
    if isinstance(value, dict):
        if not value:
            out.append("{}")
            return
        out.append("{\n")
        items = list(value.items())
        for i, (k, v) in enumerate(items):
            out.append(pad + "  " + json.dumps(k) + ": ")
            _write(v, out, depth + 1)
            out.append(",\n" if i < len(items) - 1 else "\n")
        out.append(pad + "}")
    elif isinstance(value, list) and value and all(isinstance(v, dict) for v in value) and depth <= 2 and _big(value):
        out.append("[\n")
        for i, v in enumerate(value):
            out.append(pad + "  " + json.dumps(v, separators=(",", ":"), ensure_ascii=True))
            out.append(",\n" if i < len(value) - 1 else "\n")
        out.append(pad + "]")
    else:
        out.append(json.dumps(value, separators=(",", ":"), ensure_ascii=True))


def _big(items):
    return len(items) > 1 and len(json.dumps(items)) > 100


def generate(decoded_dir, pricing_path):
    catalog = load_json(os.path.join(decoded_dir, "_catalog.json"))
    tables = load_json(os.path.join(decoded_dir, "_tables.json"))
    design = {}
    design_dir = os.path.join(decoded_dir, "Design")
    for db in ("MCVS_Meta",):
        path = os.path.join(design_dir, db + ".xlsx")
        if os.path.exists(path):
            design[db] = load_json(path)
    pricing = load_json(pricing_path)
    cat = Catalog(catalog, tables, design, include_locked_skins=bool(pricing["skins"].get("includeLocked")))

    store, store_summary = build_offline_store(cat, pricing)
    unlocks, unlock_counts = build_unlocks(cat)
    coupons = build_coupons()
    economy = build_economy(cat, store_summary, unlock_counts)
    overlay = build_crm_overlay()

    files = {
        "offline_store.json": store,
        "unlocks.json": unlocks,
        "coupons.json": coupons,
        "economy.json": economy,
        "crm_overlay.json": overlay,
    }
    manifest = {
        "schema": FORMAT,
        "generator": GENERATOR,
        "source": {"game": "Modern Combat Versus", "version": cat.game_version,
                   "inputs": ["_catalog.json", "_tables.json"] + (["Design/MCVS_Meta.xlsx (index columns only)"] if design else []),
                   "pricing": "tools/content-gen/pricing.json"},
        "files": [{"name": name, "entries": _entries(name, data)} for name, data in sorted(files.items())],
    }
    files["manifest.json"] = manifest
    return {name: dumps(data) for name, data in files.items()}


def _entries(name, data):
    if name == "offline_store.json":
        return len(data["prices"])
    if name == "unlocks.json":
        return sum(g["count"] for g in data["groups"])
    if name == "coupons.json":
        return len(data["codes"])
    return None


def main(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.normpath(os.path.join(here, "..", ".."))
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--decoded", default=os.environ.get("MCVS_DATAPACK_DECODED", "/home/user/mcvs-work/datapack-decoded"),
                        help="decoded data pack (with _catalog.json and _tables.json)")
    parser.add_argument("--pricing", default=os.path.join(here, "pricing.json"))
    parser.add_argument("--out", default=os.path.join(repo, "server", "Content"))
    parser.add_argument("--check", action="store_true", help="only verify that --out is up to date")
    args = parser.parse_args(argv)

    try:
        outputs = generate(args.decoded, args.pricing)
    except (ContentError, KeyError, ValueError, OSError) as e:
        print("content-gen: error: %s" % e, file=sys.stderr)
        return 2

    stale = []
    for name in sorted(outputs):
        path = os.path.join(args.out, name)
        current = None
        if os.path.exists(path):
            with open(path, "r", encoding="utf-8") as f:
                current = f.read()
        if current == outputs[name]:
            continue
        stale.append(name)
        if not args.check:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(outputs[name])
    if args.check:
        if stale:
            print("content-gen: out of date: %s" % ", ".join(stale), file=sys.stderr)
            return 1
        print("content-gen: server/Content is up to date")
        return 0
    print("content-gen: wrote %s" % (", ".join(stale) if stale else "nothing (already up to date)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
