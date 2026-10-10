using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using KospetMapMaker.Core;
using Xunit;
using static KospetMapMaker.Tests.TestUtil;

namespace KospetMapMaker.Tests;

/// <summary>YZL container.</summary>
public class YzlContainerTests
{
    [Fact]
    public void Header_has_signature_length_version_and_md5()
    {
        var body = Encoding.UTF8.GetBytes("Калининград — тестовое тело файла");
        var path = Path.Combine(TempDir(), "x.mlp");
        Yzl.WriteFile(path, body);
        var raw = File.ReadAllBytes(path);

        Assert.Equal(32 + body.Length, raw.Length);
        Assert.Equal("YZL\0"u8.ToArray(), raw[..4]);
        Assert.Equal((uint)body.Length, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12)));
        Assert.Equal(MD5.HashData(body), raw[16..32]);
        Assert.Equal(body, Yzl.ReadBody(path));
    }

    [Fact]
    public void ReadBody_rejects_wrong_length()
    {
        var path = Path.Combine(TempDir(), "bad.idx");
        Yzl.WriteFile(path, new byte[100]);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..^1]);   // truncated
        Assert.Throws<InvalidDataException>(() => Yzl.ReadBody(path));
    }
}

/// <summary>.mlp records.</summary>
public class MlpTests
{
    [Fact]
    public void Record_layout_and_offsets()
    {
        var s1 = Shape.Create([0], [20.5, 54.7, 20.5012347, 54.7065432]);
        var s2 = Shape.Create([0, 4], [0, 0, 1, 0, 1, 1, 0, 0, 0.2, 0.2, 0.4, 0.2, 0.3, 0.4, 0.2, 0.2]);
        var body = Yzl.BuildMlp([s1, s2], out var offs);

        // record 1
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(0)));        // recno is big-endian
        Assert.Equal(24 + 4 + 2 * 8, I(body, 4));                                         // content length
        Assert.Equal(8, offs[0]);
        Assert.Equal(20_500_000, I(body, 8));                                             // xmin
        Assert.Equal(54_706_543, I(body, 20));                                            // ymax, rounded
        Assert.Equal(1, I(body, 24)); Assert.Equal(2, I(body, 28));                       // parts, points
        Assert.Equal(20_501_235, I(body, 8 + 24 + 4 + 8));                                // 2nd point x
        // record 2
        Assert.Equal(offs[0] + 44 + 8, offs[1]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offs[1] - 8)));
        Assert.Equal(2, I(body, offs[1] + 16)); Assert.Equal(8, I(body, offs[1] + 20));
        Assert.Equal(4, I(body, offs[1] + 28));                                           // start of 2nd part
        Assert.Equal(body.Length, offs[1] + 24 + 2 * 4 + 8 * 8);
    }

    [Fact]
    public void ReadMlp_roundtrip()
    {
        var shapes = new List<Shape> { Shape.Create([0], [37.6, 55.75, 37.62, 55.76, 37.65, 55.74]) };
        var path = Path.Combine(TempDir(), "r.mlp");
        Yzl.WriteFile(path, Yzl.BuildMlp(shapes, out _));
        var (bbox, parts, pts) = Assert.Single(Yzl.ReadMlp(path));
        Assert.Equal(new[] { 37_600_000, 55_740_000, 37_650_000, 55_760_000 }, bbox);
        Assert.Equal(new[] { 0 }, parts);
        Assert.Equal(new[] { 37_600_000, 55_750_000, 37_620_000, 55_760_000, 37_650_000, 55_740_000 }, pts);
    }
}

/// <summary>R-tree .idx.</summary>
public class IdxTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(5000)]
    [InlineData(60000)]
    public void Tree_is_consistent(int count)
    {
        var rnd = new Random(count);
        var entries = new List<Yzl.IdxEntry>();
        for (int i = 0; i < count; i++)
        {
            float x = 19.6f + (float)rnd.NextDouble() * 2, y = 54.3f + (float)rnd.NextDouble() * 1;
            float w = (float)rnd.NextDouble() * 0.01f, h = (float)rnd.NextDouble() * 0.01f;
            entries.Add(new Yzl.IdxEntry(x, y, x + w, y + h, 5100 + i % 50, 8 + 100 * i, i + 1));
        }
        var info = ParseIdx(Yzl.BuildIdx(entries));

        Assert.Equal(count, info.Entries.Count);
        Assert.Equal(entries.OrderBy(e => e.RecNo).Select(e => (e.Code, e.Offset)),
                     info.Entries.OrderBy(e => e.RecNo).Select(e => (e.Code, e.Offset)));
        Assert.InRange(info.TopCount, 1, 15);
        Assert.All(info.Nodes, n => Assert.InRange(n.n, 1, 15));
        if (count >= 16)
            Assert.All(info.Nodes.Where(n => !n.top), n => Assert.InRange(n.n, 7, 15));
    }
}

