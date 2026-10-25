using System;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using Xunit;

namespace McvsOffline.Tests
{
    public class RouterTests
    {
        private static Router NewRouter(out MemoryLogSink logs, string unknownLog = null)
        {
            logs = new MemoryLogSink();
            var logger = new Logger();
            logger.AddSink(logs);
            return new Router(new RequestLogger(logger, unknownLog, () => 4096));
        }

        private static HttpResponse Named(HttpRequest r) => HttpResponse.Text(r.RouteName);

        [Fact]
        public void MatchesLiteralsCaseInsensitivelyAndCapturesParameters()
        {
            var router = NewRouter(out _);
            router.Get("config/{clientId}/datacenters/{dc}/urls", "eve.urls",
                r => HttpResponse.Text(r.RouteValue("clientId") + "|" + r.RouteValue("dc")));
            var response = router.Dispatch(HttpRequest.Create("GET", "//CONFIG/2620:66102:1.17.32:android:googleplay/datacenters/mcvs/urls/"));
            Assert.Equal("2620:66102:1.17.32:android:googleplay|mcvs", response.BodyText);
        }

        [Fact]
        public void ParametersAreDecodedAndRawIsAvailable()
        {
            var router = NewRouter(out _);
            router.Get("profiles/{credential}/myprofile", "storage.get",
                r => HttpResponse.Text(r.RouteValue("credential") + "|" + r.RouteValues.GetRaw("credential")));
            Assert.Equal("fed_id:abc|fed_id%3Aabc", router.Dispatch(HttpRequest.Create("GET", "/profiles/fed_id%3Aabc/myprofile")).BodyText);
        }

        [Fact]
        public void CatchAllMatchesZeroOrMoreSegments()
        {
            var router = NewRouter(out _);
            router.Get("profiles/{credential}/myprofile/{*selector}", "storage.selector",
                r => HttpResponse.Text("[" + r.RouteValue("selector") + "]"));
            Assert.Equal("[]", router.Dispatch(HttpRequest.Create("GET", "/profiles/me/myprofile")).BodyText);
            Assert.Equal("[_mcvs.gameProfile]", router.Dispatch(HttpRequest.Create("GET", "/profiles/me/myprofile/_mcvs.gameProfile")).BodyText);
            Assert.Equal("[a/b]", router.Dispatch(HttpRequest.Create("GET", "/profiles/me/myprofile/a/b")).BodyText);
        }

        [Fact]
        public void SpecificRoutesBeatCatchAllsRegisteredEarlier()
        {
            var router = NewRouter(out _);
            router.Any("{*anything}", "misc.catchall", Named);                 // e.g. Misc registered first
            router.Post("authorize", "auth.authorize", Named);
            router.Get("users/{credential}", "auth.user", Named);
            router.Get("users/me", "auth.me", Named, order: -1);             // explicit order beats registration order
            Assert.Equal("auth.authorize", router.Dispatch(HttpRequest.Create("POST", "/authorize", "a=1")).BodyText);
            Assert.Equal("auth.me", router.Dispatch(HttpRequest.Create("GET", "/users/me")).BodyText);
            Assert.Equal("auth.user", router.Dispatch(HttpRequest.Create("GET", "/users/anonymous:x")).BodyText);
            Assert.Equal("misc.catchall", router.Dispatch(HttpRequest.Create("GET", "/redir/?x=1")).BodyText);
        }

        [Fact]
        public void FirstRegisteredWinsAmongEqualOrders()
        {
            var router = NewRouter(out _);
            router.Get("a/{x}", "first", Named);
            router.Get("a/{y}", "second", Named);
            Assert.Equal("first", router.Dispatch(HttpRequest.Create("GET", "/a/1")).BodyText);
        }

        [Fact]
        public void HeadFallsBackToGetAndAnyMatchesEveryMethod()
        {
            var router = NewRouter(out _);
            router.Get("x", "x.get", Named);
            router.Any("y", "y.any", Named);
            Assert.Equal("x.get", router.Dispatch(HttpRequest.Create("HEAD", "/x")).BodyText);
            Assert.Equal("y.any", router.Dispatch(HttpRequest.Create("DELETE", "/y")).BodyText);
        }

        [Fact]
        public void UnknownEndpointsAreLoggedInFullAndAnsweredConservatively()
        {
            using var dir = new TempDir();
            string unknownLog = dir.Combine("unknown.jsonl");
            var router = NewRouter(out var logs, unknownLog);
            router.Get("only/get", "only.get", Named);

            var r1 = router.Dispatch(HttpRequest.Create("POST", "/not/here?client_id=abc", "credential=fed_id%3Ax&secret=1"));
            Assert.Equal(200, r1.StatusCode);
            Assert.Equal("{}", r1.BodyText);
            Assert.True(logs.Contains("UNKNOWN ENDPOINT"));
            Assert.True(logs.Contains("credential = fed_id:x"));
            Assert.True(logs.Contains("client_id = abc"));

            var r2 = router.Dispatch(HttpRequest.Create("POST", "/only/get", "a=1"));
            Assert.Equal("{}", r2.BodyText);
            Assert.True(logs.Contains("path exists for GET but not for POST"));

            string[] lines = File.ReadAllLines(unknownLog);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"path\":\"/not/here\"", lines[0]);
            Assert.Contains("\"form\":{\"credential\":\"fed_id:x\",\"secret\":\"1\"}", lines[0]);
        }

        [Fact]
        public void CustomFallbackReceivesTheReason()
        {
            var router = NewRouter(out _);
            router.Fallback = (r, reason) => HttpResponse.Text(reason, 404);
            var response = router.Dispatch(HttpRequest.Create("GET", "/nothing"));
            Assert.Equal(404, response.StatusCode);
            Assert.Equal("no route matches the path", response.BodyText);
        }

        [Fact]
        public void HandlerExceptionsBecomeLogged500s()
        {
            var router = NewRouter(out var logs);
            router.Get("boom", "test.boom", r => throw new InvalidOperationException("kaboom"));
            var response = router.Dispatch(HttpRequest.Create("GET", "/boom?x=1"));
            Assert.Equal(500, response.StatusCode);
            Assert.True(logs.Contains("kaboom"));
            Assert.True(logs.Contains("test.boom"));
        }

        [Fact]
        public void RouteNameIsRecordedOnTheRequest()
        {
            var router = NewRouter(out _);
            router.Get("x", "x.name", r => HttpResponse.EmptyObject());
            var request = HttpRequest.Create("GET", "/x");
            router.Dispatch(request);
            Assert.Equal("x.name", request.RouteName);
            var unknown = HttpRequest.Create("GET", "/y");
            router.Dispatch(unknown);
            Assert.Equal("unknown", unknown.RouteName);
        }

        [Theory]
        [InlineData("a/{*rest}/b")]
        [InlineData("a/{x}/{x}")]
        [InlineData("a/{}")]
        [InlineData("a/pre{x}")]
        public void InvalidTemplatesAreRejected(string template)
        {
            Assert.Throws<ArgumentException>(() => RouteTemplate.Parse(template));
        }
    }
}
