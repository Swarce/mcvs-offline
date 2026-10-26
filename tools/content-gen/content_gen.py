#!/usr/bin/env python3
"""MCVS Offline content generator (owner: CONTENT).

Reads the player's own decoded data pack (``datapack-decoded/_catalog.json`` and ``_tables.json``, ``Design/MCVS_Meta.xlsx``
for index columns and the collector's rules) plus our tuning in ``pricing.json``, ``liveops_tuning.json`` and
``collector_tuning.json`` and writes the generated content that the server compiles into ``McvsOffline.Server.dll``
(``server/Content/``):

  offline_store.json  CRM ``offline_store`` priced in VersusCoins (merc/skin/kill-signature screens and the
                      generic ``#VersusCoins`` shop-hub tab), see docs/ECONOMY.md and verify-C5 (d)
  unlocks.json        reward-id groups (coupon payloads): the full unlock ("order"), the opt-in paid/unreleased sets, compressed as
                      ``Prefix_{2,4-231}`` ranges
  coupons.json        built-in coupon codes (UNLOCKALL first claims every merc and its mods, verify-C5 (b))
  economy.json        ``_OverrideTables`` targets for the settings.json economy multipliers + a cost summary
  crm_overlay.json    static CRM overlay (game parameters we always send)
  liveops.json        live-ops catalogue (weekly division seasons, daily quests, weekly achievement events, weekly
                      modifiers, the collector's crate rewards) from ``liveops_tuning.json``, validated against the
                      catalogue (docs/LIVEOPS.md)
  collector.json      the Cosmetic crate (economy collector): crate rows, item pools and season tables from
                      ``collector_tuning.json`` (ids only), see docs/design/economy-collector.md §3.4
  manifest.json       what was generated from what

Only generated item ids, prices and our own tuning are written: no Gameloft text, no table values.
Output is deterministic (stable order, no timestamps) so diffs stay reviewable.

Usage (Python 3 standard library only; run isolated):
  python3 -I tools/content-gen/content_gen.py --decoded /home/user/mcvs-work/datapack-decoded
  python3 -I tools/content-gen/content_gen.py --decoded ... --check     # verify server/Content is up to date
"""

import argparse
import datetime
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


def expand_ranges(text):
    """Inverse of compress_ranges: "2,4-6,9" -> [2, 4, 5, 6, 9]; "" -> []."""
    return [int(x) for x in expand_pattern("{%s}" % text)] if text else []


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
        # The weapon whose CamosList_<weapon> table holds a merc's camos (CamosListPerWeapon, GM/WeaponCamoRewardItem.cs).
        self.merc_weapons = dict((int(m["id"]), m.get("vanilla_weapon")) for m in cats.get("mercs", []))
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
        # reward id -> (category, catalogue entry): the shipped sources of every id the unlock groups can name
        # (_find_nosource).
        self.entries = {}
        for m in cats.get("mercs", []):
            self.entries["UnlockMerc_%d" % int(m["id"])] = ("mercs", m)
        for x in cats.get("skins", []):
            if x.get("item_id"):
                self.entries[x["item_id"]] = ("skins", x)
        for w in cats.get("weapons", []):
            self.entries["MercWeapon_%d_%d" % (int(w["merc_id"]), int(w["id"]))] = ("weapons", w)
        for c in cats.get("camos", []):
            if "merc_id" in c:
                self.entries["WeaponCamo_%d_%d" % (int(c["merc_id"]), int(c["camo_id"]))] = ("camos", c)
        for a in cats.get("accessories_jerseys", []):
            for item in a.get("item_ids", []):
                self.entries[item] = ("accessories", a)
        for kind, key in (("Avatar", "avatars"), ("Background", "backgrounds"), ("Sound", "sounds")):
            for x in sig.get(key, []):
                self.entries["KillSignature_%s_%d" % (kind, int(x["id"]))] = (key, x)
        for g in cats.get("gear_slot_unlocks", []):
            self.entries[g["item_id"]] = ("gearSlots", g)
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


# ----------------------------------------------------------------------------------------------- live ops

# MainTracker values (GM/CompletionSpecs.cs) the generated quests/events may use: each is counted by
# BaseAchievementsComponent on the server Worlds for any PvP match (OnMatchResult: Wins, PlayedMatches, MVP; the
# match-stats handlers: Kills, Eliminations, Assists; OnCrateClaimed: CratesOpened). Mode- or merc-specific
# trackers are left out on purpose (a quest the player cannot do in a quick match would block the daily bonus).
LIVEOPS_TRACKERS = {"PlayedMatches", "Wins", "Kills", "Eliminations", "Assists", "MVP", "CratesOpened"}
# Plain currency rewards (GM/<Type>RewardItem.cs); crates are checked against the catalogue.
LIVEOPS_CURRENCIES = {"VersusCoins", "Credits", "UpgradeTokens", "HardCurrency"}


def _loc_tables(decoded_dir):
    """{table: set(ids)} of Localization/<table>_en.json (ids only), or None when the folder is absent."""
    loc_dir = os.path.join(decoded_dir, "Localization")
    if not os.path.isdir(loc_dir):
        return None
    tables = {}
    for name in os.listdir(loc_dir):
        if not name.endswith("_en.json"):
            continue
        try:
            rows = load_json(os.path.join(loc_dir, name))
        except (ValueError, OSError):
            continue
        if isinstance(rows, list):
            tables[name[:-len("_en.json")]] = set(r.get("Id") for r in rows if isinstance(r, dict))
    return tables


def check_loc_id(loc_tables, locid, where):
    """A "<table>.<id>" localization id that exists in Localization/<table>_en.json (when the folder is known)."""
    if not isinstance(locid, str) or "." not in locid:
        raise ContentError("%s: '%s' is not a <table>.<id> localization id" % (where, locid))
    table, key = locid.split(".", 1)
    if loc_tables is not None and key not in loc_tables.get(table, ()):
        raise ContentError("%s: localization id %s does not exist" % (where, locid))
    return locid


