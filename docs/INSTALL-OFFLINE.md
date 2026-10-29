# Installing the game data without Gameloft's servers

The APK contains no game data. On first launch the game's own installer downloads about 1.5 GB from Gameloft's CDN
into `/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data` (15,747 files, about 2 GB unpacked). The CDN
still answered in October 2026, but it will not stay up forever. This page explains how the offline build accepts data
that is already there (a backup, or a mirror you made), so the game starts **with no internet at all**, and what to do
if something goes wrong.

The patcher keeps both the 64-bit (`arm64-v8a`) and 32-bit (`armeabi-v7a`) libraries by default; `--abi arm64-v8a` builds a
smaller 64-bit-only APK (every phone from about 2017 on, such as the Galaxy S22, is 64-bit). 32-bit phones are untested.

## What you need on the phone

`/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/` must contain:

| Path | What it is |
|---|---|
| `Data/MCVS.game`, `Data/Config/EngineConfig.json`, `Data/1/` ... `Data/f/` | the data pack (15,747 files) |
| `McvsOffline/install/pack.info` | optional but recommended: the index of the data pack (see below) |
| `McvsOffline/hd/` | optional: an HD texture pack (a drop folder: only read, any tool may make it; see "HD textures" below) |
| `McvsOffline/hd-install/` | made by the game while an HD pack is installed: the list of replaced files and their originals |
| `McvsOffline/install/replaced.tsv` | only with the ASTC HD data pack: its replaced files and Gameloft's size of each (see "The ASTC HD data pack") |
| `McvsOffline/` (the rest) | your offline profile, settings and logs (only in a backup of an offline install) |

There are two variants of the data pack, by texture format: **ASTC** and **ETC**. The installer picks ASTC when the
GPU reports ASTC support (the Galaxy S22 does) and ETC otherwise. Use the variant your phone downloaded before; with a
mirror, take ASTC for a phone whose GPU supports it, ETC otherwise. Both hold the same files.

## Option 1: restore a backup of the whole `files/` folder (best)

Every start of the offline build saves a copy of the installer's index to
`files/McvsOffline/install/pack.info`. A backup of the whole `files/` folder made after that therefore carries
everything:

```sh
adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files mcvs-files-backup     # backup
adb install mcvs-offline.apk                                                                   # (re)install
adb shell mkdir -p /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files
adb push mcvs-files-backup/. /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/  # restore, before the first launch
```

Then launch. The installer screen shows briefly and the game starts, with Wi-Fi and mobile data off if you like.

## Option 2: restore only a `Data/` folder

An older backup (or one from the original game) has no `McvsOffline/install/pack.info`. If its `Data/` folder is the
**complete** 1.17.32 pack (exactly 15,747 files of 2,042,882,427 bytes for ASTC or 2,036,819,797 bytes for ETC), the
offline build rebuilds an index from the files themselves and the installer accepts them. This index has the file
sizes but not Gameloft's checksums, so it cannot tell a damaged file from a good one: check such a backup on a PC first
(see "Checking a backup" below). If the folder is not complete, the installer goes online to Gameloft's index, as the
original game would, and downloads what is missing.

To give an incomplete or unchecked `Data/` folder the real index, copy `pack.info` from a mirror (option 3) to
`files/McvsOffline/install/pack.info` or to `files/pack.info`.

## Option 3: a mirror made with `tools/cdn-mirror` (fresh install, no Gameloft servers needed)

On a PC, while Gameloft's CDN is still up, archive the data once (Python 3, standard library only):

```sh
cd tools/cdn-mirror
python3 mirror.py --out ~/mcvs-mirror --tex ASTC --extract     # or --tex ETC; without --tex: both (2.9 GiB of archives)
```

`--extract` writes `~/mcvs-mirror/1.17.32/ASTC/files/` with `Data/` and `McvsOffline/install/pack.info`. Keep the
whole `~/mcvs-mirror` folder (the `.amz` archives are byte-identical to Gameloft's and are checked against their MD5 and
every CRC32). To install on a phone:

```sh
adb install mcvs-offline.apk
adb shell mkdir -p /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files
adb push ~/mcvs-mirror/1.17.32/ASTC/files/. /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/
```

