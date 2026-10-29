#!/usr/bin/env python3
"""Unit tests for content_gen.py on a small synthetic catalogue (no game data needed), plus the exact collector counts
of docs/design/economy-collector.md on the player's decoded data pack when it is available (MCVS_DATAPACK_DECODED or
the work tree default; skipped otherwise).

Run isolated: python3 -I tools/content-gen/test_content_gen.py
"""

import contextlib
import copy
import datetime
import importlib.util
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
DECODED = os.environ.get("MCVS_DATAPACK_DECODED") or "/home/user/mcvs-work/datapack-decoded"
UTC = datetime.timezone.utc
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
                {"id": 1, "item_id": "UnlockMerc_1", "rarity": "Rarity01", "displayed": "YES", "vanilla_weapon": "AR02",
                 "unlock_kinds": ["offline-store", "starting-inventory"]},
                # no shipped source: the collector's agent row
                {"id": 2, "item_id": "UnlockMerc_2", "rarity": "Rarity06", "displayed": "YES", "vanilla_weapon": "SR01"},
                {"id": 3, "item_id": "UnlockMerc_3", "rarity": "Rarity03", "displayed": "NO", "vanilla_weapon": "SG01"},
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
                 "item_ids": ["MercAccessory_1_3", "MercAccessory_2_3"]},
                # not VisibleInShop, no source: the collector's hidden jersey row
                {"id": 4, "kayac_only": False, "visible_in_shop": "false", "item_ids": ["MercAccessory_2_4"]},
            ],
            "kill_signatures": {
                "avatars": [
                    {"id": 1, "item_id": "KillSignature_Avatar_1", "rarity": "Rarity01", "unlock_kinds": ["starting-inventory"]},
                    {"id": 2, "item_id": "KillSignature_Avatar_2", "rarity": "Rarity04", "unlock_kinds": []},
                    # in the tables, never released: no ecomm id, no source; asset on disk / missing
                    {"id": 3, "item_id": None, "rarity": "Rarity01", "unlock_kinds": [], "asset": "Menus/x/emblem3.png"},
                    {"id": 4, "item_id": None, "rarity": "Rarity01", "unlock_kinds": [], "asset": "Menus/x/missing.png"},
                ] + [
                    # 5 no source; 6 sold; 7-11 Kayac by folder, subfolder, folder, name, extra id; 12-19 season emblems
                    # I, I_1..I_3, II, II_1..II_3 (sample_design has the assets and Unlockable flags)
                    {"id": i, "item_id": "KillSignature_Avatar_%d" % i, "rarity": "Rarity01",
                     "unlock_kinds": ["offline-store"] if i == 6 else []} for i in range(5, 20)
                ] + [
                    # Kayac2, never released (no ecomm id, asset missing)
                    {"id": 20, "item_id": None, "rarity": "Rarity01", "unlock_kinds": [],
                     "asset": "Menus/assets/png/profile/Kayac2/ID9_Emblem.png"},
                ],
                "backgrounds": [
                    {"id": 1, "item_id": "KillSignature_Background_1", "purchasable": "true", "unlock_kinds": ["starting-inventory"]},
                    {"id": 2, "item_id": "KillSignature_Background_2", "purchasable": "false", "unlock_kinds": []},
                    {"id": 3, "item_id": "KillSignature_Background_3", "purchasable": "true", "unlock_kinds": []},
                    {"id": 4, "item_id": "KillSignature_Background_4", "purchasable": "true", "unlock_kinds": ["offline-store"]},
                    {"id": 5, "item_id": "KillSignature_Background_5", "purchasable": "true", "unlock_kinds": ["offline-store"]},
                ],
                "sounds": [
                    {"id": 1, "item_id": "KillSignature_Sound_1", "unlock_kinds": ["starting-inventory"]},
                    {"id": 2, "item_id": "KillSignature_Sound_2", "unlock_kinds": []},
                ],
            },
            "mods": [{"item_id": "Mod_1_HealthTier1-1"}, {"item_id": "Mod_1_HealthTier4-1"}, {"item_id": "Mod_Random"}],
            "gear_slot_unlocks": [{"item_id": "GearSlot_1_1"}, {"item_id": "GearSlot_1_2"}, {"item_id": "GearSlot_2_1"},
                                  {"item_id": "GearSlot_3_1"}],
            "crates": [{"id": "Crate_MOD_T1"}, {"id": "Crate_Contracts"}, {"id": "Crate_Event_10"}, {"id": "Crate_Event_2"},
                       {"id": "Crate_Event_1"}, {"id": "Crate_Event_Special"}],
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
        "PlayerLevelXP": {"rows": 2, "fields": ["Level", "XPRequired", "DivisionTrophiesBonus"]},
        "AgentsMasteryXP": {"rows": 3, "fields": ["Level", "XPCost"]},
        "PVPDivisionRewards": {"rows": 3, "fields": ["Rank", "Division01", "Division02"]},
    }}}


CONTENT = ["Id", "Type", "Subtype", "Subtype2", "BundledRewards", "Weight.1", "Weight.2", "FixedQuantity",
           "AlwaysGiveBundledRewards"]
TIERS = ["Starter", "Bronze"]  # division 1 Starter, division 2 Bronze
PLACES = ["FirstPlace", "SecondPlace", "ThirdPlace", "4To10Place", "11To20Place"]


def sample_design():
    """Design/MCVS_Meta.xlsx (decoded: {table: {"Fields", "Rows"}}) for the collector, matching sample_catalog."""
    def t(fields, rows):
        return {"Fields": fields, "Rows": rows}

    p = "Menus/assets/png/profile/"
    assets = {1: p + "avatars/avatar01.png", 2: p + "avatars/avatar02.png", 3: p + "avatars/emblem3.png",
              4: p + "avatars/missing.png", 5: p + "avatars/avatar05.png", 6: p + "avatars/avatar06.png",
              7: p + "Kayac/emblem_JP_agent_cub.png", 8: p + "Kayac/holidays/emblem_Xmas.png",
              9: p + "Kayac2/ID1_Emblem.png", 10: p + "avatars/emblem_JP_KAYAC_guest.png",
              11: p + "avatars/emblem_JP_japancup.png", 20: p + "Kayac2/ID9_Emblem.png"}
    for i, name in enumerate(["I", "I_1", "I_2", "I_3", "II", "II_1", "II_2", "II_3"]):
        assets[12 + i] = p + "season_rewards/%s.png" % name
    unlockable = {1, 5, 6, 7, 8}
    crates = ["Crate_Event_Special"] + ["Crate_%s_%s" % (tier, place) for tier in TIERS for place in PLACES]
    tables = {
        "KillsignatureAvatars": t(["ID", "Asset", "Unlockable", "Rarity"],
                                  [[i, assets[i], str(i in unlockable).lower(), "Rarity01"] for i in sorted(assets)]),
        "KillsignatureBackgrounds": t(["ID", "Asset", "Displayed", "Purchasable", "Unlockable"],
                                      [[i, "b82448", "true", "true", str(i in (1, 3, 4)).lower()] for i in range(1, 6)]),
        "KillsignatureSounds": t(["ID", "Asset", "LocTag", "Displayed", "Purchasable", "Unlockable"],
                                 [[i, "sfx_ui_crate_reveal", "Sound0%d" % i, "true", "true", "true"] for i in (1, 2)]),
        "SkinsListPerMerc": t(["Id", "MercId", "SkinsTableName"], [[1, 1, "SkinsList_Merc01"], [2, 2, "SkinsList_Merc02"]]),
        "SkinsList_Merc01": t(["ID", "TagName", "Locked"], [[1, "a", "false"], [2, "b", "false"], [3, "c", "false"]]),
        "SkinsList_Merc02": t(["ID", "TagName", "Locked"], [[4, "d", "true"]]),
        "MercAccessories": t(["Id", "LocId", "MercId", "VisibleInShop", "IsKayac"],
                             [[1, "jersey.A", None, "true", "false"], [2, "jersey.B", None, "true", "true"],
                              [3, "jersey.C", None, "true", "false"], [4, "jersey.D", None, "false", "false"]]),
        "CamosListPerWeapon": t(["Id", "WeaponId", "SkinsTableName"], [[1, "AR02", "CamosList_AR02"], [2, "SR01", "CamosList_SR01"]]),
        "CamosList_AR02": t(["ID", "TagName", "IsKayac"], [[2, "a", "false"], [3, "b", "false"], [4, "c", "false"],
                                                           [9, "d", "false"], [10, "e", "true"]]),
        "CamosList_SR01": t(["ID", "TagName", "IsKayac"], []),
        "LootCrates": t(["Id", "Image", "ImageShop", "LocId", "ContentTableName", "RewardsCount", "CratesCount", "RewardChance.2"],
                        [[c, "x.png", "x.png", "Crate.X", c, 1, 1, 100] for c in crates]),
        "Crate_Event_Special": t(CONTENT, [[i, None, None, None, None, None, None, None, None] for i in range(1, 101)]),
        "PVPDivisionRewardsType": t(["Rank", "Division01", "Division02"],
                                    [[r] + [("Crate_%s_%s" % (tier, PLACES[min(r, 4) - 1 if r <= 10 else 4]) if r <= 30 else "Credits")
                                            for tier in TIERS] for r in range(1, 36)]),
        "PlayerLevelXP": t(["Level", "XPRequired", "AvatarRewards"], [[1, 0, 0], [2, 200, 0]]),
        "VersusCoinsTiers": t(["Id", "QuantityMin", "QuantityMax"], [["Tiers%d" % i, 10 * i, 10 * i] for i in range(7)]),
    }
    for tier in TIERS:
        for place in PLACES:
            name = "Crate_%s_%s" % (tier, place)
            if place in ("FirstPlace", "SecondPlace", "ThirdPlace"):
                tables[name] = t(CONTENT, [[1, "HardCurrency", None, None, None, 1000, 0, 60, None],
                                           [2, "Gear_Random_1_Random", None, None, None, 0, 0, 0, None]])
            else:
                tables[name] = t(CONTENT, [[1, "Gear_Random_3_Random", None, None, None, 0, 1000, 0, None]])
    return tables