def check_image(cat, path, where):
    """An image path that is a file of the player's data pack (when names.json is known)."""
    if cat.asset_files is not None and not cat.asset_exists(path):
        raise ContentError("%s: image %s is not in the data pack" % (where, path))
    return path


# The default season grid (LiveOpsSettings.DefaultAnchor): seasons start on Mondays 00:00 UTC, whole weeks after it.
SEASON_EPOCH = datetime.datetime(2024, 1, 1, tzinfo=datetime.timezone.utc)
# content-gen warns when collector.since is closer than this to the generation time (economy-collector.md §3.2).
SINCE_MARGIN = datetime.timedelta(days=7)
# The liveops.json collector block, in output order (economy-collector.md §3.2).
COLLECTOR_BLOCK_KEYS = ("since", "dailyBonus", "weeklyMilestones", "weeklyFinal")


def parse_since(value, where):
    """An ISO 8601 UTC time ("2026-11-09", "2026-11-09T00:00:00Z", "...+00:00") that is a season start of the default
    grid (a Monday 00:00 UTC): the moment the collector's live-ops rewards and CRM layer start (D5)."""
    found = re.match(r"^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2}))?)?(Z|[+-]00:?00)?$", str(value))
    if not found:
        raise ContentError("%s: '%s' is not an ISO 8601 UTC time" % (where, value))
    try:
        since = datetime.datetime(*[int(p or 0) for p in found.groups()[:6]], tzinfo=datetime.timezone.utc)
    except ValueError:
        raise ContentError("%s: '%s' is not a valid date" % (where, value))
    if (since - SEASON_EPOCH).total_seconds() % (7 * 86400) != 0:
        raise ContentError("%s: %s is not a Monday 00:00 UTC (a season start of the default grid)" % (where, value))
    return since


def warn(message):
    print("content-gen: warning: %s" % message, file=sys.stderr)


