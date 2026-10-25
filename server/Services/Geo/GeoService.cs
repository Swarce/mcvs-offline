// Module: Geo - owner group: MISC
// IP geolocation: the hard-coded http://ip.ecomapis.gameloft.org/location/?ip=<ip> (UT/Geolocalization.cs:93-105),
// reached when the patcher's 'geo' IL patch points it at this server.
// Protocol: ARCHITECTURE §2.3 'geo'; verify-C2 §1.5 and §3 last row; docs/protocol/misc.md
//   GET location/?ip=<ip>    -> {"ip":..,"country_info":{"iso2":"US","iso3":"USA","name":"United States"}}
// Conventions and core APIs: docs/MODULES.md.

using System;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services.Misc;

namespace McvsOffline.Server.Services.Geo
{
    /// <summary>
    /// Geolocation. The TS handshake calls <c>Geolocalization.GetRegion(ip)</c> and reads
    /// <c>["country_info"]["iso2"]</c> as the player's region (<c>GM/GameServerComponent.cs:994-1023</c>); it waits for
    /// the answer inside the 60 s handshake, with HttpClient's 100 s default timeout, so this answers at once.
    /// The country is settings <c>geo.country</c> (default "US"; ISO 3166-1 alpha-2).
    /// The developer edge list (<c>dump/dump.edgelist</c>) is read from the data pack, never from the network
    /// (<c>fromLocalFile = true</c>); it is answered empty in case a build asks.
    /// </summary>
    public static class GeoService
    {
        /// <summary>Registers this module's routes. Called once at startup by <see cref="ServiceRegistry"/>.</summary>
        public static void Register(Router router, ServerContext context)
        {
            Log log = context.Logger.For("geo");
            router.Get("location", "geo.location", request => Location(context, request, request.Param("ip")));
            router.Get("location/{ip}", "geo.location.path", request => Location(context, request, request.RouteValue("ip")));
            router.Get("dump/dump.edgelist", "geo.edgelist", request => HttpResponse.Text(""));
        }

        private static HttpResponse Location(ServerContext context, HttpRequest request, string ip)
        {
            string iso2 = (context.Settings.GetString("geo.country", "US") ?? "US").Trim().ToUpperInvariant();
            if (iso2.Length != 2) iso2 = "US";
            var country = Fed.Obj();
            country["iso2"] = iso2;
            string iso3, name;
            Describe(iso2, out iso3, out name);
            if (iso3 != null) country["iso3"] = iso3;
            if (name != null) country["name"] = name;

            var body = Fed.Obj();
            body["ip"] = string.IsNullOrEmpty(ip) ? (request.RemoteEndPoint == null ? "" : request.RemoteEndPoint.Address.ToString()) : ip;
            body["country_info"] = country;
            return HttpResponse.Json(body);
        }

        private static void Describe(string iso2, out string iso3, out string name)
        {
            // A few common answers for nicer logs; only iso2 is read by the game.
            switch (iso2)
            {
                case "US": iso3 = "USA"; name = "United States"; return;
                case "GB": iso3 = "GBR"; name = "United Kingdom"; return;
                case "FR": iso3 = "FRA"; name = "France"; return;
                case "DE": iso3 = "DEU"; name = "Germany"; return;
                case "ES": iso3 = "ESP"; name = "Spain"; return;
                case "IT": iso3 = "ITA"; name = "Italy"; return;
                case "BR": iso3 = "BRA"; name = "Brazil"; return;
                case "RU": iso3 = "RUS"; name = "Russia"; return;
                case "CN": iso3 = "CHN"; name = "China"; return;
                case "JP": iso3 = "JPN"; name = "Japan"; return;
                case "KR": iso3 = "KOR"; name = "South Korea"; return;
                case "CA": iso3 = "CAN"; name = "Canada"; return;
                case "MX": iso3 = "MEX"; name = "Mexico"; return;
                default: iso3 = null; name = null; return;
            }
        }
    }
}
