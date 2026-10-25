namespace McvsOffline.Server.Core
{
    /// <summary>
    /// Numeric constants and helpers the game's trimmed mscorlib does not have: the linker removed the const
    /// fields int.MaxValue, long.MinValue/MaxValue, double.PositiveInfinity…, and Math.Max/Min(long, long) (a call
    /// would silently bind to the float overload). Use these in shared code instead.
    /// </summary>
    public static class Num
    {
        public const int IntMax = 2147483647;
        public const int IntMin = -2147483647 - 1;
        public const long LongMax = 9223372036854775807L;
        public const long LongMin = -9223372036854775807L - 1;
        public const double PositiveInfinity = 1.0 / 0.0;
        public const double NegativeInfinity = -1.0 / 0.0;

        /// <summary>2^63 as a double: doubles in [-2^63, 2^63) convert to long without overflow.</summary>
        public const double LongRangeLimit = 9223372036854775808.0;

        public static long Max(long a, long b) { return a > b ? a : b; }

        public static long Min(long a, long b) { return a < b ? a : b; }

        public static int Max(int a, int b) { return a > b ? a : b; }

        public static int Min(int a, int b) { return a < b ? a : b; }

        public static long Clamp(long value, long min, long max) { return value < min ? min : value > max ? max : value; }

        /// <summary>True when <paramref name="d"/> is an integral value representable as a long.</summary>
        public static bool IsIntegralLong(double d)
        {
            return d >= -LongRangeLimit && d < LongRangeLimit && System.Math.Floor(d) == d;
        }
    }
}