/// <summary>Whole OSM pipeline on the synthetic extract vs. the golden files.</summary>
public class OsmGoldenTests
{
    [Theory]
    [InlineData("roads", false)]
    [InlineData("water", false)]
    [InlineData("landuse", true)]
    public void Layers_match_golden_files(string layer, bool withDb)
    {
        var res = BuildTestPbf();
        var feats = layer switch { "roads" => res.Roads, "water" => res.Water, _ => res.Landuse };
        var dir = TempDir();
        Yzl.WriteLayer(dir, layer, feats, withDb);

        // geometry: byte-for-byte identical
        Assert.Equal(Yzl.ReadBody(Path.Combine(ExpectedDir, layer + ".mlp")), Yzl.ReadBody(Path.Combine(dir, layer + ".mlp")));
        // index: same entries (tree layout may differ), structurally valid
        var exp = ParseIdx(Yzl.ReadBody(Path.Combine(ExpectedDir, layer + ".idx")));
        var act = ParseIdx(Yzl.ReadBody(Path.Combine(dir, layer + ".idx")));
        Assert.Equal(exp.Entries.OrderBy(e => e.RecNo), act.Entries.OrderBy(e => e.RecNo));
        AssertIdxMatchesMlp(act, Yzl.ReadBody(Path.Combine(dir, layer + ".mlp")), feats.Count);
        if (withDb)
            Assert.Equal(Yzl.ReadBody(Path.Combine(ExpectedDir, layer + ".db")), Yzl.ReadBody(Path.Combine(dir, layer + ".db")));
    }

    [Fact]
    public void Feature_classes()
    {
        var res = BuildTestPbf();
        Assert.Equal(new[] { 5113, 5122, 5122, 5122, 5122, 5145, 5142, 5153, 5141, 5131, 5199 }, res.Roads.Select(f => f.Code));
        Assert.Equal(new[] { 8200, 8201, 8221, 8221, 8200 }, res.Water.Select(f => f.Code));
        Assert.Equal(new[] { 7201, 7202, 7229, 7201 }, res.Landuse.Select(f => f.Code));
        Assert.Contains(res.Landuse, f => f.Name == "Парк Победы" && f.FClass == "park");
        Assert.Contains(res.Roads, f => f.Name == "Ленинский проспект");
    }

    [Fact]
    public void Multipolygon_with_hole()
    {
        var forest = BuildTestPbf().Landuse.Single(f => f.OsmId == 9001);
        var s = forest.Shape;
        Assert.Equal(2, s.Parts.Length);
        var outer = s.Xy[..(2 * s.Parts[1])];
        var inner = s.Xy[(2 * s.Parts[1])..];
        Assert.True(Geometry.Area2(outer) < 0, "outer ring must be clockwise");
        Assert.True(Geometry.Area2(inner) > 0, "hole must be counter-clockwise");
        Assert.Equal(outer[..2], outer[^2..]);   // closed
        Assert.Equal(inner[..2], inner[^2..]);
    }

    [Fact]
    public void Rivers_option_adds_thin_polygon()
    {
        var off = BuildTestPbf();
        var on = BuildTestPbf(o => o.Rivers = true);
        Assert.Equal(off.Water.Count + 1, on.Water.Count);
        var river = on.Water.Single(f => f.Name == "Преголя");
        Assert.Equal(8202, river.Code);
        var xy = river.Shape.Xy;
        Assert.Equal(2 * (2 * 4 + 1), xy.Length);                // 4 line points -> 2*4+1 ring points
        Assert.Equal(xy[..2], xy[^2..]);
        // the line spans lat 54.700..54.712, lon 20.490..20.493 and runs roughly north-south:
        // the 25 m strip widens it by ~12 m east and west and a few metres north and south
        var s = river.Shape;
        double kx = 111320 * Math.Cos(54.706 * Math.PI / 180), ky = 110540;
        Assert.InRange((20.490 - s.MinX) * kx, 5, 30);
        Assert.InRange((s.MaxX - 20.493) * kx, 5, 30);
        Assert.InRange((54.700 - s.MinY) * ky, 0, 30);
        Assert.InRange((s.MaxY - 54.712) * ky, 0, 30);
    }

