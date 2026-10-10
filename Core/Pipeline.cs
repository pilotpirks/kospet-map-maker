namespace KospetMapMaker.Core;

public sealed class JobSpec
{
    public Region? Region;          // download from Geofabrik ...
    public string? LocalPbf;        // ... or use a local file
    public string OutDir = "";
    public string MapName = "";     // folder name on the watch, e.g. "moscow"
    public BuildOptions Options = new();
    public bool RefreshPbf;
}

public static class Pipeline
{
    public static string DefaultName(JobSpec j)
    {
        if (!string.IsNullOrWhiteSpace(j.MapName)) return Sanitize(j.MapName);
        if (j.Region != null) return Sanitize(j.Region.Id.Split('/').Last());
        if (j.LocalPbf != null)
        {
            var n = Path.GetFileName(j.LocalPbf);
            foreach (var suf in new[] { ".osm.pbf", ".pbf" }) if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) n = n[..^suf.Length];
            if (n.EndsWith("-latest")) n = n[..^7];
            return Sanitize(n);
        }
        return "map";
    }

    public static string Sanitize(string s)
    {
        var c = s.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').ToArray();
        var r = new string(c).Trim('-');
        return r.Length == 0 ? "map" : r;
    }

    /// <summary>Deletes an old map folder, but only if it holds nothing except map files (.mlp/.idx/.db).</summary>
    private static void ClearMapDir(string dir)
    {
        if (!Directory.Exists(dir)) return;
        string[] ok = [".mlp", ".idx", ".db"];
        var bad = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => !ok.Contains(Path.GetExtension(f).ToLowerInvariant()));
        if (bad != null)
            throw new IOException($"Folder '{dir}' contains non-map files ({Path.GetFileName(bad)}). Choose another map name or output folder.");
        Directory.Delete(dir, true);
    }

    /// <summary>Full job. log: text lines; progress: (stage text, 0..1 or -1 for indeterminate).</summary>
    public static async Task<string> RunAsync(JobSpec job, Action<string> log, Action<string, double> progress, CancellationToken ct)
    {
        var o = job.Options;
        var name = DefaultName(job);
        var mapDir = Path.Combine(job.OutDir, name);
        var cmDir = Path.Combine(job.OutDir, name + "-cm");
        var t0 = DateTime.Now;

        // 1. source
        string pbf;
        if (job.LocalPbf != null) pbf = job.LocalPbf;
        else if (job.Region != null)
            pbf = await Sources.GetPbfAsync(job.Region, job.RefreshPbf ? TimeSpan.Zero : TimeSpan.FromDays(1), log,
                f => progress("download", f), ct);
        else throw new InvalidOperationException("No source");
        log($"Source: {pbf} ({new FileInfo(pbf).Length / 1048576.0:F0} MB)");

        // 2. OSM layers
        OsmResult res = await Task.Run(() =>
            new OsmBuilder(o, log, f => progress("osm", f)).Build(pbf, ct), ct);
        ClearMapDir(mapDir);
        Directory.CreateDirectory(mapDir);
        progress("write", -1);
        await Task.Run(() =>
        {
            if (o.Roads && res.Roads.Count > 0) Yzl.WriteLayer(mapDir, "roads", res.Roads, false);
            if (o.Water && res.Water.Count > 0) Yzl.WriteLayer(mapDir, "water", res.Water, false);
            if (o.Landuse && res.Landuse.Count > 0) Yzl.WriteLayer(mapDir, "landuse", res.Landuse, true);
        }, ct);
        foreach (var f in Directory.GetFiles(mapDir))
            log($"  {Path.GetFileName(f),-12} {new FileInfo(f).Length / 1048576.0,7:F1} MB");
        var tiles = res.Tiles.OrderBy(t => t.lat).ThenBy(t => t.lon).ToList();
        res = null!;
        GC.Collect();

        // 3. contours
        if (o.Contours)
        {
            ClearMapDir(cmDir);
            Directory.CreateDirectory(cmDir);
            log($"Contours: {tiles.Count} tiles, step {o.ContourStep} m");
            int done = 0, written = 0;
            foreach (var (lat, lon) in tiles)
            {
                ct.ThrowIfCancellationRequested();
                var tn = Contours.TileName(lat, lon);
                progress("contours", done / (double)Math.Max(1, tiles.Count));
                string? hgt;
                try { hgt = await Sources.GetHgtAsync(lat, lon, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log($"  {tn}: download error {e.Message}"); hgt = null; }
                if (hgt != null)
                {
                    int n = await Task.Run(() => Contours.MakeTile(hgt, lat, lon, cmDir, o.ContourStep), ct);
                    if (n > 0) { written++; log($"  {tn}: {n} lines"); }
                }
                done++;
            }
            log($"Contours: {written} tiles written");
        }
        progress("done", 1);
        log($"Done in {(DateTime.Now - t0).TotalSeconds:F0} s -> {job.OutDir}");
        return mapDir;
    }
}