Then launch, no network needed. A mirror made today keeps working after the CDN is gone: `mirror.py --verify-only`
re-checks it offline and `--extract` can be run again from the archives.

## Option 4: let the game download the data

With nothing in `files/`, the installer downloads the data from Gameloft's CDN as the original game did (Wi-Fi
recommended; it may ask before using mobile data). The CDN's redirector limits requests to about two per second and
answers more with an error page; if the download screen shows an error or does not move, close the game and start it
again.

## Copying files on Android 11 and later

Android 11 and later hide `Android/data` from file managers on the phone. Use `adb push` (above) or a PC file transfer
that shows the folder. If the game cannot read the restored files (Android versions differ in how they treat files
pushed by `adb`), launch the game once so Android creates the folder itself, force-stop it, push again and launch.

## Checking a backup

On a PC, `tools/cdn-mirror/verify.py` checks a backup exactly as the installer does (existence, size and the CRC32 of
every file and 2 MiB piece):

```sh
python3 tools/cdn-mirror/verify.py mcvs-files-backup                 # uses the index the backup carries
python3 tools/cdn-mirror/verify.py mcvs-files-backup --mirror ~/mcvs-mirror
python3 tools/cdn-mirror/verify.py mcvs-files-backup --fetch         # Gameloft's index from the CDN
```

Exit status 0 means intact. On the phone, after the offline build gave the installer a Gameloft index back (options 1
and 3), it also checks every CRC32 in the background: it starts a minute after the installer has handed over to the
game (never while the installer is still downloading missing files), reads the whole pack at low priority and logs
`installer data: background check done`. Until it has read everything, `files/McvsOffline/install/verify-pending`
holds its position: if the game is closed first, the next start continues where it stopped (`CRC check pending` in the
`[install]` line). A file whose size is right but whose content is not is listed in
`files/McvsOffline/install/damaged.txt`; a missing file or one of another size is not listed, the installer's own size
check already fetches it. At the next start each listed file is read once more and deleted only if it is still damaged,
so the installer downloads it again (that needs Gameloft's CDN: to avoid it, copy a good file from another backup or a
mirror over it before that start; it is then kept).

## What works without internet

Everything in the offline build: start-up, menus, bot-filled matches, progression, the shop, the unlock codes. Only the
data download itself needs Gameloft's CDN, and only when no complete data pack is on the phone. The offline build also
keeps the installer's weekly "is there a new version?" check from running (there will be none).

## If the installer screen stalls

The start-up log (`Files > Downloads > MCVS-Offline > ...-bootstrap.log.txt`, or logcat tag `McvsOffline`) shows:

- one `[install] installer data: ...` line saying what was found and done, also as a `[boot]` milestone, for example
  `index restored from the saved index (ASTC, 15747 files, 16212 entries)` or `index rebuilt from the complete ASTC
  data`, or why not (`... holds 15000 files of ... bytes, not the complete 1.17.32 data pack`);
- `[installer] state ...` lines while the game's installer runs (its states, the download step, and its own error
  numbers), and `installer finished after N s` when it hands over to the game.

Send that file with a bug report.

## Settings

`files/McvsOffline/settings.json`, section `install` (all `true` by default):

| Key | Meaning |
|---|---|
| `manageInstaller` | prepare the installer's state before it runs (everything on this page); `false` leaves the installer exactly as in the original game |
| `rebuildIndex` | with no Gameloft index at hand, rebuild one from a complete `Data/` folder (option 2) |
| `verifyRestoredData` | the background CRC32 check after a restored index (resumed at later starts until done) |
| `suppressUpdateCheck` | keep the installer's weekly version check from running |
| `monitorInstaller` | the `[installer]` progress lines in the log |

## HD textures in `Data/`

An HD texture pack (`McvsOffline/hd/mcvs-hd-*.zip`, PLAYER-SETUP.md section 7, format in
[design/hd-textures.md](design/hd-textures.md), the installer in [design/hd-install.md](design/hd-install.md)) replaces files in `Data/` with larger ones. The installer deletes any
listed file whose size is not the one in its index, so the offline build changes the index together with the files, at
start-up, before the installer runs (`hosts/embedded/Hd/HdInstaller.cs`):

