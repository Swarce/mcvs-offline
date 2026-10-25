using System;
using System.Globalization;

namespace McvsOffline.Server.Core.Json
{
    /// <summary>
    /// A JSON number kept as its original literal because it does not fit the normal value model:
    /// an integer outside the <see cref="long"/> range, or a value that would overflow a double.
    /// It serializes back byte-for-byte, so such numbers survive a parse/serialize round trip unchanged.
    /// </summary>
    public sealed class JsonNumber : IEquatable<JsonNumber>
    {
        public JsonNumber(string literal)
        {
            if (string.IsNullOrEmpty(literal)) throw new ArgumentException("empty number literal", "literal");
            Literal = literal;
        }

        /// <summary>The number exactly as it appeared in the JSON text.</summary>
        public string Literal { get; private set; }

        /// <summary>Best-effort conversion (may lose precision or be infinite).</summary>
        public double ToDouble()
        {
            double value;
            if (double.TryParse(Literal, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return value;
            return Literal.StartsWith("-", StringComparison.Ordinal) ? Num.NegativeInfinity : Num.PositiveInfinity;
        }

        public bool Equals(JsonNumber other)
        {
            return other != null && string.Equals(Literal, other.Literal, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) { return Equals(obj as JsonNumber); }

        public override int GetHashCode() { return Literal.GetHashCode(); }

        public override string ToString() { return Literal; }
    }
}
