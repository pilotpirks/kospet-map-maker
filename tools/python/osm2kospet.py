#!/usr/bin/env python3
"""OSM .pbf -> Kospet map layers (roads / water / landuse) with Geofabrik code scheme.
usage: python3 osm2kospet.py input.osm.pbf outdir
"""
import sys, os, struct, zlib, time
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kospet_maps as K

# ------------------------------------------------------------------ protobuf helpers
def rv(b, p):
    """read varint -> (value, newpos)"""
    r = 0; s = 0
    while True:
        c = b[p]; p += 1
        r |= (c & 0x7F) << s
        if c < 0x80: return r, p
        s += 7

def fields(b):
    """iterate (fieldno, wiretype, value) ; value = int or memoryview slice"""
    p = 0; n = len(b)
    while p < n:
        k, p = rv(b, p)
        f, t = k >> 3, k & 7
        if t == 0:
            v, p = rv(b, p)
        elif t == 2:
            l, p = rv(b, p); v = b[p:p + l]; p += l
        elif t == 1:
            v = b[p:p + 8]; p += 8
        elif t == 5:
            v = b[p:p + 4]; p += 4
        else:
            raise ValueError('wiretype %d' % t)
        yield f, t, v

def np_varints(buf):
    a = np.frombuffer(buf, dtype=np.uint8)
    if a.size == 0: return np.zeros(0, np.uint64)
    ends = a < 0x80
    starts = np.empty(a.size, bool); starts[0] = True; starts[1:] = ends[:-1]
    sidx = np.flatnonzero(starts)
    grp = np.cumsum(starts) - 1
    pos = np.arange(a.size) - sidx[grp]
    sh = (a & 0x7F).astype(np.uint64) << (7 * pos).astype(np.uint64)
    return np.bitwise_or.reduceat(sh, sidx)

def zz(u):  # zigzag decode uint64 array -> int64
    u = u.astype(np.uint64)
    return (u >> np.uint64(1)).astype(np.int64) ^ -(u & np.uint64(1)).astype(np.int64)

def py_varints(b):
    out = []; p = 0; n = len(b)
    while p < n:
        v, p = rv(b, p); out.append(v)
    return out

def blocks(path):
    with open(path, 'rb') as f:
        while True:
            h = f.read(4)
            if len(h) < 4: return
            hl = struct.unpack('>I', h)[0]
            hdr = f.read(hl); typ = None; size = 0
            for fn, t, v in fields(memoryview(hdr)):
                if fn == 1: typ = bytes(v).decode()
                elif fn == 3: size = v
            blob = f.read(size)
            if typ != 'OSMData': continue
            raw = None
            for fn, t, v in fields(memoryview(blob)):
                if fn == 1: raw = bytes(v)
                elif fn == 3: raw = zlib.decompress(v)
            yield raw

def parse_block(raw, want):
    """want: set of 'dense','ways','relations'. yields (kind, data, strings, gran, latoff, lonoff)"""
    mv = memoryview(raw)
    st = []; groups = []; gran = 100; lato = 0; lono = 0
    for fn, t, v in fields(mv):
        if fn == 1:
            st = [bytes(s).decode('utf-8', 'replace') for f2, t2, s in fields(v) if f2 == 1]
        elif fn == 2: groups.append(v)
        elif fn == 17: gran = v
        elif fn == 19: lato = v
        elif fn == 20: lono = v
    for g in groups:
        for fn, t, v in fields(g):
            if fn == 2 and 'dense' in want: yield 'dense', v, st, gran, lato, lono
            elif fn == 3 and 'ways' in want: yield 'way', v, st, gran, lato, lono
            elif fn == 4 and 'relations' in want: yield 'rel', v, st, gran, lato, lono
            elif fn == 1 and 'dense' in want: yield 'node', v, st, gran, lato, lono

def tags_of(keys, vals, st):
    return {st[k]: st[v] for k, v in zip(keys, vals)}

