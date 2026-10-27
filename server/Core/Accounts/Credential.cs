using System;

namespace McvsOffline.Server.Core.Accounts
{
    /// <summary>
    /// A Federation credential "type:username" (report 02 §3.1): anonymous:&lt;id&gt; for players,
    /// game:&lt;name&gt; for the game-server super credential, fed_id:&lt;account&gt; to address an account directly,
    /// and the special value "me" (the token's subject). The client's parser requires exactly one ':' (two for
    /// gamecenter), so usernames here never contain one except for gamecenter.
    /// </summary>
    public struct Credential : IEquatable<Credential>
    {
        public const string Anonymous = "anonymous";
        public const string Game = "game";
        public const string FedId = "fed_id";
        public const string Me = "me";

        private readonly string type;
        private readonly string username;

        public Credential(string type, string username)
        {
            this.type = (type ?? "").Trim().ToLowerInvariant();
            this.username = username ?? "";
        }

        /// <summary>Lower-case credential type ("anonymous", "game", "fed_id", ...).</summary>
        public string Type { get { return type ?? ""; } }

        public string Username { get { return username ?? ""; } }

        public bool IsValid { get { return Type.Length > 0 && Type != "none" && Username.Length > 0; } }

        public bool IsMe { get { return Type == Me && Username.Length == 0; } }

        /// <summary>
        /// Parses "type:username" (splitting at the first ':'; the rest, gamecenter's "player:bundle", stays in
        /// the username), or "me". Returns false for anything else.
        /// </summary>
        public static bool TryParse(string text, out Credential credential)
        {
            credential = default(Credential);
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (string.Equals(t, Me, StringComparison.OrdinalIgnoreCase))
            {
                credential = new Credential(Me, "");
                return true;
            }
            int colon = t.IndexOf(':');
            if (colon <= 0 || colon == t.Length - 1) return false;
            credential = new Credential(t.Substring(0, colon), t.Substring(colon + 1));
            return true;
        }

        public static Credential Parse(string text)
        {
            Credential c;
            if (!TryParse(text, out c)) throw new FormatException("invalid credential '" + text + "'");
            return c;
        }

        /// <summary>Builds the credential from the authorize form fields (username + credential_type).</summary>
        public static Credential FromParts(string credentialType, string username)
        {
            return new Credential(credentialType, username);
        }

        public static Credential ForAccount(string accountId)
        {
            return new Credential(FedId, accountId);
        }

        /// <summary>
        /// A credential text for logs and pages: "anonymous:a1b2…f9e8", only the first and last 4 characters of the user
        /// name (the first 4 and "…" for names of 8 characters or fewer). A player's anonymous credential is what opens
        /// their account, so it is never logged or shown in full.
        /// </summary>
        public static string Mask(string credential)
        {
            if (string.IsNullOrEmpty(credential)) return credential;
            int colon = credential.IndexOf(':');
            string type = colon < 0 ? "" : credential.Substring(0, colon + 1);
            string name = colon < 0 ? credential : credential.Substring(colon + 1);
            if (name.Length <= 8) return type + (name.Length <= 4 ? name : name.Substring(0, 4)) + "…";
            return type + name.Substring(0, 4) + "…" + name.Substring(name.Length - 4);
        }

        /// <summary>"type:username" (or "me").</summary>
        public override string ToString()
        {
            return IsMe ? Me : Type + ":" + Username;
        }

        public bool Equals(Credential other)
        {
            return string.Equals(Type, other.Type, StringComparison.Ordinal)
                && string.Equals(Username, other.Username, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is Credential && Equals((Credential)obj);
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public static bool operator ==(Credential a, Credential b) { return a.Equals(b); }

        public static bool operator !=(Credential a, Credential b) { return !a.Equals(b); }
    }
}
