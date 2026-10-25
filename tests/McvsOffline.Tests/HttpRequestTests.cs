using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>Request-target parsing, percent-decoding and parameter lookup.</summary>
    public class HttpRequestTests
    {
        [Fact]
        public void DoubleAndTrailingSlashesAreNormalizedButRawIsKept()
        {
            var r = HttpRequest.Create("GET", "//config/2620:66102:1.17.32:android:googleplay/datacenters/");
            Assert.Equal(new[] { "config", "2620:66102:1.17.32:android:googleplay", "datacenters" }, r.Segments);
            Assert.Equal("/config/2620:66102:1.17.32:android:googleplay/datacenters", r.Path);
            Assert.Equal("//config/2620:66102:1.17.32:android:googleplay/datacenters/", r.RawPath);
        }

        [Fact]
        public void FedIdSegmentsDecodeWhileRawFormStaysAvailable()
        {
            var r = HttpRequest.Create("GET", "/profiles/fed_id%3A9f2c0d1e/myprofile/_mcvs.gameProfile");
            Assert.Equal("fed_id:9f2c0d1e", r.Segments[1]);
            Assert.Equal("fed_id%3A9f2c0d1e", r.RawSegments[1]);
            var plain = HttpRequest.Create("GET", "/profiles/fed_id:9f2c0d1e/myprofile");
            Assert.Equal("fed_id:9f2c0d1e", plain.Segments[1]);
        }

        [Fact]
        public void EncodedSlashStaysInsideItsSegmentAndPlusIsLiteralInPaths()
        {
            var r = HttpRequest.Create("GET", "/a%2Fb/c+d");
            Assert.Equal(new[] { "a/b", "c+d" }, r.Segments);
        }

        [Fact]
        public void QueryParsingDecodesPercentAndPlus()
        {
            var r = HttpRequest.Create("GET", "/x?a=1&b=x%20y&c=a+b&d&e=%C3%A9&f=%zz&a=2");
            Assert.Equal("1", r.Query["a"]);
            Assert.Equal(new[] { "1", "2" }, r.Query.GetAll("a"));
            Assert.Equal("x y", r.Query["b"]);
            Assert.Equal("a b", r.Query["c"]);
            Assert.Equal("", r.Query["d"]);
            Assert.Equal("é", r.Query["e"]);
            Assert.Equal("%zz", r.Query["f"]);
            Assert.Null(r.Query["missing"]);
        }

        [Fact]
        public void ParamPrefersTheFormBodyOverTheQuery()
        {
            // DELETE carries access_token in the query and the full form in the body (report 02 §1.4).
            var r = HttpRequest.Create("DELETE", "/messages/secured/me?access_token=q", "access_token=f&msgids=a%2Cb");
            Assert.Equal("f", r.Param("access_token"));
            Assert.Equal("a,b", r.Param("msgids"));
            Assert.Equal("q", r.Query["access_token"]);
            Assert.True(r.HasParam("msgids"));
        }

        [Fact]
        public void FormIsEmptyForNonFormContent()
        {
            var r = HttpRequest.Create("POST", "/x", "a=1", "text/plain");
            Assert.Equal(0, r.Form.Count);
            Assert.Equal("a=1", r.BodyText);
        }

        [Fact]
        public void JsonBodyIsParsedLazily()
        {
            var r = HttpRequest.Create("POST", "/api/campaign", "{\"comment\":\"x\",\"n\":3}", "application/json");
            var obj = Assert.IsType<System.Collections.Generic.Dictionary<string, object>>(r.Json);
            Assert.Equal(3L, obj["n"]);
            Assert.Null(HttpRequest.Create("POST", "/x", "{bad", "application/json").Json);
        }

        [Fact]
        public void AbsoluteFormTargetsAreReducedToPathAndQuery()
        {
            var r = HttpRequest.Create("GET", "http://ip.ecomapis.gameloft.org/location/?ip=127.0.0.1");
            Assert.Equal("/location", r.Path);
            Assert.Equal("127.0.0.1", r.Query["ip"]);
            Assert.Equal("/", HttpRequest.Create("GET", "http://host:51780").Path);
        }

        [Fact]
        public void UrlEncodingMatchesEscapeDataString()
        {
            string s = "fed_id:abc def/é+~";
            Assert.Equal(System.Uri.EscapeDataString(s), UrlEncoding.Encode(s));
            Assert.Equal(s, UrlEncoding.Decode(UrlEncoding.Encode(s), false));
        }
    }
}
