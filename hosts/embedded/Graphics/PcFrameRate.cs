// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent: compiled into the embedded DLLs and into the net8 tests.
using System;
using System.Globalization;
using System.Text;

namespace McvsOffline.Embedded.Graphics
{
    /// <summary>
    /// settings.json <c>graphics.pcFpsLimit</c> (PC, patch 'fpslimit', build 27; docs/design/pc-port.md §8.11): the frame rate limit
    /// the Steam game gives WCPToolkit's pacer instead of its own FPS LIMIT row (30, 60, 90, 120).
    /// <para>
    /// The pacer (WCPToolkit.dll 0x10011430) stores <c>max(1, value)</c> and spins each frame until <c>1e6/limit</c> µs minus 100 µs
    /// have passed. So: 0 and -1 (Android's "no limit") become 1 FPS, NaN is stored and the pacer never returns (a hang), +Inf takes
    /// the CRT's invalid-conversion path every frame, and any limit of about 9901 or more never waits. "unlimited" is therefore the
    /// finite <see cref="UnlimitedLimit"/> (frames of at least 0.9 ms: no screen is faster, and the client World's float physics
    /// clock never sees sub-millisecond frames), and every value the game or the setting gives is sanitised first
    /// (<see cref="Sanitize"/>). VSYNC is the game's own switch: with it on, the screen's refresh rate caps whatever the pacer allows.
    /// </para>
    /// </summary>
    public static class PcFrameRate
    {
        public const string SettingsKey = "graphics.pcFpsLimit";

        /// <summary>The default: the in-game FPS LIMIT row decides, as shipped.</summary>
        public const string Game = "game";

        public const string Unlimited = "unlimited";

        /// <summary>The page's choices, in order.</summary>
        public static readonly string[] Options = { Game, "30", "60", "90", "120", "144", "165", "240", Unlimited };

        /// <summary>The pacer limit "unlimited" sets: finite, above any screen (frames of at least 0.9 ms).</summary>
        public const float UnlimitedLimit = 1000f;

        /// <summary>The highest limit passed on: from about 9901 the pacer never waits anyway.</summary>
        public const float MaxLimit = 10000f;

        /// <summary>The pacer's own floor (it stores max(1, value)).</summary>
        public const float MinLimit = 1f;

        /// <summary>What a NaN, infinite, zero or negative value becomes (the pacer's constructor default).</summary>
        public const float Fallback = 60f;

        /// <summary>
        /// A limit the pacer handles: NaN, ±Inf, 0 and below become <see cref="Fallback"/>, anything else is clamped to
        /// [<see cref="MinLimit"/>, <see cref="MaxLimit"/>]. Every value in that range (all the game's own: 30, 60, 90, 120) comes
        /// back bit for bit.
        /// </summary>
        public static float Sanitize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) return Fallback;
            if (value < MinLimit) return MinLimit;
            if (value > MaxLimit) return MaxLimit;
            return value;
        }

        /// <summary>
        /// The setting as one of <see cref="Game"/> (also for a missing or empty value), <see cref="Unlimited"/> or a limit in
        /// invariant text ("144"; a hand-written number from 1 to 10000 is accepted too); null for anything else, which leaves the
        /// in-game value in charge.
        /// </summary>
        public static string Normalize(object setting)
        {
            if (setting == null) return Game;
            var text = setting as string;
            if (text != null)
            {
                text = text.Trim().ToLowerInvariant();
                if (text.Length == 0 || text == Game) return Game;
                if (text == Unlimited) return Unlimited;
                double parsed;
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? Number(parsed) : null;
            }
            if (setting is long || setting is int || setting is double || setting is float)
                return Number(Convert.ToDouble(setting, CultureInfo.InvariantCulture));
            return null;
        }

        private static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < MinLimit || value > MaxLimit) return null;
            return ((float)value).ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The pacer limit for the game's <paramref name="gameValue"/> under the normalised <paramref name="choice"/>:
        /// <see cref="Game"/> or null: the game's value (sanitised); a number: that limit; <see cref="Unlimited"/>:
        /// <see cref="UnlimitedLimit"/>. Never NaN, infinite or below 1.
        /// </summary>
        public static float Resolve(float gameValue, string choice)
        {
            if (choice == Unlimited) return UnlimitedLimit;
            if (choice != null && choice != Game)
            {
                double parsed;
                if (double.TryParse(choice, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return Sanitize((float)parsed);
            }
            return Sanitize(gameValue);
        }

        /// <summary>True when <paramref name="choice"/> replaces the game's value (a number or unlimited).</summary>
        public static bool Overrides(string choice)
        {
            return choice != null && choice != Game;
        }

        /// <summary>
        /// "frame rate limit 144 from settings graphics.pcFpsLimit (in-game FPS LIMIT 60 not used); VSYNC on: a screen slower than
        /// 144 Hz caps it" and the other wordings (the game's value, unlimited, an unknown setting, a value that was not usable).
        /// <paramref name="raw"/> is the setting as read, <paramref name="vsync"/> the game's VSYNC (null: unknown).
        /// </summary>
        public static string Describe(float gameValue, object raw, string choice, float limit, bool? vsync)
        {
            var sb = new StringBuilder("frame rate limit ");
            string game = Show(gameValue);
            if (choice == Unlimited)
            {
                sb.Append("unlimited from settings ").Append(SettingsKey).Append(" (pacer at ").Append(Show(limit))
                  .Append("; in-game FPS LIMIT ").Append(game).Append(" not used)");
            }
            else if (Overrides(choice))
            {
                sb.Append(Show(limit)).Append(" from settings ").Append(SettingsKey).Append(" (in-game FPS LIMIT ").Append(game).Append(" not used)");
            }
            else
            {
                sb.Append(Show(limit)).Append(", the in-game FPS LIMIT");
                if (limit != gameValue)
                    sb.Append(" (its value ").Append(game).Append(" is not a usable limit)");
                if (choice == null)
                    sb.Append("; settings ").Append(SettingsKey).Append(" '").Append(Convert.ToString(raw, CultureInfo.InvariantCulture))
                      .Append("' is not one of ").Append(string.Join(", ", Options)).Append(" and is ignored");
                else
                    sb.Append(" (settings ").Append(SettingsKey).Append(' ').Append(Game).Append(')');
            }
            if (!vsync.HasValue) sb.Append("; VSYNC unknown");
            else if (!vsync.Value) sb.Append("; VSYNC off");
            else if (choice == Unlimited) sb.Append("; VSYNC on: the screen's refresh rate caps it");
            else if (limit > 60f) sb.Append("; VSYNC on: a screen slower than ").Append(Show(limit)).Append(" Hz caps it");
            else sb.Append("; VSYNC on");
            return sb.ToString();
        }

        private static string Show(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
