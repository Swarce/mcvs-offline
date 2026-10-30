using System;
using System.Collections.Generic;
using System.Globalization;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Services.Crm;

namespace McvsOffline.Server.Services.Assets
{
    /// <summary>
    /// Coupon codes answered by <c>coupons/{cid}/{code}</c>. Built-in codes come from coupons.json (generated), whose
    /// "groups" refer to the ordered reward-id groups of unlocks.json; settings.json <c>assets.coupons</c> adds or
    /// replaces codes:
    /// <code>
    /// "assets": { "couponsEnabled": true,
    ///             "coupons": { "GIFT500": { "rewards": [ {"type": "HardCurrency", "quantity": 500} ] } } }
    /// </code>
    /// A definition may combine <c>"groups"</c> (expanded first, in order) and <c>"rewards"</c>; with
    /// <c>"allowNumericSuffix": true</c> the code also matches when followed by digits (UNLOCKALL2), because the game
    /// accepts each code only once per profile (it stores claimed codes in the profile, verify-C5 (b)).
    /// Codes are matched case-insensitively.
    /// <para>
    /// <c>"delivery"</c> says how the rewards reach the player (<see cref="DeliveryOf"/>): <c>"ledger"</c> writes them
    /// into the inventory ledger (<c>UnlockGranter.GrantKeys</c>, the path of settings.unlockAll) and the game gets an
    /// empty payload; <c>"inbox"</c> sends them to the game, which puts all of them into ONE inbox message. Without the
    /// key, every UNLOCK* code is "ledger" and every other code "inbox". The game rebuilds every reward of every
    /// unclaimed message on its game thread each time the inbox opens, and again every 10 s while it stays open, so an
    /// inbox code of more than <see cref="MaxInboxRewards"/> rewards froze it for minutes (UNLOCKCAMOS: 4,450; inbox-freeze
    /// report §2 C1, C2): such a code is refused.
    /// </para>
    /// </summary>
    public sealed class CouponCatalog
    {
        public const string CouponsFile = "coupons.json";
        public const string UnlocksFile = "unlocks.json";

        /// <summary>Upper bound on a payload; the full unlock is ~11k rewards.</summary>
        public const int MaxRewards = 50000;

        /// <summary>
        /// The most rewards an inbox code may hold. The small built-in codes held 6-150 rewards and opened the inbox in
        /// milliseconds; the ones that froze it held 1,549 and more (inbox-freeze report §2, "Predicted behaviour per code").
        /// </summary>
        public const int MaxInboxRewards = 300;

        public const string LedgerDelivery = "ledger";
        public const string InboxDelivery = "inbox";

        /// <summary>The start of every unlock code's name; such a code is "ledger" unless its definition says otherwise.</summary>
        public const string UnlockPrefix = "UNLOCK";

        private readonly Dictionary<string, Dictionary<string, object>> codes =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly Log log;

