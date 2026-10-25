using System;
using System.Collections;
using System.Collections.Generic;
using McvsOffline.Server.Core.Json;
using Xunit;

namespace McvsOffline.Tests
{
    public class MiniJsonTests
    {
        // JSON "\uXXXX" escape text, built at runtime so the source stays plain ASCII.
        private static string Esc(string hex) => "\\" + "u" + hex;

        private static string Ch(int codePoint) => char.ConvertFromUtf32(codePoint);

        [Fact]
        public void ParsesTheValueModel()
        {
            var o = MiniJson.ParseObject("{\"s\":\"x\",\"i\":42,\"n\":-7,\"d\":1.5,\"e\":1e3,\"t\":true,\"f\":false,\"z\":null,\"a\":[1,\"two\",{}],\"o\":{\"k\":[]}}");
            Assert.Equal("x", o["s"]);
            Assert.Equal(42L, o["i"]);
            Assert.Equal(-7L, o["n"]);
            Assert.Equal(1.5, o["d"]);
            Assert.Equal(1000.0, o["e"]);
            Assert.Equal(true, o["t"]);
            Assert.Equal(false, o["f"]);
            Assert.True(o.ContainsKey("z"));
            Assert.Null(o["z"]);
            var a = Assert.IsType<List<object>>(o["a"]);
            Assert.Equal(1L, a[0]);
            Assert.IsType<Dictionary<string, object>>(a[2]);
            Assert.IsType<List<object>>(((Dictionary<string, object>)o["o"])["k"]);
        }

        [Fact]
        public void RoundTripIsByteIdenticalAndKeepsKeyOrder()
        {
            string json = "{\"zeta\":1,\"alpha\":{\"b\":[true,null,\"x\"],\"a\":-0.25},\"mid\":\"" + Ch(0xe9) + Ch(0x1F600) + "\",\"big\":123456789012345678901234567890,\"empty\":{},\"list\":[]}";
            object parsed = MiniJson.Parse(json);
            Assert.Equal(json, MiniJson.Serialize(parsed));
            Assert.Equal(MiniJson.Serialize(parsed), MiniJson.Serialize(MiniJson.Parse(MiniJson.Serialize(parsed))));
            Assert.Equal(new[] { "zeta", "alpha", "mid", "big", "empty", "list" }, new List<string>(((Dictionary<string, object>)parsed).Keys).ToArray());
        }

        [Fact]
        public void StringEscapesParseAndSerialize()
        {
            string json = "\"q\\\" b\\\\ s\\/ " + Esc("0041") + " " + Esc("00e9") + " " + Esc("d83d") + Esc("de00") + " \\b\\f\\n\\r\\t\"";
            string value = (string)MiniJson.Parse(json);
            Assert.Equal("q\" b\\ s/ A " + Ch(0xe9) + " " + Ch(0x1F600) + " \b\f\n\r\t", value);
            // Writer: quotes, backslash and control characters escaped; non-ASCII stays raw UTF-8.
            Assert.Equal("\"q\\\" b\\\\ s/ A " + Ch(0xe9) + " " + Ch(0x1F600) + " \\b\\f\\n\\r\\t\"", MiniJson.Serialize(value));
            Assert.Equal("\"" + Esc("0001") + Esc("001f") + "\"", MiniJson.Serialize(((char)1).ToString() + (char)0x1f));
            Assert.Equal("\"" + Esc("2028") + Esc("2029") + "\"", MiniJson.Serialize(((char)0x2028).ToString() + (char)0x2029));
        }

        [Fact]
        public void IntegersBeyondLongKeepEveryDigit()
        {
            Assert.Equal(long.MaxValue, MiniJson.Parse("9223372036854775807"));
            Assert.Equal(long.MinValue, MiniJson.Parse("-9223372036854775808"));
            var big = Assert.IsType<JsonNumber>(MiniJson.Parse("9223372036854775808"));
            Assert.Equal("9223372036854775808", big.Literal);
            Assert.Equal("[-99999999999999999999999]", MiniJson.Serialize(MiniJson.Parse("[-99999999999999999999999]")));
            var huge = Assert.IsType<JsonNumber>(MiniJson.Parse("1e400"));
            Assert.Equal("1e400", MiniJson.Serialize(huge));
            Assert.Equal(9007199254740993L, MiniJson.Parse("9007199254740993")); // > 2^53: still exact as long
        }

