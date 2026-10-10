using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace KospetMapMaker.Core;

/// <summary>Geometry record: shapefile-like polyline/polygon in degrees.</summary>
public sealed class Shape
{
    public int[] Parts = [];
    /// <summary>Interleaved lon,lat in degrees.</summary>
    public double[] Xy = [];
    public double MinX, MinY, MaxX, MaxY;

    public int PointCount => Xy.Length / 2;

    public static Shape Create(int[] parts, double[] xy)
    {
        var s = new Shape { Parts = parts, Xy = xy };
        s.MinX = s.MinY = double.MaxValue; s.MaxX = s.MaxY = double.MinValue;
        for (int i = 0; i < xy.Length; i += 2)
        {
            double x = xy[i], y = xy[i + 1];
            if (x < s.MinX) s.MinX = x; if (x > s.MaxX) s.MaxX = x;
            if (y < s.MinY) s.MinY = y; if (y > s.MaxY) s.MaxY = y;
        }
        return s;
    }
}

/// <summary>One map object to be written to a layer.</summary>
public sealed class Feature
{
    public int Code;
    public string FClass = "";
    public long OsmId;
    public string Name = "";
    public Shape Shape = null!;
}

public record struct DbfField(string Name, char Type, byte Length, byte Decimals);

/// <summary>
/// Kospet watch map container ("YZL") and payload formats, reverse-engineered from stock maps.
///   file   : 'YZL\0' | u32 bodyLen | u32 2 | u32 0 | md5(body)[16] | body
///   .mlp   : records [u32 BE recno][u32 LE contentLen] i32 bbox[4] (deg*1e6) | u32 nParts | u32 nPts | i32 parts[] | i32 xy[]
///   .db    : plain dBase III
///   .idx   : 'SQT\x01' | i32 1 | i32 levels | i32 nTop | nodes (packed R-tree, fan-out 7..15)
///            node  = i32 size | f32 bbox[4] | i32 level | i32 n | children or entries
///            entry = f32 bbox[4] | i32 code | i32 offset(=record header pos in body + 8) | i32 recno
/// </summary>
public static class Yzl
{
    public static void WriteFile(string path, byte[] body)
    {
        var hdr = new byte[32];
        hdr[0] = (byte)'Y'; hdr[1] = (byte)'Z'; hdr[2] = (byte)'L'; hdr[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), 0);
        MD5.HashData(body).CopyTo(hdr, 16);
        using var fs = File.Create(path);
        fs.Write(hdr);
        fs.Write(body);
    }

    public static byte[] ReadBody(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 32 || raw[0] != 'Y' || raw[1] != 'Z' || raw[2] != 'L')
            throw new InvalidDataException("Not a YZL file: " + path);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        if (len != raw.Length - 32) throw new InvalidDataException("Bad length: " + path);
        return raw.AsSpan(32).ToArray();
    }

    // ------------------------------------------------------------------ .mlp
    public static byte[] BuildMlp(IReadOnlyList<Shape> shapes, out int[] offsets)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        offsets = new int[shapes.Count];
        var be = new byte[4];
        for (int k = 0; k < shapes.Count; k++)
        {
            var s = shapes[k];
            int n = s.PointCount;
            var ip = new int[n * 2];
            int minx = int.MaxValue, miny = int.MaxValue, maxx = int.MinValue, maxy = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                int x = (int)Math.Round(s.Xy[2 * i] * 1e6);
                int y = (int)Math.Round(s.Xy[2 * i + 1] * 1e6);
                ip[2 * i] = x; ip[2 * i + 1] = y;
                if (x < minx) minx = x; if (x > maxx) maxx = x;
                if (y < miny) miny = y; if (y > maxy) maxy = y;
            }
            int contentLen = 16 + 8 + 4 * s.Parts.Length + 8 * n;
            offsets[k] = (int)ms.Position + 8;
            BinaryPrimitives.WriteUInt32BigEndian(be, (uint)(k + 1));
            bw.Write(be);
            bw.Write(contentLen);
            bw.Write(minx); bw.Write(miny); bw.Write(maxx); bw.Write(maxy);
            bw.Write(s.Parts.Length); bw.Write(n);
            foreach (var p in s.Parts) bw.Write(p);
            foreach (var v in ip) bw.Write(v);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Read back records of a .mlp file (used for preview).</summary>
    public static List<(int[] bbox, int[] parts, int[] pts)> ReadMlp(string path)
    {
        var b = ReadBody(path);
        var res = new List<(int[], int[], int[])>();
        int i = 0;
        while (i + 8 <= b.Length)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(i + 4));
            int c = i + 8;
            var bbox = new int[4];
            for (int j = 0; j < 4; j++) bbox[j] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c + 4 * j));
            int np = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c + 16));
            int npt = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c + 20));
            var parts = new int[np];
            for (int j = 0; j < np; j++) parts[j] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(c + 24 + 4 * j));
            var pts = new int[npt * 2];
            int o = c + 24 + 4 * np;
            for (int j = 0; j < npt * 2; j++) pts[j] = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o + 4 * j));
            res.Add((bbox, parts, pts));
            i += 8 + len;
        }
        return res;
    }

    // ------------------------------------------------------------------ .idx
    public readonly record struct IdxEntry(float MinX, float MinY, float MaxX, float MaxY, int Code, int Offset, int RecNo);

    private sealed class Node
    {
        public float MinX, MinY, MaxX, MaxY;
        public int Level, N;
        public byte[] Payload = [];
        public double Cx => (MinX + MaxX) * 0.5;
        public double Cy => (MinY + MaxY) * 0.5;
    }

    public static byte[] BuildIdx(IReadOnlyList<IdxEntry> entries, int cap = 10, int minFill = 7, int maxFill = 15)
    {
        // level 0
        var nodes = new List<Node>();
        foreach (var g in Groups(entries.ToList(), e => ((e.MinX + e.MaxX) * 0.5, (e.MinY + e.MaxY) * 0.5), cap, minFill, maxFill))
        {
            var ms = new MemoryStream(); var bw = new BinaryWriter(ms);
            var nd = new Node { Level = 0, N = g.Count, MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
            foreach (var e in g)
            {
                bw.Write(e.MinX); bw.Write(e.MinY); bw.Write(e.MaxX); bw.Write(e.MaxY);
                bw.Write(e.Code); bw.Write(e.Offset); bw.Write(e.RecNo);
                nd.MinX = Math.Min(nd.MinX, e.MinX); nd.MinY = Math.Min(nd.MinY, e.MinY);
                nd.MaxX = Math.Max(nd.MaxX, e.MaxX); nd.MaxY = Math.Max(nd.MaxY, e.MaxY);
            }
            bw.Flush(); nd.Payload = ms.ToArray();
            nodes.Add(nd);
        }
        int level = 0;
        while (nodes.Count > maxFill)
        {
            level++;
            var next = new List<Node>();
            foreach (var g in Groups(nodes, n => (n.Cx, n.Cy), cap, minFill, maxFill))
            {
                var ms = new MemoryStream();
                var nd = new Node { Level = level, N = g.Count, MinX = float.MaxValue, MinY = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue };
                foreach (var c in g)
                {
                    WriteNode(ms, c);
                    nd.MinX = Math.Min(nd.MinX, c.MinX); nd.MinY = Math.Min(nd.MinY, c.MinY);
                    nd.MaxX = Math.Max(nd.MaxX, c.MaxX); nd.MaxY = Math.Max(nd.MaxY, c.MaxY);
                }
                nd.Payload = ms.ToArray();
                next.Add(nd);
            }
            nodes = next;
        }
        var outMs = new MemoryStream();
        var ow = new BinaryWriter(outMs);
        ow.Write((byte)'S'); ow.Write((byte)'Q'); ow.Write((byte)'T'); ow.Write((byte)1);
        ow.Write(1); ow.Write(level + 1); ow.Write(nodes.Count);
        ow.Flush();
        foreach (var n in nodes) WriteNode(outMs, n);
        return outMs.ToArray();
    }

    private static void WriteNode(Stream s, Node n)
    {
        var bw = new BinaryWriter(s, Encoding.UTF8, leaveOpen: true);
        bw.Write(8 + n.Payload.Length);
        bw.Write(n.MinX); bw.Write(n.MinY); bw.Write(n.MaxX); bw.Write(n.MaxY);
        bw.Write(n.Level); bw.Write(n.N);
        bw.Write(n.Payload);
        bw.Flush();
    }

    /// <summary>Sort-Tile-Recursive grouping into balanced groups of ~cap items.</summary>
    private static List<List<T>> Groups<T>(List<T> items, Func<T, (double x, double y)> ctr, int cap, int minFill, int maxFill)
    {
        int n = items.Count;
        if (n <= maxFill) return [items];
        int g = (int)Math.Ceiling(n / (double)cap);
        int s = (int)Math.Ceiling(Math.Sqrt(g));
        var sorted = items.OrderBy(i => ctr(i).x).ToList();
        int perSlice = (int)Math.Ceiling(n / (double)s);
        int ns = (int)Math.Ceiling(n / (double)perSlice);
        int bas = n / ns, extra = n % ns, p = 0;
        var res = new List<List<T>>();
        for (int i = 0; i < ns; i++)
        {
            int sz = bas + (i < extra ? 1 : 0);
            var sl = sorted.GetRange(p, sz).OrderBy(it => ctr(it).y).ToList();
            p += sz;
            int k = Math.Max(1, (int)Math.Ceiling(sl.Count / (double)cap));
            while (k > 1 && sl.Count / (double)k < minFill) k--;
            int b2 = sl.Count / k, e2 = sl.Count % k, q = 0;
            for (int j = 0; j < k; j++)
            {
                int sz2 = b2 + (j < e2 ? 1 : 0);
                res.Add(sl.GetRange(q, sz2));
                q += sz2;
            }
        }
        return res;
    }

    // ------------------------------------------------------------------ .dbf
    public static byte[] BuildDbf(IReadOnlyList<DbfField> fields, IEnumerable<string[]> rows)
    {
        var rowList = rows as IList<string[]> ?? rows.ToList();
        int recLen = 1 + fields.Sum(f => f.Length);
        int hdrLen = 32 + 32 * fields.Count + 1;
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((byte)3); bw.Write((byte)95); bw.Write((byte)7); bw.Write((byte)26);
        bw.Write(rowList.Count); bw.Write((short)hdrLen); bw.Write((short)recLen);
        bw.Write(new byte[20]);
        foreach (var f in fields)
        {
            var nm = new byte[11];
            var nb = Encoding.ASCII.GetBytes(f.Name);
            Array.Copy(nb, nm, Math.Min(10, nb.Length));
            bw.Write(nm); bw.Write((byte)f.Type); bw.Write(new byte[4]);
            bw.Write(f.Length); bw.Write(f.Decimals); bw.Write(new byte[14]);
        }
        bw.Write((byte)0x0D);
        foreach (var r in rowList)
        {
            bw.Write((byte)' ');
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                var v = Utf8Truncate(r[i] ?? "", f.Length);
                var cell = new byte[f.Length];
                Array.Fill(cell, (byte)' ');
                if (f.Type == 'N' || f.Type == 'F') Array.Copy(v, 0, cell, f.Length - v.Length, v.Length);
                else Array.Copy(v, cell, v.Length);
                bw.Write(cell);
            }
        }
        bw.Write((byte)0x1A);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] Utf8Truncate(string s, int max)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length <= max) return b;
        int n = max;
        while (n > 0 && (b[n] & 0xC0) == 0x80) n--;   // don't cut a multi-byte char
        return b.AsSpan(0, n).ToArray();
    }

    // ------------------------------------------------------------------ layer
    public static readonly DbfField[] LanduseFields =
    [
        new("osm_id", 'C', 12, 0), new("code", 'N', 4, 0), new("fclass", 'C', 28, 0), new("name", 'C', 100, 0)
    ];

    public static void WriteLayer(string dir, string name, List<Feature> feats, bool withDb)
    {
        Directory.CreateDirectory(dir);
        var body = BuildMlp(feats.Select(f => f.Shape).ToList(), out var offs);
        var entries = new List<IdxEntry>(feats.Count);
        for (int k = 0; k < feats.Count; k++)
        {
            var s = feats[k].Shape;
            entries.Add(new IdxEntry((float)s.MinX, (float)s.MinY, (float)s.MaxX, (float)s.MaxY, feats[k].Code, offs[k], k + 1));
        }
        Yzl.WriteFile(Path.Combine(dir, name + ".mlp"), body);
        Yzl.WriteFile(Path.Combine(dir, name + ".idx"), BuildIdx(entries));
        if (withDb)
        {
            var rows = feats.Select(f => new[] { f.OsmId.ToString(), f.Code.ToString(), f.FClass, f.Name });
            Yzl.WriteFile(Path.Combine(dir, name + ".db"), BuildDbf(LanduseFields, rows));
        }
    }
}