        public CouponCatalog(Dictionary<string, object> couponsDoc, Dictionary<string, object> unlocksDoc,
                             Dictionary<string, object> extraCodes, Log log)
        {
            this.log = log ?? Log.Null;
            foreach (object g in JsonValue.GetArray(unlocksDoc, "groups") ?? new List<object>())
            {
                var group = JsonValue.AsObject(g);
                string id = JsonValue.GetString(group, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var items = new List<string>();
                foreach (object p in JsonValue.GetArray(group, "items") ?? new List<object>())
                {
                    string pattern = JsonValue.AsString(p);
                    if (pattern == null) continue;
                    try
                    {
                        items.AddRange(RewardPattern.Expand(pattern));
                    }
                    catch (FormatException ex)
                    {
                        this.log.Error(UnlocksFile + ": group " + id + ": " + ex.Message);
                    }
                }
                groups[id] = items;
            }
            AddCodes(JsonValue.GetObject(couponsDoc, "codes"));
            AddCodes(extraCodes);
        }

        public static CouponCatalog Load(IContentProvider content, Settings settings, Log log)
        {
            return new CouponCatalog(ReadObject(content, CouponsFile, log), ReadObject(content, UnlocksFile, log),
                                     settings == null ? null : settings.GetObject("assets.coupons"), log);
        }

        /// <summary>The known codes (normalised).</summary>
        public List<string> Codes { get { return new List<string>(codes.Keys); } }

        /// <summary>
        /// Every code with its description, numeric-suffix flag, delivery and reward count (coupons.json, then the codes settings.json
        /// <c>assets.coupons</c> adds; a code it redefines keeps its place): the settings page's code list.
        /// </summary>
        public List<CouponDescription> Describe()
        {
            var list = new List<CouponDescription>();
            foreach (var kv in codes)
            {
                list.Add(new CouponDescription
                {
                    Code = kv.Key,
                    Description = JsonValue.GetString(kv.Value, "description") ?? "",
                    NumericSuffix = JsonValue.GetBool(kv.Value, "allowNumericSuffix", false),
                    Delivery = DeliveryOf(kv.Key, kv.Value),
                    Rewards = Expand(kv.Value, kv.Key, Log.Null).Count,
                });
            }
            return list;
        }

        /// <summary>Reward ids of an unlocks.json group, in order (empty when unknown).</summary>
        public List<string> Group(string id)
        {
            List<string> items;
            return groups.TryGetValue(id ?? "", out items) ? new List<string>(items) : new List<string>();
        }

        /// <summary>
        /// The rewards of <paramref name="code"/>: an array of <c>{type, quantity}</c> as the game reads it (one inbox
        /// message, claimed in array order, GM/PlayerProfileComponent.cs PushCouponDataToInbox). Null when unknown. This
        /// is the code's content whatever its delivery: AssetsService sends it only for an inbox code (<see cref="Match"/>).
        /// </summary>
        public List<object> Resolve(string code, out string matchedCode)
        {
            CouponMatch match = Match(code);
            matchedCode = match == null ? Normalize(code) : match.Code;
            return match == null ? null : match.Rewards;
        }

        /// <summary>
        /// The code's rewards and delivery, or null when unknown. For a ledger code <see cref="CouponMatch.Keys"/> holds
        /// the reward ids in order, once each; a ledger reward with a quantity other than 1 is left out with a warning
        /// (the ledger counts unlocks, it does not carry amounts).
        /// </summary>
        public CouponMatch Match(string code)
        {
            string matched, definition;
            var def = Find(code, out matched, out definition);
            if (def == null) return null;
            var match = new CouponMatch
            {
                Code = matched,
                Definition = definition,
                Delivery = DeliveryOf(definition, def),
                Rewards = Expand(def, matched, log),
            };
            if (match.Delivery == LedgerDelivery)
            {
                var keys = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (object r in match.Rewards)
                {
                    var reward = (Dictionary<string, object>)r;
                    string type = JsonValue.GetString(reward, "type");
                    if (JsonValue.GetLong(reward, "quantity", 1) != 1)
                    {
                        log.Warn("coupon " + matched + ": reward '" + type + "' skipped (a ledger code grants unlocks: quantity must be 1)");
                        continue;
                    }
                    if (seen.Add(type)) keys.Add(type);
                }
                match.Keys = keys;
            }
            return match;
        }

        /// <summary>
        /// "ledger" or "inbox" for a definition: its <c>"delivery"</c> when that is one of the two, else "ledger" for a
        /// code that starts with UNLOCK and "inbox" for any other.
        /// </summary>
        public string DeliveryOf(string definitionCode, Dictionary<string, object> def)
        {
            string delivery = (JsonValue.GetString(def, "delivery") ?? "").Trim().ToLowerInvariant();
            if (delivery == LedgerDelivery || delivery == InboxDelivery) return delivery;
            if (delivery.Length > 0)
                log.Warn("coupon " + definitionCode + ": unknown delivery '" + delivery + "' (use \"ledger\" or \"inbox\"); using the default");
            return Normalize(definitionCode).StartsWith(UnlockPrefix, StringComparison.Ordinal) ? LedgerDelivery : InboxDelivery;
        }

        private List<object> Expand(Dictionary<string, object> def, string matchedCode, Log warnings)
        {
            var result = new List<object>();
            foreach (object g in JsonValue.GetArray(def, "groups") ?? new List<object>())
            {
                string id = JsonValue.AsString(g);
                List<string> items;
                if (id == null || !groups.TryGetValue(id, out items))
                {
                    warnings.Warn("coupon " + matchedCode + ": unknown unlock group '" + id + "'");
                    continue;
                }
                foreach (string item in items) AddReward(result, item, 1, matchedCode, warnings);
            }
            foreach (object r in JsonValue.GetArray(def, "rewards") ?? new List<object>())
            {
                var reward = JsonValue.AsObject(r);
                string type = JsonValue.GetString(reward, "type");
                long quantity = JsonValue.GetLong(reward, "quantity", 1);
                AddReward(result, type, quantity, matchedCode, warnings);
            }
            return result;
        }

        private static void AddReward(List<object> result, string type, long quantity, string code, Log log)
        {
            // An unknown type becomes a null reward in the inbox message; Reset* would wipe the profile.
            if (!CrmDocumentValidator.IsKnownRewardId(type))
            {
                log.Warn("coupon " + code + ": reward '" + type + "' skipped (unknown or unsafe reward type)");
                return;
            }
            if (quantity < 1 || quantity > Num.IntMax)
            {
                log.Warn("coupon " + code + ": reward '" + type + "' skipped (quantity " + quantity.ToString(CultureInfo.InvariantCulture) + ")");
                return;
            }
            if (result.Count >= MaxRewards)
            {
                log.Warn("coupon " + code + ": more than " + MaxRewards + " rewards; the rest is dropped");
                return;
            }
            var entry = new Dictionary<string, object>(StringComparer.Ordinal);
            entry["type"] = type;
            entry["quantity"] = quantity;   // an integer: read with SafeGetInt
            result.Add(entry);
        }

        private Dictionary<string, object> Find(string code, out string matched, out string definition)
        {
            matched = Normalize(code);
            definition = matched;
            if (matched.Length == 0) return null;
            Dictionary<string, object> def;
            if (codes.TryGetValue(matched, out def)) return def;
            // UNLOCKALL2 -> UNLOCKALL when the definition allows a numeric suffix.
            int end = matched.Length;
            while (end > 0 && char.IsDigit(matched[end - 1])) end--;
            if (end < matched.Length && end > 0 && codes.TryGetValue(matched.Substring(0, end), out def)
                && JsonValue.GetBool(def, "allowNumericSuffix", false))
            {
                definition = matched.Substring(0, end);
                return def;
            }
            return null;
        }

        public static string Normalize(string code)
        {
            return (code ?? "").Trim().ToUpperInvariant();
        }

        private void AddCodes(Dictionary<string, object> source)
        {
            if (source == null) return;
            foreach (var kv in source)
            {
                var def = kv.Value as Dictionary<string, object>;
                string code = Normalize(kv.Key);
                if (def == null || code.Length == 0)
                {
                    log.Warn("coupon definition '" + kv.Key + "' ignored (must be an object)");
                    continue;
                }
                codes[code] = def;
            }
        }

        private static Dictionary<string, object> ReadObject(IContentProvider content, string name, Log log)
        {
            string text;
            object parsed;
            if (content == null || !content.TryReadContentText(name, out text))
            {
                (log ?? Log.Null).Warn("content file " + name + " is missing (run tools/content-gen)");
                return null;
            }
            if (!MiniJson.TryParse(text, out parsed) || !(parsed is Dictionary<string, object>))
            {
                (log ?? Log.Null).Error("content file " + name + " is not a JSON object");
                return null;
            }
            return (Dictionary<string, object>)parsed;
        }
    }

