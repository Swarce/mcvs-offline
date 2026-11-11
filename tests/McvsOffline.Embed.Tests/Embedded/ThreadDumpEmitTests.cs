using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using McvsOffline.Embedded.Diagnostics;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// Build 32 review: a hitch's managed dump goes to the log filtered (<see cref="ThreadDump.EnteredFromNative"/>), every thread
    /// stays in the file. The sample has the shape of the owner's build-31 dump (threaddump-20261111-182137.txt): stray text before
    /// the first thread, the header and the first frame on one line, the game thread under <c>_UpdateWorlds</c> and the render
    /// thread under <c>_OnAfter3DRender</c> (the two blocks the filter kept of that dump's 54), the server's and the pool's threads.
    /// </summary>
    public class ThreadDumpEmitTests
    {
        private const string Path = "/storage/emulated/0/McvsOffline/logs/threaddump-hitch-20261111-182137.txt";

        private static string Sample(int gameFrames)
        {
            var sb = new StringBuilder();
            sb.Append("Init_private\n\n");
            sb.Append("\"mcvs-status\"  at <unknown> <0xffffffff>\n");
            sb.Append("  at (wrapper managed-to-native) System.Threading.Thread.SleepInternal (int) <0x00007>\n");
            sb.Append("  at McvsOffline.Embedded.EmbeddedHost.StatusLoop (int) [0x0002f] in <17e7>:0\n\n");
            sb.Append("\"Thread-29\"  at Gameloft.Templates.TName.op_Equality (Gameloft.Templates.TName,Gameloft.Templates.TName) [0x00000] in <a1>:0\n");
            for (int i = 0; i < gameFrames; i++)
                sb.Append("  at Gameloft.Templates.JsonTemplateReader.ReadNode (Gameloft.Templates.TName,Newtonsoft.Json.Linq.JToken,int) [0x000").Append(i % 10).Append("0] in <a1>:0\n");
            sb.Append("  at Gameloft.Engine.GameApplication.UpdateWorlds (double,double) [0x00012] in <e1>:0\n");
            sb.Append("  at (wrapper native-to-managed) Gameloft.Engine.GameApplication._UpdateWorlds (double,double) <0x00053>\n\n");
            sb.Append("\"<unnamed thread>\"  at <unknown> <0xffffffff>\n");
            sb.Append("  at Gameloft.Util.ThreadPool.Worker () [0x00016] in <73ed>:0\n\n");
            sb.Append("\"RenderThread\"  at <unknown> <0xffffffff>\n");
            sb.Append("  at (wrapper native-to-managed) Gameloft.Engine.World._OnAfter3DRender (intptr) <0x0004b>\n");
            return sb.ToString();
        }

        [Fact]
        public void TheFilteredEmitKeepsTheThreadsNativeCodeEnteredAndCountsTheOthers()
        {
            var lines = new List<string>();
            int skipped = ThreadDump.EmitDump(Sample(60), Path, lines.Add, ThreadDump.EnteredFromNative);
            Assert.Equal(2, skipped);   // mcvs-status and the pool worker; the stray "Init_private" is no thread block
            Assert.All(lines, l => Assert.StartsWith("[threaddump] ", l));
            Assert.All(lines, l => Assert.False(l.EndsWith("\n", StringComparison.Ordinal)));
            string all = string.Join("\n", lines);
            Assert.DoesNotContain("mcvs-status", all);
            Assert.DoesNotContain("ThreadPool.Worker", all);
            Assert.DoesNotContain("Init_private", all);
            // The game thread's long block is split into entries of at most ~2800 characters, in order, header first.
            int gameEntries = lines.Count - 2;   // the render thread's one entry and the closing line
            Assert.True(gameEntries >= 2, string.Join("\n---\n", lines));
            Assert.StartsWith("[threaddump] \"Thread-29\"", lines[0]);
            Assert.All(lines.Take(gameEntries), l => Assert.True(l.Length <= 2800 + 200, l.Length.ToString()));
            Assert.Contains("_UpdateWorlds", lines[gameEntries - 1]);
            Assert.Equal(60, lines.Take(gameEntries).Sum(l => CountOf(l, "JsonTemplateReader.ReadNode")));
            Assert.StartsWith("[threaddump] \"RenderThread\"", lines[gameEntries]);
            Assert.Equal("[threaddump] 2 other thread blocks are only in " + Path, lines.Last());
        }

        [Fact]
        public void WithoutAFilterEveryBlockIsEmittedAndNoClosingLine()
        {
            var lines = new List<string>();
            Assert.Equal(0, ThreadDump.EmitDump(Sample(3), Path, lines.Add, null));
            string all = string.Join("\n", lines);
            foreach (string name in new[] { "Init_private", "\"mcvs-status\"", "\"Thread-29\"", "\"<unnamed thread>\"", "\"RenderThread\"" })
                Assert.Contains(name, all);
            Assert.Equal(5, lines.Count);   // the stray text and four threads, one entry each
            Assert.DoesNotContain(lines, l => l.Contains("other thread blocks"));
            Assert.Equal(0, ThreadDump.EmitDump("", Path, lines.Add, ThreadDump.EnteredFromNative));
        }

        [Fact]
        public void EnteredFromNativeTakesOnlyBlocksWithANativeToManagedFrame()
        {
            Assert.True(ThreadDump.EnteredFromNative("\"Thread-29\"\n  at (wrapper native-to-managed) Gameloft.Engine.GameApplication._UpdateWorlds (double,double) <0x00053>"));
            Assert.False(ThreadDump.EnteredFromNative("\"mcvs-status\"\n  at (wrapper managed-to-native) System.Threading.Thread.SleepInternal (int) <0x00007>"));
            Assert.False(ThreadDump.EnteredFromNative(null));
        }

        private static int CountOf(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
