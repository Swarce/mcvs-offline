#!/usr/bin/env python3
"""MCVS Offline content generator (owner: CONTENT).

Reads the player's own decoded data pack (``datapack-decoded/_catalog.json`` and ``_tables.json``, optionally
``Design/*.xlsx`` for index columns) plus our tuning in ``pricing.json`` and writes the generated content that
the server compiles into ``McvsOffline.Server.dll`` (``server/Content/``):

  offline_store.json  CRM ``offline_store`` priced in VersusCoins (merc/skin/kill-signature screens and the
                      generic ``#VersusCoins`` shop-hub tab), see docs/ECONOMY.md and verify-C5 (d)
  unlocks.json        reward-id groups (coupon payloads): the full unlock ("order"), the opt-in paid/unreleased sets, compressed as
                      ``Prefix_{2,4-231}`` ranges
  coupons.json        built-in coupon codes (UNLOCKALL first claims every merc and its mods, verify-C5 (b))
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
import re
import sys
import zlib

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

# Unlock kinds (_catalog.json "unlock_kinds"/"random_pool_kinds") that are no way for a player to get an item.
NON_RELEASE_KINDS = {"cheat", "duplicate-replacement", "unreferenced-table"}
# Real-money sources: IAP bundles, the first-purchase top-up bonus, the VIP subscription (its milestone rewards
# WeaponCamo_Random_Random / MercAccessory_Random_Random, MCVS_Meta SubscriptionCard_30_Days_Milestones).
PAID_KINDS = {"iap-bundle", "top-up-reward", "vip-subscription-reward"}


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


def shipped_sources(entry):
    """The ways the 1.17.32 data (design tables, default CRM, random pools) gives a player this catalogue entry."""
    return (set(entry.get("unlock_kinds") or []) | set(entry.get("random_pool_kinds") or [])) - NON_RELEASE_KINDS


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

    def __init__(self, catalog, tables, design, include_locked_skins=False, ecomm_ids=None, asset_files=None):
        cats = catalog.get("categories")
        if not isinstance(cats, dict):
            raise ContentError("_catalog.json has no 'categories' object")
        self.game_version = catalog.get("game_version")
        self.tables = tables.get("databases", {})
        self.design = design or {}

        # Special sets (UNLOCKPAID / UNLOCKUNRELEASED): see classify_special.
        self.ecomm_ids = ecomm_ids          # Config/game_object.json ids, or None (then _catalog.json item_id decides)
        self.asset_files = asset_files      # names.json "files" keys (hashed paths on disk), or None
        self.unreleased = []                # (reward id, category)
        self.excluded = []                  # {"items": [...], "reason": ...}
        self.unreleased_ids = self._find_unreleased(cats)

        self.mercs = []          # (merc_id, rarity)
        for m in cats.get("mercs", []):
            mid = int(m["id"])
            if mid == 0 or str(m.get("displayed", "YES")).upper() != "YES":
                continue  # merc 0 is the placeholder; only displayed mercs are unlockable (GM/MercsComponent.cs:2443)
            if "UnlockMerc_%d" % mid in self.unreleased_ids:
                continue
            self.mercs.append((mid, m.get("rarity")))
        self.merc_ids = [m for m, _ in self.mercs]
        if not self.mercs:
            raise ContentError("no displayed mercs in the catalogue")
        # Mercs every new profile starts with: an UnlockMerc reward for an owned merc is unavailable and turns into
        # its alternate reward (AlternateRewards "UnlockMerc_<rarity>": 40 random gear), so the unlock skips them.
        self.starting_mercs = set(int(m["id"]) for m in cats.get("mercs", [])
                                  if "starting-inventory" in (m.get("unlock_kinds") or []))
        # Highest mod tier (Mod_<merc>_<stat>Tier<n>-<k>): ModTier_<merc>_<n> unlocks every mod of that merc with
        # Tiers <= n, perks included only at tier 4 or with the "WithPerks" suffix (GM/ModTierRewardItem.cs OnClaimed).
        tiers = []
        for x in cats.get("mods", []):
            found = re.search(r"Tier(\d+)-", x.get("item_id") or "")
            if found:
                tiers.append(int(found.group(1)))
        self.mod_max_tier = max(tiers) if tiers else 4

        self.skins = []          # (item_id, crate_only)
        self.locked_skins = []
        for s in cats.get("skins", []):
            item = s.get("item_id")
            if not item or "locked_flag" not in s:
                continue  # default skins have no item id; entries without design-table rows are ecomm-only ids
            if "default" in (s.get("unlock_kinds") or []) or item in self.unreleased_ids:
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
            if item in self.unreleased_ids:
                continue
            self.weapons.append((item, int(w.get("fragments_required") or 0)))

        self.camos = {}          # merc_id -> [camo ids], design-table camos only, Kayac collaboration excluded
        for c in cats.get("camos", []):
            if "merc_id" not in c or c.get("kayac_only"):
                continue
            if "WeaponCamo_%d_%d" % (int(c["merc_id"]), int(c["camo_id"])) in self.unreleased_ids:
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
        # Unreleased signatures are neither sold nor part of the full unlock (UNLOCKUNRELEASED only).
        released = lambda kind, x: "KillSignature_%s_%d" % (kind, int(x["id"])) not in self.unreleased_ids
        self.avatars = [(int(x["id"]), x.get("rarity")) for x in sig.get("avatars", []) if released("Avatar", x)]
        self.backgrounds = [(int(x["id"]), str(x.get("purchasable")).lower() == "true") for x in sig.get("backgrounds", [])
                            if released("Background", x)]
        self.sounds = [int(x["id"]) for x in sig.get("sounds", []) if released("Sound", x)]
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
        self.paid = self._find_paid(cats)
        ecomm_only = sorted([x["item_id"] for x in cats.get("skins", []) if x.get("in_design_tables") is False and x.get("item_id")] +
                            [x["item_id"] for x in cats.get("camos", []) if "merc_id" not in x and x.get("item_id")], key=natural_key)
        if ecomm_only:
            self.excluded.append({"items": ecomm_only, "reason": "ecomm id without a design-table row: the client has no specs to "
                                                                 "claim or display it"})
        if self.locked_skins:
            self.excluded.append({"items": sorted(self.locked_skins, key=natural_key),
                                  "reason": "released (migration compensation) but flagged Locked: the client hides it "
                                            "(MercSkinsComponent onlyAvailable)"})
        kayac_camos = sum(1 for c in cats.get("camos", []) if c.get("kayac_only"))
        kayac_acc = sum(len(a.get("item_ids", [])) for a in cats.get("accessories_jerseys", []) if a.get("kayac_only"))
        if kayac_camos or kayac_acc:
            self.excluded.append({"count": kayac_camos + kayac_acc,
                                  "reason": "Kayac (Japan) collaboration camos (%d) and jerseys (%d merc pairs): released there, "
                                            "hidden by the client for every other profile (WeaponCamosComponent / "
                                            "MercAccessoriesComponent IsKayac)" % (kayac_camos, kayac_acc)})

    # ------------------------------------------------------------------------------------- special sets

    def has_ecomm_id(self, item_id, catalog_item_id):
        if self.ecomm_ids is not None:
            return item_id in self.ecomm_ids
        return catalog_item_id is not None

    def asset_exists(self, path):
        """True when the logical path is a file of the player's data pack (names.json hash rule:
        crc32(path) -> "<h[0]>/<h[1:]>.f"); False when it is not, or when names.json was not given."""
        if not path or self.asset_files is None:
            return False
        h = "%x" % (zlib.crc32(path.encode("utf-8")) & 0xffffffff)
        return (h[0] + "/" + h[1:] + ".f") in self.asset_files

    def _find_unreleased(self, cats):
        """Items the game's own tables define (so the client has their specs and can claim and display them) that were
        never released: no shipped unlock source (no design-table reward, no default-CRM store or IAP entry, no random
        pool that reaches them) AND no id in the live ecomm catalogue (Config/game_object.json), so Gameloft's server
        could not grant or sell them either. "No shipped source" alone is not enough: Witch (17) and Bli-P (21), say,
        have none in 1.17.32 yet were sold by the live server. Each candidate also needs its asset on disk; merc 0 is
        the placeholder agent. Returns the set of unreleased reward ids (also listed in self.unreleased)."""
        found = set()
        missing_assets = []

        def consider(reward_id, category, entry, catalog_item_id, asset, needs_file=True):
            if shipped_sources(entry) or self.has_ecomm_id(reward_id, catalog_item_id):
                return
            if needs_file and not self.asset_exists(asset):
                missing_assets.append(reward_id)
                found.add(reward_id)  # never sold or granted by UNLOCKALL either
                return
            found.add(reward_id)
            self.unreleased.append((reward_id, category))

        placeholder = []
        for m in cats.get("mercs", []):
            mid = int(m["id"])
            if mid == 0:
                placeholder.append("UnlockMerc_0")
                continue
            consider("UnlockMerc_%d" % mid, "mercs", m, m.get("item_id"), m.get("template"))
        for x in cats.get("skins", []):
            if x.get("in_design_tables") is False or x.get("merc_id") is None or "default" in (x.get("unlock_kinds") or []):
                continue
            if str(x.get("locked_flag")).lower() == "true":
                continue  # hidden by the client (MercSkinsComponent onlyAvailable), see the hidden exclusions
            consider("MercSkin_%d_%d" % (int(x["merc_id"]), int(x["skin_id"])), "skins", x, x.get("item_id"), x.get("image"))
        for x in cats.get("camos", []):
            if "merc_id" not in x or x.get("kayac_only"):
                continue
            consider("WeaponCamo_%d_%d" % (int(x["merc_id"]), int(x["camo_id"])), "camos", x, x.get("item_id"), x.get("thumbnail"))
        for w in cats.get("weapons", []):
            if str(w.get("is_default")).upper() == "YES":
                continue
            frag = "MercWeaponFragment_0_%d" % int(w["id"])  # fragment ids are per weapon (game_object.json)
            if self.ecomm_ids is not None and frag in self.ecomm_ids:
                continue  # obtainable through its fragments
            if self.ecomm_ids is None:
                continue  # cannot tell without game_object.json: treated as released
            consider("MercWeapon_%d_%d" % (int(w["merc_id"]), int(w["id"])), "weapons", w, w.get("item_id"), None, needs_file=False)
        sig = cats.get("kill_signatures", {})
        for x in sig.get("avatars", []):
            consider("KillSignature_Avatar_%d" % int(x["id"]), "avatars", x, x.get("item_id"), x.get("asset"))
        for x in sig.get("backgrounds", []):
            # a background is a colour value, not a file
            consider("KillSignature_Background_%d" % int(x["id"]), "backgrounds", x, x.get("item_id"), None, needs_file=False)
        for x in sig.get("sounds", []):
            # a sound is an audio event, not a file we can check: never included
            consider("KillSignature_Sound_%d" % int(x["id"]), "sounds", x, x.get("item_id"), None)

        if placeholder:
            self.excluded.append({"items": placeholder, "reason": "placeholder agent (no ecomm id, Merc00 template, no name)"})
        if missing_assets:
            self.excluded.append({"items": sorted(missing_assets, key=natural_key),
                                  "reason": "unreleased, but its asset is not in the data pack (or names.json was not given)"})
        self.unreleased.sort(key=lambda u: natural_key(u[0]))
        return found

    def _find_paid(self, cats):
        """Released items whose every shipped source is real money (PAID_KINDS): {"camos": {merc: [camo ids]},
        "accessories": {merc: [ids]}, "other": [reward ids]}. Kayac, locked, ecomm-only and unreleased items are left out."""
        def paid_only(kinds):
            return bool(kinds) and kinds <= PAID_KINDS

        camos = {}
        for x in cats.get("camos", []):
            if "merc_id" not in x or x.get("kayac_only"):
                continue
            item = "WeaponCamo_%d_%d" % (int(x["merc_id"]), int(x["camo_id"]))
            if item in self.unreleased_ids or not paid_only(shipped_sources(x)):
                continue
            camos.setdefault(int(x["merc_id"]), []).append(int(x["camo_id"]))
        accessories = {}
        for a in cats.get("accessories_jerseys", []):
            if a.get("kayac_only") or str(a.get("visible_in_shop")).lower() != "true":
                continue
            # VisibleInShop, non-Kayac jerseys are what the VIP milestone's MercAccessory_Random_Random draws from
            # (GM/MercAccessoriesComponent.cs GetLockedAccessoriesForMerc); the catalogue lists no pool for jerseys.
            kinds = shipped_sources(a) | {"vip-subscription-reward"}
            if not paid_only(kinds):
                continue
            for item in a.get("item_ids", []):
                parts = item.split("_")
                accessories.setdefault(int(parts[1]), []).append(int(parts[2]))
        other = []
        for m in cats.get("mercs", []):
            if int(m["id"]) in self.merc_ids and paid_only(shipped_sources(m)):
                other.append("UnlockMerc_%d" % int(m["id"]))
        for item, _ in self.skins:
            x = next(s for s in cats.get("skins", []) if s.get("item_id") == item)
            if paid_only(shipped_sources(x)):
                other.append(item)
        sig = cats.get("kill_signatures", {})
        for kind, key in (("Avatar", "avatars"), ("Background", "backgrounds"), ("Sound", "sounds")):
            for x in sig.get(key, []):
                item = "KillSignature_%s_%d" % (kind, int(x["id"]))
                if item not in self.unreleased_ids and paid_only(shipped_sources(x)):
                    other.append(item)
        return {"camos": camos, "accessories": accessories, "other": sorted(other, key=natural_key)}

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
    """Ordered groups for a full unlock (verify-C5 recommended order). The merc unlocks must come first: skins,
    weapons, camos, accessories, gear slots and mod tiers of a locked merc are unavailable and would be converted to
    their alternate reward and lost (GM/RewardItem.cs Claim)."""
    groups = []
    groups.append({"id": "mercs", "items": merc_unlock_items(cat),
                   "note": "every merc, then every mod of every merc (ModTier_<merc>_%d)" % cat.mod_max_tier})
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
    order = [g["id"] for g in groups]

    # Opt-in sets, never in "order" (so never part of settings.unlockAll): coupons only.
    groups.append({"id": "mercUnlocks", "items": merc_unlock_items(cat, with_mods=False),
                   "note": "every merc without the mods: what merc-dependent paid items need first"})
    if cat.paid["camos"]:
        groups.append({"id": "paidCamos", "items": ["WeaponCamo_%d_{%s}" % (m, compress_ranges(v))
                                                    for m, v in sorted(cat.paid["camos"].items())],
                       "note": "camos whose only shipped source is the VIP subscription's random pool"})
    if cat.paid["accessories"]:
        groups.append({"id": "paidAccessories", "items": ["MercAccessory_%d_{%s}" % (m, compress_ranges(v))
                                                          for m, v in sorted(cat.paid["accessories"].items())],
                       "note": "jerseys with no shipped source but the VIP subscription's random pool"})
    if cat.paid["other"]:
        groups.append({"id": "paidOther", "items": cat.paid["other"],
                       "note": "mercs, skins and signatures whose only shipped source is real money"})
    groups.append({"id": "unreleased", "items": compress_items([u for u, _ in cat.unreleased]),
                   "note": "defined in the tables, never released: no shipped source and no live ecomm id; assets checked"})

    counts = {}
    for g in groups:
        n = sum(len(expand_pattern(p)) for p in g["items"])
        g["count"] = n
        counts[g["id"]] = n
        for p in g["items"]:
            for item in expand_pattern(p)[:1]:
                if reward_type(item) not in KNOWN_REWARD_TYPES:
                    raise ContentError("unknown reward type in unlock group %s: %s" % (g["id"], item))
    return {"format": FORMAT, "order": order, "groups": groups, "excluded": cat.excluded}, counts


def merc_unlock_items(cat, with_mods=True):
    """One UnlockMerc_<id> per displayed merc the player does not start with, then one ModTier_<id>_<max> per merc.

    Not UnlockMerc_ALL_MAX: its claim works (CheatUnlockAllMercs) but every reward display then looks the merc up by
    its id, and "ALL" is no merc (GM/LotteryComponent.cs DisplayLotteryRewards: "Could not find merc data for mercId
    'ALL'", fatal on the device). UnlockMerc_<id> is the plain merc grammar (Subtype = merc id, GM/RewardItem.cs Parse;
    GM/UnlockMercRewardItem.cs OnClaimed); ModTier rewards are not IPrintableRewardItem, so the lottery never shows them,
    and the inventory diff and inbox claim them in order, after their merc is unlocked (ModTierRewardItem.IsAvailable
    needs an unlocked merc)."""
    tier = int(cat.mod_max_tier)
    suffix = "" if tier == 4 else "_WithPerks"  # tier 4 includes perks anyway (GM/ModTierRewardItem.cs OnClaimed)
    unlock = [m for m in cat.merc_ids if m not in cat.starting_mercs]
    items = []
    if unlock:
        items.append("UnlockMerc_{%s}" % compress_ranges(unlock))
    if with_mods:
        items.append("ModTier_{%s}_%d%s" % (compress_ranges(cat.merc_ids), tier, suffix))
    return items


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


def build_coupons(group_ids):
    merc_dependent = ["weapons", "gearSlots", "camos", "accessories", "skins"]
    codes = {
        "UNLOCKALL": {
            "groups": ["mercs", "weapons", "gearSlots", "signatures", "camos", "accessories", "skins"],
            "description": "Full unlock in one inbox message (about 11k rewards, claimed in this order)",
        },
        "UNLOCKMERCS": {"groups": ["mercs"], "description": "Every merc and every mod"},
        "UNLOCKSIGNATURES": {"groups": ["signatures"], "description": "Every kill-signature avatar, background and sound"},
    }
    # Opt-in: paid-only and unreleased content. Merc-dependent rewards need their merc first (verify-C5 (b)), but not the
    # mods, so these start with "mercUnlocks" (no level-up flood).
    paid = [g for g in ("paidCamos", "paidAccessories", "paidOther") if g in group_ids]
    if paid:
        codes["UNLOCKPAID"] = {"groups": ["mercUnlocks"] + paid,
                               "description": "Every item whose only source in the original game was real money (VIP pool camos "
                                              "and jerseys, IAP-only items)"}
        if "paidCamos" in group_ids:
            codes["UNLOCKPAIDCAMOS"] = {"groups": ["mercUnlocks", "paidCamos"], "description": "UNLOCKPAID, camos only"}
        if "paidAccessories" in group_ids:
            codes["UNLOCKPAIDACCESSORIES"] = {"groups": ["mercUnlocks", "paidAccessories"],
                                              "description": "UNLOCKPAID, jerseys only"}
    codes["UNLOCKUNRELEASED"] = {"groups": ["unreleased"],
                                 "description": "Items defined in the data but never released (opt-in; not in UNLOCKALL or the shop)"}
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
              "UNLOCKACCESSORIES", "UNLOCKSKINS", "UNLOCKPAID", "UNLOCKPAIDCAMOS", "UNLOCKPAIDACCESSORIES", "UNLOCKUNRELEASED"]:
        if k in codes:
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
    inputs = ["_catalog.json", "_tables.json"]
    ecomm_ids = None
    go_path = os.path.join(decoded_dir, "Config", "game_object.json")
    if os.path.exists(go_path):
        ecomm_ids = set(x["id"] for x in load_json(go_path))
        inputs.append("Config/game_object.json (ids only)")
    asset_files = None
    names_path = os.path.join(decoded_dir, "names.json")
    if os.path.exists(names_path):
        asset_files = set(load_json(names_path).get("files", {}))
        inputs.append("names.json (file list only)")
    cat = Catalog(catalog, tables, design, include_locked_skins=bool(pricing["skins"].get("includeLocked")),
                  ecomm_ids=ecomm_ids, asset_files=asset_files)

    store, store_summary = build_offline_store(cat, pricing)
    unlocks, unlock_counts = build_unlocks(cat)
    coupons = build_coupons(set(g["id"] for g in unlocks["groups"]))
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
                   "inputs": inputs + (["Design/MCVS_Meta.xlsx (index columns only)"] if design else []),
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
