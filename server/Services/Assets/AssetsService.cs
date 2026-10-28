// Module: Assets - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// Iris assets and coupons (requests carry no token).
// Protocol: report 02 §9; report 04 §10-11; report 10 §5; verify-C5 (b); docs/protocol/content.md
//   GET  coupons/{clientId}/{code}   -> [{"type":"UnlockMerc_2","quantity":1}, ...]; 404 = "coupon does not exist"
//                                       admin.redeemCode (MCVSOFFLINE) opens the settings page, always 404 (design
//                                       docs/design/settings-page.md §9.5)
//   GET  assets/{clientId}           -> [{name, hash, size}] of <data>/assets (usually [])
//   GET  assets/{clientId}/{name}[/metadata[/{attr}]|/url]   local file (Range supported) or 404

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Server.Services.Auth;

namespace McvsOffline.Server.Services.Assets
{
    /// <summary>Iris assets and coupons (requests carry no token).</summary>
    public static class AssetsService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            var log = context.Logger.For("assets");
            var assets = new LocalAssets(context.GetDataPath("assets"));
            var pageCode = new PageCode(context, log);

            router.Get("coupons/{clientId}/{code}", "assets.coupon", request => Coupon(context, pageCode, log, request));
            router.Get("assets/{clientId}", "assets.list", request => HttpResponse.Json(assets.List()));
            router.Get("assets/{clientId}/{*name}", "assets.get", request => Asset(context, assets, log, request));
        }

        private static HttpResponse Coupon(ServerContext context, PageCode pageCode, Log log, HttpRequest request)
        {
            string code = request.RouteValue("code") ?? "";
            if (pageCode.Matches(code)) return pageCode.Redeem(code, request);
            if (!context.Settings.GetBool("assets.couponsEnabled", true))
            {
                log.Info("coupon '" + code + "' refused: coupons are disabled (assets.couponsEnabled=false)");
                return HttpResponse.Error(HttpStatus.NotFound, "coupon does not exist");
            }
            var catalog = CouponCatalog.Load(context.Content, context.Settings, log);
            string matched;
            List<object> rewards = catalog.Resolve(code, out matched);
            if (rewards == null)
            {
                // 404 is the only status the game maps to "CouponDoesNotExist" (EN/AssetsComponent.cs RedeemCoupon).
                log.Info("coupon '" + code + "' does not exist (known: " + string.Join(", ", catalog.Codes.ToArray()) + ")");
                return HttpResponse.Error(HttpStatus.NotFound, "coupon does not exist");
            }
            // The game marks the code as claimed for this profile before it parses the body, and puts every reward into
            // one inbox message (7-day expiry), claimed in array order (verify-C5 (b)).
            log.Info("coupon '" + code + "' (" + matched + "): " + rewards.Count + " reward(s), first "
                     + (rewards.Count > 0 ? JsonValue.GetString((Dictionary<string, object>)rewards[0], "type") : "-"));
            return HttpResponse.Json(rewards);
        }

        private static HttpResponse Asset(ServerContext context, LocalAssets assets, Log log, HttpRequest request)
        {
            string rest = (request.RouteValue("name") ?? "").Trim('/');
            string name = rest;
            string attribute = null;
            bool wantsUrl = false;
            bool wantsMetadata = false;
            int meta = rest.IndexOf("/metadata", StringComparison.Ordinal);
            if (meta >= 0 && (meta + 9 == rest.Length || rest[meta + 9] == '/'))
            {
                wantsMetadata = true;
                name = rest.Substring(0, meta);
                attribute = meta + 10 < rest.Length ? rest.Substring(meta + 10) : null;
            }
            else if (rest.EndsWith("/url", StringComparison.Ordinal))
            {
                wantsUrl = true;
                name = rest.Substring(0, rest.Length - 4);
            }

            string path = assets.Locate(name);
            if (path == null)
            {
                // Expected: the game falls back to its own data or a default image for every Iris asset (report 04 §11).
                log.Info("asset '" + name + "' not available locally (404); put it in " + assets.Directory + " to serve it");
                return HttpResponse.Error(HttpStatus.NotFound, "asset not found");
            }

            if (wantsUrl)
            {
                string clientId = request.RouteValues.GetRaw("clientId") ?? "";
                return HttpResponse.Text(context.BaseUrlFor(request) + "assets/" + clientId + "/" + name);
            }
            if (wantsMetadata)
            {
                var metadata = LocalAssets.Metadata(name, path);
                if (string.IsNullOrEmpty(attribute)) return HttpResponse.Json(metadata);
                object value;
                if (!metadata.TryGetValue(attribute, out value)) return HttpResponse.Error(HttpStatus.NotFound, "no metadata '" + attribute + "'");
                var single = new Dictionary<string, object>(StringComparer.Ordinal);
                single[attribute] = value;
                return HttpResponse.Json(single);
            }

            byte[] data = File.ReadAllBytes(path);
            string contentType = LocalAssets.ContentTypeFor(name);
            ByteRange range;
            bool satisfiable;
            if (ByteRange.TryParse(request.Headers.Get("Range"), data.Length, out range, out satisfiable))
            {
                if (!satisfiable)
                {
                    return HttpResponse.Empty(416).WithHeader("Content-Range", "bytes */" + data.Length.ToString(CultureInfo.InvariantCulture));
                }
                var part = new byte[range.End - range.Start + 1];
                Buffer.BlockCopy(data, (int)range.Start, part, 0, part.Length);
                return HttpResponse.Bytes(part, contentType, 206)
                    .WithHeader("Content-Range", "bytes " + range.Start.ToString(CultureInfo.InvariantCulture) + "-"
                                + range.End.ToString(CultureInfo.InvariantCulture) + "/" + data.Length.ToString(CultureInfo.InvariantCulture))
                    .WithHeader("Accept-Ranges", "bytes");
            }
            return HttpResponse.Bytes(data, contentType).WithHeader("Accept-Ranges", "bytes");
        }

        /// <summary>
        /// The hidden redeem code that opens the settings page (design docs/design/settings-page.md §9.5):
        /// <c>admin.redeemCode</c> (default MCVSOFFLINE, empty: off), checked before <c>assets.couponsEnabled</c>. The
        /// answer is always 404 "coupon does not exist": after a 200 the game would mark the code claimed and put an empty
        /// message into the inbox (EN/AssetsComponent.cs RedeemCoupon, GM/PlayerProfileComponent.cs PushCouponDataToInbox);
        /// after a 404 it shows "code does not exist" (behind the browser) and claims nothing.
        /// <para>
        /// The page opens only when every check holds: the admin portal is there and enabled; LAN mode is off (a guest's
        /// redemption runs on this phone's menu server, RemoteSource.Session, so it arrives over loopback with the game's
        /// User-Agent and no token, exactly like the player's own, and would open the page on this screen); the request
        /// comes from this phone (loopback); it is the game's request and not a web page's (no browser header and the
        /// game's exact User-Agent for a client id of the game's form, <see cref="GameRequest.NotTheGame"/>); an access
        /// token, when the request carries one, belongs to the current player; a host can open the in-game browser and it is not open already; and the last opening by this code is
        /// more than 30 s ago. The game's own request carries no access token: redemption runs on the menu server
        /// (EN/AssetsComponent.cs:170-195, RedeemCouponRemote → RedeemCoupon), and
        /// <c>AssetsApi.GetRedeemCouponRequest</c> builds a FedRequestMessage without scopes, to which
        /// <c>FedRequestMessage.PrepareToSend</c> adds no <c>access_token</c> (OP/AssetsApi.cs:39-43,
        /// OP/FedRequestMessage.cs:96-99,163-171). A local app could fire the code too, at most every 30 s and only while
        /// no browser is open: the worst case is our page in the game's browser, whose ticket only the WebView receives
        /// (§5.5). A web page cannot (§5.1).
        /// </para>
        /// The request arrives on a connection thread; <see cref="IAdminPortal.OpenPage"/> posts to the game thread.
        /// </summary>
        private sealed class PageCode
        {
            public const string DefaultCode = "MCVSOFFLINE";
            public const int MinIntervalSeconds = 30;

            private readonly ServerContext context;
            private readonly Log log;
            private readonly object gate = new object();
            private DateTime? lastOpened;

            public PageCode(ServerContext context, Log log)
            {
                this.context = context;
                this.log = log;
            }

            /// <summary>True when <paramref name="code"/> is the page code (read on every request: a <c>live</c> setting).</summary>
            public bool Matches(string code)
            {
                string pageCode = CouponCatalog.Normalize(context.Settings.GetString("admin.redeemCode", DefaultCode));
                return pageCode.Length > 0 && string.Equals(CouponCatalog.Normalize(code), pageCode, StringComparison.Ordinal);
            }

            public HttpResponse Redeem(string code, HttpRequest request)
            {
                string refused;
                try
                {
                    refused = Open(request);
                }
                catch (Exception ex)
                {
                    log.Error("coupon '" + code + "': opening the MCVS Offline page failed", ex);
                    refused = "error";
                }
                log.Info("coupon '" + code + "': opens the MCVS Offline page (" + (refused == null ? "requested" : "ignored: " + refused) + ")");
                return HttpResponse.Error(HttpStatus.NotFound, "coupon does not exist");
            }

            /// <summary>Asks the portal to open the page; null when it did, else why not.</summary>
            private string Open(HttpRequest request)
            {
                IAdminPortal portal = context.Features.Get<IAdminPortal>();
                if (portal == null) return "the settings page is not part of this server";
                if (!portal.Enabled) return "the settings page is turned off (admin.enabled)";
                if (context.Settings.LanMode) return "LAN mode is on (a guest's code would open it on this phone)";
                if (!LocalAccessGate.IsLoopback(request.RemoteEndPoint)) return "not from this phone";
                string notTheGame = GameRequest.NotTheGame(request, request.RouteValue("clientId"));
                if (notTheGame != null) return notTheGame;
                string tokenProblem = CheckToken(request);
                if (tokenProblem != null) return tokenProblem;
                IAdminHost host = context.Features.Get<IAdminHost>();
                if (host == null) return "no in-game browser here";
                AdminHostInfo info = host.Describe();
                if (info != null && info.BrowserOpen) return "the in-game browser is already open";

                DateTime now = context.Clock.UtcNow;
                lock (gate)
                {
                    // A clock that went back counts as elapsed: the code must not stay blocked until it catches up.
                    TimeSpan since = lastOpened.HasValue ? now - lastOpened.Value : TimeSpan.MaxValue;
                    if (since >= TimeSpan.Zero && since < TimeSpan.FromSeconds(MinIntervalSeconds))
                        return "opened less than " + MinIntervalSeconds + " s ago";
                    lastOpened = now;
                }
                return portal.OpenPage(AdminTriggers.Redeem, null) ? null : "the in-game browser could not be opened";
            }

            /// <summary>Null when the request carries no access token or one of the current player's; else why not.</summary>
            private string CheckToken(HttpRequest request)
            {
                IAuthenticator auth = context.Features.Get<IAuthenticator>();
                string token = auth != null ? auth.GetAccessToken(request) : request.Param("access_token");
                if (string.IsNullOrEmpty(token)) return null; // the game's request (see above)
                AuthIdentity who = auth == null ? null : auth.Resolve(token);
                if (who == null) return "unknown access token";
                AdminApi api = context.Features.Get<AdminApi>();
                CurrentPlayer current = api == null ? null : api.CurrentPlayer();
                if (current == null || string.IsNullOrEmpty(current.AccountId)) return "the current player is unknown";
                if (!string.Equals(current.AccountId, who.AccountId, StringComparison.Ordinal)) return "the access token is another account's";
                return null;
            }
        }
    }
}
