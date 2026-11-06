using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// GameHooks' side of the 'botmove' patch (build 27): the first step of a resting bot (settings embedded.bots.moveFromRest),
    /// the frames of at least 1/60 s that stay bit for bit, the "bots:" lines (ramp counters, AI-tick dt, the match servers'
    /// Worlds) and the PC input-device resets (PcHooks.WatchInputDevices on an InputManager look-alike).
    /// </summary>
    [Collection("GameHooks")]
    public class BotHooksTests : IDisposable
    {
        private const float PcFrame = 0.016566f;

        private readonly Logger logger = new Logger();
        private readonly MemoryLogSink sink = new MemoryLogSink(LogLevel.Debug, 1000);
        private int now = 1000000;
        private readonly Func<IEnumerable<System.Reflection.Assembly>> loaded = McvsOffline.Embedded.LoadedTypes.Source;   // LoadedTypesIsolation's

        public BotHooksTests()
        {
            // No engine for the world line: a real one another test left loaded must not get its statics initialised from here.
            McvsOffline.Embedded.LoadedTypes.Source = () => new System.Reflection.Assembly[0];
            logger.AddSink(sink);
            GameHooks.ResetBots();
            GameHooks.Configure(false, logger.For("game"), null);
            GameHooks.BotClock = () => now;
        }

        public void Dispose()
        {
            McvsOffline.Embedded.LoadedTypes.Source = loaded;
            GameHooks.ResetBots();
            GameHooks.Configure(false, null, null);
            PcHooks.UnwatchInputDevices();
            PcHooks.Configure(null, null);
        }

        private List<string> Lines()
        {
            logger.Flush(5000);
            return sink.Snapshot().Select(e => e.Message).ToList();
        }

        [Fact]
        public void ARestingBotTakesItsFirstStepAtThePcFramePeriod()
        {
            float game = 0f + PcFrame * 3f;   // adaptSpeed's own step from rest: 0.0497, which moveToVector turns into a stop
            Assert.True(game <= GameHooks.BotStartThreshold);
            float step = GameHooks.BotMoveRamp(game, 0f, PcFrame);
            Assert.True(step > GameHooks.BotStartThreshold);
            Assert.Equal(GameHooks.BotFirstStep, step);
            Assert.Equal(GameHooks.BotFirstStep, GameHooks.BotMoveRamp(3f / 120f, 0f, 1f / 120f));   // a 120 Hz phone

            // Off (the A/B run): the game's own value, the bot stays put.
            GameHooks.ConfigureBots(false);
            Assert.Equal(BitConverter.SingleToInt32Bits(game), BitConverter.SingleToInt32Bits(GameHooks.BotMoveRamp(game, 0f, PcFrame)));
            Assert.Contains("moveFromRest off: none raised", GameHooks.DescribeBots());
            // The first blocked start is named once (it came while the switch was on).
            Assert.Single(Lines(), l => l.StartsWith("bots: a bot's step from speed 0.000 was 0.0497 (frame 16.566 ms, under 1/60 s)", StringComparison.Ordinal) &&
                                        l.EndsWith("raised to 0.051 (settings embedded.bots.moveFromRest true)", StringComparison.Ordinal));

            // A first blocked start while off says so.
            GameHooks.ResetBots();
            GameHooks.BotClock = () => now;
            GameHooks.ConfigureBots(false);
            GameHooks.BotMoveRamp(game, 0f, PcFrame);
            Assert.Contains(Lines(), l => l.EndsWith("not above the game's 0.05 start threshold: left as it is, the bot stays where it is " +
                                                     "(settings embedded.bots.moveFromRest false)", StringComparison.Ordinal));
        }

        [Fact]
        public void FramesOfASixtiethOrLongerComeBackBitForBit()
        {
            foreach (bool on in new[] { true, false })
            {
                GameHooks.ConfigureBots(on);
                foreach (float dt in new[] { 1f / 60f, 0.02f, 0.025f, 1f / 30f })
                {
                    foreach (float prevSq in new[] { 0f, 0.0025f, 0.36f })
                    {
                        float game = (float)Math.Sqrt(prevSq) + dt * 3f;
                        Assert.Equal(BitConverter.SingleToInt32Bits(game), BitConverter.SingleToInt32Bits(GameHooks.BotMoveRamp(game, prevSq, dt)));
                    }
                }
            }
            Assert.Equal(float.NaN, GameHooks.BotMoveRamp(float.NaN, 0f, PcFrame));   // never raised
        }

        [Fact]
        public void TheLinesCountTheBlockedStartsAndTheFramePeriod()
        {
            Assert.StartsWith("bots: no MoveTo ramp call yet (no bot match, or no 'botmove' patch); settings embedded.bots.moveFromRest true", GameHooks.DescribeBots());
            for (int i = 0; i < 97; i++) GameHooks.BotMoveRamp(PcFrame * 3f, 0f, PcFrame);
            for (int i = 0; i < 3; i++) GameHooks.BotMoveRamp(0.5f + 0.06f, 0.25f, 0.02f);
            string described = GameHooks.DescribeBots();
            Assert.StartsWith("bots: match 1\n", described);
            Assert.Contains("bots: MoveTo ramp 100 calls, 97 from rest; 97 under the 0.05 start threshold (moveFromRest on: 97 helped); " +
                            "AI-tick dt ms min 16.566 p50 16.566 max 20.000, 97% under 1/60 s", described);
            Assert.Contains("bots: input device resets 0; headless-World exceptions this match 0", described);
            Assert.Contains("bots: no server World", described);   // no engine loaded in the tests
            List<string> lines = Lines();
            Assert.Single(lines, l => l.StartsWith("bots: match 1: the first MoveTo ramp call (settings embedded.bots.moveFromRest true); ", StringComparison.Ordinal));
            Assert.Single(lines, l => l.StartsWith("bots: a bot's step from speed 0.000 was 0.0497 (frame 16.566 ms, under 1/60 s), not above the game's 0.05 " +
                                                   "start threshold: raised to 0.051 (settings embedded.bots.moveFromRest true)", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.StartsWith("bots: MoveTo ramp", StringComparison.Ordinal));   // the 30 s line has not come yet

            // 30 s later: the periodic lines.
            now += GameHooks.BotLineIntervalMs;
            GameHooks.BotMoveRamp(PcFrame * 3f, 0f, PcFrame);
            Assert.Contains(Lines(), l => l.StartsWith("bots: MoveTo ramp 101 calls, 98 from rest; 98 under the 0.05 start threshold (moveFromRest on: 98 helped)", StringComparison.Ordinal));

            // A minute without a ramp call: the next one starts match 2, after the last lines of match 1.
            now += GameHooks.BotMatchGapMs;
            GameHooks.BotMoveRamp(1f / 60f * 3f, 0f, 1f / 60f);
            lines = Lines();
            Assert.Contains(lines, l => l.StartsWith("bots: (match ended) MoveTo ramp 101 calls", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("bots: match 2: the first MoveTo ramp call", StringComparison.Ordinal));
            Assert.Contains("bots: MoveTo ramp 1 calls, 1 from rest; 0 under the 0.05 start threshold (moveFromRest on: 0 helped); " +
                            "AI-tick dt ms min 16.667 p50 16.667 max 16.667, 0% under 1/60 s", GameHooks.DescribeBots());
        }

        [Fact]
        public void TheMedianIsTheMiddleSample()
        {
            var random = new Random(27);
            for (int round = 0; round < 200; round++)
            {
                int n = 1 + random.Next(300);
                float[] values = Enumerable.Range(0, n).Select(_ => (float)Math.Round(random.NextDouble() * 40, round % 3)).ToArray();
                float[] sorted = values.OrderBy(v => v).ToArray();
                int k = random.Next(n);
                Assert.Equal(sorted[k], GameHooks.Select((float[])values.Clone(), k));
            }
        }

        [Fact]
        public void TheWorldLineNamesTheServerWorldsAndTheThinkRate()
        {
            var worlds = new object[]
            {
                new World { Headless = false, TotalFrames = 600, TotalTime = new Time { Seconds = 10f } },
                new World { Headless = true, TotalFrames = 6000, TotalTime = new Time { Seconds = 100f } },
                new World { Headless = true, TotalFrames = 1803, TotalTime = new Time { Seconds = 30f } },
            };
            var prefs = new Dictionary<string, string> { ["FPS"] = "60", ["Vsync"] = "true" };
            Assert.Equal("bots: server Worlds w2 at 60.0, w3 at 60.1 frames/s (TotalFrames/TotalTime), BT_Root.AiModulo 4 -> 15.0 thinks/s; " +
                         "client FPS cap 60, Vsync true (in-game)", GameHooks.FormatBotWorlds(worlds, 4, prefs));
            Assert.Equal("bots: no server World, BT_Root.AiModulo ?", GameHooks.FormatBotWorlds(null, null, null));
            Assert.Equal("bots: server Worlds w1 at ? frames/s (TotalFrames/TotalTime), BT_Root.AiModulo 2",
                         GameHooks.FormatBotWorlds(new object[] { new World { Headless = true } }, 2, new Dictionary<string, string>()));
        }

        [Fact]
        public void InputDeviceResetsAreCountedAndLogged()
        {
            PcHooks.Configure(logger.For("pc"), null);
            InputManager.Reset();
            Assert.True(PcHooks.WatchInputDevices(typeof(InputManager)));
            Assert.True(PcHooks.WatchInputDevices(typeof(InputManager)));   // once
            Assert.Equal(1, InputManager.Subscribers);
            InputManager.Disconnect(new Gamepad());
            InputManager.Disconnect(null);
            Assert.Equal(2, GameHooks.BotInputResets);
            List<string> lines = Lines();
            Assert.Contains("bots: input device disconnected (Gamepad): the game zeroes every character's movement input, the match servers' bots " +
                            "included (CharacterComponent.ResetInputsWin; reset 1)", lines);
            Assert.Contains(lines, l => l.StartsWith("bots: input device disconnected (null)", StringComparison.Ordinal));
            Assert.Contains("input device resets 2", GameHooks.DescribeBots());

            // Not loaded, or no such event: nothing is subscribed, one line says why.
            PcHooks.UnwatchInputDevices();
            Assert.False(PcHooks.WatchInputDevices(null));
            Assert.False(PcHooks.WatchInputDevices(typeof(string)));
            lines = Lines();
            Assert.Contains("bots: Gameloft.Engine.InputManager is not loaded: input device resets are not counted", lines);
            Assert.Contains("bots: System.String has no static event OnDeviceDisconnect(device): input device resets are not counted", lines);
        }

        // Look-alikes of the engine members the bots lines read (World.Headless/TotalFrames/TotalTime.Seconds) and of
        // Gameloft.Engine.InputManager.OnDeviceDisconnect (public static event Action<IInputDevice>).
        public struct Time
        {
            public float Seconds { get; set; }
        }

        public sealed class World
        {
            public bool Headless { get; set; }

            public int TotalFrames { get; set; }

            public Time TotalTime { get; set; }
        }

        public interface IInputDevice
        {
        }

        public sealed class Gamepad : IInputDevice
        {
        }

        public static class InputManager
        {
            public static event Action<IInputDevice> OnDeviceDisconnect;

            public static int Subscribers => OnDeviceDisconnect == null ? 0 : OnDeviceDisconnect.GetInvocationList().Length;

            public static void Disconnect(IInputDevice device) => OnDeviceDisconnect?.Invoke(device);

            public static void Reset() => OnDeviceDisconnect = null;
        }
    }
}
