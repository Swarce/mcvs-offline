#!/usr/bin/env python3
"""Unit tests for content_gen.py on a small synthetic catalogue (no game data needed).

Run isolated: python3 -I tools/content-gen/test_content_gen.py
"""

import importlib.util
import json
import os
import shutil
import sys
import tempfile
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True  # no __pycache__ next to the generator in the repository


def load_module():
    # -I keeps the script directory off sys.path, so load the generator by path.
    spec = importlib.util.spec_from_file_location("content_gen", os.path.join(HERE, "content_gen.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


cg = load_module()


def sample_catalog():
    return {
        "format": "mcvs-catalog/1",
        "game_version": "test",
        "categories": {
            "mercs": [
                {"id": 0, "rarity": "Rarity01", "displayed": "YES"},
                {"id": 1, "item_id": "UnlockMerc_1", "rarity": "Rarity01", "displayed": "YES",
                 "unlock_kinds": ["offline-store", "starting-inventory"]},
                {"id": 2, "item_id": "UnlockMerc_2", "rarity": "Rarity06", "displayed": "YES"},
                {"id": 3, "item_id": "UnlockMerc_3", "rarity": "Rarity03", "displayed": "NO"},
            ],
            "skins": [
                {"merc_id": 1, "skin_id": 1, "item_id": None, "locked_flag": "false", "unlock_kinds": ["default"]},
                {"merc_id": 1, "skin_id": 2, "item_id": "MercSkin_1_2", "locked_flag": "false", "unlock_kinds": [], "exclusive": None},
                {"merc_id": 1, "skin_id": 3, "item_id": "MercSkin_1_3", "locked_flag": "false", "unlock_kinds": [], "exclusive": "crate-only"},
                {"merc_id": 2, "skin_id": 4, "item_id": "MercSkin_2_4", "locked_flag": "true", "unlock_kinds": []},
                {"merc_id": 2, "skin_id": 5, "item_id": "MercSkin_2_5", "in_design_tables": False},
            ],
            "weapons": [
                {"id": 1, "merc_id": 1, "is_default": "YES", "fragments_required": 0},
                {"id": 7, "merc_id": 1, "is_default": "NO", "fragments_required": 80},
                {"id": 9, "merc_id": 2, "is_default": "NO", "fragments_required": 10},
            ],
            "camos": [
                {"merc_id": 1, "camo_id": 2, "item_id": "WeaponCamo_1_2", "kayac_only": False, "unlock_kinds": ["event-crate"]},
                {"merc_id": 1, "camo_id": 3, "item_id": "WeaponCamo_1_3", "kayac_only": False,
                 "random_pool_kinds": ["vip-subscription-reward"]},
                {"merc_id": 1, "camo_id": 4, "item_id": "WeaponCamo_1_4", "kayac_only": False,
                 "unlock_kinds": ["daily-shop"], "random_pool_kinds": ["vip-subscription-reward"]},
                {"merc_id": 1, "camo_id": 9, "item_id": "WeaponCamo_1_9", "kayac_only": False},
                {"merc_id": 1, "camo_id": 10, "item_id": "WeaponCamo_1_10", "kayac_only": True,
                 "random_pool_kinds": ["vip-subscription-reward"]},
                {"item_id": "WeaponCamo_2_99", "in_design_tables": False},
            ],
            "accessories_jerseys": [
                {"id": 1, "kayac_only": False, "visible_in_shop": "true", "item_ids": ["MercAccessory_1_1", "MercAccessory_2_1"]},
                {"id": 2, "kayac_only": True, "visible_in_shop": "true", "item_ids": ["MercAccessory_1_2", "MercAccessory_2_2"]},
                {"id": 3, "kayac_only": False, "visible_in_shop": "true", "unlock_kinds": ["event-crate"],
                 "item_ids": ["MercAccessory_1_3"]},
                {"id": 4, "kayac_only": False, "visible_in_shop": "false", "item_ids": ["MercAccessory_2_4"]},
            ],
            "kill_signatures": {
                "avatars": [
                    {"id": 1, "item_id": "KillSignature_Avatar_1", "rarity": "Rarity01", "unlock_kinds": ["starting-inventory"]},
                    {"id": 2, "item_id": "KillSignature_Avatar_2", "rarity": "Rarity04", "unlock_kinds": []},
                    # in the tables, never released: no ecomm id, no source; asset on disk / missing
                    {"id": 3, "item_id": None, "rarity": "Rarity01", "unlock_kinds": [], "asset": "Menus/x/emblem3.png"},
                    {"id": 4, "item_id": None, "rarity": "Rarity01", "unlock_kinds": [], "asset": "Menus/x/missing.png"},
                ],
                "backgrounds": [
                    {"id": 1, "item_id": "KillSignature_Background_1", "purchasable": "true", "unlock_kinds": ["starting-inventory"]},
                    {"id": 2, "item_id": "KillSignature_Background_2", "purchasable": "false", "unlock_kinds": []},
                ],
                "sounds": [
                    {"id": 1, "item_id": "KillSignature_Sound_1", "unlock_kinds": ["starting-inventory"]},
                    {"id": 2, "item_id": "KillSignature_Sound_2", "unlock_kinds": []},
                ],
            },
            "mods": [{"item_id": "Mod_1_HealthTier1-1"}, {"item_id": "Mod_1_HealthTier4-1"}, {"item_id": "Mod_Random"}],
            "gear_slot_unlocks": [{"item_id": "GearSlot_1_1"}, {"item_id": "GearSlot_1_2"}, {"item_id": "GearSlot_2_1"},
                                  {"item_id": "GearSlot_3_1"}],
            "crates": [{"id": "Crate_MOD_T1"}],
            "subscriptions_vip": [{"item_id": "SubscriptionCard_30"}],
            "divisions": [{"id": 1}, {"id": 2}],
            "player_levels": [{"level": 1}, {"level": 2}],
        },
    }


def sample_tables():
    fields = ["Division", "VictoryAmount", "DefeatAmount", "DailyCap", "MVPAmount"]
    return {"databases": {"Design/MCVS_Meta.xlsx": {
        "CreditsCurrencyMatchRewards": {"rows": 2, "fields": fields},
        "VersusCoinsMatchRewards": {"rows": 2, "fields": fields},
        "PlayerLevelXP": {"rows": 2, "fields": ["Level", "XPRequired"]},
        "AgentsMasteryXP": {"rows": 3, "fields": ["Level", "XPCost"]},
    }}}


def sample_pricing():
    with open(os.path.join(HERE, "pricing.json"), "r", encoding="utf-8") as f:
        pricing = json.load(f)
    # Pin the values the assertions use, so retuning pricing.json does not break the tests.
    pricing["crates"] = [["Crate_MOD_T1", 600]]
    pricing["mercs"]["byRarity"]["Rarity06"] = 12000
    pricing["hardCurrencyAlternative"]["versusCoinsPerHardCurrency"] = 10
    pricing["weapons"] = {"perFragment": 75, "minimum": 3000}
    pricing["camos"] = {"mystery": 75, "packSize": 10, "packDiscount": 0.1}
    pricing["skins"] = {"price": 3000, "crateOnly": 5000, "includeLocked": False}
    return pricing


class GeneratorTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="content-gen-test-")
        with open(os.path.join(self.tmp, "_catalog.json"), "w") as f:
            json.dump(sample_catalog(), f)
        with open(os.path.join(self.tmp, "_tables.json"), "w") as f:
            json.dump(sample_tables(), f)
        self.pricing_path = os.path.join(self.tmp, "pricing.json")
        with open(self.pricing_path, "w") as f:
            json.dump(sample_pricing(), f)
        h = "%x" % zlib.crc32(b"Menus/x/emblem3.png")
        with open(os.path.join(self.tmp, "names.json"), "w") as f:
            json.dump({"files": {h[0] + "/" + h[1:] + ".f": {}}}, f)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def outputs(self):
        return {k: json.loads(v) for k, v in cg.generate(self.tmp, self.pricing_path).items()}

    def test_offline_store_covers_every_category(self):
        store = self.outputs()["offline_store.json"]["prices"]
        names = [p.get("group") or p["item"] for p in store]
        self.assertEqual(len(names), len(set(names)))
        # displayed, non-placeholder mercs only, priced by rarity with an HC alternative
        self.assertIn("UnlockMerc_1", names)
        self.assertIn("UnlockMerc_2", names)
        self.assertNotIn("UnlockMerc_0", names)
        self.assertNotIn("UnlockMerc_3", names)
        merc2 = store[names.index("UnlockMerc_2")]["billing_methods"][0]["price"]
        self.assertEqual(merc2, [{"currency": "VersusCoins", "price": 12000}, {"currency": "HardCurrency", "price": 1200}])
        # skins: no default, no locked, no ecomm-only; crate-only skins cost more
        self.assertIn("MercSkin_1_2", names)
        self.assertNotIn("MercSkin_2_4", names)
        self.assertNotIn("MercSkin_2_5", names)
        self.assertGreater(store[names.index("MercSkin_1_3")]["billing_methods"][0]["price"][0]["price"],
                           store[names.index("MercSkin_1_2")]["billing_methods"][0]["price"][0]["price"])
        # weapons priced by fragments with a floor
        w7 = store[names.index("MercWeapon_1_7")]["billing_methods"][0]["price"][0]["price"]
        w9 = store[names.index("MercWeapon_2_9")]["billing_methods"][0]["price"][0]["price"]
        self.assertEqual(w7, 6000)
        self.assertEqual(w9, 3000)
        # mystery camos/accessories and packs only for mercs that have any
        self.assertIn("WeaponCamo_1_Random", names)
        self.assertNotIn("WeaponCamo_2_Random", names)
        self.assertIn("MCVSPack_Camo_1_10", names)
        pack = store[names.index("MCVSPack_Camo_1_10")]
        self.assertEqual(len(pack["bundle"]), 10)
        self.assertEqual(pack["billing_methods"][0]["price"][0]["price"], 675)
        self.assertIn("MercAccessory_2_Random", names)
        # sounds: the starting one is not sold
        self.assertIn("KillSignature_Sound_2", names)
        self.assertNotIn("KillSignature_Sound_1", names)
        # gear slots only for listed mercs
        self.assertIn("GearSlot_1_2", names)
        self.assertNotIn("GearSlot_3_1", names)
        # hub entries carry the #VersusCoins category, screen entries do not
        for p in store:
            name = p.get("group") or p["item"]
            hub = p["category"] == ["#VersusCoins"]
            on_screen = name.startswith(("UnlockMerc_", "MercSkin_", "KillSignature_Avatar_", "KillSignature_Background_"))
            self.assertEqual(hub, not on_screen, name)
            self.assertEqual(p["billing_methods"][0]["replaced_price"], [])

    def test_unlock_groups_are_ordered_and_compressed(self):
        out = self.outputs()
        unlocks = out["unlocks.json"]
        self.assertEqual(unlocks["order"], ["mercs", "weapons", "gearSlots", "signatures", "camos", "accessories", "skins"])
        groups = {g["id"]: g for g in unlocks["groups"]}
        # Explicit per-merc rewards, never UnlockMerc_ALL(_MAX): every reward display looks the merc up by id and
        # "ALL" is no merc (device test 3 crash). The starting merc is not unlocked again (its alternate reward would
        # be 40 random gear), but its mods are.
        self.assertEqual(groups["mercs"]["items"], ["UnlockMerc_{2}", "ModTier_{1-2}_4"])
        self.assertEqual(groups["mercs"]["count"], 3)
        self.assertEqual([cg.expand_pattern(p) for p in groups["mercs"]["items"]],
                         [["UnlockMerc_2"], ["ModTier_1_4", "ModTier_2_4"]])
        self.assertEqual(groups["camos"]["items"], ["WeaponCamo_1_{2-4,9}"])
        self.assertEqual(groups["camos"]["count"], 4)
        self.assertEqual(groups["accessories"]["items"], ["MercAccessory_1_{1,3}", "MercAccessory_2_{1,4}"])
        self.assertEqual(groups["signatures"]["items"],
                         ["KillSignature_Avatar_{2}", "KillSignature_Background_{2}", "KillSignature_Sound_{2}"])
        self.assertEqual(groups["gearSlots"]["items"], ["GearSlot_1_{1-2}", "GearSlot_2_{1}"])
        for code in out["coupons.json"]["codes"].values():
            for g in code["groups"]:
                self.assertIn(g, groups)

    def test_paid_and_unreleased_sets(self):
        out = self.outputs()
        unlocks = out["unlocks.json"]
        groups = {g["id"]: g for g in unlocks["groups"]}
        # opt-in groups are never part of the full unlock (settings.unlockAll grants "order" only)
        for g in ("mercUnlocks", "paidCamos", "paidAccessories", "unreleased"):
            self.assertIn(g, groups)
            self.assertNotIn(g, unlocks["order"])
        self.assertEqual(groups["mercUnlocks"]["items"], ["UnlockMerc_{2}"])
        # only-VIP camo 3 (camo 4 is also in the daily shop, kayac camo 10 is hidden)
        self.assertEqual(groups["paidCamos"]["items"], ["WeaponCamo_1_{3}"])
        # jersey 1 has no source but the VIP pool; 3 has an event crate; 4 is not VisibleInShop (not in the pool)
        self.assertEqual(groups["paidAccessories"]["items"], ["MercAccessory_1_{1}", "MercAccessory_2_{1}"])
        # avatar 3: no ecomm id, no source, asset on disk; avatar 4 lacks its asset: excluded everywhere
        self.assertEqual(groups["unreleased"]["items"], ["KillSignature_Avatar_{3}"])
        self.assertEqual(groups["signatures"]["items"][0], "KillSignature_Avatar_{2}")
        names = [p.get("group") or p["item"] for p in out["offline_store.json"]["prices"]]
        self.assertNotIn("KillSignature_Avatar_3", names)
        self.assertNotIn("KillSignature_Avatar_4", names)
        reasons = {r["reason"].split(":")[0]: r for r in unlocks["excluded"]}
        self.assertIn("KillSignature_Avatar_4", [i for r in unlocks["excluded"] for i in r.get("items", [])])
        self.assertIn("UnlockMerc_0", [i for r in unlocks["excluded"] for i in r.get("items", [])])
        self.assertTrue(any("Kayac" in r for r in reasons))
        codes = out["coupons.json"]["codes"]
        self.assertEqual(codes["UNLOCKPAID"]["groups"], ["mercUnlocks", "paidCamos", "paidAccessories"])
        self.assertEqual(codes["UNLOCKUNRELEASED"]["groups"], ["unreleased"])
        for code in ("UNLOCKALL", "UNLOCKMERCS", "UNLOCKCAMOS", "UNLOCKACCESSORIES", "UNLOCKSIGNATURES"):
            self.assertNotIn("unreleased", codes[code]["groups"])
        # without names.json nothing can be verified: no unreleased item at all
        os.remove(os.path.join(self.tmp, "names.json"))
        groups = {g["id"]: g for g in self.outputs()["unlocks.json"]["groups"]}
        self.assertEqual(groups["unreleased"]["items"], [])

    def test_economy_targets_reference_existing_fields(self):
        economy = self.outputs()["economy.json"]
        vc = economy["multipliers"]["versusCoins"][0]
        self.assertEqual(vc["index"], "Division")
        self.assertEqual(vc["ids"], [1, 2])
        self.assertEqual(vc["capFields"], ["DailyCap"])
        mastery = economy["multipliers"]["xp"][1]
        self.assertEqual(mastery["ids"], [1, 2, 3])  # no Design file: 1..rows
        self.assertTrue(mastery["inverse"])

    def test_output_is_deterministic(self):
        a = cg.generate(self.tmp, self.pricing_path)
        b = cg.generate(self.tmp, self.pricing_path)
        self.assertEqual(a, b)
        for text in a.values():
            self.assertTrue(text.endswith("\n"))
            json.loads(text)

    def test_unknown_crate_is_an_error(self):
        pricing = sample_pricing()
        pricing["crates"] = [["Crate_DoesNotExist", 1]]
        with open(self.pricing_path, "w") as f:
            json.dump(pricing, f)
        with self.assertRaises(cg.ContentError):
            cg.generate(self.tmp, self.pricing_path)

    def test_check_mode_reports_stale_files(self):
        out_dir = os.path.join(self.tmp, "out")
        os.makedirs(out_dir)
        args = ["--decoded", self.tmp, "--pricing", self.pricing_path, "--out", out_dir]
        self.assertEqual(cg.main(args + ["--check"]), 1)
        self.assertEqual(cg.main(args), 0)
        self.assertEqual(cg.main(args + ["--check"]), 0)

    def test_helpers(self):
        self.assertEqual(cg.compress_ranges([5, 1, 2, 3, 9, 9]), "1-3,5,9")
        self.assertEqual(cg.expand_pattern("A_{1-3,7}"), ["A_1", "A_2", "A_3", "A_7"])
        self.assertEqual(cg.expand_pattern("B"), ["B"])
        self.assertEqual(cg.round_price(1234, 5), 1235)
        self.assertEqual(cg.round_price(1, 5), 5)


if __name__ == "__main__":
    result = unittest.main(exit=False, verbosity=1).result
    sys.exit(0 if result.wasSuccessful() else 1)
