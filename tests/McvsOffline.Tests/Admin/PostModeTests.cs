using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// admin.openVia=post (design §5.3, §8.3): the game's WebView posts the ticket as a form (WebView.postUrl adds no
    /// header); the answer is a 303 to GET /_mcvs/ui/ with the session cookie.
    /// </summary>
    public class PostModeTests
    {
        /// <summary>What WebView.postUrl sends: a form, Origin "null", Sec-Fetch-Site "none", no X-MCVS.</summary>
        private static HttpResponse PostForm(AdminTestClient client, string ticket)
        {
            client.Origin = "null";
            client.SecFetchSite = "none";
            client.SendCsrfHeader = false;
            try
            {
                return client.Send("POST", "/_mcvs/ui", "t=" + ticket, "application/x-www-form-urlencoded");
            }
            finally
            {
                client.Origin = "http://127.0.0.1:51780";
                client.SecFetchSite = null;
                client.SendCsrfHeader = true;
            }
        }

        [Fact]
        public void TheFormPostOpensASessionAndRedirects()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            server.Context.Settings.Override("admin.openVia", "post");
            var client = new AdminTestClient(server);

            string ticket = client.MintTicket();
            var first = PostForm(client, ticket);
            Assert.Equal(303, first.StatusCode);
            Assert.Equal("See Other", first.ReasonPhrase);
            Assert.Equal("/_mcvs/ui/", first.Headers.Get("Location"));
            Assert.Matches("^mcvs_s=[0-9a-f]{64}; Path=/_mcvs/; HttpOnly; SameSite=Strict$", first.Headers.Get("Set-Cookie"));
            Assert.Equal(200, client.Get("status").StatusCode);
            string cookie = client.SessionCookie;

            // The activity is re-created and posts the used ticket again: the cookie is kept, no new session.
            var again = PostForm(client, ticket);
            Assert.Equal(303, again.StatusCode);
            Assert.Null(again.Headers.Get("Set-Cookie"));
            Assert.Equal(cookie, client.SessionCookie);

            // Without the cookie a used ticket gets a 303 without one (the page then asks to open it from the game).
            client.SessionCookie = null;
            var none = PostForm(client, ticket);
            Assert.Equal(303, none.StatusCode);
            Assert.Null(none.Headers.Get("Set-Cookie"));
            Assert.Equal(401, client.Get("status").StatusCode);
        }

        [Fact]
        public void OtherOpenModesRefuseTheForm()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            var client = new AdminTestClient(server);
            Assert.Equal(403, PostForm(client, client.MintTicket()).StatusCode);   // the gate: no exemption with openVia=auto

            server.Context.Settings.Override("admin.openVia", "post");
            client.SecFetchSite = "cross-site";
            client.Origin = "null";
            client.SendCsrfHeader = false;
            Assert.Equal(403, client.Send("POST", "/_mcvs/ui", "t=" + client.MintTicket(), "application/x-www-form-urlencoded").StatusCode);
            client.SecFetchSite = null;
            Assert.Equal(403, client.Send("POST", "/_mcvs/ui", "{\"t\":\"x\"}", "application/json").StatusCode);
            Assert.Equal(403, client.Send("POST", "/_mcvs/ui", "t=" + new string('a', 2048), "application/x-www-form-urlencoded").StatusCode);
        }
    }
}