def sample_collector():
    return {
        "crate": {"id": "Crate_Event_Special", "locId": "Crate.Cosmetic_Crate", "image": "Menus/x/crate.png"},
        "kayac": {"folders": ["Menus/assets/png/profile/Kayac/", "Menus/assets/png/profile/Kayac2/"],
                  "nameContains": "kayac", "extra": [11]},
        "reservePlacementEmblems": True,
        "sounds": [2],
        "skins": [[1, 2], [1, 3]],
        "hiddenJerseys": [4],
        "agents": [2],
        "exhausted": "VersusCoins_Tiers4",
        "rows": ["avatars", "backgrounds", "sounds", "skins", "hiddenJerseys", "agents", "camos", "jerseys", "exhausted"],
        "season": {"tiers": list(TIERS), "numerals": ["I", "II"], "emblemFolder": "Menus/assets/png/profile/season_rewards/"},
    }


def collector_block(since="2030-01-07T00:00:00Z"):
    """The shipped placement: one crate per milestone, so none on the daily bonus (Crate_Contracts) or the weekly final
    (the event crate); the final's two on tier 3."""
    crate = [{"item": "Crate_Event_Special", "quantity": 1}]
    return {"since": since, "dailyBonus": [], "dailyMission": crate,
            "weeklyMilestones": [crate, crate, [{"item": "Crate_Event_Special", "quantity": 2}]], "weeklyFinal": []}


