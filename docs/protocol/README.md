# Protocol notes

One file per module group; each documents the endpoints the group implements, the exact responses, and the
evidence (research notes) behind them.

| File | Group | Services |
|---|---|---|
| [auth.md](auth.md) | AUTH | eve, pandora, auth |
| [storage.md](storage.md) | STORAGE | storage (Seshat) |
| [content.md](content.md) | CONTENT | crm, matchmaker, assets/coupons, generated content |
| [misc.md](misc.md) | MISC | social, messaging, alerts, leaderboards, game_stats, profanity, chat, clans, wall, bantool, geo, catch-all |
| [unlocks.md](unlocks.md) | core | `settings.unlockAll` through the inventory ledger |

The full endpoint map and the settings of every module are in [../PROTOCOL.md](../PROTOCOL.md).
Conventions shared by all services (transport, token placement, status-code semantics) are in
[research/02-federation-protocol.md](../research/02-federation-protocol.md) §1 and [MODULES.md](../MODULES.md).
