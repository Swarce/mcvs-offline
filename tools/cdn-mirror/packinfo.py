"""Parser for the Gameloft ANMP installer archive (".amz") used by
Modern Combat Versus (com.gameloft.android.ANMP.GloftMVHM).

Layout of an .amz archive as served by the CDN (verified against the live
files for 1.17.32 ASTC/ETC):

    int32 BE  header_len
    byte[header_len]  pack.info      (exactly what the installer stores as
                                      /data/data/<pkg>/files/pack.info)
    byte[...]  data region: one standalone ZIP file (local header, deflated
               member, central directory, EOCD) per entry, back to back.
               entry.offset is relative to the start of the data region,
               i.e. absolute offset = 4 + header_len + entry.offset.

pack.info (java.io.DataOutputStream encoding, big endian, "UTF" = u16 length
+ modified UTF-8), repeated until EOF:

    UTF   s
    if s starts with "version: ":
        version = int(s[9:])           # 101 for this game
        int32 split_kb                 # 2048 -> files are split in 2 MiB pieces
        UTF   folder                   # e.g. ".\\Data\\1"
    else:
        folder = s
    int32 count
    count x entry:
        int64 offset                   # into the data region (int32 in old
                                       # archives, e.g. 1.4.17 -> auto-detected)
        int32 size                     # uncompressed size of this piece
        int64 crc32                    # CRC32 of the uncompressed piece
        UTF   name                     # "0084423.f" or "0084423.f.split_0001"
        if version >= 101 and name ends with ".split_0001":
            int64 total_size           # size of the reassembled file
        int32 zsize                    # bytes of the ZIP blob in the archive
        UTF   zname                    # name + ".zip"

Source: com/gameloft/android/installer/utils/i.java (PackFileReader),
h.java (PackFile), Section.java, DownloadComponent.java in the APK.

Standard library only.
"""

import io
import json
import re
import struct
import zipfile
import zlib

SPLIT_MARK = ".split_"
DEFAULT_SPLIT = 2 * 1024 * 1024
_SAFE_COMPONENT = re.compile(r"^[A-Za-z0-9_.\- ]+$")


class PackFormatError(Exception):
    pass


class Entry(object):
    __slots__ = ("index", "folder", "name", "offset", "size", "crc", "total",
                 "zsize", "zname", "section_version", "split_kb")

    def __init__(self, **kw):
        for k in self.__slots__:
            setattr(self, k, kw.get(k))

    # --- derived -------------------------------------------------------
    @property
    def is_split(self):
        return SPLIT_MARK in self.name

    @property
    def piece(self):
        """1-based piece number for split files, 0 otherwise."""
        if not self.is_split:
            return 0
        return int(self.name[self.name.rfind("_") + 1:])

    @property
    def base_name(self):
        if self.is_split:
            return self.name[:self.name.rfind(".")]
        return self.name

    @property
    def rel_dir(self):
        return folder_to_relpath(self.folder)

    @property
    def rel_path(self):
        """Path of the installed file relative to the game's data root
        (DATA_PATH = /sdcard/Android/data/<pkg>/files/)."""
        d = self.rel_dir
        return (d + "/" + self.base_name) if d else self.base_name

    def to_dict(self):
        d = {k: getattr(self, k) for k in self.__slots__}
        d["rel_path"] = self.rel_path
        d["piece"] = self.piece
        return d


def folder_to_relpath(folder):
    """Replicates the installer's mapping
    folder.replace(".\\\\", "").replace(".\\", "").replace("\\", "/")
    (Java String.replace is literal), then strips leading/trailing slashes
    and refuses anything that could escape the output directory."""
    p = folder.replace(".\\\\", "").replace(".\\", "").replace("\\", "/")
    p = p.strip("/")
    if not p:
        return ""
    parts = p.split("/")
    for part in parts:
        if part in ("", ".", "..") or not _SAFE_COMPONENT.match(part):
            raise PackFormatError("unsafe folder in pack.info: %s" % _short(folder))
    return "/".join(parts)


def _check_name(name):
    if name in ("", ".", "..") or "/" in name or "\\" in name or not _SAFE_COMPONENT.match(name):
        raise PackFormatError("unsafe file name in pack.info: %s" % _short(name))


