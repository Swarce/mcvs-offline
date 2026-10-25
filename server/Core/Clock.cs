using System;
using System.Globalization;

namespace McvsOffline.Server.Core
{
    /// <summary>Source of the current time. Inject <see cref="ManualClock"/> in tests.</summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        public DateTime UtcNow { get { return DateTime.UtcNow; } }
    }

    /// <summary>A clock that only moves when told to.</summary>
    public sealed class ManualClock : IClock
    {
        private readonly object gate = new object();
        private DateTime now;

        public ManualClock(DateTime startUtc)
        {
            now = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        }

        public DateTime UtcNow
        {
            get { lock (gate) return now; }
            set { lock (gate) now = DateTime.SpecifyKind(value, DateTimeKind.Utc); }
        }

        public void Advance(TimeSpan delta)
        {
            lock (gate) now = now.Add(delta);
        }
    }

    /// <summary>Timestamp formats used on the wire (all invariant-culture, UTC).</summary>
    public static class TimeFormat
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>"2026-10-25T10:00:00Z" (second precision, the style the Gameloft services used).</summary>
        public static string Iso8601(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>"2026-10-25T10:00:00.123Z".</summary>
        public static string Iso8601Millis(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>RFC 1123, as used by the HTTP Date header.</summary>
        public static string Rfc1123(DateTime utc)
        {
            return utc.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
        }

        public static long ToUnixSeconds(DateTime utc)
        {
            return (long)Math.Floor((utc.ToUniversalTime() - Epoch).TotalSeconds);
        }

        public static long ToUnixMillis(DateTime utc)
        {
            return (long)Math.Floor((utc.ToUniversalTime() - Epoch).TotalMilliseconds);
        }

        public static DateTime FromUnixSeconds(long seconds)
        {
            return Epoch.AddSeconds(seconds);
        }

        /// <summary>Parses ISO 8601 / RFC 1123 timestamps as UTC; false when unparseable.</summary>
        public static bool TryParse(string text, out DateTime utc)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                return true;
            }
            return false;
        }
    }
}
