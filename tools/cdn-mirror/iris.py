#!/usr/bin/env python3
"""OPTIONAL and UNTESTED against live servers: mirror Modern Combat Versus
runtime assets from Gameloft's "Iris" asset service.

These are not part of the main data CDN (mirror.py). The game fetches them
at runtime as  GET https://<asset-host>/assets/<clientId>/<name>  without an
access token. AssetsApi builds FedRequestMessage with an empty scope list,
so FedRequestMessage.PrepareToSend adds no access_token. The asset list is
GET https://<asset-host>/assets/<clientId>  (JSON array of {"name", "hash"
(sha256 hex), ...}, see AssetsComponent.LoadAssets/GetAssetHash).

The asset host itself is only discoverable through Gameloft's service
locator ("pandora", GET <pandora>/locate/asset?client_id=...), which is
covered by the federation/boot analysis, not by this tool. Pass the host you
obtained there with --asset-host. This tool never contacts eve or pandora.

Usage:
  python3 iris.py --asset-host HOST --list
  python3 iris.py --asset-host HOST --out ./mcvs-iris          # listing + every listed asset
  python3 iris.py --asset-host HOST --out ./mcvs-iris --names-only   # just the known fixed names
Standard library only.
"""

import argparse
import hashlib
import json
import os
import sys
import time
import urllib.parse

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
import netutil  # noqa: E402

CLIENT_ID = "2620:66102:1.17.32:android:googleplay"
LANGS = ["en", "fr", "de", "it", "es", "ja", "ko", "zh", "pt", "ru", "tr", "ar", "th", "id", "vi", "zt"]
# Fixed names referenced by code: native GameOptions (libnativeengine strings
# "/locate/asset" "/assets/" "/GameOptions"), UIComponent.UpdateLocalization tech_<lang>.json.
KNOWN_NAMES = ["GameOptions"] + ["tech_%s.json" % l for l in LANGS]


def safe_name(n):
    q = urllib.parse.quote(n, safe="")
    return q if q not in (".", "..") else "_" + q


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--asset-host", required=True, help="host (or https://host) returned by pandora locate/asset")
    ap.add_argument("--client-id", default=CLIENT_ID)
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--out")
    ap.add_argument("--names-only", action="store_true", help="skip the listing, fetch only known fixed names")
    ap.add_argument("--delay", type=float, default=0.5)
    ap.add_argument("--proxy")
    args = ap.parse_args()
    host = args.asset_host if "://" in args.asset_host else "https://" + args.asset_host
    base = host.rstrip("/") + "/assets/" + urllib.parse.quote(args.client_id, safe=":")  # literal colons, like the game
    cli = netutil.Client(proxy=args.proxy)
    listing = []
    if not args.names_only:
        st, hd, hl, body = cli.fetch("GET", base, max_body=64 << 20)
        if st != 200:
            print("listing %s -> HTTP %d" % (base, st), file=sys.stderr)
        else:
            listing = json.loads(body.decode("utf-8"))
    names = [a.get("name") for a in listing if isinstance(a, dict) and a.get("name")]
    hashes = {a["name"]: a.get("hash") for a in listing if isinstance(a, dict) and a.get("name")}
    for n in KNOWN_NAMES:
        if n not in names:
            names.append(n)
    if args.list or not args.out:
        for n in names:
            print("%s/%s\t%s" % (base, urllib.parse.quote(n, safe=""), hashes.get(n) or ""))
        print("TOTAL %d names (%d from listing)" % (len(names), len(listing)))
        return 0
    os.makedirs(os.path.join(args.out, "assets"), exist_ok=True)
    with open(os.path.join(args.out, "listing.json"), "w") as f:
        json.dump(listing, f, indent=1)
    report = []
    for n in names:
        p = os.path.join(args.out, "assets", safe_name(n))
        if os.path.exists(p) and hashes.get(n):
            with open(p, "rb") as f:
                if hashlib.sha256(f.read()).hexdigest() == hashes[n].lower():
                    report.append({"name": n, "status": "cached-ok"})
                    continue
        url = base + "/" + urllib.parse.quote(n, safe="")
        try:
            st, hd, hl, body = cli.fetch("GET", url, max_body=512 << 20)
        except netutil.NET_ERRORS as e:
            report.append({"name": n, "status": "error", "error": str(e)})
            continue
        rec = {"name": n, "status": st, "bytes": len(body), "etag": hd.get("etag"),
               "content_type": hd.get("content-type")}
        if st == 200:
            h = hashlib.sha256(body).hexdigest()
            rec["sha256"] = h
            rec["hash_ok"] = (h == hashes[n].lower()) if hashes.get(n) else None
            with open(p + ".tmp", "wb") as f:
                f.write(body)
            os.replace(p + ".tmp", p)
        report.append(rec)
        print("%s %s" % (st, n), file=sys.stderr)
        time.sleep(args.delay)
    with open(os.path.join(args.out, "report.json"), "w") as f:
        json.dump(report, f, indent=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
