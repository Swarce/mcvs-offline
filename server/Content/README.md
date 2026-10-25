# server/Content — generated content (owner: CONTENT)

Files in this folder are **compiled into `McvsOffline.Server.dll`** at build time (both the net8 and the
embedded build). `server/build/McvsOffline.Server.Shared.props` turns each file into a C# string literal
(`.json`, `.txt`, `.csv`, `.tsv`) or a base64 literal (anything else) inside the generated
`BuiltInContent` class. `*.md` files are skipped.

Why not manifest resources: the game's trimmed `mscorlib.dll` has no `Assembly.GetManifestResourceStream`,
and the embedded server must stay a single DLL with no extra files to deploy.

Reading content at runtime:

```csharp
string json;
if (context.Content.TryReadContentText("offline_store.json", out json)) { ... }
```

A file with the same relative name in `<data>/content/` overrides the built-in one, so players and testers
can tweak content without rebuilding.

Rules:

- Only content we generate (item ids, prices, our own tuning) may live here. Never copy game data verbatim:
  game files are read at runtime from the player's own data pack via `context.Content.TryReadGameFile`.
- `tools/content-gen` writes these files; keep them deterministic (sorted keys / stable order) so diffs stay
  reviewable.

## Files

All of them are written by `tools/content-gen` (see its README); do not edit them by hand, change
`tools/content-gen/pricing.json` (or the generator) and regenerate. Testers can drop a modified copy into
`<data>/content/` instead (then `POST /_mcvs/crm/reload` or restart).

| File | Read by | Purpose |
|---|---|---|
| `offline_store.json` | Crm | the VersusCoins catalogue served as CRM `offline_store` (docs/ECONOMY.md) |
| `crm_overlay.json` | Crm | static CRM game parameters merged over the player's preconfiguration |
| `economy.json` | Crm | `_OverrideTables` rows for the economy multipliers; cost summary |
| `unlocks.json` | Assets | ordered reward-id groups for the unlock coupons |
| `coupons.json` | Assets | built-in coupon codes |
| `liveops.json` | Social (LiveOps), Crm, Leaderboards | live-ops catalogue: objectives, rewards, modifiers, rival targets (docs/LIVEOPS.md) |
| `manifest.json` | – | generator, source version and file list |