class _Reader(object):
    def __init__(self, data):
        self.b = data
        self.o = 0

    def remaining(self):
        return len(self.b) - self.o

    def _take(self, n):
        if self.o + n > len(self.b):
            raise PackFormatError("truncated pack.info at offset %d" % self.o)
        v = self.b[self.o:self.o + n]
        self.o += n
        return v

    def i32(self):
        return struct.unpack(">i", self._take(4))[0]

    def i64(self):
        return struct.unpack(">q", self._take(8))[0]

    def utf(self):
        n = struct.unpack(">H", self._take(2))[0]
        return self._take(n).decode("utf-8", "replace")


class PackInfo(object):
    def __init__(self, entries, sections, raw):
        self.entries = entries          # list[Entry], archive order
        self.sections = sections        # list of dicts
        self.raw = raw                  # raw pack.info bytes (no length prefix)

    @property
    def header_len(self):
        return len(self.raw)

    @property
    def data_start(self):
        return 4 + len(self.raw)

    @property
    def data_len(self):
        if not self.entries:
            return 0
        last = max(self.entries, key=lambda e: e.offset)
        return last.offset + last.zsize

    @property
    def archive_size(self):
        """Expected size of the full .amz archive."""
        return self.data_start + self.data_len

    def files(self):
        """Logical (reassembled) files: list of dicts
        {rel_path, size, pieces:[Entry...]} in archive order."""
        out = []
        index = {}
        for e in self.entries:
            key = e.rel_path
            f = index.get(key)
            if f is None:
                f = {"rel_path": key, "size": None, "pieces": []}
                index[key] = f
                out.append(f)
            f["pieces"].append(e)
        for f in out:
            ps = f["pieces"]
            if len(ps) == 1 and not ps[0].is_split:
                f["size"] = ps[0].size
            else:
                ps.sort(key=lambda e: e.piece)
                tot = None
                for e in ps:
                    if e.total is not None:
                        tot = e.total
                f["size"] = tot if tot is not None else sum(e.size for e in ps)
        return out

    def split_size(self):
        for s in self.sections:
            if s.get("split_kb"):
                return int(s["split_kb"]) * 1024
        return DEFAULT_SPLIT

    def check_contiguous(self):
        """Returns list of problems (empty if the data region is a perfect
        back-to-back sequence of blobs, which is the case for the live CDN
        files)."""
        probs = []
        pos = 0
        for e in sorted(self.entries, key=lambda x: x.offset):
            if e.offset != pos:
                probs.append("gap/overlap before %s at %d (expected %d)" % (e.name, e.offset, pos))
            pos = e.offset + e.zsize
        return probs


def parse_packinfo(raw, offset_width=None):
    """Parse raw pack.info bytes (no 4-byte length prefix).

    offset_width: 8 (int64 offsets, 1.17.x installer, i.java) or 4 (int32
    offsets, seen in the 1.4.17 archive although it also says "version: 101").
    None = auto: try 8, then 4; a layout is accepted only if every entry is
    well-formed (zname == name + ".zip") and the data region is contiguous."""
    if offset_width is None:
        errs = []
        for w in (8, 4):
            try:
                pi = _parse(raw, w)
            except PackFormatError as e:
                errs.append("int%d offsets: %s" % (w * 8, e))
                continue
            if pi.check_contiguous():
                errs.append("int%d offsets: data region not contiguous" % (w * 8))
                continue
            return pi
        raise PackFormatError("; ".join(errs))
    return _parse(raw, offset_width)


def _short(x):
    r = repr(x)
    return r if len(r) <= 80 else r[:77] + "..."


