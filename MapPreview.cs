using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KospetMapMaker.Core;

namespace KospetMapMaker;

/// <summary>Simple pan/zoom preview of the generated watch files.</summary>
public sealed class MapPreview : Control
{
    public sealed class Item
    {
        public float MinX, MinY, MaxX, MaxY;
        public int[] Parts = [];
        public float[] Pts = [];   // lon,lat
        public int Code;
    }

    private List<Item> _land = new(), _water = new(), _roads = new(), _cm = new();
    private double _cx, _cy, _scale = 100, _cos = 1;   // scale = px per degree latitude
    private Point? _drag;
    private bool _hasData;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#F7F5F0"));
    private static readonly IBrush LandBrush = new SolidColorBrush(Color.Parse("#CFE8C0"));
    private static readonly IBrush WaterBrush = new SolidColorBrush(Color.Parse("#8EC5F0"));
    private static readonly Pen MajorPen = new(new SolidColorBrush(Color.Parse("#C0502A")), 1.4);
    private static readonly Pen RoadPen = new(new SolidColorBrush(Color.Parse("#555555")), 0.6);
    private static readonly Pen MinorPen = new(new SolidColorBrush(Color.Parse("#999999")), 0.4);
    private static readonly Pen CmPen = new(new SolidColorBrush(Color.Parse("#C9A27A")), 0.5);

    public MapPreview() { ClipToBounds = true; }

    /// <summary>Load files of a built map (runs on a background thread, then call Apply on UI thread).</summary>
    public static (List<Item>[] layers, double[] bbox)? LoadFiles(string mapDir)
    {
        List<Item> Read(string path)
        {
            var list = new List<Item>();
            if (!File.Exists(path)) return list;
            foreach (var (bb, parts, pts) in Yzl.ReadMlp(path))
            {
                var f = new float[pts.Length];
                for (int i = 0; i < pts.Length; i++) f[i] = pts[i] / 1e6f;
                list.Add(new Item { MinX = bb[0] / 1e6f, MinY = bb[1] / 1e6f, MaxX = bb[2] / 1e6f, MaxY = bb[3] / 1e6f, Parts = parts, Pts = f });
            }
            return list;
        }
        var land = Read(Path.Combine(mapDir, "landuse.mlp"));
        var water = Read(Path.Combine(mapDir, "water.mlp"));
        var roads = Read(Path.Combine(mapDir, "roads.mlp"));
        // road classes come from the index entries
        var idx = Path.Combine(mapDir, "roads.idx");
        if (File.Exists(idx) && roads.Count > 0)
        {
            var codes = ReadIdxCodes(idx, roads.Count);
            for (int i = 0; i < roads.Count; i++) roads[i].Code = codes[i];
        }
        var cm = new List<Item>();   // contours are not loaded: too heavy for a quick preview
        var all = land.Concat(water).Concat(roads).ToList();
        if (all.Count == 0) return null;
        // robust extent: roads if present
        var ext = roads.Count > 0 ? roads : all;
        double[] bbox = [ext.Min(i => i.MinX), ext.Min(i => i.MinY), ext.Max(i => i.MaxX), ext.Max(i => i.MaxY)];
        return (new[] { land, water, roads, cm }, bbox);
    }

    private static int[] ReadIdxCodes(string path, int count)
    {
        var codes = new int[count];
        var b = Yzl.ReadBody(path);
        int ntop = BitConverter.ToInt32(b, 12);
        int p = 16;
        void Node()
        {
            int size = BitConverter.ToInt32(b, p);
            int level = BitConverter.ToInt32(b, p + 20), n = BitConverter.ToInt32(b, p + 24);
            int end = p + 20 + size;
            p += 28;
            if (level > 0) for (int k = 0; k < n; k++) Node();
            else
                for (int k = 0; k < n; k++)
                {
                    int code = BitConverter.ToInt32(b, p + 16), rec = BitConverter.ToInt32(b, p + 24);
                    if (rec >= 1 && rec <= count) codes[rec - 1] = code;
                    p += 28;
                }
            p = end;
        }
        for (int t = 0; t < ntop; t++) Node();
        return codes;
    }

