# HD textures: the pack and its installer

How PC (Steam) textures reach the phone (docs/research/pc-textures.md, route A): a tool on the player's computer
converts them into an **HD pack**, the player copies the pack to the phone, and the embedded host installs it into the
game's own `Data/` folder at start-up. This page is the contract between the two sides.

> Written by the phone-side package (`feat/hd-install`) while the converter (`wip/hd-converter`, tools/hd-textures) was
> being built in parallel and had no design page yet. The format below is what the installer reads; where the
> converter's own design differs, one of the two is changed to match before both are merged.

Background (device test of build 10, owner's Galaxy S22, ETC pack): a zip mounted through the engine's zip search path
(route B) was found by the managed lookup but never reached the renderer; GCBF-wrapped ETC1 `.f` files written into
`Data/<h>/<rest>.f` (route A) rendered, with `SKIP_VALIDATION=1`. Route B was removed; the installer below makes route A
work without `SKIP_VALIDATION`.

## The HD pack

One file, `McvsOffline/hd/mcvs-hd-<name>.zip` (the player's game root, next to `Data/`):

* a plain zip: every entry **stored** (method 0; the `.f` files are LZ4 blocks already), no encryption, no ZIP64 (the
  whole zip under 4 GiB), no multi-disk;
* `mcvs-hd.json`, the manifest (UTF-8 JSON);
* one entry per replaced file, named exactly as the file in the game's `Data/` folder: `Data/<h[0]>/<h[1:]>.f`, where
  `h` is the lower-case, unpadded hex CRC32 of the texture's logical path (research/pc-textures.md §1.1). Its bytes are the
  complete new file as the game reads it (GCBF-wrapped KTX, or whatever the original's container is: split-alpha zip for
  ETC textures with alpha, raw KTX where the original is raw). Other entries are ignored.

An example manifest (sizes and CRCs illustrative):

```json
{
  "format": "mcvs-hd",
  "version": 1,
  "id": "2026-10-30T12:00:00Z-1a2b3c4d",
  "name": "PC textures, tiers 1-2",
  "pack": "ETC",
  "gameVersion": "1.17.32",
  "files": [
    {
      "path": "Data/8/dbd2f1.f",
      "original": { "size": 43301, "crc32": "1c7f0e2a" },
      "hd": { "size": 160126, "crc32": "9b1d77e0" },
      "group": "env"
    },
    {
      "path": "Data/3/5f0e1d2.f",
      "original": { "size": 2796360, "crc32": "0f00ba11" },
      "hd": { "size": 11184968, "crc32": "77aa0e3c" },
      "group": "lightmaps/Port"
    }
  ]
}
```

| key | required | meaning |
|---|---|---|
| `format` | yes | `"mcvs-hd"` |
| `version` | yes | `1`; a pack of another version is refused as a whole |
| `id` | no | changes with every build of a pack (the status shows `name`; `id` is kept in the installed state). Default: the CRC32 of the manifest |
| `name` | no | shown on Status and in the log. Default: the file name |
| `pack` | no, recommended | the Android data pack the originals come from, `"ETC"` or `"ASTC"`. When the phone's data is recognisably the other one, the pack is refused as a whole |
| `gameVersion` | no | informative (`"1.17.32"`) |
| `files[].path` | yes | `Data/<dir>/<name>.f`: exactly three parts, no `.`/`..`, no backslash, no part starting with `.`; listed once |
| `files[].original` | yes | `size` (bytes) and `crc32` (8 hex digits, or a JSON number) of the file the pack replaces, as it is in the phone's `Data/` |
| `files[].hd` | yes | the same for the new file; must equal the zip entry's size and CRC32 |
| `files[].group` | no | files with the same group are installed together or not at all (all lightmaps of a level: research §8.1 R7). Default: the file alone |

The manifest is the only thing the installer trusts for "which original": a file whose original on the phone is not
exactly `original` (size and CRC32) is not replaced. A pack made from another texture variant, version or a damaged copy
therefore changes nothing. When the first 20 groups all fail that test, the rest is not tried.

Packs are personal: they contain converted game files from the player's own Steam copy and are never shared or committed.

## On the phone

The player copies the pack with `adb push` (PLAYER-SETUP.md section 7). Only one pack is used: with several
`mcvs-hd-*.zip`, the last by ordinal name. `hd.enabled` (settings.json, Settings › Graphics › HD textures, default true)
installs it; false removes an installed pack at the next start. Actions › Remove HD textures sets `hd.enabled` false and
offers the restart.

### When: before the installer validates `Data/`

The Java `GameInstaller` starts after the activity's `onCreate` returns; the embedded host runs inside `onCreate`
(Bootstrap, `EmbeddedHost.PrepareInstallerData`), so everything below happens before the installer's size check, on
every start (docs/INSTALL-OFFLINE.md):

1. `HdInstaller.Prepare`: for every file in `installed.json`, read what `Data/` holds (by size; by CRC32 when the original
   and the HD file have the same size) and make the installer's index and our saved copy list exactly that (a start cut
   short between a file and its index entry; an index reset by a reinstall, a `gl_ver` change or a CDN re-sync). Delete
   temporary files of a cut-short start. Hand the original sizes of the HD files to the next step.
2. `InstallerData.Prepare` (unchanged rules, plus `HdOriginalSizes`): restores or rebuilds the installer's index as before;
   the 1.17.32 pack is recognised by the original sizes, so a `Data/` folder with HD files still counts as complete.
3. `HdInstaller.Apply`: removes and installs (below), then writes the index, the journal, the saved copy and the state.
   Logs `[boot] hd: ...`. When the installer has no index it will keep (missing, unreadable, `gl_ver` not `1173200`), it
   checks the data online and would replace every file of another size: the originals are copied back instead, and the
   pack is installed again at the first start with an index.

### Installing a group

1. Plan: every file of the group must have its original in `Data/` (size and CRC32 of the manifest) or already in
   `McvsOffline/hd/backup/` (checked the same way); the original is copied to the backup (temporary name, checked,
   renamed). One file that fails skips the group.
2. Record: the planned files go into `McvsOffline/hd/installed.json` (atomic write) before any of them changes.
3. Write: each HD file is copied from the zip to `Data/<h>/.<name>.mcvs-hd-tmp`, checked (size, CRC32 against the zip and the
   manifest), flushed and renamed over the original. A failure puts the group's originals back and ends the install for
   this start.
4. At most 8 s per start (`HdInstallerOptions.BudgetMs`; at least one group always): the rest is counted as "at the next
   start" and done then. Files already installed cost one `stat` each.

### The installer's index

For each file taken over, the entries in `pack.info` (and `McvsOffline/install/pack.info`) are replaced in place by the
entries for what `Data/` holds: the size; one entry, or 2 MiB pieces `name.split_NNNN` with the total after piece 1 when
the file is over 2 MiB and not an exact multiple of 2 MiB (Gameloft's packer, `PackIndex.PieceSizes`); the CRC32 of each
piece (0 in a rebuilt index); the archive offset and ZIP size of the original's piece N (kept for removal; they only
matter for a CDN download). Section headers, empty sections, the order and every other entry stay byte for byte
(`PackIndex.Replace`; checked on the real ETC index). The journal `d_o_w_n_l_o_a_d_e_d.txt` loses the old piece lines
of a changed file and gets one line per piece of every split HD file.

### Removing

Every recorded file is copied back from its backup (checked against the original's CRC32), its index entries become the
original's again (sizes, CRCs, archive offsets: Gameloft's index is byte-identical to before the install), the journal
gets the original's pieces, then the record and the backup go. Files a new pack no longer contains are removed the same
way. Without the pack file, installed HD files stay; a missing or damaged one gets its original back (an HD file can only
be written from the pack).

### State on the phone

| path | what |
|---|---|
| `McvsOffline/hd/mcvs-hd-*.zip` | the pack (the player's) |
| `McvsOffline/hd/installed.json` | `format` `mcvs-hd-installed`, `version` 1, `pack`, `packId`, `name`, `updated`, and per file `path`, `group`, `originalSize`, `originalCrc`, `originalPieces` (CRC per piece), `originalZip` (offset and ZIP size per piece in Gameloft's index), `hdSize`, `hdCrc`, `hdPieces`. Unreadable: nothing is touched (logged) |
| `McvsOffline/hd/backup/Data/<h>/<name>.f` | the originals, while their HD files are installed |

What `Data/` holds is always read from the files, never assumed from the state, so any start cut short is completed or
undone by the next one. Status › Game host › HD textures shows the pack, the files and bytes installed, and what is left.
