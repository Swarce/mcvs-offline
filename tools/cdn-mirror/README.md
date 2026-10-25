# mcvs-cdn-mirror

Mirror and verify the game data of **Modern Combat Versus** for Android
(`com.gameloft.android.ANMP.GloftMVHM`, APK 1.17.32 / versionCode 1173200)
while Gameloft's download CDN is still online.

Python 3.6 or newer, standard library only. Nothing needs installing.

## What the CDN serves

The APK contains no game data. On launch, the Java `GameInstaller` asks a
redirector which archive matches the device:

```
http://dl.gameloft.com/partners/androidmarket/d.cdn.php?model=<Build.MODEL>&device=<Build.DEVICE>
     &product=2620&version=1.17.32&compression=<ASTC|ETC>&portal=google_market
  -> 301 Location: http://droid06.gameloft.com/marketplace/.../google_lowres_v162074_Nexus_9_{ATC,ETC}.amz?fix=get
     x-gl-version / x-gl-size / x-gl-release / x-gl-generic headers
```

Each `.amz` file is one large archive: a 4-byte length, the `pack.info`
index, and then one small ZIP file per game file (big files are split into
2 MiB pieces). It holds the complete `Data/` tree (about 15.7k files, about
1.9 GiB unpacked). There are only two variants per game version, selected
by texture compression: **ASTC** for GPUs with
`GL_KHR_texture_compression_astc_ldr`, and **ETC** for everything else.
There is no per-language or per-resolution variant. Every device model
tested got the same two files.

Current version (checked live on 2026-10-24):

| version | tex  | bytes          | MD5 (= S3 ETag)                    |
|---------|------|----------------|------------------------------------|
| 1.17.32 | ASTC | 1,586,923,464  | `538f7382e577cd61bd79f757df3956a2` |
| 1.17.32 | ETC  | 1,519,733,632  | `80e63f5ce0eb81d9fc6a735fad421758` |

Older game versions are still on the CDN too. On 2026-10-24 a sweep of
1.0-17.0-40 found **121 versions / 241 builds**, from 1.0.6 to 1.17.32. The
results are in `versions.json`, and `KNOWN_VERSIONS` in `mirror.py` holds the
same list. All of them together add up to about 308 GiB (sum of `x-gl-size`;
the `--list` total counts each unique URL once). They are only useful together
with the matching old APK.

## Usage

```sh
# 1. See what would be mirrored. This downloads no game data: it reads the
#    redirector headers and sends a HEAD request for each archive.
python3 mirror.py --list
python3 mirror.py --list --files            # + every inner file (fetches the ~0.8 MB index of each archive)
python3 mirror.py --list --versions known --no-head   # all 121 known versions (redirector only, ~3 min because of the rate limit)

# 2. Mirror the current version, both texture formats (~2.9 GiB):
python3 mirror.py --out ./mcvs-mirror

#    Same command again = resume. Ctrl-C at any time is safe.
#    --concurrency 4 (default) parallel range requests, --chunk-mb 16
#    --extract also unpacks each archive into the on-device layout
#      (<out>/<ver>/<tex>/files/Data/...), ~2 GiB extra per archive

# 3. Older versions (large: about 1.1-1.6 GiB per archive, two archives per version, ~308 GiB total):
python3 mirror.py --out ./mcvs-mirror --versions known      # all 121 versions in KNOWN_VERSIONS / versions.json
python3 mirror.py --discover-versions --out ./mcvs-mirror   # optional re-sweep -> ./mcvs-mirror/versions.json (rate limited, ~15 min)
python3 mirror.py --out ./mcvs-mirror --versions all        # known + anything newly discovered
python3 mirror.py --out ./mcvs-mirror --versions 1.17.31,1.16.30 --tex ETC

# 4. Re-check a finished mirror later, offline:
python3 mirror.py --out ./mcvs-mirror --verify-only
```

Each finished archive is checked three ways. The size must match
Content-Length and `pack.info`. The MD5 must equal the S3 ETag. Every inner
ZIP member is unpacked and its size and CRC32 compared with `pack.info`
(`--no-deep-verify` skips this last check). Only a fully verified archive
gets a `*.verified` marker. An archive that fails is renamed `*.bad` and is
downloaded again on the next run. If two builds are byte-identical (same
ETag), the second one is hard-linked to the first instead of downloaded
again.

Output layout:

```
mcvs-mirror/
  index.json                       every build: redirector URL, archive URL, size, ETag, x-gl-* headers
  versions.json                    (after --discover-versions)
  1.17.32/ASTC/google_lowres_v162074_Nexus_9_ATC.amz      the archive, byte-identical to the CDN
  1.17.32/ASTC/pack.info           index, byte-identical to the file the game stores in /data/data/<pkg>/files/pack.info
  1.17.32/ASTC/manifest.json       parsed index (paths, sizes, CRC32s, offsets)
  1.17.32/ASTC/headers.txt         HTTP headers of the redirector answer and of the archive
  1.17.32/ETC/...
```

