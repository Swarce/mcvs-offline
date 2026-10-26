using System;
using System.Collections.Generic;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Services.Storage;

namespace McvsOffline.Server.Services.Admin
{
    /// <summary>
    /// Who "the current player" is (design §6.6), first match wins:
    /// <list type="number">
    /// <item><c>game</c>: the game's own credential as the host read it on the game thread (cannot be spoofed by a
    /// request) → <c>Accounts.Find</c>;</item>
    /// <item><c>session</c>: the last direct anonymous grant (<see cref="TokenService.LastDirectAnonymous"/>), only from
    /// the game's client id form (":android:") and, when both are known, the host's device id; any local app can call
    /// auth/authorize without a password, so nothing else is trusted;</item>
    /// <item><c>device</c>: exactly one account recorded for the host's device id;</item>
    /// <item><c>single</c>: exactly one stored profile holds a game profile;</item>
    /// <item>none (<c>Source</c> null).</item>
    /// </list>
    /// The credential is known only for "game" and "session".
    /// </summary>
    internal sealed class CurrentPlayerResolver
    {
        private readonly ServerContext context;

        public CurrentPlayerResolver(ServerContext context)
        {
            this.context = context;
        }

        public CurrentPlayer Resolve(IAdminHost host)
        {
            AdminHostInfo info = null;
            if (host != null)
            {
                try
                {
                    info = host.Describe();
                }
                catch (Exception)
                {
                    info = null;
                }
            }
            string hostDevice = info == null ? null : info.DeviceId;

            // 1. The game's own credential.
            string gameCredential = info == null ? null : info.GameCredential;
            if (!string.IsNullOrEmpty(gameCredential))
            {
                Account account = context.Accounts.Find(gameCredential);
                if (account != null) return new CurrentPlayer { AccountId = account.Id, Credential = gameCredential, Source = "game" };
            }

            // 2. The player's own last login, from the game's client and this device.
            TokenInfo last = context.Tokens.LastDirectAnonymous;
            if (last != null && last.Credential != null && last.Credential.StartsWith("anonymous:", StringComparison.Ordinal)
                && last.ClientId != null && last.ClientId.IndexOf(":android:", StringComparison.Ordinal) >= 0
                && (string.IsNullOrEmpty(hostDevice) || string.IsNullOrEmpty(last.DeviceId)
                    || string.Equals(hostDevice, last.DeviceId, StringComparison.Ordinal)))
            {
                Account account = context.Accounts.Find(last.Credential); // follows relinks
                if (account != null) return new CurrentPlayer { AccountId = account.Id, Credential = last.Credential, Source = "session" };
            }

            // 3. The only account of this device.
            if (!string.IsNullOrEmpty(hostDevice))
            {
                List<Account> accounts = context.Accounts.FindByDevice(hostDevice);
                if (accounts.Count == 1) return new CurrentPlayer { AccountId = accounts[0].Id, Source = "device" };
            }

            // 4. The only stored profile with game progress.
            string single = SingleGameProfile();
            if (single != null) return new CurrentPlayer { AccountId = single, Source = "single" };

            return new CurrentPlayer();
        }

        private string SingleGameProfile()
        {
            var store = context.Features.Get<LocalProfileStore>();
            var access = context.Features.Get<IProfileAccess>();
            if (store == null || access == null) return null;
            string found = null;
            foreach (string account in store.AccountIds())
            {
                bool hasGameProfile;
                try
                {
                    hasGameProfile = access.HasGameProfile(account);
                }
                catch (Exception)
                {
                    continue; // an unreadable profile is not "the" profile
                }
                if (!hasGameProfile) continue;
                if (found != null) return null;
                found = account;
            }
            return found;
        }
    }
}