def _parse(raw, offset_width):
    r = _Reader(raw)
    entries = []
    sections = []
    idx = 0
    while r.remaining() > 0:
        s = r.utf()
        ver = -1
        split_kb = None
        if s.startswith("version: "):
            try:
                ver = int(s[9:])
            except ValueError:
                raise PackFormatError("bad version string %r" % s)
            split_kb = r.i32()
            folder = r.utf()
        else:
            folder = s
        folder_to_relpath(folder)  # validates
        cnt = r.i32()
        if cnt < 0 or cnt > 10000000:
            raise PackFormatError("bad entry count %d" % cnt)
        sections.append({"version": ver, "split_kb": split_kb, "folder": folder, "count": cnt})
        for _ in range(cnt):
            off = r.i64() if offset_width == 8 else r.i32()
            size = r.i32()
            crc = r.i64()
            name = r.utf()
            total = None
            if ver >= 101 and name.endswith(".split_0001"):
                total = r.i64()
            zsize = r.i32()
            zname = r.utf()
            base = name[:name.rfind(".")] if SPLIT_MARK in name else name
            _check_name(base)
            if zname != name + ".zip":
                raise PackFormatError("entry %s: zip name %s does not match" % (_short(name), _short(zname)))
            if off < 0 or size < 0 or zsize < 0:
                raise PackFormatError("negative field in entry %s" % name)
            entries.append(Entry(index=idx, folder=folder, name=name, offset=off,
                                 size=size, crc=crc & 0xFFFFFFFF, total=total,
                                 zsize=zsize, zname=zname, section_version=ver,
                                 split_kb=split_kb))
            idx += 1
    pi = PackInfo(entries, sections, bytes(raw))
    pi.offset_width = offset_width
    return pi


def looks_like_raw_packinfo(b):
    if len(b) < 2:
        return False
    n = struct.unpack(">H", b[:2])[0]
    head = b[2:2 + n]
    return head.startswith(b"version: ") or head.startswith(b".\\")


def load_manifest_bytes(b):
    """Accepts any of:
      * raw pack.info (as stored on device in /data/data/<pkg>/files/pack.info)
      * 4-byte-length-prefixed header (first bytes of an .amz) or a whole .amz
      * manifest.json written by mirror.py
    Returns PackInfo."""
    if b[:1] in (b"{", b"["):
        d = json.loads(b.decode("utf-8"))
        raw = bytes.fromhex(d["packinfo_hex"])
        return parse_packinfo(raw)
    if len(b) >= 6:
        n = struct.unpack(">i", b[:4])[0]
        if 0 < n <= len(b) - 4 and looks_like_raw_packinfo(b[4:]):
            return parse_packinfo(b[4:4 + n])
    if looks_like_raw_packinfo(b):
        return parse_packinfo(b)
    raise PackFormatError("not a pack.info / .amz header / manifest.json")


def load_manifest_file(path):
    with open(path, "rb") as f:
        head = f.read(8)
        f.seek(0)
        if head[:1] in (b"{", b"["):
            return load_manifest_bytes(f.read())
        if len(head) >= 6:
            n = struct.unpack(">i", head[:4])[0]
            f.seek(0, 2)
            total = f.tell()
            f.seek(0)
            if 0 < n <= total - 4:
                pre = f.read(4 + min(n, 64))
                if looks_like_raw_packinfo(pre[4:]):
                    f.seek(4)
                    return parse_packinfo(f.read(n))
            f.seek(0)
        return load_manifest_bytes(f.read())


def extract_blob(blob, entry=None):
    """Decompress one per-entry ZIP blob. Returns bytes and verifies CRC32
    against the pack.info value when entry is given."""
    z = zipfile.ZipFile(io.BytesIO(blob))
    infos = z.infolist()
    if len(infos) != 1:
        raise PackFormatError("blob has %d members" % len(infos))
    data = z.read(infos[0])  # zipfile checks the zip-internal CRC
    if entry is not None:
        if len(data) != entry.size:
            raise PackFormatError("%s: size %d != pack.info %d" % (entry.name, len(data), entry.size))
        c = zlib.crc32(data) & 0xFFFFFFFF
        if c != entry.crc:
            raise PackFormatError("%s: crc %08x != pack.info %08x" % (entry.name, c, entry.crc))
    return data


def manifest_to_json(pi, meta=None):
    d = {
        "format": "mcvs-amz-manifest/1",
        "meta": meta or {},
        "header_len": pi.header_len,
        "archive_size": pi.archive_size,
        "split_size": pi.split_size(),
        "sections": pi.sections,
        "entry_count": len(pi.entries),
        "file_count": len(pi.files()),
        "uncompressed_total": sum(f["size"] for f in pi.files()),
        "entries": [e.to_dict() for e in pi.entries],
        "packinfo_hex": pi.raw.hex(),
    }
    return d