# ------------------------------------------------------------------ Geofabrik classes
ROAD = {'motorway': 5111, 'trunk': 5112, 'primary': 5113, 'secondary': 5114, 'tertiary': 5115,
        'unclassified': 5121, 'residential': 5122, 'living_street': 5123, 'pedestrian': 5124,
        'motorway_link': 5131, 'trunk_link': 5132, 'primary_link': 5133, 'secondary_link': 5134,
        'tertiary_link': 5135, 'service': 5141, 'track': 5142, 'bridleway': 5151, 'cycleway': 5152,
        'footway': 5153, 'path': 5154, 'steps': 5155, 'road': 5199}
ROAD_FC = {v: k for k, v in ROAD.items()}; ROAD_FC[5199] = 'unknown'
for g in range(1, 6): ROAD_FC[5142 + g] = 'track_grade%d' % g

def road_class(t):
    h = t.get('highway')
    if h not in ROAD: return None
    c = ROAD[h]
    if c == 5142:
        tt = t.get('tracktype', '')
        if tt in ('grade1', 'grade2', 'grade3', 'grade4', 'grade5'): c = 5142 + int(tt[-1])
    return c, ROAD_FC[c]

def water_class(t):
    n = t.get('natural'); w = t.get('water'); ww = t.get('waterway'); lu = t.get('landuse')
    if n == 'glacier': return 8211, 'glacier'
    if n == 'wetland': return 8221, 'wetland'
    if ww == 'riverbank': return 8202, 'riverbank'
    if ww == 'dock': return 8203, 'dock'
    if lu == 'reservoir' or (n == 'water' and w == 'reservoir'): return 8201, 'reservoir'
    if n == 'water' and w == 'river': return 8202, 'river'
    if n == 'water': return 8200, 'water'
    return None

LANDUSE = {'forest': 7201, 'residential': 7203, 'industrial': 7204, 'cemetery': 7206, 'allotments': 7207,
           'meadow': 7208, 'commercial': 7209, 'recreation_ground': 7211, 'retail': 7212, 'military': 7213,
           'quarry': 7214, 'orchard': 7215, 'vineyard': 7216, 'grass': 7218, 'farmyard': 7228, 'farmland': 7229}
NATURAL = {'wood': (7201, 'forest'), 'scrub': (7217, 'scrub'), 'heath': (7219, 'heath')}
LEISURE = {'park': (7202, 'park'), 'common': (7202, 'park'), 'nature_reserve': (7210, 'nature_reserve'),
           'recreation_ground': (7211, 'recreation_ground')}

def landuse_class(t):
    lu = t.get('landuse')
    if lu in LANDUSE: return LANDUSE[lu], lu
    if t.get('natural') in NATURAL: return NATURAL[t['natural']]
    if t.get('leisure') in LEISURE: return LEISURE[t['leisure']]
    return None

def area_classes(t):
    if t.get('area') == 'no': return None, None
    return water_class(t), landuse_class(t)

# ------------------------------------------------------------------ geometry
def ring_area2(xy):
    x, y = xy[:, 0], xy[:, 1]
    return float(np.dot(x[:-1], y[1:]) - np.dot(x[1:], y[:-1]))

def pip(pt, ring):
    x, y = pt; xs = ring[:, 0]; ys = ring[:, 1]
    x1, y1, x2, y2 = xs[:-1], ys[:-1], xs[1:], ys[1:]
    c = ((y1 > y) != (y2 > y))
    with np.errstate(divide='ignore', invalid='ignore'):
        xi = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
    return bool(np.count_nonzero(c & (x < xi)) & 1)