1. Each original is checked against the size and CRC32 the pack was made from, and copied to
   `McvsOffline/hd-install/backup/Data/<h>/<name>.f`. `McvsOffline/hd-install/installed.json` lists the files before any of
   them changes. The drop folder `McvsOffline/hd/` is only read (on Android 11+ a folder made with adb or a file manager
   belongs to that tool); when `hd-install/` cannot be written either, the game's internal storage is used, and when
   nothing can be written `Data/` is left alone and Status says why ([design/hd-install.md](design/hd-install.md)).
2. The HD file is written next to the original under a temporary name starting with `.` and renamed over it once its size
   and CRC32 are checked, so `Data/` never holds half a file.
3. The installer's index (`files/pack.info` in internal storage), its copy `McvsOffline/install/pack.info` and the journal
   `d_o_w_n_l_o_a_d_e_d.txt` are rewritten for those files: the new size, the CRC32 of the file or of each 2 MiB piece, a
   file over 2 MiB split into pieces as Gameloft's packer does (a whole file that grows past 2 MiB becomes split, every new
   piece is in the journal). Every other entry, and the rest of the file, stays byte for byte as it was; the archive
   offsets of the original are kept, as they only matter for a download from Gameloft's CDN.

So the installer's size check passes at every start without `SKIP_VALIDATION`, and the background CRC32 check (above)
reads the HD files' own CRCs. A start does at most about 8 seconds of copying, installing or removing; the rest follows
at the next start, and a level's lightmaps always go together. Removing (Actions › Remove HD textures, or `hd.enabled`
false) copies the originals back and gives the index, its copy and the journal their original entries again.

When something resets the index, the next start first makes the index match the files again: a reinstall gives back the
saved copy, which already lists the HD files; with no index at all, a `Data/` folder holding the complete pack plus HD
files is still recognised by the original sizes and gets a rebuilt index with the HD sizes; an index from Gameloft's CDN
over installed HD files gets their entries again before the installer looks. Only when the installer is left with no index
it can use (it then checks the data online) are the originals copied back, so it finds Gameloft's files; the pack is
installed again at the first start with an index. `install.manageInstaller` `false` leaves the HD files and the index
alone, as everything else on this page.

Keep `McvsOffline/hd/` (the pack) and `McvsOffline/hd-install/` (the backups) in a backup of `files/`. To check such a backup on a PC with `verify.py`, check it
against the index it carries (`McvsOffline/install/pack.info` lists the HD files), not against a mirror's.

## The ASTC HD data pack (ASTC phones such as the Galaxy S22)

The HD packs above are made for the ETC data pack. A phone with ASTC (the S22 has it) can instead run Gameloft's **ASTC**
pack with the PC textures in it: `tools/hd-textures astc-pack` ([README](../tools/hd-textures/README.md), "astc-pack")
writes the whole `files/` tree, in which the 2,215 textures the PC has sharper (map surfaces, lightmaps, characters,
equipment, props, VFX, 3D menus, minimaps) are the PC textures re-encoded to ASTC at the PC resolution, and every other
file is Gameloft's own ASTC file. It replaces the data pack as a whole (the ASTC pack's shaders read one RGBA texture,
the ETC pack's a split alpha, so ASTC textures need the ASTC pack around them), and it comes with its own installer
index, `McvsOffline/install/pack.info`, that lists every file as it is in this pack (sizes, CRC32s, 2 MiB pieces), and
`McvsOffline/install/replaced.tsv`, the replaced files with Gameloft's size and CRC32 of each.

What the offline build does with it at start-up (`hosts/embedded/Install/InstallerData.cs`):

* the pack is recognised by Gameloft's sizes in `replaced.tsv` (a listed file counts with Gameloft's size only while it
  has the listed size): the log says `ASTC with 2215 replaced files` (the first parts, built on 2026-10-28, said 2214:
  one 3D-menu emblem, `Data/d/1a57284.f`, stayed Gameloft's file; the parts rebuilt on 2026-10-29 replace it too,
  design/hd-textures.md §6), `DATA_TYPE` becomes `ASTC`;