    public void Apply((List<Item>[] layers, double[] bbox)? data)
    {
        if (data is not { } d) { _hasData = false; InvalidateVisual(); return; }
        _land = d.layers[0]; _water = d.layers[1]; _roads = d.layers[2]; _cm = d.layers[3];
        var bb = d.bbox;
        _cx = (bb[0] + bb[2]) / 2; _cy = (bb[1] + bb[3]) / 2;
        _cos = Math.Max(0.2, Math.Cos(_cy * Math.PI / 180));
        double w = Math.Max(1, Bounds.Width), h = Math.Max(1, Bounds.Height);
        _scale = Math.Min(w / Math.Max(1e-6, (bb[2] - bb[0]) * _cos), h / Math.Max(1e-6, bb[3] - bb[1])) * 0.95;
        _hasData = true;
        InvalidateVisual();
    }

    private Point ToScreen(double x, double y) =>
        new((x - _cx) * _scale * _cos + Bounds.Width / 2, (_cy - y) * _scale + Bounds.Height / 2);

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(Bg, r);
        if (!_hasData) return;
        double halfW = Bounds.Width / 2 / (_scale * _cos), halfH = Bounds.Height / 2 / _scale;
        double vx0 = _cx - halfW, vx1 = _cx + halfW, vy0 = _cy - halfH, vy1 = _cy + halfH;
        double minDeg = 1.5 / _scale;   // features smaller than ~1.5 px are skipped

        bool Visible(Item i) => !(i.MaxX < vx0 || i.MinX > vx1 || i.MaxY < vy0 || i.MinY > vy1);
        bool Big(Item i) => (i.MaxX - i.MinX) * _cos > minDeg || (i.MaxY - i.MinY) > minDeg;

        void Polys(List<Item> items, IBrush brush)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.SetFillRule(FillRule.EvenOdd);
                foreach (var it in items)
                {
                    if (!Visible(it) || !Big(it)) continue;
                    for (int p = 0; p < it.Parts.Length; p++)
                    {
                        int a = it.Parts[p], z = p + 1 < it.Parts.Length ? it.Parts[p + 1] : it.Pts.Length / 2;
                        if (z - a < 3) continue;
                        c.BeginFigure(ToScreen(it.Pts[2 * a], it.Pts[2 * a + 1]), true);
                        for (int k = a + 1; k < z; k++) c.LineTo(ToScreen(it.Pts[2 * k], it.Pts[2 * k + 1]));
                        c.EndFigure(true);
                    }
                }
            }
            ctx.DrawGeometry(brush, null, g);
        }

        void Lines(IEnumerable<Item> items, Pen pen)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                foreach (var it in items)
                {
                    if (!Visible(it) || !Big(it)) continue;
                    int n = it.Pts.Length / 2;
                    if (n < 2) continue;
                    c.BeginFigure(ToScreen(it.Pts[0], it.Pts[1]), false);
                    for (int k = 1; k < n; k++) c.LineTo(ToScreen(it.Pts[2 * k], it.Pts[2 * k + 1]));
                    c.EndFigure(false);
                }
            }
            ctx.DrawGeometry(null, pen, g);
        }

        Polys(_land, LandBrush);
        if (_scale > 400) Lines(_cm, CmPen);      // contours only when zoomed in
        Polys(_water, WaterBrush);
        bool zoomedOut = _scale < 150;
        if (!zoomedOut) Lines(_roads.Where(i => i.Code >= 5141), MinorPen);
        Lines(_roads.Where(i => i.Code >= 5121 && i.Code < 5141 && !(i.Code >= 5131 && i.Code <= 5135) && (!zoomedOut || i.Code == 5121)), RoadPen);
        Lines(_roads.Where(i => i.Code < 5121 || (i.Code >= 5131 && i.Code <= 5135)), MajorPen);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!_hasData) return;
        var p = e.GetPosition(this);
        // keep the point under the cursor fixed
        double gx = _cx + (p.X - Bounds.Width / 2) / (_scale * _cos);
        double gy = _cy - (p.Y - Bounds.Height / 2) / _scale;
        _scale *= e.Delta.Y > 0 ? 1.4 : 1 / 1.4;
        _scale = Math.Clamp(_scale, 5, 2_000_000);
        _cx = gx - (p.X - Bounds.Width / 2) / (_scale * _cos);
        _cy = gy + (p.Y - Bounds.Height / 2) / _scale;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { _drag = e.GetPosition(this); e.Handled = true; }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { _drag = null; }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } d) return;
        var p = e.GetPosition(this);
        _cx -= (p.X - d.X) / (_scale * _cos);
        _cy += (p.Y - d.Y) / _scale;
        _drag = p;
        InvalidateVisual();
    }
}
