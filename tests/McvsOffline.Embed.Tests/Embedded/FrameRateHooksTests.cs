using System;
using System.Collections.Generic;
using System.Linq;
using McvsOffline.Embedded.Graphics;
using McvsOffline.Server.Core;
using Xunit;

namespace McvsOffline.Embed.Tests.Embedded
{
    /// <summary>
    /// The host side of PC patch 'fpslimit' (build 27): settings.json <c>graphics.pcFpsLimit</c> (PcFrameRate) and
    /// GraphicsHooks.FrameRateLimit / FrameRateSettingChanged. WCPToolkit's pacer stores max(1, value) and spins until 1e6/limit µs
    /// minus 100 µs have passed, so 0 and -1 mean 1 FPS, NaN hangs it and +Inf takes the CRT's invalid path: no such value may ever
    /// reach it, and "game" must give the game's own value bit for bit.
    /// </summary>
    [Collection("GameHooks")]
    public class FrameRateHooksTests : IDisposable
    {
        private readonly Logger logger = new Logger();
        private readonly MemoryLogSink sink = new MemoryLogSink(LogLevel.Debug, 1000);
        private object setting = "game";
        private bool? vsync = true;
        private readonly List<float> set = new List<float>();
        private readonly List<Action> posted = new List<Action>();
        private bool postWorks = true;

        public FrameRateHooksTests()
        {
            logger.AddSink(sink);
            GraphicsHooks.ResetForTests();
            GraphicsHooks.Configure("auto", false, logger.For("game"), null);
            GraphicsHooks.ConfigureFrameRate(() => setting, action =>
            {
                if (!postWorks) return false;
                posted.Add(action);
                return true;
            });
            GraphicsHooks.VsyncReader = () => vsync;
            GraphicsHooks.WcpLimitSetter = value => set.Add(value);
        }

        public void Dispose()
        {
            GraphicsHooks.ResetForTests();
        }

        private List<string> Lines()
        {
            logger.Flush(5000);
            return sink.Snapshot().Select(e => e.Message).ToList();
        }

        private static int Bits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        private static readonly float[] Broken = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -0f, -1f, -30f };

        [Fact]
        public void NoValueThePacerCannotTakeEverLeaves()
        {
            foreach (float value in Broken) Assert.Equal(PcFrameRate.Fallback, PcFrameRate.Sanitize(value));
            Assert.Equal(60f, PcFrameRate.Fallback);
            Assert.Equal(1f, PcFrameRate.Sanitize(0.25f));
            Assert.Equal(10000f, PcFrameRate.Sanitize(1e9f));
            Assert.Equal(10000f, PcFrameRate.Sanitize(float.MaxValue));
            foreach (float value in new[] { 1f, 30f, 59.61f, 60f, 90f, 120f, 144f, 9999.5f, 10000f })
                Assert.Equal(Bits(value), Bits(PcFrameRate.Sanitize(value)));

            // Every choice over every game value: finite, at least 1, at most 10000.
            foreach (string choice in PcFrameRate.Options.Concat(new string[] { null }))
            {
                foreach (float game in Broken.Concat(new[] { 30f, 60f, 90f, 120f, 0.5f, 1e9f }))
                {
                    float limit = PcFrameRate.Resolve(game, choice);
                    Assert.False(float.IsNaN(limit) || float.IsInfinity(limit), choice + "/" + game);
                    Assert.InRange(limit, 1f, 10000f);
                }
            }
            // "unlimited" is a finite limit above any screen, never Android's -1 or 0.
            Assert.Equal(1000f, PcFrameRate.Resolve(60f, PcFrameRate.Unlimited));
            Assert.Equal(1000f, PcFrameRate.UnlimitedLimit);
        }

        [Fact]
        public void TheSettingIsReadAsAChoice()
        {
            Assert.Equal(new[] { "game", "30", "60", "90", "120", "144", "165", "240", "unlimited" }, PcFrameRate.Options);
            foreach (object game in new object[] { null, "", " ", "game", "GAME", " Game " }) Assert.Equal("game", PcFrameRate.Normalize(game));
            Assert.Equal("unlimited", PcFrameRate.Normalize("Unlimited"));
            Assert.Equal("144", PcFrameRate.Normalize("144"));
            Assert.Equal("144", PcFrameRate.Normalize(144L));
            Assert.Equal("144", PcFrameRate.Normalize(144.0));
            Assert.Equal("75", PcFrameRate.Normalize("75"));   // a hand-written number
            foreach (object bad in new object[] { "0", "-1", "20000", "NaN", "Infinity", "fast", true, 0L, -1.0, new Dictionary<string, object>() })
                Assert.Null(PcFrameRate.Normalize(bad));
            Assert.False(PcFrameRate.Overrides("game"));
            Assert.False(PcFrameRate.Overrides(null));
            Assert.True(PcFrameRate.Overrides("144"));
            Assert.True(PcFrameRate.Overrides("unlimited"));
            // Each page option resolves to its number (the game's value does not matter).
            foreach (string option in new[] { "30", "60", "90", "120", "144", "165", "240" })
                Assert.Equal(float.Parse(option, System.Globalization.CultureInfo.InvariantCulture), PcFrameRate.Resolve(120f, option));
        }

