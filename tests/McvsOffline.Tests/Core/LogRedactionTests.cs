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

        /// <summary>The PC connectivity tracker's MAC address (docs/design/pc-port.md §4.2), in every shape it is logged in.</summary>
        [Theory]
        // The INFO summary (form: a=b c=d), the DEBUG dump (name = value), the raw body and the query.
        [InlineData("form: type=auth status=1 device_id=04:BF:1B:6C:B9:62", "form: type=auth status=1 device_id=<redacted-mac>")]
        [InlineData("    device_id = 04:BF:1B:6C:B9:62\n", "    device_id = <redacted-mac>\n")]
        [InlineData("&type=auth&client_id=2620%3A70425&device_id=04%3ABF%3A1B%3A6C%3AB9%3A62", "&type=auth&client_id=2620%3A70425&device_id=<redacted-mac>")]
        [InlineData("device_id=04%3abf%3a1b%3a6c%3ab9%3a62&x=1", "device_id=<redacted-mac>&x=1")]
        [InlineData("device_id=04-bf-1b-6c-b9-62", "device_id=<redacted-mac>")]
        [InlineData("global_device_id=04:BF:1B:6C:B9:62&scope=x", "global_device_id=<redacted-mac>&scope=x")]
        // The jsonl entry and JSON bodies.
        [InlineData("{\"form\":{\"client_id\":\"2620:70425:1.17.30:steam:steam\",\"device_id\":\"04:BF:1B:6C:B9:62\"}}",
                    "{\"form\":{\"client_id\":\"2620:70425:1.17.30:steam:steam\",\"device_id\":\"<redacted-mac>\"}}")]
        [InlineData("{\"global_device_id\" : \"04-BF-1B-6C-B9-62\"}", "{\"global_device_id\" : \"<redacted-mac>\"}")]
        // Not a MAC, or not a device id: unchanged.
        [InlineData("device_id=mcvsac271b2c3d4e5f60718293a4b5c6d7e8", "device_id=mcvsac271b2c3d4e5f60718293a4b5c6d7e8")]
        [InlineData("device_id=Unknown", "device_id=Unknown")]
        [InlineData("{\"device_id\":\"mcvs0123456789abcdef0123456789abcdef\"}", "{\"device_id\":\"mcvs0123456789abcdef0123456789abcdef\"}")]
        [InlineData("device_id=04:BF:1B:6C:B9:62:77", "device_id=04:BF:1B:6C:B9:62:77")]   // seven pairs
        [InlineData("device_id=04:BF-1B:6C:B9:62", "device_id=04:BF-1B:6C:B9:62")]         // mixed separators
        [InlineData("device_id=04:BF:1B:6C:B9:6", "device_id=04:BF:1B:6C:B9:6")]           // short
        [InlineData("mac=04:BF:1B:6C:B9:62", "mac=04:BF:1B:6C:B9:62")]                     // another name
        [InlineData("my_device_id=04:BF:1B:6C:B9:62", "my_device_id=04:BF:1B:6C:B9:62")]
        public void RedactsMacAddressesOfDeviceIds(string input, string expected)
        {
            Assert.Equal(expected, LogRedaction.Redact(input));
        }

        /// <summary>The PC client's anonymous user name: a SteamID64 keeps its first and last 4 digits.</summary>
        [Theory]
        [InlineData("form: username=76561198366698514 credential_type=anonymous", "form: username=7656…8514 credential_type=anonymous")]
        [InlineData("authorize anonymous:76561198366698514 -> account 30a", "authorize anonymous:7656…8514 -> account 30a")]
        [InlineData("{\"username\":\"76561198366698514\"}", "{\"username\":\"7656…8514\"}")]
        [InlineData("credential=anonymous%3A76561198366698514&x=1", "credential=anonymous%3A7656…8514&x=1")]
        [InlineData("76561198366698514", "7656…8514")]
        // Other numbers: unchanged.
        [InlineData("id 765611983666985141", "id 765611983666985141")]       // 18 digits
        [InlineData("id 7656119836669851", "id 7656119836669851")]           // 16 digits
        [InlineData("id 176561198366698514", "id 176561198366698514")]       // a longer run
        [InlineData("id 86561198366698514", "id 86561198366698514")]         // another prefix
        [InlineData("x76561198366698514", "x76561198366698514")]             // glued to a letter
        [InlineData("76561198366698514b", "76561198366698514b")]
        public void ShortensSteamIds(string input, string expected)
        {
            Assert.Equal(expected, LogRedaction.Redact(input));
        }

        [Theory]
        // A JSON text inside a JSON string (unknown-endpoints.jsonl stores bodies that way): the quotes arrive as \"
        [InlineData("{\"body\":\"{\\\"device_id\\\":\\\"04:BF:1B:6C:B9:62\\\"}\"}", "{\"body\":\"{\\\"device_id\\\":\\\"<redacted-mac>\\\"}\"}")]
        [InlineData("\"{\\\"global_device_id\\\" : \\\"04-bf-1b-6c-b9-62\\\"}\"", "\"{\\\"global_device_id\\\" : \\\"<redacted-mac>\\\"}\"")]
        [InlineData("\"{\\\"password\\\":\\\"hunter22\\\",\\\"x\\\":1}\"", "\"{\\\"password\\\":\\\"hunt…<redacted>\\\",\\\"x\\\":1}\"")]
        [InlineData("\"{\\\"ticket\\\":\\\"" + Ticket + "\\\"}\"", "\"{\\\"ticket\\\":\\\"<redacted>\\\"}\"")]
        // an escaped quote inside the escaped value (\\\" in the outer text) does not end the value
        [InlineData("\"{\\\"token\\\":\\\"a\\\\\\\"bcdef\\\"}\"", "\"{\\\"token\\\":\\\"a\\\\…<redacted>\\\"}\"")]
        [InlineData("\"{\\\"device_id\\\":\\\"mcvs0123\\\"}\"", "\"{\\\"device_id\\\":\\\"mcvs0123\\\"}\"")]
        public void RedactsJsonEscapedIntoAJsonString(string input, string expected)
        {
            Assert.Equal(expected, LogRedaction.Redact(input));
            Assert.Equal(expected, LogRedaction.Redact(expected));
        }

        [Fact]
        public void TheNewRulesAreIdempotentAndKeepTheFastPath()
        {
            string[] inputs =
            {
                "device_id=04:BF:1B:6C:B9:62 username=76561198366698514",
                "{\"device_id\":\"04:BF:1B:6C:B9:62\",\"user\":\"anonymous:76561198366698514\"}",
                "device_id=04%3ABF%3A1B%3A6C%3AB9%3A62",
            };
            foreach (string input in inputs)
            {
                string once = LogRedaction.Redact(input);
                Assert.Equal(once, LogRedaction.Redact(once));
                Assert.DoesNotContain("B9:62", once);
                Assert.DoesNotContain("B9%3A62", once);
                Assert.DoesNotContain("76561198366698514", once);
            }
            string plain = "#14 POST /online_connectivity/connectionstatus -> 200 [misc.connectivity] 2 B 0.3 ms";
            Assert.Same(plain, LogRedaction.Redact(plain));
        }

        /// <summary>The real game.log line of WR2 (Session.GetAccessToken, Debug, 808 times in one start).</summary>
        private const string RealTokenLine =
            "13:53:25.781 Debug [Online] {Session.cs}.GetAccessToken:384 | [anonymous:76561198366698514] Get access token for scopes "
            + "[auth, lobby, config] succeeded: mcvs59915ef0.e681f53f4c247197a50af715d8337ccd";

        [Fact]
        public void RedactsTheAccessTokenOfTheGamesSessionLog()
        {
            string once = LogRedaction.Redact(RealTokenLine);
            Assert.Equal("13:53:25.781 Debug [Online] {Session.cs}.GetAccessToken:384 | [anonymous:7656…8514] Get access token for scopes "
                         + "[auth, lobby, config] succeeded: mcvs…<redacted>", once);
            Assert.Equal(once, LogRedaction.Redact(once));
        }

        [Theory]
        // Anywhere in a line: after a word, glued to punctuation, inside a word, several per line.
        [InlineData("token mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d3 is valid", "token mcvs…<redacted> is valid")]
        [InlineData("[mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d3]", "[mcvs…<redacted>]")]
        [InlineData("Bearer_mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d3,x", "Bearer_mcvs…<redacted>,x")]
        [InlineData("a mcvs00000000.0123456789abcdef0123456789abcdef b mcvsffffffff.fedcba9876543210fedcba9876543210",
                    "a mcvs…<redacted> b mcvs…<redacted>")]
        // The parameter and JSON rules still answer first where a name stands before the token.
        [InlineData("access_token=mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d3&x=1", "access_token=mcvs…<redacted>&x=1")]
        [InlineData("{\"access_token\":\"mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d3\"}", "{\"access_token\":\"mcvs…<redacted>\"}")]
        // Not the token shape: unchanged.
        [InlineData("mcvs59915ef0", "mcvs59915ef0")]                                                                     // the issuer alone
        [InlineData("mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d", "mcvs59915ef0.98e76e5e47ce4d8e75abfc0b1d6c00d")]     // 31 hex
        [InlineData("mcvs59915ef.98e76e5e47ce4d8e75abfc0b1d6c00d3", "mcvs59915ef.98e76e5e47ce4d8e75abfc0b1d6c00d3")]     // 7 hex issuer
        [InlineData("mcvs59915ef01.98e76e5e47ce4d8e75abfc0b1d6c00d3", "mcvs59915ef01.98e76e5e47ce4d8e75abfc0b1d6c00d3")] // 9 hex issuer
        [InlineData("McvsOffline/logs/mcvs0123456789abcdef.json", "McvsOffline/logs/mcvs0123456789abcdef.json")]
        [InlineData("device mcvs0123456789abcdef0123456789abcdef", "device mcvs0123456789abcdef0123456789abcdef")]
        public void RedactsAccessTokensAnywhere(string input, string expected)
        {
            Assert.Equal(expected, LogRedaction.Redact(input));
            Assert.Equal(expected, LogRedaction.Redact(expected));
        }

        [Fact]
        public void PathsWithMcvsKeepTheFastPath()
        {
            string text = "#3 GET /McvsOffline/mcvs59915ef0/state -> 200 [x] 2 B";
            Assert.Same(text, LogRedaction.Redact(text));
        }

        [Fact]
        public void BrokenInputNeverThrows()
        {
            foreach (string text in new[] { "\"ticket\":\"", "mcvs", "mcvs59915ef0.", "mcvs59915ef0.98e7", "\"password\" :", "password=", "#t=", "%23t%3", "{\"ticket\":\"abc\\", "mcvs_s=",
                                             "device_id=", "device_id", "\"device_id\":\"04:BF", "7656119", "%3A7656119836669851" })
                Assert.NotNull(LogRedaction.Redact(text));
            Assert.Equal("{\"ticket\":\"<redacted>", LogRedaction.Redact("{\"ticket\":\"abc"));
        }
    }
}
