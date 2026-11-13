using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using McvsOffline.Embed.Tests.Patcher.Pc;
using McvsOffline.Embed.Tests.Support;
using McvsOffline.Patcher.Core;
using McvsOffline.Patcher.Patches;
using McvsOffline.Server.Services.Storage;
using Xunit;
using Xunit.Abstractions;
using GameContext = McvsOffline.Embed.Tests.Patcher.RealGamePatchTests.GameContext;
using Reader = McvsOffline.Embed.Tests.Patcher.RealTemplatesPatchTests.Reader;

namespace McvsOffline.Embed.Tests.Patcher
{
    /// <summary>
    /// Build 32: the server answers a game-profile read whose <c>ProfileOwnerRoomId</c> an earlier start left with that value
    /// replaced by <c>null</c> (<see cref="StaleProfileOwner"/>). On the player's REAL Gameloft.Templates.dll of both builds
    /// (shipped, and with the 'templates' patch the game runs with), for the unlock-all-shaped profile:
    /// <list type="bullet">
    /// <item>the answered text is byte for byte the text of the same profile with the lock released (what
    /// <c>SaveAndReleaseProfile</c> stores), and the game's reader gives for it the tree of the stale text with exactly one
    /// difference: the lock's <c>TValue</c> is <c>Null</c> instead of the room id;</item>
    /// <item>the game's writer (<c>TObject.ToString("j")</c>) writes the stale tree with that one value set to null as a text
    /// the reader reads to the same tree, with <c>"ProfileOwnerRoomId":null</c> in it;</item>
    /// <item>the game's converter (<c>DefaultConverter.ConvertFromTemplate</c>, which applies a template to the profile
    /// component) turns that null value into a null string, as for a released profile.</item>
    /// </list>
    /// Skipped without the game's assemblies unless MCVS_REQUIRE_REAL_CLIENT=1 (Steam: MCVS_REQUIRE_PC=1).
    /// </summary>
    [Collection("GameHooks")]
    public class RealProfileOwnerTests
    {
        private const string Stale = "LocalAnubis-Room-18478500-6c40-4424-9b53-e40b7e14c747";
        private readonly ITestOutputHelper output;

        public RealProfileOwnerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private string Refs()
        {
            string dir = TestEnvironment.GameAssemblies;
            if (dir != null && File.Exists(Path.Combine(dir, "Gameloft.Templates.dll"))) return dir;
            if (Environment.GetEnvironmentVariable("MCVS_REQUIRE_REAL_CLIENT") == "1")
                throw new InvalidOperationException("MCVS_REQUIRE_REAL_CLIENT=1 but MCVS_REFS has no Gameloft.Templates.dll");
            output.WriteLine("the game's assemblies are not available (MCVS_REFS): skipped");
            return null;
        }

        /// <summary>The unlock-all-shaped profile with the lock as the writer writes it (a string or null).</summary>
        private static string Profile(string owner)
        {
            string profile = RealTemplatesPatchTests.UnlockAllProfile(RealTemplatesPatchTests.UnlockKeys());
            const string after = "\"PlayerName\":\"Pão\",";
            int at = profile.IndexOf(after, StringComparison.Ordinal);
            Assert.True(at > 0);
            return profile.Insert(at + after.Length, "\"ProfileOwnerRoomId\":" + (owner == null ? "null" : "\"" + owner + "\"") + ",");
        }

        [Fact]
        public void TheReleasedAnswerIsWhatTheGameReadsAndWritesForAReleasedProfileOnBothBuilds()
        {
            string refs = Refs();
            if (refs == null) return;
            string stale = Profile(Stale), released = Profile(null);
            int start, length;
            string owner;
            Assert.True(StaleProfileOwner.TryFindOwner(stale, out start, out length, out owner));
            Assert.Equal(Stale, owner);
            string answered = StaleProfileOwner.Release(stale, start, length);
            Assert.Equal(released, answered);
            output.WriteLine("profile " + stale.Length + " chars, answered " + answered.Length + " chars");

            byte[] server = FixtureCompiler.Compile("McvsOffline.Server", FixtureCompiler.RepoSource("hosts", "embedded", "GameDataHooks.cs"));
            Check(refs, new Dictionary<string, byte[]>(), stale, answered, "Android 1.17.32 shipped");
            Check(refs, RealGamePatchTests.PatchReal(refs, server, null, new TemplatesPatch()), stale, answered, "Android 1.17.32 'templates'");
            string pc = RealPc.Folder(output);
            if (pc == null) return;
            byte[] pcServer = PcHookServer.Compile();
            Check(pc, new Dictionary<string, byte[]>(), stale, answered, "Steam 1.17.30 shipped");
            Check(pc, RealPc.Patch(pc, pcServer, null, BuildProfile.PcSteam6372049, new TemplatesPatch()), stale, answered, "Steam 1.17.30 'templates'");
        }

