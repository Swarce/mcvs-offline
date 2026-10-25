// Module: Assets - owner group: CONTENT (Crm, Matchmaker, Assets + server/Content + tools/content-gen)
// Iris assets and coupons (requests carry no token).
// Protocol: report 02 §9; report 04 §10-11; report 10 §5; verify-C5 (b); docs/protocol/content.md
//   GET  coupons/{clientId}/{code}   -> [{"type":"UnlockMerc_ALL_MAX","quantity":1}, ...]; 404 = "coupon does not exist"
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

            router.Get("coupons/{clientId}/{code}", "assets.coupon", request => Coupon(context, log, request));
            router.Get("assets/{clientId}", "assets.list", request => HttpResponse.Json(assets.List()));
            router.Get("assets/{clientId}/{*name}", "assets.get", request => Asset(context, assets, log, request));
        }

        private static HttpResponse Coupon(ServerContext context, Log log, HttpRequest request)
        {
            string code = request.RouteValue("code") ?? "";
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
    }
}
