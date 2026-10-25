#!/usr/bin/env python3
"""Mirror the Modern Combat Versus (Android, com.gameloft.android.ANMP.GloftMVHM)
game-data downloads from Gameloft's CDN.

How the game gets its data (see ../../reports/10-cdn-download-pipeline.md):
  GameInstaller (Java) asks the redirector
     http://dl.gameloft.com/partners/androidmarket/d.cdn.php
        ?model=<Build.MODEL>&device=<Build.DEVICE>&product=2620
        &version=1.17.32&compression=<ASTC|ETC>&portal=google_market
  which answers "301 Location: http://droid06.gameloft.com/marketplace/.../*.amz"
  plus x-gl-version / x-gl-size / x-gl-release / x-gl-generic headers.
  The .amz is one big archive: [int32 header_len][pack.info][zip blobs...].
  The installer range-downloads the blobs and unpacks them into
  /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/Data/...

Modes:
  --list                   resolve every build and print archive URLs + sizes
                           (+ inner files with --files). Downloads only the
                           redirector response, a HEAD of each archive and,
                           with --files, each archive's ~0.8 MB index header.
  --out DIR                download everything selected (resumable, chunked,
                           verified against size, S3 MD5 ETag and the per-file
                           CRC32s in pack.info).
  --discover-versions      sweep version numbers on the redirector (headers
                           only) to find older builds still on the CDN.
  --verify-only --out DIR  re-verify an existing mirror offline.

Python 3.6+, standard library only.
"""

import argparse
import hashlib
import json
import os
import shutil
import sys
import threading
import time
import urllib.parse
from concurrent.futures import ThreadPoolExecutor, as_completed

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import packinfo  # noqa: E402
import netutil  # noqa: E402

REDIRECTOR = "https://dl.gameloft.com/partners/androidmarket/d.cdn.php"
PRODUCT = "2620"
PORTAL = "google_market"
CURRENT_VERSION = "1.17.32"
# The APK only ever asks for these two (GameInstaller.D(): GL_KHR_texture_compression_astc_ldr ? "ASTC" : "ETC").
TEXTURE_FORMATS = ["ASTC", "ETC"]
# Versions offered by the redirector on 2026-10-24 (sweep 1.0-17.0-40 x ASTC/ETC,
# see versions.json for per-texture sizes). --discover-versions can sweep again.
KNOWN_VERSIONS = [
    "1.17.32", "1.17.31", "1.17.30", "1.17.21", "1.17.20", "1.17.15", "1.17.12", "1.17.11",
    "1.17.10", "1.17.8", "1.17.7", "1.17.6", "1.17.5", "1.17.4", "1.17.3", "1.17.2", "1.16.32",
    "1.16.31", "1.16.30", "1.16.22", "1.16.21", "1.16.20", "1.16.10", "1.16.7", "1.16.6", "1.16.4",
    "1.15.22", "1.15.21", "1.15.20", "1.15.11", "1.15.10", "1.15.8", "1.15.7", "1.15.6", "1.15.5",
    "1.15.1", "1.15.0", "1.14.4", "1.14.1", "1.14.0", "1.13.6", "1.13.5", "1.13.4", "1.13.3",
    "1.13.2", "1.13.1", "1.13.0", "1.12.3", "1.12.2", "1.12.1", "1.12.0", "1.11.1", "1.11.0",
    "1.10.25", "1.10.24", "1.10.22", "1.10.21", "1.10.20", "1.10.18", "1.10.17", "1.10.16",
    "1.10.15", "1.10.14", "1.10.12", "1.10.11", "1.10.10", "1.10.9", "1.10.8", "1.10.7", "1.10.6",
    "1.10.4", "1.10.3", "1.10.2", "1.10.1", "1.9.8", "1.9.7", "1.9.6", "1.9.5", "1.9.4", "1.9.3",
    "1.9.2", "1.9.1", "1.9.0", "1.8.4", "1.8.2", "1.8.1", "1.7.9", "1.7.8", "1.7.7", "1.7.6",
    "1.7.5", "1.7.4", "1.7.2", "1.7.1", "1.7.0", "1.6.7", "1.6.6", "1.6.5", "1.6.4", "1.6.2",
    "1.6.0", "1.5.19", "1.5.17", "1.5.16", "1.5.15", "1.5.12", "1.5.11", "1.5.10", "1.5.9",
    "1.5.7", "1.5.6", "1.5.5", "1.5.4", "1.5.3", "1.4.23", "1.4.22", "1.4.21", "1.4.20", "1.4.18",
    "1.4.17", "1.0.6"
]
DEFAULT_MODEL = "Pixel"
DEFAULT_DEVICE = "sailfish"


