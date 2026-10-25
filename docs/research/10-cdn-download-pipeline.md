# 10: CDN download pipeline and full-content mirror (MC Versus 1.17.32, Android)

Scope: how `com.gameloft.android.ANMP.GloftMVHM` 1.17.32 (versionCode 1173200)
gets its game data, what the CDN serves, how to mirror all of it, and how to
check a device backup against it.

All paths are relative to `/home/user/mcvs-work`. Abbreviations:

* `GI` = `decompiled-java/sources/com/gameloft/android/installer/GameInstaller.java`
* `IU/` = `decompiled-java/sources/com/gameloft/android/installer/utils/`
* `GE/` = `decompiled/Gameloft.Engine/Gameloft.Engine/`
* `GM/` = `decompiled/Game/Game/`
* `OP/` = `decompiled/Gameloft.OnlinePlatform/Gameloft.OnlinePlatform/`
* Java files cited by bare name live here:
  * `SUtils.java`, `GPUInfo.java`, `XPlayer.java`: `decompiled-java/sources/com/gameloft/android/GLUtils/`
  * `AndroidUtils.java`, `AndroidUtilsPlugin.java`: `decompiled-java/sources/com/gameloft/android/PackageUtils/`
  * `Utils.java`: `decompiled-java/sources/com/gameloft/android/installer/`

Evidence tags:

* **[V]** verified, either in code or against the live CDN on 2026-10-24.
* **[I]** inference.

Live captures are in `captures/cdn/`. Tools are in `tools/cdn-mirror/`.

---

## 0. Summary

* **Who downloads:** only the Java `GameInstaller` activity, and it runs on every
  launch **[V]**.
  * On a fresh install it downloads the whole data set.
  * On later launches it re-validates the data, and about once a week it asks
    the CDN for a newer version.
  * The engine's native TOC/DLC system is compiled in but **disabled**
    (`res/raw/args.txt` has no `-UseDlc`). The managed `DownloadableContent` /
    `PackManager` path is dead code **[V]**.
* **Hosts:** two CDN hosts, both alive **[V]**. A fallback hard-coded in
  `res/raw/data.txt` reaches them without eve.
  * Redirector: `dl.gameloft.com`. Rate-limited by Varnish; works over https and http.
  * Data: `droid06.gameloft.com`. CloudFront in front of S3; **http only**,
    because the TLS certificate does not match the name.
* **URL scheme** **[V]**:
  ```
  http(s)://dl.gameloft.com/partners/androidmarket/d.cdn.php?model=<Build.MODEL>&device=<Build.DEVICE>
         &product=2620&version=1.17.32&compression=<ASTC|ETC>&portal=google_market[&head=1]
    -> 301 Location: http://droid06.gameloft.com/marketplace/3484/3484/<build>/<release>/google_lowres_v162074_Nexus_9_<ATC|ETC>.amz?fix=get
       x-gl-version, x-gl-size, x-gl-release, x-gl-generic
  ```
* **Content:** one `.amz` archive per (version, texture format). Its layout
  is `[int32 len][pack.info][one ZIP per file …]`.
  * `pack.info` lists every file with its offset, size and CRC32. Files over
    2 MiB are split into 2 MiB pieces.
  * Variants: **ASTC** or **ETC** only. The installer picks ASTC when the GPU
    reports `GL_KHR_texture_compression_astc_ldr`. Device model, language and
    resolution do not change what the redirector serves.
* **Current version 1.17.32** **[V]**:
  * ASTC archive: 1,586,923,464 B.
  * ETC archive: 1,519,733,632 B.
  * Together: **2 archives, 3,106,657,096 B (2.89 GiB)**, holding 2 × 15,747
    files (3.80 GiB unpacked).
  * The archive MD5 equals the S3 ETag, and every per-file CRC32 in `pack.info`
    is checkable.
* **Older versions:** a header-only sweep found **121 versions / 241 builds**
  still offered, from 1.0.6 to 1.17.32. Their `x-gl-size` values sum to about
  308 GiB (§3.6) **[V]**.
* **On the device:** data goes to
  `/sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data/…`. The
  installer's copy of the index goes to
  `/data/data/com.gameloft.android.ANMP.GloftMVHM/files/pack.info` **[V]**.
* **Dependence on dead APIs:** the main data path does not need eve, pandora or
  federation.
  * eve is consulted first. When it fails, the installer falls back to the
    hard-coded `dl.gameloft.com` redirector.
  * Runtime extras come from the Iris asset service, and the host for that is
    only discoverable through eve/pandora. These extras are `GameOptions`,
    `tech_<lang>.json`, and event, store and inbox images (§5).

---

## 1. Who drives the download, and when

### 1.1 Call chain (active path)

```
XamarinMainActivity (launcher, decompiled-java/resources/AndroidManifest.xml:33-46) extends MainActivity
 └─ MainActivity.onResume()      decompiled-java/sources/com/gameloft/android/MainActivity.java:127-140
     └─ startActivityForResult(GameInstaller)        MainActivity.java:134   (every app start, before Mono/engine)
         GameInstaller.onCreate                      GI:1913-1999
           SaveFolder = getFilesDir()                GI:1923 (SUtils.java:435-441)
           sd_folder  = pref "SDFolder" or C()       GI:1927-1933  -> getExternalFilesDir(null)  (GI:2040-2120)
           DATA_TYPE pref or GPUInfo probe           GI:1944-1947, GPUInfo.java:49
         GameInstaller.run() state machine           GI:702-800, n() GI:802-1220
           state 2: t = D()  (build d.cdn.php URL)   GI:1080 -> D() GI:2135-2236
                    new DownloadComponent(false,t,"")  GI:1081
                    I(): parse saved pack.info        GI:3130-3145 -> IU/DownloadComponent.java:159-167 -> IU/i.java
                    h(): which files are missing/bad  GI:497-563 -> IU/DownloadComponent.java:409-493
           state 12/o(): J() -> DownloadComponent.m()  GI:1229-1262, IU/DownloadComponent.java:169-266
                    follows the 301, reads Content-Length, downloads the header
                    (readInt len + len bytes) into SaveFolder/pack.info
           download: Downloader d -> Section threads  IU/d.java:42-98, IU/Section.java:109-261
                    HTTP Range "bytes=<4+len+offset>-" IU/HttpClient.java:213-230
                    unzip each blob into DATA_PATH/<folder>/<name>, split pieces at (n-1)*2 MiB
           state 23: DownloadGO(pandora, clientId) (native, GameOptions) GI:1517-1530
                     weekly update check (InsTime >= 604800 s)       GI:1531-1567
           state 24/25: GET t+"&head=1", compare x-gl-version with pref CurrentVersion  GI:1572-1625, IU/DownloadComponent.java:495-529
         setResult(1) -> MainActivity starts the game (Mono/libnativeengine)
```

