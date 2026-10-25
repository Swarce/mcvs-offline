#!/usr/bin/env python3
"""Verify an on-device backup of Modern Combat Versus game data against the
CDN manifest (pack.info), the same way the game's installer does it:
every file must exist with the exact size, and its CRC32 (per 2 MiB piece
for files that the archive splits) must match pack.info.

The data lives on the phone in
    /sdcard/Android/data/com.gameloft.android.ANMP.GloftMVHM/files/
        Data/MCVS.game
        Data/Config/EngineConfig.json
        Data/{1..9,a..f}/<hash>.f        (~15.7k files, ~1.9 GiB)
(the installer's own copy of the manifest is
    /data/data/com.gameloft.android.ANMP.GloftMVHM/files/pack.info,
 readable only with root/run-as).

Usage:
  python3 verify.py BACKUP_DIR                      # uses the index the backup carries (McvsOffline/install/pack.info,
                                                    # saved by MCVS Offline, or files/pack.info), else local manifests/
                                                    # copies (never committed; --fetch gets them), auto-detects ASTC/ETC
  python3 verify.py BACKUP_DIR --manifest pack.info # your device's own pack.info / an .amz / manifest.json
  python3 verify.py BACKUP_DIR --mirror MIRROR_DIR  # use manifests from a mirror.py output dir
  python3 verify.py BACKUP_DIR --fetch              # fetch current manifests from the CDN
Options: --quick (sizes only), --report out.json, --tex ASTC|ETC

Exit status: 0 = backup complete and intact, 1 = problems found, 2 = usage error.
Standard library only.
"""

import argparse
import glob
import json
import os
import sys
import time
import zlib

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import packinfo  # noqa: E402

INSTALLER_FILES = {"InsTime", "d_o_w_n_l_o_a_d_e_d.txt", ".nomedia", "qaTestingConfigs.txt"}


def log(*a):
    print(*a, file=sys.stderr)
    sys.stderr.flush()


def find_root(path):
    """Return the directory that contains Data/MCVS.game."""
    path = os.path.abspath(path)
    cands = [path, os.path.dirname(path)]
    for c in cands:
        if os.path.isfile(os.path.join(c, "Data", "MCVS.game")):
            return c
    for depth in range(1, 6):
        pat = os.path.join(path, *(["*"] * depth), "Data", "MCVS.game")
        hits = sorted(glob.glob(pat)) + sorted(glob.glob(os.path.join(path, *(["*"] * depth), "Data", "Config")))
        for h in hits:
            return os.path.dirname(os.path.dirname(h))
    # Data dir without MCVS.game (badly incomplete backup)
    for c in cands:
        if os.path.isdir(os.path.join(c, "Data")):
            return c
    return None


def backup_indexes(root):
    """Indexes a backup of the files folder carries: the copy MCVS Offline keeps of the installer's index, or one the
    player put next to Data/ (for example from mirror.py --extract)."""
    out = []
    if not root:
        return out
    for rel in (("McvsOffline", "install", "pack.info"), ("pack.info",)):
        p = os.path.join(root, *rel)
        if os.path.isfile(p):
            try:
                out.append(("%s (in the backup)" % p, packinfo.load_manifest_file(p)))
            except Exception as e:  # noqa: BLE001 - a bad copy is reported, the other sources still apply
                log("ignoring %s: %s" % (p, e))
    return out