## Verifying a device backup

Copy the phone's data folder somewhere first, for example with
`adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files ./phone-files`.
On Android 11 and later you may need a file manager or `adb shell` with
the right permissions. Then run:

```sh
python3 verify.py ./phone-files                 # the index the backup carries (McvsOffline/install/pack.info), ASTC/ETC auto
python3 verify.py ./phone-files --report r.json
python3 verify.py ./phone-files --quick         # existence + size only
python3 verify.py ./phone-files --mirror ./mcvs-mirror   # use manifests from a mirror (e.g. older versions)
python3 verify.py ./phone-files --manifest pack.info     # the phone's own index (root: /data/data/<pkg>/files/pack.info)
python3 verify.py ./phone-files --fetch         # download the current index headers from the CDN
```

The checks are the ones the installer itself relies on. Every file in the
manifest must exist under `Data/` with the exact size, and its CRC32 must
match `pack.info`. For split files the CRC32 is checked for each 2 MiB
piece. Files under `Data/` that the manifest does not list are reported as
"extra" for information only. Exit status is 0 when the backup is complete
and intact.

The two live 1.17.32 indexes are Gameloft's data and are not in the repository. `verify.py` uses, in this
order: `--manifest`/`--mirror`/`--fetch`, the index the backup carries, then local copies in
`manifests/1.17.32/{ASTC,ETC}/pack.info` (put them there from a mirror if you want `verify.py` to work offline
on backups that carry no index; `.gitignore` keeps them out of commits).

## Restoring onto a device

Full guide for players: [docs/INSTALL-OFFLINE.md](../../docs/INSTALL-OFFLINE.md). In short:

* `mirror.py --out DIR --tex ASTC --extract` (or `--tex ETC`, whichever your phone uses) writes
  `DIR/1.17.32/<tex>/files/Data/...` **and** `files/McvsOffline/install/pack.info`, the archive's index.
* Copy that `files/` folder to `/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/` before the first
  launch of the offline build. Its start-up code (MCVS Offline's Bootstrap, which runs before the game's installer)
  checks every file against the index and hands the index to the installer together with the installer's other
  markers (`files/prefs/gl_ver`, the split-file journal, its preferences). The installer then starts the game
  without any network, and a background check compares every CRC32.
* The offline build also saves the installer's own index to `files/McvsOffline/install/pack.info` on every start,
  so a backup of the whole `files/` folder restores the same way. `verify.py` checks such a backup against the index
  it carries.
* The installer's own test switches in `files/qaTestingConfigs.txt` still work as a last resort:
  `SKIP_VALIDATION=1` (no checks at all) and `DATA_LINK=<url>` (another archive URL; cleartext HTTP only for
  `*.gameloft.com` and `127.0.0.1`, the name must contain `.amz`, Range requests required).

## Caveats

* `dl.gameloft.com` is behind a Varnish rate limiter. Bursts get
  `429 Too Many Requests` with `Retry-After: 5`. The tool keeps at least
  0.5 s between redirector requests and honours Retry-After. Do not lower
  this.
* The archive host `droid06.gameloft.com` (CloudFront in front of S3)
  speaks plain HTTP only, because its TLS certificate does not match the
  name. This is also what the game uses.
* Proxies: `$HTTPS_PROXY`/`$HTTP_PROXY` are honoured. By default `http://`
  targets are reached through a CONNECT tunnel. Use `--proxy-mode forward`
  if your proxy only allows CONNECT to port 443.
* Older builds can differ from current ones in layout. 1.4.17 uses 32-bit
  offsets in `pack.info` and a flat `Data/`. `packinfo.py` auto-detects the
  offset width and rejects anything it does not understand instead of
  guessing. The layout has been checked on 1.17.32 (both textures), 1.16.30,
  1.10.3, 1.5.9 and 1.4.17 (ETC).
* 1.0.6 redirects to a 10-byte `dummy.txt` placeholder, which is mirrored
  verbatim.
* Every build is a distinct object. Same-size hotfix builds such as
  1.17.31 and 1.17.32 still differ in content, so all ~308 GiB of old builds
  are really unique data.
* Runtime assets that are *not* on this CDN, such as event banners, store
  art, inbox attachments, `GameOptions` and `tech_<lang>.json` localisation
  patches, come from Gameloft's authenticated "Iris" asset service, which
  is found through eve/pandora. This tool does not mirror them. See the
  report (`reports/10-cdn-download-pipeline.md`, section 5).