### 1.2 When

* **First launch.** No saved `pack.info` exists, so the installer fetches the
  index and then every file it lists, about 1.5 GB **[V]**.
* **Every launch.** The installer re-parses the saved `pack.info` and checks
  each file under `DATA_PATH` **[V]**.
  * Every file must exist with the right size: `IU/DownloadComponent.java:349-358`
    for single files, and `goodSize` at `:286-301` for split files.
  * CRC32 is also checked when `ZipHasCRCtest` is set. That happens after an
    APK update, because `prefs/gl_ver` != `1173200` (GI:588-595), and in
    state 20 (`y=true`).
  * Missing or bad files are downloaded again on their own, using ranges.
* **About once a week.** If `DATA_PATH/InsTime` is 7 days old or more
  (GI:1531-1567), the installer GETs `t + "&head=1"` and compares
  `x-gl-version` (dots stripped, e.g. `11732`) with the stored
  `CurrentVersion` preference. If the server version is newer, it deletes
  `shader_info_cache/` and re-syncs (GI:1595-1625). **[V code; jadx marks
  `r()` as "decompiled incorrectly", so the exact transitions are I]**
* **On demand at runtime.** Iris assets (§5), cached under the engine cache
  directory **[V]**.
* **Not used:**
  * Play OBB: `AndroidUtils.RetrieveObbPath()` returns `"NA"`
    (`decompiled-java/sources/com/gameloft/android/PackageUtils/AndroidUtils.java:46-48`),
    and `AndroidUtilsPlugin.onPluginStart` deletes
    `/sdcard/Android/obb/com.gameloft.android.ANMP.GloftMVHM`
    (`.../PackageUtils/AndroidUtilsPlugin.java:100`) **[V]**.
  * Native DLC (TOC) system:
    * `GM/GameClientComponent.cs:2996-3046` (`HandleDlc`, called from the
      game-server handshake at `:3128`) returns at once unless `Args.UseDlc`.
    * `UseDlc` comes from `AndroidUtilsPlugin.getUseDlc()`, which is true only
      if `res/raw/args.txt` contains a `-UseDlc` line
      (`AndroidUtilsPlugin.java:120-140`, `GE/GameApplication.cs:656,1163`).
    * The shipped `decompiled-java/resources/res/raw/args.txt` is just
      `-LogCategoryFilter Fatal`, so the system is **disabled** **[V]**.
    * Even when enabled, it would need two things that are gone with the
      servers: the TOC revision from the game-server handshake
      (`ServerDlcSlot`, `GE/Connection.cs:962`), and an `asset`-scoped
      federation access token (`GE/DlcComponent.cs:178`).
    * Native strings show what it would fetch: `toc.zip`, `_toc.zip`,
      `toc.json`, and ranged `bytes=%d-%d` reads from `archives`.
  * Managed `DownloadableContent` / `PackManager`:
    * `GE/DownloadableContent.cs:128` (`UpdateServerFilesListAsync`) has no
      caller in any assembly. It is the only thing that sets `m_tag` and the
      server file list.
    * `Download()` / `DownloadAsync()` therefore never build a URL
      (`GE/DownloadableContent.cs:246-281`, `:320-349`). They return without
      network I/O, so this path is dead code **[V]**.
    * Whether a (hashed) `fileList.json` exists in the data cannot be told
      from the obfuscated names, and it does not matter.
    * If it were ever used, it would fetch
      `https://<pandora locate/asset>/assets/<clientId>/<tag>/<path>`
      (`:401-416`, `:265`).

---

## 2. CDN hosts and how the URL is obtained

### 2.1 Building the URL: `GameInstaller.D()` (GI:2135-2236)

1. `compression` is `ASTC` if the GL extension string contains
   `GL_KHR_texture_compression_astc_ldr`, otherwise `ETC` (GI:2139-2146,
   `GPUInfo.java:49`). The choice is persisted in the `DATA_TYPE` preference.
2. If `qaTestingConfigs.txt` in the SD data folder has `DATA_LINK=<url>`, that
   URL is used as-is (GI:2148-2152; file format in `SUtils.java:275-296`).
3. Otherwise the installer looks up `ecomm_api_root` in the shared-data silo
   (`<game>_SELECTED_ECOMM_API_ROOT`, GI:2156). Failing that, it asks eve,
   which is a **dead/third-party dependency**. eve is asked only while the
   `siloCheckCount` preference is below 2 (GI:2157-2159), so after two failed
   launches the installer stops trying:
   * `https://eve.gameloft.com:443/config/2620:66102:1.17.32:android:googleplay/datacenters`
     and pick the preferred datacenter (`GLUtils/XPlayer.java:160`, GI:1700-1755);
   * `…/datacenters/<dc>/urls` and take `ecomm_api_root` and `pandora`
     (`XPlayer.java:195`, GI:1757-1790).
   * The resulting URL is `<ecomm_api_root>/dl/androidmarket/d.cdn.php?…`
     (GI:2170). A 301 Location is read by hand because HttpURLConnection does
     not follow https→http (GI:2174-2196).
4. **If eve gives nothing**, the installer falls back to
   `res/raw/data.txt` = `DYNAMIC:http://dl.gameloft.com/partners/androidmarket/d.cdn.php`
   (GI:2214-2224, `decompiled-java/resources/res/raw/data.txt:1`).
   jadx flags `D()` as "decompiled incorrectly". The fallback string and the
   code that builds the URL from it are verified, but the exact branch that
   reaches it when eve fails is **[I]**.
5. Query string, appended in all cases (GI:2224):
   `?model=<Build.MODEL>&device=<Build.DEVICE>&product=2620&version=1.17.32&compression=<ASTC|ETC>&portal=google_market`.
   `portal` is hard-coded as `m_portalCode = "google_market"` (GI:1952).

The eve step is therefore optional. With eve dead, the installer uses the
`dl.gameloft.com` redirector directly. The live tests below show that the
redirector works on both `https://` and `http://` **[V]**.

Cleartext HTTP is allowed for `gameloft.com` and subdomains, and for
`127.0.0.1` (`decompiled-java/resources/res/xml/network_security_config.xml:3-8`).
That is why the plain-http data host works.

