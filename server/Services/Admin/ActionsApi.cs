using System;
using System.Collections.Generic;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;
using McvsOffline.Server.Services.Unlocks;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// <c>actions/save-now</c>, <c>actions/restart</c>, <c>actions/crm-reload</c>, <c>actions/unlock-now</c>,
    /// <c>actions/open-in-browser</c> (PC) and
    /// <c>clipboard</c> (design §4.5, §7.4).
    /// </summary>
    internal sealed class ActionsApi
    {
        /// <summary>One clipboard operation (≈ 256 KB as UTF-16 in the ClipData parcel, §7.4).</summary>
        public const int MaxClipboardChars = 128000;

        private static readonly string[] RestartReasons = { "settings", "import", "user" };

        private readonly AdminApi api;
        private readonly ExitSequence exit;

        public ActionsApi(AdminApi api, ExitSequence exit)
        {
            this.api = api;
            this.exit = exit;
        }

        public void Register()
        {
            api.Post("actions/save-now", "admin.actions.saveNow", SaveNow, 1024);
            api.Post("actions/restart", "admin.actions.restart", Restart, 1024);
            api.Post("actions/crm-reload", "admin.actions.crmReload", CrmReload, 1024);
            api.Post("actions/unlock-now", "admin.actions.unlockNow", UnlockNow, 1024);
            api.Post("clipboard", "admin.clipboard", Clipboard, 512 * 1024);
            api.Post("actions/open-in-browser", "admin.actions.openInBrowser", OpenInBrowser, 1024);
        }

        /// <summary>
        /// POST actions/open-in-browser (PC, docs/design/pc-port.md §6.5): the player asked to take the page to the system
        /// browser. A fresh page URL (a new one-time ticket, trigger "browser", the page's current screen) goes to
        /// <see cref="IAdminHost.OpenPage"/>; the host's opener sends the "browser" trigger to the system browser.
        /// Answers <c>{"ok": bool}</c>; 501 where the host cannot do it.
        /// </summary>
        private HttpResponse OpenInBrowser(AdminCall call)
        {
            AdminHostInfo info = api.DescribeHost();
            IAdminHost host = api.Host;
            if (info == null || host == null || !info.CanOpenInBrowser)
                return NotSupported("This build cannot open the page in the system browser.");
            var portal = api.Context.Features.Get<IAdminPortal>();
            if (portal == null) return NotSupported("The page cannot be opened from here.");
            string screen = JsonValue.GetString(call.Body, "screen");
            bool ok = false;
            string url = portal.MintPageUrl(AdminTriggers.Browser, AdminPortal.IsScreen(screen) ? screen : null);
            if (url != null)
            {
                try
                {
                    ok = host.OpenPage(url, AdminTriggers.Browser);
                }
                catch (Exception ex)
                {
                    api.Log.Warn("admin: opening the page in the system browser failed", ex);
                }
            }
            api.Log.Info("admin: page " + (ok ? "opening" : "could not be opened") + " in the system browser (trigger browser)");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = ok;
            return AdminJson.Ok(body);
        }

        private HttpResponse SaveNow(AdminCall call)
        {
            AdminHostInfo info = api.DescribeHost();
            if (info == null || !info.CanSave || !info.CanOpenPage)
                return NotSupported("The game cannot save and reopen this page from here.");
            string screen = JsonValue.GetString(call.Body, "screen");
            var request = new ExitRequest { Kind = ExitRequest.SaveNow, Screen = AdminPortal.IsScreen(screen) ? screen : null };
            string refusal;
            if (!exit.TryBegin(request, out refusal)) return Busy(refusal);
            api.Log.Info("admin: save now requested (screen " + (request.Screen ?? "status") + ")");
            exit.Start(request);
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["accepted"] = true;
            return AdminJson.Json(body, HttpStatus.Accepted);
        }

        private HttpResponse Restart(AdminCall call)
        {
            AdminHostInfo info = api.DescribeHost();
            if (info == null || !info.CanExit) return NotSupported("The game cannot be restarted from here: close it yourself.");
            if (!JsonValue.GetBool(call.Body, "confirm"))
                return AdminJson.Error(HttpStatus.BadRequest, "invalid", "Confirm the restart.");
            string reason = JsonValue.GetString(call.Body, "reason") ?? "user";
            if (Array.IndexOf(RestartReasons, reason) < 0)
                return AdminJson.Error(HttpStatus.BadRequest, "invalid", "Unknown restart reason.");
            var request = new ExitRequest
            {
                Kind = ExitRequest.Restart,
                Reason = reason,
                RestartWithoutSave = JsonValue.GetBool(call.Body, "restartWithoutSave"),
            };
            string refusal;
            if (!exit.TryBegin(request, out refusal)) return Busy("restarting");
            api.Log.Info("admin: restart requested (" + reason + (request.RestartWithoutSave ? ", even without a save" : "") + ")");
            exit.Start(request);
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["restarting"] = true;
            return AdminJson.Json(body, HttpStatus.Accepted);
        }

        private HttpResponse CrmReload(AdminCall call)
        {
            var cache = api.Context.Features.Get<CrmDocumentCache>();
            if (cache == null) return NotSupported("The CRM module is not running.");
            cache.Invalidate();
            api.Log.Info("admin: CRM document invalidated (rebuilt at the game's next CRM fetch)");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = true;
            return AdminJson.Ok(body);
        }

        private HttpResponse UnlockNow(AdminCall call)
        {
            var granter = api.Context.Features.Get<UnlockGranter>();
            if (granter == null) return NotSupported("The unlock module is not running.");
            if (!granter.Enabled)
                return AdminJson.Error(HttpStatus.Conflict, "invalid", "Turn on \"Unlock everything\" first.");
            CurrentPlayer player = api.CurrentPlayer();
            if (player == null || player.AccountId == null)
                return AdminJson.Error(HttpStatus.Conflict, "current_player_unknown", "The current player is not known.");
            bool queued = granter.Enqueue(player.AccountId);
            api.Log.Info("admin: unlock-all " + (queued ? "queued" : "already done or queued") + " for " + player.AccountId);
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["queued"] = queued;
            return AdminJson.Ok(body);
        }

        private HttpResponse Clipboard(AdminCall call)
        {
            string text = JsonValue.GetString(call.Body, "text");
            if (text == null) return AdminJson.Error(HttpStatus.BadRequest, "bad_request", "\"text\" is missing.");
            if (text.Length > MaxClipboardChars) return AdminJson.TooLarge(MaxClipboardChars);
            string label = JsonValue.GetString(call.Body, "label") ?? "MCVS Offline";
            if (label.Length > 100) label = label.Substring(0, 100);
            AdminHostInfo info = api.DescribeHost();
            IAdminHost host = api.Host;
            if (info == null || host == null || !info.CanCopy) return NotSupported("The game cannot copy text here.");
            bool copied;
            try
            {
                copied = host.CopyToClipboard(label, text);
            }
            catch (Exception ex)
            {
                api.Log.Warn("admin: CopyToClipboard failed", ex);
                copied = false;
            }
            if (!copied) return NotSupported("The game could not copy the text.");
            var body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["ok"] = true;
            body["chars"] = (long)text.Length;
            return AdminJson.Ok(body);
        }

        private static HttpResponse NotSupported(string message)
        {
            return AdminJson.Error(HttpStatus.NotImplemented, "not_supported", message);
        }

        private static HttpResponse Busy(string code)
        {
            return AdminJson.Error(HttpStatus.Conflict, code == "restarting" ? "restarting" : "busy",
                                   code == "restarting" ? "The game is restarting." : "The game is saving. Wait a moment.");
        }
    }
}
