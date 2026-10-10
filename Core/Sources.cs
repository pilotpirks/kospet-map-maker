using System.Net;
using System.Text.Json;

namespace KospetMapMaker.Core;

public sealed class Region
{
    public string Id = "", Name = "", Parent = "", PbfUrl = "";
    public string Path = "";
    public override string ToString() => Path;
}

/// <summary>Downloads: Geofabrik region index + .osm.pbf, and SRTM tiles (AWS "skadi" terrain tiles).</summary>
public static class Sources
{
    public const string GeofabrikIndex = "https://download.geofabrik.de/index-v1-nogeom.json";
    public const string SkadiUrl = "https://s3.amazonaws.com/elevation-tiles-prod/skadi/{0}/{1}.hgt.gz";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("KospetMapMaker/1.0");
        return c;
    }

    public static string AppDir
    {
        get
        {
            var d = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KospetMapMaker");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string CacheDir
    {
        get { var d = System.IO.Path.Combine(AppDir, "cache"); Directory.CreateDirectory(d); return d; }
    }

    public static async Task<List<Region>> LoadRegionsAsync(bool forceRefresh, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(CacheDir, "geofabrik-index.json");
        if (forceRefresh || !File.Exists(path) || DateTime.Now - File.GetLastWriteTime(path) > TimeSpan.FromDays(7))
        {
            var json = await Http.GetStringAsync(GeofabrikIndex, ct);
            await File.WriteAllTextAsync(path, json, ct);
        }
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var list = new List<Region>();
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var p = f.GetProperty("properties");
            if (!p.TryGetProperty("urls", out var urls) || !urls.TryGetProperty("pbf", out var pbf)) continue;
            list.Add(new Region
            {
                Id = p.GetProperty("id").GetString() ?? "",
                Name = p.GetProperty("name").GetString() ?? "",
                Parent = p.TryGetProperty("parent", out var par) ? par.GetString() ?? "" : "",
                PbfUrl = pbf.GetString() ?? "",
            });
        }
        var byId = list.ToDictionary(r => r.Id);
        foreach (var r in list)
        {
            var names = new List<string> { r.Name };
            var cur = r; int guard = 0;
            while (cur.Parent != "" && byId.TryGetValue(cur.Parent, out var par) && guard++ < 10) { names.Insert(0, par.Name); cur = par; }
            r.Path = string.Join(" / ", names);
        }
        return list.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Download url to path (via .part file). Returns false on HTTP 404.</summary>
    public static async Task<bool> DownloadAsync(string url, string path, Action<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Forbidden) return false;
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        var tmp = path + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Invoke(done / (double)total.Value);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return true;
    }

    /// <summary>Returns cached .pbf path for a region, downloading it if missing or older than maxAge.</summary>
    public static async Task<string> GetPbfAsync(Region r, TimeSpan maxAge, Action<string> log, Action<double> progress, CancellationToken ct)
    {
        var dir = System.IO.Path.Combine(CacheDir, "pbf"); Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, r.Id.Replace('/', '_') + ".osm.pbf");
        if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < maxAge)
        {
            log($"PBF: cache {path}");
            return path;
        }
        log($"PBF: download {r.PbfUrl}");
        if (!await DownloadAsync(r.PbfUrl, path, progress, ct)) throw new IOException("Not found: " + r.PbfUrl);
        return path;
    }

    /// <summary>Returns cached SRTM tile path, or null if the tile does not exist (open ocean).</summary>
    public static async Task<string?> GetHgtAsync(int lat, int lon, CancellationToken ct)
    {
        var name = Contours.TileName(lat, lon);
        var dir = System.IO.Path.Combine(CacheDir, "hgt"); Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, name + ".hgt.gz");
        var missing = path + ".missing";
        if (File.Exists(path)) return path;
        if (File.Exists(missing)) return null;
        var url = string.Format(SkadiUrl, name[..3], name);
        if (await DownloadAsync(url, path, null, ct)) return path;
        File.WriteAllText(missing, "");
        return null;
    }
}
