# HD textures: the installer on the phone

How PC (Steam) textures reach the phone (docs/research/pc-textures.md, route A): `tools/hd-textures build` on the
player's computer converts them into an **HD pack**, the player copies the pack to the phone, and the embedded host
(`hosts/embedded/Hd/HdInstaller.cs`) installs it into the game's own `Data/` folder at start-up. The pack, its manifest
and the rules the installer can rely on are the converter's contract, [hd-textures.md](hd-textures.md) §2-§4; this page
is the installer's side: what it reads of the pack, and how it keeps the Java installer's index consistent, backs up
the originals and removes the pack (hd-textures.md §4 leaves these to the installer package).

Background (device test of build 10, owner's Galaxy S22, ETC pack): a zip mounted through the engine's zip search path
(route B) was found by the managed lookup but never reached the renderer; GCBF-wrapped ETC1 `.f` files written into
`Data/<h>/<rest>.f` (route A) rendered, with `SKIP_VALIDATION=1`. Route B was removed; the installer below makes route A
work without `SKIP_VALIDATION`.

## What the installer reads of the pack

`HdPack.Open` reads `mcvs-hd.json` and the zip's central directory (`StoredZip`; entries must be stored, the
`0xD935` alignment extra field of the converter is skipped like any other) and refuses the pack as a whole, with the
reason on Status and in the log, when any of this does not hold:

| manifest (hd-textures.md §3) | the installer |
|---|---|
| `kind` | must be `"hd-textures"` |
| `format` | must be the number `1`; a higher one is refused ("this build reads format 1") |
| `pack` | `"ETC"` today. When the phone's index is recognisably the other data pack (ASTC), the pack is refused as a whole |
| `files` | at least one |
| `files[].entry` | `Data/<dir>/<name>.f`: exactly three parts, no `.`/`..`, no backslash, no part starting with `.`; listed once; a stored zip entry of that name whose size and CRC-32 are `replacement`'s |
| `files[].original` | `size` and `crc32` (8 hex digits; a JSON number is read too) of the file it replaces, as it is in the phone's `Data/`. `width`, `height`, `mipLevels` are not used |
| `files[].replacement` | the same for the new file; a file equal to its original is refused |
| `files[].group` | `null`: the file alone; a string (`"lightmaps:<Level>"`): every file of it is installed and removed together, all or none |
| everything else | not used (`tool`, `created`, `gameVersion`, `scope`, `categories`, `maps`, `hash`, `logicalPath`, `category`, `levels`, `layout`, the PSNRs, `totals`) |

The status names the pack by its file name; `installed.json` keeps the CRC-32 of the manifest as the pack's id (every
build of a pack has another `created` time, so another id).

The manifest is the only thing the installer trusts for "which original": a file whose original on the phone is not
exactly `original` (size and CRC32) is not replaced (hd-textures.md §4 rule 2). A pack made from another texture variant,
version or a damaged copy therefore changes nothing. When the first 20 groups all fail that test, the rest is not tried.

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
   checks the data online and would replace every file of another size: the originals are copied back instead (within
   the budget; the rest at the next start), and the pack is installed again at the first start with an index.

### Installing a group

1. Plan: every file of the group must have its original in `Data/` (size and CRC32 of the manifest) or already in
   `McvsOffline/hd/backup/` (checked the same way); the original is copied to the backup (temporary name, checked,
   renamed). One file that fails skips the group.
2. Record: the planned files go into `McvsOffline/hd/installed.json` (atomic write) before any of them changes.
3. Write: each HD file is copied from the zip to `Data/<h>/.<name>.mcvs-hd-tmp`, checked (size, CRC32 against the zip and the
   manifest), flushed and renamed over the original. A failure puts the group's originals back and ends the install for
   this start.
4. At most 8 s per start (`HdInstallerOptions.BudgetMs`; at least one group always, unless the start already spent the
   budget giving originals back): the rest is counted as "at the next start" and done then. Files already installed cost
   one `stat` each.

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

Removing runs within the same start-up budget as installing, a group at a time (a level's lightmaps together; at least
one group per start): the groups left keep their records, their HD files and their index entries, which agree, and are
given back at the next start ("N originals at the next start" on Status). Removal comes first: a start that ran out of
time removing installs nothing new. The same holds when the installer has no usable index and the originals are put back
(below).

### State on the phone

| path | what |
|---|---|
| `McvsOffline/hd/mcvs-hd-*.zip` | the pack (the player's) |
| `McvsOffline/hd/installed.json` | `format` `mcvs-hd-installed`, `version` 1, `pack`, `packId`, `name`, `updated`, and per file `path`, `group`, `originalSize`, `originalCrc`, `originalPieces` (CRC per piece), `originalZip` (offset and ZIP size per piece in Gameloft's index), `hdSize`, `hdCrc`, `hdPieces`. Unreadable: nothing is touched (logged) |
| `McvsOffline/hd/backup/Data/<h>/<name>.f` | the originals, while their HD files are installed |

What `Data/` holds is always read from the files, never assumed from the state, so any start cut short is completed or
undone by the next one. Status › Game host › HD textures shows the pack, the files and bytes installed, and what is left.