def build_liveops(cat, tuning, loc_tables, now=None):
    """liveops.json: our live-ops tuning with every id validated and every id list expanded. ``now`` (UTC) only decides
    whether the collector's ``since`` warning is printed; the output never depends on it."""

    def loc(locid, where):
        return check_loc_id(loc_tables, locid, "liveops: " + where)

    def rewards(items, where, allow_empty=False):
        if not isinstance(items, list):
            raise ContentError("liveops: %s: rewards must be a list" % where)
        out = []
        for r in items:
            if not isinstance(r, dict):
                raise ContentError("liveops: %s: a reward must be an {item, quantity} object" % where)
            item, qty = r.get("item"), r.get("quantity")
            if not isinstance(qty, int) or qty <= 0:
                raise ContentError("liveops: %s: quantity of %s must be a positive integer" % (where, item))
            if item not in LIVEOPS_CURRENCIES and item not in cat.crates:
                raise ContentError("liveops: %s: unknown reward %s" % (where, item))
            out.append({"item": item, "quantity": qty})
        if not out and not allow_empty:
            raise ContentError("liveops: %s: no rewards" % where)
        return out

    def image(path, where):
        return check_image(cat, path, "liveops: " + where)

    def goals(values, where):
        if not values or any(not isinstance(g, int) or g <= 0 for g in values) or sorted(values) != values:
            raise ContentError("liveops: %s: goals must be ascending positive integers" % where)
        return list(values)

    images = {k: image(v, "images." + k) for k, v in sorted(tuning["images"].items())}

    seasons = dict(tuning["seasons"])
    seasons.pop("_promotionMatchesPerDay", None)
    if len(seasons["promotionMatchesPerDay"]) != len(cat.divisions):
        raise ContentError("liveops: seasons.promotionMatchesPerDay has %d values for %d divisions"
                           % (len(seasons["promotionMatchesPerDay"]), len(cat.divisions)))
    loc(seasons["title"], "seasons.title")
    loc(seasons["description"], "seasons.description")
    seasons["divisions"] = list(cat.divisions)

    objectives = []
    ids = set()
    for o in tuning["objectives"]:
        oid = o["id"]
        if oid in ids:
            raise ContentError("liveops: duplicate objective %s" % oid)
        ids.add(oid)
        if o["tracker"] not in LIVEOPS_TRACKERS:
            raise ContentError("liveops: objective %s: tracker %s is not supported" % (oid, o["tracker"]))
        out = {"id": oid, "tracker": o["tracker"], "title": loc(o["title"], oid + ".title"),
               "titlePlural": loc(o["titlePlural"], oid + ".titlePlural")}
        if "theme" in o:
            out["theme"] = {"title": loc(o["theme"]["title"], oid + ".theme.title"),
                            "description": loc(o["theme"]["description"], oid + ".theme.description")}
        if "daily" in o:
            out["daily"] = {"goals": goals(o["daily"]["goals"], oid + ".daily"),
                            "rewards": rewards(o["daily"]["rewards"], oid + ".daily")}
        if "weekly" in o:
            out["weekly"] = {"goals": goals(o["weekly"]["goals"], oid + ".weekly")}
        objectives.append(out)

    dq = tuning["dailyQuests"]
    daily_pool = [o for o in objectives if "daily" in o]
    if not 1 <= dq["count"] <= len(daily_pool):
        raise ContentError("liveops: dailyQuests.count must be 1..%d" % len(daily_pool))
    daily = {"title": loc(dq["title"], "dailyQuests.title"), "description": loc(dq["description"], "dailyQuests.description"),
             "advertisement": loc(dq["advertisement"], "dailyQuests.advertisement"), "count": dq["count"],
             "bonus": {"title": loc(dq["bonus"]["title"], "dailyQuests.bonus.title"),
                       "rewards": rewards(dq["bonus"]["rewards"], "dailyQuests.bonus")}}
    # The TS tells daily quests from other achievement events by this substring of the title
    # (GM/TimeLimitedAchievementsComponent.cs OnTimeLimitedEventsReceived: Title.Contains("DailyQuestTitle")).
    if "DailyQuestTitle" not in daily["title"]:
        raise ContentError("liveops: dailyQuests.title must contain DailyQuestTitle (the TS keys daily quests on it)")

    we = tuning["weeklyEvents"]
    weekly_pool = [o for o in objectives if "weekly" in o]
    by_id = {o["id"]: o for o in objectives}
    if not 1 <= we["count"] <= len(weekly_pool):
        raise ContentError("liveops: weeklyEvents.count must be 1..%d" % len(weekly_pool))
    for t in we["themes"]:
        if t not in by_id or "theme" not in by_id[t] or "weekly" not in by_id[t]:
            raise ContentError("liveops: weekly theme %s needs an objective with theme and weekly goals" % t)
    milestone_rewards = [rewards(r, "weeklyEvents.milestoneRewards[%d]" % i) for i, r in enumerate(we["milestoneRewards"])]
    for o in weekly_pool:
        if len(o["weekly"]["goals"]) != len(milestone_rewards):
            raise ContentError("liveops: objective %s has %d weekly goals for %d milestone reward tiers"
                               % (o["id"], len(o["weekly"]["goals"]), len(milestone_rewards)))
    prefix = we["eventCratePrefix"]
    event_crates = sorted([c for c in cat.crates if c.startswith(prefix) and c[len(prefix):].isdigit()], key=natural_key)
    if we["final"].get("eventCrate") and not event_crates:
        raise ContentError("liveops: no %s* crates in the catalogue" % prefix)
    weekly = {"advertisement": loc(we["advertisement"], "weeklyEvents.advertisement"),
              "boostedTag": loc(we["boostedTag"], "weeklyEvents.boostedTag"),
              "count": we["count"], "themes": list(we["themes"]), "milestoneRewards": milestone_rewards,
              "eventCrates": event_crates,
              "final": {"title": loc(we["final"]["title"], "weeklyEvents.final.title"),
                        "rewards": rewards(we["final"]["rewards"], "weeklyEvents.final"),
                        "eventCrate": bool(we["final"].get("eventCrate"))}}

    id_lists = {"divisions": list(cat.divisions), "levels": list(cat.player_levels)}
    definitions = []
    names = set()
    for m in tuning["modifiers"]["definitions"]:
        cells = []
        for c in m["cells"]:
            have = cat.table_fields("MCVS_Meta", c["table"])
            if have is None:
                raise ContentError("liveops: modifier %s: no MCVS_Meta table %s" % (m["id"], c["table"]))
            for f in [c["index"]] + c["fields"]:
                if f not in have:
                    raise ContentError("liveops: modifier %s: MCVS_Meta.%s has no field %s" % (m["id"], c["table"], f))
            factor = c["factor"]
            if not isinstance(factor, (int, float)) or factor <= 0:
                raise ContentError("liveops: modifier %s: factor must be positive" % m["id"])
            cells.append({"database": "MCVS_Meta", "table": c["table"], "index": c["index"], "ids": id_lists[c["ids"]],
                          "fields": list(c["fields"]), "factor": factor})
        d = {"id": m["id"], "cells": cells}
        if "rivalFactor" in m:
            d["rivalFactor"] = m["rivalFactor"]
        definitions.append(d)
        names.add(m["id"])
    rotation = list(tuning["modifiers"]["rotation"])
    if not rotation or any(r not in names for r in rotation):
        raise ContentError("liveops: modifiers.rotation names an undefined modifier")

    sr = tuning["seasonRewards"]
    have = cat.table_fields("MCVS_Meta", sr["table"])
    if have is None:
        raise ContentError("liveops: no MCVS_Meta table %s" % sr["table"])
    reward_fields = ["%s%02d" % (sr["fieldPrefix"], d) for d in cat.divisions]
    for f in [sr["index"]] + reward_fields:
        if f not in have:
            raise ContentError("liveops: MCVS_Meta.%s has no field %s" % (sr["table"], f))
    rows = (cat.tables.get("Design/MCVS_Meta.xlsx", {}).get(sr["table"]) or {}).get("rows", 0)
    ranks = cat.index_values("MCVS_Meta", sr["table"], sr["index"], list(range(1, rows + 1)))

    out = {
        "format": FORMAT,
        "revision": int(tuning["revision"]),
        "images": images,
        "seasons": seasons,
        "objectives": objectives,
        "dailyQuests": daily,
        "weeklyEvents": weekly,
        "modifiers": {"rotation": rotation, "definitions": definitions},
        "seasonRewards": {"database": "MCVS_Meta", "table": sr["table"], "index": sr["index"], "ids": ranks,
                          "fields": reward_fields},
    }
    if "collector" in tuning:
        out["collector"] = build_liveops_collector(tuning["collector"], len(milestone_rewards), rewards, now)
    return out


