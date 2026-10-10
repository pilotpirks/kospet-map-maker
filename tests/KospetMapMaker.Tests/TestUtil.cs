using System.Buffers.Binary;
using KospetMapMaker.Core;
using Xunit;

namespace KospetMapMaker.Tests;

public readonly record struct IdxRecord(float MinX, float MinY, float MaxX, float MaxY, int Code, int Offset, int RecNo);

public sealed class IdxInfo
{
    public int Levels, TopCount;
    public List<IdxRecord> Entries = new();
    /// <summary>(level, child count, isTop) of every node.</summary>
    public List<(int level, int n, bool top)> Nodes = new();
}

public static class TestUtil
{
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    public static string TestPbf => Path.Combine(DataDir, "test.osm.pbf");
    public static string ExpectedDir => Path.Combine(DataDir, "expected");

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "kmm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static OsmResult BuildTestPbf(Action<BuildOptions>? configure = null)
    {
        var opt = new BuildOptions { Rivers = false, ExtraNatural = false, Contours = false };
        configure?.Invoke(opt);
        return new OsmBuilder(opt, _ => { }, _ => { }).Build(TestPbf, CancellationToken.None);
    }

    /// <summary>Parses an .idx body and asserts its structural invariants (sizes, bboxes, fan-out).</summary>
    public static IdxInfo ParseIdx(byte[] body)
    {
        Assert.Equal((byte)'S', body[0]); Assert.Equal((byte)'Q', body[1]); Assert.Equal((byte)'T', body[2]); Assert.Equal(1, body[3]);
        Assert.Equal(1, I(body, 4));
        var info = new IdxInfo { Levels = I(body, 8), TopCount = I(body, 12) };
        int p = 16;
        for (int t = 0; t < info.TopCount; t++)
        {
            var (lvl, _) = Node(body, ref p, info, top: true);
            Assert.Equal(info.Levels - 1, lvl);
        }
        Assert.Equal(body.Length, p);                       // nothing left over
        return info;
    }

    private static (int level, float[] bbox) Node(byte[] b, ref int p, IdxInfo info, bool top)
    {
        int start = p;
        int size = I(b, p);
        var bb = new[] { F(b, p + 4), F(b, p + 8), F(b, p + 12), F(b, p + 16) };
        int level = I(b, p + 20), n = I(b, p + 24);
        int end = start + 20 + size;
        info.Nodes.Add((level, n, top));
        p += 28;
        var union = new[] { float.MaxValue, float.MaxValue, float.MinValue, float.MinValue };
        void Grow(float a, float c, float d, float e)
        {
            union[0] = Math.Min(union[0], a); union[1] = Math.Min(union[1], c);
            union[2] = Math.Max(union[2], d); union[3] = Math.Max(union[3], e);
        }
        for (int k = 0; k < n; k++)
        {
            if (level > 0)
            {
                var (cl, cb) = Node(b, ref p, info, top: false);
                Assert.Equal(level - 1, cl);
                Grow(cb[0], cb[1], cb[2], cb[3]);
            }
            else
            {
                var e = new IdxRecord(F(b, p), F(b, p + 4), F(b, p + 8), F(b, p + 12), I(b, p + 16), I(b, p + 20), I(b, p + 24));
                info.Entries.Add(e);
                Grow(e.MinX, e.MinY, e.MaxX, e.MaxY);
                p += 28;
            }
        }
        Assert.Equal(end, p);                                // size field is consistent
        Assert.Equal(union, bb);                             // node bbox = union of children
        return (level, bb);
    }

    /// <summary>Checks that every idx entry points at its own record in the .mlp body.</summary>
    public static void AssertIdxMatchesMlp(IdxInfo idx, byte[] mlpBody, int recordCount)
    {
        Assert.Equal(recordCount, idx.Entries.Count);
        Assert.Equal(Enumerable.Range(1, recordCount), idx.Entries.Select(e => e.RecNo).OrderBy(x => x));
        foreach (var e in idx.Entries)
        {
            Assert.Equal((uint)e.RecNo, BinaryPrimitives.ReadUInt32BigEndian(mlpBody.AsSpan(e.Offset - 8)));
            // bbox of the record (int microdegrees) must agree with the float bbox
            int x0 = I(mlpBody, e.Offset), y0 = I(mlpBody, e.Offset + 4), x1 = I(mlpBody, e.Offset + 8), y1 = I(mlpBody, e.Offset + 12);
            Assert.InRange(Math.Abs(x0 / 1e6 - e.MinX), 0, 1e-5);
            Assert.InRange(Math.Abs(y0 / 1e6 - e.MinY), 0, 1e-5);
            Assert.InRange(Math.Abs(x1 / 1e6 - e.MaxX), 0, 1e-5);
            Assert.InRange(Math.Abs(y1 / 1e6 - e.MaxY), 0, 1e-5);
        }
    }

    public static int I(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
    public static float F(byte[] b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));

    public static Dictionary<string, string> Tags(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2)).ToDictionary(a => a[0], a => a[1]);
}