### 2.2 Redirector response (captured)

`captures/cdn/01_dcdn_astc.headers` (https; the http answer `52_dcdn_http_astc.headers` is identical apart from server headers):

```
HTTP/2 301
location: http://droid06.gameloft.com/marketplace/3484/3484/6912994/136808/google_lowres_v162074_Nexus_9_ATC.amz?fix=get
x-gl-generic: yes
x-gl-version: 1.17.32
x-gl-size: 1586923464
x-gl-release: 136808
```

* `&head=1` returns `200` with the same `x-gl-*` headers and no Location
  (`05_dcdn_astc_head.headers`). The installer uses this form for its update
  check.
* An unknown version or compression returns `200` with `x-gl-size: 0` and no
  other headers (`32-36_*`, `40_*`).
* **Rate limit:** a Varnish limiter answers bursts of more than about 2
  requests per second with
  `429 Too Many Requests` and `Retry-After: 5` (`53_dcdn_429_ratelimit.*`).
  Do not mistake a 429 for "not found". The mirror tool keeps at least 0.5 s
  between redirector calls and honours Retry-After.

### 2.3 Data host

`captures/cdn/02_amz_astc_head.headers`:

```
HTTP/1.1 200 OK
Content-Length: 1586923464
Accept-Ranges: bytes
Content-Type: binary/octet-stream
Etag: "538f7382e577cd61bd79f757df3956a2"
Last-Modified: Wed, 17 Mar 2021 15:50:25 GMT
Via: 1.1 ….cloudfront.net (CloudFront)
X-Amz-Meta-S3cmd-Attrs: …/md5:538f7382e577cd61bd79f757df3956a2/…
```

