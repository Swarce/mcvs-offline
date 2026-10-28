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

### Where the installer writes: never in the drop folder

Device test of build 11 (owner's Galaxy S22, Android 16, targetSdk 35): `cannot back up Data/9/705cf.f to
.../files/McvsOffline/hd/backup/Data/9/705cf.f`, `UnauthorizedAccessException ... Permission denied`, `[boot] hd: none
installed; 906 files at the next start; 1 failed`. The owner had made `McvsOffline/hd/` with adb (Shizuku); on Android 11+
a folder under `Android/data/<package>/` made by another tool (adb's `shell` uid, a file manager through MediaStore)
belongs to that uid, so the app can read the pack in it but create nothing inside it. `McvsOffline/` itself and `Data/`
were made by the app and are writable. So:

* **`McvsOffline/hd/` is a read-only drop folder.** The installer reads the pack (and, from build 11, an old
  `installed.json` and `backup/`) there and never creates, writes, renames or deletes anything inside it. Any tool may
  make it.
* **Our files go to a folder the app makes itself** (`HdPlaces`): `McvsOffline/hd-install/` (`installed.json`, `backup/`,
  `imported.txt`). The folder is created when missing and must take a probe file (`.mcvs-hd-probe`: created, flushed,
  deleted). When it cannot (it was made by another tool too, or the storage refuses), the same folder in the app's
  internal storage, `getFilesDir()/McvsOffline/hd-install/`, is used; the log says which (`hd: installer files in
  McvsOffline/hd-install`, or a warning `hd: installer files in the app's internal storage <path> (McvsOffline/hd-install:
  permission denied)`), and while files are installed Status adds "backups in the app's internal storage". The folder
  that holds the current `installed.json` is used first. When both hold one, the internal one is current only when it
  took over `McvsOffline/hd-install`'s (its `imported.txt` lists that file exactly as it is now): `McvsOffline/hd-install`
  keeps the copy it had when it could not be written, and that stale copy never wins once it can be written again (the
  internal folder stays in use; `McvsOffline/hd-install` is still probed so Status gives its reason). Nothing is created
  at all while there is neither a pack (with `hd.enabled`) nor a state anywhere.
* **Probe first, then change `Data/`.** Before anything is removed or installed, `Data/` and every `Data/<h>/` folder of an
  installed file or of the pack take a probe file too. When our folder (both places) or one of those cannot be written,
  nothing in `Data/` changes and the reason is the one line on Status › Game host › HD textures and in `[boot] hd:`:
  `none installed; cannot write McvsOffline/hd-install: permission denied (nor the app's internal storage: permission
  denied); nothing changed`, `none installed; cannot write Data/8: permission denied; nothing changed`, or, with HD files
  installed, `<pack>, N files installed (...); nothing changed now: cannot write ...`. Both steps still make the
  installer's index, our saved copy and the journal match what `Data/` holds (they live in the installer's storage and
  `McvsOffline/install/`, never in `Data/` or our folder), reading the state wherever it is: step 2 too, because
  `InstallerData` may have replaced the index between the steps (after a reinstall, Gameloft's own index from
  `files/pack.info` lists the original sizes, and the installer would delete and download every HD file).
* **The state of an earlier start is taken over once.** Another place's `installed.json` (build 11's
  `McvsOffline/hd/installed.json`, or the other `hd-install` folder) that `imported.txt` (in either `hd-install`
  folder) does not list is read and checked like our own (`HdState.Load`; one that cannot be read is left alone,
  logged, and tried again at the next start; while we have no state of our own nothing changes until then, as with our
  own unreadable state: `McvsOffline/hd/installed.json of an earlier build cannot be read; nothing changed`). Then:
  * our folder has no `installed.json`: it is copied byte for byte into our folder (atomically);
  * it took over ours as ours is now (its `imported.txt` lists our `installed.json`): it is the newer one (that folder was
    in use while ours could not be written) and replaces ours byte for byte;
  * otherwise (two states neither took from the other): its records for files ours does not list are added to ours
    (ours wins for a file both list), so no record, and no original only it knows, is ever dropped.

  Its CRC-32, size and folder are added to `imported.txt`, so a later start never takes it again (also once our own
  state is gone after a removal). The backups stay where they are: **removal and the "already backed up" check look in our
  `backup/` first, then in the old places** (each copy checked against the original's CRC-32 on the way back). A new
  backup is always made in our folder: from `Data/` when it holds the original, else copied (checked) from an old backup,
  so a record never depends on a folder we do not own. Backups in the drop folder are never deleted (that would be a
  write there); once nothing is installed, and build 11's `installed.json` there is gone or was taken over, Status and
  the log add `McvsOffline/hd/backup (and its installed.json) of an earlier build is no longer used and may be deleted`.
  The other `hd-install` folder is the app's own: once nothing is installed, its `installed.json` (when it was taken
  over) and its backups are deleted by the installer (left as they are when that folder cannot be written); it is
  never offered for deletion.

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
   `hd-install/backup/` (checked the same way; an old backup of build 11 in `McvsOffline/hd/backup/` is copied into
   ours when `Data/` does not hold the original); the original is copied to the backup (temporary name, checked,
   renamed). One file that fails skips the group; one that cannot be written ends the install for this start.
2. Record: the planned files go into `hd-install/installed.json` (atomic write) before any of them changes.
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

Every recorded file is copied back from its backup (ours, else an old one of build 11; checked against the original's
CRC32), its index entries become the original's again (sizes, CRCs, archive offsets: Gameloft's index is byte-identical
to before the install), the journal gets the original's pieces, then the record and our backup go (an old backup stays). Files a new pack no longer contains are removed the same
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
| `McvsOffline/hd/mcvs-hd-*.zip` | the pack (the player's). The folder is only read; any tool may make it |
| `McvsOffline/hd-install/` | made by the app; when it cannot be written, `getFilesDir()/McvsOffline/hd-install/` instead (the log and Status say so) |
| `McvsOffline/hd-install/installed.json` | `format` `mcvs-hd-installed`, `version` 1, `pack`, `packId`, `name`, `updated`, and per file `path`, `group`, `originalSize`, `originalCrc`, `originalPieces` (CRC per piece), `originalZip` (offset and ZIP size per piece in Gameloft's index), `hdSize`, `hdCrc`, `hdPieces`. Unreadable: nothing is touched (logged) |
| `McvsOffline/hd-install/backup/Data/<h>/<name>.f` | the originals, while their HD files are installed |
| `McvsOffline/hd-install/imported.txt` | `<crc32> <size> <folder>` per `installed.json` of another place taken over (copied, replacing ours, or merged into ours), so it is taken over once; also how the current state is told from a stale copy |
| `McvsOffline/hd/installed.json`, `McvsOffline/hd/backup/` | build 11 only: taken over / read, never written or deleted; may be deleted once Status says so |

What `Data/` holds is always read from the files, never assumed from the state, so any start cut short is completed or
undone by the next one. Status › Game host › HD textures shows the pack, the files and bytes installed, and what is left.
