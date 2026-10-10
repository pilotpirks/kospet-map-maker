#!/usr/bin/env python3
"""Converter: ESRI Shapefile / SRTM -> Kospet (YZL) map format (.mlp/.idx/.db).

Format (reverse-engineered from stock maps):
  Every file: 32-byte header  'YZL\\0' | u32 body_len | u32 2 | u32 0 | md5(body)[16]
  .mlp body : records  [u32 BE recno][u32 LE content_len] content
              content = i32 bbox[4] (deg*1e6) | u32 nparts | u32 npoints | i32 parts[] | i32 xy[]
  .db  body : plain dBase III (.dbf)
  .idx body : 'SQT\\x01' | i32 1 | i32 nlevels | i32 ntop | nodes...
              node  = i32 size | f32 bbox[4] | i32 level | i32 n | (n child nodes | n leaf entries)
                      size = bytes after the bbox up to the end of the node
              entry = f32 bbox[4] | i32 code | i32 offset | i32 recno   (28 bytes)
                      offset = position of record content in .mlp body minus... (= rec_hdr_pos + 8)
"""
import hashlib, math, struct, sys, os
import numpy as np

# ---------------------------------------------------------------- container
def yzl_wrap(body: bytes) -> bytes:
    return b'YZL\x00' + struct.pack('<III', len(body), 2, 0) + hashlib.md5(body).digest() + body

def yzl_unwrap(raw: bytes) -> bytes:
    assert raw[:4] == b'YZL\x00'
    body = raw[32:]
    assert struct.unpack('<I', raw[4:8])[0] == len(body)
    assert hashlib.md5(body).digest() == raw[16:32]
    return body

# ---------------------------------------------------------------- shapefile in
def read_shp(path):
    """yield (parts:list[int], pts:np.ndarray Nx2 float64, bbox) or None for null shapes"""
    d = open(path, 'rb').read()
    i = 100
    out = []
    while i < len(d):
        recno, clen = struct.unpack('>II', d[i:i + 8])
        c = d[i + 8:i + 8 + clen * 2]
        i += 8 + clen * 2
        st = struct.unpack('<i', c[:4])[0]
        if st == 0:
            out.append(None); continue
        if st not in (3, 5, 13, 15, 23, 25):
            raise ValueError('unsupported shape type %d' % st)
        bbox = struct.unpack('<4d', c[4:36])
        np_, npt = struct.unpack('<ii', c[36:44])
        parts = list(struct.unpack('<%di' % np_, c[44:44 + 4 * np_]))
        o = 44 + 4 * np_
        pts = np.frombuffer(c, dtype='<f8', count=2 * npt, offset=o).reshape(-1, 2)
        out.append((parts, pts, bbox))
    return out

def read_dbf(path):
    d = open(path, 'rb').read()
    n, hl, rl = struct.unpack('<IHH', d[4:12])
    fields = []; i = 32
    while d[i] != 0x0D:
        fields.append((d[i:i + 11].rstrip(b'\0').decode(), chr(d[i + 11]), d[i + 16], d[i + 17]))
        i += 32
    rows = []
    for k in range(n):
        r = d[hl + k * rl: hl + (k + 1) * rl]; p = 1; row = {}
        for nm, t, l, dec in fields:
            row[nm] = r[p:p + l]; p += l
        rows.append(row)
    return fields, rows

# ---------------------------------------------------------------- writers
def write_dbf(fields, rows):
    """fields: [(name,type,len,dec)], rows: list of dict name->bytes(raw, will be padded)"""
    rl = 1 + sum(f[2] for f in fields)
    hl = 32 + 32 * len(fields) + 1
    h = struct.pack('<BBBBIHH', 3, 95, 7, 26, len(rows), hl, rl) + b'\0' * 20
    for nm, t, l, dec in fields:
        h += nm.encode()[:10].ljust(11, b'\0') + t.encode() + b'\0' * 4 + bytes([l, dec]) + b'\0' * 14
    h += b'\x0d'
    out = bytearray(h)
    for r in rows:
        out += b' '
        for nm, t, l, dec in fields:
            v = r.get(nm, b'')
            if isinstance(v, str): v = v.encode('utf-8')
            v = v[:l]
            out += (v.rjust(l) if t in 'NF' else v.ljust(l))
    out += b'\x1a'
    return bytes(out)