    [Fact]
    public void Extra_classes_option()
    {
        Assert.DoesNotContain(BuildTestPbf().Landuse, f => f.Code == 7218);
        var on = BuildTestPbf(o => o.ExtraNatural = true);
        Assert.Contains(on.Landuse, f => f.Code == 7218 && f.FClass == "grass");
    }

    [Fact]
    public void Road_filters()
    {
        var res = BuildTestPbf(o => { o.Paths = false; o.Service = false; o.Tracks = false; });
        Assert.DoesNotContain(res.Roads, f => f.Code is >= 5141 and <= 5155);
        Assert.Equal(7, res.Roads.Count);
    }

    [Fact]
    public void Tiles_touched_by_features()
    {
        var res = BuildTestPbf();
        Assert.Equal(new[] { (54, 20) }, res.Tiles.ToArray());
    }
}

/// <summary>Tag → class mapping (Geofabrik scheme).</summary>
public class ClassTests
{
    private static readonly BuildOptions All = new();

    [Theory]
    [InlineData("highway=motorway", 5111)]
    [InlineData("highway=primary_link", 5133)]
    [InlineData("highway=residential", 5122)]
    [InlineData("highway=track", 5142)]
    [InlineData("highway=track;tracktype=grade1", 5143)]
    [InlineData("highway=track;tracktype=grade5", 5147)]
    [InlineData("highway=track;tracktype=bad", 5142)]
    [InlineData("highway=steps", 5155)]
    [InlineData("highway=road", 5199)]
    [InlineData("highway=proposed", 0)]
    [InlineData("building=yes", 0)]
    public void Roads(string tags, int code) =>
        Assert.Equal(code, Classes.RoadClass(Tags(tags), All)?.Code ?? 0);

    [Theory]
    [InlineData("natural=water", 8200)]
    [InlineData("natural=water;water=reservoir", 8201)]
    [InlineData("landuse=reservoir", 8201)]
    [InlineData("natural=water;water=river", 8202)]
    [InlineData("waterway=riverbank", 8202)]
    [InlineData("waterway=dock", 8203)]
    [InlineData("natural=glacier", 8211)]
    [InlineData("natural=wetland", 8221)]
    [InlineData("natural=wood", 0)]
    public void Water(string tags, int code) =>
        Assert.Equal(code, Classes.WaterClass(Tags(tags), All)?.Code ?? 0);

    [Theory]
    [InlineData("landuse=forest", 7201, true)]
    [InlineData("natural=wood", 7201, true)]
    [InlineData("leisure=park", 7202, true)]
    [InlineData("landuse=farmland", 7229, true)]
    [InlineData("natural=scrub", 7217, true)]
    [InlineData("natural=grassland", 7218, true)]
    [InlineData("natural=grassland", 0, false)]
    [InlineData("natural=bare_rock", 7214, true)]
    [InlineData("landuse=plantation", 7215, true)]
    [InlineData("landuse=plantation", 0, false)]
    [InlineData("amenity=school", 0, true)]
    public void Landuse(string tags, int code, bool extra) =>
        Assert.Equal(code, Classes.LanduseClass(Tags(tags), new BuildOptions { ExtraNatural = extra })?.Code ?? 0);

    [Theory]
    [InlineData("waterway=river", 25.0)]
    [InlineData("waterway=canal", 15.0)]
    [InlineData("waterway=stream", 10.0)]
    [InlineData("waterway=stream;tunnel=culvert", 0.0)]
    [InlineData("waterway=ditch", 0.0)]
    public void River_width(string tags, double w) =>
        Assert.Equal(w, Classes.RiverWidth(Tags(tags), All), 6);

