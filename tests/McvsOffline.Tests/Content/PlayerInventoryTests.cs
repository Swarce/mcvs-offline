using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Content
{
    /// <summary>The game-profile dump read as an inventory (PlayerInventory, economy-collector.md §2.9.1).</summary>
    public class PlayerInventoryTests
    {
        /// <summary>
        /// The expected Templates-writer shape of a <c>Mercs</c> list, as the TS saves it
        /// (GameProfileSection.FromGameObject: <c>TObject.FromObject(player).ToString("j")</c>): agent 4 unlocked with
        /// <c>Skins ["1","3"]</c>, <c>Camos ["1","12"]</c>, <c>Accessories ["112"]</c>, and agent 17 as
        /// <c>new Merc.ShortDef("17")</c>. Every public read/write property of Merc.ShortDef (GM/Merc.cs:14-96; the
        /// get-only HasNewStuff/NewStuff are not written, DefaultConverter.KeepProperty), <c>Locked</c> a JSON bool,
        /// GearSlotData with its private <c>[TemplateIgnore(false)]</c> GearId. Produced by the game's own
        /// Gameloft.Templates (TemplateActivator.CreateTemplate of a <c>List&lt;Merc.ShortDef&gt;</c> under a
        /// <c>/Profile</c> node with its <c>@type</c>, so the writer does not collapse it into <c>"/Profile.Mercs"</c>);
        /// the member order is that of the writer's property cache on net8 and may differ on Mono, which the reader
        /// does not care about. CollectorConformanceTests (WP-F test 7) checks it against the real writer.
        /// </summary>
        internal const string WriterMercsFixture
            = "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"PlayerProfileComponent\","
              + "\"Mercs\":[{\"NewAccessories\":[],\"Skins\":[\"1\",\"3\"],\"Id\":\"4\",\"Accessories\":[\"112\"],\"NewMods\":[],"
              + "\"WeaponInTrial\":[],\"Fatigue\":0,\"Camos\":[\"1\",\"12\"],\"Locked\":false,\"Mods\":[],\"IsNew\":true,"
              + "\"EquippedSkin\":\"1\",\"MasteryToClaim\":[],\"Xp\":0,\"EquippedWeapon\":\"\",\"Weapons\":[],"
              + "\"GearSlots\":[{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},"
              + "{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1}],"
              + "\"Level\":1,\"EquippedAccessories\":[],\"AutoclaimMasteryLevel\":0,\"NewWeapons\":[],"
              + "\"WeaponsFragments\":{},\"UpgradeSeen\":true,\"NewCamos\":[],\"NewSkins\":[],\"EquippedCamo\":\"1\"},"
              + "{\"NewAccessories\":[],\"Skins\":[\"1\"],\"Id\":\"17\",\"Accessories\":[],\"NewMods\":[],"
              + "\"WeaponInTrial\":[],\"Fatigue\":0,\"Camos\":[\"1\"],\"Locked\":true,\"Mods\":[],\"IsNew\":true,"
              + "\"EquippedSkin\":\"1\",\"MasteryToClaim\":[],\"Xp\":0,\"EquippedWeapon\":\"\",\"Weapons\":[],"
              + "\"GearSlots\":[{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},"
              + "{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1}],"
              + "\"Level\":1,\"EquippedAccessories\":[],\"AutoclaimMasteryLevel\":0,\"NewWeapons\":[],"
              + "\"WeaponsFragments\":{},\"UpgradeSeen\":true,\"NewCamos\":[],\"NewSkins\":[],\"EquippedCamo\":\"1\"}]}}}";

        /// <summary>The public read/write properties of Merc.ShortDef (GM/Merc.cs:14-96), i.e. what the writer emits.</summary>
        private static readonly string[] ShortDefProperties =
        {
            "Id", "Level", "Xp", "Fatigue", "Locked", "IsNew", "MasteryToClaim", "AutoclaimMasteryLevel", "Skins", "EquippedSkin",
            "NewSkins", "Weapons", "WeaponsFragments", "EquippedWeapon", "NewWeapons", "WeaponInTrial", "Accessories",
            "EquippedAccessories", "NewAccessories", "GearSlots", "Camos", "EquippedCamo", "NewCamos", "Mods", "NewMods", "UpgradeSeen",
        };

        /// <summary>The dump of the store filter's original tests (CrmOwnedStoreItems.Scan).</summary>
        private const string SignatureDump
            = "{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\","
              + "\"KillSignatureAvatars\" : [ \"1\", \"22\",5 ],\"KillSignatureBackgrounds\":[],"
              + "\"KillSignatureSounds\":[\"2\"],\"Name\":\"KillSignatureAvatars\","
              + "\"Note\":\"\\\"KillSignatureBackgrounds\\\":[\\\"9\\\"]\"}}}";

        private static readonly string[] SignatureDumpNames = { "KillSignature_Avatar_1", "KillSignature_Avatar_22", "KillSignature_Avatar_5" };

        private readonly ITestOutputHelper output;

        public PlayerInventoryTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void ScanReadsTheSignatureListsOfTheDump()
        {
            var inventory = PlayerInventory.Scan(SignatureDump, false);
            Assert.True(inventory.AvatarsKnown && inventory.BackgroundsKnown && inventory.SoundsKnown);
            Assert.Equal(SignatureDumpNames, Sorted(inventory.OwnedStoreNames()));
            Assert.Equal(new[] { 1, 5, 22 }, Sorted(inventory.Avatars));
            Assert.Empty(inventory.Backgrounds);
            Assert.Equal(new[] { 2 }, Sorted(inventory.Sounds));   // sounds are read, but never leave the store
            Assert.Equal(new[] { PlayerInventory.MercsPart }, inventory.Problems.Keys.ToArray());

            var none = PlayerInventory.Scan("{\"$default\":{\"/Profile\":{\"Version\":57}}}", false);
            Assert.True(none.AvatarsKnown && none.BackgroundsKnown && none.SoundsKnown);   // absent lists are empty
            Assert.Empty(none.OwnedStoreNames());
            Assert.Empty(none.Avatars);
        }

        [Theory]
        [InlineData("{\"a\":{\"KillSignatureAvatars\":[\"1\"]},\"b\":{\"KillSignatureAvatars\":[\"2\"]}}", PlayerInventory.AvatarsPart, "appears more than once")]
        [InlineData("{\"KillSignatureAvatars\":[{\"@value\":\"1\"}]}", PlayerInventory.AvatarsPart, "not an array of ids")]
        [InlineData("{\"KillSignatureBackgrounds\":{}}", PlayerInventory.BackgroundsPart, "not an array of ids")]
        [InlineData("{\"KillSignatureAvatars\":[\"1\\u0031\"]}", PlayerInventory.AvatarsPart, "not an array of ids")]
        [InlineData("{\"KillSignatureAvatars\":[\"1\",]}", PlayerInventory.AvatarsPart, "not an array of ids")]
        [InlineData("{\"KillSignatureAvatars\":[\"1\"", PlayerInventory.AvatarsPart, "not an array of ids")]
        public void ScanRefusesAnythingUnexpected(string dump, string part, string reason)
        {
            var inventory = PlayerInventory.Scan(dump, false);
            Assert.Empty(inventory.OwnedStoreNames());   // the whole store, as before
            Assert.Contains(reason, inventory.Problems[part]);
            Assert.False(part == PlayerInventory.AvatarsPart ? inventory.AvatarsKnown : inventory.BackgroundsKnown);
            Assert.True(part == PlayerInventory.AvatarsPart ? inventory.BackgroundsKnown : inventory.AvatarsKnown);
            Assert.Empty(part == PlayerInventory.AvatarsPart ? inventory.Avatars : inventory.Backgrounds);
            Assert.True(inventory.SoundsKnown);
        }

        /// <summary>The store filter depends on the avatar and background lists only: malformed sounds or agents change nothing.</summary>
        [Fact]
        public void StoreNamesIgnoreOtherParts()
        {
            var fixtures = new Dictionary<string, string[]>
            {
                [SignatureDump] = SignatureDumpNames,
                ["{\"$default\":{\"/Profile\":{\"Version\":57}}}"] = new string[0],
                ["{\"a\":{\"KillSignatureAvatars\":[\"1\"]},\"b\":{\"KillSignatureAvatars\":[\"2\"]}}"] = new string[0],
                ["{\"KillSignatureAvatars\":[{\"@value\":\"1\"}]}"] = new string[0],
                ["{\"KillSignatureBackgrounds\":{}}"] = new string[0],
                ["{\"KillSignatureAvatars\":[\"1\\u0031\"]}"] = new string[0],
                ["{\"KillSignatureAvatars\":[\"1\",]}"] = new string[0],
                ["{\"KillSignatureAvatars\":[\"1\""] = new string[0],
            };
            string[] others =
            {
                "\"KillSignatureSounds\":{}",
                "\"KillSignatureSounds\":[\"2\"],\"x\":{\"KillSignatureSounds\":[\"3\"]}",
                "\"KillSignatureSounds\":[{\"@value\":\"2\"}]",
                "\"Mercs\":[{\"Id\":\"x\"}]",
                "\"Mercs\":[{\"Id\":\"2\",\"Locked\":7}]",
                "\"Mercs\":{\"Id\":\"2\"}",
                "\"Mercs\":[{\"Id\":\"2\",}]",
                "\"Squads\":[{\"Mercs\":[\"2\"]}],\"Mercs\":[{\"Id\":\"2\"}]",
            };
            foreach (var fixture in fixtures)
            {
                foreach (bool parseMercs in new[] { false, true })
                {
                    Assert.Equal(fixture.Value, Sorted(PlayerInventory.Scan(fixture.Key, parseMercs).OwnedStoreNames()));
                    foreach (string other in others)
                    {
                        string dump = Inject(fixture.Key, other);
                        var inventory = PlayerInventory.Scan(dump, parseMercs);
                        Assert.Equal(fixture.Value, Sorted(inventory.OwnedStoreNames()));
                        if (other.StartsWith("\"Mercs\"", StringComparison.Ordinal) || other.StartsWith("\"Squads\"", StringComparison.Ordinal))
                            Assert.False(inventory.MercsKnown, dump);
                        else
                            Assert.False(inventory.SoundsKnown, dump);
                    }
                }
            }
        }

        [Fact]
        public void SoundsFollowTheSameRulesOnTheirOwn()
        {
            var inventory = PlayerInventory.Scan(Profile("\"KillSignatureAvatars\":[\"7\"],\"KillSignatureSounds\":[\"2\",5,\"05\"]"), false);
            Assert.True(inventory.SoundsKnown);
            Assert.Equal(new[] { 2, 5 }, Sorted(inventory.Sounds));   // "05" is not how the game spells sound 5
            Assert.Equal(new[] { "KillSignature_Avatar_7" }, Sorted(inventory.OwnedStoreNames()));

            var twice = PlayerInventory.Scan(Profile("\"KillSignatureAvatars\":[\"7\"],\"KillSignatureSounds\":[\"2\"],\"o\":{\"KillSignatureSounds\":[]}"), false);
            Assert.False(twice.SoundsKnown);
            Assert.Empty(twice.Sounds);
            Assert.Equal("KillSignatureSounds appears more than once", twice.Problems[PlayerInventory.SoundsPart]);
            Assert.True(twice.AvatarsKnown && twice.BackgroundsKnown);
            Assert.Equal(new[] { "KillSignature_Avatar_7" }, Sorted(twice.OwnedStoreNames()));

            var objects = PlayerInventory.Scan(Profile("\"KillSignatureSounds\":[{\"@value\":\"2\"}]"), false);
            Assert.False(objects.SoundsKnown);
            Assert.Equal("KillSignatureSounds is not an array of ids", objects.Problems[PlayerInventory.SoundsPart]);
        }

        /// <summary>Properties the dump leaves out take the Merc.ShortDef defaults (GM/Merc.cs:22-84).</summary>
        [Fact]
        public void MercsAbsentPropertiesTakeTheShortDefDefaults()
        {
            var inventory = PlayerInventory.Scan(Profile("\"Mercs\":[{\"Id\":\"3\"},{\"Id\":\"4\",\"Locked\":false}]"), true);
            Assert.True(inventory.MercsKnown);
            Assert.False(inventory.Problems.ContainsKey(PlayerInventory.MercsPart));
            Assert.Equal(new[] { 1, 3, 4 }, Sorted(inventory.Mercs.Keys));
            AssertMerc(inventory.Mercs[3], false, new[] { 1 }, new[] { 1 }, new int[0]);   // Locked defaults to true
            AssertMerc(inventory.Mercs[4], true, new[] { 1 }, new[] { 1 }, new int[0]);
            AssertMerc(inventory.Mercs[1], true, new[] { 1 }, new[] { 1 }, new int[0]);    // the starting agent
        }

        [Fact]
        public void MercsLockedMayBeABoolOrAStringAndIdANumber()
        {
            var inventory = PlayerInventory.Scan(Profile("\"Mercs\":["
                                                         + "{\"Id\":5,\"Locked\":\"false\",\"Skins\":[\"1\",\"2\"],\"Camos\":[1,\"7\"],\"Accessories\":[112,\"143\"]},"
                                                         + "{\"Id\":\"6\",\"Locked\":\"true\"},{\"Id\":7,\"Locked\":true},{\"Id\":\"8\",\"Locked\":\"False\"},"
                                                         + "{\"Id\":\"1\",\"Locked\":false,\"Skins\":[],\"Camos\":[\"1\"]}]"), true);
            Assert.True(inventory.MercsKnown, string.Join("; ", inventory.Problems.Values));
            Assert.Equal(new[] { 1, 5, 6, 7, 8 }, Sorted(inventory.Mercs.Keys));
            AssertMerc(inventory.Mercs[5], true, new[] { 1, 2 }, new[] { 1, 7 }, new[] { 112, 143 });
            Assert.False(inventory.Mercs[6].Unlocked);
            Assert.False(inventory.Mercs[7].Unlocked);
            Assert.True(inventory.Mercs[8].Unlocked);
            AssertMerc(inventory.Mercs[1], true, new int[0], new[] { 1 }, new int[0]);   // as listed, not the default
        }

        /// <summary>Only <c>$default./Profile.Mercs</c> is the agent list; <c>Squads[].Mercs</c> and other components are not.</summary>
        [Fact]
        public void SquadsMercsIsNotTheAgentList()
        {
            var squadsOnly = PlayerInventory.Scan(Profile("\"Squads\":[{\"Id\":\"0\",\"Mercs\":[\"1\",\"2\"],\"Name\":null,\"Primary\":true}]"), true);
            Assert.False(squadsOnly.MercsKnown);
            Assert.Empty(squadsOnly.Mercs);
            Assert.Equal("no agent list at $default./Profile.Mercs", squadsOnly.Problems[PlayerInventory.MercsPart]);

            var squadsFirst = PlayerInventory.Scan(Profile("\"Squads\":[{\"Id\":\"0\",\"Mercs\":[\"2\",\"9\"]}],"
                                                           + "\"Mercs\":[{\"Id\":\"2\",\"Locked\":false},{\"Id\":\"3\"}]"), true);
            Assert.True(squadsFirst.MercsKnown);
            Assert.Equal(new[] { 1, 2, 3 }, Sorted(squadsFirst.Mercs.Keys));
            Assert.True(squadsFirst.Mercs[2].Unlocked);
            Assert.False(squadsFirst.Mercs[3].Unlocked);

            string elsewhere = "{\"$default\":{\"/Other\":{\"Mercs\":[{\"Id\":\"9\",\"Locked\":false}]},"
                               + "\"/Profile\":{\"@type\":\"PlayerProfileComponent\",\"Mercs\":[]},\"Mercs\":[{\"Id\":\"8\",\"Locked\":false}]}}";
            var other = PlayerInventory.Scan(elsewhere, true);
            Assert.True(other.MercsKnown);
            Assert.Equal(new[] { 1 }, Sorted(other.Mercs.Keys));
        }

        /// <summary>A squad member that the agent list holds must read unlocked, or the reading of the list is wrong.</summary>
        [Fact]
        public void SquadAgentReadingLockedMakesTheAgentsUnknown()
        {
            var locked = PlayerInventory.Scan(Profile("\"KillSignatureAvatars\":[\"7\"],\"Mercs\":[{\"Id\":\"1\",\"Locked\":true},{\"Id\":\"2\",\"Locked\":false}],"
                                                      + "\"Squads\":[{\"Id\":\"0\",\"Mercs\":[\"1\"],\"Name\":null,\"Primary\":true}]"), true);
            Assert.False(locked.MercsKnown);
            Assert.Empty(locked.Mercs);
            Assert.Equal("squad agent 1 reads locked: unexpected Mercs shape", locked.Problems[PlayerInventory.MercsPart]);
            Assert.True(locked.AvatarsKnown && locked.BackgroundsKnown && locked.SoundsKnown);
            Assert.Equal(new[] { "KillSignature_Avatar_7" }, Sorted(locked.OwnedStoreNames()));

            var unlocked = PlayerInventory.Scan(Profile("\"Mercs\":[{\"Id\":\"1\",\"Locked\":false},{\"Id\":\"2\"}],\"Squads\":[{\"Mercs\":[\"1\"]}]"), true);
            Assert.True(unlocked.MercsKnown);
            Assert.False(unlocked.Mercs[2].Unlocked);

            // A squad member the list does not hold, and a Squads value the guard cannot read, say nothing.
            var absent = PlayerInventory.Scan(Profile("\"Mercs\":[{\"Id\":\"2\"}],\"Squads\":[{\"Mercs\":[\"1\",3]},7,{\"Mercs\":{}}]"), true);
            Assert.True(absent.MercsKnown);
            Assert.True(absent.Mercs[1].Unlocked);
            var unreadable = PlayerInventory.Scan(Profile("\"Mercs\":[{\"Id\":\"1\",\"Locked\":true}],\"Squads\":\"1\""), true);
            Assert.True(unreadable.MercsKnown);
            Assert.False(unreadable.Mercs[1].Unlocked);
        }

        [Fact]
        public void ParseMercsFalseLeavesAgentsUnknownWithoutReadingThem()
        {
            string valid = Profile("\"KillSignatureAvatars\":[\"7\"],\"Mercs\":[{\"Id\":\"2\",\"Locked\":false}]");
            var skipped = PlayerInventory.Scan(valid, false);
            Assert.False(skipped.MercsKnown);
            Assert.Empty(skipped.Mercs);
            Assert.Equal("not read (the collector is not active)", skipped.Problems[PlayerInventory.MercsPart]);
            Assert.True(skipped.AvatarsKnown);
            Assert.True(PlayerInventory.Scan(valid, true).MercsKnown);

            // The same text for a list the parser would refuse: its span was never looked at.
            string broken = Profile("\"Mercs\":[{\"Id\":\"2\",}]");
            Assert.Equal("not read (the collector is not active)", PlayerInventory.Scan(broken, false).Problems[PlayerInventory.MercsPart]);
            Assert.Equal("Mercs is not valid JSON", PlayerInventory.Scan(broken, true).Problems[PlayerInventory.MercsPart]);
        }

        [Theory]
        [InlineData("\"Mercs\":{}", "Mercs is not an array")]
        [InlineData("\"Mercs\":null", "Mercs is not an array")]
        [InlineData("\"Mercs\":[\"4\"]", "Mercs holds something other than agent objects")]
        [InlineData("\"Mercs\":[{\"Locked\":false}]", "an agent's Id is missing or not an id")]
        [InlineData("\"Mercs\":[{\"Id\":\"04\"}]", "an agent's Id is missing or not an id")]
        [InlineData("\"Mercs\":[{\"Id\":4.5}]", "an agent's Id is missing or not an id")]
        [InlineData("\"Mercs\":[{\"Id\":{\"@value\":\"4\"}}]", "an agent's Id is missing or not an id")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\"},{\"Id\":4}]", "agent 4 appears more than once")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",\"Locked\":1}]", "agent 4: Locked is not a bool")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",\"Locked\":{\"@value\":false}}]", "agent 4: Locked is not a bool")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",\"Locked\":null}]", "agent 4: Locked is not a bool")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",\"Skins\":\"1\"}]", "agent 4: Skins is not a list of ids")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",\"Camos\":[null]}]", "agent 4: Camos is not a list of ids")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",\"Accessories\":[{\"@value\":\"1\"}]}]", "agent 4: Accessories is not a list of ids")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\",}]", "Mercs is not valid JSON")]
        [InlineData("\"Mercs\":[{\"Id\":\"4\"}", "no agent list at $default./Profile.Mercs")]
        [InlineData("\"Agents\":[]", "no agent list at $default./Profile.Mercs")]
        public void MalformedAgentListIsUnknown(string mercs, string reason)
        {
            var inventory = PlayerInventory.Scan(Profile("\"KillSignatureAvatars\":[\"7\"]," + mercs), true);
            Assert.False(inventory.MercsKnown);
            Assert.Empty(inventory.Mercs);
            Assert.Equal(reason, inventory.Problems[PlayerInventory.MercsPart]);
            Assert.True(inventory.AvatarsKnown && inventory.BackgroundsKnown && inventory.SoundsKnown);
            Assert.Equal(new[] { "KillSignature_Avatar_7" }, Sorted(inventory.OwnedStoreNames()));
        }

        [Fact]
        public void WriterMercsFixtureReadsAsOneUnlockedAgentAndOneLockedDefaultAgent()
        {
            var inventory = PlayerInventory.Scan(WriterMercsFixture, true);
            Assert.True(inventory.MercsKnown, string.Join("; ", inventory.Problems.Values));
            Assert.Equal(new[] { 1, 4, 17 }, Sorted(inventory.Mercs.Keys));
            AssertMerc(inventory.Mercs[4], true, new[] { 1, 3 }, new[] { 1, 12 }, new[] { 112 });
            AssertMerc(inventory.Mercs[17], false, new[] { 1 }, new[] { 1 }, new int[0]);
            AssertMerc(inventory.Mercs[1], true, new[] { 1 }, new[] { 1 }, new int[0]);   // not listed: the starting agent

            // The fixture is the writer's shape: every element carries exactly the ShortDef's read/write properties.
            var mercs = (List<object>)JsonPath.Get(MiniJson.ParseObject(WriterMercsFixture), "$default./Profile.Mercs");
            Assert.Equal(2, mercs.Count);
            foreach (Dictionary<string, object> merc in mercs)
            {
                Assert.Equal(Sorted(ShortDefProperties), Sorted(merc.Keys));
                Assert.IsType<bool>(merc["Locked"]);
            }
        }

        /// <summary>
        /// The shipped new profile: <c>"Mercs": []</c> and a squad of agent 1. Before the TS's first save every agent but
        /// the starting one is locked. Runs on the player's data pack when available; the excerpt always.
        /// </summary>
        [Fact]
        public void DefaultProfileScansAsEveryAgentLockedButAgentOne()
        {
            string excerpt = "{\r\n    \"@version\": \"0.1\", \r\n    \"$default\": {\r\n        \"Enabled\": true, \r\n"
                             + "        \"/Profile\": {\r\n            \"Xp\": 0, \r\n            \"Mercs\": [], \r\n            \"Squads\": [\r\n"
                             + "                {\r\n                    \"Id\": \"0\", \r\n                    \"Mercs\": [\r\n"
                             + "                        \"1\"\r\n                    ], \r\n                    \"Name\": null, \r\n"
                             + "                    \"Primary\": true\r\n                }\r\n            ], \r\n"
                             + "            \"@type\": \"PlayerProfileComponent\"\r\n        }\r\n    }\r\n}";
            AssertNewProfile(excerpt);

            string text = null;
            string pack = ContentTestSupport.RealDataPack;
            string decoded = ContentTestSupport.DecodedDataPack;
            if (pack != null) Assert.True(new DataPack(pack).TryReadText("DefaultProfile.tx", out text));
            else if (decoded != null && File.Exists(Path.Combine(decoded, "DefaultProfile.tx"))) text = File.ReadAllText(Path.Combine(decoded, "DefaultProfile.tx"));
            if (text == null)
            {
                output.WriteLine("no data pack (MCVS_DATAPACK / MCVS_DATAPACK_DECODED): DefaultProfile.tx skipped");
                return;
            }
            AssertNewProfile(text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("[1,2]")]
        [InlineData("\"KillSignatureAvatars\":")]
        [InlineData("{\"$default\":{\"/Profile\":{\"Mercs\":[{\"Id\":\"1\"")]
        [InlineData("{\"$default\":{\"/Profile\":{\"Mercs\":[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]}}}")]
        public void ScanNeverThrows(string dump)
        {
            var inventory = PlayerInventory.Scan(dump, true);
            Assert.False(inventory.MercsKnown);
            Assert.Empty(inventory.Mercs);
        }

        [Fact]
        public void AHandBuiltInventoryNamesItsIds()
        {
            var inventory = new PlayerInventory { AvatarsKnown = true, BackgroundsKnown = true };
            inventory.Avatars.Add(61);
            inventory.Backgrounds.Add(18);
            Assert.Equal(new[] { "KillSignature_Avatar_61", "KillSignature_Background_18" }, Sorted(inventory.OwnedStoreNames()));
            inventory.BackgroundsKnown = false;
            Assert.Empty(inventory.OwnedStoreNames());
        }

        /// <summary>
        /// The scan runs on the request path after every profile read: a 600 KB dump (an unlock-all profile is ~560 KB)
        /// with all three signature lists and 22 full agents. net8 (this test project); best of 10 runs after a warm-up.
        /// </summary>
        [Fact]
        public void ScansA600KbDumpInUnder50Ms()
        {
            string dump = SyntheticDump(600 * 1024);
            var first = PlayerInventory.Scan(dump, true);
            Assert.True(first.AvatarsKnown && first.BackgroundsKnown && first.SoundsKnown && first.MercsKnown, string.Join("; ", first.Problems.Values));
            Assert.Equal(1512, first.Avatars.Count);
            Assert.Equal(35, first.Backgrounds.Count);
            Assert.Equal(5, first.Sounds.Count);
            Assert.Equal(22, first.Mercs.Count);
            Assert.Equal(231, first.Mercs[21].Camos.Count);
            Assert.Equal(1547, first.OwnedStoreNames().Count());

            for (int i = 0; i < 3; i++) PlayerInventory.Scan(dump, true);
            double best = double.MaxValue;
            var runs = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                var watch = Stopwatch.StartNew();
                PlayerInventory.Scan(dump, true);
                watch.Stop();
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                runs.Add(watch.Elapsed.TotalMilliseconds.ToString("0.00", CultureInfo.InvariantCulture));
            }
            output.WriteLine(dump.Length + " chars: " + string.Join(", ", runs) + " ms");
            Assert.True(best < 50, "best of 10 scans took " + best.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
        }

        private static void AssertNewProfile(string text)
        {
            var inventory = PlayerInventory.Scan(text, true);
            Assert.True(inventory.MercsKnown, string.Join("; ", inventory.Problems.Values));
            Assert.Equal(new[] { 1 }, Sorted(inventory.Mercs.Keys));
            AssertMerc(inventory.Mercs[1], true, new[] { 1 }, new[] { 1 }, new int[0]);
            Assert.True(inventory.AvatarsKnown && inventory.BackgroundsKnown && inventory.SoundsKnown);
            Assert.Empty(inventory.Avatars);
            Assert.Empty(inventory.Backgrounds);
            Assert.Empty(inventory.Sounds);
            Assert.Empty(inventory.OwnedStoreNames());
            Assert.Empty(inventory.Problems);
        }

        private static void AssertMerc(MercState merc, bool unlocked, int[] skins, int[] camos, int[] accessories)
        {
            Assert.Equal(unlocked, merc.Unlocked);
            Assert.Equal(skins, Sorted(merc.Skins));
            Assert.Equal(camos, Sorted(merc.Camos));
            Assert.Equal(accessories, Sorted(merc.Accessories));
        }

        /// <summary>A dump whose <c>/Profile</c> holds <paramref name="members"/>.</summary>
        private static string Profile(string members)
        {
            return "{\"@version\":\"0.1\",\"$default\":{\"Enabled\":true,\"/Profile\":{\"@type\":\"PlayerProfileComponent\"," + members + "}}}";
        }

        /// <summary>Adds a member to the dump's <c>/Profile</c>, or to its root object when it has none.</summary>
        private static string Inject(string dump, string member)
        {
            const string profile = "\"/Profile\":{";
            int at = dump.IndexOf(profile, StringComparison.Ordinal);
            at = at >= 0 ? at + profile.Length : dump.IndexOf('{') + 1;
            return dump.Substring(0, at) + member + "," + dump.Substring(at);
        }

        /// <summary>
        /// An unlock-all profile's lists (avatars 1-1512, backgrounds 1-35, sounds 1-5), agents 0-21 in the writer's
        /// shape with every camo and jersey owned, and other profile data up to <paramref name="size"/> characters.
        /// </summary>
        private static string SyntheticDump(int size)
        {
            var sb = new StringBuilder("{\"@version\":\"0.1\",\"$default\":{\"Enabled\":true,\"/Profile\":{\"@type\":\"PlayerProfileComponent\",");
            sb.Append("\"KillSignatureAvatars\":[").Append(Ids(1, 1512)).Append("],\"KillSignatureBackgrounds\":[").Append(Ids(1, 35))
              .Append("],\"KillSignatureSounds\":[").Append(Ids(1, 5)).Append("],\"Mercs\":[");
            for (int merc = 0; merc <= 21; merc++)
            {
                if (merc > 0) sb.Append(',');
                sb.Append("{\"NewAccessories\":[").Append(Ids(1, 280)).Append("],\"Skins\":[").Append(Ids(1, 6))
                  .Append("],\"Id\":\"").Append(merc.ToString(CultureInfo.InvariantCulture)).Append("\",\"Accessories\":[").Append(Ids(1, 280))
                  .Append("],\"NewMods\":[],\"WeaponInTrial\":[],\"Fatigue\":0,\"Camos\":[").Append(Ids(1, 231))
                  .Append("],\"Locked\":").Append(merc == 0 ? "true" : "false").Append(",\"Mods\":[");
                for (int mod = 1; mod <= 30; mod++) sb.Append(mod > 1 ? "," : "").Append("{\"Id\":\"Mod_").Append(mod).Append("\",\"Locked\":false}");
                sb.Append("],\"IsNew\":false,\"EquippedSkin\":\"1\",\"MasteryToClaim\":[],\"Xp\":120000,\"EquippedWeapon\":\"2\",\"Weapons\":[\"2\",\"22\"],")
                  .Append("\"GearSlots\":[{\"Locked\":false,\"GearId\":12},{\"Locked\":false,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},")
                  .Append("{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1},{\"Locked\":true,\"GearId\":-1}],\"Level\":23,")
                  .Append("\"EquippedAccessories\":[\"3\"],\"AutoclaimMasteryLevel\":23,\"NewWeapons\":[],\"WeaponsFragments\":{\"2\":40},")
                  .Append("\"UpgradeSeen\":true,\"NewCamos\":[").Append(Ids(1, 231)).Append("],\"NewSkins\":[],\"EquippedCamo\":\"1\"}");
            }
            sb.Append("],\"InboxMessages\":[");
            for (int message = 0; sb.Length < size - 200; message++)
            {
                if (message > 0) sb.Append(',');
                sb.Append("{\"Id\":\"msg-").Append(message).Append("\",\"Title\":\"DIVISION REWARD\",\"Rewards\":[\"KillSignatureAvatars\",\"Crate_Gold_4To10Place\"],")
                  .Append("\"Body\":\"{\\\"Mercs\\\":[1,2],\\\"KillSignatureSounds\\\":[]}\",\"Read\":true}");
            }
            sb.Append("],\"Squads\":[{\"Id\":\"0\",\"Mercs\":[\"1\"],\"Name\":null,\"Primary\":true}],\"Version\":57}}}");
            return sb.ToString();
        }

        private static string Ids(int from, int to)
        {
            var sb = new StringBuilder();
            for (int id = from; id <= to; id++)
            {
                if (id > from) sb.Append(',');
                sb.Append('"').Append(id.ToString(CultureInfo.InvariantCulture)).Append('"');
            }
            return sb.ToString();
        }

        private static T[] Sorted<T>(IEnumerable<T> items)
        {
            var list = new List<T>(items);
            list.Sort((a, b) => Comparer<T>.Default.Compare(a, b));
            return list.ToArray();
        }

        private static string[] Sorted(IEnumerable<string> names)
        {
            var list = new List<string>(names);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }
    }
}
