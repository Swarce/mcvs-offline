# tools/content-gen (owner: CONTENT)

Generates our own content (store catalogue, prices, economy tuning) from the decoded game tables into
`server/Content/`, where the build compiles it into `McvsOffline.Server.dll` (see `server/Content/README.md`).

Inputs are the player's own decoded data pack (`tools/datapack` / `datapack-decoded`, never committed).
Outputs may contain only generated item ids, prices and our tuning, never game data copied verbatim.

Not written yet.