    /// <summary>One code of <see cref="CouponCatalog.Describe"/>.</summary>
    public sealed class CouponDescription
    {
        /// <summary>Normalised (upper case).</summary>
        public string Code { get; set; }

        /// <summary>The definition's "description", or "".</summary>
        public string Description { get; set; }

        /// <summary>The code also works followed by digits (UNLOCKALL2), "allowNumericSuffix".</summary>
        public bool NumericSuffix { get; set; }

        /// <summary>"ledger" or "inbox" (<see cref="CouponCatalog.DeliveryOf"/>).</summary>
        public string Delivery { get; set; }

        /// <summary>How many rewards the code expands to.</summary>
        public int Rewards { get; set; }

        /// <summary>An inbox code above <see cref="CouponCatalog.MaxInboxRewards"/> rewards: redeeming it is refused.</summary>
        public bool TooLargeForTheInbox
        {
            get { return Delivery == CouponCatalog.InboxDelivery && Rewards > CouponCatalog.MaxInboxRewards; }
        }
    }

    /// <summary>One redeemed code (<see cref="CouponCatalog.Match"/>).</summary>
    public sealed class CouponMatch
    {
        /// <summary>The code as redeemed, normalised (UNLOCKALL2).</summary>
        public string Code { get; set; }

        /// <summary>The definition it matched (UNLOCKALL).</summary>
        public string Definition { get; set; }