def sample_liveops():
    reward = [{"item": "VersusCoins", "quantity": 100}]
    return {
        "revision": 3,
        "images": {"weeklyBanner": "Menus/x/banner.png", "weeklyMenu": "Menus/x/banner.png",
                   "dailyBanner": "Menus/x/banner.png", "dailyMenu": "Menus/x/banner.png"},
        "seasons": {"title": "menu.DivisionTitle", "description": "menu.Desc", "promotionMatchesPerDay": [2, 3],
                    "topFactor": 2, "safeFraction": 0.1, "masterTopRank": 10, "defaultLevel": 10,
                    "fallbackZones": {"promotion": 20, "demotion": 10}},
        "objectives": [
            {"id": "wins", "tracker": "Wins", "title": "menu.Win", "titlePlural": "menu.Wins",
             "theme": {"title": "menu.WinTheme", "description": "menu.WinThemeDesc"},
             "daily": {"goals": [2, 3], "rewards": reward}, "weekly": {"goals": [5, 10, 20]}},
            {"id": "kills", "tracker": "Kills", "title": "menu.Kill", "titlePlural": "menu.Kills",
             "daily": {"goals": [10], "rewards": reward}, "weekly": {"goals": [50, 100, 200]}},
            {"id": "crates", "tracker": "CratesOpened", "title": "menu.Crate", "titlePlural": "menu.Crates",
             "weekly": {"goals": [1, 2, 3]}},
        ],
        "dailyQuests": {"title": "crm.DailyQuestTitle", "description": "crm.DailyQuestText", "advertisement": "menu.DailyQuest",
                        "count": 2, "bonus": {"title": "menu.All", "rewards": [{"item": "Crate_Contracts", "quantity": 1}]}},
        "weeklyEvents": {"advertisement": "menu.Ad", "boostedTag": "menu.Boost", "count": 2, "themes": ["wins"],
                         "milestoneRewards": [reward, reward, [{"item": "UpgradeTokens", "quantity": 5}]],
                         "eventCratePrefix": "Crate_Event_",
                         "final": {"title": "menu.All", "rewards": [{"item": "HardCurrency", "quantity": 5}], "eventCrate": True}},
        "modifiers": {"rotation": ["standard", "coinRush", "trophyFever"], "definitions": [
            {"id": "standard", "cells": []},
            {"id": "coinRush", "cells": [{"table": "VersusCoinsMatchRewards", "index": "Division", "ids": "divisions",
                                          "fields": ["VictoryAmount", "DailyCap"], "factor": 1.5}]},
            {"id": "trophyFever", "rivalFactor": 1.2, "cells": [{"table": "PlayerLevelXP", "index": "Level", "ids": "levels",
                                                                  "fields": ["DivisionTrophiesBonus"], "factor": 2}]}]},
        "seasonRewards": {"table": "PVPDivisionRewards", "index": "Rank", "fieldPrefix": "Division"},
    }


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
        files = {}
        for asset in (b"Menus/x/emblem3.png", b"Menus/x/banner.png", b"Menus/x/crate.png"):
            h = "%x" % zlib.crc32(asset)
            files[h[0] + "/" + h[1:] + ".f"] = {}
        self.asset_files = set(files)
        with open(os.path.join(self.tmp, "names.json"), "w") as f:
            json.dump({"files": files}, f)
        os.makedirs(os.path.join(self.tmp, "Design"))
        self.write_design(sample_design())
        self.liveops_path = os.path.join(self.tmp, "liveops_tuning.json")
        self.write_liveops(sample_liveops())
        self.collector_path = os.path.join(self.tmp, "collector_tuning.json")
        self.write_collector(sample_collector())

    def write_liveops(self, tuning):
        with open(self.liveops_path, "w") as f:
            json.dump(tuning, f)

    def write_collector(self, tuning):
        with open(self.collector_path, "w") as f:
            json.dump(tuning, f)

    def write_design(self, tables):
        with open(os.path.join(self.tmp, "Design", "MCVS_Meta.xlsx"), "w") as f:
            json.dump(tables, f)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def generate(self, now=None):
        return cg.generate(self.tmp, self.pricing_path, self.liveops_path, self.collector_path,
                           now=now or datetime.datetime(2026, 1, 1, tzinfo=UTC))

    def outputs(self, now=None):
        return {k: json.loads(v) for k, v in self.generate(now).items()}

    def collector_inputs(self, tuning=None, design=None):
        """The Catalog and unlock groups generate() builds, for checks on a hand-modified collector document."""
        cat = cg.Catalog(sample_catalog(), sample_tables(), {"MCVS_Meta": design or sample_design()},
                         asset_files=self.asset_files)
        unlocks, _ = cg.build_unlocks(cat)
        return cat, cg.build_collector(cat, tuning or sample_collector(), unlocks, None)

    def assert_error(self, mutate_collector=None, mutate_design=None, mutate_liveops=None, text=None):
        """generate() raises a ContentError (mentioning ``text``) after the given changes to the inputs."""
        collector, design, liveops = sample_collector(), sample_design(), sample_liveops()
        for mutate, value in ((mutate_collector, collector), (mutate_design, design), (mutate_liveops, liveops)):
            if mutate:
                mutate(value)
        self.write_collector(collector)
        self.write_design(design)
        self.write_liveops(liveops)
        with self.assertRaises(cg.ContentError) as raised:
            self.generate()
        if text:
            self.assertIn(text, str(raised.exception))

    def assert_document_error(self, mutate, text):
        """check_collector rejects the collector document after ``mutate``."""
        cat, doc = self.collector_inputs()
        cg.check_collector(cat, doc)
        mutate(doc)
        with self.assertRaises(cg.ContentError) as raised:
            cg.check_collector(cat, doc)
        self.assertIn(text, str(raised.exception))

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
        self.assertEqual(groups["accessories"]["items"], ["MercAccessory_1_{1,3}", "MercAccessory_2_{1,3-4}"])
        self.assertEqual(groups["signatures"]["items"],
                         ["KillSignature_Avatar_{2,5-19}", "KillSignature_Background_{2-5}", "KillSignature_Sound_{2}"])
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
        # avatar 3: no ecomm id, no source, asset on disk; avatars 4 and 20 lack their asset: excluded everywhere
        self.assertEqual(groups["unreleased"]["items"], ["KillSignature_Avatar_{3}"])
        self.assertEqual(groups["signatures"]["items"][0], "KillSignature_Avatar_{2,5-19}")
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
        self.assertEqual(mastery["ids"], [1, 2, 3])  # no AgentsMasteryXP rows in the Design file: 1..rows
        self.assertTrue(mastery["inverse"])

    def test_output_is_deterministic(self):
        tuning = sample_liveops()
        tuning["collector"] = collector_block()
        self.write_liveops(tuning)
        with contextlib.redirect_stderr(io.StringIO()):
            a = self.generate()
            b = self.generate()
            # the generation time only decides whether the since warning is printed
            c = self.generate(now=datetime.datetime(2030, 1, 6, tzinfo=UTC))
        self.assertEqual(a, b)
        self.assertEqual(a, c)
        self.assertIn("collector.json", a)
        for text in a.values():
            self.assertTrue(text.endswith("\n"))
            json.loads(text)

    def test_unknown_crate_is_an_error(self):
        pricing = sample_pricing()
        pricing["crates"] = [["Crate_DoesNotExist", 1]]
        with open(self.pricing_path, "w") as f:
            json.dump(pricing, f)
        with self.assertRaises(cg.ContentError):
            self.generate()

    def test_liveops_ids_are_expanded_and_validated(self):
        live = self.outputs()["liveops.json"]
        self.assertEqual(live["revision"], 3)
        self.assertEqual(live["seasons"]["divisions"], [1, 2])
        self.assertNotIn("_promotionMatchesPerDay", live["seasons"])
        # Event crates: every Crate_Event_<n> of the catalogue, in natural order (not Crate_Event_Special).
        self.assertEqual(live["weeklyEvents"]["eventCrates"], ["Crate_Event_1", "Crate_Event_2", "Crate_Event_10"])
        cells = {m["id"]: m for m in live["modifiers"]["definitions"]}
        self.assertEqual(cells["coinRush"]["cells"][0]["ids"], [1, 2])
        self.assertEqual(cells["coinRush"]["cells"][0]["database"], "MCVS_Meta")
        self.assertEqual(cells["trophyFever"]["cells"][0]["ids"], [1, 2])
        self.assertEqual(cells["trophyFever"]["rivalFactor"], 1.2)
        self.assertEqual(live["seasonRewards"]["fields"], ["Division01", "Division02"])
        self.assertEqual(live["seasonRewards"]["ids"], [1, 2, 3])  # no PVPDivisionRewards rows in the Design file: 1..rows
        self.assertEqual([o["id"] for o in live["objectives"]], ["wins", "kills", "crates"])
        self.assertNotIn("daily", live["objectives"][2])

    def test_liveops_rejects_what_the_client_could_not_use(self):
        def broken(mutate):
            tuning = sample_liveops()
            mutate(tuning)
            self.write_liveops(tuning)
            with self.assertRaises(cg.ContentError):
                self.generate()

        broken(lambda t: t["objectives"][0]["daily"].update(rewards=[{"item": "Crate_Nope", "quantity": 1}]))
        broken(lambda t: t["objectives"][0]["daily"].update(rewards=[{"item": "VersusCoins", "quantity": 0}]))
        broken(lambda t: t["objectives"][1].update(tracker="KillsOf"))
        broken(lambda t: t["objectives"][1]["weekly"].update(goals=[3, 2, 1]))
        broken(lambda t: t["objectives"][1]["weekly"].update(goals=[1, 2]))  # 3 reward tiers
        broken(lambda t: t["dailyQuests"].update(title="menu.Daily"))         # the TS keys daily quests on DailyQuestTitle
        broken(lambda t: t["dailyQuests"].update(count=3))                   # only 2 daily objectives
        broken(lambda t: t["images"].update(dailyBanner="Menus/x/missing.png"))
        broken(lambda t: t["seasons"].update(promotionMatchesPerDay=[1, 2, 3]))
        broken(lambda t: t["modifiers"]["definitions"][1]["cells"][0].update(fields=["Nope"]))
        broken(lambda t: t["modifiers"].update(rotation=["standard", "nope"]))
        broken(lambda t: t["weeklyEvents"].update(themes=["crates"]))         # no theme titles
        broken(lambda t: t["objectives"][0].update(title="NoTable"))

    def test_liveops_localization_ids_must_exist(self):
        loc = os.path.join(self.tmp, "Localization")
        os.makedirs(loc)
        ids = ["DivisionTitle", "Desc", "Win", "Wins", "WinTheme", "WinThemeDesc", "Kill", "Kills", "Crate", "Crates",
               "DailyQuest", "All", "Ad", "Boost"]
        with open(os.path.join(loc, "menu_en.json"), "w") as f:
            json.dump([{"Id": i, "Text": "x"} for i in ids], f)
        with open(os.path.join(loc, "crm_en.json"), "w") as f:
            json.dump([{"Id": "DailyQuestTitle"}, {"Id": "DailyQuestText"}], f)
        with open(os.path.join(loc, "Crate_en.json"), "w") as f:
            json.dump([{"Id": "Cosmetic_Crate"}], f)  # the collector's crate name
        self.outputs()
        with open(os.path.join(loc, "menu_en.json"), "w") as f:
            json.dump([{"Id": i} for i in ids if i != "Boost"], f)
        with self.assertRaises(cg.ContentError):
            self.outputs()
        with open(os.path.join(loc, "menu_en.json"), "w") as f:
            json.dump([{"Id": i} for i in ids], f)
        with open(os.path.join(loc, "Crate_en.json"), "w") as f:
            json.dump([{"Id": "Legendary_Crate"}], f)
        with self.assertRaises(cg.ContentError) as raised:
            self.outputs()
        self.assertIn("collector: crate.locId: localization id Crate.Cosmetic_Crate does not exist", str(raised.exception))

    def test_check_mode_reports_stale_files(self):
        out_dir = os.path.join(self.tmp, "out")
        os.makedirs(out_dir)
        args = ["--decoded", self.tmp, "--pricing", self.pricing_path, "--liveops", self.liveops_path,
                "--collector", self.collector_path, "--out", out_dir]
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(cg.main(args + ["--check"]), 1)
            self.assertEqual(cg.main(args), 0)
            self.assertEqual(cg.main(args + ["--check"]), 0)
        self.assertTrue(os.path.exists(os.path.join(out_dir, "collector.json")))

    def test_helpers(self):
        self.assertEqual(cg.compress_ranges([5, 1, 2, 3, 9, 9]), "1-3,5,9")
        self.assertEqual(cg.expand_ranges("1-3,5,9"), [1, 2, 3, 5, 9])
        self.assertEqual(cg.expand_ranges(""), [])
        self.assertEqual(cg.expand_pattern("A_{1-3,7}"), ["A_1", "A_2", "A_3", "A_7"])
        self.assertEqual(cg.expand_pattern("B"), ["B"])
        self.assertEqual(cg.round_price(1234, 5), 1235)
        self.assertEqual(cg.round_price(1, 5), 5)

    # ------------------------------------------------------------------------------------------ collector

    def test_nosource_is_released_without_a_shipped_source(self):
        cat = cg.Catalog(sample_catalog(), sample_tables(), {"MCVS_Meta": sample_design()}, asset_files=self.asset_files)
        unlocks, _ = cg.build_unlocks(cat)
        found = cg._find_nosource(cat, unlocks)
        # sold avatar 6 / backgrounds 4, 5, the starting items (not in the full unlock) and the unreleased avatars 3, 4,
        # 20 are out; visible jersey 1 is the VIP pool's, 3 has an event crate; camo 9 and the gear slots have no source
        self.assertEqual(sorted(cg._id_of(a) for a in found["avatars"]), [2, 5] + list(range(7, 20)))
        self.assertEqual(found["backgrounds"], {"KillSignature_Background_2", "KillSignature_Background_3"})
        self.assertEqual(found["sounds"], {"KillSignature_Sound_2"})
        self.assertEqual(found["skins"], {"MercSkin_1_2", "MercSkin_1_3"})
        self.assertEqual(found["accessories"], {"MercAccessory_2_4"})
        self.assertEqual(found["mercs"], {"UnlockMerc_2"})
        self.assertEqual(found["camos"], {"WeaponCamo_1_9"})
        self.assertIn("gearSlots", found)
        # an unlock group naming an id the catalogue does not know (the two would have drifted apart)
        next(g for g in unlocks["groups"] if g["id"] == "signatures")["items"].append("KillSignature_Avatar_{99}")
        with self.assertRaises(cg.ContentError) as raised:
            cg._find_nosource(cat, unlocks)
        self.assertIn("released KillSignature_Avatar_99 has no catalogue entry", str(raised.exception))

    def test_collector_sets_follow_the_rules(self):
        col = self.outputs()["collector.json"]
        self.assertEqual(list(col), ["format", "crate", "rows", "avatars", "backgrounds", "sounds", "camos", "jerseys",
                                     "season", "levels", "summary"])
        self.assertEqual(col["crate"], {"id": "Crate_Event_Special", "table": "Crate_Event_Special",
                                        "locId": "Crate.Cosmetic_Crate", "image": "Menus/x/crate.png"})
        av = col["avatars"]
        # Kayac: folder 7, subfolder 8, Kayac2 9 and unreleased 20, file name 10 (any case), extra id 11
        self.assertEqual(av["kayac"], "7-11,20")
        self.assertEqual(av["reserved"], "13-15,17-19")      # starred emblems: kept for ranks 1-3
        self.assertEqual(av["base"], "2,5,12,16")            # no-source minus Kayac minus reserved (unstarred stay)
        self.assertEqual(av["unlock"], "2,12,16")            # base with Unlockable=false: "true" cells
        self.assertEqual(av["lock"], "7-8")                  # released Kayac with Unlockable=true: "false" cells
        self.assertEqual(av["drawable"], "1,5-6")            # shipped Unlockable=true minus Kayac
        self.assertEqual(col["backgrounds"], {"base": "2-3", "drawable": "1,3-4", "unlock": "2"})
        self.assertEqual(col["sounds"], [2])
        self.assertEqual(col["camos"], {"1": "2-4,9", "2": ""})
        self.assertEqual(col["jerseys"], "1,3")
        self.assertEqual(col["season"], {
            "tiers": ["Starter", "Bronze"], "divisionTier": [0, 1],
            "retarget": [["Crate_Starter_4To10Place", "Crate_Starter_11To20Place"],
                         ["Crate_Bronze_4To10Place", "Crate_Bronze_11To20Place"]],
            "placement": [["Crate_Starter_FirstPlace", "Crate_Starter_SecondPlace", "Crate_Starter_ThirdPlace"],
                          ["Crate_Bronze_FirstPlace", "Crate_Bronze_SecondPlace", "Crate_Bronze_ThirdPlace"]],
            "emblems": [[12, 13, 14, 15], [16, 17, 18, 19]]})
        self.assertEqual(col["levels"], "2")
        # base: 4 avatars + 2 backgrounds + 1 sound + 2 skins + 1 hidden jersey pair + 1 agent; a fresh profile also
        # drains the other unlockable signatures (avatars 5, 6, 2, 12, 16; backgrounds 2, 3, 4). Paid: 4 camos + 2 x 2
        # jerseys; VIP-only: camo 3 and jersey 1 on both agents.
        self.assertEqual(col["summary"], {"baseItems": 11, "baseTier": 13, "paidPairs": 8, "vipOnlyPairs": 3,
                                          "kayacAvatars": 5})

    def test_collector_rows(self):
        rows = self.outputs()["collector.json"]["rows"]
        self.assertEqual(rows, [
            {"id": 1, "category": "avatars", "type": "KillSignature", "subtype": "Avatar", "subtype2": "Random"},
            {"id": 2, "category": "backgrounds", "type": "KillSignature", "subtype": "Background", "subtype2": "Random"},
            {"id": 3, "category": "sounds", "type": "KillSignature", "subtype": "Avatar", "subtype2": "Random",
             "bundle": "KillSignature_Sound_2", "item": 2},
            {"id": 4, "category": "skins", "type": "MercSkin", "subtype": "1", "subtype2": "2", "merc": 1, "item": 2},
            {"id": 5, "category": "skins", "type": "MercSkin", "subtype": "1", "subtype2": "3", "merc": 1, "item": 3},
            {"id": 6, "category": "hiddenJerseys", "type": "MercAccessory", "subtype": "Random", "subtype2": "4", "item": 4},
            {"id": 7, "category": "agents", "type": "UnlockMerc", "subtype": "2", "merc": 2},
            {"id": 8, "category": "camos", "type": "WeaponCamo", "subtype": "Random", "subtype2": "Random"},
            {"id": 9, "category": "jerseys", "type": "MercAccessory", "subtype": "Random", "subtype2": "Random"},
            {"id": 10, "category": "exhausted", "type": "VersusCoins", "subtype": "Tiers4"},
        ])
        for row in rows:
            self.assertIsNone(cg.collector_row_problem(row), row)
        # the row order is tuning; the manifest counts the rows
        tuning = sample_collector()
        tuning["rows"] = list(reversed(tuning["rows"]))
        self.write_collector(tuning)
        out = self.outputs()
        self.assertEqual([r["category"] for r in out["collector.json"]["rows"]][:2], ["exhausted", "jerseys"])
        self.assertIn({"name": "collector.json", "entries": 10}, out["manifest.json"]["files"])
        self.assertEqual(out["manifest.json"]["source"]["collector"], "tools/content-gen/collector_tuning.json")

    def test_collector_sound_carrier(self):
        # D8: the sounds ride on random background rows (the shipped tuning) or random avatar rows (the default)
        tuning = sample_collector()
        tuning["soundCarrier"] = "Background"
        self.write_collector(tuning)
        rows = self.outputs()["collector.json"]["rows"]
        self.assertEqual(rows[2], {"id": 3, "category": "sounds", "type": "KillSignature", "subtype": "Background",
                                   "subtype2": "Random", "bundle": "KillSignature_Sound_2", "item": 2})
        self.assert_error(lambda t: t.update(soundCarrier="Sound"), text="soundCarrier must be one of Avatar, Background")

    def test_collector_kayac_rule_inputs(self):
        tuning = sample_collector()
        tuning["kayac"]["extra"] = []
        tuning["reservePlacementEmblems"] = False
        self.write_collector(tuning)
        av = self.outputs()["collector.json"]["avatars"]
        self.assertEqual(av["kayac"], "7-10,20")
        self.assertEqual(av["reserved"], "")
        self.assertEqual(av["base"], "2,5,11-19")

    def test_preview_safety_rule(self):
        ok = {"type": "KillSignature", "subtype": "Avatar", "subtype2": "Random"}
        for row in (ok, dict(ok, bundle="KillSignature_Sound_3"), dict(ok, subtype="Background"),
                    dict(ok, subtype="Background", bundle="KillSignature_Sound_2"),          # a background carrier
                    {"type": "MercSkin", "subtype": "2", "subtype2": "5"},
                    {"type": "MercAccessory", "subtype": "Random", "subtype2": "112"},
                    {"type": "MercAccessory", "subtype": "Random", "subtype2": "Random"},
                    {"type": "UnlockMerc", "subtype": "17"}, {"type": "WeaponCamo", "subtype": "Random", "subtype2": "Random"},
                    {"type": "VersusCoins", "subtype": "Tiers4"}):
            self.assertIsNone(cg.collector_row_problem(row), row)
        for row in ({"type": "KillSignature", "subtype": "Sound", "subtype2": "2"},           # KeyNotFoundException in GetPreview
                    {"type": "KillSignature", "subtype": "Background", "subtype2": "7"},      # no image either
                    {"type": "MercSkin", "subtype": "2", "subtype2": "5", "bundle": "KillSignature_Sound_2"},   # non-carrier
                    dict(ok, bundle="WeaponCamo_1_2"),
                    {"type": "WeaponCamo", "subtype": "1", "subtype2": "2"},
                    {"type": "UnlockMerc", "subtype": "17", "subtype2": "1"},
                    {"type": "VersusCoins", "subtype": "Tiers4", "subtype2": "Random"},
                    {"type": "Gear", "subtype": "Random"}, {"type": "Nope", "subtype": "x"}):
            self.assertIsNotNone(cg.collector_row_problem(row), row)

    def test_collector_document_checks(self):
        def sound_row(doc):
            doc["rows"][2] = {"id": 3, "category": "sounds", "type": "KillSignature", "subtype": "Sound", "subtype2": "2"}

        def too_many_rows(doc):
            doc["rows"] = [dict(doc["rows"][0], id=i) for i in range(1, 102)]

        def kayac_in_base(doc):
            doc["avatars"]["base"] += ",7"

        self.assert_document_error(sound_row, "cannot be previewed")
        self.assert_document_error(lambda d: d["rows"][1].update(bundle="KillSignature_Sound_2"), "non-carrier")
        self.assert_document_error(lambda d: d["rows"][6].update(bundle="KillSignature_Sound_2"),
                                   "only KillSignature/Avatar|Background/Random")
        self.assert_document_error(lambda d: d["rows"][2].update(bundle="KillSignature_Sound_3"), "a sounds row carries its own sound")
        self.assert_document_error(lambda d: d["rows"][2].pop("bundle"), "non-carrier sounds row")
        self.assert_document_error(too_many_rows, "101 crate rows")
        self.assert_document_error(lambda d: d["rows"][3].update(id=9), "ids must run 1..n")
        self.assert_document_error(kayac_in_base, "in both base and kayac")
        self.assert_document_error(lambda d: d["avatars"].update(base=d["avatars"]["base"] + ",13"), "in both base and reserved")
        self.assert_document_error(lambda d: d["avatars"].update(unlock="2,3,12,16"), "unknown or unreleased")
        self.assert_document_error(lambda d: d["avatars"].update(unlock="2,5,12,16"), "Unlockable=false")
        self.assert_document_error(lambda d: d["avatars"].update(unlock="2,12-13,16"), "Unlockable=false")   # 13: not base
        self.assert_document_error(lambda d: d["avatars"].update(lock="7"), "released Kayac avatars")
        self.assert_document_error(lambda d: d["backgrounds"].update(unlock="2-3"), "Unlockable=false")
        self.assert_document_error(lambda d: d["backgrounds"].update(unlock="2,5"), "Unlockable=false")      # 5: not base
        self.assert_document_error(lambda d: d.update(rows=[]), "0 crate rows")
        self.assert_document_error(lambda d: d["avatars"].update(kayac=d["avatars"]["kayac"] + ",99"), "avatars.kayac holds unknown")
        self.assert_document_error(lambda d: d["backgrounds"].update(drawable="1,3-4,9"), "backgrounds.drawable holds unknown")
        self.assert_document_error(lambda d: d["avatars"].update(drawable="1,5-6,13"), "in both drawable and reserved")
        self.assert_document_error(lambda d: d["avatars"].update(drawable="1,5-7"), "avatars 7 in both drawable and kayac")

    def test_collector_rejects_bad_inputs(self):
        def second_weight(design):
            design["Crate_Bronze_SecondPlace"]["Rows"][1][5] = 10

        def gear_first(design):
            design["Crate_Starter_ThirdPlace"]["Rows"][0][1] = "Gear_Random_1_Random"

        # MercAccessories row of jersey 4: [Id, LocId, MercId, VisibleInShop, IsKayac]
        def hidden_jersey_visible(design):
            design["MercAccessories"]["Rows"][3][3] = "true"

        def hidden_jersey_kayac(design):
            design["MercAccessories"]["Rows"][3][4] = "true"

        def hidden_jersey_agent(design):
            design["MercAccessories"]["Rows"][3][2] = 2

        def short_content_table(design):
            design["Crate_Event_Special"]["Rows"] = design["Crate_Event_Special"]["Rows"][:5]   # 10 collector rows

        def placement_row_2(design):
            # the only weighted row is a HardCurrency one, but row 2: the CRM cells write row 1's bundle
            design["Crate_Starter_FirstPlace"]["Rows"] = [[1, "Gear_Random_1_Random", None, None, None, 0, 0, 0, None],
                                                          [2, "HardCurrency", None, None, None, 1000, 0, 60, None]]

        def unreleased_emblem(design):
            # season_rewards/I_1.png now belongs to avatar 3, which is in the tables but was never released
            rows = dict((r[0], r) for r in design["KillsignatureAvatars"]["Rows"])
            rows[3][1], rows[13][1] = rows[13][1], rows[3][1]

        def tier_crate_missing(design):
            design["LootCrates"]["Rows"] = [r for r in design["LootCrates"]["Rows"] if r[0] != "Crate_Bronze_11To20Place"]

        def mixed_tiers(design):
            design["PVPDivisionRewardsType"]["Rows"][20][2] = "Crate_Starter_11To20Place"

        def typed_row(design):
            design["Crate_Event_Special"]["Rows"][50][1] = "VersusCoins"

        def camo_pool(design):
            design["CamosList_AR02"]["Rows"].append([11, "f", "false"])

        self.assert_error(lambda t: t["crate"].update(id="Crate_Nope"), text="unknown crate Crate_Nope")
        self.assert_error(lambda t: t["crate"].update(locId="CosmeticCrate"), text="localization id")
        self.assert_error(lambda t: t["crate"].update(image="Menus/x/missing.png"), text="not in the data pack")
        self.assert_error(mutate_design=second_weight, text="only Weight.1 row")
        self.assert_error(mutate_design=gear_first, text="only Weight.1 row")
        self.assert_error(lambda t: t["season"].update(tiers=["Starter", "Bronze", "Silver"]), text="season.tiers")
        self.assert_error(lambda t: t["season"].update(tiers=["Bronze", "Starter"]), text="season.tiers")
        self.assert_error(lambda t: t["season"].update(tiers=["Starter"]), text="does not list")
        self.assert_error(mutate_design=tier_crate_missing, text="Crate_Bronze_11To20Place")
        self.assert_error(mutate_design=mixed_tiers, text="rank 21 of division 2")
        self.assert_error(lambda t: t["season"].update(numerals=["I", "III"]), text="season_rewards/III.png")
        self.assert_error(mutate_design=typed_row, text="already holds rewards")
        self.assert_error(mutate_design=camo_pool, text="camo pool")
        self.assert_error(mutate_design=hidden_jersey_visible, text="hidden jersey 4 must exist")
        self.assert_error(mutate_design=hidden_jersey_kayac, text="hidden jersey 4 must exist")
        self.assert_error(mutate_design=hidden_jersey_agent, text="hidden jersey 4 must exist")
        self.assert_error(mutate_design=short_content_table, text="row 6: ids must run 1..n over existing rows of Crate_Event_Special")
        self.assert_error(mutate_design=placement_row_2, text="Crate_Starter_FirstPlace row 1 must be the only Weight.1 row")
        self.assert_error(mutate_design=unreleased_emblem,
                          text="season emblem Menus/assets/png/profile/season_rewards/I_1.png does not resolve to one released")
        self.assert_error(lambda t: t.update(sounds=[1]), text="sound 1")                  # the starting sound
        self.assert_error(lambda t: t.update(skins=[[2, 4]]), text="skin 2/4")              # Locked
        self.assert_error(lambda t: t.update(agents=[1]), text="agent 1")                   # has a source
        self.assert_error(lambda t: t.update(agents=[3]), text="agent 3")                   # not displayed
        self.assert_error(lambda t: t.update(hiddenJerseys=[1]), text="hidden jersey 1")    # visible
        self.assert_error(lambda t: t["kayac"].update(extra=[99]), text="kayac.extra")
        self.assert_error(lambda t: t.update(exhausted="VersusCoins_Tiers9"), text="VersusCoins_Tiers9")
        # an existing currency tier that §2.3.1 does not list as previewable
        self.assert_error(lambda t: t.update(exhausted="UpgradeTokens_Tiers2"),
                          lambda d: d.update(UpgradeTokensTiers=copy.deepcopy(d["VersusCoinsTiers"])), text="cannot be previewed")
        self.assert_error(lambda t: t["rows"].remove("exhausted"), text="rows must list")
        self.assert_error(mutate_design=lambda d: d.pop("KillsignatureSounds"), text="KillsignatureSounds")
        os.remove(os.path.join(self.tmp, "Design", "MCVS_Meta.xlsx"))
        with self.assertRaises(cg.ContentError) as raised:
            self.generate()
        self.assertIn("needs the design tables", str(raised.exception))

    def test_collector_rejects_inconsistent_tables(self):
        def rename_field(table, old, new):
            def mutate(design):
                design[table]["Fields"] = [new if f == old else f for f in design[table]["Fields"]]
            return mutate

        def drop_row(table, key):
            def mutate(design):
                design[table]["Rows"] = [r for r in design[table]["Rows"] if r[0] != key]
            return mutate

        def jersey_hidden_in_tables(design):
            design["MercAccessories"]["Rows"][0][3] = "false"   # the catalogue still has jersey 1 in the VIP pool

        def agent_jersey(design):
            design["MercAccessories"]["Rows"][2][2] = 1         # visible jersey 3 only for agent 1

        def extra_visible_jersey(design):
            design["MercAccessories"]["Rows"].append([5, "jersey.E", None, "true", "false"])

        self.assert_error(mutate_design=rename_field("LootCrates", "CratesCount", "Count"), text="LootCrates has no field CratesCount")
        self.assert_error(mutate_design=rename_field("Crate_Event_Special", "Weight.1", "Weight1"), text="has no field Weight.1")
        self.assert_error(mutate_design=drop_row("KillsignatureAvatars", 2), text="avatars 2 have no KillsignatureAvatars row")
        self.assert_error(mutate_design=drop_row("KillsignatureBackgrounds", 2),
                          text="backgrounds 2 have no KillsignatureBackgrounds row")
        self.assert_error(mutate_design=drop_row("CamosListPerWeapon", 2), text="agent 2: no CamosListPerWeapon row for its weapon SR01")
        self.assert_error(lambda t: t.update(skins=[[1, 1]]), text="skin 1/1 is not a released no-source skin")   # default
        self.assert_error(mutate_design=drop_row("KillsignatureSounds", 2), text="sound 2 is not a released no-source sound")
        self.assert_error(lambda t: t.update(hiddenJerseys=[1]), jersey_hidden_in_tables,
                          text="hidden jersey 1 is not a released no-source jersey")
        self.assert_error(mutate_design=agent_jersey, text="jersey 3 is agent-specific")
        self.assert_error(mutate_design=extra_visible_jersey, text="visible jersey pool differs")
        for key, value in (("sounds", [2, 2]), ("skins", [[1, 2], [1, 2]]), ("hiddenJerseys", [4, 4]), ("agents", [2, 2])):
            self.assert_error(lambda t: t.update({key: value}), text="%s names an item twice" % key)

    # ------------------------------------------------------------------------------------------ liveops collector block

    def test_liveops_collector_block(self):
        plain = self.generate()["liveops.json"]
        tuning = sample_liveops()
        tuning["collector"] = collector_block(since="2030-01-07")
        self.write_liveops(tuning)
        out = self.outputs()
        live = out["liveops.json"]
        # carried unchanged, since normalised; the revision stays (a bump would replace the running day and week)
        self.assertEqual(live["collector"], collector_block(since="2030-01-07T00:00:00Z"))
        self.assertEqual(live["revision"], 3)
        self.assertEqual(list(live)[-1], "collector")
        # the rest of liveops.json is byte-identical: today's parser reads named keys only
        without = dict(live)
        del without["collector"]
        self.assertEqual(cg.dumps(without), plain)
        self.assertEqual(live["collector"]["dailyMission"][0]["item"], out["collector.json"]["crate"]["id"])
        for since in ("2030-01-07T00:00:00+00:00", "2030-01-07T00:00Z"):
            tuning["collector"]["since"] = since
            self.write_liveops(tuning)
            self.assertEqual(self.outputs()["liveops.json"]["collector"]["since"], "2030-01-07T00:00:00Z")

    def test_liveops_collector_block_is_validated(self):
        def block(**changes):
            def mutate(t):
                t["collector"] = collector_block()
                t["collector"].update(changes)
            return mutate

        crate = [{"item": "Crate_Event_Special", "quantity": 1}]
        self.assert_error(mutate_liveops=block(since="2030-01-08T00:00:00Z"), text="not a Monday")      # a Tuesday
        self.assert_error(mutate_liveops=block(since="2030-01-07T01:00:00Z"), text="not a Monday")      # not 00:00
        self.assert_error(mutate_liveops=block(since="2030-01-07T00:00:00+02:00"), text="ISO 8601 UTC")
        self.assert_error(mutate_liveops=block(since="next monday"), text="ISO 8601 UTC")
        self.assert_error(mutate_liveops=block(weeklyMilestones=[crate, crate]), text="one reward list per weekly milestone tier (3)")
        self.assert_error(mutate_liveops=block(dailyBonus=[{"item": "Crate_Nope", "quantity": 1}]), text="unknown reward")
        self.assert_error(mutate_liveops=block(weeklyFinal=[{"item": "Crate_Event_Special", "quantity": 0}]), text="positive")
        self.assert_error(mutate_liveops=block(weeklyFinal={"item": "Crate_Event_Special", "quantity": 2}), text="must be a list")
        self.assert_error(mutate_liveops=block(dailyBonus=["Crate_Event_Special"]), text="{item, quantity}")
        self.assert_error(mutate_liveops=block(dailyMission=[{"item": "Crate_Contracts", "quantity": 1}]),
                          text="not the collector's crate")
        self.assert_error(mutate_liveops=block(since="2030-02-30T00:00:00Z"), text="not a valid date")
        self.assert_error(mutate_liveops=lambda t: t.update(collector=[collector_block()]), text="collector must be an object")
        # lists may be empty, the block as a whole must hand out the crate (GeneratedContentTests asserts the same)
        self.assert_error(mutate_liveops=block(dailyMission=[], weeklyMilestones=[[], [], []]),
                          text="hands out no Crate_Event_Special")
        self.assert_error(mutate_liveops=block(dailyMission=[], weeklyMilestones=[[], [], []],
                                               weeklyFinal=[{"item": "VersusCoins", "quantity": 50}]),
                          text="hands out no Crate_Event_Special")
        # one crate per milestone: the claim reveals only a milestone's last crate (economy-collector.md §3.1)
        self.assert_error(mutate_liveops=block(dailyBonus=crate), text="the daily bonus already holds a crate (Crate_Contracts)")
        self.assert_error(mutate_liveops=block(weeklyFinal=crate), text="the weekly final already holds a crate (the event crate)")
        self.assert_error(mutate_liveops=block(weeklyMilestones=[crate, crate + crate, crate]),
                          text="collector.weeklyMilestones[1]: the crate twice in one milestone")

        def mission_with_crate(t):
            block()(t)
            t["objectives"][1]["daily"]["rewards"] = [{"item": "Crate_Contracts", "quantity": 1}]
        self.assert_error(mutate_liveops=mission_with_crate, text="the daily mission kills already holds a crate (Crate_Contracts)")

        def tier_with_crate(t):
            block()(t)
            t["weeklyEvents"]["milestoneRewards"][2] = [{"item": "Crate_Contracts", "quantity": 1}]
        self.assert_error(mutate_liveops=tier_with_crate, text="weekly milestone tier 3 already holds a crate (Crate_Contracts)")

        # without the event crate the final may hold the crate
        tuning = sample_liveops()
        block(weeklyFinal=crate)(tuning)
        tuning["weeklyEvents"]["final"]["eventCrate"] = False
        self.write_liveops(tuning)
        self.assertEqual(self.outputs()["liveops.json"]["collector"]["weeklyFinal"], crate)
        # a misspelt or missing key would silently drop its crates
        self.assert_error(mutate_liveops=block(weeklyfinal=crate), text="unknown: weeklyfinal")

        def without_final(t):
            block()(t)
            del t["collector"]["weeklyFinal"]
        self.assert_error(mutate_liveops=without_final, text="missing: weeklyFinal")

        def without_mission(t):
            block()(t)
            del t["collector"]["dailyMission"]
        self.assert_error(mutate_liveops=without_mission, text="missing: dailyMission")

    def test_liveops_collector_lists_may_be_empty(self):
        # the content-only fallback of economy-collector.md §8: crates on milestone tier 3 only
        tuning = sample_liveops()
        tuning["collector"] = dict(collector_block(), _comment="tier 3 only", dailyMission=[],
                                   weeklyMilestones=[[], [], [{"item": "Crate_Event_Special", "quantity": 2}]])
        self.write_liveops(tuning)
        block = self.outputs()["liveops.json"]["collector"]
        self.assertEqual(list(block), ["since", "dailyBonus", "dailyMission", "weeklyMilestones", "weeklyFinal"])   # comments dropped
        self.assertEqual(block["dailyBonus"], [])
        self.assertEqual(block["dailyMission"], [])
        self.assertEqual(block["weeklyMilestones"], [[], [], [{"item": "Crate_Event_Special", "quantity": 2}]])
        self.assertEqual(block["weeklyFinal"], [])
        # only the collector's lists may be empty: the other reward lists still need a reward
        tuning["dailyQuests"]["bonus"]["rewards"] = []
        self.write_liveops(tuning)
        with self.assertRaises(cg.ContentError) as raised:
            self.generate()
        self.assertIn("dailyQuests.bonus: no rewards", str(raised.exception))

    def test_since_warning(self):
        tuning = sample_liveops()
        tuning["collector"] = collector_block(since="2030-01-07T00:00:00Z")
        self.write_liveops(tuning)
        soon, past = "less than 7 days after this build", "not after this build"
        for now, warning in ((datetime.datetime(2029, 12, 31, tzinfo=UTC), None),       # exactly 7 days ahead
                             (datetime.datetime(2029, 12, 31, 0, 0, 1, tzinfo=UTC), soon),
                             (datetime.datetime(2030, 1, 6, 23, 59, 59, tzinfo=UTC), soon),
                             (datetime.datetime(2030, 1, 7, tzinfo=UTC), past),           # starts with the build
                             (datetime.datetime(2030, 2, 1, tzinfo=UTC), past)):          # a rebuild after the release
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                self.generate(now)
            self.assertEqual("collector.since 2030-01-07T00:00:00Z" in err.getvalue(), warning is not None, now)
            for text in (soon, past):
                self.assertEqual(text in err.getvalue(), text == warning, now)


