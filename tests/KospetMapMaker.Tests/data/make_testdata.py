#!/usr/bin/env python3
"""Generates the synthetic test extract `test.osm.pbf` (a fictional small town near Kaliningrad).

    python3 make_testdata.py            # run from this folder

The golden files in expected/ correspond to this exact extract (rivers and extra classes disabled);
if you change the data here, rebuild them as well.
"""
import os, struct, zlib

HERE = os.path.dirname(os.path.abspath(__file__))

# ------------------------------------------------------------------ protobuf writer
def varint(v):
    out = bytearray()
    while True:
        b = v & 0x7F; v >>= 7
        if v: out.append(b | 0x80)
        else: out.append(b); return bytes(out)

def zz(v): return (v << 1) ^ (v >> 63)
def key(f, wt): return varint((f << 3) | wt)
def f_varint(f, v): return key(f, 0) + varint(v)
def f_bytes(f, b): return key(f, 2) + varint(len(b)) + b
def packed(f, vals): return f_bytes(f, b''.join(varint(v) for v in vals)) if vals else b''
def packed_delta(f, vals):
    prev = 0; out = []
    for v in vals: out.append(zz(v - prev)); prev = v
    return packed(f, out)

def blob(typ, payload):
    b = f_varint(2, len(payload)) + f_bytes(3, zlib.compress(payload))
    hdr = f_bytes(1, typ.encode()) + f_varint(3, len(b))
    return struct.pack('>I', len(hdr)) + hdr + b

# ------------------------------------------------------------------ data
LAT0, LON0 = 54.70, 20.50
nodes = {}          # id -> (lat, lon)
_next = [1000]
def node(dlat, dlon):
    _next[0] += 1
    nodes[_next[0]] = (LAT0 + dlat, LON0 + dlon)
    return _next[0]
def line(*pts): return [node(a, b) for a, b in pts]
def ring(*pts):
    ids = line(*pts); return ids + [ids[0]]

ways = []           # (id, tags, refs)
rels = []           # (id, tags, [(type, ref, role)])
_wid = [500]
def way(tags, refs):
    _wid[0] += 1; ways.append((_wid[0], tags, refs)); return _wid[0]

# roads of every kind
way({'highway': 'primary', 'name': 'Ленинский проспект'}, line((0, 0), (0.002, 0.003), (0.004, 0.006), (0.006, 0.009), (0.008, 0.012)))
for k in range(4):
    way({'highway': 'residential'}, line((0.001 * k, 0.010), (0.001 * k + 0.0005, 0.013), (0.001 * k + 0.001, 0.016)))
way({'highway': 'track', 'tracktype': 'grade3'}, line((-0.003, 0), (-0.004, 0.004), (-0.006, 0.007)))
way({'highway': 'track'}, line((-0.006, 0.007), (-0.008, 0.009)))
way({'highway': 'footway'}, line((0.003, 0.001), (0.0035, 0.002)))
way({'highway': 'service'}, line((0.005, 0.002), (0.0052, 0.0025), (0.0055, 0.0026)))
way({'highway': 'motorway_link'}, line((0.009, 0.0), (0.010, 0.002)))
way({'highway': 'road'}, line((0.011, 0.0), (0.012, 0.001)))
way({'highway': 'proposed'}, line((0.013, 0.0), (0.014, 0.001)))          # must be skipped
way({'highway': 'residential'}, [node(0.02, 0.02)])                          # 1 point: skipped

