using System;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Accounts;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Server.Services.Auth
{
    /// <summary>
    /// Per-account aliases ("friend codes", games/mygame/alias). The game names a new player "agent-" + alias
    /// (GM/PlayerProfileComponent.cs InitPlayerName) and the call is not caught, so it must always succeed
    /// (verify-C2 §1.6).
    /// <para>
    /// An alias is 9 decimal digits derived from the account id, so it is stable across restarts and the same on
    /// every server that derives the same account id (LAN hosts, reinstalls). It is stored in
    /// <c>Account.Data["alias"]</c> on first use; a collision with another account's alias moves on to the next
    /// derivation.
    /// </para>
    /// </summary>
    public sealed class AliasRegistry
    {
        public const string DataKey = "alias";
        private const int Digits = 9;
        private const long Range = 900000000L; // 100000000..999999999: no leading zero
        private const long Offset = 100000000L;
        private const int MaxAttempts = 1000;

        private readonly object gate = new object();
        private readonly AccountStore accounts;
        private readonly Log log;

        public AliasRegistry(AccountStore accounts, Log log)
        {
            if (accounts == null) throw new ArgumentNullException("accounts");
            this.accounts = accounts;
            this.log = log ?? Log.Null;
        }

        /// <summary>Candidate number <paramref name="attempt"/> for an account: 9 digits from SHA-1.</summary>
        public static string Derive(string accountId, int attempt)
        {
            string seed = "mcvs-offline/alias/v1|" + accountId + (attempt == 0 ? "" : "#" + attempt.ToString(CultureInfo.InvariantCulture));
            string hex = Ids.Sha1Hex(seed);
            long value = 0;
            // Reduce the hex digits modulo Range step by step (no 64-bit overflow, no culture-dependent parsing).
            foreach (char c in hex)
            {
                int digit = c <= '9' ? c - '0' : c - 'a' + 10;
                value = (value * 16 + digit) % Range;
            }
            return (value + Offset).ToString(CultureInfo.InvariantCulture);
        }

        public static bool IsWellFormed(string alias)
        {
            if (alias == null || alias.Length != Digits || alias[0] == '0') return false;
            foreach (char c in alias)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /// <summary>The account's alias, created (and persisted) on first use. Null if the account does not exist.</summary>
        public string GetOrCreate(string accountId)
        {
            lock (gate)
            {
                Account account = accounts.FindById(accountId);
                if (account == null) return null;
                string existing = JsonValue.AsString(JsonValue.Get(account.Data, DataKey));
                if (IsWellFormed(existing)) return existing;

                for (int attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    string candidate = Derive(accountId, attempt);
                    Account owner = FindOwner(candidate);
                    if (owner != null && owner.Id != accountId) continue;
                    accounts.Update(accountId, a => a.Data[DataKey] = candidate);
                    log.Info("alias " + candidate + " assigned to account " + accountId);
                    return candidate;
                }
                throw new InvalidOperationException("no free alias for account " + accountId);
            }
        }

        /// <summary>The account that owns <paramref name="alias"/>, or null.</summary>
        public Account FindOwner(string alias)
        {
            if (!IsWellFormed(alias)) return null;
            foreach (Account account in accounts.All())
            {
                if (string.Equals(JsonValue.AsString(JsonValue.Get(account.Data, DataKey)), alias, StringComparison.Ordinal))
                    return account;
            }
            return null;
        }
    }
}