def build_liveops_collector(block, milestone_tiers, rewards, now):
    """The liveops.json ``collector`` block (economy-collector.md §3.2): the Cosmetic crates the daily bonus, the weekly
    milestone tiers and the weekly final hand out in every window from ``since`` on. Carried unchanged, ``since``
    normalised; the ``revision`` stays as it is (a bump would replace the running day and week for everybody). A list
    may be empty (that event part gets no crate: the content-only fallback of §8, e.g. crates on tier 3 and the final
    only); every key must be there, so that a misspelt one cannot silently drop its crates."""
    if not isinstance(block, dict):
        raise ContentError("liveops: collector must be an object")
    unknown = sorted(k for k in block if not k.startswith("_") and k not in COLLECTOR_BLOCK_KEYS)
    missing = [k for k in COLLECTOR_BLOCK_KEYS if k not in block]
    if unknown or missing:
        raise ContentError("liveops: collector needs exactly the keys %s (unknown: %s, missing: %s)"
                           % (", ".join(COLLECTOR_BLOCK_KEYS), ", ".join(unknown) or "-", ", ".join(missing) or "-"))
    since = parse_since(block["since"], "liveops: collector.since")
    milestones = block["weeklyMilestones"]
    if not isinstance(milestones, list) or len(milestones) != milestone_tiers:
        raise ContentError("liveops: collector.weeklyMilestones needs one reward list per weekly milestone tier (%d)"
                           % milestone_tiers)
    out = {
        "since": since.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "dailyBonus": rewards(block["dailyBonus"], "collector.dailyBonus", allow_empty=True),
        "weeklyMilestones": [rewards(m, "collector.weeklyMilestones[%d]" % i, allow_empty=True)
                             for i, m in enumerate(milestones)],
        "weeklyFinal": rewards(block["weeklyFinal"], "collector.weeklyFinal", allow_empty=True),
    }
    if now is not None and since < now + SINCE_MARGIN:
        warn("liveops: collector.since %s is less than 7 days after this build (%s): set collector.since to the first "
             "Monday at least 7 days after the build is published" % (out["since"], now.strftime("%Y-%m-%d")))
    return out


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


# ----------------------------------------------------------------------------------------------- collector

# Row types CratesInventoryComponent.GenerateRewardItem builds (GM/CratesInventoryComponent.cs:587-648); any other Type
# draws no reward at all.
CRATE_ROW_TYPES = {"UpgradeTokens", "SeasonTickets", "VersusCoins", "HardCurrency", "KillSignature", "Mod", "ModTier",
                   "Gear", "GearSlot", "UnlockMerc", "TrialMerc", "MercSkin", "Credits", "MercAccessory", "WeaponCamo",
                   "MercWeapon", "MercWeaponFragment", "TrialWeapon"}
# Content-table fields the collector's CRM cells write (economy-collector.md §2.2-§2.5).
LOOT_CRATE_FIELDS = ("Id", "LocId", "CratesCount", "RewardChance.2", "ContentTableName", "Image", "ImageShop")
CONTENT_FIELDS = ("Id", "Type", "Subtype", "Subtype2", "BundledRewards", "Weight.1", "AlwaysGiveBundledRewards")
# Only existing rows can change (EN/Database.cs:143-196); the crate's content table has 100 in 1.17.32.
MAX_CRATE_ROWS = 100
# Division season placements (PVPDivisionRewardsType): ranks 1, 2, 3, 4-10 and 11-30 get Crate_<tier>_<place>.
SEASON_PLACES = (("FirstPlace", 1, 1), ("SecondPlace", 2, 2), ("ThirdPlace", 3, 3), ("4To10Place", 4, 10),
                 ("11To20Place", 11, 30))
# season_rewards/<numeral><stars>.png: the emblem without star, then one, two and three stars.
EMBLEM_STARS = ("", "_1", "_2", "_3")
# The row categories of collector.json, each exactly once in collector_tuning.json "rows" (§2.3, §4.1).
COLLECTOR_CATEGORIES = ("avatars", "backgrounds", "sounds", "skins", "hiddenJerseys", "agents", "camos", "jerseys",
                        "exhausted")


def _flag(value):
    return str(value).lower() == "true"


def _design_table(cat, table):
    t = cat.design.get("MCVS_Meta", {}).get(table)
    if t is None:
        raise ContentError("collector: no MCVS_Meta table %s in Design/MCVS_Meta.xlsx" % table)
    return t


def _design_rows(cat, table):
    t = _design_table(cat, table)
    return [dict(zip(t["Fields"], r)) for r in t["Rows"]]


def _require_fields(cat, table, fields):
    have = _design_table(cat, table)["Fields"]
    for f in fields:
        if f not in have:
            raise ContentError("collector: MCVS_Meta.%s has no field %s" % (table, f))


def _number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def _id_of(reward_id):
    return int(reward_id.rsplit("_", 1)[1])


def _find_nosource(cat, unlocks):
    """Released items that no shipped source gives a player (economy-collector.md §1.1): every reward id of the full
    unlock (the unlocks.json "order" groups) whose catalogue entry has no shipped source, visible non-Kayac jerseys
    counted as sourced by the VIP subscription's random pool (GM/MercAccessoriesComponent.cs
    GetLockedAccessoriesForMerc). Returns {catalogue category: set(reward ids)}; gear slots read as no-source too but
    are no cosmetics, the crate never names them."""
    groups = dict((g["id"], g) for g in unlocks["groups"])
    found = {}
    for gid in unlocks["order"]:
        for pattern in groups[gid]["items"]:
            for item in expand_pattern(pattern):
                if reward_type(item) == "ModTier":
                    continue  # a merc's mods, not an item of their own
                if item not in cat.entries:
                    raise ContentError("collector: released %s has no catalogue entry" % item)
                category, entry = cat.entries[item]
                kinds = shipped_sources(entry)
                if category == "accessories" and _flag(entry.get("visible_in_shop")) and not entry.get("kayac_only"):
                    kinds = kinds | {"vip-subscription-reward"}
                if not kinds:
                    found.setdefault(category, set()).add(item)
    return found