def join_rings(segs):
    """segs: list of int64 node-id arrays -> list of closed id arrays"""
    segs = [s for s in segs if len(s) >= 2]
    rings = []; open_ = []
    for s in segs:
        (rings if s[0] == s[-1] and len(s) >= 4 else open_).append(list(s))
    while open_:
        cur = open_.pop()
        changed = True
        while cur[0] != cur[-1] and changed:
            changed = False
            for i, s in enumerate(open_):
                if s[0] == cur[-1]: cur += s[1:]
                elif s[-1] == cur[-1]: cur += s[::-1][1:]
                elif s[-1] == cur[0]: cur = s[:-1] + cur
                elif s[0] == cur[0]: cur = s[::-1][:-1] + cur
                else: continue
                open_.pop(i); changed = True; break
        if cur[0] == cur[-1] and len(cur) >= 4: rings.append(cur)
    return [np.array(r, np.int64) for r in rings]

def polygon(outers, inners):
    """outers/inners: lists of Nx2 float arrays (closed). -> (parts, pts, bbox) shapefile style"""
    outs = []
    for o in outers:
        if ring_area2(o) > 0: o = o[::-1]       # outer clockwise
        outs.append([o])
    for r in inners:
        if ring_area2(r) < 0: r = r[::-1]       # inner counter-clockwise
        for grp in outs:
            if pip(tuple(r[0]), grp[0]): grp.append(r); break
    rings = [r for g in outs for r in g]
    parts = []; n = 0
    for r in rings: parts.append(n); n += len(r)
    pts = np.vstack(rings)
    bbox = (pts[:, 0].min(), pts[:, 1].min(), pts[:, 0].max(), pts[:, 1].max())
    return parts, pts, bbox

