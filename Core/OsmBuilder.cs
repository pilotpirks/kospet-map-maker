namespace KospetMapMaker.Core;

public sealed class BuildOptions
{
    public bool Roads = true, Paths = true, Service = true, Tracks = true;
    public bool Water = true, Rivers = true, Landuse = true, ExtraNatural = true;
    public bool Contours = true;
    public int ContourStep = 100;
    public double RiverWidthM = 25, StreamWidthM = 10;
}

public readonly record struct Cls(int Code, string FClass);

/// <summary>Geofabrik "free shapefile" classification (+ optional extras the watch already has styles for).</summary>
public static class Classes
{
    static readonly Dictionary<string, int> Road = new()
    {
        ["motorway"] = 5111, ["trunk"] = 5112, ["primary"] = 5113, ["secondary"] = 5114, ["tertiary"] = 5115,
        ["unclassified"] = 5121, ["residential"] = 5122, ["living_street"] = 5123, ["pedestrian"] = 5124,
        ["motorway_link"] = 5131, ["trunk_link"] = 5132, ["primary_link"] = 5133, ["secondary_link"] = 5134,
        ["tertiary_link"] = 5135, ["service"] = 5141, ["track"] = 5142, ["bridleway"] = 5151, ["cycleway"] = 5152,
        ["footway"] = 5153, ["path"] = 5154, ["steps"] = 5155, ["road"] = 5199,
    };

    public static Cls? RoadClass(Dictionary<string, string> t, BuildOptions o)
    {
        if (!t.TryGetValue("highway", out var h) || !Road.TryGetValue(h, out int c)) return null;
        string fc = h == "road" ? "unknown" : h;
        if (c == 5142 && t.TryGetValue("tracktype", out var tt) && tt.Length == 6 && tt.StartsWith("grade") && tt[5] >= '1' && tt[5] <= '5')
        {
            c = 5142 + (tt[5] - '0'); fc = "track_grade" + tt[5];
        }
        if (c >= 5151 && c <= 5155 && !o.Paths) return null;
        if (c == 5141 && !o.Service) return null;
        if (c >= 5142 && c <= 5147 && !o.Tracks) return null;
        return new Cls(c, fc);
    }

    public static Cls? WaterClass(Dictionary<string, string> t, BuildOptions o)
    {
        t.TryGetValue("natural", out var n); t.TryGetValue("water", out var w);
        t.TryGetValue("waterway", out var ww); t.TryGetValue("landuse", out var lu);
        if (n == "glacier") return new Cls(8211, "glacier");
        if (n == "wetland") return new Cls(8221, "wetland");
        if (ww == "riverbank") return new Cls(8202, "riverbank");
        if (ww == "dock") return new Cls(8203, "dock");
        if (lu == "reservoir" || (lu == "basin" && o.ExtraNatural) || (n == "water" && w == "reservoir")) return new Cls(8201, "reservoir");
        if (n == "water" && w == "river") return new Cls(8202, "river");
        if (n == "water") return new Cls(8200, "water");
        return null;
    }

    static readonly Dictionary<string, int> Landuse = new()
    {
        ["forest"] = 7201, ["residential"] = 7203, ["industrial"] = 7204, ["cemetery"] = 7206, ["allotments"] = 7207,
        ["meadow"] = 7208, ["commercial"] = 7209, ["recreation_ground"] = 7211, ["retail"] = 7212, ["military"] = 7213,
        ["quarry"] = 7214, ["orchard"] = 7215, ["vineyard"] = 7216, ["grass"] = 7218, ["farmyard"] = 7228, ["farmland"] = 7229,
    };
    static readonly Dictionary<string, Cls> Natural = new()
    {
        ["wood"] = new(7201, "forest"), ["scrub"] = new(7217, "scrub"), ["heath"] = new(7219, "heath"),
    };
    static readonly Dictionary<string, Cls> Leisure = new()
    {
        ["park"] = new(7202, "park"), ["common"] = new(7202, "park"), ["nature_reserve"] = new(7210, "nature_reserve"),
        ["recreation_ground"] = new(7211, "recreation_ground"),
    };
    // extras mapped onto existing styles
    static readonly Dictionary<string, Cls> ExtraLanduse = new()
    {
        ["plantation"] = new(7215, "orchard"), ["greenhouse_horticulture"] = new(7229, "farmland"),
        ["construction"] = new(7204, "industrial"), ["brownfield"] = new(7204, "industrial"),
        ["railway"] = new(7204, "industrial"), ["village_green"] = new(7218, "grass"), ["flowerbed"] = new(7218, "grass"),
        ["religious"] = new(7203, "residential"), ["education"] = new(7209, "commercial"),
    };
    static readonly Dictionary<string, Cls> ExtraNatural = new()
    {
        ["grassland"] = new(7218, "grass"), ["fell"] = new(7219, "heath"), ["tundra"] = new(7219, "heath"),
        ["bare_rock"] = new(7214, "quarry"), ["scree"] = new(7214, "quarry"), ["shingle"] = new(7214, "quarry"),
        ["rock"] = new(7214, "quarry"), ["sand"] = new(7214, "quarry"), ["beach"] = new(7214, "quarry"),
    };
    static readonly Dictionary<string, Cls> ExtraLeisure = new()
    {
        ["golf_course"] = new(7218, "grass"), ["garden"] = new(7202, "park"), ["dog_park"] = new(7202, "park"),
    };

