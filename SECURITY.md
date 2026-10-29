# Security policy

## Reporting a problem

Report security problems privately, not in a public issue:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability** (GitHub private vulnerability reporting).
3. Describe the problem: the player build (MCVS Offline page › Status) or the commit, what an attacker can do, and the
   steps to show it.

If the Security tab has no **Report a vulnerability** button, open an issue (the feature request form, area
"Other") that only asks for a private channel. Put no details of the problem in it.

Never attach your APK, game data, an HD pack or a signing key. The project's logs are redacted at the source and hold no
live ticket or session cookie.

## What the project exposes

The MCVS Offline server runs inside the game process on the phone (or as the standalone host on a PC).

**Network.** By default the server listens on `127.0.0.1:51780` only (`bind` in `settings.json`, default
`127.0.0.1`). Nothing outside the phone can reach it. `"lanMode": true` or a non-loopback `bind` makes it listen
on the network, so other devices can reach it; LAN mode is experimental and meant for trusted networks only.

**Every request** passes a local access gate first:

- While the server is bound to loopback and LAN mode is off, a request with a foreign `Host` header gets 404 on every
  route. This stops web pages that use DNS rebinding.
- Routes under `/_mcvs/` need a loopback caller and an allowed `Host`, also in LAN mode.
- Writes under `/_mcvs/` need the header `X-MCVS: 1`, an allowed `Origin` when one is sent, and `Sec-Fetch-Site`
  `same-origin` or `none` when one is sent. The one exception is the post-mode form POST to `/_mcvs/ui`, which carries
  the one-time ticket instead (§5.2).

**The MCVS Offline page** (`/_mcvs/ui/`) can change settings and saves. Access works like this
([design/settings-page.md](docs/design/settings-page.md) §5):

- When the game opens the page, the server mints a **one-time ticket** (32 hex characters, valid for 120 seconds,
  single use). It travels only in the URL fragment handed to the game's own in-game browser (or, in post mode, in that
  browser's form body). It never reaches a request log.
- The page exchanges the ticket for a **session cookie** `mcvs_s` (64 hex characters, `HttpOnly; SameSite=Strict`).
  A session ends after 2 hours idle, 12 hours in total, or when the game process ends. At most 4 sessions and 16
  outstanding tickets exist at a time.
- Tickets and sessions live only in memory. No ticket, cookie or other bearer secret is written to a file, a profile,
  a log line or a share; the logs are redacted at the source.
- Every page API route except `ping` needs a session. Writes also need `Content-Type: application/json` and a JSON
  object body. Failed ticket exchanges and writes are rate-limited.
- Chrome and other browsers on the phone never get a ticket, so they get a view-only page at most. A small set of
  read-only diagnostics under `/_mcvs/` stays readable from the phone without a session; it holds no secret.

**The patcher** runs on your own computer and works on your own APK. It creates your signing key the first time it
runs: in your user profile (`…/McvsOffline/`), or at the path you give with `--keystore`. Its default password
(`mcvsoffline`) is public, so the file itself must stay private (or set your own password with `--ks-pass`). Anyone with
the key can sign an update to your installed game.

## Known and accepted

These are among the accepted risks of [design/settings-page.md](docs/design/settings-page.md) §5.5 and are not
vulnerabilities in this project:

- Gameloft's Federation super credential is public. An app on the same phone can already use the Federation API as
  any account and write saves through it. The `Host` check stops web pages, not apps.
- An app on the same phone can read the read-only diagnostics, which hold no secret.
- An app on the same phone can fire the redeem trigger that opens the page (at most once per 30 seconds, and only while
  no browser is open). The ticket still goes only to the game's own browser.
- An app on the same phone can log in the way the game does, with this phone's device id, and so become the page's
  current player (the `session` source of §6.6) while the game's own credential is unknown. A web page cannot.
- Native logcat lines cannot be redacted. A one-time ticket that appears there is dead within 120 seconds, and reading
  logcat needs `READ_LOGS`.

A way to get around any of the protections above, for example a web page that reaches the server, an app that gets a
session without a ticket, or a secret that reaches a file or a log, is in scope.

## Out of scope

- **Gameloft's game itself**: its native engine, its Java code, its own match and menu servers (the Game Server and the
  Transaction Server), and the original online services. This project does not fix the game; it replaces only the
  missing backend.
- Attacks that need root, `adb` or physical access to an unlocked phone.
- Problems in Android, the WebView or other third-party software.
- LAN mode on an untrusted network.