* on a fresh install (no installer index), the index is given to the installer as any saved index: `installer data:
  index restored from the saved index (ASTC with 2215 replaced files, 15747 files, ... entries)`;
* when the installer still has the index of the data that was there before (the ETC pack), which would make it delete
  and download every file of another size: the saved index differs from the installer's, the data matches it completely
  and does not match the installer's, so the saved index replaces the installer's: `installer data: index switched to
  the saved index (ASTC with 2215 replaced files, 15747 files, ... entries)`;
* the installer then checks every file's size (and the journal of split pieces) as always, accepts the data and starts
  the game with no network and **without** `SKIP_VALIDATION`; a minute after the game has loaded the background check
  reads every CRC32 of the new index: `installer data: background check done, every file matches the CRC32 ...`.

This needs an APK built from the commit that added `replaced.tsv` or later (after build 11). **Build 11 and older do not
know it**: they keep the installer's old ETC index, and the installer would delete the ASTC files of other sizes and try
to download them. With such a build, clear the app's storage (Android Settings › Apps › the game › Storage › Clear
storage), so the pushed index is restored as on a fresh install, and put the profile back afterwards (steps below).
Clear storage deletes everything in `files/`, the profile in `McvsOffline/` included, and the installer's own index.

### Installing it (from the ETC data, a manual ETC HD install and `SKIP_VALIDATION`)

The parts (`mcvs-astc-hd-NN-of-MM.zip`, below 380 MiB each, with `SHA256SUMS.txt`) unpack into one `files/` folder; the
last part has `README.txt` with the numbers of the pack and these steps (`tools/hd-textures` writes it; `AstcPackTests`
runs its commands on a stand-in for the phone, for both kinds of APK). With the phone connected (`adb`):

```sh
P=/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files
adb pull $P/McvsOffline mcvs-profile-backup                   # 1. your profile, settings and logs
#   to go back to ETC later, keep the ETC files/ on the computer (a mirror, or: adb pull $P mcvs-etc-files, 2 GB or
#   more), not on the phone: Clear storage and uninstalling delete everything in Android/data/<package>/
sha256sum -c SHA256SUMS.txt                                   # 2. the parts are intact
for z in mcvs-astc-hd-*.zip; do unzip -o "$z" -d astc-hd; done   #    -> astc-hd/files/ and astc-hd/README.txt
# 3. BUILD 11 AND OLDER ONLY: Android Settings > Apps > the game > Storage > Clear storage
adb shell am force-stop com.gameloft.android.ANMP.GloftMVHM   # 4. (after step 3 there is nothing left to remove)
adb shell rm -f $P/qaTestingConfigs.txt                       # SKIP_VALIDATION is not needed and must go
adb shell rm -rf $P/Data                                      # the ETC data with the manual HD files
adb shell rm -f $P/d_o_w_n_l_o_a_d_e_d.txt $P/pack.info \
                $P/McvsOffline/install/pack.info $P/McvsOffline/install/pack.info.txt \
                $P/McvsOffline/install/verify-pending $P/McvsOffline/install/damaged.txt
adb shell rm -rf $P/McvsOffline/hd $P/McvsOffline/hd-install  # an ETC HD pack and its installer state, if any
adb push astc-hd/files/. $P/                                  # 5.
# 6. BUILD 11 AND OLDER ONLY, before the first start: the profile back, without the ETC index (install/) and the ETC HD
#    state (hd/, hd-install/), which would replace the pushed ASTC index
for f in mcvs-profile-backup/*; do case "${f##*/}" in install|hd|hd-install) ;; *) adb push "$f" $P/McvsOffline/ ;; esac; done
```

If the game was started after Clear storage before step 6, it made a new, empty profile: force-stop it, run step 6 and
start it again. With build 11 and older the start-up log says `installer data: index restored from the saved index
(15747 files, ... entries)` (without `ASTC with ...`, which that build does not know) and the rest goes as below.

