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