* `https://droid06.gameloft.com` fails TLS verification ("no alternative
  certificate subject name matches"). Plain `http://` works, and it is what
  the game uses **[V]**.
* The ETag is the MD5 of the whole object: it equals the `md5:` in the s3cmd
  metadata. This gives an end-to-end integrity check **[V]**.
* Byte ranges are supported (`206 Partial Content`, `03_*`, `08_*`) **[V]**.

### 2.4 Variant probing (all captured in `captures/cdn/10_*`-`31_*`)

**Device model.** Eleven model/device pairs were tested: Galaxy S10, Nexus 9,
Pixel, Galaxy S7, Tab S3, J2, S3, Redmi Note 8, A50, Pixel 6 and SHIELD TV.
All got
the **same two archives**. Only `x-gl-generic` changes (`no` for Pixel, `yes`
for the others).

**compression values:**

| value                                  | result                                |
|----------------------------------------|---------------------------------------|
| `ASTC`                                 | release 136808, 1,586,923,464 B       |
| `ETC`                                  | release 136814, 1,519,733,632 B       |
| `ATC`, `PVRTC`, `DXT`, `ETC2`, `NONE`  | not offered                           |

**Not request parameters at all:**

* Language: all 16 languages ship inside every build
  (`GE/LanguageInfo.cs`: en fr de it es ja ko zh pt ru tr ar th id vi zt).
* Resolution: the only build name seen is `google_lowres_v162074_Nexus_9_*`.

---

## 3. Manifest and archive format

### 3.1 `.amz` layout **[V on both live 1.17.32 archives]**

| offset           | content                                                                 |
|------------------|-------------------------------------------------------------------------|
| 0                | `int32 BE header_len` (ASTC 822,138; ETC 822,750)                       |
| 4                | `pack.info` (`header_len` bytes)                                        |
| 4+header_len     | data region: one **standalone ZIP file per entry** (local header, deflated member, central directory, EOCD), back to back with no gaps |

For both archives, `4 + header_len + sum(zsize)` equals `Content-Length`
exactly (ASTC: 4 + 822,138 + 1,586,101,322 = 1,586,923,464).

### 3.2 `pack.info`

The encoding is Java `DataOutputStream`: big-endian, and `UTF` means a `u16`
length followed by modified UTF-8. The parser is `IU/i.java:27-123`
(PackFileReader), and the entry fields are in `IU/h.java`. The structure
repeats until EOF:

```
UTF  s                         "version: 101"
int32 split_kb                 2048  (=> 2 MiB split size; matches 2097152 hard-coded in Section/CRC/goodSize)
UTF  folder                    ".\\Data\\1"      (if s did not start with "version: ", s itself is the folder)
int32 count
count x {
  int64 offset                 relative to data region (int32 in old archives such as 1.4.17, see 3.6)
  int32 size                   uncompressed size of this piece
  int64 crc32                  CRC32 of the uncompressed piece
  UTF   name                   "0018344.f" | "0084423.f.split_0001"
  [int64 total_size]           only if version>=101 and name ends ".split_0001"
  int32 zsize                  bytes of the ZIP blob
  UTF   zname                  name + ".zip"
}
```

**Folder mapping.** On the device, the folder becomes
`folder.replace(".\\\\","").replace(".\\","").replace("\\","/")`. The file
then lives at `DATA_PATH + that + "/" + name`, with the `.split_NNNN` suffix
removed (`IU/DownloadComponent.java:338-346`, `IU/Section.java:189-205`).

* Files whose name ends in `.so` would go to `<filesDir>/libs/`. Folders whose
  name starts with `main`/`patch` would go to the OBB path. Neither occurs in
  1.17.32.
* Split files: piece *n* is written at `(n-1)*2 MiB`, and the file is
  truncated to `total_size` (`IU/Section.java:283-315`, `:232-233`).

**Real example: the first entries of the 1.17.32 ASTC index.** Decoded from
`captures/cdn/08_astc_header.bin` with `tools/cdn-mirror/packinfo.py`:

```
section "version: 101" split_kb=2048 folder ".\"            count 0
section "version: 101" split_kb=2048 folder ".\\Data"       count 1
  off=0        size=790      crc=c788324b  MCVS.game              zsize=831      MCVS.game.zip
section "version: 101" split_kb=2048 folder ".\\Data\1"     count 1072
  off=831      size=979      crc=9c4c3163  0018344.f              zsize=1018
  off=1849     size=4893     crc=a002661e  001ae8e.f              zsize=4168
  off=9884     size=2097152  crc=e3de7cef  0084423.f.split_0001   total=2397704  zsize=1640434
  off=1650318  size=300552   crc=c8c63073  0084423.f.split_0002   zsize=242896
  ...
section ".\\Data\Config" count 1:  EngineConfig.json  size=207 crc=6bef14bf
```

These were spot-checked against live data by range-downloading and inflating
the blobs (`captures/cdn/10_*`, `11_*`, `12_*`):

* The ZIP members inflate to exactly `size` bytes, and their CRC32 equals the
  `pack.info` CRC for `MCVS.game`, `EngineConfig.json` and both pieces of
  `0084423.f`.
* `MCVS.game` starts with `GCBF`, the engine's container format.
* `EngineConfig.json` is plain JSON (`{"glf": {"consoleOutputEnabled": false, …}}`).
* The reassembled `0084423.f` starts with `BRES`.

### 3.3 Totals (1.17.32) **[V]**

| | ASTC | ETC |
|---|---|---|
| archive bytes | 1,586,923,464 | 1,519,733,632 |
| MD5 / ETag | 538f7382e577cd61bd79f757df3956a2 | 80e63f5ce0eb81d9fc6a735fad421758 |
| Last-Modified | 2021-03-17 15:50:25 GMT | 2021-03-17 16:09:14 GMT |
| x-gl-release | 136808 | 136814 |
| zip entries / logical files | 16,212 / 15,747 | 16,218 / 15,747 |
| split files (pieces) | 95 (560) | 101 (572) |
| unpacked bytes | 2,042,882,427 | 2,036,819,797 |
| largest file | `Data/a/4309cd.f` 164,606,228 B (79 pieces) | same |

**Layout.** In both variants, `Data/` contains:

* `MCVS.game`
* `Config/EngineConfig.json`
* 15 hash buckets `Data/{1..9,a..f}/` (there is no `0`), holding about 980–1,140 files each.
  File names are hashes like `0018344.f`, because the game uses an
  obfuscated file system (`GE/GameDefinitionComponent.cs`:
  `UseObfuscatedFileSystem = true`).

**ASTC vs ETC.** Both variants have **exactly the same 15,747 paths**. 6,360
files are byte-identical (same CRCs) and 9,387 differ, presumably the
textures.

### 3.4 Variant selection logic (client)

* `compression` is the only real dimension: `ASTC` if
  `glGetString(GL_EXTENSIONS)` contains `GL_KHR_texture_compression_astc_ldr`,
  else `ETC` (GI:2139-2146).
* The choice is made once and kept in the `MainActivityPrefs` key `DATA_TYPE`.
* `model` and `device` are sent, but the server returns the same archive for
  every device tested.

### 3.5 Other archive types the code supports (not served for this game)

`DownloadComponent.m()` (IU/DownloadComponent.java:225-262) treats any final
URL whose name contains `.amz` or `.jar` as an indexed archive.

For any other file it writes a fake one-entry `pack.info`, and `SimpleDownload`
then fetches it as a plain zip. This path is not used by 1.17.32 **[V: the
live redirect points to an `.amz`]**.

### 3.6 Older versions still on the CDN **[V, 2026-10-24]**

`mirror.py --discover-versions` swept `1.0-17.0-40` × {ASTC, ETC}. This sent
1,476 header-only requests at 0.6 s or more apart; no 429s were hit. The
results are in `tools/cdn-mirror/versions.json`, and the table is below
(sizes from `x-gl-size`).

**121 versions / 241 builds** (every version has both ASTC and ETC except 1.5.9, which is ETC-only). Sum of `x-gl-size` over all builds: **330,389,957,077 B (307.7 GiB)**. All 241 URLs point to different S3 objects (see notes below).

<details><summary>Full table</summary>

| version | ASTC release | ASTC bytes | ETC release | ETC bytes |
|---|---|---|---|---|
| 1.17.32 | 136808 | 1,586,923,464 | 136814 | 1,519,733,632 |
| 1.17.31 | 136798 | 1,586,923,464 | 136800 | 1,519,733,632 |
| 1.17.30 | 136716 | 1,586,923,464 | 136718 | 1,519,733,632 |
| 1.17.21 | 136203 | 1,586,527,280 | 136205 | 1,519,500,191 |
| 1.17.20 | 136159 | 1,586,525,886 | 136161 | 1,519,498,753 |
| 1.17.15 | 135843 | 1,583,225,459 | 135845 | 1,516,208,054 |
| 1.17.12 | 135823 | 1,583,225,460 | 135825 | 1,516,208,055 |
| 1.17.11 | 135803 | 1,583,225,460 | 135805 | 1,516,208,055 |
| 1.17.10 | 135773 | 1,583,225,459 | 135775 | 1,516,208,054 |
| 1.17.8 | 135741 | 1,560,610,102 | 135743 | 1,495,621,208 |
| 1.17.7 | 135715 | 1,560,610,102 | 135717 | 1,495,621,206 |
| 1.17.6 | 135695 | 1,560,610,104 | 135697 | 1,495,621,208 |
| 1.17.5 | 135679 | 1,560,610,108 | 135681 | 1,495,621,211 |
| 1.17.4 | 134167 | 1,560,610,107 | 134165 | 1,495,621,210 |
| 1.17.3 | 134157 | 1,560,737,165 | 134159 | 1,495,286,421 |
| 1.17.2 | 134093 | 1,560,193,411 | 134095 | 1,495,415,041 |
| 1.16.32 | 133799 | 1,557,497,645 | 133801 | 1,493,280,273 |
| 1.16.31 | 133731 | 1,557,497,641 | 133725 | 1,493,280,264 |
| 1.16.30 | 133645 | 1,557,497,638 | 133643 | 1,493,280,264 |
| 1.16.22 | 133121 | 1,538,440,394 | 133123 | 1,475,557,696 |
| 1.16.21 | 133085 | 1,538,440,395 | 133087 | 1,475,557,694 |
| 1.16.20 | 133029 | 1,538,440,398 | 133037 | 1,475,557,697 |
| 1.16.10 | 132555 | 1,511,817,678 | 132557 | 1,451,094,524 |
| 1.16.7 | 132415 | 1,511,939,200 | 132417 | 1,451,215,352 |
| 1.16.6 | 132401 | 1,511,888,104 | 132403 | 1,451,163,784 |
| 1.16.4 | 132299 | 1,511,855,550 | 132301 | 1,451,131,962 |
| 1.15.22 | 132059 | 1,510,960,857 | 132061 | 1,450,231,334 |
| 1.15.21 | 132019 | 1,511,178,147 | 132017 | 1,450,534,764 |
| 1.15.20 | 131657 | 1,491,073,121 | 131655 | 1,432,174,604 |
| 1.15.11 | 131207 | 1,474,371,173 | 131209 | 1,416,316,386 |
| 1.15.10 | 131165 | 1,474,349,173 | 131163 | 1,416,292,748 |
| 1.15.8 | 130902 | 1,471,442,501 | 130904 | 1,413,586,296 |
| 1.15.7 | 130896 | 1,471,442,440 | 130900 | 1,413,586,237 |
| 1.15.6 | 130868 | 1,471,444,900 | 130870 | 1,413,588,700 |
| 1.15.5 | 130854 | 1,471,427,823 | 130856 | 1,413,571,617 |
| 1.15.1 | 130555 | 1,466,815,725 | 130557 | 1,409,346,139 |
| 1.15.0 | 130527 | 1,466,820,059 | 130529 | 1,409,359,857 |
| 1.14.4 | 130075 | 1,462,200,522 | 130077 | 1,405,403,314 |
| 1.14.1 | 129743 | 1,460,123,427 | 129745 | 1,403,367,898 |
| 1.14.0 | 129717 | 1,460,123,410 | 129719 | 1,403,367,882 |
| 1.13.6 | 128335 | 1,457,365,809 | 128337 | 1,400,733,025 |
| 1.13.5 | 128287 | 1,457,365,748 | 128289 | 1,400,732,970 |
| 1.13.4 | 127927 | 1,456,826,747 | 127925 | 1,400,232,670 |
| 1.13.3 | 127865 | 1,456,765,538 | 127867 | 1,400,171,228 |
| 1.13.2 | 127809 | 1,456,998,886 | 127811 | 1,400,404,577 |
| 1.13.1 | 127721 | 1,454,240,546 | 127723 | 1,397,646,302 |
| 1.13.0 | 127523 | 1,450,457,282 | 127525 | 1,394,703,622 |
| 1.12.3 | 126330 | 1,396,024,911 | 126332 | 1,344,137,292 |
| 1.12.2 | 126130 | 1,396,024,907 | 126128 | 1,344,137,284 |
| 1.12.1 | 125904 | 1,396,024,754 | 125898 | 1,344,137,130 |
| 1.12.0 | 125820 | 1,396,024,803 | 125818 | 1,344,152,826 |
| 1.11.1 | 125630 | 1,395,805,173 | 125632 | 1,343,728,707 |
| 1.11.0 | 125588 | 1,394,838,156 | 125590 | 1,342,522,214 |
| 1.10.25 | 125138 | 1,389,625,698 | 125140 | 1,338,128,339 |
| 1.10.24 | 124940 | 1,389,490,035 | 124942 | 1,338,073,346 |
| 1.10.22 | 124876 | 1,389,456,265 | 124878 | 1,338,039,575 |
| 1.10.21 | 124814 | 1,392,764,111 | 124816 | 1,341,592,190 |
| 1.10.20 | 124712 | 1,392,069,806 | 124714 | 1,340,958,929 |
| 1.10.18 | 124641 | 1,392,064,563 | 124643 | 1,340,951,685 |
| 1.10.17 | 124593 | 1,392,043,951 | 124595 | 1,340,940,239 |
| 1.10.16 | 124569 | 1,392,043,838 | 124571 | 1,340,940,208 |
| 1.10.15 | 124535 | 1,392,019,187 | 124537 | 1,340,915,701 |
| 1.10.14 | 124525 | 1,390,802,429 | 124527 | 1,340,067,894 |
| 1.10.12 | 124491 | 1,362,553,564 | 124493 | 1,312,053,193 |
| 1.10.11 | 124429 | 1,361,291,843 | 124435 | 1,310,955,707 |
| 1.10.10 | 124401 | 1,359,449,150 | 124403 | 1,308,951,152 |
| 1.10.9 | 124329 | 1,358,798,841 | 124331 | 1,308,758,932 |
| 1.10.8 | 124309 | 1,358,706,825 | 124311 | 1,308,737,353 |
| 1.10.7 | 124255 | 1,388,544,404 | 124257 | 1,334,485,231 |
| 1.10.6 | 124155 | 1,388,228,782 | 124163 | 1,334,246,511 |
| 1.10.4 | 124121 | 1,415,378,111 | 124123 | 1,358,717,484 |
| 1.10.3 | 123965 | 1,412,487,286 | 123967 | 1,355,817,453 |
| 1.10.2 | 123721 | 1,394,556,120 | 123723 | 1,337,376,770 |
| 1.10.1 | 123603 | 1,406,654,069 | 123605 | 1,342,901,265 |
| 1.9.8 | 123467 | 1,300,855,128 | 123465 | 1,247,765,388 |
| 1.9.7 | 123455 | 1,300,829,140 | 123457 | 1,247,739,594 |
| 1.9.6 | 123331 | 1,300,797,422 | 123333 | 1,247,702,755 |
| 1.9.5 | 123175 | 1,300,798,440 | 123177 | 1,247,703,993 |
| 1.9.4 | 123098 | 1,298,438,711 | 123100 | 1,245,235,990 |
| 1.9.3 | 122954 | 1,291,398,911 | 122956 | 1,238,346,751 |
| 1.9.2 | 122775 | 1,308,435,390 | 122777 | 1,255,927,729 |
| 1.9.1 | 122735 | 1,309,431,637 | 122737 | 1,256,980,729 |
| 1.9.0 | 122687 | 1,293,306,476 | 122689 | 1,240,690,716 |
| 1.8.4 | 122385 | 1,293,057,333 | 122387 | 1,240,014,945 |
| 1.8.2 | 122263 | 1,280,490,498 | 122381 | 1,240,014,945 |
| 1.8.1 | 122173 | 1,272,651,407 | 122175 | 1,219,063,821 |
| 1.7.9 | 122313 | 1,230,438,773 | 122315 | 1,177,689,287 |
| 1.7.8 | 122019 | 1,228,313,158 | 122021 | 1,175,563,788 |
| 1.7.7 | 121979 | 1,228,361,340 | 121977 | 1,175,615,119 |
| 1.7.6 | 121915 | 1,298,979,157 | 121913 | 1,170,047,085 |
| 1.7.5 | 121895 | 1,220,630,387 | 121893 | 1,167,646,989 |
| 1.7.4 | 121839 | 1,220,078,342 | 121837 | 1,166,974,772 |
| 1.7.2 | 121777 | 1,215,600,115 | 121773 | 1,163,212,782 |
| 1.7.1 | 121669 | 1,205,638,444 | 121671 | 1,158,287,622 |
| 1.7.0 | 121569 | 1,205,637,521 | 121567 | 1,153,342,024 |
| 1.6.7 | 121609 | 1,201,377,419 | 121593 | 1,149,474,408 |
| 1.6.6 | 121377 | 1,201,504,739 | 121375 | 1,149,581,438 |
| 1.6.5 | 121343 | 1,201,504,742 | 121345 | 1,149,581,442 |
| 1.6.4 | 121305 | 1,201,504,354 | 121303 | 1,149,581,060 |
| 1.6.2 | 121251 | 1,220,694,941 | 121249 | 1,168,386,493 |
| 1.6.0 | 121247 | 1,199,804,108 | 121245 | 1,147,742,432 |
| 1.5.19 | 121203 | 1,195,716,192 | 121201 | 1,146,413,862 |
| 1.5.17 | 120977 | 1,195,839,957 | 120979 | 1,146,515,895 |
| 1.5.16 | 120873 | 1,194,614,749 | 120875 | 1,145,333,113 |
| 1.5.15 | 120815 | 1,182,866,865 | 120817 | 1,133,573,369 |
| 1.5.12 | 120761 | 1,177,455,325 | 120763 | 1,127,810,584 |
| 1.5.11 | 120735 | 1,184,944,235 | 120701 | 1,135,428,399 |
| 1.5.10 | 120635 | 1,183,568,999 | 120637 | 1,133,896,784 |
| 1.5.9 | - | - | 120583 | 1,125,842,649 |
| 1.5.7 | 120509 | 1,180,536,892 | 120507 | 1,130,858,480 |
| 1.5.6 | 120437 | 1,109,774,242 | 120435 | 1,058,970,665 |
| 1.5.5 | 120393 | 1,623,918,017 | 120361 | 1,572,571,714 |
| 1.5.4 | 120327 | 1,650,528,772 | 120329 | 1,602,376,595 |
| 1.5.3 | 120275 | 1,650,741,841 | 120277 | 1,603,264,255 |
| 1.4.23 | 120425 | 1,624,472,200 | 120427 | 1,578,794,310 |
| 1.4.22 | 120137 | 1,620,912,813 | 120139 | 1,575,420,398 |
| 1.4.21 | 120127 | 1,620,905,803 | 120129 | 1,575,413,393 |
| 1.4.20 | 120097 | 1,620,905,778 | 120095 | 1,575,413,366 |
| 1.4.18 | 120007 | 1,620,878,714 | 120005 | 1,575,392,659 |
| 1.4.17 | 119979 | 1,613,901,145 | 119981 | 1,575,389,491 |
| 1.0.6 | 117510 | 10 | 117504 | 10 |

</details>

Notes:

* `1.0.6` redirects to `…/dummy.txt` / `…/dummy_2.txt`, 10-byte placeholders
  (`62_list_all_known_versions.txt`, `63_head_dummy_2.txt.headers`). `mirror.py`
  stores such non-`.amz` files verbatim.
* Each build is a separate S3 object, and the 241 URLs are all unique.
  Same-size hotfix builds are not byte-identical either: 1.17.31 ASTC has the
  same 1,586,923,464 bytes as 1.17.32 but MD5 `28c4e55d…`
  (`63_head_google_lowres_v162073_Nexus_9_ATC.amz.headers`). So
  archive-level dedup saves nothing.
* Old versions are only useful with the matching old APK.
* Old indexes checked so far (headers only, `65_*`):
  * 1.16.30, 1.10.3 and 1.5.9 ETC use the 1.17 layout.
  * **1.4.17 ETC uses 32-bit `offset` fields**, even though it also says
    `version: 101` (`64_v1.4.17_etc_first1M.bin`). Its `Data/` is flat (no hash
    buckets), with 10,652 entries.
  * `packinfo.py` auto-detects the offset width. It accepts a layout only if
    every `zname == name + ".zip"` and the data region is contiguous; for
    1.4.17 that reproduces the exact `x-gl-size`. Anything else is refused,
    never guessed.
  * Old archive file names vary: `google_Nexus_9_ETC.amz` (1.4.x),
    `_Nexus_9_ETC.amz` (1.5.9), `google_lowres_v<build>_Nexus_9_*.amz` (later).

---

## 4. Storage on the device, and validation

### 4.1 Paths **[V]**

| what | path | source |
|---|---|---|
| data root `DATA_PATH` (`sd_folder`) | `getExternalFilesDir(null)` → `/storage/emulated/0/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/` (fallback string `/sdcard/Android/data/…/files`), stored in pref `SDFolder` | GI:1927-1933, 2040-2120; `AndroidUtils.java:137-140` (`RetrieveDataPath`, used by the engine) |
| game data | `DATA_PATH/Data/MCVS.game`, `Data/Config/EngineConfig.json`, `Data/{1-9,a-f}/*.f` | §3 |
| index copy | `getFilesDir()/pack.info` = `/data/data/com.gameloft.android.ANMP.GloftMVHM/files/pack.info`: the raw header bytes, without the 4-byte length | IU/DownloadComponent.java:66, 219-246 |
| APK version marker | `getFilesDir()/prefs/gl_ver` = `1173200` | GI:588-615 |
| installer prefs | `shared_prefs/MainActivityPrefs.xml`: `SDFolder`, `DATA_TYPE`, `CurrentVersion`, `IsGenericBuild`, `ZipHasCRCtest`, `pandoraURL`, … | GI passim |
| split-piece journal | `DATA_PATH/d_o_w_n_l_o_a_d_e_d.txt` (one `<folder><piece name>` per line) | `installer/Utils.java:105-157` |
| last update check | `DATA_PATH/InsTime` (`<unix seconds>/`) | GI:3125-3128 |
| other | `DATA_PATH/.nomedia`, `DATA_PATH/shader_info_cache/` (generated), optional `DATA_PATH/qaTestingConfigs.txt` | GI:568-586, 1619 |
| runtime asset cache (Iris) | `GlfStream.CachePath[/Subdirectory]/<assetName>` (cache dir resolved natively; not resolved here) | `GE/AssetsComponent.cs:85-117` |

### 4.2 How the game validates its data

These are the checks to reproduce on a backup:

1. **Existence and size.** Every logical file must exist with exactly `size`
   bytes; split files must reach `total_size` (`IU/DownloadComponent.java:286-301, 349-358`).
2. **CRC32**, when CRC checking is active (after an APK change, see §1.2).
   * Single files: CRC32 of the whole file == `crc` (`IU/CRC.java:12-32`).
   * Split files: CRC32 of each 2 MiB window == that piece's `crc`
     (`IU/l.java` SplitCRC; `CRC.calcChecksum(path, n)`).
   * Note: `CheckedInputStream.skip()` reads the data, so these windows
     really are checksummed.
3. **No hash beyond CRC32.** The MD5 field `h.j` is never filled by the
   parser, so the MD5 branch is a no-op.
4. **Version check.** `x-gl-version` from `&head=1` is compared with the
   preference `CurrentVersion` (§1.2).

`tools/cdn-mirror/verify.py` implements checks 1 and 2 exactly. It
auto-detects ASTC vs ETC from the bundled live indexes. For the user's own
device index (`pack.info`, root needed), pass `--manifest`.

### 4.3 Restoring onto a device or a fresh install **[I, not tested on a device]**

* With the data present and valid, a saved `pack.info`, and `gl_ver=1173200`,
  the installer goes straight to "start game" (state 2 → 21, GI:1095-1110)
  without network.
* Without root, `pack.info` cannot be restored. Two documented QA hooks in
  `DATA_PATH/qaTestingConfigs.txt` help:
  * `SKIP_VALIDATION=1` skips all checks and starts the game (GI:1083-1089).
  * `DATA_LINK=<url>` replaces the d.cdn.php URL (GI:2148-2152). This allows
    installing from a self-hosted mirror.
    * The server must support Range requests.
    * The final file name must contain `.amz`.
    * Plain http works for `*.gameloft.com` or `127.0.0.1`, for example with a
      hosts/DNS override of `dl.gameloft.com` and `droid06.gameloft.com`.
* `mirror.py --extract` produces the `files/Data/…` tree, ready to push.

---

## 5. Other things fetched at runtime (not on the dl/droid06 CDN)

All of these come from Gameloft's **Iris asset service**. They need archiving
separately, and the host for that service is itself located through eve and
pandora.

**URL scheme.**

* Assets: `https://<asset-host>/assets/<clientId>/<name>`.
* Listing: `GET https://<asset-host>/assets/<clientId>`, a JSON array of
  `{name, hash, …}` where `hash` is SHA-256 (`OP/AssetsApi.cs:5-43`,
  `GE/AssetsComponent.cs:74-167`).
* `clientId` = `2620:66102:1.17.32:android:googleplay`.
* `<asset-host>` = `GET <pandora>/locate/asset`. Pandora comes from eve, or
  from the hard-coded `https://vgold-apa.gameloft.com/2620:66102:1.17.32:android:googleplay`
  that the installer passes to native `DownloadGO` (GI:1517-1530).

**No access token is attached.** AssetsApi requests carry an empty scope list,
and `FedRequestMessage.PrepareToSend` adds `access_token` only when
`Scopes.Count != 0` (`OP/FedRequestMessage.cs:166-171`) **[V]**. Once the
asset host is known, these assets should be fetchable anonymously **[I]**.

**What is fetched:**

* **`GameOptions`**: per-device GPU/quality profiles.
  * The native engine fetches it with ETag caching to `GameOptions_saved.json`
    / `GameOptionsEtag.t`.
  * Evidence is adjacent native strings: `/locate/asset`, `https://`,
    `/assets/`, `/GameOptions`, `isInitFromInstaller`, `GameInstaller`, plus
    the JNI export `Java_com_gameloft_android_installer_GameInstaller_DownloadGO`
    in `libnativeengine.so` **[V strings, I exact request]**.
* **`tech_<lang>.json`**: a localisation patch table for the 16 languages
  (`GM/UIComponent.cs:482-497`) **[V]**.
* **Event, store and menu art**, each a `DownloadableAsset`. Names come from
  server-side event and CRM configs, so they cannot be enumerated offline:
  * `GM/TimeLimitedAchievementEvent.cs:91-95`, `GM/ShopCrateEvent.cs:26`,
    `GM/ShopCrateV2Event.cs:30`, `GM/CustomBundleEvent.cs:16-18`,
    `GM/ArenaModeEvent.cs:14`, `GM/SpecialOfferEvent.cs:9`,
    `GM/CrateSpecs.cs:10`, `GM/MenuElement.cs:18`;
  * the IAP store `showcase*` images, `GM/GameIapStoreComponent.cs:286-420`;
  * inbox gift images and zips, `GM/InboxComponent.cs:721-805`;
  * localized promo images `string.Format(name, lang)`,
    `GM/PlayerProfileComponent.cs:6007-6034`;
  * ad billboards `etc_<location>`, `GM/AdBillboardManagerComponent.cs:29-55`;
  * ban-reason assets, `OP/BannedFrom.cs:54`.
* **Not game content, so not worth archiving:** ads and IGP (`ingameads.gameloft.com`,
  `201205igp.gameloft.com`), tracking (`etsv2.gameloft.com`,
  `gameoptions.gameloft.com/track/registerdevice/track.php`), and web links.

**Archiving.** The asset service has not been contacted: per the task rules
it is a federation-located API host. Two ways to archive it:

* `tools/cdn-mirror/iris.py --asset-host <host>` mirrors the listing and every
  listed asset, plus the fixed names `GameOptions` and `tech_*.json`, and
  checks each asset's SHA-256 against the listing. The script is untested
  against live servers; it was tested only against a local mock.
* From a device that has run the game, copy the engine cache directory.

---

## 6. What to archive (checklist)

1. **Required:** for 1.17.32, both archives, ASTC and ETC, 2.89 GiB in total.
   Use `python3 tools/cdn-mirror/mirror.py --out <dir>`. Keep these together
   with them:
   * `pack.info`, `manifest.json` and `headers.txt` for each archive (the tool
     writes these);
   * `index.json`, which holds the archive URLs, ETags and redirector headers.
2. **Recommended:** older versions from §3.6, via `--versions known` or
   `--versions all`: 241 archives, about 308 GiB, about 1.1–1.6 GiB each.
   Mirror the current version first.
3. **Iris assets, if their host can still be reached (§5):** the
   `assets/<clientId>` listing, `GameOptions`, `tech_{16 langs}.json`, and
   every listed image or zip.
4. **The APK itself, plus device files:** a device's
   `files/Data`, `files/pack.info`, `files/prefs/gl_ver` and
   `shared_prefs/MainActivityPrefs.xml`, as a reference restore set.

---

## 7. Tools (`tools/cdn-mirror/`, Python 3 standard library only)

| file | purpose |
|---|---|
| `mirror.py` | `--list` (redirector + HEAD; `--files` also lists inner files), `--out DIR` (chunked parallel Range download, default 4 connections × 16 MiB; resumable state file; retries with backoff and Retry-After; verifies size + MD5=ETag + every inner CRC32; hard-links identical archives; `--extract`), `--discover-versions`, `--verify-only`, `--no-redirector` (use recorded URLs if `dl.gameloft.com` dies but `droid06` lives) |
| `verify.py` | Checks a device backup against `pack.info`, using the installer's own size and CRC32 rules; auto-detects ASTC/ETC; `--report json` |
| `packinfo.py` | Strict parser for `.amz` / `pack.info` (rejects unsafe paths) and blob extractor |
| `netutil.py` | Minimal HTTP client: CONNECT-tunnel proxy support, no automatic redirects, 429 handling |
| `iris.py` | Optional Iris asset mirror (§5); needs `--asset-host` |
| `manifests/1.17.32/{ASTC,ETC}/pack.info`, `manifests/index.json` | The live 1.17.32 indexes and archive metadata, bundled so that verification works offline |

**Tests.**

* `--list` and `--list --files --summary` were run against the live CDN.
  Output is in §7.1.
* Download, resume after injected mid-stream failures, 429 retry, ETag
  hard-link dedupe, `--extract`, `--verify-only` and `verify.py` were tested
  against a local server that serves a synthetic `.amz` in the exact format.
* `verify.py` was also run on real 1.17.32 files rebuilt from captured blobs:
  3 files OK, ASTC detected.
* `verify.py --fetch` was tested live.
* **No bulk download was performed.** About 12 MB was fetched from the CDN
  in total, all of it redirector headers, archive indexes, HEADs and four
  small sample blobs. The largest single response was 1.9 MB.

### 7.1 Live `--list` output (2026-10-24)

```
$ python3 tools/cdn-mirror/mirror.py --list
version  tex  release generic          bytes  etag(md5)                          url
1.17.32  ASTC 136808  yes         1586923464  538f7382e577cd61bd79f757df3956a2   http://droid06.gameloft.com/marketplace/3484/3484/6912994/136808/google_lowres_v162074_Nexus_9_ATC.amz?fix=get
1.17.32  ETC  136814  yes         1519733632  80e63f5ce0eb81d9fc6a735fad421758   http://droid06.gameloft.com/marketplace/3484/3484/6913000/136814/google_lowres_v162074_Nexus_9_ETC.amz?fix=get
TOTAL: 2 archive(s), 3106657096 bytes (2.89 GiB) -- unique URLs only

$ python3 tools/cdn-mirror/mirror.py --list --files --summary
# 1.17.32 ASTC google_lowres_v162074_Nexus_9_ATC.amz: 16212 zip entries, 15747 files, 2042882427 bytes uncompressed, header 822138 bytes, archive size check OK
# 1.17.32 ETC google_lowres_v162074_Nexus_9_ETC.amz: 16218 zip entries, 15747 files, 2036819797 bytes uncompressed, header 822750 bytes, archive size check OK
TOTAL inner files: 31494 (4079702224 bytes = 3.80 GiB uncompressed)
```

All known versions, redirector only (242 throttled requests, about 3 min).
The full output is in `captures/cdn/62_list_all_known_versions.{txt,json}`:

```
$ python3 tools/cdn-mirror/mirror.py --list --versions known --no-head
...
  1.5.9    ASTC not offered by redirector (x-gl-size=0)
TOTAL: 241 archive(s), 330389957077 bytes (307.70 GiB) -- unique URLs only
```

### 7.2 Commands for the user

```sh
cd tools/cdn-mirror
python3 mirror.py --list                                  # what will be fetched (no data)
python3 mirror.py --out /path/to/mcvs-mirror              # 1.17.32 ASTC+ETC, resumable; rerun to resume
python3 mirror.py --out /path/to/mcvs-mirror --versions all   # + every older build in versions.json (large)
python3 mirror.py --out /path/to/mcvs-mirror --verify-only    # re-check later, offline

adb pull /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files ./phone-files
python3 verify.py ./phone-files --report phone-report.json   # exit 0 = intact
```

---

## 8. Verified vs inferred

**Verified** (code read, or the live CDN tested):

* The installer is the only active downloader, and it runs on every launch.
* The native DLC path is disabled (`args.txt`), and the managed DLC path is
  dead code.
* The redirector URL, the fallback in `res/raw/data.txt`, and the eve-first
  lookup order.
* Hosts and their behaviour: https and http on dl, http only on droid06,
  CloudFront/S3, ETag = MD5, Range support, 429 rate limit.
* The complete `.amz`/`pack.info` format, matched byte for byte against both
  live archives, with blob CRCs checked. Four older indexes were also
  checked, which is how the int32-offset variant of 1.4.17 was found.
* Only ASTC and ETC are offered, and every model tested gets the same archive.
* Sizes, counts and the list of older versions.
* Device paths, `pack.info` location, `gl_ver`, the d_o_w_n_l_o_a_d_e_d
  journal and InsTime.
* Iris asset requests carry no access token.

**Inferred:**

* Exact state-machine transitions for the weekly update check and the offline
  start (jadx could not decompile `r()` and `run()` cleanly).
* The `SKIP_VALIDATION` / `DATA_LINK` restore recipes (not tried on a device).
* The exact GameOptions request (`DownloadGO` was not disassembled).
* That Iris assets are anonymously fetchable once the host is known.
* The resolved engine cache path.
* That the 9,387 differing ASTC/ETC files are textures.

## 9. Capture index (`captures/cdn/`)

* `01_*`, `04_*`, `30_*`, `31_*`, `52_*`: redirector 301 answers.
* `05_*`, `10_*`-`29_*`, `32_*`-`36_*`, `40_*`-`51_*`: `&head=1` probes for
  models, compressions and versions.
* `53_*`: a 429 answer.
* `02_*`: HEAD of the ASTC archive.
* `03_*`, `07_*`: first bytes of the archives.
* `08_astc_header.bin`, `09_etc_header.bin`: full 1.17.32 indexes, with the
  4-byte length prefix.
* `10_*`, `11_*`, `12_*` `.bin`: sample blobs (`MCVS.game`,
  `EngineConfig.json`, both pieces of `0084423.f`).

Every `.bin` has a matching `.headers` file.