    [Fact]
    public void Path_filter() =>
        Assert.Null(Classes.RoadClass(Tags("highway=footway"), new BuildOptions { Paths = false }));
}

/// <summary>Geometry helpers.</summary>
public class GeometryTests
{
    [Fact]
    public void JoinRings_joins_reversed_pieces()
    {
        var rings = Geometry.JoinRings([[1, 2, 3], [5, 4, 3], [5, 6, 1]]);
        var r = Assert.Single(rings);
        Assert.Equal(7, r.Length);
        Assert.Equal(r[0], r[^1]);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }.Order(), r[..^1].Order());
    }

    [Fact]
    public void JoinRings_drops_unclosable()
    {
        Assert.Empty(Geometry.JoinRings([[1, 2, 3], [3, 4]]));
        Assert.Single(Geometry.JoinRings([[1, 2, 3, 1], [7, 8]]));
    }

    [Fact]
    public void Polygon_orients_rings_and_assigns_holes()
    {
        double[] outerCcw = [0, 0, 10, 0, 10, 10, 0, 10, 0, 0];
        double[] holeCw = [2, 2, 2, 4, 4, 4, 4, 2, 2, 2];
        double[] other = [20, 0, 25, 0, 25, 5, 20, 0];
        var s = Geometry.Polygon([outerCcw, other], [holeCw])!;
        Assert.Equal(new[] { 0, 5, 10 }, s.Parts);                 // hole right after its outer ring
        Assert.True(Geometry.Area2(s.Xy[..10]) < 0);
        Assert.True(Geometry.Area2(s.Xy[10..20]) > 0);
        Assert.Equal(25, s.MaxX);
    }

    [Fact]
    public void BufferLine_width()
    {
        var ring = Geometry.BufferLine([0, 0, 0.001, 0, 0.002, 0], 20)!;
        Assert.Equal(2 * 7, ring.Length);
        double maxY = Enumerable.Range(0, 7).Max(i => ring[2 * i + 1]);
        double minY = Enumerable.Range(0, 7).Min(i => ring[2 * i + 1]);
        Assert.Equal(10.0, maxY * 110540, 3);
        Assert.Equal(-10.0, minY * 110540, 3);
        Assert.Null(Geometry.BufferLine([1, 1, 1, 1], 20));        // degenerate
    }

    [Fact]
    public void PointInRing()
    {
        double[] sq = [0, 0, 4, 0, 4, 4, 0, 4, 0, 0];
        Assert.True(Geometry.PointInRing(1, 1, sq));
        Assert.False(Geometry.PointInRing(5, 1, sq));
    }
}