        /// <summary>"ledger" or "inbox".</summary>
        public string Delivery { get; set; }

        /// <summary>Every reward, <c>{type, quantity}</c>, in claim order.</summary>
        public List<object> Rewards { get; set; }

        /// <summary>Ledger codes only: the reward ids to write, in order, once each (null for an inbox code).</summary>
        public List<string> Keys { get; set; }
    }

    /// <summary>
    /// The compact id lists of unlocks.json: <c>"WeaponCamo_1_{2,4-110}"</c> stands for WeaponCamo_1_2, WeaponCamo_1_4 ...
    /// WeaponCamo_1_110 (one brace group, comma-separated numbers and inclusive ranges). Text without braces is one id.
    /// </summary>
    public static class RewardPattern
    {
        public const int MaxExpansion = 100000;

        public static List<string> Expand(string pattern)
        {
            var result = new List<string>();
            int open = pattern.IndexOf('{');
            if (open < 0)
            {
                if (pattern.IndexOf('}') >= 0) throw new FormatException("unbalanced '}' in '" + pattern + "'");
                result.Add(pattern);
                return result;
            }
            int close = pattern.IndexOf('}', open + 1);
            if (close < 0 || pattern.IndexOf('{', open + 1) >= 0 || pattern.IndexOf('}', close + 1) >= 0)
                throw new FormatException("pattern '" + pattern + "' needs exactly one {...} group");
            string prefix = pattern.Substring(0, open);
            string suffix = pattern.Substring(close + 1);
            foreach (string part in pattern.Substring(open + 1, close - open - 1).Split(','))
            {
                string p = part.Trim();
                int dash = p.IndexOf('-');
                long from, to;
                if (dash < 0)
                {
                    from = ParseNumber(p, pattern);
                    to = from;
                }
                else
                {
                    from = ParseNumber(p.Substring(0, dash), pattern);
                    to = ParseNumber(p.Substring(dash + 1), pattern);
                }
                if (to < from) throw new FormatException("descending range '" + p + "' in '" + pattern + "'");
                if (to - from + result.Count > MaxExpansion) throw new FormatException("pattern '" + pattern + "' is too large");
                for (long n = from; n <= to; n++) result.Add(prefix + n.ToString(CultureInfo.InvariantCulture) + suffix);
            }
            return result;
        }

        private static long ParseNumber(string s, string pattern)
        {
            long n;
            if (!long.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n))
                throw new FormatException("'" + s + "' is not a number in '" + pattern + "'");
            return n;
        }
    }
}
