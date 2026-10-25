using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace McvsOffline.Server.Core.Json
{
    /// <summary>
    /// Typed access to MiniJson values. All conversions are lenient in the way the Federation needs: form values
    /// arrive as strings (and booleans in C# casing, "True"/"False", report 02 §1.4), JSON numbers may be long or
    /// double. Methods never throw; they return null / the default when a value does not convert.
    /// </summary>
    public static class JsonValue
    {
        public static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static List<object> AsArray(object value)
        {
            return value as List<object>;
        }

        public static bool IsObject(object value) { return value is Dictionary<string, object>; }

        public static bool IsArray(object value) { return value is List<object>; }

        /// <summary>String value; numbers and booleans are rendered with the invariant culture.</summary>
        public static string AsString(object value)
        {
            if (value == null) return null;
            string s = value as string;
            if (s != null) return s;
            if (value is bool) return (bool)value ? "true" : "false";
            if (value is long) return ((long)value).ToString(CultureInfo.InvariantCulture);
            if (value is int) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (value is double) return MiniJson.FormatDouble((double)value);
            var number = value as JsonNumber;
            if (number != null) return number.Literal;
            return null;
        }

        public static long? AsLong(object value)
        {
            if (value == null) return null;
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            if (value is double)
            {
                double d = (double)value;
                if (Num.IsIntegralLong(d)) return (long)d;
                return null;
            }
            string s = value as string;
            if (s != null)
            {
                long l;
                if (long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) return l;
                double d;
                if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                    && Num.IsIntegralLong(d))
                {
                    return (long)d;
                }
                return null;
            }
            if (value is bool) return (bool)value ? 1 : 0;
            return null;
        }

        public static double? AsDouble(object value)
        {
            if (value == null) return null;
            if (value is double) return (double)value;
            if (value is long) return (long)value;
            if (value is int) return (int)value;
            var number = value as JsonNumber;
            if (number != null) return number.ToDouble();
            string s = value as string;
            if (s != null)
            {
                double d;
                if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return null;
        }

        /// <summary>Accepts JSON booleans, "true"/"false" in any casing (the client sends "True"), and 0/1.</summary>
        public static bool? AsBool(object value)
        {
            if (value == null) return null;
            if (value is bool) return (bool)value;
            if (value is long) return (long)value != 0;
            if (value is int) return (int)value != 0;
            string s = value as string;
            if (s != null)
            {
                s = s.Trim();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            }
            return null;
        }

        // Dictionary shortcuts ------------------------------------------------------------------

        public static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && key != null && obj.TryGetValue(key, out value) ? value : null;
        }

        public static string GetString(Dictionary<string, object> obj, string key, string defaultValue = null)
        {
            return AsString(Get(obj, key)) ?? defaultValue;
        }

        public static long GetLong(Dictionary<string, object> obj, string key, long defaultValue = 0)
        {
            return AsLong(Get(obj, key)) ?? defaultValue;
        }

        public static double GetDouble(Dictionary<string, object> obj, string key, double defaultValue = 0)
        {
            return AsDouble(Get(obj, key)) ?? defaultValue;
        }

        public static bool GetBool(Dictionary<string, object> obj, string key, bool defaultValue = false)
        {
            return AsBool(Get(obj, key)) ?? defaultValue;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> obj, string key)
        {
            return AsObject(Get(obj, key));
        }

        public static List<object> GetArray(Dictionary<string, object> obj, string key)
        {
            return AsArray(Get(obj, key));
        }

        // Structural helpers ---------------------------------------------------------------------

        /// <summary>Deep copy of a MiniJson value (objects and arrays are copied, scalars shared).</summary>
        public static object DeepClone(object value)
        {
            var obj = value as Dictionary<string, object>;
            if (obj != null)
            {
                var copy = new Dictionary<string, object>(obj.Count, StringComparer.Ordinal);
                foreach (var kv in obj) copy[kv.Key] = DeepClone(kv.Value);
                return copy;
            }
            var arr = value as List<object>;
            if (arr != null)
            {
                var copy = new List<object>(arr.Count);
                foreach (var item in arr) copy.Add(DeepClone(item));
                return copy;
            }
            return value;
        }

        public static Dictionary<string, object> DeepCloneObject(Dictionary<string, object> value)
        {
            return (Dictionary<string, object>)DeepClone(value);
        }

        /// <summary>
        /// Structural equality. Object key order is ignored; numbers compare by value (1 == 1.0).
        /// </summary>
        public static bool DeepEquals(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;

            var oa = a as Dictionary<string, object>;
            var ob = b as Dictionary<string, object>;
            if (oa != null || ob != null)
            {
                if (oa == null || ob == null || oa.Count != ob.Count) return false;
                foreach (var kv in oa)
                {
                    object other;
                    if (!ob.TryGetValue(kv.Key, out other) || !DeepEquals(kv.Value, other)) return false;
                }
                return true;
            }

            var la = a as IList;
            var lb = b as IList;
            if ((la != null && !(a is string)) || (lb != null && !(b is string)))
            {
                if (la == null || lb == null || la.Count != lb.Count) return false;
                for (int i = 0; i < la.Count; i++)
                {
                    if (!DeepEquals(la[i], lb[i])) return false;
                }
                return true;
            }

            if (IsNumber(a) && IsNumber(b))
            {
                var na = a as JsonNumber;
                var nb = b as JsonNumber;
                if (na != null && nb != null) return na.Equals(nb);
                if (a is long && b is long) return (long)a == (long)b;
                return AsDouble(a) == AsDouble(b);
            }

            return a.Equals(b);
        }

        public static bool IsNumber(object value)
        {
            return value is long || value is int || value is double || value is float || value is decimal
                || value is JsonNumber || value is short || value is byte || value is uint || value is ulong;
        }
    }
}