def collector_row_problem(row):
    """None when the client can preview a crate row with weight (economy-collector.md §2.3.1), else why not.
    GetPreview keys each weighted row on its image; a row whose reward has none (a sound, a fixed background) throws
    KeyNotFoundException on every screen that shows the crate (D8): sounds ride only as carrier bundles."""
    kind, sub, sub2, bundle = row.get("type"), row.get("subtype"), row.get("subtype2"), row.get("bundle")
    digits = lambda v: isinstance(v, str) and v.isdigit()
    if kind not in CRATE_ROW_TYPES:
        return "type %s is not a reward type GenerateRewardItem builds" % kind
    if kind == "KillSignature":
        ok = sub in ("Avatar", "Background") and sub2 == "Random"
    elif kind == "MercSkin":
        ok = digits(sub) and digits(sub2)
    elif kind == "MercAccessory":
        ok = sub == "Random" and (sub2 == "Random" or digits(sub2))
    elif kind == "UnlockMerc":
        ok = digits(sub) and sub2 is None
    elif kind == "WeaponCamo":
        ok = sub == "Random" and sub2 == "Random"
    elif kind == "VersusCoins":
        ok = isinstance(sub, str) and re.match(r"^Tiers\d+$", sub) is not None and sub2 is None
    else:
        ok = False
    if not ok:
        return "%s/%s/%s cannot be previewed with weight" % (kind, sub, sub2)
    if bundle is not None:
        if not (isinstance(bundle, str) and re.match(r"^KillSignature_Sound_\d+$", bundle)):
            return "bundle %s is not a KillSignature_Sound_<n> carrier bundle" % bundle
        if (kind, sub, sub2) != ("KillSignature", "Avatar", "Random"):
            return "bundle %s on a non-carrier %s/%s/%s row (only KillSignature/Avatar/Random rows carry sounds)" % (
                bundle, kind, sub, sub2)
    return None