def load_manifests(args, root=None):
    out = []  # (label, PackInfo)
    if args.manifest:
        for m in args.manifest:
            out.append((m, packinfo.load_manifest_file(m)))
    if args.mirror:
        for p in sorted(glob.glob(os.path.join(args.mirror, "*", "*", "pack.info"))):
            ver = os.path.basename(os.path.dirname(os.path.dirname(p)))
            tex = os.path.basename(os.path.dirname(p))
            out.append(("%s %s (%s)" % (ver, tex, p), packinfo.load_manifest_file(p)))
    if args.fetch:
        import mirror
        import netutil
        cli = netutil.Client(proxy=args.proxy)
        margs = argparse.Namespace(redirector=mirror.REDIRECTOR, model=mirror.DEFAULT_MODEL,
                                   device=mirror.DEFAULT_DEVICE, retries=8)
        for tex in mirror.TEXTURE_FORMATS:
            b = mirror.resolve_build(cli, margs, args.version, tex)
            if not b["available"]:
                log("CDN does not offer %s %s" % (args.version, tex))
                continue
            mirror.head_archive(cli, margs, b)
            out.append(("%s %s (CDN)" % (args.version, tex), mirror.fetch_header(cli, margs, b)))
    if not out:
        out.extend(backup_indexes(root))
    if not out:
        for p in sorted(glob.glob(os.path.join(HERE, "manifests", "*", "*", "pack.info"))):
            ver = os.path.basename(os.path.dirname(os.path.dirname(p)))
            tex = os.path.basename(os.path.dirname(p))
            out.append(("%s %s (local manifests/)" % (ver, tex), packinfo.load_manifest_file(p)))
    if args.tex:
        out = [x for x in out if (" %s " % args.tex.upper()) in (" " + x[0] + " ")] or out
    return out


def crc_file_pieces(path, split, npieces_hint):
    """CRC32 of the whole file and of each split-sized window."""
    whole = 0
    pieces = []
    with open(path, "rb") as f:
        while True:
            cur = 0
            got = 0
            while got < split:
                b = f.read(min(1 << 20, split - got))
                if not b:
                    break
                cur = zlib.crc32(b, cur)
                whole = zlib.crc32(b, whole)
                got += len(b)
            if got == 0:
                break
            pieces.append(cur & 0xFFFFFFFF)
            if got < split:
                break
    return whole & 0xFFFFFFFF, pieces


def score_sizes(root, pi):
    ok = 0
    for f in pi.files():
        p = os.path.join(root, *f["rel_path"].split("/"))
        try:
            if os.path.getsize(p) == f["size"]:
                ok += 1
        except OSError:
            pass
    return ok


def pick_manifest(root, manifests):
    if len(manifests) == 1:
        return manifests[0], None
    scores = [(score_sizes(root, pi), label, pi) for label, pi in manifests]
    scores.sort(key=lambda x: -x[0])
    # tie-break on CRCs of files that differ between the top candidates
    if len(scores) > 1 and scores[0][0] == scores[1][0]:
        a, b = scores[0][2], scores[1][2]
        fb = {f["rel_path"]: f for f in b.files()}
        diff = [f for f in a.files() if f["rel_path"] in fb and
                [e.crc for e in f["pieces"]] != [e.crc for e in fb[f["rel_path"]]["pieces"]]][:200]
        sa = sb = 0
        split = a.split_size()
        for f in diff:
            p = os.path.join(root, *f["rel_path"].split("/"))
            if not os.path.isfile(p):
                continue
            w, ps = crc_file_pieces(p, split, len(f["pieces"]))
            if ps[:1] == [f["pieces"][0].crc]:
                sa += 1
            if ps[:1] == [fb[f["rel_path"]]["pieces"][0].crc]:
                sb += 1
        if sb > sa:
            scores[0], scores[1] = scores[1], scores[0]
    return (scores[0][1], scores[0][2]), [(s, l) for s, l, _ in scores]