Then start the game. The start-up log (`Files > Downloads > MCVS-Offline > ...-bootstrap.log.txt`, logcat tag
`McvsOffline`) shows `[install] installer data: index switched to the saved index (ASTC with 2215 replaced files, 15747
files, ... entries)` (also as a `[boot]` milestone), then `installer data: wrote .../pack.info (... from the saved index),
.../gl_ver = 1173200, N split pieces in the journal, DATA_TYPE ASTC`, the installer screen shows briefly and the game
starts. A minute after the game has loaded: `installer data: checking the CRC32 of every file in the background (15747
files)`, and once it has read everything (low priority, a few minutes), `installer data: background check done, every
file matches the CRC32 ...`. Later starts say `installer data: Gameloft index in place (ASTC with 2215 replaced files,
...)`. The HD textures that depend on the GPU profile (normal maps, the lightmaps' direction maps) only show with
`graphics.profile` `high` or `ultra` (PLAYER-SETUP.md section 7).

Keep the whole `files/` folder in backups as before: its `McvsOffline/install/pack.info` and `replaced.tsv` are what lets
a reinstall accept the data offline (`verify.py` checks such a backup against the index it carries).

### Going back to ETC

Force-stop the game, delete `Data/`, `d_o_w_n_l_o_a_d_e_d.txt` and in `McvsOffline/install/` the files `pack.info`,
`pack.info.txt`, `replaced.tsv`, `astc-hd.json`, `verify-pending` and `damaged.txt`, then push the ETC `files/` tree with
its index (a mirror made with `tools/cdn-mirror/mirror.py --tex ETC --extract`, or your backup of the ETC phone's
`files/`). The next start logs `installer data: index switched to the saved index (ETC, 15747 files, 16218 entries)` and
`DATA_TYPE` is `ETC` again. With build 11 and older: pull `McvsOffline/` to a new folder, clear the app's storage, push the
ETC `files/` tree, then the pulled profile without its `install/`, `hd/` and `hd-install/` folders as in step 6; the
start then logs `installer data: index restored from the saved index (...)`. HD textures
for ETC then come as an ETC HD pack (`McvsOffline/hd/mcvs-hd-*.zip`, above), which needs no `SKIP_VALIDATION` either.

## Last resort: the installer's own test switches

A text file `files/qaTestingConfigs.txt` with one of these lines is read by the installer itself:

- `SKIP_VALIDATION=1`: the installer skips every check and starts the game (if `Data/` is incomplete the game then
  fails later). Remove the line again once the data is complete. HD textures do not need it (see above).
- `DATA_LINK=<url>`: download from that `.amz` URL instead of Gameloft's CDN. The URL's file name must contain `.amz`,
  the server must answer HTTP Range requests, and plain `http://` only works for `*.gameloft.com` and `127.0.0.1` (the
  game's network security config).

## How the game's installer decides (background)

From the installer's code (`com.gameloft.android.installer.GameInstaller` and `installer/utils/DownloadComponent`;
report 10 in `docs/research/` has the CDN side):

1. Its index of the data pack lives in **internal** storage, `/data/data/<package>/files/pack.info`, which a backup
   without root never contains, and Android wipes it on uninstall. Without it the installer cannot check anything: it
   fetches Gameloft's index from the CDN first, whatever is in `Data/`.
2. `files/prefs/gl_ver` (internal storage too) must say `1173200`; otherwise the installer deletes its index at once and
   runs a full CRC check online.
3. With the index, every file must exist with its exact size. Files over 2 MiB come in 2 MiB pieces, and each piece must
   also be listed in `files/d_o_w_n_l_o_a_d_e_d.txt` (the download journal, in the `files/` folder next to `Data/`, so a
   copy of `Data/` alone lacks it): **without the journal, every big file (about 100 of them) is downloaded again**.
   A file of the wrong size is deleted and downloaded again.
4. All of that holds: the game starts with no network. A week after the last check it asks the CDN for a newer version.

So a copied `Data/` folder on a fresh install always went to the CDN for the index and then re-downloaded every split
file. The installer sends four or five requests to the CDN's redirector within a fraction of a second at that point,
more than its rate limit allows; a refused request most likely made the download screen fail or stand still (no log of
the stalled run was available to confirm which request). The offline build's start-up code runs inside the game before the installer and puts points
1 to 3 in place from your files (the saved index, a mirror's index, or one rebuilt from a complete `Data/`), so none of
this needs the network. It never changes the installer itself.