def build_collector(cat, tuning, unlocks, loc_tables):
    """collector.json: the Cosmetic crate (economy-collector.md §1, §3.4). Ids only; weights, items per crate and the
    other runtime knobs are economy.collector.* settings (CollectorPlan, server side)."""
    if "MCVS_Meta" not in cat.design:
        raise ContentError("collector: needs the design tables (Design/MCVS_Meta.xlsx in the decoded data pack)")
    nosource = _find_nosource(cat, unlocks)
    nosource_ids = lambda category: sorted(_id_of(r) for r in nosource.get(category, ()))

    # Crate: an existing LootCrates row whose content table holds no reward yet (rows 1..n become ours).
    crate = tuning["crate"]
    _require_fields(cat, "LootCrates", LOOT_CRATE_FIELDS)
    loot = dict((r["Id"], r) for r in _design_rows(cat, "LootCrates"))
    if crate.get("id") not in loot:
        raise ContentError("collector: unknown crate %s (no LootCrates row)" % crate.get("id"))
    table = loot[crate["id"]]["ContentTableName"]
    _require_fields(cat, table, CONTENT_FIELDS)
    if any(r.get("Type") for r in _design_rows(cat, table)):
        raise ContentError("collector: content table %s already holds rewards" % table)
    check_loc_id(loc_tables, crate.get("locId"), "collector: crate.locId")
    check_image(cat, crate.get("image"), "collector: crate.image")

    # Avatars (§1.2, §1.3): the Kayac rule, the reserved season emblems, then the sets the CRM cells need.
    av = dict((int(r["ID"]), r) for r in _design_rows(cat, "KillsignatureAvatars"))
    released_av = set(a for a, _ in cat.avatars)
    if released_av - set(av):
        raise ContentError("collector: avatars %s have no KillsignatureAvatars row" % compress_ranges(released_av - set(av)))
    rule = tuning["kayac"]
    folders = list(rule.get("folders") or [])
    name_part = (rule.get("nameContains") or "").lower()
    extra = set(int(x) for x in rule.get("extra") or [])
    if extra - set(av):
        raise ContentError("collector: kayac.extra names unknown avatars %s" % compress_ranges(extra - set(av)))

    def is_kayac(aid):
        asset = av[aid].get("Asset") or ""
        name = asset.rsplit("/", 1)[-1].lower()
        return any(asset.startswith(f) for f in folders) or bool(name_part and name_part in name) or aid in extra

    kayac = sorted(a for a in av if is_kayac(a))

    season = tuning["season"]
    by_asset = {}
    for aid, r in av.items():
        by_asset.setdefault(r.get("Asset"), []).append(aid)
    emblems = []
    for numeral in season["numerals"]:
        ids = []
        for stars in EMBLEM_STARS:
            asset = "%s%s%s.png" % (season["emblemFolder"], numeral, stars)
            found = by_asset.get(asset, [])
            if len(found) != 1 or found[0] not in released_av:
                raise ContentError("collector: season emblem %s does not resolve to one released avatar" % asset)
            ids.append(found[0])
        emblems.append(ids)
    reserved = sorted(i for e in emblems for i in e[1:]) if tuning.get("reservePlacementEmblems", True) else []

    unlockable = lambda aid: _flag(av[aid].get("Unlockable"))
    kayac_set, reserved_set = set(kayac), set(reserved)
    base = [a for a in nosource_ids("avatars") if a not in kayac_set and a not in reserved_set]
    unlock = [a for a in base if not unlockable(a)]
    lock = [a for a in kayac if a in released_av and unlockable(a)]
    drawable = sorted(a for a in released_av if unlockable(a) and a not in kayac_set)

    # Backgrounds (§1.4): no Kayac ones.
    bg = dict((int(r["ID"]), r) for r in _design_rows(cat, "KillsignatureBackgrounds"))
    released_bg = set(b for b, _ in cat.backgrounds)
    if released_bg - set(bg):
        raise ContentError("collector: backgrounds %s have no KillsignatureBackgrounds row" % compress_ranges(released_bg - set(bg)))
    bg_base = nosource_ids("backgrounds")
    bg_unlock = [b for b in bg_base if not _flag(bg[b].get("Unlockable"))]
    bg_drawable = sorted(b for b in released_bg if _flag(bg[b].get("Unlockable")))

    # The explicit rows of O5: released no-source items each (a tuning typo would otherwise sell a sourced item).
    sound_rows = set(int(r["ID"]) for r in _design_rows(cat, "KillsignatureSounds"))
    sounds = [int(s) for s in tuning["sounds"]]
    for s in sounds:
        if s not in sound_rows or "KillSignature_Sound_%d" % s not in nosource.get("sounds", ()):
            raise ContentError("collector: sound %d is not a released no-source sound" % s)
    skin_tables = dict((int(r["MercId"]), r["SkinsTableName"]) for r in _design_rows(cat, "SkinsListPerMerc"))
    skins = [(int(m), int(s)) for m, s in tuning["skins"]]
    for m, s in skins:
        listed = [r for r in _design_rows(cat, skin_tables[m]) if int(r["ID"]) == s] if m in skin_tables else []
        if len(listed) != 1 or _flag(listed[0].get("Locked")):
            raise ContentError("collector: skin %d/%d has no unlocked SkinsList row (the client hides locked skins)" % (m, s))
        if "MercSkin_%d_%d" % (m, s) not in nosource.get("skins", ()):
            raise ContentError("collector: skin %d/%d is not a released no-source skin" % (m, s))
    accessories = dict((int(r["Id"]), r) for r in _design_rows(cat, "MercAccessories"))
    hidden = [int(j) for j in tuning["hiddenJerseys"]]
    hidden_pairs = 0
    for j in hidden:
        r = accessories.get(j)
        if r is None or _flag(r.get("VisibleInShop")) or _flag(r.get("IsKayac")) or r.get("MercId") not in (None, ""):
            raise ContentError("collector: hidden jersey %d must exist with VisibleInShop=false, IsKayac=false and no MercId" % j)
        released = set("MercAccessory_%d_%d" % (m, j) for m in cat.merc_ids if j in cat.accessories.get(m, ()))
        if not released or not released <= nosource.get("accessories", set()):
            raise ContentError("collector: hidden jersey %d is not a released no-source jersey" % j)
        hidden_pairs += len(released)
    agents = [int(a) for a in tuning["agents"]]
    for name, values in (("sounds", sounds), ("skins", skins), ("hiddenJerseys", hidden), ("agents", agents)):
        if len(set(values)) != len(values):
            raise ContentError("collector: %s names an item twice (one row per item)" % name)
    for a in agents:
        if a not in cat.merc_ids or "UnlockMerc_%d" % a not in nosource.get("mercs", ()):
            raise ContentError("collector: agent %d is not a displayed, released no-source agent" % a)

    # Paid tier (§1.5): what WeaponCamo_Random_Random / MercAccessory_Random_Random draw from, per agent. Both must equal
    # the unlocks.json groups (minus the hidden jerseys), which the client's own tables decide.
    camo_tables = dict((r["WeaponId"], r["SkinsTableName"]) for r in _design_rows(cat, "CamosListPerWeapon"))
    camos = {}
    for m in cat.merc_ids:
        weapon = cat.merc_weapons.get(m)
        if weapon not in camo_tables:
            raise ContentError("collector: agent %d: no CamosListPerWeapon row for its weapon %s" % (m, weapon))
        pool = sorted(int(r["ID"]) for r in _design_rows(cat, camo_tables[weapon]) if not _flag(r.get("IsKayac")))
        if pool != sorted(cat.camos.get(m, [])):
            raise ContentError("collector: agent %d: the camo pool of %s differs from the unlocks.json camos group"
                               % (m, camo_tables[weapon]))
        camos[str(m)] = compress_ranges(pool)
    visible = sorted(j for j, r in accessories.items() if _flag(r.get("VisibleInShop")) and not _flag(r.get("IsKayac")))
    for j in visible:
        if accessories[j].get("MercId") not in (None, ""):
            raise ContentError("collector: jersey %d is agent-specific (MercId): collector.json has one jersey pool for "
                               "every agent" % j)
    not_visible = set(j for j, r in accessories.items() if not _flag(r.get("VisibleInShop")))
    for m in cat.merc_ids:
        if set(cat.accessories.get(m, [])) - not_visible != set(visible):
            raise ContentError("collector: agent %d: the visible jersey pool differs from the unlocks.json accessories group" % m)

    # Exhausted row (D7): a currency tier row (the setting economy.collector.exhaustedReward replaces it at run time).
    exhausted_type, _, exhausted_tier = str(tuning["exhausted"]).partition("_")
    tiers_table = exhausted_type + "Tiers"
    if exhausted_tier not in [r["Id"] for r in _design_rows(cat, tiers_table)]:
        raise ContentError("collector: exhausted reward %s has no %s row" % (tuning["exhausted"], tiers_table))

    rows = []

    def row(category, kind, sub, sub2=None, bundle=None, merc=None, item=None):
        r = {"id": len(rows) + 1, "category": category, "type": kind, "subtype": sub}
        for key, value in (("subtype2", sub2), ("bundle", bundle), ("merc", merc), ("item", item)):
            if value is not None:
                r[key] = value
        rows.append(r)

    order = list(tuning["rows"])
    if sorted(order) != sorted(COLLECTOR_CATEGORIES):
        raise ContentError("collector: rows must list each of %s once" % ", ".join(COLLECTOR_CATEGORIES))
    for category in order:
        if category == "avatars":
            row(category, "KillSignature", "Avatar", "Random")
        elif category == "backgrounds":
            row(category, "KillSignature", "Background", "Random")
        elif category == "sounds":
            # Carriers (D8): a random-avatar row that always also gives its sound.
            for s in sounds:
                row(category, "KillSignature", "Avatar", "Random", bundle="KillSignature_Sound_%d" % s, item=s)
        elif category == "skins":
            for m, s in skins:
                row(category, "MercSkin", str(m), str(s), merc=m, item=s)
        elif category == "hiddenJerseys":
            for j in hidden:
                row(category, "MercAccessory", "Random", str(j), item=j)
        elif category == "agents":
            for a in agents:
                row(category, "UnlockMerc", str(a), merc=a)
        elif category == "camos":
            row(category, "WeaponCamo", "Random", "Random")
        elif category == "jerseys":
            row(category, "MercAccessory", "Random", "Random")
        else:
            row(category, exhausted_type, exhausted_tier)

    # Season (§2.4, §2.5): the tier of every division from the crates its ranks 1-30 get.
    tiers = list(season["tiers"])
    by_rank = dict((int(r["Rank"]), r) for r in _design_rows(cat, "PVPDivisionRewardsType"))
    division_tier = []
    for d in cat.divisions:
        field = "Division%02d" % d
        tier = None
        for place, first, last in SEASON_PLACES:
            for rank in range(first, last + 1):
                name = (by_rank.get(rank) or {}).get(field) or ""
                found = re.match(r"^Crate_(\w+)_%s$" % place, name)
                if not found or (tier is not None and found.group(1) != tier):
                    raise ContentError("collector: PVPDivisionRewardsType rank %d of division %d is '%s', not the division's "
                                       "Crate_<tier>_%s" % (rank, d, name, place))
                tier = found.group(1)
        if tier not in tiers:
            raise ContentError("collector: division %d uses tier %s, which season.tiers does not list" % (d, tier))
        division_tier.append(tiers.index(tier))
    if sorted(set(division_tier)) != list(range(len(tiers))) or division_tier != sorted(division_tier):
        raise ContentError("collector: season.tiers must list the %d division tiers in division order (it has %d)"
                           % (len(set(division_tier)), len(tiers)))
    retarget = [["Crate_%s_%s" % (t, p) for p, _, _ in SEASON_PLACES[3:]] for t in tiers]
    placement = [["Crate_%s_%s" % (t, p) for p, _, _ in SEASON_PLACES[:3]] for t in tiers]
    for name in [c for group in retarget + placement for c in group]:
        if name not in loot:
            raise ContentError("collector: tier crate %s has no LootCrates row" % name)
    for name in [c for group in placement for c in group]:
        content = loot[name]["ContentTableName"]
        _require_fields(cat, content, CONTENT_FIELDS)
        weighted = [r for r in _design_rows(cat, content) if _number(r.get("Weight.1")) > 0]
        if len(weighted) != 1 or int(weighted[0]["Id"]) != 1 or weighted[0].get("Type") != "HardCurrency":
            raise ContentError("collector: %s row 1 must be the only Weight.1 row, a HardCurrency one (its bundle is the "
                               "season emblem)" % content)

    _require_fields(cat, "PlayerLevelXP", ("Level", "AvatarRewards"))
    levels = [int(r["Level"]) for r in _design_rows(cat, "PlayerLevelXP") if int(r["Level"]) >= 2]

    starting_av = set(a for a in av if "KillSignature_Avatar_%d" % a in cat.default_signatures)
    starting_bg = set(b for b in bg if "KillSignature_Background_%d" % b in cat.default_signatures)
    base_items = len(base) + len(bg_base) + len(sounds) + len(skins) + hidden_pairs + len(agents)
    # What a fresh profile drains from the base tier: the random rows also reach the other unlockable signatures.
    base_tier = (len((set(drawable) | set(unlock)) - starting_av) + len((set(bg_drawable) | set(bg_unlock)) - starting_bg)
                 + len(sounds) + len(skins) + hidden_pairs + len(agents))
    paid_pairs = sum(len(expand_ranges(v)) for v in camos.values()) + len(visible) * len(cat.merc_ids)
    vip_only = sum(len(v) for v in cat.paid["camos"].values()) + sum(len(v) for v in cat.paid["accessories"].values())

    doc = {
        "format": FORMAT,
        "crate": {"id": crate["id"], "table": table, "locId": crate["locId"], "image": crate["image"]},
        "rows": rows,
        "avatars": {"base": compress_ranges(base), "drawable": compress_ranges(drawable), "unlock": compress_ranges(unlock),
                    "lock": compress_ranges(lock), "kayac": compress_ranges(kayac), "reserved": compress_ranges(reserved)},
        "backgrounds": {"base": compress_ranges(bg_base), "drawable": compress_ranges(bg_drawable),
                        "unlock": compress_ranges(bg_unlock)},
        "sounds": sounds,
        "camos": camos,
        "jerseys": compress_ranges(visible),
        "season": {"tiers": tiers, "divisionTier": division_tier, "retarget": retarget, "placement": placement,
                   "emblems": emblems},
        "levels": compress_ranges(levels),
        "summary": {"baseItems": base_items, "baseTier": base_tier, "paidPairs": paid_pairs, "vipOnlyPairs": vip_only,
                    "kayacAvatars": len([a for a in kayac if a in released_av])},
    }
    check_collector(cat, doc)
    return doc


