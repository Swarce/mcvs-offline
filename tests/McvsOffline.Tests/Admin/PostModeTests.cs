using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Services.Admin;
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
        private static HttpResponse PostForm(AdminTestClient client, string ticket, string more = "")
        {
            client.Origin = "null";
            client.SecFetchSite = "none";
            client.SendCsrfHeader = false;
            try
            {
                return client.Send("POST", "/_mcvs/ui", "t=" + ticket + more, "application/x-www-form-urlencoded");
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

        /// <summary>
        /// The reopen after "Save the game now" (#t=..&amp;s=saves&amp;r=nosave): the host posts the screen and the result with
        /// the ticket, and the redirect hands them back as the fragment the page reads (§2.5), checked the same way.
        /// </summary>
        [Fact]
        public void TheScreenAndTheResultComeBackInTheRedirect()
        {
            using var dir = new TempDir();
            using var server = AdminTestClient.CreateServer(dir.Combine("data"));
            server.Context.Settings.Override("admin.openVia", "post");
            var client = new AdminTestClient(server);

            var first = PostForm(client, client.MintTicket(AdminTriggers.Resume, "saves"), "&s=saves&r=nosave");
            Assert.Equal(303, first.StatusCode);
            Assert.Equal("/_mcvs/ui/#s=saves&r=nosave", first.Headers.Get("Location"));
            Assert.NotNull(first.Headers.Get("Set-Cookie"));

            Assert.Equal("/_mcvs/ui/#s=logs", PostForm(client, client.MintTicket(), "&s=logs").Headers.Get("Location"));
            Assert.Equal("/_mcvs/ui/#r=nosave", PostForm(client, client.MintTicket(), "&r=nosave").Headers.Get("Location"));

            // Anything the page would not accept from a fragment is left out.
            Assert.Equal("/_mcvs/ui/", PostForm(client, client.MintTicket(), "&s=Saves&r=saved").Headers.Get("Location"));
            Assert.Equal("/_mcvs/ui/", PostForm(client, client.MintTicket(), "&s=..%2Fx&r=nosave%26t%3D1").Headers.Get("Location"));
            Assert.Equal("/_mcvs/ui/#r=nosave", PostForm(client, client.MintTicket(), "&s=javascript:x&r=nosave").Headers.Get("Location"));

            // A used ticket without a cookie: no session, the screen is still handed back (the page then asks to open it from the game).
            client.SessionCookie = null;
            string used = client.MintTicket();
            Assert.Equal(303, PostForm(client, used).StatusCode);
            client.SessionCookie = null;
            var none = PostForm(client, used, "&s=saves");
            Assert.Null(none.Headers.Get("Set-Cookie"));
            Assert.Equal("/_mcvs/ui/#s=saves", none.Headers.Get("Location"));
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