        [Fact]
        public void DoublesRoundTripAndStayDoubles()
        {
            foreach (double d in new[] { 0.1, 1.0 / 3.0, 123456.789, 1e-300, 5e20, -2.5, 1.0, 0.0 })
            {
                string s = MiniJson.Serialize(d);
                object back = MiniJson.Parse(s);
                Assert.IsType<double>(back);
                Assert.Equal(d, (double)back);
            }
            Assert.Equal("1.0", MiniJson.Serialize(1.0));
            Assert.Equal("null", MiniJson.Serialize(double.NaN));
            Assert.Equal("null", MiniJson.Serialize(double.PositiveInfinity));
        }

        [Fact]
        public void SerializesClrTypes()
        {
            var ht = new Hashtable { { "k", 1 } };
            var value = new Dictionary<string, object>
            {
                ["int"] = 5,
                ["short"] = (short)-3,
                ["byte"] = (byte)200,
                ["uint"] = 4000000000u,
                ["ulong"] = ulong.MaxValue,
                ["float"] = 0.5f,
                ["decimal"] = 1.25m,
                ["char"] = 'c',
                ["array"] = new[] { "a", "b" },
                ["legacy"] = ht,
            };
            Assert.Equal("{\"int\":5,\"short\":-3,\"byte\":200,\"uint\":4000000000,\"ulong\":18446744073709551615,\"float\":0.5,\"decimal\":1.25,\"char\":\"c\",\"array\":[\"a\",\"b\"],\"legacy\":{\"k\":1}}",
                         MiniJson.Serialize(value));
            Assert.Throws<ArgumentException>(() => MiniJson.Serialize(new object()));
        }

        [Fact]
        public void IndentedOutput()
        {
            var o = MiniJson.ParseObject("{\"a\":1,\"b\":[1,2],\"c\":{},\"d\":[]}");
            Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    1,\n    2\n  ],\n  \"c\": {},\n  \"d\": []\n}", MiniJson.Serialize(o, true));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{")]
        [InlineData("{\"a\":1,}")]
        [InlineData("[1,]")]
        [InlineData("{a:1}")]
        [InlineData("01")]
        [InlineData("1.")]
        [InlineData("-")]
        [InlineData("1e")]
        [InlineData("\"unterminated")]
        [InlineData("\"bad \\x escape\"")]
        [InlineData("tru")]
        [InlineData("{} {}")]
        [InlineData("NaN")]
        public void RejectsMalformedJson(string json)
        {
            Assert.Throws<JsonParseException>(() => MiniJson.Parse(json));
            Assert.False(MiniJson.TryParse(json, out _));
        }

        [Fact]
        public void ToleratesBomAndWhitespace()
        {
            Assert.Equal(1L, ((Dictionary<string, object>)MiniJson.Parse(((char)0xFEFF) + " \r\n\t{\"a\" : 1 }\n"))["a"]);
        }

        [Fact]
        public void DuplicateKeysKeepTheLastValue()
        {
            Assert.Equal(2L, MiniJson.ParseObject("{\"a\":1,\"a\":2}")["a"]);
        }

        [Fact]
        public void NestingDepthIsLimited()
        {
            string ok = new string('[', 500) + new string(']', 500);
            Assert.NotNull(MiniJson.Parse(ok));
            string tooDeep = new string('[', 600) + new string(']', 600);
            Assert.Throws<JsonParseException>(() => MiniJson.Parse(tooDeep));
        }

        [Fact]
        public void JsonValueConversionsAreLenient()
        {
            Assert.Equal(true, JsonValue.AsBool("True"));   // the client's C# bool casing
            Assert.Equal(false, JsonValue.AsBool("False"));
            Assert.Null(JsonValue.AsBool("maybe"));
            Assert.Equal(12L, JsonValue.AsLong("12"));
            Assert.Equal(12L, JsonValue.AsLong(12.0));
            Assert.Null(JsonValue.AsLong(12.5));
            Assert.Null(JsonValue.AsLong(1e30));
            Assert.Equal(2.5, JsonValue.AsDouble("2.5"));
            Assert.Equal("3", JsonValue.AsString(3L));
            Assert.True(JsonValue.DeepEquals(MiniJson.Parse("{\"a\":[1,{\"b\":2.0}]}"), MiniJson.Parse("{\"a\":[1.0,{\"b\":2}]}")));
            Assert.False(JsonValue.DeepEquals(MiniJson.Parse("[1,2]"), MiniJson.Parse("[2,1]")));
        }

        [Fact]
        public void DeepCloneDoesNotAlias()
        {
            var o = MiniJson.ParseObject("{\"a\":{\"b\":[1]}}");
            var c = JsonValue.DeepCloneObject(o);
            ((List<object>)((Dictionary<string, object>)c["a"])["b"]).Add(2L);
            Assert.Equal("{\"a\":{\"b\":[1]}}", MiniJson.Serialize(o));
        }
    }
}