def check_collector(cat, doc):
    """The invariants of collector.json (economy-collector.md §3.4), checked on the document itself so that neither a
    generator change nor a tuning edit can ship a crate the client cannot open or a set that breaks O5/D4."""
    rows = doc["rows"]
    table_ids = set(int(r["Id"]) for r in _design_rows(cat, doc["crate"]["table"]))
    if not rows or len(rows) > MAX_CRATE_ROWS:
        raise ContentError("collector: %d crate rows (1..%d)" % (len(rows), MAX_CRATE_ROWS))
    for i, r in enumerate(rows, 1):
        if r.get("id") != i or i not in table_ids:
            raise ContentError("collector: row %s: ids must run 1..n over existing rows of %s" % (r.get("id"), doc["crate"]["table"]))
        problem = collector_row_problem(r)
        if problem:
            raise ContentError("collector: row %d: %s" % (i, problem))

    av = dict((int(r["ID"]), r) for r in _design_rows(cat, "KillsignatureAvatars"))
    released_av = set(a for a, _ in cat.avatars)
    sets = dict((k, set(expand_ranges(v))) for k, v in doc["avatars"].items())
    for key in ("base", "drawable", "unlock", "lock", "reserved"):
        # released excludes the never-released ids (1513-1518 in 1.17.32): never in base, unlock or lock
        bad = sets[key] - released_av
        if bad:
            raise ContentError("collector: avatars.%s holds unknown or unreleased avatars %s" % (key, compress_ranges(bad)))
    if sets["kayac"] - set(av):
        raise ContentError("collector: avatars.kayac holds unknown avatars")
    unlockable = lambda aid: _flag(av[aid].get("Unlockable"))
    for key, other in (("base", "kayac"), ("base", "reserved"), ("drawable", "kayac"), ("drawable", "reserved")):
        if sets[key] & sets[other]:
            raise ContentError("collector: avatars %s in both %s and %s" % (compress_ranges(sets[key] & sets[other]), key, other))
    if not sets["unlock"] <= sets["base"] or any(unlockable(a) for a in sets["unlock"]):
        raise ContentError("collector: avatars.unlock must be the base avatars with Unlockable=false")
    if sets["lock"] != set(a for a in sets["kayac"] & released_av if unlockable(a)):
        raise ContentError("collector: avatars.lock must be the released Kayac avatars with Unlockable=true")

    bg = dict((int(r["ID"]), r) for r in _design_rows(cat, "KillsignatureBackgrounds"))
    released_bg = set(b for b, _ in cat.backgrounds)
    bgs = dict((k, set(expand_ranges(v))) for k, v in doc["backgrounds"].items())
    for key, ids in bgs.items():
        if ids - released_bg:
            raise ContentError("collector: backgrounds.%s holds unknown or unreleased backgrounds" % key)
    if not bgs["unlock"] <= bgs["base"] or any(_flag(bg[b].get("Unlockable")) for b in bgs["unlock"]):
        raise ContentError("collector: backgrounds.unlock must be the base backgrounds with Unlockable=false")