/// <summary>Contour lines.</summary>
public class ContourTests
{
    private static float[] Cone(int n, float peak, float slopePerCell)
    {
        var g = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                double d = Math.Sqrt((i - n / 2) * (i - n / 2) + (j - n / 2) * (j - n / 2));
                g[j * n + i] = (float)Math.Max(0, peak - slopePerCell * d);
            }
        return g;
    }

    [Fact]
    public void Cone_gives_one_closed_line_per_level()
    {
        const int n = 101;
        var lines = Contours.Trace(Cone(n, 450, 10), n, 55, 37, 100);
        Assert.Equal(new[] { 100, 200, 300, 400 }, lines.Select(l => l.elev));
        foreach (var (elev, xy) in lines)
        {
            Assert.Equal(xy[..2], xy[^2..]);                       // closed
            // radius in cells = (450 - elev) / 10, cell = 1/(n-1) degree
            double cx = 37 + 0.5, cy = 55 + 0.5, r = (450 - elev) / 10.0 / (n - 1);
            for (int k = 0; k < xy.Length; k += 2)
                Assert.InRange(Math.Sqrt(Math.Pow(xy[k] - cx, 2) + Math.Pow(xy[k + 1] - cy, 2)), r * 0.9, r * 1.1);
        }
    }

    [Fact]
    public void Lines_cut_by_tile_edge_are_open()
    {
        const int n = 51;
        var g = new float[n * n];
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) g[j * n + i] = i * 10;   // slope west -> east
        var lines = Contours.Trace(g, n, 55, 37, 100);
        Assert.Equal(5, lines.Count);                                                  // 100..500
        Assert.All(lines, l => Assert.NotEqual(l.xy[1], l.xy[^1]));                   // runs north-south, open
    }

    [Fact]
    public void Voids_and_lowlands_are_skipped()
    {
        const int n = 21;
        var g = Enumerable.Repeat(float.NaN, n * n).ToArray();
        Assert.Empty(Contours.Trace(g, n, 0, 0, 100));
        var flat = Enumerable.Repeat(50f, n * n).ToArray();
        Assert.Empty(Contours.Trace(flat, n, 0, 0, 100));
    }

    [Theory]
    [InlineData(55, 37, "N55E037")]
    [InlineData(0, 0, "N00E000")]
    [InlineData(-1, -1, "S01W001")]
    [InlineData(64, 177, "N64E177")]
    public void Tile_names(int lat, int lon, string name) => Assert.Equal(name, Contours.TileName(lat, lon));

    [Fact]
    public void MakeTile_writes_valid_files()
    {
        const int n = 1201;
        var g = Cone(n, 650, 2);
        var raw = new byte[n * n * 2];
        for (int k = 0; k < g.Length; k++) BinaryPrimitives.WriteInt16BigEndian(raw.AsSpan(2 * k), (short)g[k]);
        var dir = TempDir();
        var hgt = Path.Combine(dir, "N55E037.hgt");
        File.WriteAllBytes(hgt, raw);

        int count = Contours.MakeTile(hgt, 55, 37, dir, 100);
        Assert.Equal(6, count);                                      // 100..600
        var tile = Path.Combine(dir, "N55E037");
        var mlp = Yzl.ReadBody(Path.Combine(tile, "N55E037.mlp"));
        var idx = ParseIdx(Yzl.ReadBody(Path.Combine(tile, "N55E037.idx")));
        AssertIdxMatchesMlp(idx, mlp, count);
        Assert.Equal(new[] { 100, 200, 300, 400, 500, 600 }, idx.Entries.OrderBy(e => e.RecNo).Select(e => e.Code));
        Assert.True(File.Exists(Path.Combine(tile, "N55E037.db")));
    }
}

/// <summary>dBase writer.</summary>
public class DbfTests
{
    [Fact]
    public void Layout_and_utf8_truncation()
    {
        DbfField[] fields = [new("name", 'C', 5, 0), new("code", 'N', 4, 0)];
        var b = Yzl.BuildDbf(fields, [["Привет", "7201"], ["ab", "5"]]);
        int hdr = 32 + 32 * 2 + 1, rec = 1 + 5 + 4;
        Assert.Equal(3, b[0]);
        Assert.Equal(2, I(b, 4));
        Assert.Equal(hdr, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8)));
        Assert.Equal(rec, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(10)));
        Assert.Equal(0x0D, b[hdr - 1]);
        Assert.Equal(hdr + 2 * rec + 1, b.Length);
        Assert.Equal(0x1A, b[^1]);
        var row1 = b.AsSpan(hdr, rec).ToArray();
        Assert.Equal("Пр ", Encoding.UTF8.GetString(row1, 1, 5));     // 5 bytes: "Пр" (4) + pad, no broken char
        Assert.Equal("7201", Encoding.ASCII.GetString(row1, 6, 4));
        var row2 = b.AsSpan(hdr + rec, rec).ToArray();
        Assert.Equal("ab   ", Encoding.ASCII.GetString(row2, 1, 5));   // C: left-aligned
        Assert.Equal("   5", Encoding.ASCII.GetString(row2, 6, 4));    // N: right-aligned
    }
}

/// <summary>Naming helpers.</summary>
public class PipelineTests
{
    [Theory]
    [InlineData("Kaliningrad", "kaliningrad")]
    [InlineData("  Москва и область ", "москва-и-область")]
    [InlineData("central/fed.district", "central-fed-district")]
    [InlineData("...", "map")]
    public void Sanitize(string input, string expected) => Assert.Equal(expected, Pipeline.Sanitize(input));

    [Fact]
    public void DefaultName_from_pbf_file()
    {
        Assert.Equal("kaliningrad", Pipeline.DefaultName(new JobSpec { LocalPbf = "/tmp/kaliningrad-latest.osm.pbf" }));
        Assert.Equal("moscow", Pipeline.DefaultName(new JobSpec { LocalPbf = "x.pbf", MapName = "Moscow" }));
    }
}