def log(*a):
    print(*a, file=sys.stderr)
    sys.stderr.flush()


def human(n):
    n = float(n)
    for u in ("B", "KiB", "MiB", "GiB", "TiB"):
        if n < 1024 or u == "TiB":
            return ("%d %s" % (n, u)) if u == "B" else ("%.2f %s" % (n, u))
        n /= 1024


def redirector_url(base, version, tex, model, device, head=False):
    q = [("model", model), ("device", device), ("product", PRODUCT), ("version", version),
         ("compression", tex), ("portal", PORTAL)]
    s = base + "?" + "&".join("%s=%s" % (k, urllib.parse.quote(v, safe="")) for k, v in q)
    if head:
        s += "&head=1"
    return s


def atomic_write(path, data):
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(data if isinstance(data, bytes) else data.encode("utf-8"))
    os.replace(tmp, path)


REDIRECTOR_THROTTLE = netutil.Throttle(0.5)  # dl.gameloft.com sits behind a Varnish rate limiter (429)


def retry(fn, retries, what, throttle=None):
    last = None
    for attempt in range(retries):
        try:
            if throttle is not None:
                throttle.wait()
            return fn()
        except netutil.RetryableStatus as e:
            last = e
            wait = e.retry_after if e.retry_after is not None else min(60, 2 * 2 ** attempt)
            wait = wait + 0.5 + attempt
            if throttle is not None:
                throttle.penalize(wait)
            log("  %s: HTTP %d, waiting %.1fs (retry %d/%d)" % (what, e.status, wait, attempt + 1, retries))
            time.sleep(wait)
        except netutil.NET_ERRORS as e:
            last = e
            t = netutil.backoff_sleep(attempt, cap=60)
            log("  retry %d/%d for %s after error: %s (slept %.1fs)" % (attempt + 1, retries, what, e, t))
    raise netutil.HttpError("giving up on %s: %s" % (what, last))


def redirector_fetch(cli, args, url, what):
    def go():
        st, hd, hl, body = cli.fetch("GET", url)
        netutil.check_retryable(st, hd, url)
        return st, hd, hl, body
    return retry(go, max(args.retries, 12), what, throttle=REDIRECTOR_THROTTLE)


# ---------------------------------------------------------------- resolve
def bundled_build(version, tex):
    """Archive URL recorded in manifests/index.json (used when the redirector
    is down or --no-redirector is given)."""
    p = os.path.join(HERE, "manifests", "index.json")
    try:
        with open(p) as f:
            for r in json.load(f):
                if r["version"] == version and r["texture"] == tex:
                    return {"version": version, "texture": tex, "redirector_url": None,
                            "redirector_status": None, "x_gl_version": r.get("x_gl_version"),
                            "x_gl_release": r.get("x_gl_release"), "x_gl_size": r["size"],
                            "x_gl_generic": None, "url": r["url"], "redirector_headers": "",
                            "available": True, "from_bundled_index": True,
                            "filename": os.path.basename(urllib.parse.urlsplit(r["url"]).path)}
    except (OSError, ValueError, KeyError):
        pass
    return None


def resolve_build(cli, args, version, tex):
    if getattr(args, "no_redirector", False):
        b = bundled_build(version, tex)
        return b or {"version": version, "texture": tex, "available": False, "x_gl_size": 0}
    url = redirector_url(args.redirector, version, tex, args.model, args.device)
    try:
        st, hd, hl, _ = redirector_fetch(cli, args, url, "redirector %s/%s" % (version, tex))
    except netutil.NET_ERRORS as e:
        b = bundled_build(version, tex)
        if b is None:
            raise
        log("  redirector unreachable (%s); using archive URL from manifests/index.json" % e)
        return b
    loc = hd.get("location")
    b = {
        "version": version, "texture": tex, "redirector_url": url,
        "redirector_status": st,
        "x_gl_version": hd.get("x-gl-version"), "x_gl_release": hd.get("x-gl-release"),
        "x_gl_size": int(hd.get("x-gl-size") or 0), "x_gl_generic": hd.get("x-gl-generic"),
        "url": loc, "redirector_headers": netutil.format_headers(st, hl),
    }
    if st not in (301, 302, 303, 307, 308) or not loc or not b["x_gl_size"]:
        if st not in (200, 301, 302, 303, 307, 308):
            log("  %s/%s: redirector answered HTTP %d" % (version, tex, st))
        fb = bundled_build(version, tex)
        if fb is not None:
            log("  %s/%s: redirector no longer offers it; trying the archive URL from manifests/index.json"
                % (version, tex))
            return fb
        b["available"] = False
        return b
    b["available"] = True
    b["filename"] = os.path.basename(urllib.parse.urlsplit(loc).path)
    return b


