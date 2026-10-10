using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace KospetMapMaker.Core;

/// <summary>Minimal protobuf field reader over a byte array slice.</summary>
public struct Pr
{
    private readonly byte[] _b;
    private int _p;
    private readonly int _end;
    public int Field, Wire, Start, Len;
    public ulong V;

    public Pr(byte[] b, int start, int len)
    {
        _b = b; _p = start; _end = start + len;
        Field = Wire = Start = Len = 0; V = 0;
    }

    public bool Next()
    {
        if (_p >= _end) return false;
        ulong k = Pbf.Varint(_b, ref _p);
        Field = (int)(k >> 3); Wire = (int)(k & 7);
        switch (Wire)
        {
            case 0: V = Pbf.Varint(_b, ref _p); break;
            case 2: Len = (int)Pbf.Varint(_b, ref _p); Start = _p; _p += Len; break;
            case 1: Start = _p; Len = 8; _p += 8; break;
            case 5: Start = _p; Len = 4; _p += 4; break;
            default: throw new InvalidDataException("protobuf wire type " + Wire);
        }
        return true;
    }

    public readonly Pr Sub() => new(_b, Start, Len);
    public readonly string Str() => Encoding.UTF8.GetString(_b, Start, Len);
}

public sealed class PrimitiveBlock
{
    public byte[] Data = [];
    public string[] Strings = [];
    public long Granularity = 100, LatOffset, LonOffset;
    /// <summary>(kind, start, len): kind 1 node, 2 dense, 3 way, 4 relation</summary>
    public List<(int kind, int start, int len)> Items = new();
}

public static class Pbf
{
    public static ulong Varint(byte[] b, ref int p)
    {
        ulong r = 0; int s = 0;
        while (true)
        {
            byte c = b[p++];
            r |= (ulong)(c & 0x7F) << s;
            if (c < 0x80) return r;
            s += 7;
        }
    }

    public static long ZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    public static uint[] PackedUInt(byte[] b, int start, int len)
    {
        var res = new List<uint>();
        int p = start, end = start + len;
        while (p < end) res.Add((uint)Varint(b, ref p));
        return res.ToArray();
    }

    /// <summary>Packed sint64, delta-coded.</summary>
    public static long[] PackedDelta(byte[] b, int start, int len)
    {
        var res = new List<long>(len / 2 + 1);
        int p = start, end = start + len;
        long acc = 0;
        while (p < end) { acc += ZigZag(Varint(b, ref p)); res.Add(acc); }
        return res.ToArray();
    }

    /// <summary>Iterate OSMData blocks of a .osm.pbf file. progress gets fraction of file read.</summary>
    public static IEnumerable<PrimitiveBlock> Blocks(string path, Action<double>? progress = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long total = fs.Length;
        var lenBuf = new byte[4];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (fs.ReadAtLeast(lenBuf, 4, throwOnEndOfStream: false) < 4) yield break;
            int hl = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            var hdr = new byte[hl];
            fs.ReadExactly(hdr);
            string? type = null; int size = 0;
            var r = new Pr(hdr, 0, hl);
            while (r.Next())
            {
                if (r.Field == 1) type = r.Str();
                else if (r.Field == 3) size = (int)r.V;
            }
            var blob = new byte[size];
            fs.ReadExactly(blob);
            progress?.Invoke(fs.Position / (double)total);
            if (type != "OSMData") continue;
            yield return ParseBlock(DecodeBlob(blob));
        }
    }

    private static byte[] DecodeBlob(byte[] blob)
    {
        var r = new Pr(blob, 0, blob.Length);
        int rawSize = 0; int zs = -1, zl = 0; int rs = -1, rl = 0;
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: rs = r.Start; rl = r.Len; break;
                case 2: rawSize = (int)r.V; break;
                case 3: zs = r.Start; zl = r.Len; break;
            }
        }
        if (rs >= 0) return blob.AsSpan(rs, rl).ToArray();
        if (zs < 0) throw new InvalidDataException("Unsupported PBF blob compression");
        var outBuf = new byte[rawSize];
        using var z = new ZLibStream(new MemoryStream(blob, zs, zl), CompressionMode.Decompress);
        z.ReadExactly(outBuf);
        return outBuf;
    }

    private static PrimitiveBlock ParseBlock(byte[] data)
    {
        var pb = new PrimitiveBlock { Data = data };
        var groups = new List<(int, int)>();
        var r = new Pr(data, 0, data.Length);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1:
                    {
                        var st = new List<string>();
                        var s = r.Sub();
                        while (s.Next()) if (s.Field == 1) st.Add(s.Str());
                        pb.Strings = st.ToArray();
                        break;
                    }
                case 2: groups.Add((r.Start, r.Len)); break;
                case 17: pb.Granularity = (long)r.V; break;
                case 19: pb.LatOffset = (long)r.V; break;
                case 20: pb.LonOffset = (long)r.V; break;
            }
        }
        foreach (var (gs, gl) in groups)
        {
            var g = new Pr(data, gs, gl);
            while (g.Next())
                if (g.Field >= 1 && g.Field <= 4) pb.Items.Add((g.Field, g.Start, g.Len));
        }
        return pb;
    }
}
