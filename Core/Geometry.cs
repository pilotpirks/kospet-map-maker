namespace KospetMapMaker.Core;

public static class Geometry
{
    /// <summary>Twice the signed area (positive = counter-clockwise).</summary>
    public static double Area2(double[] r)
    {
        double a = 0;
        for (int i = 0; i + 3 < r.Length; i += 2)
            a += r[i] * r[i + 3] - r[i + 2] * r[i + 1];
        return a;
    }

    public static double[] Reverse(double[] r)
    {
        var o = new double[r.Length];
        int n = r.Length / 2;
        for (int i = 0; i < n; i++) { o[2 * i] = r[2 * (n - 1 - i)]; o[2 * i + 1] = r[2 * (n - 1 - i) + 1]; }
        return o;
    }

    public static bool PointInRing(double x, double y, double[] r)
    {
        bool inside = false;
        for (int i = 0, j = r.Length - 2; i < r.Length; j = i, i += 2)
        {
            double xi = r[i], yi = r[i + 1], xj = r[j], yj = r[j + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Shapefile-style polygon: outer rings clockwise, holes counter-clockwise, holes after their outer.</summary>
    public static Shape? Polygon(List<double[]> outers, List<double[]> inners)
    {
        if (outers.Count == 0) return null;
        var groups = new List<List<double[]>>();
        foreach (var o0 in outers)
        {
            var o = Area2(o0) > 0 ? Reverse(o0) : o0;
            groups.Add([o]);
        }
        foreach (var i0 in inners)
        {
            var r = Area2(i0) < 0 ? Reverse(i0) : i0;
            foreach (var g in groups)
                if (PointInRing(r[0], r[1], g[0])) { g.Add(r); break; }
        }
        var rings = groups.SelectMany(g => g).ToList();
        var parts = new int[rings.Count];
        int n = 0;
        for (int i = 0; i < rings.Count; i++) { parts[i] = n; n += rings[i].Length / 2; }
        var xy = new double[n * 2];
        int p = 0;
        foreach (var r in rings) { Array.Copy(r, 0, xy, p, r.Length); p += r.Length; }
        return Shape.Create(parts, xy);
    }

    /// <summary>Join way node-id sequences into closed rings (unclosable remainders are dropped).</summary>
    public static List<long[]> JoinRings(List<long[]> segs)
    {
        var rings = new List<long[]>();
        var open = new List<List<long>>();
        foreach (var s in segs)
        {
            if (s.Length < 2) continue;
            if (s[0] == s[^1] && s.Length >= 4) rings.Add(s);
            else open.Add(s.ToList());
        }
        while (open.Count > 0)
        {
            var cur = open[^1]; open.RemoveAt(open.Count - 1);
            bool changed = true;
            while (cur[0] != cur[^1] && changed)
            {
                changed = false;
                for (int i = 0; i < open.Count; i++)
                {
                    var s = open[i];
                    if (s[0] == cur[^1]) cur.AddRange(s.Skip(1));
                    else if (s[^1] == cur[^1]) { var rv = new List<long>(s); rv.Reverse(); cur.AddRange(rv.Skip(1)); }
                    else if (s[^1] == cur[0]) { var nw = new List<long>(s); nw.RemoveAt(nw.Count - 1); nw.AddRange(cur); cur = nw; }
                    else if (s[0] == cur[0]) { var rv = new List<long>(s); rv.Reverse(); rv.RemoveAt(rv.Count - 1); rv.AddRange(cur); cur = rv; }
                    else continue;
                    open.RemoveAt(i); changed = true; break;
                }
            }
            if (cur[0] == cur[^1] && cur.Count >= 4) rings.Add(cur.ToArray());
        }
        return rings;
    }

    /// <summary>Turn a polyline (lon/lat degrees) into a thin closed polygon of the given width in metres.</summary>
    public static double[]? BufferLine(double[] xy, double widthM)
    {
        // de-duplicate consecutive points
        var pts = new List<(double x, double y)>();
        for (int i = 0; i < xy.Length; i += 2)
        {
            var p = (xy[i], xy[i + 1]);
            if (pts.Count == 0 || pts[^1] != p) pts.Add(p);
        }
        int n = pts.Count;
        if (n < 2) return null;
        double lat0 = pts.Average(p => p.y) * Math.PI / 180;
        double kx = 111320 * Math.Cos(lat0), ky = 110540;
        if (kx < 1) kx = 1;
        var m = pts.Select(p => (x: p.x * kx, y: p.y * ky)).ToArray();
        double h = widthM / 2;
        var left = new (double x, double y)[n];
        var right = new (double x, double y)[n];
        for (int i = 0; i < n; i++)
        {
            // normals of adjacent segments
            (double nx, double ny) n1 = (0, 0), n2 = (0, 0);
            if (i > 0) n1 = Normal(m[i - 1], m[i]);
            if (i < n - 1) n2 = Normal(m[i], m[i + 1]);
            if (i == 0) n1 = n2;
            if (i == n - 1) n2 = n1;
            double ax = n1.nx + n2.nx, ay = n1.ny + n2.ny;
            double al = Math.Sqrt(ax * ax + ay * ay);
            double ox, oy;
            if (al < 1e-9) { ox = n2.nx * h; oy = n2.ny * h; }
            else
            {
                ax /= al; ay /= al;
                double cos = ax * n2.nx + ay * n2.ny;
                double miter = h / Math.Max(cos, 0.5);   // miter limit 2
                ox = ax * miter; oy = ay * miter;
            }
            left[i] = (m[i].x + ox, m[i].y + oy);
            right[i] = (m[i].x - ox, m[i].y - oy);
        }
        var ring = new double[(2 * n + 1) * 2];
        int k = 0;
        for (int i = 0; i < n; i++) { ring[k++] = left[i].x / kx; ring[k++] = left[i].y / ky; }
        for (int i = n - 1; i >= 0; i--) { ring[k++] = right[i].x / kx; ring[k++] = right[i].y / ky; }
        ring[k++] = ring[0]; ring[k++] = ring[1];
        return ring;
    }

    private static (double nx, double ny) Normal((double x, double y) a, (double x, double y) b)
    {
        double dx = b.x - a.x, dy = b.y - a.y;
        double l = Math.Sqrt(dx * dx + dy * dy);
        if (l < 1e-12) return (0, 0);
        return (-dy / l, dx / l);
    }
}