def head_archive(cli, args, b):
    def go():
        r = cli.fetch("HEAD", b["url"])
        netutil.check_retryable(r[0], r[1], b["url"])
        return r
    st, hd, hl, _ = retry(go, args.retries, "HEAD " + b["filename"])
    if st != 200:
        raise netutil.HttpError("HEAD %s -> %d" % (b["url"], st))
    b["size"] = int(hd.get("content-length") or 0)
    b["etag"] = (hd.get("etag") or "").strip('"')
    b["last_modified"] = hd.get("last-modified")
    b["accept_ranges"] = hd.get("accept-ranges")
    b["archive_headers"] = netutil.format_headers(st, hl)
    return b


def fetch_header(cli, args, b):
    """Returns PackInfo for the archive (downloads 4 bytes + header_len bytes)."""
    def first4():
        st, hd, hl, body = cli.fetch("GET", b["url"], {"Range": "bytes=0-3"}, max_body=16)
        netutil.check_retryable(st, hd, b["url"])
        if st != 206 or len(body) != 4:
            raise netutil.HttpError("range 0-3 -> %d (%d bytes)" % (st, len(body)))
        return hd, body
    hd, body = retry(first4, args.retries, "header length " + b["filename"])
    n = int.from_bytes(body, "big", signed=True)
    if n <= 0 or n > 64 * 1024 * 1024:
        raise packinfo.PackFormatError("implausible header length %d" % n)

    def rest():
        st, hd2, hl, raw = cli.fetch("GET", b["url"], {"Range": "bytes=4-%d" % (3 + n)}, max_body=n)
        netutil.check_retryable(st, hd2, b["url"])
        if st != 206 or len(raw) != n:
            raise netutil.HttpError("range header -> %d (%d/%d bytes)" % (st, len(raw), n))
        return raw
    raw = retry(rest, args.retries, "header " + b["filename"])
    pi = packinfo.parse_packinfo(raw)
    return pi


def is_archive(b):
    return b.get("filename", "").lower().endswith((".amz", ".jar"))


def build_dir(root, b):
    return os.path.join(root, b["version"], b["texture"])


def load_or_fetch_manifest(cli, args, b, cache_root):
    d = build_dir(cache_root, b)
    mpath = os.path.join(d, "pack.info")
    if os.path.exists(mpath):
        with open(mpath, "rb") as f:
            raw = f.read()
        try:
            pi = packinfo.parse_packinfo(raw)
            if not b.get("size") or pi.archive_size == b["size"]:
                return pi
        except packinfo.PackFormatError:
            pass
    pi = fetch_header(cli, args, b)
    os.makedirs(d, exist_ok=True)
    atomic_write(mpath, pi.raw)
    meta = {k: b.get(k) for k in ("version", "texture", "url", "size", "etag", "last_modified",
                                  "x_gl_version", "x_gl_release", "x_gl_generic", "filename")}
    atomic_write(os.path.join(d, "manifest.json"),
                 json.dumps(packinfo.manifest_to_json(pi, meta), indent=1))
    atomic_write(os.path.join(d, "headers.txt"),
                 "# redirector\n" + b.get("redirector_headers", "") +
                 "\n# archive HEAD\n" + b.get("archive_headers", ""))
    return pi


def select_versions(args, cache_root):
    v = args.versions
    if v == "current":
        return [CURRENT_VERSION]
    if v in ("known", "all"):
        out = list(KNOWN_VERSIONS)
        if v == "all":
            for root in {cache_root, args.out, HERE} - {None}:
                p = os.path.join(root, "versions.json")
                if os.path.exists(p):
                    with open(p) as f:
                        for x in json.load(f).get("found", []):
                            if x["version"] not in out:
                                out.append(x["version"])
        return out
    return [x.strip() for x in v.split(",") if x.strip()]


