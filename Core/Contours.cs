using System.IO.Compression;

namespace KospetMapMaker.Core;

/// <summary>SRTM .hgt -> contour lines (marching squares) -> watch contour tile (mlp/idx/db).</summary>
public static class Contours
{
    public static string TileName(int lat, int lon) =>
        $"{(lat >= 0 ? 'N' : 'S')}{Math.Abs(lat):D2}{(lon >= 0 ? 'E' : 'W')}{Math.Abs(lon):D3}";

    /// <summary>Loads .hgt or .hgt.gz; 1" tiles are thinned to 3" (1201x1201) like the stock maps. Voids = NaN.</summary>
    public static (float[] grid, int n) LoadHgt(string path)
    {
        byte[] raw;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            raw = ms.ToArray();
        }
        else raw = File.ReadAllBytes(path);
        int n = (int)Math.Round(Math.Sqrt(raw.Length / 2.0));
        int step = n == 3601 ? 3 : 1;
        int m = (n - 1) / step + 1;
        var g = new float[m * m];
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                int o = 2 * ((j * step) * n + i * step);
                short v = (short)((raw[o] << 8) | raw[o + 1]);
                g[j * m + i] = v < -1000 ? float.NaN : v;
            }
        return (g, m);
    }

    /// <summary>Contours for one tile. Returns (elevation, interleaved lon/lat polyline).</summary>
    public static List<(int elev, double[] xy)> Trace(float[] g, int n, int lat, int lon, int step)
    {
        // collect segments per level: each segment joins two edge ids
        var segs = new Dictionary<int, List<(int a, int b)>>();
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                float a = g[j * n + i], b = g[j * n + i + 1], c = g[(j + 1) * n + i + 1], d = g[(j + 1) * n + i];
                if (float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c) || float.IsNaN(d)) continue;
                float mn = Math.Min(Math.Min(a, b), Math.Min(c, d));
                float mx = Math.Max(Math.Max(a, b), Math.Max(c, d));
                int L = (int)Math.Floor(mn / step) * step + step;
                if (L < step) L = step;                     // only positive levels
                for (; L <= mx; L += step)
                {
                    if (L <= mn) continue;
                    int idx = (a >= L ? 8 : 0) | (b >= L ? 4 : 0) | (c >= L ? 2 : 0) | (d >= L ? 1 : 0);
                    if (idx == 0 || idx == 15) continue;
                    int top = j * n + i, bottom = (j + 1) * n + i;
                    int left = n * n + j * n + i, right = n * n + j * n + i + 1;
                    if (!segs.TryGetValue(L, out var list)) segs[L] = list = new List<(int, int)>();
                    float center = (a + b + c + d) / 4f;
                    switch (idx)
                    {
                        case 1: case 14: list.Add((left, bottom)); break;
                        case 2: case 13: list.Add((bottom, right)); break;
                        case 3: case 12: list.Add((left, right)); break;
                        case 4: case 11: list.Add((top, right)); break;
                        case 6: case 9: list.Add((top, bottom)); break;
                        case 7: case 8: list.Add((left, top)); break;
                        case 5: // b,d high
                            if (center >= L) { list.Add((left, top)); list.Add((bottom, right)); }
                            else { list.Add((top, right)); list.Add((left, bottom)); }
                            break;
                        case 10: // a,c high
                            if (center >= L) { list.Add((left, bottom)); list.Add((top, right)); }
                            else { list.Add((left, top)); list.Add((bottom, right)); }
                            break;
                    }
                }
            }
        }

        var res = new List<(int, double[])>();
        double inv = 1.0 / (n - 1);
        foreach (var (L, list) in segs.OrderBy(kv => kv.Key))
        {
            // adjacency: edge -> up to two neighbour edges
            var adj = new Dictionary<int, (int n1, int n2)>(list.Count * 2);
            void Link(int e, int o)
            {
                if (adj.TryGetValue(e, out var v)) adj[e] = (v.n1, o);
                else adj[e] = (o, -1);
            }
            foreach (var (s1, s2) in list) { Link(s1, s2); Link(s2, s1); }
            var used = new HashSet<int>();

            (double x, double y) Pt(int e)
            {
                int j, i, j2, i2;
                if (e < n * n) { j = e / n; i = e % n; j2 = j; i2 = i + 1; }
                else { int q = e - n * n; j = q / n; i = q % n; j2 = j + 1; i2 = i; }
                float v0 = g[j * n + i], v1 = g[j2 * n + i2];
                double t = v1 == v0 ? 0.5 : (L - v0) / (double)(v1 - v0);
                double fi = i + (i2 - i) * t, fj = j + (j2 - j) * t;
                return (lon + fi * inv, lat + 1 - fj * inv);
            }

            void Walk(int start)
            {
                var chain = new List<int> { start };
                used.Add(start);
                int prev = -1, cur = start;
                while (true)
                {
                    var (a1, a2) = adj[cur];
                    int nx = a1 != prev && !used.Contains(a1) ? a1 : (a2 >= 0 && a2 != prev && !used.Contains(a2) ? a2 : -1);
                    if (nx < 0)
                    {
                        // close a loop if we came back to the start
                        if (chain.Count > 2 && (a1 == start || a2 == start)) chain.Add(start);
                        break;
                    }
                    chain.Add(nx); used.Add(nx); prev = cur; cur = nx;
                }
                if (chain.Count < 3) return;
                var xy = new double[chain.Count * 2];
                for (int k = 0; k < chain.Count; k++) { var p = Pt(chain[k]); xy[2 * k] = p.x; xy[2 * k + 1] = p.y; }
                res.Add((L, xy));
            }

            foreach (var kv in adj) if (kv.Value.n2 < 0 && !used.Contains(kv.Key)) Walk(kv.Key);   // open chains first
            foreach (var kv in adj) if (!used.Contains(kv.Key)) Walk(kv.Key);                     // then loops
        }
        return res;
    }

    public static readonly DbfField[] Fields = [new("ID", 'N', 8, 0), new("elevation", 'N', 12, 3)];

    /// <summary>Builds one contour tile folder. Returns number of contour lines (0 = nothing written).</summary>
    public static int MakeTile(string hgtPath, int lat, int lon, string outRoot, int step)
    {
        var (g, n) = LoadHgt(hgtPath);
        float mx = float.MinValue;
        foreach (var v in g) if (!float.IsNaN(v) && v > mx) mx = v;
        if (mx < step) return 0;
        var lines = Trace(g, n, lat, lon, step);
        if (lines.Count == 0) return 0;
        var name = TileName(lat, lon);
        var dir = Path.Combine(outRoot, name);
        Directory.CreateDirectory(dir);
        var shapes = lines.Select(l => Shape.Create([0], l.xy)).ToList();
        var body = Yzl.BuildMlp(shapes, out var offs);
        var entries = new List<Yzl.IdxEntry>(shapes.Count);
        for (int k = 0; k < shapes.Count; k++)
        {
            var s = shapes[k];
            entries.Add(new Yzl.IdxEntry((float)s.MinX, (float)s.MinY, (float)s.MaxX, (float)s.MaxY, lines[k].elev, offs[k], k + 1));
        }
        Yzl.WriteFile(Path.Combine(dir, name + ".mlp"), body);
        Yzl.WriteFile(Path.Combine(dir, name + ".idx"), Yzl.BuildIdx(entries));
        var rows = lines.Select((l, k) => new[] { k.ToString(), l.elev.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) });
        Yzl.WriteFile(Path.Combine(dir, name + ".db"), Yzl.BuildDbf(Fields, rows));
        return lines.Count;
    }
}