        private void Check(string folder, Dictionary<string, byte[]> patched, string stale, string answered, string build)
        {
            using (var game = new GameContext(folder, patched, null))
            {
                var reader = new Reader(game);
                Assembly templates = reader.Templates;
                Type tname = templates.GetType(TemplatesPatch.NameType, true);
                Type tnode = templates.GetType(TemplatesPatch.NodeType, true);
                Type tvalue = templates.GetType(TemplatesPatch.ValueType, true);
                MethodInfo toName = tname.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                         .Single(m => m.Name == "op_Implicit" && m.ReturnType == tname && m.GetParameters()[0].ParameterType == typeof(string));
                MethodInfo node = tnode.GetMethod("Node", new[] { tname });
                Func<object, string, object> child = (n, name) => node.Invoke(n, new[] { toName.Invoke(null, new object[] { name }) });
                Func<object, object> lockNode = root => child(child(root, "/Profile"), StaleProfileOwner.OwnerKey);

                object staleTree = reader.Read(stale), answeredTree = reader.Read(answered);
                object staleLock = lockNode(staleTree), answeredLock = lockNode(answeredTree);
                Assert.IsType(tvalue, staleLock);
                Assert.Equal(Stale, tvalue.GetProperty("Value").GetValue(staleLock));
                Assert.Equal("String", tvalue.GetProperty("ValueType").GetValue(staleLock).ToString());
                Assert.IsType(tvalue, answeredLock);
                Assert.Null(tvalue.GetProperty("Value").GetValue(answeredLock));
                Assert.Equal("Null", tvalue.GetProperty("ValueType").GetValue(answeredLock).ToString());

                // The trees differ in exactly that one line.
                string[] a = reader.Describe(staleTree).Split('\n'), b = reader.Describe(answeredTree).Split('\n');
                Assert.Equal(a.Length, b.Length);
                List<int> differ = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
                Assert.Single(differ);
                Assert.Contains("ProfileOwnerRoomId = String", a[differ[0]]);
                Assert.Contains("ProfileOwnerRoomId = Null null", b[differ[0]]);

                // The writer: the stale tree with the lock set to null (SaveAndReleaseProfile's value) is written as null and read
                // back to the answered tree.
                tvalue.GetProperty("Value").SetValue(staleLock, null);
                Assert.Equal(reader.Describe(answeredTree), reader.Describe(staleTree));
                string written = (string)staleTree.GetType().GetMethod("ToString", new[] { typeof(string) }).Invoke(staleTree, new object[] { "j" });
                Assert.Contains("\"ProfileOwnerRoomId\":null", written);
                Assert.Equal(reader.Describe(answeredTree), reader.Describe(reader.Read(written)));

                // The converter that applies the template to the profile component: a null value gives a null string.
                Type converter = templates.GetType("Gameloft.Templates.DefaultConverter", true);
                Type contextType = templates.GetType("Gameloft.Templates.TemplateContext", true);
                object instance = Activator.CreateInstance(converter, typeof(object));
                object context = Activator.CreateInstance(contextType, new object[] { true });
                object value = converter.GetMethod("ConvertFromTemplate", new[] { typeof(Type), tnode, contextType })
                                        .Invoke(instance, new[] { typeof(string), answeredLock, context });
                Assert.Null(value);
                output.WriteLine(build + ": the answered text reads to the stale tree with only the lock null; written back it is the same tree");
            }
        }
    }
}