def resolve_all(cli, args, cache_root, need_head=True):
    builds = []
    for ver in select_versions(args, cache_root):
        for tex in args.tex:
            b = resolve_build(cli, args, ver, tex)
            if not b["available"]:
                log("  %-8s %-4s not offered by redirector (x-gl-size=%s)" % (ver, tex, b["x_gl_size"]))
                continue
            if need_head:
                try:
                    head_archive(cli, args, b)
                except netutil.NET_ERRORS as e:
                    log("  WARNING %s/%s: HEAD of archive failed: %s" % (ver, tex, e))
                    b["head_error"] = str(e)
                    b["size"] = b["x_gl_size"]
                    b["etag"] = ""
                    builds.append(b)
                    continue
                if b["size"] != b["x_gl_size"]:
                    log("  WARNING %s/%s: redirector x-gl-size %d != Content-Length %d"
                        % (ver, tex, b["x_gl_size"], b["size"]))
            builds.append(b)
    return builds


# ---------------------------------------------------------------- list
def cmd_list(cli, args):
    cache_root = args.cache or os.path.abspath("mcvs-cdn-cache")
    builds = resolve_all(cli, args, cache_root, need_head=not args.no_head)
    total = 0
    urls = {}
    print("%-8s %-4s %-7s %-7s %14s  %-34s %s" % ("version", "tex", "release", "generic", "bytes", "etag(md5)", "url"))
    for b in builds:
        size = b.get("size") or b["x_gl_size"]
        dup = b["url"] in urls
        print("%-8s %-4s %-7s %-7s %14d  %-34s %s%s" % (b["version"], b["texture"], b["x_gl_release"],
                                                     b["x_gl_generic"], size, b.get("etag", "-"), b["url"],
                                                     "  (same URL as above)" if dup else ""))
        if not dup:
            total += size
            urls[b["url"]] = b
    print("TOTAL: %d archive(s), %d bytes (%s) -- unique URLs only" % (len(urls), total, human(total)))
    nfiles = 0
    unc = 0
    if args.files:
        for b in urls.values():
            if not is_archive(b):
                print("\n# %s %s %s: not an .amz archive (%d bytes), no index" % (b["version"], b["texture"],
                                                                              b["filename"], b.get("size") or 0))
                continue
            try:
                pi = load_or_fetch_manifest(cli, args, b, cache_root)
            except (netutil.HttpError, packinfo.PackFormatError) as e:
                print("\n# %s %s %s: index not readable: %s" % (b["version"], b["texture"], b["filename"], e))
                continue
            files = pi.files()
            fu = sum(f["size"] for f in files)
            nfiles += len(files)
            unc += fu
            print("\n# %s %s %s: %d zip entries, %d files, %d bytes uncompressed, header %d bytes, "
                  "archive size check %s"
                  % (b["version"], b["texture"], b["filename"], len(pi.entries), len(files), fu, pi.header_len,
                     "OK" if pi.archive_size == b.get("size", pi.archive_size) else
                     "MISMATCH (%d)" % pi.archive_size))
            if not args.summary:
                for f in files:
                    crcs = ",".join("%08x" % e.crc for e in f["pieces"])
                    print("%s\t%d\t%s" % (f["rel_path"], f["size"], crcs))
        print("\nTOTAL inner files: %d (%d bytes = %s uncompressed)" % (nfiles, unc, human(unc)))
    if args.json:
        with open(args.json, "w") as f:
            json.dump({"generated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                       "builds": builds, "total_unique_bytes": total}, f, indent=1)
    return 0


# ---------------------------------------------------------------- download
class Progress(object):
    def __init__(self, total, already):
        self.total = total
        self.done = already
        self.lock = threading.Lock()
        self.t0 = time.time()
        self.base = already
        self.last = 0

    def add(self, n):
        with self.lock:
            self.done += n
            now = time.time()
            if now - self.last >= 5:
                self.last = now
                rate = (self.done - self.base) / max(1e-6, now - self.t0)
                log("    %s / %s (%.1f%%) %.2f MiB/s" % (human(self.done), human(self.total),
                                                         100.0 * self.done / max(1, self.total), rate / 1048576))


class ChangedOnServer(Exception):
    pass


def download_chunk(cli, args, url, path, start, end, etag, prog):
    want = end - start + 1
    for attempt in range(args.retries):
        got = 0
        try:
            conn, resp = cli.open("GET", url, {"Range": "bytes=%d-%d" % (start, end)})
            try:
                if resp.status != 206:
                    netutil.check_retryable(resp.status, {k.lower(): v for k, v in resp.getheaders()}, url)
                    raise netutil.HttpError("HTTP %d for range %d-%d" % (resp.status, start, end))
                et = (resp.getheader("ETag") or "").strip('"')
                if etag and et and et != etag:
                    raise ChangedOnServer("ETag changed on server: %s -> %s" % (etag, et))
                cr = resp.getheader("Content-Range") or ""
                if not cr.startswith("bytes %d-%d/" % (start, end)):
                    raise netutil.HttpError("unexpected Content-Range %r" % cr)
                with open(path, "r+b") as f:
                    f.seek(start)
                    while got < want:
                        buf = resp.read(min(1 << 20, want - got))
                        if not buf:
                            raise netutil.HttpError("short read %d/%d" % (got, want))
                        f.write(buf)
                        got += len(buf)
                        prog.add(len(buf))
            finally:
                conn.close()
            return
        except netutil.NET_ERRORS as e:
            prog.add(-got)
            if attempt + 1 >= args.retries:
                raise
            ra = getattr(e, "retry_after", None)
            if ra is not None:
                t = ra + 1 + attempt
                time.sleep(t)
            else:
                t = netutil.backoff_sleep(attempt)
            log("    chunk %d-%d failed (%s); retried %d/%d after %.0fs" % (start, end, e, attempt + 1, args.retries, t))


def md5_file(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        while True:
            b = f.read(8 << 20)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def deep_verify(path, pi, quiet=False):
    """Inflate every zip blob and compare size + CRC32 with pack.info."""
    bad = []
    with open(path, "rb") as f:
        n = len(pi.entries)
        for i, e in enumerate(sorted(pi.entries, key=lambda x: x.offset)):
            f.seek(pi.data_start + e.offset)
            blob = f.read(e.zsize)
            try:
                if len(blob) != e.zsize:
                    raise packinfo.PackFormatError("short blob")
                packinfo.extract_blob(blob, e)
            except Exception as ex:  # zipfile errors, crc mismatch, ...
                bad.append("%s/%s: %s" % (e.rel_dir, e.name, ex))
            if not quiet and i % 2000 == 0:
                log("    deep-verify %d/%d" % (i, n))
    return bad


def verify_archive(path, b, pi, deep=True):
    probs = []
    size = os.path.getsize(path)
    if size != b["size"]:
        probs.append("size %d != %d" % (size, b["size"]))
    if pi is not None:
        if pi.archive_size != size:
            probs.append("pack.info expects %d bytes, file has %d" % (pi.archive_size, size))
        with open(path, "rb") as f:
            n = int.from_bytes(f.read(4), "big", signed=True)
            if n != pi.header_len or f.read(n) != pi.raw:
                probs.append("embedded header differs from pack.info")
    etag = b.get("etag") or ""
    if len(etag) == 32 and "-" not in etag:
        log("    md5 ...")
        m = md5_file(path)
        if m != etag.lower():
            probs.append("md5 %s != ETag %s" % (m, etag))
    if not probs and deep and pi is not None:
        probs += deep_verify(path, pi)
    return probs


INSTALL_INDEX = ("McvsOffline", "install", "pack.info")


def write_install_index(pi, b, dest):
    """Put the archive's index where MCVS Offline looks for it (files/McvsOffline/install/pack.info): on the phone it
    gives it to the game's installer, which then accepts the copied Data/ without any network
    (docs/INSTALL-OFFLINE.md)."""
    out = os.path.join(dest, *INSTALL_INDEX)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    atomic_write(out, pi.raw)
    atomic_write(out + ".txt",
                 "Index of the Modern Combat Versus %s %s data pack (%s), written by tools/cdn-mirror/mirror.py --extract.\n"
                 "Copy the whole files/ folder to /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/ ;\n"
                 "MCVS Offline gives this index to the game's installer, which then accepts the data without internet.\n"
                 % (b.get("version"), b.get("texture"), b.get("filename")))
    return out


def extract_archive(path, pi, dest):
    """Unpack into the on-device layout (dest/Data/...), exactly like the
    installer: pieces of split files are written at (piece-1)*2MiB."""
    split = pi.split_size()
    with open(path, "rb") as f:
        for fl in pi.files():
            out = os.path.join(dest, *fl["rel_path"].split("/"))
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out + ".tmp", "wb") as o:
                for e in fl["pieces"]:
                    f.seek(pi.data_start + e.offset)
                    data = packinfo.extract_blob(f.read(e.zsize), e)
                    if e.piece:
                        o.seek((e.piece - 1) * split)
                    o.write(data)
                o.truncate(fl["size"])
            os.replace(out + ".tmp", out)


def download_build(cli, args, b, pi, root, done_by_etag):
    d = build_dir(root, b)
    os.makedirs(d, exist_ok=True)
    final = os.path.join(d, b["filename"])
    part = final + ".part"
    state_p = part + ".json"
    key = (b.get("etag"), b["size"])
    if os.path.exists(final) and os.path.getsize(final) == b["size"] and os.path.exists(final + ".verified"):
        log("  %s already complete and verified" % final)
        done_by_etag[key] = final
        return True
    if key in done_by_etag and key[0]:
        src = done_by_etag[key]
        try:
            if os.path.exists(final):
                os.remove(final)
            os.link(src, final)
            how = "hard-linked"
        except OSError:
            shutil.copyfile(src, final)
            how = "copied"
        atomic_write(final + ".verified", "identical (ETag %s) to %s\n" % (key[0], src))
        log("  %s %s from identical archive %s" % (final, how, src))
        return True
    if not os.path.exists(final) or os.path.getsize(final) != b["size"] or not os.path.exists(final + ".verified"):
        chunk = args.chunk_mb * 1024 * 1024
        nchunks = (b["size"] + chunk - 1) // chunk
        state = None
        if os.path.exists(part) and os.path.exists(state_p):
            try:
                with open(state_p) as f:
                    state = json.load(f)
                if (state.get("size") != b["size"] or state.get("etag") != b.get("etag")
                        or state.get("chunk") != chunk):
                    log("  existing partial download does not match (size/etag/chunk); restarting")
                    state = None
            except ValueError:
                state = None
        if state is None:
            with open(part, "wb") as f:
                f.truncate(b["size"])
            state = {"url": b["url"], "size": b["size"], "etag": b.get("etag"), "chunk": chunk, "done": []}
            atomic_write(state_p, json.dumps(state))
        done = set(state["done"])
        todo = [i for i in range(nchunks) if i not in done]
        already = sum(min(chunk, b["size"] - i * chunk) for i in done)
        log("  downloading %s: %s, %d/%d chunks already done" % (b["filename"], human(b["size"]), len(done), nchunks))
        prog = Progress(b["size"], already)
        lock = threading.Lock()
        with ThreadPoolExecutor(max_workers=args.concurrency) as ex:
            futs = {}
            for i in todo:
                s = i * chunk
                e = min(b["size"], s + chunk) - 1
                futs[ex.submit(download_chunk, cli, args, b["url"], part, s, e, b.get("etag"), prog)] = i
            failed = None
            for fu in as_completed(futs):
                i = futs[fu]
                try:
                    fu.result()
                except Exception as e:  # keep going so the state file stays useful
                    failed = failed or e
                    log("  chunk %d FAILED: %s" % (i, e))
                    continue
                with lock:
                    done.add(i)
                    state["done"] = sorted(done)
                    atomic_write(state_p, json.dumps(state))
        if failed:
            log("  %s incomplete (%d/%d chunks); rerun the same command to resume" % (b["filename"], len(done), nchunks))
            return False
        os.replace(part, final)
        os.remove(state_p)
    log("  verifying %s ..." % final)
    probs = verify_archive(final, b, pi, deep=not args.no_deep_verify)
    if probs:
        log("  VERIFY FAILED for %s:\n    %s" % (final, "\n    ".join(probs[:20])))
        bad = final + ".bad"
        os.replace(final, bad)
        log("  moved to %s; rerun to download again" % bad)
        return False
    atomic_write(final + ".verified", "size=%d md5/etag=%s deep=%s %s\n" %
                 (b["size"], b.get("etag"), not args.no_deep_verify, time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())))
    done_by_etag[key] = final
    log("  OK %s" % final)
    return True


def cmd_out(cli, args):
    root = os.path.abspath(args.out)
    os.makedirs(root, exist_ok=True)
    builds = resolve_all(cli, args, root, need_head=True)
    ok = True
    done_by_etag = {}
    total = sum(b["size"] for b in {b["url"]: b for b in builds}.values())
    log("selected %d build(s), %s unique" % (len(builds), human(total)))
    index = []
    ip = os.path.join(root, "index.json")
    if os.path.exists(ip):
        try:
            with open(ip) as f:
                index = json.load(f)
        except ValueError:
            index = []
    for b in builds:
        log("\n== %s %s (release %s) %s" % (b["version"], b["texture"], b["x_gl_release"], b["url"]))
        rec = {k: v for k, v in b.items() if not k.endswith("_headers")}
        good = False
        try:
            if b.get("head_error"):
                raise netutil.HttpError("archive not reachable: %s" % b["head_error"])
            if is_archive(b):
                pi = load_or_fetch_manifest(cli, args, b, root)
                if pi.archive_size != b["size"]:
                    log("  WARNING: pack.info describes %d bytes but archive is %d" % (pi.archive_size, b["size"]))
                rec["entries"] = len(pi.entries)
                rec["files"] = len(pi.files())
            else:
                # e.g. 1.0.6 -> "dummy.txt" (10 bytes): mirrored verbatim, no index
                log("  not an .amz archive; mirroring the file verbatim")
                pi = None
            good = download_build(cli, args, b, pi, root, done_by_etag)
            if good and args.extract and pi is not None:
                dest = os.path.join(build_dir(root, b), "files")
                log("  extracting to %s ..." % dest)
                extract_archive(os.path.join(build_dir(root, b), b["filename"]), pi, dest)
                log("  index for the phone: %s" % write_install_index(pi, b, dest))
        except (netutil.HttpError, packinfo.PackFormatError, OSError, ChangedOnServer) as e:
            log("  FAILED %s %s: %s" % (b["version"], b["texture"], e))
            rec["error"] = str(e)
            good = False
        ok = ok and good
        rec["complete"] = good
        index = [r for r in index if not (r["version"] == b["version"] and r["texture"] == b["texture"])]
        index.append(rec)
        atomic_write(os.path.join(root, "index.json"), json.dumps(index, indent=1))
    log("\n%s" % ("ALL DONE, all archives verified" if ok else "SOME ARCHIVES INCOMPLETE OR FAILED - rerun to resume"))
    return 0 if ok else 1


def cmd_verify_only(args):
    root = os.path.abspath(args.out)
    p = os.path.join(root, "index.json")
    if not os.path.exists(p):
        log("no index.json in %s" % root)
        return 2
    with open(p) as f:
        index = json.load(f)
    ok = True
    for b in index:
        d = build_dir(root, b)
        path = os.path.join(d, b["filename"])
        if not os.path.exists(path):
            log("MISSING %s" % path)
            ok = False
            continue
        pi = None
        if is_archive(b):
            with open(os.path.join(d, "pack.info"), "rb") as f:
                pi = packinfo.parse_packinfo(f.read())
        probs = verify_archive(path, b, pi, deep=not args.no_deep_verify)
        log(("OK      %s" % path) if not probs else ("BAD     %s\n  %s" % (path, "\n  ".join(probs[:20]))))
        ok = ok and not probs
    return 0 if ok else 1


# ---------------------------------------------------------------- discovery
def parse_range(s):
    out = []
    for part in s.split(","):
        if "-" in part:
            a, b = part.split("-")
            out += list(range(int(a), int(b) + 1))
        else:
            out.append(int(part))
    return out


def cmd_discover(cli, args):
    cands = ["%d.%d.%d" % (ma, mi, pa) for ma in parse_range(args.major)
             for mi in parse_range(args.minor) for pa in parse_range(args.patch)]
    if args.delay:
        REDIRECTOR_THROTTLE.min_interval = max(REDIRECTOR_THROTTLE.min_interval, args.delay)
    log("probing %d version strings x %s on %s (headers only, >= %.2fs between requests)"
        % (len(cands), ",".join(args.tex), args.redirector, REDIRECTOR_THROTTLE.min_interval))
    found = []
    lock = threading.Lock()

    def probe(ver, tex):
        url = redirector_url(args.redirector, ver, tex, args.model, args.device, head=True)
        st, hd, hl, _ = redirector_fetch(cli, args, url, "%s/%s" % (ver, tex))
        if st != 200:
            log("  %s %s: unexpected HTTP %d" % (ver, tex, st))
        size = int(hd.get("x-gl-size") or 0)
        if size > 0:
            r = {"version": ver, "texture": tex, "x_gl_version": hd.get("x-gl-version"),
                 "x_gl_release": hd.get("x-gl-release"), "x_gl_size": size, "x_gl_generic": hd.get("x-gl-generic")}
            with lock:
                found.append(r)
            log("  found %s %s release=%s size=%d" % (ver, tex, r["x_gl_release"], size))

    with ThreadPoolExecutor(max_workers=args.concurrency) as ex:
        list(ex.map(lambda vt: probe(*vt), [(v, t) for v in cands for t in args.tex]))
    found.sort(key=lambda r: ([int(x) for x in r["version"].split(".")], r["texture"]))
    root = args.out or args.cache or "."
    os.makedirs(root, exist_ok=True)
    p = os.path.join(root, "versions.json")
    atomic_write(p, json.dumps({"generated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                                "probed": len(cands), "textures": args.tex, "found": found}, indent=1))
    for r in found:
        print("%-8s %-4s release=%-7s size=%d" % (r["version"], r["texture"], r["x_gl_release"], r["x_gl_size"]))
    log("wrote %s (%d builds); use --versions all to include them" % (p, len(found)))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    m = ap.add_mutually_exclusive_group(required=True)
    m.add_argument("--list", action="store_true", help="print archive URLs, sizes and total; download no data")
    m.add_argument("--out", metavar="DIR", help="download (resumable) into DIR")
    m.add_argument("--discover-versions", action="store_true", help="sweep the redirector for other versions")
    ap.add_argument("--verify-only", action="store_true", help="with --out: re-verify existing files, no network")
    ap.add_argument("--files", action="store_true", help="with --list: also fetch each archive index and list inner files")
    ap.add_argument("--summary", action="store_true", help="with --list --files: per-archive totals only")
    ap.add_argument("--no-head", action="store_true", help="with --list: skip HEAD of archives (no ETag)")
    ap.add_argument("--json", metavar="FILE", help="with --list: write machine readable result")
    ap.add_argument("--versions", default="current",
                    help="current (default, %s) | known | all (known + versions.json) | comma list" % CURRENT_VERSION)
    ap.add_argument("--tex", default=",".join(TEXTURE_FORMATS), help="texture formats (default ASTC,ETC)")
    ap.add_argument("--model", default=DEFAULT_MODEL)
    ap.add_argument("--device", default=DEFAULT_DEVICE)
    ap.add_argument("--redirector", default=REDIRECTOR,
                    help="d.cdn.php URL (the APK itself uses the http:// form)")
    ap.add_argument("--no-redirector", action="store_true",
                    help="do not ask d.cdn.php; use the archive URLs recorded in manifests/index.json")
    ap.add_argument("--concurrency", type=int, default=4, help="parallel connections (default 4)")
    ap.add_argument("--chunk-mb", type=int, default=16, help="range chunk size in MiB (default 16)")
    ap.add_argument("--retries", type=int, default=8)
    ap.add_argument("--timeout", type=int, default=60)
    ap.add_argument("--no-deep-verify", action="store_true", help="skip per-file CRC32 check (size+MD5 only)")
    ap.add_argument("--extract", action="store_true",
                    help="also unpack each archive to <ver>/<tex>/files/Data/... (plus files/McvsOffline/install/pack.info, "
                         "which lets the offline build accept the copied data without internet)")
    ap.add_argument("--cache", metavar="DIR", help="where --list keeps fetched indexes (default ./mcvs-cdn-cache)")
    ap.add_argument("--proxy", help="proxy URL (default: $HTTPS_PROXY/$HTTP_PROXY)")
    ap.add_argument("--proxy-mode", choices=["connect", "forward"], default="connect",
                    help="for http:// targets: CONNECT tunnel (default) or absolute-URI forwarding")
    ap.add_argument("--cacert", help="CA bundle file for https")
    ap.add_argument("--major", default="1")
    ap.add_argument("--minor", default="0-17")
    ap.add_argument("--patch", default="0-40")
    ap.add_argument("--delay", type=float, default=0.0,
                    help="minimum seconds between redirector requests (default 0.5)")
    args = ap.parse_args(argv)
    args.tex = [t.strip().upper() for t in args.tex.split(",") if t.strip()]
    if args.concurrency < 1:
        args.concurrency = 1
    if args.verify_only:
        if not args.out:
            ap.error("--verify-only needs --out DIR")
        return cmd_verify_only(args)
    cli = netutil.Client(proxy=args.proxy, proxy_mode=args.proxy_mode, cafile=args.cacert, timeout=args.timeout)
    if args.list:
        return cmd_list(cli, args)
    if args.discover_versions:
        return cmd_discover(cli, args)
    return cmd_out(cli, args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        log("\ninterrupted; rerun the same command to resume")
        sys.exit(130)