def write_mlp(shapes):
    """shapes: list of (parts, pts float64 deg Nx2, bbox_double). recno = list index + 1.
    returns body, list of (recno, idx_offset)"""
    out = bytearray(); offs = []
    for k, (parts, pts, bbox) in enumerate(shapes):
        ip = np.round(pts * 1e6).astype('<i4')
        ib = (int(ip[:, 0].min()), int(ip[:, 1].min()), int(ip[:, 0].max()), int(ip[:, 1].max()))
        content = struct.pack('<4i', *ib) + struct.pack('<II', len(parts), len(ip)) \
            + struct.pack('<%di' % len(parts), *parts) + ip.tobytes()
        offs.append(len(out) + 8)
        out += struct.pack('>I', k + 1) + struct.pack('<I', len(content)) + content
    return bytes(out), offs

F32 = np.float32
def _f(b):  # bbox of doubles -> float32 tuple
    return tuple(float(F32(x)) for x in b)

def build_idx(entries, cap=10, minfill=7, maxfill=15):
    """entries: list of (bbox(4 floats), code, offset, recno). STR-packed R-tree."""
    def groups(items, centre):
        n = len(items)
        if n <= maxfill:
            return [items]
        g = math.ceil(n / cap)
        s = math.ceil(math.sqrt(g))
        items = sorted(items, key=lambda it: centre(it)[0])
        slices = []
        per_slice = math.ceil(n / s)
        # balanced slice sizes
        ns = math.ceil(n / per_slice)
        base, extra = divmod(n, ns)
        p = 0
        for i in range(ns):
            sz = base + (1 if i < extra else 0)
            slices.append(items[p:p + sz]); p += sz
        out = []
        for sl in slices:
            sl = sorted(sl, key=lambda it: centre(it)[1])
            k = max(1, math.ceil(len(sl) / cap))
            while k > 1 and len(sl) / k < minfill: k -= 1
            b2, e2 = divmod(len(sl), k)
            q = 0
            for j in range(k):
                sz = b2 + (1 if j < e2 else 0)
                out.append(sl[q:q + sz]); q += sz
        return out

    def ub(bbs):
        return (min(b[0] for b in bbs), min(b[1] for b in bbs), max(b[2] for b in bbs), max(b[3] for b in bbs))
    ctr = lambda b: ((b[0] + b[2]) / 2, (b[1] + b[3]) / 2)

    # level 0
    ents = [(_f(e[0]),) + tuple(e[1:]) for e in entries]
    nodes = []
    for g in groups(ents, lambda e: ctr(e[0])):
        payload = b''.join(struct.pack('<4f3i', *e[0], e[1], e[2], e[3]) for e in g)
        nodes.append((ub([e[0] for e in g]), 0, len(g), payload))
    level = 0
    while len(nodes) > maxfill:
        level += 1
        new = []
        for g in groups(nodes, lambda nd: ctr(nd[0])):
            payload = b''.join(_node_bytes(nd) for nd in g)
            new.append((ub([nd[0] for nd in g]), level, len(g), payload))
        nodes = new
    body = b'SQT\x01' + struct.pack('<3i', 1, level + 1, len(nodes)) + b''.join(_node_bytes(nd) for nd in nodes)
    return body

def _node_bytes(nd):
    bb, lvl, n, payload = nd
    return struct.pack('<i', 8 + len(payload)) + struct.pack('<4f', *bb) + struct.pack('<ii', lvl, n) + payload