        [Fact]
        public void GameKeepsTheInGameValueBitForBitAndLogsOnce()
        {
            foreach (float value in new[] { 30f, 60f, 90f, 120f })
                Assert.Equal(Bits(value), Bits(GraphicsHooks.FrameRateLimit(value)));
            setting = null;   // no key at all: the same
            Assert.Equal(Bits(120f), Bits(GraphicsHooks.FrameRateLimit(120f)));
            // The start value and the property load pass the same value twice at boot: one line.
            List<string> lines = Lines().Where(l => l.StartsWith("graphics: frame rate limit", StringComparison.Ordinal)).ToList();
            Assert.Equal(new[]
            {
                "graphics: frame rate limit 30, the in-game FPS LIMIT (settings graphics.pcFpsLimit game); VSYNC on",
                "graphics: frame rate limit 60, the in-game FPS LIMIT (settings graphics.pcFpsLimit game); VSYNC on",
                "graphics: frame rate limit 90, the in-game FPS LIMIT (settings graphics.pcFpsLimit game); VSYNC on: a screen slower than 90 Hz caps it",
                "graphics: frame rate limit 120, the in-game FPS LIMIT (settings graphics.pcFpsLimit game); VSYNC on: a screen slower than 120 Hz caps it",
            }, lines);
            Assert.Empty(set);   // the hook answers; nothing is set behind the game's back
            Assert.Null(GraphicsHooks.FrameRateOverrideNote());
            // A value the pacer cannot take (a hand-edited LocalStorage, a broken row) never reaches it.
            Assert.Equal(60f, GraphicsHooks.FrameRateLimit(float.NaN));
            Assert.Equal(60f, GraphicsHooks.FrameRateLimit(-1f));
            Assert.Contains("graphics: frame rate limit 60, the in-game FPS LIMIT (its value -1 is not a usable limit) (settings graphics.pcFpsLimit game); VSYNC on", Lines());
        }

        [Fact]
        public void ANumberOrUnlimitedReplacesTheInGameValue()
        {
            setting = "144";
            vsync = false;
            Assert.Equal(144f, GraphicsHooks.FrameRateLimit(60f));
            Assert.Contains("graphics: frame rate limit 144 from settings graphics.pcFpsLimit (in-game FPS LIMIT 60 not used); VSYNC off", Lines());
            Assert.Equal("; pacer limit 144 (settings graphics.pcFpsLimit)", GraphicsHooks.FrameRateOverrideNote());
            // The in-game row changes: the setting still wins, the line names the new in-game value.
            Assert.Equal(144f, GraphicsHooks.FrameRateLimit(120f));
            Assert.Contains("graphics: frame rate limit 144 from settings graphics.pcFpsLimit (in-game FPS LIMIT 120 not used); VSYNC off", Lines());

            setting = "unlimited";
            vsync = true;
            Assert.Equal(1000f, GraphicsHooks.FrameRateLimit(60f));
            Assert.Contains("graphics: frame rate limit unlimited from settings graphics.pcFpsLimit (pacer at 1000; in-game FPS LIMIT 60 not used); VSYNC on: the screen's refresh rate caps it", Lines());
            Assert.Contains("frame rate limit unlimited", GraphicsHooks.FrameRateSummary);

            setting = "fast";   // not a choice: the in-game value, said so
            vsync = null;
            Assert.Equal(60f, GraphicsHooks.FrameRateLimit(60f));
            Assert.Contains("graphics: frame rate limit 60, the in-game FPS LIMIT; settings graphics.pcFpsLimit 'fast' is not one of game, 30, 60, 90, 120, 144, 165, 240, unlimited and is ignored; VSYNC unknown", Lines());
            Assert.Null(GraphicsHooks.FrameRateOverrideNote());
        }

        [Fact]
        public void AFailingSettingReadLeavesTheGamesValue()
        {
            GraphicsHooks.ConfigureFrameRate(() => throw new InvalidOperationException("boom"), null);
            Assert.Equal(Bits(90f), Bits(GraphicsHooks.FrameRateLimit(90f)));
        }

