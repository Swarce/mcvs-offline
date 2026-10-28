using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests.Core
{
    /// <summary>The one redaction function (design §5.4).</summary>
    public class LogRedactionTests
    {
        private const string Ticket = "0123456789abcdef0123456789abcdef";
        private const string Cookie = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

        [Theory]
        // Tickets in fragments, plain and percent-encoded (any case of the escapes).
        [InlineData("open http://127.0.0.1:51780/_mcvs/ui/#t=" + Ticket + "&s=saves", "open http://127.0.0.1:51780/_mcvs/ui/#t=<redacted>&s=saves")]
        [InlineData("url=http%3A%2F%2F127.0.0.1%3A51780%2F_mcvs%2Fui%2F%23t%3D" + Ticket, "url=http%3A%2F%2F127.0.0.1%3A51780%2F_mcvs%2Fui%2F#t=<redacted>")]
        [InlineData("x %23T%3d" + Ticket + " y", "x #t=<redacted> y")]
        [InlineData("#t=abcdef12", "#t=<redacted>")]
        [InlineData("#t=abc", "#t=abc")]                        // too short to be a ticket
        // Session cookies.
        [InlineData("Cookie: mcvs_s=" + Cookie, "Cookie: mcvs_s=<redacted>")]
        [InlineData("a=1; mcvs_s=" + Cookie + "; b=2", "a=1; mcvs_s=<redacted>; b=2")]
        [InlineData("Set-Cookie: mcvs_s=; Path=/_mcvs/; Max-Age=0", "Set-Cookie: mcvs_s=; Path=/_mcvs/; Max-Age=0")]
        // The ticket in a JSON body, any spacing.
        [InlineData("{\"ticket\":\"" + Ticket + "\"}", "{\"ticket\":\"<redacted>\"}")]
        [InlineData("{ \"ticket\" : \"" + Ticket + "\", \"x\": 1}", "{ \"ticket\" : \"<redacted>\", \"x\": 1}")]
        // A form/query parameter t=<32 hex> (post mode), but not other t= values.
        [InlineData("t=" + Ticket, "t=<redacted>")]
        [InlineData("form: t=" + Ticket + " | x", "form: t=<redacted> | x")]
        [InlineData("a=1&t=" + Ticket + "&b=2", "a=1&t=<redacted>&b=2")]
        [InlineData("t=12", "t=12")]
        [InlineData("count=" + Ticket, "count=" + Ticket)]
        // Passwords and tokens: the first 4 characters stay.
        [InlineData("username=p&password=mcvsoffline&scope=auth", "username=p&password=mcvs…<redacted>&scope=auth")]
        [InlineData("POST /authorize?access_token=mcvs1a2b.0011&x=1", "POST /authorize?access_token=mcvs…<redacted>&x=1")]
        [InlineData("refresh_token=abcdefgh token=zyxwvuts", "refresh_token=abcd…<redacted> token=zyxw…<redacted>")]
        [InlineData("    password = hunter22\n", "    password = hunt…<redacted>\n")]
        [InlineData("{\"superPassword\": \"mcvsoffline\", \"port\": 51780}", "{\"superPassword\": \"mcvs…<redacted>\", \"port\": 51780}")]
        [InlineData("{\"Password\":\"ab\"}", "{\"Password\":\"ab…<redacted>\"}")]
        [InlineData("PASSWORD=secret", "PASSWORD=secr…<redacted>")]
        // Names that only contain a secret name are not secret.
        [InlineData("tokens=3&mytoken=4&password2=5", "tokens=3&mytoken=4&password2=5")]
        [InlineData("token count: 5", "token count: 5")]
        public void Redacts(string input, string expected)
        {
            Assert.Equal(expected, LogRedaction.Redact(input));
        }

        [Fact]
        public void TextWithoutTriggersIsReturnedUnchanged()
        {
            string text = "#12 GET /configs/users/me -> 200 [crm.config] 89282 B 3.1 ms";
            Assert.Same(text, LogRedaction.Redact(text));
            Assert.Null(LogRedaction.Redact(null));
            Assert.Same("", LogRedaction.Redact(""));
            // A trigger without a secret: a new string with the same text is fine, but nothing changes.
            Assert.Equal("set=1 ticket booth", LogRedaction.Redact("set=1 ticket booth"));
        }

        [Fact]
        public void RedactingTwiceChangesNothing()
        {
            string[] inputs =
            {
                "password=mcvsoffline&access_token=abcdefgh",
                "{\"ticket\":\"" + Ticket + "\",\"password\":\"x\"}",
                "#t=" + Ticket + " mcvs_s=" + Cookie + " t=" + Ticket,
            };
            foreach (string input in inputs)
            {
                string once = LogRedaction.Redact(input);
                Assert.Equal(once, LogRedaction.Redact(once));
                Assert.DoesNotContain(Ticket, once);
                Assert.DoesNotContain(Cookie, once);
            }
        }

        [Fact]
        public void SecretsSplitOverSeveralParametersAndMembersAreAllFound()
        {
            string text = "query: access_token=AAAAbbbb | form: password=PPPPqqqq&t=" + Ticket
                          + "\n  body: {\"ticket\":\"" + Ticket + "\",\"nested\":{\"token\":\"TTTTuuuu\"}}";
            string redacted = LogRedaction.Redact(text);
            Assert.DoesNotContain("bbbb", redacted);
            Assert.DoesNotContain("qqqq", redacted);
            Assert.DoesNotContain("uuuu", redacted);
            Assert.DoesNotContain(Ticket, redacted);
            Assert.Contains("AAAA…<redacted>", redacted);
            Assert.Contains("\"token\":\"TTTT…<redacted>\"", redacted);
        }

        [Fact]
        public void BrokenInputNeverThrows()
        {
            foreach (string text in new[] { "\"ticket\":\"", "\"password\" :", "password=", "#t=", "%23t%3", "{\"ticket\":\"abc\\", "mcvs_s=" })
                Assert.NotNull(LogRedaction.Redact(text));
            Assert.Equal("{\"ticket\":\"<redacted>", LogRedaction.Redact("{\"ticket\":\"abc"));
        }
    }
}
