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

## Last resort: the installer's own test switches

A text file `files/qaTestingConfigs.txt` with one of these lines is read by the installer itself:

- `SKIP_VALIDATION=1`: the installer skips every check and starts the game (if `Data/` is incomplete the game then
  fails later). Remove the line again once the data is complete.
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