        [Fact]
        public void ASaveOnTheSettingsPageReachesThePacerOnTheGameThread()
        {
            // Before the game passed a value through the patch: nothing is set, the line says when it applies.
            setting = "240";
            GraphicsHooks.FrameRateSettingChanged();
            Assert.Empty(posted);
            Assert.Empty(set);
            Assert.Contains("graphics: settings graphics.pcFpsLimit '240' saved; it applies when the game sets its frame rate limit (at start and when FPS LIMIT changes; needs patch 'fpslimit')", Lines());

            setting = "game";
            Assert.Equal(60f, GraphicsHooks.FrameRateLimit(60f));
            setting = "240";
            GraphicsHooks.FrameRateSettingChanged();
            Assert.Empty(set);   // only on the game thread
            Action run = Assert.Single(posted);
            run();
            Assert.Equal(new[] { 240f }, set);
            Assert.Contains("graphics: frame rate limit 240 from settings graphics.pcFpsLimit (in-game FPS LIMIT 60 not used); VSYNC on: a screen slower than 240 Hz caps it (from the settings page)", Lines());

            // The value that is current when the game thread runs it wins (here: back to the game's own).
            setting = "unlimited";
            GraphicsHooks.FrameRateSettingChanged();
            setting = "game";
            posted[1]();
            Assert.Equal(new[] { 240f, 60f }, set);
            Assert.Equal(Bits(60f), Bits(set[1]));

            // No game thread: nothing is set and the player is told.
            postWorks = false;
            setting = "144";
            GraphicsHooks.FrameRateSettingChanged();
            Assert.Equal(2, set.Count);
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn &&
                            e.Message == "graphics: settings graphics.pcFpsLimit '144' saved, but the game thread could not be reached; it applies at the next start or FPS LIMIT change");

            // A setter that throws (no engine): a warning, and the last line stays what the pacer has.
            postWorks = true;
            GraphicsHooks.WcpLimitSetter = value => throw new InvalidOperationException("GameApplication.Instance is null");
            GraphicsHooks.FrameRateSettingChanged();
            posted.Last()();
            Assert.Contains(sink.Snapshot(), e => e.Level == LogLevel.Warn && e.Message.StartsWith("graphics: the frame rate limit 144 could not be set", StringComparison.Ordinal));
            Assert.Contains("frame rate limit 60, the in-game FPS LIMIT", GraphicsHooks.FrameRateSummary);
        }

        // ---- a look-alike of GamePreferencesComponent.instance.World.LocalStorage (its JObject mData) for ReadVsync

        public sealed class Token
        {
            public object Raw;

            public override string ToString()
            {
                return Raw is bool b ? (b ? "True" : "False") : Convert.ToString(Raw, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public sealed class Member
        {
            public Token Value { get; set; }
        }

        public sealed class Data
        {
            public Dictionary<string, object> Members = new Dictionary<string, object>();

            public Member Property(string name)
            {
                object value;
                return Members.TryGetValue(name, out value) ? new Member { Value = new Token { Raw = value } } : null;
            }
        }

        public class StorageBase
        {
            protected Data mData;

            public Data Inner { get { return mData; } set { mData = value; } }
        }

        public sealed class Storage : StorageBase
        {
        }

        public sealed class World
        {
            public Storage LocalStorage { get; set; }
        }

        public sealed class Preferences
        {
            public static Preferences instance;

            public World World { get; set; }
        }

        [Fact]
        public void VsyncIsReadFromTheGamesLocalStorage()
        {
            Preferences.instance = null;
            Assert.Null(GraphicsHooks.ReadVsync(typeof(Preferences)));
            Assert.Null(GraphicsHooks.ReadVsync(null));
            Preferences.instance = new Preferences { World = new World() };
            Assert.Null(GraphicsHooks.ReadVsync(typeof(Preferences)));   // no LocalStorage (a server World)
            Preferences.instance.World.LocalStorage = new Storage();
            Assert.Null(GraphicsHooks.ReadVsync(typeof(Preferences)));   // no data loaded
            Preferences.instance.World.LocalStorage.Inner = new Data();
            Assert.Equal(true, GraphicsHooks.ReadVsync(typeof(Preferences)));   // no member: the game's default
            Preferences.instance.World.LocalStorage.Inner.Members["Vsync"] = false;
            Assert.Equal(false, GraphicsHooks.ReadVsync(typeof(Preferences)));
            Preferences.instance.World.LocalStorage.Inner.Members["Vsync"] = true;
            Assert.Equal(true, GraphicsHooks.ReadVsync(typeof(Preferences)));
            Preferences.instance.World.LocalStorage.Inner.Members["Vsync"] = "maybe";
            Assert.Null(GraphicsHooks.ReadVsync(typeof(Preferences)));
            Preferences.instance = null;
        }
    }
}