@unittest.skipUnless(os.path.exists(os.path.join(DECODED, "_catalog.json")), "no decoded data pack")
class RealDataTests(unittest.TestCase):
    """The exact counts of docs/design/economy-collector.md §1 on the player's 1.17.32 data pack."""

    @classmethod
    def setUpClass(cls):
        with contextlib.redirect_stderr(io.StringIO()):
            out = cg.generate(DECODED, os.path.join(HERE, "pricing.json"), os.path.join(HERE, "liveops_tuning.json"),
                              os.path.join(HERE, "collector_tuning.json"), now=datetime.datetime(2026, 1, 1, tzinfo=UTC))
        cls.out = dict((k, json.loads(v)) for k, v in out.items())
        cls.col = cls.out["collector.json"]
        cls.av = dict((k, set(cg.expand_ranges(v))) for k, v in cls.col["avatars"].items())

    def test_avatar_sets(self):
        av = self.av
        self.assertEqual(len(av["base"]), 304)
        self.assertEqual(len(av["unlock"]), 139)
        self.assertEqual(len(av["base"] - av["unlock"]), 165)      # already Unlockable=true
        self.assertEqual(len(av["drawable"] - av["base"]), 65)     # other drawable (shop, event pools, rank-ups)
        self.assertEqual(len(av["lock"]), 75)
        self.assertEqual(self.col["avatars"]["lock"], "598-672")
        self.assertEqual(len(av["kayac"]), 1066)
        self.assertEqual(self.col["summary"]["kayacAvatars"], 1064)
        self.assertEqual(self.col["avatars"]["kayac"], "382-384,443-531,533-589,598-1514")
        self.assertEqual(len(av["reserved"]), 36)
        self.assertEqual(len(av["drawable"] | av["unlock"]), 369)  # the real SignatureItemDataCollection fill (§1.3)
        for a in (382, 383, 384):
            self.assertIn(a, av["kayac"])
            self.assertNotIn(a, av["base"])
            self.assertNotIn(a, av["unlock"])
        self.assertFalse(set(range(1513, 1519)) & (av["base"] | av["unlock"] | av["lock"]))

    def test_other_sets(self):
        col = self.col
        bg = dict((k, cg.expand_ranges(v)) for k, v in col["backgrounds"].items())
        self.assertEqual((len(bg["base"]), len(bg["unlock"])), (18, 6))
        self.assertEqual(col["sounds"], [2, 3, 4, 5])
        count = lambda category: len([r for r in col["rows"] if r["category"] == category])
        self.assertEqual((count("sounds"), count("skins"), count("hiddenJerseys"), count("agents")), (4, 16, 12, 2))
        self.assertEqual(len(col["rows"]), 39)
        # the shipped order and carriers (§2.3): the exhausted VersusCoins row first, so CrateRewardItem.IsAvailable
        # stops there without a signature list; the sounds on random background rows (35 entries, not 1,518 avatars)
        self.assertEqual(col["rows"][0], {"id": 1, "category": "exhausted", "type": "VersusCoins", "subtype": "Tiers4"})
        self.assertEqual(set(r["subtype"] for r in col["rows"] if r["category"] == "sounds"), {"Background"})
        self.assertEqual([r["id"] for r in col["rows"] if r["type"] == "KillSignature" and r["subtype"] == "Avatar"], [2])
        self.assertEqual(col["crate"]["locId"], "crm.CrateCustomization")
        self.assertEqual(sum(len(cg.expand_ranges(v)) for v in col["camos"].values()), 4409)
        self.assertEqual(len(cg.expand_ranges(col["jerseys"])) * len(col["camos"]), 4956)
        self.assertEqual(col["summary"], {"baseItems": 596, "baseTier": 665, "paidPairs": 9365, "vipOnlyPairs": 5624,
                                          "kayacAvatars": 1064})
        self.assertEqual(col["season"]["divisionTier"], [0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6])
        self.assertEqual(col["season"]["emblems"][8], [269, 270, 271, 272])   # IX (out of id order)
        self.assertEqual(sum(len(g) for g in col["season"]["retarget"] + col["season"]["placement"]), 35)

    def test_per_agent_pools_equal_the_unlock_groups(self):
        groups = dict((g["id"], g) for g in self.out["unlocks.json"]["groups"])

        def per_agent(gid):
            out = {}
            for pattern in groups[gid]["items"]:
                for item in cg.expand_pattern(pattern):
                    _, merc, i = item.split("_")
                    out.setdefault(merc, set()).add(int(i))
            return out

        camos, jerseys = per_agent("camos"), per_agent("accessories")
        hidden = set(r["item"] for r in self.col["rows"] if r["category"] == "hiddenJerseys")
        self.assertEqual(len(self.col["camos"]), 21)
        for merc, pool in self.col["camos"].items():
            self.assertEqual(set(cg.expand_ranges(pool)), camos[merc], merc)
            self.assertEqual(set(cg.expand_ranges(self.col["jerseys"])) | hidden, jerseys[merc], merc)

    def test_liveops_block_is_shipped(self):
        block = self.out["liveops.json"]["collector"]
        self.assertEqual(block["since"], "2026-11-09T00:00:00Z")
        self.assertEqual(len(block["weeklyMilestones"]), 3)
        self.assertEqual(self.out["liveops.json"]["revision"], 1)

    def test_arena_modes_from_the_data_pack(self):
        """The Special Event modes (docs/LIVEOPS.md §9): every Arena rule table and titled variant of 1.17.32 is used,
        the playable maps leave out the three MapsList rows without a level, every mode allows the 21 agents."""
        arena = self.out["liveops.json"]["arena"]
        self.assertEqual(arena["maps"], ["Snow", "Desert", "Favelas", "Rooftop", "DogDays", "Port", "Stadium", "Stadium2"])
        self.assertEqual(len(arena["modes"]), 23)
        self.assertEqual([m["id"] for m in arena["modes"] if not m["rotate"]], ["tripleElixir"])
        used = set(t for m in arena["modes"] for t in m["parameterSets"])
        self.assertEqual(used, {"ArenaHavoc", "ArenaOverride", "ArenaAmbush", "ArenaGrandBounty", "ArenaBountiful", "ArenaBlitz",
                                "ArenaScorechargeTDM", "ArenaScorechargeZoneControl", "ArenaScorechargeWarzone",
                                "ArenaScorechargeBounty", "Arena3RoundSystem", "ArenaTripleElixir"})
        for m in arena["modes"]:
            self.assertEqual(m["agents"], ["Merc_%d" % i for i in range(1, 22)])
            self.assertTrue(m["img"].startswith("Menus/assets/png/tournament/mode_"), m["id"])
            self.assertEqual(m["map"], "Random")
            self.assertEqual(sorted(m["multipliers"]), sorted(cg.ARENA_MULTIPLIERS))
            self.assertEqual(m["allowDuplicates"], not m["id"].startswith("competitive"), m["id"])