# areas from closed ways
way({'natural': 'water', 'name': 'Верхнее озеро'}, ring((0.010, 0.010), (0.010, 0.014), (0.013, 0.014), (0.013, 0.010)))
way({'landuse': 'reservoir'}, ring((0.015, 0.010), (0.015, 0.012), (0.016, 0.011)))
way({'natural': 'wetland'}, ring((0.017, 0.010), (0.017, 0.013), (0.019, 0.012)))
way({'landuse': 'forest', 'natural': 'wetland'}, ring((0.020, 0.010), (0.020, 0.012), (0.022, 0.011)))   # both layers
way({'leisure': 'park', 'name': 'Парк Победы'}, ring((-0.002, 0.010), (-0.002, 0.012), (-0.004, 0.012), (-0.004, 0.010)))
way({'landuse': 'farmland'}, ring((-0.006, 0.010), (-0.006, 0.014), (-0.009, 0.014), (-0.009, 0.010)))
way({'landuse': 'grass', 'area': 'no'}, ring((-0.010, 0.0), (-0.010, 0.001), (-0.011, 0.001)))         # area=no: skipped
way({'natural': 'grassland'}, ring((-0.012, 0.0), (-0.012, 0.002), (-0.013, 0.002)))                    # extra class only
way({'building': 'yes'}, ring((0.001, 0.001), (0.001, 0.0012), (0.0012, 0.0012)))                       # ignored
way({'waterway': 'river', 'name': 'Преголя'}, line((0.0, -0.010), (0.004, -0.008), (0.008, -0.009), (0.012, -0.007)))  # rivers option only

# multipolygon: forest, outer from two open ways (second one reversed), inner hole
a = line((0.030, 0.000), (0.030, 0.010), (0.040, 0.010))
# outer: a goes (0,0) -> (0,10) -> (10,10); b closes (10,10) -> (10,0) -> (5,-2) -> a[0], stored reversed
b_ids = [a[-1], node(0.040, 0.000), node(0.035, -0.002), a[0]]
w_outer1 = way({}, a)
w_outer2 = way({}, list(reversed(b_ids)))
w_inner = way({}, ring((0.033, 0.003), (0.033, 0.006), (0.036, 0.006), (0.036, 0.003)))
rels.append((9001, {'type': 'multipolygon', 'landuse': 'forest', 'name': 'Бор'},
             [(1, w_outer1, 'outer'), (1, w_outer2, 'outer'), (1, w_inner, 'inner')]))
# multipolygon water with one missing member (outside the extract)
w_lake = way({}, ring((0.050, 0.0), (0.050, 0.004), (0.053, 0.004), (0.053, 0.0)))
rels.append((9002, {'type': 'multipolygon', 'natural': 'water'}, [(1, w_lake, 'outer'), (1, 999999, 'outer')]))
# relation of another type: ignored
rels.append((9003, {'type': 'route', 'route': 'bus'}, [(1, w_lake, '')]))

# ------------------------------------------------------------------ encode
strings = ['']
sidx = {}
def s(x):
    if x not in sidx: sidx[x] = len(strings); strings.append(x)
    return sidx[x]

ids = sorted(nodes)
dense = packed_delta(1, ids) + packed_delta(8, [round(nodes[i][0] * 1e7) for i in ids]) \
    + packed_delta(9, [round(nodes[i][1] * 1e7) for i in ids])
g_nodes = f_bytes(2, dense)
g_ways = b''
for wid, tags, refs in ways:
    g_ways += f_bytes(3, f_varint(1, wid) + packed(2, [s(k) for k in tags]) + packed(3, [s(v) for v in tags.values()])
                      + packed_delta(8, refs))
g_rels = b''
for rid, tags, mem in rels:
    g_rels += f_bytes(4, f_varint(1, rid) + packed(2, [s(k) for k in tags]) + packed(3, [s(v) for v in tags.values()])
                      + packed(8, [s(r) for _, _, r in mem]) + packed_delta(9, [m for _, m, _ in mem])
                      + packed(10, [t for t, _, _ in mem]))
st = f_bytes(1, b''.join(f_bytes(1, x.encode()) for x in strings))
block = st + f_bytes(2, g_nodes) + f_bytes(2, g_ways) + f_bytes(2, g_rels)   # granularity default 100

header = f_bytes(4, b'OsmSchema-V0.6') + f_bytes(4, b'DenseNodes')
pbf = os.path.join(HERE, 'test.osm.pbf')
with open(pbf, 'wb') as f:
    f.write(blob('OSMHeader', header))
    f.write(blob('OSMData', block))
print('wrote', pbf, os.path.getsize(pbf), 'bytes,', len(nodes), 'nodes', len(ways), 'ways', len(rels), 'relations')