    public static Cls? LanduseClass(Dictionary<string, string> t, BuildOptions o)
    {
        if (t.TryGetValue("landuse", out var lu) && Landuse.TryGetValue(lu, out int c)) return new Cls(c, lu);
        if (t.TryGetValue("natural", out var n) && Natural.TryGetValue(n, out var nc)) return nc;
        if (t.TryGetValue("leisure", out var l) && Leisure.TryGetValue(l, out var lc)) return lc;
        if (!o.ExtraNatural) return null;
        if (lu != null && ExtraLanduse.TryGetValue(lu, out var e1)) return e1;
        if (n != null && ExtraNatural.TryGetValue(n, out var e2)) return e2;
        if (l != null && ExtraLeisure.TryGetValue(l, out var e3)) return e3;
        return null;
    }

    /// <summary>River line width in metres, 0 = not a river line.</summary>
    public static double RiverWidth(Dictionary<string, string> t, BuildOptions o)
    {
        if (!o.Rivers || !t.TryGetValue("waterway", out var ww)) return 0;
        if (t.TryGetValue("tunnel", out var tn) && tn != "no") return 0;
        return ww switch
        {
            "river" => o.RiverWidthM,
            "canal" => o.RiverWidthM * 0.6,
            "stream" => o.StreamWidthM,
            _ => 0,
        };
    }

    public static readonly HashSet<string> Interesting =
    [
        "highway", "tracktype", "landuse", "natural", "leisure", "waterway", "water", "area", "name", "type", "tunnel"
    ];
}

public sealed class OsmResult
{
    public List<Feature> Roads = new(), Water = new(), Landuse = new();
    /// <summary>1-degree tiles (lat, lon of SW corner) touched by map features.</summary>
    public HashSet<(int lat, int lon)> Tiles = new();
}

/// <summary>Reads an .osm.pbf in three passes (relations, ways, nodes) and builds the three watch layers.</summary>
public sealed class OsmBuilder(BuildOptions opt, Action<string> log, Action<double> progress)
{
    private sealed class WayRec
    {
        public long Id;
        public long[] Refs = [];
        public Cls? Road, Water, Land;
        public double RiverW;
        public string Name = "";
    }

    private sealed class RelRec
    {
        public long Id;
        public Cls? Water, Land;
        public string Name = "";
        public List<(long way, bool inner)> Members = new();
    }