# ------------------------------------------------------------------ main
def main(pbf, outdir):
    t0 = time.time()
    log = lambda *a: print('[%5.0fs]' % (time.time() - t0), *a, flush=True)
    # pass 1: relations
    mp = []            # (rid, tags, [(wid, role)])
    member_ways = set()
    for raw in blocks(pbf):
        for kind, v, st, *_ in parse_block(raw, {'relations'}):
            keys = vals = roles = memids = types = b''; rid = 0
            for fn, t, x in fields(v):
                if fn == 1: rid = x
                elif fn == 2: keys = x
                elif fn == 3: vals = x
                elif fn == 8: roles = x
                elif fn == 9: memids = x
                elif fn == 10: types = x
            tg = tags_of(py_varints(keys), py_varints(vals), st)
            if tg.get('type') not in ('multipolygon', 'boundary'): continue
            w, l = area_classes(tg)
            if not w and not l: continue
            ids = np.cumsum(zz(np.array(py_varints(memids), np.uint64))) if len(memids) else []
            rl = py_varints(roles); ty = py_varints(types)
            mem = [(int(i), st[r]) for i, r, y in zip(ids, rl, ty) if y == 1]
            mp.append((rid, tg, mem)); member_ways.update(m[0] for m in mem)
    log('relations', len(mp), 'member ways', len(member_ways))

    # pass 2: nodes + ways
    nid = []; nlat = []; nlon = []
    roads = []; areas = []; mways = {}
    nw = 0
    for raw in blocks(pbf):
        for kind, v, st, gran, lato, lono in parse_block(raw, {'dense', 'ways'}):
            if kind == 'dense':
                d = {}
                for fn, t, x in fields(v):
                    if fn in (1, 8, 9): d[fn] = x
                ids = np.cumsum(zz(np_varints(d[1])))
                la = np.cumsum(zz(np_varints(d[8]))); lo = np.cumsum(zz(np_varints(d[9])))
                nid.append(ids)
                nlat.append(((lato + gran * la) // 100).astype(np.int64))   # 1e-7 deg
                nlon.append(((lono + gran * lo) // 100).astype(np.int64))
            elif kind == 'way':
                nw += 1
                wid = 0; keys = vals = refs = b''
                for fn, t, x in fields(v):
                    if fn == 1: wid = x
                    elif fn == 2: keys = x
                    elif fn == 3: vals = x
                    elif fn == 8: refs = x
                tg = tags_of(py_varints(keys), py_varints(vals), st) if len(keys) else {}
                rc = road_class(tg) if tg else None
                wc, lc = area_classes(tg) if tg else (None, None)
                ism = wid in member_ways
                if not (rc or wc or lc or ism): continue
                r = np.cumsum(zz(np_varints(refs)))
                if rc: roads.append((wid, rc, tg.get('name', ''), r))
                if (wc or lc) and len(r) >= 4 and r[0] == r[-1]:
                    areas.append((wid, wc, lc, tg.get('name', ''), [r], []))
                if ism: mways[wid] = r
            elif kind == 'node':
                pass
    log('ways seen', nw, 'roads', len(roads), 'area ways', len(areas))
    nid = np.concatenate(nid); nlat = np.concatenate(nlat); nlon = np.concatenate(nlon)
    o = np.argsort(nid, kind='stable'); nid = nid[o]; nlat = nlat[o]; nlon = nlon[o]
    log('nodes', len(nid))

    def coords(ids):
        i = np.searchsorted(nid, ids); i[i >= len(nid)] = 0
        ok = nid[i] == ids
        xy = np.column_stack([nlon[i] / 1e7, nlat[i] / 1e7])
        return xy, ok

    for rid, tg, mem in mp:
        w, l = area_classes(tg)
        outer = [mways[m] for m, role in mem if role != 'inner' and m in mways]
        inner = [mways[m] for m, role in mem if role == 'inner' and m in mways]
        areas.append((rid, w, l, tg.get('name', ''), join_rings(outer), join_rings(inner)))
    log('areas total', len(areas))

    # ---- build layers
    out_road = []
    for wid, (code, fc), name, r in roads:
        xy, ok = coords(r); xy = xy[ok]
        if len(xy) < 2: continue
        out_road.append(((code, fc, wid, name), ([0], xy, (xy[:, 0].min(), xy[:, 1].min(), xy[:, 0].max(), xy[:, 1].max()))))
    out_w = []; out_l = []
    for aid, wc, lc, name, outs, ins in areas:
        O = []; I = []
        for rg, dst in ((outs, O), (ins, I)):
            for r in rg:
                xy, ok = coords(r)
                if ok.all(): dst.append(xy)
        if not O: continue
        shp = polygon(O, I)
        if wc: out_w.append(((wc[0], wc[1], aid, name), shp))
        if lc: out_l.append(((lc[0], lc[1], aid, name), shp))
    log('features roads', len(out_road), 'water', len(out_w), 'landuse', len(out_l))
    for name, feats, db in (('roads', out_road, False), ('water', out_w, False), ('landuse', out_l, True)):
        write_layer(outdir, name, feats, db); log('wrote', name)

def write_layer(outdir, name, feats, keep_db):
    os.makedirs(outdir, exist_ok=True)
    body, offs = K.write_mlp([s for a, s in feats])
    entries = [(s[2], a[0], off, k + 1) for k, ((a, s), off) in enumerate(zip(feats, offs))]
    open(os.path.join(outdir, name + '.mlp'), 'wb').write(K.yzl_wrap(body))
    open(os.path.join(outdir, name + '.idx'), 'wb').write(K.yzl_wrap(K.build_idx(entries)))
    if keep_db:
        fl = [('osm_id', 'C', 12, 0), ('code', 'N', 4, 0), ('fclass', 'C', 28, 0), ('name', 'C', 100, 0)]
        rows = []
        for (code, fc, oid, nm), s in feats:
            nb = nm.encode('utf-8')[:100].decode('utf-8', 'ignore').encode('utf-8')
            rows.append({'osm_id': str(oid).encode(), 'code': str(code).encode(), 'fclass': fc.encode(), 'name': nb})
        open(os.path.join(outdir, name + '.db'), 'wb').write(K.yzl_wrap(K.write_dbf(fl, rows)))

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