class ArenaTests(unittest.TestCase):
    """build_liveops_arena on a synthetic catalogue: what it expands and what it refuses."""

    class Cat:
        def __init__(self):
            self.merc_ids = [1, 2, 3]
            self.files = {"Levels/A/A.level", "Levels/B/B.level", "img/tdm.png", "img/rnd.png"}
            self.asset_files = set(["marker"])
            self.tables = {"Design/MCVS_Gameplay.xlsx": {"ArenaFast": {"fields": ["Index", "X"]}, "ArenaSlow": {"fields": ["Index"]},
                                                         "ArenaNoIndex": {"fields": ["X"]}, "MapsList": {"fields": []}}}

        def asset_exists(self, path):
            return path in self.files

        def table_fields(self, db, table):
            t = self.tables.get("Design/%s.xlsx" % db, {}).get(table)
            return t["fields"] if t else None

    MAPS = {"Fields": ["Index", "MapPath", "AvailableInGameModes"],
            "Rows": [["A", "Levels/A/A.level", "PVP,Tournament"], ["B", "Levels/B/B.level", "Tournament"],
                     ["C", "Levels/C/C.level", "PVP"], ["random", None, None]]}
    LOC = {"Arena": {"Fast", "FastDesc", "Slow", "SlowDesc", "Lonely", "LonelyDesc", "Other"}, "HUD": {"TDM", "Any"}}

    def block(self):
        return {"agents": "all", "multipliers": {"creditsVictory": 1.25},
                "matchTypes": {"TeamDeathMatch": {"text": "HUD.TDM", "img": "img/tdm.png"},
                               "None": {"text": "HUD.Any", "img": "img/rnd.png"}},
                "modes": [{"id": "fast", "name": "Arena.Fast", "rule": "Arena.FastDesc", "matchType": "TeamDeathMatch",
                           "parameterSets": ["ArenaFast"]},
                          {"id": "slow", "name": "Arena.Slow", "rule": "Arena.SlowDesc", "matchType": "None", "parameterSets": ["ArenaSlow"],
                           "allowDuplicates": False, "map": "A", "multipliers": {"versusCoinsMvp": 2}}],
                "unusedTables": {"ArenaNoIndex": "test"}, "unusedTexts": {"Arena.Lonely": "test"}}

    def build(self, block=None):
        return cg.build_liveops_arena(self.Cat(), block or self.block(), self.LOC, self.MAPS)

    def refused(self, change, text):
        block = self.block()
        change(block)
        with self.assertRaises(cg.ContentError) as raised:
            self.build(block)
        self.assertIn(text, str(raised.exception))

    def test_expands_every_field(self):
        out = self.build()
        self.assertEqual(out["maps"], ["A"])          # B is not PVP, C has no level, random has no path
        fast, slow = out["modes"]
        self.assertEqual(fast, {"id": "fast", "eventName": "Arena.Fast", "rule": "Arena.FastDesc", "matchTypeText": "HUD.TDM",
                                "matchType": "TeamDeathMatch", "parameterSets": ["ArenaFast"], "map": "Random", "allowDuplicates": True,
                                "img": "img/tdm.png", "agents": ["Merc_1", "Merc_2", "Merc_3"],
                                "multipliers": {"creditsVictory": 1.25, "creditsDefeat": 1, "creditsMvp": 1, "versusCoinsVictory": 1,
                                                "versusCoinsDefeat": 1, "versusCoinsMvp": 1}, "rotate": True})
        self.assertEqual((slow["map"], slow["allowDuplicates"], slow["matchTypeText"], slow["img"]), ("A", False, "HUD.Any", "img/rnd.png"))
        self.assertEqual(slow["multipliers"]["versusCoinsMvp"], 2)
        self.assertEqual(slow["multipliers"]["creditsVictory"], 1.25)
        self.assertEqual(out["priority"], 3)

    def test_refuses_what_the_client_cannot_use(self):
        self.refused(lambda b: b.update(agents=["Merc_1"]), "agents must be")
        self.refused(lambda b: b["modes"][0].update(map="C"), "not playable")
        self.refused(lambda b: b["modes"][0].update(map="B"), "not playable")
        self.refused(lambda b: b["modes"][0].update(id="with_underscore"), "letters or digits")
        self.refused(lambda b: b["modes"][1].update(id="fast"), "duplicate")
        self.refused(lambda b: b["modes"][0].update(matchType="Uphold"), "no matchTypes entry")
        self.refused(lambda b: b["matchTypes"].update(CaptureFlag={"text": "HUD.TDM", "img": "img/tdm.png"}), "CaptureFlag")
        self.refused(lambda b: b["matchTypes"]["None"].update(img="img/missing.png"), "not in the data pack")
        self.refused(lambda b: b["modes"][0].update(img="img/missing.png"), "not in the data pack")
        self.refused(lambda b: b["modes"][0].update(name="Arena.Nope"), "does not exist")
        self.refused(lambda b: b["modes"][0].update(parameterSets=["ArenaMissing"]), "no MCVS_Gameplay table")
        self.refused(lambda b: b["modes"][0].update(parameterSets=["ArenaNoIndex"]), "no Index")
        self.refused(lambda b: b["modes"][0].update(multipliers={"creditsVictory": 0}), "0.1 to 10")
        self.refused(lambda b: b["modes"][0].update(multipliers={"bogus": 1}), "unknown multiplier")
        self.refused(lambda b: b["modes"][0].update(allowDuplicates="no"), "true or false")
        self.refused(lambda b: [m.update(rotate=False) for m in b["modes"]], "no mode in the rotation")

    def test_every_variant_is_used_or_named(self):
        self.refused(lambda b: b.pop("unusedTables"), "ArenaNoIndex is used by no mode")
        self.refused(lambda b: b.pop("unusedTexts"), "Arena.Lonely")
        self.refused(lambda b: b["unusedTables"].update(ArenaFast="x"), "listed in unusedTables")
        self.refused(lambda b: b["modes"].pop(1), "ArenaSlow is used by no mode")


if __name__ == "__main__":
    result = unittest.main(exit=False, verbosity=1).result
    sys.exit(0 if result.wasSuccessful() else 1)