    private static Dictionary<string, string> Tags(PrimitiveBlock pb, uint[] keys, uint[] vals)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < keys.Length && i < vals.Length; i++)
        {
            var k = pb.Strings[keys[i]];
            if (Classes.Interesting.Contains(k)) d[k] = pb.Strings[vals[i]];
        }
        return d;
    }

    public OsmResult Build(string pbf, CancellationToken ct)
    {
        var rels = new List<RelRec>();
        var memberWays = new HashSet<long>();

        // ---------- pass 1: multipolygon relations
        log("OSM 1/3: relations");
        foreach (var pb in Pbf.Blocks(pbf, f => progress(f / 3), ct))
        {
            foreach (var (kind, start, len) in pb.Items)
            {
                if (kind != 4) continue;
                var r = new Pr(pb.Data, start, len);
                long id = 0; uint[] keys = [], vals = [], roles = [], types = []; long[] mem = [];
                while (r.Next())
                {
                    switch (r.Field)
                    {
                        case 1: id = (long)r.V; break;
                        case 2: keys = Pbf.PackedUInt(pb.Data, r.Start, r.Len); break;
                        case 3: vals = Pbf.PackedUInt(pb.Data, r.Start, r.Len); break;
                        case 8: roles = Pbf.PackedUInt(pb.Data, r.Start, r.Len); break;
                        case 9: mem = Pbf.PackedDelta(pb.Data, r.Start, r.Len); break;
                        case 10: types = Pbf.PackedUInt(pb.Data, r.Start, r.Len); break;
                    }
                }
                var t = Tags(pb, keys, vals);
                if (!t.TryGetValue("type", out var ty) || (ty != "multipolygon" && ty != "boundary")) continue;
                if (t.TryGetValue("area", out var ar) && ar == "no") continue;
                var w = opt.Water ? Classes.WaterClass(t, opt) : null;
                var l = opt.Landuse ? Classes.LanduseClass(t, opt) : null;
                if (w == null && l == null) continue;
                var rec = new RelRec { Id = id, Water = w, Land = l, Name = t.GetValueOrDefault("name", "") };
                for (int i = 0; i < mem.Length && i < types.Length; i++)
                {
                    if (types[i] != 1) continue;
                    bool inner = i < roles.Length && pb.Strings[roles[i]] == "inner";
                    rec.Members.Add((mem[i], inner));
                    memberWays.Add(mem[i]);
                }
                rels.Add(rec);
            }
        }
        log($"  relations: {rels.Count}, member ways: {memberWays.Count}");

        // ---------- pass 2: ways
        log("OSM 2/3: ways");
        var ways = new List<WayRec>();
        var memberRefs = new Dictionary<long, long[]>();
        var needed = new List<long>();
        foreach (var pb in Pbf.Blocks(pbf, f => progress((1 + f) / 3), ct))
        {
            foreach (var (kind, start, len) in pb.Items)
            {
                if (kind != 3) continue;
                var r = new Pr(pb.Data, start, len);
                long id = 0; uint[] keys = [], vals = []; int rs = -1, rl = 0;
                while (r.Next())
                {
                    switch (r.Field)
                    {
                        case 1: id = (long)r.V; break;
                        case 2: keys = Pbf.PackedUInt(pb.Data, r.Start, r.Len); break;
                        case 3: vals = Pbf.PackedUInt(pb.Data, r.Start, r.Len); break;
                        case 8: rs = r.Start; rl = r.Len; break;
                    }
                }
                bool isMember = memberWays.Contains(id);
                WayRec? wr = null;
                if (keys.Length > 0)
                {
                    var t = Tags(pb, keys, vals);
                    if (t.Count > 0)
                    {
                        var road = opt.Roads ? Classes.RoadClass(t, opt) : null;
                        bool areaNo = t.TryGetValue("area", out var ar) && ar == "no";
                        var water = opt.Water && !areaNo ? Classes.WaterClass(t, opt) : null;
                        var land = opt.Landuse && !areaNo ? Classes.LanduseClass(t, opt) : null;
                        double rw = opt.Water ? Classes.RiverWidth(t, opt) : 0;
                        if (road != null || water != null || land != null || rw > 0)
                            wr = new WayRec { Id = id, Road = road, Water = water, Land = land, RiverW = rw, Name = t.GetValueOrDefault("name", "") };
                    }
                }
                if (wr == null && !isMember) continue;
                var refs = rs >= 0 ? Pbf.PackedDelta(pb.Data, rs, rl) : Array.Empty<long>();
                if (wr != null) { wr.Refs = refs; ways.Add(wr); }
                if (isMember) memberRefs[id] = refs;
                needed.AddRange(refs);
            }
        }
        log($"  ways kept: {ways.Count}");

        // ---------- pass 3: nodes (only the needed ones)
        log("OSM 3/3: nodes");
        needed.Sort();
        var ids = Dedup(needed);
        needed = null!;
        var lat = new int[ids.Length];
        var lon = new int[ids.Length];
        var found = new bool[ids.Length];
        foreach (var pb in Pbf.Blocks(pbf, f => progress((2 + f) / 3), ct))
        {
            foreach (var (kind, start, len) in pb.Items)
            {
                if (kind == 2)
                {
                    var r = new Pr(pb.Data, start, len);
                    long[] nid = [], la = [], lo = [];
                    while (r.Next())
                    {
                        if (r.Field == 1) nid = Pbf.PackedDelta(pb.Data, r.Start, r.Len);
                        else if (r.Field == 8) la = Pbf.PackedDelta(pb.Data, r.Start, r.Len);
                        else if (r.Field == 9) lo = Pbf.PackedDelta(pb.Data, r.Start, r.Len);
                    }
                    for (int i = 0; i < nid.Length; i++)
                    {
                        int k = Array.BinarySearch(ids, nid[i]);
                        if (k < 0) continue;
                        lat[k] = (int)((pb.LatOffset + pb.Granularity * la[i]) / 100);
                        lon[k] = (int)((pb.LonOffset + pb.Granularity * lo[i]) / 100);
                        found[k] = true;
                    }
                }
                else if (kind == 1)
                {
                    var r = new Pr(pb.Data, start, len);
                    long id = 0, la = 0, lo = 0;
                    while (r.Next())
                    {
                        if (r.Field == 1) id = Pbf.ZigZag(r.V);
                        else if (r.Field == 8) la = Pbf.ZigZag(r.V);
                        else if (r.Field == 9) lo = Pbf.ZigZag(r.V);
                    }
                    int k = Array.BinarySearch(ids, id);
                    if (k < 0) continue;
                    lat[k] = (int)((pb.LatOffset + pb.Granularity * la) / 100);
                    lon[k] = (int)((pb.LonOffset + pb.Granularity * lo) / 100);
                    found[k] = true;
                }
            }
        }
        log($"  nodes: {ids.Length}");

        // returns interleaved xy; allOk = every node found
        double[] Coords(long[] refs, out bool allOk)
        {
            var xy = new List<double>(refs.Length * 2);
            allOk = true;
            foreach (var id in refs)
            {
                int k = Array.BinarySearch(ids, id);
                if (k < 0 || !found[k]) { allOk = false; continue; }
                xy.Add(lon[k] / 1e7); xy.Add(lat[k] / 1e7);
            }
            return xy.ToArray();
        }

        var res = new OsmResult();
        foreach (var w in ways)
        {
            ct.ThrowIfCancellationRequested();
            var xy = Coords(w.Refs, out bool ok);
            if (xy.Length < 4) continue;
            if (w.Road is { } rc)
                res.Roads.Add(new Feature { Code = rc.Code, FClass = rc.FClass, OsmId = w.Id, Name = w.Name, Shape = Shape.Create([0], xy) });
            bool closed = w.Refs.Length >= 4 && w.Refs[0] == w.Refs[^1] && ok;
            if (closed && (w.Water != null || w.Land != null))
            {
                var poly = Geometry.Polygon([xy], []);
                if (poly != null)
                {
                    if (w.Water is { } wc) res.Water.Add(new Feature { Code = wc.Code, FClass = wc.FClass, OsmId = w.Id, Name = w.Name, Shape = poly });
                    if (w.Land is { } lc) res.Landuse.Add(new Feature { Code = lc.Code, FClass = lc.FClass, OsmId = w.Id, Name = w.Name, Shape = poly });
                }
            }
            if (w.RiverW > 0)
            {
                var ring = Geometry.BufferLine(xy, w.RiverW);
                if (ring != null)
                    res.Water.Add(new Feature { Code = 8202, FClass = "river", OsmId = w.Id, Name = w.Name, Shape = Shape.Create([0], ring) });
            }
        }
        foreach (var rel in rels)
        {
            ct.ThrowIfCancellationRequested();
            var outerSegs = rel.Members.Where(m => !m.inner && memberRefs.ContainsKey(m.way)).Select(m => memberRefs[m.way]).ToList();
            var innerSegs = rel.Members.Where(m => m.inner && memberRefs.ContainsKey(m.way)).Select(m => memberRefs[m.way]).ToList();
            var outers = new List<double[]>(); var inners = new List<double[]>();
            foreach (var ring in Geometry.JoinRings(outerSegs)) { var xy = Coords(ring, out bool ok); if (ok) outers.Add(xy); }
            foreach (var ring in Geometry.JoinRings(innerSegs)) { var xy = Coords(ring, out bool ok); if (ok) inners.Add(xy); }
            var poly = Geometry.Polygon(outers, inners);
            if (poly == null) continue;
            if (rel.Water is { } wc) res.Water.Add(new Feature { Code = wc.Code, FClass = wc.FClass, OsmId = rel.Id, Name = rel.Name, Shape = poly });
            if (rel.Land is { } lc) res.Landuse.Add(new Feature { Code = lc.Code, FClass = lc.FClass, OsmId = rel.Id, Name = rel.Name, Shape = poly });
        }
        foreach (var f in res.Roads.Concat(res.Water).Concat(res.Landuse))
        {
            var s = f.Shape;
            int la0 = (int)Math.Floor(s.MinY), la1 = (int)Math.Floor(s.MaxY);
            int lo0 = (int)Math.Floor(s.MinX), lo1 = (int)Math.Floor(s.MaxX);
            if ((la1 - la0 + 1) * (lo1 - lo0 + 1) > 400) continue;   // ignore gigantic outliers
            for (int a = la0; a <= la1; a++) for (int b = lo0; b <= lo1; b++) res.Tiles.Add((a, b));
        }
        log($"  features: roads {res.Roads.Count}, water {res.Water.Count}, landuse {res.Landuse.Count}");
        return res;
    }

    private static long[] Dedup(List<long> sorted)
    {
        var o = new List<long>(sorted.Count);
        long prev = long.MinValue;
        foreach (var v in sorted) if (v != prev) { o.Add(v); prev = v; }
        return o.ToArray();
    }
}