# ---------------------------------------------------------------- layer
def make_layer(shp_path, outdir, name, code_field='code', keep_db=False, filt=None):
    shapes = read_shp(shp_path)
    fields, rows = read_dbf(shp_path[:-4] + '.dbf')
    sel = []
    for s, r in zip(shapes, rows):
        if s is None or len(s[1]) < 2: continue
        if filt and not filt(r): continue
        sel.append((s, r))
    body, offs = write_mlp([s for s, r in sel])
    entries = [(s[2], int(r[code_field].strip() or 0), off, k + 1) for k, ((s, r), off) in enumerate(zip(sel, offs))]
    idx = build_idx(entries)
    os.makedirs(outdir, exist_ok=True)
    open(os.path.join(outdir, name + '.mlp'), 'wb').write(yzl_wrap(body))
    open(os.path.join(outdir, name + '.idx'), 'wb').write(yzl_wrap(idx))
    if keep_db:
        want = [('osm_id', 'C', 12, 0), ('code', 'N', 4, 0), ('fclass', 'C', 28, 0), ('name', 'C', 100, 0)]
        rws = [{k: r.get(k, b'').strip() for k, *_ in want} for s, r in sel]
        open(os.path.join(outdir, name + '.db'), 'wb').write(yzl_wrap(write_dbf(want, rws)))
    return len(sel)

# ---------------------------------------------------------------- contours
def hgt_tile_name(lat, lon):
    return '%s%02d%s%03d' % ('N' if lat >= 0 else 'S', abs(lat), 'E' if lon >= 0 else 'W', abs(lon))

def load_hgt(path):
    import gzip
    raw = gzip.open(path).read() if path.endswith('.gz') else open(path, 'rb').read()
    n = int(round(math.sqrt(len(raw) // 2)))
    a = np.frombuffer(raw, dtype='>i2').reshape(n, n).astype(np.float32)
    if n == 3601:
        a = a[::3, ::3]          # -> 1201x1201 (3 arc-sec), same as stock maps
    a[a < -1000] = np.nan
    return a

def make_contour_tile(hgt_path, lat, lon, outroot, step=100):
    import contourpy
    a = load_hgt(hgt_path)
    n = a.shape[0]
    if np.all(np.isnan(a)) or np.nanmax(a) < step:
        return 0
    xs = lon + np.arange(n) / (n - 1)
    ys = (lat + 1) - np.arange(n) / (n - 1)
    gen = contourpy.contour_generator(xs, ys, np.ma.masked_invalid(a), line_type='Separate')
    shapes = []; elev = []
    for lv in range(step, int(np.nanmax(a)) + 1, step):
        for line in gen.lines(lv):
            if len(line) < 3: continue
            pts = np.asarray(line, dtype=np.float64)
            bbox = (pts[:, 0].min(), pts[:, 1].min(), pts[:, 0].max(), pts[:, 1].max())
            shapes.append(([0], pts, bbox)); elev.append(lv)
    if not shapes:
        return 0
    name = hgt_tile_name(lat, lon)
    od = os.path.join(outroot, name); os.makedirs(od, exist_ok=True)
    body, offs = write_mlp(shapes)
    entries = [(s[2], e, o, k + 1) for k, (s, e, o) in enumerate(zip(shapes, elev, offs))]
    open(os.path.join(od, name + '.mlp'), 'wb').write(yzl_wrap(body))
    open(os.path.join(od, name + '.idx'), 'wb').write(yzl_wrap(build_idx(entries)))
    fields = [('ID', 'N', 8, 0), ('elevation', 'N', 12, 3)]
    rows = [{'ID': str(k).encode(), 'elevation': ('%.3f' % e).encode()} for k, e in enumerate(elev)]
    open(os.path.join(od, name + '.db'), 'wb').write(yzl_wrap(write_dbf(fields, rows)))
    return len(shapes)