def check_collector_rewards(liveops, collector):
    """The crates the liveops.json collector block hands out are the collector's crate (what CollectorRuntime guards)."""
    block = liveops.get("collector")
    if block is None:
        return
    lists = [block["dailyBonus"], block["weeklyFinal"]] + block["weeklyMilestones"]
    for r in [r for rewards in lists for r in rewards]:
        if r["item"] not in LIVEOPS_CURRENCIES and r["item"] != collector["crate"]["id"]:
            raise ContentError("liveops: the collector block hands out %s, not the collector's crate %s"
                               % (r["item"], collector["crate"]["id"]))


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


def generate(decoded_dir, pricing_path, liveops_path=None, collector_path=None, now=None):
    """{file name: text} for server/Content. ``now`` (UTC, default the current time) only feeds warnings."""
    if now is None:
        now = datetime.datetime.now(datetime.timezone.utc)
    catalog = load_json(os.path.join(decoded_dir, "_catalog.json"))
    tables = load_json(os.path.join(decoded_dir, "_tables.json"))
    design = {}
    design_dir = os.path.join(decoded_dir, "Design")
    for db in ("MCVS_Meta",):
        path = os.path.join(design_dir, db + ".xlsx")
        if os.path.exists(path):
            design[db] = load_json(path)
    pricing = load_json(pricing_path)
    here = os.path.dirname(os.path.abspath(__file__))
    if liveops_path is None:
        liveops_path = os.path.join(here, "liveops_tuning.json")
    liveops_tuning = load_json(liveops_path)
    if collector_path is None:
        collector_path = os.path.join(here, "collector_tuning.json")
    collector_tuning = load_json(collector_path)
    loc_tables = _loc_tables(decoded_dir)
    inputs = ["_catalog.json", "_tables.json"]
    if loc_tables is not None:
        inputs.append("Localization/*_en.json (ids only)")
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
    liveops = build_liveops(cat, liveops_tuning, loc_tables, now)
    collector = build_collector(cat, collector_tuning, unlocks, loc_tables)
    check_collector_rewards(liveops, collector)

    files = {
        "offline_store.json": store,
        "unlocks.json": unlocks,
        "coupons.json": coupons,
        "economy.json": economy,
        "crm_overlay.json": overlay,
        "liveops.json": liveops,
        "collector.json": collector,
    }
    manifest = {
        "schema": FORMAT,
        "generator": GENERATOR,
        "source": {"game": "Modern Combat Versus", "version": cat.game_version,
                   "inputs": inputs + (["Design/MCVS_Meta.xlsx (index columns; ids and flags for the collector)"] if design else []),
                   "pricing": "tools/content-gen/pricing.json", "liveops": "tools/content-gen/liveops_tuning.json",
                   "collector": "tools/content-gen/collector_tuning.json"},
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
    if name == "liveops.json":
        return len(data["objectives"])
    if name == "collector.json":
        return len(data["rows"])
    return None


def main(argv=None):
    here = os.path.dirname(os.path.abspath(__file__))
    repo = os.path.normpath(os.path.join(here, "..", ".."))
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--decoded", default=os.environ.get("MCVS_DATAPACK_DECODED", "/home/user/mcvs-work/datapack-decoded"),
                        help="decoded data pack (with _catalog.json and _tables.json)")
    parser.add_argument("--pricing", default=os.path.join(here, "pricing.json"))
    parser.add_argument("--liveops", default=os.path.join(here, "liveops_tuning.json"))
    parser.add_argument("--collector", default=os.path.join(here, "collector_tuning.json"))
    parser.add_argument("--out", default=os.path.join(repo, "server", "Content"))
    parser.add_argument("--check", action="store_true", help="only verify that --out is up to date")
    args = parser.parse_args(argv)

    try:
        outputs = generate(args.decoded, args.pricing, args.liveops, args.collector)
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