def verify(root, pi, quick=False):
    res = {"ok": [], "missing": [], "size": [], "crc": [], "extra": [], "installer_files": []}
    split = pi.split_size()
    files = pi.files()
    expected = set()
    t0 = time.time()
    for i, f in enumerate(files):
        rel = f["rel_path"]
        expected.add(rel)
        p = os.path.join(root, *rel.split("/"))
        if not os.path.isfile(p):
            res["missing"].append(rel)
            continue
        sz = os.path.getsize(p)
        if sz != f["size"]:
            res["size"].append({"path": rel, "have": sz, "want": f["size"]})
            continue
        if not quick:
            whole, pieces = crc_file_pieces(p, split, len(f["pieces"]))
            ps = f["pieces"]
            if len(ps) == 1 and not ps[0].is_split:
                good = whole == ps[0].crc
            else:
                good = len(pieces) == len(ps) and all(pieces[e.piece - 1] == e.crc for e in ps)
            if not good:
                res["crc"].append(rel)
                continue
        res["ok"].append(rel)
        if i and i % 2000 == 0:
            log("  %d/%d files checked (%.0fs)" % (i, len(files), time.time() - t0))
    data_dir = os.path.join(root, "Data")
    for dp, dn, fn in os.walk(data_dir):
        for n in fn:
            rel = os.path.relpath(os.path.join(dp, n), root).replace(os.sep, "/")
            if rel not in expected and n != ".nomedia":
                res["extra"].append(rel)
    for n in sorted(os.listdir(root)):
        if n in INSTALLER_FILES or n == "shader_info_cache":
            res["installer_files"].append(n)
    return res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("backup", help="backup directory (the 'files' dir, its 'Data' dir, or anything above it)")
    ap.add_argument("--manifest", action="append", help="pack.info / .amz / manifest.json (repeatable)")
    ap.add_argument("--mirror", help="mirror.py output directory to take manifests from")
    ap.add_argument("--fetch", action="store_true", help="download the manifests (index headers only) from the CDN")
    ap.add_argument("--version", default="1.17.32", help="with --fetch (default 1.17.32)")
    ap.add_argument("--tex", help="force ASTC or ETC instead of auto-detecting")
    ap.add_argument("--quick", action="store_true", help="check existence + size only (no CRC)")
    ap.add_argument("--report", help="write a JSON report")
    ap.add_argument("--proxy", help="proxy for --fetch")
    args = ap.parse_args(argv)

    root = find_root(args.backup)
    if not root:
        log("could not find a Data/ directory (with MCVS.game) under %s" % args.backup)
        return 2
    manifests = load_manifests(args, root)
    if not manifests:
        log("no manifest available: the backup carries none (McvsOffline/install/pack.info) - use --manifest, --mirror or --fetch")
        return 2
    (label, pi), scores = pick_manifest(root, manifests)
    print("data root : %s" % root)
    if scores:
        print("variant   : %s  (size matches: %s)" % (label, ", ".join("%s=%d" % (l, s) for s, l in scores)))
    else:
        print("manifest  : %s" % label)
    res = verify(root, pi, quick=args.quick)
    files = pi.files()
    tot = sum(f["size"] for f in files)
    print("expected  : %d files, %d bytes" % (len(files), tot))
    print("ok        : %d" % len(res["ok"]))
    print("missing   : %d" % len(res["missing"]))
    print("bad size  : %d" % len(res["size"]))
    print("bad crc32 : %d%s" % (len(res["crc"]), "  (not checked: --quick)" if args.quick else ""))
    print("extra     : %d files under Data/ not in the manifest" % len(res["extra"]))
    if res["installer_files"]:
        print("installer : %s" % ", ".join(res["installer_files"]))
    for k, title in (("missing", "MISSING"), ("size", "SIZE"), ("crc", "CRC")):
        for x in res[k][:25]:
            print("  %-7s %s" % (title, x if isinstance(x, str) else "%s have=%d want=%d" % (x["path"], x["have"], x["want"])))
        if len(res[k]) > 25:
            print("  ... %d more %s (see --report)" % (len(res[k]) - 25, title))
    if args.report:
        with open(args.report, "w") as f:
            json.dump({"root": root, "manifest": label, "scores": scores, "quick": args.quick,
                       "counts": {k: len(v) for k, v in res.items()},
                       "missing": res["missing"], "size": res["size"], "crc": res["crc"],
                       "extra": res["extra"], "installer_files": res["installer_files"]}, f, indent=1)
    bad = res["missing"] or res["size"] or res["crc"]
    print("RESULT    : %s" % ("INTACT - matches the CDN manifest" if not bad else "INCOMPLETE/CORRUPT"))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
