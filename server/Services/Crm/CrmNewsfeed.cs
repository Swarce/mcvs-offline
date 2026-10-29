using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Services.Admin;

namespace McvsOffline.Server.Services.Crm
{
    /// <summary>
    /// The CRM <c>newsfeeds</c> list: one inbox message "MCVS Offline" whose button opens the settings page (design
    /// docs/design/settings-page.md §9.1).
    /// <para>
    /// Client side: <c>StandardCrm.Data.DeserializeNewsFeed</c> reads <c>newsfeeds</c> when the key is present
    /// (OP/StandardCrm.cs:138-144) into <c>NewsFeedData</c> (OP/NewsFeedData.cs:17-30: <c>SafeGetString</c> of
    /// <c>news_id</c>, <c>title</c> and <c>body</c> (both trimmed), <c>redirect</c>, <c>asset</c>, <c>youtube</c>;
    /// <c>timestamp</c> as epoch seconds through <c>Convert.ToInt64</c>; <c>end_date</c> as ISO 8601). On the menu
    /// server <c>InboxComponent.OnCrmDataSet</c> (GM/InboxComponent.cs:164-236) first removes the news messages whose id
    /// is no longer listed, then creates <c>"newsfeed_" + news_id</c> for every item whose timestamp is less than 30
    /// days old (expiry: <c>end_date</c>, else now + 30 days), or refreshes the title, body, asset and
    /// <c>RedirectLink</c> of the existing message. <c>From</c> is the part of <c>news_id</c> (split on '_') that
    /// contains "From", minus "From" (GM/InboxMessage.cs:142-155): "MCVS". Flash <c>Inbox.gotoLink</c> opens a link
    /// starting with "http" through <c>Native.OpenUrl</c> → <c>BrowserComponent.OpenUrl</c>, whose prefix (patch
    /// <c>adminlink</c>, §9.3) opens the page with a fresh one-time ticket for the marker.
    /// </para>
    /// <para>
    /// The link is <see cref="IAdminPortal.PageMarkerUrl"/>: fixed per port, no secret (invariant I2: <c>GET
    /// configs/users/me</c> answers any caller without a token). The timestamp is Monday 00:00 UTC of the current week:
    /// it changes at most once a week and never reaches the client's 30-day cut-off. Without the item the list is
    /// empty, never absent: <c>OnCrmDataSet</c> returns before <c>RemoveExpiredNewsFeed</c> when <c>NewsFeed</c> is
    /// null, which would leave an existing message in the inbox.
    /// </para>
    /// </summary>
    public static class CrmNewsfeed
    {
        /// <summary>The inbox message id is "newsfeed_" + this; "FromMCVS" makes the sender "MCVS".</summary>
        public const string NewsId = "mcvsoffline_FromMCVS";

        public const string Title = "MCVS Offline";

        public const string Body = "Settings, saves and logs of your offline game. Tap the button to open them.";

        /// <summary>
        /// The <c>newsfeeds</c> list for the current state: our item when <c>crm.newsfeedLink</c> is on and the admin
        /// portal is registered and enabled (<c>admin.enabled</c>), else empty. Never null; never throws.
        /// </summary>
        public static List<object> Build(CrmOptions options, IAdminPortal portal, DateTime nowUtc)
        {
            var list = new List<object>();
            if (options == null || !options.NewsfeedLink || portal == null) return list;
            string marker;
            try
            {
                if (!portal.Enabled) return list;
                marker = portal.PageMarkerUrl;
            }
            catch (Exception)
            {
                return list; // the portal never throws; a broken one only costs the inbox item
            }
            if (string.IsNullOrEmpty(marker)) return list;
            list.Add(Item(marker, WeekStart(nowUtc)));
            return list;
        }

        /// <summary>Days from the item's timestamp (a week start) to its <c>end_date</c>: always 30 or more days ahead.</summary>
        public const int EndDateDays = 37;

        /// <summary>
        /// Our item, keys in the order of design §9.1, plus <c>end_date</c> = <paramref name="createdUtc"/> + 37 days
        /// (ISO 8601). The PC client keeps every newsfeed item in the static <c>NewsFeedData.Newsfeeds</c> (keyed by
        /// <c>asset</c>) and, when an inbox message's creation date differs from its item's (our timestamp moves every
        /// Monday), sets the message's expiry to the item's <c>EndDate</c> (PC-GM/InboxComponent.cs:465-491): without an
        /// <c>end_date</c> that is <c>default(DateTime)</c> and the message vanished at every week rollover
        /// (docs/design/pc-port.md §4.3). On Android the expiry becomes this date instead of now + 30 days.
        /// </summary>
        public static Dictionary<string, object> Item(string redirect, DateTime createdUtc)
        {
            var item = new Dictionary<string, object>(StringComparer.Ordinal);
            item["news_id"] = NewsId;
            item["title"] = Title;
            item["body"] = Body;
            item["redirect"] = redirect ?? "";
            item["asset"] = "";
            item["youtube"] = "";
            item["timestamp"] = TimeFormat.ToUnixSeconds(createdUtc);
            item["end_date"] = TimeFormat.Iso8601(createdUtc.AddDays(EndDateDays));
            return item;
        }

        /// <summary>Monday 00:00 UTC of the week that contains <paramref name="utc"/>.</summary>
        public static DateTime WeekStart(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            DateTime day = utc.Date;
            int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
            return DateTime.SpecifyKind(day.AddDays(-sinceMonday), DateTimeKind.Utc);
        }
    }
}
